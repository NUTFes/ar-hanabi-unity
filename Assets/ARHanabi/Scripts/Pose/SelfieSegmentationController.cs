using System.Collections;
using UnityEngine;


// ===== SelfieSegmentationController (Sentis 2.x版) =====
// Unity Sentis を使って WebCamTexture から人物マスクを生成する。
//
// Sentis 2.x の変更点:
//   TensorFloat        → Tensor<float>
//   worker.Execute()   → worker.Schedule()
//   MakeReadable()     → DownloadToArray()
//   IWorker            → Worker
//
// セットアップ:
//   1. Package Manager で "Sentis" をインストール
//   2. ONNX モデルを Assets/ARHanabi/Models/ に配置
//   3. Inspector の Model Asset にドラッグ&ドロップ

public class SelfieSegmentationController : MonoBehaviour
{
    [Header("ON/OFF")]
    [Tooltip("OFF にするとセグメンテーション処理をスキップする")]
    [SerializeField] private bool enableSegmentation = true;

    [Header("依存コンポーネント")]
    [SerializeField] private CameraBackgroundController cameraBackgroundController;
    [SerializeField] private BackgroundRemovalEffect    backgroundRemovalEffect;

    [Header("Sentis モデル設定")]
    [Tooltip("Assets/ARHanabi/Models/ に配置した .onnx をドラッグ&ドロップ")]
    [SerializeField] private Unity.InferenceEngine.ModelAsset modelAsset;

    [Tooltip("GPU推論（高速）か CPU推論（互換性高い）か")]
    [SerializeField] private Unity.InferenceEngine.BackendType backendType = Unity.InferenceEngine.BackendType.GPUCompute;

    [Tooltip("モデルの入力解像度（SelfieBarracuda の landscape モデルは 256）")]
    [SerializeField] private int modelInputWidth  = 256;
    [SerializeField] private int modelInputHeight = 144;

    // ── 内部状態 ──
    private WebCamTexture _webCamTexture;
    private Unity.InferenceEngine.Worker        _worker;
    private Texture2D     _maskTexture;
    private bool          _isInitialized;

    // ── Public API ──
    public bool IsEnabled => enableSegmentation;

    public void SetEnabled(bool value)
    {
        enableSegmentation = value;
        backgroundRemovalEffect?.SetEnabled(value);
        if (!value) backgroundRemovalEffect?.ClearMask();
        Debug.Log($"[Segmentation] {(value ? "ON" : "OFF")}");
    }

    // ── ライフサイクル ──
    private void Start() => StartCoroutine(Initialize());

    private IEnumerator Initialize()
    {
        if (!enableSegmentation) yield break;

        if (modelAsset == null)
        {
            Debug.LogError("[Segmentation] ModelAsset が未設定です。Inspector で .onnx をアサインしてください。");
            yield break;
        }

        // カメラ準備待ち
        yield return new WaitUntil(() =>
            cameraBackgroundController != null &&
            cameraBackgroundController.GetWebCamTexture() != null &&
            cameraBackgroundController.GetWebCamTexture().width > 16
        );

        _webCamTexture = cameraBackgroundController.GetWebCamTexture();

        // Sentis Worker 初期化（Sentis 2.x: new Worker()）
        var model = Unity.InferenceEngine.ModelLoader.Load(modelAsset);
        _worker   = new Unity.InferenceEngine.Worker(model, backendType);

        // マスク出力用テクスチャ
        _maskTexture = new Texture2D(modelInputWidth, modelInputHeight, TextureFormat.RGBA32, false);

        _isInitialized = true;
        Debug.Log($"[Segmentation] Sentis 初期化完了 (backend={backendType}, " +
                  $"input={modelInputWidth}x{modelInputHeight})");
    }

    private int _debugFrameCount = 0;

    private void Update()
    {
        _debugFrameCount++;

        // 60フレームごとに状態をログ出力
        if (_debugFrameCount % 60 == 0)
        {
            Debug.Log($"[Segmentation] Update check: " +
                      $"enabled={enableSegmentation} " +
                      $"initialized={_isInitialized} " +
                      $"webCam={(_webCamTexture != null ? "OK" : "NULL")} " +
                      $"didUpdate={_webCamTexture?.didUpdateThisFrame} " +
                      $"bgEffect={backgroundRemovalEffect != null}");
        }

        if (!enableSegmentation || !_isInitialized) return;
        if (_webCamTexture == null) return;

        // didUpdateThisFrame を使わず毎フレーム最新の WebCamTexture を取得して実行
        // （didUpdateThisFrame が常に False になる問題の回避策）
        var latest = cameraBackgroundController.GetWebCamTexture();
        if (latest == null || !latest.isPlaying) return;
        _webCamTexture = latest;

        RunSegmentation();
    }

