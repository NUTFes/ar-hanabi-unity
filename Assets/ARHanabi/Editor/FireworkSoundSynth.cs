using System;
using System.Collections.Generic;

// ===== FireworkSoundSynth =====
// 花火の効果音をコードで合成し、16bit PCM の WAV バイト列にする。
//
// ── なぜ合成でやるのか ──
//   権利上の必要に迫られてではない。効果音ラボの FAQ は「アプリの演出音として
//   組み込むこと」を再配布とみなさず、音源ファイルがむき出しでも可としているので、
//   実素材をそのままアセットにしても規約上は問題ない。
//   合成を選ぶ利点は次の3点:
//     ・素材ごとの規約を追わなくて済む（第三者の素材への依存が無い）
//     ・数値を変えるだけで無限にバリエーションが作れる。展示で同じ音の反復を避けられる
//     ・実録音から採れない音を作れる（手元の素材にはクラックルが入っていなかった）
//   逆に、実録音のほうが質感は上。用途ごとに使い分けるのが素直。
//
// ── 実測に基づいて土台から作り直した ──
//   最初の実装（白色ノイズ ＋ 1次ローパス3段 ＋ tanh）を AudioSpectrum で測ると
//   スペクトル平坦度が 0.17〜0.97 になっていた。1 に近いほど白色ノイズそのもので、
//   つまり「フィルタが緩すぎてノイズが素通りしていた」。
//
//   実際の花火の録音を測った値（これを目標にする）:
//     開花     平坦度 0.054 / 重心 1080Hz / 2kHz以上 0.30% / 500Hz以下 98.1%
//              62Hz にピークがあり、そこから約 -5〜6dB/oct で落ちる
//     打ち上げ 平坦度 0.049 / 重心 3084Hz / 2kHz以上 99.9%
//              2〜4kHz の狭帯域にほぼ全部のエネルギー。1kHz以下は -68dB 以下。
//              しかもピッチは 3.5k → 2.6kHz へ「下降」する
//
//   ここから分かった、最初の実装の具体的な誤り:
//     1. 励振が白色ノイズだった            → ピンク/ブラウンに変更（AudioDsp）
//     2. フィルタが1次(6dB/oct)だった      → 2次biquadの2段(24dB/oct)に変更
//     3. tanh で音圧を稼ごうとしていた      → 高調波が高域を埋め戻すだけなので撤去
//     4. 打ち上げに広帯域の「息」を足していた → 実物にはほぼ無い。狭帯域トーンに変更
//     5. 打ち上げのスイープが上昇だった      → 実物は下降。逆にした
//     6. クラックルの粒の帯域幅が間違っていた → 効果音ラボの 爆竹.mp3 / 手持ち花火.mp3 を
//        トランジェント解析して正解が判明した。実物の粒は帯域幅 約4700Hz（中心6783Hz、
//        つまり Q≒1.4 のごく緩いバンドパスノイズ）。
//        白色ノイズの粒（帯域幅 約12000Hz）は広すぎて「サーッ」＝静電気、
//        減衰正弦の粒（帯域幅 約100Hz）は狭すぎて「ポーン」＝電子音になる。
//        平坦度だけを見て音程を付けに行ったのが電子音化の原因だった。
//
// ── 乱数を自前で持っている理由 ──
//   AudioDsp.Rng（xorshift32）を使う。ランタイムに依らず同じ seed から同じ波形が出るので、
//   再生成しても音が変わらない。

public static class FireworkSoundSynth
{
    public const int SampleRate = 44100;

    public enum SoundKind { Launch, Burst, Crackle }

    public sealed class Recipe
    {
        public string    name     = "sound";
        public SoundKind kind     = SoundKind.Burst;
        public uint      seed     = 12345u;
        public float     duration = 1f;

        /// <summary>書き出し時のピークの天井。0.89 ≒ -1dBFS</summary>
        public float peak = 0.89f;

        /// <summary>
        /// 体感音量の目標（A特性 RMS）。0 ならピーク正規化のみ。
        ///
        /// ピークだけ揃えても体感音量は揃わない。低域中心の音は、同じピークでも
        /// A特性 RMS が 20dB 以上低く出る（人の耳が低域に鈍いため）。
        /// 実測では合成の開花音がピーク同一で実録音より 9.3dB 小さく、
        /// さらにセット内で打ち上げとの差が 24dB もあった。
        /// 実録音の開花は 0.033（-29.6dBFS）。これを基準にしている。
        /// </summary>
        public float loudnessTarget = 0.033f;

        /// <summary>残響の混ぜ量。0 で無効。乾いた合成音は作り物に聞こえる</summary>
        public float reverbMix = 0.16f;
        public float reverbRoomSec = 0.9f;
        public float reverbDamp = 0.4f;

        // ══ 開花（ドーン）══
        // 層構成: 衝撃波(Friedlander) / 低域の突き / 胴体 / ゴロゴロ
        // 各層はピークを 1 に揃えてから gain で混ぜるので、gain がそのまま比率になる。

        /// <summary>衝撃波の時定数[秒]。小さいほど鋭い。3ms 前後で「パンッ」になる</summary>
        public float shockTStar   = 0.003f;
        public float shockGain    = 0.68f;

