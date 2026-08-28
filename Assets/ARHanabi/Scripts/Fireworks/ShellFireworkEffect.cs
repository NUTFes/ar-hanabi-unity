using UnityEngine;

// ===== ShellFireworkEffect =====
// 打ち上げ花火（割物・ポカ物・小割物）をコードで描く。ShellPreset で型を指定する。
//
// ── なぜプレハブの ParticleSystem をやめてコード駆動にしたか ──
//   従来の FireworkLaunch.prefab は Sphere シェイプで300粒を1回バーストするだけで、
//   TrailModule / SizeModule / ForceModule / NoiseModule がすべて無効だった。
//   そのため
//     ・尾を引かない  → 「菊」の定義（星が尾を引く）を満たせない
//     ・重力で垂れない → 「冠」「柳」が作れない
//     ・多段階にできない → 「千輪」（一瞬遅れて小玉が一斉に開く）が原理的に不可能
//   ParticleSystem のカーブをプレハブ側で組む方法もあるが、型ごとにプレハブを
//   量産することになり、数値の見通しも悪い。
//   ImageFireworkEffect が既に「1 GameObject / 1 Material / 1 DrawCall で
//   数千粒を SetParticles で動かす」方式で動いているので、それに合わせる。
//
// ── 星の運動 ──
//   実際の花火は「割火薬で高速に飛び出す → 空気抵抗で急減速 → 重力で垂れる」。
//   位置を解析式で出しているので、フレームレートに依存せず毎フレーム同じ軌跡になる。
//     v(t) = v0·exp(-t/τ)
//     x(t) = v0·τ·(1 - exp(-t/τ)) − ½·g·t²
//   第1項が「開いて止まる」動き、第2項が「垂れる」動き。
//   τ（dragTau）が小さいほど早く止まって丸くなり、大きいほど遠くまで伸びる。
//
// ── 尾を自前の粒で作る ──
//   Unity の TrailModule は ParticleSystem 側の更新で軌跡を作るため、
//   SetParticles で位置を直接書き込むこの方式では追従が保証されない。
//   実際の花火の尾は「燃えかすがその場に残って消えていく」ものなので、
//   星の軌跡上に粒を置いて減衰させるほうが物理的にも正しい。
//
// 毎フレームのアロケーションはゼロ。配列は Launch() で確保して使い回す。

public class ShellFireworkEffect : MonoBehaviour
{
    [Header("表示設定")]
    [Tooltip("全体の大きさ倍率。FireworkLauncher が視錐台から計算して渡す")]
    public float scale = 1f;

    [Tooltip("1発あたりの最大粒数。超えた分は星を間引く")]
    public int maxParticles = 5000;

    [Header("シェーダー設定")]
    [Tooltip("Custom/ParticleAdditive が見つからないときのフォールバック")]
    [SerializeField] private Shader particleShader;

    private const string AdditiveShaderName    = "Custom/ParticleAdditive";
    private const string VertexColorShaderName = "Custom/ParticleUnlit";

    // ── 粒の役割 ──
    private enum Role : byte { Star, Trail, ChildStar }

    // 粒ごとの静的な情報。Update では位置と色だけを計算する
    private struct Grain
    {
        public Role    role;
        public Vector3 origin;      // 出発点（親玉の中心、または小玉の中心）
        public Vector3 dir;         // 単位方向
        public float   speed;       // 初速
        public float   birth;       // 生まれる時刻（発射からの秒）
        public float   life;        // 寿命[秒]
        public float   size;
        public Color   colorA;
        public Color   colorB;
        public float   twinklePhase;
        // 尾の粒は「親の星の軌跡上の一点」に固定されるので、
        // 生成時にその位置を計算して origin に入れてしまう（dir/speed は 0）
    }

    private ParticleSystem.Particle[] _buf;
    private Grain[]                   _grains;
    private int                       _count;

    private ParticleSystem _ps;
    private Material       _material;
    private ShellPreset    _preset;
    private float          _startTime;
    private bool           _launched;
    private Vector3        _origin;
    private float          _total;

