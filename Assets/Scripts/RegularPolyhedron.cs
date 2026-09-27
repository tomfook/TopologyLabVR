using System.Collections.Generic;
using UnityEngine;

// 正多面体の Mesh を「頂点の座標」と「面 = 頂点番号の並び」から手組みするコンポーネント。
[ExecuteAlways]                 // 再生していないエディタ上でも動かす → Scene ビューでそのまま形が見える
[DefaultExecutionOrder(-100)]   // 同じ GameObject の XR Grab Interactable より先に初期化してコライダーを用意する
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class RegularPolyhedron : MonoBehaviour
{
    public enum Kind { Tetrahedron, Cube, Octahedron, Dodecahedron, Icosahedron }

    [SerializeField] Kind kind = Kind.Tetrahedron;
    [SerializeField, Min(0.01f)] float radius = 0.15f; // 外接球の半径 [m]

    Mesh mesh;
    bool dirty = true;

    void OnEnable() => Build();
    void OnValidate() => dirty = true;  // Inspector で値を変えたら「作り直し予約」だけ（ここで Mesh を触ると警告が出る）
    void Update() { if (dirty) Build(); }

    void OnDestroy()
    {
        if (mesh == null) return;
        if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
    }

    void Build()
    {
        dirty = false;
        var (verts, faces) = Shape(kind);

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<int>();

        foreach (int[] face in faces)
        {
            // 正多面体の頂点はみな原点から等距離なので、正規化して半径を掛ければ外接球半径 = radius
            var p = new Vector3[face.Length];
            for (int i = 0; i < face.Length; i++) p[i] = verts[face[i]].normalized * radius;

            // 面の法線。Unity は「表から見て時計回りに並んだ三角形」が表で、Cross(b-a, c-a) がその表側を向く。
            // 下の面データは外から見て時計回りに並べてあるので、n は外向きになる。
            Vector3 n = Vector3.Cross(p[1] - p[0], p[2] - p[0]).normalized;

            // 面ごとに頂点を複製する（隣の面と共有しない）→ 法線が面ごとに不連続になり、角がくっきり出る
            int start = positions.Count;
            foreach (var q in p) { positions.Add(q); normals.Add(n); }

            // n 角形を扇形に三角形分割: (0,1,2), (0,2,3), ...
            for (int i = 1; i < p.Length - 1; i++)
            {
                triangles.Add(start);
                triangles.Add(start + i);
                triangles.Add(start + i + 1);
            }
        }

        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.hideFlags = HideFlags.DontSave; // 実行時に毎回作るのでシーンファイルには保存しない
        }
        mesh.Clear();
        mesh.name = kind.ToString();
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();

        GetComponent<MeshFilter>().sharedMesh = mesh;

        // 掴むための当たり判定。MeshCollider があれば同じ Mesh を渡す（null を挟むのは再計算させるため）
        if (TryGetComponent(out MeshCollider col))
        {
            col.sharedMesh = null;
            col.sharedMesh = mesh;
        }
    }

    // ---- 形のデータ ------------------------------------------------------------
    static readonly float P = (1f + Mathf.Sqrt(5f)) / 2f; // 黄金比 φ
    static readonly float Q = P - 1f;                     // 1/φ

    static (Vector3[] verts, int[][] faces) Shape(Kind k) => k switch
    {
        Kind.Tetrahedron => (
            new[] { V(1, 1, 1), V(1, -1, -1), V(-1, 1, -1), V(-1, -1, 1) },
            new[] { F(2, 0, 1), F(1, 0, 3), F(3, 0, 2), F(2, 1, 3) }),

        Kind.Cube => (
            new[] { V(-1, -1, -1), V(-1, -1, 1), V(-1, 1, -1), V(-1, 1, 1),
                    V( 1, -1, -1), V( 1, -1, 1), V( 1, 1, -1), V( 1, 1, 1) },
            new[] { F(2, 0, 1, 3), F(1, 0, 4, 5), F(4, 0, 2, 6),
                    F(3, 1, 5, 7), F(6, 2, 3, 7), F(5, 4, 6, 7) }),

        Kind.Octahedron => (
            new[] { V(1, 0, 0), V(-1, 0, 0), V(0, 1, 0), V(0, -1, 0), V(0, 0, 1), V(0, 0, -1) },
            new[] { F(4, 0, 2), F(2, 0, 5), F(3, 0, 4), F(5, 0, 3),
                    F(2, 1, 4), F(5, 1, 2), F(4, 1, 3), F(3, 1, 5) }),

        Kind.Dodecahedron => (
            new[] { V(-1, -1, -1), V(-1, -1, 1), V(-1, 1, -1), V(-1, 1, 1),   // 立方体の 8 頂点
                    V( 1, -1, -1), V( 1, -1, 1), V( 1, 1, -1), V( 1, 1, 1),
                    V(0, -Q, -P), V(0, -Q, P), V(0, Q, -P), V(0, Q, P),       // + 3 枚の黄金長方形風の 12 頂点
                    V(-Q, -P, 0), V(-Q, P, 0), V(Q, -P, 0), V(Q, P, 0),
                    V(-P, 0, -Q), V(-P, 0, Q), V(P, 0, -Q), V(P, 0, Q) },
            new[] { F(17, 16, 0, 12, 1), F(10, 8, 0, 16, 2), F(14, 12, 0, 8, 4),
                    F(3, 17, 1, 9, 11),  F(5, 9, 1, 12, 14), F(3, 13, 2, 16, 17),
                    F(6, 10, 2, 13, 15), F(15, 13, 3, 11, 7), F(5, 14, 4, 18, 19),
                    F(6, 18, 4, 8, 10),  F(11, 9, 5, 19, 7), F(19, 18, 6, 15, 7) }),

        _ /* Icosahedron */ => (
            new[] { V(0, -1, -P), V(0, -1, P), V(0, 1, -P), V(0, 1, P),       // 3 枚の黄金長方形の 12 頂点
                    V(-1, -P, 0), V(-1, P, 0), V(1, -P, 0), V(1, P, 0),
                    V(-P, 0, -1), V(-P, 0, 1), V(P, 0, -1), V(P, 0, 1) },
            new[] { F(2, 0, 8), F(10, 0, 2), F(4, 0, 6), F(8, 0, 4), F(6, 0, 10),
                    F(9, 1, 3), F(3, 1, 11), F(6, 1, 4), F(4, 1, 9), F(11, 1, 6),
                    F(7, 2, 5), F(5, 2, 8), F(10, 2, 7), F(5, 3, 7), F(9, 3, 5),
                    F(7, 3, 11), F(8, 4, 9), F(9, 5, 8), F(11, 6, 10), F(10, 7, 11) }),
    };

    static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);
    static int[] F(params int[] idx) => idx;
}
