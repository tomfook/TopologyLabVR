using System.Collections.Generic;
using UnityEngine;

// MazeGrid（迷路データ）から、床と壁の「頂点・法線・UV・三角形」を作る。
// Mesh そのものは作らない（Mesh を作って配るのは MazeView の仕事）。ここは Vector3 などの数値の組み立てだけ。
//
// 座標: 迷路の平面は XZ、上は Y。マス (x, y) は [x·cell, (x+1)·cell] × [y·cell, (y+1)·cell]（x → ワールド X、y → ワールド Z）。
//
// 周りのタイルも描く: 迷路は貼り合わせ（トーラス）なので、東の端の向こうには「西の端と同じマス」がある。
// 継ぎ目の向こうが見えないと世界が途切れて見えるので、迷路そのものを (2R+1)×(2R+1) 枚敷き詰めて、
// 1 枚の Mesh にまとめて描く（R = tileRadius）。真ん中の 1 枚が「本物」で、残りは同じものの複製。
//
// 壁は直方体（箱）。辺の上に、厚み t の箱を置く。箱の端は t/2 だけ長くして、角のすき間を埋める。
// 同じ辺は 1 回だけ作る（東の辺と北の辺だけを見る。西・南は隣のマスの東・北だから）。
public sealed class MazeMeshBuilder
{
    public readonly List<Vector3> Vertices = new List<Vector3>();
    public readonly List<Vector3> Normals = new List<Vector3>();
    public readonly List<Vector2> Uvs = new List<Vector2>();

    // サブメッシュ = Material（0 壁 / 1 床 / 2 床の市松のもう片方）
    public readonly List<int> WallTriangles = new List<int>();
    public readonly List<int> FloorTriangles = new List<int>();
    public readonly List<int> FloorAltTriangles = new List<int>();
    // 目印（スタートとゴール）。サブメッシュ 3, 4 = Material 2 つ
    public readonly List<int> StartTriangles = new List<int>();
    public readonly List<int> GoalTriangles = new List<int>();

    // 作った壁の箱（検算・あとの当たり判定や目印の置き場所に使える）
    public readonly List<(Vector3 min, Vector3 max)> WallBoxes = new List<(Vector3 min, Vector3 max)>();

    // 目印のパッドの半分の大きさ（マスの一辺に対する割合）。ゴールに着いた判定も同じ大きさを使う（見えている板に乗ったら着いたことになる）
    public const float PadHalfRatio = 0.35f;

    float cell;

    public void Clear()
    {
        Vertices.Clear(); Normals.Clear(); Uvs.Clear();
        WallTriangles.Clear(); FloorTriangles.Clear(); FloorAltTriangles.Clear();
        StartTriangles.Clear(); GoalTriangles.Clear();
        WallBoxes.Clear();
    }

    public void Build(MazeGrid grid, float cellSize, float wallHeight, float wallThickness, int tileRadius)
    {
        Clear();
        cell = cellSize;
        float half = wallThickness * 0.5f;
        int w = grid.Width, h = grid.Height;

        // 描くマスの範囲 [x0, x1) × [y0, y1)。タイル (0,0) が真ん中
        int x0 = -tileRadius * w, x1 = (tileRadius + 1) * w;
        int y0 = -tileRadius * h, y1 = (tileRadius + 1) * h;

        // 床: 1 マスにつき 1 枚。市松は「タイルの中での」座標の偶奇で決める → どのタイルも同じ模様
        for (int gx = x0; gx < x1; gx++)
        {
            for (int gy = y0; gy < y1; gy++)
            {
                int cx = MazeGrid.Mod(gx, w), cy = MazeGrid.Mod(gy, h);
                var tris = ((cx + cy) & 1) == 0 ? FloorTriangles : FloorAltTriangles;
                AddQuad(
                    new Vector3(gx * cell, 0f, gy * cell),
                    new Vector3(gx * cell, 0f, (gy + 1) * cell),
                    new Vector3((gx + 1) * cell, 0f, (gy + 1) * cell),
                    new Vector3((gx + 1) * cell, 0f, gy * cell),
                    Vector3.up, tris);
            }
        }

        // 壁: 範囲の外周の壁も閉じるため、壁だけは 1 マス外側（x0-1, y0-1）から見る
        //   東の辺 (gx, gy) は x = (gx+1)·cell の線の上、z 方向に 1 マス分。gx ∈ [x0-1, x1)、gy ∈ [y0, y1)
        //   北の辺 (gx, gy) は z = (gy+1)·cell の線の上、x 方向に 1 マス分。gx ∈ [x0, x1)、gy ∈ [y0-1, y1)
        for (int gx = x0 - 1; gx < x1; gx++)
        {
            for (int gy = y0 - 1; gy < y1; gy++)
            {
                int cx = MazeGrid.Mod(gx, w), cy = MazeGrid.Mod(gy, h);

                if (gy >= y0 && grid.HasWall(cx, cy, MazeDir.East))
                {
                    float xc = (gx + 1) * cell;
                    AddBox(new Vector3(xc - half, 0f, gy * cell - half),
                           new Vector3(xc + half, wallHeight, (gy + 1) * cell + half), WallTriangles, true);
                }
                if (gx >= x0 && grid.HasWall(cx, cy, MazeDir.North))
                {
                    float zc = (gy + 1) * cell;
                    AddBox(new Vector3(gx * cell - half, 0f, zc - half),
                           new Vector3((gx + 1) * cell + half, wallHeight, zc + half), WallTriangles, true);
                }
            }
        }
    }

