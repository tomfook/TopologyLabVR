using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// 「この多様体の迷路」を持って、表面に線で見せるコンポーネント。トーラス（Torus）と同じ GameObject に付ける。
//
// ── 考え方: 迷路は多様体の持ち物 ──
//   迷路のデータ（MazeGrid）はここが持つ。元の空間で見える表面の線も、Dive in した先の平面の迷路も、同じ MazeGrid から作る。
//   だから「位相的に同値な迷路が多様体の上に見える」が、作り方からして成り立つ（見比べて合わせるのではなく、同じデータを 2 通りに描いているだけ）。
//   トーラスを何個か置いて seed を変えれば、「どの迷路に入るか」は「どのトーラスを掴むか」になる。
//
// ── 貼り合わせ ──
//   今はトーラス専用。迷路の格子の貼り合わせ（x, y とも周期）が、トーラスの (u, v) の貼り合わせと同じ形だから、そのまま乗る。
//   メビウスの帯・クラインの壺は貼り合わせが違う（辺を反転して貼る）ので、迷路の側の貼り合わせから変える必要がある（後で）。
//
// ── 置き方（Inspector）──
//   Torus の子に空の GameObject を 3 つ作り、それぞれに SurfaceCurve を付けて、下の Walls / Start Mark / Goal Mark に入れる。
//   子はシーン上に置く（実行時に作った GameObject は Quest で左右に割れることがあった）。線の Mesh の中身だけ実行時に作る。
[DefaultExecutionOrder(300)]   // MazeWalker（200）が現在地を更新したあとに、現在地の目印を置く
public class SurfaceMaze : MonoBehaviour
{
    [Header("迷路")]
    [SerializeField, Range(2, 32)] int width = 8;      // 輪っか方向（u）のマス数
    [SerializeField, Range(2, 32)] int height = 8;     // 管の断面方向（v）のマス数
    [SerializeField] int seed = 1;                     // 同じ数字なら同じ迷路。トーラスごとに変えると別の迷路になる
    [Tooltip("この行を、ぐるっと一周する直線の通路にする。-1 なら作らない")]
    [SerializeField, Min(-1)] int lapRow = -1;
    [Tooltip("この列を、ぐるっと一周する直線の通路にする。-1 なら作らない")]
    [SerializeField, Min(-1)] int lapColumn = -1;

    [Header("表面に描く線（Torus の子の SurfaceCurve）")]
    [SerializeField] SurfaceCurve walls;       // 壁
    [SerializeField] SurfaceCurve startMark;   // スタートの印（輪）
    [SerializeField] SurfaceCurve goalMark;    // ゴールの印（輪）
    [Tooltip("マスの 1 辺を何分割して点を打つか。曲面に沿って曲がって見えるのに必要。増やすと滑らかだが重い")]
    [SerializeField, Range(1, 16)] int subdivisions = 4;

    [Header("現在地の目印（初級。Dive in の間だけ出る）")]
    [Tooltip("自分の現在地を示す球。Torus の子の Sphere（コライダーは外す）。大きさは Scale で決める")]
    [SerializeField] Transform positionMarker;
    [Tooltip("自分が向いている向きを示す小さな球。現在地のすぐ前に出る")]
    [SerializeField] Transform headingMarker;
    [Tooltip("向きの球を、現在地からどれだけ離すか（Torus のローカルの長さ）")]
    [SerializeField, Min(0.001f)] float headingDistance = 0.02f;
    [Tooltip("目印を面からどれだけ浮かせるか（Torus のローカルの長さ）。面に埋もれないように")]
    [SerializeField, Min(0f)] float lift = 0.012f;

    MazeGrid grid;
    Vector2Int goal;
    XRGrabInteractable grab;
    ParametricSurface surface;
    MazeWalker guideWalker;   // 今この多様体を「案内役」として持ち込んでいる迷路の歩行（無ければ null）

    // Dive in した先の迷路（MazeView）が、これを受け取って同じ迷路を作る
    public MazeGrid Grid { get { EnsureGrid(); return grid; } }
    public Vector2Int StartCell => Vector2Int.zero;
    public Vector2Int GoalCell { get { EnsureGrid(); return goal; } }   // スタートから歩数が最大のマス

    // 手で掴まれているか（同じ GameObject の XRGrabInteractable が選択中か）
    public bool IsHeld => grab != null && grab.isSelected;

