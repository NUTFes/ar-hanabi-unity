using UnityEngine;

// ===== ImageFireworkEffect =====
// 画像から変換したパーティクル群を「ParticleSystem 1個 + SetParticles()」で描画する花火エフェクト。
// 以前は 1粒 = 1GameObject（MeshFilter + MeshRenderer + Material）だったため
// 解像度128（最大16,384粒）× 同時2発で最大32,768 GameObject / Material / DrawCall となり、
// 生成時に0.5〜1.0秒のフリーズ、描画は2〜5FPSまで落ちていた。
// 現在は1発あたり GameObject 1個 / Material 1個 / DrawCall 1回で済む。
//
// ── 動きは「止まらない1本の連続運動」──
//   位置は2つの成分の重ね合わせで作る。フェーズ分岐は無い。
//     ・展開       … origin → target へ 1 - e^(-t/τ) で漸近する（τ = expandTime/3）。
//                    指数関数なので到達しきらず、常にわずかに膨らみ続ける
//     ・落下＋拡散 … 加速度を 0 から smoothstep で立ち上げて積分する。
//                    展開の途中から重なり始めるので繋ぎ目が無い
//   見た目の流れは「膨らむ → 膨らみが緩んで絵が読める → そのまま沈みながら拡散して消える」。
//
//   もともとは Phase 1 展開 → Phase 2 形を維持 → Phase 3 落下 の3フェーズだった。
//   Phase 2 は position に目標座標を毎フレーム代入する = 完全な静止なので、
//   Phase 3 の速度をどれだけ連続にしても「止まってから動き出す」不自然さが残った。
//   静止区間そのものを無くさないと直らない、というのが結論。
//   settleTime は「位置を固定する時間」ではなく「落下と拡散が全開になるまでの時間」。
//
// ── 落下を作り直した理由（元の実装では落ちていなかった）──
//   旧実装は毎フレーム `v *= velocityDamping`(0.92) を掛けていた。これは dt 非依存なので
//   60fps では 1秒あたり 0.92^60 ≒ 0.0068 まで速度が削られる。重力 2/s と釣り合う
//   終端速度は約 -0.42 units/s しかなく、fadeTime 1.5秒での落下量は約0.5ユニット。
//   絵の全長が約5ユニットあるので、目視ではほぼ「その場でフェード」だった。
//   直した点は4つ:
//     1. 減衰を Mathf.Pow(velocityDamping, dt) にしてフレームレート非依存にした。
//        velocityDamping の意味も「1フレームで残る割合」から「1秒で残る割合」に変えた
//     2. 重力と fadeTime を上げて落下が目で追えるようにした
//        （終端速度 = gravityPerSec / -ln(velocityDamping) が 3 units/秒 前後になるよう調整）
//     3. 粒ごとに散り方（外向き＋水平ドリフト＋わずかな上昇）を持たせて散らばらせた。
//        ここは「初速」ではなく「加速度」で与えている。速度を直接入れると、
//        入れた瞬間に粒がいきなり動き出して折れが見える。
//        外向き成分は中心からの距離に比例させる。全粒に同じ大きさを与えると
//        内側から潰れて「崩壊」に見えるが、距離に比例させれば絵が
//        相似形のまま膨らむので「拡散」に見える
//     4. 粒ごとの時間差（fallStagger）は用意したが既定は 0 にしてある。
//        時間差を付けると端からパラパラ崩れる絵になり、拡散ではなく
//        崩壊の印象になってしまうため。試したい場合だけ Inspector で上げる
//
// ── 見た目を「花火っぽく」した点 ──
//   ・粒サイズを固定値ではなく imageScale / 解像度 から導出する。解像度を落として
//     粒を大きくしても隙間だらけにも団子にもならない
//   ・描画は Custom/ParticleAdditive（加算合成 ＋ UV から丸いフォールオフ）。
//     Custom/ParticleUnlit は UV を読まないため1粒が塗りつぶしの正方形になり、
//     粒を大きくするとモザイクのタイルに見えてしまう
//   ・サイズとアルファを分離した。旧実装は両方を同じ比率で線形に落としていたので
//     「縮んで消える」動きになり、落下が見えなかった
//
// ── 毎フレームのアロケーションはゼロ ──
//   位置・目標・速度・落下変位・散り加速度・基準色・立ち上がり遅延・点滅位相は
//   すべて Launch() で確保した配列に持つ。
//   Update() では配列を新規確保せず、SetParticles で一括反映する。

