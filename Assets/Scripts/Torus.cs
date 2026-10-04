using UnityEngine;

// トーラスの Mesh を「パラメータ (u, v) の格子」から手組みするコンポーネント。ParametricSurface（曲面の共通窓口）の一つ。
//
//   p(u, v) = R·(cos u, 0, sin u) + r·n(u, v)          (0 ≤ u, v ≤ 2π)
//   n(u, v) = (cos v·cos u, sin v, cos v·sin u)        ← 管の断面の円の外向き単位ベクトル = 面の法線そのもの
//
// u = 輪っかを一周する角度、v = 管の断面を一周する角度。軸は Y（床に置くと寝たドーナツ）。
// R = majorRadius（輪の中心線の半径）、r = minorRadius（管の太さ）。輪っかの穴が空くのは r < R のとき。
//
// 貼り合わせ（正方形の対辺の同一視）:
//   (2π, v) = (0, v)    u 方向は周期。向きも変わらない
//   (u, 2π) = (u, 0)    v 方向も周期
// メビウスの帯と違って、貼り合わせで v が反転することはない → 向き付け可能で、法線は継ぎ目でもそのまま連続。
//
// ── Mesh の作り方（2026-10-04 に変更）──
//   以前: 頂点を nu × nv 個の格子で共有し、番号を % で回り込ませて貼っていた（継ぎ目は Mesh の上に存在しない）。
//   今:   頂点を (nu+1) × (nv+1) 個にして、継ぎ目の列・行を「複製」する。
//   理由: Mesh の UV に (u, v) を入れて、レイが当たった点から hit.textureCoord で (u, v) を逆引きしたいから。
//         頂点を共有すると、継ぎ目の頂点が UV = 0 と UV = 2π の両方を持てず、補間が途切れる。
//         複製した頂点は位置も法線も同じ（i % nu, j % nv で式を評価するので、浮動小数点の誤差も出ない）。UV だけが 2π になる。
[ExecuteAlways]                 // 再生していないエディタ上でも動かす
[DefaultExecutionOrder(-100)]   // 同じ GameObject の XR Grab Interactable より先に初期化してコライダーを用意する
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Torus : ParametricSurface
{
    const float TwoPi = 2f * Mathf.PI;

    [SerializeField, Min(0.01f)]  float majorRadius = 0.12f;   // R: 輪の中心線の半径 [m]
    [SerializeField, Min(0.005f)] float minorRadius = 0.045f;  // r: 管の半径 [m]（R より小さくすること）
    [SerializeField, Range(3, 128)] int uSegments = 48;        // 輪っか方向の分割数
    [SerializeField, Range(3, 128)] int vSegments = 24;        // 管の断面方向の分割数

    Mesh mesh;
    bool dirty = true;

    void OnEnable() => Build();
    void OnValidate() => dirty = true;  // Inspector で値を変えたら「作り直し予約」だけ
    void Update() { if (dirty) Build(); }

    void OnDestroy()
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
    }

    // ── ParametricSurface の窓口 ──

    public override Vector2 UVMin => Vector2.zero;
    public override Vector2 UVMax => new Vector2(TwoPi, TwoPi);

    // 式そのもの（このオブジェクトのローカル座標）
    public override Vector3 LocalPoint(float u, float v) => majorRadius * RingDirection(u) + minorRadius * LocalNormal(u, v);

    // 法線は式から直接。Cross(∂p/∂v, ∂p/∂u) を正規化した向きと一致する（三角形を (a, c, b) の順に並べたとき表になる側）
    public override Vector3 LocalNormal(float u, float v)
    {
        float cu = Mathf.Cos(u), su = Mathf.Sin(u), cv = Mathf.Cos(v), sv = Mathf.Sin(v);
        return new Vector3(cv * cu, sv, cv * su);
    }

    // ∂p/∂u = (R + r·cos v)·(−sin u, 0, cos u)      u を増やすと輪っかに沿って回る。管が太いところ（外側）ほど速い
    public override Vector3 dPdu(float u, float v)
    {
        float k = majorRadius + minorRadius * Mathf.Cos(v);
        return new Vector3(-k * Mathf.Sin(u), 0f, k * Mathf.Cos(u));
    }

    // ∂p/∂v = r·(−sin v·cos u, cos v, −sin v·sin u)   v を増やすと管の断面を回る。速さは常に r
    public override Vector3 dPdv(float u, float v)
    {
        float cu = Mathf.Cos(u), su = Mathf.Sin(u), cv = Mathf.Cos(v), sv = Mathf.Sin(v);
        return minorRadius * new Vector3(-sv * cu, cv, -sv * su);
    }

    // 継ぎ目の規則: P(u + 2π, v) = P(u, v)、P(u, v + 2π) = P(u, v)。u, v とも周期 2π（反転なし）。
    // 前の点 previous に一番近い別名 (u + 2πk, v + 2πl) を返して、点列を連続に伸ばす。
    // メビウスの帯の LiftNear は「k が奇数なら v を反転」だったが、トーラスは符号の反転がなく、2 方向とも独立に持ち上げるだけ。
    public override Vector2 LiftNear(Vector2 previous, Vector2 uv)
    {
        float ku = Mathf.Round((previous.x - uv.x) / TwoPi);
        float kv = Mathf.Round((previous.y - uv.y) / TwoPi);
        return new Vector2(uv.x + TwoPi * ku, uv.y + TwoPi * kv);
    }

    // 輪の中心線上の単位方向 (cos u, 0, sin u)
    static Vector3 RingDirection(float u) => new Vector3(Mathf.Cos(u), 0f, Mathf.Sin(u));

    void Build()
    {
        dirty = false;
        int nu = uSegments, nv = vSegments;

        int cols = nu + 1;               // 継ぎ目の列（i = nu）を1列多く持つ
        int rows = nv + 1;               // 継ぎ目の行（j = nv）を1行多く持つ
        var positions = new Vector3[cols * rows];
        var normals = new Vector3[cols * rows];
        var uvs = new Vector2[cols * rows];
        var triangles = new int[nu * nv * 6];

        // 格子点 (i, j) の頂点番号。i: u 方向（0 〜 nu）、j: v 方向（0 〜 nv）
        int Index(int i, int j) => i * rows + j;

        for (int i = 0; i < cols; i++)
        {
            // 位置・法線は i % nu で評価 → i = nu の継ぎ目の列は i = 0 と完全に同じ値になる。UV だけ 2π まで進める
            float uPos = TwoPi * (i % nu) / nu;
            float uTex = TwoPi * i / nu;
            for (int j = 0; j < rows; j++)
            {
                float vPos = TwoPi * (j % nv) / nv;
                float vTex = TwoPi * j / nv;

                // 式から直接求めた法線（隣の面と平均する RecalculateNormals は使わない → 継ぎ目でも滑らか）
                positions[Index(i, j)] = LocalPoint(uPos, vPos);
                normals[Index(i, j)] = LocalNormal(uPos, vPos);
                // UV には (u, v) そのものを入れる。hit.textureCoord がそのまま「当たった点のパラメータ」になる
                uvs[Index(i, j)] = new Vector2(uTex, vTex);
            }
        }

        // 各格子マスを 2 枚の三角形に分ける。継ぎ目は複製した列・行につなぐので、% での回り込みは要らない。
        //   c --- d      a=(i,j)  b=(i+1,j)  c=(i,j+1)  d=(i+1,j+1)
        //   |     |      三角形 (a, c, b) と (b, c, d)。
        //   a --- b      この順で並べると、外から見て時計回り = 表が外向きになる（Cross(p_v, p_u) = 外向き法線）
        int t = 0;
        for (int i = 0; i < nu; i++)
        {
            for (int j = 0; j < nv; j++)
            {
                int a = Index(i, j), b = Index(i + 1, j), c = Index(i, j + 1), d = Index(i + 1, j + 1);
                triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
            }
        }

        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.hideFlags = HideFlags.DontSave; // 実行時に毎回作るのでシーンファイルには保存しない
        }
        mesh.Clear();
        mesh.name = "Torus";
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().sharedMesh = mesh;

        // 掴むための当たり判定。MeshCollider があれば同じ Mesh を渡す（null を挟むのは再計算させるため）
        if (TryGetComponent(out MeshCollider col))
        {
            // Convex（凸包）にすると穴が塞がってしまう。トーラスは凸ではないので Convex は OFF にすること。
            // 非凸の MeshCollider は Kinematic な Rigidbody となら共存できる（この物体は Is Kinematic on）。
            if (col.convex)
                Debug.LogWarning("Torus: MeshCollider の Convex が ON だと穴が塞がります。OFF にしてください。", this);
            col.sharedMesh = null;
            col.sharedMesh = mesh;
        }
    }
}
