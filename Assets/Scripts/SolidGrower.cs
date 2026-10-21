using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// 4 章「立体を育てる」: 発見ボードの ○ のマスが選ばれたら、その角から正多面体を育てる。
//   ① 角: 工房と同じく、平らな星形から角が閉じる（カチッ）
//   ② 段: 「どの角も同じ」で、もうある面に辺でつながる面が 1 段ずつ出てくる。
//          展開図のように親の面と同じ平面に開いた形で現れ、蝶番の辺でパタンと折れる（カチッ）
//   ③ 最後の段が閉じると、緑に光ってチャイム。そこから先は掴んで回せる（当たり判定を入れる）
//   掴めなくするのはコライダーを切るだけ。XR Grab Interactable 自体は切らない（入れ直すと Interaction Manager への
//   コライダーの登録をやり直すことになり、1 回目だけ掴めないことがあった。2026-10-21）
// 段取りの計算は PolyhedronGrowth にある。ここは時間を進めて Mesh を作り直すだけ。
[RequireComponent(typeof(AudioSource))]
public class SolidGrower : MonoBehaviour
{
    [Header("つなぐもの")]
    [Tooltip("どの ○ が選ばれたかを聞くボード。空ならシーンから探す")]
    [SerializeField] DiscoveryBoard board;
    [Tooltip("育てる立体（このオブジェクトの子）。MeshFilter / MeshRenderer（Material 2 つ）/ MeshCollider（Convex ON）/ Rigidbody（Kinematic）/ XR Grab Interactable / Glow")]
    [SerializeField] XRGrabInteractable solid;

    [Header("形と動き")]
    [Tooltip("辺の長さ [m]（工房のタイルと同じにしておく）")]
    [SerializeField, Min(0.02f)] float edgeLength = 0.15f;
    [Tooltip("タイルどうしのすき間（タイルを中心へ縮める割合。工房と同じ）")]
    [SerializeField, Range(0f, 0.2f)] float inset = 0.04f;
    [SerializeField, Min(0.1f)] float cornerSeconds = 1.0f;
    [SerializeField, Min(0.1f)] float waveSeconds = 0.7f;
    [SerializeField, Min(0f)] float pauseSeconds = 0.3f;

    [Header("反応")]
    [SerializeField] Color doneColor = new Color(0.2f, 0.9f, 0.3f);
    [SerializeField, Range(0f, 1f)] float volume = 0.6f;

    // 立体が閉じたとき (p, q)。後で「ボードのマスをミニチュアに」「面の数を出す」などがこれを聞く
    public event Action<int, int> Grown;

    public bool Growing => plan != null && !done;

    const float GapAzimuth = -Mathf.PI / 2f;   // 角のすき間を手前（ローカルの −Z）へ向ける（工房と同じ）

    PolyhedronGrowth plan;
    Vector2[] coef;
    Vector3[] corners;
    float timer;
    int landed;        // 着地の音を鳴らし終えた数（0 = 角、1.. = 段）
    bool done;

    Mesh mesh;            // 見た目（育つ間は毎フレーム書き換える）
    Mesh colliderMesh;    // 当たり判定（閉じるたびに新しく作る。見た目の Mesh とは共有しない）
    MeshRenderer solidRenderer;
    MeshCollider solidCollider;
    Glow solidGlow;
    AudioSource source;
    AudioClip chime, click;
    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector3> norms = new List<Vector3>();
    readonly List<int>[] tris = { new List<int>(), new List<int>() };
    readonly List<int> colliderTris = new List<int>();

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        chime = ToneSynth.Chime();
        click = ToneSynth.Click();

        if (solid != null)
        {
            mesh = new Mesh { name = "GrowingSolid", hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            var mf = solid.GetComponent<MeshFilter>();
            if (mf != null) mf.sharedMesh = mesh;
            solidRenderer = solid.GetComponent<MeshRenderer>();
            solidCollider = solid.GetComponent<MeshCollider>();
            solidGlow = solid.GetComponent<Glow>();
            if (solidGlow != null) solidGlow.Pulse = false;
            if (solidRenderer != null && solidRenderer.sharedMaterials.Length < 2)
                Debug.LogWarning("SolidGrower: Solid の MeshRenderer に Material を 2 つ入れてください（段ごとに交互の色）");
            Show(false);
        }
        else Debug.LogWarning("SolidGrower: Solid を入れてください");
    }

    void OnEnable()
    {
        if (board == null)
        {
            var found = FindObjectsByType<DiscoveryBoard>();
            if (found.Length > 0) board = found[0];
        }
        if (board != null) board.CornerPicked += Grow;
    }

    void OnDisable()
    {
        if (board != null) board.CornerPicked -= Grow;
    }

