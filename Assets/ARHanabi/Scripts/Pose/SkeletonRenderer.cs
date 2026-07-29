using System.Collections.Generic;
using UnityEngine;
using Mediapipe.Tasks.Components.Containers;

// ===== SkeletonRenderer =====
// MediaPipe Pose のランドマークを LineRenderer でスケルトン描画する。
//
// ── 位置合わせ（backgroundQuad を使う理由）──
//   landmark を ScreenToWorldPoint で「画面全体」に張ると、カメラ映像とズレる。
//   映像は CameraBackground の Quad に貼られており、その Quad が画面上のどこに
//   どれだけの大きさで映るかは カメラのFOV・Quadまでの距離・Quadのスケール・
//   カメラのY位置・そして画面のアスペクト比 で決まる。画面全体とは一致しない。
//
//   実測（Quad: pos(0,0,5) scale(42,24) / Camera: pos(0,1,-10) 垂直FOV60°）:
//     カメラ〜Quad は 15 units、そこでの視錐台の高さは 2*15*tan30° = 17.32。
//     Quad は 24 あるため映像は画面に収まらず中央だけが見えている。
//     映像が占める画面範囲は 縦 -0.251〜1.135 / 横(16:9) -0.182〜1.182。
//     一方スケルトンは 0〜1 に張られるので、約72%に縮んだうえ縦に約6%ずれる。
//     しかも横は「高さ×アスペクト比」なので、ウィンドウの縦横比を変えると
//     ズレ量が変わる（16:9 で映像の73%、4:3 で55%）。
//
//   そこで landmark を Quad の面上に直接マッピングする。こうすれば画面サイズ・
//   アスペクト比・FOV・カメラ位置が何であっても映像と必ず一致する。
//   backgroundQuad 未指定時は従来の画面全体マッピングにフォールバックする。
//
// マテリアルの扱い:
//   LineRenderer.material に代入すると Unity はマテリアルを「インスタンス複製」する。
//   接続線ごとに複製すると 16本 × 最大5人 = 最大80個のマテリアルが作られ、
//   バッチングも効かず DrawCall が 80 に膨らむ上、破棄もされずリークする。
//   そのため sharedMaterial を使い、色分け用のマテリアルは「人ごとに1個」だけ
//   自前で生成して使い回す（最大5個）。生成したものは OnDestroy で破棄する。
//
// 詳細ログを見たい場合は ArLog.cs 冒頭の手順で AR_VERBOSE_LOG を定義する。

public class SkeletonRenderer : MonoBehaviour
{
    [Header("設定")]
    [Tooltip("スクリーン座標→ワールド座標変換に使うカメラ")]
    [SerializeField] private Camera mainCamera;

    [Header("位置合わせ")]
    [Tooltip("カメラ映像を貼っている Quad（CameraBackground）をアサインする。\n" +
             "アサインするとスケルトンをこの Quad の面上に描くため、画面サイズや\n" +
             "アスペクト比・FOV が変わっても映像とずれない。\n" +
             "空のままだと従来どおり画面全体へのマッピングになり、映像とずれる。")]
    [SerializeField] private Transform backgroundQuad;

    [Tooltip("Quad 面からカメラ側へどれだけ手前に描くか（Zファイト回避）")]
    [SerializeField] private float quadOffset = 0.05f;

    [Tooltip("【backgroundQuad が未設定のときだけ使う】\n" +
             "スケルトンを描画するカメラからの距離")]
    [SerializeField] private float drawDistance = 9f;

    [Tooltip("線の太さ")]
    [SerializeField] private float lineWidth = 0.02f;

    [Tooltip("線に使うマテリアル。複製せず sharedMaterial として共有する")]
    [SerializeField] private Material lineMaterial;

    // ── ランドマーク接続定義 ──
    // MediaPipe Pose ランドマーク接続定義（16本）
    private static readonly (int, int)[] _connections = new[]
    {
        // 上半身
        (11, 12), (11, 13), (13, 15),
        (12, 14), (14, 16),
        // 体幹
        (11, 23), (12, 24), (23, 24),
        // 下半身
        (23, 25), (25, 27), (27, 29), (29, 31),
        (24, 26), (26, 28), (28, 30), (30, 32),
    };

