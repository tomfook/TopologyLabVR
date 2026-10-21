using UnityEngine;

// 1 つの角（頂点）のまわりに正多角形のタイルを並べて、折って角にする — その数式だけを持つ静的クラス。
// MonoBehaviour ではない（MazeGrid と同じく「論理の側」）。描画や操作は CornerWorkshop が受け持つ。
//
// 考え方: 頂点から出る辺を、頂点を頂上とする円錐の上に並べる。
//   正 p 角形の 1 つの角を α、タイルの枚数を q とする。辺は q+1 本（辺 0 〜 辺 q）、タイル k は辺 k と辺 k+1 の間。
//   辺 k の向き e_k = (sinθ·cos a_k, −cosθ, sinθ·sin a_k)、a_k = base + k·φ（θ = 円錐の軸からの傾き、φ = となりの辺との方位の差）
//   となりの辺のなす角が α であるには: cos α = cos²θ + sin²θ·cos φ  →  sin²θ = (1 − cos α) / (1 − cos φ)
//   φ = α なら θ = 90°（平ら）。φ を 2π/q まで広げると辺 q が辺 0 に重なって、すき間が閉じる。
//   sin²θ ≤ 1 なので φ ≥ α が必要 → 閉じられるのは 2π/q > α（= q·α < 2π）のときだけ。これがそのまま「なぜ 5 種類か」の芯。
//
// 長さの単位は「辺の長さ = 1」。呼ぶ側で辺の長さを掛ける。
public static class CornerFold
{
    public enum Result
    {
        Corner,   // すき間がある → 折れて角になる（q ≥ 3）
        Flat,     // ぴったり 1 周 → 平らのまま
        Overlap,  // 1 周を超える → 重なる
        TooFew,   // 2 枚 → 折れるが、ぺたんこに重なるだけで角にならない
    }

    const float Eps = 1e-4f;
    const float TwoPi = 2f * Mathf.PI;

    // 正 p 角形の 1 つの角 [ラジアン]。三角形 60°、四角形 90°、五角形 108°、六角形 120°
    public static float InteriorAngle(int p) => Mathf.PI * (p - 2) / p;

    public static Result Classify(int p, int q)
    {
        float total = q * InteriorAngle(p);
        if (total > TwoPi + Eps) return Result.Overlap;
        if (total > TwoPi - Eps) return Result.Flat;
        return q < 3 ? Result.TooFew : Result.Corner;
    }

    // 折れるか（ピンを引っぱって持ち上がるか）。2 枚も折れはする（ぺたんこになるまで）
    public static bool CanFold(int p, int q)
    {
        var r = Classify(p, q);
        return q >= 2 && (r == Result.Corner || r == Result.TooFew);
    }

    // 並べられる最大の枚数 = 初めて重なる枚数（三角形 7、四角形 5、五角形 4、六角形 4）
    public static int MaxCount(int p) => Mathf.FloorToInt(TwoPi / InteriorAngle(p) + Eps) + 1;

    // すき間が閉じたときの方位の差
    public static float ClosedPhi(int q) => TwoPi / q;

    public static float SinSqTheta(float alpha, float phi)
    {
        float d = 1f - Mathf.Cos(phi);
        if (d < 1e-6f) return 1f;
        return Mathf.Clamp01((1f - Mathf.Cos(alpha)) / d);
    }

    // すき間が閉じたときの頂点の高さ（辺の長さ = 1）。折れないときは 0
    public static float MaxHeight(int p, int q)
    {
        if (!CanFold(p, q)) return 0f;
        return Mathf.Sqrt(Mathf.Max(0f, 1f - SinSqTheta(InteriorAngle(p), ClosedPhi(q))));
    }

    // 頂点の高さ h（辺の長さ = 1。0 ≤ h ≤ MaxHeight）から方位の差 φ を逆算する。
    // 辺の先が高さ 0 の面にあるとき、h = cosθ。そこから sin²θ = 1 − h²、cos φ = 1 − (1 − cos α) / sin²θ
    public static float PhiFromHeight(float alpha, float h)
    {
        float s2 = 1f - h * h;
        if (s2 < 1e-6f) return Mathf.PI;
        float c = 1f - (1f - Mathf.Cos(alpha)) / s2;
        return Mathf.Acos(Mathf.Clamp(c, -1f, 1f));
    }

    // 辺 k の向き（単位ベクトル。頂点から外へ、下向き）。
    // すき間（辺 q と辺 0 の間）の真ん中が方位 gapAzimuth を向くように並べる。方位は +X から +Z へ測る（−Z なら −π/2）。
    // q 枚を超える k（予備のタイル用）もそのまま延長して求められる。
    public static Vector3 EdgeDirection(float alpha, float phi, int q, float gapAzimuth, int k)
    {
        float s2 = SinSqTheta(alpha, phi);
        float sin = Mathf.Sqrt(s2);
        float cos = Mathf.Sqrt(Mathf.Max(0f, 1f - s2));
        // すき間の真ん中 = base + q·φ + (2π − q·φ)/2 = base + π + q·φ/2
        float baseAz = gapAzimuth - Mathf.PI - q * phi * 0.5f;
        float a = baseAz + k * phi;
        return new Vector3(sin * Mathf.Cos(a), -cos, sin * Mathf.Sin(a));
    }

    // 正 p 角形の頂点 j を「頂点 0 から出る 2 本の辺 a（頂点 1 へ）と b（頂点 p−1 へ）」の組み合わせ s·a + t·b で表す係数。
    // 平面の正多角形で一度だけ解いておけば、3D のどんな向きの 2 辺にもそのまま使える（形が同じなら係数は同じ）
    public static Vector2[] PolygonCoefficients(int p)
    {
        var q = new Vector2[p];
        for (int j = 0; j < p; j++)
        {
            float t = TwoPi * j / p;
            q[j] = new Vector2(Mathf.Cos(t), Mathf.Sin(t));
        }
        Vector2 a = q[1] - q[0], b = q[p - 1] - q[0];
        float det = a.x * b.y - a.y * b.x;
        var coef = new Vector2[p];
        for (int j = 0; j < p; j++)
        {
            Vector2 d = q[j] - q[0];
            coef[j] = new Vector2((d.x * b.y - d.y * b.x) / det, (a.x * d.y - a.y * d.x) / det);
        }
        return coef;
    }

    // タイルの角の位置: apex + length·(s·u + t·w)。u, w は頂点から出る 2 本の辺の向き（単位ベクトル）
    public static void TileCorners(Vector3 apex, Vector3 u, Vector3 w, float length, Vector2[] coef, Vector3[] result)
    {
        for (int j = 0; j < coef.Length; j++)
            result[j] = apex + length * (coef[j].x * u + coef[j].y * w);
    }
}
