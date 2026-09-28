using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// ボタンを押すと、シーン内の掴める物体を全部、起動時の位置・向き・大きさへスーッと戻す。
// 空の GameObject に1個だけ付ける。
public class RecallButton : MonoBehaviour
{
    // 入力アクション: 「何のボタンで発動するか」を Inspector で差し替えられる形で持つ。
    // 初期値は左コントローラーの Y ボタン（XR コントローラー共通の名前では secondaryButton）
    [SerializeField] InputAction recallAction =
        new InputAction("Recall", InputActionType.Button, "<XRController>{LeftHand}/secondaryButton");

    [SerializeField, Min(0.05f)] float duration = 0.6f; // 戻るのにかける時間 [秒]

    // 起動時の状態を覚えておく箱
    struct Home
    {
        public XRGrabInteractable interactable;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    readonly List<Home> homes = new();
    Coroutine running;

    // 入力アクションは「有効化」しないと反応しない。コンポーネントの有効・無効に合わせてオンオフする
    void OnEnable() => recallAction.Enable();
    void OnDisable() => recallAction.Disable();

    void Start()
    {
        // シーンにある XR Grab Interactable を全部拾って、今の姿勢を「家」として記録
        foreach (var gi in FindObjectsByType<XRGrabInteractable>(FindObjectsSortMode.None))
        {
            var t = gi.transform;
            homes.Add(new Home { interactable = gi, position = t.position, rotation = t.rotation, scale = t.localScale });
        }
    }

    void Update()
    {
        if (recallAction.WasPressedThisFrame())
        {
            if (running != null) StopCoroutine(running); // 戻ってる最中に連打されたら、今の位置から仕切り直し
            running = StartCoroutine(Recall());
        }
    }

    // コルーチン: 何フレームにもまたがる処理を、1本の関数として上から順に書ける仕組み。
    // yield return null で「続きは次のフレーム」になる
    IEnumerator Recall()
    {
        // 出発点を記録（今どこにいるか）
        var starts = new List<(Vector3 p, Quaternion r, Vector3 s)>();
        foreach (var h in homes)
        {
            var t = h.interactable.transform;
            starts.Add((t.position, t.rotation, t.localScale));
        }

        for (var elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime)
        {
            var k = Mathf.SmoothStep(0f, 1f, elapsed / duration); // 出だしと着地をゆっくりに
            Apply(starts, k);
            yield return null;
        }
        Apply(starts, 1f); // 最後はピッタリ家に置く
        running = null;
    }

    void Apply(List<(Vector3 p, Quaternion r, Vector3 s)> starts, float k)
    {
        for (var i = 0; i < homes.Count; i++)
        {
            var h = homes[i];
            if (h.interactable == null || h.interactable.isSelected) continue; // 誰かが掴んでる物体には手を出さない

            var t = h.interactable.transform;
            t.SetPositionAndRotation(Vector3.Lerp(starts[i].p, h.position, k),
                                     Quaternion.Slerp(starts[i].r, h.rotation, k));
            t.localScale = Vector3.Lerp(starts[i].s, h.scale, k);
        }
    }
}
