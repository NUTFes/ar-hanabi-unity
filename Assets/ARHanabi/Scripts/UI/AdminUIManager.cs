using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using System.Collections;
using System.Collections.Generic;

// ===== AdminUIManager =====
// 管理画面 UI を制御するコンポーネント
//
// 画面レイアウト（Canvas 上）:
//   ┌──────────────────────────────────────────────────────┐
//   │  🎆 花火管理                          [終了] [閉じる] │ Header
//   │  ［基本］［宇宙モード］［検出の調整］  全12件 / 有効8件│ TabBar
//   │  （選択中のタブの中身。同時に1枚だけ出る）            │ TabContent
//   │  ▸ タブごとの1行ヘルプ                                │ TabHelpText
//   │  ステータス（2行・接頭辞を色分け）                    │ StatusText
//   ├──────────────────────────────────────────────────────┤
//   │  [thumb] 名前.jpg  変換済み [表示中] [選択]           │ ← エントリ行
//   │  [thumb] 名前2.jpg 未変換   [停止中] [選択]           │
//   │  ...                                                 │
//   └──────────────────────────────────────────────────────┘
//   ※ パネルを閉じている間は画面左上に [開く] タブが出る
//      （AdminPanel の外に置いてあるので CanvasGroup で一緒に消えない）
//
// ── なぜタブ構成にしたか ──
//   展示当日、この画面を触るのは非エンジニアのスタッフでマウス操作。
//   以前は「宇宙モードON中だけ出る行」と「SETTINGS ON中だけ出る行」の2本を
//   折りたたみで足していたため、両方開くと画面の4割がボタンで埋まり、
//   一覧が9行しか見えないうえ「今どこを触っているのか」が分からなくなっていた。
//   同時に1枚しか開かないタブにすると、この2つの問題が構造的に消える。
//   また、宇宙タブは宇宙モードOFFでも開ける（個別スイッチの設定は
//   マスターとは独立に保存されるので、OFF中に仕込んでおける）。
//
// ── 数値の調整をスライダーにした理由 ──
//   以前はボタンを押すたびにプリセットを1つ先へ送る方式だったが、
//   行き過ぎると一周するまで最大8連打が必要で、現場で使いものにならなかった。
//   uGUI の Slider を使うのはこの画面が初なので、部品の生成は
//   AdminUIBuilder.BuildSliderBlock 側に寄せてある。
//
// ── 配色 ──
//   色と寸法は AdminUiStyle が単一ソース。ここに直書きしない。
//   「色が付いている＝有効・注意」（ON=緑 / 危険=赤 / 選択中=濃紺）で意味を固定し、
//   白背景は「通常・OFF側」に固定している。全ての文字/背景の組でコントラスト比 4.5:1 以上。
//
// セットアップ手順:
//   1. Hierarchy: Canvas > AdminPanel を作成
//   2. AdminPanel に AdminUIManager をアタッチ
//   3. Inspector の各フィールドに UI 要素を割り当て
//   4. FireworkManager を同シーンに配置しておく
//   ※ CanvasGroup は Awake() で自動追加されるので手動追加は不要
//
// 表示制御:
//   [閉じる] ボタンは gameObject.SetActive(false) ではなく CanvasGroup の
//   alpha / interactable / blocksRaycasts を落として「見た目だけ」隠す。
//   GameObject を落とすと AdminUIManager 自身が停止し、再表示する手段が
//   どこにも無くなる（かつ API 取得コルーチンも巻き込んで死ぬ）ため。
//   再表示は F1 キー、または外部から SetVisible(true) / ToggleVisible()。
//
// 将来拡張:
//   ・isShareable フィールドの表示

