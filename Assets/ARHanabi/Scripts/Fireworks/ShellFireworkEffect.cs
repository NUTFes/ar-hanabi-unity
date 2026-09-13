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
    // ── 位置用と描画サイズ用で倍率を分けている理由 ──
    //   画面フィットの正規化は「星の到達半径 = burstSpeed·dragTau が画面の半分に収まる」
    //   ように scale を逆算する（FireworkLauncher.LaunchShellWithPreset）。
    //   この scale をそのまま星の描画サイズにも掛けると、scale が dragTau に反比例するため
    //   「開く速さ（dragTau）を変えただけで粒の大きさが変わってしまう」。
    //   型ごとに開き方を詰めるたびに starSize を同倍率で手直しする必要があり、
    //   調整が常に2値同時修正になっていた。
    //
    //   そこで倍率を2本に分けた。
    //     posScale  … 位置（DisplacementAt）専用。従来どおり dragTau に反比例する
    //     sizeScale … 描画サイズ専用。固定の基準半径から求めるので dragTau に依存しない
    //   これで dragTau は「開く速さ」だけの純粋なつまみになり、
    //   starSize は「菊を基準にした粒の大きさ」という一定の意味を持つ。

    [Tooltip("位置の倍率。FireworkLauncher が視錐台から計算して渡す（dragTau に反比例）")]
    public float posScale = 1f;

    [Tooltip("描画サイズの倍率。dragTau に依存しない（FireworkLauncher が渡す）")]
    public float sizeScale = 1f;

    [Tooltip("1発あたりの最大粒数。超えた分は星を間引く")]
    public int maxParticles = 5000;

    [Header("シェーダー設定")]
    [Tooltip("Custom/ParticleAdditive が見つからないときのフォールバック")]
    [SerializeField] private Shader particleShader;

    private const string AdditiveShaderName    = "Custom/ParticleAdditive";
    private const string VertexColorShaderName = "Custom/ParticleUnlit";

    // HDR発光の倍率を渡す先。フォールバックの ParticleUnlit には無いプロパティなので、
    // SetFloat は「あれば効く／無ければ何も起きない」ものとして呼んでよい
    private static readonly int PropIntensity = Shader.PropertyToID("_Intensity");

    // sizeGrowIn の立ち上がりの下限。生まれた瞬間の大きさ（full に対する比）。
    // 0 にすると開いた瞬間に何も見えない時間ができるので、少し残す
    private const float GrowInFloor = 0.3f;

    // ── 粒の役割 ──
    // Flash は「開花の瞬間に中心で光る芯」。位置を動かさず急速に減衰させる。
    // 1発につき1粒しか作らないので負荷は無視できる
    private enum Role : byte { Star, Trail, ChildStar, Flash }

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

        // ドーパミンモードの虹色で使う、粒ごとに固定の色相オフセット（0..1）。
        // Grain[] は BuildGrains で1回確保するだけなので、
        // フィールドを1本増やしても毎フレームのアロケーションは増えない
        public float   hue;

        // 尾の粒は「親の星の軌跡上の一点」に固定されるので、
        // 生成時にその位置を計算して origin に入れてしまう（dir/speed は 0）
    }

    private ParticleSystem.Particle[] _buf;
    private Grain[]                   _grains;
    private int                       _count;

    private ParticleSystem _ps;
    private Material       _material;
    private ShellPreset    _preset;

    // 玉全体の移動速度。Launch で1回だけ求めて使い回す（ShellPreset.DriftVelocity 参照）。
    // 毎フレーム・毎粒で正規化と除算をやり直さないためのキャッシュ。
    // driftRatio が 0 なら零ベクトルなので、位置の式に何も足されない
    private Vector3        _driftVelocity;

    // ドーパミンモード（虹色）の設定。Launch() で1回だけスナップショットする。
    //
    // ── 毎フレーム Instance を見に行かない理由 ──
    //   1. 飛んでいる最中に管理画面で OFF にされた玉が途中で色を失うと「バグに見える」。
    //      1発は最後まで同じモードで飛びきるべき
    //   2. Instance != null は UnityEngine.Object の == オーバーロード
    //      （ネイティブ側への問い合わせを含む）。粒ごとに呼ぶと実測できる負荷になる
    //   時間で回る位相（HuePhase）だけは Update() のループの外で1回読む
    private RainbowTint.Settings _rainbow;

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

    public void Launch(ShellPreset preset, float positionScale, float drawSizeScale)
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

        _preset        = preset;
        posScale       = positionScale;
        sizeScale      = drawSizeScale;
        _origin        = transform.position;
        _total         = preset.TotalLifetime;
        _driftVelocity = preset.DriftVelocity;
        _rainbow       = RainbowTint.Settings.Capture();

        // HDR発光の倍率。粒の色は Color32（上限1.0）でしか渡せないので、
        // 1.0 を超える明るさはマテリアル側の倍率で作る（ParticleAdditive の _Intensity）。
        // 1発ごとに Material を作っているので、型ごとに違う値を入れても
        // 他の花火には影響しない。
        //
        // 虹色のときは底上げする。彩度を上げると輝度が落ちる
        //（菊の (1, 0.82, 0.40) は平均0.74、飽和した青 (0,0,1) は 0.33）ので、
        // 同じ _Intensity のままだと虹色のほうが暗く見えるため。
        //
        // ⚠️ 掛け先は Material であって ShellPreset ではない。
        //   ShellPreset は [Serializable] の参照型で、ライブラリ Asset が持つ
        //   インスタンスがそのまま渡ってくる。preset.emissiveIntensity に代入すると
        //   Asset に焼き付いて、Play を抜けても戻らない
        float boost = _rainbow.Enabled ? _rainbow.IntensityBoost : 1f;
        _material.SetFloat(PropIntensity, Mathf.Max(0.01f, preset.emissiveIntensity) * boost);

        BuildGrains(preset);
        SetupParticleSystem();

        _startTime = Time.time;
        _launched  = true;

        Debug.Log($"[Shell] {preset.name}（{preset.category}）を発射: " +
                  $"{_count} 粒 / {_total:F2}s / pos={posScale:F2} size={sizeScale:F2} @ {_origin}");

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
        int flashCore  = p.flashCoreSize > 0f ? 1 : 0;

        _count  = starCount + starCount * trailPer + childCount * childStars + flashCore;
        _buf    = new ParticleSystem.Particle[_count];
        _grains = new Grain[_count];

        int coreStars = p.coreSpeedRatio > 0f
                        ? (int)(starCount * p.coreStarRatio)
                        : 0;

        int w = 0;

        // ── 親玉の星 ──
        //
        // ── birth を 0 固定にしていない理由（この回の変更）──
        //   従来は全ての星が birth = 0 だったため、開花は常に「一瞬で全部出る」動きしか
        //   作れなかった。実物の割物は割火薬で星が押し出されるので、ごくわずかな
        //   時間差がある。burstStagger でそのばらつきを与える。
        //   芯入りの型は coreDelay で「外郭が開いてから一瞬遅れて芯が光る」を作る。
        //   どちらも既定 0 なので、値を入れるまでは従来と同一の挙動になる。
        for (int i = 0; i < starCount; i++)
        {
            bool isCore = i < coreStars;

            var dir = SampleDirection(p, p.shape, i, starCount);

            float speed = p.burstSpeed
                        * (1f + Random.Range(-p.speedJitter, p.speedJitter))
                        * (isCore ? p.coreSpeedRatio : 1f);

            float life = p.starLifetime * (1f + Random.Range(-p.lifetimeJitter, p.lifetimeJitter));
            float size = p.starSize     * (1f + Random.Range(-p.sizeJitter, p.sizeJitter));

            float birth = (p.burstStagger > 0f ? Random.Range(0f, p.burstStagger) : 0f)
                        + (isCore ? Mathf.Max(0f, p.coreDelay) : 0f);

            var cA = isCore ? p.coreColor : p.colorA;
            var cB = p.colorShiftAt > 0f ? p.colorB : cA;

            _grains[w++] = new Grain
            {
                role         = Role.Star,
                origin       = _origin,
                dir          = dir,
                speed        = speed,
                birth        = birth,
                life         = life,
                size         = size,
                colorA       = cA,
                colorB       = cB,
                twinklePhase = Random.Range(0f, Mathf.PI * 2f),

                // 虹色の色相オフセット。i/starCount ではなく黄金比で散らすのが要点。
                // SampleDirection が方向を黄金角のらせんで配っているので、
                // 色相まで i に比例させると位置と色相が完全に相関し、
                // 球面を虹の帯が1本巻く見た目になる（RainbowTint.HueOffset 参照）
                hue          = RainbowTint.HueOffset(i),
            };
        }

        // ── 尾 ──
        // 星の軌跡上の一点に置いて、その場で減衰させる（燃えかすが残るイメージ）
        //
        // ── birth に star.birth を足す必要がある（要注意）──
        //   at は「星が生まれてからの年齢」なので、StarPositionAt(star, at) は
        //   その年齢での位置を返す。一方 Update() の t は now - birth（発射からの絶対時刻基準）。
        //   星が burstStagger / coreDelay で birth != 0 を持つようになったので、
        //   尾の粒が現れるべき絶対時刻は star.birth + at になる。
        //   ここを at だけにすると、遅れて開く星の尾が星本体より先に出てしまう。
        for (int s = 0; s < starCount && trailPer > 0; s++)
        {
            var star = _grains[s];

            for (int t = 1; t <= trailPer; t++)
            {
                float at = t * p.trailSpacing;
                if (at >= star.life) break;

                // 置かれた瞬間の星の色を焼き付ける。colorA と colorB を同じ値にすると
                // Update() の Color.Lerp が恒等になるので、尾では色変化が起きなくなる
                //（StarColorAt のコメント参照）
                var deposit = StarColorAt(star, at, p) * p.trailBrightness;

                _grains[w++] = new Grain
                {
                    role         = Role.Trail,
                    origin       = StarPositionAt(star, at, p),
                    dir          = Vector3.zero,
                    speed        = 0f,
                    birth        = star.birth + at,
                    life         = p.trailLifetime,
                    size         = star.size * p.trailSizeScale,
                    colorA       = deposit,
                    colorB       = deposit,
                    twinklePhase = Random.Range(0f, Mathf.PI * 2f),

                    // 虹色でも親の星と同じ色相を引き継ぐ。
                    // 尾は「その星が落とした燃えかす」なので、親と違う色になると
                    // どの粒がどの星のものか読めなくなり、
                    // 花火ではなくただのカラフルな砂に見える
                    hue          = star.hue,
                };
            }
        }

        // ── 千輪の小玉 ──
        // 親玉が開いたあと、一瞬遅れて小玉が一斉に開く
        for (int c = 0; c < childCount; c++)
        {
            // 小玉が散る位置。親玉と同じ運動で childDelay の時点まで飛ばす
            var scatterDir = Random.onUnitSphere;
            // 奥行きを完全な球より浅くしてある。
            // ただし潰しすぎると全ての小玉がカメラから等距離＝同じ見かけの大きさになり、
            // 平面に貼ったシールのように見える。透視投影は距離で大きさを変えてくれるので、
            // ある程度の奥行きを残すことが「粒の集まり」から抜け出す助けになる
            scatterDir.z *= 0.45f;

            float scatterSpeed = p.burstSpeed * p.childScatterRatio;
            // 小玉が生まれる位置は「親玉が childDelay の時点で居る場所」。
            // drift を渡すので、落下していく親の軌跡の上に正しく乗る
            //（＝降ってきた玉のいる位置で2段目が開く）
            var   childOrigin  = _origin + DisplacementAt(scatterDir, scatterSpeed,
                                                          p.childDelay, p.dragTau,
                                                          p.EffectiveGravity, p.fallDragTau,
                                                          _driftVelocity);

            var childColor = p.childRandomColor
                             ? ShellPreset.ChildPalette[Random.Range(0, ShellPreset.ChildPalette.Length)]
                             : p.colorA;

            // 虹色も「小玉1個につき1色」。既存の childRandomColor と同じ粒度に揃える。
            // 小玉の中でさらに粒ごとの虹にすると、30粒しかない小さな玉が
            // 色の塊として読めなくなり、千輪という型そのものが壊れる
            float childHue = RainbowTint.HueOffset(c);

            for (int i = 0; i < childStars; i++)
            {
                Vector3 dir;
                if (p.childShape == ShellShape.Sphere)
                {
                    // 従来どおりランダムに散る球。千輪の小玉は1つ30粒しかなく、
                    // 等間隔に並べるとかえって人工的に見えるのでここは残す
                    dir = Random.onUnitSphere;
                    // 小玉自身の広がりにも奥行きを残す（scatterDir と同じ理由）
                    dir.z *= 0.55f;
                    if (dir.sqrMagnitude < 1e-6f) dir = Vector3.up;
                    dir.Normalize();
                }
                else
                {
                    // 形を指定された小玉は親玉と同じ形の定義を使う。
                    // 「降ってきた玉が最下点で★の形に開く」のはこの経路
                    dir = SampleDirection(p, p.childShape, i, childStars);
                }

                _grains[w++] = new Grain
                {
                    role         = Role.ChildStar,
                    origin       = childOrigin,
                    dir          = dir,
                    speed        = p.childBurstSpeed * (1f + Random.Range(-0.2f, 0.2f)),
                    birth        = p.childDelay,
                    life         = p.childLifetime * (1f + Random.Range(-0.15f, 0.15f)),
                    // 親玉と同じくサイズをばらつかせる。
                    // ここが固定値だと小玉の星が全て同じ大きさで並び、
                    // 「花火」ではなく「同じ粒を撒いたもの」に見える
                    size         = p.EffectiveChildStarSize
                                 * (1f + Random.Range(-p.sizeJitter, p.sizeJitter)),
                    colorA       = childColor,
                    colorB       = childColor,
                    twinklePhase = Random.Range(0f, Mathf.PI * 2f),
                    hue          = childHue,
                };
            }
        }

        // ── 開花の芯（白い閃光の玉）──
        // flashStrength は全粒を白へ寄せるだけなので「中心が一瞬白く飛ぶ」という
        // 開花の一撃が出せなかった。中心に大きめの粒を1つだけ置いて急速に減衰させると、
        // Bloom と合わせて「割れた瞬間の光」として読める。粒1個なので負荷はゼロ。
        if (flashCore > 0)
        {
            _grains[w++] = new Grain
            {
                role         = Role.Flash,
                origin       = _origin,
                dir          = Vector3.zero,
                speed        = 0f,
                birth        = 0f,
                life         = Mathf.Max(0.02f, p.flashDuration),
                size         = p.starSize * p.flashCoreSize,
                colorA       = Color.white,
                colorB       = Color.white,
                twinklePhase = 0f,

                // 芯には虹を適用しない（Update 側で Role.Flash を除外している）。
                // 開花の一撃は白でなければ「割れた瞬間の光」に読めず、
                // 色が付くと「中心に大きな粒が1つある」としか見えなくなる。
                // ここの値は使われないので 0 のまま
                hue          = 0f,
            };
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
    // ── 正規化してよい形と、してはいけない形がある ──
    //   DisplacementAt は dir * (speed * drag * posScale) を使うので、
    //   dir の大きさがそのまま「中心からどこまで飛ぶか」の比率になる。
    //   つまり .normalized は「全ての星を同じ半径に揃える」という意味を持つ。
    //
    //   正規化してよい … Sphere / Ring / UpperHemisphere
    //     もともと全ての点が同じ半径にある形なので、正規化しても何も失われない。
    //
    //   正規化してはいけない … Heart / Star / Saucer / RingedPlanet /
    //                          SpiralGalaxy / Comet
    //     これらは「角度ごとに中心からの距離が違う」ことが形そのもの。
    //     揃えてしまうと残るのは角度の分布だけになり、円に潰れる。
    //
    //   ※ 以前は Heart もまとめて正規化していて、ハートが円になっていた。
    //     形を作る側と「向きだけ返す」側が同じメソッドに同居していると
    //     この取り違えが起きやすいので、新しい形を足すときは
    //     「その形は半径の情報を持っているか」を必ず確認すること。
    //
    //   大きさは 1 を超えないこと。呼び出し元
    //   （FireworkLauncher.LaunchShellWithPreset）が |dir|=1 を前提に
    //   画面フィット半径を逆算しているため、超えると画面からはみ出す。
    private static Vector3 SampleDirection(ShellPreset p, ShellShape shape, int index, int total)
    {
        switch (shape)
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
                // 媒介変数表示のハート。奥行きは薄くして正面から形が読めるようにする。
                //
                // ── 正規化してはいけない（円になっていた不具合の原因）──
                //   ハートは「角度ごとに中心からの距離が違う」ことが形そのもの。
                //   .normalized すると全ての点が半径1に揃い、残るのは角度の分布だけになる。
                //   つまりハートではなく円が描かれる（角度が不均等なので
                //   濃淡のあるリングに見える）。
                //   Ring や Sphere は元から全点が同じ半径なので正規化しても影響が無く、
                //   ハートだけがこの書き方の巻き添えになっていた。
                float t = index / (float)total * Mathf.PI * 2f;
                float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
                float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2f * t)
                        - 2f * Mathf.Cos(3f * t) - Mathf.Cos(4f * t);

                // この曲線の最大半径は t=π（ハート下端の尖り）の 17。
                //   x = 16·sin³π = 0 / y = -13-5+2-1 = -17
                // ここで割ると輪郭がちょうど半径1に収まり、
                // 画面フィット半径（|dir|=1 を前提に逆算している）にそのまま乗る
                const float HeartMaxRadius = 17f;
                return new Vector3(x / HeartMaxRadius,
                                   y / HeartMaxRadius,
                                   Random.Range(-0.08f, 0.08f));
            }

            case ShellShape.UpperHemisphere:
            {
                var v = GoldenSpiral(index, total);
                v.y = Mathf.Abs(v.y) * 0.9f + 0.1f;   // 上向きに寄せる
                return v.normalized;
            }

            case ShellShape.Saucer:
            {
                // 円盤＋その上に載るドーム（操縦席）。
                //
                // ── 以前は円盤の縁だけだった ──
                //   y を discThickness で潰した楕円を1周描くだけだったので、
                //   正面から見ると単なる横長の楕円にしかならず UFO として読めなかった。
                //   上にドームを足して初めて「円盤＋操縦席」のシルエットになる。
                //
                // ── index の分け方に剰余を使わない理由 ──
                //   index % 100 で振り分けると、角度に使う index/total が
                //   飛び飛びの値になり、円弧が均等に埋まらない（塊と隙間ができる）。
                //   先頭 domeCount 個をドーム、残りを円盤、と範囲で切れば
                //   どちらも 0〜1 を均等に走査できる。
                int domeCount = Mathf.Clamp(
                    Mathf.RoundToInt(total * p.domeStarRatio), 1, total - 1);

                if (index < domeCount)
                {
                    // ドーム: 上半円（0〜π）。円盤の上面に載せるので厚みぶん持ち上げる
                    float a = (index + 0.5f) / domeCount * Mathf.PI;
                    return new Vector3(
                        Mathf.Cos(a) * p.domeRadius,
                        Mathf.Sin(a) * p.domeRadius + p.discThickness,
                        Random.Range(-0.04f, 0.04f));
                }

                // 円盤の縁: 薄い楕円を1周
                int   discIndex = index - domeCount;
                int   discTotal = total - domeCount;
                float t = (discIndex + 0.5f) / discTotal * Mathf.PI * 2f;
                return new Vector3(
                    Mathf.Cos(t),
                    Mathf.Sin(t) * p.discThickness,
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

            case ShellShape.Star:
            {
                // 五芒星（★）の輪郭。外側の頂点5つと内側の頂点5つ、
                // 計10点を結ぶ折れ線の上に粒を等間隔で並べる。
                //
                // 正規化しないのは、頂点（半径1）と谷（半径 starInnerRatio）で
                // 中心からの距離が違うことが★の形そのものだから
                //（正規化すると全部が同じ半径になり十角形にすらならず円に潰れる。
                //  Heart が正規化のせいでリングに見える既知の不具合と同じ理屈）。
                float seg = index / (float)total * 10f;   // 10本の辺
                int   k   = (int)seg;                     // 何本目の辺か
                float f   = seg - k;                      // その辺の中の進み

                // 上を頂点にする（-π/2 始まり）。偶数番が外側、奇数番が内側
                float a0 = -Mathf.PI * 0.5f + k * Mathf.PI / 5f;
                float a1 = -Mathf.PI * 0.5f + (k + 1) * Mathf.PI / 5f;
                float r0 = (k % 2 == 0) ? 1f : p.starInnerRatio;
                float r1 = ((k + 1) % 2 == 0) ? 1f : p.starInnerRatio;

                var v0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r0;
                var v1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r1;
                var v  = Vector2.Lerp(v0, v1, f);

                // 奥行きは極小のノイズだけ。正面から形が読めるようにする
                return new Vector3(v.x, v.y, Random.Range(-0.04f, 0.04f));
            }

            case ShellShape.Comet:
                // 彗星は「星を1つ出して、その軌跡を光で描く」型。
                // 形として持つのは星のかたまりだけで、尾は一切定義しない。
                //
                // ── 尾は配置ではなく軌跡から生まれる ──
                //   一度、尾を「進行方向の逆へ並べた星の列」として作ったが、
                //   星は原点から自分の位置へ飛ぶので「星が上へ発射される」動きになった。
                //   その次は星を頭と尾の2群に分けたが、今度は
                //   「星の塊が、それぞれ自分の尾を引きずっている」絵になった。
                //   どちらも尾を"物"として持たせたのが誤りだった。
                //
                //   今は星がごく小さなかたまり（既定 3粒）で、それが速く落ちる。
                //   通った道に残る燃えかす（Trail グレイン）が1本の光の筋になる。
                //   cometHeadRadius を小さく保つことで、複数粒でも筋が1本に重なる。
                return GoldenSpiral(index, total) * p.cometHeadRadius;

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
    //   横方向: x(t) = v0·τ·(1 - exp(-t/τ))
    //   縦方向: y(t) = −g·τf·( t − τf·(1 − exp(−t/τf)) )    … τf > 0（落下にも抵抗）
    //           y(t) = −½·g·t²                              … τf ≤ 0（抵抗なし）
    //
    // ── dragTau（τ）を変えても最終的な広がりは変わらない ──
    //   t→∞ での変位は v0·τ（FireworkLauncher が radius = burstSpeed·dragTau として
    //   posScale を逆算する値そのもの）。posScale = halfView / (burstSpeed·dragTau) なので、
    //   実際の到達距離 = v0·τ·posScale = halfView となり τ が約分されて消える。
    //   つまり dragTau は「最終的にどこまで広がるか」には効かず、
    //   「そこに到達するまでの速さ（拡散の速さ）」だけを変える。
    //   拡散をゆっくりにしたいときは dragTau を大きくすればよい。
    //   星の描画サイズは sizeScale（dragTau に依存しない）側で決まるので、
    //   dragTau を動かしても粒の大きさは変わらない。
    //
    // ── 落下にも空気抵抗を入れている理由（fallDragTau）──
    //   ½gt² は加速し続けるため、寿命の長い型（柳は3.7秒）では落ちるほど速くなり、
    //   実物の「垂れる」動きにならなかった。重力＋線形抵抗の解も閉じた形で書けるので、
    //   解析式・フレームレート非依存のまま終端速度 g·τf に収束させられる。
    //   fallDragTau = 0 のときは従来どおりの ½gt² に落ちる（既定＝挙動不変）。
    //
    // ── 第3項の drift（玉全体の移動）──
    //   第1項は減速して漸近的に止まるので、「玉そのものがどこかへ移動していく」
    //   動きが作れなかった。等速の移動を独立した項として足すことで、
    //   彗星のように「咲いてから落ちていく」動きが作れる。
    //   _driftVelocity は Launch で1回だけ求めてある（driftRatio=0 なら零ベクトル）。
    //
    //   尾（Role.Trail）と芯（Role.Flash）はそもそもこのメソッドを通らず、
    //   置かれた場所に留まる。だから頭が移動すると通った道に燃えかすが残り、
    //   それがそのまま尾になる。
    //
    // ── drift を引数で受けるのは、小玉には効かせないため ──
    //   小玉（2段目の爆発）まで親と同じ速度で流れ続けると、
    //   せっかく最下点で開いた形が下へ流れて崩れてしまう。
    //   親の星と「小玉が生まれる位置の計算」には drift を渡し、
    //   小玉の星自身には Vector3.zero を渡す。
    private Vector3 DisplacementAt(Vector3 dir, float speed, float t,
                                   float dragTau, float gravity, float fallDragTau,
                                   Vector3 drift)
    {
        float tau  = Mathf.Max(0.01f, dragTau);
        float dragK = tau * (1f - Mathf.Exp(-t / tau));

        float fall = fallDragTau > 0.0001f
                     ? gravity * fallDragTau * (t - fallDragTau * (1f - Mathf.Exp(-t / fallDragTau)))
                     : 0.5f * gravity * t * t;

        return dir * (speed * dragK * posScale)
             + Vector3.down * (fall * posScale)
             + drift * (t * posScale);
    }

    // 尾の粒を置く位置を求めるのに使う。drift を含めるので、
    // 落下していく軌跡の上に燃えかすが残る（＝軌跡がそのまま尾になる）
    private Vector3 StarPositionAt(in Grain g, float t, ShellPreset p)
        => g.origin + DisplacementAt(g.dir, g.speed, t,
                                     p.dragTau, p.EffectiveGravity, p.fallDragTau,
                                     _driftVelocity);

    // 星が「年齢 t の時点」で持っている色。Update() の色変化と同じ式にしてある。
    //
    // ── 何のために要るか ──
    //   尾の粒に「置かれた瞬間の星の色」を焼き付けるため。
    //   以前は尾に star.colorA / star.colorB をそのまま渡していたので、
    //   Update() の色変化が尾の粒でも独立に走っていた。ところが尾の u は
    //   trailLifetime 基準なので、爆心近くに残った古い燃えかすが自分の寿命の中で
    //   勝手に2色目へ変わってしまい、星の色変化と無関係なタイミングで
    //   画面に2色目が散らばって変化が読みにくくなっていた。
    //
    //   実際の尾は「その時に燃えていた星が落とした燃えかす」なので、
    //   置かれた時点の色のまま消えていくのが正しい。こうすると尾には
    //   変化前の色が残り、星だけが変化するので、対比で変化が読めるようになる。
    private static Color StarColorAt(in Grain star, float t, ShellPreset p)
    {
        if (p.colorShiftAt <= 0f) return star.colorA;

        float u = t / Mathf.Max(0.0001f, star.life);
        if (u <= p.colorShiftAt) return star.colorA;

        float k = Mathf.Clamp01((u - p.colorShiftAt) / Mathf.Max(0.01f, p.colorShiftSpan));
        return Color.Lerp(star.colorA, star.colorB, k);
    }

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

        // 虹の位相。時間で回るのでここだけは毎フレーム読む必要があるが、
        // 読むのはループの外で1回だけ（Instance の == は
        // UnityEngine.Object のオーバーロードで、粒ごとに呼ぶと効いてくる）。
        // モードの ON/OFF や彩度は _rainbow に固定してあるので、
        // 飛行中に管理画面を触られても1発の見た目は最後まで変わらない
        float huePhase = 0f;
        if (_rainbow.Enabled)
        {
            var dop = DopamineModeController.Instance;
            if (dop != null) huePhase = dop.HuePhase;
        }

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
                pos = g.origin + Vector3.down * (p.trailSink * t * t * posScale);
            }
            else if (g.role == Role.Flash)
            {
                // 開花の芯は中心に留まる（動かさないので閃光として読める）
                pos = g.origin;
            }
            else
            {
                // 小玉は自分の広がりと寿命から重力を出す。
                // 親玉の EffectiveGravity を流用すると、寿命の違い（千輪では 0.7秒 対 1.5秒）が
                // そのまま落下量の差になり、小玉だけが画面外へ落ちていた
                //（ShellPreset.childSagRatio のコメント参照）
                bool  isChild = g.role == Role.ChildStar;
                float tau     = isChild ? p.ChildDragTau          : p.dragTau;
                float gravity = isChild ? p.ChildEffectiveGravity : p.EffectiveGravity;

                // 小玉には drift を効かせない。効かせると、最下点で開いた形が
                // 親と同じ速度で下へ流れ続けて崩れてしまう。
                // 小玉が生まれる位置には drift が入っているので、
                // 「落ちてきた先で開く」ことは保たれる
                var drift = isChild ? Vector3.zero : _driftVelocity;

                pos = g.origin + DisplacementAt(g.dir, g.speed, t, tau, gravity,
                                                p.fallDragTau, drift);
            }

            // ── サイズ ──
            // 終盤だけ縮める。最初から縮めると「開いた瞬間に痩せる」不自然さが出る。
            //
            // 尾は星と別の縮み始めを持てる（EffectiveTrailShrinkFrom）。
            // 1.0 にすると置かれた瞬間から縮み始めるので、古い粒＝星から遠い粒ほど
            // 細くなり、尾が先細りになる（彗星の尾がこれで作れる）
            float shrinkAt  = g.role == Role.Trail
                              ? p.EffectiveTrailShrinkFrom
                              : p.shrinkFrom;
            float sizeRatio = remain >= shrinkAt
                              ? 1f
                              : remain / Mathf.Max(0.01f, shrinkAt);

            // ── 立ち上がり ──
            //   星は生まれた瞬間、全て同じ一点に居る。そこで最初から full サイズだと
            //   その1点に粒が丸ごと重なり、加算合成で飽和した「大きな白い塊」になって
            //   開いた瞬間だけ作り物っぽく見える。
            //   ごく短い時間で立ち上げると、点火して膨らむように見える。
            //
            //   尾は「置かれた瞬間がいちばん濃い燃えかす」なので対象外。
            //   芯（Flash）は開花の一撃そのものなので、こちらも立ち上げない。
            if (p.sizeGrowIn > 0f && (g.role == Role.Star || g.role == Role.ChildStar))
            {
                // 0 からではなく GrowInFloor から始める。完全に 0 にすると
                // 開いた瞬間に何も見えない時間ができてしまう
                sizeRatio *= Mathf.Lerp(GrowInFloor, 1f, Mathf.Clamp01(t / p.sizeGrowIn));
            }

            // ── 色 ──
            var col = g.colorA;
            if (p.colorShiftAt > 0f && u > p.colorShiftAt)
            {
                float k = Mathf.Clamp01((u - p.colorShiftAt) / p.colorShiftSpan);
                col = Color.Lerp(g.colorA, g.colorB, k);
            }

            // ── ドーパミンモードの虹色 ──
            //   ここ（色が決まった直後）で差し込むのが要点。
            //   この下の明滅・再点火・減衰・閃光はすべてアルファしか触らないので、
            //   色相を置き換えてもそれらの設計はそのまま生きる。
            //
            //   RainbowTint.Replace は元の色の「最大成分」を明るさに使うので、
            //   trailBrightness で暗くした尾は暗いまま、型ごとの明暗差も残る。
            //   アルファも元のまま渡るので、減衰・明滅の計算は一切壊れない。
            //
            //   芯（Flash）だけは除外する。開花の一撃は白でなければ
            //   「割れた瞬間の光」として読めないため（BuildGrains 側のコメント参照）。
            //
            // ⚠️ 型花火と昇りは Replace（色相を奪う）、画像花火だけは Rotate（色相を回す）。
            //   この非対称は意図的で、揃えてはいけない。
            //   Replace を投稿写真に掛けると絵の中の色の関係が全部消えて虹色の塊になる。
            //   理由は ImageFireworkEffect.cs の該当箇所と RainbowTint.Rotate を参照
            if (_rainbow.Enabled && g.role != Role.Flash)
                col = RainbowTint.Replace(_rainbow, col, g.hue, huePhase);

            // ── 明滅 ──
            //
            // ── twinkleRampUp（消え際に向けて明滅を強める）──
            //   従来は twinkleFrom を過ぎたら振幅が最後まで一定だった。
            //   実物の星は燃え尽きる直前ほど激しくまたたいて消えるので、
            //   twinkleFrom → 寿命の終わりへ向けて振幅を持ち上げられるようにした。
            //   0 のときは従来どおりの一定振幅（既定＝挙動不変）。
            float bright = 1f;
            if (p.twinkleDepth > 0f && u > p.twinkleFrom && g.role != Role.Flash)
            {
                float depth = p.twinkleDepth;
                if (p.twinkleRampUp > 0f)
                {
                    // twinkleFrom で 1、寿命の終わりで (1 + twinkleRampUp) 倍まで持ち上げる
                    float ramp = Mathf.Clamp01((u - p.twinkleFrom) / Mathf.Max(0.01f, 1f - p.twinkleFrom));
                    depth = Mathf.Clamp01(depth * (1f + p.twinkleRampUp * ramp));
                }

                float s = Mathf.Sin(t * p.twinkleHz * Mathf.PI * 2f + g.twinklePhase);
                bright = 1f - depth + depth * s;
            }

            // ── 色が変わる瞬間の再点火 ──
            //   色変化は寿命に対する割合で起きるので、変化が完了するのは必ず
            //   寿命の後半になる。そこは同時に「暗くなる・縮む・点滅する」区間でもあり、
            //   2色目が暗く小さくちらつきながらしか出ていなかった。
            //   全体を明るくすると花火全体が白っぽくなるので、変化した瞬間だけ
            //   明るさを取り戻して、そこから改めて減衰させる。
            //
            //   下の a = Clamp01(fade * bright) でアルファは 1.0 で頭打ちになるため、
            //   これは「失った明るさを取り戻す」だけで白飛びはしない。
            //
            //   星本体だけに掛ける。尾は下の理由で色が固定されており（＝変化しない）、
            //   小玉と芯は単色なので、掛けると理由なく明るくなってしまう。
            if (g.role == Role.Star && p.colorShiftRelight > 0f
                && p.colorShiftAt > 0f && u > p.colorShiftAt)
            {
                float since   = (u - p.colorShiftAt) / Mathf.Max(0.01f, p.colorShiftSpan);
                float relight = p.colorShiftRelight * Mathf.Exp(-since);
                bright *= 1f + relight;
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
            // ── 消え際を型ごとに設定できるようにした（この回の変更）──
            //   ここは「落ち方＝消え方」の肝そのものなのに、全ての型で同じ式に
            //   固定されていた。牡丹はスッと消し、冠・柳は長く燃え残らせたい、
            //   といった型ごとの差が作れなかったので、2つのつまみに分けた。
            //     fadeHold  … 全開の明るさを保つ寿命の割合
            //     fadeCurve … その後の落ち方（1=線形 / >1=最後に急 / <1=長い残光）
            //
            //   既定値 fadeHold=0.2, fadeCurve=1 は従来の Min(1, remain*1.25) と一致し、
            //   trailFadeCurve=1 は従来の remain と一致する（＝既定では絵が変わらない）。
            //   尾は「燃えかすが残って消えていく」ものなので hold を持たせない。
            //   芯（Flash）だけは型の設定に従わせない。「一瞬で白く飛んで即座に引く」
            //   のが役割なので、hold を持たせず固定の急カーブで落とす
            float hold = g.role == Role.Star || g.role == Role.ChildStar
                         ? Mathf.Clamp01(p.fadeHold)
                         : 0f;
            float held = hold >= 0.999f
                         ? 1f
                         : Mathf.Clamp01(remain / (1f - hold));

            float curve = g.role switch
            {
                Role.Trail => p.trailFadeCurve,
                Role.Flash => 2.5f,
                _          => p.fadeCurve,
            };
            float fade = curve == 1f ? held : Mathf.Pow(held, Mathf.Max(0.05f, curve));

            float a = Mathf.Clamp01(fade * bright);

            // 閃光は白へ寄せる
            if (flash > 0f) col = Color.Lerp(col, Color.white, flash);

            _buf[i].position          = pos;
            _buf[i].startSize         = g.size * sizeRatio * sizeScale;
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
