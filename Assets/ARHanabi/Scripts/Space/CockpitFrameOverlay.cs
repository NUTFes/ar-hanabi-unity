using UnityEngine;

// ===== CockpitFrameOverlay =====
// 宇宙モード（別大学祭向けテーマ）向けの「操縦席の窓から外を見ている」演出。
// メインカメラの子に、中心が完全に透けたコックピット窓枠の Quad を1枚貼り付ける。
//
// ── なぜ Canvas ではなく素の World Space Quad なのか ──
//   シーン内の唯一の Canvas（AdminCanvas）は RenderMode.ScreenSpaceOverlay で
//   m_TargetDisplay: 0 に固定されている。一方メインカメラ（と全ての花火）は
//   m_TargetDisplay: 1 に描画している。ScreenSpaceOverlay の Canvas は
//   「カメラ」ではなく「ディスプレイ」に紐付く仕組みなので、この枠を Canvas で
//   実装すると2画面構成のときに間違ったモニターへ出てしまう。
//   カメラの子にした素の Quad なら、メインカメラがどのディスプレイへ
//   描画してもそのまま一緒についてくるので、ディスプレイ指定のロジックが
//   一切不要になる。
//
// ── なぜカメラ前方 distance = 1.0 なのか ──
//   このシーンでは開花演出が最も近づくケースでもカメラ前方距離 ≈2.0
//   （型花火の星が最も遠くまで届いた場合の到達点）までしか来ない。
//   1.0 に置いておけば、ニアクリップ(0.3)より十分手前かつ、あらゆる花火より
//   確実にカメラへ近いので、花火側との距離比較を毎フレーム行わなくても
//   「枠は常に花火より手前に描かれる」ことを保証できる
//   （シェーダー側の Queue=Transparent+100 とあわせて二重に担保している）。
//
// ── ビルド時の注意（このスクリプトでは対応しない）──
//   Shader.Find でのみ参照されるシェーダーは、Always Included Shaders に
//   登録されていないとスタンドアロンビルドから丸ごと除外されうる。
//   Custom/CockpitFrame と、並行作業中の Custom/SpaceCraft の両方を
//   ビルド設定に追加する必要があることを、最終ビルド確認の担当者は
//   忘れないこと（本スクリプトはその登録作業自体は行わない）。
[DisallowMultipleComponent]
public class CockpitFrameOverlay : MonoBehaviour
{
    private const string ShaderName    = "Custom/CockpitFrame";
    private const string QuadChildName = "CockpitFrameOverlay_Quad";

    // カメラ前方への距離（ワールド単位）。花火の最近接点(≈2.0)より確実に手前
    private const float Distance = 1.0f;

    // 視錐台にぴったり合わせると、アスペクト比の丸め誤差で縁に髪の毛1本分の
    // 隙間ができることがあるため、わずかに大きめに描いておく
    private const float Overscan = 1.02f;

    private Camera        _camera;
    private MeshRenderer   _renderer;
    private Transform      _quadTransform;
    private Material       _material;
    private bool           _initialized;

    /// <summary>
    /// 見た目のON/OFF。SpaceModeController が居る限りは LateUpdate で
    /// 毎フレーム実効値に追従して上書きされる（後述）。それでもプロパティとして
    /// 公開しておくのは、SpaceModeController が存在しない環境（単体テスト等）や、
    /// 将来 Admin UI 側から直接叩きたくなった場合の窓口として残すため。
    /// Quad の生成・破棄は一切行わず MeshRenderer.enabled を切り替えるだけなので、
    /// 何度呼んでも安価で、再表示も即座に行える。
    /// </summary>
    public bool FrameVisible
    {
        get => _renderer != null && _renderer.enabled;
        set { if (_renderer != null) _renderer.enabled = value; }
    }

    public void SetVisible(bool visible) => FrameVisible = visible;

