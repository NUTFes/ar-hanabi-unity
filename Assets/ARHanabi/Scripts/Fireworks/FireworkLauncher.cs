using UnityEngine;

public class FireworkLauncher : MonoBehaviour
{
    [Header("花火Prefab（既存）")]
    [SerializeField] private GameObject fireworkSmall;  // 片手・ジャンプ用
    [SerializeField] private GameObject fireworkLarge;  // 両手用

    [Header("発射設定（既存）")]
    [SerializeField] private float launchHeightMin = 3f;
    [SerializeField] private float launchHeightMax = 8f;
    [SerializeField] private Camera mainCamera;

    [Header("画像花火設定（追加）")]
    [Tooltip("ON: VFX Prefab と同時に画像花火も打ち上げる")]
    [SerializeField] private bool enableImageFirework = true;

    [Tooltip("画像花火の表示スケール（ImageFireworkEffect.scale に渡す）")]
    [SerializeField] private float imageFireworkScale = 5f;

    [Tooltip("画像花火の爆発速度")]
    [SerializeField] private float imageFireworkSpeed = 0.3f;

    // ── イベント購読 ──
    private void OnEnable()
    {
        if (PoseEventBus.Instance != null)
            PoseEventBus.Instance.OnGestureDetected += OnGestureDetected;
    }

    private void OnDisable()
    {
        if (PoseEventBus.Instance != null)
            PoseEventBus.Instance.OnGestureDetected -= OnGestureDetected;
    }

    // ── ジェスチャー受信 ──
    private void OnGestureDetected(int personIndex, GestureType gesture, Vector2 normalizedPos)
    {
        Debug.Log($"[Launcher] Person{personIndex} {gesture} pos={normalizedPos}");

        switch (gesture)
        {
            case GestureType.BothHandsUp:
                // 両手：豪華な花火を2発
                LaunchAt(normalizedPos, fireworkLarge, -1.5f);
                LaunchAt(normalizedPos, fireworkLarge,  1.5f);
                break;

            case GestureType.OneHandUp:
                // 片手：通常花火を1発
                LaunchAt(normalizedPos, fireworkSmall, 0f);
                break;

            case GestureType.Jump:
                // ジャンプ：ランダム位置
                LaunchAt(normalizedPos, fireworkSmall, Random.Range(-3f, 3f));
                break;
        }
    }

    // ── VFX Prefab 打ち上げ（既存ロジックそのまま）──
    private void LaunchAt(Vector2 normalizedPos, GameObject prefab, float xOffset)
    {
        if (prefab == null)
        {
            Debug.LogError("[Launcher] Prefab が設定されていません");
            return;
        }

        float distanceFromCamera = 5f;
        var screenPos = new Vector3(
            Mathf.Clamp01(normalizedPos.x)       * Screen.width,
            Mathf.Clamp01(1f - normalizedPos.y)  * Screen.height,
            distanceFromCamera
        );

        var worldPos  = mainCamera.ScreenToWorldPoint(screenPos);
        worldPos.x   += xOffset;
        worldPos.y    = Random.Range(launchHeightMin, launchHeightMax);

        Debug.Log($"[Launcher] VFX 生成: {worldPos}");
        var fw = Instantiate(prefab, worldPos, Quaternion.identity);
        Destroy(fw, 5f);

        // ── 画像花火を追加発射 ──
        if (enableImageFirework)
            TryLaunchImageFirework(worldPos);
    }

    // ── 画像花火の発射（追加） ──
    private void TryLaunchImageFirework(Vector3 worldPos)
    {
        var manager = FireworkManager.Instance;
        if (manager == null) return;               // Manager 未配置なら従来動作のみ

        var actives = manager.GetActiveEntries();
        if (actives.Count == 0) return;            // Active エントリなければスキップ

        var entry = actives[Random.Range(0, actives.Count)];

        // ImageFireworkEffect を動的生成
        var go  = new GameObject($"ImageFW_{entry.displayName}");
        go.transform.position = worldPos;

        var ps  = go.AddComponent<ParticleSystem>();  // RequireComponent を満たす
        var fx  = go.AddComponent<ImageFireworkEffect>();

        // パラメータをここで上書き（Inspector 設定を反映）
        fx.scale          = imageFireworkScale;
        fx.explosionSpeed = imageFireworkSpeed;

        fx.Launch(entry.particleData);
    }
}