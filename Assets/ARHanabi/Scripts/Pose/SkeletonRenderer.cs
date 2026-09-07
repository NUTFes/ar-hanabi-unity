using System.Collections.Generic;
using UnityEngine;
using Mediapipe.Tasks.Components.Containers;

// ===== SkeletonRenderer =====
// MediaPipe Pose のランドマークを LineRenderer でスケルトン描画する。
// あわせて PoseEventBus の PoseFeedback を受け取り、ジェスチャーの状態を
// 「線の色」と「線の太さ」で常時可視化する。
//
// ── 位置合わせ（backgroundQuad を使う理由）──
//   landmark を ScreenToWorldPoint で「画面全体」に張ると、カメラ映像とズレる。
//   映像は CameraBackground の Quad に貼られており、その Quad が画面上のどこに
//   どれだけの大きさで映るかは カメラのFOV・Quadまでの距離・Quadのスケール・
//   カメラのY位置・そして画面のアスペクト比 で決まる。画面全体とは一致しない。
//
//   実測（Quad: pos(0,0,5) scale(42,24) / Camera: pos(0,1,-10) 垂直FOV60°）:
//     カメラ〜Quad は 15 units、そこでの視錐台の高さは 2*15*tan30° = 17.32。
//     Quad は 24 あるため映像は画面に収まらず中央だけが見えている。
//     映像が占める画面範囲は 縦 -0.251〜1.135 / 横(16:9) -0.182〜1.182。
//     一方スケルトンは 0〜1 に張られるので、約72%に縮んだうえ縦に約6%ずれる。
//     しかも横は「高さ×アスペクト比」なので、ウィンドウの縦横比を変えると
//     ズレ量が変わる（16:9 で映像の73%、4:3 で55%）。
//
//   そこで landmark を Quad の面上に直接マッピングする。こうすれば画面サイズ・
//   アスペクト比・FOV・カメラ位置が何であっても映像と必ず一致する。
//   backgroundQuad 未指定時は従来の画面全体マッピングにフォールバックする。
//
// ── 状態フィードバックの3状態 ──
//   ポーズ保持中（0.5秒）とクールダウン中（2.0秒）に何も反応が無いと、
//   展示に来た人が「反応しない」と誤解して離れてしまう。そこで
//   PoseEventBus.OnPoseFeedback を購読し、次の3状態を色と太さで表現する。
//     Idle / 未報告 … 人ごとの基本色 / 通常の太さ
//     Charging      … 基本色→chargeColor へ progress で補間、太さも同時に太くする
//                     （溜まっていることが分かるので「あと少し」が伝わる）
//     Cooldown      … 基本色を暗く（cooldownColorMultiplier）＋半透明（cooldownAlpha）
//                     太さは通常のまま（「今は撃てない」ことだけ伝える）
//   GestureDetector は早期 return するフレームがあり、その人の PoseFeedback が
//   届かないことがある。_feedback にエントリが無い trackId は Idle として扱う。
//
// ── ただし既定ではこの演出は無効にしてある（enableInteractiveFeedback = false）──
//   運用側から「ボーンの色がインタラクティブに変わるのをやめたい」という要望があったため、
//   既定で色も太さも「人ごとの基本色 ＋ lineWidth」に完全固定している。
//   上の3状態の計算コードと関連フィールド（flashColor / chargeColor /
//   cooldownColorMultiplier など）は一切消していないので、
//   Inspector で enableInteractiveFeedback を ON に戻せば従来の挙動がそのまま復活する。
//
//   PoseEventBus の購読（OnGestureDetected / OnPoseFeedback）も残してある。
//   _flashUntil と _feedback は更新され続けるが、どちらも辞書への代入だけで、
//   ResolveFeedbackStyle が基本値を返す限り _appliedStyle の差分スキップが効いて
//   LineRenderer への代入は一度も走らない。よって描画コストは増えない。
//   購読を切ってしまうと、トグルを ON に戻したときに配線から直す必要が出てしまう。
//
// ── マテリアルを1個に統一した理由 ──
//   以前は「頂点カラーが効くか分からない」ため、人ごとに Material を1個複製して
//   _Color / _BaseColor に色を入れていた（最大5個 + OnDestroy での破棄が必要）。
//   SkeletonMaterial は Custom/ParticleUnlit（頂点カラー × _BaseColor を返し、
//   Blend SrcAlpha OneMinusSrcAlpha）に差し替えたので、色もアルファも
//   lr.startColor / lr.endColor だけで確実に制御できる。
//   よって全員が Inspector の lineMaterial を sharedMaterial として共有する。
//   マテリアルは1個なので複製もリークも無く、DrawCall もまとまる。
//   （LineRenderer.material に代入すると Unity がマテリアルを複製するため、
//    必ず sharedMaterial を使うこと。material だと 16本×5人=80個作られる。）
//
// 詳細ログを見たい場合は ArLog.cs 冒頭の手順で AR_VERBOSE_LOG を定義する。

