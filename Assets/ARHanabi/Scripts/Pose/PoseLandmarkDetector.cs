using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
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
// 人物の同一性:
//   配り先に渡す ID は検出リストの添字ではなく PoseTracker が発行する trackId を使う。
//   添字はリスト順にすぎず、人が入れ替わると色分けやジェスチャーの保持状態が
//   別人に飛び移る。理由と対策の詳細は PoseTracker.cs 冒頭のコメントを参照。
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

    [Tooltip("人として検出するのに必要な確信度（0〜1、MediaPipe既定 0.5）。\n" +
             "上げるほど「人らしくないもの」を人と誤検知しにくくなるが、\n" +
             "遠い人・暗い場所の人・半分見切れている人は検出されなくなる。\n" +
             "展示会場の背景に人型のポスターや人形がある場合はここを上げる")]
    [SerializeField, Range(0.1f, 0.95f)] private float personConfidence = 0.5f;

    [Header("依存コンポーネント")]
    [Tooltip("ジェスチャー判定を行うコンポーネント")]
    [SerializeField] private GestureDetector gestureDetector;

    [Tooltip("WebCamTexture の供給元")]
    [SerializeField] private CameraBackgroundController cameraBackgroundController;

    [Tooltip("スケルトン描画を行うコンポーネント")]
    [SerializeField] private SkeletonRenderer skeletonRenderer;

    [Header("人物追跡")]
    [Tooltip("検出リストの順番ではなく、腰の位置で同一人物を追跡するための設定")]
    [SerializeField] private PoseTrackerSettings trackerSettings = new();

    [Header("検出頻度")]
    [Tooltip("ポーズ推定を実行する頻度（Hz）。0以下なら毎フレーム。\n" +
             "カメラが30fpsならこれを30より上げても意味がない。\n" +
             "重い場合は下げると推論回数がそのまま減る（追従は鈍くなる）")]
    [SerializeField] private float detectionHz = 30f;

    [Tooltip("カメラのフレームがこの秒数以上更新されなかったら警告を出す。\n" +
             "isPlaying が true のままフレームだけ止まるケースの検知用")]
    [SerializeField] private float frameStallWarnSeconds = 1.0f;

    // ── 内部状態 ──
    private WebCamTexture  _webCamTexture;
    private PoseLandmarker _poseLandmarker;
    // 閾値変更時に PoseLandmarker を作り直すために保持しておくモデルのバイト列
    private byte[]         _modelData;
    private Texture2D      _inputTexture;
    private PoseTracker    _tracker;

    // トラック消失時の後始末。毎フレーム EndFrame に渡すため、ラムダをフィールドに
    // キャッシュしておく（毎回ラムダ式を書くと this をキャプチャしたクロージャが
    // 毎フレーム確保されてしまう）
    private System.Action<int> _onTrackLost;

    // 毎フレームの確保を避けるための使い回しバッファ
    private Color32[] _pixelBuffer;

    // 最後に推論を投げた時刻（detectionHz による間引き用）
    private float _lastDetectionTime = -999f;

    // フレーム停止の検知用（DetectFrozenFrame 参照）
    private uint  _lastFrameHash;
    private float _lastFrameChange;
    private float _frozenSince;
    private bool  _frozenWarned;

    // ── Profiler マーカー ──
    // Profiler の CPU Usage > Hierarchy で "ARHanabi." を検索すると
    // どの工程に何ミリ秒かかっているかが名前付きで読める。
    // ProfilerMarker.Auto() は構造体を返すのでアロケーションは発生しない。
    private static readonly ProfilerMarker _markerReadback = new("ARHanabi.Pose.Readback");
    private static readonly ProfilerMarker _markerUpload   = new("ARHanabi.Pose.Upload");
    private static readonly ProfilerMarker _markerDetect   = new("ARHanabi.Pose.DetectAsync");
    private static readonly ProfilerMarker _markerDispatch = new("ARHanabi.Pose.Dispatch");

    // スレッド間の受け渡し（最新の1件のみ保持）
    private PoseLandmarkerResult _latestResult;
    private bool  _hasNewResult;
    private readonly object _resultLock = new();

    // ── ライフサイクル ──
    // 追跡機構はカメラやモデルの準備を待つ必要がないので Awake で組み立てておく
    // （ProcessLatestResult は初期化コルーチンの完了前にも Update から呼ばれる）
    private void Awake()
    {
        // Admin画面で調整した検出閾値を引き継ぐ。
        // BuildLandmarker より先に読んでおけば、初回生成からこの値が使われる
        personConfidence = SettingsStore.GetFloat(
            $"{nameof(PoseLandmarkDetector)}.{nameof(personConfidence)}", personConfidence);
        maxPeople = Mathf.Clamp(SettingsStore.GetInt(
            $"{nameof(PoseLandmarkDetector)}.{nameof(maxPeople)}", maxPeople), MinPeople, MaxPeopleLimit);

        _tracker = new PoseTracker(trackerSettings);

        _onTrackLost = id =>
        {
            gestureDetector ?.RemovePerson(id);
            skeletonRenderer?.RemovePerson(id);
            PoseEventBus.Instance?.ReportPersonLost(id);
        };
    }

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

        // 再生成のためにモデルのバイト列を保持しておく。
        // 閾値を変えるたびにファイルを読み直すのは無駄なうえ、
        // 展示中に I/O で引っかかると目に見えて止まる
        _modelData = modelData;
        BuildLandmarker();
        yield return null;
    }

    // PoseLandmarker を現在の設定で作り直す。
    //
    // ── なぜ「作り直し」なのか ──
    //   MediaPipe の PoseLandmarkerOptions は生成時にしか渡せず、
    //   後から minPoseDetectionConfidence を差し替える API が無い。
    //   そのため閾値を変える＝作り直しになる。
    //   生成は数十msかかるので毎フレーム呼んではいけない
    //   （Admin画面のボタンを押したときだけ呼ぶ）。
    private void BuildLandmarker()
    {
        if (_modelData == null)
        {
            ArLog.Warn("[Pose] モデル未読み込みのため PoseLandmarker を生成できません");
            return;
        }

        // 古いものは必ず閉じる。閉じずに捨てるとネイティブ側のリソースが残る
        _poseLandmarker?.Close();

        var options = new PoseLandmarkerOptions(
            new BaseOptions(modelAssetBuffer: _modelData),
            runningMode: RunningMode.LIVE_STREAM,
            numPoses: maxPeople,
            minPoseDetectionConfidence: personConfidence,
            minPosePresenceConfidence:  personConfidence,
            minTrackingConfidence:      personConfidence,
            resultCallback: OnPoseDetected
        );

        _poseLandmarker = PoseLandmarker.CreateFromOptions(options);
        ArLog.Info($"[Pose] PoseLandmarker初期化完了（人の検出閾値 {personConfidence:F2} / 最大{maxPeople}人）");
    }

    /// <summary>
    /// 人として検出するのに必要な確信度。Admin画面から調整する。
    /// setter は PoseLandmarker を作り直すので、毎フレーム呼んではいけない。
    /// </summary>
    public float PersonConfidence
    {
        get => personConfidence;
        set
        {
            float v = Mathf.Clamp(value, 0.1f, 0.95f);
            if (Mathf.Approximately(personConfidence, v)) return;

            personConfidence = v;
            SettingsStore.SetFloat($"{nameof(PoseLandmarkDetector)}.{nameof(personConfidence)}", v);

            // 初期化前（モデル未読み込み）に触られても、後の BuildLandmarker が
            // 新しい値を読むので取りこぼさない
            if (_modelData != null) BuildLandmarker();
        }
    }

    // 同時検出人数の実用域。
    // 1未満だと誰も検出されず機能が死ぬ。上限を10で切っているのは、
    // numPoses に比例して1フレームの推論時間が伸びるため
    // （MediaPipe は人数ぶんランドマーク推論を回す）。
    // 会場で10人を同時に拾える画角なら、そもそも1人あたりが小さすぎて
    // ジェスチャー判定が安定しない、という実務上の理由もある。
    public const int MinPeople      = 1;
    public const int MaxPeopleLimit = 10;

    /// <summary>
    /// 同時に検出する人数の上限。Admin画面から調整する。
    /// PersonConfidence と同じく setter が PoseLandmarker を作り直すので、
    /// 毎フレーム呼んではいけない（スライダーはドラッグ終了時にだけ適用すること）。
    /// </summary>
    public int MaxPeople
    {
        get => maxPeople;
        set
        {
            int v = Mathf.Clamp(value, MinPeople, MaxPeopleLimit);
            if (maxPeople == v) return;

            maxPeople = v;
            SettingsStore.SetInt($"{nameof(PoseLandmarkDetector)}.{nameof(maxPeople)}", v);

            if (_modelData != null) BuildLandmarker();
        }
    }

    private void OnDestroy()
    {
        // WebCamTexture は CameraBackgroundController が生成・所有しているものを
        // GetWebCamTexture() で借りているだけなので、ここで Stop() してはいけない。
        // 停止すると背景映像の Quad と SelfieSegmentationController まで巻き込んで
        // 映像が止まる（「時々カメラが停止する」の原因）。破棄は所有者側の責任。
        _webCamTexture = null;
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
        if (_poseLandmarker == null)
        {
            ProcessLatestResult();
            return;
        }

        // didUpdateThisFrame は環境によって常に false を返すことがあり、
        // これを条件にすると推論が一度も走らず「カメラが止まった」ように見える。
        // SelfieSegmentationController には既に同じ問題の回避策が入っていたが
        // こちら側は未対応だった。所有者から最新の参照を取り直して isPlaying で見る。
        var latest = cameraBackgroundController != null
                     ? cameraBackgroundController.GetWebCamTexture()
                     : null;

        if (latest != null && latest.isPlaying && latest.width > 16)
        {
            // カメラが作り直された場合に古い参照を掴み続けないよう毎フレーム追従する
            if (!ReferenceEquals(latest, _webCamTexture))
            {
                _webCamTexture = latest;
                AllocateBuffers(latest.width, latest.height);
                ArLog.Info($"[Pose] WebCamTexture を再取得: {latest.width}x{latest.height}");
            }

            // 推論頻度の間引き。カメラが30fpsなら detectionHz を30より上げても無駄
            if (detectionHz <= 0f || Time.time - _lastDetectionTime >= 1f / detectionHz)
            {
                _lastDetectionTime = Time.time;
                DetectFromCamera();
            }
        }

        ProcessLatestResult();
    }

    private void DetectFromCamera()
    {
        // 解像度が途中で変わることがあるため念のため確認
        AllocateBuffers(_webCamTexture.width, _webCamTexture.height);

        // GPU→CPU の読み戻し。カメラ解像度に比例して重く、GPU パイプラインを待たせる
        using (_markerReadback.Auto())
        {
            // 事前確保した配列に直接受け取る（戻り値の新規配列確保を避ける）
            _webCamTexture.GetPixels32(_pixelBuffer);
        }

        DetectFrozenFrame();

        // CPU コピー ＋ CPU→GPU アップロード
        using (_markerUpload.Auto())
        {
            // Color32 と RGBA32 はメモリレイアウトが一致するので、変換なしの生コピーで済む
            _inputTexture.SetPixelData(_pixelBuffer, 0);
            _inputTexture.Apply(false);
        }

        // MediaPipe への投入。numPoses の値と入力解像度に比例して重くなる
        using (_markerDetect.Auto())
        {
            using (var image = new Mediapipe.Image(_inputTexture))
            {
                long timestamp = (long)(Time.realtimeSinceStartup * 1000);
                _poseLandmarker.DetectAsync(image, timestamp);
            }
        }
    }

    // ── フレーム停止の検知 ──
    // isPlaying が true のままフレームだけ来なくなるケースを捕まえる。
    // 実カメラの映像はセンサーノイズで必ず微妙に変化するため、
    // サンプルした画素が完全に同一なら「同じフレームを見続けている」と判断できる。
    // _pixelBuffer は読み戻し済みなので追加のコストはほぼゼロ。
    private void DetectFrozenFrame()
    {
        if (_pixelBuffer == null || _pixelBuffer.Length == 0) return;

        // 全画素を見る必要はない。等間隔に SampleCount 点だけ拾って畳み込む
        const int SampleCount = 64;
        int stride = Mathf.Max(1, _pixelBuffer.Length / SampleCount);

        uint hash = 2166136261u;   // FNV-1a
        for (int i = 0; i < _pixelBuffer.Length; i += stride)
        {
            var c = _pixelBuffer[i];
            hash = (hash ^ c.r) * 16777619u;
            hash = (hash ^ c.g) * 16777619u;
            hash = (hash ^ c.b) * 16777619u;
        }

        float now = Time.time;

        if (hash != _lastFrameHash)
        {
            _lastFrameHash   = hash;
            _lastFrameChange = now;

            if (_frozenWarned)
            {
                _frozenWarned = false;
                Debug.Log($"[Pose] カメラのフレーム更新が再開しました（停止していた時間: " +
                          $"{now - _frozenSince:F1}秒）");
            }
            return;
        }

        // ハッシュが変わらないまま経過している
        if (_frozenWarned || now - _lastFrameChange < frameStallWarnSeconds) return;

        _frozenWarned = true;
        _frozenSince  = _lastFrameChange;
        Debug.LogWarning($"[Pose] カメラのフレームが {frameStallWarnSeconds}秒 以上更新されていません" +
                         $"（isPlaying={_webCamTexture.isPlaying}）。" +
                         "isPlaying が true のままなら、Unity 側は配信中だと思っているのに" +
                         "デバイスがフレームを送っていない状態です");
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

        // 追跡・ジェスチャー判定・スケルトン描画の合計。推論結果が来たフレームだけ走る
        using (_markerDispatch.Auto())
        {
            _tracker.BeginFrame();

            for (int i = 0; i < result.poseLandmarks.Count; i++)
            {
                var landmarks = result.poseLandmarks[i].landmarks;

                // 腰中心（landmark 23 = 左腰 / 24 = 右腰）で人物を照合する。
                // ランドマーク数が足りない検出はスキップする（追跡が壊れるため）
                if (landmarks == null || landmarks.Count < 25) continue;

                int trackId = _tracker.Resolve(HipCenter(landmarks));

                gestureDetector ?.ProcessLandmarks(trackId, landmarks);
                skeletonRenderer?.UpdateSkeleton(trackId, landmarks);
            }

            // タイムアウトしたトラックの後始末。デリゲートはフィールドにキャッシュ済み
            _tracker.EndFrame(_onTrackLost);
        }
    }

    // ── 腰中心の算出 ──
    // x も y も反転しない。WebCamTexture.GetPixels32() が下の行から並んだ配列を返すため、
    // MediaPipe の y は既に表示映像に対して「下が 0」になっている（PoseCoordinateUtil の
    // 冒頭コメント参照）。そもそも追跡は前フレームとの相対距離しか見ないので、
    // 片方だけ反転させると距離計算が壊れる。
    private static Vector2 HipCenter(List<NormalizedLandmark> landmarks)
    {
        var leftHip  = landmarks[23];
        var rightHip = landmarks[24];

        return new Vector2(
            (leftHip.x + rightHip.x) * 0.5f,
            (leftHip.y + rightHip.y) * 0.5f
        );
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
