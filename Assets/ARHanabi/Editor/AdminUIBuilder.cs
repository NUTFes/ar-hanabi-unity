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
// 画面構成（AdminPanel は VerticalLayoutGroup で上から順に積む）:
//
//   ┌─────────────────────────────────────────────────────────┐
//   │ Header      花火管理                          [終了][閉じる] │
//   ├─────────────────────────────────────────────────────────┤
//   │ TabBar      [基本][宇宙モード][検出の調整]      全12件 / 有効8件 │
//   ├─────────────────────────────────────────────────────────┤
//   │ TabContent  TabBasicPage / TabSpacePage / TabTunePage の   │
//   │             どれか1つだけを SetActive(true) して見せる         │
//   ├─────────────────────────────────────────────────────────┤
//   │ TabHelpText   選択中のタブの1行ヘルプ                        │
//   │ StatusText    直近の操作結果（2行ぶんの高さ、折り返しあり）      │
//   │ EntryScrollView  画像エントリの一覧（残りの縦幅を全部使う）      │
//   └─────────────────────────────────────────────────────────┘
//
// なぜタブ + スライダーなのか（今回の作り替えの理由）:
//   展示当日に操作するのはエンジニアではない。旧構成はボタンが横3段に十数個並び、
//   ・"HAND [0.15]" のようなラベルが何のことか分からない
//   ・数値の調整がボタン連打（1押しで1段階）でしか行えない
//   ・今どのモードなのかが一目で分からない
//   という状態で、当日の運用に耐えないことが分かった。そこで
//   「基本 / 宇宙モード / 検出の調整」の3タブに分けて一度に見える数を減らし、
//   数値は uGUI の Slider で直接つまめるようにした。
//   配色・寸法は AdminUiStyle（Scripts/UI/AdminUiStyle.cs）に集約してあり、
//   このファイルには色の数値を直接書かない（Manager 側とズレるのを防ぐため）。
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
    // ── Builder 専用のレイアウト定数 ──
    // 色・主要な高さ・ボタンの文字サイズは AdminUiStyle が単一ソースなので、
    // ここに残すのは「組み立てるときにしか使わない＝Manager 側が知る必要のない値」だけ。
    // RectOffset が int しか受け取らないので、AdminUiStyle の float から丸めて持つ
    private static readonly int PanelPadding = Mathf.RoundToInt(AdminUiStyle.PanelPadding);
    private const int   PanelSpacing     = 12;

    private const float ToolbarSpacing   = 12f;

    private const float ScrollbarWidth   = 20f;
    private const float TitleFontSize    = 40f;
    private const float StatusFontSize   = 24f;   // 2行に収めるため旧 28 から少し下げた
    private const float HelpFontSize     = 20f;

    // スライダーブロックの内訳。
    // 32 + 4(spacing) + 40 + 4(padding) = 80 で AdminUiStyle.SliderBlockH(88) に収まる
    private const float SliderLabelH     = 32f;   // 「手上げ判定のきびしさ 0.15（肩幅比）」の行
    private const float SliderBarH       = 40f;   // スライダー本体（トラック＋ハンドル）
    private const float SliderHandleW    = 28f;   // マウスでつまめる最低限の幅

    // 「検出の調整」タブのグリッドのセル幅。
    // GridLayoutGroup はセルを自動で伸縮しないので実寸で持つしかない。
    // Canvas の参照解像度 1920 − パネル左右padding(24*2) = 1872 に
    // 3列 + 列間 12*2 を収める（600*3 + 24 = 1824）
    private const float TuneCellWidth    = 600f;
    private const float TuneGridSpacing  = 12f;

    // ── タブ行（TabBar）に並べるタブボタン ──
    // 表示するページは AdminUIManager が SetActive で切り替える。Builder は器だけ作る
    private static readonly string[] TabBarButtonOrder =
    {
        "TabBasicButton",
        "TabSpaceButton",
        "TabTuneButton",
    };

    // 「基本」タブ（横1行）。当日いちばん触るものだけを置く。
    // ここに名前を足すだけで、無ければ FindOrCreateButton() が作る。
    // 既にパネルのどこかに在るボタンは作り直さず reparent されるだけなので、
    // タブ構成を変えたいときはこの配列を書き換えれば済む
    private static readonly string[] TabBasicButtonOrder =
    {
        "TestLaunchButton",
        "RefreshButton",
        "CameraIndexButton",
        "ImgEnableButton",
        "ImageResButton",
        "MatteButton",
        "SkeletonButton",
    };

    // 「宇宙モード」タブ（横1行）。
    // 宇宙モードOFF中も個別スイッチの状態は保存されるので、旧構成のように
    // 行ごと隠す必要はなくなった（隠すのではなくタブを開かなければ見えない）
    private static readonly string[] TabSpaceButtonOrder =
    {
        "SpaceModeButton",
        "FrameToggleButton",
        "UfoToggleButton",
        "HanabiModeButton",
        "SfxToggleButton",
    };

    // 「検出の調整」タブに並べるスライダー。3列×2行のグリッドにこの順で入る。
    //
    // ── なぜボタンからスライダーに変えたか ──
    //   旧構成は1押しで1段階しか動かず、0.15 → 0.60 にするのに連打が必要だった。
    //   min/max/wholeNumbers は Builder（＝この表）が唯一の設定元で、
    //   Manager は値の読み書きだけを行う。両方で設定すると再構築のたびに
    //   どちらが勝つか分からなくなるため、意図的に片側へ寄せている。
    private readonly struct SliderSpec
    {
        public readonly string Key;            // GameObject 名の接頭辞（{Key}Block / {Key}Label / {Key}Slider）
        public readonly string InitialLabel;   // 新規作成時だけ入れる文言（実値は Manager が上書きする）
        public readonly float  Min;
        public readonly float  Max;
        public readonly bool   WholeNumbers;

        public SliderSpec(string key, string initialLabel, float min, float max, bool wholeNumbers)
        {
            Key          = key;
            InitialLabel = initialLabel;
            Min          = min;
            Max          = max;
            WholeNumbers = wholeNumbers;
        }
    }

    private static readonly SliderSpec[] TuneSliders =
    {
        new SliderSpec("HandUp",     "手上げ判定のきびしさ  0.15（肩幅比）", 0.05f,   2.50f, false),
        new SliderSpec("Jump",       "ジャンプ判定のきびしさ  0.06（肩幅比）", 0.02f, 2.00f, false),
        new SliderSpec("Cooldown",   "連発防止の間隔  2.0秒",              0.00f,   3.00f, false),
        new SliderSpec("Hold",       "ポーズの保持時間  0.5秒",            0.10f,   1.50f, false),
        // 画像花火の割合だけは 0–100 の百分率（既存の ImageFireworkChance の扱いに合わせる）
        new SliderSpec("ImgChance",  "画像花火の割合  50%",                0f,    100f,  true),
        new SliderSpec("PersonConf", "人物検出のきびしさ  0.50",            0.30f,   0.90f, false),
    };

    // タブごとの1行ヘルプ。TabHelpText の初期値に使う。
    // 実行中の切り替えは AdminUIManager が行うので、同じ文言を向こうにも持っている
    // （文言を直すときは admin-ui-contract.md の表と両方を揃えること）
    private const string TabBasicHelpText = "テスト打上=花火を1発試す ／ 丸窓=映像をドーム型に切り抜く表示";

    // 新規作成したときだけ入れる初期ラベル。
    // 既存ボタンのラベル文字列は content なので上書きしない（StyleButton と同じ方針）。
    // 実行中は AdminUIManager が状態に応じて書き換えるため、ここの値は
    // 「Editor で開いたときにボタンが空に見えない」ためのもの。
    // FindOrCreateButton はこの1つの辞書しか見ないため、全タブのボタン分をここへ統合する
    private static readonly Dictionary<string, string> ToolbarButtonInitialLabel = new()
    {
        // 基本タブ
        { "TestLaunchButton",   "テスト打上" },
        { "RefreshButton",      "更新（API取得）" },
        // カメラ名と本数は起動して VideoCapture を開くまで分からないので、
        // 実行中の書式「カメラ切替 [2/4] name」の器だけ置いておく
        { "CameraIndexButton",  "カメラ切替 [-]" },
        { "ImgEnableButton",    "画像花火 [ON]" },
        { "MatteButton",        "丸窓モード [OFF]" },
        { "SkeletonButton",     "ボーン表示 [ON]" },
        // 段は 小(24) / 中(32) / 大(48)。中がシーンに保存されている既定値
        { "ImageResButton",     "画像花火の細かさ [中]" },

        // 宇宙モードタブ
        { "SpaceModeButton",    "宇宙モード [OFF]" },
        { "FrameToggleButton",  "宇宙船フレーム [ON]" },
        { "UfoToggleButton",    "UFO [ON]" },
        { "HanabiModeButton",   "花火の種類 [混合]" },
        { "SfxToggleButton",    "宇宙効果音 [ON]" },

        // タブ行・ヘッダー・パネル外
        { "TabBasicButton",     "基本" },
        { "TabSpaceButton",     "宇宙モード" },
        { "TabTuneButton",      "検出の調整" },
        { "QuitButton",         "終了" },
        { "CloseButton",        "閉じる" },
        { "OpenTabButton",      "開く" },
    };

    // 廃止したボタン。存在すれば削除する（Ctrl+Z 1回で戻せるよう Undo に登録する）。
    //
    // 後半の7個は「1押しで1段階」だった数値ボタンで、タブ構成への作り替えで
    // スライダー（TuneSliders）に置き換わった。名前で探して消しているだけなので、
    // 既に存在しないシーンで実行しても何も起きない
    private static readonly string[] ObsoleteButtonNames =
    {
        "AddImageButton",
        "ConvertAllButton",
        "SegmentationToggleButton",
        "SettingsModeButton",   // 3段目の開閉ボタン → 「検出の調整」タブに置き換え
        "HandUpButton",
        "JumpButton",
        "CooldownButton",
        "HoldButton",
        "ImgChanceButton",
        "PersonConfButton",
    };

    // 旧構成のセクション。中のボタンは新しいページへ reparent されるので、
    // 空になった入れ物だけが残る。これを消さないと VerticalLayoutGroup の中に
    // 高さ0の隙間が残り続ける。
    // 削除は必ず全ページを組んだ後に行う（先に消すと reparent 前の子ごと道連れになる）
    private static readonly string[] ObsoleteSectionNames =
    {
        "Toolbar",
        "SpaceToolbar",
        "SettingsToolbar",
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

        // パネル地の色。シーンでは半透明の白 (1,1,1,0.39) が入っており、
        // その上に白いタイトル文字とステータス文字を載せていたため、
        // 背後のカメラ映像が明るいとほぼ読めなかった。暗色に塗り替える。
        // シーンを手で直すとメニュー再構築のたびに元へ戻る可能性があるので、
        // 「毎回 Builder が塗る」形にして再発しないようにしてある
        var panelImage = GetOrAdd<Image>(panel.gameObject);
        panelImage.color = AdminUiStyle.PanelBackground;

        var panelLayout = GetOrAdd<VerticalLayoutGroup>(panel.gameObject);
        panelLayout.padding                = new RectOffset(PanelPadding, PanelPadding, PanelPadding, PanelPadding);
        panelLayout.spacing                = PanelSpacing;
        panelLayout.childAlignment         = TextAnchor.UpperCenter;
        panelLayout.childControlWidth      = true;
        panelLayout.childControlHeight     = true;
        panelLayout.childForceExpandWidth  = true;
        panelLayout.childForceExpandHeight = false;

        // ── セクションを用意（順序が Hierarchy 上の並び順 = 表示順になる）──
        var header     = EnsureSection(panel, "Header",     log);
        var tabBar     = EnsureSection(panel, "TabBar",     log);
        var tabContent = EnsureSection(panel, "TabContent", log);
        var help       = FindOrCreateChild(panel, "TabHelpText",     log);
        var status     = FindOrCreateChild(panel, "StatusText",      log);
        var scroll     = FindOrCreateChild(panel, "EntryScrollView", log);

        header    .SetSiblingIndex(0);
        tabBar    .SetSiblingIndex(1);
        tabContent.SetSiblingIndex(2);
        help      .SetSiblingIndex(3);
        status    .SetSiblingIndex(4);
        scroll    .SetSiblingIndex(5);

        BuildHeader(panel, header, log);
        BuildTabBar(panel, tabBar, log);
        BuildTabContent(panel, tabContent, log);
        BuildHelp(help, log);
        BuildStatus(status, log);
        BuildScrollView(scroll, log);

        // 旧セクション（Toolbar / SpaceToolbar / SettingsToolbar）の後始末。
        // 上の Build* で中身が新しいページへ移った後でないと、
        // まだ移動していないボタンごと消してしまう
        RemoveEmptyLegacySections(panel, log);

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

        SetHeight(header, AdminUiStyle.HeaderHeight);

        // タイトル（装飾。AdminUIManager からは参照しない）
        var title = FindOrCreateChild(header, "TitleText", log);
        var titleText = GetOrAdd<TextMeshProUGUI>(title.gameObject);
        if (string.IsNullOrEmpty(titleText.text) || titleText.text == "New Text")
            titleText.text = "花火管理";
        titleText.fontSize  = TitleFontSize;
        titleText.alignment = TextAlignmentOptions.MidlineLeft;
        titleText.color     = AdminUiStyle.TextOnPanel;
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
        //
        //   色は AdminUiStyle.Danger*（薄赤地＋濃赤文字）にしてある。
        //   押すと戻れない操作だけが赤、という対応を全画面で固定しておくと、
        //   説明を読まないスタッフでも「赤は気をつける」だけ覚えれば済む
        var quit = FindOrCreateButton(panel, header, "QuitButton", log);
        if (quit != null)
        {
            Reparent(quit, header);
            StyleButton(quit, minWidth: 140f, height: AdminUiStyle.HeaderHeight - 8f, flexible: false,
                        background: AdminUiStyle.DangerBackground, labelColor: AdminUiStyle.DangerLabel);
        }

        // 閉じるボタンは既存を再利用してヘッダー右端へ移す
        var close = FindDescendant(panel, "CloseButton");
        if (close != null)
        {
            Reparent(close, header);
            close.SetAsLastSibling();
            StyleButton(close, minWidth: 140f, height: AdminUiStyle.HeaderHeight - 8f, flexible: false);
            log.AppendLine("  CloseButton を Header に移動");
        }
        else
        {
            log.AppendLine("  [WARN] CloseButton が見つかりません");
        }
    }

    // 「開く」タブ。AdminPanel の外（AdminCanvas 直下）に置く一点物のボタンなので、
    // TabBasicButtonOrder のような配列駆動ではなく単独の関数にしてある。
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
            label.fontSizeMin      = AdminUiStyle.ButtonFontMin;
            label.fontSizeMax      = AdminUiStyle.ButtonFontMax;
            label.alignment        = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.color            = AdminUiStyle.ButtonLabel;

            // StyleButton を通らない経路なので、既定ラベルもここで入れ直す
            // （実行時にこのボタンのラベルを書き換える処理は無いため、
            //   ここで入れないと "OPEN" のまま日本語にならない）
            if (ToolbarButtonInitialLabel.TryGetValue("OpenTabButton", out var openLabel))
                label.text = openLabel;
        }

        // カメラ映像の上に単独で浮かぶボタンなので、地の色は明示しておく
        ApplyButtonColors(btn, AdminUiStyle.ButtonBackground);

        // 実際の表示/非表示は AdminUIManager.ApplyVisible() が起動時に
        // visibleOnStart を見て決める（パネルが表示中なら Open タブは要らない）ので、
        // ここでの Active 状態は最終的な見た目を左右しない。Editor 上で自然に見える
        // よう、ひとまず表示させておく
        log.AppendLine("  OpenTabButton を配置");
    }

    // タブ行。3つのタブボタンを左に、右端に件数ラベルを置く。
    // どのタブが選択中かの色付けは AdminUIManager が行う（Builder は非選択色で置くだけ）
    private static void BuildTabBar(Transform panel, Transform tabBar, StringBuilder log)
    {
        var layout = GetOrAdd<HorizontalLayoutGroup>(tabBar.gameObject);
        layout.spacing                = ToolbarSpacing;
        layout.childAlignment         = TextAnchor.MiddleLeft;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = false;
        layout.childForceExpandHeight = true;

        SetHeight(tabBar, AdminUiStyle.TabBarHeight);

        for (int i = 0; i < TabBarButtonOrder.Length; i++)
        {
            var btn = FindOrCreateButton(panel, tabBar, TabBarButtonOrder[i], log);
            if (btn == null) continue;

            Reparent(btn, tabBar);
            btn.SetSiblingIndex(i);
            // タブは flexible: false。幅を分配して伸ばすと下のページ幅と揃って
            // 「タブなのかボタンなのか」が見分けにくくなるため、内容ぶんの幅で置く
            StyleButton(btn, AdminUiStyle.ToolbarBtnMinW, AdminUiStyle.TabBarHeight - 8f, flexible: false);
        }

        // 右端の件数ラベル「全12件 / 有効8件」。
        // flexibleWidth=1 で余白を全部吸わせ、右寄せにすることで右端に張り付く
        var count = FindOrCreateChild(tabBar, "EntryCountText", log);
        count.SetAsLastSibling();
        var countText = GetOrAdd<TextMeshProUGUI>(count.gameObject);
        if (string.IsNullOrEmpty(countText.text) || countText.text == "New Text")
            countText.text = "全0件 / 有効0件";
        countText.enableAutoSizing = false;
        countText.fontSize         = HelpFontSize;
        countText.alignment        = TextAlignmentOptions.MidlineRight;
        countText.textWrappingMode = TextWrappingModes.NoWrap;
        countText.overflowMode     = TextOverflowModes.Ellipsis;
        countText.color            = AdminUiStyle.TextOnPanel;
        Flexible(count, flexibleWidth: 1f);

        log.AppendLine($"  TabBar に {tabBar.childCount} 個の要素を配置");
    }

    // タブの中身。3ページを同じ場所に重ねて置き、AdminUIManager が
    // SetActive で1つだけ見せる。
    //
    // TabContent 自身には高さを固定しない。VerticalLayoutGroup は
    // 「アクティブな子だけ」から preferredHeight を計算するので、
    // 1行のページ（72px）と2段のスライダー（188px）で高さが自動的に切り替わり、
    // 一覧（EntryScrollView）が残りを全部使う形になる
    private static void BuildTabContent(Transform panel, Transform tabContent, StringBuilder log)
    {
        var layout = GetOrAdd<VerticalLayoutGroup>(tabContent.gameObject);
        layout.padding                = new RectOffset(0, 0, 0, 0);
        layout.spacing                = 0f;
        layout.childAlignment         = TextAnchor.UpperCenter;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = true;
        layout.childForceExpandHeight = false;
        ClearFixedHeight(tabContent);

        var basicPage = FindOrCreateChild(tabContent, "TabBasicPage", log);
        var spacePage = FindOrCreateChild(tabContent, "TabSpacePage", log);
        var tunePage  = FindOrCreateChild(tabContent, "TabTunePage",  log);

        basicPage.SetSiblingIndex(0);
        spacePage.SetSiblingIndex(1);
        tunePage .SetSiblingIndex(2);

        BuildButtonRow(panel, basicPage, TabBasicButtonOrder, log);
        BuildButtonRow(panel, spacePage, TabSpaceButtonOrder, log);
        BuildTunePage(tunePage, log);

        // Editor で開いたときに何も見えないと壊れて見えるので、
        // 既定で「基本」タブを開いた状態にしておく。
        // 実行中は AdminUIManager が最後に選んだタブを復元する
        SetActive(basicPage, true);
        SetActive(spacePage, false);
        SetActive(tunePage,  false);
    }

    // 「基本」「宇宙モード」ページのボタン1行。
    // order に載っている名前のボタンをパネル全体から探して、この行へ引っ越させる。
    // 旧構成の Toolbar/SpaceToolbar/SettingsToolbar に居たボタンも
    // 名前が一致すればここで拾われるので、作り直しは起きない
    // （＝Inspector のアサインや onClick の手設定は失われない）
    private static void BuildButtonRow(Transform panel, Transform page, string[] order, StringBuilder log)
    {
        var layout = GetOrAdd<HorizontalLayoutGroup>(page.gameObject);
        layout.spacing                = ToolbarSpacing;
        layout.childAlignment         = TextAnchor.MiddleCenter;
        layout.childControlWidth      = true;
        layout.childControlHeight     = true;
        layout.childForceExpandWidth  = false;   // flexibleWidth で分配する
        layout.childForceExpandHeight = true;

        SetHeight(page, AdminUiStyle.TabRowHeight);

        for (int i = 0; i < order.Length; i++)
        {
            var btn = FindOrCreateButton(panel, page, order[i], log);
            if (btn == null) continue;

            Reparent(btn, page);
            btn.SetSiblingIndex(i);
            StyleButton(btn, AdminUiStyle.ToolbarBtnMinW, AdminUiStyle.TabRowHeight - 8f, flexible: true);
        }
        log.AppendLine($"  {page.name} に {page.childCount} 個のボタンを配置");
    }

    // 「検出の調整」ページ。スライダー6ブロックを3列×2行に並べる。
    //
    // HorizontalLayoutGroup を2つ入れ子にするのではなく GridLayoutGroup にしたのは、
    // ブロックの幅を全部同じにしたいため（数値がタブの中で縦に揃って読める）。
    // GridLayoutGroup はセルを自動で伸縮しないので、幅は TuneCellWidth の実寸で持つ
    private static void BuildTunePage(Transform page, StringBuilder log)
    {
        // 横1行のページと違い HorizontalLayoutGroup が残っていると競合するので落とす
        var strayLayout = page.GetComponent<HorizontalLayoutGroup>();
        if (strayLayout != null) Undo.DestroyObjectImmediate(strayLayout);

        var grid = GetOrAdd<GridLayoutGroup>(page.gameObject);
        grid.padding          = new RectOffset(0, 0, 0, 0);
        grid.cellSize         = new Vector2(TuneCellWidth, AdminUiStyle.SliderBlockH);
        grid.spacing          = new Vector2(TuneGridSpacing, TuneGridSpacing);
        grid.startCorner      = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis        = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment   = TextAnchor.UpperCenter;
        grid.constraint       = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount  = 3;

        // GridLayoutGroup は行数から preferredHeight を出せるが、
        // 親（TabContent）が高さを聞きに来るタイミングで確実に値が要るので明示する
        SetHeight(page, AdminUiStyle.SliderBlockH * 2f + TuneGridSpacing);

        for (int i = 0; i < TuneSliders.Length; i++)
            BuildSliderBlock(page, TuneSliders[i], i, log);

        log.AppendLine($"  TabTunePage に {TuneSliders.Length} 個のスライダーを配置");
    }

    // スライダー1個ぶんのブロックを組む。
    //
    //   {Key}Block                     VerticalLayoutGroup
    //     ├ {Key}Label                 ラベル（値を含む文言。Manager が毎回書き換える）
    //     └ {Key}Slider                Slider 本体
    //          ├ Background            トラック（溝）
    //          ├ Fill Area / Fill      左からの塗り
    //          └ Handle Slide Area / Handle  つまみ
    //
    // 名前と入れ子は uGUI 標準（GameObject > UI > Slider が作る構成）に合わせてある。
    // Slider コンポーネントは fillRect / handleRect のアンカーを毎フレーム書き換えるので、
    // ここで入れる位置は「Slider が触らない部分」だけが意味を持つ。
    private static void BuildSliderBlock(Transform page, SliderSpec spec, int index, StringBuilder log)
    {
        var block = FindOrCreateChild(page, spec.Key + "Block", log);
        Reparent(block, page);
        block.SetSiblingIndex(index);

        var blockLayout = GetOrAdd<VerticalLayoutGroup>(block.gameObject);
        blockLayout.padding                = new RectOffset(4, 4, 2, 2);
        blockLayout.spacing                = 4f;
        blockLayout.childAlignment         = TextAnchor.UpperLeft;
        blockLayout.childControlWidth      = true;
        blockLayout.childControlHeight     = true;
        blockLayout.childForceExpandWidth  = true;
        blockLayout.childForceExpandHeight = false;
        // GridLayoutGroup の下では cellSize が優先されるので実質使われないが、
        // ブロック単体で他の場所に置いても同じ高さになるように持たせておく
        SetHeight(block, AdminUiStyle.SliderBlockH);

        // ── ラベル ──
        var labelT = FindOrCreateChild(block, spec.Key + "Label", log);
        labelT.SetSiblingIndex(0);
        var label = GetOrAdd<TextMeshProUGUI>(labelT.gameObject);
        // 文言は content なので既存があれば触らない（実行中は Manager が値付きで書き換える）
        if (string.IsNullOrEmpty(label.text) || label.text == "New Text")
            label.text = spec.InitialLabel;
        label.enableAutoSizing = true;
        label.fontSizeMin      = AdminUiStyle.RowFontMin;
        label.fontSizeMax      = AdminUiStyle.ButtonFontMin;   // ラベルはボタン文字より大きくしない
        label.alignment        = TextAlignmentOptions.MidlineLeft;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode     = TextOverflowModes.Ellipsis;
        label.color            = AdminUiStyle.TextOnPanel;     // パネル地（暗色）の上に直接載る
        SetHeight(labelT, SliderLabelH);

        // ── スライダー本体 ──
        var sliderT = FindOrCreateChild(block, spec.Key + "Slider", log);
        sliderT.SetSiblingIndex(1);
        SetHeight(sliderT, SliderBarH);

        var slider = GetOrAdd<Slider>(sliderT.gameObject);

        // Background（溝）。uGUI 標準は上下 25%–75% の細い溝だが、
        // マウスで狙う面積を稼ぎたいのでスライダーの高さいっぱいに敷く
        var bg = FindOrCreateChild(sliderT, "Background", log);
        StretchBand(bg, 0f, 0f);
        GetOrAdd<Image>(bg.gameObject).color = AdminUiStyle.SliderTrack;

        // Fill Area / Fill。左右にハンドル半個ぶんの余白を空けて、
        // つまみの中心と塗りの端が一致するようにする
        var fillArea = FindOrCreateChild(sliderT, "Fill Area", log);
        StretchBand(fillArea, SliderHandleW * 0.5f, SliderHandleW * 0.5f);
        var fill = FindOrCreateChild(fillArea, "Fill", log);
        StretchBand(fill, 0f, 0f);
        GetOrAdd<Image>(fill.gameObject).color = AdminUiStyle.SliderFill;

        // Handle Slide Area / Handle
        var slideArea = FindOrCreateChild(sliderT, "Handle Slide Area", log);
        StretchBand(slideArea, SliderHandleW * 0.5f, SliderHandleW * 0.5f);
        var handle = FindOrCreateChild(slideArea, "Handle", log);
        var handleRt = handle.GetComponent<RectTransform>();
        Undo.RecordObject(handleRt, "style slider handle");
        handleRt.anchorMin = new Vector2(0f, 0f);
        handleRt.anchorMax = new Vector2(0f, 1f);
        handleRt.pivot     = new Vector2(0.5f, 0.5f);
        handleRt.sizeDelta = new Vector2(SliderHandleW, 0f);   // 横だけ実寸、縦はバーいっぱい
        var handleImage = GetOrAdd<Image>(handle.gameObject);
        handleImage.color = AdminUiStyle.SliderHandle;

        slider.fillRect      = fill.GetComponent<RectTransform>();
        slider.handleRect    = handleRt;
        slider.targetGraphic = handleImage;
        slider.direction     = Slider.Direction.LeftToRight;
        slider.wholeNumbers  = spec.WholeNumbers;
        slider.minValue      = spec.Min;
        slider.maxValue      = spec.Max;
        // 再構築で範囲が変わったときに、前回の値が範囲外へ取り残されないようにする。
        // 実際の値は AdminUIManager が起動時に SettingsStore から入れ直す
        slider.value         = Mathf.Clamp(slider.value, spec.Min, spec.Max);
        slider.interactable  = true;
    }

    // タブごとの1行ヘルプ。ラベルだけでは伝わらない用語（丸窓・宇宙モードの保存など）を
    // 補うための行で、Manager がタブ切替のたびに文言を差し替える
    private static void BuildHelp(Transform help, StringBuilder log)
    {
        var text = GetOrAdd<TextMeshProUGUI>(help.gameObject);
        if (string.IsNullOrEmpty(text.text) || text.text == "New Text")
            text.text = TabBasicHelpText;
        text.enableAutoSizing = false;
        text.fontSize         = HelpFontSize;
        text.alignment        = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode     = TextOverflowModes.Ellipsis;
        // 本文（白）より一段落とした色。読めるが主役ではない、を色で示す
        text.color            = AdminUiStyle.HelpTextColor;

        SetHeight(help, AdminUiStyle.HelpHeight);
        log.AppendLine("  TabHelpText を整列");
    }

    private static void BuildStatus(Transform status, StringBuilder log)
    {
        var text = GetOrAdd<TextMeshProUGUI>(status.gameObject);
        text.enableAutoSizing = false;
        text.fontSize  = StatusFontSize;
        text.alignment = TextAlignmentOptions.TopLeft;
        // 【変更】NoWrap をやめて折り返す。
        // 1行固定だと「[NG] 変換に失敗しました: ...」のような長いメッセージが
        // 途中で "…" に化けて、何が起きたのか分からなかった。
        // 高さを2行ぶん（AdminUiStyle.StatusHeight）取り、折り返して全部見せる
        text.textWrappingMode = TextWrappingModes.Normal;
        text.overflowMode     = TextOverflowModes.Ellipsis;
        text.color            = AdminUiStyle.TextOnPanel;

        SetHeight(status, AdminUiStyle.StatusHeight);
        log.AppendLine("  StatusText を整列（2行）");
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

        // 行そのもの（と行の中の [表示中][選択][変換済み] ボタン）は
        // AdminUIManager.BuildEntryRow() が実行時に生成する。
        // 高さと文字サイズは AdminUiStyle.EntryRowHeight / RowButtonHeight /
        // RowFontMin / RowFontMax を両者が参照するので、ここで作る器は
        // 「行の間隔」だけを持てばよい（間隔 6px は行の当たり判定を分けるため）
        var contentLayout = GetOrAdd<VerticalLayoutGroup>(content.gameObject);
        contentLayout.padding                = new RectOffset(4, 4, 4, 4);
        contentLayout.spacing                = 6f;
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
        rect.scrollSensitivity = AdminUiStyle.EntryRowHeight * 0.5f;
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

        // ── ヘッダー ──
        AssignButton(so, panel, "TestLaunchButton", "testLaunchButton", null,       log);
        AssignButton(so, panel, "CloseButton",      "closeButton",      null,       log);
        AssignButton(so, panel, "QuitButton",       "quitButton",       "quitText", log);

        // OpenTabButton は AdminPanel の外（AdminCanvas 直下）にあるため、
        // panel ではなく panel.parent を探索の起点にする
        AssignButton(so, panel.parent, "OpenTabButton", "openTabButton", null, log);

        // ── タブ行 ──
        AssignButton(so, panel, "TabBasicButton", "tabBasicButton", "tabBasicText", log);
        AssignButton(so, panel, "TabSpaceButton", "tabSpaceButton", "tabSpaceText", log);
        AssignButton(so, panel, "TabTuneButton",  "tabTuneButton",  "tabTuneText",  log);
        Assign(so, "entryCountText", FindComponent<TextMeshProUGUI>(panel, "EntryCountText"), log);

        // ページそのもの（Manager が SetActive で1つだけ見せる）
        Assign(so, "tabBasicPage", FindDescendant(panel, "TabBasicPage"), log);
        Assign(so, "tabSpacePage", FindDescendant(panel, "TabSpacePage"), log);
        Assign(so, "tabTunePage",  FindDescendant(panel, "TabTunePage"),  log);

        Assign(so, "tabHelpText", FindComponent<TextMeshProUGUI>(panel, "TabHelpText"), log);
        Assign(so, "statusText",  FindComponent<TextMeshProUGUI>(panel, "StatusText"),  log);

        // ── 基本タブ ──
        AssignButton(so, panel, "RefreshButton",     "refreshButton",     null,              log);
        AssignButton(so, panel, "CameraIndexButton", "cameraIndexButton", "cameraIndexText", log);
        AssignButton(so, panel, "ImgEnableButton",   "imgEnableButton",   "imgEnableText",   log);
        AssignButton(so, panel, "MatteButton",       "matteButton",       "matteText",       log);
        AssignButton(so, panel, "SkeletonButton",    "skeletonButton",    "skeletonText",    log);
        AssignButton(so, panel, "ImageResButton",    "imageResButton",    "imageResText",    log);

        // ── 宇宙モードタブ ──
        AssignButton(so, panel, "SpaceModeButton",   "spaceModeButton",   "spaceModeText",   log);
        AssignButton(so, panel, "FrameToggleButton", "frameToggleButton", "frameToggleText", log);
        AssignButton(so, panel, "UfoToggleButton",   "ufoToggleButton",   "ufoToggleText",   log);
        AssignButton(so, panel, "HanabiModeButton",  "hanabiModeButton",  "hanabiModeText",  log);
        AssignButton(so, panel, "SfxToggleButton",   "sfxToggleButton",   "sfxToggleText",   log);

        // ── 検出の調整タブ ──
        // GameObject 名（HandUpSlider / HandUpLabel）と フィールド名（handUpSlider /
        // handUpText）は先頭1文字の大小しか違わないので、TuneSliders から機械的に導く。
        // 名前を1か所（TuneSliders）でしか持たないので、片方だけ直して
        // 黙って null が入る、という失敗が起きない
        foreach (var spec in TuneSliders)
        {
            string field = char.ToLowerInvariant(spec.Key[0]) + spec.Key.Substring(1);
            Assign(so, field + "Slider", FindComponent<Slider>(panel, spec.Key + "Slider"), log);
            Assign(so, field + "Text",   FindComponent<TextMeshProUGUI>(panel, spec.Key + "Label"), log);
        }

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

    // 「ボタン本体」と「その配下の Label(TMP)」をまとめて結線する。
    // ボタンは全部この形（Button + 子 Label）なので、1つずつ
    // FindDescendant → GetComponentInChildren を書き下すと同じ4行が20回並ぶ。
    // labelField に null を渡すと本体だけ結線する（Manager がラベルを書き換えない
    // 閉じる／更新のようなボタン用）
    private static void AssignButton(SerializedObject so, Transform root, string objectName,
                                     string buttonField, string labelField, StringBuilder log)
    {
        var t = FindDescendant(root, objectName);
        if (t == null)
        {
            log.AppendLine($"  [WARN] {objectName} が見つかりません");
            return;
        }

        Assign(so, buttonField, t.GetComponent<Button>(), log);
        if (labelField != null)
            Assign(so, labelField, t.GetComponentInChildren<TextMeshProUGUI>(true), log);
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

    // ページに置くボタンを探し、無ければ Image + Button + 子 Label(TMP) を組んで作る。
    //
    // 以前はここで見つからなければ WARN を出してスキップするだけだったので、
    // ボタンを1つ増やすたびに Hierarchy での手作業（GameObject 作成 → Image/Button 追加 →
    // 子 Text 追加 → AdminUIManager へのアサイン）が必要だった。
    // TabBasicButtonOrder などに名前を足すだけで完結するようにしてある。
    // 生成物は Undo に登録するので、Ctrl+Z 1回で全部戻る性質は保たれる。
    //
    // 探索の起点は fallbackParent ではなく panel（＝パネル全体）である点が重要で、
    // 「既に別のセクションに居るボタン」も見つけて返す。呼び出し側がそれを
    // Reparent するので、タブ構成を組み替えても既存のボタンは作り直されずに引っ越す。
    private static Transform FindOrCreateButton(Transform panel, Transform fallbackParent,
                                                string name, StringBuilder log)
    {
        var existing = FindDescendant(panel, name);
        if (existing != null) return existing;

        var go = new GameObject(name, typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(go, $"create {name}");
        go.transform.SetParent(fallbackParent, false);

        var img = Undo.AddComponent<Image>(go);
        var btn = Undo.AddComponent<Button>(go);
        btn.targetGraphic = img;   // interactable = false の見た目を効かせる

        var labelGO = new GameObject("Label", typeof(RectTransform));
        Undo.RegisterCreatedObjectUndo(labelGO, "create Label");
        labelGO.transform.SetParent(go.transform, false);

        var label = Undo.AddComponent<TextMeshProUGUI>(labelGO);
        label.text  = ToolbarButtonInitialLabel.TryGetValue(name, out var initial) ? initial : name;
        label.color = AdminUiStyle.ButtonLabel;
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

    // 旧構成のセクションの入れ物を片付ける。
    // 中身が空のときだけ消すのは、想定外の子（手で足した何か）を巻き添えにしないため。
    // 空でなければ [WARN] を出して残し、人が中身を確認してから消せるようにする
    private static void RemoveEmptyLegacySections(Transform panel, StringBuilder log)
    {
        foreach (var name in ObsoleteSectionNames)
        {
            var found = FindDescendant(panel, name);
            if (found == null) continue;

            if (found.childCount > 0)
            {
                log.AppendLine($"  [WARN] 旧セクション {name} に子が {found.childCount} 個残っているため削除しません");
                continue;
            }

            Undo.DestroyObjectImmediate(found.gameObject);
            log.AppendLine($"  [REMOVE] 空になった旧セクション {name} を削除");
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

    // 高さの固定を解除する。LayoutElement を消すのではなく -1（未設定）に戻すのは、
    // 幅や flexible の設定を巻き添えにしないため。
    // 前回の再構築で入った preferredHeight が残っていると、
    // 中身の高さが変わっても（例: タブを切り替えても）追従しなくなる
    private static void ClearFixedHeight(Transform t)
    {
        var le = t.GetComponent<LayoutElement>();
        if (le == null) return;

        Undo.RecordObject(le, "clear fixed height");
        le.minHeight       = -1f;
        le.preferredHeight = -1f;
        le.flexibleHeight  = 0f;
    }

    private static void Flexible(Transform t, float flexibleWidth = -1f, float flexibleHeight = -1f)
    {
        var le = GetOrAdd<LayoutElement>(t.gameObject);
        if (flexibleWidth  >= 0f) le.flexibleWidth  = flexibleWidth;
        if (flexibleHeight >= 0f) le.flexibleHeight = flexibleHeight;
    }

    // 親いっぱいに広げ、左右だけ内側に寄せる（スライダーの溝・塗り・ハンドル用）。
    // 上下は常に親いっぱい。溝を細く見せたいときはスライダー自体の高さで調整する
    private static void StretchBand(Transform t, float insetLeft, float insetRight)
    {
        var rt = t.GetComponent<RectTransform>();
        Undo.RecordObject(rt, "stretch band");
        rt.anchorMin        = Vector2.zero;
        rt.anchorMax        = Vector2.one;
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.offsetMin        = new Vector2(insetLeft,   0f);
        rt.offsetMax        = new Vector2(-insetRight, 0f);
        rt.anchoredPosition = new Vector2((insetLeft - insetRight) * 0.5f, 0f);
    }

    // SetActive を Undo に載せる。素の SetActive だと Ctrl+Z で戻らず、
    // 「再構築を取り消したのに表示中のタブだけ変わったまま」になる
    private static void SetActive(Transform t, bool active)
    {
        if (t.gameObject.activeSelf == active) return;

        Undo.RecordObject(t.gameObject, "toggle page");
        t.gameObject.SetActive(active);
    }

    // ボタンをタブページ / ヘッダー用に整える。
    // background / labelColor は省略すると通常ボタンの配色（白地＋暗紺文字）になる。
    // 終了ボタンだけが Danger 系を指定して呼ぶ。
    //
    // ── ラベルの文字列もここで揃える理由 ──
    //   以前は「文字列は content なので Builder は触らない」方針で、
    //   ToolbarButtonInitialLabel を新規作成時にしか適用していなかった。
    //   その結果、日本語化のあとに再構築しても既に存在するボタン
    //   （QuitButton / CloseButton / TestLaunchButton / RefreshButton / OpenTabButton）は
    //   "QUIT" "Close" "Test Launch" のまま英語で残ってしまった。
    //   実行時にラベルを書き換えるのは状態を持つトグル類だけなので、
    //   これらは永久に英語のままになる（＝日本語化が効かない）。
    //   状態つきのボタンは AdminUIManager が Start() で上書きするため、
    //   ここで一律に既定ラベルを入れても衝突しない
    private static void StyleButton(Transform btn, float minWidth, float height, bool flexible,
                                    Color? background = null, Color? labelColor = null)
    {
        var le = GetOrAdd<LayoutElement>(btn.gameObject);
        le.minWidth        = minWidth;
        le.preferredWidth  = -1f;                 // 未設定 = minWidth が下限として効く
        le.minHeight       = height;
        le.preferredHeight = height;
        le.flexibleWidth   = flexible ? 1f : 0f;
        le.flexibleHeight  = 0f;

        ApplyButtonColors(btn, background ?? AdminUiStyle.ButtonBackground);

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
        label.fontSizeMin      = AdminUiStyle.ButtonFontMin;
        label.fontSizeMax      = AdminUiStyle.ButtonFontMax;
        label.alignment        = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.overflowMode     = TextOverflowModes.Ellipsis;

        // 文字色もここで統一する。ここで塗ることで、既にシーンに存在するボタンも
        // 再構築のたびに揃う（新規作成時だけ色を入れていると、既存ボタンは
        // 白のまま取り残される）。
        label.color = labelColor ?? AdminUiStyle.ButtonLabel;

        // 既定ラベルも同じ理由で毎回入れ直す（英語のまま取り残されるのを防ぐ）
        if (ToolbarButtonInitialLabel.TryGetValue(btn.name, out var initial) && label.text != initial)
            label.text = initial;
    }

    // ボタンの地の色と、押下/無効時の色の振れ幅を決める。
    //
    // ── なぜ Image.color と ColorBlock を分けるか ──
    //   uGUI の最終的な色は「Image.color × Selectable の遷移色」の掛け算になる。
    //   ON/OFF などの状態色は AdminUIManager が実行中に Image.color を塗り替えて表すので、
    //   ColorBlock 側の normal は白（＝素通し）にしておかないと状態色が濁る。
    //
    // ── disabledColor を明示する理由 ──
    //   Unity 既定の disabledColor は α0.5 の白で、白いボタンの上では
    //   文字とのコントラストが 4.5:1 を割って「押せないのか、字が薄いだけなのか」
    //   分からなくなる。AdminUiStyle.DisabledBackground（不透明のグレー）を
    //   掛けることで、薄くではなく「灰色になる」見え方に固定する。
    private static void ApplyButtonColors(Transform btn, Color background)
    {
        var image = btn.GetComponent<Image>();
        if (image != null)
        {
            Undo.RecordObject(image, "style button background");
            image.color = background;
        }

        var button = btn.GetComponent<Button>();
        if (button == null) return;

        Undo.RecordObject(button, "style button colors");
        if (button.targetGraphic == null) button.targetGraphic = image;

        var colors = button.colors;
        colors.normalColor      = Color.white;
        colors.highlightedColor = new Color(0.90f, 0.94f, 1f);   // わずかに青寄せ＝カーソルが乗っている
        colors.pressedColor     = new Color(0.75f, 0.80f, 0.88f);
        colors.selectedColor    = Color.white;
        colors.disabledColor    = AdminUiStyle.DisabledBackground;
        colors.colorMultiplier  = 1f;
        button.colors = colors;
    }
}
#endif
