using System;
using System.Collections.Generic;
using UnityEngine;

// ===== PoseTracker =====
// 「腰の位置の最近傍マッチ」で人物の同一性を保った ID（trackId）を発行する。
//
// ── なぜ検出リストの添字では駄目なのか ──
//   MediaPipe PoseLandmarker が返す poseLandmarks は「検出された人のリスト」でしかなく、
//   その並び順が前フレームと同じ人物を指す保証はどこにもない。
//   numPoses=5 の LIVE_STREAM 推論では、人が横切る・一瞬検出が落ちる・信頼度の順番が
//   入れ替わる、といっただけで添字がシャッフルされる。
//   添字をそのまま personIndex として使うと、
//     - SkeletonRenderer の色分け（personIndex % 5）が別人に飛び移る
//     - GestureDetector の PersonState（prevHipY / ポーズ保持時間 / クールダウン）が
//       別人の状態を引き継ぎ、誤発火や発火漏れが起きる
//   という形で破綻する。3人目が抜けたら 4人目・5人目の添字が繰り上がる、という
//   ごく普通の出来事でも起きるので「稀なケース」ではない。
//
//   そこで検出ごとに腰中心（landmark 23/24 の中点・正規化座標）を受け取り、
//   前フレームまでのトラックのうち最も近いものへ結び付けて、安定した trackId を返す。
//   人物は 1フレーム（約16〜33ms）で画面幅の十数％も移動しないため、
//   maxMatchDistance を超える飛びは「別人が現れた」と解釈して新しい ID を発行する。
//
// ── MonoBehaviour にしていない理由 ──
//   Inspector に出したいのは設定値だけで、Update も GameObject も必要ない。
//   ImageToParticles と同じ「[Serializable] 設定クラス ＋ 純粋C#クラス」の形にして、
//   利用側（PoseLandmarkDetector）が設定を [SerializeField] で持ち、
//   初期化時に new して所有する。
//
// ── 毎フレームのアロケーションはゼロ ──
//   最大5人 × 60fps で BeginFrame / Resolve / EndFrame が回るため、
//   辞書・リストは全てフィールドで使い回し、LINQ もラムダも使わない。
//   「このフレームで既にマッチしたか」もフレーム番号の比較で表すことで、
//   フラグを毎フレーム書き戻すループ自体を無くしている。

[Serializable]
public class PoseTrackerSettings
{
    [Tooltip("同一人物とみなす1フレームあたりの最大移動量（正規化座標）。超えたら別人扱い")]
    public float maxMatchDistance = 0.15f;

    [Tooltip("この秒数見えなかったトラックを破棄する")]
    public float trackTimeout = 0.5f;
}

public class PoseTracker
{
    // ── トラック1件の状態 ──
    // class ではなく struct にしてある。辞書の値として値コピーで持てるので、
    // 新しい人が現れるたびにヒープ確保が走らない（更新は _tracks[id] = t で書き戻す）。
    private struct Track
    {
        public Vector2 position;          // 最後に観測した腰中心（正規化座標）
        public float   lastSeenTime;      // 最後に観測した時刻（Time.time）
        public int     matchedFrame;      // 最後にマッチしたフレーム番号
    }

    private readonly PoseTrackerSettings _s;

    // trackId → トラック状態。毎フレーム作り直さず、増減があったときだけ触る
    private readonly Dictionary<int, Track> _tracks = new();

    // EndFrame で「列挙しながら削除」をしないための一時リスト（毎フレーム確保しない）。
    // SkeletonRenderer._timedOutBuffer と同じ役割。
    private readonly List<int> _timedOutBuffer = new();

    // 次に発行する trackId。0 から始まる単調増加で、破棄した ID は使い回さない
    // （使い回すと GestureDetector 側に残った状態と混ざる恐れがある）
    private int _nextTrackId;

    // 現在のフレーム番号。BeginFrame で 1 ずつ増える。
    // Track.matchedFrame == _frameNumber が「このフレームで既にマッチ済み」を意味する。
    // 既定値 0 と衝突しないよう、最初の BeginFrame で 1 になるようにしている。
    private int _frameNumber;