public class SkeletonRenderer : MonoBehaviour
{
    [Header("設定")]
    [Tooltip("スクリーン座標→ワールド座標変換に使うカメラ")]
    [SerializeField] private Camera mainCamera;

    [Header("位置合わせ")]
    [Tooltip("カメラ映像を貼っている Quad（CameraBackground）をアサインする。\n" +
             "アサインするとスケルトンをこの Quad の面上に描くため、画面サイズや\n" +
             "アスペクト比・FOV が変わっても映像とずれない。\n" +
             "空のままだと従来どおり画面全体へのマッピングになり、映像とずれる。")]
    [SerializeField] private Transform backgroundQuad;

    [Tooltip("Quad 面からカメラ側へどれだけ手前に描くか（Zファイト回避）")]
    [SerializeField] private float quadOffset = 0.05f;

    [Tooltip("【backgroundQuad が未設定のときだけ使う】\n" +
             "スケルトンを描画するカメラからの距離")]
    [SerializeField] private float drawDistance = 9f;

    [Tooltip("線の太さ")]
    [SerializeField] private float lineWidth = 0.02f;

    [Tooltip("線に使うマテリアル。複製せず sharedMaterial として共有する。\n" +
             "Custom/ParticleUnlit なら色もアルファも頂点カラーで制御できる")]
    [SerializeField] private Material lineMaterial;

    [Tooltip("OFF: 骨格の線を一切描かない。花火を見せることを優先する運用向け。\n" +
             "機能自体は残してあるので、ONに戻せば従来どおり描画される。\n" +
             "実行中は ShowSkeleton プロパティ経由で Admin画面から切り替わる。\n" +
             "丸窓（ドーム）モードとは無関係な独立設定で、ドーム中でもONならボーンは出る")]
    [SerializeField] private bool showSkeleton = true;

    [Header("感知フラッシュ")]
    [Tooltip("ジェスチャーを感知した瞬間に光らせる色")]
    [SerializeField] private Color flashColor              = new Color(1f, 0.95f, 0.6f, 1f);

    [Tooltip("フラッシュの長さ（秒）。0 にすると無効")]
    [SerializeField] private float flashDuration           = 0.35f;

    [Tooltip("フラッシュのピーク時の線の太さ倍率")]
    [SerializeField] private float flashWidthMultiplier    = 3.0f;

    [Header("状態フィードバック")]
    [Tooltip("ON: ジェスチャー状態に応じて線の色と太さを変える（フラッシュ/チャージ/クールダウン）\n" +
             "OFF: 人ごとの基本色と lineWidth に完全固定する。演出のコードは残してあるので、\n" +
             "     このトグルを ON に戻せば従来の挙動がそのまま復活する")]
    [SerializeField] private bool  enableInteractiveFeedback = false;

    [Tooltip("ポーズ保持中のチャージ表示を出すか")]
    [SerializeField] private bool  showChargeFeedback      = true;

    [Tooltip("クールダウン中の暗転表示を出すか")]
    [SerializeField] private bool  showCooldownFeedback    = false;

    [Tooltip("チャージ完了時に向かう色")]
    [SerializeField] private Color chargeColor             = Color.white;

    [Tooltip("チャージ完了時の線の太さ倍率")]
    [SerializeField] private float chargeWidthMultiplier   = 2.0f;

    [Tooltip("クールダウン中の明るさ倍率（RGBに掛ける）")]
    [SerializeField] private float cooldownColorMultiplier = 0.35f;

    [Tooltip("クールダウン中の不透明度")]
    [SerializeField] private float cooldownAlpha           = 0.4f;

    // ── ランドマーク接続定義 ──
    // MediaPipe Pose ランドマーク接続定義（16本）
    private static readonly (int, int)[] _connections = new[]
    {
        // 上半身
        (11, 12), (11, 13), (13, 15),
        (12, 14), (14, 16),
        // 体幹
        (11, 23), (12, 24), (23, 24),
        // 下半身
        (23, 25), (25, 27), (27, 29), (29, 31),
        (24, 26), (26, 28), (28, 30), (30, 32),
    };

