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

    // ── Quad 面上へのマッピング（SkeletonRenderer と共通）──
    // カメラ映像を貼った Quad（CameraBackground）は画面よりかなり大きく張り出している
    //（MainScene の実測では横に画面外へ最大 18%程度）。そのため関節の生座標（0〜1）を
    // そのまま画面上の割合として使うと、画面端に近い人ほど実際に見えている位置と
    // ズレる（中央は一致するが、端では画面幅の1割以上ズレ得る）。
    //
    // Quad は「動画がそのまま貼られた面」なので、関節座標を Quad の UV として
    // そのまま面上に置けば、動画に映っている本人の位置と一致する
    //（SkeletonRenderer が骨を描くのと同じ考え方）。
    //
    // Unity 内蔵 Quad メッシュはローカル 1x1・原点中心・+X が右 / +Y が上で、
    // テクスチャの uv=(0,0) がローカル (-0.5,-0.5) に対応する。
    // localScale がそのまま表示サイズになるので、(u-0.5, v-0.5) を TransformPoint
    // すれば uv=(u,v) の位置にある面上の点がそのまま得られる。
    public static Vector3 LandmarkToQuadPoint(Transform backgroundQuad, float u, float v)
    {
        return backgroundQuad.TransformPoint(
            new Vector3(Mathf.Clamp01(u) - 0.5f, Mathf.Clamp01(v) - 0.5f, 0f));
    }

    // ── 関節座標 → 画面上のビューポート位置 ──
    // 一度 Quad 面上のワールド座標に変換してから WorldToViewportPoint に通すことで、
    // 「動画に映っている本人の位置」を画面上の実際の割合（0〜1が画面内）として得る。
    // backgroundQuad が未設定なら Quad マッピングを使わず (u, v) をそのまま返す
    //（画面全体マッピングへのフォールバック。ToWorldPoint と同じ考え方）
    public static Vector2 LandmarkToViewport(Camera camera, Transform backgroundQuad, float u, float v)
    {
        if (camera == null || backgroundQuad == null)
            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));

        var viewport = camera.WorldToViewportPoint(LandmarkToQuadPoint(backgroundQuad, u, v));
        return new Vector2(viewport.x, viewport.y);
    }
}
