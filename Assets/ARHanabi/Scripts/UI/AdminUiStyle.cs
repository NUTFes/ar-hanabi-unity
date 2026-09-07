using UnityEngine;

// ===== AdminUiStyle =====
// Admin画面の配色・寸法の「単一ソース」。
//
// ── なぜ static クラスなのか ──
// 以前は同じ色（ボタン文字色）が AdminUIManager と AdminUIBuilder の両方に
// 定数として書かれており、「片方を変えたらもう片方も手で直すこと」という
// コメント付きで運用していた。案の定ズレるので、ここに集約した。
// ランタイム側（Scripts/）に置いてあるので Editor 側（Editor/AdminUIBuilder）
// からも参照できる（Editor → Runtime の参照は合法。逆はできない）。
//
// ── 配色の設計原則 ──
//  1. 全ての「文字色 / 背景色」の組み合わせで WCAG AA（コントラスト比 4.5:1）以上。
//     会場は明るく画面に外光が乗るため、大きな文字でも 3:1 に妥協しない。
//     比率は相対輝度から (L1 + 0.05) / (L2 + 0.05) で算出したもので、
//     各定数のコメントに実測値を書いてある。色を変えるときは比率も計算し直すこと。
//  2. 「色が付いている＝有効・注意」で意味を固定する。
//     ON = 緑 / 危険 = 赤 / 選択中 = 濃紺。白背景は「通常・OFF側」の意味に固定。
//  3. パネル地が暗色・ボタンが明色なので、ボタンの輪郭線は引かない
//     （背景との明度差だけで十分に分離して見える）。
public static class AdminUiStyle
{
    // ── パネル地 ──
    // #14181F を α0.85 で敷く。以前は半透明の白 (1,1,1,0.39) だったため、
    // 白いタイトル文字とステータス文字がカメラ映像次第でほぼ読めなかった。
    public static readonly Color PanelBackground = new Color(0.078f, 0.094f, 0.122f, 0.85f);

    // パネル地の上に載る文字
    public static readonly Color TextOnPanel = Color.white;                          // 15.4:1
    public static readonly Color HelpTextColor = new Color(0.722f, 0.753f, 0.800f);  //  8.2:1

    // ── 通常ボタン（白背景 + 暗紺文字）──
    public static readonly Color ButtonBackground = new Color(0.949f, 0.957f, 0.969f); // #F2F4F7
    public static readonly Color ButtonLabel      = new Color(0.090f, 0.118f, 0.169f); // #171E2B / 14.6:1

    // ── トグル ON（緑背景 + 暗緑文字）──
    public static readonly Color OnBackground = new Color(0.102f, 0.702f, 0.302f);   // #1AB34D
    public static readonly Color OnLabel      = new Color(0.043f, 0.133f, 0.075f);   // #0B2213 / 5.8:1

    // ── 危険操作（終了ボタン）──
    // 通常時から薄赤にしておき、「押すと戻れない操作」であることを常に示す。
    public static readonly Color DangerBackground = new Color(0.961f, 0.839f, 0.839f); // #F5D6D6
    public static readonly Color DangerLabel      = new Color(0.545f, 0.102f, 0.102f); // #8B1A1A / 6.9:1

    // 確認待ち（もう一度押すと終了）。
    // 以前はここが濃赤背景に暗い文字で 3.3:1（パネル中で最悪）だった。白文字にして解決。
    public static readonly Color DangerArmedBackground = new Color(0.702f, 0.071f, 0.071f); // #B31212
    public static readonly Color DangerArmedLabel      = Color.white;                       // 7.5:1

    // ── タブ ──
    public static readonly Color TabSelectedBackground = new Color(0.118f, 0.165f, 0.239f); // #1E2A3D
    public static readonly Color TabSelectedLabel      = Color.white;                       // 11.6:1
    // 非選択タブは通常ボタンと同じ（ButtonBackground / ButtonLabel）

    // ── enum ボタン（花火の種類）──
    // bool のトグルと見分けが付くよう、値ごとに背景色を変える。
    // 「和風のみ」は通常ボタンと同じ白（＝宇宙要素なしの既定状態）。
    public static readonly Color EnumMixBackground   = new Color(0.804f, 0.922f, 0.839f); // #CDEBD6
    public static readonly Color EnumMixLabel        = new Color(0.059f, 0.239f, 0.125f); // #0F3D20 / 8.9:1
    public static readonly Color EnumSpaceBackground = new Color(0.851f, 0.804f, 0.922f); // #D9CDEB
    public static readonly Color EnumSpaceLabel      = new Color(0.180f, 0.102f, 0.322f); // #2E1A52 / 9.3:1

    // ── 無効化ボタン ──
    // Unity 既定の「α0.5 で薄くする」だと白ボタン上の文字が 4.5:1 を割るため、
    // Button.colors.disabledColor に明示的な色を設定して使う。
    public static readonly Color DisabledBackground = new Color(0.788f, 0.812f, 0.847f); // #C9CFD8
    public static readonly Color DisabledLabel      = new Color(0.353f, 0.392f, 0.447f); // #5A6472 / 4.6:1

    // ── スライダー ──
    public static readonly Color SliderTrack  = new Color(0.227f, 0.263f, 0.337f); // #3A4356
    public static readonly Color SliderFill   = new Color(0.298f, 0.553f, 1.000f); // #4C8DFF
    public static readonly Color SliderHandle = new Color(0.949f, 0.957f, 0.969f); // #F2F4F7

    // ── 一覧の行 ──
    public static readonly Color RowNormal   = new Color(1f, 1f, 1f, 0.06f);
    public static readonly Color RowSelected = new Color(0.35f, 0.55f, 0.95f, 0.45f);

    // ── ステータス行の接頭辞の色（rich text 用の16進文字列）──
    // SetStatus() が "[OK] ..." のような接頭辞を検出して <color=#...> を差し込む。
    public const string StatusOkHex     = "#5EE08A";
    public const string StatusWarnHex   = "#F2D257";
    public const string StatusErrorHex  = "#FF7B7B";
    public const string StatusLaunchHex = "#6BD5FF";

    // ── 寸法（Builder が使う。Manager 側は行の生成で一部を使う）──
    public const float PanelPadding     = 24f;
    public const float HeaderHeight     = 64f;
    public const float TabBarHeight     = 56f;
    public const float TabRowHeight     = 72f;   // タブ内の1行（ボタン列）の高さ
    public const float SliderBlockH     = 88f;   // スライダー1ブロック（ラベル+スライダー）
    public const float HelpHeight       = 28f;
    public const float StatusHeight     = 64f;   // 2行ぶん
    public const float EntryRowHeight   = 64f;
    public const float RowButtonHeight  = 48f;
    public const float ToolbarBtnMinW   = 160f;

    // ボタン文字サイズ（TMP の Auto Size 範囲）
    public const float ButtonFontMin    = 18f;
    public const float ButtonFontMax    = 32f;
    public const float RowFontMin       = 14f;
    public const float RowFontMax       = 18f;
}
