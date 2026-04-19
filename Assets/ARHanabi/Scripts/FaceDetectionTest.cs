using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.FaceDetector;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Core;

public class FaceDetectionTest : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private RawImage cameraView;

    [Header("設定")]
    [SerializeField] private int webcamIndex = 0;
    [SerializeField] private int targetWidth = 640;
    [SerializeField] private int targetHeight = 480;

    private WebCamTexture _webCamTexture;
    private FaceDetector _faceDetector;
    private Texture2D _inputTexture;

    private void Start()
    {
        StartCoroutine(Initialize());
    }

    private IEnumerator Initialize()
    {
        yield return StartCoroutine(StartCamera());
        yield return StartCoroutine(InitializeFaceDetector());
    }

    private IEnumerator StartCamera()
    {
        foreach (var device in WebCamTexture.devices)
        {
            Debug.Log($"カメラ検出: {device.name}");
        }

        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("カメラが見つかりません");
            yield break;
        }

        _webCamTexture = new WebCamTexture(
            WebCamTexture.devices[webcamIndex].name,
            targetWidth,
            targetHeight,
            30
        );

        _webCamTexture.Play();

        yield return new WaitUntil(() => _webCamTexture.width > 16);

        cameraView.texture = _webCamTexture;

        _inputTexture = new Texture2D(
            _webCamTexture.width,
            _webCamTexture.height,
            TextureFormat.RGBA32,
            false
        );

        Debug.Log($"カメラ起動成功: {_webCamTexture.width}x{_webCamTexture.height}");
    }

    private IEnumerator InitializeFaceDetector()
    {
        var modelPath = System.IO.Path.Combine(
        Application.streamingAssetsPath,
        "MediaPipe/face_detection_short_range.bytes"
    );
        if (!System.IO.File.Exists(modelPath))
        {
            Debug.LogError($"モデルファイルが見つかりません: {modelPath}");
            yield break;
        }

        var modelData = System.IO.File.ReadAllBytes(modelPath);
        Debug.Log($"モデル読み込み成功: {modelData.Length} bytes");

        var options = new FaceDetectorOptions(
            new BaseOptions(modelAssetBuffer: modelData),  // ← パスではなくバイト列で渡す
            runningMode: RunningMode.LIVE_STREAM,
            resultCallback: OnFaceDetected
        );

        _faceDetector = FaceDetector.CreateFromOptions(options);
        Debug.Log("FaceDetector初期化完了");
        yield return null;
    }

    private void Update()
    {
        if (_faceDetector == null || _webCamTexture == null) return;
        if (!_webCamTexture.didUpdateThisFrame) return;

        _inputTexture.SetPixels32(_webCamTexture.GetPixels32());
        _inputTexture.Apply();

        var image = new Mediapipe.Image(_inputTexture);
        long timestamp = (long)(Time.realtimeSinceStartup * 1000);
        _faceDetector.DetectAsync(image, timestamp);
    }

    // FaceDetectorOptions.csより: delegate void ResultCallback(DetectionResult, Image, long)
    private void OnFaceDetected(
        DetectionResult result,
        Mediapipe.Image image,
        long timestamp)
    {
        if (result.detections == null || result.detections.Count == 0)
        {
            Debug.Log("顔が検出されませんでした");
            return;
        }

        foreach (var detection in result.detections)
        {
            var score = detection.categories[0].score;
            var bbox = detection.boundingBox;
            Debug.Log($"顔検出: 信頼度={score:F2} " +
                      $"左={bbox.left} 上={bbox.top} " +
                      $"右={bbox.right} 下={bbox.bottom}");
        }
    }

    private void OnDestroy()
    {
        _webCamTexture?.Stop();
        _faceDetector?.Close();
    }
}