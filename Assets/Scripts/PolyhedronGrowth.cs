using System.Collections.Generic;
using UnityEngine;

// 「角から立体を育てる」の段取りを計算する純ロジック（UnityEngine は Vector3 と Mathf だけ。CornerFold と同じ「論理の側」）。
//
//   ① 角: 頂点 apex のまわりの q 枚が、工房と同じ式（CornerFold）で平らな星形から閉じる
//   ② 段: まだ無い面のうち、もうある面と辺を共有する面が、1 段ずつまとめて出てくる。
//          出てくる面は、親の面と同じ平面に開いた形（展開図）から、共有する辺を蝶番にして折れる
//   ③ 最後の段が閉じると、正多面体になる
//
// 向き: 立体の中心が原点、apex が真上（+Y）。閉じた角のタイルが CornerFold のタイルとぴったり重なるよう、Y 軸まわりに回してある。
// 形のデータは RegularPolyhedron と同じ（面は外から見て時計回り）。
public sealed class PolyhedronGrowth
{
    public int Sides { get; private set; }   // 面が正何角形か (p)
    public int Count { get; private set; }   // 1 つの角に集まる枚数 (q)
    public float Edge { get; private set; }
    public float GapAzimuth { get; private set; }

    public Vector3[] Verts { get; private set; }   // 中心が原点、apex が真上
    public int[][] Faces { get; private set; }
    public int Apex { get; private set; }
    public int[] Wave { get; private set; }        // 面 → 何段目に出るか（0 = 角）
    public int[] Parent { get; private set; }      // 面 → 蝶番でつながる親の面（角の面は −1）
    public int[] HingeA { get; private set; }      // 蝶番の辺の両端（頂点番号）
    public int[] HingeB { get; private set; }
    public float[] OpenAngle { get; private set; } // 蝶番（HingeA → HingeB の向き）まわりにこれだけ回すと、親と同じ平面に開く
    public int WaveCount { get; private set; }     // 段の数（角を含む）

    // (p, q) が正多面体でなければ null
    public static PolyhedronGrowth Plan(int p, int q, float edge, float gapAzimuth)
    {
        Vector3[] v; int[][] f;
        if (!Shape(p, q, out v, out f)) return null;
        var g = new PolyhedronGrowth { Sides = p, Count = q, Edge = edge, GapAzimuth = gapAzimuth, Faces = f };
        g.Build(v);
        return g;
    }

    public static bool IsSolid(int p, int q) { Vector3[] v; int[][] f; return Shape(p, q, out v, out f); }