    // 目印を置く: マス (cx, cy) の床に色つきの板（パッド）と、背の高い細い柱（ビーコン）。周りのタイルの複製にも同じものを置く。
    //   柱は壁より高くすると、壁ごしに遠くからも見える（高さ 0 なら柱なし）。柱もパッドも当たり判定はない（見えるだけ）。
    //   継ぎ目の向こうの複製にも置くので、一周するたびに同じ目印が現れる = 「一周した」「ここは継ぎ目の向こう」が見て分かる。
    // Build の後に呼ぶ。triangles = StartTriangles か GoalTriangles
    public void AddMarker(MazeGrid grid, int tileRadius, int cx, int cy, List<int> triangles, float beaconHeight)
    {
        float padHalf = cell * PadHalfRatio;   // パッドの半分の大きさ（マスの 7 割）
        float padY = 0.01f;                // 床（y = 0）と同じ高さだとちらつくので少し浮かせる
        float beaconHalf = 0.06f;          // 柱の太さの半分
        for (int tx = -tileRadius; tx <= tileRadius; tx++)
        {
            for (int ty = -tileRadius; ty <= tileRadius; ty++)
            {
                float mx = (tx * grid.Width + cx + 0.5f) * cell;
                float mz = (ty * grid.Height + cy + 0.5f) * cell;
                AddQuad(
                    new Vector3(mx - padHalf, padY, mz - padHalf),
                    new Vector3(mx - padHalf, padY, mz + padHalf),
                    new Vector3(mx + padHalf, padY, mz + padHalf),
                    new Vector3(mx + padHalf, padY, mz - padHalf),
                    Vector3.up, triangles);
                if (beaconHeight > 0f)
                    AddBox(new Vector3(mx - beaconHalf, 0f, mz - beaconHalf),
                           new Vector3(mx + beaconHalf, beaconHeight, mz + beaconHalf), triangles, false);
            }
        }
    }

    // 箱 = 側面 4 枚 + 上面 1 枚（底は見えないので作らない）。各面は外向きの法線を持つ。isWall = 壁の箱として WallBoxes に記録するか
    void AddBox(Vector3 min, Vector3 max, List<int> triangles, bool isWall)
    {
        if (isWall) WallBoxes.Add((min, max));
        float a = min.x, b = min.y, c = min.z, d = max.x, e = max.y, f = max.z;
        // 各面の 4 隅は、面の外周を 1 周する順に並べる（AddQuad が巻き順を直す）
        AddQuad(new Vector3(d, b, c), new Vector3(d, b, f), new Vector3(d, e, f), new Vector3(d, e, c), Vector3.right, triangles);   // +X
        AddQuad(new Vector3(a, b, c), new Vector3(a, b, f), new Vector3(a, e, f), new Vector3(a, e, c), Vector3.left, triangles);    // -X
        AddQuad(new Vector3(a, b, f), new Vector3(d, b, f), new Vector3(d, e, f), new Vector3(a, e, f), Vector3.forward, triangles); // +Z
        AddQuad(new Vector3(a, b, c), new Vector3(d, b, c), new Vector3(d, e, c), new Vector3(a, e, c), Vector3.back, triangles);    // -Z
        AddQuad(new Vector3(a, e, c), new Vector3(a, e, f), new Vector3(d, e, f), new Vector3(d, e, c), Vector3.up, triangles);      // 上
    }

    // 四角形 p0→p1→p2→p3（外周を 1 周する順）を、法線 n が表になるように 2 枚の三角形にして足す。
    // Unity の表は「外から見て時計回り」で、Cross(p1-p0, p2-p0) が表側を向く。向きが逆なら並びを入れ替える。
    void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 n, List<int> triangles)
    {
        int i = Vertices.Count;
        Vertices.Add(p0); Vertices.Add(p1); Vertices.Add(p2); Vertices.Add(p3);
        for (int k = 0; k < 4; k++) Normals.Add(n);
        Uvs.Add(Uv(p0, n)); Uvs.Add(Uv(p1, n)); Uvs.Add(Uv(p2, n)); Uvs.Add(Uv(p3, n));

        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), n) >= 0f)
        {
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 3);
        }
        else
        {
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 1);
            triangles.Add(i); triangles.Add(i + 3); triangles.Add(i + 2);
        }
    }

    // UV は「マス単位の長さ」で、面に平行な 2 軸を取る（後でテクスチャを貼るとき 1 マス = 1 枚になる）
    Vector2 Uv(Vector3 p, Vector3 n)
    {
        if (n.y > 0.5f || n.y < -0.5f) return new Vector2(p.x / cell, p.z / cell);   // 上・下向き: XZ
        if (n.x > 0.5f || n.x < -0.5f) return new Vector2(p.z / cell, p.y / cell);   // X 向き: ZY
        return new Vector2(p.x / cell, p.y / cell);                                   // Z 向き: XY
    }
}
