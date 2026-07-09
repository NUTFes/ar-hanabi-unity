using UnityEngine;

// ===== ImageFireworkEffect =====
// アニメーション3フェーズ:
//   Phase 1 (0    ~ expandTime): 中心から絵の形に展開
//   Phase 2 (expandTime ~ holdTime): 絵の形を維持
//   Phase 3 (holdTime ~ lifetime): 重力で落下・縮小フェード

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
    public float imageScale  = 6f;
    [Tooltip("粒子1つのサイズ")]
    public float particleSize = 0.15f;

    [Header("落下設定")]
    public float gravityPerSec   = 2f;
    public float velocityDamping = 0.92f;

    [Header("シェーダー設定")]
    [Tooltip("Assets/ARHanabi/Shaders/ParticleColor.shader をここにアサインする")]
    [SerializeField] private Shader particleShader;

    private struct Particle
    {
        public Transform tf;
        public Vector3   startPos;
        public Vector3   targetPos;
        public Vector3   vel;
        public Material  mat;
        public float     baseSize;
    }

    private Particle[] _particles;
    private int        _count;
    private float      _startTime;
    private bool       _launched;
    private Vector3    _origin;

    private static Mesh _quad;

    private float TotalLifetime => expandTime + holdTime + fadeTime;

    private void Awake()
    {
        if (_quad != null) return;
        _quad = new Mesh();
        _quad.vertices  = new Vector3[] {
            new(-0.5f,-0.5f,0), new(0.5f,-0.5f,0),
            new(0.5f, 0.5f,0),  new(-0.5f,0.5f,0)
        };
        _quad.triangles = new int[] { 0,2,1, 0,3,2 };
        _quad.uv        = new Vector2[] {
            new(0,0), new(1,0), new(1,1), new(0,1)
        };
        _quad.RecalculateBounds();
    }

    /// <summary>
    /// FireworkLauncher から動的生成時にシェーダーを注入するためのメソッド。
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

        // Inspector アサイン優先、なければ Shader.Find にフォールバック
        var shader = particleShader != null
                  ? particleShader
                  : Shader.Find("Custom/ParticleColor");
                
        Debug.Log($"[ImageFX] Using shader: {(shader != null ? shader.name : "NULL")}");
        if (shader == null)
        {
            Debug.LogError("[ImageFX] ParticleColor シェーダーが見つかりません。" +
                           "Inspector の Particle Shader フィールドに " +
                           "Assets/ARHanabi/Shaders/ParticleColor.shader をアサインしてください。");
            Destroy(gameObject);
            return;
        }

        _origin    = transform.position;
        _count     = data.particles.Length;
        _particles = new Particle[_count];

        for (int i = 0; i < _count; i++)
        {
            var p = data.particles[i];

            float lx = (p.x - 0.5f) * imageScale;
            float ly = (0.5f - p.y) * imageScale;
            var targetPos = _origin + new Vector3(lx, ly, 0f);

            var go = new GameObject($"p{i}");
            go.transform.SetParent(transform);
            go.transform.position   = _origin;
            go.transform.localScale = Vector3.one * particleSize;

            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var mr  = go.AddComponent<MeshRenderer>();
            var mat = new Material(shader);
            mat.SetColor("_Color", p.ToColor());
            if (i < 5) Debug.Log($"[ImageFX] p{i} color: {p.ToColor()} r={p.r} g={p.g} b={p.b}");
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            _particles[i] = new Particle
            {
                tf        = go.transform,
                startPos  = _origin,
                targetPos = targetPos,
                vel       = Vector3.zero,
                mat       = mat,
                baseSize  = particleSize,
            };
        }

        _startTime = Time.time;
        _launched  = true;
        Debug.Log($"[ImageFX] Launched {_count} particles @ {_origin}");
        Destroy(gameObject, TotalLifetime + 0.5f);
    }

    private void Update()
    {
        if (!_launched) return;

        float elapsed = Time.time - _startTime;
        float dt      = Time.deltaTime;

        if (elapsed < expandTime)
        {
            float t    = elapsed / expandTime;
            float ease = 1f - (1f - t) * (1f - t);

            for (int i = 0; i < _count; i++)
            {
                if (_particles[i].tf == null) continue;
                _particles[i].tf.position   = Vector3.Lerp(
                    _particles[i].startPos,
                    _particles[i].targetPos,
                    ease);
                _particles[i].tf.localScale = Vector3.one * _particles[i].baseSize;
            }
        }
        else if (elapsed < expandTime + holdTime)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_particles[i].tf == null) continue;
                _particles[i].tf.position = _particles[i].targetPos;
                _particles[i].vel         = Vector3.zero;
            }
        }
        else
        {
            float fadeElapsed = elapsed - expandTime - holdTime;
            float fadeRatio   = Mathf.Clamp01(1f - fadeElapsed / fadeTime);

            for (int i = 0; i < _count; i++)
            {
                if (_particles[i].tf == null) continue;

                _particles[i].vel   *= velocityDamping;
                _particles[i].vel.y -= gravityPerSec * dt;
                _particles[i].tf.position += _particles[i].vel * dt;

                float s = _particles[i].baseSize * fadeRatio;
                _particles[i].tf.localScale = Vector3.one * Mathf.Max(s, 0f);
            }
        }
    }

    private void OnDestroy()
    {
        if (_particles == null) return;
        foreach (var p in _particles)
            if (p.mat != null) Destroy(p.mat);
    }
}