public class AdminUIManager : MonoBehaviour
{
    // ── Inspector ──
    [Header("Main UI")]
    [SerializeField] private Button    testLaunchButton;
    [SerializeField] private Button    closeButton;
    [Tooltip("2回押すとアプリを終了する。[閉じる]は見た目を隠すだけでアプリは終了しない\n" +
             "（F1で再表示できるようにするための意図的な設計）ため、\n" +
             "ビルド後にUnity Editorへ触れない運用ではこのボタンが唯一の終了手段になる")]
    [SerializeField] private Button    quitButton;
    [SerializeField] private TextMeshProUGUI quitText;
    [Tooltip("パネルが閉じている間だけ表示する「開く」ボタン。AdminPanel の外\n" +
             "（AdminCanvas直下）に置く必要がある。AdminPanel の中に置くと\n" +
             "CanvasGroup で一緒に消えてしまい、二度と押せなくなるため")]
    [SerializeField] private Button    openTabButton;
    [SerializeField] private Transform entryListContent;   // ScrollView > Viewport > Content
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("タブ")]
    [Tooltip("3枚のタブ。同時に開くのは1枚だけ。\n" +
             "選択中は濃紺背景＋白文字、非選択は白背景＋濃紺文字（AdminUiStyle）")]
    [SerializeField] private Button           tabBasicButton;
    [SerializeField] private TextMeshProUGUI  tabBasicText;
    [SerializeField] private Button           tabSpaceButton;
    [SerializeField] private TextMeshProUGUI  tabSpaceText;
    [SerializeField] private Button           tabTuneButton;
    [SerializeField] private TextMeshProUGUI  tabTuneText;
    [Tooltip("各タブの中身。SetActive で1枚だけ表示する")]
    [SerializeField] private Transform        tabBasicPage;
    [SerializeField] private Transform        tabSpacePage;
    [SerializeField] private Transform        tabTunePage;
    [Tooltip("選択中のタブに応じて切り替わる1行の説明文")]
    [SerializeField] private TextMeshProUGUI  tabHelpText;
    [Tooltip("タブ行の右端に常設する件数表示。\n" +
             "以前は RefreshList がステータス行を「Entries: N」で毎回上書きしてしまい、\n" +
             "更新やエラーの通知が読む前に消えていた。件数だけをここへ追い出す")]
    [SerializeField] private TextMeshProUGUI  entryCountText;

    [Header("宇宙モード")]
    [SerializeField] private Button           spaceModeButton;
    [SerializeField] private TextMeshProUGUI  spaceModeText;
    [SerializeField] private Button           frameToggleButton;
    [SerializeField] private TextMeshProUGUI  frameToggleText;
    [SerializeField] private Button           ufoToggleButton;
    [SerializeField] private TextMeshProUGUI  ufoToggleText;
    [SerializeField] private Button           hanabiModeButton;
    [SerializeField] private TextMeshProUGUI  hanabiModeText;
    [SerializeField] private Button           sfxToggleButton;
    [SerializeField] private TextMeshProUGUI  sfxToggleText;

    [Header("基本タブのトグル")]
    [SerializeField] private Button           imgEnableButton;
    [SerializeField] private TextMeshProUGUI  imgEnableText;
    [Tooltip("カメラ映像を画面下部の半楕円ドームに収めて周囲を黒くする演出のON/OFF。\n" +
             "OFFにすると、カメラのClearFlags・背景Quadのscale/positionが\n" +
             "すべて元の値に復元される（現場で「あり/なし」を見比べるためのトグル）")]
    [SerializeField] private Button           matteButton;
    [SerializeField] private TextMeshProUGUI  matteText;
    [Tooltip("認識した人の骨格線を描くか。丸窓モードとは独立した設定で、\n" +
             "ドーム表示中でもONならボーンは出る（以前はドーム化が強制的にOFFにしていた）")]
    [SerializeField] private Button           skeletonButton;
    [SerializeField] private TextMeshProUGUI  skeletonText;

    [Header("検出の調整タブ（スライダー）")]
    [Tooltip("ビルド後にUnity Editorへ触れない前提で、展示中に調整したくなる値を\n" +
             "ここへ集約している。値そのものは GestureDetector / FireworkLauncher /\n" +
             "PoseLandmarkDetector 側で PlayerPrefs に永続化される")]
    [SerializeField] private Slider           handUpSlider;
    [SerializeField] private TextMeshProUGUI  handUpText;
    [SerializeField] private Slider           jumpSlider;
    [SerializeField] private TextMeshProUGUI  jumpText;
    [SerializeField] private Slider           cooldownSlider;
    [SerializeField] private TextMeshProUGUI  cooldownText;
    [SerializeField] private Slider           holdSlider;
    [SerializeField] private TextMeshProUGUI  holdText;
    [SerializeField] private Slider           imgChanceSlider;
    [SerializeField] private TextMeshProUGUI  imgChanceText;
    [Tooltip("人として検出するのに必要な確信度。上げると人型のポスターや人形などの\n" +
             "誤検知が減るが、遠い人・暗い場所の人を拾わなくなる。\n" +
             "適用のたびに MediaPipe の PoseLandmarker を作り直す（数十ms）ので、\n" +
             "このスライダーだけは「マウスを離した瞬間に1回だけ」適用する")]
    [SerializeField] private Slider           personConfSlider;
    [SerializeField] private TextMeshProUGUI  personConfText;
    [Tooltip("同時に検出する人数の上限。多いほど一度に多くの人がジェスチャーできるが、\n" +
             "MediaPipe は人数ぶん推論を回すので1フレームの処理時間が伸びる。\n" +
             "人物検出のきびしさと同じく、適用のたびに PoseLandmarker を作り直す")]
    [SerializeField] private Slider           maxPeopleSlider;
    [SerializeField] private TextMeshProUGUI  maxPeopleText;

    [Header("カメラ")]
    [Tooltip("押すたびに次のカメラへ循環切替するボタン")]
    [SerializeField] private Button                     cameraIndexButton;
    [Tooltip("cameraIndexButton 配下のラベル。index・台数・デバイス名を出す")]
    [SerializeField] private TextMeshProUGUI            cameraIndexText;
    [Tooltip("未設定なら Start() でシーンから自動的に探す")]
    [SerializeField] private CameraBackgroundController cameraBackground;

    [Header("API")]
    [Tooltip("DBから新規花火を差分取得する更新ボタン")]
    [SerializeField] private Button refreshButton;

    [Header("Preview (optional)")]
    [SerializeField] private RawImage        previewImage;
    [SerializeField] private TextMeshProUGUI detailText;

    [Header("テスト打ち上げ")]
    [Tooltip("未設定なら Start() でシーンから自動的に探す。\n" +
             "テスト打ち上げは実際のジェスチャーと同じ経路を通すので、\n" +
             "位置・大きさ・開花音がすべて本番と一致する")]
    [SerializeField] private FireworkLauncher fireworkLauncher;

    [Header("表示制御")]
    [Tooltip("起動時にパネルを表示するか")]
    [SerializeField] private bool visibleOnStart = true;
    [Tooltip("F1キーで管理画面の表示/非表示をトグルする（新Input System）")]
    [SerializeField] private bool toggleWithF1 = true;

    [Header("終了ボタンの2段階確認")]
    [Tooltip("1回目のクリックから、この秒数内に再クリックされたら実際にアプリを終了する。\n" +
             "誤タップでキオスクを落とさないための猶予")]
    [SerializeField] private float quitConfirmSeconds = 3f;

    // ── 定数 ──
    // 色・寸法・フォントサイズは AdminUiStyle が単一ソース。
    // 以前はここと AdminUIBuilder の両方に同じ色定数があり、
    // 「片方を変えたらもう片方も手で直すこと」というコメント付きで運用していた（案の定ズレた）。
    private const float ButtonMinWidth = 72f;   // 行のボタンの最小幅（幅自体はラベルに合わせて伸びる）

    // ── 内部 ──
    private FireworkManager     _manager;
    private FireworkLauncher    _launcher;
    private SpaceModeController _spaceMode;
    private GestureDetector     _gesture;
    private CameraCircleMatte   _matte;
    private PoseLandmarkDetector _poseDetector;
    private SkeletonRenderer    _skeleton;
    private FireworkEntry       _selectedEntry;
    private CanvasGroup        _canvasGroup;
    private bool               _isVisible = true;

    // ── タブ ──
    // 宇宙モードのマスターと違い、この値自体は「効き目」を持たない純粋なUI表示切替なので
    // 永続化しない（次回起動時は「基本」から始まってよい）
    private enum AdminTab { Basic, Space, Tune }
    private AdminTab _activeTab = AdminTab.Basic;

    // 終了ボタンの2段階確認。行ごとの確認待ちを持つ削除ボタンと違い
    // アプリ全体につき1つしか無いので、行(EntryRow)を介さずここに直接持つ
    private bool       _quitAwaitingConfirm;
    private Coroutine  _quitConfirmRoutine;
    private const string QuitNormalLabel  = "終了";
    private const string QuitConfirmLabel = "もう一度押すと終了";

    // ── 検出設定スライダーの遅延適用 ──
    // PersonConfidence と MaxPeople の setter は、どちらも MediaPipe の
    // PoseLandmarker を作り直す（数十ms）。ドラッグ中に onValueChanged が
    // 毎フレーム飛ぶとその都度作り直しになって映像が固まるので、
    // 値を控えておいてマウスを離したフレームで1回だけ適用する。
    // EventTrigger を持ち込まず、既存の Update ポーリングの作法に揃えている。
    //
    // 2つを1つの型にまとめてあるのは、今後この種の「重い setter を持つ設定」を
    // 足すときに、遅延適用の作法ごとコピーできるようにするため
    private struct PendingValue
    {
        public bool  waiting;
        public float value;

        public void Request(float v) { waiting = true; value = v; }
    }

    private PendingValue _pendingPersonConf;
    private PendingValue _pendingMaxPeople;

    // 行GameObject と FireworkEntry の対応表。
    // 選択ハイライトは全行 Destroy → 再生成ではなく、この表を使って背景色だけ差し替える。
    private readonly List<EntryRow> _rows = new();

    // 1行ぶんの UI 参照
    private class EntryRow
    {
        public FireworkEntry   entry;
        public GameObject      rowGO;
        public Image           background;
        // ［選択］⇔［解除］でラベルだけを差し替えるための参照。
        // 行を作り直さずに文字を変えたいので持っている
        public TextMeshProUGUI selectLabel;
        // 削除ボタン用の参照（deleteImage / confirmRoutine など）は、
        // [削除] ボタンごと廃止したので持っていない
    }

    // ── ライフサイクル ──
    private void Awake()
    {
        // 「見た目だけ隠す」方式に必要。シーン側での手動追加は不要にする。
        _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
        {
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();
            Debug.Log("[AdminUI] CanvasGroup added automatically");
        }
    }

    private void Start()
    {
        Debug.Log("[AdminUI] Start called - v2");

        // FireworkManager が無くても表示トグルだけは効くように、先に適用しておく
        ApplyVisible(visibleOnStart);

        // カメラ切替は FireworkManager に依存しないので、早期 return より先に配線する。
        // cameraBackground は AdminPanel の外（CameraBackground オブジェクト）にあるため
        // AdminUIBuilder からはアサインできず、ここで自動解決することで
        // AdminUIBuilder（AdminPanel 配下しか触らない）だけでセットアップが完結するようにしている
        if (cameraBackground == null)
            cameraBackground = FindFirstObjectByType<CameraBackgroundController>();

        // テスト打ち上げは FireworkLauncher の経路を使う。
        // 未アサインでもシーンから拾えるようにしておく（シーン編集を不要にするため）
        _launcher = fireworkLauncher != null
                    ? fireworkLauncher
                    : FindFirstObjectByType<FireworkLauncher>();

        if (_launcher == null)
            Debug.LogWarning("[AdminUI] FireworkLauncher が見つかりません。テスト打ち上げは無効です");

        cameraIndexButton?.onClick.AddListener(OnCameraIndexClicked);
        UpdateCameraIndexLabel();

        // 宇宙モードもカメラ切替と同じく FireworkManager に依存しないので、早期 return より先に配線する。
        // GetOrCreate() はシーンに無ければ自分で生成して返す（null を返さない）ため、
        // 以降 _spaceMode 自体の null チェックは不要
        _spaceMode = SpaceModeController.GetOrCreate();
        spaceModeButton   ?.onClick.AddListener(OnSpaceModeClicked);
        frameToggleButton ?.onClick.AddListener(OnFrameToggleClicked);
        ufoToggleButton   ?.onClick.AddListener(OnUfoToggleClicked);
        hanabiModeButton  ?.onClick.AddListener(OnHanabiModeClicked);
        sfxToggleButton   ?.onClick.AddListener(OnSfxToggleClicked);
        UpdateSpaceModeLabel();
        UpdateFrameToggleLabel();
        UpdateUfoToggleLabel();
        UpdateHanabiModeLabel();
        UpdateSfxToggleLabel();

        // 設定パネル（ジェスチャー感度・花火の出し方）も FireworkManager に依存しないので、
        // 同じ理由で早期 return より先に配線する。
        // GestureDetector はシーンに1つある前提で自動解決する（AdminPanel の外にあるため
        // AdminUIBuilder からはアサインできない。cameraBackground と同じ事情）
        _gesture = FindFirstObjectByType<GestureDetector>();
        if (_gesture == null)
            Debug.LogWarning("[AdminUI] GestureDetector が見つかりません。感度調整は無効です");

        imgEnableButton ?.onClick.AddListener(OnImgEnableClicked);
        matteButton     ?.onClick.AddListener(OnMatteClicked);
        skeletonButton  ?.onClick.AddListener(OnSkeletonClicked);

        // 人の検出閾値は PoseLandmarkDetector が持つ。GestureDetector と同じく
        // AdminPanel の外にあるのでシーンから自動解決する
        _poseDetector = FindFirstObjectByType<PoseLandmarkDetector>();
        if (_poseDetector == null)
            Debug.LogWarning("[AdminUI] PoseLandmarkDetector が見つかりません。人の検出閾値の調整は無効です");

        // 骨格の表示トグル。丸窓モードとは独立した設定で、
        // 表示状態は SkeletonRenderer 自身が PlayerPrefs に永続化する
        _skeleton = FindFirstObjectByType<SkeletonRenderer>();
        if (_skeleton == null)
            Debug.LogWarning("[AdminUI] SkeletonRenderer が見つかりません。ボーン表示の切替は無効です");

        WireSliders();

        UpdateImgEnableLabel();
        UpdateMatteLabel();
        UpdateSkeletonLabel();

        // タブは「基本」から始める。ページの SetActive とタブの色をここで揃える
        SwitchTab(AdminTab.Basic);

        // 終了ボタンも FireworkManager に依存しないので早期 return より先に配線する。
        // アプリ全体の終了操作なので、これが機能しない状態は避けたい
        quitButton?.onClick.AddListener(OnQuitClicked);
        ResetQuitButton();

        // タブは FireworkManager にも他のどのコンポーネントにも依存しない純粋なUI操作なので、
        // 早期 return より先に配線する（エラー状態でも画面を見て回れるようにするため）
        tabBasicButton ?.onClick.AddListener(() => SwitchTab(AdminTab.Basic));
        tabSpaceButton ?.onClick.AddListener(() => SwitchTab(AdminTab.Space));
        tabTuneButton  ?.onClick.AddListener(() => SwitchTab(AdminTab.Tune));

        // 閉じる/開くも FireworkManager に依存しない、パネル自身の表示制御なので
        // 同じ理由で早期 return より先に配線する
        // （以前は他のボタン群と一緒に早期 return の後ろにあり、FireworkManager が
        //  見つからないエラー状態だとパネルを閉じることも開くこともできなかった）
        closeButton   ?.onClick.AddListener(() => SetVisible(false));
        openTabButton ?.onClick.AddListener(() => SetVisible(true));
        // openTabButton の初期表示状態自体は、Start() の先頭で呼んでいる
        // ApplyVisible(visibleOnStart) で既に決まっている（クリックの配線と
        // 表示状態の初期化は別物なので、ここで改めて呼び直す必要はない）

        _manager = FireworkManager.Instance;
        if (_manager == null)
        {
            UpdateEntryCountLabel();
            SetStatus("[ERROR] FireworkManager が見つかりません（一覧と更新は使えません）");
            return;
        }

        // ボタンイベント
        testLaunchButton  ?.onClick.AddListener(OnTestLaunchClicked);

        // API 差分取得
        refreshButton?.onClick.AddListener(OnRefreshClicked);

        // エントリ変更の購読
        _manager.OnEntriesChanged += RefreshList;

        RefreshList();
        SetStatus("[OK] 準備完了（F1キーでこの画面の表示/非表示）");
    }

    private void Update()
    {
        HealStuckButtons();
        SyncCameraIndexLabel();
        ApplyPendingDetectorSettings();

        if (!toggleWithF1) return;

        // 新 Input System 専用プロジェクト（activeInputHandler: 1）なので
        // Input.GetKeyDown は InvalidOperationException になる。Keyboard を直接見る。
        var keyboard = Keyboard.current;
        if (keyboard == null) return;   // キーボード未接続（モバイル等）では何もしない

        if (keyboard.f1Key.wasPressedThisFrame)
            ToggleVisible();
    }

    // 処理中はボタンを無効化しているが、コールバックが届かないまま
    // コルーチンが死ぬとボタンが無効のまま固まって二度と押せなくなる。
    // 実際の進行状態（IsFetching）を正として毎フレーム突き合わせて復帰させる。
    private void HealStuckButtons()
    {
        if (_manager == null) return;

        if (refreshButton != null && !refreshButton.interactable && !_manager.IsFetching)
        {
            refreshButton.interactable = true;
            Debug.LogWarning("[AdminUI] 更新ボタンが無効のまま残っていたので復帰させました");
        }

        // 切替コルーチンがタイムアウトや例外で死んでもボタンが無効のまま固まらないようにする
        if (cameraIndexButton != null && !cameraIndexButton.interactable
            && (cameraBackground == null || !cameraBackground.IsSwitching))
            cameraIndexButton.interactable = true;

    }

    private void OnDestroy()
    {
        if (_manager != null)
            _manager.OnEntriesChanged -= RefreshList;
    }

    // ── 表示 / 非表示 ──

    public bool IsVisible => _isVisible;

    public void ToggleVisible() => SetVisible(!_isVisible);

    public void SetVisible(bool value)
    {
        ApplyVisible(value);
        Debug.Log($"[AdminUI] Panel {(value ? "shown" : "hidden")} (F1 to toggle)");
    }

    private void ApplyVisible(bool value)
    {
        _isVisible = value;

        if (_canvasGroup != null)
        {
            // GameObject は落とさない（コルーチンと Update を生かしたまま見た目だけ消す）
            _canvasGroup.alpha          = value ? 1f : 0f;
            _canvasGroup.interactable   = value;
            _canvasGroup.blocksRaycasts = value;
        }

        // パネルが閉じている間だけ「開く」タブを出す。
        // このタブは AdminPanel の外（AdminCanvas 直下）にあるので、上の
        // CanvasGroup を通らない。ここで直接 SetActive する
        // （openTabButton 自身に常駐コンポーネントは無いので、AdminPanel と違い
        //  SetActive(false) で消しても問題ない）
        if (openTabButton != null) openTabButton.gameObject.SetActive(!value);
    }

    // ── テスト打ち上げ ──
    //
    // 以前は FireworkManager.LaunchRandom() を直接呼んでいたため、
    //   ・画像花火しか打たず、エントリが0件だと何も出なかった
    //   ・位置が testLaunchPosition (0,5,0) 固定で、実際の打ち上げ（z=-5）とは深度が違った
    //   ・imageScale もシェーダーも設定されず見た目が実際と異なった
    //   ・開花音が鳴らなかった
    // という食い違いがあった。今は FireworkLauncher の経路をそのまま使う。
    private void OnTestLaunchClicked()
    {
        if (_launcher == null)
        {
            SetStatus("[WARN] FireworkLauncher が見つかりません");
            return;
        }

        // 行が選択されているときは、その画像花火だけをプレビューする
        if (_selectedEntry != null)
        {
            if (!_selectedEntry.isConverted)
            {
                SetStatus("[WARN] 先に変換してください");
                return;
            }

            if (_launcher.LaunchTestImage(_selectedEntry))
                SetStatus($"[LAUNCH] {_selectedEntry.displayName}（選択中の画像花火）");
            else
                SetStatus($"[ERROR] 打ち上げに失敗: {_selectedEntry.displayName}");
            return;
        }

        // 選択が無いときはジェスチャーと同じ判定。
        // 取ってきた花火があれば抽選、無ければ自動で型花火にフォールバックする
        int  actives = _launcher.ActiveImageCount;
        var  kind    = _launcher.LaunchTest(isLarge: true);
        bool isImage = kind == FireworkAudioPlayer.FireworkKind.Image;

        if (actives == 0)
            SetStatus("[LAUNCH] 型花火（取ってきた花火が0件）");
        else
            SetStatus($"[LAUNCH] {(isImage ? "画像花火" : "型花火")}" +
                      $"（ランダム / 取ってきた花火 {actives}件）");
    }

    private void OnRefreshClicked()
    {
        if (_manager == null) return;
        if (_manager.IsFetching)
        {
            SetStatus("[WARN] Already fetching");
            return;
        }

        SetRefreshInteractable(false);
        SetStatus("APIから取得中...");

        // コルーチンは FireworkManager 側で回す。
        // このパネルが非アクティブ化されると自分で StartCoroutine したコルーチンは
        // 取得中に死に、IsFetching が立ちっぱなしになる。
        _manager.StartCoroutine(_manager.FetchNewEntriesFromApi((added, err) =>
        {
            SetRefreshInteractable(true);

            // 件数表示はタブ行の常設ラベルへ移したので、RefreshList が
            // この通知を上書きすることはもう無い（以前は読む前に消えていた）
            if (err != null)        SetStatus($"[ERROR] {err}");
            else if (added == 0)    SetStatus("[OK] 新しい花火はありませんでした");
            else                    SetStatus($"[OK] 新しい花火を{added}件取得しました");
        }));
    }

    private void SetRefreshInteractable(bool value)
    {
        if (refreshButton != null) refreshButton.interactable = value;
    }

    // ── エントリ一覧の再描画 ──
    public void RefreshList()
    {
        // 既存行を削除
        foreach (var row in _rows)
        {
            if (row == null) continue;
            if (row.rowGO != null) Destroy(row.rowGO);
        }
        _rows.Clear();

        if (_manager == null || entryListContent == null) return;

        // 件数はタブ行の右端の常設ラベルへ。ステータス行は通知専用に空けておく
        UpdateEntryCountLabel();

        // 削除済みエントリが選択されたままにならないように
        if (_selectedEntry != null && !ContainsEntry(_selectedEntry))
            ClearSelection();

        // エントリ行を生成
        foreach (var entry in _manager.Entries)
        {
            var row = BuildEntryRow(entry);
            row.rowGO.transform.SetParent(entryListContent, false);
            _rows.Add(row);
        }

        RefreshRowHighlights();
    }

    // Entries は IReadOnlyList なので Contains が無い。LINQ を持ち込まず線形探索する。
    private bool ContainsEntry(FireworkEntry entry)
    {
        var entries = _manager.Entries;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i] == entry) return true;
        return false;
    }

    // ── 選択ハイライト（行を作り直さない軽い経路）──
    //
    // 同じ行をもう一度押したら選択解除する。
    // 以前は一度選ぶと戻す手段が無く、［テスト打上］がその画像の固定プレビューに
    // 化けたまま「ランダムに戻せない」状態になっていた
    private void ToggleSelectEntry(FireworkEntry entry)
    {
        if (_selectedEntry == entry)
        {
            ClearSelection();
            RefreshRowHighlights();
            RefreshRowSelectLabels();
            SetStatus("[OK] 選択を解除しました（テスト打上はランダムに戻ります）");
            return;
        }

        _selectedEntry = entry;

        // RefreshList() は全行 Destroy → 再生成になるので使わない。背景色だけ差し替える。
        RefreshRowHighlights();
        RefreshRowSelectLabels();

        if (previewImage != null) previewImage.texture = entry.localTexture;
        if (detailText   != null)
        {
            detailText.text = entry.isConverted
                ? $"粒子数: {entry.particleData.particles.Length}\nサイズ: {entry.particleData.width}x{entry.particleData.height}"
                : "未変換";
        }
        SetStatus($"[OK] 選択中: {entry.displayName}");
    }

    // 選択/解除でラベルが変わるのは選択中の行と直前まで選択中だった行だけだが、
    // 行数はたかだか数十なので分岐を持たずに全行を舐める（分岐のほうが壊れやすい）
    private void RefreshRowSelectLabels()
    {
        foreach (var row in _rows)
        {
            if (row == null || row.selectLabel == null) continue;
            row.selectLabel.text = (row.entry == _selectedEntry && _selectedEntry != null)
                ? "解除" : "選択";
        }
    }

    // 選択解除。プレビューも一緒に空にする。
    // FireworkManager.RemoveEntry() が localTexture を Destroy するため、
    // ここを残すと RawImage が破棄済みテクスチャを握ったままになる。
    private void ClearSelection()
    {
        _selectedEntry = null;
        if (previewImage != null) previewImage.texture = null;
        if (detailText   != null) detailText.text      = "";
    }

    private void RefreshRowHighlights()
    {
        foreach (var row in _rows)
        {
            if (row == null || row.background == null) continue;
            row.background.color = (row.entry == _selectedEntry && _selectedEntry != null)
                ? AdminUiStyle.RowSelected
                : AdminUiStyle.RowNormal;
        }
    }

    // ── エントリ行のコード生成 ──
    // Prefab を用意しない場合のフォールバック。
    // Prefab がある場合は BuildEntryRow の中身を差し替えてください。
    private EntryRow BuildEntryRow(FireworkEntry entry)
    {
        // ── 行ルート ──
        var rowGO  = new GameObject($"Row_{entry.displayName}");
        var rowRT  = rowGO.AddComponent<RectTransform>();
        rowRT.sizeDelta = new Vector2(0f, AdminUiStyle.EntryRowHeight);

        // 親（Viewport > Content）の VerticalLayoutGroup は子の高さを
        // LayoutElement から決めるため、sizeDelta だけでは効かない
        var rowLe = rowGO.AddComponent<LayoutElement>();
        rowLe.minHeight       = AdminUiStyle.EntryRowHeight;
        rowLe.preferredHeight = AdminUiStyle.EntryRowHeight;
        rowLe.flexibleHeight  = 0f;

        // 選択ハイライト用の背景（クリックを奪わないよう raycastTarget は切る）
        var bg = rowGO.AddComponent<Image>();
        bg.color         = AdminUiStyle.RowNormal;
        bg.raycastTarget = false;

        var hLayout = rowGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing        = 8f;
        hLayout.padding        = new RectOffset(8, 8, 6, 6);
        hLayout.childAlignment = TextAnchor.MiddleLeft;
        hLayout.childForceExpandWidth  = false;
        hLayout.childForceExpandHeight = true;

        var row = new EntryRow
        {
            entry      = entry,
            rowGO      = rowGO,
            background = bg,
        };

        // サムネイル
        var thumb    = MakeChild<RawImage>(rowGO.transform, "Thumb", new Vector2(52f, 52f));
        thumb.texture = entry.localTexture;

        // 名前ラベル
        var nameLbl  = MakeChild<TextMeshProUGUI>(rowGO.transform, "Name", flexible: true);
        nameLbl.text = entry.id >= 0
            ? $"{entry.displayName}  <size=10><color=#88bbff>API</color></size>"
            : entry.displayName;
        nameLbl.fontSize  = 14f;
        nameLbl.overflowMode = TextOverflowModes.Ellipsis;

        // ステータスラベル
        var statLbl  = MakeChild<TextMeshProUGUI>(rowGO.transform, "Status", new Vector2(120f, 0f));
        RefreshRowStatus(statLbl, entry);

        // ── [変換] と [削除] を置かない理由 ──
        //   変換: APIから取得した時点で FireworkManager が ConvertEntry と SetActive まで
        //         済ませている（FetchNewEntriesFromApi 参照）ので、手で押す場面が無い。
        //   削除: 運用上そもそも消さないと決まったため。
        //         誤タップで取得済みの花火を失う事故のほうが実害が大きい。
        //   どちらも FireworkManager 側の API（ConvertEntry / RemoveEntry）は
        //   残してあるので、必要になればボタンを戻すだけでよい。

        // ［表示中 / 停止中］トグル。
        // ON=緑 / OFF=白 という他のトグルと同じ規則に揃えてある。
        // 以前の OFF は灰背景に暗い文字でコントラスト比 2.9:1 と読めていなかった
        MakeButton(rowGO.transform,
            entry.isActive ? "表示中" : "停止中",
            entry.isActive ? AdminUiStyle.OnBackground : AdminUiStyle.ButtonBackground,
            entry.isActive ? AdminUiStyle.OnLabel      : AdminUiStyle.ButtonLabel,
            () =>
            {
                _manager.SetActive(entry, !entry.isActive);
                // ラベルはOnEntriesChanged → RefreshListで更新される
            });

        // ［細かさ］ボタン。この花火だけの粒の細かさを 小→中→大 で循環させる。
        // 中（既定値）は白のまま、小/大に振ったときだけ色が付く
        //（＝色が付いている＝既定から動かしてある、という規則を他のトグルと揃える）
        int resIndex = NearestImageResIndex(
            _manager != null ? _manager.ResolutionOf(entry) : ImageResSteps[1]);

        var (resBg, resFg) = resIndex switch
        {
            0 => (AdminUiStyle.EnumMixBackground,   AdminUiStyle.EnumMixLabel),    // 小
            2 => (AdminUiStyle.EnumSpaceBackground, AdminUiStyle.EnumSpaceLabel),  // 大
            _ => (AdminUiStyle.ButtonBackground,    AdminUiStyle.ButtonLabel),     // 中
        };

        MakeButton(rowGO.transform, $"細かさ {ImageResLabels[resIndex]}", resBg, resFg,
            () => OnEntryResolutionClicked(entry));

        // ［選択 / 解除］ボタン（プレビューパネルと行ハイライトに反映）。
        // 選択中の行では「解除」になり、押すと選択を外せる
        MakeButton(rowGO.transform,
            entry == _selectedEntry && _selectedEntry != null ? "解除" : "選択",
            AdminUiStyle.ButtonBackground, AdminUiStyle.ButtonLabel,
            () => ToggleSelectEntry(entry),
            out row.selectLabel);

        return row;
    }

    // ── 削除の2段階確認は削除した ──
    //   運用上そもそもエントリを消さないと決まったため、行の [削除] ボタンごと外した。
    //   確認待ちの状態・タイマー・色戻しも合わせて不要になっている。
    //   FireworkManager.RemoveEntry() は残してあるので、必要になれば
    //   ボタンと2段階確認を戻すだけでよい。

    // ── 終了ボタン（2段階確認）──
    //
    // [閉じる] は CanvasGroup で見た目を隠すだけでアプリは終了しない
    // （F1で再表示できるようにするための意図的な設計）。ビルド後は
    // Unity Editor に触れない運用が前提のため、アプリ自体を終了する手段が
    // Alt+F4 しか無いのは運用上わかりにくい、という指摘を受けて追加した。
    // 削除ボタンと同じ「1回目でタップ待ち状態に、2回目で実行」の型を踏襲している
    // （誤タップでキオスクをその場で落とさないための猶予）
    private void OnQuitClicked()
    {
        if (!_quitAwaitingConfirm)
        {
            BeginQuitConfirm();
            return;
        }

        StopQuitConfirmRoutine();
        Debug.Log("[AdminUI] アプリを終了します（確認済み）");

        // Application.Quit() は Editor の Play モードでは何もしない仕様なので、
        // Editor 上でも動作確認できるよう isPlaying を落とす経路を分けている
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void BeginQuitConfirm()
    {
        _quitAwaitingConfirm = true;
        if (quitText != null) quitText.text = QuitConfirmLabel;

        // 確認待ちは濃赤＋白文字。
        // 以前は濃赤の上に暗い文字を載せていてコントラスト比 3.3:1 と
        // このパネル中で最悪の読みにくさだった（＝一番読ませたい警告が一番読めない）
        SetButtonColors(quitButton, quitText,
                        AdminUiStyle.DangerArmedBackground, AdminUiStyle.DangerArmedLabel);
        SetStatus("[WARN] もう一度［終了］を押すとアプリを終了します");

        StopQuitConfirmRoutine();
        _quitConfirmRoutine = StartCoroutine(QuitConfirmTimeout());
    }

    private IEnumerator QuitConfirmTimeout()
    {
        float elapsed = 0f;
        while (elapsed < quitConfirmSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        _quitConfirmRoutine = null;
        ResetQuitButton();
    }

    // 通常時から薄赤にしてある。「押すと戻れない操作」であることを常に示すためで、
    // 隣の［閉じる］（見た目を隠すだけ）と押し間違えないようにする狙いもある
    private void ResetQuitButton()
    {
        _quitAwaitingConfirm = false;
        if (quitText != null) quitText.text = QuitNormalLabel;
        SetButtonColors(quitButton, quitText,
                        AdminUiStyle.DangerBackground, AdminUiStyle.DangerLabel);
    }

    private void StopQuitConfirmRoutine()
    {
        if (_quitConfirmRoutine == null) return;
        StopCoroutine(_quitConfirmRoutine);
        _quitConfirmRoutine = null;
    }

    // ── ステータスラベルだけを更新 ──
    private void RefreshRowStatus(TextMeshProUGUI label, FireworkEntry entry)
    {
        if (label == null) return;
        // 暗いパネル地の上に載るので、緑も灰も暗い側ではなく明るい側の色を使う
        label.text = entry.isConverted
            ? $"<color={AdminUiStyle.StatusOkHex}>変換済み</color>\n{entry.particleData.particles.Length} 粒"
            : $"<color={AdminUiStyle.StatusWarnHex}>未変換</color>";
        label.fontSize = AdminUiStyle.RowFontMin;
    }

    // ── UI ヘルパー ──

    private T MakeChild<T>(Transform parent, string name,
        Vector2? fixedSize = null, bool flexible = false) where T : Component
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var comp = go.AddComponent<T>();

        var le = go.AddComponent<LayoutElement>();
        if (fixedSize.HasValue)
        {
            le.preferredWidth  = fixedSize.Value.x;
            le.preferredHeight = fixedSize.Value.y;
        }
        if (flexible) le.flexibleWidth = 1f;

        return comp;
    }

    private Button MakeButton(Transform parent, string label, Color bgColor, Color labelColor,
        System.Action onClick, out TextMeshProUGUI labelOut, float minWidth = ButtonMinWidth)
    {
        var go  = new GameObject($"Btn_{label}");
        go.transform.SetParent(parent, false);

        // 幅は固定せず「最小幅」として扱う。
        // preferredWidth を -1（未設定）にすると LayoutElement は幅を上書きせず、
        // 下の HorizontalLayoutGroup が算出する値（= ラベルの preferredWidth + padding）が
        // 採用される。日本語ラベル（"表示中" "解除" など）でも切れずに伸びる。
        var le  = go.AddComponent<LayoutElement>();
        le.minWidth        = minWidth;
        le.preferredWidth  = -1f;
        le.minHeight       = AdminUiStyle.RowButtonHeight;
        le.preferredHeight = AdminUiStyle.RowButtonHeight;

        var img = go.AddComponent<Image>();
        img.color = bgColor;

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;   // interactable = false の見た目を効かせる
        btn.onClick.AddListener(() => onClick());

        // ラベルをレイアウトの子として扱い、テキスト幅をボタン幅へ伝える
        var inner = go.AddComponent<HorizontalLayoutGroup>();
        inner.padding                = new RectOffset(10, 10, 2, 2);
        inner.childAlignment         = TextAnchor.MiddleCenter;
        inner.childControlWidth      = true;
        inner.childControlHeight     = true;
        inner.childForceExpandWidth  = true;
        inner.childForceExpandHeight = true;

        // テキスト
        var textGO  = new GameObject("Label");
        textGO.transform.SetParent(go.transform, false);
        var tmp     = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.alignment = TextAlignmentOptions.Center;
        // 背景色ごとに読める文字色が違うので、呼び出し側から受け取る。
        // 組み合わせは AdminUiStyle が単一ソース（全て 4.5:1 以上）
        tmp.color     = labelColor;

        // 溢れ対策: 1行維持したままフォントを自動縮小する。
        // 以前は 8〜12pt で、遠目にはほぼ読めなかった
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin      = AdminUiStyle.RowFontMin;
        tmp.fontSizeMax      = AdminUiStyle.RowFontMax;
        tmp.fontSize         = AdminUiStyle.RowFontMax;

        labelOut = tmp;
        return btn;
    }

    // overload（labelOut 不要な場合）
    private Button MakeButton(Transform parent, string label, Color bgColor, Color labelColor,
        System.Action onClick, float minWidth = ButtonMinWidth)
    {
        return MakeButton(parent, label, bgColor, labelColor, onClick, out _, minWidth);
    }

    // ── 宇宙モード ──
    //
    // マスターON/OFFは「個別トグルの設定はそのまま、効き目だけを止める」ためのもの
    // （SpaceModeController 側の Setting/Enabled 分離を参照）。
    // ラベルには常に個別設定値（*Setting）を出す。実効値（*Enabled）を出すと、
    // マスターOFF中は4つとも [OFF] に見えてしまい、個別トグルが勝手にリセット
    // されたかのように誤解される（実際はマスターを戻せば元の組み合わせに戻る）ため。

    // ON/OFF のトグル表示の共通処理。
    // 「色が付いている＝有効」の規則をこの1箇所に閉じ込めてあるので、
    // トグルを増やすときもここを通せば見た目が自動的に揃う
    private void ApplyToggleVisual(Button button, TextMeshProUGUI label, string name, bool on)
    {
        if (label != null) label.text = $"{name} [{(on ? "ON" : "OFF")}]";
        SetButtonColors(button, label,
            on ? AdminUiStyle.OnBackground : AdminUiStyle.ButtonBackground,
            on ? AdminUiStyle.OnLabel      : AdminUiStyle.ButtonLabel);
    }

    private void OnSpaceModeClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleMaster();
        UpdateSpaceModeLabel();
        Debug.Log($"[AdminUI] SpaceMode: {(_spaceMode.MasterEnabled ? "ON" : "OFF")}");
    }

    private void UpdateSpaceModeLabel() =>
        ApplyToggleVisual(spaceModeButton, spaceModeText, "宇宙モード",
                          _spaceMode != null && _spaceMode.MasterEnabled);

    private void OnFrameToggleClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleFrame();
        UpdateFrameToggleLabel();
    }

    private void UpdateFrameToggleLabel() =>
        ApplyToggleVisual(frameToggleButton, frameToggleText, "宇宙船フレーム",
                          _spaceMode != null && _spaceMode.FrameSetting);

    private void OnUfoToggleClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleUfo();
        UpdateUfoToggleLabel();
    }

    private void UpdateUfoToggleLabel() =>
        ApplyToggleVisual(ufoToggleButton, ufoToggleText, "UFO",
                          _spaceMode != null && _spaceMode.UfoSetting);

    private void OnHanabiModeClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.CycleFireworkMode();
        UpdateHanabiModeLabel();
    }

    // 3値の enum なので bool のトグルとは見た目を変える。
    // 「和風のみ」＝宇宙要素なしの既定状態なので通常ボタンと同じ白、
    // 混合＝薄緑、宇宙のみ＝薄紫。並んでいる ON/OFF トグルと取り違えないための区別
    private void UpdateHanabiModeLabel()
    {
        if (_spaceMode == null)
        {
            if (hanabiModeText != null) hanabiModeText.text = "花火の種類  ―";
            return;
        }

        var (name, bg, fg) = _spaceMode.FireworkSetting switch
        {
            SpaceModeController.SpaceFireworkMode.Mix       =>
                ("混合",   AdminUiStyle.EnumMixBackground,   AdminUiStyle.EnumMixLabel),
            SpaceModeController.SpaceFireworkMode.SpaceOnly =>
                ("宇宙のみ", AdminUiStyle.EnumSpaceBackground, AdminUiStyle.EnumSpaceLabel),
            _                                               =>
                ("和風のみ", AdminUiStyle.ButtonBackground,    AdminUiStyle.ButtonLabel),
        };

        if (hanabiModeText != null) hanabiModeText.text = $"花火の種類 [{name}]";
        SetButtonColors(hanabiModeButton, hanabiModeText, bg, fg);
    }

    private void OnSfxToggleClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleSpaceAudio();
        UpdateSfxToggleLabel();
    }

    private void UpdateSfxToggleLabel() =>
        ApplyToggleVisual(sfxToggleButton, sfxToggleText, "宇宙効果音",
                          _spaceMode != null && _spaceMode.SpaceAudioSetting);

    // ── タブ ──
    //
    // 3枚のうち1枚だけを SetActive(true) にする。親（AdminPanel）の
    // VerticalLayoutGroup は非アクティブな子を自動でスキップしてリフローするので、
    // SetActive の切替だけで見た目が崩れない
    // （Destroy → 再生成にすると設定値やアサインが失われるので避ける）。
    //
    // 宇宙タブを「宇宙モードON中だけ」にしていない理由:
    //   個別スイッチ（フレーム/UFO/花火の種類/効果音）の設定値はマスターとは
    //   独立に保存される（SpaceModeController の Setting/Enabled 分離）。
    //   マスターOFF中に仕込んでおけると開演前の準備が楽なので、常に開けるようにした。
    private void SwitchTab(AdminTab tab)
    {
        _activeTab = tab;

        if (tabBasicPage != null) tabBasicPage.gameObject.SetActive(tab == AdminTab.Basic);
        if (tabSpacePage != null) tabSpacePage.gameObject.SetActive(tab == AdminTab.Space);
        if (tabTunePage  != null) tabTunePage .gameObject.SetActive(tab == AdminTab.Tune);

        ApplyTabVisual(tabBasicButton, tabBasicText, tab == AdminTab.Basic);
        ApplyTabVisual(tabSpaceButton, tabSpaceText, tab == AdminTab.Space);
        ApplyTabVisual(tabTuneButton,  tabTuneText,  tab == AdminTab.Tune);

        if (tabHelpText != null)
        {
            tabHelpText.text = tab switch
            {
                AdminTab.Space => "宇宙モードONで枠・UFO・宇宙花火が有効。個別スイッチはOFF中も保存されます",
                AdminTab.Tune  => "右へ動かすほど反応しにくくなります（誤発火を減らしたいときは右へ）",
                _              => "テスト打上=花火を1発試す ／ 丸窓=映像をドーム型に切り抜く表示 ／ " +
                                  "花火ごとの細かさは下の一覧の［細かさ］から",
            };
        }
    }

    // 選択中は濃紺＋白文字、非選択は白＋濃紺文字。
    // 「色が付いている＝今ここ」という規則をトグル（ON=緑）と揃えている
    private static void ApplyTabVisual(Button button, TextMeshProUGUI label, bool selected)
    {
        SetButtonColors(button, label,
            selected ? AdminUiStyle.TabSelectedBackground : AdminUiStyle.ButtonBackground,
            selected ? AdminUiStyle.TabSelectedLabel      : AdminUiStyle.ButtonLabel);
    }

    // ボタンの背景色とラベル色をまとめて塗る共通処理。
    // Image.color を直に触ると Button の遷移（Normal色）と食い違うので、
    // ColorBlock 側も一緒に更新する
    private static void SetButtonColors(Button button, TextMeshProUGUI label, Color bg, Color fg)
    {
        if (button != null)
        {
            var image = button.targetGraphic as Image ?? button.GetComponent<Image>();
            if (image != null) image.color = bg;

            var colors = button.colors;
            colors.normalColor    = Color.white;   // Image.color に対する乗算なので白＝そのまま
            colors.disabledColor  = AdminUiStyle.DisabledBackground;
            button.colors = colors;
        }

        if (label != null) label.color = fg;
    }

    // ── 検出の調整タブ（スライダー）──
    //
    // ビルド後は Unity Editor に一切触れない運用を前提にしたパネル。
    // 会場・客層・投稿写真の状況で変えたくなる値をここに集約している。
    // 個々の値自体は GestureDetector / FireworkLauncher / PoseLandmarkDetector 側で
    // PlayerPrefs に永続化されるので、ここは「触って値を書く」だけでよい。
    //
    // min/max/wholeNumbers は AdminUIBuilder が設定する（生成側の責務）。
    // ここで初期値を入れるときに SetValueWithoutNotify を使うのは、
    // onValueChanged が発火して「読んだ値をそのまま書き戻す」無駄な往復
    // （＝起動のたびに PlayerPrefs へ Save が走る）を避けるため。
    private void WireSliders()
    {
        if (_gesture != null)
        {
            InitSlider(handUpSlider,   _gesture.HandUpThreshold,  v => { _gesture.HandUpThreshold  = v; UpdateHandUpLabel();   });
            InitSlider(jumpSlider,     _gesture.JumpThreshold,    v => { _gesture.JumpThreshold    = v; UpdateJumpLabel();     });
            InitSlider(cooldownSlider, _gesture.GestureCooldown,  v => { _gesture.GestureCooldown  = v; UpdateCooldownLabel(); });
            InitSlider(holdSlider,     _gesture.PoseHoldDuration, v => { _gesture.PoseHoldDuration = v; UpdateHoldLabel();     });
        }

        // 画像花火の割合だけはプロパティが 0〜1 なのに対しスライダーは 0〜100（整数）。
        // 「50%」と表示したいのに 0.5 刻みのスライダーは触りにくいため、
        // UI 側を百分率にして、ここで換算している
        if (_launcher != null)
            InitSlider(imgChanceSlider, _launcher.ImageFireworkChance * 100f,
                       v => { _launcher.ImageFireworkChance = v / 100f; UpdateImgChanceLabel(); });

        // 検出まわりの2本は即時適用しない（PoseLandmarker の作り直しが走るため）。
        // ラベルはドラッグに追従させ、実際の適用は
        // ApplyPendingDetectorSettings に任せる
        if (_poseDetector != null)
        {
            InitSlider(personConfSlider, _poseDetector.PersonConfidence, v =>
            {
                _pendingPersonConf.Request(v);
                UpdatePersonConfLabel(v);
            });

            InitSlider(maxPeopleSlider, _poseDetector.MaxPeople, v =>
            {
                _pendingMaxPeople.Request(v);
                UpdateMaxPeopleLabel(Mathf.RoundToInt(v));
            });
        }

        UpdateHandUpLabel();
        UpdateJumpLabel();
        UpdateCooldownLabel();
        UpdateHoldLabel();
        UpdateImgChanceLabel();
        UpdatePersonConfLabel();
        UpdateMaxPeopleLabel();
    }

    private static void InitSlider(Slider slider, float initialValue, UnityEngine.Events.UnityAction<float> onChanged)
    {
        if (slider == null) return;
        slider.SetValueWithoutNotify(Mathf.Clamp(initialValue, slider.minValue, slider.maxValue));
        slider.onValueChanged.AddListener(onChanged);
    }

    // 検出設定の遅延適用。マウスの左ボタンが離れたフレームで1回だけ適用する。
    // ドラッグ中に適用すると MediaPipe の PoseLandmarker を毎フレーム作り直して
    // 映像が固まるため（作り直し自体は数十msだが、毎フレームだと止まって見える）。
    private void ApplyPendingDetectorSettings()
    {
        if (_poseDetector == null) return;
        if (!_pendingPersonConf.waiting && !_pendingMaxPeople.waiting) return;

        // 新 Input System 専用プロジェクトなので Input.GetMouseButton は使えない。
        // マウスが無い環境（タッチ等）では押しっぱなし判定ができないので、即時適用に倒す
        var mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.isPressed) return;

        if (_pendingPersonConf.waiting)
        {
            _pendingPersonConf.waiting = false;
            _poseDetector.PersonConfidence = _pendingPersonConf.value;
            UpdatePersonConfLabel();
            SetStatus($"[OK] 人物検出のきびしさ: {_poseDetector.PersonConfidence:F2}");
        }

        if (_pendingMaxPeople.waiting)
        {
            _pendingMaxPeople.waiting = false;
            _poseDetector.MaxPeople = Mathf.RoundToInt(_pendingMaxPeople.value);
            UpdateMaxPeopleLabel();
            SetStatus($"[OK] 同時に検出する人数: {_poseDetector.MaxPeople}人");
        }
    }

    private void UpdateHandUpLabel()
    {
        if (handUpText == null) return;
        handUpText.text = _gesture != null
            ? $"手上げ判定のきびしさ  {_gesture.HandUpThreshold:F2}（肩幅比）"
            : "手上げ判定のきびしさ  ―";
    }

    private void UpdateJumpLabel()
    {
        if (jumpText == null) return;
        jumpText.text = _gesture != null
            ? $"ジャンプ判定のきびしさ  {_gesture.JumpThreshold:F2}（肩幅比）"
            : "ジャンプ判定のきびしさ  ―";
    }

    private void UpdateCooldownLabel()
    {
        if (cooldownText == null) return;
        cooldownText.text = _gesture != null
            ? $"連発防止の間隔  {_gesture.GestureCooldown:F1}秒"
            : "連発防止の間隔  ―";
    }

    private void UpdateHoldLabel()
    {
        if (holdText == null) return;
        holdText.text = _gesture != null
            ? $"ポーズの保持時間  {_gesture.PoseHoldDuration:F1}秒"
            : "ポーズの保持時間  ―";
    }

    private void UpdateImgChanceLabel()
    {
        if (imgChanceText == null) return;
        imgChanceText.text = _launcher != null
            ? $"画像花火の割合  {Mathf.RoundToInt(_launcher.ImageFireworkChance * 100f)}%"
            : "画像花火の割合  ―";
    }

    // 人として検出するのに必要な確信度。
    // ジェスチャーの閾値が「検出できた人が手を上げたか」の判定なのに対し、
    // こちらは「そもそも人として拾うか」の手前の段階を絞る。
    //
    // 引数ありの版はドラッグ中の表示用（まだ適用していない値を出す）。
    // 引数なしの版は適用後の実値を出す
    private void UpdatePersonConfLabel() =>
        UpdatePersonConfLabel(_poseDetector != null ? _poseDetector.PersonConfidence : 0f);

    private void UpdatePersonConfLabel(float value)
    {
        if (personConfText == null) return;
        personConfText.text = _poseDetector != null
            ? $"人物検出のきびしさ  {value:F2}"
            : "人物検出のきびしさ  ―";
    }

    // 同時に検出する人数の上限。
    // 「人物検出のきびしさ」が1人ずつの採否を決めるのに対し、こちらは
    // 何人まで並行して追いかけるかの上限。多いほど1フレームの推論が重くなる
    private void UpdateMaxPeopleLabel() =>
        UpdateMaxPeopleLabel(_poseDetector != null ? _poseDetector.MaxPeople : 0);

    private void UpdateMaxPeopleLabel(int value)
    {
        if (maxPeopleText == null) return;
        maxPeopleText.text = _poseDetector != null
            ? $"同時に検出する人数  {value}人"
            : "同時に検出する人数  ―";
    }

    // 比率スライダーと違い、こちらは即座に「画像花火を一切出さない」緊急スイッチ。
    // API/投稿写真パイプラインが壊れたときに、比率を0にするより速く・確実に止められる
    private void OnImgEnableClicked()
    {
        if (_launcher == null) return;
        _launcher.EnableImageFirework = !_launcher.EnableImageFirework;
        UpdateImgEnableLabel();
    }

    private void UpdateImgEnableLabel() =>
        ApplyToggleVisual(imgEnableButton, imgEnableText, "画像花火",
                          _launcher != null && _launcher.EnableImageFirework);

    // カメラ映像を画面下部の半楕円ドームに収める演出のトグル。
    //
    // CameraCircleMatte は Main Camera へ自動アタッチされるので、ここでは
    // 見つけて叩くだけ。OFF にすると同コンポーネントが控えておいた元の値
    // （カメラのClearFlags・背景Quadのscale/position）を復元するので、
    // ビルドし直さずに現場で「あり/なし」を見比べられる
    private void OnMatteClicked()
    {
        var matte = ResolveMatte();
        if (matte == null)
        {
            SetStatus("[WARN] CameraCircleMatte が見つかりません");
            return;
        }

        matte.MatteEnabled = !matte.MatteEnabled;
        UpdateMatteLabel();
        Debug.Log($"[AdminUI] CircleMatte: {(matte.MatteEnabled ? "ON" : "OFF")}");
    }

    private void UpdateMatteLabel()
    {
        var matte = ResolveMatte();
        ApplyToggleVisual(matteButton, matteText, "丸窓モード", matte != null && matte.MatteEnabled);
    }

    // ── 骨格（ボーン）の表示トグル ──
    //
    // 丸窓モードとは独立した設定。以前はドーム化が骨格を強制的に隠していたが、
    // 「ドーム中でも認識されている人のボーンは見せたい」という運用要望で切り離した。
    // 表示状態は SkeletonRenderer 自身が PlayerPrefs に永続化するので、
    // ここは ShowSkeleton を反転するだけでよい
    private void OnSkeletonClicked()
    {
        if (_skeleton == null)
        {
            SetStatus("[WARN] SkeletonRenderer が見つかりません");
            return;
        }

        _skeleton.ShowSkeleton = !_skeleton.ShowSkeleton;
        UpdateSkeletonLabel();
        Debug.Log($"[AdminUI] Skeleton: {(_skeleton.ShowSkeleton ? "ON" : "OFF")}");
    }

    private void UpdateSkeletonLabel() =>
        ApplyToggleVisual(skeletonButton, skeletonText, "ボーン表示",
                          _skeleton != null && _skeleton.ShowSkeleton);

    // ── 画像花火の細かさ（一覧の行ごと）──
    //
    // 画像を n×n に縮小してから粒にしているので、この n が「細かさ」そのもの。
    // 小さくすると粒が減って軽くなり、大きくすると絵が細かく出るぶん粒数が増える。
    // 24〜48 という幅は ImageToParticles 側のコメントが「花火らしく粗い粒にしたい場合は
    // 24〜48 あたり」と書いている実用域をそのまま3段にしたもの。
    //
    // ── 全体で1つ持たず、行ごとにした理由 ──
    //   絵によって最適な細かさが違う。文字や線画は細かく焼かないと読めないが、
    //   面で塗った絵は粗いほうがむしろ花火らしい粒に見える。
    //   全体で1つの値を共有すると、どちらかが必ず妥協になる。
    private static readonly int[]    ImageResSteps  = { 24, 32, 48 };
    private static readonly string[] ImageResLabels = { "小", "中", "大" };

    // 与えられた解像度に一番近い段の番号。完全一致を求めないのは、
    // Inspector の既定値が段からずれていても（例: 40）そこから自然に巡回できるようにするため
    private static int NearestImageResIndex(int resolution)
    {
        int nearest = 0;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < ImageResSteps.Length; i++)
        {
            int diff = Mathf.Abs(ImageResSteps[i] - resolution);
            if (diff < bestDiff) { bestDiff = diff; nearest = i; }
        }
        return nearest;
    }

    // 行の［細かさ］ボタン。押すたびに 小→中→大 を循環し、その1件だけを焼き直す。
    // 1枚ぶんの変換なので同期で終わる（全件焼き直しと違いフレームを跨がない）
    private void OnEntryResolutionClicked(FireworkEntry entry)
    {
        if (_manager == null || entry == null) return;

        int current = _manager.ResolutionOf(entry);
        int next    = ImageResSteps[(NearestImageResIndex(current) + 1) % ImageResSteps.Length];

        // 焼き直すと OnEntriesChanged → RefreshList で行が作り直され、
        // ラベルは新しい値で組まれる。ここで個別にラベルを触る必要はない
        _manager.SetEntryResolution(entry, next);
        SetStatus($"[OK] {entry.displayName} の細かさ: {ImageResLabels[NearestImageResIndex(next)]}");
    }

    // Main Camera への自動アタッチは AfterSceneLoad で走るので、AdminUIManager.Start()
    // の時点では間に合っていない可能性がある。毎回探し直して見つかった時点でキャッシュする
    private CameraCircleMatte ResolveMatte()
    {
        if (_matte == null) _matte = FindFirstObjectByType<CameraCircleMatte>();
        return _matte;
    }

    // ── カメラ Index の循環切替 ──
    //
    // 展示現場では「どの index がどのカメラか」が現地に行くまで分からない。
    // 以前は Unity に戻って Inspector の webcamIndex を直して再生し直すしか
    // 手が無かったので、ここから総当たりできるようにしている。
    // 選んだ index は保存しない（起動ごとに Inspector の値から始まる）。

    private void OnCameraIndexClicked()
    {
        if (cameraBackground == null)
        {
            SetStatus("[WARN] CameraBackgroundController が見つかりません");
            return;
        }

        if (cameraBackground.IsSwitching)
        {
            SetStatus("[WARN] カメラ切替中です");
            return;
        }

        cameraBackground.CycleNextCamera();

        // 切替はコルーチンなので、この時点のラベルはまだ確定値ではない。
        // 確定値は SyncCameraIndexLabel() が次のフレーム以降に反映する
        UpdateCameraIndexLabel();
        SetStatus($"[OK] {CameraLabelText()}");
    }

    // 切替が非同期に完了するため、毎フレーム実際の状態と突き合わせる。
    // ただし比較に使うのは int と bool だけ。ラベル文字列を毎フレーム組み立てると
    // 1フレーム1個の string を無条件に捨てることになるので、
    // 「表示の元になる値が変わったフレーム」でしか文字列を作らない
    // （TMP.text への代入も同じ理由で最小限にする）。
    private int  _shownCameraIndex     = int.MinValue;
    private int  _shownCameraCount     = int.MinValue;
    private bool _shownCameraSwitching;

    private void SyncCameraIndexLabel()
    {
        if (cameraIndexText == null) return;

        int  index     = cameraBackground != null ? cameraBackground.CurrentIndex  : -1;
        int  count     = cameraBackground != null ? cameraBackground.DeviceCount   : -1;
        bool switching = cameraBackground != null && cameraBackground.IsSwitching;

        if (index == _shownCameraIndex
            && count == _shownCameraCount
            && switching == _shownCameraSwitching) return;

        UpdateCameraIndexLabel();
    }

    private void UpdateCameraIndexLabel()
    {
        if (cameraIndexText == null) return;

        _shownCameraIndex     = cameraBackground != null ? cameraBackground.CurrentIndex : -1;
        _shownCameraCount     = cameraBackground != null ? cameraBackground.DeviceCount  : -1;
        _shownCameraSwitching = cameraBackground != null && cameraBackground.IsSwitching;

        cameraIndexText.text = CameraLabelText();
    }

    // 例: "カメラ切替 [2/4] HD Webcam"。切替中は index の代わりに "..." を出す
    private string CameraLabelText()
    {
        if (cameraBackground == null) return "カメラ切替 [―]";

        if (cameraBackground.IsSwitching) return "カメラ切替 [...] 切替中";

        return $"カメラ切替 [{cameraBackground.CurrentIndex}/{cameraBackground.DeviceCount}] " +
               $"{cameraBackground.CurrentDeviceName}";
    }

    // ── ステータス表示 ──
    //
    // 接頭辞（[OK] / [WARN] / [ERROR] / [LAUNCH]）を見つけたら rich text で色を付ける。
    // ここ1箇所を通すだけで全メッセージに効くので、呼び出し側は今までどおり
    // 素の文字列を渡せばよい。色は AdminUiStyle（暗いパネル地の上で 7:1 以上）。
    //
    // ログには色タグの付かない元の文字列を出す（Console で読みにくくなるため）
    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = Colorize(msg);
        Debug.Log($"[AdminUI] {msg}");
    }

    private static string Colorize(string msg)
    {
        if (string.IsNullOrEmpty(msg) || msg.Length == 0 || msg[0] != '[') return msg;

        int close = msg.IndexOf(']');
        if (close < 0) return msg;

        string tag = msg.Substring(0, close + 1);
        string hex = tag switch
        {
            "[OK]"     => AdminUiStyle.StatusOkHex,
            "[WARN]"   => AdminUiStyle.StatusWarnHex,
            "[ERROR]"  => AdminUiStyle.StatusErrorHex,
            "[LAUNCH]" => AdminUiStyle.StatusLaunchHex,
            _          => null,
        };

        return hex == null ? msg : $"<color={hex}>{tag}</color>{msg.Substring(close + 1)}";
    }

    // 件数はステータス行ではなくタブ行の右端に常設する。
    // 以前は RefreshList が毎回 SetStatus("Entries: N ...") を呼んでいたため、
    // 更新完了やエラーの通知が読む前に件数で上書きされて消えていた
    private void UpdateEntryCountLabel()
    {
        if (entryCountText == null) return;

        if (_manager == null)
        {
            entryCountText.text = "全― 件";
            return;
        }

        int total  = _manager.Entries.Count;
        int active = 0;
        foreach (var e in _manager.Entries) if (e.isActive) active++;

        entryCountText.text = $"全{total}件 / 有効{active}件";
    }
}