    // ── 推論 ──
    private void RunSegmentation()
    {
        // 1. WebCamTexture → RenderTexture でモデル入力サイズにリサイズ
        var rt = RenderTexture.GetTemporary(modelInputWidth, modelInputHeight, 0);
        Graphics.Blit(_webCamTexture, rt);

        // 2. RenderTexture → Tensor<float>（NHWC形式: 1, H, W, 3）
        // モデルが NHWC を期待しているため TextureConverter ではなく手動で作成する
        var tex = new Texture2D(modelInputWidth, modelInputHeight, TextureFormat.RGB24, false);
        var prevRT = RenderTexture.active;
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, modelInputWidth, modelInputHeight), 0, 0);
        tex.Apply();
        RenderTexture.active = prevRT;
        RenderTexture.ReleaseTemporary(rt);

        // NHWC (1, H, W, 3) のテンソルを手動作成
        var pixels32 = tex.GetPixels32();
        Destroy(tex);
        int size = modelInputHeight * modelInputWidth * 3;
        var floatData = new float[size];
        for (int i = 0; i < modelInputHeight * modelInputWidth; i++)
        {
            floatData[i * 3 + 0] = pixels32[i].r / 255f;
            floatData[i * 3 + 1] = pixels32[i].g / 255f;
            floatData[i * 3 + 2] = pixels32[i].b / 255f;
        }
        var shape = new Unity.InferenceEngine.TensorShape(1, modelInputHeight, modelInputWidth, 3);
        using var inputTensor = new Unity.InferenceEngine.Tensor<float>(shape, floatData);

        // 3. 推論実行（Sentis 2.x: worker.Schedule）
        _worker.Schedule(inputTensor);

        // 4. 出力テンソル取得（Sentis 2.x: Tensor<float>）
        var outputTensor = _worker.PeekOutput() as Unity.InferenceEngine.Tensor<float>;
        if (outputTensor == null)
        {
            Debug.LogWarning("[Segmentation] 出力テンソルが null です");
            return;
        }

        // 5. GPU → CPU に転送（Sentis 2.x: DownloadToArray）
        // 出力形状: (1, H, W, 1) = NHWC
        // DownloadToArray は行優先フラット配列なので index = y * w + x で正しい
        var data = outputTensor.DownloadToArray();
        Debug.Log($"[Segmentation] Output shape: {outputTensor.shape} dataLen={data.Length}");

        // 6. マスクテクスチャを更新
        UpdateMaskTexture(data, outputTensor.shape[1], outputTensor.shape[2]);
    }

    // ── マスクテクスチャ更新 ──
    private void UpdateMaskTexture(float[] data, int h, int w)
    {
        // 出力形状: (1, H, W, 1) = NHWC
        // フラット配列のインデックス: index = y * w + x
        // （最後の次元 =1 なので channel オフセットは不要）
        var pixels = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int dataIdx = y * w + x;
                if (dataIdx >= data.Length) break;

                float val = Mathf.Clamp01(data[dataIdx]);
                // 上下反転（Texture2D は左下原点、カメラは左上原点）
                pixels[(h - 1 - y) * w + x] = new Color(val, 0, 0, 1);
            }
        }

        _maskTexture.SetPixels(pixels);
        _maskTexture.Apply();

        // デバッグ: マスクの値の範囲を確認（最初の3回だけ）
        if (_debugFrameCount <= 180)
        {
            float minVal = float.MaxValue, maxVal = float.MinValue, sum = 0;
            foreach (var p in pixels) { minVal = Mathf.Min(minVal, p.r); maxVal = Mathf.Max(maxVal, p.r); sum += p.r; }
            Debug.Log($"[Segmentation] Mask stats: min={minVal:F3} max={maxVal:F3} avg={sum/pixels.Length:F3} count={pixels.Length}");
        }

        backgroundRemovalEffect?.UpdateMask(_maskTexture);
    }

    private static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));

    private void OnDestroy()
    {
        _worker?.Dispose();
        if (_maskTexture != null) Destroy(_maskTexture);
    }
}