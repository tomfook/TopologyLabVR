using UnityEngine;
using UnityEngine.Rendering;

// トーラス迷路を、平面（床）と高い壁として描くコンポーネント。「Dive in した中の世界」の見た目担当。
//
// 役割の分担:
//   MazeGrid         … 迷路データ（壁の有無・生成・到達可能性）。Unity と無関係の純 C#
//   MazeMeshBuilder  … データから頂点・三角形を組み立てる（Mesh は作らない）
//   MazeView（これ） … 上の 2 つを呼んで Mesh を作り、MeshFilter に配る。Inspector のつまみもここ
//
// 周りのタイルも含めて 1 枚の Mesh にする。真ん中の 1 枚が本物で、残りは複製（継ぎ目の向こうが見えるように）。
// 当たり判定（Collider）は付けない。壁の判定は次の「歩く」で、格子の辺を見て自前でやる。
//
// Material は MeshRenderer の Materials に 5 つ入れる（サブメッシュの順）:
//   Element 0 = 壁 / 1 = 床 / 2 = 床（市松のもう片方） / 3 = スタートの目印 / 4 = ゴールの目印
[ExecuteAlways]                 // 再生していないエディタ上でも動かす（Scene ビューで迷路が見える）
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class MazeView : MonoBehaviour
{
    [Header("迷路")]
    [SerializeField, Range(2, 32)] int width = 8;      // 横のマス数（x 方向）
    [SerializeField, Range(2, 32)] int height = 8;     // 縦のマス数（y 方向 = ワールドの Z）
    [SerializeField] int seed = 1;                     // 同じ数字なら同じ迷路
    [Tooltip("この行を、ぐるっと一周する直線の通路にする。-1 なら作らない")]
    [SerializeField, Min(-1)] int lapRow = -1;
    [Tooltip("この列を、ぐるっと一周する直線の通路にする。-1 なら作らない")]
    [SerializeField, Min(-1)] int lapColumn = -1;

    [Header("寸法 [m]")]
    [SerializeField, Min(0.5f)] float cellSize = 2f;         // 1 マスの一辺
    [SerializeField, Min(0.1f)] float wallHeight = 2.5f;     // 壁の高さ。人の背より高く = 先が見えない。低くすると見渡せて簡単になる
    [SerializeField, Min(0.02f)] float wallThickness = 0.2f; // 壁の厚み（cellSize の半分まで）

    [Header("目印")]
    [Tooltip("スタートの柱の高さ [m]。壁（2.5m）より高いと、壁ごしに遠くからも見える。0 なら柱なし（床の板だけ）")]
    [SerializeField, Min(0f)] float startBeaconHeight = 6f;
    [Tooltip("ゴールにも柱を立てるか。ONだとどこからでもゴールが見えて簡単になる")]
    [SerializeField] bool goalBeacon = false;

    [Header("周りのタイル")]
    [Tooltip("真ん中の 1 枚の周りに何周ぶん複製を敷くか。1 = 3×3 枚、2 = 5×5 枚")]
    [SerializeField, Range(0, 4)] int tileRadius = 2;

    // 他のスクリプト（歩行など）が読む
    public MazeGrid Grid { get; private set; }
    public float CellSize => cellSize;
    public float WallThickness => Mathf.Min(wallThickness, cellSize * 0.5f);   // 実際に使う厚み（当たり判定もこれを使う）
    public Vector2Int StartCell => Vector2Int.zero;
    public Vector2Int GoalCell { get; private set; }   // スタートから歩数が最大のマス

    Mesh mesh;
    bool dirty = true;
    MazeGrid external;          // UseGrid で渡された迷路（null なら自分の設定で作る）
    Vector2Int externalGoal;
    readonly MazeMeshBuilder builder = new MazeMeshBuilder();

    void OnEnable() => Build();
    void OnValidate() => dirty = true;  // Inspector で値を変えたら「作り直し予約」だけ
    void Update() { if (dirty) Build(); }

    void OnDestroy()
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
    }

    // 迷路の種を替える。Maze が非アクティブのときに呼んでから SetActive(true) すると、OnEnable の Build でこの seed の迷路になる
    // （アクティブのときに呼べば、次の Update で作り直される）。再生中に替えた値は、再生をやめるともとに戻る
    // 外から迷路を渡している（UseGrid 済み）ときは無視される
    public void SetSeed(int newSeed)
    {
        seed = newSeed;
        dirty = true;
    }

    // 多様体（SurfaceMaze）が持っている迷路を、そのまま使う。Maze が非アクティブのときに呼んでから SetActive(true) する。
    // 渡した MazeGrid は作り直さず共有する（元の空間で表面に描いてある迷路と、同じものに入るため）。
    // 以後、Inspector の Width / Height / Seed / Lap は使われない。迷路の大きさ（マス数）は渡した grid のもの
    public void UseGrid(MazeGrid grid, Vector2Int goalCell)
    {
        external = grid;
        externalGoal = goalCell;
        dirty = true;
    }

    // マス (x, y)（真ん中のタイル内）の中心のワールド座標。床の高さ
    public Vector3 CellCenter(int x, int y)
        => transform.TransformPoint(new Vector3((x + 0.5f) * cellSize, 0f, (y + 0.5f) * cellSize));

    void Build()
    {
        dirty = false;

        if (external != null)
        {
            // 多様体から渡された迷路をそのまま使う（作り直さない）
            Grid = external;
            GoalCell = externalGoal;
        }
        else
        {
            Grid = new MazeGrid(width, height);
            Grid.Generate(seed);
            if (lapRow >= 0) Grid.CarveLapRow(lapRow);
            if (lapColumn >= 0) Grid.CarveLapColumn(lapColumn);

            var far = Grid.Farthest(StartCell.x, StartCell.y);
            GoalCell = new Vector2Int(far.x, far.y);
        }

        builder.Build(Grid, cellSize, wallHeight, WallThickness, tileRadius);
        builder.AddMarker(Grid, tileRadius, StartCell.x, StartCell.y, builder.StartTriangles, startBeaconHeight);
        builder.AddMarker(Grid, tileRadius, GoalCell.x, GoalCell.y, builder.GoalTriangles, goalBeacon ? startBeaconHeight : 0f);

        // 8×8・周り 2 周（5×5 枚）で約 4 万頂点。マス数 × タイル数に比例して増えるので、Quest 2 では重くなりすぎに注意
        if (builder.Vertices.Count > 200000)
            Debug.LogWarning($"MazeView: 頂点が {builder.Vertices.Count} 個あります。Quest 2 では重いので、マス数か Tile Radius を減らしてください。", this);

        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.hideFlags = HideFlags.DontSave; // 実行時に毎回作るのでシーンファイルには保存しない
        }
        mesh.Clear();
        mesh.name = "Maze";
        mesh.indexFormat = IndexFormat.UInt32;   // 頂点が 65535 個を超えうる（タイルを敷き詰めるため）
        mesh.SetVertices(builder.Vertices);
        mesh.SetNormals(builder.Normals);
        mesh.SetUVs(0, builder.Uvs);
        mesh.subMeshCount = 5;
        mesh.SetTriangles(builder.WallTriangles, 0);
        mesh.SetTriangles(builder.FloorTriangles, 1);
        mesh.SetTriangles(builder.FloorAltTriangles, 2);
        mesh.SetTriangles(builder.StartTriangles, 3);
        mesh.SetTriangles(builder.GoalTriangles, 4);
        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().sharedMesh = mesh;

        if (GetComponent<MeshRenderer>().sharedMaterials.Length < 5)
            Debug.LogWarning("MazeView: MeshRenderer の Materials を 5 つにしてください（壁 / 床 / 床の市松 / スタートの目印 / ゴールの目印）。足りない分は描かれません。", this);
    }
}
