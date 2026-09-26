using UnityEngine;

// ===== ExperienceDirector =====
// 体験設計（子どもを中心に、全年齢が楽しめる体験設計）の中核。
// PoseEventBus からジェスチャーを受け取り、コンボ・アンサンブルを踏まえて
// 実際に打ち上げる LaunchRequest を組み立てる。
//
// ── 最終的なスコープ（縮小版）──
//   当初のプランには「無人時の自動打ち上げ（アトラクト）」「お手本の表示」
//   「フィナーレ」「レア型＋隠し操作」も含まれていたが、これらは実装しない方針に
//   確定した。実装するのは次の4つだけ:
//     1. 判定を子どもの体に合わせる（GestureDetector 側で対応済み）
//     2. 花火をその人の位置から打つ（FireworkLauncher.ResolveCenterU で対応済み）
//     3. コンボ（連打を「育てる」。数字表示・光跡の成長は個別ON/OFF可）
//     4. 一緒に（アンサンブル）
//   そのため「誰か居るか」を集計する PresenceTracker や Vacant/Attract/Occupied の
//   状態機械は不要と判断して持たない。コンボ・アンサンブルはどちらも
//   「人ごとの直近の振る舞い」だけで完結する仕組みなので、在席状況を知らなくても動く。
//
// ── なぜ Director に集約するか ──
//   OnGestureDetected は1ジェスチャーにつき1回しか飛ばないが、コンボとアンサンブルの
//   両方がそれぞれ独立に判定ロジックを持つ。ここでまとめて受けて両方に流すことで、
//   将来どちらかを増改築しても購読側を増やさずに済む。
//
// ── SpaceModeController / UfoSpawner と同じ作法にしてある理由 ──
//   このリポジトリで「シーンに置かなくても動く常駐コントローラ」は既にこの2つがあり、
//   同じ形（masterEnabled && xxxEnabled の2層、GetOrCreate + RuntimeInitializeOnLoadMethod
//   での自己生成）に揃えておくことで、Admin UI 側の配線も既存の書き方をそのまま踏襲できる。
//
// ── 唯一の購読者ではない点について ──
//   masterEnabled が ON の間は、ここが個々の花火を FireworkPlan.Individual／Ensemble を
//   通じて打つ「唯一の」経路になる（RoutesGestures を FireworkLauncher.OnGestureDetected が
//   確認し、二重発火を避ける）。OFF のときは FireworkLauncher が従来経路でそのまま打つ
//  （回帰確認用の逃げ道）。
public class ExperienceDirector : MonoBehaviour
{
    public static ExperienceDirector Instance { get; private set; }

    [Header("マスター")]
    [Tooltip("体験演出の大元のON/OFF。OFFのときは他の個別設定がONでも実効値は全てOFFになる。\n" +
             "OFF中はジェスチャーからの花火は FireworkLauncher の従来経路がそのまま動く\n" +
             "（回帰確認用の逃げ道。プラン「検証」参照）。\n" +
             "既定でON: コンボ・アンサンブルが本実装の主目的なので、Admin UIが揃った時点で\n" +
             "既定を有効側に倒す。現場で問題があれば体験タブの「体験演出」でOFFにできる")]
    [SerializeField] private bool masterEnabled = true;

    [Header("コンボ")]
    [Tooltip("個別設定。OFFのときは常に段階1（従来どおり）として扱う")]
    [SerializeField] private bool comboEnabled = true;

    [Tooltip("昇りの光跡をコンボ段階で育てるかどうか（数字表示は別設定）")]
    [SerializeField] private bool comboTrailEnabled = true;

    [Tooltip("コンボの段階を「2!」のような数字でポップ表示するかどうか（光跡は別設定）。\n" +
             "ComboNumberOverlay がこの設定を見て表示するかどうかを決める")]
    [SerializeField] private bool comboNumberEnabled = true;

    [Tooltip("同一人物とみなす連続ジェスチャーの間隔（秒）。これを超えて間が空くとコンボが切れる")]
    [SerializeField] private float comboWindowSeconds = 2.0f;

    [Tooltip("この回数に達するとフィニッシャー（段階5）として発火し、0から数え直す")]
    [SerializeField] private int comboFinisherCount = 5;

    [Tooltip("コンボ段階2・3で使う型（空なら通常の抽選）")]
    [SerializeField] private string[] comboLightNames = { "牡丹", "花雷" };

    [Tooltip("コンボ段階4で使う型（空なら通常の抽選）")]
    [SerializeField] private string[] comboBigNames = { "菊", "変化菊" };

    [Tooltip("コンボ段階5（フィニッシャー）で使う型（空なら通常の抽選）")]
    [SerializeField] private string[] comboFinisherNames = { "千輪菊", "芯入り菊", "冠" };

