using UnityEngine;

// 迷路の中を歩くための処理を、毎フレーム「頭の位置」に対してかける。
//   ① 壁にめり込ませない（MazeCollision で円を押し出し、その分だけ XR Rig を動かす）
//   ② 真ん中のタイルの外へ出たら、周期ぶん XR Rig をずらして中へ戻す
//   ③ ゴールの板に乗ったら GoalReached を知らせる
//
// ② について: 迷路は貼り合わせ（トーラス）なので、タイル 1 枚ぶん（Width·cell, Height·cell）ずれた場所は
// 迷路の中では「同じ場所」。周りには同じ絵を敷き詰めて描いてあるので、ずらしても見た目は変わらない
// （継ぎ目をまたいだことに気づかない）。ずらすことで、いくら歩いても描いてある範囲（5×5 枚）から出ない。
//
// 動かし方: 移動は XR Rig（XR Origin）側で起きるので、頭ではなく Rig を動かす。
// XR Origin には CharacterController が付いていて、XRI の移動もそれ越しに動かしているので、こちらも同じ口を使う
// （Transform を直接書き換えると、CharacterController の位置とずれて元に戻されることがある）。
//
// 前提: Maze の Transform は水平（回転は Y 軸まわりだけ）で、スケールは 1。
// Maze（MazeView）と同じ GameObject に付けるのを想定。Rig と Head は Inspector で渡す。
[DefaultExecutionOrder(200)]   // XRI の移動や頭の追従より後に実行したい
public class MazeWalker : MonoBehaviour
{
    [SerializeField] MazeView maze;       // 空なら同じ GameObject の MazeView
    [Tooltip("動かす対象。Hierarchy の「XR Origin (XR Rig)」")]
    [SerializeField] Transform rig;
    [Tooltip("判定に使う頭の位置。XR Origin > Camera Offset > Main Camera")]
    [SerializeField] Transform head;
    [Tooltip("体の半径 [m]。壁から頭の中心がこれ以上離れる")]
    [SerializeField, Range(0.1f, 0.45f)] float radius = 0.25f;

    CharacterController body;
    bool onGoal;

    // 頭の現在地（マス単位。真ん中のタイルの中 [0, Width) × [0, Height)。x がマスの横、y が縦）と、見ている向き（床の上の単位ベクトル。
    // Maze のローカルの (x, z)）。Dive in の中で毎フレーム更新される。トーラスの表面の位置に直すのは SurfaceMaze の仕事
    public Vector2 GridPosition { get; private set; }
    public Vector2 Heading { get; private set; } = Vector2.up;

    // ゴールの板に乗った瞬間に 1 回呼ばれる（板から降りて、また乗るとまた呼ばれる）。音や演出は、これを聞く別の部品がやる
    public event System.Action GoalReached;

    void Awake()
    {
        if (maze == null) maze = GetComponent<MazeView>();
        if (rig != null) body = rig.GetComponent<CharacterController>();   // 無ければ null（Transform を直接動かす）
    }

    void Start()
    {
        if (rig == null || head == null || maze == null)
        {
            Debug.LogWarning("MazeWalker: Maze / Rig / Head のどれかが空です。Inspector で渡してください。", this);
            return;
        }
    }

    void LateUpdate()
    {
        if (maze == null || maze.Grid == null || rig == null || head == null) return;

        float cell = maze.CellSize;
        // 半径は 1 マスの半分より十分小さくしておく（通路に入れなくなるのを防ぐ）
        float r = Mathf.Min(radius, cell * 0.5f - maze.WallThickness * 0.5f - 0.01f);

        // 頭の位置を、迷路の平面の座標（Maze のローカル座標の x, z）に直す
        Vector3 local = maze.transform.InverseTransformPoint(head.position);
        Vector2 p = new Vector2(local.x, local.z);

        // ① 壁から押し出す
        Vector2 q = MazeCollision.Resolve(maze.Grid, cell, maze.WallThickness, p, r);

        // ② 真ん中のタイル [0, Width·cell) × [0, Height·cell) の外なら、周期ぶん戻す
        float periodX = maze.Grid.Width * cell, periodY = maze.Grid.Height * cell;
        Vector2 shift = new Vector2(-Mathf.Floor(q.x / periodX) * periodX, -Mathf.Floor(q.y / periodY) * periodY);

        // ゴールの板の上にいるか（板と同じ大きさで判定。周期ぶんずらしても答えは同じ）
        bool nowOnGoal = MazeCollision.OnCell(maze.Grid, cell, q, maze.GoalCell.x, maze.GoalCell.y, cell * MazeMeshBuilder.PadHalfRatio);
        if (nowOnGoal && !onGoal) GoalReached?.Invoke();
        onGoal = nowOnGoal;

        // 現在地と向きを公開する（手元の多様体に現在地を光らせる SurfaceMaze が読む）。動かなかったフレームでも更新する
        Vector2 inTile = q + shift;                                        // 真ん中のタイルの中の位置 [m]
        GridPosition = new Vector2(inTile.x / cell, inTile.y / cell);      // マス単位に直す: [0, Width) × [0, Height)
        Vector3 fwd = maze.transform.InverseTransformDirection(head.forward);
        Vector2 look = new Vector2(fwd.x, fwd.z);                          // 見ている向きを床に落としたもの
        if (look.sqrMagnitude > 0.01f) Heading = look.normalized;          // ほぼ真上・真下を向いているときは前の向きのまま

        Vector2 delta = (q - p) + shift;
        if (delta.sqrMagnitude < 1e-12f) return;

        // Maze のローカルの動きをワールドの動きに直して、Rig を動かす
        MoveRig(maze.transform.TransformVector(new Vector3(delta.x, 0f, delta.y)));
    }

    // 頭が水平に target（ワールド座標）の真上に来るように Rig を動かす。高さと向きは変えない。
    // Dive in で迷路のスタートへ移るとき、クリアして元の空間へ戻るときに使う（Maze が非アクティブでも呼べる）
    public void MoveHeadTo(Vector3 target)
    {
        if (rig == null || head == null) return;
        Vector3 d = target - head.position;
        d.y = 0f;
        MoveRig(d);
    }

    // 頭がマス (x, y)（真ん中のタイル）の中心の真上に来るように動かす
    public void PlaceAtCell(int x, int y)
    {
        if (maze == null) return;
        MoveHeadTo(maze.CellCenter(x, y));
    }

    void MoveRig(Vector3 worldDelta)
    {
        if (body != null && body.enabled) body.Move(worldDelta);
        else rig.position += worldDelta;
    }
}
