using UnityEngine;
using UnityEngine.InputSystem;

// ゲームの基本の流れ:
//   元の空間（Hub）で多様体を掴む → 左 X で Dive in → 迷路（平面の世界）を歩く → ゴール（または左 X で降参）→ 元の空間へ戻る
//
// 入る迷路は「掴んでいる多様体（SurfaceMaze）の迷路」。元の空間で表面に線で描いてあるものと同じ迷路に入る。
// 多様体を何個か置いて SurfaceMaze の Seed を変えておけば、どの迷路に入るかは、どれを掴むかで決まる。
//
// 今は「切り替え」だけ（演出なし）: Dive in すると元の空間のものを全部消して、迷路を出し、スタートへ移る。
// 戻るときは逆。元の空間のものは、消えていた間も同じ場所に置いてあるので、戻ると同じ景色になる。
// Dive in の演出（小さくなって入る・平らになる）や Zoom out は、この切り替えの上に後から足す。
//
// 空の GameObject（Maze とは別。常にアクティブなもの）に付ける。Maze 自身に付けると、Maze を消したときにこの部品も止まる。
public class GameFlow : MonoBehaviour
{
    [Header("つなぐもの")]
    [Tooltip("元の空間にあるもの全部の親。Dive in 中はこれごと消える")]
    [SerializeField] GameObject hub;
    [SerializeField] MazeView maze;
    [SerializeField] MazeWalker walker;
    [Tooltip("動かす対象。XR Origin (XR Rig)")]
    [SerializeField] Transform rig;
    [Tooltip("XR Origin > Camera Offset > Main Camera")]
    [SerializeField] Transform head;

    [Header("操作と時間")]
    // 元の空間では「掴んで Dive in」、迷路の中では「降参して戻る」。左 X ボタン（XRI 標準では空きボタン）
    [SerializeField] InputAction diveAction =
        new InputAction("Dive", InputActionType.Button, "<XRController>{LeftHand}/primaryButton");
    [Tooltip("ゴールしてから元の空間に戻るまでの時間 [秒]。音と光が終わるくらい")]
    [SerializeField, Min(0f)] float returnDelay = 2f;

    enum State { Hub, InMaze }
    State state = State.Hub;

    // 元の空間を離れるときの頭の位置と Rig の向き（戻るときに復元）
    Vector3 hubHeadPosition;
    Quaternion hubRigRotation;
    float returnTimer = -1f;   // 負なら待っていない
    SurfaceMaze guide;         // Dive in に持ち込んだ多様体（迷路の中にいる間だけ非 null）

    void Awake()
    {
        // 起動時は元の空間から始める（Maze はエディタでは見えるように置いてあるが、遊ぶときは消えている）
        if (maze != null) maze.gameObject.SetActive(false);
        if (hub != null) hub.SetActive(true);
    }

    void OnEnable()
    {
        diveAction.Enable();
        if (walker != null) walker.GoalReached += OnGoal;
    }

    void OnDisable()
    {
        diveAction.Disable();
        if (walker != null) walker.GoalReached -= OnGoal;
    }

    void Update()
    {
        if (diveAction.WasPressedThisFrame())
        {
            if (state == State.Hub)
            {
                var held = FindHeldMaze();      // 掴んでいる多様体があるときだけ入れる
                if (held != null) DiveIn(held);
            }
            else ReturnToHub();                 // 迷路の中ならいつでも降参できる
        }

        if (returnTimer >= 0f)
        {
            returnTimer -= Time.deltaTime;
            if (returnTimer < 0f) ReturnToHub();
        }
    }

    // いま手に持っている多様体の迷路。無ければ null。元の空間（アクティブなもの）の中から探す
    SurfaceMaze FindHeldMaze()
    {
        foreach (var m in FindObjectsByType<SurfaceMaze>())
            if (m.IsHeld) return m;
        return null;
    }

    void OnGoal()
    {
        if (state == State.InMaze && returnTimer < 0f) returnTimer = returnDelay;   // 待っている間の二重ゴールは無視
    }

    void DiveIn(SurfaceMaze source)
    {
        hubHeadPosition = head.position;
        hubRigRotation = rig.rotation;

        // 掴んでいる多様体は、Hub の子から出して一番上の階層に置く。こうすると Hub を消しても消えず、手に持ったまま迷路に入れる。
        // （XR Grab Interactable は、掴んだときに自動で一番上へ出す設定が標準で ON。それに頼らず、ここで明示する）
        // 離すと元の親（Hub）へ戻る仕組みもあるが、Hub が消えている間は戻らず、その場に浮いたままになる（消えない）
        if (source.transform.parent != null) source.transform.SetParent(null, true);

        // 先に迷路を渡してから（hub を消すと Hub の中の物は非アクティブになるが、データはそのまま使える）
        maze.UseGrid(source.Grid, source.GoalCell);
        hub.SetActive(false);                 // Hub の中の物は全部消える。手に持った多様体だけ残る
        maze.gameObject.SetActive(true);      // OnEnable で、渡された迷路から平面の世界が作られる
        walker.PlaceAtCell(source.StartCell.x, source.StartCell.y);

        guide = source;
        guide.BeginGuide(walker);             // 手元の多様体の上に、迷路の中の自分の現在地と向きを光らせる
        state = State.InMaze;
    }

    void ReturnToHub()
    {
        returnTimer = -1f;
        maze.gameObject.SetActive(false);

        // 向きを戻してから、頭が出発した位置の真上に来るように動かす（向きを変えると頭の位置もずれるので、この順）
        rig.rotation = hubRigRotation;
        walker.MoveHeadTo(hubHeadPosition);

        hub.SetActive(true);

        if (guide != null)
        {
            guide.EndGuide();
            // 迷路の中で離していたら、一番上の階層に浮いたままなので Hub の子に戻す（まだ手に持っているなら、離したときに XRI が戻す）
            if (!guide.IsHeld) guide.transform.SetParent(hub.transform, true);
            guide = null;
        }
        state = State.Hub;
    }
}