    /// <summary>
    /// 明示的な初期化の入口。複数回呼んでも Quad を二重生成しない
    /// （既に子がいればそれを使い回す）。Awake からも自動で1度呼ばれるので、
    /// AutoAttach 経由で AddComponent しただけの場合はこれを呼ぶ必要はない。
    /// </summary>
    public void Initialize(Camera mainCamera)
    {
        if (_initialized) return;

        _camera = mainCamera != null ? mainCamera : GetComponent<Camera>();
        if (_camera == null)
        {
            Debug.LogError("[CockpitFrame] アタッチ先に Camera が見つかりません");
            enabled = false;
            return;
        }

        var shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[CockpitFrame] シェーダーが見つかりません: {ShaderName}" +
                            "（Always Included Shaders への登録漏れの可能性があります）");
            enabled = false;
            return;
        }

        BuildQuad(shader);

        _initialized = true;
        SyncVisibilityFromController();
    }

    private void Awake()
    {
        // AutoAttach（AddComponent のみ）経由でも自力で立ち上がれるようにする
        if (!_initialized) Initialize(GetComponent<Camera>());
    }

    private void BuildQuad(Shader shader)
    {
        // 既存の子がいれば使い回す（二重生成防止。Initialize が再実行されても壊れない）
        var existing = transform.Find(QuadChildName);
        GameObject quadGo;

        if (existing != null)
        {
            quadGo    = existing.gameObject;
            _renderer = quadGo.GetComponent<MeshRenderer>();
            _material = _renderer != null ? _renderer.sharedMaterial : null;
        }
        else
        {
            quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quadGo.name = QuadChildName;

            // CreatePrimitive は既定で MeshCollider を付けてくる。
            // この枠は見た目だけの飾りなので、レイキャストの当たり判定に
            // 混ざり込んではいけない（現時点でプロジェクト内を確認した限り
            // Physics.Raycast / Physics.RaycastAll を使っている箇所は無いが、
            // 将来増えたときに「カメラの目の前に浮かぶ透明な壁」として
            // 誤って拾われないよう、この場で無条件に外す）
            var collider = quadGo.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var t = quadGo.transform;
            t.SetParent(_camera.transform, false);
            t.localPosition = new Vector3(0f, 0f, Distance);

            // 組み込み Quad の法線は -Z を向く。この Quad はカメラの子で
            // localPosition.z = +Distance（カメラのローカル前方）に置くため、
            // カメラはこの Quad から見て -Z 側（後方）に立っていることになり、
            // 法線 -Z はちょうどカメラの方を向く ＝ 回転なし(Identity)で正しい表を向く。
            // シェーダー自体は Cull Off なので表裏どちらでも描画結果は破綻しないが、
            // 将来 _FrameTex に左右非対称な絵（文字入りの枠画像など）を差し込んだときに
            // 鏡写しにならないよう、ここで正しい向きにしておく
            t.localRotation = Quaternion.identity;

            // CreatePrimitive は MeshRenderer も既定で付けてくる。
            // ここで AddComponent<MeshRenderer>() してしまうと「1つの GameObject に
            // 2つ目の MeshRenderer は追加できない」という Unity 側の制約に阻まれ、
            // 戻り値が null になって次の行で NullReferenceException を起こす
            // （実際にこれで例外が発生し、生成直後の既定マテリアル・既定サイズの
            //  MeshRenderer がそのまま有効になって画面にグレーの板が残った）。
            // 既に付いているものを取得するだけでよい
            _renderer = quadGo.GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows    = false;

            _material = new Material(shader) { name = "CockpitFrame_Instance" };
            _renderer.material = _material;
        }

        ApplyGeneratedTexture();

        _quadTransform = quadGo.transform;

        ApplyScale();
    }

    // Resources/Space/CockpitFrame（コードで手続き生成した TGA。宇宙船の窓枠だと
    // 一目でわかるよう、ベゼル・補強ステー・リベット・計器パネル・ハザード柄を
    // 描き込んである）があれば読み込み、シェーダーの _FrameTex / _UseTexture へ渡す。
    // 見つからない場合は何もしない＝シェーダー側の手続き描画（枠のみの簡易版）に
    // 自動でフォールバックする。将来 Firefly 等で作った本物の画像に差し替えたい
    // ときも、同じ Resources/Space/CockpitFrame という名前で置き換えるだけでよい
    private void ApplyGeneratedTexture()
    {
        if (_material == null) return;

        var tex = Resources.Load<Texture2D>("Space/CockpitFrame");
        if (tex == null) return;

        _material.SetTexture("_FrameTex", tex);
        _material.SetFloat("_UseTexture", 1f);
    }

    private void LateUpdate()
    {
        // カメラの移動そのものではなく FOV/アスペクト比の変化にだけ追従すればよいが、
        // ウィンドウリサイズのタイミングを個別に拾うより、LateUpdate で
        // 毎フレーム計算し直すほうが単純で確実（このシェーダーは1枚しか描かないので
        // 負荷は無視できる）
        if (!_initialized || _camera == null || _quadTransform == null) return;

        ApplyScale();
        SyncVisibilityFromController();
    }

    // カメラの FOV / アスペクト比から、この距離でぴったり画面を覆う大きさを求め直す。
    // 組み込み Quad は localScale=1 のとき 1x1 ワールド単位の正方形なので、
    // このスケール値がそのまま世界サイズになる
    private void ApplyScale()
    {
        float height = _camera.orthographic
            ? _camera.orthographicSize * 2f
            : 2f * Distance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

        float width = height * _camera.aspect;

        _quadTransform.localScale = new Vector3(width * Overscan, height * Overscan, 1f);
    }

    // SpaceModeController が居ればその実効値（FrameEnabled）にそのまま従う。
    // 居ない場合は「宇宙モードがこのプレイセッションで一度も有効化されていない」
    // とみなし、非表示側に倒す（表示側をデフォルトにすると、宇宙テーマを
    // 使わない通常運用でも枠が出てしまいかねないため）
    private void SyncVisibilityFromController()
    {
        if (_renderer == null) return;
        _renderer.enabled = SpaceModeController.Instance != null && SpaceModeController.Instance.FrameEnabled;
    }

    /// <summary>
    /// シーンにあればそれを、無ければアタッチして初期化した上で返す。
    /// 何度呼んでも Quad を増やさない（GetComponentInChildren で既存を探す）
    /// </summary>
    public static CockpitFrameOverlay GetOrCreate(Camera mainCamera)
    {
        if (mainCamera == null) return null;

        var found = mainCamera.GetComponentInChildren<CockpitFrameOverlay>();
        if (found != null) return found;

        var overlay = mainCamera.gameObject.AddComponent<CockpitFrameOverlay>();
        overlay.Initialize(mainCamera);
        return overlay;
    }

    // シーン編集を必須にしないための自動アタッチ。SpaceModeController や
    // SelfieSegmentationController と同じ、このプロジェクト全体で一貫した方針
    // （「機能追加にシーン編集を要求しない」）に倣う。
    // Camera.main はタグ検索でやや重いので、シーンロード直後の一度だけに留める
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoAttach()
    {
        var cam = Camera.main;
        if (cam == null) return;
        if (cam.GetComponentInChildren<CockpitFrameOverlay>() != null) return;

        cam.gameObject.AddComponent<CockpitFrameOverlay>();
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
    }
}
