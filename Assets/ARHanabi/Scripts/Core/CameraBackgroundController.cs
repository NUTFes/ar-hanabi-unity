using System.Collections;
using UnityEngine;

public class CameraBackgroundController : MonoBehaviour
{
    [SerializeField] private int webcamIndex = 0;
    [SerializeField] private int targetWidth = 640;
    [SerializeField] private int targetHeight = 480;

    private WebCamTexture _webCamTexture;
    private Renderer _renderer;

    private void Start()
    {
        _renderer = GetComponent<Renderer>();
        StartCoroutine(InitializeCamera());
    }

    private IEnumerator InitializeCamera()
    {
        if (WebCamTexture.devices.Length == 0)
        {
            Debug.LogError("カメラが見つかりません");
            yield break;
        }

        _webCamTexture = new WebCamTexture(
            WebCamTexture.devices[webcamIndex].name,
            targetWidth, targetHeight, 30
        );
        _webCamTexture.Play();

        // 映像が実際に届くまで待機
        yield return new WaitUntil(() => _webCamTexture.width > 16);

        // 待機後にテクスチャを設定
        _renderer.material.SetTexture("_MainTex", _webCamTexture);
        Debug.Log($"カメラ映像準備完了: {_webCamTexture.width}x{_webCamTexture.height}");
    }

    public WebCamTexture GetWebCamTexture() => _webCamTexture;

    private void OnDestroy()
    {
        _webCamTexture?.Stop();
    }
}