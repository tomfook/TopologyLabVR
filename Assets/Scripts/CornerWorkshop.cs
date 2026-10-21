using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// 角の工房: 1 つの角（頂点 = ピン）のまわりに正多角形のタイルを並べ、ピンを引っぱり上げて角を作ってみる。
//   ・すき間がある → 引っぱると折れ、すき間が閉じた所でパチンと止まる（緑 + チャイム）。手を離しても角のまま
//   ・ぴったり（三角 6 枚など）→ ピンは上がらず、全体がボヨンと揺れる（青 + ボヨン）
//   ・重なる（三角 7 枚など）→ 重なったタイルが赤い。引っぱると揺れる（赤 + ブブー）
//   ・2 枚 → 折れるが、閉じるとぺたんこに重なる（赤 + ブブー）
//
// 枚数を変える（文字なしで分かるように、「光る物は掴んで運べる」に乗せる）:
//   ・増やす: 横のトレーの位置で、予備のタイル（Tile）がふわふわ浮いて光っている。掴んで運ぶと、すき間に
//            「ここ」の枠が出る。近づけると枠が緑になり、離すとはまって 1 枚増える。それ以外で離すとトレーへ帰る
//            （枠は Fan の Mesh の一部として描く。別の GameObject にしたら Quest で左右の目に別々の位置で見えたため）
//   ・減らす: 並んだタイル（Fan）を掴むと、端の 1 枚が取れて手に残る（手の中身を予備のタイルに持ち替えさせる）。
//            すき間に戻せばまたはまる。ほかで離せばトレーへ帰る
//   ・折れているときに Fan を掴む / 予備のタイルを掴むと、まずほどけて平らに戻る
//   ・掴む前の合図: 並んだタイルにレイ（手）を当てると、平らなら「取れる端の 1 枚」だけが予備のタイルの色になる
//            （= これが手に来る）。折れているなら全体が明るくなる（= 掴むと全体がほどける）。2 枚のときは取れないので光らない
//
// 数式は CornerFold にある。ここは「入力を見て状態を変え、Mesh を作り直して配る」だけ。
[RequireComponent(typeof(AudioSource))]
public class CornerWorkshop : MonoBehaviour
{
    [Header("タイル")]
    [Tooltip("正何角形か（3 = 三角形）")]
    [SerializeField, Range(3, 6)] int sides = 3;
    [SerializeField, Range(2, 7)] int startCount = 3;
    [Tooltip("タイルの辺の長さ [m]")]
    [SerializeField, Min(0.02f)] float edgeLength = 0.15f;
    [Tooltip("タイルどうしのすき間（タイルを中心へ縮める割合）")]
    [SerializeField, Range(0f, 0.2f)] float inset = 0.04f;
    [Tooltip("予備のタイルのトレーの位置（このオブジェクトのローカル。x = 右、y = 手前(−)/奥(+)。辺の長さが単位）")]
    [SerializeField] Vector2 trayOffset = new Vector2(2.2f, -0.6f);

    [Header("つなぐもの（どれもこのオブジェクトの子。互いの子にはしない）")]
    [Tooltip("並んだタイル。MeshFilter / MeshRenderer（Material 6 つ）/ MeshCollider / XR Simple Interactable / Glow")]
    [SerializeField] XRSimpleInteractable fan;
    [Tooltip("頂点のピン（Sphere）。SphereCollider / XR Simple Interactable / Glow")]
    [SerializeField] XRSimpleInteractable pin;
    [Tooltip("予備のタイル。MeshFilter / MeshRenderer / MeshCollider / Rigidbody（Kinematic）/ XR Grab Interactable / Glow")]
    [SerializeField] XRGrabInteractable tile;
    [Tooltip("「ここをつまんで上へ」の矢印。MeshFilter / MeshRenderer（TileA を複製した Material）。コライダーなし")]
    [SerializeField] MeshFilter arrow;