    /// <summary>FireworkLauncher から動的生成時にシェーダーを注入する</summary>
    public void SetShader(Shader shader)
    {
        if (shader != null) particleShader = shader;
    }

    // ── 発射 ──

    public void Launch(ShellPreset preset, float sizeScale)
    {
        if (preset == null)
        {
            Debug.LogWarning("[Shell] preset が null です");
            Destroy(gameObject);
            return;
        }

        if (!PrepareMaterial())
        {
            Destroy(gameObject);
            return;
        }

        _preset = preset;
        scale   = sizeScale;
        _origin = transform.position;
        _total  = preset.TotalLifetime;

        BuildGrains(preset);
        SetupParticleSystem();

        _startTime = Time.time;
        _launched  = true;

        Debug.Log($"[Shell] {preset.name}（{preset.category}）を発射: " +
                  $"{_count} 粒 / {_total:F2}s / scale={scale:F2} @ {_origin}");

        Destroy(gameObject, _total + 0.4f);
    }

    // ── 粒の組み立て ──
    private void BuildGrains(ShellPreset p)
    {
        int wanted = p.TotalParticleCount;

        // 上限を超える場合は星を間引く（尾と小玉は星に比例して減る）
        int starCount = Mathf.Max(1, p.starCount);
        if (wanted > maxParticles)
        {
            float ratio = maxParticles / (float)wanted;
            starCount = Mathf.Max(8, (int)(starCount * ratio));
            Debug.Log($"[Shell] 粒数を間引きました: {wanted} → 上限 {maxParticles} " +
                      $"(星 {p.starCount} → {starCount})");
        }

        int trailPer   = Mathf.Max(0, p.trailPerStar);
        int childCount = Mathf.Max(0, p.childCount);
        int childStars = childCount > 0 ? Mathf.Max(1, p.childStarCount) : 0;

        _count  = starCount + starCount * trailPer + childCount * childStars;
        _buf    = new ParticleSystem.Particle[_count];
        _grains = new Grain[_count];

        int coreStars = p.coreSpeedRatio > 0f
                        ? (int)(starCount * p.coreStarRatio)
                        : 0;

        int w = 0;

        // ── 親玉の星 ──
        for (int i = 0; i < starCount; i++)
        {
            bool isCore = i < coreStars;

            var dir = SampleDirection(p, i, starCount);

            float speed = p.burstSpeed
                        * (1f + Random.Range(-p.speedJitter, p.speedJitter))
                        * (isCore ? p.coreSpeedRatio : 1f);

            float life = p.starLifetime * (1f + Random.Range(-p.lifetimeJitter, p.lifetimeJitter));
            float size = p.starSize     * (1f + Random.Range(-p.sizeJitter, p.sizeJitter));

            var cA = isCore ? p.coreColor : p.colorA;
            var cB = p.colorShiftAt > 0f ? p.colorB : cA;

            _grains[w++] = new Grain
            {
                role         = Role.Star,
                origin       = _origin,
                dir          = dir,
                speed        = speed,
                birth        = 0f,
                life         = life,
                size         = size,
                colorA       = cA,
                colorB       = cB,
                twinklePhase = Random.Range(0f, Mathf.PI * 2f),
            };
        }

        // ── 尾 ──
        // 星の軌跡上の一点に置いて、その場で減衰させる（燃えかすが残るイメージ）
        for (int s = 0; s < starCount && trailPer > 0; s++)
        {
            var star = _grains[s];

            for (int t = 1; t <= trailPer; t++)
            {
                float at = t * p.trailSpacing;
                if (at >= star.life) break;

                _grains[w++] = new Grain
                {
                    role         = Role.Trail,
                    origin       = StarPositionAt(star, at, p),
                    dir          = Vector3.zero,
                    speed        = 0f,
                    birth        = at,
                    life         = p.trailLifetime,
                    size         = star.size * p.trailSizeScale,
                    colorA       = star.colorA * p.trailBrightness,
                    colorB       = star.colorB * p.trailBrightness,
                    twinklePhase = Random.Range(0f, Mathf.PI * 2f),
                };
            }
        }

        // ── 千輪の小玉 ──
        // 親玉が開いたあと、一瞬遅れて小玉が一斉に開く
        for (int c = 0; c < childCount; c++)
        {
            // 小玉が散る位置。親玉と同じ運動で childDelay の時点まで飛ばす
            var scatterDir = Random.onUnitSphere;
            scatterDir.z *= 0.35f;      // 奥行きを浅くして画面内に収める

            float scatterSpeed = p.burstSpeed * p.childScatterRatio;
            var   childOrigin  = _origin + DisplacementAt(scatterDir, scatterSpeed,
                                                          p.childDelay, p.dragTau,
                                                          p.EffectiveGravity);

            var childColor = p.childRandomColor
                             ? ShellPreset.ChildPalette[Random.Range(0, ShellPreset.ChildPalette.Length)]
                             : p.colorA;

            for (int i = 0; i < childStars; i++)
            {
                var dir = Random.onUnitSphere;
                dir.z *= 0.4f;
                if (dir.sqrMagnitude < 1e-6f) dir = Vector3.up;
                dir.Normalize();

                _grains[w++] = new Grain
                {
                    role         = Role.ChildStar,
                    origin       = childOrigin,
                    dir          = dir,
                    speed        = p.childBurstSpeed * (1f + Random.Range(-0.2f, 0.2f)),
                    birth        = p.childDelay,
                    life         = p.childLifetime * (1f + Random.Range(-0.15f, 0.15f)),
                    size         = p.starSize * 0.85f,
                    colorA       = childColor,
                    colorB       = childColor,
                    twinklePhase = Random.Range(0f, Mathf.PI * 2f),
                };
            }
        }

        _count = w;   // break で打ち切った分を反映

        // ── ParticleSystem 側の初期化 ──
        // 生まれる前の粒は画面に出したくないので、サイズ0・透明で中心に置いておく
        float psLife = _total + 1f;
        for (int i = 0; i < _count; i++)
        {
            _buf[i].position          = _grains[i].origin;
            _buf[i].velocity          = Vector3.zero;
            _buf[i].startSize         = 0f;
            _buf[i].startColor        = new Color32(0, 0, 0, 0);
            _buf[i].startLifetime     = psLife;
            _buf[i].remainingLifetime = psLife;
        }
    }