    void Build(Vector3[] raw)
    {
        // 辺の長さを edge にそろえる（正多面体の頂点はみな中心から等距離）
        int n = raw.Length;
        var unit = new Vector3[n];
        for (int i = 0; i < n; i++) unit[i] = raw[i].normalized;
        float unitEdge = Vector3.Distance(unit[Faces[0][0]], unit[Faces[0][1]]);
        Verts = new Vector3[n];
        for (int i = 0; i < n; i++) Verts[i] = unit[i] * (Edge / unitEdge);

        // apex を真上へ回す
        Apex = Faces[0][0];
        Vector3 d = Verts[Apex].normalized;
        Vector3 axis = Vector3.Cross(d, Vector3.up);
        float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, Vector3.up), -1f, 1f));
        if (axis.magnitude > 1e-6f) { axis = axis.normalized; for (int i = 0; i < n; i++) Verts[i] = Rotate(Verts[i], axis, angle); }
        else if (Vector3.Dot(d, Vector3.up) < 0f) for (int i = 0; i < n; i++) Verts[i] = Rotate(Verts[i], Vector3.right, Mathf.PI);

        // Y 軸まわりに回して、apex から出る辺の 1 本を CornerFold の閉じた角の辺 0 にそろえる（辺は等間隔なので全部そろう）
        float alpha = CornerFold.InteriorAngle(Sides);
        Vector3 e0 = CornerFold.EdgeDirection(alpha, CornerFold.ClosedPhi(Count), Count, GapAzimuth, 0);
        int nb = -1;
        foreach (var face in Faces)
            for (int j = 0; j < face.Length; j++)
                if (face[j] == Apex) { nb = face[(j + 1) % face.Length]; break; }
        Vector3 en = Verts[nb] - Verts[Apex];
        float turn = Mathf.Atan2(en.x, en.z) - Mathf.Atan2(e0.x, e0.z);   // Y 軸まわりの回転（+Z から +X へ向かう向きが正）
        for (int i = 0; i < n; i++) Verts[i] = Rotate(Verts[i], Vector3.up, -turn);

        // 段を決める（辺でつながる面を幅優先で）
        int fc = Faces.Length;
        Wave = new int[fc]; Parent = new int[fc]; HingeA = new int[fc]; HingeB = new int[fc]; OpenAngle = new float[fc];
        for (int i = 0; i < fc; i++) { Wave[i] = -1; Parent[i] = -1; HingeA[i] = HingeB[i] = -1; }
        var placed = new List<int>();
        for (int i = 0; i < fc; i++) if (System.Array.IndexOf(Faces[i], Apex) >= 0) { Wave[i] = 0; placed.Add(i); }
        int w = 0;
        while (placed.Count < fc)
        {
            w++;
            var next = new List<int>();
            for (int i = 0; i < fc; i++)
            {
                if (Wave[i] >= 0) continue;
                foreach (int pf in placed)   // 先に置いた面を優先して親にする
                {
                    int a, b;
                    if (Wave[pf] >= w || !SharedEdge(Faces[i], Faces[pf], out a, out b)) continue;
                    Parent[i] = pf; HingeA[i] = a; HingeB[i] = b;
                    next.Add(i);
                    break;
                }
            }
            foreach (int i in next) { Wave[i] = w; placed.Add(i); OpenAngle[i] = ComputeOpenAngle(i); }
            if (next.Count == 0) break;   // 起きないはず（面は全部つながっている）
        }
        WaveCount = w + 1;
    }

    // 2 つの面が共有する辺（頂点番号）
    static bool SharedEdge(int[] f, int[] g, out int a, out int b)
    {
        for (int j = 0; j < f.Length; j++)
        {
            int x = f[j], y = f[(j + 1) % f.Length];
            if (System.Array.IndexOf(g, x) >= 0 && System.Array.IndexOf(g, y) >= 0) { a = x; b = y; return true; }
        }
        a = b = -1; return false;
    }

    public Vector3 FaceNormal(int face)   // 外向き
    {
        var f = Faces[face];
        return Vector3.Cross(Verts[f[1]] - Verts[f[0]], Verts[f[2]] - Verts[f[0]]).normalized;
    }

    // 面を蝶番まわりに回して、親の面と同じ平面（同じ向き）に開く角度
    float ComputeOpenAngle(int face)
    {
        Vector3 u = (Verts[HingeB[face]] - Verts[HingeA[face]]).normalized;
        Vector3 nf = FaceNormal(face), np = FaceNormal(Parent[face]);
        float gamma = Mathf.Acos(Mathf.Clamp(Vector3.Dot(nf, np), -1f, 1f));
        float sign = Vector3.Dot(Vector3.Cross(nf, np), u) >= 0f ? 1f : -1f;
        return sign * gamma;
    }

    // 段の面の、開き具合 open（1 = 親と同じ平面に開いている / 0 = 閉じて正しい位置）での頂点
    public void FaceCorners(int face, float open, Vector3[] result)
    {
        var f = Faces[face];
        if (Parent[face] < 0 || open <= 0f) { for (int j = 0; j < f.Length; j++) result[j] = Verts[f[j]]; return; }
        Vector3 a = Verts[HingeA[face]];
        Vector3 u = (Verts[HingeB[face]] - a).normalized;
        float ang = OpenAngle[face] * open;
        for (int j = 0; j < f.Length; j++) result[j] = a + Rotate(Verts[f[j]] - a, u, ang);
    }

    // 角の面（段 0）を、工房と同じく φ（隣り合う辺の間の角）で並べたときのタイル k の頂点。
    // φ = 内角 α で平らな星形、φ = 2π/q で閉じた角（= 立体の apex のまわりの面とぴったり重なる）
    public void CornerTile(float phi, int k, Vector2[] coef, Vector3[] result)
    {
        float alpha = CornerFold.InteriorAngle(Sides);
        Vector3 e = CornerFold.EdgeDirection(alpha, phi, Count, GapAzimuth, k);
        Vector3 f = CornerFold.EdgeDirection(alpha, phi, Count, GapAzimuth, k + 1);
        CornerFold.TileCorners(Verts[Apex], e, f, Edge, coef, result);
    }

    // ロドリゲスの回転公式: v を単位ベクトル u のまわりに θ 回す（右ねじ）
    public static Vector3 Rotate(Vector3 v, Vector3 u, float theta)
    {
        float c = Mathf.Cos(theta), s = Mathf.Sin(theta);
        return v * c + Vector3.Cross(u, v) * s + u * (Vector3.Dot(u, v) * (1f - c));
    }

    // ---- 形のデータ（RegularPolyhedron と同じ）------------------------------------------
    static readonly float P = (1f + Mathf.Sqrt(5f)) / 2f;
    static readonly float Q = P - 1f;

    static bool Shape(int p, int q, out Vector3[] v, out int[][] f)
    {
        v = null; f = null;
        if (p == 3 && q == 3)
        {
            v = new[] { V(1, 1, 1), V(1, -1, -1), V(-1, 1, -1), V(-1, -1, 1) };
            f = new[] { F(2, 0, 1), F(1, 0, 3), F(3, 0, 2), F(2, 1, 3) };
        }
        else if (p == 4 && q == 3)
        {
            v = new[] { V(-1, -1, -1), V(-1, -1, 1), V(-1, 1, -1), V(-1, 1, 1), V(1, -1, -1), V(1, -1, 1), V(1, 1, -1), V(1, 1, 1) };
            f = new[] { F(2, 0, 1, 3), F(1, 0, 4, 5), F(4, 0, 2, 6), F(3, 1, 5, 7), F(6, 2, 3, 7), F(5, 4, 6, 7) };
        }
        else if (p == 3 && q == 4)
        {
            v = new[] { V(1, 0, 0), V(-1, 0, 0), V(0, 1, 0), V(0, -1, 0), V(0, 0, 1), V(0, 0, -1) };
            f = new[] { F(4, 0, 2), F(2, 0, 5), F(3, 0, 4), F(5, 0, 3), F(2, 1, 4), F(5, 1, 2), F(4, 1, 3), F(3, 1, 5) };
        }
        else if (p == 5 && q == 3)
        {
            v = new[] { V(-1, -1, -1), V(-1, -1, 1), V(-1, 1, -1), V(-1, 1, 1), V(1, -1, -1), V(1, -1, 1), V(1, 1, -1), V(1, 1, 1),
                        V(0, -Q, -P), V(0, -Q, P), V(0, Q, -P), V(0, Q, P), V(-Q, -P, 0), V(-Q, P, 0), V(Q, -P, 0), V(Q, P, 0),
                        V(-P, 0, -Q), V(-P, 0, Q), V(P, 0, -Q), V(P, 0, Q) };
            f = new[] { F(17, 16, 0, 12, 1), F(10, 8, 0, 16, 2), F(14, 12, 0, 8, 4), F(3, 17, 1, 9, 11), F(5, 9, 1, 12, 14), F(3, 13, 2, 16, 17),
                        F(6, 10, 2, 13, 15), F(15, 13, 3, 11, 7), F(5, 14, 4, 18, 19), F(6, 18, 4, 8, 10), F(11, 9, 5, 19, 7), F(19, 18, 6, 15, 7) };
        }
        else if (p == 3 && q == 5)
        {
            v = new[] { V(0, -1, -P), V(0, -1, P), V(0, 1, -P), V(0, 1, P), V(-1, -P, 0), V(-1, P, 0), V(1, -P, 0), V(1, P, 0),
                        V(-P, 0, -1), V(-P, 0, 1), V(P, 0, -1), V(P, 0, 1) };
            f = new[] { F(2, 0, 8), F(10, 0, 2), F(4, 0, 6), F(8, 0, 4), F(6, 0, 10), F(9, 1, 3), F(3, 1, 11), F(6, 1, 4), F(4, 1, 9), F(11, 1, 6),
                        F(7, 2, 5), F(5, 2, 8), F(10, 2, 7), F(5, 3, 7), F(9, 3, 5), F(7, 3, 11), F(8, 4, 9), F(9, 5, 8), F(11, 6, 10), F(10, 7, 11) };
        }
        return v != null;
    }

    static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);
    static int[] F(params int[] idx) => idx;
}
