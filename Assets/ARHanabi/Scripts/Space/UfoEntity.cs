using UnityEngine;

// ===== UfoEntity =====
// 1体のUFO（空飛ぶ円盤 or エイリアン）。生成時に渡された始点から終点まで
// まっすぐ漂い、寿命が来たら自分で消える。花火が近くで開くと一度だけ「逃げる」。
//
// ── LaunchTrailEffect と同じ骨格にした理由 ──
//   このプロジェクトの動的エフェクトは「1 GameObject / 1 Material（インスタンス）/
//   1 ParticleSystem（Billboard, View空間, emission/shape無効, SetParticles()で駆動）」
//   という形に統一されている（SpriteRenderer は意図的に1件も使っていない）。
//   UFOだけ別の作りにする理由がないため、PrepareMaterial() / SetupParticleSystem() /
//   Update() / OnDestroy() の形をそのまま踏襲した。パーティクルは1粒で十分
//   （機体の見た目そのものはシェーダー側がUVから手続き的に描くため、粒側は
//   位置・大きさ・色（白＝無着色でシェーダーの _BaseColor を素通しする）を運ぶだけでよい）。
//
// ── 動きをVector3.Lerp(from,to,u)にした理由 ──
//   実際の速度をVector3で持つ方式だと、逃走時に「時間を短縮する」という
//   仕様（後述）が距離と速度の掛け算の整合を取り直す手間になる。
//   区間内の経過率 u=elapsed/duration で線形補間する方式なら、逃走時は
//   「今の位置を新しいfromに置き直し、時計をリセットし、durationを短くする」
//   だけで、位置の飛び（テレポート）なく滑らかに繋がる。
//
// ── 逃走演出を「速度を上げる本物の回避AI」にしなかった理由 ──
//   このシステムでUFOに求められている役割は「花火に反応した」という
//   印象を一目で与えることだけ。ステアリングや障害物回避は過剰で、
//   「同じ方向のまま、速く・短く離脱する」という単純な変化で十分に
//   「気づいて逃げた」ように見える。
public class UfoEntity : MonoBehaviour
{
    private const string ShaderName = "Custom/SpaceCraft";

    [Header("見た目")]
    [Tooltip("機体の大きさ（世界座標単位）。距離5でのカメラ視錐台の高さが約5.77なので、" +
             "花火の星(0.15〜0.3程度)よりはっきり大きく、画面全体に対しては控えめなサイズにする")]
    [SerializeField] private float craftSize = 0.6f;

    [Tooltip("上下にゆらす揺れの速さ(Hz)")]
    [SerializeField] private float bobHz = 0.6f;

    [Tooltip("上下にゆらす揺れの振幅（世界座標単位）")]
    [SerializeField] private float bobAmplitude = 0.12f;

    [Header("花火に反応して逃げる")]
    [Tooltip("開花位置が、UFOの『今の』位置からこの距離以内なら逃走を開始する")]
    [SerializeField] private float fleeDistance = 3.5f;

    [Tooltip("逃走中は通常の何倍の速さで移動するか")]
    [SerializeField] private float fleeSpeedMultiplier = 2.75f;

    [Tooltip("逃走発生時点で残っていた飛行時間を、さらにこの割合だけ短縮する（0.4 = 4割カット）")]
    [SerializeField, Range(0f, 0.9f)] private float fleeTimeCutRatio = 0.4f;

    [Tooltip("逃走中に加える上方向への追加の逃げ幅（世界座標単位）")]
    [SerializeField] private float fleeUpwardDrift = 1.2f;

    // パーティクルの寿命は「消えるタイミング」の実体ではなく上限のお守りとして
    // 十分大きい固定値にしておく。実際の消滅は Destroy(gameObject, ...) が担う
    // （LaunchTrailEffect 同様、GameObject 自体が消えれば Particle も一緒に消える）
    private const float ParticleLifetimeGuard = 60f;

    private ParticleSystem.Particle[] _buf;
    private ParticleSystem            _ps;
    private Material                  _material;

    private Vector3 _from, _to;
    private float   _duration;
    private float   _startTime;
    private bool    _launched;
    private bool    _fleeing;