        /// <summary>低域の突き。下降スイープさせると「ドン」の芯になる</summary>
        public float thumpFreqFrom = 110f;
        public float thumpFreqTo   = 45f;
        public float thumpSweepSec = 0.25f;
        public float thumpTau      = 0.28f;
        public float thumpGain     = 1.0f;
        /// <summary>
        /// 低域に足す倍音の量。純正弦のサブは小型スピーカーで基音が再生できず
        /// 何も聞こえなくなる。倍音を少し混ぜると基音が推定されて「ドン」が伝わる。
        /// </summary>
        public float thumpHarmonic = 0.35f;

        /// <summary>
        /// 破片の弾ける成分（2〜6kHz の短いピンクノイズ）。
        /// これが無いと開花音が「壁越しの鈍い音」になり、輪郭も体感音量も出ない。
        /// A特性は 3kHz 付近の感度が最も高いので、ここを少し足すだけで体感が大きく上がる。
        /// 短く切るのが要点。長く残すとヒスになる。
        /// </summary>
        public float crackLow  = 2200f;
        public float crackHigh = 6000f;
        public float crackTau  = 0.07f;
        public float crackGain = 0.42f;

        /// <summary>
        /// 胴体の振幅を揺らす量（乱流）。0 だと滑らかな指数減衰そのままで
        /// 「合成した音」の平板さが出る。実際の爆発は不規則に揺れる。
        /// </summary>
        public float turbulence     = 0.45f;
        public float turbulenceRate = 14f;

        /// <summary>
        /// 胴体。ブラウンノイズ（-6dB/oct）が既に目標の傾きなので、
        /// ローパスは 1段（12dB/oct）だけ、しかも高めから緩やかに下げる。
        /// ここを 24dB/oct で深く削ると高域が崖のように消えて埋もれた音になる。
        /// </summary>
        public float bodyCutFrom = 4500f;
        public float bodyCutTo   = 2200f;
        public float bodyCutSec  = 0.5f;
        public float bodyQ       = 0.7f;
        public int   bodyStages  = 1;
        public float bodyTau     = 0.45f;
        public float bodyGain    = 0.8f;

        /// <summary>
        /// 長く残るゴロゴロ。カットオフを低くしすぎないこと。
        /// ブラウンノイズは -6dB/oct なので、それ自体が既に「低音の塊」になっている。
        /// ここを 150Hz などにすると合計 -18dB/oct になって高域が消え、
        /// 重心が目標 1080Hz に対して 245Hz まで落ちてしまった。
        /// 実録音は 16kHz まで -65dBFS の裾を保っており、その裾が重心を押し上げている。
        /// </summary>
        public float rumbleCut  = 2000f;
        public float rumbleQ    = 0.7f;
        public float rumbleTau  = 1.3f;
        public float rumbleGain = 0.45f;

        /// <summary>
        /// 混ぜた後に掛ける最終ローパス。
        /// 実録音のスペクトルは 125Hz から 16kHz まで knee の無い滑らかな
        /// -6dB/oct なので、ここで急峻に切ると崖ができて不自然になる。
        /// 既定は 0段（無効）。超高域だけ整えたいときに 1段入れる程度に留める。
        /// </summary>
        public float finalCut    = 9000f;
        public float finalQ      = 0.7f;
        public int   finalStages = 0;

        // ══ 打ち上げ（ヒュ〜）══
        // 実物は 2〜4kHz の狭帯域トーンが下降するだけ。広帯域ノイズは足さない。
        public float launchFreqFrom  = 3600f;
        public float launchFreqTo    = 2400f;
        public float launchVibHz     = 5.5f;
        public float launchVibCent   = 40f;
        /// <summary>倍音の量。0 で純音</summary>
        public float launchHarmonic  = 0.12f;
        /// <summary>息の量。実物にはほぼ無いので既定はごく少量。高Qバンドパス通し</summary>
        public float launchAirGain   = 0.05f;
        public float launchAirQ      = 6f;
        /// <summary>後半の減衰の鋭さ。遠ざかっていく分</summary>
        public float launchFadeCurve = 1.3f;
        /// <summary>
        /// 不規則なピッチのゆらぎ（セント）。周期的なビブラートだけだと
        /// テルミンや試験信号のように機械的に聞こえる。実物の笛は揺れる。
        /// </summary>
        public float launchDriftCent = 90f;
        /// <summary>振幅の不規則なゆらぎ量</summary>
        public float launchFlutter   = 0.18f;

        // ══ パチパチ / バチバチ（クラックル）══
        //
        // ── 実測して分かった正解（爆竹.mp3 と 手持ち花火.mp3 をトランジェント解析）──
        //   爆竹       粒の重心 6783Hz / 粒の帯域幅 4700Hz / 減衰(-20dB) 105ms / 25.6粒/秒
        //   手持ち花火 粒の重心 9185Hz / 粒の帯域幅 4900Hz / 減衰(-20dB) 14〜250ms / 17〜77粒/秒
        //
        //   決め手は「粒の帯域幅」だった。
        //     白色ノイズの粒  … 帯域幅 約12000Hz → 広すぎて「サーッ」（静電気）
        //     減衰正弦の粒    … 帯域幅 約100Hz  → 狭すぎて「ポーン」（電子音・オルゴール）
        //     実物            … 帯域幅 約4700Hz → その中間
        //   中心 6783Hz / 帯域幅 4700Hz は Q = 6783/4700 ≒ 1.4。
        //   つまり「ごく緩いバンドパスを通したノイズ」が正解で、音程は付けない。
        //   以前は平坦度だけを見て音程を付けに行ったが、それが電子音の原因だった。
        //
        //   減衰も実測より2桁短かった。-20dB 到達 105ms は指数の時定数 tau ≒ 45ms
        //   （t = tau·ln10）。以前の tau 2〜8ms では「プツプツ」というクリックになる。
        //
        //   間隔の分布は Poisson より裾が重い（90%点/中央値 = 7.1、Poisson なら 3.3）。
        //   つまり一定確率でばらけているのではなく、房状に固まって鳴っている。
        //   そこで「房の開始時刻をまず決めて、その中で数発まとめて鳴らす」形にした。

