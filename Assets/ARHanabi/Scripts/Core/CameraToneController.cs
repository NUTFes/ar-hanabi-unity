// System.Diagnostics.Debug と UnityEngine.Debug が衝突するため、
// 名前空間ごと using せず [Conditional] だけを別名で取り込む（ArLog.cs と同じ作法）
using ConditionalAttribute = System.Diagnostics.ConditionalAttribute;
using UnityEngine;

// ===== CameraToneController =====
// 会場の照明が明るいとカメラ映像が白飛び（画素が255に張り付く）し、
// MediaPipe PoseLandmarker が人を検出できなくなる問題への対処。
//
// Unity の WebCamTexture には露出・ゲイン・ホワイトバランスの API が無いので、
// カメラ側を絞ることはコードからはできない。そこで「検出に渡す画像だけを
// 白飛びの手前まで戻す」トーン補正（レベル補正＋ガンマ）を1段挟む。
// 表示側（背景Quad）には掛けない。詳しい設計は docs/plans/2026-09-camera-tone.md 参照。
//
// ── 自動レベルは「画面全体を縦6分割」して分割ごとに独立に計算する ──
//   白飛びは画面のどこにでも起こりうる（照明が上から当たれば上部、机や床の反射なら
//   下部、その他の高さも当然ありうる）。フレーム全体を1本のヒストグラムで見ると、
//   飛んでいる範囲が画面の一部にとどまる場合に統計へほとんど効かず、自動補正が
//   利かない。逆に1本の値を画面全体に強く掛けると、飛んでいない範囲まで
//   不必要に暗くなる。
//   そこで縦方向に AutoBandCount 個のバンドへ分け、バンドごとに個別の
//   黒レベル・白レベルを自動計算する。適用時は「その行がどのバンドの
//   近くか」で隣り合う2バンドの間をなめらかに補間するので、バンドの境目が
//   段差として見えることはない（LUTの補間なので計算は軽い）。
//   横方向は分割しない（会場の照明は天井・窓など高さに依存することが多く、
//   横方向のむらは想定していない。必要になれば同じ考え方を横方向にも拡張できる）。
//
// ── DopamineModeController と同じ形にしてある理由 ──
//   「上書きして戻す」を一切やらず、「実効値を毎回引く」に統一する。
//   自動レベル（autoLevels）が ON の間、手動の保存値（blackLevel/whiteLevel）には
//   一切触れない。自動が計算した値はバンドごとの実行時フィールドにしか入らないので、
//   自動を OFF にした瞬間に読む先が保存値へ戻るだけで済む
//   （戻す処理が存在しなければ、戻し忘れも、戻す前に落ちる事故も、原理的に起きない）。
//
// ── 手動モードは今もバンド分割しない ──
//   自動が「画面のどこが飛んでいるか」を勝手に見つけて直すのに対し、
//   手動は「操作した人が画面全体に対して決めた1組の値」という単純な役割に留める。
//   会場が変わっても飛ぶ場所が毎回同じなら自動に任せず1組の値で運用したい、
//   という現場向けの単純な受け皿として GUI を増やさないままにしてある。
//
// ── 呼び出し側 ──
//   ・PoseLandmarkDetector           … 検出用バッファへ毎フレーム Measure→Apply
//   ・SelfieSegmentationController   … NHWC 変換ループの中で行ごとに LUT を直接引く
//   どちらも「発火の瞬間に Instance を引く」pull 方式で、push（配る）はしない
//   （RuntimeInitializeOnLoadMethod での自己生成とシーン上のコンポーネントの
//   起動順を考える必要が出るのを避ける。CockpitFrameOverlay 等と同じ作法）。
public class CameraToneController : MonoBehaviour
{
    public static CameraToneController Instance { get; private set; }

    // ── 既定値 ──
    // フィールドの初期値と ResetToDefault() の両方がここを見るので、
    // 片方だけ直して既定値がズレる事故が起きない
    private const bool  DefaultToneEnabled = true;
    private const bool  DefaultAutoLevels  = true;
    private const float DefaultBlackLevel  = 0f;
    private const float DefaultWhiteLevel  = 1f;
    private const float DefaultGamma       = 1f;

    [Header("ON/OFF")]
    [Tooltip("検出に渡す画像にレベル補正＋ガンマを掛ける。OFF のときは画素に一切触れない\n" +
             "（HasCorrection が false になり、呼び出し側はループごとスキップする）")]
    [SerializeField] private bool toneEnabled = DefaultToneEnabled;

