using UnityEngine;

// 「パラメータ (u, v) で表せる曲面」の共通の窓口。Torus / MobiusStrip などの親玉。
//
// ── なぜ interface ではなく abstract class（しかも MonoBehaviour の子）なのか ──
//   Unity の Inspector は「interface 型の欄」を出せない（シリアライズできない）。
//   だから SurfaceProbe のように「どの曲面か」を扱う側は、MonoBehaviour を継承した抽象クラスで受ける。
//   abstract のメンバは C++ の純粋仮想関数と同じで、子クラスが override して中身を書く（書かないとコンパイルが通らない）。
//
// ── この窓口が約束すること ──
//   ・曲面は (u, v) ∈ [UVMin, UVMax] の長方形から3次元への写像 p(u, v) で表される（座標はこのオブジェクトのローカル）。
//   ・長方形の辺は、曲面ごとの規則で貼り合わされている（トーラス = 両方向とも周期、メビウス = u 方向に v を反転して貼る）。
//     貼り合わせの規則は LiftNear に集約する。曲線を描く側・歩く側は、貼り合わせの中身を知らなくてよい。
//   ・Mesh の UV には (u, v) そのものを入れておくこと。レイが当たった三角形の hit.textureCoord が、そのまま (u, v) になる。
//
// Mesh を組み立てる部分（格子を作る、継ぎ目を複製する、両面にする）は、ここには入れていない。曲面ごとに残す。
public abstract class ParametricSurface : MonoBehaviour
{
    // 定義域の長方形。Mesh の UV に入る (u, v) の範囲でもある
    public abstract Vector2 UVMin { get; }
    public abstract Vector2 UVMax { get; }

    // 曲面の式。どれもこのオブジェクトのローカル座標
    public abstract Vector3 LocalPoint(float u, float v);     // p(u, v)
    public abstract Vector3 LocalNormal(float u, float v);    // 単位法線（Mesh の表側 = Cross(∂p/∂v, ∂p/∂u) の向き）
    public abstract Vector3 dPdu(float u, float v);           // ∂p/∂u （u を増やす向きの接ベクトル。長さは「距離/単位u」で単位長さではない）
    public abstract Vector3 dPdv(float u, float v);           // ∂p/∂v

    // 継ぎ目の規則を使った「持ち上げ」。
    // Mesh から読んだ (u, v) は定義域の中に畳まれているので、そのまま点列にすると継ぎ目で値が飛ぶ。
    // previous（定義域の外に出ていてよい）に一番近い「同じ点の別名」を返して、点列を連続に伸ばす。
    public abstract Vector2 LiftNear(Vector2 previous, Vector2 uv);

    // (u, v) → ワールド座標
    public Vector3 SurfacePoint(float u, float v) => transform.TransformPoint(LocalPoint(u, v));
}
