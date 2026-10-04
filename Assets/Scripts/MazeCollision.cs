using UnityEngine;

// 迷路の壁と、プレイヤー（床の上の円）の当たり判定。物理エンジンは使わず、格子の辺から壁の位置を求めて自前で押し出す。
// Unity の部品は Vector2 / Mathf しか使わない（MazeGrid と同じく、データと論理の側）。
//
// 位置は「迷路の平面の座標」: MazeView のローカル座標の (x, z) を Vector2 の (x, y) に入れたもの。
//   マス (gx, gy) は [gx·cell, (gx+1)·cell] × [gy·cell, (gy+1)·cell]。MazeMeshBuilder と同じ並べ方で、壁の箱も同じ式で作る。
//
// 位置がタイルの外（x < 0 や x ≥ Width·cell）にあっても、格子は周期的に続いているものとして判定できる
// （マス番号を Mod で畳んで壁を引く）。だから「継ぎ目をまたぐ」ために特別な処理は要らない。
//
// 押し出しの考え方: 壁の箱の上で、円の中心に一番近い点 q を求める。中心と q の距離が半径 r より近ければ、
// 距離が r になるまで q から離れる向きに中心を押す。壁に斜めにぶつかると、壁に沿った成分は残る → 壁に沿って滑る。
// 前提: 1 回の呼び出しで動く距離が (r + 壁の厚み/2) より小さいこと（1 フレームの歩く距離は数 cm なので十分余裕がある）。
public static class MazeCollision
{
    public static Vector2 Resolve(MazeGrid grid, float cell, float thickness, Vector2 p, float radius)
    {
        float half = thickness * 0.5f;

        // 角では押し出しが別の壁へ押し込むことがあるので、動かなくなるまで数回繰り返す
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            int gx = Mathf.FloorToInt(p.x / cell), gy = Mathf.FloorToInt(p.y / cell);

            // 今いるマスとその周り 3×3 マスの、東の辺と北の辺（半径 < 1 マスなら、近い壁は全部ここに入る）
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int cgx = gx + dx, cgy = gy + dy;
                    int cx = MazeGrid.Mod(cgx, grid.Width), cy = MazeGrid.Mod(cgy, grid.Height);

                    if (grid.HasWall(cx, cy, MazeDir.East))
                    {
                        float xc = (cgx + 1) * cell;
                        moved |= PushOut(ref p,
                            xc - half, cgy * cell - half,
                            xc + half, (cgy + 1) * cell + half, radius);
                    }
                    if (grid.HasWall(cx, cy, MazeDir.North))
                    {
                        float yc = (cgy + 1) * cell;
                        moved |= PushOut(ref p,
                            cgx * cell - half, yc - half,
                            (cgx + 1) * cell + half, yc + half, radius);
                    }
                }
            }
            if (!moved) break;
        }
        return p;
    }

    // 位置 p が、マス (cx, cy) の中心から四方 half 以内（板の上）にいるか。迷路は周期的に続いているので、
    // どのタイルの (cx, cy) の上にいても true になる（p を周期ぶんずらしても答えは変わらない）
    public static bool OnCell(MazeGrid grid, float cell, Vector2 p, int cx, int cy, float half)
    {
        float periodX = grid.Width * cell, periodY = grid.Height * cell;
        float dx = p.x - (cx + 0.5f) * cell;
        float dy = p.y - (cy + 0.5f) * cell;
        // 周期の整数倍を引いて、一番近い複製との差（-周期/2 〜 周期/2）にする
        dx -= Mathf.Round(dx / periodX) * periodX;
        dy -= Mathf.Round(dy / periodY) * periodY;
        return Mathf.Abs(dx) < half && Mathf.Abs(dy) < half;
    }

    // 箱 [minX, maxX] × [minY, maxY] から円（中心 p、半径 r）を押し出す。押し出したら true
    static bool PushOut(ref Vector2 p, float minX, float minY, float maxX, float maxY, float r)
    {
        // 箱の上で中心に一番近い点
        float qx = Mathf.Clamp(p.x, minX, maxX);
        float qy = Mathf.Clamp(p.y, minY, maxY);
        float dx = p.x - qx, dy = p.y - qy;
        float d2 = dx * dx + dy * dy;
        if (d2 >= r * r) return false;

        if (d2 > 1e-10f)
        {
            // 中心は箱の外: q から離れる向きに、距離が r になるまで
            float d = Mathf.Sqrt(d2);
            float k = (r - d) / d;
            p.x += dx * k;
            p.y += dy * k;
        }
        else
        {
            // 中心が箱の中（めり込みすぎ）: 一番近い面の外へ出す
            float left = p.x - minX, right = maxX - p.x, down = p.y - minY, up = maxY - p.y;
            float m = Mathf.Min(Mathf.Min(left, right), Mathf.Min(down, up));
            if (m == left) p.x = minX - r;
            else if (m == right) p.x = maxX + r;
            else if (m == down) p.y = minY - r;
            else p.y = maxY + r;
        }
        return true;
    }
}
