using UnityEngine;

// ===== DopamineModeController =====
// 「ドーパミンモード」の状態を一元管理する。
// ON の間だけ、花火を虹色に光らせ、検出のしきい値を最小にし、
// 音をパチンコの先バレ音・大当たり音に差し替える。
//
// 通常運用のための機能ではなく、盛り上げたい時間帯だけ管理画面から入れる飛び道具。
// 設計の全体像は docs/plans/2026-09-dopamine-mode.md を参照。
//
// ── 最重要の制約 ──
//   OFF に戻したとき、通常の設定が一切汚れていないこと。
//
//   展示は複数日にまたがり、現場で調整した判定のしきい値が PlayerPrefs に蓄積されている。
//   このモードがその値を書き換えて戻す方式だと、ON のまま電源が落ちた翌日、
//   緩めきった状態で開場する。この事故の損害は、もう一度 ON にする手間とは比べものにならない。
//
//   そこで「上書きして戻す」を一切やらず、「実効値を毎回引く」に統一してある。
//   このクラスは値を誰にも配らないし、誰も購読しない。引かれるだけ。
//     ・GestureDetector  … 実効値プロパティ層が毎フレーム引く
//     ・RainbowTint      … 1発の Launch 時にスナップショット、位相だけ毎フレーム1回
//     ・FireworkLauncher … 発射の瞬間に音の上書き先を決める
//
//   戻す処理が存在しなければ、戻し忘れも、戻す前に落ちる事故も、原理的に起きない。
//
// ── SpaceModeController / ExperienceDirector と同じ形にしてある理由 ──
//   このリポジトリで「マスター＋個別設定」のモードは既に2つあり、構造がほぼ一致している。
//   3つ目も同じ形（Instance / masterEnabled && 個別 の2層 / XxxSetting / SettingsStore /
//   GetOrCreate + RuntimeInitializeOnLoadMethod）に揃えておけば、
//   Admin UI 側の配線も既存の書き方をそのまま踏襲できる。新しく考える設計を持ち込まない。
public class DopamineModeController : MonoBehaviour
{
    public static DopamineModeController Instance { get; private set; }

    [Header("マスター")]
    [Tooltip("ドーパミンモードの大元のON/OFF。OFFのときは他の3要素がONでも実効値は全てOFFになる。\n" +
             "\n" +
             "⚠️ この値だけは毎起動 OFF から始まる（個別設定は復元される）。\n" +
             "宇宙モードは「その会場の構成」なので前回の状態を引き継ぐのが正しいが、\n" +
             "こちらは「その瞬間の出し物」。しきい値最小＋クールダウン0＋大当たり音のまま\n" +
             "翌日の開場を迎える事故のほうが、もう一度ONにする手間より重い")]
    [SerializeField] private bool masterEnabled = false;

    [Header("個別設定（マスターOFF中も保持される）")]
    [Tooltip("花火を虹色に光らせる。粒ごとに虹色が並び、時間で色相が回る")]
    [SerializeField] private bool rainbowEnabled = true;

    [Tooltip("打ち上げ音を先バレ音に、開花音を大当たり音に差し替える")]
    [SerializeField] private bool dopamineAudioEnabled = true;

    [Tooltip("検出のしきい値を最小にし、クールダウンを外す。\n" +
             "ジェスチャーの種類判定そのものは従来どおり残る")]
    [SerializeField] private bool looseDetectionEnabled = true;

    // ── 虹色の見た目 ──
    [Header("虹色")]
    [Tooltip("色相が1周する速さ（周/秒）。0 にすると回転が止まり、\n" +
             "1発の中での色相の散らばりだけが残る")]
    [SerializeField, Range(0f, 3f)] private float hueCycleHz = 0.35f;

    [Tooltip("虹の濃さ。1.0 にしないのは、実物の星も完全な純色ではなく\n" +
             "白が少し混ざっていたほうが「燃えている」ように見えるため")]
    [SerializeField, Range(0f, 1f)] private float saturation = 0.85f;

    [Tooltip("1発の中で粒ごとの色相がどれだけ散らばるか。\n" +
             "0 なら1発1色、1 なら1発で全色が出る")]
    [SerializeField, Range(0f, 1f)] private float hueSpread = 0.55f;

    [Tooltip("彩度を上げると輝度が落ちる（菊の (1, 0.82, 0.40) は平均0.74 だが、\n" +
             "飽和した青 (0,0,1) は 0.33）。同じ _Intensity のままだと虹色のほうが\n" +
             "暗く見えるので、モード中だけ発光の天井を持ち上げる")]
    [SerializeField, Range(1f, 3f)] private float intensityBoost = 1.25f;

    [Tooltip("画像花火の彩度をどれだけ持ち上げるか。\n" +
             "白飛びした肌や紙は彩度がほぼ0で、色相をいくら回しても白のまま。\n" +
             "少しだけ色を乗せると虹が乗る。上げすぎると絵が塗り絵になる")]
    [SerializeField, Range(0f, 1f)] private float imageSaturationLift = 0.5f;