        /// <summary>鳴らす粒の総数</summary>
        public int   popCount        = 110;

        /// <summary>
        /// 粒ごとの音色のばらつき（0 = 全粒同じ、1 = 大きくばらつく）。
        ///
        /// これが足りないと「スタンガン（電気アーク）」の音になる。
        /// 電気アークの特徴は
        ///   ①全ての粒が同じ音色 ②間隔が規則的 ③密度が高く融合する ④空間がなく乾いている
        /// で、中心周波数と減衰だけをランダムにしても ① が残る。
        /// 実物の火薬は1粒ごとに Q・頭の鋭さ・胴の量・芯の量がすべて違う。
        /// ここを振ると「同じ音の連射」から「個々に違う破裂の集まり」に変わる。
        /// </summary>
        public float popVariation    = 0.65f;

        /// <summary>粒の中心周波数の範囲。爆竹なら 5〜9kHz、手持ち花火なら 7〜12kHz</summary>
        public float popFreqMin      = 5000f;
        public float popFreqMax      = 9000f;

        /// <summary>
        /// 粒のバンドパスの Q。実測の 中心6783Hz / 帯域幅4700Hz から Q≒1.4。
        /// 上げると音程が付いて電子音に寄り、下げると白色ノイズに寄る。
        /// </summary>
        public float popQ            = 1.4f;

        /// <summary>粒の「尾」の時定数[秒]。実測の -20dB 到達 105ms に対応する</summary>
        public float popTauMin       = 0.010f;
        public float popTauMax       = 0.045f;

        /// <summary>
        /// 粒の「頭」の時定数[秒]。ここが「弾ける」の正体。
        ///
        /// 実測すると、爆竹の粒は -6dB まで 0.7ms で落ちるのに -20dB までは 105ms かかる。
        /// 比は 107 で、単一の指数減衰なら 3.33 にしかならない。
        /// つまり実物は「鋭いスパイク ＋ 低く長い尾」の2段構造。
        /// 単一の指数減衰だと減衰が均等になり、弾けずにボテッとした音になる
        /// （合成の実測は -6dB が 1.2ms、比が 61 だった）。
        /// </summary>
        public float popSpikeTau     = 0.0009f;

        /// <summary>
        /// 尾の量。小さいほど頭が際立って弾ける。大きいと余韻が勝ってボテつく。
        /// </summary>
        public float popTailGain     = 0.24f;

        /// <summary>
        /// 粒の「胴」= 破裂感を作る中域成分。
        ///
        /// 高域のバンドパス（popFreqMin〜Max）と低域の衝撃波だけで組むと、
        /// その間の 600〜1600Hz に穴が空く。実測と比べると 1kHz が 7.5dB 不足していて、
        /// そこが抜けると「破裂」ではなく軽い「クリック」に聞こえてしまう。
        /// 実録音の爆竹は 500Hz〜8kHz が平坦なので、この帯域を独立に足して埋める。
        ///
        /// 衝撃波（Friedlander）を上げて埋めようとすると -6dB/oct なので
        /// 低域ばかり増えて全体が暗くなる（実際にそれをやって 2kHz以上が
        /// 69.7% の目標に対し 26.7% まで落ちた）。帯域を絞って足すのが正しい。
        /// </summary>
        public float popBodyLow      = 600f;
        public float popBodyHigh     = 2300f;
        public float popBodyQ        = 1.0f;
        /// <summary>
        /// 胴の減衰の時定数[秒]。短く保つのが要点。
        /// 13ms にすると 1kHz が持続音になり、頭と尾のコントラスト（減衰比）が
        /// 126 → 55 まで落ちて弾けなくなった。実物では 1kHz 成分もトランジェントの
        /// 一部として速く減衰している。
        /// </summary>
        public float popBodyTau      = 0.005f;
        public float popBodyAttack   = 0.0003f;
        public float popBodyGain     = 0.85f;

        /// <summary>粒の立ち上がり時間[秒]。実物のトランジェントは非常に鋭い。
        /// ここを長くすると頭が丸まって弾けなくなる</summary>
        public float popAttack       = 0.00008f;

        /// <summary>1より大きいほど房が前半に密集する（時間経過で疎になる）。
        /// 爆竹の実測は 前半38粒 / 後半5粒 と強く減衰している</summary>
        public float popDensityCurve = 2.6f;

        /// <summary>1つの房に入る粒数の範囲。房状に固まらせるための構造</summary>
        public int   popClusterMin   = 2;
        public int   popClusterMax   = 7;

        /// <summary>房の中での粒の間隔[秒]。実測の間隔の 10%点 2.5ms 付近に合わせる</summary>
        public float popInClusterMin = 0.002f;
        public float popInClusterMax = 0.011f;

        /// <summary>
        /// 粒に混ぜる正弦の量。既定は 0。
        /// 実物の粒は帯域幅 4700Hz で音程感がほぼ無いので、足すと電子音に寄る。
        /// </summary>
        public float popToneGain     = 0f;

