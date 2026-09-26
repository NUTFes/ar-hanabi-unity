using System;

// ===== AudioDsp =====
// 効果音を合成するための DSP 部品。
//
// ── なぜ作り直したか ──
//   最初の実装は「白色ノイズ ＋ 1次ローパス3段 ＋ tanh」で作っていた。
//   実測すると次の状態だった（AudioSpectrum で測定）:
//     ・スペクトル平坦度 0.17〜0.97（1 に近いほど白色ノイズそのもの）
//     ・ロールオフが実測 約5dB/oct しかない
//     ・8kHz がピーク比 -24dB しか落ちていない
//   一方、実際の花火の録音を測ると 平坦度 0.05前後 / 8kHz はピーク比 -33dB。
//   つまり「フィルタが緩すぎて白色ノイズがそのまま出ていた」のが
//   ノイズっぽく聞こえた原因だった。
//
//   そこで次の3点を土台から入れ替えた。
//     1. 励振を白色から ピンク(-3dB/oct) / ブラウン(-6dB/oct) に変える
//        自然界の音はほぼ 1/f〜1/f²。白色は定義上「シャー」にしかならない
//     2. フィルタを 1次(6dB/oct) から 2次 biquad(12dB/oct) に変える
//        2段カスケードで 24dB/oct。共振(Q)も付けられる
//     3. 衝撃波は Friedlander 波形（爆風の圧力プロファイルの標準式）で作る
//        ノイズで「パンッ」を作ろうとすると必ずヒスになる
//
// ── Unity に依存させていない ──
//   UnityEngine の型を使っていない（Mathf ではなく System.Math）。
//   Unity の外でコンソールアプリとして走らせ、生成→測定→調整を回せるようにするため。

public static class AudioDsp
{
    // ── 決定論的な乱数（xorshift32）──
    // System.Random はランタイムによって実装が変わりうる。自前にしておけば
    // Unity でも Unity の外でも、同じ seed から必ず同じ波形が出る。
    public struct Rng
    {
        private uint _s;
        public Rng(uint seed) { _s = seed == 0u ? 0x9E3779B9u : seed; }

        public uint NextUInt()
        {
            _s ^= _s << 13;
            _s ^= _s >> 17;
            _s ^= _s << 5;
            return _s;
        }

        /// <summary>0..1</summary>
        public float NextFloat() => (NextUInt() >> 8) / 16777216f;

        /// <summary>-1..1</summary>
        public float NextBipolar() => NextFloat() * 2f - 1f;

        public float Range(float min, float max) => min + (max - min) * NextFloat();
    }

    // ── ピンクノイズ（-3dB/oct）──
    // Paul Kellet の refined IIR。44.1kHz で 9.2Hz 以上 ±0.05dB。
    // 出典: https://www.firstpr.com.au/dsp/pink-noise/
    public struct PinkFilter
    {
        private float _b0, _b1, _b2, _b3, _b4, _b5, _b6;

        public float Process(float white)
        {
            _b0 = 0.99886f * _b0 + white * 0.0555179f;
            _b1 = 0.99332f * _b1 + white * 0.0750759f;
            _b2 = 0.96900f * _b2 + white * 0.1538520f;
            _b3 = 0.86650f * _b3 + white * 0.3104856f;
            _b4 = 0.55000f * _b4 + white * 0.5329522f;
            _b5 = -0.7616f * _b5 - white * 0.0168980f;

            float pink = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + white * 0.5362f;
            _b6 = white * 0.115926f;

            // 素の出力は白色より 3〜4倍大きいので、扱いやすい範囲へ落としておく
            return pink * 0.11f;
        }
    }

    // ── ブラウンノイズ（-6dB/oct）──
    // 白色に 1次ローパスを1段だけ掛ける（漏れ積分器）。これで -6dB/oct になる。
    //
    // 注意: ピンク(-3dB/oct)に 1次ローパス(-6dB/oct)を足すと -9dB/oct になってしまう。
    // 最初はそれをやってしまい、開花音の重心が 1080Hz の目標に対して 60Hz まで落ち、
    // 完全に埋もれた音になった。ブラウンは「白色 ＋ 1次ローパス1段」が正しい。
    //
    // 実測した花火の開花音は 62Hz→16kHz で -46.5dB（8オクターブ）= 約 -5.8dB/oct。
    // つまりブラウンノイズの傾きがそのまま目標になっている。
    // 出力レベルは呼び出し側で NormalizePeak するので、利得補正はしていない。
    public struct BrownFilter
    {
        private float _lp;
        private float _a;
        private bool  _init;

        public float Process(float white, int sampleRate, float cornerHz = 15f)
        {
            if (!_init) { _a = OnePoleCoeff(cornerHz, sampleRate); _init = true; }

            _lp += _a * (white - _lp);
            return _lp;
        }
    }

