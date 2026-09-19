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
// ── DopamineModeController と同じ形にしてある理由 ──
//   「上書きして戻す」を一切やらず、「実効値を毎回引く」に統一する。
//   自動レベル（autoLevels）が ON の間、手動の保存値（blackLevel/whiteLevel）には
//   一切触れない。自動が計算した値は _autoBlack/_autoWhite という別のフィールドに
//   しか入らないので、自動を OFF にした瞬間に読む先が保存値へ戻るだけで済む
//   （戻す処理が存在しなければ、戻し忘れも、戻す前に落ちる事故も、原理的に起きない）。
//
// ── 呼び出し側 ──
//   ・PoseLandmarkDetector           … 検出用バッファへ毎フレーム Measure→Apply
//   ・SelfieSegmentationController   … NHWC 変換ループの中で LUT を直接引く
//   どちらも「発火の瞬間に Instance を引く」pull 方式で、push（配る）はしない
//   （RuntimeInitializeOnLoadMethod での自己生成とシーン上のコンポーネントの
//   起動順を考える必要が出るのを避ける。CockpitFrameOverlay 等と同じ作法）。
public class CameraToneController : MonoBehaviour
{
    public static CameraToneController Instance { get; private set; }

    [Header("ON/OFF")]
    [Tooltip("検出に渡す画像にレベル補正＋ガンマを掛ける。OFF のときは画素に一切触れない\n" +
             "（LutOrNull が null を返し、呼び出し側はループごとスキップする）")]
    [SerializeField] private bool toneEnabled = true;

    [Tooltip("黒レベル・白レベルを映像から自動で決める。ON の間、手動のスライダー値\n" +
             "（blackLevel/whiteLevel）には一切書き込まない。OFF にした瞬間、\n" +
             "読む先が手動値へ戻るだけで、値そのものは汚れていない")]
    [SerializeField] private bool autoLevels = true;

    [Header("手動レベル補正（自動 OFF のときだけ効く）")]
    [Tooltip("この値以下を黒に落とす（0..1）。白飛びした映像は画素が高輝度側の\n" +
             "狭い範囲に潰れているので、下端を切って全域へ伸ばすとコントラストが戻る")]
    [SerializeField, Range(0f, 0.6f)] private float blackLevel = 0f;

    [Tooltip("この値以上を白に上げる（0..1）")]
    [SerializeField, Range(0.4f, 1f)] private float whiteLevel = 1f;

    [Tooltip("レベル補正後に掛けるガンマ。1 を超えると中間調を落として\n" +
             "高輝度側の分離を広げる（白飛びの補助）。自動レベルの対象外で、\n" +
             "常にこの値をそのまま使う")]
    [SerializeField, Range(0.4f, 2.5f)] private float gamma = 1f;

    // ── 自動レベルが計算した値（保存しない・実行時のみ）──
    // SettingsStore には一切書かない。「上書きして戻す」をやらないための核心
    private float _autoBlack;
    private float _autoWhite = 1f;

    // ── 診断用の統計（管理画面のヘルプ行に常時出す）──
    public float ClipRatio { get; private set; }   // 輝度 >= 250 の画素の割合（＝白飛び率）
    public float MeanLuma  { get; private set; }
    public float AutoBlack => _autoBlack;
    public float AutoWhite => _autoWhite;

    /// <summary>ラベルに実効値（自動が計算した値）を併記すべきか</summary>
    public bool LevelsOverridden => autoLevels;

    // ── 実効値。呼び出し側はここではなく LutOrNull/Apply しか見ない ──
    private float EffBlack => autoLevels ? _autoBlack : blackLevel;
    private float EffWhite => autoLevels ? _autoWhite : whiteLevel;
    private float EffGamma => gamma;   // ガンマは自動で動かさない（自動は「範囲」だけを決める）

    /// <summary>ほぼ恒等（無補正）とみなす許容誤差。ここに収まれば LUT を配らずコストをゼロにする</summary>
    private const float NoOpEps = 1e-3f;

    private const int LutSize = 256;
    private readonly byte[] _lut = new byte[LutSize];
    private bool  _lutBuilt;
    private float _lutBlack = float.NaN, _lutWhite = float.NaN, _lutGamma = float.NaN;

    /// <summary>
    /// 検出に使う LUT。補正 OFF、またはほぼ恒等（自動が「普通の露出」と判断した場合を含む）
    /// なら null を返す。呼び出し側は null ならループへ入らないこと（コストがゼロになる）。
    /// </summary>
    public byte[] LutOrNull
    {
        get
        {
            if (!toneEnabled) return null;

            RebuildLutIfNeeded();

            if (_lutBlack < NoOpEps && _lutWhite > 1f - NoOpEps && Mathf.Abs(_lutGamma - 1f) < NoOpEps)
                return null;

            return _lut;
        }
    }

