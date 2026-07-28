using UnityEngine;

// ===== PoseCoordinateUtil =====
// MediaPipe の正規化座標(0〜1, 原点は左上)を Unity のスクリーン座標(原点は左下)に変換する。
// MediaPipe の y は下向きに増えるため、Unity のスクリーン座標にするには反転が必要。
//
// 以前は SkeletonRenderer が y を反転せず、FireworkLauncher が反転していたため
// スケルトンだけ上下逆に描画されていた（実機で確認済み）。
// 正しいのは花火側（1f - y）なので、そちらの挙動をこのユーティリティに一本化している。
//
// x は反転しない（左右はそのまま）。
// 範囲外の値が来ても画面内に収まるよう Clamp01 を内部で適用する
// （FireworkLauncher の既存挙動と一致させるため）。

public static class PoseCoordinateUtil
{
    // ── 正規化座標 → スクリーン座標 ──
    // distance はカメラからの距離（ScreenToWorldPoint に渡す z 値）
    public static Vector3 ToScreenPoint(float normalizedX, float normalizedY, float distance)
    {
        return new Vector3(
            Mathf.Clamp01(normalizedX)      * Screen.width,   // x はそのまま
            Mathf.Clamp01(1f - normalizedY) * Screen.height,  // y は上下反転
            distance
        );
    }

    // ── 正規化座標 → ワールド座標 ──
    public static Vector3 ToWorldPoint(Camera camera, float normalizedX, float normalizedY, float distance)
    {
        if (camera == null)
        {
            ArLog.Warn("[PoseCoord] Camera が null のためワールド座標を計算できません");
            return Vector3.zero;
        }

        return camera.ScreenToWorldPoint(ToScreenPoint(normalizedX, normalizedY, distance));
    }
}
