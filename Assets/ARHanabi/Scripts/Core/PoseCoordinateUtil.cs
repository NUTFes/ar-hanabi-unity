using UnityEngine;

// ===== PoseCoordinateUtil =====
// MediaPipe の landmark 正規化座標を Unity のスクリーン座標／ワールド座標に変換する。
//
// ── y を反転してはいけない ──
// PoseLandmarkDetector は WebCamTexture.GetPixels32() で取得した配列をそのまま
// MediaPipe に渡している。GetPixels32() は Texture2D の慣習どおり「下の行から」
// 並んだ配列を返すため、MediaPipe 側はそれを「上の行から」と解釈する。
// 結果として MediaPipe が返す y は、画面に表示されているカメラ映像に対して
// 既に「下が 0 / 上が 1」= Unity のスクリーン座標と同じ向きになっている。
// したがって y はそのまま Screen.height に掛けるのが正しく、1f - y にすると
// スケルトンが上下逆に描画される（実機で確認済み）。
//
// ── 「花火とスケルトンで反転が食い違っている」という指摘は誤りだった ──
// FireworkLauncher.LaunchAt() は worldPos.y を launchHeightMin〜Max の乱数で
// 上書きしており、変換で得た y を捨てている。使っているのは x だけなので、
// 花火側は y の反転有無に一切影響されない。
// 「花火は正しく見える」という観察は y の正しさを何も保証していなかった。
//
// x は反転しない。範囲外の値が来ても画面内に収まるよう Clamp01 を適用する。

public static class PoseCoordinateUtil
{
    // ── 正規化座標 → スクリーン座標 ──
    // distance はカメラからの距離（ScreenToWorldPoint に渡す z 値）
    public static Vector3 ToScreenPoint(float normalizedX, float normalizedY, float distance)
    {
        return new Vector3(
            Mathf.Clamp01(normalizedX) * Screen.width,
            Mathf.Clamp01(normalizedY) * Screen.height,   // 反転しない（理由は上のコメント）
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