    /// <summary>1次ローパスの係数。カットオフ fc[Hz] を -3dB 点にする</summary>
    public static float OnePoleCoeff(float cutoffHz, int sampleRate)
    {
        float fc = Clamp(cutoffHz, 1f, sampleRate * 0.45f);
        return 1f - (float)Math.Exp(-2.0 * Math.PI * fc / sampleRate);
    }

    // ── 2次 biquad（RBJ Audio-EQ-Cookbook）──
    // 1段で 12dB/oct。2段カスケードで 24dB/oct。
    // Q は 0.5 以上（0.707 でバターワース = 通過帯域が平坦、それより大きいと共振ピーク）。
    // カットオフを時間で動かす場合は毎サンプル SetLowpass を呼び直してよい
    // （オフライン生成なので係数計算のコストは問題にならない）。
    // 出典: https://www.musicdsp.org/en/latest/Filters/197-rbj-audio-eq-cookbook.html
    public struct Biquad
    {
        private float _b0, _b1, _b2, _a1, _a2;
        private float _x1, _x2, _y1, _y2;

        public void SetLowpass(float freq, float q, int sampleRate)
        {
            float w0    = 2f * (float)Math.PI * Clamp(freq, 1f, sampleRate * 0.45f) / sampleRate;
            float cosW  = (float)Math.Cos(w0);
            float alpha = (float)Math.Sin(w0) / (2f * Math.Max(0.5f, q));

            float a0 = 1f + alpha;
            _b0 = (1f - cosW) * 0.5f / a0;
            _b1 = (1f - cosW)        / a0;
            _b2 = (1f - cosW) * 0.5f / a0;
            _a1 = (-2f * cosW)       / a0;
            _a2 = (1f - alpha)       / a0;
        }

        public void SetHighpass(float freq, float q, int sampleRate)
        {
            float w0    = 2f * (float)Math.PI * Clamp(freq, 1f, sampleRate * 0.45f) / sampleRate;
            float cosW  = (float)Math.Cos(w0);
            float alpha = (float)Math.Sin(w0) / (2f * Math.Max(0.5f, q));

            float a0 = 1f + alpha;
            _b0 =  (1f + cosW) * 0.5f / a0;
            _b1 = -(1f + cosW)        / a0;
            _b2 =  (1f + cosW) * 0.5f / a0;
            _a1 = (-2f * cosW)        / a0;
            _a2 = (1f - alpha)        / a0;
        }

        /// <summary>バンドパス（ピーク利得 0dB 版）。狭い帯域だけ残したいときに使う</summary>
        public void SetBandpass(float freq, float q, int sampleRate)
        {
            float w0    = 2f * (float)Math.PI * Clamp(freq, 1f, sampleRate * 0.45f) / sampleRate;
            float cosW  = (float)Math.Cos(w0);
            float alpha = (float)Math.Sin(w0) / (2f * Math.Max(0.5f, q));

            float a0 = 1f + alpha;
            _b0 =  alpha        / a0;
            _b1 =  0f;
            _b2 = -alpha        / a0;
            _a1 = (-2f * cosW)  / a0;
            _a2 = (1f - alpha)  / a0;
        }

        public float Process(float x)
        {
            float y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x;
            _y2 = _y1; _y1 = y;
            return y;
        }

        /// <summary>係数を保ったまま内部状態だけ消す（粒ごとに使い回すとき用）</summary>
        public void ResetState() { _x1 = _x2 = _y1 = _y2 = 0f; }
    }

    // ── Friedlander 波形（爆風の圧力プロファイル）──
    //   p(t) = (1 - t/t*) * exp(-alpha * t / t*)
    // 急峻な立ち上がり → 指数減衰 → 負圧相。t = t* で 0、t = 2t* で最小（alpha=1 なら -0.135）。
    //
    // ここが「パンッ」の芯になる。ノイズで作ろうとすると必ずヒスになるので、
    // 決定論的な波形で作るのが正しい。
    // さらにこの波形はスペクトルが自然に約 -6dB/oct で落ちるため、
    // 実測した花火の傾き（約 -5〜6dB/oct）とそのまま一致する。
    //
    // 出典: Friedlander (1946) / https://www.sciencedirect.com/topics/engineering/blast-wave
    public static float Friedlander(float t, float tStar, float alpha = 1f)
    {
        if (t < 0f) return 0f;
        float u = t / Math.Max(1e-6f, tStar);
        return (1f - u) * (float)Math.Exp(-alpha * u);
    }

    // ── エンベロープ ──

    /// <summary>指数減衰。tau 秒で 1/e になる</summary>
    public static float ExpDecay(float t, float tau)
        => (float)Math.Exp(-t / Math.Max(1e-6f, tau));

