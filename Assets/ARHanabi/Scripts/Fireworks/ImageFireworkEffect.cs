using UnityEngine;

// ===== ImageFireworkEffect =====
// 画像から変換したパーティクル群を「ParticleSystem 1個 + SetParticles()」で描画する花火エフェクト。
// 以前は 1粒 = 1GameObject（MeshFilter + MeshRenderer + Material）だったため
// 解像度128（最大16,384粒）× 同時2発で最大32,768 GameObject / Material / DrawCall となり、
// 生成時に0.5〜1.0秒のフリーズ、描画は2〜5FPSまで落ちていた。
// 現在は1発あたり GameObject 1個 / Material 1個 / DrawCall 1回で済む。
//
// アニメーション3フェーズ（旧実装の挙動をそのまま再現）:
//   Phase 1 (0          ~ expandTime): 中心から絵の形に展開（ease = 1-(1-t)^2）
//   Phase 2 (expandTime ~ +holdTime) : 絵の形を維持
//   Phase 3 (以降 fadeTime)          : 重力で落下しつつ縮小フェード

public class ImageFireworkEffect : MonoBehaviour
{
    [Header("フェーズ設定")]
    [Tooltip("絵の形に展開するまでの秒数")]
    public float expandTime  = 0.8f;
    [Tooltip("絵の形を維持する秒数")]
    public float holdTime    = 0.6f;
    [Tooltip("落下・フェードの秒数")]
    public float fadeTime    = 1.5f;

    [Header("表示設定")]
    [Tooltip("絵の表示サイズ（ワールド座標）")]
    public float imageScale   = 6f;
    [Tooltip("粒子1つのサイズ")]
    public float particleSize = 0.15f;

    [Header("落下設定")]
    public float gravityPerSec   = 2f;
    public float velocityDamping = 0.92f;

    [Header("粒数制限")]
    [Tooltip("1発あたりの最大粒数。超えた分は等間隔で間引く")]
    public int maxParticles = 6000;

    [Header("シェーダー設定")]
    [Tooltip("頂点カラー非対応シェーダーへのフォールバック用。通常は Custom/ParticleUnlit が自動で使われる")]
    [SerializeField] private Shader particleShader;

    // 粒ごとの状態。ParticleSystem.Particle[] は位置とサイズと色だけを持たせ、
    // 目標位置・速度・基準色は自前の配列で管理する（GetParticles を呼ばずに済ませるため）。
    private ParticleSystem.Particle[] _buf;
    private Vector3[]                 _targets;
    private Vector3[]                 _velocities;
    private Color32[]                 _baseColors;

    private ParticleSystem _ps;
    private Material       _material;
    private int            _count;
    private float          _startTime;
    private bool           _launched;
    private Vector3        _origin;

    private const string VertexColorShaderName = "Custom/ParticleUnlit";

    private float TotalLifetime => expandTime + holdTime + fadeTime;

    /// <summary>
    /// FireworkLauncher から動的生成時にシェーダーを注入するためのメソッド。
    /// 互換のため残しているが、渡されたシェーダーは
    /// Custom/ParticleUnlit が見つからなかった場合のフォールバックとしてのみ使う。
    /// Launch() を呼ぶ前に呼び出すこと。
    /// </summary>
    public void SetShader(Shader shader)
    {
        if (shader != null)
            particleShader = shader;
    }

    public void Launch(ParticleData data)
    {
        if (data == null || data.particles == null || data.particles.Length == 0)
        {
            Debug.LogWarning("[ImageFX] ParticleData is empty");
            Destroy(gameObject);
            return;
        }

        if (!PrepareMaterial())
        {
            Destroy(gameObject);
            return;
        }

        _origin = transform.position;

        // ---- 粒数の間引き（等間隔ストライド） ----
        var   src    = data.particles;
        int   limit  = Mathf.Max(1, maxParticles);
        float stride = 1f;

        if (src.Length > limit)
        {
            stride = (float)src.Length / limit;
            _count = limit;
            Debug.Log($"[ImageFX] 粒数を間引きました: {src.Length} 粒 → {_count} 粒 " +
                      $"(maxParticles={limit}, stride={stride:F2})");
        }
        else
        {
            _count = src.Length;
        }

        _buf        = new ParticleSystem.Particle[_count];
        _targets    = new Vector3[_count];
        _velocities = new Vector3[_count];
        _baseColors = new Color32[_count];

        float life = TotalLifetime + 1f;

        for (int i = 0; i < _count; i++)
        {
            var p = src[stride > 1f ? Mathf.Min(src.Length - 1, (int)(i * stride)) : i];

            // 旧実装と同じ座標計算（Y は上下反転）
            float lx = (p.x - 0.5f) * imageScale;
            float ly = (0.5f - p.y) * imageScale;

            _targets[i]    = _origin + new Vector3(lx, ly, 0f);
            _velocities[i] = Vector3.zero;
            _baseColors[i] = p.ToColor();

            _buf[i].position          = _origin;   // Phase 1 開始時は全粒が中心
            _buf[i].velocity          = Vector3.zero;
            _buf[i].startSize         = particleSize;
            _buf[i].startColor        = _baseColors[i];
            _buf[i].startLifetime     = life;
            _buf[i].remainingLifetime = life;
        }

        SetupParticleSystem(life);
        _ps.SetParticles(_buf, _count);

        _startTime = Time.time;
        _launched  = true;
        Debug.Log($"[ImageFX] Launched {_count} particles @ {_origin} " +
                  $"(GameObject 1 / Material 1 / DrawCall 1)");
        Destroy(gameObject, TotalLifetime + 0.5f);
    }

