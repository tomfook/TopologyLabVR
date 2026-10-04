using System;
using System.Collections.Generic;

// 迷路データ。UnityEngine には一切依存しない「ただの C# クラス」（MonoBehaviour ではない）。
//
// 迷路は (x, y) の格子上のグラフとして持つ。マス = 頂点、隣り合うマスの間の「壁があるかどうか」= 辺の状態。
// 端は貼り合わせてある（トーラス）:
//     x = Width にあたるマスは x = 0、y = Height にあたるマスは y = 0 と同じマス。どちらの向きも周期で、向きの反転はない。
// 3D のメッシュは「見せ方」にすぎず、迷路のロジック（生成・到達可能性・歩行の判定）はここで完結する。
// 将来メビウスの帯に載せるときは、貼り合わせの規則（Neighbor の回り込み方）だけが変わる。
//
// 壁は「マスとマスの間の辺」に持つ。各辺は 1 つだけ（マスごとに 4 辺持つのではない）:
//     wallEast[x, y]  : マス (x, y) とその東隣 (x+1, y) の間の辺
//     wallNorth[x, y] : マス (x, y) とその北隣 (x, y+1) の間の辺
// 西・南の辺は、隣のマスの東・北の辺として扱う。こうすると、隣り合うマスで壁の状態がずれることが構造上ない。
public enum MazeDir { East = 0, North = 1, West = 2, South = 3 }

public sealed class MazeGrid
{
    // 方向ごとの移動量。MazeDir の値で引く（East=0, North=1, West=2, South=3）
    static readonly int[] DX = { 1, 0, -1, 0 };
    static readonly int[] DY = { 0, 1, 0, -1 };

    public readonly int Width;
    public readonly int Height;

    readonly bool[] wallEast;   // true = 壁あり。添字は y * Width + x
    readonly bool[] wallNorth;

    public MazeGrid(int width, int height)
    {
        // 2 未満だと、東隣・西隣が自分自身になって辺の扱いが破綻する
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException("width / height は 2 以上にしてください。");
        Width = width;
        Height = height;
        wallEast = new bool[width * height];
        wallNorth = new bool[width * height];
        FillWalls();
    }

    // 負の数にも使える剰余（C# の % は負の数だと負になる）
    public static int Mod(int a, int n) => ((a % n) + n) % n;

    int Cell(int x, int y) => y * Width + x;

    void FillWalls()
    {
        for (int i = 0; i < wallEast.Length; i++) { wallEast[i] = true; wallNorth[i] = true; }
    }

    // マス (x, y) から方向 d へ 1 歩進んだマス。端は貼り合わせの規則で回り込む（トーラス: 両方向とも単純に周期）
    public (int x, int y) Neighbor(int x, int y, MazeDir d)
        => (Mod(x + DX[(int)d], Width), Mod(y + DY[(int)d], Height));

    // 辺を配列と添字に引き直す。西は「西隣の東」、南は「南隣の北」
    bool[] EdgeArray(MazeDir d) => (d == MazeDir.East || d == MazeDir.West) ? wallEast : wallNorth;

    int EdgeIndex(int x, int y, MazeDir d)
    {
        x = Mod(x, Width);
        y = Mod(y, Height);
        if (d == MazeDir.West) x = Mod(x - 1, Width);
        if (d == MazeDir.South) y = Mod(y - 1, Height);
        return Cell(x, y);
    }

    public bool HasWall(int x, int y, MazeDir d) => EdgeArray(d)[EdgeIndex(x, y, d)];

    public void SetWall(int x, int y, MazeDir d, bool wall) => EdgeArray(d)[EdgeIndex(x, y, d)] = wall;

    // ── 生成 ──

