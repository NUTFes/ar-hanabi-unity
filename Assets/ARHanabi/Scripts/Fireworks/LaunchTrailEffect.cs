using UnityEngine;

// ===== LaunchTrailEffect =====
// 打ち上げの光跡。画面下から開花位置まで、火の粉を残しながら上昇する。
//
// ── なぜ必要になったか ──
//   打ち上げ音と破裂音を分けると、その間（既定0.9秒）に音だけが鳴って
//   画面には何も起きない時間ができてしまう。実際の花火は上昇中の光が見えるので、
//   そこを埋めないと「音がずれている」ように感じられる。
//
// ── 動き ──
//   実際の打ち上げは、打ち上げ薬で加速したあと惰性で登って頂点で開く。
//   到達時にちょうど速度が緩むよう、減速するイージング（1-(1-u)^2）で上げる。
//   火の粉は玉の通過位置に置いて、その場で落ちながら消える
//   （ShellFireworkEffect の尾と同じ考え方）。
//
// 1 GameObject / 1 Material / 1 DrawCall。粒は Launch() で確保して使い回す。

public class LaunchTrailEffect : MonoBehaviour
{
    [Header("見た目")]
    [Tooltip("玉（先頭の光）の大きさ")]
    public float headSize = 0.22f;

    [Tooltip("火の粉の数。多いほど濃い軌跡になる")]
    public int sparkCount = 34;

    [Tooltip("火の粉の大きさ（玉に対する比）")]
    public float sparkSizeScale = 0.45f;

    [Tooltip("火の粉が消えるまでの秒数")]
    public float sparkLifetime = 0.34f;

    [Tooltip("火の粉が後ろへ落ちる速さ")]
    public float sparkFall = 1.1f;

    [Tooltip("火の粉が横に散る幅")]
    public float sparkSpread = 0.10f;

    public Color headColor  = new Color(1f, 0.92f, 0.62f);
    public Color sparkColor = new Color(1f, 0.62f, 0.22f);

    [Tooltip("HDR発光の倍率（Custom/ParticleAdditive の _Intensity）。1 で従来どおり。\n" +
             "粒の色は Color32（上限1.0）でしか渡せないので、\n" +
             "1.0 を超える明るさはここで作る。\n" +
             "型花火（ShellPreset.emissiveIntensity は 1.6〜3.0）より控えめにしてあるのは、\n" +
             "昇りが開花より明るいと主役が入れ替わって見えてしまうため")]
    [Range(0.2f, 8f)] public float emissiveIntensity = 1.5f;

    [Header("シェーダー設定")]
    [SerializeField] private Shader particleShader;

    private const string AdditiveShaderName    = "Custom/ParticleAdditive";
    private const string VertexColorShaderName = "Custom/ParticleUnlit";

    private ParticleSystem.Particle[] _buf;
    private Vector3[]                 _sparkPos;
    private Vector3[]                 _sparkVel;
    private float[]                   _sparkBirth;

    // ドーパミンモード（虹色）の設定。Launch() で1回だけスナップショットする。
    //
    // ── 毎フレーム Instance を見に行かない理由 ──
    //   1. 昇っている最中に管理画面で OFF にされた玉が途中で色を失うと「バグに見える」。
    //      1発は最後まで同じモードで飛びきるべき
    //   2. Instance != null は UnityEngine.Object の == オーバーロード
    //      （ネイティブ側への問い合わせを含む）。粒ごとに呼ぶと実測できる負荷になる
    //   時間で回る位相（HuePhase）だけは Update() のループの外で1回読む
    private RainbowTint.Settings _rainbow;

    private ParticleSystem _ps;
    private Material       _material;
    private Vector3        _from, _to;
    private float          _rise;
    private float          _startTime;
    private bool           _launched;
    private int            _count;

    public void SetShader(Shader shader)
    {
        if (shader != null) particleShader = shader;
    }

