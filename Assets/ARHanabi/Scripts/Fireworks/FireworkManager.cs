using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

// ===== FireworkManager =====
// 花火エントリの一元管理シングルトン
//
// 責務:
//   ・Admin画面からのローカル画像登録
//   ・APIからの差分取得（GET /fireworks → 画像DL → 変換 → 有効化）
//   ・画像 → ParticleData 変換
//   ・isActive なエントリを FireworkLauncher に渡す
//
// API連携の方針:
//   ・取得済みの id は _knownApiIds に記録し、次回は MaxKnownApiId より新しいものだけ取る
//   ・PUT /fireworks/:id は送らない（ユーザーの共有設定 isShareable を書き換えてしまうため）
//   ・通信そのものは FireworkApiClient に委譲する

public class FireworkManager : MonoBehaviour
{
    public static FireworkManager Instance { get; private set; }

    // ── Inspector ──
    [Header("Conversion Settings")]
    public ImageToParticlesSettings conversionSettings = new();

    [Header("References")]
    [Tooltip("FireworkLauncher reference")]
    public FireworkLauncher fireworkLauncher;

    [Header("API")]
    [Tooltip("FireworkApiClient reference")]
    public FireworkApiClient apiClient;

    // ── 内部 ──
    private readonly List<FireworkEntry> _entries     = new();
    private readonly HashSet<long>       _knownApiIds = new();
    private ImageToParticles             _converter;

    // API取得ループ中だけ true にして OnEntriesChanged の連続発火を抑制する
    private bool _suppressEvents;

    // 管理画面UIが購読して再描画に使う
    public event System.Action OnEntriesChanged;

    public IReadOnlyList<FireworkEntry> Entries => _entries.AsReadOnly();

    /// <summary>取得済みAPI IDの最大値（未取得なら0）</summary>
    public long MaxKnownApiId => _knownApiIds.Count == 0 ? 0 : _knownApiIds.Max();

    /// <summary>API取得中フラグ（多重実行防止用）</summary>
    public bool IsFetching { get; private set; }

