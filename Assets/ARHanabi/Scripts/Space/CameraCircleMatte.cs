using UnityEngine;

// ===== CameraCircleMatte =====
// hanabi画面のカメラ映像を「下から立ち上がる半楕円のドーム」に見せる演出。
// 主役は花火なのに画面いっぱいの（明るい室内の）カメラ映像が背景を占めていて
// 花火が埋もれてしまうため、映像を画面下部のドームに収めて上を黒くし、
// 「地上に人がいて、その上に花火が上がる夜空が広がる」構図を作る。
//
// ── 形の変遷（当初は中央の真円だった）──
//   最初は画面中央の真円で作ったが、宇宙モードのコックピット枠との整合性を取るため
//   「下から立ち上がる半楕円」に変えた。枠の窓開口部はもともと
//   「アーチ状の上端＋ほぼ平らな下端」（uv.y 0.088〜0.985 / uv.x 0.017〜0.983）で
//   ドーム型をしているので、ドームをその内側に収めれば枠と衝突しない。
//   baseY = 0.5 かつ domeWidth = domeHeight にすれば元の真円にも戻せる。
//
// ── このコンポーネントが3つまとめて面倒を見る理由 ──
//   見た目を成立させるには次の3つが同時に揃っている必要がある。
//   どれか1つでも欠けると破綻するので、1箇所に集約して「まとめてON/OFF」できるようにした。
//     1. カメラを黒クリアにする      … 欠けると青いスカイボックスが露出する
//     2. 背景 Quad の大きさと位置    … 欠けると人の頭や上げた手がドームから切れる
//     3. ドーム型の幕を重ねる        … 欠けると縁がはっきり出て「切り抜いた感」が出る
//
// ── かつてあった4つ目「骨格の線を消す」を外した理由 ──
//   当初はドーム化と骨格の非表示をセットにしていたが、
//   「ドーム中でも認識されている人のボーンは見せたい」という方針に変わった。
//   骨格の表示ON/OFFはドームとは無関係な独立設定として Admin 画面から切り替える形にし、
//   その状態は SkeletonRenderer 自身が永続化する。ここは一切関与しない。
//
// ── なぜ「実行時に書き換えて実行時に戻す」方式なのか ──
//   この見せ方は現場で試して戻す可能性がある、という要件だった。
//   シーンに保存してしまうと戻すのが手作業になるので、
//   1〜3 はすべて「有効化した瞬間の値を控えて、無効化時に復元する」形にしてある。
//   結果として MainScene.unity には一切の永続的変更が入らない。
//
// ── 前提となっている実測値（触るときは必ず読む）──
//   ・背景 Quad `CameraBackground` は root 直下、localPosition (0,0,5)、
//     localScale (42,24,1)。カメラは (0,1,-10) なのでカメラ前方距離 15。
//   ・距離15の視錐台の高さは 2*15*tan30° = 17.32。Quad は 24 あるので
//     映像は画面に収まらず中央だけが見えている（縦72% / 横85%）。
//     つまり「今見えている映像」は既に 1.39 倍に中央クロップされた状態。
//     この事実を踏まえずに丸くクロップすると人の頭や手が切れる（SkeletonRenderer.cs:16-22 に実測が残っている）。
//   ・花火の粒はカメラ前方 2〜8（最悪ケースでも 9.5）に出る。
//     幕を 12 に置けば全ての花火より奥・背景 Quad(15) より手前に収まる。
[DisallowMultipleComponent]
public class CameraCircleMatte : MonoBehaviour
{
    private const string ShaderName    = "Custom/CameraCircleMatte";
    private const string QuadChildName = "CameraCircleMatte_Quad";

    // 幕を置くカメラ前方距離。花火(〜9.5)より奥、背景Quad(15)より手前
    private const float MatteDistance = 12f;

    // 視錐台ぴったりだと丸め誤差で縁に隙間ができるのでわずかに大きく描く
    private const float Overscan = 1.02f;

    [Header("ドームの形（下から立ち上がる半楕円）")]
    [Tooltip("ドームの底辺の高さ（uv。0 = 画面の下端）。\n" +
             "0 にすると楕円の下半分が画面外に出て「下から立ち上がる半楕円」になる。\n" +
             "映像Quadの下端はさらに下（画面外）へ置くので、底辺の切れ目は画面に出ない")]
    [SerializeField, Range(-0.3f, 0.5f)] private float baseY = 0f;

