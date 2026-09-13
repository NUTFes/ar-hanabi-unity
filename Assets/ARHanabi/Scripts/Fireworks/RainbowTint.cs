using UnityEngine;

// ===== RainbowTint =====
// ドーパミンモードの「虹色」を、各エフェクトから使える1本の式にまとめたもの。
// DopamineModeController が持つ数値（彩度・散らばり・発光の底上げ）を読み、
// 粒1つ分の色を返すだけの純関数の集まり。
//
// ── MonoBehaviour にしていない理由 ──
//   このクラスは状態を持たない（LUT はただのキャッシュ）。
//   シーンに置く必要も、Update を回す必要も、参照を配線する必要も無い。
//   MonoBehaviour にすると「どこに付いているか」を各エフェクトが知る必要が出て、
//   エフェクト側のプレハブ／動的生成の手順に1行増える。static ならその配線がゼロになる。
//
// ── 明るさ（V）に original.maxColorComponent を使う ──
//   これがこの設計の肝。Color.HSVToRGB(h, s, v) の出力は最大成分がちょうど v なので、
//   V に「元の色の最大成分」を入れると、返る色のどの成分も元の色の最大成分を超えない。
//   つまり
//     ・startColor の Color32 暗黙変換でクランプが起きない
//       （書き込み側のコードを一切改造せずに済む）
//     ・trailBrightness で暗くした尾は暗いまま、型ごとの明暗差もそのまま残る
//   「明るさの設計は既存のまま、色相だけ奪う」がこの1式で実現できる。
//
//   アルファには触らない。減衰（fade）・明滅（twinkle）・閃光（flash）・
//   昇りのテーパーはすべてアルファで明るさを作っているので、
//   ここで書き換えるとそれらの計算が丸ごと壊れる。
public static class RainbowTint
{
    // ── 1発ぶんのモード設定のスナップショット ──
    //
    // ⚠️ 各エフェクトは Launch() で1回だけ Capture() し、毎フレーム引き直さないこと。
    //   1. 飛んでいる最中に管理画面で OFF にされた玉が途中で色を失うと「バグに見える」。
    //      1発は最後まで同じモードで飛びきるべき
    //   2. DopamineModeController.Instance != null は UnityEngine.Object の ==
    //      オーバーロード（ネイティブ側への問い合わせを含む）。粒ごとに呼ぶと
    //      実測できる負荷になる
    //   位相（HuePhase）だけは時間で回るので、Update() のループの外で1回読む。
    public readonly struct Settings
    {
        public readonly bool  Enabled;
        public readonly float Saturation;
        public readonly float HueSpread;
        public readonly float IntensityBoost;
        public readonly float ImageSaturationLift;

        public Settings(bool enabled, float saturation, float hueSpread,
                        float intensityBoost, float imageSaturationLift)
        {
            Enabled             = enabled;
            Saturation          = saturation;
            HueSpread           = hueSpread;
            IntensityBoost      = intensityBoost;
            ImageSaturationLift = imageSaturationLift;
        }

        // OFF のときの値。IntensityBoost だけ 0 ではなく 1 にしてあるのは、
        // 呼び出し側が Enabled の確認を忘れて素朴に掛けても
        // 「花火が真っ暗になる」という致命的な壊れ方をしないため
        public static readonly Settings Disabled = new Settings(false, 0f, 0f, 1f, 0f);

        /// <summary>今のモード設定を1発ぶんスナップショットする。Instance が無ければ Disabled</summary>
        public static Settings Capture()
        {
            var c = DopamineModeController.Instance;
            if (c == null || !c.RainbowEnabled) return Disabled;

            return new Settings(true, c.Saturation, c.HueSpread,
                                c.IntensityBoost, c.ImageSaturationLift);
        }
    }

    // ── 色相の LUT ──
    //
    // Color.HSVToRGB は分岐と除算を含むので、5000粒 × 60fps × 同時数発で呼ぶのは避ける。
    // 色相→RGB は「時間で回る位相」に依存しない（位相は添字を回すだけ）ので、
    // 表を1枚焼いておけば毎フレームの計算は添字計算1回と乗算数回で済む。
    //
    // ── 彩度 1 で焼いてある理由 ──
    //   HSV→RGB は  out = V·(1 − S) + V·S·pure(h)  と書ける（pure = 彩度1・明度1の色）。
    //   つまり表を「彩度1」で持っておけば、彩度は後から Lerp で乗せられる。
    //   こうしておくと
    //     ・Replace（型花火・昇り）… 彩度はモード共通の1値
    //     ・Rotate （画像花火）    … 彩度が粒ごとに違う
    //   の両方を同じ1枚の表で賄えるうえ、本番中に管理画面で彩度スライダーを
    //   動かしても表の焼き直しが一切起きない。
    //   （彩度ごとに焼く設計だと、Rotate 側は粒ごとに彩度が違うので表が使えない）
    private const int   LutSize = 256;
    private const int   LutMask = LutSize - 1;   // 256 は2の冪なので & で剰余が取れる
    private static readonly Color[] Lut = new Color[LutSize];
    private static bool _lutReady;

    /// <summary>LUT を作り直させる。次に色を引いたときに焼き直される</summary>
    public static void InvalidateLut() => _lutReady = false;

    // Editor の「ドメインリロード無効」設定では static がプレイ間で生き残る。
    // 表の中身は常に同じなので実害は無いが、static の状態を持ち越さないという
    // このリポジトリの他のクラスと同じ形に揃えておく
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => InvalidateLut();