    /// <summary>from から to へ riseSeconds かけて上昇する</summary>
    public void Launch(Vector3 from, Vector3 to, float riseSeconds, float scale)
    {
        // PrepareMaterial が _Intensity を書き込むので、その前に取っておく
        _rainbow = RainbowTint.Settings.Capture();

        if (!PrepareMaterial())
        {
            Destroy(gameObject);
            return;
        }

        _from = from;
        _to   = to;
        _rise = Mathf.Max(0.05f, riseSeconds);

        headSize       *= scale;
        sparkSpread    *= scale;
        sparkFall      *= scale;

        int sparks = Mathf.Max(0, sparkCount);
        _count = sparks + 1;               // 末尾の1個が玉（先頭の光）

        _buf        = new ParticleSystem.Particle[_count];
        _sparkPos   = new Vector3[sparks];
        _sparkVel   = new Vector3[sparks];
        _sparkBirth = new float[sparks];

        // 火の粉は上昇のあいだ等間隔に置く。
        // 位置は Update で玉の軌跡から求めるので、ここでは生成時刻と初速だけ決める
        for (int i = 0; i < sparks; i++)
        {
            _sparkBirth[i] = _rise * (i + 0.5f) / sparks;
            _sparkVel[i]   = new Vector3(Random.Range(-sparkSpread, sparkSpread),
                                         -Random.Range(0.3f, 1f) * sparkFall,
                                         Random.Range(-sparkSpread, sparkSpread) * 0.4f);
        }

        float psLife = _rise + sparkLifetime + 1f;
        for (int i = 0; i < _count; i++)
        {
            _buf[i].position          = _from;
            _buf[i].velocity          = Vector3.zero;
            _buf[i].startSize         = 0f;
            _buf[i].startColor        = new Color32(0, 0, 0, 0);
            _buf[i].startLifetime     = psLife;
            _buf[i].remainingLifetime = psLife;
        }

        SetupParticleSystem(psLife);

        _startTime = Time.time;
        _launched  = true;

        Destroy(gameObject, _rise + sparkLifetime + 0.3f);
    }

    // 上昇の位置。減速するイージングで、到達時にちょうど速度が緩む
    private Vector3 HeadPositionAt(float t)
    {
        float u    = Mathf.Clamp01(t / _rise);
        float ease = 1f - (1f - u) * (1f - u);
        return Vector3.Lerp(_from, _to, ease);
    }

    private void Update()
    {
        if (!_launched) return;

        float now    = Time.time - _startTime;
        float psLife = _rise + sparkLifetime + 1f;
        int   sparks = _sparkPos.Length;

        // 虹の位相。時間で回るのでここだけは毎フレーム読むが、ループの外で1回だけ。
        // モードの ON/OFF や彩度は _rainbow に固定してあるので、
        // 昇っている最中に管理画面を触られても1発の見た目は最後まで変わらない
        float huePhase = 0f;
        if (_rainbow.Enabled)
        {
            var dop = DopamineModeController.Instance;
            if (dop != null) huePhase = dop.HuePhase;
        }

        // ── 玉（先頭の光）の色 ──
        //   玉は「燃えている火薬の塊」なので、火の粉と同じだけ色相を奪うと
        //   真っ青な玉が昇ってきて火に見えなくなる。虹へ 0.6 だけ寄せて、
        //   元の暖色を残す。
        //
        // ⚠️ FireworkLauncher がコンボ段階5で headColor / sparkColor を白へ寄せているが、
        //   虹色はそれを上書きして消す（白は彩度0なので、色相を置き換えると
        //   フィニッシャーの白寄せが見えなくなる）。
        //   静かに消える挙動なので、ドーパミンモード中に「コンボの白が出ない」と
        //   報告が来たらここが原因。虹色そのものが十分に「特別な1発」を伝えるため、
        //   意図的に譲っている
        var headRgb = _rainbow.Enabled
                      ? Color.Lerp(headColor,
                                   RainbowTint.Replace(_rainbow, headColor, 0f, huePhase),
                                   0.6f)
                      : headColor;

        // ── 火の粉 ──
        for (int i = 0; i < sparks; i++)
        {
            float age = now - _sparkBirth[i];

            if (age < 0f || age > sparkLifetime)
            {
                _buf[i].startSize         = 0f;
                _buf[i].startColor        = new Color32(0, 0, 0, 0);
                _buf[i].remainingLifetime = psLife;
                continue;
            }

            // 生まれた瞬間だけ玉の位置を拾い、以後は自分で落ちる
            if (age <= Time.deltaTime)
                _sparkPos[i] = HeadPositionAt(_sparkBirth[i]);
            else
                _sparkPos[i] += _sparkVel[i] * Time.deltaTime;

            float remain = 1f - age / sparkLifetime;

            // 火の粉は玉と違って完全に色相を置き換える（＝ Replace）。
            // 色相オフセットは粒の番号から毎回求める。黄金比の剰余なので
            // 計算は乗算1回で済み、配列を1本増やさずに済む。
            //
            // ⚠️ 型花火・昇りは Replace（色相を奪う）、画像花火だけ Rotate（色相を回す）。
            //   この非対称は意図的。理由は ImageFireworkEffect.cs の該当箇所と
            //   RainbowTint.Rotate を参照（揃えると投稿写真が虹色の塊になる）
            var sparkRgb = _rainbow.Enabled
                           ? RainbowTint.Replace(_rainbow, sparkColor,
                                                 RainbowTint.HueOffset(i), huePhase)
                           : sparkColor;

            _buf[i].position          = _sparkPos[i];
            _buf[i].startSize         = headSize * sparkSizeScale * remain;
            _buf[i].startColor        = new Color(sparkRgb.r, sparkRgb.g, sparkRgb.b,
                                                  remain * remain);
            _buf[i].remainingLifetime = psLife;
        }

        // ── 玉（先頭の光）──
        int head = _count - 1;
        if (now <= _rise)
        {
            // 到達直前に少し絞ると「開く前の溜め」に見える
            float u    = now / _rise;
            float taper = u > 0.85f ? Mathf.InverseLerp(1f, 0.85f, u) : 1f;

            _buf[head].position   = HeadPositionAt(now);
            _buf[head].startSize  = headSize * taper;
            // アルファ（taper）はそのまま。虹は色相しか触らないので「溜め」の絞りは残る
            _buf[head].startColor = new Color(headRgb.r, headRgb.g, headRgb.b, taper);
        }
        else
        {
            _buf[head].startSize  = 0f;
            _buf[head].startColor = new Color32(0, 0, 0, 0);
        }
        _buf[head].remainingLifetime = psLife;

        _ps.SetParticles(_buf, _count);
    }

