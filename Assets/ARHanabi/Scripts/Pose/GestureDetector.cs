using System.Collections.Generic;
using UnityEngine;
using Mediapipe.Tasks.Components.Containers;

// ===== GestureDetector =====
// MediaPipe Pose のランドマークからジェスチャー（両手上げ／片手上げ／ジャンプ）を判定し、
// PoseEventBus 経由で花火の発射イベントを流す。
//
// ログについて（コードレビュー指摘 4.11）:
//   判定用の詳細ログは「毎フレーム × 最大5人」で出るため、以前は 1秒あたり1,000本超の
//   Debug.Log が発生していた（1本あたり10〜50µs なので実測で効くレベル）。
//   これらは ArLog.Verbose に置き換えてあり、AR_VERBOSE_LOG が未定義なら
//   呼び出しごと消える（文字列補間のコストもゼロになる）。
//   一方でジェスチャーが成立した瞬間のログは gestureCooldown で頻度が抑えられているため
//   Debug.Log のまま残している。
//
// 詳細ログを見たい場合は ArLog.cs 冒頭の手順で AR_VERBOSE_LOG を定義する。

public class GestureDetector : MonoBehaviour
{
    [Header("ジェスチャー判定設定")]
    [Tooltip("手が肩より何割上なら「上げた」と判定するか（肩幅に対する相対値）")]
    [SerializeField] private float handUpThreshold    = 0.15f;

    [Tooltip("ジャンプ判定の閾値（肩幅に対する相対値。大きいほど誤検知しにくい）")]
    [SerializeField] private float jumpThreshold      = 0.06f;

    [Tooltip("同じジェスチャーの連続発火を防ぐ秒数")]
    [SerializeField] private float gestureCooldown    = 2.0f;

    [Tooltip("ポーズを何秒維持したら発射するか")]
    [SerializeField] private float poseHoldDuration   = 0.5f;

