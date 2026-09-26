// System.Diagnostics.Debug と UnityEngine.Debug が衝突するため、
// 名前空間ごと using せず [Conditional] だけを別名で取り込む。
using ConditionalAttribute = System.Diagnostics.ConditionalAttribute;
using UnityEngine;

// ===== ArLog =====
// 詳細ログを一括で切れるようにするラッパ。
//
// Verbose() は AR_VERBOSE_LOG が定義されていないビルドでは
// 「呼び出しごと」コンパイル時に消える（＝引数の評価も走らない）。
// そのため次のような文字列補間を書いても、無効時のコストは完全にゼロになる。
//
//     ArLog.Verbose($"[Pose] P{i} shoulderWidth={w:F3}");
//
// これが `if (verbose) Debug.Log(...)` より優れている点で、
// 後者は verbose が false でも文字列生成（GC アロケーション）が発生してしまう。
//
// ── AR_VERBOSE_LOG を有効化する手順 ──
//   方法1（推奨・エディタ操作）:
//     Edit > Project Settings > Player > Other Settings >
//     Script Compilation > Scripting Define Symbols に
//     `AR_VERBOSE_LOG` を追加して Apply。
//     ※ Player 設定はビルドターゲットごとに独立しているので、
//        使用中のプラットフォームタブで追加すること。
//
//   方法2（ファイル操作・プロジェクト設定を汚さない）:
//     `Assets/csc.rsp` を新規作成し、次の1行を書いて Unity を再コンパイルさせる。
//         -define:AR_VERBOSE_LOG
//     不要になったら csc.rsp を削除するだけで元に戻る。
//
//   既定では OFF（詳細ログなし）。本番運用時は OFF のままにする。

public static class ArLog
{
    // ── 詳細ログ（毎フレーム／毎人に出るもの）──
    // AR_VERBOSE_LOG 未定義時は呼び出し自体が消える
    [Conditional("AR_VERBOSE_LOG")]
    public static void Verbose(string message) => Debug.Log(message);

    // ── 常時出すログ（初期化完了・状態変化・エラーなど低頻度なもの）──
    public static void Info(string message)  => Debug.Log(message);
    public static void Warn(string message)  => Debug.LogWarning(message);
    public static void Error(string message) => Debug.LogError(message);
}