    // ── 人ごとの色 ──
    private static readonly Color[] _personColors =
    {
        new Color(0.2f, 0.8f, 1.0f),  // 水色
        new Color(1.0f, 0.6f, 0.2f),  // オレンジ
        new Color(0.4f, 1.0f, 0.4f),  // 緑
        new Color(1.0f, 0.4f, 0.8f),  // ピンク
        new Color(1.0f, 1.0f, 0.4f),  // 黄色
    };

    // ── 内部状態 ──
    // 人ごとの LineRenderer を管理
    private readonly Dictionary<int, List<LineRenderer>> _personLines = new();
    private readonly Dictionary<int, float> _lastUpdateTime = new();
    private readonly Dictionary<int, bool>  _personVisible  = new();

    // 最新のフィードバック状態。OnPoseFeedback は最大5人×毎フレーム流れてくるので
    // ハンドラでは代入だけ行い、描画もログもしない（PoseFeedback は struct なので
    // 辞書への代入でアロケーションは起きない）
    private readonly Dictionary<int, PoseFeedback> _feedback = new();

    // 感知フラッシュの終了時刻（trackId → Time.time ベース）
    private readonly Dictionary<int, float> _flashUntil = new();

    // 直前に LineRenderer へ適用した色と太さ。
    // startColor / startWidth への代入は LineRenderer をダーティにするため、
    // 値が変わっていないフレームは代入自体をスキップする
    // （16本 × 最大5人 = 80本 × 4プロパティ = 320回/フレームの無駄を消す）
    private struct AppliedStyle
    {
        public Color color;
        public float width;
        public bool  valid;
    }
    private readonly Dictionary<int, AppliedStyle> _appliedStyle = new();

    // タイムアウト判定でループ中に辞書を触らないための一時リスト（毎フレーム確保しない）
    private readonly List<int> _timedOutBuffer = new();

    private const float _TimeoutSeconds = 0.5f; // この秒数更新がなければ非表示

    // 購読済みかどうか。OnEnable と Start の二重購読を防ぐ
    private bool _subscribed;

    // ── 永続化 ──
    // ボーンの表示ON/OFFは Admin画面から切り替える運用設定なので、
    // 展示は複数セッション・複数日にまたがって電源を落とすため PlayerPrefs 経由で
    // 次回起動時にも引き継ぐ（SettingsStore 参照）。
    // キーが無い＝一度も Admin 画面から触っていない場合は、Inspector/シーンに
    // 保存されている値（= このフィールドの現在値）がそのまま使われる。
    // ※ この設定は CameraCircleMatte（丸窓モード）とは無関係な独立設定。
    //   以前はドーム有効時に強制OFFにしていたが、「ドーム中でもボーンを見せる」
    //   方針に変わったため、あちら側の骨格処理は撤去してある
    private void Awake()
    {
        showSkeleton = SettingsStore.GetBool($"{nameof(SkeletonRenderer)}.{nameof(showSkeleton)}", showSkeleton);

        // setter と同じ後処理を通す。Awake 時点では線がまだ1本も無いので実質は空振りだが、
        // 「フィールドへ直接代入したら必ず後処理を通す」形を崩さないために揃えておく
        ApplyShowSkeleton();
    }

    // ── イベント購読 ──
    // Unity は「全オブジェクトの Awake → 全オブジェクトの OnEnable」ではなく
    // オブジェクトごとに Awake → OnEnable を順に呼ぶ。そのため OnEnable の時点では
    // PoseEventBus.Awake() がまだ走っておらず Instance が null になりうる。
    // その場合に購読を諦めると「フィードバックが一切出ないのにエラーも出ない」という
    // 最悪の壊れ方をするので、全 Awake の完了後に必ず走る Start でも再試行する。
    private void OnEnable() => TrySubscribe();

    private void Start()    => TrySubscribe();

    private void TrySubscribe()
    {
        if (_subscribed) return;

        var bus = PoseEventBus.Instance;
        if (bus == null) return;   // Start でもう一度試す

        bus.OnPoseFeedback    += OnPoseFeedback;
        bus.OnPersonLost      += OnPersonLost;
        bus.OnGestureDetected += OnGestureDetected;
        _subscribed            = true;
    }

