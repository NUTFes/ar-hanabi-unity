using UnityEngine;

// ===== SpaceModeController =====
// 別の大学祭（宇宙船／宇宙テーマ）向けの「宇宙モード」の状態を一元管理する。
//
// ── なぜコントローラを1つに集約したか ──
//   SelfieSegmentationController が「有効/無効の真実はコントローラが持ち、
//   ボタンは叩くだけ」という作りになっているのに倣った。
//   フレーム・UFO・花火・音の4要素がそれぞれ別の場所に状態を持つと、
//   「宇宙モードOFF中に個別トグルを触ったらどうなるか」のような組み合わせ爆発が
//   コード各所に散らばってしまう。ここに集めておけば、他のスクリプトは
//   FrameEnabled 等の「実効値」を読むだけでよい。
//
// ── Master OFF でも個別設定を覚えている理由 ──
//   本番中に「今日はUFOだけ切りたい」と個別に落としたあと、宇宙モードごと
//   OFF→ONし直しても、さっき切ったUFOだけがOFFのまま戻ってきてほしい。
//   個別フラグは Master の状態に関係なく独立して保持し、
//   実効値プロパティ側で Master && 個別 を返す。
//
// ── シーンに置かなくても動く ──
//   AdminUIManager.cameraBackground と同じ方針で、Instance が無ければ
//   自動生成する。シーン編集を必須にしないため。
public class SpaceModeController : MonoBehaviour
{
    public static SpaceModeController Instance { get; private set; }

    /// <summary>花火の出し分け。HANABI ボタンでこの順に循環する</summary>
    public enum SpaceFireworkMode
    {
        /// <summary>既存の11種のみ（宇宙型は出さない）</summary>
        Off,
        /// <summary>既存＋宇宙型を混ぜる</summary>
        Mix,
        /// <summary>宇宙型のみ</summary>
        SpaceOnly,
    }

    [Tooltip("宇宙モードの大元のON/OFF。OFFのときは他の4要素がONでも実効値は全てOFFになる")]
    [SerializeField] private bool masterEnabled = false;

    [Tooltip("フレーム・UFO・花火・音、それぞれの個別設定（Master OFF中も保持される）")]
    [SerializeField] private bool frameEnabled = true;
    [SerializeField] private bool ufoEnabled = true;
    [SerializeField] private SpaceFireworkMode fireworkMode = SpaceFireworkMode.Mix;
    [SerializeField] private bool spaceAudioEnabled = true;

    // ── 実効値（呼び出し側は必ずこちらを見る。&& の書き忘れを構造的に防ぐ）──
    public bool MasterEnabled => masterEnabled;
    public bool FrameEnabled  => masterEnabled && frameEnabled;
    public bool UfoEnabled    => masterEnabled && ufoEnabled;
    public SpaceFireworkMode FireworkMode => masterEnabled ? fireworkMode : SpaceFireworkMode.Off;
    public bool SpaceAudioEnabled => masterEnabled && spaceAudioEnabled;

    // 個別設定そのもの（UI がラベル表示用に "ON/OFF" を出し分けるのに使う。
    // Master OFF 中でも「戻したときに何が有効になるか」をUIに見せるため公開する）
    public bool FrameSetting => frameEnabled;
    public bool UfoSetting   => ufoEnabled;
    public SpaceFireworkMode FireworkSetting => fireworkMode;
    public bool SpaceAudioSetting => spaceAudioEnabled;

    /// <summary>シーンにあればそれを、無ければ自動生成して返す</summary>
    public static SpaceModeController GetOrCreate()
    {
        if (Instance != null) return Instance;

        var found = FindFirstObjectByType<SpaceModeController>();
        if (found != null) return found;

        var go = new GameObject("SpaceModeController");
        return go.AddComponent<SpaceModeController>();
    }

    // ── 永続化 ──
    // 展示は複数セッション・複数日にまたがって電源を落とすため、Admin画面で
    // 触った5つの値は PlayerPrefs 経由で次回起動時にも引き継ぐ（SettingsStore 参照）。
    // ここは bool 3つと enum 1つで「範囲外になって起動が壊れる」ような値が無いため
    // （CameraBackgroundController の webcamIndex のように、保存先の台数構成が
    //  変わると起動時に何も開けなくなる、といった機種依存のリスクが無い）、
    // 単純にそのまま保存・復元してよい
    private const string KeyMaster = nameof(SpaceModeController) + "." + nameof(masterEnabled);
    private const string KeyFrame  = nameof(SpaceModeController) + "." + nameof(frameEnabled);
    private const string KeyUfo    = nameof(SpaceModeController) + "." + nameof(ufoEnabled);
    private const string KeyMode   = nameof(SpaceModeController) + "." + nameof(fireworkMode);
    private const string KeyAudio  = nameof(SpaceModeController) + "." + nameof(spaceAudioEnabled);

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        masterEnabled     = SettingsStore.GetBool(KeyMaster, masterEnabled);
        frameEnabled      = SettingsStore.GetBool(KeyFrame, frameEnabled);
        ufoEnabled        = SettingsStore.GetBool(KeyUfo, ufoEnabled);
        fireworkMode      = (SpaceFireworkMode)SettingsStore.GetInt(KeyMode, (int)fireworkMode);
        spaceAudioEnabled = SettingsStore.GetBool(KeyAudio, spaceAudioEnabled);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void SetMaster(bool on) { masterEnabled = on; SettingsStore.SetBool(KeyMaster, on); }
    public void ToggleMaster() => SetMaster(!masterEnabled);

    public void ToggleFrame() { frameEnabled = !frameEnabled; SettingsStore.SetBool(KeyFrame, frameEnabled); }
    public void ToggleUfo()   { ufoEnabled   = !ufoEnabled;   SettingsStore.SetBool(KeyUfo, ufoEnabled); }
    public void ToggleSpaceAudio() { spaceAudioEnabled = !spaceAudioEnabled; SettingsStore.SetBool(KeyAudio, spaceAudioEnabled); }

    /// <summary>HANABI ボタン用。Off → Mix → SpaceOnly → Off と循環する</summary>
    public void CycleFireworkMode()
    {
        fireworkMode = fireworkMode switch
        {
            SpaceFireworkMode.Off       => SpaceFireworkMode.Mix,
            SpaceFireworkMode.Mix       => SpaceFireworkMode.SpaceOnly,
            SpaceFireworkMode.SpaceOnly => SpaceFireworkMode.Off,
            _                           => SpaceFireworkMode.Off,
        };
        SettingsStore.SetInt(KeyMode, (int)fireworkMode);
    }

    // ── UFOの逃走通知 ──
    // 花火が開いたときに FireworkLauncher から1行呼ぶだけでよい。
    // 宇宙モードが無効、または UfoSpawner が未生成のときは何もしない
    // （null条件演算子で呼ぶ側に負担をかけない設計）。
    public event System.Action<Vector3> OnFireworkBurst;

    public static void NotifyBurst(Vector3 worldPos)
    {
        if (Instance == null || !Instance.UfoEnabled) return;
        Instance.OnFireworkBurst?.Invoke(worldPos);
    }
}
