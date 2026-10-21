using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

// 「光る」の共通部品。文字を減らす代わりに、光り方で「次はこれを触る」「結果はこう」を伝える。
//   ・脈打つ（Pulse = ON）: 触っていい物・次に触る物。ゆっくり明るくなったり戻ったりする
//   ・手やレイが近づく（同じ GameObject に XR の Interactable があれば、その Hover を見る）: もう一段明るく
//   ・色を付けたままにする（SetTint）: 状態を表す（例: 角が閉じている間はうっすら緑）
//   ・ぱっと光る（Flash）: 結果の知らせ（緑 = できた / 青 = 平らのまま / 赤 = だめ）。だんだんもとの色へ戻る
//
// Material のファイルは書き換えず、MaterialPropertyBlock で描画のときだけ色を上書きする（GoalFeedback と同じ）。
// Renderer の Material が何個あっても全部に効く。
[RequireComponent(typeof(Renderer))]
public class Glow : MonoBehaviour
{
    [SerializeField] bool pulse = true;
    [SerializeField] Color glowColor = Color.white;
    [Tooltip("脈打つときにどれだけ glowColor へ寄せるか")]
    [SerializeField, Range(0f, 1f)] float pulseAmount = 0.45f;
    [Tooltip("手やレイが近づいたときにどれだけ glowColor へ寄せるか")]
    [SerializeField, Range(0f, 1f)] float hoverAmount = 0.6f;
    [SerializeField, Min(0.1f)] float period = 1.4f;
    [SerializeField, Min(0.1f)] float flashSeconds = 1.2f;

    public bool Pulse { get => pulse; set => pulse = value; }
    // 手やレイが近づいたときに全体を明るくするか。持ち主が「一部だけ色を変える」で知らせたいときは切る
    public bool HoverGlow { get => hoverGlow; set => hoverGlow = value; }
    bool hoverGlow = true;

    Renderer rend;
    XRBaseInteractable interactable;   // 無くてもよい
    MaterialPropertyBlock block;
    Color tint; float tintAmount;
    Color flashColor; float flashTimer;

    void Awake()
    {
        rend = GetComponent<Renderer>();
        interactable = GetComponent<XRBaseInteractable>();
        block = new MaterialPropertyBlock();
    }

    public void Flash(Color c) { flashColor = c; flashTimer = flashSeconds; }
    public void SetTint(Color c, float amount) { tint = c; tintAmount = Mathf.Clamp01(amount); }
    public void ClearTint() => tintAmount = 0f;

    void Update()
    {
        float k = 0f;
        if (pulse) k = pulseAmount * (0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * Time.time / period));
        if (hoverGlow && interactable != null && interactable.isHovered) k = Mathf.Max(k, hoverAmount);

        float f = 0f;
        if (flashTimer > 0f)
        {
            flashTimer -= Time.deltaTime;
            f = Mathf.Max(0f, flashTimer / flashSeconds);
            f *= f;   // 最初はくっきり、だんだんゆっくり戻る
        }

        var mats = rend.sharedMaterials;
        if (k <= 0f && f <= 0f && tintAmount <= 0f)
        {
            for (int i = 0; i < mats.Length; i++) rend.SetPropertyBlock(null, i);   // 何もしないときは上書きを外す
            return;
        }
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            if (m == null) continue;
            string prop = m.HasProperty("_BaseColor") ? "_BaseColor" : "_Color";   // URP の Lit / Unlit は _BaseColor
            Color baseColor = m.GetColor(prop);
            Color c = Color.Lerp(baseColor, tint, tintAmount);
            c = Color.Lerp(c, glowColor, k);
            c = Color.Lerp(c, flashColor, f);
            c.a = baseColor.a;   // 半透明の Material は透け具合を保つ
            rend.GetPropertyBlock(block, i);
            block.SetColor(prop, c);
            rend.SetPropertyBlock(block, i);
        }
    }

    void OnDisable()
    {
        if (rend == null) return;
        for (int i = 0; i < rend.sharedMaterials.Length; i++) rend.SetPropertyBlock(null, i);
    }
}