    private void OnDisable()
    {
        if (!_subscribed) return;

        var bus = PoseEventBus.Instance;
        if (bus != null)
        {
            bus.OnPoseFeedback    -= OnPoseFeedback;
            bus.OnPersonLost      -= OnPersonLost;
            bus.OnGestureDetected -= OnGestureDetected;
        }
        _subscribed = false;
    }

    // ジェスチャーが感知された瞬間。フラッシュの終了時刻を記録するだけ。
    // gestureCooldown が効くので低頻度（1人あたり最短2秒に1回）
    private void OnGestureDetected(int trackId, GestureType gesture, Vector2 normalizedPos)
    {
        if (flashDuration <= 0f) return;
        _flashUntil[trackId] = Time.time + flashDuration;
    }

    // 毎フレーム×人数分呼ばれる。最新値の保存だけに留めること
    private void OnPoseFeedback(PoseFeedback feedback)
    {
        _feedback[feedback.trackId] = feedback;
    }

    private void OnPersonLost(int trackId)
    {
        RemovePerson(trackId);
    }

    // ── ライフサイクル ──
    private void Update()
    {
        // タイムアウトした人のスケルトンを非表示
        // （PoseTracker が人を破棄して OnPersonLost が飛ぶより先に見た目を消す保険）
        var now = Time.time;

        _timedOutBuffer.Clear();
        foreach (var kvp in _lastUpdateTime)
        {
            if (now - kvp.Value > _TimeoutSeconds) _timedOutBuffer.Add(kvp.Key);
        }

        for (int i = 0; i < _timedOutBuffer.Count; i++)
        {
            SetPersonVisible(_timedOutBuffer[i], false);
        }
    }

    private void OnDestroy()
    {
        foreach (var lines in _personLines.Values)
        {
            foreach (var lr in lines)
            {
                if (lr != null) Destroy(lr.gameObject);
            }
        }
        _personLines.Clear();
        _lastUpdateTime.Clear();
        _personVisible.Clear();
        _feedback.Clear();
        _flashUntil.Clear();
        _appliedStyle.Clear();
    }

    // ── Public API ──

    /// <summary>
    /// 骨格の線を描くか。false にすると既存の線も含めて一切表示しない。
    ///
    /// 「花火を見せることを優先したい」運用向けのスイッチで、Admin画面から切り替える。
    /// 丸窓（ドーム）モードとは独立した設定であり、ドーム中でも true ならボーンは出る。
    /// 機能自体は消していないので true に戻せば従来どおり描画される。
    ///
    /// コンポーネントの enabled を落とす方式にしなかった理由:
    /// UpdateSkeleton() は PoseLandmarkDetector からの直接呼び出しなので、
    /// enabled = false にしても呼ばれ続けてしまい線が消えない。
    /// </summary>
    public bool ShowSkeleton
    {
        get => showSkeleton;
        set
        {
            if (showSkeleton == value) return;
            showSkeleton = value;

            ApplyShowSkeleton();

            // 展示は複数セッション・複数日にまたがって電源を落とすので、選択を次回起動へ引き継ぐ。
            // 切り替えは Admin画面のボタン操作時だけなので、毎回 Save() を呼ぶコストは無視できる
            SettingsStore.SetBool($"{nameof(SkeletonRenderer)}.{nameof(showSkeleton)}", showSkeleton);
        }
    }

    // showSkeleton を変えた直後に必要な後処理。
    // OFF にした瞬間、既に生成済みの線を隠す
    // （次の UpdateSkeleton を待たずに即座に消えるようにするため）
    private void ApplyShowSkeleton()
    {
        if (showSkeleton) return;

        foreach (var personIndex in _personLines.Keys)
            SetPersonVisible(personIndex, false);
    }