    void Awake()
    {
        grab = GetComponent<XRGrabInteractable>();
        surface = GetComponent<ParametricSurface>();
        EnsureGrid();
        SetMarkersVisible(false);   // 目印は Dive in の間だけ

        // 迷路の線は、ペン（SurfaceProbe）の書き込み先にも「全部消す」にもならないようにする
        if (walls != null) walls.UserDrawable = false;
        if (startMark != null) startMark.UserDrawable = false;
        if (goalMark != null) goalMark.UserDrawable = false;
    }

    // SurfaceCurve の Awake（曲線の親や Mesh の用意）が全部終わってから描きたいので、Awake ではなく Start で描く
    void Start() => Draw();

    // ── 案内役: Dive in に持ち込んだ多様体の上に、迷路の中の自分の現在地と向きを光らせる ──
    // 平面の現在地（マス単位）を、表面の線と同じ対応（MazeOutline.Corner）で (u, v) に直し、曲面の式で 3D の点にする。
    // 壁の線と同じ式で位置を出すので、「いま立っているマス」が表面の同じマスに光る。

    public void BeginGuide(MazeWalker walker)
    {
        guideWalker = walker;
        SetMarkersVisible(true);
    }

    public void EndGuide()
    {
        guideWalker = null;
        SetMarkersVisible(false);
    }

    void SetMarkersVisible(bool visible)
    {
        if (positionMarker != null) positionMarker.gameObject.SetActive(visible);
        if (headingMarker != null) headingMarker.gameObject.SetActive(visible);
    }

    // MazeWalker（実行順 200）が現在地を更新したあとに置きたいので、このクラスの実行順は 300（クラスの属性）にしてある
    void LateUpdate()
    {
        if (guideWalker == null || surface == null || grid == null) return;

        Vector2 g = guideWalker.GridPosition;
        Vector2 uv = MazeOutline.Corner(grid, g.x, g.y);   // マス単位 → (u, v)。壁の線と同じ対応
        if (positionMarker != null) positionMarker.localPosition = OnSurface(uv);

        if (headingMarker != null)
        {
            // 向き: 平面での向き h（マス単位）を、(u, v) の変化の向きに直す。u, v とも「マスの数 → 2π」の比で伸び縮みする
            Vector2 h = guideWalker.Heading;
            Vector2 duv = new Vector2(h.x * 2f * Mathf.PI / grid.Width, h.y * 2f * Mathf.PI / grid.Height);
            // その向きに動いたときの 3D の速さ |∂p/∂u·du + ∂p/∂v·dv|。これで割って、表面に沿って一定の距離だけ進める
            Vector3 t = surface.dPdu(uv.x, uv.y) * duv.x + surface.dPdv(uv.x, uv.y) * duv.y;
            float s = headingDistance / Mathf.Max(t.magnitude, 1e-6f);
            headingMarker.localPosition = OnSurface(uv + duv * s);
        }
    }

    // (u, v) の点を、面の法線の向きに lift だけ浮かせた位置（Torus のローカル座標）
    Vector3 OnSurface(Vector2 uv)
        => surface.LocalPoint(uv.x, uv.y) + surface.LocalNormal(uv.x, uv.y) * lift;

    void EnsureGrid()
    {
        if (grid != null) return;
        grid = new MazeGrid(width, height);
        grid.Generate(seed);
        if (lapRow >= 0) grid.CarveLapRow(lapRow);
        if (lapColumn >= 0) grid.CarveLapColumn(lapColumn);
        var far = grid.Farthest(StartCell.x, StartCell.y);
        goal = new Vector2Int(far.x, far.y);
    }

    void Draw()
    {
        if (walls != null)
            walls.SetStrokes(MazeOutline.Walls(grid, subdivisions));

        // 印の輪の大きさは、Dive in した先のパッドと同じ（ゴール判定の範囲 = 輪の内側）
        if (startMark != null)
            startMark.SetStrokes(new List<List<Vector2>> {
                MazeOutline.CellLoop(grid, StartCell.x, StartCell.y, MazeMeshBuilder.PadHalfRatio, subdivisions) });
        if (goalMark != null)
            goalMark.SetStrokes(new List<List<Vector2>> {
                MazeOutline.CellLoop(grid, goal.x, goal.y, MazeMeshBuilder.PadHalfRatio, subdivisions) });
    }
}
