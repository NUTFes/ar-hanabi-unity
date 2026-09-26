using UnityEngine;

// ===== UfoSpawner =====
// 一定間隔でUFOを1体ずつ、画面の左右どちらかの端の少し外から出して
// 反対側へ向けて飛ばす。UFO自体の飛行・逃走ロジックは一切持たず、
// 「いつ・どこから・どこへ」を決めて UfoEntity.Launch() を1回呼ぶだけ。
//
// ── InvokeRepeating やコルーチンを使わない理由 ──
//   このプロジェクトの「一定間隔で何かする」処理は、すべて Update() 内で
//   Time.time と次回予定時刻を比較する素朴なタイマーで書かれている
//   （InvokeRepeating・周期コルーチンはコードベース内に1件も無い）。
//   ここでも同じ形に揃え、_nextSpawnTime フィールドだけで完結させる。
//
// ── 生成したUFOの参照を持たない理由 ──
//   FireworkLauncher.SpawnLaunchTrail() と同じ「作って Launch() を呼んだら
//   あとは本人任せ」の使い捨て（fire-and-forget）方式。UfoEntity は
//   Launch() の中で自分の寿命ぶんの Destroy(gameObject, ...) を予約済みなので、
//   このスポナー側でプールや生存リストを管理する必要が無い。
//
// ── シーンに置かなくても動く ──
//   SpaceModeController / CockpitFrameOverlay と同じ方針。
//   [RuntimeInitializeOnLoadMethod] で、シーンに居なければ自動生成する。
//
// ── UfoEnabled が false の間、_nextSpawnTime を進め続ける理由 ──
//   OFFの間もタイマーの基準時刻を放置すると、長時間OFFのあとにONへ戻した瞬間
//   「予定時刻をとっくに過ぎている」状態になり、複数体が一斉湧きしてしまう。
//   毎フレーム Mathf.Max(_nextSpawnTime, Time.time) で今の時刻まで押し出しておけば、
//   ON復帰後は普通に spawnIntervalRange 待ってから1体だけ出る。
public class UfoSpawner : MonoBehaviour
{
    [Header("出現間隔")]
    [Tooltip("次のUFOが出るまでの間隔（秒）。毎回この範囲からランダムに選ぶ")]
    [SerializeField] private Vector2 spawnIntervalRange = new Vector2(8f, 16f);

    [Header("飛行")]
    [Tooltip("画面を端から端まで漂うのにかける時間（秒）。逃走時はUfoEntity側でこれより短くなる")]
    [SerializeField] private Vector2 flightSecondsRange = new Vector2(4f, 9f);

    [Tooltip("カメラ前方距離の基準値。花火の開花距離(5)に揃えて、同じ奥行き感で見えるようにする")]
    [SerializeField] private float baseDistance = 5f;

    [Tooltip("基準距離からの前後ジッター幅。毎回同じ奥行きだと不自然なので少しばらつかせる")]
    [SerializeField] private float distanceJitter = 0.5f;

    [Tooltip("通過する高さ（viewport Y, 0=画面下 1=画面上）の範囲。0/1に寄せすぎると上下端で" +
             "見切れるため、余裕を持って0.2〜0.8程度にしておく")]
    [SerializeField] private Vector2 viewportYRange = new Vector2(0.2f, 0.8f);

    [Tooltip("画面端(0または1)からどれだけ外側から出現/消滅させるか(viewport単位)。" +
             "0だと画面端にいきなり現れて/消えて唐突に見えるため、少し外側に余白を持たせる")]
    [SerializeField] private float offscreenMargin = 0.15f;

    [Tooltip("空飛ぶ円盤(kind=0)が選ばれる確率。残りはエイリアン(kind=1)になる")]
    [SerializeField, Range(0f, 1f)] private float saucerProbability = 0.7f;

    private float _nextSpawnTime;

    private void Update()
    {
        if (SpaceModeController.Instance == null || !SpaceModeController.Instance.UfoEnabled)
        {
            _nextSpawnTime = Mathf.Max(_nextSpawnTime, Time.time);
            return;
        }

        if (Time.time < _nextSpawnTime) return;

        // 先に次回予定を立て直してから生成する（生成処理内で早期returnしても
        // タイマーが壊れないようにするため）
        _nextSpawnTime = Time.time + Random.Range(spawnIntervalRange.x, spawnIntervalRange.y);
        SpawnOne();
    }

    private void SpawnOne()
    {
        var cam = Camera.main;
        if (cam == null) return;

        float distance = baseDistance + Random.Range(-distanceJitter, distanceJitter);
        float v        = Random.Range(viewportYRange.x, viewportYRange.y);

        // 左→右／右→左を半々でランダムに選ぶ
        bool leftToRight = Random.value < 0.5f;
        float uFrom = leftToRight ? -offscreenMargin : 1f + offscreenMargin;
        float uTo   = leftToRight ? 1f + offscreenMargin : -offscreenMargin;

        Vector3 from = cam.ViewportToWorldPoint(new Vector3(uFrom, v, distance));
        Vector3 to   = cam.ViewportToWorldPoint(new Vector3(uTo, v, distance));

        float flightSeconds = Random.Range(flightSecondsRange.x, flightSecondsRange.y);
        int   kind           = Random.value < saucerProbability ? 0 : 1;

        var go     = new GameObject("Ufo");
        var entity = go.AddComponent<UfoEntity>();
        entity.Launch(from, to, flightSeconds, kind);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<UfoSpawner>() != null) return;

        var go = new GameObject("UfoSpawner");
        go.AddComponent<UfoSpawner>();
        DontDestroyOnLoad(go);
    }
}
