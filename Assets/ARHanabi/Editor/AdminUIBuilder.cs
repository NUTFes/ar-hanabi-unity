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

    // ボタンのラベル色。
    //
    // ── 白をやめた理由 ──
    //   ツールバーのボタンの背景 Image は既定の白（MainScene の m_Color が
    //   r:1 g:1 b:1 a:1）のままなので、白文字だとほぼ読めなかった。
    //   背景を塗り替えるより文字を暗くするほうが影響範囲が小さいので、
    //   濃紺寄りのダークグレーに統一する。宇宙モードのコックピット枠の
    //   金属色とも系統が揃う。
    //
    //   AdminUIManager.MakeButton()（一覧の行に動的生成するボタン）の
    //   ラベル色もこの値に合わせてある。片方だけ変えると混在するため、
    //   色を変えるときは両方まとめて直すこと。
    private static readonly Color ButtonLabelColor = new Color(0.09f, 0.12f, 0.17f, 1f);

    // ツールバーに並べる順番。この配列の順で左から並ぶ。
    // ここに名前を足すだけで、無ければ FindOrCreateButton() が作る
    private static readonly string[] ToolbarButtonOrder =
    {
        "TestLaunchButton",
        "RefreshButton",
        "CameraIndexButton",
        "SpaceModeButton",
        "SettingsModeButton",
    };

    // 宇宙モードの個別トグル4つを並べる2段目のツールバー。
    // 1段目に詰め込まず行を分けているのは、宇宙モードOFF中はこの行ごと
    // 非表示にしたいため（1段目に混ぜると個別トグルだけを隠す配置制御が煩雑になる）
    private static readonly string[] SpaceToolbarButtonOrder =
    {
        "FrameToggleButton",
        "UfoToggleButton",
        "HanabiModeButton",
        "SfxToggleButton",
    };

    // ジェスチャー感度・花火の出し方の6ボタンを並べる3段目のツールバー。
    // ビルド後は Unity Editor に触れない前提で、展示中に調整したくなる値を
    // ここへ集約している。SpaceToolbar と同じ理由で行を分け、
    // SETTINGSボタンでこの行ごと開閉する
    private static readonly string[] SettingsToolbarButtonOrder =
    {
        "HandUpButton",
        "JumpButton",
        "CooldownButton",
        "HoldButton",
        "ImgChanceButton",
        "ImgEnableButton",
        "MatteButton",
    };

    // 新規作成したときだけ入れる初期ラベル。
    // 既存ボタンのラベル文字列は content なので上書きしない（StyleButton と同じ方針）。
    // FindOrCreateButton はこの1つの辞書しか見ないため、2段目・3段目のボタン分も
    // ここへ統合する
    private static readonly Dictionary<string, string> ToolbarButtonInitialLabel = new()
    {
        { "CameraIndexButton",  "CAM [-]" },
        { "SpaceModeButton",    "SPACE [OFF]" },
        { "FrameToggleButton",  "FRAME [ON]" },
        { "UfoToggleButton",    "UFO [ON]" },
        { "HanabiModeButton",   "HANABI [MIX]" },
        { "SfxToggleButton",    "SFX [ON]" },
        { "SettingsModeButton", "SETTINGS [OFF]" },
        { "HandUpButton",       "HAND [0.15]" },
        { "JumpButton",         "JUMP [0.06]" },
        { "CooldownButton",     "COOLDOWN [2.0s]" },
        { "HoldButton",         "HOLD [0.5s]" },
        { "ImgChanceButton",    "IMG% [50]" },
        { "ImgEnableButton",    "IMG [ON]" },
        { "QuitButton",         "QUIT" },
        { "OpenTabButton",      "OPEN" },
        { "MatteButton",        "MATTE [OFF]" },
    };

    // 廃止したボタン。存在すれば削除する（Ctrl+Z 1回で戻せるよう Undo に登録する）
    private static readonly string[] ObsoleteButtonNames =
    {
        "AddImageButton",
        "ConvertAllButton",
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

        // 廃止ボタンの削除は他の Build* より先にやる。
        // FindOrCreateButton は名前で「既存を再利用」するため、先に消しておかないと
        // 古い AddImageButton 等が「見つかった」ことにされて再利用されてしまう
        RemoveObsoleteButtons(panel, log);

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
        var header          = EnsureSection(panel, "Header",          log);
        var toolbar         = EnsureSection(panel, "Toolbar",         log);
        var spaceToolbar    = EnsureSection(panel, "SpaceToolbar",    log);
        var settingsToolbar = EnsureSection(panel, "SettingsToolbar", log);
        var status          = FindOrCreateChild(panel, "StatusText", log);
        var scroll          = FindOrCreateChild(panel, "EntryScrollView", log);

        header         .SetSiblingIndex(0);
        toolbar        .SetSiblingIndex(1);
        spaceToolbar   .SetSiblingIndex(2);
        settingsToolbar.SetSiblingIndex(3);
        status         .SetSiblingIndex(4);
        scroll         .SetSiblingIndex(5);

        BuildHeader(panel, header, log);
        BuildToolbar(toolbar, log);
        BuildSpaceToolbar(spaceToolbar, log);
        BuildSettingsToolbar(settingsToolbar, log);
        BuildStatus(status, log);
        BuildScrollView(scroll, log);

        // ── 「開く」タブ（AdminPanel の外＝AdminCanvas 直下に置く）──
        // [閉じる] は AdminPanel の CanvasGroup で見た目ごと消す方式なので、
        // 再表示ボタンを AdminPanel の中に置くと一緒に消えて二度と押せなくなる。
        // そのため panel.parent（AdminCanvas）に直接ぶら下げる
        BuildOpenTab(panel.parent, log);

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

        // 終了ボタン。無ければ作る（ツールバーのボタンと同じ FindOrCreateButton 経路）。
        // 閉じるボタンが直後に SetAsLastSibling() で右端固定になるので、
        // 先にこちらを配置しておけば自然に「閉じるボタンのすぐ左」に収まる。
        //
        // ── なぜ終了ボタンが要るか ──
        //   [閉じる] は CanvasGroup で見た目を隠すだけで、アプリ自体は終了しない
        //   （F1 で再表示できるようにするための意図的な設計。このファイル冒頭の
        //   AdminUIManager.cs 側のコメント参照）。ビルドしたスタンドアロン版には
        //   Alt+F4 以外にアプリを終了する手段が無く、実機テストで「閉じるボタンを
        //   押してもアプリが終わらない」という運用上の分かりにくさが実際に出たため、
        //   ここに専用の終了ボタンを追加した
        var quit = FindOrCreateButton(panel, header, "QuitButton", log);
        if (quit != null)
        {
            Reparent(quit, header);
            StyleButton(quit, minWidth: 100f, height: HeaderHeight - 8f, flexible: false);
        }

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

    // 「開く」タブ。AdminPanel の外（AdminCanvas 直下）に置く一点物のボタンなので、
    // ToolbarButtonOrder のような配列駆動ではなく単独の関数にしてある。
    // AdminPanel 配下と違い VerticalLayoutGroup/HorizontalLayoutGroup の管理下に無いため、
    // StyleButton（LayoutElement 経由でサイズを決める前提）は使わず、
    // 画面左上に固定サイズで置く RectTransform を直接組む
    private static void BuildOpenTab(Transform canvas, StringBuilder log)
    {
        var btn = FindOrCreateButton(canvas, canvas, "OpenTabButton", log);
        if (btn == null) return;

        var rt = btn.GetComponent<RectTransform>();
        rt.anchorMin        = new Vector2(0f, 1f);
        rt.anchorMax        = new Vector2(0f, 1f);
        rt.pivot            = new Vector2(0f, 1f);
        rt.sizeDelta        = new Vector2(140f, 56f);
        rt.anchoredPosition = new Vector2(16f, -16f);

        var label = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        if (label != null)
        {
            var lrt = label.rectTransform;
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
            label.color            = ButtonLabelColor;
        }

        // 実際の表示/非表示は AdminUIManager.ApplyVisible() が起動時に
        // visibleOnStart を見て決める（パネルが表示中なら Open タブは要らない）ので、
        // ここでの Active 状態は最終的な見た目を左右しない。Editor 上で自然に見える
        // よう、ひとまず表示させておく
        log.AppendLine("  OpenTabButton を配置");
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

        // ボタンを ToolbarButtonOrder の順に集める。無いものは作る
        var panel = toolbar.parent;
        for (int i = 0; i < ToolbarButtonOrder.Length; i++)
        {
            string name = ToolbarButtonOrder[i];
            var btn = FindOrCreateButton(panel, toolbar, name, log);
            if (btn == null) continue;

            Reparent(btn, toolbar);
            btn.SetSiblingIndex(i);
            StyleButton(btn, ToolbarBtnMinW, ToolbarHeight - 8f, flexible: true);
        }
        log.AppendLine($"  Toolbar に {toolbar.childCount} 個のボタンを配置");
    }

    // 宇宙モードの個別トグル4つの行。BuildToolbar と全く同じ組み方で、
    // 対象の配列とセクションだけが違う（レイアウト定数を揃えることで見た目を統一する）
    private static void BuildSpaceToolbar(Transform spaceToolbar, StringBuilder log)
    {
        var layout = GetOrAdd<HorizontalLayoutGroup>(spaceToolbar.gameObject);
        layout.spacing                = ToolbarSpacing;
        layout.childAlignment         = TextAnchor.MiddleCenter;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = false;   // flexibleWidth で分配する
        layout.childForceExpandHeight = true;

        SetHeight(spaceToolbar, ToolbarHeight);

        // ボタンを SpaceToolbarButtonOrder の順に集める。無いものは作る
        var panel = spaceToolbar.parent;
        for (int i = 0; i < SpaceToolbarButtonOrder.Length; i++)
        {
            string name = SpaceToolbarButtonOrder[i];
            var btn = FindOrCreateButton(panel, spaceToolbar, name, log);
            if (btn == null) continue;

            Reparent(btn, spaceToolbar);
            btn.SetSiblingIndex(i);
            StyleButton(btn, ToolbarBtnMinW, ToolbarHeight - 8f, flexible: true);
        }
        log.AppendLine($"  SpaceToolbar に {spaceToolbar.childCount} 個のボタンを配置");
    }

    // ジェスチャー感度・花火の出し方の6ボタンの行。BuildSpaceToolbar と全く同じ組み方
    private static void BuildSettingsToolbar(Transform settingsToolbar, StringBuilder log)
    {
        var layout = GetOrAdd<HorizontalLayoutGroup>(settingsToolbar.gameObject);
        layout.spacing                = ToolbarSpacing;
        layout.childAlignment         = TextAnchor.MiddleCenter;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = false;   // flexibleWidth で分配する
        layout.childForceExpandHeight = true;

        SetHeight(settingsToolbar, ToolbarHeight);

        // ボタンを SettingsToolbarButtonOrder の順に集める。無いものは作る
        var panel = settingsToolbar.parent;
        for (int i = 0; i < SettingsToolbarButtonOrder.Length; i++)
        {
            string name = SettingsToolbarButtonOrder[i];
            var btn = FindOrCreateButton(panel, settingsToolbar, name, log);
            if (btn == null) continue;

            Reparent(btn, settingsToolbar);
            btn.SetSiblingIndex(i);
            StyleButton(btn, ToolbarBtnMinW, ToolbarHeight - 8f, flexible: true);
        }
        log.AppendLine($"  SettingsToolbar に {settingsToolbar.childCount} 個のボタンを配置");
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

        Assign(so, "testLaunchButton",         FindComponent<Button>(panel, "TestLaunchButton"), log);
        Assign(so, "closeButton",              FindComponent<Button>(panel, "CloseButton"), log);
        Assign(so, "quitButton",               FindComponent<Button>(panel, "QuitButton"), log);

        // OpenTabButton は AdminPanel の外（AdminCanvas 直下）にあるため、
        // panel ではなく panel.parent を探索の起点にする
        var openTabBtn = FindDescendant(panel.parent, "OpenTabButton");
        if (openTabBtn != null)
            Assign(so, "openTabButton", openTabBtn.GetComponent<Button>(), log);
        Assign(so, "refreshButton",            FindComponent<Button>(panel, "RefreshButton"), log);
        Assign(so, "statusText",               FindComponent<TextMeshProUGUI>(panel, "StatusText"), log);

        Assign(so, "cameraIndexButton",        FindComponent<Button>(panel, "CameraIndexButton"), log);

        Assign(so, "spaceModeButton",          FindComponent<Button>(panel, "SpaceModeButton"), log);
        Assign(so, "frameToggleButton",        FindComponent<Button>(panel, "FrameToggleButton"), log);
        Assign(so, "ufoToggleButton",           FindComponent<Button>(panel, "UfoToggleButton"), log);
        Assign(so, "hanabiModeButton",         FindComponent<Button>(panel, "HanabiModeButton"), log);
        Assign(so, "sfxToggleButton",          FindComponent<Button>(panel, "SfxToggleButton"), log);

        Assign(so, "settingsModeButton",       FindComponent<Button>(panel, "SettingsModeButton"), log);
        Assign(so, "handUpButton",             FindComponent<Button>(panel, "HandUpButton"), log);
        Assign(so, "jumpButton",               FindComponent<Button>(panel, "JumpButton"), log);
        Assign(so, "cooldownButton",           FindComponent<Button>(panel, "CooldownButton"), log);
        Assign(so, "holdButton",               FindComponent<Button>(panel, "HoldButton"), log);
        Assign(so, "imgChanceButton",          FindComponent<Button>(panel, "ImgChanceButton"), log);
        Assign(so, "imgEnableButton",          FindComponent<Button>(panel, "ImgEnableButton"), log);
        Assign(so, "matteButton",              FindComponent<Button>(panel, "MatteButton"), log);

        // 終了ボタンのラベルも同様
        var quitBtn = FindDescendant(panel, "QuitButton");
        if (quitBtn != null)
            Assign(so, "quitText", quitBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        // カメラボタンのラベルは、そのボタン配下の TMP を拾う
        var camBtn = FindDescendant(panel, "CameraIndexButton");
        if (camBtn != null)
            Assign(so, "cameraIndexText", camBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        // 宇宙モード関連ボタンのラベルも同様（ボタン配下の Label TMP を拾う）
        var spaceModeBtn = FindDescendant(panel, "SpaceModeButton");
        if (spaceModeBtn != null)
            Assign(so, "spaceModeText", spaceModeBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var frameBtn = FindDescendant(panel, "FrameToggleButton");
        if (frameBtn != null)
            Assign(so, "frameToggleText", frameBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var ufoBtn = FindDescendant(panel, "UfoToggleButton");
        if (ufoBtn != null)
            Assign(so, "ufoToggleText", ufoBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var hanabiBtn = FindDescendant(panel, "HanabiModeButton");
        if (hanabiBtn != null)
            Assign(so, "hanabiModeText", hanabiBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var sfxBtn = FindDescendant(panel, "SfxToggleButton");
        if (sfxBtn != null)
            Assign(so, "sfxToggleText", sfxBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        // 個別トグル4つの行そのもの（宇宙モードOFF中の表示/非表示を AdminUIManager 側で切替える）
        var spaceToolbar = FindDescendant(panel, "SpaceToolbar");
        if (spaceToolbar != null)
            Assign(so, "spaceToolbar", spaceToolbar, log);

        // 設定パネルの6ボタンのラベルも同様
        var settingsModeBtn = FindDescendant(panel, "SettingsModeButton");
        if (settingsModeBtn != null)
            Assign(so, "settingsModeText", settingsModeBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var handUpBtn = FindDescendant(panel, "HandUpButton");
        if (handUpBtn != null)
            Assign(so, "handUpText", handUpBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var jumpBtn = FindDescendant(panel, "JumpButton");
        if (jumpBtn != null)
            Assign(so, "jumpText", jumpBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var cooldownBtn = FindDescendant(panel, "CooldownButton");
        if (cooldownBtn != null)
            Assign(so, "cooldownText", cooldownBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var holdBtn = FindDescendant(panel, "HoldButton");
        if (holdBtn != null)
            Assign(so, "holdText", holdBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var imgChanceBtn = FindDescendant(panel, "ImgChanceButton");
        if (imgChanceBtn != null)
            Assign(so, "imgChanceText", imgChanceBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var imgEnableBtn = FindDescendant(panel, "ImgEnableButton");
        if (imgEnableBtn != null)
            Assign(so, "imgEnableText", imgEnableBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        var matteBtn = FindDescendant(panel, "MatteButton");
        if (matteBtn != null)
            Assign(so, "matteText", matteBtn.GetComponentInChildren<TextMeshProUGUI>(true), log);

        // 6ボタンの行そのもの（SETTINGS OFF中の表示/非表示を AdminUIManager 側で切替える）
        var settingsToolbar = FindDescendant(panel, "SettingsToolbar");
        if (settingsToolbar != null)
            Assign(so, "settingsToolbar", settingsToolbar, log);

        // 行の親は Viewport > Content
        var content = FindDescendant(panel, "Content");
        if (content != null)
            Assign(so, "entryListContent", content, log);

        // previewImage / detailText は AdminPanel の外にある想定なので触らない。
        // cameraBackground も AdminPanel の外（CameraBackground オブジェクト）なので触らない。
        // こちらは AdminUIManager.Start() が FindFirstObjectByType で自動解決するため
        // 手作業でのアサインは不要になっている

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

    // ツールバー用のボタンを探し、無ければ Image + Button + 子 Label(TMP) を組んで作る。
    //
    // 以前はここで見つからなければ WARN を出してスキップするだけだったので、
    // ボタンを1つ増やすたびに Hierarchy での手作業（GameObject 作成 → Image/Button 追加 →
    // 子 Text 追加 → AdminUIManager へのアサイン）が必要だった。
    // ToolbarButtonOrder に名前を足すだけで完結するようにしてある。
    // 生成物は Undo に登録するので、Ctrl+Z 1回で全部戻る性質は保たれる。
    private static Transform FindOrCreateButton(Transform panel, Transform toolbar,
                                                string name, StringBuilder log)
    {
        var existing = FindDescendant(panel, name);
        if (existing != null) return existing;

        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, $"create {name}");
        go.transform.SetParent(toolbar, false);

        var img = Undo.AddComponent<Image>(go);
        var btn = Undo.AddComponent<Button>(go);
        btn.targetGraphic = img;   // interactable = false の見た目を効かせる

        var labelGO = new GameObject("Label", typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(labelGO, "create Label");
        labelGO.transform.SetParent(go.transform, false);

        var label = Undo.AddComponent<TextMeshProUGUI>(labelGO);
        label.text  = ToolbarButtonInitialLabel.TryGetValue(name, out var initial) ? initial : name;
        label.color = ButtonLabelColor;
        // 位置・オートサイズ・色は直後に呼ばれる StyleButton が仕上げる
        // （ここで入れているのは、StyleButton を通らない経路が将来できても
        //   白に戻らないようにするための保険）

        log.AppendLine($"  {name} を新規作成（ラベル \"{label.text}\"）");
        return go.transform;
    }

    // 廃止ボタンを AdminPanel 配下から探して削除する。
    // Undo.DestroyObjectImmediate を使うことで、他の生成・変更と同じ
    // Undo グループにまとまり、Ctrl+Z 1回で再構築全体が戻せる性質を崩さない。
    private static void RemoveObsoleteButtons(Transform panel, StringBuilder log)
    {
        foreach (var name in ObsoleteButtonNames)
        {
            var found = FindDescendant(panel, name);
            if (found == null) continue;

            Undo.DestroyObjectImmediate(found.gameObject);
            log.AppendLine($"  [REMOVE] 廃止ボタン {name} を削除");
        }
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

        // 文字色もここで統一する。
        // 「文字列は content なので変更しない」方針は保つが、色は見た目（style）なので
        // このメソッドの担当。ここで塗ることで、既にシーンに存在するボタンも
        // 再構築のたびに揃う（新規作成時だけ色を入れていると、既存ボタンは
        // 白のまま取り残される）。
        label.color = ButtonLabelColor;
    }
}
#endif
