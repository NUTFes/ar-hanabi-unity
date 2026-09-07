using System.Collections.Generic;
using UnityEngine;

// ===== ComboTracker =====
// 純粋C#。人ごとの連続ジェスチャー回数（コンボ）を数える。
// PoseTracker と同じ形式（MonoBehaviourではなく、呼び出し側が now を渡す）。
//
// ジェスチャーの種類は問わず数える（片手→両手→ジャンプでも育つ）。
// 子どもは種類を選ばず連打するため、種類を区別すると「育たない」体験になってしまう。
//
// ── 孤児（Orphan）を即削除しない理由 ──
//   PoseTracker はトラッキングが一瞬途切れると新しい trackId を発行することがある
//  （振り直し）。これをそのまま「別人が現れた」として扱うと、連打の途中でコンボが
//   0に戻ってしまう。OnPersonLost が来ても即削除せず orphanedAt を記録しておき、
//   近い時刻・近い位置に新しい trackId が現れたら、そのカウントを引き継ぐ（Adopt）。
public class ComboTracker
{
    private const float DefaultWindowSeconds = 2.0f;
    private const int   DefaultFinisherCount = 5;
    private const float OrphanAdoptSeconds   = 1.5f;
    private const float OrphanAdoptDistance  = 0.12f;

    private class Entry
    {
        public int     count;
        public float   lastTime   = -999f;
        public Vector2 lastPos;
        public float   orphanedAt = -1f; // -1 = 孤児ではない（現在追跡中）
    }

    private readonly float _windowSeconds;
    private readonly int   _finisherCount;
    private readonly Dictionary<int, Entry> _entries = new();

    // Sweep 用の使い回しバッファ（毎フレーム確保しない）
    private readonly List<int> _sweepBuffer = new();

    public ComboTracker(float windowSeconds = DefaultWindowSeconds, int finisherCount = DefaultFinisherCount)
    {
        _windowSeconds = windowSeconds > 0f ? windowSeconds : DefaultWindowSeconds;
        _finisherCount = finisherCount > 0 ? finisherCount : DefaultFinisherCount;
    }

    /// <summary>ジェスチャー1件を登録し、その段階（1〜finisherCount）を返す。
    /// finisherCount に達したら 0 から数え直す（次のフィニッシャーへ向けてまた育つ）</summary>
    public int Register(int trackId, Vector2 pos, float now)
    {
        var entry = ResolveEntry(trackId, pos, now);

        if (now - entry.lastTime > _windowSeconds)
            entry.count = 0;

        entry.count++;
        entry.lastTime   = now;
        entry.lastPos    = pos;
        entry.orphanedAt = -1f;

        if (entry.count >= _finisherCount)
        {
            entry.count = 0;
            return _finisherCount;
        }

        return entry.count;
    }

    /// <summary>OnPersonLost で呼ぶ。即削除せず、孤児として少しの間だけ保持する</summary>
    public void Orphan(int trackId, float now)
    {
        if (_entries.TryGetValue(trackId, out var entry))
            entry.orphanedAt = now;
    }

    /// <summary>毎フレーム呼ぶ。孤児のまま OrphanAdoptSeconds を超えた（誰にも
    /// 引き継がれなかった）エントリを削除する。展示を通してのメモリ増加を防ぐための保険</summary>
    public void Sweep(float now)
    {
        _sweepBuffer.Clear();
        foreach (var kvp in _entries)
        {
            var e = kvp.Value;
            if (e.orphanedAt >= 0f && now - e.orphanedAt > OrphanAdoptSeconds)
                _sweepBuffer.Add(kvp.Key);
        }
        for (int i = 0; i < _sweepBuffer.Count; i++)
            _entries.Remove(_sweepBuffer[i]);
    }

    // 既存の trackId ならそれを返す。無ければ、直近の孤児のうち位置が最も近いものを
    // 引き継ぐ（Adopt）。それも無ければ新規に作る
    private Entry ResolveEntry(int trackId, Vector2 pos, float now)
    {
        if (_entries.TryGetValue(trackId, out var existing))
            return existing;

        int   adoptFrom = -1;
        float bestDist  = OrphanAdoptDistance;

        foreach (var kvp in _entries)
        {
            var e = kvp.Value;
            if (e.orphanedAt < 0f) continue;
            if (now - e.orphanedAt > OrphanAdoptSeconds) continue;

            float dist = Vector2.Distance(e.lastPos, pos);
            if (dist <= bestDist)
            {
                bestDist  = dist;
                adoptFrom = kvp.Key;
            }
        }

        Entry entry;
        if (adoptFrom >= 0)
        {
            entry = _entries[adoptFrom];
            _entries.Remove(adoptFrom);
        }
        else
        {
            entry = new Entry();
        }

        _entries[trackId] = entry;
        return entry;
    }

    /// <summary>新しいセッションの開始時に呼ぶ（前の来場者のコンボを持ち越さない）</summary>
    public void Reset() => _entries.Clear();
}