    [Tooltip("黒レベル・白レベルを映像から自動で決める。ON の間、手動のスライダー値\n" +
             "（blackLevel/whiteLevel）には一切書き込まない。OFF にした瞬間、\n" +
             "読む先が手動値へ戻るだけで、値そのものは汚れていない。\n" +
             "ON 中は画面を縦6分割し、白飛びしている場所だけを見つけて補正する\n" +
             "（クラス冒頭コメント参照）")]
    [SerializeField] private bool autoLevels = DefaultAutoLevels;

    [Header("手動レベル補正（自動 OFF のときだけ効く・画面全体に同じ値）")]
    [Tooltip("この値以下を黒に落とす（0..1）。白飛びした映像は画素が高輝度側の\n" +
             "狭い範囲に潰れているので、下端を切って全域へ伸ばすとコントラストが戻る")]
    [SerializeField, Range(0f, 0.6f)] private float blackLevel = DefaultBlackLevel;

    [Tooltip("この値以上を白に上げる（0..1）")]
    [SerializeField, Range(0.4f, 1f)] private float whiteLevel = DefaultWhiteLevel;

    [Tooltip("レベル補正後に掛けるガンマ。1 を超えると中間調を落として\n" +
             "高輝度側の分離を広げる（白飛びの補助）。自動レベルの対象外で、\n" +
             "画面全体・全バンド共通で常にこの値をそのまま使う")]
    [SerializeField, Range(0.4f, 2.5f)] private float gamma = DefaultGamma;

    // ── 縦方向の自動分割数 ──
    // 増やすほど局所的に効くが、1バンドあたりの標本数が減って自動計算が
    // ノイジーになる（TargetSampleCount 個の標本をバンド数で割った数しか
    // 1バンドに残らないため）。GUIには出さない内部実装の粒度なので、
    // 現場の傾向を見て調整したければここを直すだけで済む
    private const int AutoBandCount = 6;

    // ── 自動レベルが計算した値（保存しない・実行時のみ）──
    // SettingsStore には一切書かない。「上書きして戻す」をやらないための核心。
    // バンドごとに1本ずつ持つ（index 0 が画面の下端側、AutoBandCount-1 が上端側）
    private readonly float[] _autoBlackBand = NewFilled(AutoBandCount, 0f);
    private readonly float[] _autoWhiteBand = NewFilled(AutoBandCount, 1f);

    // ── 診断用の統計（管理画面のヘルプ行に常時出す。画面全体の集計）──
    public float ClipRatio { get; private set; }   // 輝度 >= 250 の画素の割合（＝白飛び率）
    public float MeanLuma  { get; private set; }

    /// <summary>自動が計算した黒レベルの、バンド間での最小〜最大（ラベルの実効値併記に使う）</summary>
    public float AutoBlackMin => Min(_autoBlackBand);
    public float AutoBlackMax => Max(_autoBlackBand);
    /// <summary>自動が計算した白レベルの、バンド間での最小〜最大</summary>
    public float AutoWhiteMin => Min(_autoWhiteBand);
    public float AutoWhiteMax => Max(_autoWhiteBand);

    /// <summary>ラベルに実効値（自動が計算した値）を併記すべきか</summary>
    public bool LevelsOverridden => autoLevels;

    // ── 実効値。呼び出し側はここではなく HasCorrection/Apply/RowLutOrNull しか見ない ──
    private float EffBlackForBand(int band) => autoLevels ? _autoBlackBand[band] : blackLevel;
    private float EffWhiteForBand(int band) => autoLevels ? _autoWhiteBand[band] : whiteLevel;
    private float EffGamma => gamma;   // ガンマは自動で動かさない（自動は「範囲」だけを決める）。バンド共通

    /// <summary>ほぼ恒等（無補正）とみなす許容誤差。ここに収まれば補正を配らずコストをゼロにする</summary>
    private const float NoOpEps = 1e-3f;

    private const int LutSize = 256;

    // バンドごとの LUT（256バイト×バンド数）。焼き直しは値が変わったときだけ
    private readonly byte[][] _bandLuts      = NewJaggedBytes(AutoBandCount, LutSize);
    private readonly float[]  _lutBlackBand  = NewFilled(AutoBandCount, float.NaN);
    private readonly float[]  _lutWhiteBand  = NewFilled(AutoBandCount, float.NaN);
    private float _lutGamma = float.NaN;
    private bool  _lutsBuilt;

