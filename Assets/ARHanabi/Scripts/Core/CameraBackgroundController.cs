using System.Collections;
using UnityEngine;

// ===== CameraBackgroundController =====
// WebCamTexture を生成・所有し、背景 Quad のマテリアル（_MainTex）へ流し込む。
//
// ── このコンポーネントが WebCamTexture の唯一の所有者 ──
//   借用側（PoseLandmarkDetector / SelfieSegmentationController / BackgroundRemovalEffect）は
//   GetWebCamTexture() で参照を借りるだけで、Stop() も Destroy() もしない約束にしている。
//   借用側が Stop() すると背景映像まで巻き込んで止まるため。
//
// ── 実行中のデバイス切替（CycleNextCamera / SwitchToIndex）──
//   展示現場では「どの index がどのカメラか」が現地に行くまで分からない。
//   以前は webcamIndex が Start() の一度きりしか読まれず、間違っていたら
//   Unity に戻って Inspector を直して再生し直すしかなかった。
//   そこで Admin 画面のボタンから総当たりできるようにしている。
//
//   実行中に WebCamTexture を作り直しても安全なのは、借用側が毎フレーム
//   GetWebCamTexture() を呼び直して参照の差し替えに追従する作りになっているから。
//     ・PoseLandmarkDetector … ReferenceEquals で差分を見て入力バッファを再確保する
//     ・SelfieSegmentationController … 固定サイズの RenderTexture へ Blit するので解像度非依存
//     ・BackgroundRemovalEffect … 借用側から取りに来ないので、所有者から明示的に渡す
//
//   選んだ index は SettingsStore（PlayerPrefs）で永続化し、次回起動時にも
//   引き継ぐ。ただし保存した値をそのまま使うと、別のPC・別のカメラ台数構成の
//   環境では範囲外になり得るため、Start() も SwitchToIndex() と同じ「範囲外なら
//   剰余で丸める」経路を必ず通す（詳細は Start() 本体のコメントを参照）。
//
// ── 切替時に古い WebCamTexture を必ず破棄する理由 ──
//   参照を捨てるだけではデバイスが開いたままになり、次のカメラを開けない環境がある。
//   Stop() → Destroy() の順で明示的に解放する。

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

    [Header("デバイス切替")]
    [Tooltip("切替後にカメラが映像を返し始めるのを待つ上限秒数。\n" +
             "超えたら諦めて切替を終了する（待ち続けると IsSwitching が立ちっぱなしになるため）")]
    [SerializeField] private float openTimeoutSeconds = 5f;

    private WebCamTexture _webCamTexture;
    private Renderer      _renderer;

    // 停止検知の状態。ログを毎フレーム出さないための記録
    private bool  _wasPlaying;
    private float _lastRestartAttempt = -999f;

    // 切替中フラグ。Update() の自動再開が切替に割り込まないようにするために見る
    private bool      _switching;
    private Coroutine _openRoutine;

    // WebCamTexture.devices はプロパティ呼び出しごとにデバイス列挙が走るため、
    // Admin 画面が毎フレームラベル同期に使うと無駄になる。切替のタイミングだけ更新する。
    private int _deviceCount;

    // ── 公開API（Admin 画面から使う）──

    /// <summary>検出しているカメラの台数（切替時にだけ更新されるキャッシュ）</summary>
    public int DeviceCount => _deviceCount;

    /// <summary>現在使用しているカメラの index</summary>
    public int CurrentIndex => webcamIndex;

    /// <summary>現在使用しているカメラのデバイス名。未初期化なら "(none)"</summary>
    public string CurrentDeviceName
    {
        get
        {
            if (_webCamTexture != null && !string.IsNullOrEmpty(_webCamTexture.deviceName))
                return _webCamTexture.deviceName;

            var devices = WebCamTexture.devices;
            if (webcamIndex >= 0 && webcamIndex < devices.Length)
                return devices[webcamIndex].name;

            return "(none)";
        }
    }

    /// <summary>切替処理の進行中フラグ。UI 側の多重クリック防止に使う</summary>
    public bool IsSwitching => _switching;

    // PlayerPrefs 経由で保存された index を読む。展示は複数セッション・複数日に
    // またがって電源を落とすため、前回 Admin 画面で選んだカメラを覚えておきたい
    private const string WebcamIndexKey = nameof(CameraBackgroundController) + "." + nameof(webcamIndex);

    private void Start()
    {
        _renderer  = GetComponent<Renderer>();
        webcamIndex = SettingsStore.GetInt(WebcamIndexKey, webcamIndex);

        // 以前はここで BeginOpenDevice(webcamIndex) を直接呼んでいたが、それだと
        // 範囲外の index（Inspector の設定ミス、あるいは別のPC・別のカメラ台数の
        // 環境で復元された古い永続化値）が渡されたとき、OpenDevice 内のガードで
        // 「範囲外です」とエラーを吐くだけで何も開かずに終わっていた
        // （無言でカメラ映像が出ない状態になる）。
        // SwitchToIndex() は範囲外の値を剰余で丸めてから開くので、
        // 初回起動もこの安全な経路に統一する
        SwitchToIndex(webcamIndex);
    }

    // ── デバイス切替 ──

    /// <summary>
    /// 次のカメラへ循環して切り替える。
    /// デバイスが1台以下のときは切り替える先が無いので警告だけ出して何もしない。
    /// </summary>
    public void CycleNextCamera()
    {
        RefreshDeviceCount();

        if (_deviceCount == 0)
        {
            Debug.LogWarning("[CameraBG] カメラが1台も見つかりません。切替できません");
            return;
        }

        if (_deviceCount == 1)
        {
            Debug.LogWarning($"[CameraBG] カメラが1台（'{CurrentDeviceName}'）しかないため切替先がありません");
            return;
        }

        SwitchToIndex(webcamIndex + 1);
    }

    /// <summary>
    /// 指定した index のカメラへ切り替える。
    /// 範囲外の値は剰余で丸めるので、呼び出し側は台数を気にしなくてよい。
    /// </summary>
    public void SwitchToIndex(int index)
    {
        RefreshDeviceCount();

        if (_deviceCount == 0)
        {
            Debug.LogWarning("[CameraBG] カメラが1台も見つかりません。切替できません");
            return;
        }

        if (_switching)
        {
            Debug.LogWarning("[CameraBG] 切替処理が進行中です。完了までお待ちください");
            return;
        }

        BeginOpenDevice(Normalize(index));
    }

    // index を必ず 0..DeviceCount-1 に収める（負値にも耐える）
    private int Normalize(int index)
    {
        int slot = index % _deviceCount;
        if (slot < 0) slot += _deviceCount;
        return slot;
    }

    private void RefreshDeviceCount() => _deviceCount = WebCamTexture.devices.Length;

    // 進行中のコルーチンがあれば止めてから開き直す。
    // _switching は「コルーチン開始前」に立てる。StartCoroutine は最初の yield まで
    // 同期実行されるため、コルーチンの中で立てると呼び出し直後の IsSwitching が
    // まだ false になっているフレームが生まれ、UI 側の多重クリック防止が漏れる。
    private void BeginOpenDevice(int index)
    {
        if (_openRoutine != null) StopCoroutine(_openRoutine);
        _switching   = true;
        _openRoutine = StartCoroutine(OpenDevice(index));
    }

    /// <summary>
    /// デバイスを開いて背景マテリアルと BackgroundRemovalEffect に流し込む。
    /// Start() からの初期化と、実行中の切替の両方がここを通る。
    /// </summary>
    private IEnumerator OpenDevice(int index)
    {
        // finally 相当の後始末。途中で yield break しても _switching を必ず倒すため、
        // 抜け道を作らずこのコルーチンの最後まで通す作りにしている。
        try
        {
            var devices = WebCamTexture.devices;
            _deviceCount = devices.Length;

            if (_deviceCount == 0)
            {
                Debug.LogError("カメラが見つかりません");
                yield break;
            }

            if (index < 0 || index >= _deviceCount)
            {
                Debug.LogError($"webcamIndex ({index}) が範囲外です。" +
                               $"利用可能なカメラ数: {_deviceCount}");
                yield break;
            }

            webcamIndex = index;
            // Start() が丸めた値で開いた場合も含め、実際に確定した index を
            // 保存し直す（自己修復: 次回はこの正常な値から始まる）
            SettingsStore.SetInt(WebcamIndexKey, webcamIndex);
            LogDeviceList();

            // 古いテクスチャを解放する。参照を捨てるだけではデバイスが開いたままになり、
            // 次のカメラを開けない環境がある
            ReleaseWebCamTexture();

            var device = devices[webcamIndex];

            _webCamTexture = new WebCamTexture(device.name, targetWidth, targetHeight, 30);
            _webCamTexture.Play();

            // 映像が来ないデバイスを引くと WaitUntil が永久に待ち、_switching が
            // 立ちっぱなしになって以後の切替が一切できなくなる。必ず上限を設ける
            float waited = 0f;
            while (_webCamTexture.width <= 16 && waited < openTimeoutSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_webCamTexture.width <= 16)
            {
                Debug.LogError($"[CameraBG] '{device.name}' が {openTimeoutSeconds} 秒以内に映像を返しませんでした。" +
                               "別の index を試してください");
                yield break;
            }

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
            if (_renderer != null)
                _renderer.material.SetTexture("_MainTex", _webCamTexture);
            else
                Debug.LogWarning("[CameraBG] Renderer が見つからないため背景に映像を貼れません");

            // BackgroundRemovalEffect にも直接セット（シェーダー差し替え後でも反映されるよう）。
            // 借用側から取りに来ない唯一の相手なので、切替のたびに所有者から渡す必要がある
            var bgEffect = GetComponent<BackgroundRemovalEffect>();
            if (bgEffect != null)
                bgEffect.SetWebCamTexture(_webCamTexture);

            Debug.Log($"[CameraBG] カメラ映像準備完了: index {webcamIndex} '{device.name}' " +
                      $"{_webCamTexture.width}x{_webCamTexture.height} @{_webCamTexture.requestedFPS}fps");
        }
        finally
        {
            // 停止検知の状態をリセットする。切替直後は「前フレームまで別のテクスチャで
            // 再生中だった」履歴が残っており、そのままだと停止したと誤検知して
            // 警告ログと自動再開が走ってしまう
            _wasPlaying         = _webCamTexture != null && _webCamTexture.isPlaying;
            _lastRestartAttempt = Time.time;
            _switching          = false;
            _openRoutine        = null;
        }
    }

    private void ReleaseWebCamTexture()
    {
        if (_webCamTexture == null) return;

        _webCamTexture.Stop();
        Destroy(_webCamTexture);
        _webCamTexture = null;
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
    private void Update()
    {
        // 切替中は古い／作りかけのテクスチャを見てしまうので何もしない。
        // ここで Play() を叩くと切替対象のデバイスと競合する
        if (_switching || _webCamTexture == null) return;

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
        // 生成した本人なのでここで解放するのが正しい
        ReleaseWebCamTexture();
    }
}
