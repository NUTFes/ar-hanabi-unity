using UnityEngine;

// ===== LaunchRequest =====
// 花火1発分の打ち上げ指示書。
//
// これまで FireworkLauncher.LaunchSequence は8個の位置引数（normalizedPos,
// xOffsetViewport, isLarge, startDelay, volumeScale, forceImage, forceDecided,
// forcedPreset）を取っていた。呼び出し側（OnGestureDetected / LaunchTest /
// LaunchTestShell）が増えるたびに引数の組み合わせが増え、呼び出し順を間違えても
// コンパイルは通ってしまう危険があった。
//
// 体験設計（コンボ・アンサンブルなど）で「状況ごとに打ち上げ内容を組み立てる」処理が
// 増える前提のため、ここで指示書 struct にまとめておく。値を渡さなかったフィールドは
// 既定値（0 / null / false）になるが、それぞれ「従来どおりの挙動」になるよう既定値を選んである
// （sizeScale だけは 0 のままだと大きさ0になってしまうため、
//  EffectiveSizeScale で 0→1 のフォールバックを持たせている。
//  ShellPreset の childStarSize と同じイディオム）。

/// <summary>
/// 画像花火にするかどうかの決定方法。
///   Auto      … 従来どおり enableImageFirework / imageFireworkChance で毎回抽選する
///   ForceImage… 呼び出し側が既に「画像花火にする」と決めている（抽選をスキップ）
///   ForceShell… 呼び出し側が既に「型花火にする」と決めている（抽選をスキップ）
/// forcedPreset が設定されているときは、この値に関わらず型花火が優先される
///（指名した型を必ず打つのが目的の経路なので、確率で画像花火に化けると意味がない）
/// </summary>
public enum ImageDecision
{
    Auto = 0,
    ForceImage,
    ForceShell,
}

public struct LaunchRequest
{
    /// <summary>打ち上げの基準位置（人の腰など、正規化された関節座標 0〜1）。
    /// Quad経由の変換とlaunchAtScreenCenterの対象になる（FireworkLauncher.ResolveCenterU 参照）</summary>
    public Vector2 normalizedPos;

    /// <summary>基準位置からの左右オフセット（ビューポート比）。両手上げの2発分離などに使う</summary>
    public float xOffsetViewport;

    /// <summary>true: 大玉（両手上げ相当）／false: 小玉</summary>
    public bool isLarge;

    /// <summary>発射までの遅延秒数。0なら即発射</summary>
    public float startDelay;

    /// <summary>音量倍率。1が通常</summary>
    public float volumeScale;

    /// <summary>型を指名する場合はここに入れる。null なら PickPreset で抽選する</summary>
    public ShellPreset forcedPreset;

    /// <summary>画像花火にするかどうかの決定方法</summary>
    public ImageDecision image;

    /// <summary>
    /// 大きさ倍率。1が通常。0以下は「未指定」とみなし1として扱う
    ///（LaunchRequest を new しただけだと float 既定値が0になるため。
    ///  EffectiveSizeScale 経由で参照すること）
    /// </summary>
    public float sizeScale;

    /// <summary>型を名前で絞り込む。null/空なら isLarge に応じた既定
    ///（largeShellNames / smallShellNames）を使う</summary>
    public string[] nameFilter;

    /// <summary>コンボの段階（0 = 通常、1以上は連続ジェスチャーの段数）。
    /// 昇りの光跡を育てるのに使う</summary>
    public int comboStage;

    /// <summary>sizeScale の実効値。0以下（未設定）なら1</summary>
    public readonly float EffectiveSizeScale => sizeScale > 0f ? sizeScale : 1f;
}
