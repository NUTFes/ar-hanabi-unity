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

    private struct Particle
    {
        public Transform tf;
        public Vector3   startPos;   // 発射位置（origin）
        public Vector3   targetPos;  // 絵の形の位置
        public Vector3   vel;        // Phase3 用の速度
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

    public void Launch(ParticleData data)
    {
        if (data == null || data.particles == null || data.particles.Length == 0)
        {
            Debug.LogWarning("[ImageFX] ParticleData is empty");
            Destroy(gameObject);
            return;
        }

        _origin    = transform.position;
        _count     = data.particles.Length;
        _particles = new Particle[_count];

        var shader = Shader.Find("Custom/ParticleColor")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Sprites/Default");

        // 絵全体の中心を origin に合わせるためのオフセット
        // 正規化座標 0.5,0.5 が origin になるよう計算
        for (int i = 0; i < _count; i++)
        {
            var p = data.particles[i];

            // 絵の形の最終位置（originを中心にimageScaleの大きさで展開）
            float lx = (p.x - 0.5f) * imageScale;
            float ly = (0.5f - p.y) * imageScale;
            var targetPos = _origin + new Vector3(lx, ly, 0f);

            // GO生成
            var go = new GameObject($"p{i}");
            go.transform.SetParent(transform);
            go.transform.position   = _origin;  // 全粒子が origin からスタート
            go.transform.localScale = Vector3.one * particleSize;

            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var mr = go.AddComponent<MeshRenderer>();
            var mat = new Material(shader);
            mat.SetColor("_Color", p.ToColor());
            mr.material          = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            _particles[i] = new Particle
            {
                tf         = go.transform,
                startPos   = _origin,
                targetPos  = targetPos,
                vel        = Vector3.zero,
                mat        = mat,
                baseSize   = particleSize,
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

        // ── Phase 1: 展開（origin → targetPos へ Lerp）──
        if (elapsed < expandTime)
        {
            float t = elapsed / expandTime;
            // EaseOut: 最初は速く、最後はゆっくり止まる
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
        // ── Phase 2: 維持（targetPos で静止）──
        else if (elapsed < expandTime + holdTime)
        {
            // Phase2 に入った瞬間だけ位置を確定させ、vel を初期化
            for (int i = 0; i < _count; i++)
            {
                if (_particles[i].tf == null) continue;
                _particles[i].tf.position = _particles[i].targetPos;
                _particles[i].vel         = Vector3.zero;
            }
        }
        // ── Phase 3: 落下・フェード ──
        else
        {
            float fadeElapsed = elapsed - expandTime - holdTime;
            float fadeRatio   = Mathf.Clamp01(1f - fadeElapsed / fadeTime);

            for (int i = 0; i < _count; i++)
            {
                if (_particles[i].tf == null) continue;

                // 重力加速
                _particles[i].vel   *= velocityDamping;
                _particles[i].vel.y -= gravityPerSec * dt;
                _particles[i].tf.position += _particles[i].vel * dt;

                // サイズでフェード
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