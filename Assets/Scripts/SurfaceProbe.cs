using UnityEngine;
using UnityEngine.InputSystem;

// 曲面（メビウスの帯）の上に線を描く「ペン」。
//
// 右トリガーを押している間、右手のレイが MobiusStrip に当たった点について
//   ① hit.textureCoord から (u, v) を読む（Mesh の UV に (u, v) を入れてあるので、補間済みの値がそのまま来る）
//   ② その (u, v) を帯の子の SurfaceCurve に渡す（SurfaceCurve が (u, v) の点列として持ち、チューブにする）
//   ③ その (u, v) を式に戻した点へ Marker の球を置く（ペン先の目印）
// トリガーを離す、または帯からレイが外れると、そこで 1 画が終わる。次に当たったら新しい画が始まる。
// 右 B ボタンで、描いた線を全部消す。
//
// 自前の Physics.Raycast を使う（XRI のレイとは別）。トリガーの XRI 側の割り当ては Activate / UI Press で、
// 掴んでいる物体が無ければ何も起きないので、同時に使っても干渉しない。
public class SurfaceProbe : MonoBehaviour
{
    [SerializeField] Transform rayOrigin;          // 右手のレイの出どころ。Hierarchy の Right Controller の下の Near-Far Interactor を入れる
    [SerializeField] float maxDistance = 10f;
    [SerializeField] float markerSize = 0.01f;     // 球の直径 (m)（Marker を自作する時だけ使う）

    // 既定バインディング: 右手トリガー（XRI の標準設定と同じ {TriggerButton}）。Inspector で差し替え可。
    [SerializeField] InputAction probeAction =
        new InputAction("Probe", InputActionType.Button, "<XRController>{RightHand}/{TriggerButton}");

    // 既定バインディング: 右 B ボタン（secondaryButton）。XRI の標準設定では空きボタン。
    [SerializeField] InputAction clearAction =
        new InputAction("ClearCurves", InputActionType.Button, "<XRController>{RightHand}/secondaryButton");

    // ペン先の球。シーンに置いた Sphere（コライダーなし）を入れる。空ならスクリプトで作る（実機で左右に割れて見えたので、シーン側を推奨）
    [SerializeField] Transform marker;

    bool materialReady;
    SurfaceCurve activeCurve;   // 今描いている線（描いていなければ null）

    void OnEnable()
    {
        probeAction.Enable();   // Enable() しないと反応しない
        clearAction.Enable();
    }

    void OnDisable()
    {
        probeAction.Disable();
        clearAction.Disable();
    }

    void Start()
    {
        if (marker == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "ProbeMarker";
            Destroy(go.GetComponent<Collider>());   // 自分のレイに当たらないよう、コライダーは外す
            go.transform.localScale = Vector3.one * markerSize;
            marker = go.transform;
        }
        else if (marker.TryGetComponent(out Collider c))
        {
            Destroy(c);                              // シーンの球にコライダーが残っていても、自分のレイに当たらないよう外す
        }
        marker.gameObject.SetActive(false);
    }

    void Update()
    {
        if (clearAction.WasPressedThisFrame())
            foreach (var curve in FindObjectsByType<SurfaceCurve>())
                curve.Clear();

        if (rayOrigin == null)
        {
            if (Time.frameCount % 300 == 0)
                Debug.LogWarning("SurfaceProbe: Ray Origin が未設定です。Inspector で右手のレイの Transform を入れてください。", this);
            return;
        }

        if (!probeAction.IsPressed()) { Release(); return; }

        var ray = new Ray(rayOrigin.position, rayOrigin.forward);
        if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance, ~0, QueryTriggerInteraction.Ignore)
            || !hit.collider.TryGetComponent(out MobiusStrip strip))
        {
            Release();   // 帯から外れたら、1 画を終える
            return;
        }

        // 球のマテリアルは、帯のマテリアルを複製して色を付けたものを使う（最初の 1 回だけ）
        if (!materialReady)
        {
            var src = strip.GetComponent<Renderer>().sharedMaterial;
            marker.GetComponent<Renderer>().material = new Material(src) { color = Color.green };
            materialReady = true;
        }

        Vector2 uv = hit.textureCoord;                       // (u, v)
        marker.position = strip.SurfacePoint(uv.x, uv.y);    // 式へ戻した点（ワールド座標）
        marker.gameObject.SetActive(true);

        // 帯の子の SurfaceCurve に (u, v) を渡す。別の帯に移ったら、前の線の 1 画は終える
        var target = strip.GetComponentInChildren<SurfaceCurve>();
        if (target != activeCurve)
        {
            EndStroke();
            activeCurve = target;
        }
        if (activeCurve != null) activeCurve.AddPoint(uv);
    }

    void EndStroke()
    {
        if (activeCurve == null) return;
        activeCurve.EndStroke();
        activeCurve = null;
    }

    void Release()
    {
        EndStroke();
        marker.gameObject.SetActive(false);
    }
}