    // ── 人ごとの色 ──
    private static readonly Color[] _personColors =
    {
        new Color(0.2f, 0.8f, 1.0f),  // 水色
        new Color(1.0f, 0.6f, 0.2f),  // オレンジ
        new Color(0.4f, 1.0f, 0.4f),  // 緑
        new Color(1.0f, 0.4f, 0.8f),  // ピンク
        new Color(1.0f, 1.0f, 0.4f),  // 黄色
    };

    private static readonly int _PropBaseColor = Shader.PropertyToID("_BaseColor");
    private static readonly int _PropColor     = Shader.PropertyToID("_Color");

    // ── 内部状態 ──
    // 人ごとの LineRenderer を管理
    private readonly Dictionary<int, List<LineRenderer>> _personLines = new();
    private readonly Dictionary<int, float> _lastUpdateTime = new();
    private readonly Dictionary<int, bool>  _personVisible  = new();

    // 人ごとに1個だけ生成する色分けマテリアル（自前生成なので OnDestroy で破棄する）
    private readonly Dictionary<int, Material> _personMaterials = new();

    // タイムアウト判定でループ中に辞書を触らないための一時リスト（毎フレーム確保しない）
    private readonly List<int> _timedOutBuffer = new();

    private const float _TimeoutSeconds = 0.5f; // この秒数更新がなければ非表示

    // ── ライフサイクル ──
    private void Update()
    {
        // タイムアウトした人のスケルトンを非表示
        var now = Time.time;

        _timedOutBuffer.Clear();
        foreach (var kvp in _lastUpdateTime)
        {
            if (now - kvp.Value > _TimeoutSeconds) _timedOutBuffer.Add(kvp.Key);
        }

        for (int i = 0; i < _timedOutBuffer.Count; i++)
        {
            SetPersonVisible(_timedOutBuffer[i], false);
        }
    }

    private void OnDestroy()
    {
        foreach (var lines in _personLines.Values)
        {
            foreach (var lr in lines)
            {
                if (lr != null) Destroy(lr.gameObject);
            }
        }
        _personLines.Clear();

        // 自前生成したマテリアルを破棄（これを忘れるとリークする）
        foreach (var mat in _personMaterials.Values)
        {
            if (mat != null) Destroy(mat);
        }
        _personMaterials.Clear();
    }

    // ── Public API ──
    public void UpdateSkeleton(int personIndex, List<NormalizedLandmark> landmarks)
    {
        // _connections が参照する最大インデックスは32 → 33点必要
        const int requiredLandmarkCount = 33;
        if (landmarks == null || landmarks.Count < requiredLandmarkCount)
        {
            // ランドマーク数が不足している場合は描画をスキップ（前回の姿勢のまま表示維持）
            return;
        }

        if (mainCamera == null)
        {
            ArLog.Verbose("[Skeleton] mainCamera が未設定のため描画をスキップします");
            return;
        }

        _lastUpdateTime[personIndex] = Time.time;

        // 初回のみ LineRenderer を生成
        if (!_personLines.TryGetValue(personIndex, out var lines))
        {
            lines = CreateLineRenderers(personIndex);
        }

        SetPersonVisible(personIndex, true);

        for (int i = 0; i < _connections.Length; i++)
        {
            var (a, b) = _connections[i];

            // 念のための二重チェック（将来 _connections が変更されても安全に）
            if (a >= landmarks.Count || b >= landmarks.Count) continue;

            lines[i].SetPosition(0, LandmarkToWorld(landmarks[a]));
            lines[i].SetPosition(1, LandmarkToWorld(landmarks[b]));
        }
    }

    // ── 座標変換 ──
    private Vector3 LandmarkToWorld(NormalizedLandmark landmark)
    {
        // x も y も反転しない。WebCamTexture.GetPixels32() が下の行から並んだ配列を
        // 返すため、MediaPipe の y は既に表示映像に対して「下が 0」になっている。
        // 詳しい理由は PoseCoordinateUtil の冒頭コメントを参照。

        // Quad があるなら面上に直接置く（画面サイズやアスペクト比に影響されない）
        if (backgroundQuad != null)
            return LandmarkToQuadPoint(landmark.x, landmark.y);

        // フォールバック: 画面全体へのマッピング。映像とズレるので非推奨
        return PoseCoordinateUtil.ToWorldPoint(mainCamera, landmark.x, landmark.y, drawDistance);
    }