    // このフレームの基準時刻。Time.time を使う（既存コードと揃えるため。
    // Time.realtimeSinceStartup は一時停止やタイムスケールの影響が異なる）
    private float _frameTime;

    private const int _NoTrack = -1;

    public PoseTracker(PoseTrackerSettings settings)
    {
        // Inspector 未設定でも既定値で動くようにしておく
        _s = settings ?? new PoseTrackerSettings();
    }

    // ── フレーム制御 ──

    /// <summary>フレームの開始。Resolve を呼ぶ前に必ず1回呼ぶ</summary>
    public void BeginFrame()
    {
        _frameNumber++;
        _frameTime = Time.time;
    }

    /// <summary>検出1件ごとに呼び、安定した trackId を得る</summary>
    public int Resolve(Vector2 hipCenter)
    {
        int   bestId  = _NoTrack;
        float bestSqr = float.MaxValue;

        // 平方距離で比較して Sqrt を省く
        float maxSqr = _s.maxMatchDistance * _s.maxMatchDistance;

        // このフレームで未マッチのトラックのうち最も近いものを探す。
        // Dictionary の foreach は構造体列挙子なのでアロケーションは発生しない。
        foreach (var kvp in _tracks)
        {
            // 1トラックにつき1検出まで。既にこのフレームで別の検出に取られたトラックは
            // 候補から外す（同じトラックに2人が吸着すると ID が重複してしまう）
            if (kvp.Value.matchedFrame == _frameNumber) continue;

            float sqr = (kvp.Value.position - hipCenter).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                bestId  = kvp.Key;
            }
        }

        // 最近傍が遠すぎる場合は同一人物とみなさない
        if (bestId != _NoTrack && bestSqr <= maxSqr)
        {
            var track = _tracks[bestId];
            track.position     = hipCenter;
            track.lastSeenTime = _frameTime;
            track.matchedFrame = _frameNumber;
            _tracks[bestId]    = track;   // 既存キーへの代入なので再確保は起きない

            return bestId;
        }

        return IssueTrack(hipCenter);
    }

    /// <summary>フレームの終了。タイムアウトしたトラックを onTrackLost で通知して破棄する</summary>
    public void EndFrame(Action<int> onTrackLost)
    {
        // 辞書を列挙しながら Remove すると列挙子が壊れるので、
        // 破棄対象を事前確保したリストに集めてから消す
        _timedOutBuffer.Clear();

        foreach (var kvp in _tracks)
        {
            if (_frameTime - kvp.Value.lastSeenTime > _s.trackTimeout)
                _timedOutBuffer.Add(kvp.Key);
        }

        for (int i = 0; i < _timedOutBuffer.Count; i++)
        {
            int trackId = _timedOutBuffer[i];

            // 破棄前に通知する（受け側が状態を後始末できるように）
            onTrackLost?.Invoke(trackId);
            _tracks.Remove(trackId);

            // 人が去った瞬間だけなので低頻度。運用上追いたいログなので常時出す
            Debug.Log($"[PoseTracker] トラック破棄: id={trackId} " +
                      $"（{_s.trackTimeout}秒 未検出 / 残り{_tracks.Count}人）");
        }
    }

    // ── トラックの発行 ──
    private int IssueTrack(Vector2 hipCenter)
    {
        int trackId = _nextTrackId++;

        _tracks[trackId] = new Track
        {
            position     = hipCenter,
            lastSeenTime = _frameTime,
            // 発行したフレームでは既にマッチ済み扱いにする。
            // こうしないと同じフレームの後続の検出がこの新品トラックに吸い付いてしまう
            matchedFrame = _frameNumber,
        };

        // 人が現れた瞬間だけなので低頻度。運用上追いたいログなので常時出す
        Debug.Log($"[PoseTracker] トラック発行: id={trackId} " +
                  $"hip=({hipCenter.x:F3}, {hipCenter.y:F3}) / 追跡中{_tracks.Count}人");

        return trackId;
    }
}