    public void UpdateSkeleton(int personIndex, List<NormalizedLandmark> landmarks)
    {
        if (!showSkeleton)
        {
            // 生成済みの線があれば隠しておく（この人が今フレーム初めて現れた場合も含めて、
            // 表示状態を必ず false 側へ倒す）
            SetPersonVisible(personIndex, false);
            return;
        }

        // _connections が参照する最大インデックスは32 → 33点必要
        const int requiredLandmarkCount = 33;
        if (landmarks == null || landmarks.Count < requiredLandmarkCount)
        {
            // ランドマーク数が不足している場合は描画をスキップ（前回の姿勢のまま表示維持）
            return;
        }

        if (mainCamera == null)
        {
            ArLog.Verbose("[Skeleton] mainCamera が未設定のため描画をスキップします");
            return;
        }

        _lastUpdateTime[personIndex] = Time.time;

        // 初回のみ LineRenderer を生成
        if (!_personLines.TryGetValue(personIndex, out var lines))
        {
            lines = CreateLineRenderers(personIndex);
        }

        SetPersonVisible(personIndex, true);

        // 状態から色と太さを1回だけ求める
        ResolveFeedbackStyle(personIndex, out var color, out var width);

        // 前フレームと同じ見た目なら色・太さの代入を省く。
        // startColor / startWidth の setter は LineRenderer をダーティにするので、
        // Idle で止まっている間ずっと 320回/フレームの無駄な代入が走っていた
        _appliedStyle.TryGetValue(personIndex, out var applied);
        bool styleChanged = !applied.valid
                         || applied.width   != width
                         || applied.color.r != color.r
                         || applied.color.g != color.g
                         || applied.color.b != color.b
                         || applied.color.a != color.a;

        if (styleChanged)
            _appliedStyle[personIndex] = new AppliedStyle { color = color, width = width, valid = true };

        for (int i = 0; i < _connections.Length; i++)
        {
            var (a, b) = _connections[i];

            // 念のための二重チェック（将来 _connections が変更されても安全に）
            if (a >= landmarks.Count || b >= landmarks.Count) continue;

            var lr = lines[i];
            lr.SetPosition(0, LandmarkToWorld(landmarks[a]));
            lr.SetPosition(1, LandmarkToWorld(landmarks[b]));

            if (!styleChanged) continue;

            lr.startColor = color;
            lr.endColor   = color;
            lr.startWidth = width;
            lr.endWidth   = width;
        }
    }