    // ── Quad 面上へのマッピング ──
    // Unity 内蔵 Quad メッシュはローカル 1x1・原点中心・+X が右 / +Y が上で、
    // テクスチャの uv=(0,0) がローカル (-0.5,-0.5) に対応する。
    // localScale がそのまま表示サイズになるので、(u-0.5, v-0.5) を TransformPoint
    // すれば uv=(u,v) の位置にある面上の点がそのまま得られる。
    // Transform を経由するので、Quad を動かしても拡大しても自動で追従する。
    private Vector3 LandmarkToQuadPoint(float u, float v)
    {
        var point = backgroundQuad.TransformPoint(
            new Vector3(Mathf.Clamp01(u) - 0.5f, Mathf.Clamp01(v) - 0.5f, 0f));

        // Quad と同じ深さだと Z ファイトで線が消えるのでカメラ側へ少し寄せる
        if (mainCamera != null && quadOffset != 0f)
        {
            var toCamera = mainCamera.transform.position - point;
            if (toCamera.sqrMagnitude > 0.0001f)
                point += toCamera.normalized * quadOffset;
        }

        return point;
    }

    // ── LineRenderer 生成 ──
    private List<LineRenderer> CreateLineRenderers(int personIndex)
    {
        var lines    = new List<LineRenderer>(_connections.Length);
        var color    = GetPersonColor(personIndex);
        var material = GetPersonMaterial(personIndex, color, out bool colorIsInMaterial);

        // マテリアル側で色を付けられた場合、頂点カラーは白にしておく
        // （頂点カラー対応シェーダーだと二重に乗算されて暗くなるため）
        var vertexColor = colorIsInMaterial ? Color.white : color;

        foreach (var _ in _connections)
        {
            var go = new GameObject($"SkeletonLine_P{personIndex}");
            go.transform.SetParent(transform);

            var lr = go.AddComponent<LineRenderer>();
            // material ではなく sharedMaterial（material はインスタンスを複製してしまう）
            lr.sharedMaterial = material;
            lr.startColor     = vertexColor;
            lr.endColor       = vertexColor;
            lr.startWidth     = lineWidth;
            lr.endWidth       = lineWidth;
            lr.positionCount  = 2;
            lr.useWorldSpace  = true;

            lines.Add(lr);
        }

        _personLines[personIndex] = lines;
        return lines;
    }

    // ── マテリアル管理 ──
    // 人ごとに1個だけマテリアルを生成して使い回す。
    // colorIsInMaterial: マテリアルのカラープロパティで色を付けられたかどうか
    private Material GetPersonMaterial(int personIndex, Color color, out bool colorIsInMaterial)
    {
        int slot = personIndex % _personColors.Length;

        if (_personMaterials.TryGetValue(slot, out var cached) && cached != null)
        {
            colorIsInMaterial = HasColorProperty(cached);
            return cached;
        }

        if (lineMaterial == null)
        {
            ArLog.Warn("[Skeleton] lineMaterial が未設定です。既定のマテリアルで描画します");
            colorIsInMaterial = false;
            return null;
        }

        var mat = new Material(lineMaterial) { name = $"SkeletonMaterial_P{slot}" };
        colorIsInMaterial = HasColorProperty(mat);

        if (mat.HasProperty(_PropBaseColor)) mat.SetColor(_PropBaseColor, color);
        if (mat.HasProperty(_PropColor))     mat.SetColor(_PropColor,     color);

        if (!colorIsInMaterial)
        {
            ArLog.Warn($"[Skeleton] {lineMaterial.name} のシェーダーに _Color / _BaseColor が無いため、" +
                       "頂点カラーで色分けします（シェーダーが頂点カラー非対応なら色が付きません）");
        }

        _personMaterials[slot] = mat;
        return mat;
    }

    private static bool HasColorProperty(Material mat)
    {
        return mat != null && (mat.HasProperty(_PropBaseColor) || mat.HasProperty(_PropColor));
    }

    // ── 表示制御 ──
    private void SetPersonVisible(int personIndex, bool visible)
    {
        if (!_personLines.TryGetValue(personIndex, out var lines)) return;

        // 状態が変わっていないなら何もしない（毎フレーム80回の enabled 代入を防ぐ）
        if (_personVisible.TryGetValue(personIndex, out bool current) && current == visible) return;
        _personVisible[personIndex] = visible;

        foreach (var lr in lines)
        {
            if (lr != null) lr.enabled = visible;
        }
    }

    private static Color GetPersonColor(int index) => _personColors[index % _personColors.Length];
}
