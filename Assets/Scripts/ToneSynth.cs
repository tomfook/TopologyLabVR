using UnityEngine;

// 反応の音を、音声ファイルなしでコードで作る（GoalFeedback のチャイムと同じやり方）。
//   Chime = 正解（ド・ミ・ソ・高いド）/ Boing = 平らのまま（ボヨン）/ Buzz = だめ（ブブー）
public static class ToneSynth
{
    const int Rate = 44100;

    public static AudioClip Chime()
    {
        float[] freqs = { 523.25f, 659.25f, 783.99f, 1046.5f };
        var data = new float[(int)(Rate * 1.4f)];
        for (int note = 0; note < freqs.Length; note++)
        {
            int start = (int)(Rate * 0.12f * note);
            for (int i = start; i < data.Length; i++)
            {
                float t = (i - start) / (float)Rate;
                float env = Mathf.Exp(-3.5f * t) * Mathf.Min(1f, t * 200f);
                float s = Mathf.Sin(2f * Mathf.PI * freqs[note] * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * freqs[note] * t);
                data[i] += 0.22f * env * s;
            }
        }
        return Make("Chime", data);
    }

    // 低い音の高さが揺れながら下がっていく（バネが戻る感じ）。周波数が時間で変わるので、位相を足し合わせて作る
    public static AudioClip Boing()
    {
        var data = new float[(int)(Rate * 0.8f)];
        float phase = 0f;
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)Rate;
            float f = 150f * (1f + 0.5f * Mathf.Exp(-5f * t) * Mathf.Sin(2f * Mathf.PI * 8f * t)) * (1f - 0.3f * t);
            phase += 2f * Mathf.PI * f / Rate;
            float env = Mathf.Exp(-4f * t) * Mathf.Min(1f, t * 300f);
            data[i] = 0.5f * env * Mathf.Sin(phase);
        }
        return Make("Boing", data);
    }

    // 少しずらした低い 2 音を角張らせて重ねる（ブッブー）
    public static AudioClip Buzz()
    {
        var data = new float[(int)(Rate * 0.55f)];
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)Rate;
            // 0.2 秒鳴って、少し切れて、0.3 秒鳴る
            bool on = t < 0.18f || (t > 0.24f && t < 0.52f);
            if (!on) continue;
            float local = t < 0.2f ? t : t - 0.24f;
            float env = Mathf.Min(1f, local * 200f) * Mathf.Exp(-2f * local);
            float s = Soft(Mathf.Sin(2f * Mathf.PI * 110f * t)) + Soft(Mathf.Sin(2f * Mathf.PI * 116f * t));
            data[i] = 0.18f * env * s;
        }
        return Make("Buzz", data);
    }

    // タイルがはまったときの短い音（カチッ）。高めの 2 音をすぐ消える形で重ねる
    public static AudioClip Click()
    {
        var data = new float[(int)(Rate * 0.12f)];
        for (int i = 0; i < data.Length; i++)
        {
            float t = i / (float)Rate;
            float env = Mathf.Exp(-60f * t);
            data[i] = 0.5f * env * (Mathf.Sin(2f * Mathf.PI * 1800f * t) + 0.6f * Mathf.Sin(2f * Mathf.PI * 900f * t));
        }
        return Make("Click", data);
    }

    static float Soft(float x) => (float)System.Math.Tanh(3f * x);   // サイン波を角張らせる（四角い波に近づける）

    static AudioClip Make(string name, float[] data)
    {
        var clip = AudioClip.Create(name, data.Length, 1, Rate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