    // ── 判定の緩和 ──
    //
    // ⚠️ ここは「GestureDetector の値を書き換えるための一時退避」ではない。
    //    このモードが独立して持つ、もう1組の設定値。
    //    GestureDetector 側は読む先をこちらに切り替えるだけで、
    //    自分の保存値には最後まで触らない。
    //
    // 「最小」を定数ではなく設定項目にしたのは、最小が会場によって違うため。
    // カメラが遠ければ 0.15 でも厳しく、近ければ緩すぎる。現場で振り切れるようにしておく。
    [Header("判定の緩和")]
    [Tooltip("連発防止の秒数。0 で無制限。\n" +
             "0 でも同一フレームでは撃てない（CanFire が (now - last) > 0 を見るため）ので、\n" +
             "1フレーム1発は構造的に維持される")]
    [SerializeField, Range(0f, 2f)] private float relaxCooldown = 0f;

    [Tooltip("手上げのきびしさ（肩幅比）")]
    [SerializeField, Range(0.05f, 1f)] private float relaxHandUpThreshold = 0.15f;

    [Tooltip("ジャンプの高さ（肩幅比）")]
    [SerializeField, Range(0.05f, 1f)] private float relaxJumpRiseThreshold = 0.12f;

    [Tooltip("ポーズの保持時間[秒]。0 で即発火")]
    [SerializeField, Range(0f, 1f)] private float relaxPoseHold = 0f;

    [Tooltip("片手上げだけに足す追加の保持時間[秒]。0 にすると片手が両手を待たなくなる")]
    [SerializeField, Range(0f, 1f)] private float relaxOneHandExtraHold = 0f;

    [Tooltip("ジャンプの再武装までの最短間隔[秒]。通常は 0.4 固定")]
    [SerializeField, Range(0f, 1f)] private float relaxJumpRearm = 0.15f;

    // ── 実効値（呼び出し側は必ずこちらを見る。&& の書き忘れを構造的に防ぐ）──
    public bool MasterEnabled         => masterEnabled;
    public bool RainbowEnabled        => masterEnabled && rainbowEnabled;
    public bool AudioEnabled          => masterEnabled && dopamineAudioEnabled;
    public bool LooseDetectionEnabled => masterEnabled && looseDetectionEnabled;

    // 個別設定そのもの（UI がラベル表示用に使う。マスターOFF中でも
    // 「戻したときに何が有効になるか」を見せるため公開する）
    public bool RainbowSetting        => rainbowEnabled;
    public bool AudioSetting          => dopamineAudioEnabled;
    public bool LooseDetectionSetting => looseDetectionEnabled;

    // ── 虹色のパラメータ ──
    public float Saturation          => saturation;
    public float HueSpread           => hueSpread;
    public float IntensityBoost      => intensityBoost;
    public float ImageSaturationLift => imageSaturationLift;

    /// <summary>色相が1周する速さ（周/秒）。Admin のスライダーから調整する</summary>
    public float HueCycleHz
    {
        get => hueCycleHz;
        set { hueCycleHz = value; SettingsStore.SetFloat(KeyHueHz, value); Changed(); }
    }

    // ── 判定の緩和のパラメータ ──
    public float RelaxOneHandExtraHold => relaxOneHandExtraHold;
    public float RelaxJumpRearm        => relaxJumpRearm;
    public float RelaxPoseHold         => relaxPoseHold;

    public float RelaxCooldown
    {
        get => relaxCooldown;
        set { relaxCooldown = value; SettingsStore.SetFloat(KeyCooldown, value); Changed(); }
    }

    public float RelaxHandUpThreshold
    {
        get => relaxHandUpThreshold;
        set { relaxHandUpThreshold = value; SettingsStore.SetFloat(KeyHandUp, value); Changed(); }
    }

    public float RelaxJumpRiseThreshold
    {
        get => relaxJumpRiseThreshold;
        set { relaxJumpRiseThreshold = value; SettingsStore.SetFloat(KeyJump, value); Changed(); }
    }

    // ── 色相の現在位置（0..1）──
    //
    // ── なぜ Time.time を掛け算せず、位相を積分するのか ──
    //   1. 展示を何時間も回すと Time.time が大きくなり、frac の精度が落ちて
    //      色相の刻みが目に見えて粗くなる。0..1 に巻き戻せばこの問題が消える
    //   2. 速さのスライダーを動かした瞬間に色が飛ばない。
    //      Time.time * hz だと hz を変えた瞬間に位相が不連続に跳ぶが、
    //      積分していれば速さだけが変わって色は連続に繋がる。
    //      本番中に管理画面でスライダーを触る前提なので、これは実用上かなり効く
    public float HuePhase { get; private set; }

    /// <summary>マスター・個別トグル・数値のどれかが変わった。
    /// Admin UI がラベルを再計算するために購読する</summary>
    public event System.Action OnChanged;

