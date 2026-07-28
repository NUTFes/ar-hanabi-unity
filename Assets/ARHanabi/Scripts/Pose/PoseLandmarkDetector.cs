using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Core;

// ===== PoseLandmarkDetector =====
// WebCamTexture の映像を MediaPipe PoseLandmarker に流し込み、
// 検出結果を GestureDetector / SkeletonRenderer に配る。
//
// 毎フレームのアロケーション対策:
//   - WebCamTexture.GetPixels32() は毎回 約1.2MB(640x480) の Color32[] を新規確保するので、
//     配列を事前確保して GetPixels32(buffer) のオーバーロードで受け取る。
//   - Texture2D への書き込みは SetPixels32() ではなく SetPixelData() を使い、
//     変換なしの生メモリコピー1回に減らす。
//
// 推論結果の受け渡し:
//   PoseLandmarker は別スレッドからコールバックしてくるため、
//   以前は Queue に積んでいたが、描画が追いつかないとキューが無制限に伸びる。
//   ポーズ描画・ジェスチャー判定はどちらも「最新の姿勢」だけが意味を持つので、
//   キューではなく「最新の1件のみ保持」に変更している（古い結果は捨てる）。
//
// 詳細ログを見たい場合は ArLog.cs 冒頭の手順で AR_VERBOSE_LOG を定義する。

public class PoseLandmarkDetector : MonoBehaviour
{
    // cameraView（デバッグ用 RawImage）は削除した。
    // どこからも読まれておらず、MainScene でも未アサイン（fileID: 0）だったため、
    // Inspector に枠だけ残っていても何も起きない死んだフィールドだった。
    // 生映像を UI に出したい場合は FaceDetectionTest.cs の実装を参考にすること。

    [Header("MediaPipe設定")]
    [Tooltip("同時に検出する人数の上限")]
    [SerializeField] private int maxPeople = 5;

    [Header("依存コンポーネント")]
    [Tooltip("ジェスチャー判定を行うコンポーネント")]
    [SerializeField] private GestureDetector gestureDetector;

    [Tooltip("WebCamTexture の供給元")]
    [SerializeField] private CameraBackgroundController cameraBackgroundController;

    [Tooltip("スケルトン描画を行うコンポーネント")]
    [SerializeField] private SkeletonRenderer skeletonRenderer;

    // ── 内部状態 ──
    private WebCamTexture  _webCamTexture;
    private PoseLandmarker _poseLandmarker;
    private Texture2D      _inputTexture;

    // 毎フレームの確保を避けるための使い回しバッファ
    private Color32[] _pixelBuffer;

    // スレッド間の受け渡し（最新の1件のみ保持）
    private PoseLandmarkerResult _latestResult;
    private bool  _hasNewResult;
    private readonly object _resultLock = new();

    // ── ライフサイクル ──
    private void Start() => StartCoroutine(Initialize());

    private IEnumerator Initialize()
    {
        if (cameraBackgroundController == null)
        {
            ArLog.Error("[Pose] cameraBackgroundController が未設定です");
            yield break;
        }

        yield return StartCoroutine(StartCamera());
        yield return StartCoroutine(InitializePoseLandmarker());
    }

    private IEnumerator StartCamera()
    {
        yield return new WaitUntil(() =>
            cameraBackgroundController.GetWebCamTexture() != null &&
            cameraBackgroundController.GetWebCamTexture().width > 16
        );

        _webCamTexture = cameraBackgroundController.GetWebCamTexture();
        AllocateBuffers(_webCamTexture.width, _webCamTexture.height);

        ArLog.Info($"[Pose] カメラ映像取得: {_webCamTexture.width}x{_webCamTexture.height}");
    }

    private IEnumerator InitializePoseLandmarker()
    {
        var modelPath = System.IO.Path.Combine(
            Application.streamingAssetsPath,
            "MediaPipe/pose_landmarker_lite.bytes"
        );

        if (!System.IO.File.Exists(modelPath))
        {
            ArLog.Error($"[Pose] モデルファイルが見つかりません: {modelPath}");
            yield break;
        }

        var modelData = System.IO.File.ReadAllBytes(modelPath);
        ArLog.Info($"[Pose] モデル読み込み成功: {modelData.Length} bytes");

        var options = new PoseLandmarkerOptions(
            new BaseOptions(modelAssetBuffer: modelData),
            runningMode: RunningMode.LIVE_STREAM,
            numPoses: maxPeople,
            resultCallback: OnPoseDetected
        );

        _poseLandmarker = PoseLandmarker.CreateFromOptions(options);
        ArLog.Info("[Pose] PoseLandmarker初期化完了");
        yield return null;
    }

    private void OnDestroy()
    {
        _webCamTexture?.Stop();
        _poseLandmarker?.Close();

        // Texture2D は GC 対象外のネイティブリソースなので明示的に破棄する
        if (_inputTexture != null)
        {
            Destroy(_inputTexture);
            _inputTexture = null;
        }
        _pixelBuffer = null;
    }

    // ── バッファ確保 ──
    // カメラ解像度が変わった場合も含めて、必要なときだけ確保し直す
    private void AllocateBuffers(int width, int height)
    {
        if (_inputTexture != null && _inputTexture.width == width && _inputTexture.height == height)
            return;

        if (_inputTexture != null) Destroy(_inputTexture);

        _inputTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        _pixelBuffer  = new Color32[width * height];

        ArLog.Info($"[Pose] 入力バッファ確保: {width}x{height} ({_pixelBuffer.Length * 4 / 1024} KB)");
    }

    // ── メインループ ──
    private void Update()
    {
        if (_poseLandmarker != null && _webCamTexture != null && _webCamTexture.didUpdateThisFrame)
        {
            DetectFromCamera();
        }

        ProcessLatestResult();
    }

    private void DetectFromCamera()
    {
        // 解像度が途中で変わることがあるため念のため確認
        AllocateBuffers(_webCamTexture.width, _webCamTexture.height);

        // 事前確保した配列に直接受け取る（戻り値の新規配列確保を避ける）
        _webCamTexture.GetPixels32(_pixelBuffer);

        // Color32 と RGBA32 はメモリレイアウトが一致するので、変換なしの生コピーで済む
        _inputTexture.SetPixelData(_pixelBuffer, 0);
        _inputTexture.Apply(false);

        using (var image = new Mediapipe.Image(_inputTexture))
        {
            long timestamp = (long)(Time.realtimeSinceStartup * 1000);
            _poseLandmarker.DetectAsync(image, timestamp);
        }
    }

    // ── 推論結果の反映 ──
    private void ProcessLatestResult()
    {
        PoseLandmarkerResult result;

        lock (_resultLock)
        {
            if (!_hasNewResult) return;
            result        = _latestResult;
            _hasNewResult = false;
        }

        if (result.poseLandmarks == null) return;

        ArLog.Verbose($"[Pose] 検出人数: {result.poseLandmarks.Count}");

        for (int i = 0; i < result.poseLandmarks.Count; i++)
        {
            var landmarks = result.poseLandmarks[i].landmarks;

            if (gestureDetector  != null) gestureDetector.ProcessLandmarks(i, landmarks);
            if (skeletonRenderer != null) skeletonRenderer.UpdateSkeleton(i, landmarks);
        }
    }

    // 別スレッドから呼ばれる。最新の結果だけを保持し、未処理の古い結果は捨てる
    private void OnPoseDetected(
        PoseLandmarkerResult result,
        Mediapipe.Image image,
        long timestamp)
    {
        lock (_resultLock)
        {
            _latestResult = result;
            _hasNewResult = true;
        }
    }
}