    [Header("反応")]
    [SerializeField] Color cornerColor = new Color(0.2f, 0.9f, 0.3f);
    [SerializeField] Color flatColor = new Color(0.25f, 0.55f, 1f);
    [SerializeField] Color failColor = new Color(1f, 0.2f, 0.15f);
    [SerializeField, Range(0f, 1f)] float volume = 0.6f;
    [Tooltip("平ら・重なるときに、これだけ引っぱったら反応する [m]")]
    [SerializeField, Min(0.001f)] float tugDistance = 0.015f;
    [Tooltip("閉じる高さのこれだけ手前まで来たら、パチンと閉じる [m]")]
    [SerializeField, Min(0f)] float snapDistance = 0.006f;
    [Tooltip("予備のタイルの中心が、はめる場所の中心からこの距離（辺の長さが単位）以内なら、はまる")]
    [SerializeField, Range(0.1f, 1.5f)] float placeRadius = 0.6f;

    // 試した結果（ピンを引っぱって反応が出たとき）。後で「発見ボード」がこれを聞いて ○✕ を付ける
    public event Action<int, int, CornerFold.Result> Tried;

    public int Sides => sides;
    public int Count => count;
    public bool Closed => closed;

    const float GapAzimuth = -Mathf.PI / 2f;   // すき間をローカルの −Z（このオブジェクトの手前）へ向ける
    const float OverlapLift = 0.003f;          // 重なったタイルを少し浮かせる（同じ面でちらつかないように）
    const float NormalOffset = 0.0007f;        // タイルを自分の表側へ少しずらす（2 枚がぺたんこに重なったときのちらつき防止）
    const float SlotLift = 0.002f;
    const float HomeSeconds = 0.45f;

    // 「ここをつまんで上へ」の合図: 矢印がピンの上でスーッと上がって消えるのを繰り返し、合わせてピンが小さく跳ねる
    const float HintPeriod = 1.5f;     // 1 回の長さ [秒]
    const float HintRise = 0.06f;      // 矢印が上がる距離 [m]
    const float HintGap = 0.035f;      // ピンから矢印の根元までの距離 [m]
    const float PinHop = 0.008f;       // ピンが跳ねる高さ [m]
    Mesh arrowMesh;

    int count;
    float height;          // ピン（頂点）の高さ [ローカル座標の m]。タイルの辺の先が乗っている面から測る
    bool closed;
    bool pinWasHeld, fanWasHeld, tileWasHeld;
    float grabStartY, heightAtGrab;
    bool reacted;          // この掴みで、もう反応を出したか
    float wobbleTime = -1f; // 揺れの経過時間。負なら揺れていない
    float wobble;

    // 予備のタイルの帰り道
    float homeTimer = -1f; // 負なら帰っていない
    Vector3 homeFromPos; Quaternion homeFromRot;

    Mesh fanMesh, fanColliderMesh, tileMesh;   // 当たり判定はタイルだけの Mesh（枠は掴めない）
    MeshCollider fanCollider;
    Glow fanGlow, pinGlow;
    Renderer tileRenderer;
    Collider[] tileColliders;
    AudioSource source;
    AudioClip chime, boing, buzz, click;
    Vector2[] coef;        // 正多角形の形の係数（CornerFold.PolygonCoefficients）
    Vector3[] corners;
    readonly List<Vector3> verts = new List<Vector3>();
    readonly List<Vector3> norms = new List<Vector3>();
    // サブメッシュ: 0, 1 = タイル（交互の色）/ 2 = 重なったタイル / 3 = はめる場所の枠 / 4 = 近いときの枠 / 5 = 取れる端の 1 枚
    const int TakeSubmesh = 5;
    readonly List<int>[] tris = { new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>(), new List<int>() };
    readonly List<int> colliderTris = new List<int>();