    [Header("一緒に（アンサンブル）")]
    [Tooltip("個別設定。OFFのときは複数人が揃っても特別な演出は起きない")]
    [SerializeField] private bool ensembleEnabled = true;

    [Tooltip("ON: 片手上げ／両手上げを同じジェスチャーとみなして揃える。\n" +
             "親子で片手／両手が揃わないことが多いための救済")]
    [SerializeField] private bool treatAnyHandUpAsSame = true;

    [Tooltip("この秒数以内に同じジェスチャーをした人をまとめて「一緒」とみなす")]
    [SerializeField] private float ensembleWindowSeconds = 0.8f;

    [Tooltip("2人揃ったときに使う型（空なら通常の抽選）")]
    [SerializeField] private string[] ensembleShellNames = { "型物・ハート", "芯入り菊" };

    [Header("参照")]
    [Tooltip("未設定ならシーンから自動で探す（FindFirstObjectByType）")]
    [SerializeField] private FireworkLauncher launcher;

    // ── 実効値（呼び出し側は必ずこちらを見る）──
    public bool MasterEnabled      => masterEnabled;
    public bool ComboEnabled       => masterEnabled && comboEnabled;
    public bool ComboTrailEnabled  => masterEnabled && comboTrailEnabled;
    public bool ComboNumberEnabled => masterEnabled && comboNumberEnabled;
    public bool EnsembleEnabled    => masterEnabled && ensembleEnabled;

    /// <summary>true の間、FireworkLauncher は自前でジェスチャーに反応しない
    /// （Director がここで個々の花火を打つため）</summary>
    public bool RoutesGestures => masterEnabled;

    // 個別設定そのもの（UIがラベル表示用に使う。Master OFF中でも「戻したときに
    // 何が有効になるか」を見せるため公開する。SpaceModeController と同じ考え方）
    public bool ComboSetting       => comboEnabled;
    public bool ComboTrailSetting  => comboTrailEnabled;
    public bool ComboNumberSetting => comboNumberEnabled;
    public bool EnsembleSetting    => ensembleEnabled;

    /// <summary>コンボが進んだ瞬間に発火する（trackId, 段階, 位置）。ComboNumberOverlay が購読する。
    /// 段階1を出すかどうか・ComboNumberEnabled を見るかどうかは購読側（Overlay）の判断に委ねてある</summary>
    public event System.Action<int, int, Vector2> OnComboAdvanced;

    private ComboTracker    _combo;
    private GestureEnsemble _ensemble;
    private bool  _subscribed;

    /// <summary>シーンにあればそれを、無ければ自動生成して返す</summary>
    public static ExperienceDirector GetOrCreate()
    {
        if (Instance != null) return Instance;

        var found = FindFirstObjectByType<ExperienceDirector>();
        if (found != null) return found;

        var go = new GameObject("ExperienceDirector");
        return go.AddComponent<ExperienceDirector>();
    }

