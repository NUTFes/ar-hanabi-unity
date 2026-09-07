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
    [Tooltip("FireworkLauncher reference。現在コードからは未参照。将来の直接呼び出し用に保持\n" +
             "（MainScene.unity でアサイン済みのため削除するとシーンの参照が壊れる）")]
    public FireworkLauncher fireworkLauncher;

    [Header("API")]
    [Tooltip("FireworkApiClient reference")]
    public FireworkApiClient apiClient;

    // ── 内部 ──
    private readonly List<FireworkEntry> _entries     = new();
    private readonly HashSet<long>       _knownApiIds = new();
    private ImageToParticles             _converter;

    // _knownApiIds.Max() を毎回走査しないよう Add 時に最大値をキャッシュする
    private long _maxKnownApiId;

    // 一括処理ループ中だけ true にして OnEntriesChanged の連続発火を抑制する
    private bool _suppressEvents;

    // 管理画面UIが購読して再描画に使う
    public event System.Action OnEntriesChanged;

    public IReadOnlyList<FireworkEntry> Entries => _entries.AsReadOnly();

    /// <summary>取得済みAPI IDの最大値（未取得なら0）</summary>
    public long MaxKnownApiId => _maxKnownApiId;

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

    private void OnDestroy()
    {
        // 変換用の中間 Texture2D を解放する
        _converter?.Dispose();
        _converter = null;

        if (Instance == this) Instance = null;
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

    /// <summary>エントリ削除。保持していた Texture2D も破棄する</summary>
    public void RemoveEntry(FireworkEntry entry)
    {
        if (entry == null) return;

        // 順序が重要:
        //   1. リストから外す  → 再描画時にこのエントリの行は作られない
        //   2. テクスチャを破棄 → 参照元がいなくなってから解放する
        //   3. 再描画を通知     → AdminUIManager が全行を作り直す
        // （AdminUIManager は entry.localTexture を RawImage.texture として渡すため、
        //   破棄より先に再描画してしまうと破棄済みテクスチャを参照する行が残る）
        _entries.Remove(entry);
        ReleaseEntryResources(entry);
        Debug.Log($"[FWManager] Removed: {entry.displayName}");
        RaiseEntriesChanged();
    }

    /// <summary>
    /// エントリが抱えているリソースを解放する。
    /// API画像は1〜4MB級のネイティブメモリを占めるので参照を切るだけでは足りない。
    /// </summary>
    private void ReleaseEntryResources(FireworkEntry entry)
    {
        if (entry == null) return;

        if (entry.localTexture != null)
        {
            DestroyTexture(entry.localTexture);
            entry.localTexture = null;
        }

        entry.particleData = null;
        entry.isConverted  = false;
    }

    // Object.Destroy はエディタの非再生時に使えないので切り替える
    private static void DestroyTexture(Texture2D tex)
    {
        if (tex == null) return;
        if (Application.isPlaying) Destroy(tex);
        else                       DestroyImmediate(tex);
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

    // ── 画像花火の細かさ（変換解像度）──
    //
    // 画像を n×n に縮小してから粒に変換している（ImageToParticles 参照）ので、
    // この n が「画像花火の細かさ」そのものになる。
    // 粒のサイズは ImageFireworkEffect が data.width から導出するため、
    // n を変えても粒が隙間だらけ／団子になることはなく、見た目は自動で追従する。
    //
    // 細かさは1件ごとに持つ（FireworkEntry.resolution）。
    // 絵によって最適な細かさが違う——文字や線画は細かく、面で塗った絵は粗くしたほうが
    // 花火らしく見える——ので、全体で1つの値を共有する形にはしていない。
    // 未設定（0）のエントリは下記の既定値で焼く。

    /// <summary>細かさが未設定のエントリを焼くときに使う既定値（Inspector 由来）</summary>
    public int DefaultResolution => conversionSettings.resolution;

    /// <summary>エントリに実際に使う細かさ。未設定なら既定値。</summary>
    public int ResolutionOf(FireworkEntry entry)
        => entry != null && entry.resolution > 0 ? entry.resolution : DefaultResolution;

    /// <summary>
    /// 1件の細かさを変えて、その場で焼き直す。
    /// 1枚ぶんの変換（最大でも128×128の縮小＋読み出し）なので同期で十分速い。
    /// 全件の焼き直しと違ってフレームを跨ぐ必要がない。
    /// </summary>
    public void SetEntryResolution(FireworkEntry entry, int resolution)
    {
        if (entry == null) return;

        // 極端な値で焼くと粒が1個になったり数万個になったりするので、実用域に丸める
        resolution = Mathf.Clamp(resolution, 8, 128);
        if (entry.resolution == resolution) return;

        entry.resolution = resolution;

        // 未変換のまま細かさだけ変えられた場合は、焼くのは変換時でよい
        if (entry.localTexture == null)
        {
            RaiseEntriesChanged();
            return;
        }

        ConvertEntry(entry);   // ここで RaiseEntriesChanged される
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
        // 細かさは1件ごと。未設定なら既定値で焼き、実際に使った値を書き戻す
        // （以後この行は具体値を表示できる＝「既定」という曖昧な状態が残らない）
        int n = ResolutionOf(entry);
        entry.resolution = n;

        entry.particleData = _converter.Convert(entry.localTexture, n);
        entry.isConverted  = true;
        Debug.Log($"[FWManager] Converted {entry.displayName}: {entry.particleData.particles.Length} pts ({n}x{n})");
        RaiseEntriesChanged();
    }

    /// <summary>
    /// 未変換の全エントリを変換（同期版・後方互換用）。
    /// 件数が多いと変換中アプリが固まるので、可能なら ConvertAllCoroutine を使うこと。
    /// </summary>
    public void ConvertAll()
    {
        var targets = _entries.Where(e => !e.isConverted).ToList();
        if (targets.Count == 0)
        {
            Debug.Log("[FWManager] ConvertAll: nothing to convert");
            return;
        }

        // ConvertEntry ごとに通知するとUIが N 回全行を作り直して O(N²) になるため、
        // ループ中は抑制して最後に1回だけ通知する
        int converted = 0;

        _suppressEvents = true;
        try
        {
            foreach (var e in targets)
            {
                ConvertEntry(e);
                if (e.isConverted) converted++;
            }
        }
        finally
        {
            // 例外が出ても抑制フラグを立てっぱなしにしない
            _suppressEvents = false;
        }

        Debug.Log($"[FWManager] ConvertAll: {converted}/{targets.Count} entries converted");
        RaiseEntriesChanged();
    }

    /// <summary>
    /// 未変換の全エントリを1フレーム1件ずつ変換する（フリーズ回避）。
    /// onDone(convertedCount)
    /// </summary>
    public IEnumerator ConvertAllCoroutine(System.Action<int> onDone)
    {
        var targets = _entries.Where(e => !e.isConverted).ToList();
        if (targets.Count == 0)
        {
            Debug.Log("[FWManager] ConvertAllCoroutine: nothing to convert");
            onDone?.Invoke(0);
            yield break;
        }

        int converted = 0;

        _suppressEvents = true;
        try
        {
            foreach (var e in targets)
            {
                ConvertEntry(e);
                if (e.isConverted) converted++;

                // 1件ごとに1フレーム譲ってメインスレッドを解放する
                yield return null;
            }
        }
        finally
        {
            // 中断・例外時も抑制フラグを確実に戻す
            _suppressEvents = false;
        }

        Debug.Log($"[FWManager] ConvertAllCoroutine: {converted}/{targets.Count} entries converted");
        RaiseEntriesChanged();
        onDone?.Invoke(converted);
    }

    // ── 打ち上げ ──

    /// <summary>Active＆変換済みのエントリ一覧</summary>
    public List<FireworkEntry> GetActiveEntries() =>
        _entries.Where(e => e.isActive && e.isConverted).ToList();

    // ── 以下2つは FireworkLauncher を経由しない低レベルAPI ──
    //
    // 【注意】ユーザーに見せる打ち上げには使わないこと。
    //   FireworkLauncher が設定する imageScale（視錐台から計算する画面占有率）、
    //   scatterMode、シェーダーの注入をどれも行わないため、
    //   実際の打ち上げとは大きさも見た目も違う花火が出る。
    //   さらに開花音も鳴らない。
    //   実際にこれが原因で、Admin画面のテスト打ち上げが本番と食い違っていた。
    //
    //   打ち上げには FireworkLauncher.LaunchTest() / LaunchTestImage() を使う。
    //   ここは動作確認用の最小経路として残してある。

    /// <summary>
    /// ランダムに1件打ち上げる（低レベル。FireworkLauncher の設定は適用されない）
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

    /// <summary>特定エントリを打ち上げる（低レベル。FireworkLauncher の設定は適用されない）</summary>
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

    /// <summary>取得済みAPI IDを記録し、最大値キャッシュを更新する</summary>
    private void RegisterApiId(long id)
    {
        _knownApiIds.Add(id);
        if (id > _maxKnownApiId) _maxKnownApiId = id;
    }

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

        int    added     = 0;
        int    totalDtos = 0;
        string error     = null;
        bool   completed = false;

        // try/finally で囲むのは必須。
        // ここで例外が出たり、コルーチンが外部から停止されたりすると、
        // IsFetching と _suppressEvents が true のまま残る。そうなると
        //   ・IsFetching   → 以後の [更新] が「already fetching」で永久に弾かれる
        //   ・_suppressEvents → RaiseEntriesChanged() が無効化され一覧が二度と更新されない
        // となり、「APIの更新がされなくなった」状態に陥る。
        // （C# のイテレータでは try-catch の中に yield return は書けないが、
        //   try-finally なら書ける。だから catch ではなく completed フラグで判定する）
        try
        {
            // ── 1. 一覧を差分取得 ──
            List<FireworkDto> dtos       = null;
            string            fetchError = null;

            yield return StartCoroutine(apiClient.FetchFireworks(
                MaxKnownApiId,
                result => dtos       = result,
                err    => fetchError = err));

            if (fetchError != null)
            {
                error = fetchError;
                Debug.LogWarning($"[FWManager] Fetch failed: {fetchError}");
            }
            else if (dtos == null || dtos.Count == 0)
            {
                Debug.Log($"[FWManager] No new entries (sinceId={MaxKnownApiId})");
            }
            else
            {
                totalDtos = dtos.Count;

                // ── 2. 1件ずつ 画像DL → 変換 → 有効化 ──
                // ConvertEntry / SetActive がそれぞれ発火するとUIが 2N 回再描画されるため、
                // ループ中は抑制して最後に1回だけ通知する
                _suppressEvents = true;

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
                    RegisterApiId(dto.id);

                    ConvertEntry(entry);
                    SetActive(entry, true);
                    added++;
                }
            }

            completed = true;
        }
        finally
        {
            // 例外・コルーチン停止・正常終了のいずれでも必ず通る
            _suppressEvents = false;
            IsFetching      = false;
        }

        // ── 3. 後片付け ──
        if (!completed)
        {
            // finally は通ったが try を抜けきっていない = 例外か外部停止。
            // ここで onDone を呼ばないと呼び出し側のボタンが無効のまま固まる。
            Debug.LogError("[FWManager] 取得処理が異常終了しました。上の例外ログを確認してください");
            onDone?.Invoke(added, "取得処理が異常終了しました（Console を確認してください）");
            yield break;
        }

        RaiseEntriesChanged();

        if (error == null)
            Debug.Log($"[FWManager] Fetched {added}/{totalDtos} entries (maxKnownApiId={MaxKnownApiId})");

        onDone?.Invoke(added, error);
    }
}