using UnityEngine;

public class FireworkLauncher : MonoBehaviour
{
    [Header("花火Prefab")]
    [SerializeField] private GameObject fireworkSmall;   // 片手・ジャンプ用
    [SerializeField] private GameObject fireworkLarge;   // 両手用（豪華版）

    [Header("発射設定")]
    [SerializeField] private float launchHeightMin = 3f;
    [SerializeField] private float launchHeightMax = 8f;
    [SerializeField] private Camera mainCamera;

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

    private void OnGestureDetected(int personIndex, GestureType gesture, Vector2 normalizedPos)
    {
        Debug.Log($"[Launcher] イベント受信: Person{personIndex} {gesture} pos={normalizedPos}");

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
                // ジャンプ：ランダム位置に通常花火
                var randomOffset = Random.Range(-3f, 3f);
                LaunchAt(normalizedPos, fireworkSmall, randomOffset);
                break;
        }
    }

    private void LaunchAt(Vector2 normalizedPos, GameObject prefab, float xOffset)
    {
        if (prefab == null)
        {
            Debug.LogError("[Launcher] Prefabが設定されていません！");
            return;
        }

        float clampedX = Mathf.Clamp01(normalizedPos.x);
        float clampedY = Mathf.Clamp01(1f - normalizedPos.y);

        // カメラからの距離をカメラのZ位置に合わせて調整
        // Z=-10のカメラから見て前方15の距離に配置
        float distanceFromCamera = 5f;

        var screenPos = new Vector3(
            clampedX * Screen.width,
            clampedY * Screen.height,
            distanceFromCamera
        );

        var worldPos = mainCamera.ScreenToWorldPoint(screenPos);
        worldPos.x += xOffset;
        worldPos.y = Random.Range(launchHeightMin, launchHeightMax);

        Debug.Log($"[Launcher] 花火生成: {worldPos}");
        var fw = Instantiate(prefab, worldPos, Quaternion.identity);
        Destroy(fw, 5f);
    }
}