    private bool PrepareMaterial()
    {
        var shader = Shader.Find(AdditiveShaderName)
                  ?? Shader.Find(VertexColorShaderName)
                  ?? particleShader;

        if (shader == null)
        {
            Debug.LogError("[LaunchTrail] 使えるシェーダーが見つかりません");
            return false;
        }

        _material = new Material(shader) { name = $"LaunchTrail_{shader.name}" };

        // フォールバックの ParticleUnlit にはこのプロパティが無いが、
        // SetFloat は存在しないプロパティに対して何もしないので分岐は不要。
        //
        // 虹色のときは底上げする。彩度を上げると輝度が落ちる（暖色の (1, 0.62, 0.22) は
        // 平均0.61 だが、飽和した青 (0,0,1) は 0.33）ので、同じ _Intensity のままだと
        // 昇りだけが目に見えて暗くなり、開花との明るさの関係が崩れるため
        float boost = _rainbow.Enabled ? _rainbow.IntensityBoost : 1f;
        _material.SetFloat(Shader.PropertyToID("_Intensity"),
                           Mathf.Max(0.01f, emissiveIntensity) * boost);
        return true;
    }

    private void SetupParticleSystem(float psLife)
    {
        _ps = GetComponent<ParticleSystem>();
        if (_ps == null) _ps = gameObject.AddComponent<ParticleSystem>();

        var main = _ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed      = 0f;
        main.startSize       = headSize;
        main.startLifetime   = psLife;
        main.maxParticles    = _count;
        main.gravityModifier = 0f;
        main.loop            = false;
        main.playOnAwake     = false;

        var emission = _ps.emission;
        emission.enabled = false;

        var shape = _ps.shape;
        shape.enabled = false;

        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr == null) psr = gameObject.AddComponent<ParticleSystemRenderer>();

        psr.renderMode        = ParticleSystemRenderMode.Billboard;
        psr.alignment         = ParticleSystemRenderSpace.View;
        psr.sharedMaterial    = _material;
        psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        psr.receiveShadows    = false;

        _ps.Clear();
        _ps.Play();
        _ps.SetParticles(_buf, _count);
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
    }
}
