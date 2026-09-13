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

    [Tooltip("要求する解像度。0 にするとドライバ任せ（WebCamTexture に解像度を指定しない）。\n" +
             "\n" +
             "── 要求モードが原因でプロセスが即死することがある ──\n" +
             "USB2.0 接続のカメラで 1280x720@30fps を無圧縮（YUY2）で要求すると\n" +
             "約 55MB/s になり、USB2.0 の実効帯域（35〜40MB/s）を超える。\n" +
             "ドライバが対応していないモードを要求した場合、開いた瞬間に\n" +
             "Unity のプロセスが即死することが実際にあった（ログに \"Shut down.\" だけ残る）。\n" +
             "落ちる場合は 640x480 → 0（ドライバ任せ）の順に下げて試すこと")]
    [SerializeField] private int targetWidth  = 640;
    [SerializeField] private int targetHeight = 480;

    [Tooltip("要求するフレームレート。0 にするとドライバ任せ。\n" +
             "解像度を下げたくない場合は、ここを 15 にして帯域を半分にする手もある")]
    [SerializeField] private int targetFps = 30;

    [Tooltip("この文字列を名前に含むカメラは一覧・切替の対象から除外する（部分一致・大小無視）。\n" +
             "実カメラの台数だけで index を数えたいので、使わないデバイスはここへ入れる。\n" +
             "OBS Virtual Camera は配信を ON にしていないと映像を返さないので既定で除外している")]
    [SerializeField]
    private string[] excludedDeviceNamePatterns = { "OBS Virtual Camera" };

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

            var devices = GetUsableDevices();
            if (webcamIndex >= 0 && webcamIndex < devices.Length)
                return devices[webcamIndex].name;

            return "(none)";
        }
    }

    /// <summary>切替処理の進行中フラグ。UI 側の多重クリック防止に使う</summary>
    public bool IsSwitching => _switching;

    // ── 左右反転（鏡像）──
    //
    // ── なぜ所有者がまとめて持つのか ──
    //   反転は2か所を同時に裏返して初めて成立する。
    //     1. 表示   … BackgroundRemoval.shader（_FlipX）
    //     2. 関節座標 … PoseCoordinateUtil.MirrorX
    //   片方だけだと「映像は鏡像なのに、骨格と花火は元の向き」になり、
    //   手を上げた側と反対側から花火が上がる。
    //   別々のスイッチにすると必ず片方だけ切り替わる事故が起きるので、
    //   カメラ映像の所有者であるここが唯一の入口になる。
    //
    // ── 設置してみるまで正解が分からない ──
    //   来場者が自分を見る展示なので普通は鏡像が自然だが、
    //   カメラを人に向けるか天井から回すか、ハーフミラー越しかで変わる。
    //   会場で試して決められるよう Admin 画面から切り替え、
    //   選択は webcamIndex と同じく次回起動へ引き継ぐ
    private const string MirrorKey = nameof(CameraBackgroundController) + "." + nameof(mirrorHorizontal);

    [Header("映像の向き")]
    [Tooltip("カメラ映像を左右反転して鏡像にする。\n" +
             "表示と関節座標の両方を同時に裏返すので、骨格や花火の位置はズレない")]
    [SerializeField] private bool mirrorHorizontal = false;

    /// <summary>カメラ映像を左右反転（鏡像）するか。表示と関節座標の両方に効く</summary>
    public bool MirrorHorizontal
    {
        get => mirrorHorizontal;
        set
        {
            mirrorHorizontal = value;
            SettingsStore.SetBool(MirrorKey, value);
            ApplyMirror();
            Debug.Log($"[CameraBG] 左右反転: {(value ? "ON（鏡像）" : "OFF")}");
        }
    }

    public void ToggleMirror() => MirrorHorizontal = !mirrorHorizontal;

    // 表示側と関節座標側へ同時に流し込む。
    // 起動時・切替時・カメラを開き直した直後のいずれからも呼ぶ
    private void ApplyMirror()
    {
        PoseCoordinateUtil.MirrorX = mirrorHorizontal;

        var bgEffect = GetComponent<BackgroundRemovalEffect>();
        if (bgEffect != null) bgEffect.SetMirrorX(mirrorHorizontal);
    }

    // PlayerPrefs 経由で保存された index を読む。展示は複数セッション・複数日に
    // またがって電源を落とすため、前回 Admin 画面で選んだカメラを覚えておきたい
    private const string WebcamIndexKey = nameof(CameraBackgroundController) + "." + nameof(webcamIndex);

    // 「今カメラを開こうとしている最中」を表す永続フラグ。
    //
    // ── なぜ必要か（実際に起きた事故）──
    //   デバイスによっては WebCamTexture を開いた瞬間にドライバ側でプロセスが即死する
    //   （Unity のクラッシュハンドラも動かず、ログには "Shut down." だけが残る）。
    //   C# の例外でもタイムアウトでもないので、コード側では一切受け止められない。
    //   このとき「開こうとした index」が既に保存済みだと、次の起動でも同じデバイスを
    //   開こうとして再び即死する ＝ 起動できない無限ループに陥る（実際に陥った）。
    //
    //   そこで「開く直前にフラグを立て、映像が来たことを確認してから倒す」ようにし、
    //   起動時にフラグが立ったままなら「前回はそのindexで落ちた」と判断して
    //   別のデバイスから試す。1回のクラッシュはもう避けられないが、
    //   ループにはならない（＝人の手で PlayerPrefs を消す必要がなくなる）
    private const string OpenInProgressKey = nameof(CameraBackgroundController) + ".openInProgress";

    private void Start()
    {
        _renderer  = GetComponent<Renderer>();
        webcamIndex = SettingsStore.GetInt(WebcamIndexKey, webcamIndex);

        // 前回の選択を復元して、表示側・関節座標側の両方へ入れておく。
        // カメラを開く前に済ませるのは、BackgroundRemovalEffect が
        // マテリアルを作るときに現在値を拾えるようにするため
        mirrorHorizontal = SettingsStore.GetBool(MirrorKey, mirrorHorizontal);
        ApplyMirror();

        // 前回の起動がカメラを開いている途中で終わっている（＝そのデバイスで落ちた）なら、
        // 同じ index を避けて次のデバイスから試す
        if (SettingsStore.GetBool(OpenInProgressKey, false))
        {
            int crashed = webcamIndex;
            webcamIndex = crashed + 1;
            SettingsStore.SetBool(OpenInProgressKey, false);
            Debug.LogWarning($"[CameraBG] 前回の起動は index {crashed} のカメラを開いている途中で終了しました。" +
                             $"そのデバイスは避けて index {webcamIndex} から試します" +
                             "（同じデバイスで再発するなら excludedDeviceNamePatterns に名前を追加してください）");
        }

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

    private void RefreshDeviceCount() => _deviceCount = GetUsableDevices().Length;

    // WebCamTexture.devices から除外パターンに一致するデバイスを取り除く。
    // 呼び出しごとに配列を作り直す（WebCamTexture.devices 自体もプロパティ呼び出しごとに
    // デバイス列挙が走るため、フィルタの有無でコストの桁は変わらない）
    private WebCamDevice[] GetUsableDevices()
    {
        var all = WebCamTexture.devices;
        if (excludedDeviceNamePatterns == null || excludedDeviceNamePatterns.Length == 0)
            return all;

        var list = new System.Collections.Generic.List<WebCamDevice>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            if (!IsExcluded(all[i].name))
                list.Add(all[i]);
        }
        return list.ToArray();
    }

    private bool IsExcluded(string deviceName)
    {
        for (int i = 0; i < excludedDeviceNamePatterns.Length; i++)
        {
            var pattern = excludedDeviceNamePatterns[i];
            if (!string.IsNullOrEmpty(pattern) &&
                deviceName.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

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
            var devices = GetUsableDevices();
            _deviceCount = devices.Length;

            if (_deviceCount == 0)
            {
                Debug.LogError("カメラが見つかりません（除外リストに一致するものしか無い場合も含む）");
                yield break;
            }

            if (index < 0 || index >= _deviceCount)
            {
                Debug.LogError($"webcamIndex ({index}) が範囲外です。" +
                               $"利用可能なカメラ数: {_deviceCount}");
                yield break;
            }

            webcamIndex = index;
            LogDeviceList();

            // 古いテクスチャを解放する。参照を捨てるだけではデバイスが開いたままになり、
            // 次のカメラを開けない環境がある
            ReleaseWebCamTexture();

            var device = devices[webcamIndex];

            // ここから先はプロセスが即死しうる区間（ドライバ次第）。
            // 「開いている最中」を永続フラグで残しておき、映像が来たことを
            // 確認できてから倒す（OpenInProgressKey のコメント参照）
            SettingsStore.SetBool(OpenInProgressKey, true);

            // 解像度・fps を 0 にしてある場合は指定せずに開く（ドライバが対応するモードを選ぶ）。
            // 対応していないモードを要求すると開いた瞬間に即死するデバイスがあるため、
            // 「まず開けること」を優先したいときの逃げ道
            bool specifyMode = targetWidth > 0 && targetHeight > 0;

            _webCamTexture = specifyMode
                             ? (targetFps > 0
                                ? new WebCamTexture(device.name, targetWidth, targetHeight, targetFps)
                                : new WebCamTexture(device.name, targetWidth, targetHeight))
                             : new WebCamTexture(device.name);

            Debug.Log($"[CameraBG] '{device.name}' を開きます" +
                      (specifyMode
                       ? $"（要求 {targetWidth}x{targetHeight}" + (targetFps > 0 ? $"@{targetFps}fps）" : "）")
                       : "（解像度・fpsはドライバ任せ）"));

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
                // 映像が来なかった index は保存しない（次の起動でまた同じ外れを引かせない）
                SettingsStore.SetBool(OpenInProgressKey, false);
                Debug.LogError($"[CameraBG] '{device.name}' が {openTimeoutSeconds} 秒以内に映像を返しませんでした。" +
                               "デバイスは閉じました。別の index を試してください");
                yield break;
            }

            // ここまで来たら「本当に映像が来ている」と確認できたので、初めて index を保存する。
            //

                // ── 諦めるときは必ずデバイスを閉じる ──
                //   ここで Stop() せずに抜けていたため、映像を返さないデバイスを掴んだまま
                //   放置され、画面には何も映らないのにカメラのLEDだけ点きっぱなしになっていた。
                //   さらに Update() の自動再開は isPlaying が true（映像は来ないが再生中）なので
                //   介入せず、次に切り替えるまで永久にデバイスを占有し続けていた
                ReleaseWebCamTexture();

            // ── 以前は開く前に保存していた（事故の原因）──
            //   開いた瞬間にプロセスが即死するデバイスがあると、死ぬ前に保存された index が
            //   次の起動でも使われ、起動するたびに即死する無限ループになっていた。
            //   保存を「成功の確認後」に限定すると、落ちても前回の正常な index が残る。
            //   Start() が丸めた値で開いた場合の自己修復（次回はこの正常な値から始まる）も
            //   この位置で同じように効く
            SettingsStore.SetInt(WebcamIndexKey, webcamIndex);
            SettingsStore.SetBool(OpenInProgressKey, false);

            // 要求解像度が通ったかを必ず残す。
            // 要求と実際が食い違う場合、デバイスが対応していないモードを要求しており
            // ドライバ側で近いモードに丸められている。1080p の無圧縮ストリームは
            // USB の帯域を使い切りやすく、配信が途中で落ちる（LEDが消える）原因になる。
            if (specifyMode &&
                (_webCamTexture.width != targetWidth || _webCamTexture.height != targetHeight))
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
        var devices = GetUsableDevices();
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
