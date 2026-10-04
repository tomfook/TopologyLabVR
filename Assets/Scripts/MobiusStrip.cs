using UnityEngine;

// メビウスの帯の Mesh を「パラメータ (u, v) の格子」から手組みするコンポーネント。
//
//   A(u,v) = R + v·cos(u/2)
//   x = A·cos u,   y = A·sin u,   z = v·sin(u/2)      (0 ≤ u < 2π,  −w ≤ v ≤ w)
//   Unity へは (x, z, y) として渡す（数学の z 軸 = Unity の上 = Y 軸）
//
// 輪を一周する間に、幅方向の線分 (v の軸) が半回転 (u/2) する。これが「半ひねり」。
//
// クラインの壺と同じ貼り合わせ:
//   (2π, v) = (0, −v)   ← v を逆向きにして貼る
// 式へ u = 2π を入れると cos(u/2) = −1, sin(u/2) = 0 になり、ちょうど (0, −v) の点に一致する。
//
// クラインの壺との違いは v の扱いだけ:
//   クラインの壺: v は周期 (0 〜 2π) → v 方向も回り込む。縁は無い（閉じた面）
//   メビウスの帯: v は区間 [−w, w] → v 方向は回り込まない。縁がある（境界が 1 本の輪になる）
//   そのぶん、頂点の行が 1 行多い (nv + 1 行)。
//
// ── この面は「向き付け不可能」──（クラインの壺と同じ）
//   ① 継ぎ目: 貼り合わせ位置では位置は同じでも法線は逆向き → 継ぎ目の列を複製して法線にマイナス
//   ② 表裏: 一周すると表が裏に入れ替わる → 表の面と裏の面を両方作る（両面 Mesh）
//   メッシュコライダーへのレイも、両面にしておけばどちら向きにも当たる。
[ExecuteAlways]                 // 再生していないエディタ上でも動かす
[DefaultExecutionOrder(-100)]   // 同じ GameObject の XR Grab Interactable より先に初期化してコライダーを用意する
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class MobiusStrip : ParametricSurface
{
    [SerializeField, Min(0.01f)]    float majorRadius = 0.12f;  // R: 輪の中心線の半径 (m)
    [SerializeField, Min(0.001f)]   float halfWidth = 0.05f;    // w: 帯の半幅 (m)。R より小さくすること（R 以上だと帯が回転軸に触れて潰れる）
    [SerializeField, Range(4, 256)] int uSegments = 96;         // 輪っか方向の分割数
    [SerializeField, Range(1, 64)]  int vSegments = 8;          // 幅方向の分割数

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

    // 実際に使う半幅。R 以上にならないよう頭打ちにする
    public float HalfWidth => Mathf.Min(halfWidth, majorRadius * 0.95f);

    // 式そのもの。(x, y, z) の y と z を入れ替えて Unity の座標にする（このオブジェクトのローカル座標）
    Vector3 Position(float u, float v)
    {
        float h = u * 0.5f;
        float a = majorRadius + v * Mathf.Cos(h);
        return new Vector3(a * Mathf.Cos(u), v * Mathf.Sin(h), a * Mathf.Sin(u));
    }

    // ── ParametricSurface の窓口 ──
    // (u, v) → ワールド座標の SurfacePoint は親クラスにある（u は 2π を超えてもよい。式は u 方向に連続でつながる）
    public override Vector2 UVMin => new Vector2(0f, -HalfWidth);
    public override Vector2 UVMax => new Vector2(2f * Mathf.PI, HalfWidth);

    // (u, v) → このオブジェクトのローカル座標 / ローカル法線（線を描く側が子オブジェクトの Mesh を作るのに使う）
    public override Vector3 LocalPoint(float u, float v) => Position(u, v);
    public override Vector3 LocalNormal(float u, float v) => Normal(u, v);

    // 継ぎ目の規則を使った「持ち上げ」。この面は u を 2π 進めると (u, v) → (u, −v) に貼り合わさる:
    //   P(u + 2π, v) = P(u, −v)       （だから u を 4π 進めると元の点に戻る）
    // レイから読んだ (u, v) は 0 ≤ u ≤ 2π の範囲に畳まれているので、そのまま点列にすると継ぎ目で u が 2π → 0、v が反転して飛ぶ。
    // 前の点 previous（u は 2π を超えていてよい）に一番近い「同じ点の別名」(u + 2πk, (−1)^k · v) を返して、u を連続に伸ばす。
    public override Vector2 LiftNear(Vector2 previous, Vector2 uv)
    {
        int k = Mathf.RoundToInt((previous.x - uv.x) / (2f * Mathf.PI));
        float sign = (k & 1) == 0 ? 1f : -1f;   // k が奇数なら v の向きが反転（負の奇数でも (k & 1) == 1）
        return new Vector2(uv.x + 2f * Mathf.PI * k, sign * uv.y);
    }

    // ∂p/∂u, ∂p/∂v は式を手で微分したもの。A = R + v·cos(u/2) とおくと ∂A/∂u = −0.5·v·sin(u/2)、∂A/∂v = cos(u/2)
    public override Vector3 dPdu(float u, float v)
    {
        float h = u * 0.5f, s = Mathf.Sin(h), c = Mathf.Cos(h);
        float cu = Mathf.Cos(u), su = Mathf.Sin(u);
        float a  = majorRadius + v * c;
        float au = -0.5f * v * s;   // ∂A/∂u
        return new Vector3(au * cu - a * su, 0.5f * v * c, au * su + a * cu);
    }

    public override Vector3 dPdv(float u, float v)
    {
        float h = u * 0.5f, s = Mathf.Sin(h), c = Mathf.Cos(h);
        float cu = Mathf.Cos(u), su = Mathf.Sin(u);
        float av = c;               // ∂A/∂v
        return new Vector3(av * cu, s, av * su);
    }

    // 法線 = Cross(∂p/∂v, ∂p/∂u) を正規化したもの。
    // 三角形を (a, c, b) の順に並べたとき、Unity が「表」とみなす側がこの向き（Torus.cs / KleinBottle.cs と同じ理屈）
    Vector3 Normal(float u, float v) => Vector3.Cross(dPdv(u, v), dPdu(u, v)).normalized;

    void Build()
    {
        dirty = false;
        int nu = uSegments, nv = vSegments;
        float w = HalfWidth;

        int cols = nu + 1;               // u 方向は「最後の列 (i = nu)」を継ぎ目用に1列多く持つ
        int rows = nv + 1;               // v 方向は周期ではなく区間なので、端の行まで持つ（回り込まない）
        int count = cols * rows;         // 片面ぶんの頂点数
        var positions = new Vector3[count * 2];   // 前半 = 表の面、後半 = 裏の面
        var normals = new Vector3[count * 2];
        var triangles = new int[nu * nv * 6 * 2];

        // 格子点 (i, j) の頂点番号。i: u 方向、j: v 方向（0 〜 nv）
        int Index(int i, int j) => i * rows + j;

        for (int i = 0; i < nu; i++)
        {
            float u = 2f * Mathf.PI * i / nu;
            for (int j = 0; j < rows; j++)
            {
                float v = -w + 2f * w * j / nv;
                positions[Index(i, j)] = Position(u, v);
                normals[Index(i, j)] = Normal(u, v);
            }
        }

        // 継ぎ目の列 (i = nu) は、最初の列 (i = 0) を「v を逆向きにして」貼ったもの。
        //   位置: (0, −v) と同じ点 → 行番号は nv − j（v = −w + 2w·j/nv の符号を変えると j が nv − j になる）
        //   法線: 同じ点でも向きは逆 → マイナスを付ける
        for (int j = 0; j < rows; j++)
        {
            int src = Index(0, nv - j);
            positions[Index(nu, j)] = positions[src];
            normals[Index(nu, j)] = -normals[src];
        }

        // UV には (u, v) そのものを入れる。レイが当たった三角形の重心座標で補間されるので、
        // hit.textureCoord がそのまま「当たった点のパラメータ (u, v)」になる（曲面に描くための逆引き）。
        // 継ぎ目の列 (i = nu) は u = 2π。位置は列 0 の (−v) と同じ点だが、パラメータとしては (2π, v) なので補間が途切れない。
        var uvs = new Vector2[count * 2];
        for (int i = 0; i < cols; i++)
        {
            float u = 2f * Mathf.PI * i / nu;
            for (int j = 0; j < rows; j++)
                uvs[Index(i, j)] = new Vector2(u, -w + 2f * w * j / nv);
        }

        // 両面: 後半に、同じ位置で法線を反転した頂点を用意する（裏の面）。UV は同じ
        for (int k = 0; k < count; k++)
        {
            positions[count + k] = positions[k];
            normals[count + k] = -normals[k];
            uvs[count + k] = uvs[k];
        }

        // 各格子マスを 2 枚の三角形に分ける。u 方向は継ぎ目の列 (i+1 = nu) につなぐ。v 方向は回り込まない（j+1 ≤ nv）。
        //   表: (a, c, b), (b, c, d)      裏: 表の順序を逆にしたもの（頂点は count 個ぶんずらす）
        int t = 0;
        for (int i = 0; i < nu; i++)
        {
            for (int j = 0; j < nv; j++)
            {
                int a = Index(i, j), b = Index(i + 1, j), c = Index(i, j + 1), d = Index(i + 1, j + 1);

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
        mesh.name = "MobiusStrip";
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().sharedMesh = mesh;

        // 掴むための当たり判定。MeshCollider があれば同じ Mesh を渡す（null を挟むのは再計算させるため）。
        // 両面の Mesh なので、レイがどちら向きの三角形に当たっても掴める。
        if (TryGetComponent(out MeshCollider col))
        {
            // Convex（凸包）にすると帯の穴が塞がる。Convex は OFF にすること。
            if (col.convex)
                Debug.LogWarning("MobiusStrip: MeshCollider の Convex が ON だと穴が塞がります。OFF にしてください。", this);
            col.sharedMesh = null;
            col.sharedMesh = mesh;
        }
    }
}
