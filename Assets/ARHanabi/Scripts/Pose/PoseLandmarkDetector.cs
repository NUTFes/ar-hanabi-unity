using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Core;

public class PoseLandmarkDetector : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private RawImage cameraView;

    [Header("MediaPipe設定")]
    [SerializeField] private int maxPeople = 5;

    [Header("依存コンポーネント")]
    [SerializeField] private GestureDetector gestureDetector;
    [SerializeField] private CameraBackgroundController cameraBackgroundController;
    [SerializeField] private SkeletonRenderer skeletonRenderer;

    private WebCamTexture  _webCamTexture;
    private PoseLandmarker _poseLandmarker;
    private Texture2D      _inputTexture;

    // スレッド間のキュー
    private readonly Queue<PoseLandmarkerResult> _resultQueue = new();
    private readonly object _queueLock = new();

    private void Start() => StartCoroutine(Initialize());

    private IEnumerator Initialize()
    {
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

        _inputTexture = new Texture2D(
            _webCamTexture.width, _webCamTexture.height,
            TextureFormat.RGBA32, false
        );

        Debug.Log($"カメラ映像取得: {_webCamTexture.width}x{_webCamTexture.height}");
    }

    private IEnumerator InitializePoseLandmarker()
    {
        var modelPath = System.IO.Path.Combine(
            Application.streamingAssetsPath,
            "MediaPipe/pose_landmarker_lite.bytes"
        );

        if (!System.IO.File.Exists(modelPath))
        {
            Debug.LogError($"モデルファイルが見つかりません: {modelPath}");
            yield break;
        }

        var modelData = System.IO.File.ReadAllBytes(modelPath);
        Debug.Log($"モデル読み込み成功: {modelData.Length} bytes");

        var options = new PoseLandmarkerOptions(
            new BaseOptions(modelAssetBuffer: modelData),
            runningMode: RunningMode.LIVE_STREAM,
            numPoses: maxPeople,
            resultCallback: OnPoseDetected
        );

        _poseLandmarker = PoseLandmarker.CreateFromOptions(options);
        Debug.Log("PoseLandmarker初期化完了");
        yield return null;
    }

    private void Update()
    {
        if (_poseLandmarker != null && _webCamTexture != null
            && _webCamTexture.didUpdateThisFrame)
        {
            _inputTexture.SetPixels32(_webCamTexture.GetPixels32());
            _inputTexture.Apply();

            using (var image = new Mediapipe.Image(_inputTexture))
            {
                long timestamp = (long)(Time.realtimeSinceStartup * 1000);
                _poseLandmarker.DetectAsync(image, timestamp);
            }
        }

        ProcessResultQueue();
    }

    private void ProcessResultQueue()
    {
        PoseLandmarkerResult result = default;
        bool hasResult = false;

        lock (_queueLock)
        {
            if (_resultQueue.Count > 0)
            {
                result = _resultQueue.Dequeue();
                hasResult = true;
            }
        }

        if (!hasResult || result.poseLandmarks == null) return;

        for (int i = 0; i < result.poseLandmarks.Count; i++)
        {
            var landmarks = result.poseLandmarks[i].landmarks;
            gestureDetector.ProcessLandmarks(i, landmarks);
            skeletonRenderer.UpdateSkeleton(i, landmarks);
        }
    }

    private void OnPoseDetected(
        PoseLandmarkerResult result,
        Mediapipe.Image image,
        long timestamp)
    {
        lock (_queueLock)
        {
            _resultQueue.Enqueue(result);
        }
    }

    private void OnDestroy()
    {
        _webCamTexture?.Stop();
        _poseLandmarker?.Close();
    }
}