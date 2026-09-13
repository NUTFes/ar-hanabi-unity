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
    // ── 左右反転（鏡像）──
    //
    // 映像を鏡像で出すとき、関節座標も同じだけ反転させないと、骨格の線も
    // 花火の打ち上げ位置も「画面に映っている本人」から左右にズレる。
    //
    // ── なぜここに置くのか ──
    //   関節座標から画面上の位置への変換は、全て下の2つの入口を通る
    //   （SkeletonRenderer も FireworkLauncher.ResolveCenterU も例外なく）。
    //   ここで1回反転させれば、利用側は反転の有無を知らなくてよい。
    //   利用側に配ると「片方だけ直し忘れて骨格と花火がズレる」が必ず起きる。
    //
    // ── 反転しても影響を受けないもの ──
    //   ・GestureDetector の肩幅（|左x − 右x| なので符号に依存しない）
    //   ・PoseTracker の最近傍マッチ（生の座標系で完結している）
    //   ・y（上下は反転しない。y の向きの話は上のコメントを参照）
    //
    // 表示側（カメラ映像そのもの）の反転は BackgroundRemoval.shader が行う。
    // 両方を同時に切り替える責任は CameraBackgroundController.MirrorHorizontal が持つ
    public static bool MirrorX { get; set; }

    /// <summary>左右反転が有効なら u を鏡像にする。無効ならそのまま返す</summary>
    public static float ApplyMirror(float u) => MirrorX ? 1f - u : u;

    // ── 正規化座標 → スクリーン座標 ──
    // distance はカメラからの距離（ScreenToWorldPoint に渡す z 値）
    public static Vector3 ToScreenPoint(float normalizedX, float normalizedY, float distance)
    {
        return new Vector3(
            ApplyMirror(Mathf.Clamp01(normalizedX)) * Screen.width,
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
            new Vector3(ApplyMirror(Mathf.Clamp01(u)) - 0.5f, Mathf.Clamp01(v) - 0.5f, 0f));
    }

    // ── 関節座標 → 画面上のビューポート位置 ──
    // 一度 Quad 面上のワールド座標に変換してから WorldToViewportPoint に通すことで、
    // 「動画に映っている本人の位置」を画面上の実際の割合（0〜1が画面内）として得る。
    // backgroundQuad が未設定なら Quad マッピングを使わず (u, v) をそのまま返す
    //（画面全体マッピングへのフォールバック。ToWorldPoint と同じ考え方）
    public static Vector2 LandmarkToViewport(Camera camera, Transform backgroundQuad, float u, float v)
    {
        // フォールバックでも反転を忘れない。ここだけ素通しにすると、
        // Quad 未設定の環境で骨格と花火が左右にズレる
        if (camera == null || backgroundQuad == null)
            return new Vector2(ApplyMirror(Mathf.Clamp01(u)), Mathf.Clamp01(v));

        var viewport = camera.WorldToViewportPoint(LandmarkToQuadPoint(backgroundQuad, u, v));
        return new Vector2(viewport.x, viewport.y);
    }
}