    [Tooltip("ドームの横半径（画面の高さを1とした単位）。\n" +
             "0.56 で左右 uv.x 0.185〜0.815（＝宇宙フレームの窓の内側）に収まる")]
    [SerializeField, Range(0.05f, 1.2f)] private float domeWidth = 0.56f;

    [Tooltip("ドームの縦半径（画面の高さを1とした単位）。\n" +
             "0.60 でドームの頂点が画面の高さの6割。上に残る黒が花火の夜空になる")]
    [SerializeField, Range(0.05f, 1.2f)] private float domeHeight = 0.60f;

    [Tooltip("楕円の縁に対する外周グラデーションの幅（半径比）。大きいほど境界が分からなくなる。\n" +
             "0 にすると輪郭がはっきり出て「切り抜いた感」が出る。\n" +
             "ドームは丸より大きいので、同じ比率でもぼけ幅の実寸は広くなる")]
    [SerializeField, Range(0f, 1f)] private float feather = 0.25f;

    [Tooltip("丸の内側に乗せる黒の濃さ。0 = 映像そのまま。\n" +
             "加算合成の花火は背景が明るいほど埋もれるので、花火を目立たせたいときに\n" +
             "0.15〜0.25 まで上げる。既定 0 は「見た目を変えない」という意味")]
    [SerializeField, Range(0f, 1f)] private float innerDim = 0f;

    // ── 内部 ──
    private Camera        _camera;
    private MeshRenderer  _renderer;
    private Transform     _quadTransform;
    private Material      _material;
    private bool          _initialized;
    private bool          _active;

    // 復元用に控えた元の値。
    // ⚠️ 控えるのは「有効化した瞬間の1回だけ」。有効化中に控え直すと
    //    自分が書いた値を「元の値」として覚えてしまい二度と戻らなくなるので、
    //    _captured フラグで二重取得を防いでいる
    private bool               _captured;
    private CameraClearFlags   _origClearFlags;
    private Color              _origBackgroundColor;
    private Transform          _bgQuad;
    private Vector3            _origBgQuadScale;
    private Vector3            _origBgQuadPos;

    // 背景 Quad のアスペクトを WebCamTexture から取りたいが、カメラが開くまで来ない。
    // 取れるまで LateUpdate で粘り、取れたら一度だけ確定する
    private bool _bgQuadScaleApplied;

    private static readonly int PropBaseY      = Shader.PropertyToID("_BaseY");
    private static readonly int PropDomeWidth  = Shader.PropertyToID("_DomeWidth");
    private static readonly int PropDomeHeight = Shader.PropertyToID("_DomeHeight");
    private static readonly int PropFeather    = Shader.PropertyToID("_Feather");
    private static readonly int PropInnerDim   = Shader.PropertyToID("_InnerDim");
    private static readonly int PropAspect     = Shader.PropertyToID("_Aspect");

    /// <summary>
    /// 演出のON/OFF。OFF にすると控えておいた元の値（カメラのClearFlags・
    /// 背景Quadのscale/position）をすべて復元し、幕を隠す。
    /// 骨格の表示状態はここでは触らない（SkeletonRenderer.ShowSkeleton の独立設定）。
    /// </summary>
    public bool MatteEnabled
    {
        get => _active;
        set
        {
            if (_active == value) return;
            if (value) Activate();
            else       Deactivate();

            // 展示は複数セッションにまたがって電源を落とすので、選択を次回起動へ引き継ぐ
            // （SpaceModeController 等と同じ SettingsStore 経由）
            SettingsStore.SetBool(MatteEnabledKey, _active);
        }
    }

    private const string MatteEnabledKey = nameof(CameraCircleMatte) + ".enabled";

    public void SetEnabled(bool value) => MatteEnabled = value;

    private void Awake()
    {
        Initialize(GetComponent<Camera>());
    }

