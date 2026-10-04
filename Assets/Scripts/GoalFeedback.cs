using UnityEngine;

// ゴールに着いたときの知らせ: 音（その場で作るチャイム）と、迷路全体が金色にぱっと光ってもとに戻る演出。
// MazeWalker の GoalReached を聞いて動く。Maze と同じ GameObject に付ける想定。
//
// 音: 音声ファイルは使わず、サイン波を重ねて AudioClip をコードで作る（ド・ミ・ソ・高いドの順に鳴る、だんだん小さくなる音）。
//     実機では Console が見えないので、音と光で「着いた」と分かるようにしている。
// 光: Material の色そのものは書き換えず、MaterialPropertyBlock（描画時だけ色を上書きする仕組み）で壁と床の色を
//     金色へ寄せ、時間とともにもとの色へ戻す。Material のファイルや他のオブジェクトには影響しない。
[RequireComponent(typeof(AudioSource))]
public class GoalFeedback : MonoBehaviour
{
    [SerializeField] MazeWalker walker;   // 空なら同じ GameObject の MazeWalker
    [SerializeField] Color flashColor = new Color(1f, 0.85f, 0.2f);
    [SerializeField, Min(0.1f)] float flashSeconds = 1.5f;
    [SerializeField, Range(0f, 1f)] float volume = 0.6f;

    // 光らせる Material の番号（MazeView のサブメッシュ: 0 = 壁 / 1 = 床 / 2 = 床の市松）
    const int LitMaterials = 3;

    AudioSource source;
    AudioClip chime;
    MeshRenderer view;
    MaterialPropertyBlock block;
    float timer;   // 光の残り時間。0 なら光っていない

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;           // 2D（頭の中で鳴る）。位置に依存しない
        view = GetComponent<MeshRenderer>();
        block = new MaterialPropertyBlock();
        if (walker == null) walker = GetComponent<MazeWalker>();
        chime = MakeChime();
    }

    void OnEnable() { if (walker != null) walker.GoalReached += OnGoal; }
    void OnDisable() { if (walker != null) walker.GoalReached -= OnGoal; ClearFlash(); }

    void OnGoal()
    {
        source.PlayOneShot(chime, volume);
        timer = flashSeconds;
    }

    void Update()
    {
        if (timer <= 0f) return;
        timer -= Time.deltaTime;
        if (timer <= 0f) { ClearFlash(); return; }

        // 最初は金色、だんだんもとの色へ（二乗でゆっくり戻る）
        float k = timer / flashSeconds;
        k *= k;
        var mats = view.sharedMaterials;
        for (int i = 0; i < LitMaterials && i < mats.Length; i++)
        {
            if (mats[i] == null) continue;
            string prop = mats[i].HasProperty("_BaseColor") ? "_BaseColor" : "_Color";   // URP の Lit は _BaseColor
            view.GetPropertyBlock(block, i);
            block.SetColor(prop, Color.Lerp(mats[i].GetColor(prop), flashColor, k));
            view.SetPropertyBlock(block, i);
        }
    }

    void ClearFlash()
    {
        timer = 0f;
        if (view == null) return;
        for (int i = 0; i < LitMaterials; i++) view.SetPropertyBlock(null, i);   // 上書きを外してもとの色へ
    }

    // ド・ミ・ソ・高いド を 0.15 秒ずつずらして鳴らす。1 音はサイン波 + 1 オクターブ上を少し混ぜて、指数的に小さくする
    static AudioClip MakeChime()
    {
        const int rate = 44100;
        float length = 1.6f;
        int n = (int)(rate * length);
        var data = new float[n];
        float[] freqs = { 523.25f, 659.25f, 783.99f, 1046.5f };
        for (int note = 0; note < freqs.Length; note++)
        {
            int start = (int)(rate * 0.15f * note);
            for (int i = start; i < n; i++)
            {
                float t = (i - start) / (float)rate;
                float env = Mathf.Exp(-3.5f * t) * Mathf.Min(1f, t * 200f);   // 立ち上がりを少し丸めて、あとは指数で減衰
                float s = Mathf.Sin(2f * Mathf.PI * freqs[note] * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * freqs[note] * t);
                data[i] += 0.25f * env * s;
            }
        }
        var clip = AudioClip.Create("GoalChime", n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