    void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (colliderMesh != null) Destroy(colliderMesh);
    }

    // ○ のマスが選ばれた: その (p, q) の立体を最初から育てる
    public void Grow(int p, int q)
    {
        if (solid == null) return;
        if (solid.isSelected) return;   // 手で持っている間は育て直さない
        var next = PolyhedronGrowth.Plan(p, q, edgeLength, GapAzimuth);
        if (next == null) return;

        plan = next;
        coef = CornerFold.PolygonCoefficients(p);
        corners = new Vector3[p];
        timer = 0f;
        landed = 0;
        done = false;

        // 掴んで動かしたり大きさを変えたりしていても、育てる場所へ戻す
        solid.transform.SetParent(transform, false);
        solid.transform.localPosition = Vector3.zero;
        solid.transform.localRotation = Quaternion.identity;
        solid.transform.localScale = Vector3.one;
        Show(true);
        SetGrabbable(false);
        Rebuild();
    }

    void Update()
    {
        if (plan == null || done) return;
        timer += Time.deltaTime;

        // 角が閉じた / 段が着地したときの音
        int nowLanded = LandedCount(timer);
        while (landed < nowLanded)
        {
            landed++;
            if (landed < plan.WaveCount) source.PlayOneShot(click, volume);
        }
        Rebuild();

        if (landed >= plan.WaveCount)
        {
            done = true;
            if (solidCollider != null)
            {
                // 当たり判定の Mesh は毎回新しく作る。コライダーが握っている Mesh の中身を書き換えて使い回すと、
                // 1 回おきに掴めなかった（2026-10-21）。古い Mesh は、コライダーから外してから捨てる
                var old = colliderMesh;
                colliderMesh = new Mesh { name = "GrownSolidCollider", hideFlags = HideFlags.DontSave };
                colliderMesh.SetVertices(verts);
                colliderTris.Clear();
                colliderTris.AddRange(tris[0]); colliderTris.AddRange(tris[1]);
                colliderMesh.SetTriangles(colliderTris, 0);
                colliderMesh.RecalculateBounds();
                solidCollider.enabled = true;
                solidCollider.sharedMesh = colliderMesh;
                if (old != null) Destroy(old);
                Physics.SyncTransforms();   // 位置と当たり判定の変化を、物理エンジンへすぐ伝える（次のレイから効くように）
            }
            SetGrabbable(true);
            if (solidGlow != null) solidGlow.Flash(doneColor);
            source.PlayOneShot(chime, volume);
            Grown?.Invoke(plan.Sides, plan.Count);
        }
    }

    // ---- 時間割 ---------------------------------------------------------------------------
    // 角: [0, cornerSeconds]。段 w（1 以上）: 前が終わってから pauseSeconds 待ち、waveSeconds で折れる

    float WaveStart(int w) => cornerSeconds + (w - 1) * (pauseSeconds + waveSeconds) + pauseSeconds;
    float WaveEnd(int w) => w == 0 ? cornerSeconds : WaveStart(w) + waveSeconds;

    int LandedCount(float t)
    {
        int n = 0;
        for (int w = 0; w < plan.WaveCount; w++) if (t >= WaveEnd(w)) n = w + 1;
        return n;
    }

    // ---- 見た目 ---------------------------------------------------------------------------

    void Show(bool show)
    {
        if (solidRenderer != null) solidRenderer.enabled = show;
        if (!show) SetGrabbable(false);
    }

    // 当たり判定が無ければ、レイも手も届かないので掴めない
    void SetGrabbable(bool on)
    {
        if (solidCollider != null) solidCollider.enabled = on;
    }

    void Rebuild()
    {
        Clear();
        if (timer < cornerSeconds)
        {
            // ① 角: 平らな星形（φ = 内角）から閉じた角（φ = 360°/q）へ。出だしと終わりがゆっくり
            float k = Smooth(timer / cornerSeconds);
            float alpha = CornerFold.InteriorAngle(plan.Sides);
            float phi = Mathf.Lerp(alpha, CornerFold.ClosedPhi(plan.Count), k);
            for (int i = 0; i < plan.Count; i++)
            {
                plan.CornerTile(phi, i, coef, corners);
                AddTile(0);
            }
        }
        else
        {
            for (int f = 0; f < plan.Faces.Length; f++)
            {
                int w = plan.Wave[f];
                float open = 0f;
                if (w > 0)
                {
                    if (timer < WaveStart(w)) continue;   // まだ出ていない
                    float k = Mathf.Clamp01((timer - WaveStart(w)) / waveSeconds);
                    open = 1f - k * k;                    // だんだん速くなってパタンと閉じる
                }
                plan.FaceCorners(f, open, corners);
                AddTile(w % 2);
            }
        }
        Apply();
    }

    static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

    // corners の多角形を、中心へ少し縮めて（タイルの境目を見せる）両面で足す
    void AddTile(int sub)
    {
        Vector3 c = Vector3.zero;
        foreach (var p in corners) c += p;
        c /= corners.Length;
        for (int j = 0; j < corners.Length; j++) corners[j] = c + (corners[j] - c) * (1f - inset);
        Vector3 n = Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]).normalized;
        AddPolygon(n, sub);
        AddPolygon(-n, sub);   // 育つ途中は中も見えるので両面
    }

    // Unity では Cross(b − a, c − a) が三角形 (a, b, c) の表側を向く。n と逆なら並びを入れ替える
    void AddPolygon(Vector3 n, int sub)
    {
        int s = verts.Count;
        foreach (var p in corners) { verts.Add(p); norms.Add(n); }
        bool flip = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]), n) < 0f;
        var list = tris[sub];
        for (int i = 1; i < corners.Length - 1; i++)
        {
            list.Add(s);
            list.Add(flip ? s + i + 1 : s + i);
            list.Add(flip ? s + i : s + i + 1);
        }
    }

    void Clear()
    {
        verts.Clear(); norms.Clear();
        foreach (var t in tris) t.Clear();
    }

    void Apply()
    {
        if (mesh == null) return;
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(tris[0], 0);
        mesh.SetTriangles(tris[1], 1);
        mesh.RecalculateBounds();
    }
}