public class ImageFireworkEffect : MonoBehaviour
{
    [Header("時間設定")]
    [Tooltip("絵の形が 95% 出来上がるまでの秒数。\n" +
             "展開は指数関数なので完全には止まらず、常にわずかに膨らみ続ける")]
    public float expandTime  = 0.8f;

    [Tooltip("落下と拡散が全開になるまでの秒数（絵が読める時間の目安）。\n" +
             "この区間でも位置は固定せず、少しずつ沈みながら広がり続ける")]
    public float settleTime  = 0.6f;

    [Tooltip("その後、粒が消えきるまでの秒数")]
    public float fadeTime    = 2.5f;

    [Tooltip("粒ごとに落下の立ち上がりをずらす最大秒数。\n" +
             "0（既定）: 全粒が揃って拡散する\n" +
             "0より大きい: 端から時間差で散る。ただし「崩壊」に見えやすい")]
    public float fallStagger = 0f;

    [Header("表示設定")]
    [Tooltip("絵の表示サイズ（ワールド座標）。FireworkLauncher が視錐台の高さから計算して渡す")]
    public float imageScale = 6f;

    [Tooltip("粒1つのサイズの倍率。粒の間隔（imageScale / 解像度）に対する比。\n" +
             "1.0 で隙間なく敷き詰まり、それより大きいと重なって光がつながる")]
    public float particleSizeFactor = 1.7f;

    [Header("落下設定")]
    // 終端速度は gravityPerSec / k で決まる（k = -ln(velocityDamping)）。
    // 既定値では k ≒ 1.20/秒 なので終端速度は約 2.5 units/秒。
    // 画面の高さは花火の大きさとほぼ同じ（imageScale ≒ 5.8）なので、
    // 火の粉が一定速度で流れ落ちながら消えていく見え方になる。
    // もっとゆっくり落としたいならこの値を下げる（拡散量とは独立に調整できる）。
    [Tooltip("重力加速度（units/秒²）")]
    public float gravityPerSec   = 3f;
    [Tooltip("空気抵抗。1秒あたりに速度が残る割合（dt 非依存で適用する）。\n" +
             "小さいほど早く終端速度に達し、落下がゆっくりになる")]
    public float velocityDamping = 0.3f;
    // 散り方は「初速」ではなく「加速度」で与える。理由はファイル冒頭の 3. を参照。
    [Tooltip("中心から外向きへの加速度（units/秒²、絵の端での値）。\n" +
             "大きいほど外へ広がりながら消える = 拡散に見える")]
    public float spreadAcceleration = 2.2f;
    [Tooltip("粒ごとの水平ランダム加速度の最大値（units/秒²）")]
    public float driftAcceleration  = 0.8f;
    [Tooltip("粒ごとの上向きランダム加速度の最大値（units/秒²）。\n" +
             "少し浮いてから落ちる粒が混ざると火の粉らしくなる")]
    public float riseAcceleration   = 0.8f;

    [Header("消え方")]
    [Tooltip("残り寿命がこの割合を下回ってから縮み始める。それまでサイズは維持する")]
    [Range(0.05f, 1f)]
    public float shrinkStartRatio = 0.35f;
    [Tooltip("落下中の点滅の速さ（rad/秒）")]
    public float flickerSpeed     = 12f;
    [Tooltip("点滅の深さ。0 で点滅なし")]
    [Range(0f, 1f)]
    public float flickerDepth     = 0.25f;

    [Header("粒数制限")]
    [Tooltip("1発あたりの最大粒数。超えた分は等間隔で間引く")]
    public int maxParticles = 2000;

