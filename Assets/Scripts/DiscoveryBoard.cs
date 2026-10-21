using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// 発見ボード: 角の工房で試した「何角形を何枚」の結果を、表のマスに付けていく。文字は使わない。
//   縦 = タイルの形（左端の列に、その形の絵）。上から三角・四角・五角・六角
//   横 = 枚数（上端の行に、枚数ぶんの点を輪に並べた絵 = ピンのまわりに何枚）。左から 2 枚 … 7 枚
//   結果: ○（緑の輪）= 角ができた / ✕（青）= ぴったり平ら / ✕（赤）= 重なる・ぺたんこ
//   まだ試していないマスは脈打つ（= ここも試せる）。マスは、その形で重なる枚数（CornerFold.MaxCount）までしか作らない
//   ○ が全部そろうと（ちょうど 5 つ）、ボード全体が緑に光ってチャイム。Completed を呼ぶ。そのあとはふちに緑の枠が残り、○ がゆっくり息をする
//   ○ のマスを選ぶ（4 章）: レイ（手）を当てるとそのマスが明るくなり、グリップすると CornerPicked(p, q) を呼ぶ（SolidGrower が聞いて立体を育てる）
//     Board に XR Simple Interactable と Box Collider を付けておく（Box Collider の大きさはコードが板に合わせる）
//
// 工房（CornerWorkshop）の Tried イベントを聞くだけ。工房はボードのことを知らない（知らせる側と聞く側を分ける）。
// 見た目は「シーンに置いた GameObject + 実行時に Mesh だけ作る」（実機で左右の目にずれないための方針）。
[ExecuteAlways]
[RequireComponent(typeof(AudioSource))]
public class DiscoveryBoard : MonoBehaviour
{
    [Header("つなぐもの")]
    [Tooltip("結果を聞く工房。空なら、シーンにある工房を全部聞く")]
    [SerializeField] CornerWorkshop[] workshops;
    [Tooltip("ボード本体（このオブジェクトの子）。MeshFilter / MeshRenderer（Material 6 つ）/ Glow（Pulse OFF）/ Box Collider / XR Simple Interactable")]
    [SerializeField] MeshFilter board;
    [Tooltip("まだ試していないマス（このオブジェクトの子）。MeshFilter / MeshRenderer（Material 1 つ）/ Glow（Pulse ON）")]
    [SerializeField] MeshFilter untried;

    [Header("大きさ")]
    [Tooltip("1 マスの大きさ [m]")]
    [SerializeField, Min(0.02f)] float cellSize = 0.12f;
    [Tooltip("マスどうしのすき間（マスの大きさに対する割合）")]
    [SerializeField, Range(0f, 0.4f)] float cellGap = 0.12f;

    [Header("反応")]
    [SerializeField] Color completeColor = new Color(0.2f, 0.9f, 0.3f);
    [SerializeField, Range(0f, 1f)] float volume = 0.6f;

    // ○ が全部そろったとき（2 章のゴール）。後で章をつなぐ台本がこれを聞く
    public event Action Completed;
    // ○ のマスが選ばれたとき (p, q)。SolidGrower がこれを聞いて立体を育てる
    public event Action<int, int> CornerPicked;

    public const int MinSides = 3, MaxSides = 6, MinCount = 2, MaxCount = 7;
    const int Rows = MaxSides - MinSides + 1;   // 4
    const int Cols = MaxCount - MinCount + 1;   // 6

    // サブメッシュ（= ボード本体の Material の番号）
    const int SubBack = 0, SubCell = 1, SubIcon = 2, SubCorner = 3, SubFlat = 4, SubFail = 5, Submeshes = 6;

    const float PopSeconds = 0.35f;     // 印が付いたときに、大きく出てから縮む時間
    const float CompleteDelay = 0.7f;   // 最後の ○ のあと、工房のチャイムと重ならないよう少し待つ
    const float CellZ = -0.002f, MarkZ = -0.004f;   // 板より手前（ボードの表は −Z 向き = 見る人の側）

