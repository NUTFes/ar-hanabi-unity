using UnityEngine;

// ===== BackgroundRemovalEffect =====
// CameraBackground（Quad + MeshRenderer）に背景除去シェーダーを適用する。
// MeshRenderer の sharedMaterial をカスタムシェーダーに差し替える。

[RequireComponent(typeof(MeshRenderer))]
public class BackgroundRemovalEffect : MonoBehaviour
{
    [Header("ON/OFF")]
    [SerializeField] private bool enableSegmentation = true;

    [Header("マスク設定")]
    [SerializeField, Range(0f, 1f)]   private float threshold    = 0.5f;
    [SerializeField, Range(0f, 0.5f)] private float edgeSoftness = 0.05f;

    private MeshRenderer _meshRenderer;
    private Material     _originalMaterial;
    private Material     _segMaterial;
    private WebCamTexture _webCamTexture;

    private static readonly int PropMaskTex      = Shader.PropertyToID("_MaskTex");
    private static readonly int PropThreshold    = Shader.PropertyToID("_Threshold");
    private static readonly int PropEdgeSoftness = Shader.PropertyToID("_EdgeSoftness");
    private static readonly int PropSegEnabled   = Shader.PropertyToID("_SegEnabled");

    public bool IsEnabled => enableSegmentation;

    public void SetEnabled(bool value)
    {
        enableSegmentation = value;
        if (_segMaterial != null)
            _segMaterial.SetFloat(PropSegEnabled, value ? 1f : 0f);
        if (!value) ClearMask();
        Debug.Log($"[BGRemoval] SetEnabled: {value}");
    }

    public void SetWebCamTexture(WebCamTexture tex)
    {
        _webCamTexture = tex;
        // 両方のマテリアルに WebCamTexture をセット
        _originalMaterial?.SetTexture("_MainTex", tex);
        _segMaterial?.SetTexture("_MainTex", tex);
        Debug.Log($"[BGRemoval] WebCamTexture セット: {tex.width}x{tex.height}");
    }

    public void UpdateMask(Texture2D mask)
    {
        if (_segMaterial == null) return;
        _segMaterial.SetTexture(PropMaskTex, mask);
        _segMaterial.SetFloat(PropSegEnabled, enableSegmentation ? 1f : 0f);
    }

    public void ClearMask()
    {
        if (_segMaterial == null) return;
        _segMaterial.SetFloat(PropSegEnabled, 0f);
    }

    private void Awake()
    {
        _meshRenderer    = GetComponent<MeshRenderer>();
        _originalMaterial = _meshRenderer.sharedMaterial;
    }

    private void Start()
    {
        var shader = Shader.Find("Custom/BackgroundRemoval");
        if (shader == null)
        {
            Debug.LogError("[BGRemoval] Custom/BackgroundRemoval シェーダーが見つかりません");
            enabled = false;
            return;
        }

        _segMaterial = new Material(shader);
        _segMaterial.SetFloat(PropThreshold,    threshold);
        _segMaterial.SetFloat(PropEdgeSoftness, edgeSoftness);
        _segMaterial.SetFloat(PropSegEnabled,   0f);

        // WebCamTexture がすでにセットされていれば引き継ぐ
        if (_webCamTexture != null)
            _segMaterial.SetTexture("_MainTex", _webCamTexture);
        else if (_originalMaterial != null)
        {
            var tex = _originalMaterial.GetTexture("_MainTex");
            if (tex != null) _segMaterial.SetTexture("_MainTex", tex);
        }

        _meshRenderer.sharedMaterial = _segMaterial;
        Debug.Log("[BGRemoval] MeshRenderer にシェーダーを適用しました");
    }

    private void LateUpdate()
    {
        if (_segMaterial == null) return;

        // sharedMaterial がいつの間にか変わっていないか監視
        if (_meshRenderer != null && _meshRenderer.sharedMaterial != _segMaterial)
        {
            _meshRenderer.sharedMaterial = _segMaterial;
            Debug.LogWarning("[BGRemoval] sharedMaterial が差し替わっていたため再セットしました");
        }

        // 60フレームに1回状態をログ。
        // 背景除去が OFF のときは何も出さない。以前は enableSegmentation を見ずに
        // 出していたため、機能を使っていない間もずっと毎秒1本 Console を流し続け、
        // 他のログ（花火やカメラ切替）が埋もれていた。
        if (!enableSegmentation) return;

        if (Time.frameCount % 60 == 0)
        {
            var mainTex    = _segMaterial.GetTexture("_MainTex");
            var maskTex    = _segMaterial.GetTexture(PropMaskTex);
            var segEnabled = _segMaterial.GetFloat(PropSegEnabled);
            Debug.Log($"[BGRemoval] state: mainTex={mainTex != null} " +
                      $"maskTex={maskTex != null} segEnabled={segEnabled}");
        }
    }

    private void OnValidate()
    {
        if (_segMaterial == null) return;
        _segMaterial.SetFloat(PropThreshold,    threshold);
        _segMaterial.SetFloat(PropEdgeSoftness, edgeSoftness);
    }

    private void OnDestroy()
    {
        // 元のマテリアルを復元
        if (_meshRenderer != null && _originalMaterial != null)
            _meshRenderer.sharedMaterial = _originalMaterial;
        if (_segMaterial != null) Destroy(_segMaterial);
    }
}