    [Header("形の崩し（比較検証用）")]
    [Tooltip("ON: 粒の目標位置をランダムにずらして輪郭を崩し、火の粉寄りの見た目にする。\n" +
             "OFF: 元絵の形をそのまま残す（既定）")]
    public bool  scatterMode   = false;
    [Tooltip("scatterMode が ON のときのずらし量。imageScale に対する割合")]
    public float scatterAmount = 0.15f;

    [Header("シェーダー設定")]
    [Tooltip("頂点カラー非対応シェーダーへのフォールバック用。" +
             "通常は Custom/ParticleAdditive が自動で使われる")]
    [SerializeField] private Shader particleShader;

    // 粒ごとの状態。ParticleSystem.Particle[] は位置とサイズと色だけを持たせ、
    // 目標位置・速度・落下変位・基準色・立ち上がり遅延・点滅位相は自前の配列で管理する
    // （GetParticles を呼ばずに済ませるため）。
    //
    // 位置は「中心 → 目標への展開」＋「落下と拡散による変位」の重ね合わせで作る。
    // 前者は解析式（指数関数）で常に目標へ漸近し続け、後者は 0 から積分で立ち上がる。
    // どちらも途中で止まらないので、合成した動きも止まらない。
    private ParticleSystem.Particle[] _buf;
    private Vector3[]                 _targets;
    private Vector3[]                 _velocities;    // 落下＋拡散の速度
    private Vector3[]                 _fallOffset;    // 落下＋拡散の累積変位
    private Vector3[]                 _spreadAccel;
    private Color32[]                 _baseColors;
    private float[]                   _fallDelay;
    private float[]                   _flickerPhase;

    private ParticleSystem _ps;
    private Material       _material;
    private int            _count;
    private float          _startTime;
    private bool           _launched;
    private Vector3        _origin;

    // imageScale と変換解像度から導出した1粒のサイズ
    private float _particleSize;

    private const string AdditiveShaderName    = "Custom/ParticleAdditive";
    private const string VertexColorShaderName = "Custom/ParticleUnlit";

    // fallStagger の分だけ最後の粒の消滅が後ろにずれるので寿命に含める
    private float TotalLifetime => expandTime + settleTime + fadeTime + Mathf.Max(0f, fallStagger);

    // 展開の時定数。t = expandTime で 1 - e^-3 ≒ 95% 形になる
    private float ExpandTau => Mathf.Max(0.01f, expandTime) / 3f;

    // 落下・拡散の加速度が 0 から全開になるまでの時間
    private float RampDuration => Mathf.Max(0.01f, expandTime * 0.5f + settleTime);

    /// <summary>
    /// FireworkLauncher から動的生成時にシェーダーを注入するためのメソッド。
    /// 互換のため残しているが、渡されたシェーダーは Custom/ParticleAdditive も
    /// Custom/ParticleUnlit も見つからなかった場合のフォールバックとしてのみ使う。
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

        // ---- 粒サイズを解像度から導出 ----
        // 粒の間隔は imageScale / 解像度。そこに particleSizeFactor を掛けて
        // わずかに重ねる。固定値だと解像度や imageScale を変えたときに
        // 隙間だらけ（粒が小さすぎる）か団子（大きすぎる）になっていた。
        int gridSize  = Mathf.Max(1, data.width);
        _particleSize = imageScale / gridSize * Mathf.Max(0.01f, particleSizeFactor);

        _buf          = new ParticleSystem.Particle[_count];
        _targets      = new Vector3[_count];
        _velocities   = new Vector3[_count];
        _fallOffset   = new Vector3[_count];
        _spreadAccel  = new Vector3[_count];
        _baseColors   = new Color32[_count];
        _fallDelay    = new float[_count];
        _flickerPhase = new float[_count];

        float life     = TotalLifetime + 1f;
        float scatter  = scatterMode ? imageScale * scatterAmount : 0f;

        // 外向き加速度を「絵の端でちょうど spreadAcceleration」に正規化するための基準距離
        float halfSize = Mathf.Max(0.01f, imageScale * 0.5f);