    // 前回 Mesh を作ったときの状態（変わったときだけ作り直す）
    int builtCount = -1, builtSides = -1, builtSlot = -1; float builtHeight = -1f, builtWobble; bool builtClosed, builtTake;
    int slotState;         // はめる場所の枠: 0 = 出さない / 1 = 出す / 2 = 予備のタイルが近い（緑）
    bool takeHint;         // 取れる端の 1 枚を予備のタイルの色にする（並んだタイルにレイ・手が当たっていて、取れるとき）
    bool hasTakeMaterial;  // Fan の MeshRenderer に 6 つ目の Material があるか（無ければ今まで通り全体が明るくなる）
    bool colliderDirty = true;

    float L => edgeLength;
    bool CanAdd => count < CornerFold.MaxCount(sides);
    bool IsFlat => !closed && height <= 1e-4f;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        chime = ToneSynth.Chime();
        boing = ToneSynth.Boing();
        buzz = ToneSynth.Buzz();
        click = ToneSynth.Click();

        if (fan != null)
        {
            ResetLocal(fan.transform);
            fanMesh = NewMesh("CornerFan", fan.GetComponent<MeshFilter>());
            fanCollider = fan.GetComponent<MeshCollider>();
            fanGlow = fan.GetComponent<Glow>();
            if (fanGlow != null) fanGlow.Pulse = false;   // 並んだタイルは脈打たせない（手が近づくと明るくなるだけ）
            var mr = fan.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterials.Length < 6)
                Debug.LogWarning("CornerWorkshop: Fan の MeshRenderer に Material を 6 つ入れてください（0 = タイル / 1 = タイルのもう片方の色 / 2 = 重なったタイル / 3 = はめる場所の枠 / 4 = 近づいたときの枠 / 5 = 取れる端の 1 枚 = Spare）");
            hasTakeMaterial = mr != null && mr.sharedMaterials.Length >= 6 && mr.sharedMaterials[TakeSubmesh] != null;
            fanColliderMesh = new Mesh { name = "CornerFanCollider", hideFlags = HideFlags.DontSave };
        }
        if (tile != null)
        {
            tileMesh = NewMesh("SpareTile", tile.GetComponent<MeshFilter>());
            tileRenderer = tile.GetComponent<Renderer>();
            tileColliders = tile.GetComponents<Collider>();
        }
        if (pin != null) pinGlow = pin.GetComponent<Glow>();
        if (arrow != null)
        {
            ResetLocal(arrow.transform);
            arrowMesh = NewMesh("PullArrow", arrow);
            BuildArrowMesh();
        }
        if (pin == null || fan == null || tile == null)
            Debug.LogWarning("CornerWorkshop: Fan / Pin / Tile を全部入れてください");

        count = Mathf.Clamp(startCount, 2, CornerFold.MaxCount(sides));
        SetupShape();
        PlaceTileHome(0f);
    }

    static void ResetLocal(Transform t)
    {
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;
        t.localScale = Vector3.one;
    }

    static Mesh NewMesh(string name, MeshFilter filter)
    {
        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        mesh.MarkDynamic();   // 何度も書き換える Mesh だと Unity に伝える
        if (filter != null) filter.sharedMesh = mesh;
        return mesh;
    }

    void OnDestroy()
    {
        if (fanMesh != null) Destroy(fanMesh);
        if (fanColliderMesh != null) Destroy(fanColliderMesh);
        if (tileMesh != null) Destroy(tileMesh);
    }

    // 形（正何角形か）が決まったら、係数と予備のタイルの Mesh を作る
    void SetupShape()
    {
        coef = CornerFold.PolygonCoefficients(sides);
        corners = new Vector3[sides];
        BuildTileMesh();
    }

    // 再生中に Inspector で Sides を変えたら、そこからやり直す（四角・五角・六角も試せる）
    void OnValidate()
    {
        if (!Application.isPlaying || coef == null || coef.Length == sides) return;
        SetupShape();
        count = Mathf.Clamp(count, 2, CornerFold.MaxCount(sides));
        ResetFold();
    }

    void ResetFold()
    {
        closed = false;
        height = 0f;
        reacted = false;
    }

    void Update()
    {
        var result = CornerFold.Classify(sides, count);

        // ---- 並んだタイルを掴む: ほどく / 端の 1 枚を取る --------------------------
        bool fanHeld = fan != null && fan.isSelected;
        if (fanHeld && !fanWasHeld)
        {
            if (!IsFlat) closed = false;                    // 折れているなら、まずほどく（下で平らへ戻る）
            else if (count > 2 && tile != null && !tile.isSelected) TakeLastTile();
        }
        fanWasHeld = fan != null && fan.isSelected;

        // ---- 予備のタイル -------------------------------------------------------------
        bool tileHeld = tile != null && tile.isSelected;
        if (tileHeld)
        {
            homeTimer = -1f;
            if (!tileWasHeld) closed = false;              // 持ったら、折れている角はほどく
        }
        if (!tileHeld && tileWasHeld)                       // 離した瞬間
        {
            if (CanAdd && IsFlat && NearSlot())
            {
                count++;
                ResetFold();
                source.PlayOneShot(click, volume);
                PlaceTileHome(0f);                          // はまった（並びの中に同じタイルが現れる）。予備はトレーへ瞬間移動
            }
            else StartHoming();
        }
        tileWasHeld = tileHeld;
        UpdateTileIdle(tileHeld);

        result = CornerFold.Classify(sides, count);
        bool foldable = CornerFold.CanFold(sides, count);
        float hMax = CornerFold.MaxHeight(sides, count) * L;

        // ---- ピンを引っぱる ----------------------------------------------------------
        bool pinHeld = pin != null && pin.isSelected;
        if (pinHeld)
        {
            // 掴んでいる手（コントローラー）の高さの変化だけを使う。近くで握っても、レイで遠くから掴んでも同じ
            float y = transform.InverseTransformPoint(pin.firstInteractorSelecting.transform.position).y;
            if (!pinWasHeld) { grabStartY = y; heightAtGrab = height; reacted = closed; }
            float raw = heightAtGrab + (y - grabStartY);

            if (foldable)
            {
                if (raw >= hMax - snapDistance)
                {
                    height = hMax;
                    if (!closed) { closed = true; React(result); }
                }
                else
                {
                    height = Mathf.Clamp(raw, 0f, hMax);
                    closed = false;
                }
            }
            else
            {
                height = 0f;   // 折れない。ピンは上がらない
                if (!reacted && raw > tugDistance) { reacted = true; React(result); }
            }
        }
        else if (!closed && height > 0f)
        {
            height *= Mathf.Exp(-12f * Time.deltaTime);   // 離したら平らへ戻る（だんだんゆっくり）
            if (height < 1e-4f) height = 0f;
        }
        pinWasHeld = pinHeld;

        // ---- 揺れ（ボヨン）-----------------------------------------------------------
        wobble = 0f;
        if (wobbleTime >= 0f)
        {
            wobbleTime += Time.deltaTime;
            wobble = 0.012f * Mathf.Exp(-5f * wobbleTime) * Mathf.Sin(2f * Mathf.PI * 4f * wobbleTime);
            if (wobbleTime > 1.2f) { wobbleTime = -1f; wobble = 0f; }
        }

        // ---- 見た目 ------------------------------------------------------------------
        if (fanGlow != null)
        {
            if (closed) fanGlow.SetTint(result == CornerFold.Result.Corner ? cornerColor : failColor, 0.35f);
            else fanGlow.ClearTint();
        }
        if (pinGlow != null) pinGlow.Pulse = !closed && !tileHeld;   // 角ができるまでは「引っぱって」と脈打つ

        // 並んだタイルにレイ（手）が当たっているとき、掴むと何が起きるかを先に見せる
        //   平ら: 取れる端の 1 枚だけが予備のタイルの色（= これが手に来る）。2 枚なら取れないので何も変えない
        //   折れている: 全体が明るくなる（= 掴むと全体がほどける）。Glow の「近づくと明るく」をそのまま使う
        bool fanHovered = fan != null && fan.isHovered && !fan.isSelected && !tileHeld;
        takeHint = hasTakeMaterial && fanHovered && IsFlat && count > 2;
        if (fanGlow != null) fanGlow.HoverGlow = !(hasTakeMaterial && IsFlat);

        // はめる場所の枠: 予備のタイルを持っていて、はめられるときだけ。近づくと緑
        slotState = !(tileHeld && CanAdd && IsFlat) ? 0 : NearSlot() ? 2 : 1;

        if (count != builtCount || sides != builtSides || height != builtHeight || wobble != builtWobble || closed != builtClosed || slotState != builtSlot || takeHint != builtTake)
            Rebuild(result, foldable);

        // MeshCollider の作り直しは少し重いので、ピンを引っぱっている間はしない
        if (colliderDirty && !pinHeld && fanCollider != null)
        {
            fanCollider.sharedMesh = null;
            fanCollider.sharedMesh = fanColliderMesh;
            colliderDirty = false;
        }

        // 合図はピンがまだ引かれていないときだけ（掴んでいる間・予備のタイルを持っている間・角が閉じたあとは出さない）
        bool hint = !pinHeld && !tileHeld && IsFlat;
        float hop = 0f;
        if (arrow != null)
        {
            float t = Mathf.Repeat(Time.time, HintPeriod) / HintPeriod;            // 0 → 1 を繰り返す
            float rise = 1f - (1f - t) * (1f - t);                                 // 出だしが速く、だんだんゆっくり
            float size = Mathf.Min(1f, t / 0.12f) * Mathf.Min(1f, (1f - t) / 0.25f);  // ふっと出て、上で小さくなって消える
            arrow.transform.localPosition = new Vector3(0f, height + wobble + HintGap + HintRise * rise, 0f);
            arrow.transform.localScale = Vector3.one * Mathf.Max(size, 1e-3f);
            var r = arrow.GetComponent<Renderer>();
            if (r != null) r.enabled = hint;
            if (hint && t < 0.3f) hop = PinHop * Mathf.Sin(Mathf.PI * t / 0.3f);  // 矢印が出るのに合わせてぴょこっと
        }
        if (pin != null) pin.transform.localPosition = new Vector3(0f, height + wobble + hop, 0f);

    }

    // ---- 予備のタイル ------------------------------------------------------------------

    // 端の 1 枚を取って、手の中身をそのタイルに持ち替えさせる
    void TakeLastTile()
    {
        // 取るタイル（今の並びの k = count − 1）の位置と向き
        float alpha = CornerFold.InteriorAngle(sides);
        Vector3 c; Quaternion r;
        FlatTilePose(alpha, count, count - 1, out c, out r);

        var interactor = fan.firstInteractorSelecting;
        var manager = fan.interactionManager;
        count--;
        ResetFold();

        ShowTile(true);
        tile.transform.SetPositionAndRotation(transform.TransformPoint(c), transform.rotation * r);
        if (manager != null && interactor != null)
        {
            manager.SelectExit(interactor, fan);    // 並んだタイルを離させて
            manager.SelectEnter(interactor, tile);  // 取れたタイルを掴ませる
        }
        tileWasHeld = tile.isSelected;
        if (!tile.isSelected) StartHoming();        // 持ち替えに失敗したら、取れたタイルはトレーへ飛んで帰る
    }

    void StartHoming()
    {
        if (tile == null) return;
        homeFromPos = tile.transform.position;
        homeFromRot = tile.transform.rotation;
        homeTimer = 0f;
    }

    // 持っていないときの予備のタイル: 帰り道の途中なら帰る、トレーにいればふわふわ浮く。足せないときは隠す
    void UpdateTileIdle(bool held)
    {
        if (tile == null || held) { if (tile != null) ShowTile(true); return; }
        if (homeTimer >= 0f)
        {
            homeTimer += Time.deltaTime;
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, homeTimer / HomeSeconds));
            Vector3 p; Quaternion r;
            HomePose(Time.time, out p, out r);
            tile.transform.SetPositionAndRotation(Vector3.Lerp(homeFromPos, p, k), Quaternion.Slerp(homeFromRot, r, k));
            if (homeTimer >= HomeSeconds) homeTimer = -1f;
            ShowTile(true);
            return;
        }
        PlaceTileHome(Time.time);
        ShowTile(CanAdd);
    }

    void PlaceTileHome(float time)
    {
        if (tile == null) return;
        Vector3 p; Quaternion r;
        HomePose(time, out p, out r);
        tile.transform.SetPositionAndRotation(p, r);
    }

    // トレーの位置（ワールド）。ふわふわ上下に浮く（動いている物は目に入りやすい）
    void HomePose(float time, out Vector3 p, out Quaternion r)
    {
        var local = new Vector3(trayOffset.x * L, 0.02f + 0.012f * Mathf.Sin(2f * Mathf.PI * time / 1.6f), trayOffset.y * L);
        p = transform.TransformPoint(local);
        r = transform.rotation;
    }

    // 隠すときは、Renderer とコライダーだけ切る（GameObject ごと消すと XR の登録が外れて、持ち替えができなくなる）
    void ShowTile(bool show)
    {
        if (tileRenderer != null) tileRenderer.enabled = show;
        if (tileColliders != null) foreach (var col in tileColliders) col.enabled = show;
    }

    bool NearSlot()
    {
        if (tile == null) return false;
        float alpha = CornerFold.InteriorAngle(sides);
        Vector3 c; Quaternion r;
        FlatTilePose(alpha, count, count, out c, out r);
        return Vector3.Distance(tile.transform.position, transform.TransformPoint(c)) < placeRadius * L * transform.lossyScale.x;
    }

    // 平らに並べたときのタイル k の中心（ローカル）と向き（予備のタイルの Mesh の +Z が頂点を向く向き）
    void FlatTilePose(float alpha, int q, int k, out Vector3 center, out Quaternion rot)
    {
        Vector3 e = CornerFold.EdgeDirection(alpha, alpha, q, GapAzimuth, k);
        Vector3 f = CornerFold.EdgeDirection(alpha, alpha, q, GapAzimuth, k + 1);
        CornerFold.TileCorners(Vector3.zero, e, f, L, coef, corners);
        center = Vector3.zero;
        foreach (var p in corners) center += p;
        center /= corners.Length;
        rot = Quaternion.LookRotation(-center, Vector3.up);   // 中心から頂点（原点）へ向く
    }

    // 予備のタイルの Mesh: 中心が原点、XZ 平面に寝た正多角形。頂点 0 が +Z を向く。両面
    // 上向きの矢印（四角い軸 + 四角すいの頭）。根元が原点、上が +Y。どの向きから見ても矢印に見えるよう立体にする
    void BuildArrowMesh()
    {
        if (arrowMesh == null) return;
        Clear();
        const float shaftHalf = 0.005f, shaftLen = 0.03f, headHalf = 0.014f, headLen = 0.025f;
        var up = Vector3.up;
        // 軸の 4 つの側面
        Vector3[] sq = { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1) };
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = sq[i] * shaftHalf, b = sq[(i + 1) % 4] * shaftHalf;
            Vector3 n = Vector3.Cross(b - a, up).normalized;
            if (Vector3.Dot(n, (a + b) * 0.5f) < 0f) n = -n;   // 外向きに揃える
            SetCorners(a, b, b + up * shaftLen, a + up * shaftLen);
            AddPolygon(n, 0);
        }
        // 頭の底面（下向き）と 4 つの斜面
        Vector3 baseY = up * shaftLen, tip = up * (shaftLen + headLen);
        SetCorners(sq[0] * headHalf + baseY, sq[1] * headHalf + baseY, sq[2] * headHalf + baseY, sq[3] * headHalf + baseY);
        AddPolygon(Vector3.down, 0);
        for (int i = 0; i < 4; i++)
        {
            Vector3 a = sq[i] * headHalf + baseY, b = sq[(i + 1) % 4] * headHalf + baseY;
            Vector3 n = Vector3.Cross(b - a, tip - a).normalized;
            if (Vector3.Dot(n, (a + b) * 0.5f - baseY) < 0f) n = -n;
            SetCorners(a, b, tip);
            AddPolygon(n, 0);
        }
        Apply(arrowMesh, 1);
        corners = new Vector3[sides];   // SetCorners で差し替えた作業用の配列を戻す
    }

    void SetCorners(params Vector3[] ps) => corners = ps;

    void BuildTileMesh()
    {
        if (tileMesh == null) return;
        Clear();
        float radius = L / (2f * Mathf.Sin(Mathf.PI / sides)) * (1f - inset);
        for (int j = 0; j < sides; j++)
        {
            float a = 2f * Mathf.PI * j / sides;
            corners[j] = new Vector3(radius * Mathf.Sin(a), 0f, radius * Mathf.Cos(a));
        }
        AddPolygon(Vector3.up, 0);
        AddPolygon(Vector3.down, 0);
        Apply(tileMesh, 1);
        var col = tile != null ? tile.GetComponent<MeshCollider>() : null;
        if (col != null) { col.sharedMesh = null; col.sharedMesh = tileMesh; }
    }

    // ---- 反応 --------------------------------------------------------------------------

    void React(CornerFold.Result result)
    {
        Tried?.Invoke(sides, count, result);
        switch (result)
        {
            case CornerFold.Result.Corner:
                if (fanGlow != null) fanGlow.Flash(cornerColor);
                source.PlayOneShot(chime, volume);
                break;
            case CornerFold.Result.Flat:
                if (fanGlow != null) fanGlow.Flash(flatColor);
                source.PlayOneShot(boing, volume);
                wobbleTime = 0f;
                break;
            default:   // Overlap / TooFew
                if (fanGlow != null) fanGlow.Flash(failColor);
                source.PlayOneShot(buzz, volume);
                if (result == CornerFold.Result.Overlap) wobbleTime = 0f;
                break;
        }
    }

    // ---- Mesh ---------------------------------------------------------------------------

    void Rebuild(CornerFold.Result result, bool foldable)
    {
        // 形が変わったときだけ当たり判定を作り直す（色だけの変化で作り直すと、レイの当たりが一瞬外れて合図がちらつくおそれがある）
        bool shapeChanged = count != builtCount || sides != builtSides || height != builtHeight || wobble != builtWobble || closed != builtClosed;
        builtCount = count; builtSides = sides; builtHeight = height; builtWobble = wobble; builtClosed = closed; builtSlot = slotState; builtTake = takeHint;

        float alpha = CornerFold.InteriorAngle(sides);
        float phi = !foldable ? alpha
                  : closed ? CornerFold.ClosedPhi(count)                       // 閉じたら式の誤差なしでぴったり
                  : CornerFold.PhiFromHeight(alpha, height / L);
        var apex = new Vector3(0f, height + wobble, 0f);

        // 並んだタイル
        Clear();
        for (int k = 0; k < count; k++)
        {
            bool over = result == CornerFold.Result.Overlap && (k + 1) * alpha > 2f * Mathf.PI + 1e-4f;
            int sub = takeHint && k == count - 1 ? TakeSubmesh : over ? 2 : k % 2;
            AddTile(apex, alpha, phi, count, k, sub, over ? OverlapLift : 0f);
        }
        if (shapeChanged) { ApplyCollider(fanColliderMesh); colliderDirty = true; }   // 当たり判定はタイルだけ

        // はめる場所の枠: 今の並びの次（k = count）の位置に、平らな状態で少し浮かせる。サブメッシュ 3（ふだん）/ 4（近い = 緑）
        if (slotState > 0)
            AddTile(new Vector3(0f, wobble, 0f), alpha, alpha, count, count, slotState == 2 ? 4 : 3, SlotLift, true);
        Apply(fanMesh, hasTakeMaterial ? 6 : 5);
    }

    // 当たり判定用: タイルのサブメッシュ（0, 1, 2, 5）を 1 つにまとめる。枠（3, 4）は入れない = 掴めない
    void ApplyCollider(Mesh mesh)
    {
        if (mesh == null) return;
        colliderTris.Clear();
        colliderTris.AddRange(tris[0]); colliderTris.AddRange(tris[1]); colliderTris.AddRange(tris[2]); colliderTris.AddRange(tris[TakeSubmesh]);
        mesh.Clear();
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.subMeshCount = 1;
        mesh.SetTriangles(colliderTris, 0);
        mesh.RecalculateBounds();
    }

    const float FrameWidth = 0.22f;   // 枠の太さ（中心から角までの距離に対する割合）

    void AddTile(Vector3 apex, float alpha, float phi, int q, int k, int submesh, float lift, bool frame = false)
    {
        Vector3 e = CornerFold.EdgeDirection(alpha, phi, q, GapAzimuth, k);
        Vector3 f = CornerFold.EdgeDirection(alpha, phi, q, GapAzimuth, k + 1);
        CornerFold.TileCorners(apex, e, f, L, coef, corners);

        Vector3 n = Vector3.Cross(f, e).normalized;   // 平らなときは真上を向く（タイルの表）
        Vector3 c = Vector3.zero;
        foreach (var p in corners) c += p;
        c /= corners.Length;
        Vector3 shift = n * NormalOffset + Vector3.up * lift;
        for (int j = 0; j < corners.Length; j++)
            corners[j] = c + (corners[j] - c) * (1f - inset) + shift;   // 中心へ少し縮めて、タイルの境目を見せる

        if (frame)
        {
            AddFrame(c + shift, n, submesh);
            AddFrame(c + shift, -n, submesh);
            return;
        }
        AddPolygon(n, submesh);
        AddPolygon(-n, submesh);   // 裏の面も作る（折れると裏側も見えるので両面）
    }

    // corners を、法線 n の側から見て表になる向きで扇形に三角形分割して足す
    void AddPolygon(Vector3 n, int submesh)
    {
        int s = verts.Count;
        foreach (var p in corners) { verts.Add(p); norms.Add(n); }
        // Unity では Cross(b − a, c − a) が三角形 (a, b, c) の表側を向く。n と逆なら並びを入れ替える
        bool flip = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]), n) < 0f;
        var list = tris[submesh];
        for (int i = 1; i < corners.Length - 1; i++)
        {
            list.Add(s);
            list.Add(flip ? s + i + 1 : s + i);
            list.Add(flip ? s + i : s + i + 1);
        }
    }

    // corners の輪郭を太さのある枠にして足す: 外側 = corners、内側 = 中心 c へ縮めたもの。辺ごとに四角形 1 枚（三角形 2 枚）
    void AddFrame(Vector3 c, Vector3 n, int submesh)
    {
        int p = corners.Length;
        int s = verts.Count;
        foreach (var o in corners) { verts.Add(o); norms.Add(n); }
        foreach (var o in corners) { verts.Add(c + (o - c) * (1f - FrameWidth)); norms.Add(n); }
        var list = tris[submesh];
        for (int j = 0; j < p; j++)
        {
            int a = s + j, b = s + (j + 1) % p, ai = s + p + j, bi = s + p + (j + 1) % p;
            bool flip = Vector3.Dot(Vector3.Cross(verts[b] - verts[a], verts[bi] - verts[a]), n) < 0f;
            if (!flip) { list.Add(a); list.Add(b); list.Add(bi); list.Add(a); list.Add(bi); list.Add(ai); }
            else { list.Add(a); list.Add(bi); list.Add(b); list.Add(a); list.Add(ai); list.Add(bi); }
        }
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
        mesh.subMeshCount = submeshes;   // サブメッシュ i は MeshRenderer の Material i で描かれる
        for (int i = 0; i < submeshes; i++) mesh.SetTriangles(tris[i], i);
        mesh.RecalculateBounds();
    }
}
