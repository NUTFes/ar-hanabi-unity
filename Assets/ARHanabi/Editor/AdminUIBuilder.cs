#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// ===== AdminUIBuilder =====
// 管理画面（AdminCanvas > AdminPanel）のレイアウトをコードから組み直す Editor 拡張。
//
// なぜ必要か:
//   従来 AdminPanel の中身は全要素が「パネル中心アンカー＋絶対座標」で手置きされており、
//   ・StatusText と Segmentation/Refresh ボタンが 50px 重なっていた
//   ・EntryScrollView が 800x200 しかなく、行(64px)が同時に3行しか見えなかった
//   ・1920x1080 のうち中央 800x720 しか使っておらず下 616px が死んでいた
//   ・要素を1つ足すたびに全部の Y を手で振り直す必要があった
//   という状態だった。LayoutGroup ベースに移し、以後の変更をコードで回せるようにする。
//
// 使い方:
//   1. MainScene を開く
//   2. メニュー ARHanabi > Admin UI を再構築
//   3. 気に入らなければ Ctrl+Z（Undo 1回で全部戻る）
//   4. 問題なければ Ctrl+S でシーンを保存
//
//   ARHanabi > Admin UI のレイアウトを出力 で、現在の実測値を Console に吐ける。
//   見た目の相談をするときはこの出力を貼ると話が早い。
//
// 冪等性:
//   何度実行しても同じ結果になる。既存の GameObject は名前で探して「作り直さず再利用」
//   するため、Inspector のアサインや onClick の設定は失われない。
//   足りないものだけ新規作成し、最後に AdminUIManager の参照を貼り直す。

public static class AdminUIBuilder
{
    // ── レイアウト定数（ここを変えれば見た目が変わる）──
    private const int   PanelPadding     = 24;
    private const int   PanelSpacing     = 16;

    private const float HeaderHeight     = 64f;
    private const float ToolbarHeight    = 72f;
    private const float StatusHeight     = 40f;

    private const float ToolbarSpacing   = 12f;
    private const float ToolbarBtnMinW   = 160f;

    private const float ScrollbarWidth   = 20f;
    private const float TitleFontSize    = 40f;
    private const float StatusFontSize   = 28f;
    private const float ButtonFontMin    = 18f;
    private const float ButtonFontMax    = 32f;

    // 行の高さ。AdminUIManager.BuildEntryRow() の LayoutElement と揃えること
    private const float EntryRowHeight   = 64f;

    // ツールバーに並べる順番。この配列の順で左から並ぶ
    private static readonly string[] ToolbarButtonOrder =
    {
        "AddImageButton",
        "ConvertAllButton",
        "TestLaunchButton",
        "RefreshButton",
        "SegmentationToggleButton",
    };

    // ── メニュー: 再構築 ──

    [MenuItem("ARHanabi/Admin UI を再構築", false, 100)]
    public static void Rebuild()
    {
        var panel = FindAdminPanel(out string error);
        if (panel == null)
        {
            EditorUtility.DisplayDialog("Admin UI を再構築", error, "OK");
            return;
        }

        Undo.SetCurrentGroupName("Admin UI を再構築");
        int group = Undo.GetCurrentGroup();
        Undo.RegisterFullObjectHierarchyUndo(panel.gameObject, "Admin UI を再構築");

        var log = new StringBuilder();
        log.AppendLine("[AdminUIBuilder] 再構築を開始");

        // ── パネル自身 ──
        StretchFull(panel.GetComponent<RectTransform>());

        var panelLayout = GetOrAdd<VerticalLayoutGroup>(panel.gameObject);
        panelLayout.padding                = new RectOffset(PanelPadding, PanelPadding, PanelPadding, PanelPadding);
        panelLayout.spacing                = PanelSpacing;
        panelLayout.childAlignment         = TextAnchor.UpperCenter;
        panelLayout.childControlWidth      = true;
        panelLayout.childControlHeight     = true;
        panelLayout.childForceExpandWidth  = true;
        panelLayout.childForceExpandHeight = false;

        // ── セクションを用意（順序が Hierarchy 上の並び順 = 表示順になる）──
        var header  = EnsureSection(panel, "Header",  log);
        var toolbar = EnsureSection(panel, "Toolbar", log);
        var status  = FindOrCreateChild(panel, "StatusText", log);
        var scroll  = FindOrCreateChild(panel, "EntryScrollView", log);

        header .SetSiblingIndex(0);
        toolbar.SetSiblingIndex(1);
        status .SetSiblingIndex(2);
        scroll .SetSiblingIndex(3);

        BuildHeader(panel, header, log);
        BuildToolbar(toolbar, log);
        BuildStatus(status, log);
        BuildScrollView(scroll, log);

        // ── AdminUIManager の参照を貼り直す ──
        WireManager(panel, log);

        Undo.CollapseUndoOperations(group);
        EditorSceneManager.MarkSceneDirty(panel.gameObject.scene);
        EditorUtility.SetDirty(panel.gameObject);

        log.AppendLine("[AdminUIBuilder] 完了。Ctrl+S で保存、Ctrl+Z で元に戻せます。");
        Debug.Log(log.ToString());
    }

