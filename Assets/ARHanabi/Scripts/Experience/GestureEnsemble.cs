using System.Collections.Generic;
using UnityEngine;

// ===== GestureEnsemble =====
// 純粋C#。複数人が短い時間差で同じジェスチャーをしたことを検出する
// （「一緒にやると特別」の判定ロジック）。
//
// ── 固定長リングバッファにしている理由 ──
//   展示中ずっと動き続けるコンポーネントなのでアロケーションは避けたい。
//   window(既定0.8秒)を超えて古いエントリは次の書き込みで上書きされるだけで、
//   明示的な削除処理を持たない（GCが一切走らない）。
//
// ── 発火を Register の場ではなく TryFire に分けている理由 ──
//   2人目が来た瞬間に即発火すると、0.3秒後に来るはずの3人目を待てない。
//   保留（Pending）を作って collectDelay 秒だけ待ち、その間に加わった参加者を
//   まとめてから1回で発火する。
public class GestureEnsemble
{
    private const float DefaultWindowSeconds       = 0.8f;
    private const float DefaultCollectDelaySeconds = 0.3f;
    private const float SamePersonDistance         = 0.12f; // これより近いと「同一人物の振り直し」とみなす
    private const float RefractorySeconds          = 1.5f;  // 発火に参加した人を少しの間、次の判定から除外する
    private const int   RingCapacity                = 32;

    public readonly struct FireResult
    {
        public readonly int     level;  // 参加人数
        public readonly Vector2 center; // 参加者の関節座標の平均（landmark空間。Quad変換前）
        public FireResult(int level, Vector2 center) { this.level = level; this.center = center; }
    }

    private struct Entry
    {
        public bool        valid;
        public int         trackId;
        public GestureType gesture;
        public Vector2     pos;
        public float       time;
    }

    private readonly Entry[] _ring = new Entry[RingCapacity];
    private int _ringHead;

    // 直前に発火へ参加した trackId → refractory が明ける時刻
    private readonly Dictionary<int, float> _refractoryUntil = new();

    // 現在保留中の参加者（trackId → その人の最新位置）
    private readonly Dictionary<int, Vector2> _pendingParticipants = new();
    private bool  _pending;
    private float _pendingFireAt;

    private readonly float _windowSeconds;
    private readonly float _collectDelaySeconds;
    private readonly bool  _treatAnyHandUpAsSame;

    public GestureEnsemble(float windowSeconds = DefaultWindowSeconds,
                            float collectDelaySeconds = DefaultCollectDelaySeconds,
                            bool treatAnyHandUpAsSame = true)
    {
        _windowSeconds        = windowSeconds > 0f ? windowSeconds : DefaultWindowSeconds;
        _collectDelaySeconds  = collectDelaySeconds >= 0f ? collectDelaySeconds : DefaultCollectDelaySeconds;
        _treatAnyHandUpAsSame = treatAnyHandUpAsSame;
    }

    // 片手／両手は treatAnyHandUpAsSame フラグで同一視できる。ジャンプは常に別扱い
    //（親子で片手／両手が揃わないことが多いための救済。プランの treatAnyHandUpAsSame 参照）
    private bool SameGestureGroup(GestureType a, GestureType b)
    {
        if (a == b) return true;
        if (!_treatAnyHandUpAsSame) return false;

        bool aIsHand = a == GestureType.BothHandsUp || a == GestureType.OneHandUp;
        bool bIsHand = b == GestureType.BothHandsUp || b == GestureType.OneHandUp;
        return aIsHand && bIsHand;
    }

    /// <summary>ジェスチャー1件を登録する。同じ時間帯に別人の同種ジェスチャーが
    /// 見つかれば保留（Pending）を作る／参加者を追加する</summary>
    public void Register(int trackId, GestureType gesture, Vector2 pos, float now)
    {
        // 直前の発火に参加済み（refractory中）なら無視する
        if (_refractoryUntil.TryGetValue(trackId, out var myUntil) && now < myUntil)
            return;

        _ring[_ringHead] = new Entry { valid = true, trackId = trackId, gesture = gesture, pos = pos, time = now };
        _ringHead = (_ringHead + 1) % RingCapacity;

        bool foundPartner = false;

        for (int i = 0; i < RingCapacity; i++)
        {
            var e = _ring[i];
            if (!e.valid || e.trackId == trackId) continue;
            if (now - e.time > _windowSeconds) continue;
            if (!SameGestureGroup(gesture, e.gesture)) continue;
            // 近すぎる位置は「同一人物が振り直された別ID」とみなして除外する
            if (Vector2.Distance(pos, e.pos) < SamePersonDistance) continue;
            if (_refractoryUntil.TryGetValue(e.trackId, out var eUntil) && now < eUntil) continue;

            if (!_pendingParticipants.ContainsKey(e.trackId))
                _pendingParticipants[e.trackId] = e.pos;
            foundPartner = true;
        }

        if (!foundPartner) return;

        _pendingParticipants[trackId] = pos;

        // fireAt は最初に2人目が揃った瞬間だけ立てる。以後に3人目が加わっても
        // 待ち時間を延長しない（無限に後ろへずれるのを防ぐ）
        if (!_pending)
        {
            _pending       = true;
            _pendingFireAt = now + _collectDelaySeconds;
        }
    }

    /// <summary>Update から毎フレーム呼ぶ。発火時刻に達していたら結果を返してクリアする。
    /// 参加者全員に refractory を付ける</summary>
    public bool TryFire(float now, out FireResult result)
    {
        if (_pending && now >= _pendingFireAt)
        {
            int     level = _pendingParticipants.Count;
            Vector2 sum   = Vector2.zero;

            foreach (var kv in _pendingParticipants)
            {
                sum += kv.Value;
                _refractoryUntil[kv.Key] = now + RefractorySeconds;
            }

            result = new FireResult(level, sum / Mathf.Max(1, level));

            _pending = false;
            _pendingParticipants.Clear();
            return true;
        }

        result = default;
        return false;
    }

    /// <summary>新しいセッションの開始時に呼ぶ</summary>
    public void Reset()
    {
        for (int i = 0; i < RingCapacity; i++) _ring[i] = default;
        _ringHead = 0;
        _refractoryUntil.Clear();
        _pending = false;
        _pendingParticipants.Clear();
    }
}