        for (int i = 0; i < _count; i++)
        {
            var p = src[stride > 1f ? Mathf.Min(src.Length - 1, (int)(i * stride)) : i];

            // 旧実装と同じ座標計算（Y は上下反転）
            float lx = (p.x - 0.5f) * imageScale;
            float ly = (0.5f - p.y) * imageScale;

            var target = _origin + new Vector3(lx, ly, 0f);

            // 形を崩すモード。z は動かさない（Billboard の絵は XY 平面に載っている）
            if (scatter > 0f)
            {
                var offset = Random.insideUnitCircle * scatter;
                target += new Vector3(offset.x, offset.y, 0f);
            }

            _targets[i]    = target;
            _baseColors[i] = p.ToColor32();

            // ---- 落下と拡散（速度ではなく加速度で与える）----
            // 外向き成分は「中心からの距離に比例」させる。全粒に同じ大きさを
            // 与えると、中心付近の粒だけが自分の位置に対して極端に速く動くため
            // 絵の内側から潰れ、拡散ではなく崩壊に見えてしまう。
            // 距離に比例させると絵が相似形のまま膨らむので、素直に拡散して見える。
            var radial = target - _origin;
            _spreadAccel[i] = radial / halfSize * spreadAcceleration
                            + new Vector3(Random.Range(-driftAcceleration, driftAcceleration), 0f, 0f)
                            + Vector3.up * (riseAcceleration * Random.value);

            // 速度・変位はどちらも 0 から始めて積分で立ち上げる
            _velocities[i] = Vector3.zero;
            _fallOffset[i] = Vector3.zero;

            // 粒ごとに散り始めをずらす。既定は 0（全粒同時 = 拡散）。
            // 0より大きくすると端からパラパラ時間差で崩れる見た目になる
            _fallDelay[i]    = Random.Range(0f, Mathf.Max(0f, fallStagger));
            _flickerPhase[i] = Random.Range(0f, Mathf.PI * 2f);

            _buf[i].position          = _origin;   // Phase 1 開始時は全粒が中心
            _buf[i].velocity          = Vector3.zero;
            _buf[i].startSize         = _particleSize;
            _buf[i].startColor        = _baseColors[i];
            _buf[i].startLifetime     = life;
            _buf[i].remainingLifetime = life;
        }

        SetupParticleSystem(life);
        _ps.SetParticles(_buf, _count);