    private void Initialize(Camera mainCamera)
    {
        if (_initialized) return;

        _camera = mainCamera != null ? mainCamera : GetComponent<Camera>();
        if (_camera == null)
        {
            Debug.LogError("[CircleMatte] アタッチ先に Camera が見つかりません");
            enabled = false;
            return;
        }

        var shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[CircleMatte] シェーダーが見つかりません: {ShaderName}" +
                            "（Always Included Shaders への登録漏れの可能性があります）");
            enabled = false;
            return;
        }

        BuildQuad(shader);
        _initialized = true;

        // 生成直後は隠しておく。実際の表示は下の復元と MatteEnabled 経由で決まる
        if (_renderer != null) _renderer.enabled = false;

        // 前回の選択を復元する。既定は ON（この演出を入れるのが今回の目的なので、
        // 何も保存されていない初回起動では有効な状態で始める）
        if (SettingsStore.GetBool(MatteEnabledKey, true)) Activate();
    }

    private void BuildQuad(Shader shader)
    {
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

            // CreatePrimitive は MeshCollider を付けてくる。見た目だけの幕なので
            // レイキャストの当たり判定に混ざり込んではいけない
            var collider = quadGo.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var t = quadGo.transform;
            t.SetParent(_camera.transform, false);
            t.localPosition = new Vector3(0f, 0f, MatteDistance);
            // 組み込み Quad の法線は -Z。カメラの子で localPosition.z = +距離 に置くと
            // カメラはこの Quad から見て -Z 側に立つので、回転なしで正しく表を向く
            t.localRotation = Quaternion.identity;

            // CreatePrimitive は MeshRenderer も既に付けている。
            // ここで AddComponent<MeshRenderer>() すると「2つ目は追加できない」制約で
            // null が返り、次行で NullReferenceException になる
            // （実際にこれで既定マテリアルのグレーの板が画面に残る不具合を作った）
            _renderer = quadGo.GetComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows    = false;

            _material = new Material(shader) { name = "CameraCircleMatte_Instance" };
            _renderer.material = _material;
        }

        _quadTransform = quadGo.transform;
        ApplyScale();
        ApplyMaterialProps();
    }

    // ── 有効化 / 無効化 ──

    private void Activate()
    {
        if (!_initialized) return;

        CaptureOriginals();

        // 1. カメラを黒クリアにする。
        //    これをやらないと、縮めた背景 Quad の外に Unity 既定の青いスカイボックスが
        //    露出し、さらに外周フェードが青に向かって溶けて青いハローの輪ができる
        _camera.clearFlags      = CameraClearFlags.SolidColor;
        _camera.backgroundColor = Color.black;

        // 2. 背景 Quad の大きさ・位置は WebCamTexture のアスペクトが要るので LateUpdate で粘る
        _bgQuadScaleApplied = false;

        // 3. 幕を出す
        if (_renderer != null) _renderer.enabled = true;

        _active = true;
        Debug.Log("[CircleMatte] 有効化しました（カメラを黒クリア・背景Quad縮小・幕）");
    }

    private void Deactivate()
    {
        if (_renderer != null) _renderer.enabled = false;
        _active = false;
        RestoreOriginals();
        Debug.Log("[CircleMatte] 無効化し、元の状態へ復元しました");
    }

    private void CaptureOriginals()
    {
        if (_captured) return;   // 二重取得の防止（自分が書いた値を「元の値」にしないため）

        _origClearFlags      = _camera.clearFlags;
        _origBackgroundColor = _camera.backgroundColor;

        _bgQuad = FindBackgroundQuad();
        if (_bgQuad != null)
        {
            _origBgQuadScale = _bgQuad.localScale;
            _origBgQuadPos   = _bgQuad.position;
        }

        _captured = true;
    }

    private void RestoreOriginals()
    {
        if (!_captured) return;

        if (_camera != null)
        {
            _camera.clearFlags      = _origClearFlags;
            _camera.backgroundColor = _origBackgroundColor;
        }

        if (_bgQuad != null)
        {
            _bgQuad.localScale = _origBgQuadScale;
            _bgQuad.position   = _origBgQuadPos;
        }

        _captured = false;
    }

    // 背景 Quad は AdminPanel の外の root オブジェクトなので、名前で探す。
    // CameraBackgroundController が付いている方を正とし、名前一致より優先する
    // （名前は運用で変わりうるが、コンポーネントの有無は変わりにくい）
    private static Transform FindBackgroundQuad()
    {
        var controller = FindFirstObjectByType<CameraBackgroundController>();
        if (controller != null) return controller.transform;

        var byName = GameObject.Find("CameraBackground");
        return byName != null ? byName.transform : null;
    }

    // ── 毎フレームの追従 ──

    private void LateUpdate()
    {
        if (!_initialized || _camera == null || _quadTransform == null) return;

        ApplyScale();
        ApplyMaterialProps();

        if (_active && !_bgQuadScaleApplied) TryApplyBackgroundQuadTransform();
    }

    // 幕を視錐台にフィットさせる。組み込み Quad は scale 1 で 1x1 ワールド単位なので
    // このスケール値がそのまま世界サイズになる（CockpitFrameOverlay と同じ式）
    private void ApplyScale()
    {
        float height = FrustumHeightAt(MatteDistance);
        float width  = height * _camera.aspect;
        _quadTransform.localScale = new Vector3(width * Overscan, height * Overscan, 1f);
    }

    private void ApplyMaterialProps()
    {
        if (_material == null) return;

        _material.SetFloat(PropBaseY,      baseY);
        _material.SetFloat(PropDomeWidth,  domeWidth);
        _material.SetFloat(PropDomeHeight, domeHeight);
        _material.SetFloat(PropFeather,    feather);
        _material.SetFloat(PropInnerDim,   innerDim);
        _material.SetFloat(PropAspect,     _camera.aspect);
    }

    // 背景 Quad の大きさと位置を、ドームのパラメータから導出して適用する。
    //
    // ── なぜ「ドームから逆算」するのか ──
    //   映像は長方形、ドームは楕円なので、両者を独立に指定すると
    //   「ドームの一部に映像が無くて黒く欠ける」「映像がドームからはみ出て縁が見える」
    //   のどちらかが必ず起きる。ドームを覆う最小の長方形を計算して映像を合わせれば、
    //   ドームの3つの値だけを触れば常に整合する。
    //
    // 横幅は WebCamTexture の実アスペクトから出す。現在のシーンの Quad は
    // 42:24 = 1.750 だが映像は 16:9 = 1.778 なので約1.6%横に潰れており、
    // ここで作り直すことでその歪みも同時に直る。
    // WebCamTexture はカメラが開くまで null なので、取れるまで毎フレーム試す。
    private void TryApplyBackgroundQuadTransform()
    {
        if (_bgQuad == null) return;

        float videoAspect = 16f / 9f;   // 取れなかった場合のフォールバック

        var controller = _bgQuad.GetComponent<CameraBackgroundController>();
        var webcam     = controller != null ? controller.GetWebCamTexture() : null;
        if (webcam != null && webcam.width > 16 && webcam.height > 16)
            videoAspect = webcam.width / (float)webcam.height;
        else if (controller != null)
            return;   // まだ開いていない。次フレーム以降に再挑戦する

        // 背景 Quad はカメラの子ではないので、カメラからの距離を実測して視錐台を出す
        var camT = _camera.transform;
        float distance = Vector3.Dot(_bgQuad.position - camT.position, camT.forward);
        if (distance <= 0.01f) distance = 15f;   // 想定外の配置でも破綻させない

        float frustumH = FrustumHeightAt(distance);

        // ── ドームを覆うのに必要な範囲（すべて「画面の高さ = 1」の単位）──
        //
        // ⚠️ ここは「ドームの縁（e=1）まで」では足りない。
        //   フェードは e = 1+feather まで続くので、そこまで映像が無いと
        //   「半分フェードした映像」と「映像が無い黒」が映像Quadの縁で
        //   ぶつかり、輝度が急に落ちる直線の境界が出る。
        //   実際に feather を無視して組んだところ、ドームのアーチが映像の上辺で
        //   まっすぐ切り落とされ、角丸長方形のように見える不具合になった。
        //   フェードが完全に終わる位置まで映像で覆うのが正しい。
        float outer      = 1f + feather;
        float needTop    = baseY + domeHeight * outer;
        float needBottom = Mathf.Min(baseY, 0f) - BottomMargin;
        float needH      = (needTop - needBottom) * CoverSafety;
        // 横: フェード外側までの直径
        float needW      = domeWidth * outer * 2f * CoverSafety;

        // 映像のアスペクトは固定なので、縦横どちらか厳しい方に合わせる
        float videoH = Mathf.Max(needH, needW / videoAspect);
        float videoW = videoH * videoAspect;

        // 必要範囲の中心へ映像を置く。安全率や縦横比の都合で映像が必要範囲より
        // 大きくなった場合も、中心を合わせておけば上下左右へ均等に余るだけで済む
        float centerYUv = (needTop + needBottom) * 0.5f;

        // uv(0..1) の中心は 0.5。そこからのズレを視錐台の高さでワールド量に直す。
        // カメラは回転していない前提ではなく、forward/up から組んで将来の回転にも耐える形にする
        float worldH = videoH * frustumH;
        float worldW = videoW * frustumH;
        float offsetY = (centerYUv - 0.5f) * frustumH;

        Vector3 viewCenter = camT.position + camT.forward * distance;
        Vector3 target     = viewCenter + camT.up * offsetY;

        _bgQuad.localScale = new Vector3(worldW, worldH, _origBgQuadScale.z);
        // z（奥行き）は元のまま維持する。ここを動かすと花火との前後関係が壊れる
        _bgQuad.position   = new Vector3(target.x, target.y, _origBgQuadPos.z);

        _bgQuadScaleApplied = true;

        Debug.Log($"[CircleMatte] 背景Quadを適用: 距離{distance:F1} 映像アスペクト{videoAspect:F3} " +
                  $"→ scale({worldW:F2}, {worldH:F2}) pos.y={target.y:F2} " +
                  $"(uv 縦 {needBottom:F3}〜{needTop:F3})");
    }

    // 映像の下端をどれだけ画面の外へ出すか（画面の高さ比）。
    // 0 にすると映像の下端が画面下端とちょうど重なり、
    // そこに横一直線のはっきりした境界が出てしまう
    private const float BottomMargin = 0.04f;

    // 映像がフェード範囲をきっちり覆うための安全率。
    // 丸め誤差で1画素でも足りないと、その線が境界として見えてしまう
    private const float CoverSafety = 1.03f;

    private float FrustumHeightAt(float distance)
    {
        if (_camera.orthographic) return _camera.orthographicSize * 2f;
        return 2f * distance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
    }

    // ── 後始末 ──
    //
    // OnDisable と OnDestroy の両方で片付ける。片方だけだと
    // 「コンポーネントを無効化した」「アプリを終了した」のどちらかで戻し漏れる。
    //
    // OnDisable では RestoreOriginals() だけを呼ぶのでは足りない。
    // 幕の MeshRenderer は子 GameObject 側にあるのでコンポーネントを無効化しても
    // 描画され続け、さらに _active が true のまま残るため Admin のラベルが
    // 「ON」なのに何も適用されていない、という食い違いが起きる。
    // Deactivate() を通して「幕を隠す・復元する・_active を倒す」を揃える。
    private void OnDisable()
    {
        if (_active) Deactivate();
        else         RestoreOriginals();
    }

    private void OnDestroy()
    {
        if (_active) Deactivate();
        else         RestoreOriginals();

        if (_material != null) Destroy(_material);
    }

    /// <summary>
    /// シーンにあればそれを、無ければアタッチして返す。何度呼んでも幕を増やさない
    /// </summary>
    public static CameraCircleMatte GetOrCreate(Camera mainCamera)
    {
        if (mainCamera == null) return null;

        var found = mainCamera.GetComponent<CameraCircleMatte>();
        if (found != null) return found;

        return mainCamera.gameObject.AddComponent<CameraCircleMatte>();
    }

    // シーン編集を必須にしないための自動アタッチ。
    // CockpitFrameOverlay と同じ、このプロジェクト全体で一貫した方針に倣う
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoAttach()
    {
        var cam = Camera.main;
        if (cam == null) return;
        if (cam.GetComponent<CameraCircleMatte>() != null) return;

        cam.gameObject.AddComponent<CameraCircleMatte>();
    }
}