    int[,] results = new int[Rows, Cols];   // −1 = まだ / それ以外 = (int)CornerFold.Result
    float[,] popTime = new float[Rows, Cols];
    int cornerTotal, cornerFound;
    bool complete;          // ○ がそろった
    bool completeShown;     // そろったことを見せた（光ってチャイムのあと）。ボードのふちに緑の枠を出す
    float completeTimer = -1f;

    Mesh boardMesh, untriedMesh;
    Glow boardGlow;
    XRSimpleInteractable picker;   // Board の XR Simple Interactable（○ を選ぶ）
    BoxCollider pickArea;
    int hoverR = -1, hoverC = -1;  // レイが当たっている ○ のマス（無ければ −1）
    bool pickerWasSelected;
    const float BreathePeriod = 1.6f;
    AudioSource source;
    AudioClip chime;
    readonly List<CornerWorkshop> listening = new List<CornerWorkshop>();
    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector3> norms = new List<Vector3>();
    readonly List<int>[] tris = new List<int>[Submeshes];
    bool dirty = true;
    bool animating;

    void OnEnable()
    {
        for (int i = 0; i < Submeshes; i++) if (tris[i] == null) tris[i] = new List<int>();
        if (board != null) boardMesh = NewMesh("DiscoveryBoard", board);
        if (untried != null) untriedMesh = NewMesh("DiscoveryBoardUntried", untried);
        ResetResults();

        if (!Application.isPlaying) return;   // エディタで置いて眺めるときは、絵を出すだけ

        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        chime = ToneSynth.Chime();
        if (board != null)
        {
            boardGlow = board.GetComponent<Glow>();
            if (boardGlow != null) { boardGlow.Pulse = false; boardGlow.HoverGlow = false; }   // レイが当たっても全体は光らせない（マスだけ）
            picker = board.GetComponent<XRSimpleInteractable>();
            pickArea = board.GetComponent<BoxCollider>();
            if (picker == null || pickArea == null)
                Debug.LogWarning("DiscoveryBoard: ○ を選べるようにするには、Board に Box Collider と XR Simple Interactable を付けてください");
            var mr = board.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterials.Length < Submeshes)
                Debug.LogWarning("DiscoveryBoard: Board の MeshRenderer に Material を 6 つ入れてください（0 = 板 / 1 = 試したマス / 2 = 形と枚数の絵 / 3 = ○ / 4 = 平ら（青）/ 5 = ✕）");
        }
        if (board == null || untried == null) Debug.LogWarning("DiscoveryBoard: Board / Untried を入れてください");