        _startTime = Time.time;
        _launched  = true;
        Debug.Log($"[ImageFX] Launched {_count} particles @ {_origin} " +
                  $"(grid={gridSize} scale={imageScale:F2} size={_particleSize:F3} " +
                  $"scatter={(scatterMode ? "ON" : "OFF")})");
        Destroy(gameObject, TotalLifetime + 0.5f);
    }

    /// <summary>
    /// 頂点カラーが効くシェーダーを優先してマテリアルを1個だけ生成する。
    /// 優先順は 加算合成 → 通常のアルファブレンド → Inspector のフォールバック。
    /// </summary>
    private bool PrepareMaterial()
    {
        var shader = Shader.Find(AdditiveShaderName);

        if (shader == null)
        {
            shader = Shader.Find(VertexColorShaderName);

            if (shader != null)
            {
                Debug.LogWarning($"[ImageFX] {AdditiveShaderName} が見つからないため " +
                                 $"{VertexColorShaderName} にフォールバックします。" +
                                 "色は正しく出ますが、粒が丸くならず塗りつぶしの正方形として" +
                                 "描かれ、加算合成による発光も効きません");
            }
        }

        if (shader == null)
        {
            shader = particleShader;
            if (shader == null)
            {
                Debug.LogError($"[ImageFX] {AdditiveShaderName} も {VertexColorShaderName} も " +
                               "フォールバック用シェーダーも見つかりません。" +
                               "Inspector の Particle Shader フィールドに " +
                               "Assets/ARHanabi/Shaders/ParticleAdditive.shader をアサインしてください。");
                return false;
            }

            Debug.LogWarning($"[ImageFX] 既定のシェーダーが見つからないため " +
                             $"'{shader.name}' にフォールバックします。" +
                             "このシェーダーが頂点カラーを読まない場合、" +
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
        main.startSize       = _particleSize;
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

    // ── 位置の更新（フェーズ分岐なしの1本の連続運動）──
    //
    // 以前は「展開 → 形を維持 → 崩れて落下」の3フェーズを if で切り替えていた。
    // 維持フェーズは position に目標座標を毎フレーム代入する = 完全な静止なので、
    // どれだけ速度を連続にしても「止まってから動き出す」動きは消えなかった。
    //
    // そこで位置を2つの成分の重ね合わせに変えた。どちらも途中で止まらない。
    //   ・展開  … origin から target へ 1 - e^(-t/τ) で漸近する。指数関数なので
    //             到達しきることがなく、常にわずかに膨らみ続ける
    //   ・落下＋拡散 … 加速度を 0 から smoothstep で立ち上げて積分する。
    //             展開が終わるのを待たず途中から重なり始めるので繋ぎ目が無い
    // 結果として、膨らみながら少しずつ沈み、そのまま拡散して消える1本の動きになる。
    private void Update()
    {
        if (!_launched) return;

        float elapsed = Time.time - _startTime;
        float dt      = Time.deltaTime;
        float life    = TotalLifetime;

        // 展開の進み具合。1 に漸近するだけで到達しないので動きが止まらない
        float expand = 1f - Mathf.Exp(-elapsed / ExpandTau);

        // 減衰の dt 非依存化。1秒で velocityDamping 倍になるよう毎フレーム換算する
        float damp    = Mathf.Pow(Mathf.Clamp01(velocityDamping), dt);
        var   gravity = new Vector3(0f, -gravityPerSec, 0f);

        float rampDuration = RampDuration;
        float fadeStart    = expandTime + settleTime;
        float invFadeTime  = 1f / Mathf.Max(0.01f, fadeTime);

        for (int i = 0; i < _count; i++)
        {
            // ---- 落下＋拡散の加速度を滑らかに立ち上げる ----
            // 加速度そのものを 0 から上げるので、速度も変位も 0 から連続に増える。
            // 立ち上げに smoothstep を使うのは、線形だと立ち上がり始めの瞬間に
            // 加速度が階段状に変わって微かな折れが見えるため。
            float rampT = Mathf.Clamp01((elapsed - _fallDelay[i]) / rampDuration);
            float ramp  = rampT * rampT * (3f - 2f * rampT);

            var v = _velocities[i];
            v *= damp;
            v += (_spreadAccel[i] + gravity) * ramp * dt;
            _velocities[i] = v;

            _fallOffset[i] += v * dt;

            _buf[i].position = _origin
                             + (_targets[i] - _origin) * expand
                             + _fallOffset[i];

            // ---- 消え方 ----
            // 残り寿命の割合。1 → 0
            float ratio = elapsed < fadeStart + _fallDelay[i]
                          ? 1f
                          : Mathf.Clamp01(1f - (elapsed - fadeStart - _fallDelay[i]) * invFadeTime);

            // サイズは終盤だけ縮める（アルファと同率で縮めると落下ではなく
            // 「縮んで消える」動きに見えてしまう）
            float sizeRatio = ratio >= shrinkStartRatio
                              ? 1f
                              : ratio / shrinkStartRatio;

            // 火の粉らしい点滅。位相は粒ごとに固定なので毎フレーム乱数を引かない。
            // ramp を掛けて、形が出来上がるまでは点滅させない（絵が読みにくくなるため）
            float flicker = 1f - flickerDepth * ramp
                          + flickerDepth * ramp * Mathf.Sin(elapsed * flickerSpeed + _flickerPhase[i]);

            var c = _baseColors[i];

            // 元のアルファを維持して掛ける（白粒子の whiteAlpha を潰さないため）
            float alpha = c.a / 255f * ratio * flicker;

            _buf[i].startSize         = _particleSize * sizeRatio;
            _buf[i].startColor        = new Color32(
                c.r, c.g, c.b,
                (byte)Mathf.Clamp(Mathf.RoundToInt(alpha * 255f), 0, 255));
            _buf[i].remainingLifetime = life;
        }

        // GetParticles は呼ばず、自前配列を書き換えて一括反映する
        _ps.SetParticles(_buf, _count);
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
    }
}
