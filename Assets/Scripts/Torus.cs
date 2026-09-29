using UnityEngine;

// トーラスの Mesh を「パラメータ (u, v) の格子」から手組みするコンポーネント。
//
//   p(u, v) = R·(cos u, 0, sin u) + r·n(u, v)          (0 ≤ u, v < 2π)
//   n(u, v) = (cos v·cos u, sin v, cos v·sin u)        ← 管の断面の円の外向き単位ベクトル = 面の法線そのもの
//
// u = 輪っかを一周する角度、v = 管の断面を一周する角度。軸は Y（床に置くと寝たドーナツ）。
// R = majorRadius（輪の中心線の半径）、r = minorRadius（管の太さ）。輪っかの穴が空くのは r < R のとき。
//
// 要点: 頂点は nu × nv 個の格子状に並べ、隣り合う面で共有する（正多面体のように面ごとに複製しない）。
// u の最後と最初、v の最後と最初は「番号を回り込ませて」貼る（% で剰余）。
// これが「正方形の対辺を同一視するとトーラスになる」の実装そのもの。継ぎ目は Mesh の上には存在しない。
[ExecuteAlways]                 // 再生していないエディタ上でも動かす
[DefaultExecutionOrder(-100)]   // 同じ GameObject の XR Grab Interactable より先に初期化してコライダーを用意する
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Torus : MonoBehaviour
{
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

    void Build()
    {
        dirty = false;
        int nu = uSegments, nv = vSegments;

        var positions = new Vector3[nu * nv];
        var normals = new Vector3[nu * nv];
        var triangles = new int[nu * nv * 6];

        // 格子点 (i, j) の頂点番号。i: u 方向、j: v 方向
        int Index(int i, int j) => i * nv + j;

        for (int i = 0; i < nu; i++)
        {
            float u = 2f * Mathf.PI * i / nu;
            float cu = Mathf.Cos(u), su = Mathf.Sin(u);
            var ringCenter = new Vector3(cu, 0f, su) * majorRadius; // 中心線上の点

            for (int j = 0; j < nv; j++)
            {
                float v = 2f * Mathf.PI * j / nv;
                float cv = Mathf.Cos(v), sv = Mathf.Sin(v);

                // 式から直接求めた法線（隣の面と平均する RecalculateNormals は使わない → 継ぎ目でも滑らか）
                var n = new Vector3(cv * cu, sv, cv * su);
                positions[Index(i, j)] = ringCenter + n * minorRadius;
                normals[Index(i, j)] = n;
            }
        }

        // 各格子マスを 2 枚の三角形に分ける。i+1, j+1 は % で回り込ませる ← ここが「貼り合わせ」
        //   c --- d      a=(i,j)  b=(i+1,j)  c=(i,j+1)  d=(i+1,j+1)
        //   |     |      三角形 (a, c, b) と (b, c, d)。
        //   a --- b      この順で並べると、外から見て時計回り = 表が外向きになる（Cross(p_v, p_u) = 外向き法線）
        int t = 0;
        for (int i = 0; i < nu; i++)
        {
            int i1 = (i + 1) % nu;
            for (int j = 0; j < nv; j++)
            {
                int j1 = (j + 1) % nv;
                int a = Index(i, j), b = Index(i1, j), c = Index(i, j1), d = Index(i1, j1);
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
