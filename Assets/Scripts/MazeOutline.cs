using System.Collections.Generic;
using UnityEngine;

// 迷路（MazeGrid）を、トーラスの (u, v) 平面の「線の点列」にする。純ロジック（Unity の部品は Vector2 / Mathf だけ）。
//
// 対応: マス (x, y) ↔ パラメータ長方形の [x·2π/W, (x+1)·2π/W] × [y·2π/H, (y+1)·2π/H]
//        x ↔ u（輪っかを回る向き）、y ↔ v（管の断面を回る向き）。
//   Dive in した先の平面（x → ワールド X、y → ワールド Z）と、トーラスを外から見たときの (u, v) は、向き（右手・左手）が同じ。
//   → 平面を上から見た迷路と、トーラスを外から見た迷路は、鏡像にならず「同じ迷路」に見える。
//   迷路は (u, v) 長方形の上に描くので、貼り合わせ（u, v とも 2π で元に戻る）は MazeGrid の Mod の貼り合わせとぴったり重なる。
//
// 出す点は、継ぎ目をまたいで連続になるよう u, v を 2π 以上まで伸ばしてよい（曲面の式は周期的なのでそのまま評価できる）。
//   長い壁は 1 本の折れ線にまとめる（チューブの両端のふたが減る）。継ぎ目をまたぐ壁は、継ぎ目で 2 本に分かれる。
public static class MazeOutline
{
    const float TwoPi = 2f * Mathf.PI;

    // 壁の線を全部。subdivisions = マスの 1 辺を何分割して点を打つか（曲面に沿って曲がって見えるのに必要）
    public static List<List<Vector2>> Walls(MazeGrid grid, int subdivisions)
    {
        var result = new List<List<Vector2>>();
        int w = grid.Width, h = grid.Height;
        int sub = subdivisions < 1 ? 1 : subdivisions;

        // 北の辺: 行 y の上端（v = (y+1)·2π/H）に沿って、x 方向に連なる壁
        for (int y = 0; y < h; y++)
        {
            int x = 0;
            while (x < w)
            {
                if (!grid.HasWall(x, y, MazeDir.North)) { x++; continue; }
                int start = x;
                while (x < w && grid.HasWall(x, y, MazeDir.North)) x++;
                var line = new List<Vector2>();
                int n = (x - start) * sub;
                for (int k = 0; k <= n; k++) line.Add(Corner(grid, start + (float)k / sub, y + 1));
                result.Add(line);
            }
        }

        // 東の辺: 列 x の右端（u = (x+1)·2π/W）に沿って、y 方向に連なる壁
        for (int x = 0; x < w; x++)
        {
            int y = 0;
            while (y < h)
            {
                if (!grid.HasWall(x, y, MazeDir.East)) { y++; continue; }
                int start = y;
                while (y < h && grid.HasWall(x, y, MazeDir.East)) y++;
                var line = new List<Vector2>();
                int n = (y - start) * sub;
                for (int k = 0; k <= n; k++) line.Add(Corner(grid, x + 1, start + (float)k / sub));
                result.Add(line);
            }
        }
        return result;
    }

    // マス (cx, cy) の中心を囲む正方形の輪（半分の大きさ = マスの一辺 × halfRatio）。スタート・ゴールの印。始点と終点が同じ点の閉じた折れ線
    public static List<Vector2> CellLoop(MazeGrid grid, int cx, int cy, float halfRatio, int subdivisions)
    {
        int sub = subdivisions < 1 ? 1 : subdivisions;
        float x0 = cx + 0.5f - halfRatio, x1 = cx + 0.5f + halfRatio;
        float y0 = cy + 0.5f - halfRatio, y1 = cy + 0.5f + halfRatio;
        var loop = new List<Vector2>();
        for (int k = 0; k < sub; k++) loop.Add(Corner(grid, x0 + (x1 - x0) * k / sub, y0));   // 下辺 →
        for (int k = 0; k < sub; k++) loop.Add(Corner(grid, x1, y0 + (y1 - y0) * k / sub));   // 右辺 ↑
        for (int k = 0; k < sub; k++) loop.Add(Corner(grid, x1 - (x1 - x0) * k / sub, y1));   // 上辺 ←
        for (int k = 0; k < sub; k++) loop.Add(Corner(grid, x0, y1 - (y1 - y0) * k / sub));   // 左辺 ↓
        loop.Add(loop[0]);
        return loop;
    }

    // 格子の座標 (gx, gy)（マス単位の実数。整数がマスの角）→ (u, v)
    public static Vector2 Corner(MazeGrid grid, float gx, float gy)
        => new Vector2(TwoPi * gx / grid.Width, TwoPi * gy / grid.Height);
}