    private static void EnsureLut()
    {
        if (_lutReady) return;

        for (int i = 0; i < LutSize; i++)
            Lut[i] = Color.HSVToRGB(i / (float)LutSize, 1f, 1f);

        _lutReady = true;
    }

    // ── 粒ごとの色相オフセット ──
    //
    // ⚠️ index / count にしないこと。
    //   ShellFireworkEffect.SampleDirection の球は方向を黄金角のらせんで配っているので、
    //   色相まで index に比例させると位置と色相が完全に相関し、
    //   「球面を虹の帯が1本巻いている」見た目になる。
    //   欲しいのは「粒ごとに虹色」なので、位置と無相関にばらけさせる。
    //
    //   黄金比の小数部を足していく列は、どこで切っても 0..1 にほぼ均等に散る
    //   （低食い違い量列）。乱数と違って隣り合う番号が同じ色に寄ることもない。
    //
    //   帯が欲しくなったら、この1行を index / (float)count に差し替えれば切り替わる
    //   （その場合は呼び出し側から粒数を渡す必要がある）。
    private const float GoldenRatioConjugate = 0.6180339887f;

    public static float HueOffset(int index) => (index * GoldenRatioConjugate) % 1f;

    // ── 色相の表引き ──
    // 渡す hue は「合成済みの色相」。0..1 に丸める処理はここでまとめて行う。
    //
    // 呼び出し側が渡す値は必ず 0 以上（HueOffset も HuePhase も baseHue も 0..1、
    // HueSpread も 0..1、Rotate に渡すオフセットも 0 以上と決めてある）ので、
    // (int) の 0 方向への切り捨てで負に回ることは無い。
    // ※ 負の色相を渡すと & の剰余が1段ずれるので、新しい呼び出しを足すときは
    //   「0 以上を渡す」を必ず守ること。
    // 256 を掛けて int に落とすと、小数部の取り出しと 256段への量子化が同時に済む。
    private static Color PureHue(float hue)
    {
        EnsureLut();
        int idx = (int)(hue * LutSize) & LutMask;
        return Lut[idx];
    }

    /// <summary>
    /// 元の色の「明るさとアルファ」だけ残して、色相と彩度を虹で置き換える。
    /// 型花火の星・尾・小玉と、昇りの火の粉で使う。
    ///
    /// ⚠️ 画像花火はこちらではなく Rotate を使う（非対称は意図的。Rotate のコメント参照）。
    /// </summary>
    public static Color Replace(in Settings s, Color original, float hueOffset, float phase)
    {
        if (!s.Enabled) return original;

        // HueSpread は「1発の中で粒ごとの色相がどれだけ散らばるか」。
        // 0 なら全粒が phase だけを見るので1発1色、1 なら1発で全色が出る
        var pure = PureHue(hueOffset * s.HueSpread + phase);

        // V に元の色の最大成分を使う（クラス冒頭「明るさ（V）に〜」参照）。
        // out = V·(1−S) + V·S·pure なので、最大成分はちょうど V に一致し、
        // どの成分も 1.0 を超えない = Color32 変換でクランプが起きない
        float v  = original.maxColorComponent;
        float lo = v * (1f - s.Saturation);
        float hi = v * s.Saturation;

        // アルファは元のまま。ここを触ると減衰・明滅・閃光・テーパーが壊れる
        return new Color(lo + hi * pure.r,
                         lo + hi * pure.g,
                         lo + hi * pure.b,
                         original.a);
    }

    /// <summary>
    /// 元の彩度と明度を残したまま、色相だけを回す。画像花火（投稿写真）専用。
    ///
    /// ⚠️ 型花火・昇りが Replace、画像花火だけが Rotate という非対称は意図的。
    ///   Replace を写真に掛けると、絵の中の色の関係（肌・空・服の差）が全部消えて
    ///   ただの虹色の塊になり、写真が写真でなくなる。
    ///   逆に型花火に Rotate を掛けても、元が単色なので「1発1色が少し回る」だけで
    ///   虹にならない。揃えようとして片方に寄せると、どちらかの絵が壊れる。
    ///   詳しくは ImageFireworkEffect.cs 側のコメントも参照。
    ///
    /// baseSat には「持ち上げ済みの彩度」を渡すこと
    /// （Lerp(元の彩度, 1, ImageSaturationLift)。Launch 時に1回だけ計算しておく）。
    /// </summary>
    public static Color Rotate(in Settings s, float baseHue, float baseSat, float baseVal,
                               float hueOffset, float phase)
    {
        if (!s.Enabled) return Color.HSVToRGB(baseHue, baseSat, baseVal);

        // 彩度1で焼いた表から純色を引き、そこへ粒ごとの彩度を乗せる。
        // 彩度が粒ごとに違うので表そのものは彩度1でなければならない（LUT のコメント参照）。
        //
        // baseHue には HueSpread を掛けない。掛けると絵が持っている色相の関係
        // （赤い服と青い空の差）まで圧縮されて絵が読めなくなる。
        // 散らばりが掛かるのは「粒ごとのオフセット」の側だけ
        var pure = PureHue(baseHue + hueOffset * s.HueSpread + phase);

        float lo = baseVal * (1f - baseSat);
        float hi = baseVal * baseSat;

        return new Color(lo + hi * pure.r,
                         lo + hi * pure.g,
                         lo + hi * pure.b,
                         1f);
    }
}
