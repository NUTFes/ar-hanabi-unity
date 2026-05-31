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
        public float prevHipY             = -1f;
        public float lastGestureTime      = -999f;

        public float bothHandsUpStartTime = -1f;
        public float oneHandUpStartTime   = -1f;

        public bool bothHandsFired        = false;
        public bool oneHandFired          = false;
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

        Debug.Log($"[Pose] P{personIndex} shoulderWidth={shoulderWidth:F3} " +
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

        Debug.Log($"[Pose] P{personIndex} " +
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
            Debug.Log($"[Pose] P{personIndex} 両手上げ中: {heldDuration:F2}秒 / {poseHoldDuration}秒");

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
            Debug.Log($"[Pose] P{personIndex} 片手上げ中: {heldDuration:F2}秒 / {poseHoldDuration}秒");

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