using UnityEngine;

// クラインの壺（figure-8 型のはめ込み）の Mesh を「パラメータ (u, v) の格子」から手組みするコンポーネント。
//
//   h = u/2
//   A(u,v) = R + cos h·sin v − sin h·sin 2v
//   x = A·cos u,   y = A·sin u,   z = sin h·sin v + cos h·sin 2v      (0 ≤ u, v < 2π)
//   Unity へは (x, z, y) として渡す（数学の z 軸 = Unity の上 = Y 軸）
//
// トーラスとの違いは「u 方向の貼り合わせ」ただ1点:
//   トーラス:      (2π, v) = (0,  v)   ← 端をそのまま貼る
//   クラインの壺:  (2π, v) = (0, −v)   ← v を逆向きにして貼る（= 片方の辺の組を裏返して貼る）
// 実際に式へ u = 2π を入れると cos(u/2), sin(u/2) の符号が反転して、ちょうど (0, −v) の点に一致する。
//
// ── この面は「向き付け不可能」──
// 面には表と裏があるはずだが、クラインの壺は歩いて一周すると表が裏に入れ替わる。
// だから面全体に「外向き」の法線を一貫して決めることができない。Mesh に落とすとき、これが2つの形で現れる:
//   ① 継ぎ目: 貼り合わせ位置では位置は同じでも法線は逆向き。頂点を共有できないので、継ぎ目の列を複製する。
//   ② 表裏: 見える範囲のどこかに必ず「裏向き」の三角形が混ざる。だから表の面と裏の面を両方作る（下の「両面」）。
//
// ── 3次元では自己交差する ──
// この形は壺の首が自分の側面を貫く。これは避けられない（4次元なら交差なしで作れる）。
// 数学的には「はめ込み (immersion)」であって「埋め込み (embedding)」ではない。
[ExecuteAlways]                 // 再生していないエディタ上でも動かす
[DefaultExecutionOrder(-100)]   // 同じ GameObject の XR Grab Interactable より先に初期化してコライダーを用意する
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class KleinBottle : MonoBehaviour
{
    [SerializeField, Min(0.001f)] float size = 0.06f;      // 式の 1.0 が何 m か。全体の幅はおよそ 6 × size ×（R+1.5 の 2 倍）
    [SerializeField, Min(1.6f)]   float ringRadius = 2f;   // R: 8 の字の輪の大きさ（式の単位）。小さいと壺の首が輪の中心に近づく
    [SerializeField, Range(4, 128)] int uSegments = 64;    // 輪っか方向の分割数
    [SerializeField, Range(4, 128)] int vSegments = 32;    // 断面（8 の字）方向の分割数

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

    // 式そのもの。(x, y, z) の y と z を入れ替えて Unity の座標にする
    Vector3 Position(float u, float v)
    {
        float h = u * 0.5f;
        float a = ringRadius + Mathf.Cos(h) * Mathf.Sin(v) - Mathf.Sin(h) * Mathf.Sin(2f * v);
        float x = a * Mathf.Cos(u);
        float y = a * Mathf.Sin(u);
        float z = Mathf.Sin(h) * Mathf.Sin(v) + Mathf.Cos(h) * Mathf.Sin(2f * v);
        return new Vector3(x, z, y);
    }

    // 法線 = Cross(∂p/∂v, ∂p/∂u) を正規化したもの。∂p/∂u, ∂p/∂v は式を手で微分したもの。
    // 三角形を (a, c, b) の順に並べたとき、Unity が「表」とみなす側がこの向き（Torus.cs と同じ理屈）
    Vector3 Normal(float u, float v)
    {
        float h = u * 0.5f, s = Mathf.Sin(h), c = Mathf.Cos(h);
        float sv = Mathf.Sin(v), cv = Mathf.Cos(v), s2 = Mathf.Sin(2f * v), c2 = Mathf.Cos(2f * v);
        float cu = Mathf.Cos(u), su = Mathf.Sin(u);

        float a  = ringRadius + c * sv - s * s2;
        float au = -0.5f * s * sv - 0.5f * c * s2;   // ∂A/∂u
        float av = c * cv - 2f * s * c2;             // ∂A/∂v

        var pu = new Vector3(au * cu - a * su, 0.5f * c * sv - 0.5f * s * s2, au * su + a * cu);
        var pv = new Vector3(av * cu,          s * cv + 2f * c * c2,          av * su);
        return Vector3.Cross(pv, pu).normalized;
    }

    void Build()
    {
        dirty = false;
        int nu = uSegments, nv = vSegments;

        int cols = nu + 1;               // u 方向は「最後の列 (i = nu)」を継ぎ目用に1列多く持つ
        int count = cols * nv;           // 片面ぶんの頂点数
        var positions = new Vector3[count * 2];   // 前半 = 表の面、後半 = 裏の面
        var normals = new Vector3[count * 2];
        var triangles = new int[nu * nv * 6 * 2];

        // 格子点 (i, j) の頂点番号。i: u 方向、j: v 方向
        int Index(int i, int j) => i * nv + j;

        for (int i = 0; i < nu; i++)
        {
            float u = 2f * Mathf.PI * i / nu;
            for (int j = 0; j < nv; j++)
            {
                float v = 2f * Mathf.PI * j / nv;
                positions[Index(i, j)] = Position(u, v) * size;
                normals[Index(i, j)] = Normal(u, v);
            }
        }

        // 継ぎ目の列 (i = nu) は、最初の列 (i = 0) を「v を逆向きにして」貼ったもの。
        //   位置: (0, −v) と同じ点 → 番号は (nv − j) % nv         ← トーラスに無かった「裏返し」がこの1行
        //   法線: 同じ点でも向きは逆 → マイナスを付ける             ← 向き付け不可能の正体
        for (int j = 0; j < nv; j++)
        {
            int src = Index(0, (nv - j) % nv);
            positions[Index(nu, j)] = positions[src];
            normals[Index(nu, j)] = -normals[src];
        }

        // 両面: 後半に、同じ位置で法線を反転した頂点を用意する（裏の面）
        for (int k = 0; k < count; k++)
        {
            positions[count + k] = positions[k];
            normals[count + k] = -normals[k];
        }

        // 各格子マスを 2 枚の三角形に分ける。u 方向は回り込ませず、継ぎ目の列 (i+1 = nu) につなぐ。v 方向だけ % で回り込む。
        //   表: (a, c, b), (b, c, d)      裏: 表の順序を逆にしたもの（頂点は count 個ぶんずらす）
        int t = 0;
        for (int i = 0; i < nu; i++)
        {
            for (int j = 0; j < nv; j++)
            {
                int j1 = (j + 1) % nv;
                int a = Index(i, j), b = Index(i + 1, j), c = Index(i, j1), d = Index(i + 1, j1);

                triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;

                triangles[t++] = count + a; triangles[t++] = count + b; triangles[t++] = count + c;
                triangles[t++] = count + b; triangles[t++] = count + d; triangles[t++] = count + c;
            }
        }

        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.hideFlags = HideFlags.DontSave; // 実行時に毎回作るのでシーンファイルには保存しない
        }
        mesh.Clear();
        mesh.name = "KleinBottle";
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().sharedMesh = mesh;

        // 掴むための当たり判定。MeshCollider があれば同じ Mesh を渡す（null を挟むのは再計算させるため）。
        // 両面の Mesh なので、レイがどちら向きの三角形に当たっても掴める。
        if (TryGetComponent(out MeshCollider col))
        {
            // Convex（凸包）にすると壺の空洞が塞がる上、面数の上限 (256) も超える。Convex は OFF にすること。
            if (col.convex)
                Debug.LogWarning("KleinBottle: MeshCollider の Convex が ON だと空洞が塞がります。OFF にしてください。", this);
            col.sharedMesh = null;
            col.sharedMesh = mesh;
        }
    }
}
