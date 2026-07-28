using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.IO;

// ===== AdminUIManager =====
// 管理画面 UI を制御するコンポーネント
//
// 画面レイアウト（Canvas 上）:
//   ┌─────────────────────────────────────┐
//   │  🎆 花火管理                  [閉じる] │
//   │  [画像を追加] [全変換] [テスト打ち上げ] [更新] │
//   │  ステータステキスト                    │
//   ├─────────────────────────────────────┤
//   │  [thumb] 名前.jpg  [変換] [有効] [削除]│  ← エントリ行
//   │  [thumb] 名前2.jpg [変換] [有効] [削除]│
//   │  ...                                  │
//   └─────────────────────────────────────┘
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
    [SerializeField] private Button    addImageButton;
    [SerializeField] private Button    convertAllButton;
    [SerializeField] private Button    testLaunchButton;
    [SerializeField] private Button    closeButton;
    [SerializeField] private Transform entryListContent;   // ScrollView > Viewport > Content
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("背景除去")]
    [SerializeField] private UnityEngine.UI.Button segmentationToggleButton;
    [SerializeField] private TMPro.TextMeshProUGUI  segmentationToggleText;
    [SerializeField] private SelfieSegmentationController selfieSegmentation;

    [Header("API")]
    [Tooltip("DBから新規花火を差分取得する更新ボタン")]
    [SerializeField] private Button refreshButton;

    [Header("Preview (optional)")]
    [SerializeField] private RawImage        previewImage;
    [SerializeField] private TextMeshProUGUI detailText;

    [Header("Test Launch Position")]
    [SerializeField] private Vector3 testLaunchPosition = new Vector3(0f, 5f, 0f);

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

    [Header("削除の2段階確認")]
    [Tooltip("1回目のクリックから、この秒数内に再クリックされたら実際に削除する")]
    [SerializeField] private float deleteConfirmSeconds = 3f;
    [Tooltip("確認待ち状態の削除ボタンの色（警告色）")]
    [SerializeField] private Color deleteConfirmColor = new Color(0.95f, 0.6f, 0.1f);

    [Header("ボタンのラベル")]
    [Tooltip("ボタンラベルの自動縮小の下限フォントサイズ")]
    [SerializeField] private float buttonFontSizeMin = 8f;
    [Tooltip("ボタンラベルの自動縮小の上限フォントサイズ")]
    [SerializeField] private float buttonFontSizeMax = 12f;

    // ── 定数 ──
    private const float ButtonMinWidth = 72f;   // 従来の preferredWidth。今は「最小幅」として扱う
    private const float ButtonHeight   = 36f;

    // ── 内部 ──
    private FireworkManager _manager;
    private FireworkEntry   _selectedEntry;
    private CanvasGroup     _canvasGroup;
    private bool            _isVisible = true;
    private bool            _isConvertingAll;

    // 行GameObject と FireworkEntry の対応表。
    // 選択ハイライトは全行 Destroy → 再生成ではなく、この表を使って背景色だけ差し替える。
    private readonly List<EntryRow> _rows = new();

    // 1行ぶんの UI 参照
    private class EntryRow
    {
        public FireworkEntry   entry;
        public GameObject      rowGO;
        public Image           background;
        public Image           deleteImage;
        public TextMeshProUGUI deleteLabel;
        public Color           deleteNormalColor;
        public string          deleteNormalLabel;
        public Coroutine       confirmRoutine;   // 確認待ちタイマー（AdminUIManager 側で回す）
        public bool            awaitingConfirm;
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

        _manager = FireworkManager.Instance;
        if (_manager == null)
        {
            SetStatus("[ERROR] FireworkManager not found");
            return;
        }

        // ボタンイベント
        addImageButton    ?.onClick.AddListener(OnAddImageClicked);
        convertAllButton  ?.onClick.AddListener(OnConvertAllClicked);
        testLaunchButton  ?.onClick.AddListener(OnTestLaunchClicked);
        closeButton       ?.onClick.AddListener(() => SetVisible(false));

        // 背景除去トグル
        segmentationToggleButton?.onClick.AddListener(OnSegmentationToggleClicked);
        UpdateSegmentationButtonLabel();

        // API 差分取得
        refreshButton?.onClick.AddListener(OnRefreshClicked);

        // エントリ変更の購読
        _manager.OnEntriesChanged += RefreshList;

        RefreshList();
        SetStatus("[OK] Admin UI ready  (F1: show/hide)");
    }

    private void Update()
    {
        if (!toggleWithF1) return;

        // 新 Input System 専用プロジェクト（activeInputHandler: 1）なので
        // Input.GetKeyDown は InvalidOperationException になる。Keyboard を直接見る。
        var keyboard = Keyboard.current;
        if (keyboard == null) return;   // キーボード未接続（モバイル等）では何もしない

        if (keyboard.f1Key.wasPressedThisFrame)
            ToggleVisible();
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
        if (_canvasGroup == null) return;

        // GameObject は落とさない（コルーチンと Update を生かしたまま見た目だけ消す）
        _canvasGroup.alpha          = value ? 1f : 0f;
        _canvasGroup.interactable   = value;
        _canvasGroup.blocksRaycasts = value;
    }

    // ── ボタンハンドラ ──

    private void OnAddImageClicked()
    {
#if UNITY_EDITOR
        string path = UnityEditor.EditorUtility.OpenFilePanel(
            "Select Image", "", "jpg,jpeg,png");
        if (string.IsNullOrEmpty(path)) return;
        StartCoroutine(LoadImageCoroutine(path));
#else
        SetStatus("[WARN] Editor only");
#endif
    }

    private IEnumerator LoadImageCoroutine(string path)
    {
        SetStatus($"Loading: {Path.GetFileName(path)}");
        yield return null;  // 1フレーム待ってUIを更新

        byte[] bytes = File.ReadAllBytes(path);
        var tex = new Texture2D(2, 2);
        if (!tex.LoadImage(bytes))
        {
            SetStatus("[ERROR] Load failed");
            yield break;
        }

        string name = Path.GetFileNameWithoutExtension(path);
        _manager.AddLocalEntry(name, tex);
        SetStatus($"[OK] Added: {name}");
    }

    private void OnConvertAllClicked()
    {
        if (_manager == null) return;
        if (_isConvertingAll)
        {
            SetStatus("[WARN] Already converting");
            return;
        }

        _isConvertingAll = true;
        SetConvertAllInteractable(false);
        SetStatus("Converting...");

        // 変換は重いのでコルーチン版を使う（同期版だと "Converting..." が
        // 同一フレーム内で上書きされ、画面に一度も出ないまま固まる）。
        // コルーチンは FireworkManager 側で回す。OnRefreshClicked と同じ理由で、
        // このパネルが外部から SetActive(false) されても変換が中断しないようにするため。
        _manager.StartCoroutine(_manager.ConvertAllCoroutine(count =>
        {
            _isConvertingAll = false;
            SetConvertAllInteractable(true);
            SetStatus($"[OK] Converted {count}");
        }));
    }

    private void OnTestLaunchClicked()
    {
        if (_selectedEntry != null)
        {
            if (!_selectedEntry.isConverted)
            {
                SetStatus("[WARN] Convert first");
                return;
            }
            FireworkAudioPlayer.Instance?.PlayLaunch();
            _manager.LaunchEntry(_selectedEntry, testLaunchPosition);
            SetStatus($"[LAUNCH] {_selectedEntry.displayName}");
        }
        else
        {
            FireworkAudioPlayer.Instance?.PlayLaunch();
            _manager.LaunchRandom(testLaunchPosition);
            SetStatus("[LAUNCH] Random");
        }
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

    private void SetConvertAllInteractable(bool value)
    {
        if (convertAllButton != null) convertAllButton.interactable = value;
    }

    // ── エントリ一覧の再描画 ──
    public void RefreshList()
    {
        // 既存行を削除（確認待ちタイマーも止める）
        foreach (var row in _rows)
        {
            if (row == null) continue;
            StopConfirmRoutine(row);
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
        rowRT.sizeDelta = new Vector2(0f, 64f);

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

        // [変換] ボタン
        MakeButton(rowGO.transform, "Convert", new Color(0.2f, 0.5f, 0.9f), () =>
        {
            _manager.ConvertEntry(entry);
            RefreshRowStatus(statLbl, entry);
            SetStatus($"[OK] Converted: {entry.displayName}");
        });

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

        // [削除] ボタン（2段階確認。1回目で「確認?」、時間切れで元に戻る）
        var deleteColor = new Color(0.8f, 0.2f, 0.2f);
        var delBtn = MakeButton(rowGO.transform, "✕", deleteColor,
            () => OnDeleteClicked(row), out var delLabel);
        row.deleteImage       = delBtn.image;
        row.deleteLabel       = delLabel;
        row.deleteNormalColor = deleteColor;
        row.deleteNormalLabel = "✕";

        return row;
    }

    // ── 削除の2段階確認 ──

    private void OnDeleteClicked(EntryRow row)
    {
        if (row == null || row.entry == null || _manager == null) return;

        if (!row.awaitingConfirm)
        {
            BeginDeleteConfirm(row);
            return;
        }

        // 2回目のクリック → 実際に削除
        StopConfirmRoutine(row);
        row.awaitingConfirm = false;

        var entry = row.entry;
        if (_selectedEntry == entry) ClearSelection();

        SetStatus($"[OK] Removed: {entry.displayName}");
        _manager.RemoveEntry(entry);   // → OnEntriesChanged → RefreshList で行が作り直される
    }

    private void BeginDeleteConfirm(EntryRow row)
    {
        // 他の行の確認待ちは畳んでおく（誤タップの取り違えを防ぐ）
        foreach (var other in _rows)
        {
            if (other == null || other == row || !other.awaitingConfirm) continue;
            StopConfirmRoutine(other);
            ResetDeleteButton(other);
        }

        row.awaitingConfirm = true;
        if (row.deleteLabel != null) row.deleteLabel.text  = "確認?";
        if (row.deleteImage != null) row.deleteImage.color = deleteConfirmColor;

        // タイマーは行ではなく this（AdminUIManager）で回す。
        // 行が Destroy されてもコルーチンが道連れにならないようにするため。
        StopConfirmRoutine(row);
        row.confirmRoutine = StartCoroutine(DeleteConfirmTimeout(row));

        SetStatus($"[WARN] Tap again to delete: {row.entry.displayName}");
    }

    private IEnumerator DeleteConfirmTimeout(EntryRow row)
    {
        float elapsed = 0f;
        while (elapsed < deleteConfirmSeconds)
        {
            // 行が作り直された / 破棄された場合はここで抜ける
            if (row == null || row.rowGO == null) yield break;
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        if (row == null) yield break;
        row.confirmRoutine = null;
        row.awaitingConfirm = false;
        ResetDeleteButton(row);

        if (row.rowGO != null && row.entry != null)
            SetStatus($"Delete cancelled: {row.entry.displayName}");
    }

    private void ResetDeleteButton(EntryRow row)
    {
        if (row == null) return;
        row.awaitingConfirm = false;
        if (row.deleteLabel != null) row.deleteLabel.text  = row.deleteNormalLabel;
        if (row.deleteImage != null) row.deleteImage.color = row.deleteNormalColor;
    }

    private void StopConfirmRoutine(EntryRow row)
    {
        if (row == null || row.confirmRoutine == null) return;
        StopCoroutine(row.confirmRoutine);
        row.confirmRoutine = null;
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
        tmp.color     = Color.white;

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

    // ── セグメンテーション ON/OFF ──

    private void OnSegmentationToggleClicked()
    {
        if (selfieSegmentation == null) return;
        selfieSegmentation.SetEnabled(!selfieSegmentation.IsEnabled);
        UpdateSegmentationButtonLabel();
        Debug.Log($"[AdminUI] Segmentation: {(selfieSegmentation.IsEnabled ? "ON" : "OFF")}");
    }

    private void UpdateSegmentationButtonLabel()
    {
        if (segmentationToggleText == null) return;

        if (selfieSegmentation == null)
        {
            segmentationToggleText.text = "BG Remove [N/A]";
            return;
        }

        segmentationToggleText.text = selfieSegmentation.IsEnabled
            ? "BG Remove [ON]"
            : "BG Remove [OFF]";
    }

    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
        Debug.Log($"[AdminUI] {msg}");
    }
}