    /// <summary>立ち上がり付きの指数減衰。attack 秒で立ち上がってから減衰する</summary>
    public static float AttackDecay(float t, float attack, float tau)
        => (1f - (float)Math.Exp(-t / Math.Max(1e-6f, attack))) * ExpDecay(t, tau);

    /// <summary>from から to へ対数補間（周波数スイープに使う。音程として自然に動く）</summary>
    public static float LogSweep(float from, float to, float u01)
    {
        float u = Clamp(u01, 0f, 1f);
        return from * (float)Math.Pow(to / Math.Max(1e-6f, from), u);
    }

    public static float SmoothStep(float u01)
    {
        float u = Clamp(u01, 0f, 1f);
        return u * u * (3f - 2f * u);
    }

    // ── バッファ操作 ──

    public static float Peak(float[] buf)
    {
        float p = 0f;
        for (int i = 0; i < buf.Length; i++)
        {
            float a = Math.Abs(buf[i]);
            if (a > p) p = a;
        }
        return p;
    }

    public static void NormalizePeak(float[] buf, float target)
    {
        float p = Peak(buf);
        if (p <= 1e-9f) return;

        float g = target / p;
        for (int i = 0; i < buf.Length; i++) buf[i] *= g;
    }

    public static void AddScaled(float[] dst, float[] src, float gain)
    {
        int n = Math.Min(dst.Length, src.Length);
        for (int i = 0; i < n; i++) dst[i] += src[i] * gain;
    }

    /// <summary>2段の biquad ローパスを通す（24dB/oct）。ヒス帯域を確実に落とすため</summary>
    public static void LowpassInPlace(float[] buf, float freq, float q, int sampleRate, int stages = 2)
    {
        for (int s = 0; s < stages; s++)
        {
            var f = new Biquad();
            f.SetLowpass(freq, q, sampleRate);
            for (int i = 0; i < buf.Length; i++) buf[i] = f.Process(buf[i]);
        }
    }

    // ── ピークリミッタ ──
    // 体感音量を上げるために全体を持ち上げると、ピークだけが天井を超える。
    // tanh の一律ソフトクリップだと波形全体が歪んで高調波が増え、
    // せっかく削った高域が埋め戻されてヒスに戻ってしまう（最初の実装の失敗）。
    // 超えた瞬間だけゲインを下げて、すぐ戻すエンベロープ方式にすれば
    // 歪みを増やさずに音圧だけ稼げる。
    //
    // attack を極短にするとクリック状の歪みが出るので 1ms 程度、
    // release は長すぎるとポンピングするので 60ms 程度が無難。
    public static void LimitInPlace(float[] buf, float ceiling,
                                    int sampleRate,
                                    float attackSec = 0.001f,
                                    float releaseSec = 0.06f)
    {
        float aAtk = OnePoleCoeff(1f / Math.Max(1e-4f, attackSec),  sampleRate);
        float aRel = OnePoleCoeff(1f / Math.Max(1e-4f, releaseSec), sampleRate);

        float env = 0f;   // 追従している振幅
        for (int i = 0; i < buf.Length; i++)
        {
            float a = Math.Abs(buf[i]);
            // 上がるときは速く、下がるときはゆっくり
            env += (a > env ? aAtk : aRel) * (a - env);

            float gain = env > ceiling ? ceiling / env : 1f;
            buf[i] *= gain;
        }
    }

    /// <summary>
    /// A特性 RMS を目標値に合わせてから、リミッタで天井を守る。
    ///
    /// ピーク正規化だけでは体感音量が揃わない。低域だけの音は、同じピークでも
    /// A特性 RMS が 20dB 以上低く出る（人の耳が低域に鈍いため）。
    /// 実測では合成の開花音が実録音より 9.3dB 体感が小さかった。
    /// </summary>
    public static void NormalizeLoudness(float[] buf, float targetAWeightedRms,
                                         float ceiling, int sampleRate)
    {
        // 1回測ってゲインを掛けるだけでは目標に届かない。
        // そのあとリミッタと天井クランプが働いて音量が下がるので、
        // 「掛ける → 制限する → 測る」を繰り返して収束させる。
        // 実測では開ループだと目標 0.033 に対して 0.025（-2.4dB）で止まっていた。
        var src = (float[])buf.Clone();
        float gain = 1f;

        // 上げ幅の上限。これを超えるとリミッタが潰しすぎて音が死ぬ
        const float maxGainDb = 18f;
        float maxGain = (float)Math.Pow(10.0, maxGainDb / 20.0);

        for (int iter = 0; iter < 6; iter++)
        {
            Array.Copy(src, buf, buf.Length);
            for (int i = 0; i < buf.Length; i++) buf[i] *= gain;

            LimitInPlace(buf, ceiling, sampleRate);

            // リミッタは 1ms のエンベロープで追従するので、それより速い立ち上がり
            // （衝撃波は 3ms 弱）は取りこぼして天井を超える。実測で 0dBFS に張り付き
            // クリップしていたので、必ず天井まで落とす。
            float p = Peak(buf);
            if (p > ceiling)
            {
                float g2 = ceiling / p;
                for (int i = 0; i < buf.Length; i++) buf[i] *= g2;
            }

            var m = AudioSpectrum.Analyze(buf, sampleRate);
            if (m.aWeightedRms <= 1e-9f) return;

            float err = targetAWeightedRms / m.aWeightedRms;
            if (Math.Abs(20.0 * Math.Log10(err)) < 0.1) break;   // 0.1dB 以内で終了

            gain *= err;
            if (gain > maxGain) { gain = maxGain; }
        }

        // ここで「天井まで上げ直す」ことはしない。
        // ピークに余裕がある素材（クラックルはクレストが 15dB 以上ある）を
        // 天井まで持ち上げると、目標より 10dB も大きくなって音量合わせが壊れる。
        // 体感音量を揃えるのが目的なので、ピークの余りは残したままでよい。
    }

