using System;
using UnityEngine;

public enum GestureType
{
    BothHandsUp,    // 両手を上げる
    OneHandUp,      // 片手を上げる
    Jump            // ジャンプ
}

public class PoseEventBus : MonoBehaviour
{
    public static PoseEventBus Instance { get; private set; }

    // personIndex: 何人目か, gesture: ジェスチャー種類, position: 画面上の位置
    public event Action<int, GestureType, Vector2> OnGestureDetected;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public void FireGesture(int personIndex, GestureType gesture, Vector2 screenPos)
    {
        OnGestureDetected?.Invoke(personIndex, gesture, screenPos);
    }
}