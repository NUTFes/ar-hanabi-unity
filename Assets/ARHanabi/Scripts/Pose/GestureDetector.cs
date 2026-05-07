using System.Collections.Generic;
using UnityEngine;
using Mediapipe.Tasks.Components.Containers;

public class GestureDetector : MonoBehaviour
{
    [Header("ジェスチャー判定設定")]
    [SerializeField] private float handUpThreshold    = 0.15f; // 手が肩より何割上なら「上げた」と判定
    [SerializeField] private float jumpThreshold      = 0.06f; // ジャンプ判定の閾値（大きいほど誤検知しにくい）
    [SerializeField] private float gestureCooldown    = 2.0f;  // 同じジェスチャーの連続発火を防ぐ秒数
    [SerializeField] private float poseHoldDuration   = 0.5f;  // ポーズを何秒維持したら発射するか

    private class PersonState
    {
        public float prevHipY          = -1f;
        public float lastGestureTime   = -999f;

        // ポーズ維持時間の計測
        public float bothHandsUpTimer  = 0f;
        public float oneHandUpTimer    = 0f;

        // 発射済みフラグ（ポーズを解除するまで再発射しない）
        public bool bothHandsFired     = false;
        public bool oneHandFired       = false;
    }

    private readonly Dictionary<int, PersonState> _personStates = new();

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

        var centerX  = (leftHip.x + rightHip.x) / 2f;
        var centerY  = (leftHip.y + rightHip.y) / 2f;
        var screenPos = new Vector2(centerX, centerY);

        float now      = Time.time;
        float dt       = Time.deltaTime;
        bool  canFire  = (now - state.lastGestureTime) > gestureCooldown;

        // ── ジャンプ判定（変化量ベースは維持・閾値を上げて誤検知防止）──
        float hipY = (leftHip.y + rightHip.y) / 2f;
        if (state.prevHipY > 0f && canFire)
        {
            float deltaY = state.prevHipY - hipY;
            if (deltaY > jumpThreshold)
            {
                FireGesture(personIndex, GestureType.Jump, screenPos, state);
            }
        }
        state.prevHipY = hipY;

        // ── 手上げ判定（ポーズ維持ベース）──
        float shoulderY  = (leftShoulder.y + rightShoulder.y) / 2f;
        bool  leftHandUp  = leftWrist.y  < (shoulderY - handUpThreshold);
        bool  rightHandUp = rightWrist.y < (shoulderY - handUpThreshold);

        bool bothHandsUp = leftHandUp && rightHandUp;
        bool oneHandUp   = leftHandUp ^ rightHandUp; // どちらか片方のみ

        // 両手上げ：維持時間を計測
        if (bothHandsUp)
        {
            state.bothHandsUpTimer += dt;
            state.oneHandUpTimer    = 0f; // 両手上げ中は片手タイマーリセット
            state.oneHandFired      = false;

            if (state.bothHandsUpTimer >= poseHoldDuration
                && !state.bothHandsFired && canFire)
            {
                FireGesture(personIndex, GestureType.BothHandsUp, screenPos, state);
                state.bothHandsFired = true;
            }
        }
        else
        {
            // ポーズが解除されたらリセット
            state.bothHandsUpTimer = 0f;
            state.bothHandsFired   = false;
        }

        // 片手上げ：維持時間を計測（両手上げでないとき）
        if (oneHandUp && !bothHandsUp)
        {
            state.oneHandUpTimer += dt;

            if (state.oneHandUpTimer >= poseHoldDuration
                && !state.oneHandFired && canFire)
            {
                FireGesture(personIndex, GestureType.OneHandUp, screenPos, state);
                state.oneHandFired = true;
            }
        }
        else
        {
            state.oneHandUpTimer = 0f;
            state.oneHandFired   = false;
        }
    }

    private void FireGesture(
        int personIndex, GestureType gesture,
        Vector2 screenPos, PersonState state)
    {
        state.lastGestureTime = Time.time;
        PoseEventBus.Instance?.FireGesture(personIndex, gesture, screenPos);
        Debug.Log($"[Gesture] Person{personIndex}: {gesture}");
    }

    public void RemovePerson(int personIndex)
    {
        _personStates.Remove(personIndex);
    }
}