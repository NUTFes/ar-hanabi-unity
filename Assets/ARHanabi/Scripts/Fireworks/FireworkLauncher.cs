using UnityEngine;

// ===== FireworkLauncher =====
// PoseEventBus からジェスチャーイベントを受け取り、花火を打ち上げる
//
// 既存の VFX Prefab 花火（fireworkSmall / fireworkLarge）はそのまま維持。
// isActive な画像花火がある場合は ImageFireworkEffect も同時に発射する。

public class FireworkLauncher : MonoBehaviour
{
    [Header("花火Prefab（既存）")]
    [SerializeField] private GameObject fireworkSmall;  // 片手・ジャンプ用
    [SerializeField] private GameObject fireworkLarge;  // 両手用

    [Header("発射設定（既存）")]
    [SerializeField] private float launchHeightMin = 3f;
    [SerializeField] private float launchHeightMax = 8f;
    [SerializeField] private Camera mainCamera;

    [Header("画像花火設定")]
    [Tooltip("ON: VFX Prefab と同時に画像花火も打ち上げる")]
    [SerializeField] private bool enableImageFirework = true;

    [Tooltip("画像花火の表示スケール（ImageFireworkEffect.imageScale に渡す）")]
    [SerializeField] private float imageFireworkScale = 5f;

    [Header("画像花火 爆発位置")]
    [Tooltip("OFF: VFX と同じ高さ（launchHeightMin/Max）にオフセットを加算\n" +
             "ON : imageFireworkYFixed の固定Y座標で爆発させる")]
    [SerializeField] private bool useFixedImageFireworkY = false;

    [Tooltip("useFixedImageFireworkY が OFF のとき、VFX の Y 座標に加算するオフセット\n" +
             "正の値 = より高く / 負の値 = より低く")]
    [SerializeField] private float imageFireworkYOffset = 0f;

    [Tooltip("useFixedImageFireworkY が ON のとき使う固定 Y 座標")]
    [SerializeField] private float imageFireworkYFixed = 5f;

    [Header("画像花火 シェーダー設定")]
    [Tooltip("Assets/ARHanabi/Shaders/ParticleColor.shader をここにアサインする\n" +
             "アサインしないと色が白になります")]
    [SerializeField] private Shader particleColorShader;

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
                LaunchAt(normalizedPos, fireworkLarge, -1.5f);
                LaunchAt(normalizedPos, fireworkLarge,  1.5f);
                break;

            case GestureType.OneHandUp:
                LaunchAt(normalizedPos, fireworkSmall, 0f);
                break;

            case GestureType.Jump:
                LaunchAt(normalizedPos, fireworkSmall, Random.Range(-3f, 3f));
                break;
        }
    }

    // ── VFX Prefab 打ち上げ ──
    private void LaunchAt(Vector2 normalizedPos, GameObject prefab, float xOffset)
    {
        if (prefab == null)
        {
            Debug.LogError("[Launcher] Prefab が設定されていません");
            return;
        }

        float distanceFromCamera = 5f;
        var screenPos = new Vector3(
            Mathf.Clamp01(normalizedPos.x)      * Screen.width,
            Mathf.Clamp01(1f - normalizedPos.y) * Screen.height,
            distanceFromCamera
        );

        var worldPos = mainCamera.ScreenToWorldPoint(screenPos);
        worldPos.x  += xOffset;
        worldPos.y   = Random.Range(launchHeightMin, launchHeightMax);

        Debug.Log($"[Launcher] VFX 生成: {worldPos}");
        var fw = Instantiate(prefab, worldPos, Quaternion.identity);
        Destroy(fw, 5f);

        // 画像花火を追加発射
        if (enableImageFirework)
            TryLaunchImageFirework(worldPos);
    }

    // ── 画像花火の発射 ──
    private void TryLaunchImageFirework(Vector3 vfxWorldPos)
    {
        var manager = FireworkManager.Instance;
        if (manager == null) return;

        var actives = manager.GetActiveEntries();
        if (actives.Count == 0) return;

        var entry = actives[Random.Range(0, actives.Count)];

        // 爆発Y座標を決定
        var imagePos = vfxWorldPos;
        imagePos.y   = useFixedImageFireworkY
                       ? imageFireworkYFixed
                       : vfxWorldPos.y + imageFireworkYOffset;

        var go = new GameObject($"ImageFW_{entry.displayName}");
        go.transform.position = imagePos;

        var fx          = go.AddComponent<ImageFireworkEffect>();
        fx.imageScale   = imageFireworkScale;

        // シェーダーを注入してから Launch
        fx.SetShader(particleColorShader);
        fx.Launch(entry.particleData);
    }
}