using System.Collections.Generic;
using UnityEngine;
using Mediapipe.Tasks.Components.Containers;

public class GestureDetector : MonoBehaviour
{
    [Header("ジェスチャー判定設定")]
    [SerializeField] private float handUpThreshold = 0.3f;    // 手が肩より何割上なら「上げた」と判定
    [SerializeField] private float jumpThreshold = 0.05f;      // 腰のY座標の変化量でジャンプ判定
    [SerializeField] private float gestureCooldown = 1.0f;     // 同じジェスチャーの連続発火を防ぐ秒数

    // 人ごとの状態管理
    private class PersonState
    {
        public float prevHipY = -1f;           // 前フレームの腰Y座標
        public float lastGestureTime = -999f;  // 最後にジェスチャーを発火した時刻
        public bool wasOneHandUp = false;
        public bool wasBothHandsUp = false;
    }

    private readonly Dictionary<int, PersonState> _personStates = new();

    // PoseLandmarkDetectorから呼ばれる
    public void ProcessLandmarks(int personIndex, List<NormalizedLandmark> landmarks)
    {
        if (landmarks == null || landmarks.Count < 29) return;

        if (!_personStates.TryGetValue(personIndex, out var state))
        {
            state = new PersonState();
            _personStates[personIndex] = state;
        }

        // MediaPipe Pose のランドマークインデックス
        var leftShoulder  = landmarks[11];
        var rightShoulder = landmarks[12];
        var leftWrist     = landmarks[15];
        var rightWrist    = landmarks[16];
        var leftHip       = landmarks[23];
        var rightHip      = landmarks[24];

        // 画面中心位置（花火の発射基準点）
        var centerX = (leftHip.x + rightHip.x) / 2f;
        var centerY = (leftHip.y + rightHip.y) / 2f;
        var screenPos = new Vector2(centerX, centerY);

        float now = Time.time;
        bool canFire = (now - state.lastGestureTime) > gestureCooldown;

        // ── ジャンプ判定 ──
        float hipY = (leftHip.y + rightHip.y) / 2f;
        if (state.prevHipY > 0f && canFire)
        {
            float deltaY = state.prevHipY - hipY; // Y座標が小さくなる = 上に移動
            if (deltaY > jumpThreshold)
            {
                FireGesture(personIndex, GestureType.Jump, screenPos, state);
            }
        }
        state.prevHipY = hipY;

        // ── 手上げ判定 ──
        float shoulderY = (leftShoulder.y + rightShoulder.y) / 2f;
        bool leftHandUp  = leftWrist.y  < (shoulderY - handUpThreshold);
        bool rightHandUp = rightWrist.y < (shoulderY - handUpThreshold);

        bool bothHandsUp = leftHandUp && rightHandUp;
        bool oneHandUp   = leftHandUp ^ rightHandUp; // XOR: どちらか片方だけ

        // 両手上げ（優先判定）
        if (bothHandsUp && !state.wasBothHandsUp && canFire)
        {
            FireGesture(personIndex, GestureType.BothHandsUp, screenPos, state);
        }
        // 片手上げ（両手上げでないとき）
        else if (oneHandUp && !state.wasOneHandUp && !bothHandsUp && canFire)
        {
            FireGesture(personIndex, GestureType.OneHandUp, screenPos, state);
        }

        state.wasBothHandsUp = bothHandsUp;
        state.wasOneHandUp   = oneHandUp;
    }

    private void FireGesture(int personIndex, GestureType gesture, Vector2 screenPos, PersonState state)
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