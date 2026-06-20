using UnityEngine;
using System.Collections.Generic;
using System.Linq;

// ===== FireworkManager =====
// 花火エントリの一元管理シングルトン
//
// 現在の責務:
//   ・Admin画面からのローカル画像登録
//   ・画像 → ParticleData 変換
//   ・isActive なエントリを FireworkLauncher に渡す
//
// 将来の拡張（コメント参照）:
//   ・GET /fireworks でエントリ取得
//   ・PUT /fireworks/:id で isActive を同期

public class FireworkManager : MonoBehaviour
{
    public static FireworkManager Instance { get; private set; }

    // ── Inspector ──
    [Header("Conversion Settings")]
    public ImageToParticlesSettings conversionSettings = new();

    [Header("References")]
    [Tooltip("FireworkLauncher reference")]
    public FireworkLauncher fireworkLauncher;

    // ── 内部 ──
    private readonly List<FireworkEntry> _entries = new();
    private ImageToParticles _converter;

    // 管理画面UIが購読して再描画に使う
    public event System.Action OnEntriesChanged;

    public IReadOnlyList<FireworkEntry> Entries => _entries.AsReadOnly();

    // ── ライフサイクル ──
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _converter = new ImageToParticles(conversionSettings);
    }

    // ── エントリ操作 ──

    /// <summary>ローカル画像からエントリを追加（Admin画面から呼ぶ）</summary>
    public void AddLocalEntry(string displayName, Texture2D texture)
    {
        _entries.Add(new FireworkEntry(displayName, texture));
        Debug.Log($"[FWManager] Added: {displayName}");
        OnEntriesChanged?.Invoke();
    }

    /// <summary>エントリ削除</summary>
    public void RemoveEntry(FireworkEntry entry)
    {
        _entries.Remove(entry);
        OnEntriesChanged?.Invoke();
    }

    /// <summary>Active フラグ切り替え</summary>
    public void SetActive(FireworkEntry entry, bool active)
    {
        entry.isActive = active;
        Debug.Log($"[FWManager] SetActive: {entry.displayName} -> {active} (converted={entry.isConverted})");
        // 将来: active なら PUT /fireworks/:id {isActive: true} を送る
        OnEntriesChanged?.Invoke();
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
        OnEntriesChanged?.Invoke();
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

    // ── 将来: API連携 ──
    // public IEnumerator FetchFromApi()
    // {
    //     using var req = UnityWebRequest.Get($"{apiBaseUrl}/fireworks");
    //     yield return req.SendWebRequest();
    //     // レスポンスをパースして AddLocalEntry or new FireworkEntry(apiId,...) で追加
    //     // 画像は GET /fireworks/:id/image でダウンロードして localTexture に設定
    // }
}