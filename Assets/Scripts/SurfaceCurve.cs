using System.Collections.Generic;
using UnityEngine;

// 曲面（メビウスの帯）の上に描いた線を持って、細いチューブの Mesh にして見せるコンポーネント。
//
// ── 線は「3D の点」ではなく「曲面のパラメータ (u, v) の点列」として持つ ──
//   ・この GameObject は MobiusStrip の「子」に置く。だから掴んで動かしても、拡大縮小しても、線は帯についてくる。
//   ・(u, v) から 3D の点を出すのは、帯の式（MobiusStrip.LocalPoint / LocalNormal）。点列が曲面の上にあることは構造上保証される。
//   ・継ぎ目をまたぐと (u, v) が (0, −v) に飛ぶので、点を足すたびに MobiusStrip.LiftNear で「前の点に一番近い別名」へ持ち上げて、
//     u を 2π を超えて連続に伸ばす。メビウスの帯を一周すると v の符号が反転する、という性質が、そのまま点列に現れる。
//     （u が 4π 進むと元の位置に戻る。v ≠ 0 の線は 2 周しないと閉じない。v = 0 の中心線は 1 周で閉じる。）
//
// ── チューブの作り方 ──
//   各点で、接線 T（隣の点との差）と面の法線 N から、断面の円の向き (N2, B) を決める。
//     B = T × N,  N2 = B × T       → N2, B, T は互いに直交（右手系）
//   断面の円は p + radius·(cosθ·N2 + sinθ·B)。面の法線 N を基準にするので、チューブがねじれない。
//   法線は面の中心を通る円なので、表裏どちらから見ても同じ太さの線に見える（メビウスで「表と裏」を区別しなくて済む）。
//   三角形は (a, b, c) の並びが外向き（Torus.cs などの (a, c, b) とは、向きの基準が逆なのでこの並びになる）。
//   両端は小さな円盤（扇形）でふさぐ。
//
// ── 置き方 ──
//   MobiusStrip の子の空の GameObject に付ける（MeshFilter / MeshRenderer は RequireComponent で自動で付く）。
//   実機で見せる GameObject はシーンに置く（実行時に作った球が左右に割れたため）。Mesh の中身だけ実行時に作る。
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SurfaceCurve : MonoBehaviour
{
    [SerializeField, Min(0.0005f)]  float radius = 0.0025f;   // チューブの半径 (m)
    [SerializeField, Range(3, 16)]  int sides = 8;            // 断面の円の分割数
    [SerializeField, Min(0.0005f)]  float minStep = 0.004f;   // 点を足す最小間隔 (m)。これより近い点は捨てる
    [SerializeField] Material material;                       // 空なら、帯のマテリアルを複製して下の色を付ける
    [SerializeField] Color color = new Color(1f, 0.35f, 0.1f);

    MobiusStrip strip;
    Mesh mesh;
    readonly List<List<Vector2>> strokes = new List<List<Vector2>>();   // 1 画 = (u, v) の点列
    List<Vector2> current;                                              // 今描いている画（描いていなければ null）

    // 作り直しのたびに使い回す作業用の入れ物（毎回 new しない）
    readonly List<Vector3> vertices = new List<Vector3>();
    readonly List<Vector3> normals = new List<Vector3>();
    readonly List<int> triangles = new List<int>();

    void Awake()
    {
        strip = GetComponentInParent<MobiusStrip>();

        // 帯の子として、帯のローカル座標とぴったり重ねる（式のローカル座標をそのまま使うため）
        transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
        transform.localScale = Vector3.one;

        var meshRenderer = GetComponent<MeshRenderer>();
        if (material != null)
            meshRenderer.sharedMaterial = material;
        else if (strip != null)
            meshRenderer.material = new Material(strip.GetComponent<Renderer>().sharedMaterial) { color = color };

        mesh = new Mesh { name = "SurfaceCurve", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();   // 何度も作り直す Mesh だと Unity に知らせる
        GetComponent<MeshFilter>().sharedMesh = mesh;
    }

    void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
    }

    // レイから読んだ (u, v)（0 ≤ u ≤ 2π に畳まれている）を、今の画に足す。画が無ければ新しく始める
    public void AddPoint(Vector2 uv)
    {
        if (strip == null) return;
        if (current == null)
        {
            current = new List<Vector2>();
            strokes.Add(current);
        }

        Vector2 p = uv;
        if (current.Count > 0)
        {
            Vector2 last = current[current.Count - 1];
            p = strip.LiftNear(last, uv);   // 継ぎ目をまたいでも u を連続に伸ばす
            Vector3 a = strip.LocalPoint(p.x, p.y), b = strip.LocalPoint(last.x, last.y);
            if ((a - b).sqrMagnitude < minStep * minStep) return;   // 近すぎる点は捨てる
        }

        current.Add(p);
        Rebuild();
    }

    // 今の画を終える（トリガーを離した、または帯からレイが外れた）。次の AddPoint で新しい画が始まる
    public void EndStroke() => current = null;

    // 全部消す
    public void Clear()
    {
        strokes.Clear();
        current = null;
        Rebuild();
    }

    void Rebuild()
    {
        vertices.Clear();
        normals.Clear();
        triangles.Clear();

        foreach (var stroke in strokes)
            if (stroke.Count >= 2) AppendTube(stroke);

        mesh.Clear();
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
    }

    void AppendTube(List<Vector2> stroke)
    {
        int n = stroke.Count;
        var position = new Vector3[n];
        for (int k = 0; k < n; k++) position[k] = strip.LocalPoint(stroke[k].x, stroke[k].y);

        int firstRing = vertices.Count;                 // 輪 k の頂点は firstRing + k * sides + j
        Vector3 startTangent = Vector3.zero, endTangent = Vector3.zero;

        for (int k = 0; k < n; k++)
        {
            // 接線: 端は片側の差、途中は前後の差
            Vector3 t = k == 0 ? position[1] - position[0]
                      : k == n - 1 ? position[n - 1] - position[n - 2]
                      : position[k + 1] - position[k - 1];
            t.Normalize();
            if (k == 0) startTangent = t;
            if (k == n - 1) endTangent = t;

            Vector3 surfaceNormal = strip.LocalNormal(stroke[k].x, stroke[k].y);
            Vector3 b = Vector3.Cross(t, surfaceNormal).normalized;
            Vector3 n2 = Vector3.Cross(b, t);

            for (int j = 0; j < sides; j++)
            {
                float theta = 2f * Mathf.PI * j / sides;
                Vector3 d = Mathf.Cos(theta) * n2 + Mathf.Sin(theta) * b;   // 断面の外向き
                vertices.Add(position[k] + radius * d);
                normals.Add(d);
            }
        }

        // 隣り合う輪をつなぐ。(a, b, c), (b, d, c) の並びが外向き
        for (int k = 0; k < n - 1; k++)
        {
            for (int j = 0; j < sides; j++)
            {
                int j1 = (j + 1) % sides;
                int a = firstRing + k * sides + j;
                int b = firstRing + k * sides + j1;
                int c = firstRing + (k + 1) * sides + j;
                int d = firstRing + (k + 1) * sides + j1;
                triangles.Add(a); triangles.Add(b); triangles.Add(c);
                triangles.Add(b); triangles.Add(d); triangles.Add(c);
            }
        }

        // 両端のふた。法線が違うので頂点を複製する。始点は −T 向き、終点は +T 向き
        AppendCap(firstRing, position[0], -startTangent, true);
        AppendCap(firstRing + (n - 1) * sides, position[n - 1], endTangent, false);
    }

    void AppendCap(int ringStart, Vector3 center, Vector3 normal, bool flip)
    {
        int c = vertices.Count;
        vertices.Add(center);
        normals.Add(normal);
        int rim = vertices.Count;
        for (int j = 0; j < sides; j++)
        {
            vertices.Add(vertices[ringStart + j]);
            normals.Add(normal);
        }
        for (int j = 0; j < sides; j++)
        {
            int j1 = (j + 1) % sides;
            if (flip) { triangles.Add(c); triangles.Add(rim + j1); triangles.Add(rim + j); }
            else      { triangles.Add(c); triangles.Add(rim + j);  triangles.Add(rim + j1); }
        }
    }
}
