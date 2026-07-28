using System;
using UnityEngine;

[System.Serializable]
public class ImageToParticlesSettings
{
    [Tooltip("リサイズ解像度（n×n）。大きいほど細かい")]
    public int resolution = 64;

    [Tooltip("白とみなすRGB閾値（0-255）")]
    [Range(0, 255)]
    public int whiteThreshold = 200;

    [Tooltip("彩度の最小値（max-min）。これ以下はノイズとして除外")]
    [Range(0, 255)]
    public int saturationThreshold = 30;

    [Tooltip("白いピクセルも半透明の白粒子として含める")]
    public bool includeWhite = false;

    [Tooltip("白粒子のアルファ値（0=透明, 1=不透明）")]
    [Range(0f, 1f)]
    public float whiteAlpha = 0.3f;
}

public class ImageToParticles : IDisposable
{
    private readonly ImageToParticlesSettings _s;

    // ── 中間バッファ ──
    // ReadPixels 用の Texture2D は Convert() ごとに作り直すとネイティブメモリが
    // GC 待ちで積み上がる（128×128 RGBA32 で 1回 64KB）。
    // 解像度が変わったときだけ作り直して使い回す。
    private Texture2D _work;
    private int       _workSize;
    private bool      _disposed;

    public ImageToParticles(ImageToParticlesSettings settings)
    {
        _s = settings;
    }

    // ── 変換 ──

    public ParticleData Convert(Texture2D src)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ImageToParticles));

        int n = _s.resolution;

        var rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
        rt.filterMode = FilterMode.Bilinear;
        Graphics.Blit(src, rt);

        var tex  = GetWorkTexture(n);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, n, n), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        var pixels = tex.GetPixels32();
        var points = new System.Collections.Generic.List<ParticlePoint>();

        for (int py = 0; py < n; py++)
        for (int px = 0; px < n; px++)
        {
            var col = pixels[(n - 1 - py) * n + px];

            bool isWhite = col.r > _s.whiteThreshold
                        && col.g > _s.whiteThreshold
                        && col.b > _s.whiteThreshold;

            int sat = Mathf.Max(col.r, col.g, col.b)
                    - Mathf.Min(col.r, col.g, col.b);

            if (isWhite)
            {
                // 白ピクセル
                if (_s.includeWhite)
                {
                    byte a = (byte)Mathf.Clamp(Mathf.RoundToInt(_s.whiteAlpha * 255f), 0, 255);
                    points.Add(new ParticlePoint(
                        x:    px / (float)(n - 1),
                        y:    py / (float)(n - 1),
                        r:    255, g: 255, b: 255,
                        size: 0.6f,  // 白粒子は少し小さく
                        a:    a      // whiteAlpha を実際に反映させる
                    ));
                }
            }
            else
            {
                // 色ピクセル（彩度チェック）
                if (sat >= _s.saturationThreshold)
                {
                    points.Add(new ParticlePoint(
                        x:    px / (float)(n - 1),
                        y:    py / (float)(n - 1),
                        r:    col.r,
                        g:    col.g,
                        b:    col.b,
                        size: 1f,
                        a:    255
                    ));
                }
            }
        }

        Debug.Log($"[ImageToParticles] {n}x{n} -> {points.Count} particles " +
                  $"(includeWhite={_s.includeWhite})");
        return new ParticleData(n, n, points.ToArray());
    }

    // ── 中間バッファ管理 ──

    /// <summary>n×n の作業用 Texture2D を返す。サイズが変わったときだけ作り直す</summary>
    private Texture2D GetWorkTexture(int n)
    {
        if (_work != null && _workSize == n) return _work;

        if (_work != null)
        {
            DestroyTexture(_work);
            Debug.Log($"[ImageToParticles] Work texture resized: {_workSize} -> {n}");
        }

        _work     = new Texture2D(n, n, TextureFormat.RGBA32, false);
        _workSize = n;
        return _work;
    }

    /// <summary>作業用 Texture2D を解放する。再利用しないなら必ず呼ぶこと</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_work != null)
        {
            DestroyTexture(_work);
            _work     = null;
            _workSize = 0;
            Debug.Log("[ImageToParticles] Disposed work texture");
        }
    }

    // Object.Destroy はエディタの非再生時に使えないので切り替える
    private static void DestroyTexture(Texture2D tex)
    {
        if (tex == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(tex);
        else                       UnityEngine.Object.DestroyImmediate(tex);
    }
}