        // 工房の知らせを聞き始める（+= で自分の関数を登録する。OnDisable で -= して外す）
        listening.Clear();
        if (workshops != null) foreach (var w in workshops) if (w != null) listening.Add(w);
        if (listening.Count == 0) listening.AddRange(FindObjectsByType<CornerWorkshop>());
        foreach (var w in listening) w.Tried += OnTried;
    }

    void OnDisable()
    {
        foreach (var w in listening) if (w != null) w.Tried -= OnTried;
        listening.Clear();
    }

    void OnDestroy()
    {
        DestroyMesh(boardMesh);
        DestroyMesh(untriedMesh);
    }

    void OnValidate() => dirty = true;

    static Mesh NewMesh(string name, MeshFilter filter)
    {
        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        filter.sharedMesh = mesh;
        return mesh;
    }

    static void DestroyMesh(Mesh m)
    {
        if (m == null) return;
        if (Application.isPlaying) Destroy(m); else DestroyImmediate(m);
    }

    void ResetResults()
    {
        cornerTotal = 0; cornerFound = 0; complete = false; completeShown = false; completeTimer = -1f;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                results[r, c] = -1;
                popTime[r, c] = -1f;
                if (HasCell(r, c) && CornerFold.Classify(MinSides + r, MinCount + c) == CornerFold.Result.Corner) cornerTotal++;
            }
        dirty = true;
    }

    // その形で重なる枚数までがマス（三角 2〜7、四角 2〜5、五角 2〜4、六角 2〜4）
    static bool HasCell(int r, int c) => MinCount + c <= CornerFold.MaxCount(MinSides + r);

    void OnTried(int sides, int count, CornerFold.Result result)
    {
        int r = sides - MinSides, c = count - MinCount;
        if (r < 0 || r >= Rows || c < 0 || c >= Cols || !HasCell(r, c)) return;
        if (results[r, c] >= 0) return;   // 同じ組み合わせは何度試しても同じ結果。最初の 1 回だけ印を付ける

        results[r, c] = (int)result;
        popTime[r, c] = 0f;
        dirty = true;
        if (result == CornerFold.Result.Corner && ++cornerFound == cornerTotal && !complete)
        {
            complete = true;
            completeTimer = 0f;
        }
    }

    void Update()
    {
        // 印が付いた直後は、大きく出てから縮む（毎フレーム作り直す）
        animating = false;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (popTime[r, c] >= 0f)
                {
                    popTime[r, c] += Time.deltaTime;
                    if (popTime[r, c] >= PopSeconds) popTime[r, c] = -1f;
                    animating = true;
                }
        UpdatePicking();
        if (completeShown) animating = true;   // そろったあとは ○ がゆっくり息をする（= 触れる）
        if (dirty || animating) Rebuild();
        dirty = animating;   // 縮み終わった次のフレームでもう 1 回作り、ちょうど 1 倍で止める

        if (completeTimer >= 0f)
        {
            completeTimer += Time.deltaTime;
            if (completeTimer >= CompleteDelay)
            {
                completeTimer = -1f;
                completeShown = true;   // ここから緑の枠がずっと残る（そろったことが後からも見える）
                dirty = true;
                if (boardGlow != null) boardGlow.Flash(completeColor);
                if (source != null) source.PlayOneShot(chime, volume);
                Completed?.Invoke();
            }
        }
    }

    // ---- ○ を選ぶ ------------------------------------------------------------------------

    void UpdatePicking()
    {
        if (!Application.isPlaying || picker == null || pickArea == null) return;

        int r = -1, c = -1;
        if (picker.isHovered)
            foreach (var it in picker.interactorsHovering)
                if (CellUnder(it.transform, out r, out c)) break;
        if (r != hoverR || c != hoverC) { hoverR = r; hoverC = c; dirty = true; }

        bool selected = picker.isSelected;
        if (selected && !pickerWasSelected)
        {
            int sr, sc;
            if (CellUnder(picker.firstInteractorSelecting.transform, out sr, out sc))
            {
                popTime[sr, sc] = 0f;   // 選んだ ○ がポンと跳ねる
                dirty = true;
                CornerPicked?.Invoke(MinSides + sr, MinCount + sc);
            }
        }
        pickerWasSelected = selected;
    }

    // 手（レイの出どころ）が指している ○ のマス。レイが板に当たればその点、当たらなければ（手が近いとき）手の位置を板に落とした点で見る
    bool CellUnder(Transform hand, out int r, out int c)
    {
        r = c = -1;
        if (hand == null || board == null) return false;
        RaycastHit hit;
        Vector3 world = pickArea.Raycast(new Ray(hand.position, hand.forward), out hit, 20f) ? hit.point : hand.position;
        Vector3 p = board.transform.InverseTransformPoint(world);

        float s = cellSize, half = s * (1f - cellGap) * 0.5f;
        int col = Mathf.RoundToInt(p.x / s + Cols * 0.5f);
        int row = Mathf.RoundToInt(Rows * 0.5f - p.y / s);
        int rr = row - 1, cc = col - 1;
        if (rr < 0 || rr >= Rows || cc < 0 || cc >= Cols || !HasCell(rr, cc)) return false;
        Vector3 o = CellCenter(row, col);
        if (Mathf.Abs(p.x - o.x) > half || Mathf.Abs(p.y - o.y) > half) return false;
        if (results[rr, cc] != (int)CornerFold.Result.Corner) return false;   // 選べるのは ○ だけ
        r = rr; c = cc;
        return true;
    }

    // ---- 配置 ----------------------------------------------------------------------------
    // 表は 1 + Cols 列 × 1 + Rows 行（左端の列 = 形の絵、上端の行 = 枚数の絵）。ボードの中心が表の中心

    Vector3 CellCenter(int row, int col)   // row, col は見出しを含めた番号（0 = 見出し）
    {
        float x = (col - Cols * 0.5f) * cellSize;
        float y = (Rows * 0.5f - row) * cellSize;
        return new Vector3(x, y, 0f);
    }

    // ---- Mesh ----------------------------------------------------------------------------

    void Rebuild()
    {
        float s = cellSize;
        float half = s * (1f - cellGap) * 0.5f;   // マスの半分の大きさ

        // まだ試していないマス
        Clear();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (HasCell(r, c) && results[r, c] < 0)
                    AddRect(CellCenter(r + 1, c + 1) + new Vector3(0, 0, CellZ), half, half, 0f, SubCell);
        Apply(untriedMesh, 1, SubCell);

        Clear();
        // 板（表と裏）
        float bw = (Cols + 1) * s * 0.5f + s * 0.3f, bh = (Rows + 1) * s * 0.5f + s * 0.3f;
        var center = (CellCenter(0, 0) + CellCenter(Rows, Cols)) * 0.5f;
        AddRect(center, bw, bh, 0f, SubBack);
        AddRect(center, bw, bh, 0f, SubBack, back: true);
        if (pickArea != null)   // ○ を選ぶ当たり判定を板に合わせる（Mesh は毎フレーム変わりうるので、MeshCollider でなく箱にする）
        {
            var size = new Vector3(bw * 2f, bh * 2f, 0.01f);
            if (pickArea.center != center) pickArea.center = center;
            if (pickArea.size != size) pickArea.size = size;
        }

        // そろったあと: 板のふちに緑の枠（○ と同じ Material）。印の色は変えない
        if (completeShown)
        {
            float t = 0.1f * s;
            var z = new Vector3(0, 0, MarkZ);
            AddRect(center + z + new Vector3(0, bh - t / 2f, 0), bw, t / 2f, 0f, SubCorner);    // 上
            AddRect(center + z + new Vector3(0, -bh + t / 2f, 0), bw, t / 2f, 0f, SubCorner);   // 下
            AddRect(center + z + new Vector3(-bw + t / 2f, 0, 0), t / 2f, bh - t, 0f, SubCorner);   // 左
            AddRect(center + z + new Vector3(bw - t / 2f, 0, 0), t / 2f, bh - t, 0f, SubCorner);    // 右
        }

        // 見出し: 左端の列 = 形の絵（平らな辺が下）、上端の行 = 枚数ぶんの点を輪に並べた絵
        for (int r = 0; r < Rows; r++)
        {
            int p = MinSides + r;
            AddRegularPolygon(CellCenter(r + 1, 0) + new Vector3(0, -0.04f * s, MarkZ), 0.36f * s, p, -Mathf.PI / 2f + Mathf.PI / p, SubIcon);
        }
        for (int c = 0; c < Cols; c++)
        {
            int q = MinCount + c;
            var o = CellCenter(0, c + 1) + new Vector3(0, 0, MarkZ);
            for (int k = 0; k < q; k++)
            {
                float a = Mathf.PI / 2f + 2f * Mathf.PI * k / q;   // 真上から回る
                AddRegularPolygon(o + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.27f * s, 0.075f * s, 16, 0f, SubIcon);
            }
        }

        // 試したマスと印
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                if (!HasCell(r, c) || results[r, c] < 0) continue;
                var result = (CornerFold.Result)results[r, c];
                var o = CellCenter(r + 1, c + 1);
                float pop = 1f;
                if (popTime[r, c] >= 0f) { float k = 1f - popTime[r, c] / PopSeconds; pop = 1f + 0.6f * k * k; }

                bool hovered = r == hoverR && c == hoverC;
                AddRect(o + new Vector3(0, 0, CellZ), half, half, 0f, hovered ? SubIcon : SubCell);   // レイが当たった ○ はマスが明るい
                var m = o + new Vector3(0, 0, MarkZ);
                if (result == CornerFold.Result.Corner)
                {
                    if (hovered) pop *= 1.15f;
                    else if (completeShown) pop *= 1f + 0.07f * (0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * Time.time / BreathePeriod));
                    AddRing(m, 0.36f * s * pop, 0.25f * s * pop, 40, SubCorner);
                }
                else
                {
                    // ✕ = 斜めの棒 2 本。ぴったり平らは青の ✕（角にはならない。でも 3 章で「どこまでも敷き詰まる床」になる組なので区別する）
                    int sub = result == CornerFold.Result.Flat ? SubFlat : SubFail;
                    AddRect(m, 0.38f * s * pop, 0.065f * s * pop, Mathf.PI / 4f, sub);
                    AddRect(m, 0.38f * s * pop, 0.065f * s * pop, -Mathf.PI / 4f, sub);
                }
            }
        Apply(boardMesh, Submeshes, -1);
    }

    static readonly Vector3 Front = new Vector3(0f, 0f, -1f);   // 表の向き（見る人の側）

    // 中心 o、半幅 hx・半高さ hy、角度 angle だけ回した長方形
    void AddRect(Vector3 o, float hx, float hy, float angle, int sub, bool back = false)
    {
        float cs = Mathf.Cos(angle), sn = Mathf.Sin(angle);
        Vector3 ax = new Vector3(cs, sn, 0f) * hx, ay = new Vector3(-sn, cs, 0f) * hy;
        Vector3 n = back ? -Front : Front;
        int i0 = AddVert(o - ax - ay, n), i1 = AddVert(o + ax - ay, n), i2 = AddVert(o + ax + ay, n), i3 = AddVert(o - ax + ay, n);
        AddTri(i0, i1, i2, n, sub);
        AddTri(i0, i2, i3, n, sub);
    }

    void AddRegularPolygon(Vector3 o, float radius, int p, float angle0, int sub)
    {
        int ci = AddVert(o, Front);
        int first = verts.Count;
        for (int j = 0; j < p; j++)
        {
            float a = angle0 + 2f * Mathf.PI * j / p;
            AddVert(o + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * radius, Front);
        }
        for (int j = 0; j < p; j++) AddTri(ci, first + j, first + (j + 1) % p, Front, sub);
    }

    void AddRing(Vector3 o, float outer, float inner, int segments, int sub)
    {
        int first = verts.Count;
        for (int j = 0; j < segments; j++)
        {
            float a = 2f * Mathf.PI * j / segments;
            var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            AddVert(o + d * outer, Front);
            AddVert(o + d * inner, Front);
        }
        for (int j = 0; j < segments; j++)
        {
            int a = first + 2 * j, b = first + 2 * ((j + 1) % segments);
            AddTri(a, b, b + 1, Front, sub);
            AddTri(a, b + 1, a + 1, Front, sub);
        }
    }

    int AddVert(Vector3 p, Vector3 n) { verts.Add(p); norms.Add(n); return verts.Count - 1; }

    // Unity では Cross(b − a, c − a) が三角形 (a, b, c) の表側を向く。n と逆なら並びを入れ替える
    void AddTri(int a, int b, int c, Vector3 n, int sub)
    {
        bool flip = Vector3.Dot(Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]), n) < 0f;
        var list = tris[sub];
        list.Add(a);
        list.Add(flip ? c : b);
        list.Add(flip ? b : c);
    }

    void Clear()
    {
        verts.Clear(); norms.Clear();
        foreach (var t in tris) t.Clear();
    }

    // submeshes 個のサブメッシュを配る。only ≥ 0 なら、そのサブメッシュ 1 つだけを 0 番として配る
    void Apply(Mesh mesh, int submeshes, int only)
    {
        if (mesh == null) return;
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.subMeshCount = submeshes;
        if (only >= 0) mesh.SetTriangles(tris[only], 0);
        else for (int i = 0; i < submeshes; i++) mesh.SetTriangles(tris[i], i);
        mesh.RecalculateBounds();
    }
}