    /// <summary>
    /// from から to へ flightSeconds かけて直線的に漂う。kind は 0=空飛ぶ円盤, 1=エイリアン。
    /// </summary>
    public void Launch(Vector3 from, Vector3 to, float flightSeconds, int kind)
    {
        if (!PrepareMaterial())
        {
            Destroy(gameObject);
            return;
        }

        _from     = from;
        _to       = to;
        _duration = Mathf.Max(0.1f, flightSeconds);

        _material.SetFloat("_Kind", kind);

        _buf = new ParticleSystem.Particle[1];
        _buf[0].position          = _from;
        _buf[0].velocity          = Vector3.zero;
        _buf[0].startSize         = craftSize;
        // 白＝無着色。実際の色・形はシェーダーが _BaseColor と _Kind から手続き的に作る
        _buf[0].startColor        = Color.white;
        _buf[0].startLifetime     = ParticleLifetimeGuard;
        _buf[0].remainingLifetime = ParticleLifetimeGuard;

        SetupParticleSystem();

        _startTime = Time.time;
        _launched  = true;

        Destroy(gameObject, _duration + 0.3f);
    }

    private void OnEnable()
    {
        if (SpaceModeController.Instance != null)
            SpaceModeController.Instance.OnFireworkBurst += OnFireworkBurst;
    }

    private void OnDisable()
    {
        if (SpaceModeController.Instance != null)
            SpaceModeController.Instance.OnFireworkBurst -= OnFireworkBurst;
    }

    // 花火が開くたびに呼ばれる。既に逃走中なら無視（再トリガーで加速が積み重ならないように）
    private void OnFireworkBurst(Vector3 burstPos)
    {
        if (!_launched || _fleeing) return;

        float elapsed     = Time.time - _startTime;
        Vector3 currentPos = Vector3.Lerp(_from, _to, Mathf.Clamp01(elapsed / _duration));

        if (Vector3.Distance(currentPos, burstPos) > fleeDistance) return;

        TriggerFlee(currentPos, elapsed);
    }

    // 「今いる位置」を新しい始点に置き直し、時計をリセットしてから残り区間を
    // 短い duration で引き直す。こうすると位置がテレポートせず、かつ以降は
    // 速く・短く目的地へ向かうように見える
    private void TriggerFlee(Vector3 currentPos, float elapsed)
    {
        _fleeing = true;

        float remaining          = Mathf.Max(0.05f, _duration - elapsed);
        float shortenedRemaining = remaining * (1f - fleeTimeCutRatio);
        float newDuration        = Mathf.Max(0.05f, shortenedRemaining / fleeSpeedMultiplier);

        Vector3 newTo = _to;
        newTo.y += fleeUpwardDrift;

        _from      = currentPos;
        _to        = newTo;
        _startTime = Time.time;
        _duration  = newDuration;

        // 逃走で到達が早まった分、消滅も前倒しする。Destroy は複数回呼んでも
        // 先に来た方が効くだけで安全なので、以前の予約を取り消す必要はない
        Destroy(gameObject, _duration + 0.3f);
    }

    private void Update()
    {
        if (!_launched) return;

        float elapsed = Time.time - _startTime;
        float u       = Mathf.Clamp01(elapsed / _duration);

        Vector3 pos = Vector3.Lerp(_from, _to, u);
        pos.y += Mathf.Sin(Time.time * bobHz * Mathf.PI * 2f) * bobAmplitude;

        _buf[0].position          = pos;
        _buf[0].remainingLifetime = ParticleLifetimeGuard;

        _ps.SetParticles(_buf, 1);
    }

    private bool PrepareMaterial()
    {
        var shader = Shader.Find(ShaderName);
        if (shader == null)
        {
            Debug.LogError($"[UfoEntity] シェーダーが見つかりません: {ShaderName}" +
                            "（Always Included Shaders への登録漏れの可能性があります）");
            return false;
        }

        _material = new Material(shader) { name = $"Ufo_{shader.name}" };
        return true;
    }

    private void SetupParticleSystem()
    {
        _ps = GetComponent<ParticleSystem>();
        if (_ps == null) _ps = gameObject.AddComponent<ParticleSystem>();

        var main = _ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed      = 0f;
        main.startSize       = craftSize;
        main.startLifetime   = ParticleLifetimeGuard;
        main.maxParticles    = 1;
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
        _ps.SetParticles(_buf, 1);
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
    }
}