    // ── メニュー: 現状出力 ──

    [MenuItem("ARHanabi/Admin UI のレイアウトを出力", false, 101)]
    public static void DumpLayout()
    {
        var panel = FindAdminPanel(out string error);
        if (panel == null)
        {
            Debug.LogWarning($"[AdminUIBuilder] {error}");
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("[AdminUIBuilder] 現在のレイアウト");
        DumpRecursive(panel.GetComponent<RectTransform>(), 0, sb);
        Debug.Log(sb.ToString());
    }

    private static void DumpRecursive(RectTransform rt, int depth, StringBuilder sb)
    {
        string pad = new string(' ', depth * 2);
        var comps = new List<string>();
        foreach (var c in rt.GetComponents<Component>())
        {
            if (c == null || c is RectTransform || c is CanvasRenderer) continue;
            comps.Add(c.GetType().Name);
        }

        sb.AppendLine($"{pad}{rt.name}{(rt.gameObject.activeSelf ? "" : "  [INACTIVE]")}");
        sb.AppendLine($"{pad}   anchor[{rt.anchorMin.x},{rt.anchorMin.y}]-[{rt.anchorMax.x},{rt.anchorMax.y}] " +
                      $"pos({rt.anchoredPosition.x},{rt.anchoredPosition.y}) " +
                      $"size({rt.sizeDelta.x},{rt.sizeDelta.y}) rect({rt.rect.width:F0}x{rt.rect.height:F0})");
        if (comps.Count > 0) sb.AppendLine($"{pad}   {string.Join(", ", comps)}");

        for (int i = 0; i < rt.childCount; i++)
        {
            if (rt.GetChild(i) is RectTransform child)
                DumpRecursive(child, depth + 1, sb);
        }
    }

    // ── セクション構築 ──

    private static void BuildHeader(Transform panel, Transform header, StringBuilder log)
    {
        var layout = GetOrAdd<HorizontalLayoutGroup>(header.gameObject);
        layout.spacing                = ToolbarSpacing;
        layout.childAlignment         = TextAnchor.MiddleLeft;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = false;
        layout.childForceExpandHeight = true;

        SetHeight(header, HeaderHeight);

        // タイトル（装飾。AdminUIManager からは参照しない）
        var title = FindOrCreateChild(header, "TitleText", log);
        var titleText = GetOrAdd<TextMeshProUGUI>(title.gameObject);
        if (string.IsNullOrEmpty(titleText.text) || titleText.text == "New Text")
            titleText.text = "花火管理";
        titleText.fontSize  = TitleFontSize;
        titleText.alignment = TextAlignmentOptions.MidlineLeft;
        Flexible(title, flexibleWidth: 1f);

        // 閉じるボタンは既存を再利用してヘッダー右端へ移す
        var close = FindDescendant(panel, "CloseButton");
        if (close != null)
        {
            Reparent(close, header);
            close.SetAsLastSibling();
            StyleButton(close, minWidth: 120f, height: HeaderHeight - 8f, flexible: false);
            log.AppendLine("  CloseButton を Header に移動");
        }
        else
        {
            log.AppendLine("  [WARN] CloseButton が見つかりません");
        }
    }

    private static void BuildToolbar(Transform toolbar, StringBuilder log)
    {
        var layout = GetOrAdd<HorizontalLayoutGroup>(toolbar.gameObject);
        layout.spacing                = ToolbarSpacing;
        layout.childAlignment         = TextAnchor.MiddleCenter;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = false;   // flexibleWidth で分配する
        layout.childForceExpandHeight = true;

        SetHeight(toolbar, ToolbarHeight);

        // 既存ボタンを ToolbarButtonOrder の順に集める
        var panel = toolbar.parent;
        for (int i = 0; i < ToolbarButtonOrder.Length; i++)
        {
            string name = ToolbarButtonOrder[i];
            var btn = FindDescendant(panel, name);
            if (btn == null)
            {
                log.AppendLine($"  [WARN] {name} が見つからないためスキップ");
                continue;
            }

            Reparent(btn, toolbar);
            btn.SetSiblingIndex(i);
            StyleButton(btn, ToolbarBtnMinW, ToolbarHeight - 8f, flexible: true);
        }
        log.AppendLine($"  Toolbar に {toolbar.childCount} 個のボタンを配置");
    }

    private static void BuildStatus(Transform status, StringBuilder log)
    {
        var text = GetOrAdd<TextMeshProUGUI>(status.gameObject);
        text.fontSize  = StatusFontSize;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode     = TextOverflowModes.Ellipsis;

        SetHeight(status, StatusHeight);
        log.AppendLine("  StatusText を整列");
    }

    private static void BuildScrollView(Transform scroll, StringBuilder log)
    {
        // 【バグ修正】ScrollRect と同じ GameObject に VerticalLayoutGroup が付いていた。
        // これは Viewport / Scrollbar を縦に並べようとしてアンカーを上書きするため、
        // Scrollbar の値が壊れていた（pos(0,-72.33) size(0,10) など）。
        var strayLayout = scroll.GetComponent<VerticalLayoutGroup>();
        if (strayLayout != null)
        {
            Undo.DestroyObjectImmediate(strayLayout);
            log.AppendLine("  [FIX] EntryScrollView の VerticalLayoutGroup を削除（ScrollRect と競合していた）");
        }
        var strayFitter = scroll.GetComponent<ContentSizeFitter>();
        if (strayFitter != null)
        {
            Undo.DestroyObjectImmediate(strayFitter);
            log.AppendLine("  [FIX] EntryScrollView の ContentSizeFitter を削除");
        }

        // 残りの縦幅を全部使う。これで一覧が3行 → 10行以上見えるようになる
        Flexible(scroll, flexibleHeight: 1f);
        var scrollLe = GetOrAdd<LayoutElement>(scroll.gameObject);
        scrollLe.minHeight = 200f;

        var rect = GetOrAdd<ScrollRect>(scroll.gameObject);

        // Viewport
        var viewport = FindOrCreateChild(scroll, "Viewport", log);
        var viewportRt = viewport.GetComponent<RectTransform>();
        viewportRt.anchorMin        = Vector2.zero;
        viewportRt.anchorMax        = Vector2.one;
        viewportRt.pivot            = new Vector2(0f, 1f);
        viewportRt.offsetMin        = Vector2.zero;
        viewportRt.offsetMax        = new Vector2(-ScrollbarWidth, 0f);   // 縦スクロールバーの分だけ空ける
        viewportRt.anchoredPosition = Vector2.zero;
        GetOrAdd<Image>(viewport.gameObject);
        GetOrAdd<Mask>(viewport.gameObject).showMaskGraphic = false;

        // Content
        var content = FindOrCreateChild(viewport, "Content", log);
        var contentRt = content.GetComponent<RectTransform>();
        contentRt.anchorMin        = new Vector2(0f, 1f);
        contentRt.anchorMax        = new Vector2(1f, 1f);
        contentRt.pivot            = new Vector2(0f, 1f);
        contentRt.sizeDelta        = new Vector2(0f, 0f);
        contentRt.anchoredPosition = Vector2.zero;

        var contentLayout = GetOrAdd<VerticalLayoutGroup>(content.gameObject);
        contentLayout.padding                = new RectOffset(4, 4, 4, 4);
        contentLayout.spacing                = 4f;
        contentLayout.childAlignment         = TextAnchor.UpperLeft;
        contentLayout.childControlWidth      = true;
        contentLayout.childControlHeight     = true;
        contentLayout.childForceExpandWidth  = true;
        contentLayout.childForceExpandHeight = false;

        var fitter = GetOrAdd<ContentSizeFitter>(content.gameObject);
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        // 縦スクロールバーを右端に張り付ける
        var vbar = FindDescendant(scroll, "Scrollbar Vertical");
        if (vbar != null)
        {
            var vbarRt = vbar.GetComponent<RectTransform>();
            vbarRt.anchorMin        = new Vector2(1f, 0f);
            vbarRt.anchorMax        = new Vector2(1f, 1f);
            vbarRt.pivot            = new Vector2(1f, 1f);
            vbarRt.sizeDelta        = new Vector2(ScrollbarWidth, 0f);
            vbarRt.anchoredPosition = Vector2.zero;
            rect.verticalScrollbar  = vbar.GetComponent<Scrollbar>();
            log.AppendLine("  Scrollbar Vertical を右端に配置");
        }

        // 横スクロールは不要（行は幅いっぱいに伸びる）
        var hbar = FindDescendant(scroll, "Scrollbar Horizontal");
        if (hbar != null && hbar.gameObject.activeSelf)
        {
            Undo.RecordObject(hbar.gameObject, "disable horizontal scrollbar");
            hbar.gameObject.SetActive(false);
            log.AppendLine("  Scrollbar Horizontal を無効化（横スクロールは使わない）");
        }
        rect.horizontalScrollbar = null;

        rect.content    = contentRt;
        rect.viewport   = viewportRt;
        rect.horizontal = false;
        rect.vertical   = true;
        rect.movementType = ScrollRect.MovementType.Clamped;
        rect.scrollSensitivity = EntryRowHeight * 0.5f;
        // AutoHideAndExpandViewport は ScrollRect が Viewport のサイズを乗っ取るため、
        // 上で入れた offsetMax(-ScrollbarWidth) が無意味になる。
        // AutoHide なら幅は常に確保されたまま、不要なときだけバーが消えるので予測しやすい。
        rect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
    }

    // ── AdminUIManager の参照を貼り直す ──

    private static void WireManager(Transform panel, StringBuilder log)
    {
        var manager = panel.GetComponent<AdminUIManager>();
        if (manager == null)
        {
            log.AppendLine("  [WARN] AdminPanel に AdminUIManager が付いていません");
            return;
        }

        var so = new SerializedObject(manager);

        Assign(so, "addImageButton",           FindComponent<Button>(panel, "AddImageButton"), log);
        Assign(so, "convertAllButton",         FindComponent<Button>(panel, "ConvertAllButton"), log);
        Assign(so, "testLaunchButton",         FindComponent<Button>(panel, "TestLaunchButton"), log);
        Assign(so, "closeButton",              FindComponent<Button>(panel, "CloseButton"), log);
        Assign(so, "refreshButton",            FindComponent<Button>(panel, "RefreshButton"), log);
        Assign(so, "segmentationToggleButton", FindComponent<Button>(panel, "SegmentationToggleButton"), log);
        Assign(so, "statusText",               FindComponent<TextMeshProUGUI>(panel, "StatusText"), log);

        // Segmentation ボタンのラベルは、そのボタン配下の TMP を拾う
        var segBtn = FindDescendant(panel, "SegmentationToggleButton");
        if (segBtn != null)
            Assign(so, "segmentationToggleText", segBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        // 行の親は Viewport > Content
        var content = FindDescendant(panel, "Content");
        if (content != null)
            Assign(so, "entryListContent", content, log);

        // selfieSegmentation / previewImage / detailText は AdminPanel の外にある想定なので触らない

        so.ApplyModifiedProperties();
    }

    private static void Assign(SerializedObject so, string field, Object value, StringBuilder log)
    {
        var prop = so.FindProperty(field);
        if (prop == null)
        {
            log.AppendLine($"  [WARN] AdminUIManager に {field} が見つかりません");
            return;
        }
        if (value == null)
        {
            log.AppendLine($"  [WARN] {field} に入れる対象が見つからないため未設定のまま");
            return;
        }
        if (prop.objectReferenceValue != value)
        {
            prop.objectReferenceValue = value;
            log.AppendLine($"  {field} を再アサイン");
        }
    }

    // ── ヘルパー ──

    private static Transform FindAdminPanel(out string error)
    {
        error = null;

        var canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        Canvas adminCanvas = null;
        foreach (var c in canvases)
        {
            if (c.name == "AdminCanvas") { adminCanvas = c; break; }
        }
        if (adminCanvas == null)
        {
            error = "AdminCanvas が見つかりません。MainScene を開いてから実行してください。";
            return null;
        }

        var panel = FindDescendant(adminCanvas.transform, "AdminPanel");
        if (panel == null)
        {
            error = "AdminCanvas 配下に AdminPanel が見つかりません。";
            return null;
        }
        return panel;
    }

    // 非アクティブも含めて子孫を名前で探す
    private static Transform FindDescendant(Transform root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var found = FindDescendant(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    private static T FindComponent<T>(Transform root, string name) where T : Component
    {
        var t = FindDescendant(root, name);
        return t != null ? t.GetComponent<T>() : null;
    }

    // 直下に無ければ子孫から探し、それでも無ければ新規作成
    private static Transform FindOrCreateChild(Transform parent, string name, StringBuilder log)
    {
        var existing = FindDescendant(parent, name);
        if (existing != null && existing != parent)
        {
            if (existing.parent != parent) Reparent(existing, parent);
            return existing;
        }

        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, $"create {name}");
        go.transform.SetParent(parent, false);
        log.AppendLine($"  {name} を新規作成");
        return go.transform;
    }

    private static Transform EnsureSection(Transform panel, string name, StringBuilder log)
    {
        var section = FindOrCreateChild(panel, name, log);
        // セクション自体は透明。背景を出したい場合は Image を手で足す
        return section;
    }

    private static void Reparent(Transform child, Transform parent)
    {
        if (child.parent == parent) return;
        Undo.SetTransformParent(child, parent, $"reparent {child.name}");
        child.SetParent(parent, false);
    }

    private static T GetOrAdd<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null)
        {
            c = Undo.AddComponent<T>(go);
        }
        else
        {
            Undo.RecordObject(c, $"configure {typeof(T).Name}");
        }
        return c;
    }

    private static void StretchFull(RectTransform rt)
    {
        Undo.RecordObject(rt, "stretch");
        rt.anchorMin        = Vector2.zero;
        rt.anchorMax        = Vector2.one;
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.offsetMin        = Vector2.zero;
        rt.offsetMax        = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;
    }

    private static void SetHeight(Transform t, float height)
    {
        var le = GetOrAdd<LayoutElement>(t.gameObject);
        le.minHeight      = height;
        le.preferredHeight = height;
        le.flexibleHeight = 0f;
    }

    private static void Flexible(Transform t, float flexibleWidth = -1f, float flexibleHeight = -1f)
    {
        var le = GetOrAdd<LayoutElement>(t.gameObject);
        if (flexibleWidth  >= 0f) le.flexibleWidth  = flexibleWidth;
        if (flexibleHeight >= 0f) le.flexibleHeight = flexibleHeight;
    }

    // ボタンを Toolbar / Header 用に整える。ラベルの文字列は content なので変更しない。
    private static void StyleButton(Transform btn, float minWidth, float height, bool flexible)
    {
        var le = GetOrAdd<LayoutElement>(btn.gameObject);
        le.minWidth        = minWidth;
        le.preferredWidth  = -1f;                 // 未設定 = minWidth が下限として効く
        le.minHeight       = height;
        le.preferredHeight = height;
        le.flexibleWidth   = flexible ? 1f : 0f;
        le.flexibleHeight  = 0f;

        // ラベルはボタン全体に伸ばし、はみ出さないようオートサイズを有効化
        var label = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label == null) return;

        Undo.RecordObject(label, "style label");
        var lrt = label.rectTransform;
        Undo.RecordObject(lrt, "style label rect");
        lrt.anchorMin        = Vector2.zero;
        lrt.anchorMax        = Vector2.one;
        lrt.offsetMin        = new Vector2(8f, 4f);
        lrt.offsetMax        = new Vector2(-8f, -4f);
        lrt.anchoredPosition = Vector2.zero;

        label.enableAutoSizing = true;
        label.fontSizeMin      = ButtonFontMin;
        label.fontSizeMax      = ButtonFontMax;
        label.alignment        = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode     = TextOverflowModes.Ellipsis;
    }
}
#endif
