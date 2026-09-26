using UnityEngine;
using UnityEngine.InputSystem;

public class FireworkTest : MonoBehaviour
{
    [Header("花火Prefab")]
    [SerializeField] private GameObject fireworkPrefab;

    [Header("発射設定")]
    [SerializeField] private float launchInterval = 2f;
    [SerializeField] private float launchHeight = 8f;
    [SerializeField] private float spawnRangeX = 4f;

    private float _timer;

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer >= launchInterval)
        {
            _timer = 0f;
            LaunchFirework();
        }

        // スペースキーで手動発射（新Input System）
        if (Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            LaunchFirework();
        }
    }

    private void LaunchFirework()
    {
        var spawnPos = new Vector3(
            Random.Range(-spawnRangeX, spawnRangeX),
            0f,
            0f
        );

        var fw = Instantiate(fireworkPrefab, spawnPos, Quaternion.identity);
        Destroy(fw, 5f);
    }
}