    /// <summary>
    /// 頂点カラーが効くシェーダーを優先してマテリアルを1個だけ生成する。
    /// </summary>
    private bool PrepareMaterial()
    {
        var shader = Shader.Find(VertexColorShaderName);

        if (shader == null)
        {
            shader = particleShader;
            if (shader == null)
            {
                Debug.LogError($"[ImageFX] {VertexColorShaderName} も " +
                               "フォールバック用シェーダーも見つかりません。" +
                               "Inspector の Particle Shader フィールドに " +
                               "Assets/ARHanabi/Shaders/ParticleUnlit.shader をアサインしてください。");
                return false;
            }

            Debug.LogWarning($"[ImageFX] {VertexColorShaderName} が見つからないため " +
                             $"'{shader.name}' にフォールバックします。" +
                             "このシェーダーは uniform の単色を使い頂点カラーを読まないため、" +
                             "全粒が同じ色で描画されます（絵の色が再現されません）。");
        }

        _material = new Material(shader) { name = $"ImageFX_{shader.name}" };
        Debug.Log($"[ImageFX] Using shader: {shader.name}");
        return true;
    }

    /// <summary>
    /// ParticleSystem をコードだけで組み立てる（Inspector 作業を発生させない）。
    /// 動的 AddComponent では [RequireComponent] が効かないケースがあるため自前で確保する。
    /// </summary>
    private void SetupParticleSystem(float life)
    {
        _ps = GetComponent<ParticleSystem>();
        if (_ps == null) _ps = gameObject.AddComponent<ParticleSystem>();

        var main = _ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed      = 0f;
        main.startSize       = particleSize;
        main.startLifetime   = life;
        main.maxParticles    = _count;
        main.gravityModifier = 0f;      // 落下は Update() で自前計算する
        main.loop            = false;
        main.playOnAwake     = false;

        var emission = _ps.emission;
        emission.enabled = false;       // 発生器は止めて SetParticles だけで管理する

        var shape = _ps.shape;
        shape.enabled = false;

        // ParticleSystem を AddComponent すると通常は同時に付くが、念のため自前で確保する
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr == null) psr = gameObject.AddComponent<ParticleSystemRenderer>();

        psr.renderMode        = ParticleSystemRenderMode.Billboard;
        psr.alignment         = ParticleSystemRenderSpace.View;
        psr.sharedMaterial    = _material;
        psr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        psr.receiveShadows    = false;

        _ps.Clear();
        _ps.Play();
    }

    private void Update()
    {
        if (!_launched) return;

        float elapsed = Time.time - _startTime;
        float dt      = Time.deltaTime;
        float life    = TotalLifetime;

        if (elapsed < expandTime)
        {
            // Phase 1: 中心 → 絵の形（旧実装と同じイージング）
            float t    = elapsed / expandTime;
            float ease = 1f - (1f - t) * (1f - t);

            for (int i = 0; i < _count; i++)
            {
                _buf[i].position          = Vector3.Lerp(_origin, _targets[i], ease);
                _buf[i].startSize         = particleSize;
                _buf[i].startColor        = _baseColors[i];
                _buf[i].remainingLifetime = life;
            }
        }
        else if (elapsed < expandTime + holdTime)
        {
            // Phase 2: 絵の形を維持
            for (int i = 0; i < _count; i++)
            {
                _buf[i].position          = _targets[i];
                _velocities[i]            = Vector3.zero;
                _buf[i].startSize         = particleSize;
                _buf[i].startColor        = _baseColors[i];
                _buf[i].remainingLifetime = life;
            }
        }
        else
        {
            // Phase 3: 落下しながら縮小＋アルファフェード
            float fadeElapsed = elapsed - expandTime - holdTime;
            float fadeRatio   = Mathf.Clamp01(1f - fadeElapsed / fadeTime);
            byte  alpha       = (byte)Mathf.RoundToInt(255f * fadeRatio);

            for (int i = 0; i < _count; i++)
            {
                var v = _velocities[i];
                v *= velocityDamping;
                v.y -= gravityPerSec * dt;
                _velocities[i] = v;

                var c = _baseColors[i];

                _buf[i].position          += v * dt;
                _buf[i].startSize         = Mathf.Max(particleSize * fadeRatio, 0f);
                _buf[i].startColor        = new Color32(c.r, c.g, c.b, alpha);
                _buf[i].remainingLifetime = life;
            }
        }

        // GetParticles は呼ばず、自前配列を書き換えて一括反映する
        _ps.SetParticles(_buf, _count);
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
    }
}
