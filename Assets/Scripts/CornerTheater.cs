using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// 3 章「角はだんだん太る」の舞台（見る章）。
//   始め方: ○ が 5 つそろうと、七角形のタイル（お題「七角形なら？」を文字なしで）が光って浮かぶ。掴むと上演が始まる
//   幕 1: 1 つの点のまわりに平らなタイルが 3 枚。三角 → 四角 → … → 九角と辺が増え、点に集まる角（くさび）が太っていく。
//         形ごとに、工房でピンを引いたときと同じことを見せる（ピンそのものは出さない。角とタイルだけ）:
//           すき間が残る（三〜五角）= 点が上がって 3 枚が折れ、すき間が閉じてパチン → 角ができる（緑 + チャイム）
//           ぴったり（六角）       = 持ち上げようとしても平らなまま折れず、ボヨンと戻って揺れる（青）
//           かぶる（七角〜）       = 3 枚目が 1 枚目にかぶって赤。持ち上げようとするとブブーで揺れる
//   この章を一度見終わるまで、ボードの ○ は選べない（矢印も出ない。DiscoveryBoard.PickLocked）。上演中（もう一回見ているときも）も選べない。
//   見終わったら矢印が出て 4 章へ進める。七角形はまた浮かぶ（もう一回見られる）
//
// 台本はコルーチン（Show）で書く: 「出す → 動かす → 待つ → 次」を上から順に書ける。yield return null = 1 フレーム待つ。
// 時間を進めるのは Tween / Wait だけ（毎フレーム Time.deltaTime を足す）。見た目は状態（alpha / height など）から毎フレーム Mesh を作り直す。
// タイルの並びと折れ方は工房と同じ式（CornerFold）。舞台の平面はローカルの XZ、点が上がる向きは +Y。
[RequireComponent(typeof(AudioSource))]
public class CornerTheater : MonoBehaviour
{
    [Header("つなぐもの")]
    [Tooltip("○ がそろったことを聞き、上演中は ○ を指す矢印を止めるボード。空ならシーンから探す")]
    [SerializeField] DiscoveryBoard board;
    [Tooltip("舞台（このオブジェクトの子）。MeshFilter / MeshRenderer（Material 5 つ）/ Glow（Pulse OFF）")]
    [SerializeField] MeshFilter stage;
    [Tooltip("お題の七角形（このオブジェクトの子）。MeshFilter / MeshRenderer（Spare）/ Box Collider / XR Simple Interactable / Glow")]
    [SerializeField] XRSimpleInteractable starter;

    [Header("形")]
    [Tooltip("タイルの辺の長さ [m]")]
    [SerializeField, Min(0.02f)] float edgeLength = 0.08f;
    [Tooltip("タイルどうしのすき間（中心へ縮める割合。工房と同じ）")]
    [SerializeField, Range(0f, 0.2f)] float inset = 0.04f;
    [Tooltip("くさびの半径（辺の長さに対する割合）")]
    [SerializeField, Range(0.1f, 1f)] float wedgeRadius = 0.45f;
    [Tooltip("どこまで辺を増やすか")]
    [SerializeField, Range(7, 12)] int lastSides = 9;
    [Tooltip("お題の七角形の辺の長さ [m]")]
    [SerializeField, Min(0.02f)] float starterEdge = 0.1f;

    [Header("時間 [秒]")]
    [SerializeField, Min(0.1f)] float fattenSeconds = 0.9f;
    [SerializeField, Min(0.05f)] float tilesInSeconds = 0.3f;
    [SerializeField, Min(0.1f)] float foldSeconds = 0.9f;
    [SerializeField, Min(0.1f)] float holdSeconds = 1.0f;

    [Header("反応")]
    [SerializeField] Color cornerColor = new Color(0.2f, 0.9f, 0.3f);
    [SerializeField] Color flatColor = new Color(0.25f, 0.55f, 1f);
    [SerializeField] Color failColor = new Color(1f, 0.2f, 0.15f);
    [SerializeField, Range(0f, 1f)] float volume = 0.6f;

    // 上演が終わったとき。後で章をつなぐ台本がこれを聞く
    public event Action Finished;
    public bool Playing { get; private set; }

    // サブメッシュ（= 舞台の Material の番号）: 0, 1 = タイル（交互の色）/ 2 = 重なり（赤）/ 3 = すき間あり（緑）/ 4 = ぴったり（青）
    const int SubTileA = 0, SubTileB = 1, SubOver = 2, SubCorner = 3, SubFlat = 4, Submeshes = 5;
    const int Tiles = 3;
    const float GapAzimuth = -Mathf.PI / 2f;    // すき間（重なり）をローカルの −Z へ向ける
    const float Eps = 1e-3f;
    const float OverLift = 0.003f, WedgeLift = 0.002f;
    const float TugHeight = 0.015f;             // 折れないとき、平らなまま持ち上げて戻す高さ [m]