    // ── 永続化 ──
    // 会場・客層で毎回変えたくなる値なので、Admin画面（SETTINGS）から調整できる。
    // 展示は複数セッション・複数日にまたがって電源を落とすため、調整した値は
    // PlayerPrefs 経由で次回起動時にも引き継ぐ（SettingsStore 参照）。
    // キーが無い＝一度も Admin 画面から触っていない場合は、Inspector/シーンに
    // 保存されている値（= このフィールドの現在値）がそのまま使われる
    private void Awake()
    {
        handUpThreshold  = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(handUpThreshold)}",  handUpThreshold);
        jumpThreshold    = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(jumpThreshold)}",    jumpThreshold);
        gestureCooldown  = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(gestureCooldown)}",  gestureCooldown);
        poseHoldDuration = SettingsStore.GetFloat($"{nameof(GestureDetector)}.{nameof(poseHoldDuration)}", poseHoldDuration);
    }

    // ── Admin画面（SETTINGS）からの調整用 ──
    // set のたびに PlayerPrefs へ保存する。頻繁に呼ばれる値ではない
    // （ボタンクリック時のみ）ので、毎回 Save() を呼ぶコストは無視できる
    public float HandUpThreshold
    {
        get => handUpThreshold;
        set { handUpThreshold = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(handUpThreshold)}", value); }
    }

    public float JumpThreshold
    {
        get => jumpThreshold;
        set { jumpThreshold = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(jumpThreshold)}", value); }
    }

    public float GestureCooldown
    {
        get => gestureCooldown;
        set { gestureCooldown = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(gestureCooldown)}", value); }
    }

    public float PoseHoldDuration
    {
        get => poseHoldDuration;
        set { poseHoldDuration = value; SettingsStore.SetFloat($"{nameof(GestureDetector)}.{nameof(poseHoldDuration)}", value); }
    }

    // ── 人ごとの判定状態 ──
    private class PersonState
    {
        public float prevHipY             = -1f;
        public float lastGestureTime      = -999f;

        public float bothHandsUpStartTime = -1f;
        public float oneHandUpStartTime   = -1f;

        public bool bothHandsFired        = false;
        public bool oneHandFired          = false;
    }

    private readonly Dictionary<int, PersonState> _personStates = new();

    // ── ランドマーク処理（毎フレーム × 人数分呼ばれる）──
    // personIndex: 何人目か（PoseTracker が振る安定した trackId が渡される想定）
    public void ProcessLandmarks(int personIndex, List<NormalizedLandmark> landmarks)
    {
        if (landmarks == null || landmarks.Count < 29) return;

        if (!_personStates.TryGetValue(personIndex, out var state))
        {
            state = new PersonState();
            _personStates[personIndex] = state;
        }

        var leftShoulder  = landmarks[11];
        var rightShoulder = landmarks[12];
        var leftWrist     = landmarks[15];
        var rightWrist    = landmarks[16];
        var leftHip       = landmarks[23];
        var rightHip      = landmarks[24];

        var centerX   = (leftHip.x + rightHip.x) / 2f;
        var centerY   = (leftHip.y + rightHip.y) / 2f;
        var screenPos = new Vector2(centerX, centerY);

        float now     = Time.time;
        bool  canFire = (now - state.lastGestureTime) > gestureCooldown;

        // ── 肩幅を基準にスケールを計算 ──
        float shoulderWidth = Mathf.Abs(leftShoulder.x - rightShoulder.x);
        // 肩幅が極端に小さい場合（検出不安定）はスキップ
        if (shoulderWidth < 0.01f) return;

        // 閾値を肩幅に対する相対値で計算
        float dynamicHandUpThreshold = shoulderWidth * handUpThreshold;
        float dynamicJumpThreshold   = shoulderWidth * jumpThreshold;

        // 毎フレーム × 人数分出るので Verbose
        ArLog.Verbose($"[Pose] P{personIndex} shoulderWidth={shoulderWidth:F3} " +
                      $"handUpThreshold={dynamicHandUpThreshold:F3} " +
                      $"jumpThreshold={dynamicJumpThreshold:F3}");

        // ── ジャンプ判定 ──
        float hipY = (leftHip.y + rightHip.y) / 2f;
        if (state.prevHipY > 0f && canFire)
        {
            float deltaY = state.prevHipY - hipY;
            if (deltaY > dynamicJumpThreshold)
            {
                FireGesture(personIndex, GestureType.Jump, screenPos, state);
            }
        }
        state.prevHipY = hipY;

        // ── 手上げ判定 ──
        float shoulderY   = (leftShoulder.y + rightShoulder.y) / 2f;
        bool  leftHandUp  = leftWrist.y  < (shoulderY - dynamicHandUpThreshold);
        bool  rightHandUp = rightWrist.y < (shoulderY - dynamicHandUpThreshold);
        bool  bothHandsUp = leftHandUp && rightHandUp;
        bool  oneHandUp   = leftHandUp ^ rightHandUp;

        // 毎フレーム × 人数分出るので Verbose
        ArLog.Verbose($"[Pose] P{personIndex} " +
                      $"shoulderY={shoulderY:F2} " +
                      $"leftWristY={leftWrist.y:F2} rightWristY={rightWrist.y:F2} " +
                      $"leftUp={leftHandUp} rightUp={rightHandUp} " +
                      $"canFire={canFire}");

        // 両手上げ
        if (bothHandsUp)
        {
            if (state.bothHandsUpStartTime < 0f)
                state.bothHandsUpStartTime = now;

            state.oneHandUpStartTime = -1f;
            state.oneHandFired       = false;

            float heldDuration = now - state.bothHandsUpStartTime;
            // ポーズを維持している間ずっと出るので Verbose
            ArLog.Verbose($"[Pose] P{personIndex} 両手上げ中: {heldDuration:F2}秒 / {poseHoldDuration}秒");

            if (heldDuration >= poseHoldDuration && !state.bothHandsFired && canFire)
            {
                FireGesture(personIndex, GestureType.BothHandsUp, screenPos, state);
                state.bothHandsFired = true;
            }
        }
        else
        {
            state.bothHandsUpStartTime = -1f;
            state.bothHandsFired       = false;
        }

        // 片手上げ
        if (oneHandUp && !bothHandsUp)
        {
            if (state.oneHandUpStartTime < 0f)
                state.oneHandUpStartTime = now;

            float heldDuration = now - state.oneHandUpStartTime;
            // ポーズを維持している間ずっと出るので Verbose
            ArLog.Verbose($"[Pose] P{personIndex} 片手上げ中: {heldDuration:F2}秒 / {poseHoldDuration}秒");

            if (heldDuration >= poseHoldDuration && !state.oneHandFired && canFire)
            {
                FireGesture(personIndex, GestureType.OneHandUp, screenPos, state);
                state.oneHandFired = true;
            }
        }
        else
        {
            state.oneHandUpStartTime = -1f;
            state.oneHandFired       = false;
        }

        // ── 状態の配信 ──
        // 優先順位は Cooldown > Charging > Idle。
        // クールダウン中は canFire が false で実際には発射できないため、
        // 保持中の見た目を出すと「溜まっているのに撃てない」という嘘になる。
        var feedback = new PoseFeedback { trackId = personIndex };

        if (!canFire)
        {
            feedback.state    = PoseFeedbackState.Cooldown;
            feedback.progress = gestureCooldown <= 0f
                ? 0f
                : 1f - Mathf.Clamp01((now - state.lastGestureTime) / gestureCooldown);
        }
        else if (state.bothHandsUpStartTime >= 0f)
        {
            feedback.state    = PoseFeedbackState.Charging;
            feedback.gesture  = GestureType.BothHandsUp;
            feedback.progress = poseHoldDuration <= 0f
                ? 1f
                : Mathf.Clamp01((now - state.bothHandsUpStartTime) / poseHoldDuration);
        }
        else if (state.oneHandUpStartTime >= 0f)
        {
            feedback.state    = PoseFeedbackState.Charging;
            feedback.gesture  = GestureType.OneHandUp;
            feedback.progress = poseHoldDuration <= 0f
                ? 1f
                : Mathf.Clamp01((now - state.oneHandUpStartTime) / poseHoldDuration);
        }
        else
        {
            feedback.state = PoseFeedbackState.Idle;
        }

        PoseEventBus.Instance?.ReportFeedback(feedback);
    }

    public void RemovePerson(int personIndex)
    {
        _personStates.Remove(personIndex);
    }

    // ── 発射 ──
    private void FireGesture(
        int personIndex, GestureType gesture,
        Vector2 screenPos, PersonState state)
    {
        state.lastGestureTime = Time.time;

        if (PoseEventBus.Instance == null)
        {
            ArLog.Warn("[Gesture] PoseEventBus が存在しないためイベントを発行できません");
            return;
        }

        PoseEventBus.Instance.FireGesture(personIndex, gesture, screenPos);

        // gestureCooldown が効くので低頻度。運用上必要なログなので常時出す
        Debug.Log($"[Gesture] Person{personIndex}: {gesture}");
    }
}