    private void Changed() => OnChanged?.Invoke();

    /// <summary>シーンにあればそれを、無ければ自動生成して返す</summary>
    public static DopamineModeController GetOrCreate()
    {
        if (Instance != null) return Instance;

        var found = FindFirstObjectByType<DopamineModeController>();
        if (found != null) return found;

        var go = new GameObject(nameof(DopamineModeController));
        return go.AddComponent<DopamineModeController>();
    }

    // SpaceModeController / ExperienceDirector と同じく、シーンに置かなくても動くようにする。
    // Admin 画面を一度も開かずにジェスチャーだけ行われた場合でも、
    // GestureDetector と各エフェクトが Instance を引きに来るので、起動時に必ず存在させておく
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        GetOrCreate();
    }

    // ── 永続化 ──
    private const string KeyMaster   = nameof(DopamineModeController) + "." + nameof(masterEnabled);
    private const string KeyRainbow  = nameof(DopamineModeController) + "." + nameof(rainbowEnabled);
    private const string KeyAudio    = nameof(DopamineModeController) + "." + nameof(dopamineAudioEnabled);
    private const string KeyLoose    = nameof(DopamineModeController) + "." + nameof(looseDetectionEnabled);
    private const string KeyHueHz    = nameof(DopamineModeController) + "." + nameof(hueCycleHz);
    private const string KeyCooldown = nameof(DopamineModeController) + "." + nameof(relaxCooldown);
    private const string KeyHandUp   = nameof(DopamineModeController) + "." + nameof(relaxHandUpThreshold);
    private const string KeyJump     = nameof(DopamineModeController) + "." + nameof(relaxJumpRiseThreshold);

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        rainbowEnabled         = SettingsStore.GetBool(KeyRainbow, rainbowEnabled);
        dopamineAudioEnabled   = SettingsStore.GetBool(KeyAudio,   dopamineAudioEnabled);
        looseDetectionEnabled  = SettingsStore.GetBool(KeyLoose,   looseDetectionEnabled);
        hueCycleHz             = SettingsStore.GetFloat(KeyHueHz,    hueCycleHz);
        relaxCooldown          = SettingsStore.GetFloat(KeyCooldown, relaxCooldown);
        relaxHandUpThreshold   = SettingsStore.GetFloat(KeyHandUp,   relaxHandUpThreshold);
        relaxJumpRiseThreshold = SettingsStore.GetFloat(KeyJump,     relaxJumpRiseThreshold);

        // ── マスターだけは毎起動 OFF から始める ──
        //   前回の状態を引き継ぐ価値より、ONのまま開場する事故のほうが重い（クラス冒頭参照）。
        //   保存もしておくのは、Admin 側が SettingsStore を読んで判断する場面が
        //   将来できたときに、保存値と実体が食い違わないようにするため
        masterEnabled = false;
        SettingsStore.SetBool(KeyMaster, false);

        // 「昨日ONにしたのに」と現場で混乱しないよう、必ず1行残す
        Debug.Log("[Dopamine] ドーパミンモードは OFF から開始しました（マスターは毎起動リセットされます）。" +
                  $"個別設定は復元済み: 虹色={rainbowEnabled} / 音={dopamineAudioEnabled} / 検出ゆるめ={looseDetectionEnabled}");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        // 色相を回すのはモードが有効なときだけでよいが、止めると
        // ONにした瞬間に前回の位相から飛ぶ。常に回しておけば、
        // いつONにしても色が連続して見える（コストは1フレーム1回の加算のみ）
        HuePhase = Mathf.Repeat(HuePhase + Time.deltaTime * hueCycleHz, 1f);
    }

    // ── Admin 画面からの操作 ──

    public void SetMaster(bool on)
    {
        masterEnabled = on;
        SettingsStore.SetBool(KeyMaster, on);

        // この1操作で展示の挙動が大きく変わるので、必ず記録を残す
        Debug.Log($"[Dopamine] ドーパミンモード: {(on ? "ON" : "OFF")}" +
                  $"（虹色={RainbowEnabled} / 音={AudioEnabled} / 検出ゆるめ={LooseDetectionEnabled}）");
        Changed();
    }

    public void ToggleMaster() => SetMaster(!masterEnabled);

    public void ToggleRainbow()
    {
        rainbowEnabled = !rainbowEnabled;
        SettingsStore.SetBool(KeyRainbow, rainbowEnabled);
        Changed();
    }

    public void ToggleAudio()
    {
        dopamineAudioEnabled = !dopamineAudioEnabled;
        SettingsStore.SetBool(KeyAudio, dopamineAudioEnabled);
        Changed();
    }

    public void ToggleLooseDetection()
    {
        looseDetectionEnabled = !looseDetectionEnabled;
        SettingsStore.SetBool(KeyLoose, looseDetectionEnabled);
        Changed();
    }
}