        /// <summary>
        /// 粒の頭に混ぜる衝撃波（Friedlander）の量。
        ///
        /// 爆竹は1粒が小さな「爆発」なので、実測では 125Hz が -55.6dBFS もある。
        /// バンドパスノイズだけで作ると同じ帯域が -82dB まで落ちて低域の芯が消え、
        /// 「シャカシャカ」した軽い音になってしまった（実測で 25dB 不足）。
        /// Friedlander 波形は約 -6dB/oct なので、これを少し混ぜると
        /// 100Hz〜2kHz が自然に埋まる。
        /// </summary>
        public float popClickGain    = 0.38f;
        /// <summary>粒の衝撃波の時定数[秒]。大きいほど低域寄りになる</summary>
        public float popClickTStar   = 0.0016f;

        // ══ こだま・残響（null なら無効）══
        public float[] echoDelays = null;   // 秒
        public float[] echoGains  = null;   // 元音のピークに対する比
        /// <summary>反射1回目のカットオフ。2回目以降は 1/2, 1/3 … と下がる</summary>
        public float   echoCutoff = 1200f;
    }

    // ── レンダリング ──

    public static float[] Render(Recipe r)
    {
        int n = Math.Max(1, (int)(r.duration * SampleRate));
        var rng = new AudioDsp.Rng(r.seed);

        float[] buf;
        switch (r.kind)
        {
            case SoundKind.Launch:  buf = RenderLaunch (r, n, ref rng); break;
            case SoundKind.Crackle: buf = RenderCrackle(r, n, ref rng); break;
            default:                buf = RenderBurst  (r, n, ref rng); break;
        }

        if (r.echoDelays != null && r.echoDelays.Length > 0)
            buf = ApplyEcho(buf, r);

        // 残響。乾いた合成音は作り物に聞こえるので、屋外の反射を薄く足す
        if (r.reverbMix > 0f)
            AudioDsp.ReverbInPlace(buf, SampleRate, r.reverbMix, r.reverbRoomSec, r.reverbDamp);

        AudioDsp.ApplyEdgeFades(buf, 0.004f, SampleRate);

        // ピークではなく体感音量（A特性 RMS）で揃える。
        // ピーク正規化だけだと、暗い音（開花）が明るい音（笛）より
        // 20dB 以上小さく聞こえてしまう
        if (r.loudnessTarget > 0f)
            AudioDsp.NormalizeLoudness(buf, r.loudnessTarget, r.peak, SampleRate);
        else
            AudioDsp.NormalizePeak(buf, r.peak);

        return buf;
    }

    // ── 開花音 ──
    private static float[] RenderBurst(Recipe r, int n, ref AudioDsp.Rng rng)
    {
        var shock  = new float[n];
        var thump  = new float[n];
        var body   = new float[n];
        var rumble = new float[n];

        // 衝撃波。1発の Friedlander パルス。
        // この波形はスペクトルが自然に約 -6dB/oct で落ちるので、
        // 「鋭い立ち上がり」と「実測に合う傾き」が同時に手に入る
        for (int i = 0; i < n; i++)
            shock[i] = AudioDsp.Friedlander(i / (float)SampleRate, r.shockTStar);

        // 低域の突き。ピッチを下げながら減衰させる。
        // 倍音を混ぜるのは、純正弦だと小型スピーカーで基音が出ず何も聞こえないため
        double phase = 0.0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float f = AudioDsp.LogSweep(r.thumpFreqFrom, r.thumpFreqTo, t / r.thumpSweepSec);
            phase += 2.0 * Math.PI * f / SampleRate;

            float s = (float)Math.Sin(phase)
                    + (float)Math.Sin(phase * 2.0) * r.thumpHarmonic
                    + (float)Math.Sin(phase * 3.0) * r.thumpHarmonic * 0.4f;

            thump[i] = s * AudioDsp.ExpDecay(t, r.thumpTau);
        }

        // 胴体。ブラウンノイズ（-6dB/oct）が既に目標の傾きなので、
        // ローパスは緩めに1段だけ。カットオフを時間で下げるのは空気の吸収で
        // 丸くなっていく分を表すため
        var brown = new AudioDsp.BrownFilter();
        var lps   = new AudioDsp.Biquad[Math.Max(1, r.bodyStages)];
        var turb  = new AudioDsp.SlowNoise();

        for (int i = 0; i < n; i++)
        {
            float t   = i / (float)SampleRate;
            float cut = AudioDsp.LogSweep(r.bodyCutFrom, r.bodyCutTo, t / r.bodyCutSec);

            float x = brown.Process(rng.NextBipolar(), SampleRate);
            for (int s = 0; s < lps.Length; s++)
            {
                lps[s].SetLowpass(cut, r.bodyQ, SampleRate);
                x = lps[s].Process(x);
            }

            // 乱流。振幅をゆっくり不規則に揺らして平板さを消す
            float wobble = 1f + r.turbulence * turb.Process(ref rng, SampleRate, r.turbulenceRate);
            body[i] = x * AudioDsp.ExpDecay(t, r.bodyTau) * Math.Max(0f, wobble);
        }