    // 行ごとに「隣り合う2バンドを補間したLUT」を作る使い回しバッファ。
    // 幅ぶん（数百〜数千画素）ではなく256要素だけなので、行ごとに焼き直しても軽い
    private readonly byte[] _rowLutScratch = new byte[LutSize];

    /// <summary>
    /// 今、補正すべきものが何かあるか。補正 OFF、またはほぼ恒等（自動が全バンドとも
    /// 「普通の露出」と判断した場合を含む）なら false。呼び出し側はこれが false なら
    /// ループへ入らないこと（コストがゼロになる）
    /// </summary>
    public bool HasCorrection
    {
        get
        {
            if (!toneEnabled) return false;

            RebuildLutsIfNeeded();

            if (Mathf.Abs(_lutGamma - 1f) >= NoOpEps) return true;

            for (int b = 0; b < AutoBandCount; b++)
                if (_lutBlackBand[b] >= NoOpEps || _lutWhiteBand[b] <= 1f - NoOpEps)
                    return true;

            return false;
        }
    }

    private void RebuildLutsIfNeeded()
    {
        float g = EffGamma;
        bool gammaChanged = !_lutsBuilt || !Mathf.Approximately(g, _lutGamma);

        for (int b = 0; b < AutoBandCount; b++)
        {
            float black = EffBlackForBand(b);
            float white = EffWhiteForBand(b);

            if (!_lutsBuilt || gammaChanged ||
                !Mathf.Approximately(black, _lutBlackBand[b]) ||
                !Mathf.Approximately(white, _lutWhiteBand[b]))
            {
                BuildLevelsLut(_bandLuts[b], black, white, g);
                _lutBlackBand[b] = black;
                _lutWhiteBand[b] = white;
            }
        }

        _lutGamma  = g;
        _lutsBuilt = true;
    }

    // レベル補正 → ガンマの順。range が0近くまで潰れても NaN/Inf を出さないよう底を敷く
    private static void BuildLevelsLut(byte[] lut, float black, float white, float gamma)
    {
        float range = Mathf.Max(1e-4f, white - black);
        for (int i = 0; i < LutSize; i++)
        {
            float x = i / (float)(LutSize - 1);
            float y = Mathf.Clamp01((x - black) / range);
            y = Mathf.Pow(y, gamma);
            lut[i] = (byte)Mathf.RoundToInt(y * 255f);
        }
    }

    // ── 行位置 → バンド座標 ──
    //
    // row は 0 が画面の下端、height-1 が画面の上端
    // （WebCamTexture.GetPixels32() が下の行から並んだ配列を返すため。
    //   PoseLandmarkDetector.HipCenter の冒頭コメントと同じ前提）。
    //
    // バンド b の「代表位置」はそのバンドが受け持つ範囲の中央 (b+0.5)/AutoBandCount
    // に置く。RowToBandIndex（計測時の振り分け）と RowToBandCoord（適用時の補間）が
    // 同じ中央基準で揃っていないと、バンドの境目で補間がズレる
    private static float RowToBandCoord(int row, int height)
    {
        float pos = (height <= 1) ? 0f : row / (float)(height - 1);
        return pos * AutoBandCount - 0.5f;
    }

    private static int RowToBandIndex(int row, int height)
    {
        float pos = (height <= 1) ? 0f : row / (float)(height - 1);
        int band = Mathf.FloorToInt(pos * AutoBandCount);
        return Mathf.Clamp(band, 0, AutoBandCount - 1);
    }

    // その行に使う LUT を _rowLutScratch へ焼く（隣り合う2バンドの線形補間）
    private void BuildRowLut(int row, int height)
    {
        float coord = RowToBandCoord(row, height);
        int   lower = Mathf.Clamp(Mathf.FloorToInt(coord), 0, AutoBandCount - 1);
        int   upper = Mathf.Clamp(lower + 1,               0, AutoBandCount - 1);
        float t     = Mathf.Clamp01(coord - lower);

        var lutLower = _bandLuts[lower];
        var lutUpper = _bandLuts[upper];
        for (int i = 0; i < LutSize; i++)
            _rowLutScratch[i] = (byte)Mathf.RoundToInt(Mathf.Lerp(lutLower[i], lutUpper[i], t));
    }