    // 形に応じた方向を返す。
    // 球は「見た目が偏らないように」黄金角のらせん配置にする。
    // 乱数だけで散らすと粒がまとまって斑になり、玉として見えにくい。
    //
    // ── Sphere/Ring/UpperHemisphere/Heart は戻り値を .normalized している ──
    //   つまり大きさ（半径方向の情報）を捨てて「向き」だけを使っている。
    //   これは Heart が本来のハートの輪郭比率を潰してしまっている既知の不具合
    //   （リングのように偏って見える）だが、既存の見た目を変える修正はここでは行わない。
    //
    // ── 新しい形（Saucer/RingedPlanet/SpiralGalaxy/Comet）は正規化しない ──
    //   円盤・環・渦巻・彗星の尾はどれも「中心からの距離（半径）が場所によって違う」
    //   ことでシルエットが決まる。DisplacementAt は dir * (speed * drag * scale) を
    //   使うため、dir の大きさがそのまま最終半径の比率になる。normalized してしまうと
    //   全ての星が同じ半径（＝ただの球）に潰れてしまうので、ここでは意図的に
    //   大きさ 0〜1 のベクトルのまま返す。scale は呼び出し元
    //   （FireworkLauncher.LaunchShellWithPreset）が |dir|=1 を前提に画面フィット半径を
    //   逆算しているため、大きさが 1 を超えなければ既存の仕組みに手を入れずに済む。
    private static Vector3 SampleDirection(ShellPreset p, int index, int total)
    {
        switch (p.shape)
        {
            case ShellShape.Ring:
            {
                // 環。わずかに厚みを持たせて板に見えないようにする
                float a = index / (float)total * Mathf.PI * 2f;
                var   v = new Vector3(Mathf.Cos(a), Mathf.Sin(a), Random.Range(-0.08f, 0.08f));
                return v.normalized;
            }

            case ShellShape.Heart:
            {
                // 媒介変数表示のハート。奥行きは薄くして正面から形が読めるようにする
                float t = index / (float)total * Mathf.PI * 2f;
                float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
                float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t)
                        - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);
                var v = new Vector3(x / 16f, y / 16f, Random.Range(-0.1f, 0.1f));
                return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.up;
            }