    // ── ゆっくり揺れるノイズ（乱流の表現）──
    // 爆発の胴体は指数減衰の滑らかなカーブではなく、実際には乱流で不規則に揺れる。
    // 帯域を絞ったノイズを振幅に掛けると、その「生っぽさ」が出る。
    public struct SlowNoise
    {
        private float _lp1, _lp2, _a;
        private bool  _init;

        /// <summary>0..1 付近を揺れる値を返す。rateHz を上げると細かく揺れる</summary>
        public float Process(ref Rng rng, int sampleRate, float rateHz)
        {
            if (!_init) { _a = OnePoleCoeff(rateHz, sampleRate); _init = true; }

            float x = rng.NextBipolar();
            _lp1 += _a * (x - _lp1);
            _lp2 += _a * (_lp1 - _lp2);

            // 2段ローパスで大きく減衰するので戻す。範囲は概ね -1..1
            return Clamp(_lp2 * 14f, -1f, 1f);
        }
    }

    // ── 短い残響 ──
    // 乾いた合成ノイズは「作り物」に聞こえる。屋外の反射を少し足すと馴染む。
    // Schroeder 型（並列コムフィルタ ＋ 直列オールパス）の最小構成。
    public static void ReverbInPlace(float[] buf, int sampleRate,
                                     float mix, float roomSec = 0.9f, float damp = 0.35f)
    {
        if (mix <= 0f) return;

        // 互いに素に近い遅延にして、金属的な癖が出ないようにする
        int[] combMs   = { 29, 37, 41, 47 };
        int[] allpassMs = { 5, 12 };

        var wet = new float[buf.Length];

        foreach (int ms in combMs)
        {
            int   d    = Math.Max(1, ms * sampleRate / 1000);
            float fb   = (float)Math.Pow(0.001, d / (double)(roomSec * sampleRate));
            var   line = new float[d];
            int   idx  = 0;
            float lp   = 0f;

            for (int i = 0; i < buf.Length; i++)
            {
                float y = line[idx];
                // 反射のたびに高域が減る（空気と壁の吸収）
                lp += (1f - damp) * (y - lp);
                line[idx] = buf[i] + lp * fb;
                idx = (idx + 1) % d;
                wet[i] += y * 0.25f;
            }
        }

        foreach (int ms in allpassMs)
        {
            int d = Math.Max(1, ms * sampleRate / 1000);
            var line = new float[d];
            int idx = 0;
            const float g = 0.5f;

            for (int i = 0; i < buf.Length; i++)
            {
                float bufd = line[idx];
                float y    = -g * wet[i] + bufd;
                line[idx]  = wet[i] + g * y;
                idx = (idx + 1) % d;
                wet[i] = y;
            }
        }

        float m = Clamp(mix, 0f, 1f);
        for (int i = 0; i < buf.Length; i++) buf[i] = buf[i] * (1f - m * 0.3f) + wet[i] * m;
    }

    /// <summary>端のクリック除去。数ミリ秒だけ直線でフェードする</summary>
    public static void ApplyEdgeFades(float[] buf, float seconds, int sampleRate)
    {
        int fade = Math.Min((int)(seconds * sampleRate), buf.Length / 2);
        if (fade <= 0) return;

        for (int i = 0; i < fade; i++)
        {
            float g = i / (float)fade;
            buf[i]                  *= g;
            buf[buf.Length - 1 - i] *= g;
        }
    }

    public static float Clamp(float v, float min, float max)
        => v < min ? min : (v > max ? max : v);
}