    /// <summary>
    /// LUT を buffer にその場で適用する（Color32 用）。HasCorrection が false なら
    /// ループへ入らずすぐ返る。width/height は buffer の実際の解像度
    /// （WebCamTexture.GetPixels32() が返す配列と同じ並び）
    /// </summary>
    public void Apply(Color32[] buffer, int width, int height)
    {
        if (buffer == null || width <= 0 || height <= 0) return;
        if (!HasCorrection) return;

        for (int row = 0; row < height; row++)
        {
            BuildRowLut(row, height);

            int rowStart = row * width;
            int rowEnd   = rowStart + width;
            for (int i = rowStart; i < rowEnd; i++)
            {
                var p = buffer[i];
                buffer[i] = new Color32(_rowLutScratch[p.r], _rowLutScratch[p.g], _rowLutScratch[p.b], p.a);
            }
        }
    }

    /// <summary>
    /// 指定した行に使う LUT を返す（Color32 以外の画素形式を扱う呼び出し側向け。
    /// SelfieSegmentationController 参照）。HasCorrection が false なら null を返す
    /// （呼び出し側は無補正のまま処理してよい）。
    /// 戻り値は使い回しバッファなので、次にこのメソッドを呼ぶまでの間だけ有効
    /// </summary>
    public byte[] RowLutOrNull(int row, int height)
    {
        if (!HasCorrection) return null;   // これが RebuildLutsIfNeeded を内部で呼ぶ

        BuildRowLut(row, height);
        return _rowLutScratch;
    }

    // ── 自動レベルの計測 ──
    //
    // ⚠️ 呼び出し側は「生画素」を渡すこと（Apply より前）。
    //   補正後の画素を測ると、次のフレームの目標値が補正結果に引かれて発散する。
    //
    // 0.25秒に1回だけ実際に計測する。呼び出し自体は毎フレーム行ってよい
    // （DetectFrozenFrame と同じ、間引きは内部で持つ形）。
    private const float SampleIntervalSeconds = 0.25f;
    private const int   TargetSampleCount     = 20000;   // 全バンド合計の目安
    private const float LowerPercentile       = 0.02f;
    private const float UpperPercentile       = 0.98f;

    // 安全弁。真っ白な壁を見たときに人まで潰さないための床
    //（これが無いと「白飛びに応じて黒レベルを上げる」が発散し、映像全体が真っ黒になる）
    private const float MaxAutoBlack = 0.60f;
    private const float MinAutoRange = 0.15f;

    // 時定数（秒）。毎フレーム値が飛ぶと検出のしきい値判定が揺れるので必ず均す。
    // 初回計測時は over-shoot気味に速く寄せる（起動直後から待たされないため）
    private const float SmoothingTimeConstant = 1.5f;
    private const float MaxSmoothingDt        = 2.0f;

    private const int LumaBins = 256;

    // バンドごとのヒストグラムと集計。毎フレーム確保しないよう全部フィールドで使い回す
    private readonly int[][] _bandHistograms   = NewJaggedInts(AutoBandCount, LumaBins);
    private readonly int[]   _bandSampleCount  = new int[AutoBandCount];
    private readonly long[]  _bandLumaSum      = new long[AutoBandCount];
    private readonly int[]   _bandClipCount    = new int[AutoBandCount];

    private float _lastMeasureTime = float.NegativeInfinity;