            case ShellShape.UpperHemisphere:
            {
                var v = GoldenSpiral(index, total);
                v.y = Mathf.Abs(v.y) * 0.9f + 0.1f;   // 上向きに寄せる
                return v.normalized;
            }

            case ShellShape.Saucer:
            {
                // 薄い円盤。y だけ discThickness で潰して、正面から見ると
                // 「縁が丸く、厚みが薄い」UFOのシルエットになる。z は極小のノイズだけ
                float a = index / (float)total * Mathf.PI * 2f;
                return new Vector3(
                    Mathf.Cos(a),
                    Mathf.Sin(a) * p.discThickness,
                    Random.Range(-0.05f, 0.05f));
            }

            case ShellShape.RingedPlanet:
            {
                // 本体（小さな球）と環（傾いた大きな円）を index で振り分けて描く。
                // 環だけ正規化しない別の半径・傾きを持つので、球と同じ
                // SampleDirection 内で分岐させたほうが呼び出し側を汚さない
                bool isRing = (index % 100) >= 65;   // 65%を本体、残り35%を環に振り分ける
                if (!isRing)
                {
                    // 本体: 黄金螺旋を縮小しただけの小さな球
                    return GoldenSpiral(index, total) * 0.4f;
                }

                float a = index / (float)total * Mathf.PI * 2f;
                var   ring = Quaternion.Euler(p.ringTilt, 0f, 0f)
                           * new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                return ring * Mathf.Min(1f, p.ringRadius);
            }

