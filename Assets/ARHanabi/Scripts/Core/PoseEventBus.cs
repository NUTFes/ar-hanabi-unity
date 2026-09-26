using System;
using UnityEngine;

// ===== PoseEventBus =====
// なぜこのイベントが必要か:
//   これまで PoseEventBus は「ジェスチャーが成立した瞬間」（OnGestureDetected）しか
//   配信していなかった。そのため利用者から見ると、
//     - ポーズ保持中（poseHoldDuration 分だけ待たされている間）
//     - クールダウン中（発射直後、次のジェスチャーを受け付けない間）
//   の状態が一切見えず、「両手を上げたのに反応しない」と感じて展示から離れてしまう。
//   OnPoseFeedback は毎フレームの「保持中／クールダウン中」の進捗を配信し、
//   UI 側でプログレス表示などのフィードバックを出せるようにするためのもの。

public enum GestureType
{
    BothHandsUp,    // 両手を上げる
    OneHandUp,      // 片手を上げる
    Jump            // ジャンプ
}

// ポーズ保持／クールダウンの状態
public enum PoseFeedbackState
{
    Idle,       // 検出されているが何もしていない（progress は意味を持たない）
    Charging,   // ポーズ保持中。progress は 0→1 に進む（1 で発射条件を満たす）
    Cooldown    // 発射直後の待機中。progress は 1→0 に減る（残り待機割合。0 で再度発射可能）
}

// 毎フレーム × 最大5人分流れるため struct にしてある（class にすると
// 1秒あたり最大300個のアロケーションが発生してしまう）
public struct PoseFeedback
{
    public int               trackId;
    public PoseFeedbackState state;
    public float             progress;
    public GestureType       gesture;   // state が Charging のときのみ対象ジェスチャーを表す
}

public class PoseEventBus : MonoBehaviour
{
    public static PoseEventBus Instance { get; private set; }

    // personIndex: 何人目か, gesture: ジェスチャー種類, position: 画面上の位置
    public event Action<int, GestureType, Vector2> OnGestureDetected;

    // 毎フレーム配信される保持中／クールダウン中の進捗フィードバック
    public event Action<PoseFeedback> OnPoseFeedback;

    // トラッキングが途切れて人物がいなくなったことの通知
    public event Action<int> OnPersonLost;

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

    // 毎フレーム呼ばれるためログを出さないこと。in 修飾でコピーを避ける
    public void ReportFeedback(in PoseFeedback feedback)
    {
        OnPoseFeedback?.Invoke(feedback);
    }

    public void ReportPersonLost(int trackId)
    {
        OnPersonLost?.Invoke(trackId);
    }
}