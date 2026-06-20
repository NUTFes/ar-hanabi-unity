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

public class ImageToParticles
{
    private readonly ImageToParticlesSettings _s;

    public ImageToParticles(ImageToParticlesSettings settings)
    {
        _s = settings;
    }

    public ParticleData Convert(Texture2D src)
    {
        int n = _s.resolution;

        var rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
        rt.filterMode = FilterMode.Bilinear;
        Graphics.Blit(src, rt);

        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
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
                    byte a = (byte)(_s.whiteAlpha * 255);
                    points.Add(new ParticlePoint(
                        x: px / (float)(n - 1),
                        y: py / (float)(n - 1),
                        r: 255, g: 255, b: 255,
                        size: 0.6f  // 白粒子は少し小さく
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
                        size: 1f
                    ));
                }
            }
        }

        Debug.Log($"[ImageToParticles] {n}x{n} -> {points.Count} particles " +
                  $"(includeWhite={_s.includeWhite})");
        return new ParticleData(n, n, points.ToArray());
    }
}