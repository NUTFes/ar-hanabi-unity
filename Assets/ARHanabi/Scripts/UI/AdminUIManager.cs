using UnityEngine;
using UnityEngine.UI;
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
//   │  [画像を追加] [全変換] [テスト打ち上げ] │
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
//
// 将来拡張:
//   ・[APIから取得] ボタン → FireworkManager.FetchFromApi() を呼ぶ
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

    [Header("Preview (optional)")]
    [SerializeField] private RawImage        previewImage;
    [SerializeField] private TextMeshProUGUI detailText;

    [Header("Test Launch Position")]
    [SerializeField] private Vector3 testLaunchPosition = new Vector3(0f, 5f, 0f);

    // ── 内部 ──
    private FireworkManager       _manager;
    private FireworkEntry         _selectedEntry;
    private readonly List<GameObject> _rowObjects = new();

    // ── ライフサイクル ──
    private void Start()
    {
        Debug.Log("[AdminUI] Start called - v2");
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
        closeButton       ?.onClick.AddListener(() => gameObject.SetActive(false));

        // エントリ変更の購読
        _manager.OnEntriesChanged += RefreshList;

        RefreshList();
        SetStatus("[OK] Admin UI ready");
    }

    private void OnDestroy()
    {
        if (_manager != null)
            _manager.OnEntriesChanged -= RefreshList;
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
        SetStatus("Converting...");
        _manager.ConvertAll();
        SetStatus("[OK] All converted");
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
            _manager.LaunchEntry(_selectedEntry, testLaunchPosition);
            SetStatus($"[LAUNCH] {_selectedEntry.displayName}");
        }
        else
        {
            _manager.LaunchRandom(testLaunchPosition);
            SetStatus("[LAUNCH] Random");
        }
    }

    // ── エントリ一覧の再描画 ──
    public void RefreshList()
    {
        // 既存行を削除
        foreach (var go in _rowObjects)
            if (go != null) Destroy(go);
        _rowObjects.Clear();

        if (_manager == null || entryListContent == null) return;

        // ステータス更新
        int total  = _manager.Entries.Count;
        int active = 0;
        foreach (var e in _manager.Entries) if (e.isActive) active++;
        SetStatus($"Entries: {total}  Active: {active}");

        // エントリ行を生成
        foreach (var entry in _manager.Entries)
        {
            var rowGO = BuildEntryRow(entry);
            rowGO.transform.SetParent(entryListContent, false);
            _rowObjects.Add(rowGO);
        }
    }

    // ── エントリ行のコード生成 ──
    // Prefab を用意しない場合のフォールバック。
    // Prefab がある場合は BuildEntryRow の中身を差し替えてください。
    private GameObject BuildEntryRow(FireworkEntry entry)
    {
        // ── 行ルート ──
        var rowGO  = new GameObject($"Row_{entry.displayName}");
        var rowRT  = rowGO.AddComponent<RectTransform>();
        rowRT.sizeDelta = new Vector2(0f, 64f);

        var hLayout = rowGO.AddComponent<HorizontalLayoutGroup>();
        hLayout.spacing        = 8f;
        hLayout.padding        = new RectOffset(8, 8, 6, 6);
        hLayout.childAlignment = TextAnchor.MiddleLeft;
        hLayout.childForceExpandWidth  = false;
        hLayout.childForceExpandHeight = true;

        // サムネイル
        var thumb    = MakeChild<RawImage>(rowGO.transform, "Thumb", new Vector2(52f, 52f));
        thumb.texture = entry.localTexture;

        // 名前ラベル
        var nameLbl  = MakeChild<TextMeshProUGUI>(rowGO.transform, "Name", flexible: true);
        nameLbl.text      = entry.displayName;
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

        // [選択] ボタン（プレビューパネルに反映）
        MakeButton(rowGO.transform, "Select", new Color(0.6f, 0.4f, 0.8f), () =>
        {
            _selectedEntry = entry;
            if (previewImage != null)  previewImage.texture = entry.localTexture;
            if (detailText   != null)
            {
                detailText.text = entry.isConverted
                    ? $"Particles: {entry.particleData.particles.Length}\nSize: {entry.particleData.width}x{entry.particleData.height}"
                    : "Not converted";
            }
            SetStatus($"Selected: {entry.displayName}");
        });

        // [削除] ボタン
        MakeButton(rowGO.transform, "✕", new Color(0.8f, 0.2f, 0.2f), () =>
        {
            if (_selectedEntry == entry) _selectedEntry = null;
            _manager.RemoveEntry(entry);
        });

        return rowGO;
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
        System.Action onClick, out TextMeshProUGUI labelOut)
    {
        var go  = new GameObject($"Btn_{label}");
        go.transform.SetParent(parent, false);

        var le  = go.AddComponent<LayoutElement>();
        le.preferredWidth  = 72f;
        le.preferredHeight = 36f;

        var img = go.AddComponent<Image>();
        img.color = bgColor;

        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(() => onClick());

        // テキスト
        var textGO  = new GameObject("Label");
        textGO.transform.SetParent(go.transform, false);
        var tmp     = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 12f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color     = Color.white;
        var trt       = textGO.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.sizeDelta = Vector2.zero;

        labelOut = tmp;
        return btn;
    }

    // overload（labelOut 不要な場合）
    private Button MakeButton(Transform parent, string label, Color bgColor,
        System.Action onClick)
    {
        return MakeButton(parent, label, bgColor, onClick, out _);
    }

    private void SetStatus(string msg)
    {
        if (statusText != null) statusText.text = msg;
        Debug.Log($"[AdminUI] {msg}");
    }
}