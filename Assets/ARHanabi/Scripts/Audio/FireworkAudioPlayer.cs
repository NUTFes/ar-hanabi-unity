using UnityEngine;

// ===== FireworkAudioPlayer =====
// 花火の打ち上げ効果音を再生するシングルトン
//
// 現在の責務:
//   ・打ち上げジェスチャー検知時に効果音を1回再生する
//   ・両手ジェスチャーなど短時間に複数回呼ばれても二重再生させない

[RequireComponent(typeof(AudioSource))]
public class FireworkAudioPlayer : MonoBehaviour
{
    public static FireworkAudioPlayer Instance { get; private set; }

    // ── Inspector ──
    [Header("再生設定")]
    [Tooltip("打ち上げ時に再生する効果音クリップ")]
    [SerializeField] private AudioClip launchClip;

    [Tooltip("再生音量")]
    [SerializeField, Range(0f, 1f)] private float volume = 0.7f;

    [Tooltip("この秒数以内の連続再生は無視する（両手ジェスチャーの二重再生防止）")]
    [SerializeField] private float minInterval = 0.05f;

    // ── 内部 ──
    private AudioSource _source;
    private float       _lastPlayTime = -Mathf.Infinity;
    private bool        _warnedMissingClip;

    // ── ライフサイクル ──
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        _source              = GetComponent<AudioSource>();
        _source.playOnAwake  = false;
        _source.loop         = false;
        _source.spatialBlend = 0f;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── 再生 ──

    /// <summary>打ち上げ効果音を再生する（短時間の連続呼び出しは1回にまとめる）</summary>
    public void PlayLaunch()
    {
        if (launchClip == null)
        {
            if (!_warnedMissingClip)
            {
                Debug.LogWarning("[FWAudio] launchClip が設定されていません");
                _warnedMissingClip = true;
            }
            return;
        }

        if (Time.time - _lastPlayTime < minInterval) return;
        _lastPlayTime = Time.time;

        _source.PlayOneShot(launchClip, volume);
    }
}