    private void RebuildLutIfNeeded()
    {
        float black = EffBlack;
        float white = EffWhite;
        float g     = EffGamma;

        if (_lutBuilt &&
            Mathf.Approximately(black, _lutBlack) &&
            Mathf.Approximately(white, _lutWhite) &&
            Mathf.Approximately(g,     _lutGamma))
            return;

        // レベル補正 → ガンマの順。range が0近くまで潰れても NaN/Inf を出さないよう底を敷く
        float range = Mathf.Max(1e-4f, white - black);
        for (int i = 0; i < LutSize; i++)
        {
            float x = i / (float)(LutSize - 1);
            float y = Mathf.Clamp01((x - black) / range);
            y = Mathf.Pow(y, g);
            _lut[i] = (byte)Mathf.RoundToInt(y * 255f);
        }

        _lutBlack = black;
        _lutWhite = white;
        _lutGamma = g;
        _lutBuilt = true;
    }

    /// <summary>LUT を buffer にその場で適用する。LutOrNull が null ならループへ入らずすぐ返る</summary>
    public void Apply(Color32[] buffer)
    {
        var lut = LutOrNull;
        if (lut == null || buffer == null) return;

        for (int i = 0; i < buffer.Length; i++)
        {
            var p = buffer[i];
            buffer[i] = new Color32(lut[p.r], lut[p.g], lut[p.b], p.a);
        }
    }

    // ── 自動レベルの計測 ──
    //
    // ⚠️ 呼び出し側は「生画素」を渡すこと（Apply より前）。
    //   補正後の画素を測ると、次のフレームの目標値が補正結果に引かれて発散する。
    //
    // 0.25秒に1回だけ実際に計測する。呼び出し自体は毎フレーム行ってよい
    // （DetectFrozenFrame と同じ、間引きは内部で持つ形）。
    private const float SampleIntervalSeconds = 0.25f;
    private const int   TargetSampleCount     = 20000;
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
    private readonly int[] _histogram = new int[LumaBins];
    private float _lastMeasureTime = float.NegativeInfinity;

    /// <summary>
    /// 生画素から白飛び率・平均輝度・自動レベルの目標値を更新する。
    /// toneEnabled/autoLevels の ON/OFF に関係なく常に計算する
    /// （管理画面のヘルプ行に白飛び率を常時出したいため。補正 OFF でも診断はできる）。
    /// </summary>
    public void Measure(Color32[] raw, float now)
    {
        if (raw == null || raw.Length == 0) return;

        float elapsed = now - _lastMeasureTime;
        if (elapsed < SampleIntervalSeconds) return;

        float dt = Mathf.Clamp(elapsed, SampleIntervalSeconds, MaxSmoothingDt);
        _lastMeasureTime = now;

        // 全画素を見る必要はない。等間隔に間引いて畳み込む（DetectFrozenFrame と同じ考え方）
        int stride = Mathf.Max(1, raw.Length / TargetSampleCount);

        System.Array.Clear(_histogram, 0, LumaBins);
        int  sampled  = 0;
        long lumaSum  = 0;
        int  clipCount = 0;

        for (int i = 0; i < raw.Length; i += stride)
        {
            var p = raw[i];
            // BT.601相当の輝度近似。÷256 をシフトで済ませる固定小数（77+150+29=256）
            int luma = (p.r * 77 + p.g * 150 + p.b * 29) >> 8;
            _histogram[luma]++;
            lumaSum += luma;
            if (luma >= 250) clipCount++;
            sampled++;
        }

        if (sampled == 0) return;

        MeanLuma  = lumaSum / (float)sampled / 255f;
        ClipRatio = clipCount / (float)sampled;

        float targetBlack = PercentileLuma(sampled, LowerPercentile) / 255f;
        float targetWhite = PercentileLuma(sampled, UpperPercentile) / 255f;

        targetBlack = Mathf.Min(targetBlack, MaxAutoBlack);
        targetWhite = Mathf.Max(targetWhite, targetBlack + MinAutoRange);

        float alpha = 1f - Mathf.Exp(-dt / SmoothingTimeConstant);
        _autoBlack = Mathf.Lerp(_autoBlack, targetBlack, alpha);
        _autoWhite = Mathf.Lerp(_autoWhite, targetWhite, alpha);
    }

    // ヒストグラムの下から積算して、累積が sampled*fraction に達した輝度を返す
    private int PercentileLuma(int sampled, float fraction)
    {
        int threshold  = Mathf.CeilToInt(sampled * fraction);
        int cumulative = 0;
        for (int i = 0; i < _histogram.Length; i++)
        {
            cumulative += _histogram[i];
            if (cumulative >= threshold) return i;
        }
        return _histogram.Length - 1;
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
    // ⚠️ _autoBlack/_autoWhite はここに無い。自動が計算した値は実行時のみで、
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

        Debug.Log($"[Tone] 明るさ補正 初期化完了（補正={toneEnabled} / 自動={autoLevels} / " +
                  $"黒={blackLevel:F2} / 白={whiteLevel:F2} / ガンマ={gamma:F2}）");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
