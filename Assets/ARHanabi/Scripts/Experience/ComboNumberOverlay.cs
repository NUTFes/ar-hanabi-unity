using UnityEngine;
using TMPro;

// ===== ComboNumberOverlay =====
// コンボの段階を、その人の花火と同じ横位置あたりに「2!」〜「5!」としてポップ表示する。
// ExperienceDirector.OnComboAdvanced を購読する唯一のクラス。
//
// ── world-space TextMeshPro（TextMeshProUGUI ではない）を使う理由 ──
//   UGUI の Screen Space - Overlay Canvas は Display 0 に紐付いており、客席ディスプレイ
//  （Display 1）には出せない（CockpitFrameOverlay・SkeletonRenderer と同じ制約）。
//   3D オブジェクトとして Main Camera の前に置けば、そのカメラが映すディスプレイに
//   そのまま乗る。花火・上昇の光跡と同じ考え方（どちらも world-space の使い捨てオブジェクト）。
//
// ── フォントを指定していない理由 ──
//   表示する文字は数字と "!" のみ（ASCII）。プロジェクトの NotoSansJP SDF は日本語表示の
//   ために導入したもので、ASCII の表示には不要。TextMeshPro の既定フォント（TMP Essential
//   Resources に含まれる）で足りるため、あえて指定せずシンプルにしてある
//  （日本語を表示する必要が出たら font フィールドに割り当てること）。
//
// ── ComboNumberPopup を分けている理由 ──
//   このクラスは「いつ・どこに出すか」の窓口。実際のポップ→上昇→フェードのアニメーションは
//   生成したオブジェクト自身に持たせる（LaunchTrailEffect と同じ、作って任せる方式）。
public class ComboNumberOverlay : MonoBehaviour
{
    [Tooltip("段階がこの値未満なら表示しない（初回はいつもの花火でよいため）")]
    [SerializeField] private int minStageToShow = 2;

    [Tooltip("拡大ポップにかける秒数")]
    [SerializeField] private float popSeconds = 0.2f;

    [Tooltip("ポップ後、上昇しながらフェードする秒数")]
    [SerializeField] private float riseSeconds = 0.8f;

    [Tooltip("上昇する距離（ワールド単位）")]
    [SerializeField] private float riseDistance = 0.6f;

    [Tooltip("表示するビューポート上の高さ（0=下端、1=上端）。人の実際の縦位置ではなく、\n" +
             "画面内で見やすい固定の高さを使う（人の腰位置は画面下寄りになりやすいため）")]
    [SerializeField, Range(0f, 1f)] private float viewportY = 0.62f;

    [Tooltip("文字の大きさ（TextMeshPro のワールド単位フォントサイズ）")]
    [SerializeField] private float fontSize = 3.5f;

    [Tooltip("段階5（フィニッシャー）の文字色")]
    [SerializeField] private Color finisherColor = Color.white;

    [Tooltip("段階2〜4の文字色")]
    [SerializeField] private Color normalColor = new Color(1f, 0.85f, 0.3f);

    [Header("参照")]
    [Tooltip("未設定ならシーンから自動で探す（FindFirstObjectByType）")]
    [SerializeField] private FireworkLauncher launcher;

    [Tooltip("日本語等、既定フォントでは表示できない文字を使う場合にのみ割り当てる")]
    [SerializeField] private TMP_FontAsset font;

    private bool _subscribed;

    // UfoSpawner / SpaceModeController / ExperienceDirector と同じく、
    // シーンに置かなくても動くようにする
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (FindFirstObjectByType<ComboNumberOverlay>() != null) return;