    /// <summary>
    /// 生画素から白飛び率・平均輝度・バンドごとの自動レベルの目標値を更新する。
    /// toneEnabled/autoLevels の ON/OFF に関係なく常に計算する
    /// （管理画面のヘルプ行に白飛び率を常時出したいため。補正 OFF でも診断はできる）。
    /// width/height は raw の実際の解像度（WebCamTexture.GetPixels32() と同じ並び）
    /// </summary>
    public void Measure(Color32[] raw, int width, int height, float now)
    {
        if (raw == null || raw.Length == 0 || width <= 0 || height <= 0) return;

        float elapsed = now - _lastMeasureTime;
        if (elapsed < SampleIntervalSeconds) return;

        float dt = Mathf.Clamp(elapsed, SampleIntervalSeconds, MaxSmoothingDt);
        _lastMeasureTime = now;

        for (int b = 0; b < AutoBandCount; b++)
        {
            System.Array.Clear(_bandHistograms[b], 0, LumaBins);
            _bandSampleCount[b] = 0;
            _bandLumaSum[b]     = 0;
            _bandClipCount[b]   = 0;
        }

        // 全画素を見る必要はない。等間隔に間引いて畳み込む（DetectFrozenFrame と同じ考え方）
        int stride = Mathf.Max(1, raw.Length / TargetSampleCount);

        long totalLumaSum = 0;
        int  totalClip    = 0;
        int  totalSampled = 0;

        for (int i = 0; i < raw.Length; i += stride)
        {
            int row  = i / width;
            int band = RowToBandIndex(row, height);

            var p = raw[i];
            // BT.601相当の輝度近似。÷256 をシフトで済ませる固定小数（77+150+29=256）
            int luma = (p.r * 77 + p.g * 150 + p.b * 29) >> 8;

            _bandHistograms[band][luma]++;
            _bandSampleCount[band]++;
            _bandLumaSum[band] += luma;

            bool clipped = luma >= 250;
            if (clipped) { _bandClipCount[band]++; totalClip++; }

            totalLumaSum += luma;
            totalSampled++;
        }

        if (totalSampled == 0) return;

        MeanLuma  = totalLumaSum / (float)totalSampled / 255f;
        ClipRatio = totalClip    / (float)totalSampled;

        float alpha = 1f - Mathf.Exp(-dt / SmoothingTimeConstant);

        for (int b = 0; b < AutoBandCount; b++)
        {
            // このバンドに標本が入らなかった（解像度が極端に低いなど）場合は
            // 前回の値を維持する。0で計算すると「白飛び100%」に誤認識するため
            if (_bandSampleCount[b] == 0) continue;

            float targetBlack = PercentileLuma(_bandHistograms[b], _bandSampleCount[b], LowerPercentile) / 255f;
            float targetWhite = PercentileLuma(_bandHistograms[b], _bandSampleCount[b], UpperPercentile) / 255f;

            targetBlack = Mathf.Min(targetBlack, MaxAutoBlack);
            targetWhite = Mathf.Max(targetWhite, targetBlack + MinAutoRange);

            _autoBlackBand[b] = Mathf.Lerp(_autoBlackBand[b], targetBlack, alpha);
            _autoWhiteBand[b] = Mathf.Lerp(_autoWhiteBand[b], targetWhite, alpha);
        }

        LogBandStats();
    }

    // 現場で「どのバンドが飛んでいるか」を追いたいときのための詳細ログ。
    // AR_VERBOSE_LOG 未定義時は呼び出しごと消えるので、文字列組み立てのコストもゼロになる
    // （SelfieSegmentationController.LogMaskStats と同じ作法）
    [Conditional("AR_VERBOSE_LOG")]
    private void LogBandStats()
    {
        var sb = new System.Text.StringBuilder("[Tone] バンド別（下→上）白飛び率／平均輝度: ");
        for (int b = 0; b < AutoBandCount; b++)
        {
            if (_bandSampleCount[b] == 0) { sb.Append("[―] "); continue; }
            float clip = _bandClipCount[b] / (float)_bandSampleCount[b];
            float mean = _bandLumaSum[b]   / (float)_bandSampleCount[b] / 255f;
            sb.Append($"[{clip * 100f:F0}%/{mean:F2}] ");
        }
        Debug.Log(sb.ToString());
    }

