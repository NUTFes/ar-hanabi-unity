using System.Collections;
using UnityEngine;

public class CameraBackgroundController : MonoBehaviour
{
    [SerializeField] private int webcamIndex  = 0;
    [SerializeField] private int targetWidth  = 640;
    [SerializeField] private int targetHeight = 480;

    [Header("停止時の復帰")]
    [Tooltip("カメラの配信が止まった（isPlaying が false になった）ときに自動で再開を試みる。\n" +
             "展示中に止まったまま放置されるのを防ぐための保険")]
    [SerializeField] private bool  autoRestartOnStall  = true;

    [Tooltip("再開を試みる間隔（秒）。連続で Play() を叩かないための下限")]
    [SerializeField] private float restartRetryInterval = 1.0f;

    private WebCamTexture _webCamTexture;
    private Renderer      _renderer;

    // 停止検知の状態。ログを毎フレーム出さないための記録
    private bool  _wasPlaying;
    private float _lastRestartAttempt = -999f;

    private void Start()
    {
        _renderer = GetComponent<Renderer>();
        StartCoroutine(InitializeCamera());
    }

    private IEnumerator InitializeCamera()
    {
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("カメラが見つかりません");
            yield break;
        }

        if (webcamIndex >= WebCamTexture.devices.Length)
        {
            Debug.LogError($"webcamIndex ({webcamIndex}) が範囲外です。" +
                           $"利用可能なカメラ数: {WebCamTexture.devices.Length}");
            yield break;
        }

        LogDeviceList();

        var device = WebCamTexture.devices[webcamIndex];

        _webCamTexture = new WebCamTexture(device.name, targetWidth, targetHeight, 30);
        _webCamTexture.Play();

        yield return new WaitUntil(() => _webCamTexture.width > 16);

        // 要求解像度が通ったかを必ず残す。
        // 要求と実際が食い違う場合、デバイスが対応していないモードを要求しており
        // ドライバ側で近いモードに丸められている。1080p の無圧縮ストリームは
        // USB の帯域を使い切りやすく、配信が途中で落ちる（LEDが消える）原因になる。
        if (_webCamTexture.width != targetWidth || _webCamTexture.height != targetHeight)
        {
            Debug.LogWarning($"[CameraBG] 要求 {targetWidth}x{targetHeight} に対して " +
                             $"実際は {_webCamTexture.width}x{_webCamTexture.height} で開始しました。" +
                             "デバイスが要求モードに対応していない可能性があります");
        }

        // マテリアルの _MainTex に WebCamTexture をセット
        _renderer.material.SetTexture("_MainTex", _webCamTexture);

        // BackgroundRemovalEffect にも直接セット（シェーダー差し替え後でも反映されるよう）
        var bgEffect = GetComponent<BackgroundRemovalEffect>();
        if (bgEffect != null)
            bgEffect.SetWebCamTexture(_webCamTexture);

        Debug.Log($"[CameraBG] カメラ映像準備完了: '{device.name}' " +
                  $"{_webCamTexture.width}x{_webCamTexture.height} @{_webCamTexture.requestedFPS}fps");
    }

    // ── デバイス一覧の出力 ──
    // webcamIndex がどのデバイスを指しているかと、対応解像度を残す。
    // availableResolutions は環境によって空を返すことがある（その場合は不明と出す）。
    private void LogDeviceList()
    {
        var devices = WebCamTexture.devices;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[CameraBG] 検出したカメラ {devices.Length} 台（使用するのは index {webcamIndex}）");

        for (int i = 0; i < devices.Length; i++)
        {
            string mark = (i == webcamIndex) ? " ← 使用" : "";
            sb.AppendLine($"  [{i}] {devices[i].name}{mark}");

            var resolutions = devices[i].availableResolutions;
            if (resolutions == null || resolutions.Length == 0)
            {
                sb.AppendLine("        対応解像度: 取得できません（この環境では非対応）");
                continue;
            }

            sb.Append("        対応解像度: ");
            for (int r = 0; r < resolutions.Length; r++)
            {
                if (r > 0) sb.Append(", ");
                sb.Append($"{resolutions[r].width}x{resolutions[r].height}" +
                          $"@{resolutions[r].refreshRateRatio.value:F0}");
            }
            sb.AppendLine();
        }

        Debug.Log(sb.ToString());
    }

    public WebCamTexture GetWebCamTexture() => _webCamTexture;

    // ── 停止の検知と復帰 ──
    // 「時々カメラが停止する」対策。isPlaying が落ちたことを検知して警告を出し、
    // 必要なら Play() で再開を試みる。
    //
    // 注意: このコンポーネントが WebCamTexture の唯一の所有者。
    // 借用側（PoseLandmarkDetector / SelfieSegmentationController）が Stop() すると
    // 背景映像まで巻き込んで止まるため、借用側では Stop() しない約束にしている。
    private void Update()
    {
        if (_webCamTexture == null) return;

        bool playing = _webCamTexture.isPlaying;

        if (_wasPlaying && !playing)
        {
            Debug.LogWarning("[CameraBG] カメラの配信が停止しました（isPlaying = false）。" +
                             $"要求解像度 {targetWidth}x{targetHeight} がデバイスの能力を" +
                             "超えている場合（USB帯域不足など）にも起こります");
        }
        else if (!_wasPlaying && playing)
        {
            Debug.Log($"[CameraBG] カメラの配信が再開しました: " +
                      $"{_webCamTexture.width}x{_webCamTexture.height}");
        }
        _wasPlaying = playing;

        if (playing || !autoRestartOnStall) return;

        if (Time.time - _lastRestartAttempt < restartRetryInterval) return;
        _lastRestartAttempt = Time.time;

        Debug.LogWarning("[CameraBG] カメラの再開を試みます");
        _webCamTexture.Play();
    }

    private void OnDestroy()
    {
        // 生成した本人なのでここで止めるのが正しい
        _webCamTexture?.Stop();
    }
}