    // ── ライフサイクル ──
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _converter = new ImageToParticles(conversionSettings);
    }

    private void OnDisable()
    {
        // コルーチンが中断された場合にフラグが立ちっぱなしになるのを防ぐ
        IsFetching      = false;
        _suppressEvents = false;
    }

    // ── イベント通知 ──

    /// <summary>UI再描画通知。抑制中は発火しない（API取得ループ用）</summary>
    private void RaiseEntriesChanged()
    {
        if (!_suppressEvents) OnEntriesChanged?.Invoke();
    }

    // ── エントリ操作 ──

    /// <summary>ローカル画像からエントリを追加（Admin画面から呼ぶ）</summary>
    public void AddLocalEntry(string displayName, Texture2D texture)
    {
        _entries.Add(new FireworkEntry(displayName, texture));
        Debug.Log($"[FWManager] Added: {displayName}");
        RaiseEntriesChanged();
    }

    /// <summary>エントリ削除</summary>
    public void RemoveEntry(FireworkEntry entry)
    {
        _entries.Remove(entry);
        RaiseEntriesChanged();
    }

    /// <summary>Active フラグ切り替え</summary>
    public void SetActive(FireworkEntry entry, bool active)
    {
        entry.isActive = active;
        Debug.Log($"[FWManager] SetActive: {entry.displayName} -> {active} (converted={entry.isConverted})");
        // PUT /fireworks/:id は送らない（ユーザーの共有設定 isShareable を書き換えてしまうため）
        // isActive はこのアプリ内だけのローカルな表示制御として扱う
        RaiseEntriesChanged();
    }

    // ── 変換 ──

    /// <summary>1件変換（Admin画面の「変換」ボタンから）</summary>
    public void ConvertEntry(FireworkEntry entry)
    {
        if (entry.localTexture == null)
        {
            Debug.LogWarning($"[FWManager] No texture: {entry.displayName}");
            return;
        }
        entry.particleData = _converter.Convert(entry.localTexture);
        entry.isConverted  = true;
        Debug.Log($"[FWManager] Converted {entry.displayName}: {entry.particleData.particles.Length} pts");
        RaiseEntriesChanged();
    }

    /// <summary>未変換の全エントリを変換</summary>
    public void ConvertAll()
    {
        foreach (var e in _entries.Where(e => !e.isConverted))
            ConvertEntry(e);
    }

    // ── 打ち上げ ──

    /// <summary>Active＆変換済みのエントリ一覧</summary>
    public List<FireworkEntry> GetActiveEntries() =>
        _entries.Where(e => e.isActive && e.isConverted).ToList();

    /// <summary>
    /// ランダムに1件打ち上げる
    /// FireworkLauncher から GestureType に応じて呼ばれる
    /// </summary>
    public void LaunchRandom(Vector3 worldPosition)
    {
        var actives = GetActiveEntries();
        if (actives.Count == 0)
        {
            Debug.LogWarning("[FWManager] No active & converted entries. Count=" + _entries.Count + " active=" + _entries.Count(e=>e.isActive) + " converted=" + _entries.Count(e=>e.isConverted));
            return;
        }
        var entry = actives[Random.Range(0, actives.Count)];
        LaunchEntry(entry, worldPosition);
    }

    /// <summary>特定エントリを打ち上げる</summary>
    public void LaunchEntry(FireworkEntry entry, Vector3 worldPosition)
    {
        if (!entry.isConverted)
        {
            Debug.LogWarning($"[FWManager] Not converted: {entry.displayName}");
            return;
        }

        // ImageFireworkEffect を動的生成して打ち上げる
        var go  = new GameObject($"ImageFirework_{entry.displayName}");
        go.transform.position = worldPosition;
        var fx  = go.AddComponent<ImageFireworkEffect>();
        fx.Launch(entry.particleData);

        Debug.Log($"[FWManager] Launch: {entry.displayName} @ {worldPosition}");
    }

    // ── API連携 ──

    /// <summary>
    /// APIから差分取得 → 画像DL → ParticleData変換 → 有効化 までを一括で行う。
    /// onDone(addedCount, errorMessageOrNull)
    /// </summary>
    public IEnumerator FetchNewEntriesFromApi(System.Action<int, string> onDone)
    {
        if (IsFetching)
        {
            onDone?.Invoke(0, "already fetching");
            yield break;
        }

        if (apiClient == null)
        {
            onDone?.Invoke(0, "FireworkApiClient is not assigned");
            yield break;
        }

        IsFetching = true;

        // ── 1. 一覧を差分取得 ──
        List<FireworkDto> dtos       = null;
        string            fetchError = null;

        yield return StartCoroutine(apiClient.FetchFireworks(
            MaxKnownApiId,
            result => dtos       = result,
            err    => fetchError = err));

        if (fetchError != null)
        {
            IsFetching = false;
            Debug.LogWarning($"[FWManager] Fetch failed: {fetchError}");
            onDone?.Invoke(0, fetchError);
            yield break;
        }

        if (dtos == null || dtos.Count == 0)
        {
            IsFetching = false;
            Debug.Log($"[FWManager] No new entries (sinceId={MaxKnownApiId})");
            onDone?.Invoke(0, null);
            yield break;
        }

        // ── 2. 1件ずつ 画像DL → 変換 → 有効化 ──
        // ConvertEntry / SetActive がそれぞれ発火するとUIが 2N 回再描画されるため、
        // ループ中は抑制して最後に1回だけ通知する
        _suppressEvents = true;
        int added = 0;

        foreach (var dto in dtos)
        {
            Texture2D tex     = null;
            string    dlError = null;

            yield return StartCoroutine(apiClient.DownloadTexture(
                dto.imageUrl,
                t   => tex     = t,
                err => dlError = err));

            if (tex == null)
            {
                // 1件の失敗で全体を止めない。_knownApiIds に入れないので次回リトライされる
                Debug.LogWarning($"[FWManager] Skip id={dto.id}: image download failed ({dlError})");
                continue;
            }

            var entry = new FireworkEntry((int)dto.id, $"#{dto.id}", dto.imageUrl, dto.isShareable);
            entry.localTexture = tex;
            entry.createdAt    = dto.createdAt;
            _entries.Add(entry);
            _knownApiIds.Add(dto.id);

            ConvertEntry(entry);
            SetActive(entry, true);
            added++;
        }

        // ── 3. 後片付け（抑制解除して1回だけ通知）──
        _suppressEvents = false;
        IsFetching      = false;
        RaiseEntriesChanged();

        Debug.Log($"[FWManager] Fetched {added}/{dtos.Count} entries (maxKnownApiId={MaxKnownApiId})");
        onDone?.Invoke(added, null);
    }
}