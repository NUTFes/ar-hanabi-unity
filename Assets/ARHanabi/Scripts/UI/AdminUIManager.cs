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
//   │  🎆 花火管理                          [QUIT] [閉じる] │
//   │  [テスト打ち上げ] [更新] [CAM] [SPACE] [SETTINGS]     │
//   │  [FRAME] [UFO] [HANABI] [SFX]                        │ ← 宇宙モードON中のみ
//   │  [HAND] [JUMP] [COOLDOWN] [HOLD]                     │ ← SETTINGS ON中のみ
//   │  [IMG%] [IMG] [MATTE] [PERSON]                       │
//   │  ステータステキスト                                   │
//   ├──────────────────────────────────────────────────────┤
//   │  [thumb] 名前.jpg  [有効] [選択]                      │ ← エントリ行
//   │  [thumb] 名前2.jpg [有効] [選択]                      │
//   │  ...                                                 │
//   └──────────────────────────────────────────────────────┘
//   ※ パネルを閉じている間は画面左上に [OPEN] タブが出る
//      （AdminPanel の外に置いてあるので CanvasGroup で一緒に消えない）
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

    [Header("宇宙モード")]
    [SerializeField] private Button           spaceModeButton;
    [SerializeField] private TextMeshProUGUI  spaceModeText;
    [Tooltip("個別トグル4つの行。宇宙モードOFF中は非表示にする")]
    [SerializeField] private Transform        spaceToolbar;
    [SerializeField] private Button           frameToggleButton;
    [SerializeField] private TextMeshProUGUI  frameToggleText;
    [SerializeField] private Button           ufoToggleButton;
    [SerializeField] private TextMeshProUGUI  ufoToggleText;
    [SerializeField] private Button           hanabiModeButton;
    [SerializeField] private TextMeshProUGUI  hanabiModeText;
    [SerializeField] private Button           sfxToggleButton;
    [SerializeField] private TextMeshProUGUI  sfxToggleText;

    [Header("設定（ジェスチャー感度・花火の出し方）")]
    [Tooltip("ビルド後にUnity Editorへ触れない前提で、展示中に調整したくなる値を\n" +
             "ここへ集約している。押すたびにプリセット値を順に切り替える方式\n" +
             "（宇宙モードのHANABIボタンと同じ操作感）。スライダーではなくボタンに\n" +
             "したのは、このAdmin UIに既にある「ボタン+ラベル」の部品だけで\n" +
             "完結させ、新しいUI部品（Slider等）を持ち込まないため")]
    [SerializeField] private Button           settingsModeButton;
    [SerializeField] private TextMeshProUGUI  settingsModeText;
    [Tooltip("感度・花火比率の6ボタンの行。設定モードOFF中は非表示にする")]
    [SerializeField] private Transform        settingsToolbar;
    [SerializeField] private Button           handUpButton;
    [SerializeField] private TextMeshProUGUI  handUpText;
    [SerializeField] private Button           jumpButton;
    [SerializeField] private TextMeshProUGUI  jumpText;
    [SerializeField] private Button           cooldownButton;
    [SerializeField] private TextMeshProUGUI  cooldownText;
    [SerializeField] private Button           holdButton;
    [SerializeField] private TextMeshProUGUI  holdText;
    [SerializeField] private Button           imgChanceButton;
    [SerializeField] private TextMeshProUGUI  imgChanceText;
    [SerializeField] private Button           imgEnableButton;
    [SerializeField] private TextMeshProUGUI  imgEnableText;
    [Tooltip("カメラ映像を画面中央の丸に収めて周囲を黒くする演出のON/OFF。\n" +
             "OFFにすると、カメラのClearFlags・背景Quadのscale・骨格の表示が\n" +
             "すべて元の値に復元される（現場で「あり/なし」を見比べるためのトグル）")]
    [SerializeField] private Button           matteButton;
    [SerializeField] private TextMeshProUGUI  matteText;
    [Tooltip("人として検出するのに必要な確信度。上げると人型のポスターや人形などの\n" +
             "誤検知が減るが、遠い人・暗い場所の人を拾わなくなる。\n" +
             "押すたびに MediaPipe の PoseLandmarker を作り直す（数十ms）")]
    [SerializeField] private Button           personConfButton;
    [SerializeField] private TextMeshProUGUI  personConfText;

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

    [Header("行の見た目")]
    [Tooltip("通常の行の背景色")]
    [SerializeField] private Color rowNormalColor   = new Color(1f, 1f, 1f, 0.04f);
    [Tooltip("選択中の行の背景色（ハイライト）")]
    [SerializeField] private Color rowSelectedColor = new Color(0.35f, 0.55f, 0.95f, 0.35f);

    [Header("終了ボタンの2段階確認")]
    [Tooltip("1回目のクリックから、この秒数内に再クリックされたら実際にアプリを終了する。\n" +
             "誤タップでキオスクを落とさないための猶予")]
    [SerializeField] private float quitConfirmSeconds = 3f;
    [Tooltip("確認待ち状態の終了ボタンの色（警告色）")]
    [SerializeField] private Color quitConfirmColor = new Color(0.85f, 0.15f, 0.15f);

    [Header("ボタンのラベル")]
    [Tooltip("ボタンラベルの自動縮小の下限フォントサイズ")]
    [SerializeField] private float buttonFontSizeMin = 8f;
    [Tooltip("ボタンラベルの自動縮小の上限フォントサイズ")]
    [SerializeField] private float buttonFontSizeMax = 12f;

    // ── 定数 ──
    private const float ButtonMinWidth = 72f;   // 従来の preferredWidth。今は「最小幅」として扱う
    private const float ButtonHeight   = 36f;
    private const float EntryRowHeight = 64f;   // AdminUIBuilder.EntryRowHeight と一致させる

    // ボタンのラベル色。AdminUIBuilder.ButtonLabelColor と同じ値にすること。
    // 以前は白だったが、ツールバーのボタンの背景が既定の白のままで
    // 文字がほぼ読めなかったため、濃紺寄りのダークグレーに統一した
    private static readonly Color ButtonLabelColor = new Color(0.09f, 0.12f, 0.17f, 1f);

    // ── 内部 ──
    private FireworkManager     _manager;
    private FireworkLauncher    _launcher;
    private SpaceModeController _spaceMode;
    private GestureDetector     _gesture;
    private CameraCircleMatte   _matte;
    private PoseLandmarkDetector _poseDetector;
    private FireworkEntry       _selectedEntry;
    private CanvasGroup        _canvasGroup;
    private bool               _isVisible = true;

    // SETTINGS行の開閉。宇宙モードのマスターと違い、この値自体は「効き目」を
    // 持たない純粋なUI表示切替なので、永続化もしない（次回起動時は閉じた状態でよい）
    private bool _settingsToolbarVisible = false;

    // 終了ボタンの2段階確認。行ごとの確認待ちを持つ削除ボタンと違い
    // アプリ全体につき1つしか無いので、行(EntryRow)を介さずここに直接持つ
    private bool       _quitAwaitingConfirm;
    private Coroutine  _quitConfirmRoutine;
    private Image      _quitImage;
    private Color      _quitNormalColor;
    private const string QuitNormalLabel = "QUIT";

    // ジェスチャー感度・花火比率のプリセット値。
    // スライダーではなくボタンで「近い値の次」へ進める方式にしている
    // （NextPreset 参照）ので、厳密な連続値ではなくこの一覧の中だけを巡回する。
    // HandUp/Jump に 1.00 を含めているのは、MainScene に既に保存されている値
    // （handUpThreshold: 1, jumpThreshold: 1）が一覧に無いと、初回クリックで
    // いきなり一番近いプリセットへ大ジャンプしてしまうため
    // ── 1.0 より上まで用意している理由 ──
    //   閾値は「肩幅に対する相対値」で、1.0 = 肩幅ぶん手を上げる、という意味。
    //   上限が 1.0 だと「肩幅ぶん上げれば発火」までしか厳しくできず、
    //   人が密集して腕が写り込む展示では誤発火が止められなかった。
    //   1.0 を超える値は「肩幅の1.5倍・2倍まで上げないと発火しない」を意味し、
    //   物理的に到達可能（万歳すれば手は肩から肩幅2倍ほど上がる）なので実用範囲。
    private static readonly float[] HandUpPresets      = { 0.10f, 0.15f, 0.25f, 0.40f, 0.60f, 1.00f, 1.50f, 2.00f, 2.50f };
    private static readonly float[] JumpPresets        = { 0.03f, 0.06f, 0.10f, 0.15f, 0.25f, 0.40f, 1.00f, 1.50f, 2.00f };

    // 人として検出するのに必要な確信度。MediaPipe の既定は 0.5。
    // 上げるほど誤検知が減るが、遠い人や見切れている人を拾わなくなる
    private static readonly float[] PersonConfPresets  = { 0.30f, 0.40f, 0.50f, 0.60f, 0.70f, 0.80f, 0.90f };
    private static readonly float[] CooldownPresets    = { 0f, 0.3f, 0.5f, 1.0f, 1.5f, 2.0f, 3.0f };
    private static readonly float[] HoldPresets        = { 0.2f, 0.3f, 0.5f, 0.8f, 1.0f, 1.5f };
    private static readonly float[] ImageChancePresets = { 0f, 0.25f, 0.5f, 0.75f, 1.0f };

    // 行GameObject と FireworkEntry の対応表。
    // 選択ハイライトは全行 Destroy → 再生成ではなく、この表を使って背景色だけ差し替える。
    private readonly List<EntryRow> _rows = new();

    // 1行ぶんの UI 参照
    private class EntryRow
    {
        public FireworkEntry   entry;
        public GameObject      rowGO;
        public Image           background;
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
        UpdateSpaceToolbarVisibility();

        // 設定パネル（ジェスチャー感度・花火の出し方）も FireworkManager に依存しないので、
        // 同じ理由で早期 return より先に配線する。
        // GestureDetector はシーンに1つある前提で自動解決する（AdminPanel の外にあるため
        // AdminUIBuilder からはアサインできない。cameraBackground と同じ事情）
        _gesture = FindFirstObjectByType<GestureDetector>();
        if (_gesture == null)
            Debug.LogWarning("[AdminUI] GestureDetector が見つかりません。感度調整は無効です");

        settingsModeButton?.onClick.AddListener(OnSettingsModeClicked);
        handUpButton      ?.onClick.AddListener(OnHandUpClicked);
        jumpButton        ?.onClick.AddListener(OnJumpClicked);
        cooldownButton    ?.onClick.AddListener(OnCooldownClicked);
        holdButton        ?.onClick.AddListener(OnHoldClicked);
        imgChanceButton   ?.onClick.AddListener(OnImgChanceClicked);
        imgEnableButton   ?.onClick.AddListener(OnImgEnableClicked);
        matteButton       ?.onClick.AddListener(OnMatteClicked);
        personConfButton  ?.onClick.AddListener(OnPersonConfClicked);

        // 人の検出閾値は PoseLandmarkDetector が持つ。GestureDetector と同じく
        // AdminPanel の外にあるのでシーンから自動解決する
        _poseDetector = FindFirstObjectByType<PoseLandmarkDetector>();
        if (_poseDetector == null)
            Debug.LogWarning("[AdminUI] PoseLandmarkDetector が見つかりません。人の検出閾値の調整は無効です");

        UpdateSettingsModeLabel();
        UpdateHandUpLabel();
        UpdateJumpLabel();
        UpdateCooldownLabel();
        UpdateHoldLabel();
        UpdateImgChanceLabel();
        UpdateImgEnableLabel();
        UpdateMatteLabel();
        UpdatePersonConfLabel();
        UpdateSettingsToolbarVisibility();

        // 終了ボタンも FireworkManager に依存しないので早期 return より先に配線する。
        // アプリ全体の終了操作なので、これが機能しない状態は避けたい
        if (quitButton != null)
        {
            _quitImage = quitButton.GetComponent<Image>();
            if (_quitImage != null) _quitNormalColor = _quitImage.color;
            quitButton.onClick.AddListener(OnQuitClicked);
        }
        if (quitText != null) quitText.text = QuitNormalLabel;

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
            SetStatus("[ERROR] FireworkManager not found");
            return;
        }

        // ボタンイベント
        testLaunchButton  ?.onClick.AddListener(OnTestLaunchClicked);

        // API 差分取得
        refreshButton?.onClick.AddListener(OnRefreshClicked);

        // エントリ変更の購読
        _manager.OnEntriesChanged += RefreshList;

        RefreshList();
        SetStatus("[OK] Admin UI ready  (F1: show/hide)");
    }

    private void Update()
    {
        HealStuckButtons();
        SyncCameraIndexLabel();

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
        SetStatus("Fetching from API...");

        // コルーチンは FireworkManager 側で回す。
        // このパネルが非アクティブ化されると自分で StartCoroutine したコルーチンは
        // 取得中に死に、IsFetching が立ちっぱなしになる。
        _manager.StartCoroutine(_manager.FetchNewEntriesFromApi((added, err) =>
        {
            SetRefreshInteractable(true);

            // OnEntriesChanged はこのコールバックより先に発火し RefreshList が
            // 既に "Entries: ..." を表示済みのため、ここで上書きしても問題ない
            if (err != null)        SetStatus($"[ERROR] {err}");
            else if (added == 0)    SetStatus("[OK] No new fireworks");
            else                    SetStatus($"[OK] Fetched {added} new");
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

        // ステータス更新
        int total  = _manager.Entries.Count;
        int active = 0;
        foreach (var e in _manager.Entries) if (e.isActive) active++;
        SetStatus($"Entries: {total}  Active: {active}");

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
    private void SelectEntry(FireworkEntry entry)
    {
        _selectedEntry = entry;

        // RefreshList() は全行 Destroy → 再生成になるので使わない。背景色だけ差し替える。
        RefreshRowHighlights();

        if (previewImage != null) previewImage.texture = entry.localTexture;
        if (detailText   != null)
        {
            detailText.text = entry.isConverted
                ? $"Particles: {entry.particleData.particles.Length}\nSize: {entry.particleData.width}x{entry.particleData.height}"
                : "Not converted";
        }
        SetStatus($"Selected: {entry.displayName}");
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
                ? rowSelectedColor
                : rowNormalColor;
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
        rowRT.sizeDelta = new Vector2(0f, EntryRowHeight);

        // 親（Viewport > Content）の VerticalLayoutGroup は子の高さを
        // LayoutElement から決めるため、sizeDelta だけでは効かない。
        // 値は AdminUIBuilder.EntryRowHeight と揃えること。
        var rowLe = rowGO.AddComponent<LayoutElement>();
        rowLe.minHeight       = EntryRowHeight;
        rowLe.preferredHeight = EntryRowHeight;
        rowLe.flexibleHeight  = 0f;

        // 選択ハイライト用の背景（クリックを奪わないよう raycastTarget は切る）
        var bg = rowGO.AddComponent<Image>();
        bg.color         = rowNormalColor;
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

        // [有効/無効] トグル
        TextMeshProUGUI activeLabel = null;
        MakeButton(rowGO.transform, entry.isActive ? "[ON]" : "[OFF]",
            entry.isActive ? new Color(0.1f, 0.7f, 0.3f) : new Color(0.4f, 0.4f, 0.4f),
            () =>
            {
                _manager.SetActive(entry, !entry.isActive);
                // ラベルはOnEntriesChanged → RefreshListで更新される
            }, out activeLabel);

        // [選択] ボタン（プレビューパネルと行ハイライトに反映）
        MakeButton(rowGO.transform, "Select", new Color(0.6f, 0.4f, 0.8f),
            () => SelectEntry(entry));

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
        if (quitText   != null) quitText.text  = "確認?";
        if (_quitImage != null) _quitImage.color = quitConfirmColor;
        SetStatus("[WARN] Tap QUIT again to exit the application");

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

    private void ResetQuitButton()
    {
        _quitAwaitingConfirm = false;
        if (quitText   != null) quitText.text  = QuitNormalLabel;
        if (_quitImage != null) _quitImage.color = _quitNormalColor;
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
        label.text = entry.isConverted
            ? $"<color=#44ff88>Converted</color>\n{entry.particleData.particles.Length} pts"
            : "<color=#888888>Not converted</color>";
        label.fontSize = 11f;
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

    private Button MakeButton(Transform parent, string label, Color bgColor,
        System.Action onClick, out TextMeshProUGUI labelOut, float minWidth = ButtonMinWidth)
    {
        var go  = new GameObject($"Btn_{label}");
        go.transform.SetParent(parent, false);

        // 幅は固定せず「最小幅」として扱う。
        // preferredWidth を -1（未設定）にすると LayoutElement は幅を上書きせず、
        // 下の HorizontalLayoutGroup が算出する値（= ラベルの preferredWidth + padding）が
        // 採用される。日本語ラベル（"確認?" "更新" など）でも切れずに伸びる。
        var le  = go.AddComponent<LayoutElement>();
        le.minWidth        = minWidth;
        le.preferredWidth  = -1f;
        le.minHeight       = ButtonHeight;
        le.preferredHeight = ButtonHeight;

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
        // ツールバーのボタンと同じ濃紺寄りのダークグレーに揃える。
        // 値は AdminUIBuilder.ButtonLabelColor と一致させること
        //（片方だけ変えると一覧の行とツールバーで文字色が混在する）
        tmp.color     = ButtonLabelColor;

        // 溢れ対策: 1行維持したままフォントを自動縮小する
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin      = buttonFontSizeMin;
        tmp.fontSizeMax      = buttonFontSizeMax;
        tmp.fontSize         = buttonFontSizeMax;

        labelOut = tmp;
        return btn;
    }

    // overload（labelOut 不要な場合）
    private Button MakeButton(Transform parent, string label, Color bgColor,
        System.Action onClick, float minWidth = ButtonMinWidth)
    {
        return MakeButton(parent, label, bgColor, onClick, out _, minWidth);
    }

    // ── 宇宙モード ──
    //
    // マスターON/OFFは「個別トグルの設定はそのまま、効き目だけを止める」ためのもの
    // （SpaceModeController 側の Setting/Enabled 分離を参照）。
    // ラベルには常に個別設定値（*Setting）を出す。実効値（*Enabled）を出すと、
    // マスターOFF中は4つとも [OFF] に見えてしまい、個別トグルが勝手にリセット
    // されたかのように誤解される（実際はマスターを戻せば元の組み合わせに戻る）ため。

    private void OnSpaceModeClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleMaster();
        UpdateSpaceModeLabel();
        UpdateSpaceToolbarVisibility();
        Debug.Log($"[AdminUI] SpaceMode: {(_spaceMode.MasterEnabled ? "ON" : "OFF")}");
    }

    private void UpdateSpaceModeLabel()
    {
        if (spaceModeText == null) return;
        spaceModeText.text = _spaceMode != null && _spaceMode.MasterEnabled ? "SPACE [ON]" : "SPACE [OFF]";
    }

    // 宇宙モードOFF中は4つの個別トグル行を隠す。
    // spaceToolbar の親（AdminPanel）の VerticalLayoutGroup は非アクティブな子を
    // 自動でスキップしてリフローするので、SetActive を切り替えるだけで見た目が崩れない
    // （Destroy → 再生成にすると設定値やアサインが失われるので避ける）
    private void UpdateSpaceToolbarVisibility()
    {
        if (spaceToolbar == null) return;
        spaceToolbar.gameObject.SetActive(_spaceMode != null && _spaceMode.MasterEnabled);
    }

    private void OnFrameToggleClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleFrame();
        UpdateFrameToggleLabel();
    }

    private void UpdateFrameToggleLabel()
    {
        if (frameToggleText == null) return;
        frameToggleText.text = _spaceMode != null && _spaceMode.FrameSetting ? "FRAME [ON]" : "FRAME [OFF]";
    }

    private void OnUfoToggleClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleUfo();
        UpdateUfoToggleLabel();
    }

    private void UpdateUfoToggleLabel()
    {
        if (ufoToggleText == null) return;
        ufoToggleText.text = _spaceMode != null && _spaceMode.UfoSetting ? "UFO [ON]" : "UFO [OFF]";
    }

    private void OnHanabiModeClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.CycleFireworkMode();
        UpdateHanabiModeLabel();
    }

    private void UpdateHanabiModeLabel()
    {
        if (hanabiModeText == null) return;
        if (_spaceMode == null)
        {
            hanabiModeText.text = "HANABI [N/A]";
            return;
        }

        hanabiModeText.text = _spaceMode.FireworkSetting switch
        {
            SpaceModeController.SpaceFireworkMode.Mix       => "HANABI [MIX]",
            SpaceModeController.SpaceFireworkMode.SpaceOnly => "HANABI [SPACE]",
            _                                                => "HANABI [OFF]",
        };
    }

    private void OnSfxToggleClicked()
    {
        if (_spaceMode == null) return;
        _spaceMode.ToggleSpaceAudio();
        UpdateSfxToggleLabel();
    }

    private void UpdateSfxToggleLabel()
    {
        if (sfxToggleText == null) return;
        sfxToggleText.text = _spaceMode != null && _spaceMode.SpaceAudioSetting ? "SFX [ON]" : "SFX [OFF]";
    }

    // ── 設定（ジェスチャー感度・花火の出し方）──
    //
    // ビルド後は Unity Editor に一切触れない運用を前提にしたパネル。
    // 会場・客層・投稿写真の状況で変えたくなる値をここに集約している。
    // SETTINGSボタンで行の開閉だけを切り替える（宇宙モードの SpaceToolbar と
    // 同じ考え方だが、こちらは「効き目」を持たない純粋な表示トグルなので
    // 永続化はしない）。個々の値自体（ジェスチャー感度・花火比率）は
    // GestureDetector / FireworkLauncher 側で PlayerPrefs に永続化される

    private void OnSettingsModeClicked()
    {
        _settingsToolbarVisible = !_settingsToolbarVisible;
        UpdateSettingsModeLabel();
        UpdateSettingsToolbarVisibility();
    }

    private void UpdateSettingsModeLabel()
    {
        if (settingsModeText == null) return;
        settingsModeText.text = _settingsToolbarVisible ? "SETTINGS [ON]" : "SETTINGS [OFF]";
    }

    private void UpdateSettingsToolbarVisibility()
    {
        if (settingsToolbar == null) return;
        settingsToolbar.gameObject.SetActive(_settingsToolbarVisible);
    }

    // 現在値に一番近いプリセットの「次」を返す（末尾なら先頭へ戻る）。
    // 完全一致を要求しないのは、PlayerPrefs 経由で復元された値や Inspector で
    // 直接入れた値がプリセット外でも、そこから自然に巡回を始められるようにするため
    private static float NextPreset(float[] presets, float current)
    {
        int nearest = 0;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < presets.Length; i++)
        {
            float diff = Mathf.Abs(presets[i] - current);
            if (diff < bestDiff) { bestDiff = diff; nearest = i; }
        }
        return presets[(nearest + 1) % presets.Length];
    }

    private void OnHandUpClicked()
    {
        if (_gesture == null) return;
        _gesture.HandUpThreshold = NextPreset(HandUpPresets, _gesture.HandUpThreshold);
        UpdateHandUpLabel();
    }

    private void UpdateHandUpLabel()
    {
        if (handUpText == null) return;
        handUpText.text = _gesture != null ? $"HAND [{_gesture.HandUpThreshold:F2}]" : "HAND [N/A]";
    }

    // 人として検出するのに必要な確信度。
    // ジェスチャーの閾値が「検出できた人が手を上げたか」の判定なのに対し、
    // こちらは「そもそも人として拾うか」の手前の段階を絞る
    private void OnPersonConfClicked()
    {
        if (_poseDetector == null) return;
        _poseDetector.PersonConfidence = NextPreset(PersonConfPresets, _poseDetector.PersonConfidence);
        UpdatePersonConfLabel();
        SetStatus($"[OK] 人の検出閾値: {_poseDetector.PersonConfidence:F2}");
    }

    private void UpdatePersonConfLabel()
    {
        if (personConfText == null) return;
        personConfText.text = _poseDetector != null
            ? $"PERSON [{_poseDetector.PersonConfidence:F2}]"
            : "PERSON [N/A]";
    }

    private void OnJumpClicked()
    {
        if (_gesture == null) return;
        _gesture.JumpThreshold = NextPreset(JumpPresets, _gesture.JumpThreshold);
        UpdateJumpLabel();
    }

    private void UpdateJumpLabel()
    {
        if (jumpText == null) return;
        jumpText.text = _gesture != null ? $"JUMP [{_gesture.JumpThreshold:F2}]" : "JUMP [N/A]";
    }

    private void OnCooldownClicked()
    {
        if (_gesture == null) return;
        _gesture.GestureCooldown = NextPreset(CooldownPresets, _gesture.GestureCooldown);
        UpdateCooldownLabel();
    }

    private void UpdateCooldownLabel()
    {
        if (cooldownText == null) return;
        cooldownText.text = _gesture != null ? $"COOLDOWN [{_gesture.GestureCooldown:F1}s]" : "COOLDOWN [N/A]";
    }

    private void OnHoldClicked()
    {
        if (_gesture == null) return;
        _gesture.PoseHoldDuration = NextPreset(HoldPresets, _gesture.PoseHoldDuration);
        UpdateHoldLabel();
    }

    private void UpdateHoldLabel()
    {
        if (holdText == null) return;
        holdText.text = _gesture != null ? $"HOLD [{_gesture.PoseHoldDuration:F1}s]" : "HOLD [N/A]";
    }

    private void OnImgChanceClicked()
    {
        if (_launcher == null) return;
        _launcher.ImageFireworkChance = NextPreset(ImageChancePresets, _launcher.ImageFireworkChance);
        UpdateImgChanceLabel();
    }

    private void UpdateImgChanceLabel()
    {
        if (imgChanceText == null) return;
        imgChanceText.text = _launcher != null
            ? $"IMG% [{Mathf.RoundToInt(_launcher.ImageFireworkChance * 100)}]"
            : "IMG% [N/A]";
    }

    // 比率スライダーと違い、こちらは即座に「画像花火を一切出さない」緊急スイッチ。
    // API/投稿写真パイプラインが壊れたときに、比率を0にするより速く・確実に止められる
    private void OnImgEnableClicked()
    {
        if (_launcher == null) return;
        _launcher.EnableImageFirework = !_launcher.EnableImageFirework;
        UpdateImgEnableLabel();
    }

    private void UpdateImgEnableLabel()
    {
        if (imgEnableText == null) return;
        imgEnableText.text = _launcher != null
            ? (_launcher.EnableImageFirework ? "IMG [ON]" : "IMG [OFF]")
            : "IMG [N/A]";
    }

    // カメラ映像を画面中央の丸に収める演出のトグル。
    //
    // CameraCircleMatte は Main Camera へ自動アタッチされるので、ここでは
    // 見つけて叩くだけ。OFF にすると同コンポーネントが控えておいた元の値
    // （カメラのClearFlags・背景Quadのscale・骨格の表示）をすべて復元するので、
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
        if (matteText == null) return;

        var matte = ResolveMatte();
        matteText.text = matte != null
            ? (matte.MatteEnabled ? "MATTE [ON]" : "MATTE [OFF]")
            : "MATTE [N/A]";
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
        SetStatus($"カメラ切替: {CameraLabelText()}");
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

    // 例: "CAM [2/4] HD Webcam"。切替中は index の代わりに "..." を出す
    private string CameraLabelText()
    {
        if (cameraBackground == null) return "CAM [N/A]";

        if (cameraBackground.IsSwitching) return "CAM [...] 切替中";

        return $"CAM [{cameraBackground.CurrentIndex}/{cameraBackground.DeviceCount}] " +
               $"{cameraBackground.CurrentDeviceName}";
    }

    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
        Debug.Log($"[AdminUI] {msg}");
    }
}