    // ヒストグラムの下から積算して、累積が sampled*fraction に達した輝度を返す
    private static int PercentileLuma(int[] histogram, int sampled, float fraction)
    {
        int threshold  = Mathf.CeilToInt(sampled * fraction);
        int cumulative = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= threshold) return i;
        }
        return histogram.Length - 1;
    }

    private static float Min(float[] a) { float m = a[0]; for (int i = 1; i < a.Length; i++) m = Mathf.Min(m, a[i]); return m; }
    private static float Max(float[] a) { float m = a[0]; for (int i = 1; i < a.Length; i++) m = Mathf.Max(m, a[i]); return m; }

    private static float[] NewFilled(int n, float v)
    {
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = v;
        return a;
    }

    private static byte[][] NewJaggedBytes(int outer, int inner)
    {
        var a = new byte[outer][];
        for (int i = 0; i < outer; i++) a[i] = new byte[inner];
        return a;
    }

    private static int[][] NewJaggedInts(int outer, int bins)
    {
        var a = new int[outer][];
        for (int i = 0; i < outer; i++) a[i] = new int[bins];
        return a;
    }

    /// <summary>変更通知。Admin UI がラベルを再計算するために購読する</summary>
    public event System.Action OnChanged;
    private void Changed() => OnChanged?.Invoke();

    // ── 公開プロパティ（Admin 画面から書き込む唯一の対象）──

    public bool ToneEnabled
    {
        get => toneEnabled;
        set { toneEnabled = value; SettingsStore.SetBool(KeyEnabled, value); Changed(); }
    }
    public void ToggleToneEnabled() => ToneEnabled = !toneEnabled;

    public bool AutoLevelsEnabled
    {
        get => autoLevels;
        set { autoLevels = value; SettingsStore.SetBool(KeyAuto, value); Changed(); }
    }
    public void ToggleAutoLevels() => AutoLevelsEnabled = !autoLevels;

    public float BlackLevel
    {
        get => blackLevel;
        set { blackLevel = value; SettingsStore.SetFloat(KeyBlack, value); Changed(); }
    }

    public float WhiteLevel
    {
        get => whiteLevel;
        set { whiteLevel = value; SettingsStore.SetFloat(KeyWhite, value); Changed(); }
    }

    public float Gamma
    {
        get => gamma;
        set { gamma = value; SettingsStore.SetFloat(KeyGamma, value); Changed(); }
    }

    /// <summary>
    /// すべての保存値を既定値へ戻す（ON/OFF・自動・黒/白/ガンマ）。保存もその場で行うので、
    /// 次回起動を待たずに今すぐ効く。バンドごとの自動計算値は消さない
    /// （測定して分かった値であって「設定」ではないので戻す対象ではなく、
    ///   自動ONならこの直後の Measure でまた正しい値に更新される）
    /// </summary>
    public void ResetToDefault()
    {
        toneEnabled = DefaultToneEnabled;
        autoLevels  = DefaultAutoLevels;
        blackLevel  = DefaultBlackLevel;
        whiteLevel  = DefaultWhiteLevel;
        gamma       = DefaultGamma;

        SettingsStore.SetBool(KeyEnabled, toneEnabled);
        SettingsStore.SetBool(KeyAuto,    autoLevels);
        SettingsStore.SetFloat(KeyBlack,  blackLevel);
        SettingsStore.SetFloat(KeyWhite,  whiteLevel);
        SettingsStore.SetFloat(KeyGamma,  gamma);

        Debug.Log("[Tone] 既定値に戻しました（補正ON・自動ON・黒0.00・白1.00・ガンマ1.00）");
        Changed();
    }

    /// <summary>シーンにあればそれを、無ければ自動生成して返す</summary>
    public static CameraToneController GetOrCreate()
    {
        if (Instance != null) return Instance;

        var found = FindFirstObjectByType<CameraToneController>();
        if (found != null) return found;

        var go = new GameObject(nameof(CameraToneController));
        return go.AddComponent<CameraToneController>();
    }

    // SpaceModeController / ExperienceDirector / DopamineModeController と同じく、
    // シーンに置かなくても動くようにする。Admin 画面を一度も開かずに検出だけ
    // 動いた場合でも、PoseLandmarkDetector が Instance を引きに来るので、
    // 起動時に必ず存在させておく
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap() => GetOrCreate();

    // ── 永続化 ──
    // ⚠️ バンドごとの自動値はここに無い。自動が計算した値は実行時のみで、
    //   SettingsStore には一切書かない（クラス冒頭のコメント参照）
    private const string KeyEnabled = nameof(CameraToneController) + "." + nameof(toneEnabled);
    private const string KeyAuto    = nameof(CameraToneController) + "." + nameof(autoLevels);
    private const string KeyBlack   = nameof(CameraToneController) + "." + nameof(blackLevel);
    private const string KeyWhite   = nameof(CameraToneController) + "." + nameof(whiteLevel);
    private const string KeyGamma   = nameof(CameraToneController) + "." + nameof(gamma);

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        // カメラ位置・鏡像設定と同じく、現場の照明という「その会場の条件」なので
        // ドーパミンモードのマスターとは違い、毎起動リセットはしない（前回の状態を引き継ぐ）
        toneEnabled = SettingsStore.GetBool(KeyEnabled, toneEnabled);
        autoLevels  = SettingsStore.GetBool(KeyAuto,    autoLevels);
        blackLevel  = SettingsStore.GetFloat(KeyBlack,  blackLevel);
        whiteLevel  = SettingsStore.GetFloat(KeyWhite,  whiteLevel);
        gamma       = SettingsStore.GetFloat(KeyGamma,  gamma);

        Debug.Log($"[Tone] 明るさ補正 初期化完了（補正={toneEnabled} / 自動={autoLevels}（{AutoBandCount}分割） / " +
                  $"黒={blackLevel:F2} / 白={whiteLevel:F2} / ガンマ={gamma:F2}）");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