    // UfoSpawner / SpaceModeController と同じく、シーンに置かなくても動くようにする。
    // アトラクトが無くなった今でも、Admin UI を一度も開かずにジェスチャーだけ行われた
    // 場合にコンボ・アンサンブルが効くよう、起動時に必ず存在させておく必要がある
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        GetOrCreate();
    }

    // ── 永続化 ──
    private const string KeyMaster      = nameof(ExperienceDirector) + "." + nameof(masterEnabled);
    private const string KeyCombo       = nameof(ExperienceDirector) + "." + nameof(comboEnabled);
    private const string KeyComboTrail  = nameof(ExperienceDirector) + "." + nameof(comboTrailEnabled);
    private const string KeyComboNumber = nameof(ExperienceDirector) + "." + nameof(comboNumberEnabled);
    private const string KeyEnsemble    = nameof(ExperienceDirector) + "." + nameof(ensembleEnabled);

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        masterEnabled      = SettingsStore.GetBool(KeyMaster, masterEnabled);
        comboEnabled       = SettingsStore.GetBool(KeyCombo, comboEnabled);
        comboTrailEnabled  = SettingsStore.GetBool(KeyComboTrail, comboTrailEnabled);
        comboNumberEnabled = SettingsStore.GetBool(KeyComboNumber, comboNumberEnabled);
        ensembleEnabled    = SettingsStore.GetBool(KeyEnsemble, ensembleEnabled);

        _combo    = new ComboTracker(comboWindowSeconds, comboFinisherCount);
        _ensemble = new GestureEnsemble(ensembleWindowSeconds, treatAnyHandUpAsSame: treatAnyHandUpAsSame);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void SetMaster(bool on) { masterEnabled = on; SettingsStore.SetBool(KeyMaster, on); }
    public void ToggleMaster()      => SetMaster(!masterEnabled);
    public void ToggleCombo()       { comboEnabled       = !comboEnabled;       SettingsStore.SetBool(KeyCombo, comboEnabled); }
    public void ToggleComboTrail()  { comboTrailEnabled  = !comboTrailEnabled;  SettingsStore.SetBool(KeyComboTrail, comboTrailEnabled); }
    public void ToggleComboNumber() { comboNumberEnabled = !comboNumberEnabled; SettingsStore.SetBool(KeyComboNumber, comboNumberEnabled); }
    public void ToggleEnsemble()    { ensembleEnabled    = !ensembleEnabled;    SettingsStore.SetBool(KeyEnsemble, ensembleEnabled); }

    // ── イベント購読 ──
    // SkeletonRenderer.TrySubscribe と同じ形。Unity は Awake→OnEnable をオブジェクトごとに
    // 順に呼ぶため、OnEnable の時点では PoseEventBus.Awake() がまだ走っておらず
    // Instance が null になりうる。全 Awake 完了後に必ず走る Start でも再試行する
    private void OnEnable() => TrySubscribe();
    private void Start()    => TrySubscribe();

    private void TrySubscribe()
    {
        if (_subscribed) return;

        var bus = PoseEventBus.Instance;
        if (bus == null) return; // Start でもう一度試す

        bus.OnPersonLost      += OnPersonLost;
        bus.OnGestureDetected += OnGestureDetected;
        _subscribed = true;

        if (launcher == null)
            launcher = FindFirstObjectByType<FireworkLauncher>();
    }

    private void OnDisable()
    {
        if (!_subscribed) return;

        var bus = PoseEventBus.Instance;
        if (bus != null)
        {
            bus.OnPersonLost      -= OnPersonLost;
            bus.OnGestureDetected -= OnGestureDetected;
        }
        _subscribed = false;
    }

    private void OnPersonLost(int trackId)
    {
        // 即削除せず、少しの間だけ孤児として保持する（振り直し対策。ComboTracker.Orphan 参照）
        _combo.Orphan(trackId, Time.time);
    }

    // ジェスチャー1件ぶんの打ち上げをここで組み立てて打つ
    // （データフローはプラン「アーキテクチャ」参照）。
    // RoutesGestures=true の間、FireworkLauncher 側は同じジェスチャーに反応しない
    private void OnGestureDetected(int trackId, GestureType gesture, Vector2 normalizedPos)
    {
        float now = Time.time;

        int stage = ComboEnabled ? _combo.Register(trackId, normalizedPos, now) : 1;

        if (EnsembleEnabled)
            _ensemble.Register(trackId, gesture, normalizedPos, now);

        int trailStage = ComboTrailEnabled ? stage : 0;

        var requests = FireworkPlan.Individual(
            gesture, stage, normalizedPos,
            launcher != null ? launcher.PairSeparationViewport : 0.18f,
            launcher != null ? launcher.JumpSpreadViewport     : 0.15f,
            trailStage, comboLightNames, comboBigNames, comboFinisherNames);

        for (int i = 0; i < requests.Length; i++)
            launcher?.Launch(requests[i]);

        // ── 発射会計 ──
        // 「ジェスチャー1回に対して実際に何発上がったか」を1行で残す。
        //
        // 発数はジェスチャーの種類だけでは決まらない。段階1の両手上げは大玉2発、
        // 段階2〜4は1発、段階5（フィニッシャー）は2発、さらにアンサンブルが
        // 別枠で上乗せされる（下の Update 側のログ）。
        // 「1回のジェスチャーなのに花火が多い」と感じたときに、
        // どの要素が何発足しているのかをログだけで切り分けられるようにしておく
        Debug.Log($"[体験] P{trackId} {gesture} → 個人 {requests.Length}発 " +
                  $"（コンボ段階 {stage}{(ComboEnabled ? "" : " ※コンボOFF")}）");

        OnComboAdvanced?.Invoke(trackId, stage, normalizedPos);
    }

    // ── アンサンブルの発火 ──
    // GestureEnsemble.Register は保留（Pending）を作るだけで、実際の発火は
    // collectDelay 秒だけ待ってからここで行う（3人目を待てるようにするため）
    private void Update()
    {
        float now = Time.time;

        _combo.Sweep(now);

        if (!EnsembleEnabled) return;
        if (!_ensemble.TryFire(now, out var fireResult)) return;

        var shots = FireworkPlan.Ensemble(fireResult.level, fireResult.center, ensembleShellNames);
        for (int i = 0; i < shots.Length; i++)
            launcher?.Launch(shots[i]);

        // アンサンブルは個人ぶんの「上乗せ」で、参加者それぞれの花火は既に上がっている。
        // 3人以上ではミニスターマイン（3〜5発）になるので上乗せの量が一気に増える。
        // 発射会計として個人ぶんと同じ粒度で残す（上の OnGestureDetected 側のログ参照）
        Debug.Log($"[体験] いっしょに {fireResult.level}人 → 追加 {shots.Length}発" +
                  "（各自の個人ぶんとは別に上乗せされる）");
    }
}
