using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

// 発見ボード: 角の工房で試した「何角形を何枚」の結果を、表のマスに付けていく。文字は使わない。
//   縦 = タイルの形（左端の列に、その形の絵）。上から三角・四角・五角・六角
//   横 = 枚数（上端の行に、枚数ぶんの点を輪に並べた絵 = ピンのまわりに何枚）。左から 2 枚 … 7 枚
//   結果: ○（緑の輪）= 角ができた / ✕（青）= ぴったり平ら / ✕（赤）= 重なる・ぺたんこ
//   まだ試していないマスは脈打つ（= ここも試せる）。マスは、その形で重なる枚数（CornerFold.MaxCount）までしか作らない
//   ○ が全部そろうと（ちょうど 5 つ）、チャイムだけ鳴って ○ がゆっくり息をし始める（= 次はこれを触る）。CornersFound を呼ぶ
//   ○ の立体を 5 つ全部育てると、ボード全体が緑に光ってチャイム。そのあとふちに緑の枠が残る。AllGrown を呼ぶ
//   ○ のマスを選ぶ（4 章）: レイ（手）を当てるとそのマスが明るくなり、グリップすると CornerPicked(p, q) を呼ぶ（SolidGrower が聞いて立体を育てる）
//     Board に XR Simple Interactable と Box Collider を付けておく（Box Collider の大きさはコードが板に合わせる）
//   育てたマス: SolidGrower の Grown を聞いて、そのマスの ○ を、くるくる回る小さな立体（緑）に置き換える。もう一度選べば育て直せる
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

    [Header("選ぶ")]
    [Tooltip("○ を選ぶボタン（グリップに加えて）。既定は右手のトリガー。左手のトリガーは実行時に足す")]
    [SerializeField] InputAction pickAction =
        new InputAction("Pick", InputActionType.Button, "<XRController>{RightHand}/{TriggerButton}");

    [Header("反応")]
    [SerializeField] Color completeColor = new Color(0.2f, 0.9f, 0.3f);
    [SerializeField, Range(0f, 1f)] float volume = 0.6f;

    // ○ が全部そろったとき（2 章のゴール）/ ○ の立体を全部育てたとき（4 章のゴール）。後で章をつなぐ台本がこれを聞く
    public event Action CornersFound;
    public event Action AllGrown;
    // ○ のマスが選ばれたとき (p, q)。SolidGrower がこれを聞いて立体を育てる
    public event Action<int, int> CornerPicked;
    // true の間は ○ を選べない（矢印も、レイを当てたときの明るさ・予告も出さない）。3 章の上演を見終わるまで。CornerTheater が切り替える
    public bool PickLocked { get; set; }

    public const int MinSides = 3, MaxSides = 6, MinCount = 2, MaxCount = 7;
    const int Rows = MaxSides - MinSides + 1;   // 4
    const int Cols = MaxCount - MinCount + 1;   // 6

    // サブメッシュ（= ボード本体の Material の番号）
    const int SubBack = 0, SubCell = 1, SubIcon = 2, SubCorner = 3, SubFlat = 4, SubFail = 5, Submeshes = 6;

    const float PopSeconds = 0.35f;     // 印が付いたときに、大きく出てから縮む時間
    const float CompleteDelay = 0.7f;   // 最後の ○ のあと、工房のチャイムと重ならないよう少し待つ
    const float GrownDelay = 1.2f;      // 最後の立体が閉じたあと、育てる側のチャイムと重ならないよう少し待つ
    const float CellZ = -0.002f, MarkZ = -0.004f;   // 板より手前（ボードの表は −Z 向き = 見る人の側）

    int[,] results = new int[Rows, Cols];   // −1 = まだ / それ以外 = (int)CornerFold.Result
    bool[,] grown = new bool[Rows, Cols];   // 立体を育てたマス（○ の代わりにミニチュアを出す）
    readonly Dictionary<int, PolyhedronGrowth> minis = new Dictionary<int, PolyhedronGrowth>();   // ミニチュアの形（p * 10 + q）
    readonly List<SolidGrower> growers = new List<SolidGrower>();
    const float MiniSpin = 0.6f;                  // ミニチュアの回る速さ [rad/秒]
    const float MiniTilt = 20f * Mathf.Deg2Rad;   // 少し手前に傾けて、上の面も見えるように
    float[,] popTime = new float[Rows, Cols];
    int cornerTotal, cornerFound, grownFound;
    bool cornersDone;       // ○ がそろった（2 章のゴール）
    bool cornersShown;      // そろったことを知らせた（チャイムのあと）。ここから ○ がゆっくり息をする = 次はこれを触る
    float cornersTimer = -1f;
    bool allGrown;          // ○ の立体を全部育てた（4 章のゴール）
    bool completeShown;     // 全部育てたことを見せた（光ってチャイムのあと）。ボードのふちに緑の枠を出す
    float grownTimer = -1f;

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

        // 立体が育ったという知らせも聞く
        growers.Clear();
        growers.AddRange(FindObjectsByType<SolidGrower>());
        foreach (var g in growers) g.Grown += OnGrown;

        // トリガーで選ぶ: 左手のトリガーも足す（InputAction は Enable の前でないとバインドを足せない）
        if (pickAction.bindings.Count < 2) pickAction.AddBinding("<XRController>{LeftHand}/{TriggerButton}");
        pickAction.Enable();
    }

    void OnDisable()
    {
        foreach (var w in listening) if (w != null) w.Tried -= OnTried;
        listening.Clear();
        foreach (var g in growers) if (g != null) g.Grown -= OnGrown;
        growers.Clear();
        pickAction.Disable();
    }

    void OnGrown(int sides, int count)
    {
        int r = sides - MinSides, c = count - MinCount;
        if (r < 0 || r >= Rows || c < 0 || c >= Cols || !HasCell(r, c)) return;
        if (results[r, c] != (int)CornerFold.Result.Corner || grown[r, c]) { dirty = true; return; }   // 育て直しは何もしない
        grown[r, c] = true;
        popTime[r, c] = 0f;   // 初めて育ったときだけポンと出す
        dirty = true;
        if (++grownFound == cornerTotal && !allGrown)
        {
            allGrown = true;
            grownTimer = 0f;
        }
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
        cornerTotal = 0; cornerFound = 0; grownFound = 0;
        cornersDone = false; cornersShown = false; cornersTimer = -1f;
        allGrown = false; completeShown = false; grownTimer = -1f;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                results[r, c] = -1;
                popTime[r, c] = -1f;
                grown[r, c] = false;
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
        if (result == CornerFold.Result.Corner && ++cornerFound == cornerTotal && !cornersDone)
        {
            cornersDone = true;
            cornersTimer = 0f;
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
        if (cornersShown) animating = true;    // ○ がそろったあとは ○ がゆっくり息をする（= 触れる）
        foreach (bool g in grown) if (g) { animating = true; break; }   // ミニチュアが回る
        int ar, ac;
        if (hoverR >= 0 || ArrowTarget(out ar, out ac)) animating = true;   // 予告のミニチュアが回る / 矢印が跳ねる
        if (dirty || animating) Rebuild();
        dirty = animating;   // 縮み終わった次のフレームでもう 1 回作り、ちょうど 1 倍で止める

        // ○ がそろった（2 章のゴール）: チャイムだけ。ボードは光らせない（まだ「候補が 5 つ」。本当に閉じるかは 4 章で確かめる）
        if (cornersTimer >= 0f)
        {
            cornersTimer += Time.deltaTime;
            if (cornersTimer >= CompleteDelay)
            {
                cornersTimer = -1f;
                cornersShown = true;
                dirty = true;
                if (source != null) source.PlayOneShot(chime, volume);
                CornersFound?.Invoke();
            }
        }

        // 5 つ全部が本当に閉じた（4 章のゴール = だから 5 つだけ）: ボード全体が緑に光ってチャイム。そのあと緑の枠が残る
        if (grownTimer >= 0f)
        {
            grownTimer += Time.deltaTime;
            if (grownTimer >= GrownDelay)
            {
                grownTimer = -1f;
                completeShown = true;
                dirty = true;
                if (boardGlow != null) boardGlow.Flash(completeColor);
                if (source != null) source.PlayOneShot(chime, volume);
                AllGrown?.Invoke();
            }
        }
    }

    // ---- ○ を選ぶ ------------------------------------------------------------------------

    void UpdatePicking()
    {
        if (!Application.isPlaying || picker == null || pickArea == null) return;

        int r = -1, c = -1;
        if (PickLocked)
        {
            // 選べない間: レイが当たっても何も起きない（明るくもならない）。押しっぱなしで開いたときに選ばれないよう、押した状態は覚えておく
            if (hoverR != -1 || hoverC != -1) { hoverR = hoverC = -1; dirty = true; }
            pickerWasSelected = picker.isSelected;
            return;
        }
        if (picker.isHovered)
            foreach (var it in picker.interactorsHovering)
                if (CellUnder(it.transform, out r, out c)) break;
        if (r != hoverR || c != hoverC) { hoverR = r; hoverC = c; dirty = true; }

        bool selected = picker.isSelected;
        if (selected && !pickerWasSelected)
        {
            int sr, sc;
            if (CellUnder(picker.firstInteractorSelecting.transform, out sr, out sc)) Pick(sr, sc);
        }
        pickerWasSelected = selected;

        // トリガーでも選べる（レイが ○ に当たっているとき）。グリップだけだと、トリガーを押す子には何も起きない
        if (pickAction.WasPressedThisFrame() && hoverR >= 0) Pick(hoverR, hoverC);
    }

    void Pick(int r, int c)
    {
        popTime[r, c] = 0f;   // 選んだ ○ がポンと跳ねる
        dirty = true;
        CornerPicked?.Invoke(MinSides + r, MinCount + c);
    }

    bool AnyGrowing()
    {
        foreach (var g in growers) if (g != null && g.Growing) return true;
        return false;
    }

    // 矢印が指す ○: まだ育てていない ○ のうち最初の 1 つ（一度に指すのは 1 つだけ）。指さないときは false
    bool ArrowTarget(out int tr, out int tc)
    {
        tr = tc = -1;
        if (!Application.isPlaying || !cornersShown || allGrown || PickLocked || hoverR >= 0 || AnyGrowing()) return false;
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                if (results[r, c] == (int)CornerFold.Result.Corner && !grown[r, c]) { tr = r; tc = c; return true; }
        return false;
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
                    if (grown[r, c])
                        AddMini(o, MinSides + r, MinCount + c, 0.4f * s * pop, SubCorner);   // 育てた立体のミニチュア（回る）
                    else if (hovered)
                        AddMini(o, MinSides + r, MinCount + c, 0.4f * s * pop, SubCell);     // 予告: 選ぶとこれが出てくる（緑ではない色）
                    else
                    {
                        if (!hovered && cornersShown) pop *= 1f + 0.07f * (0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * Time.time / BreathePeriod));
                        AddRing(m, 0.36f * s * pop, 0.25f * s * pop, 40, SubCorner);
                    }
                }
                else
                {
                    // ✕ = 斜めの棒 2 本。ぴったり平らは青の ✕（角にはならない。でも 3 章で「どこまでも敷き詰まる床」になる組なので区別する）
                    int sub = result == CornerFold.Result.Flat ? SubFlat : SubFail;
                    AddRect(m, 0.38f * s * pop, 0.065f * s * pop, Mathf.PI / 4f, sub);
                    AddRect(m, 0.38f * s * pop, 0.065f * s * pop, -Mathf.PI / 4f, sub);
                }
            }
        // 「これを選んで」の矢印: ピンの矢印と同じ形で、まだ育てていない ○ の真上から下向きに指し、上下に跳ねる
        int ar, ac;
        if (ArrowTarget(out ar, out ac))
        {
            float t = Mathf.Repeat(Time.time, ArrowPeriod) / ArrowPeriod;
            float bob = ArrowBob * s * (0.5f + 0.5f * Mathf.Cos(2f * Mathf.PI * t));   // 上で始まって、○ へ近づいて、戻る
            var tip = CellCenter(ar + 1, ac + 1) + new Vector3(0f, half + 0.06f * s + bob, ArrowZ);
            AddDownArrow(tip, s, SubIcon);
        }
        Apply(boardMesh, Submeshes, -1);
    }

    const float ArrowPeriod = 1.0f;   // 跳ねる周期 [秒]
    const float ArrowBob = 0.25f;     // 跳ねる幅（マスの大きさに対する割合）
    const float ArrowZ = -0.03f;      // 板より手前に浮かせる（上のマスに重なっても前に見える）

    // 下向きの立体の矢印（四角い軸 + 四角すいの頭。工房の矢印と同じ形）。tip = 先端
    void AddDownArrow(Vector3 tip, float s, int sub)
    {
        float headLen = 0.3f * s, headHalf = 0.17f * s, shaftLen = 0.4f * s, shaftHalf = 0.06f * s;
        Vector3[] sq = { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1) };
        Vector3 baseY = tip + Vector3.up * headLen, top = baseY + Vector3.up * shaftLen;
        for (int i = 0; i < 4; i++)
        {
            // 頭の 4 つの斜面
            Vector3 a = baseY + sq[i] * headHalf, b = baseY + sq[(i + 1) % 4] * headHalf;
            Vector3 n = Vector3.Cross(b - a, tip - a).normalized;
            if (Vector3.Dot(n, (a + b) * 0.5f - baseY) < 0f) n = -n;   // 外向きに揃える
            int i0 = AddVert(a, n), i1 = AddVert(b, n), i2 = AddVert(tip, n);
            AddTri(i0, i1, i2, n, sub);
            // 軸の 4 つの側面
            Vector3 c = baseY + sq[i] * shaftHalf, d = baseY + sq[(i + 1) % 4] * shaftHalf;
            Vector3 m = Vector3.Cross(d - c, Vector3.up).normalized;
            if (Vector3.Dot(m, (c + d) * 0.5f - baseY) < 0f) m = -m;
            int j0 = AddVert(c, m), j1 = AddVert(d, m), j2 = AddVert(d + Vector3.up * shaftLen, m), j3 = AddVert(c + Vector3.up * shaftLen, m);
            AddTri(j0, j1, j2, m, sub);
            AddTri(j0, j2, j3, m, sub);
        }
        // 頭の底（上向き）と軸のふた（上向き）
        int h0 = AddVert(baseY + sq[0] * headHalf, Vector3.up), h1 = AddVert(baseY + sq[1] * headHalf, Vector3.up),
            h2 = AddVert(baseY + sq[2] * headHalf, Vector3.up), h3 = AddVert(baseY + sq[3] * headHalf, Vector3.up);
        AddTri(h0, h1, h2, Vector3.up, sub); AddTri(h0, h2, h3, Vector3.up, sub);
        int t0 = AddVert(top + sq[0] * shaftHalf, Vector3.up), t1 = AddVert(top + sq[1] * shaftHalf, Vector3.up),
            t2 = AddVert(top + sq[2] * shaftHalf, Vector3.up), t3 = AddVert(top + sq[3] * shaftHalf, Vector3.up);
        AddTri(t0, t1, t2, Vector3.up, sub); AddTri(t0, t2, t3, Vector3.up, sub);
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

    // マスの中心 o の手前に、外接球の半径 radius の正多面体（p 角形が q 枚ずつ）を浮かせる。Y 軸まわりに回り、少し手前に傾く。
    // 閉じた凸の立体なので片面で足りる（裏は見えない）。面ごとの法線で陰が付いて立体に見える
    void AddMini(Vector3 o, int p, int q, float radius, int sub)
    {
        PolyhedronGrowth shape;
        if (!minis.TryGetValue(p * 10 + q, out shape))
        {
            shape = PolyhedronGrowth.Plan(p, q, 1f, 0f);
            minis[p * 10 + q] = shape;
        }
        if (shape == null) return;
        float scale = radius / shape.Verts[0].magnitude;
        float spin = Application.isPlaying ? Time.time * MiniSpin : 0.5f;
        var center = o + new Vector3(0f, 0f, MarkZ - radius);   // 板にめり込まないよう、半径ぶん手前へ
        foreach (var face in shape.Faces)
        {
            int first = verts.Count;
            foreach (int vi in face) AddVert(center + MiniTurn(shape.Verts[vi] * scale, spin), Vector3.zero);
            Vector3 n = Vector3.Cross(verts[first + 1] - verts[first], verts[first + 2] - verts[first]).normalized;   // 面のデータは外から見て時計回り → 外向き
            for (int j = first; j < verts.Count; j++) norms[j] = n;
            for (int j = 1; j < face.Length - 1; j++) AddTri(first, first + j, first + j + 1, n, sub);
        }
    }

    static Vector3 MiniTurn(Vector3 v, float spin) =>
        PolyhedronGrowth.Rotate(PolyhedronGrowth.Rotate(v, Vector3.up, spin), Vector3.right, -MiniTilt);   // 回して、上を手前（−Z）へ傾ける

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