            case ShellShape.SpiralGalaxy:
            {
                // 腕ごとに開始角をずらし、t（0→1）が増えるほど角度も半径も進む
                // 対数螺旋もどき。z はごく浅くして正面から渦巻きが読めるようにする
                float t = index / (float)total;
                float a = p.spiralArms > 0
                          ? (index % p.spiralArms) * (Mathf.PI * 2f / p.spiralArms) + t * Mathf.PI * 2f * 2f
                          : t * Mathf.PI * 2f;
                float r = t;
                return new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, Random.Range(-0.05f, 0.05f));
            }

            case ShellShape.Comet:
            {
                // 星の大半（1-tailBias）は中心近くに小さい半径で残して明るい「頭」を作り、
                // 残り（tailBias）は決め打ちの一方向（Vector3.up）へ半径を伸ばして「尾」にする。
                // 尾の向きを星ごとにばらけさせず固定するのが、対称な玉と違って
                // 彗星に見えるポイント
                bool isTail = (index / (float)total) < p.tailBias;
                if (!isTail)
                    return GoldenSpiral(index, total) * Random.Range(0.05f, 0.2f);

                float r = Random.Range(0.4f, 0.95f);
                var   spread = new Vector3(Random.Range(-0.05f, 0.05f), 0f, Random.Range(-0.05f, 0.05f));
                return Vector3.up * r + spread;
            }

            default:
                return GoldenSpiral(index, total);
        }
    }

    // 黄金角を使った球面上の等間隔配置（Fibonacci sphere）
    private static Vector3 GoldenSpiral(int index, int total)
    {
        float k     = index + 0.5f;
        float phi   = Mathf.Acos(1f - 2f * k / total);
        float theta = Mathf.PI * (1f + Mathf.Sqrt(5f)) * k;

        return new Vector3(
            Mathf.Cos(theta) * Mathf.Sin(phi),
            Mathf.Cos(phi),
            Mathf.Sin(theta) * Mathf.Sin(phi));
    }

    // 空気抵抗と重力を含めた変位。
    //   x(t) = v0·τ·(1 - exp(-t/τ)) − ½·g·t²·up
    //
    // ── dragTau（τ）を変えても最終的な広がりは変わらない ──
    //   t→∞ での変位は v0·τ（FireworkLauncher が radius = burstSpeed·dragTau として
    //   scale を逆算する値そのもの）。scale = halfView / (burstSpeed·dragTau) なので、
    //   実際の到達距離 = v0·τ·scale = v0·τ·halfView/(burstSpeed·τ) となり τ が約分されて
    //   消える。つまり dragTau は「最終的にどこまで広がるか」には効かず、
    //   「そこに到達するまでの速さ（拡散の速さ）」だけを変える。
    //   拡散をゆっくりにしたいときは dragTau を大きくすればよい。
    //
    // ── ただし星のサイズも scale で決まる ──
    //   ShellPreset.starSize はここではなく Update() で
    //   `g.size * sizeRatio * scale` として使われる。scale は上の理由で
    //   dragTau に反比例するため、dragTau だけを大きくすると星が小さく描画される
    //   （広がりの速さを変えるついでに粒が縮んでしまう）。
    //   ShellPreset.DefaultLibrary() では dragTau を上げた分だけ starSize も
    //   同じ比率で上げて、見た目の粒サイズが変わらないようにしてある。
    //   dragTau を調整するときは starSize も一緒に、同じ倍率で動かすこと。
    private Vector3 DisplacementAt(Vector3 dir, float speed, float t,
                                   float dragTau, float gravity)
    {
        float tau  = Mathf.Max(0.01f, dragTau);
        float drag = tau * (1f - Mathf.Exp(-t / tau));
        return dir * (speed * drag * scale)
             + Vector3.down * (0.5f * gravity * t * t * scale);
    }

    private Vector3 StarPositionAt(in Grain g, float t, ShellPreset p)
        => g.origin + DisplacementAt(g.dir, g.speed, t, p.dragTau, p.EffectiveGravity);

    // ── マテリアル ──
    private bool PrepareMaterial()
    {
        var shader = Shader.Find(AdditiveShaderName);

        if (shader == null)
        {
            shader = Shader.Find(VertexColorShaderName);
            if (shader != null)
            {
                Debug.LogWarning($"[Shell] {AdditiveShaderName} が見つからないため " +
                                 $"{VertexColorShaderName} にフォールバックします。" +
                                 "粒が丸くならず加算発光も効きません");
            }
        }

        if (shader == null)
        {
            shader = particleShader;
            if (shader == null)
            {
                Debug.LogError($"[Shell] {AdditiveShaderName} も {VertexColorShaderName} も " +
                               "フォールバックシェーダーも見つかりません");
                return false;
            }
        }

        _material = new Material(shader) { name = $"Shell_{shader.name}" };
        return true;
    }

    private void SetupParticleSystem()
    {
        _ps = GetComponent<ParticleSystem>();
        if (_ps == null) _ps = gameObject.AddComponent<ParticleSystem>();

        float psLife = _total + 1f;

        var main = _ps.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startSpeed      = 0f;
        main.startSize       = 0.1f;
        main.startLifetime   = psLife;
        main.maxParticles    = _count;
        main.gravityModifier = 0f;      // 位置は Update() で解析式から出す
        main.loop            = false;
        main.playOnAwake     = false;

        var emission = _ps.emission;
        emission.enabled = false;       // 発生器は使わず SetParticles だけで管理する

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

    // ── 毎フレームの更新 ──
    private void Update()
    {
        if (!_launched) return;

        float now    = Time.time - _startTime;
        float psLife = _total + 1f;
        var   p      = _preset;

        // 花雷の閃光。開いた瞬間だけ全粒を白く飛ばす
        float flash = 0f;
        if (p.flashStrength > 0f && now < p.flashDuration)
            flash = p.flashStrength * (1f - now / p.flashDuration);

        for (int i = 0; i < _count; i++)
        {
            ref var g = ref _grains[i];

            float t = now - g.birth;

            // まだ生まれていない / もう寿命が尽きた粒は消しておく
            if (t < 0f || t > g.life)
            {
                _buf[i].startSize         = 0f;
                _buf[i].startColor        = new Color32(0, 0, 0, 0);
                _buf[i].remainingLifetime = psLife;
                continue;
            }

            float u = t / g.life;          // 0→1 の進行度
            float remain = 1f - u;

            // ── 位置 ──
            Vector3 pos;
            if (g.role == Role.Trail)
            {
                // 尾はその場に留まり、わずかに沈むだけ（燃えかすが落ちる分）
                pos = g.origin + Vector3.down * (0.35f * t * t * scale);
            }
            else
            {
                float tau     = g.role == Role.ChildStar ? p.dragTau * 0.8f : p.dragTau;
                float gravity = p.EffectiveGravity * (g.role == Role.ChildStar ? 0.8f : 1f);
                pos = g.origin + DisplacementAt(g.dir, g.speed, t, tau, gravity);
            }

            // ── サイズ ──
            // 終盤だけ縮める。最初から縮めると「開いた瞬間に痩せる」不自然さが出る
            float sizeRatio = remain >= p.shrinkFrom
                              ? 1f
                              : remain / Mathf.Max(0.01f, p.shrinkFrom);

            // ── 色 ──
            var col = g.colorA;
            if (p.colorShiftAt > 0f && u > p.colorShiftAt)
            {
                float k = Mathf.Clamp01((u - p.colorShiftAt) / p.colorShiftSpan);
                col = Color.Lerp(g.colorA, g.colorB, k);
            }

            // ── 明滅 ──
            float bright = 1f;
            if (p.twinkleDepth > 0f && u > p.twinkleFrom)
            {
                float s = Mathf.Sin(t * p.twinkleHz * Mathf.PI * 2f + g.twinklePhase);
                bright = 1f - p.twinkleDepth + p.twinkleDepth * s;
            }

            // 終わりに向かって暗くする。加算合成なのでアルファで明るさが決まる。
            //
            // ── 残光（afterglow）を作るための減衰カーブ ──
            //   以前は星が K=2.2 で「寿命の後半45%だけ」フェードし、尾は remain²
            //   （寿命の序盤からアルファが急落する）カーブだった。どちらも
            //   「開いた瞬間は明るいが、すぐ暗くなって消える」動きになり、
            //   実物の花火が持つ「開いたあと、じわじわ暗くなりながら燃え残る」
            //   残光の質感が出ていなかった。
            //
            //   星は K=1.25 にして、寿命の最初 20%（remain>=1/1.25=0.8）だけ
            //   全開の明るさを保ち、残り 80% を一定速度でゆっくり暗くする。
            //   尾（燃えかす）は remain の1乗（線形）にして、序盤の急落を無くし、
            //   寿命いっぱいまでじわっと暗くなる燃え残りにする。
            //   （trailLifetime も ShellPreset 側で伸ばしてあるので、
            //    合わせて「長く・ゆっくり消える」残光になる）
            float fade = g.role == Role.Trail
                         ? remain                    // 尾は寿命いっぱい線形に暗くなる（燃え残り）
                         : Mathf.Min(1f, remain * 1.25f);

            float a = Mathf.Clamp01(fade * bright);

            // 閃光は白へ寄せる
            if (flash > 0f) col = Color.Lerp(col, Color.white, flash);

            _buf[i].position          = pos;
            _buf[i].startSize         = g.size * sizeRatio * scale;
            _buf[i].startColor        = new Color(col.r, col.g, col.b, a);
            _buf[i].remainingLifetime = psLife;
        }

        _ps.SetParticles(_buf, _count);
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
    }
}
