using System.Collections;
using System.Diagnostics;
using Unity.Collections;
using UnityEngine;

// ===== SelfieSegmentationController (Sentis / Inference Engine 2.x版) =====
// Unity Inference Engine (旧 Sentis) を使って WebCamTexture から人物マスクを生成する。
//
// Sentis 2.x の変更点:
//   TensorFloat        → Tensor<float>
//   worker.Execute()   → worker.Schedule()
//   MakeReadable()     → DownloadToArray() / DownloadToNativeArray()
//   IWorker            → Worker
//
// セットアップ:
//   1. Package Manager で "Inference Engine (Sentis)" をインストール
//   2. ONNX モデルを Assets/ARHanabi/Models/ に配置
//   3. Inspector の Model Asset にドラッグ&ドロップ
//
// 毎フレームのコスト対策（コードレビュー指摘 4.9）:
//   - 入力用 Texture2D / float[] / 出力マスク用 Color32[] をフィールドで使い回す
//     （以前は毎フレーム 約1.4MB を新規確保 → GC スパイクの原因）
//   - Texture2D.SetPixels32() + Apply() ではなく SetPixelData() で生メモリコピー1回にする
//   - ReadPixels 後の Apply() を廃止（CPU から読むだけなら GPU への再アップロードは不要）
//   - 推論結果の読み戻しは DownloadToArray() ではなく DownloadToNativeArray() を使う。
//     DownloadToArray() は「GPU→CPU 転送」＋「ToArray() によるマネージド配列への再コピー」で
//     同じデータを2回コピーしていた。NativeArray 版なら転送1回で済み、GC アロケーションも消える。
//     （返る NativeArray は Allocator.Temp / 読み戻しリクエストのビューなので Dispose 不要）
//   - inferenceHz で推論頻度を間引き、間引いたフレームは前回のマスクを使い続ける
//
// 詳細ログを見たい場合は ArLog.cs 冒頭の手順で AR_VERBOSE_LOG を定義する。

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

    [Header("パフォーマンス")]
    [Tooltip("推論を実行する頻度（Hz）。0 以下なら毎フレーム")]
    [SerializeField] private float inferenceHz = 20f;

    // ── 内部状態 ──
    private WebCamTexture                _webCamTexture;
    private Unity.InferenceEngine.Worker _worker;
    private Texture2D                    _maskTexture;
    private bool                         _isInitialized;

    // 使い回しバッファ（毎フレームの確保を避ける）
    private RenderTexture _resizeRT;       // WebCamTexture のリサイズ先
    private Texture2D     _inputTexture;   // RT から CPU へ読み戻す先（RGB24）
    private float[]       _inputFloats;    // モデル入力 NHWC (1,H,W,3)
    private Color32[]     _maskPixels;     // マスクテクスチャ書き込み用

    private float _lastInferenceTime = float.NegativeInfinity;

    // ── Public API ──
    public bool IsEnabled => enableSegmentation;

    public void SetEnabled(bool value)
    {
        enableSegmentation = value;

        if (backgroundRemovalEffect != null)
        {
            backgroundRemovalEffect.SetEnabled(value);
            if (!value) backgroundRemovalEffect.ClearMask();
        }

        ArLog.Info($"[Segmentation] {(value ? "ON" : "OFF")}");
    }

    // ── ライフサイクル ──
    private void Start() => StartCoroutine(Initialize());

    private IEnumerator Initialize()
    {
        if (!enableSegmentation) yield break;

        if (modelAsset == null)
        {
            ArLog.Error("[Segmentation] ModelAsset が未設定です。Inspector で .onnx をアサインしてください。");
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

        AllocateBuffers();

        _isInitialized = true;
        ArLog.Info($"[Segmentation] Sentis 初期化完了 (backend={backendType}, " +
                   $"input={modelInputWidth}x{modelInputHeight}, inferenceHz={inferenceHz})");
    }

    private void OnDestroy()
    {
        // Worker / Texture2D / RenderTexture はいずれも GC 対象外なので確実に破棄する
        _worker?.Dispose();
        _worker = null;

        if (_maskTexture  != null) { Destroy(_maskTexture);  _maskTexture  = null; }
        if (_inputTexture != null) { Destroy(_inputTexture); _inputTexture = null; }

        if (_resizeRT != null)
        {
            _resizeRT.Release();
            Destroy(_resizeRT);
            _resizeRT = null;
        }

        _inputFloats = null;
        _maskPixels  = null;
    }

    // ── バッファ確保（初期化時に1回だけ）──
    private void AllocateBuffers()
    {
        int pixelCount = modelInputWidth * modelInputHeight;

        // マスク出力用テクスチャ
        _maskTexture  = new Texture2D(modelInputWidth, modelInputHeight, TextureFormat.RGBA32, false);
        _maskPixels   = new Color32[pixelCount];

        // モデル入力用
        _inputTexture = new Texture2D(modelInputWidth, modelInputHeight, TextureFormat.RGB24, false);
        _inputFloats  = new float[pixelCount * 3];

        _resizeRT = new RenderTexture(modelInputWidth, modelInputHeight, 0, RenderTextureFormat.ARGB32);
        _resizeRT.Create();
    }

    // ── メインループ ──
    private void Update()
    {
        if (!enableSegmentation || !_isInitialized) return;

        // didUpdateThisFrame を使わず毎フレーム最新の WebCamTexture を取得して実行
        // （didUpdateThisFrame が常に False になる問題の回避策）
        var latest = cameraBackgroundController != null
                     ? cameraBackgroundController.GetWebCamTexture()
                     : null;
        if (latest == null || !latest.isPlaying) return;
        _webCamTexture = latest;

        // 推論頻度の間引き。スキップしたフレームは前回のマスクをそのまま使い続ける
        if (inferenceHz > 0f && Time.time - _lastInferenceTime < 1f / inferenceHz) return;
        _lastInferenceTime = Time.time;

        RunSegmentation();
    }

    // ── 推論 ──
    private void RunSegmentation()
    {
        // 1. WebCamTexture → RenderTexture でモデル入力サイズにリサイズ
        Graphics.Blit(_webCamTexture, _resizeRT);

        // 2. RenderTexture → CPU（モデルが NHWC を期待しているため TextureConverter は使わない）
        var prevRT = RenderTexture.active;
        RenderTexture.active = _resizeRT;
        _inputTexture.ReadPixels(new Rect(0, 0, modelInputWidth, modelInputHeight), 0, 0);
        RenderTexture.active = prevRT;
        // ReadPixels は Texture2D の CPU 側バッファへ直接書き込むため、
        // CPU から読むだけなら Apply()（GPU への再アップロード）は不要

        // 3. NHWC (1, H, W, 3) のテンソルを使い回しバッファ上に構築
        //    GetPixelData は生データへのビューなのでコピー・確保が発生しない
        var pixels = _inputTexture.GetPixelData<Color24>(0);
        int pixelCount = modelInputWidth * modelInputHeight;
        for (int i = 0; i < pixelCount; i++)
        {
            var p = pixels[i];
            _inputFloats[i * 3 + 0] = p.r / 255f;
            _inputFloats[i * 3 + 1] = p.g / 255f;
            _inputFloats[i * 3 + 2] = p.b / 255f;
        }

        var shape = new Unity.InferenceEngine.TensorShape(1, modelInputHeight, modelInputWidth, 3);
        using var inputTensor = new Unity.InferenceEngine.Tensor<float>(shape, _inputFloats);

        // 4. 推論実行（Sentis 2.x: worker.Schedule）
        _worker.Schedule(inputTensor);

        // 5. 出力テンソル取得（Sentis 2.x: Tensor<float>）
        var outputTensor = _worker.PeekOutput() as Unity.InferenceEngine.Tensor<float>;
        if (outputTensor == null)
        {
            ArLog.Warn("[Segmentation] 出力テンソルが null です");
            return;
        }

        // 6. GPU → CPU に転送（1回のみ）
        //    出力形状: (1, H, W, 1) = NHWC の行優先フラット配列なので index = y * w + x で正しい
        var data = outputTensor.DownloadToNativeArray();

        ArLog.Verbose($"[Segmentation] Output shape: {outputTensor.shape} dataLen={data.Length}");

        // 7. マスクテクスチャを更新
        UpdateMaskTexture(data, outputTensor.shape[1], outputTensor.shape[2]);
    }

    // ── マスクテクスチャ更新 ──
    private void UpdateMaskTexture(NativeArray<float> data, int h, int w)
    {
        // 出力形状: (1, H, W, 1) = NHWC
        // フラット配列のインデックス: index = y * w + x
        // （最後の次元 =1 なので channel オフセットは不要）
        if (_maskPixels.Length < w * h)
        {
            ArLog.Warn($"[Segmentation] 出力サイズ({w}x{h})がバッファ({modelInputWidth}x{modelInputHeight})を超えています");
            return;
        }

        for (int y = 0; y < h; y++)
        {
            int srcRow = y * w;
            // 上下反転（Texture2D は左下原点、カメラは左上原点）
            int dstRow = (h - 1 - y) * w;

            for (int x = 0; x < w; x++)
            {
                int dataIdx = srcRow + x;
                if (dataIdx >= data.Length) break;

                byte val = (byte)(Mathf.Clamp01(data[dataIdx]) * 255f);
                _maskPixels[dstRow + x] = new Color32(val, 0, 0, 255);
            }
        }

        // SetPixels32() + Apply() の2段コピーではなく、生メモリコピー1回で書き込む
        _maskTexture.SetPixelData(_maskPixels, 0);
        _maskTexture.Apply(false);

        LogMaskStats(w * h);

        if (backgroundRemovalEffect != null) backgroundRemovalEffect.UpdateMask(_maskTexture);
    }

    // ── デバッグ ──
    // AR_VERBOSE_LOG 未定義時は呼び出しごと消えるので、集計ループのコストもゼロになる
    [Conditional("AR_VERBOSE_LOG")]
    private void LogMaskStats(int count)
    {
        byte minVal = 255, maxVal = 0;
        long sum = 0;

        for (int i = 0; i < count; i++)
        {
            byte r = _maskPixels[i].r;
            if (r < minVal) minVal = r;
            if (r > maxVal) maxVal = r;
            sum += r;
        }

        ArLog.Verbose($"[Segmentation] Mask stats: min={minVal / 255f:F3} " +
                      $"max={maxVal / 255f:F3} avg={sum / (float)count / 255f:F3} count={count}");
    }

    // RGB24 の 1 ピクセル（3バイト）を GetPixelData<T>() で読むための構造体。
    // GetPixelData は sizeof(T) がテクスチャの1ピクセルのバイト数と一致する必要があるため、
    // パディングが入らないよう明示的に Sequential + Pack=1 を指定する。
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct Color24
    {
        public byte r;
        public byte g;
        public byte b;
    }
}