        // ゴロゴロ。もっと低い帯域だけを長く残す
        var brown2 = new AudioDsp.BrownFilter();
        var rl     = new AudioDsp.Biquad();
        rl.SetLowpass(r.rumbleCut, r.rumbleQ, SampleRate);

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float x = brown2.Process(rng.NextBipolar(), SampleRate);
            rumble[i] = rl.Process(x) * AudioDsp.ExpDecay(t, r.rumbleTau);
        }

        // 破片の弾ける成分。2〜6kHz のピンクノイズを短く切る。
        // これが無いと開花音が壁越しの鈍い音になり、輪郭も体感音量も出ない
        var crack = new float[n];
        var pink  = new AudioDsp.PinkFilter();
        var chp   = new AudioDsp.Biquad();
        var clp   = new AudioDsp.Biquad();
        chp.SetHighpass(r.crackLow,  0.7f, SampleRate);
        clp.SetLowpass (r.crackHigh, 0.7f, SampleRate);

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float x = pink.Process(rng.NextBipolar());
            x = clp.Process(chp.Process(x));
            // 立ち上がりを一瞬つけて、頭のクリックを避ける
            crack[i] = x * AudioDsp.AttackDecay(t, 0.0008f, r.crackTau);
        }

        // 各層のピークを 1 に揃えてから混ぜる。
        // フィルタを通すと層の音量が読めなくなるので、混ぜる前に揃えてしまう。
        // こうすると gain がそのまま「聞こえる比率」になり調整が予測可能になる
        AudioDsp.NormalizePeak(shock,  1f);
        AudioDsp.NormalizePeak(thump,  1f);
        AudioDsp.NormalizePeak(body,   1f);
        AudioDsp.NormalizePeak(rumble, 1f);
        AudioDsp.NormalizePeak(crack,  1f);

        var buf = new float[n];
        AudioDsp.AddScaled(buf, shock,  r.shockGain);
        AudioDsp.AddScaled(buf, thump,  r.thumpGain);
        AudioDsp.AddScaled(buf, body,   r.bodyGain);
        AudioDsp.AddScaled(buf, rumble, r.rumbleGain);
        AudioDsp.AddScaled(buf, crack,  r.crackGain);

        // 最終ローパス。ここで 2kHz 以上を削り切る。
        // tanh のソフトクリップは使わない（高調波が高域を埋め戻してヒスになるため）
        AudioDsp.LowpassInPlace(buf, r.finalCut, r.finalQ, SampleRate, r.finalStages);

        return buf;
    }

    // ── 打ち上げ音 ──
    // 実物は 2〜4kHz の狭帯域トーンが下降するだけ。ここに広帯域ノイズを足すと
    // それだけで平坦度が 0.05 → 0.2 に悪化して「シャー」が混ざる。
    private static float[] RenderLaunch(Recipe r, int n, ref AudioDsp.Rng rng)
    {
        var tone = new float[n];
        var air  = new float[n];

        double phase = 0.0;
        var    bp    = new AudioDsp.Biquad();
        var    drift = new AudioDsp.SlowNoise();
        var    flutt = new AudioDsp.SlowNoise();

        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float u = t / r.duration;

            // 下降スイープ＋ビブラート。
            // さらに不規則なピッチのゆらぎを足す。周期的なビブラートだけだと
            // テルミンや試験信号のように機械的に聞こえてしまう
            float baseFreq = AudioDsp.LogSweep(r.launchFreqFrom, r.launchFreqTo, u);
            float vib      = (float)Math.Sin(2.0 * Math.PI * r.launchVibHz * t);
            float wander   = drift.Process(ref rng, SampleRate, 3.5f) * r.launchDriftCent;
            float freq     = baseFreq
                           * (float)Math.Pow(2.0, (vib * r.launchVibCent + wander) / 1200.0);

            phase += 2.0 * Math.PI * freq / SampleRate;

            float s = (float)Math.Sin(phase)
                    + (float)Math.Sin(phase * 2.0) * r.launchHarmonic;

            // 立ち上がりは速く、後半は遠ざかって落ちる。
            // 振幅も不規則に揺らす（実物の笛は一定では鳴らない）
            float env = (1f - (float)Math.Exp(-t / 0.06f))
                      * (float)Math.Pow(Math.Max(0.0, 1.0 - u), r.launchFadeCurve);
            env *= 1f + r.launchFlutter * flutt.Process(ref rng, SampleRate, 6f);
            if (env < 0f) env = 0f;

            tone[i] = s * env;

            // 息はごく少量。しかも高Qバンドパスでトーンの帯域に閉じ込める。
            // 広帯域のまま足すと平坦度が一気に悪化する
            bp.SetBandpass(freq, r.launchAirQ, SampleRate);
            air[i] = bp.Process(rng.NextBipolar()) * env;
        }

        AudioDsp.NormalizePeak(tone, 1f);
        AudioDsp.NormalizePeak(air,  1f);

        var buf = new float[n];
        AudioDsp.AddScaled(buf, tone, 1f);
        AudioDsp.AddScaled(buf, air,  r.launchAirGain);
        return buf;
    }

    // ── パチパチ / バチバチ ──
    //
    // 1粒 = 「Q≒1.4 のごく緩いバンドパスを通したノイズ」＋鋭い立ち上がり＋指数減衰。
    // 爆竹と手持ち花火をトランジェント解析して得た値に合わせている（詳細は Recipe のコメント）。
    //
    // 過去2回の失敗の理由が、粒の帯域幅を測って初めて分かった:
    //   1回目 白色ノイズの粒（帯域幅 約12000Hz）→ 広すぎて「サーッ」＝静電気
    //   2回目 減衰正弦の粒（帯域幅 約100Hz）    → 狭すぎて「ポーン」＝電子音
    //   実物  帯域幅 約4700Hz                  → その中間。音程は付けない
    //
    // 発生時刻は「房（クラスタ）」単位で決める。実測の間隔分布が Poisson より
    // 裾が重かった（90%点/中央値 = 7.1 に対し Poisson なら 3.3）ため、
    // 一定確率でばらけさせるだけでは房状の粗密が再現できない。
    private static float[] RenderCrackle(Recipe r, int n, ref AudioDsp.Rng rng)
    {
        var buf  = new float[n];
        int want = Math.Max(1, r.popCount);

        int emitted = 0;
        int guard   = 0;   // 房が画面外に落ちても無限ループしないための保険

        while (emitted < want && guard++ < want * 8)
        {
            // 房の開始時刻。pow(uniform, curve) で前半に寄せる
            float clusterAt = (float)Math.Pow(rng.NextFloat(), r.popDensityCurve) * r.duration;

            int inCluster = (int)rng.Range(r.popClusterMin, r.popClusterMax + 0.999f);
            if (inCluster < 1) inCluster = 1;

            float at = clusterAt;
            for (int c = 0; c < inCluster && emitted < want; c++)
            {
                EmitPop(buf, n, at, r, ref rng);
                emitted++;
                at += rng.Range(r.popInClusterMin, r.popInClusterMax);
            }
        }

        return buf;
    }

    // 1粒を書き込む
    private static void EmitPop(float[] buf, int n, float atSec, Recipe r, ref AudioDsp.Rng rng)
    {
        int start = (int)(atSec * SampleRate);
        if (start < 0 || start >= n) return;

        float freq = rng.Range(r.popFreqMin, r.popFreqMax);
        float tau  = rng.Range(r.popTauMin,  r.popTauMax);
        // 粒ごとの音量差。実測の爆竹は 最大/最小 が 4.8倍あり、
        // 差が大きいほど「たまに大きく弾ける」感じが出る
        float amp  = rng.Range(0.18f, 1f);

        // ── 粒ごとに音色そのものを振る ──
        // ここを振らないと全粒が同じ鳴りになり、電気アーク（スタンガン）に聞こえる。
        // 実物の火薬は1粒ごとに Q も頭の鋭さも胴の量も違う。
        float v      = AudioDsp.Clamp(r.popVariation, 0f, 1f);
        float q      = r.popQ         * rng.Range(1f - 0.45f * v, 1f + 0.80f * v);
        float spike  = r.popSpikeTau  * rng.Range(1f - 0.50f * v, 1f + 1.40f * v);
        float tailG  = r.popTailGain  * rng.Range(1f - 0.65f * v, 1f + 0.90f * v);
        float bodyG  = r.popBodyGain  * rng.Range(1f - 0.85f * v, 1f + 0.55f * v);
        float clickG = r.popClickGain * rng.Range(1f - 0.90f * v, 1f + 0.90f * v);

        // 減衰が聞こえなくなるまで（tau の6倍 ≒ -52dB）
        int len = Math.Min(n - start, Math.Max(8, (int)(tau * 6f * SampleRate)));

        // 粒ごとに新しいフィルタを使う。状態を持ち越すと前の粒を引きずる
        var bp = new AudioDsp.Biquad();
        bp.SetBandpass(freq, q, SampleRate);

        // 胴（破裂感）の帯域。高域のバンドパスと衝撃波の間に空く
        // 600〜1700Hz の穴を埋める
        var  bodyBp   = new AudioDsp.Biquad();
        float bodyFreq = rng.Range(r.popBodyLow, r.popBodyHigh);
        bodyBp.SetBandpass(bodyFreq, r.popBodyQ, SampleRate);

        double phase = rng.NextFloat() * 2.0 * Math.PI;
        double step  = 2.0 * Math.PI * freq / SampleRate;

        for (int j = 0; j < len; j++)
        {
            float lt = j / (float)SampleRate;

            // 2段エンベロープ = 鋭い頭 ＋ 低く長い尾。
            // 単一の指数減衰にすると減衰が均等になり弾けない（実測比較で判明。
            // 実物は -6dB まで 0.7ms なのに -20dB までは 105ms かかる）。
            float e = AudioDsp.AttackDecay(lt, r.popAttack, spike)
                    + AudioDsp.AttackDecay(lt, r.popAttack, tau) * tailG;

            float s = bp.Process(rng.NextBipolar()) * e;

            // 音程成分は既定で 0。実物の粒には音程感がほぼ無い
            if (r.popToneGain > 0f)
                s += (float)Math.Sin(phase + step * j) * e * r.popToneGain;

            // 胴。破裂の「圧」を作る。頭より少し長く鳴らす
            if (bodyG > 0f)
            {
                float be = AudioDsp.AttackDecay(lt, r.popBodyAttack, r.popBodyTau);
                s += bodyBp.Process(rng.NextBipolar()) * be * bodyG;
            }

            // 衝撃波成分。低域の芯を作る（約 -6dB/oct なので 100Hz〜2kHz が埋まる）
            if (clickG > 0f)
                s += AudioDsp.Friedlander(lt, r.popClickTStar) * clickG;

            buf[start + j] += s * amp;
        }
    }

    // ── こだま・残響 ──
    // 遅延させた減衰コピーを重ねる。反射を重ねるごとにカットオフを下げて
    // 「遠くから返ってくる」感じにする。
    private static float[] ApplyEcho(float[] src, Recipe r)
    {
        int taps = r.echoDelays.Length;

        float maxDelay = 0f;
        for (int j = 0; j < taps; j++)
            if (r.echoDelays[j] > maxDelay) maxDelay = r.echoDelays[j];

        int n = src.Length + (int)((maxDelay + 0.3f) * SampleRate);
        var outBuf = new float[n];
        Array.Copy(src, outBuf, src.Length);

        float srcPeak = AudioDsp.Peak(src);
        if (srcPeak <= 0f) return outBuf;

        var tap = new float[src.Length];

        for (int j = 0; j < taps; j++)
        {
            float gain = j < r.echoGains.Length ? r.echoGains[j] : 0.2f;
            float cut  = Math.Max(80f, r.echoCutoff / (j + 1));

            Array.Copy(src, tap, src.Length);
            AudioDsp.LowpassInPlace(tap, cut, 0.7f, SampleRate, 2);

            // フィルタで下がった分は測って戻す。こうすれば echoGains が
            // そのまま「元音に対する反射の大きさ」になる
            AudioDsp.NormalizePeak(tap, srcPeak * gain);

            int d = (int)(r.echoDelays[j] * SampleRate);
            for (int i = 0; i < src.Length; i++)
            {
                int idx = i + d;
                if (idx >= n) break;
                outBuf[idx] += tap[i];
            }
        }

        return outBuf;
    }

    // ── WAV エンコード（16bit PCM モノラル）──
    // モノラルにしてあるのは、Unity で 3D 定位（spatialBlend > 0）を効かせるには
    // モノラル素材が必要なため。花火が出た画面位置から鳴らせるようにしておく。
    public static byte[] EncodeWav16(float[] samples, int sampleRate)
    {
        const int channels      = 1;
        const int bitsPerSample = 16;

        int dataBytes  = samples.Length * 2;
        int byteRate   = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        var bytes = new byte[44 + dataBytes];
        int p = 0;

        void PutAscii(string s) { for (int i = 0; i < s.Length; i++) bytes[p++] = (byte)s[i]; }
        void PutU32(uint v)
        {
            bytes[p++] = (byte)(v      );
            bytes[p++] = (byte)(v >>  8);
            bytes[p++] = (byte)(v >> 16);
            bytes[p++] = (byte)(v >> 24);
        }
        void PutU16(ushort v)
        {
            bytes[p++] = (byte)(v     );
            bytes[p++] = (byte)(v >> 8);
        }

        PutAscii("RIFF");
        PutU32((uint)(36 + dataBytes));   // このあとに続くバイト数
        PutAscii("WAVE");

        PutAscii("fmt ");
        PutU32(16);                       // fmt チャンクの長さ
        PutU16(1);                        // PCM
        PutU16(channels);
        PutU32((uint)sampleRate);
        PutU32((uint)byteRate);
        PutU16((ushort)blockAlign);
        PutU16(bitsPerSample);

        PutAscii("data");
        PutU32((uint)dataBytes);

        for (int i = 0; i < samples.Length; i++)
        {
            float s = AudioDsp.Clamp(samples[i], -1f, 1f);
            short v = (short)Math.Round(s * 32767.0);
            bytes[p++] = (byte)(v     );
            bytes[p++] = (byte)(v >> 8);
        }

        return bytes;
    }

    /// <summary>互換用。ピーク値の取得は AudioDsp.Peak に移した</summary>
    public static float Peak(float[] buf) => AudioDsp.Peak(buf);

    public static float Rms(float[] buf)
    {
        if (buf.Length == 0) return 0f;
        double sum = 0.0;
        for (int i = 0; i < buf.Length; i++) sum += (double)buf[i] * buf[i];
        return (float)Math.Sqrt(sum / buf.Length);
    }

    // ── 生成セット ──
    // 4カテゴリ（開花 / 打ち上げ / パチパチ / こだま）を聞き比べられる10本。
    // 数値を変えて FireworkSoundBaker のメニューを再実行すれば作り直せる。
    public static List<Recipe> DefaultSet()
    {
        return new List<Recipe>
        {
            // ── 打ち上げ ヒュ〜（3種）──
            // 実測に合わせて 2〜4kHz の狭帯域・下降スイープにしてある
            new Recipe {
                name = "launch_whistle_01", kind = SoundKind.Launch, seed = 1001u,
                reverbMix = 0.10f,
                duration = 1.2f,
                launchFreqFrom = 3600f, launchFreqTo = 2400f,
            },
            new Recipe {
                name = "launch_whistle_02", kind = SoundKind.Launch, seed = 1002u,
                reverbMix = 0.10f,
                duration = 0.9f,
                launchFreqFrom = 3900f, launchFreqTo = 2500f,
                launchVibHz = 7f, launchVibCent = 55f,
            },
            new Recipe {
                name = "launch_whistle_03", kind = SoundKind.Launch, seed = 1003u,
                reverbMix = 0.10f,
                duration = 1.5f,
                launchFreqFrom = 3100f, launchFreqTo = 1900f,
                launchAirGain = 0.10f, launchFadeCurve = 1.1f,
            },

            // ── 開花 大玉 ドーン（2種）──
            new Recipe {
                name = "burst_large_01", kind = SoundKind.Burst, seed = 2001u,
                duration = 1.9f,
            },
            // より低く重い大玉
            new Recipe {
                name = "burst_large_02", kind = SoundKind.Burst, seed = 2002u,
                duration = 2.2f,
                shockTStar = 0.0045f, shockGain = 0.45f,
                thumpFreqFrom = 90f, thumpFreqTo = 34f, thumpSweepSec = 0.32f,
                thumpTau = 0.38f, thumpGain = 1.15f,
                bodyCutFrom = 3500f, bodyCutTo = 1100f, bodyCutSec = 0.65f, bodyTau = 0.6f,
                rumbleCut = 900f, rumbleTau = 1.7f, rumbleGain = 0.5f,
            },

            // ── 開花 小玉 パン（2種）──
            // 小玉は衝撃波を強め・低域を浅くして、短く高い「パン」にする
            new Recipe {
                name = "burst_small_01", kind = SoundKind.Burst, seed = 2101u,

                duration = 0.9f,
                shockTStar = 0.0016f, shockGain = 0.85f,
                thumpFreqFrom = 190f, thumpFreqTo = 90f, thumpSweepSec = 0.09f,
                thumpTau = 0.10f, thumpGain = 0.75f,
                bodyCutFrom = 7000f, bodyCutTo = 1800f, bodyCutSec = 0.18f,
                bodyTau = 0.16f, bodyGain = 0.8f,
                rumbleCut = 2200f, rumbleTau = 0.35f, rumbleGain = 0.25f,

            },
            new Recipe {
                name = "burst_small_02", kind = SoundKind.Burst, seed = 2102u,
                crackGain = 0.30f, crackHigh = 5000f,
                duration = 0.75f,
                shockTStar = 0.0012f, shockGain = 1.0f,
                thumpFreqFrom = 240f, thumpFreqTo = 110f, thumpSweepSec = 0.07f,
                thumpTau = 0.08f, thumpGain = 0.65f,
                bodyCutFrom = 9000f, bodyCutTo = 2400f, bodyCutSec = 0.14f,
                bodyTau = 0.12f, bodyGain = 0.8f,
                rumbleCut = 2600f, rumbleTau = 0.28f, rumbleGain = 0.2f,

            },

            // ── こだま・残響つきの大玉 ──
            // burst_large_01 と同じ音に反射を足したもの。聞き比べ用に seed も揃えてある
            new Recipe {
                name = "burst_large_echo_01", kind = SoundKind.Burst, seed = 2001u,
                duration = 1.9f,
                echoDelays = new[] { 0.18f, 0.41f, 0.78f },
                echoGains  = new[] { 0.45f, 0.24f, 0.11f },
                echoCutoff = 1200f,
            },

            // ── バチバチ（爆竹風）──
            // 爆竹.mp3 の実測: 粒の重心 6783Hz / 帯域幅 4700Hz / 減衰(-20dB) 105ms
            //                  25.6粒/秒 / 前半38粒:後半5粒 と強く減衰
            new Recipe {
                name = "crackle_firecracker_01", kind = SoundKind.Crackle, seed = 3001u,
                reverbMix = 0.22f,
                duration = 1.7f,
                popCount = 36,                                    // 密度を下げて粒を融合させない
                popFreqMin = 2200f, popFreqMax = 5200f, popQ = 1.25f, popClickGain = 0.11f,
                reverbDamp = 0.68f,
                popTauMin = 0.030f, popTauMax = 0.080f,
                popInClusterMin = 0.012f, popInClusterMax = 0.055f,
                popDensityCurve = 3.2f,
                popClusterMin = 2, popClusterMax = 7,
            },
            // もっと連続して弾ける長め版
            new Recipe {
                name = "crackle_firecracker_02", kind = SoundKind.Crackle, seed = 3002u,
                reverbMix = 0.24f,
                duration = 2.4f,
                popCount = 54,
                popFreqMin = 2000f, popFreqMax = 4900f, popQ = 1.2f, popClickGain = 0.12f,
                reverbDamp = 0.70f,
                popTauMin = 0.034f, popTauMax = 0.090f,
                popInClusterMin = 0.013f, popInClusterMax = 0.060f,
                popDensityCurve = 1.9f,
                popClusterMin = 3, popClusterMax = 9,
            },

            // ── パチパチ（手持ち花火風）──
            // 手持ち花火.mp3 の実測: 粒の重心 9185〜9325Hz / 帯域幅 4900Hz
            //                        17〜77粒/秒 / 密度の偏りは小さい（持続的）
            new Recipe {
                name = "crackle_sparkler_01", kind = SoundKind.Crackle, seed = 3101u,
                reverbMix = 0.16f,
                duration = 2.0f,
                popCount = 120,
                popFreqMin = 4200f, popFreqMax = 9500f, popQ = 1.4f, popClickGain = 0.18f, popBodyGain = 0.22f,
                popTauMin = 0.006f, popTauMax = 0.022f,
                popDensityCurve = 1.2f,                           // ほぼ一様＝持続する
                popClusterMin = 1, popClusterMax = 4,
                popInClusterMin = 0.012f, popInClusterMax = 0.055f,
            },
            // 粒を細かく密にした版
            new Recipe {
                name = "crackle_sparkler_02", kind = SoundKind.Crackle, seed = 3102u,
                reverbMix = 0.16f,
                duration = 2.2f,
                popCount = 220,
                popFreqMin = 4800f, popFreqMax = 10500f, popQ = 1.45f, popClickGain = 0.16f, popBodyGain = 0.18f,
                popTauMin = 0.004f, popTauMax = 0.014f,
                popDensityCurve = 1.1f,
                popClusterMin = 1, popClusterMax = 3,
                popInClusterMin = 0.003f, popInClusterMax = 0.014f,
            },
        };
    }
}
