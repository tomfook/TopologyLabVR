using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

// 掴んでいる物体が床より下に潜らないようにする Grab Transformer。
//
// XR Grab Interactable は毎フレーム「目標の姿勢 (位置・回転) とスケール」を用意して、
// 登録された Grab Transformer に順番に ref で回し、最後に出てきた値を物体に適用する。
//   手の位置 → [General Grab Transformer: 手に追従・両手で拡大縮小] → [これ: 床で止める] → 物体に適用
// このクラスは列の最後尾で、目標姿勢の高さだけを持ち上げる。物理演算は一切使わない。
[DefaultExecutionOrder(100)] // Start を General Grab Transformer より後に走らせる → 登録順が後ろ = 処理も後ろ
[RequireComponent(typeof(XRGrabInteractable))]
public class FloorClampGrabTransformer : XRBaseGrabTransformer
{
    [SerializeField] float floorHeight = 0f; // これより下には行かせない高さ [m]（ワールド座標の y）

    // 片手で掴んでいても両手でも働くように、両方のリストに登録させる
    protected override RegistrationMode registrationMode => RegistrationMode.SingleAndMultiple;

    Vector3[] vertices; // Mesh の頂点（物体ローカル座標）。掴んだ瞬間に取り直す

    public override void OnGrab(XRGrabInteractable grabInteractable)
    {
        // 正多面体の種類を切り替えると Mesh が変わるので、掴むたびに取り直す
        var meshFilter = GetComponent<MeshFilter>();
        vertices = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.vertices : null;
    }

    public override void Process(XRGrabInteractable grabInteractable, XRInteractionUpdateOrder.UpdatePhase updatePhase,
                                 ref Pose targetPose, ref Vector3 localScale)
    {
        if (vertices == null || vertices.Length == 0) return;

        // 目標の姿勢・スケールで置いたとき、一番低い頂点は物体の中心から何 m 下か
        //   ワールドでの頂点 = 位置 + 回転 * (スケール ⊙ ローカル頂点)
        var lowest = float.PositiveInfinity;
        foreach (var v in vertices)
        {
            var y = (targetPose.rotation * Vector3.Scale(v, localScale)).y;
            if (y < lowest) lowest = y;
        }

        // 一番低い点が床より下なら、その分だけ真上に持ち上げる（横方向と回転は手の動きのまま）
        var bottom = targetPose.position.y + lowest;
        if (bottom < floorHeight)
            targetPose.position.y += floorHeight - bottom;
    }
}
