using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

// ===== FireworkApiClient =====
// 本番API（https://hanabi.nutfes.net）との通信だけを担当するトランスポート層
//
// 責務:
//   ・GET /fireworks で花火一覧を取得し FireworkDto にパースする
//   ・imageUrl から Texture2D をダウンロードする
//
// 責務ではないこと:
//   ・FireworkEntry / ParticleData への変換（FireworkManager 側の担当）
//   ・エントリの保持や重複管理（FireworkManager 側の担当）
//
// 注意:
//   ・該当0件のとき API は "[]" ではなく "null" を返す（Go の nil slice）ためガードしている
//   ・JSONパースは Newtonsoft ではなく JsonUtility + 配列ラッパ方式で行う
//     （Newtonsoft は manifest.json の直接依存ではないため）

// ===== FireworkDto =====
// GET /fireworks のレスポンス1件分（APIのJSONと1:1対応）
[Serializable]
public class FireworkDto
{
    public long   id;
    public bool   isShareable;
    public string imageUrl;
    public string createdAt;
    public string updatedAt;
}

public class FireworkApiClient : MonoBehaviour
{
    // ── Inspector ──
    [Header("API Settings")]
    [Tooltip("APIのベースURL（末尾のスラッシュは自動で除去される）")]
    [SerializeField] private string apiBaseUrl        = "https://hanabi.nutfes.net";

    [Tooltip("共有許可されたもの（isShareable = true）だけを取得対象にする")]
    [SerializeField] private bool   onlyShareable     = true;

    [Tooltip("リクエストのタイムアウト秒数")]
    [SerializeField] private int    requestTimeoutSec = 15;

    // ── JSON配列ラッパ ──
    // JsonUtility はトップレベルの配列を直接パースできないため
    // "{\"items\": <配列本文>}" で包んでからパースする
    [Serializable]
    private class FireworkListWrapper
    {
        public FireworkDto[] items;
    }

    // ── 一覧取得 ──

    /// <summary>
    /// GET /fireworks で花火一覧を取得する。
    /// sinceId より大きい id のものだけを id 昇順で返す（APIは created_at DESC で返すため並べ替える）。
    /// 0件・空レスポンス・"null" レスポンスはエラーではなく空リストとして onSuccess で返す。
    /// </summary>
    public IEnumerator FetchFireworks(long sinceId, Action<List<FireworkDto>> onSuccess, Action<string> onError)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/fireworks";

        using var req = UnityWebRequest.Get(url);
        req.timeout = requestTimeoutSec;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[FWApi] GET {url} failed: {req.responseCode} {req.error}");
            onError?.Invoke($"{req.responseCode} {req.error}");
            yield break;
        }

        var body = req.downloadHandler != null ? req.downloadHandler.text : null;

        // 該当0件のとき Go の nil slice がそのまま "null" として返ってくる
        if (string.IsNullOrWhiteSpace(body) || body.Trim() == "null")
        {
            Debug.Log($"[FWApi] GET {url} -> 0 new (total=0, sinceId={sinceId}) empty body");
            onSuccess?.Invoke(new List<FireworkDto>());
            yield break;
        }

        // yield return は try-catch の中に書けないので、通信は try の外・パースだけ try の中で行う
        List<FireworkDto> list       = null;
        int               totalCount = 0;
        string            parseError = null;

        try
        {
            var wrapped = "{\"items\":" + body + "}";
            var parsed  = JsonUtility.FromJson<FireworkListWrapper>(wrapped);

            if (parsed?.items == null)
            {
                parseError = "JSON parse failed: items is null";
            }
            else
            {
                var all = parsed.items;
                totalCount = all.Length;
                list = all
                    .Where(d => d != null)
                    .Where(d => d.id > sinceId)
                    .Where(d => !onlyShareable || d.isShareable)
                    .OrderBy(d => d.id)
                    .ToList();
            }
        }
        catch (Exception e)
        {
            parseError = $"JSON parse error: {e.Message}";
        }

        if (parseError != null)
        {
            Debug.LogWarning($"[FWApi] {parseError}");
            onError?.Invoke(parseError);
            yield break;
        }

        Debug.Log($"[FWApi] GET {url} -> {list.Count} new (total={totalCount}, sinceId={sinceId})");
        onSuccess?.Invoke(list);
    }

    // ── 画像ダウンロード ──

    /// <summary>
    /// imageUrl から Texture2D をダウンロードする。
    /// ストレージ（hanabi-storage.nutfes.net）は認証不要で image/png を直接返す。
    /// </summary>
    public IEnumerator DownloadTexture(string url, Action<Texture2D> onSuccess, Action<string> onError)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            onError?.Invoke("url is null or empty");
            yield break;
        }

        using var req = UnityWebRequestTexture.GetTexture(url);
        req.timeout = requestTimeoutSec;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[FWApi] DL {url} failed: {req.responseCode} {req.error}");
            onError?.Invoke($"{req.responseCode} {req.error}");
            yield break;
        }

        var tex = DownloadHandlerTexture.GetContent(req);
        Debug.Log($"[FWApi] DL {url} -> {tex.width}x{tex.height}");
        onSuccess?.Invoke(tex);
    }
}