    // 「深さ優先で壁を掘る」迷路生成（再帰バックトラッカー）。全マスをちょうど 1 本の道でつなぐ全域木になる
    //   → 全マスがつながっている（ゴールに必ず着ける）、解けない迷路は出ない。
    // 隣マスの「訪問済み」は貼り合わせ後のマスで判定するので、端を越えてつながる通路も普通に掘られる。
    // 同じ seed なら同じ迷路。再帰は使わず、自前のスタックで回す（深くなってもスタックオーバーフローしない）。
    public void Generate(int seed)
    {
        FillWalls();
        var rng = new Random(seed);
        var visited = new bool[Width * Height];
        var stack = new Stack<int>();
        var candidates = new List<MazeDir>(4);

        visited[0] = true;
        stack.Push(0);
        while (stack.Count > 0)
        {
            int c = stack.Peek();
            int x = c % Width, y = c / Width;

            candidates.Clear();
            for (int k = 0; k < 4; k++)
            {
                var d = (MazeDir)k;
                var (nx, ny) = Neighbor(x, y, d);
                if (!visited[Cell(nx, ny)]) candidates.Add(d);
            }

            if (candidates.Count == 0) { stack.Pop(); continue; }   // 行き止まり: 1 つ戻る

            var pick = candidates[rng.Next(candidates.Count)];
            var (px, py) = Neighbor(x, y, pick);
            SetWall(x, y, pick, false);                             // 壁を壊して隣へ進む
            visited[Cell(px, py)] = true;
            stack.Push(Cell(px, py));
        }
    }

    // 行 y の東向きの壁を全部壊して、輪っかを丸ごと一周する直線の通路を作る（通路が x 方向にぐるっと一周して自分に戻る）。
    // 全域木に余分な通路を足すので、迷路は「ちょうど 1 本道」ではなくなる（輪ができる）が、全マスがつながっている性質は保たれる。
    public void CarveLapRow(int y)
    {
        y = Mod(y, Height);
        for (int x = 0; x < Width; x++) SetWall(x, y, MazeDir.East, false);
    }

    // 列 x の北向きの壁を全部壊して、y 方向に一周する直線の通路を作る
    public void CarveLapColumn(int x)
    {
        x = Mod(x, Width);
        for (int y = 0; y < Height; y++) SetWall(x, y, MazeDir.North, false);
    }

    // ── 調べる ──

    // 壊れている（通れる）辺の数。全域木なら Width * Height - 1
    public int PassageCount()
    {
        int n = 0;
        for (int i = 0; i < wallEast.Length; i++)
        {
            if (!wallEast[i]) n++;
            if (!wallNorth[i]) n++;
        }
        return n;
    }

    // スタート (sx, sy) から各マスまでの最短歩数（幅優先探索）。たどり着けないマスは -1。添字は y * Width + x
    public int[] Distances(int sx, int sy)
    {
        var dist = new int[Width * Height];
        for (int i = 0; i < dist.Length; i++) dist[i] = -1;

        var queue = new Queue<int>();
        dist[Cell(sx, sy)] = 0;
        queue.Enqueue(Cell(sx, sy));
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            int x = c % Width, y = c / Width;
            for (int k = 0; k < 4; k++)
            {
                var d = (MazeDir)k;
                if (HasWall(x, y, d)) continue;
                var (nx, ny) = Neighbor(x, y, d);
                int n = Cell(nx, ny);
                if (dist[n] >= 0) continue;
                dist[n] = dist[c] + 1;
                queue.Enqueue(n);
            }
        }
        return dist;
    }

    // スタートから一番遠い（歩数が最大の）マス。同点なら走査順（y 小さい順、x 小さい順）で最初のもの。ゴールの置き場所の既定にする
    public (int x, int y, int steps) Farthest(int sx, int sy)
    {
        var dist = Distances(sx, sy);
        int best = 0;
        for (int i = 1; i < dist.Length; i++) if (dist[i] > dist[best]) best = i;
        return (best % Width, best / Width, dist[best]);
    }

    // 全マスがスタートからたどり着けるか
    public bool IsFullyConnected(int sx, int sy)
    {
        foreach (int d in Distances(sx, sy)) if (d < 0) return false;
        return true;
    }
}