        var go = new GameObject("ComboNumberOverlay");
        go.AddComponent<ComboNumberOverlay>();
        DontDestroyOnLoad(go);
    }

    // ── イベント購読 ──
    // SkeletonRenderer.TrySubscribe と同じ形。ExperienceDirector も自身の
    // RuntimeInitializeOnLoadMethod で生成されるため、生成順は保証されない。
    // OnEnable で見つからなければ Start でもう一度試す
    private void OnEnable() => TrySubscribe();
    private void Start()    => TrySubscribe();

    private void TrySubscribe()
    {
        if (_subscribed) return;

        var director = ExperienceDirector.Instance;
        if (director == null) return; // Start でもう一度試す

        director.OnComboAdvanced += OnComboAdvanced;
        _subscribed = true;

        if (launcher == null)
            launcher = FindFirstObjectByType<FireworkLauncher>();
    }

    private void OnDisable()
    {
        if (!_subscribed) return;

        var director = ExperienceDirector.Instance;
        if (director != null)
            director.OnComboAdvanced -= OnComboAdvanced;

        _subscribed = false;
    }

    private void OnComboAdvanced(int trackId, int stage, Vector2 normalizedPos)
    {
        if (stage < minStageToShow) return;

        // 「コンボの数字」個別トグル（管理画面）。ComboTrailEnabled とは独立に切れる
        var director = ExperienceDirector.Instance;
        if (director == null || !director.ComboNumberEnabled) return;

        if (launcher == null)
        {
            launcher = FindFirstObjectByType<FireworkLauncher>();
            if (launcher == null) return;
        }

        // 花火の発射位置決定と全く同じロジック（Quad変換・remap・ジッター）を通すため、
        // 数字は常にその人の花火と同じ横位置に出る
        var worldPos = launcher.ResolveOverlayWorldPosition(normalizedPos, viewportY);
        if (worldPos == null) return;

        Spawn(stage, worldPos.Value);
    }

    private void Spawn(int stage, Vector3 worldPos)
    {
        var go = new GameObject($"ComboNumber_{stage}");
        go.transform.position = worldPos;

        var text = go.AddComponent<TextMeshPro>();
        text.text      = $"{stage}!";
        text.fontSize  = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.color     = stage >= 5 ? finisherColor : normalColor;
        if (font != null) text.font = font;

        var popup = go.AddComponent<ComboNumberPopup>();
        popup.Init(text, popSeconds, riseSeconds, riseDistance);

        Destroy(go, popSeconds + riseSeconds + 0.2f);
    }
}

// ===== ComboNumberPopup =====
// 1個の数字ポップの寿命ぶんの見た目（拡大→静止→上昇しながらフェード）を持つ、
// 使い捨て（fire-and-forget）コンポーネント。SpawnLaunchTrail / LaunchTrailEffect と同じ方針。
// 常にカメラの方を向く（ビルボード）。回転を毎フレーム合わせるだけなので、
// 3D TextMeshPro が transform.rotation の影響を受ける形状（平面）でも正面から読める。
public class ComboNumberPopup : MonoBehaviour
{
    private TextMeshPro _text;
    private float       _popSeconds;
    private float       _riseSeconds;
    private float       _riseDistance;
    private Vector3     _basePos;
    private float       _startTime;
    private Camera      _camera;

    public void Init(TextMeshPro text, float popSeconds, float riseSeconds, float riseDistance)
    {
        _text         = text;
        _popSeconds   = Mathf.Max(0.01f, popSeconds);
        _riseSeconds  = Mathf.Max(0.01f, riseSeconds);
        _riseDistance = riseDistance;
        _basePos      = transform.position;
        _startTime    = Time.time;
        _camera       = Camera.main;

        transform.localScale = Vector3.zero;
    }

    private void Update()
    {
        if (_text == null) return;

        if (_camera != null)
            transform.rotation = _camera.transform.rotation;

        float t = Time.time - _startTime;

        if (t <= _popSeconds)
        {
            // 0→1 まで滑らかに、最後に少しオーバーシュートして「ポン」と出た感じにする
            float u     = t / _popSeconds;
            float ease  = Mathf.SmoothStep(0f, 1f, u);
            float scale = ease * (1f + 0.15f * (1f - u));

            transform.localScale = Vector3.one * scale;
            transform.position   = _basePos;

            var c = _text.color;
            c.a = ease;
            _text.color = c;
        }
        else
        {
            float riseT = Mathf.Clamp01((t - _popSeconds) / _riseSeconds);

            transform.localScale = Vector3.one;
            transform.position   = _basePos + Vector3.up * (_riseDistance * riseT);

            var c = _text.color;
            c.a = 1f - riseT;
            _text.color = c;
        }
    }
}