    // 人が消えたときに LineRenderer と状態を完全に破棄する。
    // trackId は PoseTracker によって単調増加し上限が無いため、これを呼ばないと
    // 消えた人の GameObject が永久に溜まり続ける。
    // 未知の trackId を渡されても安全（Dictionary.Remove は無いキーでも例外を投げない）。
    public void RemovePerson(int trackId)
    {
        if (_personLines.TryGetValue(trackId, out var lines))
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] != null) Destroy(lines[i].gameObject);
            }
            _personLines.Remove(trackId);
        }

        _lastUpdateTime.Remove(trackId);
        _personVisible.Remove(trackId);
        _feedback.Remove(trackId);
        _flashUntil.Remove(trackId);
        _appliedStyle.Remove(trackId);
    }

    // ── 状態 → 色・太さ ──
    // 優先順位は フラッシュ > Cooldown > Charging > Idle。
    // フラッシュは「ジェスチャーを感知した」ことを伝える主役なので、
    // 発火直後に始まるクールダウン表示に潰されないよう最優先で判定する。
    // _feedback にエントリが無い trackId は Idle 扱い。
    // Color / float はどちらも構造体なのでこのメソッドはアロケーションしない。
    private void ResolveFeedbackStyle(int trackId, out Color color, out float width)
    {
        var baseColor = GetPersonColor(trackId);

        color = baseColor;
        width = lineWidth;

        // 基本値を入れた直後に抜ける。out 引数は既に初期化済みなので安全に return できるうえ、
        // 以降のフラッシュ／チャージ／クールダウンの計算をまとめて飛ばせる。
        // 既定は OFF（= 完全に静的）。理由と復帰方法はファイル冒頭のコメントを参照
        if (!enableInteractiveFeedback) return;

        // ── 感知フラッシュ ──
        if (_flashUntil.TryGetValue(trackId, out float flashUntil))
        {
            float remaining = flashUntil - Time.time;
            if (remaining > 0f && flashDuration > 0f)
            {
                // 立ち上がりは瞬間、消え際はゆっくり（t を2乗して減衰を後ろに寄せる）
                float t = Mathf.Clamp01(remaining / flashDuration);
                float eased = t * t;

                color = Color.Lerp(baseColor, flashColor, eased);
                width = Mathf.Lerp(lineWidth, lineWidth * flashWidthMultiplier, eased);
                return;   // フラッシュ中は他の状態を上書きする
            }
        }

        if (!_feedback.TryGetValue(trackId, out var feedback)) return;

        switch (feedback.state)
        {
            case PoseFeedbackState.Charging:
            {
                if (!showChargeFeedback) break;

                // progress: 0→1。1 に近づくほど chargeColor へ寄り、線も太くなる
                float t = Mathf.Clamp01(feedback.progress);
                color = Color.Lerp(baseColor, chargeColor, t);
                width = Mathf.Lerp(lineWidth, lineWidth * chargeWidthMultiplier, t);
                break;
            }

            case PoseFeedbackState.Cooldown:
                if (!showCooldownFeedback) break;

                // 暗く半透明にして「今は撃てない」ことを示す
                color = new Color(baseColor.r * cooldownColorMultiplier,
                                  baseColor.g * cooldownColorMultiplier,
                                  baseColor.b * cooldownColorMultiplier,
                                  cooldownAlpha);
                break;

            // Idle は基本色・通常の太さのまま
        }
    }

    // ── 座標変換 ──
    private Vector3 LandmarkToWorld(NormalizedLandmark landmark)
    {
        // x も y も反転しない。WebCamTexture.GetPixels32() が下の行から並んだ配列を
        // 返すため、MediaPipe の y は既に表示映像に対して「下が 0」になっている。
        // 詳しい理由は PoseCoordinateUtil の冒頭コメントを参照。

        // Quad があるなら面上に直接置く（画面サイズやアスペクト比に影響されない）
        if (backgroundQuad != null)
            return LandmarkToQuadPoint(landmark.x, landmark.y);

        // フォールバック: 画面全体へのマッピング。映像とズレるので非推奨
        return PoseCoordinateUtil.ToWorldPoint(mainCamera, landmark.x, landmark.y, drawDistance);
    }

    // ── Quad 面上へのマッピング ──
    // Unity 内蔵 Quad メッシュはローカル 1x1・原点中心・+X が右 / +Y が上で、
    // テクスチャの uv=(0,0) がローカル (-0.5,-0.5) に対応する。
    // localScale がそのまま表示サイズになるので、(u-0.5, v-0.5) を TransformPoint
    // すれば uv=(u,v) の位置にある面上の点がそのまま得られる。
    // Transform を経由するので、Quad を動かしても拡大しても自動で追従する。
    private Vector3 LandmarkToQuadPoint(float u, float v)
    {
        var point = backgroundQuad.TransformPoint(
            new Vector3(Mathf.Clamp01(u) - 0.5f, Mathf.Clamp01(v) - 0.5f, 0f));

        // Quad と同じ深さだと Z ファイトで線が消えるのでカメラ側へ少し寄せる
        if (mainCamera != null && quadOffset != 0f)
        {
            var toCamera = mainCamera.transform.position - point;
            if (toCamera.sqrMagnitude > 0.0001f)
                point += toCamera.normalized * quadOffset;
        }

        return point;
    }

    // ── LineRenderer 生成 ──
    // 色は頂点カラー（startColor / endColor）で付けるので、マテリアルは全員で共有する
    private List<LineRenderer> CreateLineRenderers(int personIndex)
    {
        var lines = new List<LineRenderer>(_connections.Length);
        var color = GetPersonColor(personIndex);

        if (lineMaterial == null)
        {
            ArLog.Warn("[Skeleton] lineMaterial が未設定です。既定のマテリアルで描画します");
        }

        for (int i = 0; i < _connections.Length; i++)
        {
            var go = new GameObject($"SkeletonLine_P{personIndex}");
            go.transform.SetParent(transform);

            var lr = go.AddComponent<LineRenderer>();
            // material ではなく sharedMaterial（material はインスタンスを複製してしまう）
            lr.sharedMaterial = lineMaterial;
            lr.startColor     = color;
            lr.endColor       = color;
            lr.startWidth     = lineWidth;
            lr.endWidth       = lineWidth;
            lr.positionCount  = 2;
            lr.useWorldSpace  = true;

            lines.Add(lr);
        }

        _personLines[personIndex] = lines;
        return lines;
    }

    // ── 表示制御 ──
    private void SetPersonVisible(int personIndex, bool visible)
    {
        if (!_personLines.TryGetValue(personIndex, out var lines)) return;

        // 状態が変わっていないなら何もしない（毎フレーム80回の enabled 代入を防ぐ）
        if (_personVisible.TryGetValue(personIndex, out bool current) && current == visible) return;
        _personVisible[personIndex] = visible;

        foreach (var lr in lines)
        {
            if (lr != null) lr.enabled = visible;
        }
    }

    // trackId は PoseTracker により単調増加して上限が無いので、必ず剰余で丸める
    // （そのまま添字にすると IndexOutOfRangeException になる）
    private static Color GetPersonColor(int trackId)
    {
        int length = _personColors.Length;
        int slot   = trackId % length;
        if (slot < 0) slot += length; // trackId は必ず 0 以上だが念のため負値にも耐える
        return _personColors[slot];
    }
}