    // ---- 見た目の状態（台本が書き、Rebuild が読む）----
    float alpha;          // くさびの角（いま）
    int tileSides;        // 出しているタイルの形（0 = タイルなし）
    float tileScale;      // タイルの大きさ（0 → 1 で点から出てくる）
    float wedgeScale;     // くさびの大きさ（0 = 出さない）
    float height;         // 点の高さ [m]。タイルの外の辺は高さ 0 のまま = 工房と同じ折れ方
    float tug;            // 折れないとき、平らなまま持ち上がる高さ [m]
    float wobble;         // 全体の揺れ [m]
    bool visible;

    Mesh stageMesh, starterMesh;
    Glow stageGlow;
    Renderer starterRenderer;
    Collider starterCollider;
    AudioSource source;
    AudioClip chime, boing, buzz;
    bool starterShown, starterWasSelected;
    Vector2[] coef;
    Vector3[] corners;
    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector3> norms = new List<Vector3>();
    readonly List<int>[] tris = new List<int>[Submeshes];

    void Awake()
    {
        for (int i = 0; i < Submeshes; i++) tris[i] = new List<int>();
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        chime = ToneSynth.Chime(); boing = ToneSynth.Boing(); buzz = ToneSynth.Buzz();

        if (stage != null)
        {
            stageMesh = NewMesh("TheaterStage", stage);
            stageGlow = stage.GetComponent<Glow>();
            if (stageGlow != null) stageGlow.Pulse = false;
            var mr = stage.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterials.Length < Submeshes)
                Debug.LogWarning("CornerTheater: Stage の MeshRenderer に Material を 5 つ入れてください（0, 1 = タイル / 2 = 重なり（赤）/ 3 = 緑 / 4 = 青）");
        }
        if (starter != null)
        {
            starterMesh = NewMesh("TheaterStarter", starter.GetComponent<MeshFilter>());
            starterRenderer = starter.GetComponent<Renderer>();
            starterCollider = starter.GetComponent<Collider>();
            BuildStarterMesh();
            ShowStarter(false);
        }
        if (stage == null || starter == null) Debug.LogWarning("CornerTheater: Stage / Starter を入れてください");
    }

    void OnEnable()
    {
        if (board == null)
        {
            var found = FindObjectsByType<DiscoveryBoard>();
            if (found.Length > 0) board = found[0];
        }
        if (board != null)
        {
            board.CornersFound += OnCornersFound;
            board.PickLocked = true;   // この章を見るまで、4 章（○ を選んで育てる）へは進めない
        }
    }

    void OnDisable()
    {
        if (board != null) board.CornersFound -= OnCornersFound;
    }

    void OnDestroy()
    {
        if (stageMesh != null) Destroy(stageMesh);
        if (starterMesh != null) Destroy(starterMesh);
    }

    static Mesh NewMesh(string name, MeshFilter filter)
    {
        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();
        if (filter != null) filter.sharedMesh = mesh;
        return mesh;
    }

    void OnCornersFound() => ShowStarter(true);

    void Update()
    {
        // お題の七角形: ふわふわ浮く（工房の黄色いタイルと同じ「掴める物」の見た目）。掴んだら上演
        if (starter != null)
        {
            if (starterShown)
                starter.transform.localPosition = new Vector3(0f, 0.02f, 0.012f * Mathf.Sin(2f * Mathf.PI * Time.time / 1.6f));
            bool sel = starter.isSelected;
            if (sel && !starterWasSelected && starterShown && !Playing) StartCoroutine(Show());
            starterWasSelected = sel;
        }
        if (visible || Playing) Rebuild();
    }

    void ShowStarter(bool show)
    {
        starterShown = show;
        if (starterRenderer != null) starterRenderer.enabled = show;
        if (starterCollider != null) starterCollider.enabled = show;
        if (show && starter != null) starter.transform.localScale = Vector3.one;
    }

    // ---- 台本 -------------------------------------------------------------------------------

    IEnumerator Show()
    {
        Playing = true;
        if (board != null) board.PickLocked = true;   // 上演中は ○ を選べない

        // 七角形が舞台の点へ吸い込まれて消える
        yield return Tween(0.35f, k => { if (starter != null) starter.transform.localScale = Vector3.one * (1f - k); });
        ShowStarter(false);

        // 幕 1: 角はだんだん太る
        visible = true;
        tileSides = 0; height = 0f; tug = 0f; wobble = 0f;
        alpha = CornerFold.InteriorAngle(3);
        yield return Tween(0.4f, k => wedgeScale = k);   // くさびが点から出てくる
        for (int p = 3; p <= lastSides; p++)
        {
            if (p > 3)
            {
                float from = CornerFold.InteriorAngle(p - 1), to = CornerFold.InteriorAngle(p);
                yield return Tween(fattenSeconds, k => alpha = Mathf.Lerp(from, to, Smooth(k)));   // くさびが太る（タイルはない）
            }
            alpha = CornerFold.InteriorAngle(p);
            tileSides = p;
            coef = CornerFold.PolygonCoefficients(p);
            corners = new Vector3[p];
            yield return Tween(tilesInSeconds, k => tileScale = Smooth(k));   // その形のタイルが点から出てくる
            yield return Wait(0.4f);

            if (CornerFold.CanFold(p, Tiles))
            {
                // すき間がある: 点が上がって折れ、すき間が閉じたところでパチン（工房と同じ）
                float hMax = CornerFold.MaxHeight(p, Tiles) * edgeLength;
                yield return Tween(foldSeconds, k => height = hMax * Smooth(k));
                React(State.Gap);
                yield return Wait(holdSeconds);
                yield return Tween(0.5f, k => height = hMax * (1f - Smooth(k)));   // ほどく
            }
            else
            {
                // ぴったり / かぶる: 持ち上げようとしても平らなまま（折れない）。ボヨン（ブブー）と戻って全体が揺れる
                yield return Tween(0.35f, k => tug = TugHeight * Smooth(k));
                React(Current());
                yield return Tween(1.2f, k =>
                {
                    float t = k * 1.2f;
                    tug = TugHeight * Mathf.Exp(-8f * t);
                    wobble = 0.012f * Mathf.Exp(-5f * t) * Mathf.Sin(2f * Mathf.PI * 4f * t);
                });
                tug = 0f; wobble = 0f;
                yield return Wait(holdSeconds * 0.5f);
            }
            if (p < lastSides) yield return Tween(0.25f, k => tileScale = 1f - Smooth(k));   // 次へ: タイルを引っ込める
        }
        yield return Wait(0.6f);
        yield return Tween(0.5f, k => { tileScale = 1f - k; wedgeScale = 1f - k; });   // 全部しまう
        visible = false;
        tileSides = 0;
        Rebuild();

        // （幕 2「ぴったりは床になる」・幕 3「○ だけが残る」はこのあとに足す）

        Playing = false;
        if (board != null) board.PickLocked = false;   // ○ を選べる（矢印が出る）= 4 章へ
        Finished?.Invoke();
        yield return Wait(1f);
        ShowStarter(true);   // もう一回見られる
    }

    // seconds かけて k を 0 → 1 に進め、毎フレーム step(k) を呼ぶ
    IEnumerator Tween(float seconds, Action<float> step)
    {
        for (float t = 0f; t < seconds; t += Time.deltaTime)
        {
            step(t / seconds);
            yield return null;   // 1 フレーム待つ
        }
        step(1f);
    }

    IEnumerator Wait(float seconds)
    {
        for (float t = 0f; t < seconds; t += Time.deltaTime) yield return null;
    }

    static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

    enum State { Gap, Flat, Over }

    // 3 枚の角の合計と 1 周（360°）の比べ: すき間 / ぴったり / かぶる
    State Current()
    {
        float sum = Tiles * alpha;
        if (sum < 2f * Mathf.PI - Eps) return State.Gap;
        if (sum <= 2f * Mathf.PI + Eps) return State.Flat;
        return State.Over;
    }

    void React(State state)
    {
        switch (state)
        {
            case State.Gap: source.PlayOneShot(chime, volume); if (stageGlow != null) stageGlow.Flash(cornerColor); break;
            case State.Flat: source.PlayOneShot(boing, volume); if (stageGlow != null) stageGlow.Flash(flatColor); break;
            default: source.PlayOneShot(buzz, volume); if (stageGlow != null) stageGlow.Flash(failColor); break;
        }
    }

    // ---- Mesh -------------------------------------------------------------------------------

    void Rebuild()
    {
        Clear();
        if (visible)
        {
            var state = Current();
            int wedgeSub = state == State.Gap ? SubCorner : state == State.Flat ? SubFlat : SubOver;

            // 折れ具合: 点の高さ → 隣り合う辺の間の角 φ（工房と同じ式）。タイルがない間・折れないときは平ら（φ = α）
            bool folding = tileSides > 0 && height > 0f && state == State.Gap;
            float phi = folding ? CornerFold.PhiFromHeight(alpha, height / edgeLength) : alpha;
            var apex = new Vector3(0f, (folding ? height : 0f) + tug + wobble, 0f);   // 折れないときは平らなまま全体が上下する
            var e = new Vector3[Tiles + 1];
            for (int k = 0; k <= Tiles; k++) e[k] = CornerFold.EdgeDirection(alpha, phi, Tiles, GapAzimuth, k);

            // タイル（3 枚目が 1 枚目にかぶるときは、3 枚目を赤にして少し浮かせる。工房の「はみ出したタイル」と同じ見せ方）
            if (tileSides > 0 && tileScale > 0f)
                for (int k = 0; k < Tiles; k++)
                {
                    bool over = state == State.Over && k == Tiles - 1;
                    Vector3 n = Vector3.Cross(e[k + 1], e[k]).normalized;   // タイルの表（平らなら真上）
                    CornerFold.TileCorners(apex, e[k], e[k + 1], edgeLength * tileScale, coef, corners);
                    Shrink(corners, n * (over ? OverLift : 0f));
                    int sub = over ? SubOver : (k % 2 == 0 ? SubTileA : SubTileB);
                    AddFan(corners, n, sub);
                    AddFan(corners, -n, sub);
                }

            // くさび（点に集まる角）。タイルの面の上に少し浮かせる
            if (wedgeScale > 0f)
                for (int k = 0; k < Tiles; k++)
                {
                    bool over = state == State.Over && k == Tiles - 1;
                    Vector3 n = Vector3.Cross(e[k + 1], e[k]).normalized;
                    AddWedge(apex, e[k], e[k + 1], wedgeRadius * edgeLength * wedgeScale, n * (WedgeLift + (over ? OverLift : 0f)), wedgeSub);
                }
        }
        Apply(stageMesh, Submeshes);
    }

    // タイルを中心へ少し縮める（境目を見せる）+ 少し浮かせる
    void Shrink(Vector3[] c, Vector3 lift)
    {
        Vector3 m = Vector3.zero;
        foreach (var p in c) m += p;
        m /= c.Length;
        for (int j = 0; j < c.Length; j++) c[j] = m + (c[j] - m) * (1f - inset) + lift;
    }

    // 辺 a から辺 b までの扇形（中心 = 点、半径 r）。a, b は単位ベクトルで、その 2 本が張る平面の上に描く。両面
    void AddWedge(Vector3 apex, Vector3 a, Vector3 b, float r, Vector3 lift, int sub)
    {
        const int Segments = 24;
        float w = Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f));
        float sw = Mathf.Sin(w);
        var fan = new Vector3[Segments + 2];
        fan[0] = apex + lift;
        for (int j = 0; j <= Segments; j++)
        {
            float t = (float)j / Segments;
            // 球面線形補間（a から b へ、同じ平面の上を一定の速さで回る）
            Vector3 d = sw > 1e-5f ? (a * Mathf.Sin((1f - t) * w) + b * Mathf.Sin(t * w)) / sw : a;
            fan[j + 1] = apex + d * r + lift;
        }
        Vector3 n = Vector3.Cross(b, a).normalized;
        AddFan(fan, n, sub);
        AddFan(fan, -n, sub);
    }

    // 多角形（凸）を扇形に三角形分割して足す。法線 n の側から見て表になる向きに並べる
    void AddFan(Vector3[] c, Vector3 n, int sub)
    {
        int s = verts.Count;
        foreach (var p in c) { verts.Add(p); norms.Add(n); }
        var list = tris[sub];
        for (int i = 1; i < c.Length - 1; i++)
        {
            // Unity では Cross(b − a, c − a) が三角形 (a, b, c) の表側を向く。n と逆なら並びを入れ替える
            bool flip = Vector3.Dot(Vector3.Cross(c[i] - c[0], c[i + 1] - c[0]), n) < 0f;
            list.Add(s);
            list.Add(flip ? s + i + 1 : s + i);
            list.Add(flip ? s + i : s + i + 1);
        }
    }

    // お題の七角形（中心が原点、XZ 平面、両面）と、掴むための薄い箱
    void BuildStarterMesh()
    {
        const int P = 7;
        float radius = starterEdge / (2f * Mathf.Sin(Mathf.PI / P));
        var c = new Vector3[P];
        for (int j = 0; j < P; j++)
        {
            float a = 2f * Mathf.PI * j / P;
            c[j] = new Vector3(radius * Mathf.Sin(a), 0f, radius * Mathf.Cos(a));
        }
        Clear();
        AddFan(c, Vector3.up, 0);
        AddFan(c, Vector3.down, 0);
        Apply(starterMesh, 1);
        var box = starterCollider as BoxCollider;
        if (box == null && starter != null) box = starter.GetComponent<BoxCollider>();
        if (box != null) { box.center = Vector3.zero; box.size = new Vector3(2f * radius, 0.02f, 2f * radius); }
    }

    void Clear()
    {
        verts.Clear(); norms.Clear();
        foreach (var t in tris) t.Clear();
    }

    void Apply(Mesh mesh, int submeshes)
    {
        if (mesh == null) return;
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.subMeshCount = submeshes;
        for (int i = 0; i < submeshes; i++) mesh.SetTriangles(tris[i], i);
        mesh.RecalculateBounds();
    }
}
