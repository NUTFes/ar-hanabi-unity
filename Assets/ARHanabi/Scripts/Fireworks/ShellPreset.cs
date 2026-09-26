using System;
using System.Collections.Generic;
using UnityEngine;

// ===== ShellPreset =====
// 打ち上げ花火の「玉の型」をデータとして定義する。
//
// 分類は実際の花火の呼び名に合わせている。
//   割物   … 玉が割れて星が球状に飛ぶ。菊・牡丹・冠・型物
//   ポカ物 … 玉が2つに割れて中身がこぼれる。柳・蜂・花雷
//   小割物 … 親玉が割れたあと、一瞬遅れて小玉が一斉に開く。千輪
//
// ── 星の運動モデル ──
//   実際の花火は「割火薬で高速に飛び出す → 空気抵抗で急減速 → 重力で垂れる」。
//   そのため球形に開いてから下側が流れ落ちる。これを次の式で作る。
//     v(t) = v0 * exp(-t / dragTau) ＋ 重力の積分
//   dragTau が小さいほど早く止まって丸くなり、大きいほど遠くまで伸びる。
//
// ── 尾（トレイル）を自前の粒で作る理由 ──
//   Unity の TrailModule は ParticleSystem 側の更新で軌跡を作るが、
//   ここは SetParticles で位置を直接書き込む方式なので追従の保証がない。
//   実際の花火の尾は「燃えかすがその場に残って消えていく」ものなので、
//   星が通った位置に粒を置いて減衰させるほうが物理的にも正しい。

public enum ShellShape
{
    /// <summary>球状に均等（割物の基本）</summary>
    Sphere,
    /// <summary>環状。型物のリングに使う</summary>
    Ring,
    /// <summary>上半球のみ。柳のように垂れる型で自然になる</summary>
    UpperHemisphere,
    /// <summary>ハート型（型物）</summary>
    Heart,
    /// <summary>薄い円盤状。UFOのような形</summary>
    Saucer,
    /// <summary>小さな球（本体）＋傾いた大きな環。土星のような形</summary>
    RingedPlanet,
    /// <summary>2本以上の腕を持つ渦巻き。奥行きは浅く潰した銀河型</summary>
    SpiralGalaxy,
    /// <summary>非対称。ほとんどの星が中心近くに残り、一部だけ一方向に尾を引く彗星型</summary>
    Comet,
    /// <summary>五芒星（★）の輪郭。型物と同じく形で読ませる</summary>
    // 新しい値は必ず末尾に足すこと。途中に挿入すると
    // ShellPresets.asset に保存済みの shape（整数）が別の形にずれる
    Star,
}

[Serializable]
public class ShellPreset
{
    [Header("識別")]
    public string name = "菊";
    [Tooltip("分類の表示用。挙動には影響しない")]
    public string category = "割物";

    [Header("音（型ごとの効果音の鳴らし分け）")]
    [Tooltip("Resources/Sfx/Burst/<soundKey>/ と Resources/Sfx/Crackle/<soundKey>/ を\n" +
             "優先的に探すためのキー。空なら共通プール（Sfx/Burst・Sfx/Crackle 直下）を使う。\n" +
             "そのキーのフォルダが無い／中身が空でも共通プールに自動でフォールバックするので、\n" +
             "音をまだ用意していない型に設定しても安全。\n" +
             "見た目が近い型（菊・変化菊・芯入り菊など）は同じキーを共有してよい")]
    public string soundKey = "";

    [Tooltip("破裂音のあとに「パチパチ／落下音」を2枚目の音として重ねるまでの遅れ[秒]。\n" +
             "0未満なら鳴らさない／FireworkAudioPlayer の既定値を使う。\n" +
             "\n" +
             "── 現状（この回で変更）──\n" +
             "「1花火＝1音で十分」という方針に合わせて、冠・柳・彩色柳・千輪菊のランタイムでの\n" +
             "2枚重ねはやめた（この値は全て既定の -1 で未使用）。\n" +
             "\n" +
             "ただし注意点がある: これら4型のために Firefly で作った専用音\n" +
             "（kamuro_droop / yanagi_drift / senrin_pop）は、そもそも「開花後にゆっくり\n" +
             "垂れ落ちる／時間差でポンと弾ける」という“2枚目”専用の音としてプロンプトを\n" +
             "作ったため、頭に破裂の一撃が無い（無音〜微音から静かに始まる）。\n" +
             "これをそのまま単独再生すると「爆発音が無い」ように聞こえてしまう\n" +
             "（実際に指摘されて気づいた）。そこで Burst/<soundKey>/ に置いてある\n" +
             "wav 自体を、共通の爆発音（burst_explosion_01）とこの専用音を\n" +
             "この値と同じ遅れ幅（0.9秒 / 千輪は childDelay と同じ0.4秒）で\n" +
             "あらかじめ ffmpeg で1本のファイルに合成したものに差し替えてある。\n" +
             "つまり「実行時に2回 Play する」から「録音時点で2枚重ねを済ませた\n" +
             "1本のファイルを1回 Play する」に変えただけで、聞こえ方（頭に爆発＋\n" +
             "尾を引く）は元の2枚重ねと同じになるよう意図している。\n" +
             "\n" +
             "仕組み自体（このフィールドと Sfx/Crackle/<soundKey>/）は残してあるので、\n" +
             "将来また実行時の2段階に戻したくなったら、Burst 側を単発の音に戻し、\n" +
             "Crackle/<soundKey>/ に遅延用の音を置いてここへ遅れ秒数を設定すればよい。\n" +
             "\n" +
             "── 柳・彩色柳の尾の長さ調整（追加の修正）──\n" +
             "合成直後は爆発(1.27s) + drift(4.0s、0.9s遅延) で合計4.90秒あったが、\n" +
             "星の寿命は starLifetime*(1±lifetimeJitter) で 柳=最大4.26秒 /\n" +
             "彩色柳=最大4.37秒（平均は両方とも3.7秒）。つまり最悪ケースの星より\n" +
             "音の方が長く、火花が消えたあとにもパチパチが鳴り続けていた。\n" +
             "burst_yanagi_drift_01.wav を4.0秒（末尾0.4秒をフェードアウト）まで\n" +
             "詰めて、星の平均寿命よりわずかに長い程度に収まるようにした。\n" +
             "尾（Trail グレイン）は星の燃え殻の初速部分だけを表現しており\n" +
             "trailPer*trailSpacing+trailLifetime（最大2.55秒）で先に消えるため、\n" +
             "全体の見た目の長さを決めているのは星本体の寿命であることに注意")]
    public float crackleDelayOverride = -1f;

    [Header("画面内の配置")]
    [Tooltip("画面の高さ方向で開く位置（0=下端 / 0.5=中央 / 1=上端）。\n" +
             "垂れる型（冠・柳）は高く開かないと、落ちる部分が画面下に出てしまう。\n" +
             "実測: 柳を中央で開くと 6.2 ユニット分（画面の高さ以上）が画面外に落ちた")]
    [Range(0f, 1f)] public float launchViewportY = 0.5f;

    [Tooltip("この型だけの大きさ倍率。垂れる型は広がりを抑えないと上下にはみ出す")]
    [Range(0.2f, 1.5f)] public float sizeMultiplier = 1f;

    [Header("昇り（打ち上げ）")]
    [Tooltip("昇り時間の倍率。1 が既定。\n" +
             "従来は全ての型が FireworkLauncher.launchToBurstDelay の一定時間で昇っていた。\n" +
             "ところが開く高さ（launchViewportY）は型ごとに違うので、\n" +
             "高く開く柳（0.72）や冠（0.66）は同じ時間でより長い距離を飛ぶ＝速く昇って見えた。\n" +
             "実装側では先に距離で正規化して「見かけの昇り速度」を揃えたうえで、\n" +
             "この倍率を掛ける。花雷のように「速く上がって即座に割れる」個性を\n" +
             "付けたいときだけ 1 から動かす。\n" +
             "\n" +
             "注意: 昇り時間には音の上限がある。打ち上げ笛は約0.75秒なので、\n" +
             "これを超えると笛が鳴り終わってから破裂するまでに無音の隙間ができる。\n" +
             "実装側で 0.72 秒に丸めているので、大きな値を入れても伸びない")]
    [Range(0.5f, 1.5f)] public float riseTimeScale = 1f;

    [Tooltip("昇りの火の粉の濃さ倍率。大玉は濃い軌跡のほうが「重い玉が昇る」感じが出る")]
    [Range(0.3f, 2.5f)] public float riseTrailScale = 1f;

    [Tooltip("昇りの段階（画面下から上がる光跡と打ち上げ音）を丸ごと省くか。\n" +
             "\n" +
             "「打ち下ろし花火」——画面上端に玉が現れて降りてくる型のために用意した。\n" +
             "下からロケットが上がってから玉が降りてくると動きが往復して見えるので、\n" +
             "そういう型では昇りを出さず、いきなり launchViewportY の位置に出す。\n" +
             "\n" +
             "打ち上げ音も一緒に省かれる。降ってくる玉に下からの笛は合わないため。\n" +
             "音は burstSoundDelay で本当の爆発の瞬間に合わせる")]
    public bool skipRisePhase = false;

    [Tooltip("破裂音を鳴らすまでの遅れ[秒]。0 で開花と同時（従来）。\n" +
             "\n" +
             "「降ってきて最下点で開く」型のように、\n" +
             "見せ場が生成の瞬間ではなく childDelay 秒後にある場合に使う。\n" +
             "ここを 0 のままにすると、玉が現れた瞬間に爆発音が鳴って\n" +
             "実際の爆発と1秒以上ずれる。\n" +
             "\n" +
             "childDelay と同じ値を入れるのが基本")]
    public float burstSoundDelay = 0f;

    [Tooltip("最終位置（移動しきったあとの場所）の横方向のばらつき。\n" +
             "画面幅に対する標準偏差。0 で従来どおり（出現位置に一様なジッターを掛ける）。\n" +
             "\n" +
             "── なぜ「出現位置」ではなく「最終位置」を決めるのか ──\n" +
             "drift で大きく移動する型は、出現位置を散らすと\n" +
             "移動したあとの着地点が画面外へ出てしまう。\n" +
             "逆に着地点を先に決めて、そこから移動量を引いて出現位置を求めれば、\n" +
             "見せ場（＝最後に開く場所）が必ず画面に収まる。\n" +
             "\n" +
             "分布は正規分布で、中心（画面中央）がいちばん出やすい。\n" +
             "±2σ で打ち切るので、実効的な範囲は中央 ± 2×この値。\n" +
             "出現位置は逆算の結果なので画面外になることがあるが、\n" +
             "「画面外から入ってくる」動きになるだけで問題ない")]
    public float finalSpreadX = 0f;

    [Header("形")]
    public ShellShape shape = ShellShape.Sphere;

    [Tooltip("星（主役の粒）の数。多いほど密で豪華だが重くなる")]
    public int starCount = 320;

    [Header("運動")]
    [Tooltip("割火薬で飛び出す初速。広がりの大きさはここで決まる")]
    public float burstSpeed = 9f;
    [Tooltip("初速のばらつき（±割合）。0 だと完全な球で人工的に見える")]
    [Range(0f, 1f)] public float speedJitter = 0.16f;

    [Tooltip("空気抵抗の時定数[秒]。小さいほど早く止まって丸くなる。\n" +
             "菊・牡丹は 0.35 前後、柳は長めに伸ばす。\n" +
             "\n" +
             "── starSize との連動は解消済み ──\n" +
             "以前は画面フィットの倍率が dragTau に反比例し、その倍率が星の描画サイズにも\n" +
             "掛かっていたため、dragTau を上げると粒が縮んだ（starSize も同倍率で\n" +
             "上げ直す必要があった）。ShellFireworkEffect が位置用（posScale）と\n" +
             "描画サイズ用（sizeScale）で倍率を分けたので、この値は\n" +
             "「開く速さ」だけの純粋なつまみになっている。単独で動かしてよい")]
    public float dragTau = 0.35f;

    [Tooltip("星が飛び出す時刻のばらつき[秒]。0 で全ての星が同時に生まれる。\n" +
             "実物の割物は割火薬で星が押し出されるのでごくわずかな時間差がある。\n" +
             "0.03〜0.06 程度入れると「一瞬で全部出る」から「押し出される」動きに変わる。\n" +
             "型物（ハート・リング）や円盤・環のように輪郭で読ませる型は、\n" +
             "時間差があると形が滲むので 0 のままにする")]
    public float burstStagger = 0f;

    [Tooltip("寿命いっぱいで「広がり半径の何倍」垂れるか。\n" +
             "絶対値の重力加速度で持つと、寿命の長い型（柳は3.2秒）で落下量が\n" +
             "画面の高さを何倍も超えてしまう。半径に対する比にすれば、\n" +
             "全体倍率を変えても形が崩れない。\n" +
             "菊 0.35 前後 / 冠 1.8 / 柳 2.5 / 型物 0.15 が目安")]
    public float sagRatio = 0.35f;

    [Tooltip("落下の空気抵抗の時定数[秒]。0 で抵抗なし（＝従来の ½gt²）。\n" +
             "\n" +
             "── なぜ必要か ──\n" +
             "½gt² は加速し続けるため、寿命の長い型では落ちるほど速くなる。\n" +
             "柳（寿命3.7秒）や冠（3.5秒）は終盤で不自然に加速していて、\n" +
             "実物の「垂れる・お辞儀する」動きになっていなかった。\n" +
             "抵抗を入れると終端速度 g×fallDragTau で等速に落ちるようになる。\n" +
             "\n" +
             "値の目安: 冠・柳は 1.0〜1.5。割物（寿命2秒前後）は寿命が短く\n" +
             "そもそも終端に達しないので効果が薄い。垂れない型（型物・宇宙系）は 0 でよい。\n" +
             "\n" +
             "sagRatio の意味（垂れ量が広がり半径の何倍か）は EffectiveGravity 側で\n" +
             "逆算し直しているので、この値を変えても垂れの総量は変わらない。\n" +
             "変わるのは「そこへ至る速度カーブ」だけ")]
    public float fallDragTau = 0f;

    [Header("全体の移動（drift）")]
    // ── なぜこの項が要るのか ──
    //   星の位置は「原点から外へ広がる」式だけで決まっていて、
    //   しかもその式は減速して漸近的に停止する。つまり
    //   「玉そのものがどこかへ移動していく」という動きが原理的に作れなかった。
    //   彗星を「咲いてから落ちながら尾を引く」ようにするには、
    //   開花（広がり）とは独立に、玉全体が等速で移動する成分が要る。
    //
    // ── 尾はこの移動から自動的に生まれる ──
    //   尾（Trail グレイン）は「星の軌跡上に置かれ、置かれた場所に留まる」実装なので、
    //   頭が移動すれば通った道に燃えかすが残る。
    //   つまり尾を形として作り込む必要はなく、これは「実際に通った軌跡」になる。
    //   さらに古い粒ほど寿命を消化しているので、尾は根元へ向かって自然に暗くなる。
    //
    // ── 彗星専用にしていない理由 ──
    //   実装が同じなので専用にする意味がない。既定 0 で従来と完全に同一。
    //   「風で全体が流れる」「落ちていく型」にもそのまま使える。

    [Tooltip("玉全体が移動する向き。長さは無視して向きだけを使う。\n" +
             "(0,-1,0) で真下へ落ちる。斜めに流したいときは x を混ぜる")]
    public Vector3 driftDirection = Vector3.zero;

    [Tooltip("寿命いっぱいでどれだけ移動するか。広がり半径（≒画面の高さの半分）に対する比。\n" +
             "0 で移動なし＝従来の挙動。1.0 で画面の高さの半分ぶん移動する。\n" +
             "\n" +
             "絶対速度ではなく比で持つのは sagRatio と同じ理由。\n" +
             "burstSpeed × dragTau は画面フィット正規化の基準半径そのものなので、\n" +
             "比で持てば burstSpeed や dragTau を触っても\n" +
             "「画面のどれだけ動くか」の意味が変わらない。\n" +
             "\n" +
             "── これは『等速』であることに注意（落下には向かない）──\n" +
             "drift は時間に対して線形なので速度が一定になる。\n" +
             "人の目は「加速しているか」で落下かどうかを判断するので、\n" +
             "落下を drift だけで作ると、速度そのものは速くても\n" +
             "『落ちている』ではなく『一定速度で降ろされている』ように見えてしまう\n" +
             "（実際に彗星をこれで作って不自然だと指摘された）。\n" +
             "\n" +
             "落下させたい型では sagRatio（＝加速）を主役にして、\n" +
             "drift は『開花直後から動き出すための初速』として小さく添えるとよい。\n" +
             "drift が 0 だと重力は初速 0 から始まるので、\n" +
             "開花点で一瞬止まって見えてから落ち始める。\n" +
             "\n" +
             "注意: 下向きに大きくすると画面下から出る。\n" +
             "launchViewportY を上げて落ちしろを確保すること")]
    public float driftRatio = 0f;

    /// <summary>
    /// 玉全体の移動速度（ワールド単位／秒。posScale を掛ける前）。
    ///
    /// driftRatio × 広がり半径 を寿命で割ったもの。時間に対して線形（等速）にしてある。
    /// 加速は重力の項、減速は開花の項が既に担っているので、
    /// ここを等速にすることで3つの成分が役割で分かれる。
    ///
    /// driftDirection が零ベクトルか driftRatio が 0 なら零ベクトルを返し、
    /// 位置の式に何も足さない（＝従来と完全に同一の挙動）。
    /// </summary>
    public Vector3 DriftVelocity
    {
        get
        {
            if (driftRatio == 0f || driftDirection.sqrMagnitude < 1e-6f)
                return Vector3.zero;

            float life = Mathf.Max(0.05f, starLifetime);
            return driftDirection.normalized * (driftRatio * burstSpeed * dragTau / life);
        }
    }

    /// <summary>
    /// 寿命いっぱいまでの落下量の時間積分。重力加速度 1 のときの落下距離。
    ///   抵抗なし … ½·t²
    ///   抵抗あり … τf·( t − τf·(1 − e^(−t/τf)) )     ← 終端速度 g·τf に収束する
    /// </summary>
    private static float FallIntegral(float t, float fallTau)
    {
        if (fallTau <= 0.0001f) return 0.5f * t * t;
        return fallTau * (t - fallTau * (1f - Mathf.Exp(-t / fallTau)));
    }

    /// <summary>
    /// sagRatio から実際の重力加速度を求める。
    ///   落下量 = g × FallIntegral(life, fallDragTau) = sagRatio × 広がり半径(= burstSpeed·dragTau)
    ///
    /// 落下量を「絶対値の重力加速度」ではなく「広がり半径に対する比」で持つのは、
    /// 寿命の長い型（柳は3.7秒）で落下量が画面の高さを何倍も超えてしまうため。
    ///
    /// fallDragTau を足しても sagRatio の意味を保つために、積分の中身を
    /// FallIntegral に委ねて逆算している。こうすれば既に調整済みの16種の
    /// sagRatio 値がそのまま使え、抵抗の有無で垂れの総量が変わらない。
    /// </summary>
    public float EffectiveGravity
    {
        get
        {
            float life = Mathf.Max(0.05f, starLifetime);
            float integral = Mathf.Max(1e-4f, FallIntegral(life, fallDragTau));
            return sagRatio * burstSpeed * dragTau / integral;
        }
    }

    /// <summary>
    /// 小玉の空気抵抗の時定数。親玉より少し早く止まる（＝小さく開く）。
    /// ChildEffectiveGravity と ShellFireworkEffect の両方から参照されるので、
    /// 二か所に同じ係数を書かないようここに集約してある。
    /// </summary>
    public float ChildDragTau => dragTau * 0.8f;

    /// <summary>
    /// 小玉の重力加速度。EffectiveGravity と同じ考え方だが、
    /// 小玉自身の広がり半径（childBurstSpeed × ChildDragTau）と
    /// 寿命（childLifetime）で計算する。
    ///
    /// 親玉の EffectiveGravity を流用すると、寿命の違い（千輪では 0.7秒 対 1.5秒）が
    /// そのまま落下量の差になって小玉だけが画面外へ落ちる（childSagRatio のコメント参照）。
    /// </summary>
    public float ChildEffectiveGravity
    {
        get
        {
            float life = Mathf.Max(0.05f, childLifetime);
            float integral = Mathf.Max(1e-4f, FallIntegral(life, fallDragTau));
            return childSagRatio * childBurstSpeed * ChildDragTau / integral;
        }
    }

    [Header("寿命とサイズ")]
    public float starLifetime = 1.6f;
    [Range(0f, 1f)] public float lifetimeJitter = 0.18f;
    public float starSize = 0.16f;
    [Range(0f, 1f)] public float sizeJitter = 0.25f;

    [Tooltip("残り寿命がこの割合を下回ってから縮み始める")]
    [Range(0.05f, 1f)] public float shrinkFrom = 0.45f;

    [Tooltip("星が生まれてから full サイズになるまでの時間[秒]。0 で従来どおり最初から full。\n" +
             "\n" +
             "── 何を直すためのものか ──\n" +
             "星は生まれた瞬間に全て同じ一点に居る。そこで最初から full サイズだと、\n" +
             "その1点に粒が丸ごと重なって、加算合成で飽和した「大きな白い塊」になる。\n" +
             "実測: 千輪の小玉は1つ30粒。直径 0.126 world の粒が30枚重なっていた。\n" +
             "開いた瞬間だけ作り物っぽく見えるのはこれが原因。\n" +
             "\n" +
             "ごく短い時間で立ち上げると、点火して膨らむように見える。\n" +
             "0.08〜0.15秒 が目安。長くすると開花が鈍く見える。\n" +
             "\n" +
             "0 からではなく3割の大きさから始まる。完全に0にすると\n" +
             "開いた瞬間に何も無い時間ができてしまうため")]
    public float sizeGrowIn = 0f;

    [Header("消え際（落ち方の質感）")]
    // ここは「落ち方＝消え方」の肝そのものなのに、以前は全ての型で同じ式に
    // 固定されていた（星 Min(1, remain*1.25) / 尾 remain）。牡丹はスッと消し、
    // 冠・柳は長く燃え残らせたいという型ごとの差が作れなかったので、
    // 2つのつまみに分けてある。既定値は従来の式と一致する。

    [Tooltip("全開の明るさを保つ寿命の割合。この間は暗くならない。\n" +
             "既定 0.2 は従来の Min(1, remain*1.25) と同じ挙動")]
    [Range(0f, 0.9f)] public float fadeHold = 0.2f;

    [Tooltip("hold を過ぎたあとの暗くなり方。\n" +
             "  1   … 線形（従来）\n" +
             "  >1  … 序盤は明るさを保ち、最後に一気に消える（牡丹・型物向き）\n" +
             "  <1  … 早めに落ちてから長く薄く残る（冠・柳の残光向き）")]
    [Range(0.2f, 3f)] public float fadeCurve = 1f;

    [Tooltip("尾（燃えかす）の暗くなり方。既定 1 は従来の線形と同じ挙動")]
    [Range(0.2f, 3f)] public float trailFadeCurve = 1f;

    [Header("尾（トレイル）")]
    [Tooltip("星1つが残す尾の粒数。0 にすると尾を引かない＝牡丹")]
    public int trailPerStar = 6;
    [Tooltip("尾の粒を置く間隔[秒]")]
    public float trailSpacing = 0.055f;
    [Tooltip("尾の粒の寿命[秒]")]
    public float trailLifetime = 0.5f;
    [Tooltip("尾の粒のサイズ倍率（星に対する比）")]
    public float trailSizeScale = 0.55f;
    [Tooltip("尾の粒の明るさ倍率")]
    public float trailBrightness = 0.6f;

    [Tooltip("尾の粒が縮み始める残り寿命の割合。0 で星と同じ shrinkFrom を使う（従来）。\n" +
             "\n" +
             "1.0 にすると置かれた瞬間から縮み始めるので、\n" +
             "古い粒＝星から遠い粒ほど細くなり、尾が先細りになる。\n" +
             "彗星の「星から離れるほど細く消えていく尾」はこれで作る。\n" +
             "\n" +
             "星本体の shrinkFrom と分けてあるのは、\n" +
             "尾を先細りにしても星の縮み方は変えたくないため")]
    [Range(0f, 1f)] public float trailShrinkFrom = 0f;

    /// <summary>
    /// 尾の粒に実際に使う縮み始め。未設定（0）なら星と同じ shrinkFrom にフォールバックする。
    /// </summary>
    public float EffectiveTrailShrinkFrom
        => trailShrinkFrom > 0f ? trailShrinkFrom : shrinkFrom;

    [Tooltip("尾の粒が沈んでいく加速度。従来は 0.35 のハードコードだった。\n" +
             "尾は「星が通った位置に残った燃えかす」なので、星本体ほどは落ちない。\n" +
             "大きくすると煙のように流れ落ち、0 にするとその場に留まる")]
    public float trailSink = 0.35f;

    [Header("色")]
    [Tooltip("HDR発光の倍率。1 で従来どおり（＝明るさの上限が 1.0）。\n" +
             "\n" +
             "── なぜ倍率が要るのか ──\n" +
             "粒の色は ParticleSystem.Particle.startColor（Color32・8bit）で渡すため、\n" +
             "C# 側からどんなに明るい色を入れても頂点カラーは 1.0 で止まる。\n" +
             "URP のカメラは HDR で Bloom も入っているのに、\n" +
             "「芯が白く飛んで縁がグラデーションになる」花火本来の光り方が出なかった。\n" +
             "この値は Custom/ParticleAdditive の _Intensity へ渡り、天井を持ち上げる。\n" +
             "\n" +
             "粒ごとの明るさの差は colorA / colorB / trailBrightness（0〜1）で表せるので、\n" +
             "ここは型全体の「発光の強さ」だけを決める。\n" +
             "目安: 割物・宇宙系は 1.8 前後 / 花雷・超新星は 2.6 前後。\n" +
             "上げすぎるとトーンマッパーの肩に乗って白一色になり、逆に色が飛ぶ")]
    [Range(0.2f, 8f)] public float emissiveIntensity = 1f;

    public Color colorA = new Color(1f, 0.85f, 0.45f);
    [Tooltip("色変化の行き先。変化菊などで使う")]
    public Color colorB = new Color(0.35f, 0.65f, 1f);
    [Tooltip("寿命のこの割合を過ぎたら colorB へ変化する。0 で変化なし")]
    [Range(0f, 1f)] public float colorShiftAt = 0f;
    [Tooltip("色変化にかける時間の割合。小さいと瞬時に切り替わる")]
    [Range(0.01f, 1f)] public float colorShiftSpan = 0.25f;

    [Tooltip("色が変わる瞬間に星を「再点火」させる強さ。0 で無し。\n" +
             "\n" +
             "── なぜ必要になったか ──\n" +
             "色変化は寿命に対する割合（colorShiftAt）で起きるので、変化が完了する\n" +
             "のは必ず寿命の後半になる。ところがそこは星が暗くなり（fadeCurve）、\n" +
             "縮み（shrinkFrom）、点滅もしている（twinkleFrom）区間でもあるため、\n" +
             "2色目が「暗く・小さく・ちらつきながら」しか出ておらず視認できなかった。\n" +
             "実測: 変化菊は変化完了時点（u=0.68）で明るさが既に 40% しかなかった。\n" +
             "\n" +
             "全体を明るくして解決すると花火全体が白っぽくなってしまうので、\n" +
             "「変化した瞬間だけ明るさを取り戻して、そこから改めて減衰する」形にした。\n" +
             "実物の変化菊も新しい薬剤に火が移って一度明るくなるので、\n" +
             "見た目のごまかしではなく現象としても自然。\n" +
             "\n" +
             "明るさは加算合成のアルファ上限（1.0）で頭打ちになるので、\n" +
             "大きくしても白飛びはせず「失った明るさを取り戻す」上限で止まる。\n" +
             "減衰は colorShiftSpan を単位にした指数（span の3倍でほぼ0）。\n" +
             "目安 1.2〜1.5")]
    [Range(0f, 3f)] public float colorShiftRelight = 0f;

    [Header("きらめき")]
    [Tooltip("明滅の深さ。0 で明滅なし")]
    [Range(0f, 1f)] public float twinkleDepth = 0.25f;
    public float twinkleHz = 14f;
    [Tooltip("経過がこの割合を過ぎてから明滅を始める。終盤だけ瞬かせると自然")]
    [Range(0f, 1f)] public float twinkleFrom = 0.4f;

    [Tooltip("明滅を消え際へ向けて強める度合い。0 で twinkleFrom 以降の振幅が一定（従来）。\n" +
             "実物の星は燃え尽きる直前ほど激しくまたたいて消えるので、\n" +
             "1 前後入れると「引き」の質感が出る。花雷・超新星で効く")]
    [Range(0f, 3f)] public float twinkleRampUp = 0f;

    [Header("芯入り（内側の玉）")]
    [Tooltip("0 で芯なし。外郭に対する内側の玉の速度比")]
    [Range(0f, 1f)] public float coreSpeedRatio = 0f;
    [Tooltip("芯に回す星の割合")]
    [Range(0f, 0.8f)] public float coreStarRatio = 0.35f;
    public Color coreColor = new Color(1f, 0.35f, 0.45f);

    [Tooltip("芯が開くまでの遅れ[秒]。0 で外郭と同時（従来）。\n" +
             "実物の芯入りは外郭が開いたわずかあとに芯が見えてくるので、\n" +
             "0.08〜0.15 入れると「二重に開く」のが読めるようになる。\n" +
             "大きくしすぎると別の花火が重なったように見える")]
    public float coreDelay = 0f;

    [Header("千輪（小割物）")]
    [Tooltip("親玉が開いたあとに開く小玉の数。0 で千輪ではない")]
    public int childCount = 0;
    [Tooltip("小玉が開くまでの遅れ[秒]。「一瞬遅れて一斉に」が千輪の要")]
    public float childDelay = 0.42f;
    [Tooltip("小玉1つあたりの星数")]
    public int childStarCount = 26;
    [Tooltip("小玉が飛び散る距離を決める速度（親玉の初速に対する比）")]
    [Range(0f, 1f)] public float childScatterRatio = 0.55f;
    [Tooltip("小玉が開くときの星の初速")]
    public float childBurstSpeed = 2.6f;
    [Tooltip("小玉の星の寿命[秒]")]
    public float childLifetime = 0.85f;

    [Tooltip("小玉の垂れ量。小玉自身の広がり半径に対する比。\n" +
             "\n" +
             "── 親玉の重力を流用してはいけない理由（実測で見つけた不具合）──\n" +
             "以前は小玉の重力を「親玉の EffectiveGravity × 0.8」で出していた。\n" +
             "ところが EffectiveGravity は親玉の starLifetime（千輪では0.7秒）を\n" +
             "基準に逆算した値で、小玉は childLifetime（1.5秒）生きる。\n" +
             "落下量は時間の2乗で効くので、小玉は親玉の想定の4倍以上落ちていた。\n" +
             "実測: 千輪の小玉は寿命いっぱいで画面の高さの半分以上を落下し、\n" +
             "下端から出ていた（開花位置 viewportY 0.5 に対し最下点 -0.36）。\n" +
             "\n" +
             "sagRatio と同じ「広がり半径に対する比」の意味で、\n" +
             "ただし小玉自身の広がりと寿命で計算する（ChildEffectiveGravity 参照）")]
    public float childSagRatio = 0.5f;

    [Tooltip("小玉の星の大きさ。starSize と同じ単位（＝菊の星が 0.20）の絶対値。\n" +
             "0 にすると従来どおり「親玉の starSize × 0.85」になる。\n" +
             "\n" +
             "── 比率ではなく絶対値にした理由 ──\n" +
             "千輪は「親玉は一瞬の小さなポン → 主役は遅れて開く小玉」という型なので、\n" +
             "親玉の starSize は意図的に小さくしてある（0.067）。\n" +
             "小玉をその比率で持つと、主役であるはずの2段目が親玉に引きずられて\n" +
             "小さくなり、「他の花火の大玉と同じ大きさにしたい」という指定ができない。\n" +
             "絶対値にすれば菊と同じ 0.20 と書くだけで済み、\n" +
             "あとから親玉のサイズを変えても小玉が巻き添えにならない。\n" +
             "\n" +
             "なお sizeMultiplier と小玉小玉（smallShellScale）は\n" +
             "全ての星に一様に掛かるので、ここでは考慮しなくてよい")]
    public float childStarSize = 0f;

    /// <summary>
    /// 小玉の星に実際に使う大きさ。childStarSize が未設定（0）なら従来式にフォールバックする。
    /// </summary>
    public float EffectiveChildStarSize
        => childStarSize > 0f ? childStarSize : starSize * 0.85f;
    [Tooltip("小玉ごとに色を変えるか（千輪菊）")]
    public bool childRandomColor = true;

    [Tooltip("小玉が開くときの形。\n" +
             "\n" +
             "Sphere（既定）… 従来どおりランダムに散る球。\n" +
             "  千輪の小玉は1つ30粒しかなく、等間隔に並べるとかえって人工的に見えるので、\n" +
             "  ここは意図的にランダムのまま残してある。\n" +
             "\n" +
             "Sphere 以外 … 親玉と同じ形の定義（SampleDirection）を使う。\n" +
             "  「降ってきた玉が最下点で★の形に開く」といった、\n" +
             "  2段目で形を見せたい型のために用意した")]
    public ShellShape childShape = ShellShape.Sphere;

    [Header("閃光（花雷）")]
    [Tooltip("開いた瞬間の白い閃光の強さ。0 で無し")]
    [Range(0f, 1f)] public float flashStrength = 0f;
    public float flashDuration = 0.12f;

    [Tooltip("開花の瞬間に中心へ出る白い芯の大きさ（starSize に対する比）。0 で無し。\n" +
             "flashStrength は全粒を白へ寄せるだけなので、\n" +
             "「中心が一瞬白く飛ぶ」という割れた瞬間の一撃が作れなかった。\n" +
             "中心に大きめの粒を1つ置いて急速に減衰させると、Bloom と合わさって\n" +
             "開花の瞬間の光として読める。粒1個なので負荷は無い。\n" +
             "寿命は flashDuration をそのまま使う。目安 6〜14")]
    public float flashCoreSize = 0f;

    [Header("宇宙の形（円盤・環・渦巻・彗星）")]
    [Tooltip("SpiralGalaxy の腕の本数。1本だと単なる渦（銀河に見えない）、\n" +
             "多すぎると腕同士が重なって環に潰れて見える。2〜3本が「銀河」らしく読める境界")]
    public int spiralArms = 2;

    [Tooltip("RingedPlanet の環の傾き[度]。0度だと環が真円のまま本体の輪郭と重なって\n" +
             "見分けがつかず、90度だと環が線になって消える。土星のように斜めから\n" +
             "見た「楕円の環」に見えるのは、その中間（15〜30度程度）のときだけ")]
    public float ringTilt = 20f;

    [Tooltip("RingedPlanet の環の半径（本体は別に半径0.4倍程度の小さな球で描く）。\n" +
             "SampleDirection の戻り値は magnitude がそのまま半径比として使われる\n" +
             "（.normalized しない）ため、1.0 を超えると burstSpeed*dragTau から\n" +
             "計算した画面フィット半径をはみ出す。1.0 以下に収めること")]
    [Range(0.3f, 1f)] public float ringRadius = 0.9f;

    [Tooltip("Saucer の円盤の厚み（0〜1、y方向の潰し具合）。\n" +
             "0 だと完全に線（厚み無し）で見えなくなり、1 だと潰れておらず\n" +
             "ただの球に見える。UFOの「薄い円盤」らしさは 0.1〜0.2 程度で出る")]
    [Range(0f, 1f)] public float discThickness = 0.12f;

    [Tooltip("Saucer の上に載せるドーム（操縦席）の半径。円盤の半径 1.0 に対する比。\n" +
             "\n" +
             "── ドームを足した理由 ──\n" +
             "円盤の縁だけだと、正面から見ると単なる横長の楕円にしかならず\n" +
             "UFOとして読めなかった。上にドームが載って初めて\n" +
             "「円盤＋操縦席」というUFOの典型的なシルエットになる。\n" +
             "\n" +
             "円盤の半径（1.0）に対して小さすぎると点にしか見えず、\n" +
             "大きすぎると円盤より背が高くなってキノコ型に見える。0.3〜0.4 が目安")]
    [Range(0.1f, 0.8f)] public float domeRadius = 0.35f;

    [Tooltip("Saucer の星のうちドームへ回す割合。残りが円盤の縁になる。\n" +
             "ドームは円盤より周長が短いので、同じ割合だとドームだけ密になる。\n" +
             "輪郭の粒の間隔を揃えたいなら、周長の比（おおよそ 0.25〜0.35）にする")]
    [Range(0.05f, 0.6f)] public float domeStarRatio = 0.3f;

    // ── Comet は「星を1つ出して、その軌跡を光で描く」型 ──
    //   形として持つのは星のかたまりだけ。尾は一切定義しない。
    //   星が速く落ち、通った道に残る燃えかす（Trail グレイン）が1本の光の筋になる。
    //
    // ── 尾を"物"として持たせようとして2回失敗している ──
    //   1回目: 尾を進行方向の逆へ並べた星の列にした
    //          → 星は原点から自分の位置へ飛ぶので「星が上へ発射される」動きになった
    //   2回目: 星を頭の群と尾の群に分け、尾の群だけ尾を出した
    //          → 「星の塊が、それぞれ自分の尾を引きずっている」絵になった
    //   どちらも尾を配置で作ろうとしたのが誤り。尾は軌跡から生まれる。

    [Tooltip("Comet の星のかたまりの半径。広がり半径に対する比。\n" +
             "\n" +
             "星は1粒だと billboard 1枚で寂しいので既定は数粒にしてあるが、\n" +
             "ここを小さく保つことで数粒でも1つの星に見え、\n" +
             "それぞれの軌跡も1本の筋に重なる。\n" +
             "大きくすると筋が複数本に分かれて「星が尾を引きずっている」絵になる")]
    [Range(0f, 0.5f)] public float cometHeadRadius = 0.03f;

    [Tooltip("Star（★）の凹みの深さ。外側の頂点を 1 としたときの内側の頂点の半径。\n" +
             "\n" +
             "幾何学的に正しい五芒星は 0.382。粒で輪郭を描くと\n" +
             "痩せて見えるので、少し太らせた 0.42 前後が読みやすい。\n" +
             "1 に近づけると凹みが消えて十角形になり、\n" +
             "0 に近づけると細い5本の針になる")]
    [Range(0.15f, 0.9f)] public float starInnerRatio = 0.42f;

    /// <summary>
    /// この型を描き切るのに必要な秒数。
    ///
    /// burstStagger / coreDelay を足しているのは、遅れて生まれた星が
    /// 寿命を迎える前に GameObject が Destroy されるのを防ぐため
    /// （ShellFireworkEffect.Launch が この値 + 0.4 秒で破棄する）。
    /// </summary>
    public float TotalLifetime
    {
        get
        {
            float delay = Mathf.Max(Mathf.Max(0f, burstStagger), Mathf.Max(0f, coreDelay));
            float own   = delay + starLifetime * (1f + lifetimeJitter) + trailLifetime;
            float child = childCount > 0
                          ? childDelay + childLifetime * 1.3f + trailLifetime
                          : 0f;
            return Mathf.Max(own, child) + 0.3f;
        }
    }

    /// <summary>確保する必要のある粒の総数</summary>
    public int TotalParticleCount
    {
        get
        {
            int stars  = Mathf.Max(1, starCount);
            int trails = stars * Mathf.Max(0, trailPerStar);
            int child  = childCount > 0
                         ? childCount * Mathf.Max(1, childStarCount)
                         : 0;
            int flash  = flashCoreSize > 0f ? 1 : 0;
            return stars + trails + child + flash;
        }
    }

    // ── 型のライブラリ ──
    // 記事の分類に対応させてある。数値は見え方に寄せた初期値なので、
    // Inspector で調整する前提。
    //
    // ── 拡散を遅く・残光を長くした調整（この回で入れた変更）──
    //   「拡散が早くすぐ消える」という指摘を受けて、各型の dragTau（拡散の速さ）と
    //   starLifetime（星の寿命）・trailLifetime（尾＝燃えかすの寿命）を上げてある。
    //   タイプごとに元の性格を保つよう倍率を変えている。
    //     菊系・型物（元が短命）        … dragTau/starSize/starLifetime を約1.35〜1.5倍
    //     冠・柳系（元から長命）        … 約1.15〜1.2倍（すでに長いので伸ばしすぎない）
    //     花雷（バンバン鳴る速さが個性）… 約1.2倍のみ（強い明滅の速さは変えない）
    //     千輪（親玉は一瞬の「ポン」が定義）… 親玉はほぼ変えず、小玉 childLifetime を延長
    //   trailLifetime は afterglow の主役なので、上記より大きめの約1.5〜1.8倍にしてある。
    //
    //   ※以前はここに「dragTau を上げたら starSize も同じ倍率で上げること」という
    //     注意書きがあった。画面フィットの倍率が dragTau に反比例し、その倍率が
    //     星の描画サイズにも掛かっていたためで、開き方を詰めるたびに2値を
    //     同時に直す必要があった。現在は ShellFireworkEffect が位置用（posScale）と
    //     描画サイズ用（sizeScale）で倍率を分けているので、この連動は無い。
    //     dragTau は単独で動かしてよい。
    //
    //     この変更に伴い、全16種の starSize を
    //       新しい値 = 元の値 × (burstSpeed × dragTau) ÷ 4.14
    //     で機械的に変換してある（4.14 は基準半径＝菊の burstSpeed 9 × dragTau 0.46）。
    //     見た目は変換前と同一。以後 starSize は「菊を基準にした粒の大きさ」を意味する。
    //
    // ── 宇宙テーマの追加（この回で入れた変更）──
    //   category = "宇宙" の型を5つ追加した（銀河・土星・彗星・超新星・UFO円盤）。
    //   宇宙モード用の選出ロジック（FireworkLauncher.PickPreset）は別の作業で対応中で、
    //   ここでは category を正しく設定するだけでよい。
    //   これに伴い SampleDirection の引数を (ShellShape, int, int) から
    //   (ShellPreset, int, int) に変更した。新しい形（円盤・環・渦巻・彗星）は
    //   腕の本数・環の傾き・円盤の厚み・尾側への偏りなど、形ごとに固有の調整値を
    //   必要とするため、その都度メソッドの引数を増やすのではなく、プリセット自体を
    //   渡して必要な値を直接読ませることにした。既存の Sphere/Ring/UpperHemisphere/
    //   Heart の分岐は shape を p.shape に読み替えただけで、挙動は一切変えていない。

    public static List<ShellPreset> DefaultLibrary()
    {
        return new List<ShellPreset>
        {
            // ══ 割物 ══

            // 菊: 星が尾を引きながら放射状に飛び散る。花火の基本形
            new ShellPreset {
                name = "菊", category = "割物", soundKey = "kiku",
                shape = ShellShape.Sphere,
                starCount = 340, burstSpeed = 9f, dragTau = 0.46f, sagRatio = 0.35f,
                starLifetime = 2.3f, starSize = 0.20f,
                trailPerStar = 7, trailSpacing = 0.05f, trailLifetime = 0.95f,
                emissiveIntensity = 1.8f,
                colorA = new Color(1f, 0.82f, 0.40f),
                twinkleDepth = 0.22f, twinkleFrom = 0.45f,
                // 基本形なので開き方も落ち方も素直に。ごく短い押し出しだけ与える
                burstStagger = 0.04f,
            },

            // 変化菊: 花びらの先で色が変わる
            new ShellPreset {
                name = "変化菊", category = "割物", soundKey = "kiku",
                shape = ShellShape.Sphere,
                starCount = 340, burstSpeed = 9f, dragTau = 0.46f, sagRatio = 0.35f,
                starLifetime = 2.4f, starSize = 0.20f,
                trailPerStar = 7, trailSpacing = 0.05f, trailLifetime = 0.95f,
                emissiveIntensity = 1.8f,
                colorA = new Color(0.35f, 1f, 0.55f),
                colorB = new Color(1f, 0.35f, 0.75f),
                colorShiftAt = 0.5f, colorShiftSpan = 0.18f,
                twinkleDepth = 0.25f,
                burstStagger = 0.04f,
                // ── 2色目が見えるようにするための調整 ──
                // 変化が完了する u=0.68 の時点で、以前は明るさ 40%・縮小も開始済み・
                // 点滅中だったので2色目がほぼ読めなかった。
                //   colorShiftRelight … 変化の瞬間に明るさを取り戻す（主役の対策）
                //   fadeHold/fadeCurve … 変化後の減衰を緩めて残光を伸ばす
                //   shrinkFrom        … 変化が終わるまで縮み始めないよう遅らせる
                //   twinkleFrom       … 変化が読めるまで点滅を待たせる
                colorShiftRelight = 1.4f,
                fadeHold = 0.45f, fadeCurve = 0.8f,
                shrinkFrom = 0.3f,
                twinkleFrom = 0.7f,
            },

            // 牡丹: 尾を引かず、光の点が広がる
            new ShellPreset {
                name = "牡丹", category = "割物", soundKey = "botan",
                shape = ShellShape.Sphere,
                starCount = 300, burstSpeed = 8.5f, dragTau = 0.48f, sagRatio = 0.28f,
                starLifetime = 2.1f, starSize = 0.276f, sizeJitter = 0.2f,
                trailPerStar = 0,                       // 尾なしが牡丹の定義
                emissiveIntensity = 2.0f,
                colorA = new Color(1f, 0.45f, 0.30f),
                twinkleDepth = 0.15f, twinkleFrom = 0.5f,
                // 尾が無いぶん消え際が全て。長く明るさを保ってから最後に一気に落とすと
                // 「光の点がパッと広がってスッと消える」牡丹らしさが出る
                burstStagger = 0.05f, fadeHold = 0.3f, fadeCurve = 1.7f,
            },

            // 冠（かむろ）: 星が長く燃え、大きく流れ落ちて地面近くで消える
            new ShellPreset {
                name = "冠", category = "割物", soundKey = "kamuro",
                shape = ShellShape.Sphere,
                launchViewportY = 0.66f, sizeMultiplier = 0.85f,
                starCount = 240, burstSpeed = 7f, dragTau = 0.58f, sagRatio = 1.0f,
                starLifetime = 3.5f, lifetimeJitter = 0.12f,
                starSize = 0.196f, shrinkFrom = 0.3f,
                trailPerStar = 10, trailSpacing = 0.07f, trailLifetime = 1.3f,
                trailBrightness = 0.7f,
                emissiveIntensity = 1.7f,
                colorA = new Color(1f, 0.78f, 0.38f),
                twinkleDepth = 0.18f, twinkleFrom = 0.55f,
                // 「流れ落ちて地面近くで消える」のが冠。落下に抵抗を入れて
                // 終端速度で垂れるようにし、消え際は長く薄く残す。
                // 大玉なので昇りの火の粉も濃くして「重い玉が昇る」感じを出す
                burstStagger = 0.05f,
                fallDragTau = 1.2f,
                fadeHold = 0.12f, fadeCurve = 0.7f, trailFadeCurve = 0.8f,
                riseTrailScale = 1.4f,
            },

            // 芯入り菊: 外郭の中にもう一重の玉が見える
            new ShellPreset {
                name = "芯入り菊", category = "割物", soundKey = "kiku",
                shape = ShellShape.Sphere,
                starCount = 380, burstSpeed = 9.5f, dragTau = 0.49f, sagRatio = 0.35f,
                starLifetime = 2.4f, starSize = 0.225f,
                trailPerStar = 6, trailSpacing = 0.05f, trailLifetime = 0.85f,
                emissiveIntensity = 1.9f,
                colorA = new Color(0.55f, 0.85f, 1f),
                coreSpeedRatio = 0.42f, coreStarRatio = 0.34f,
                coreColor = new Color(1f, 0.40f, 0.35f),
                twinkleDepth = 0.2f, twinkleFrom = 0.5f,
                // 芯入りの見せ場は「外郭が開いてから芯が現れる」二段。
                // 同時に開くと2色が混ざって一重に見えるので、芯だけ遅らせる
                burstStagger = 0.04f, coreDelay = 0.11f,
            },

            // 型物（ハート）: 光の点で形を描く
            new ShellPreset {
                name = "型物・ハート", category = "割物", soundKey = "katamono",
                shape = ShellShape.Heart,
                starCount = 260, burstSpeed = 8f, dragTau = 0.63f, sagRatio = 0.15f,
                starLifetime = 2.5f, starSize = 0.317f,
                speedJitter = 0.05f,                    // 形を保つのでばらつきは小さく
                trailPerStar = 3, trailSpacing = 0.04f, trailLifetime = 0.55f,
                emissiveIntensity = 2.0f,
                colorA = new Color(1f, 0.35f, 0.55f),
                twinkleDepth = 0.12f, twinkleFrom = 0.6f,
                // 輪郭で形を読ませる型なので burstStagger は 0 のまま
                //（時間差があると線が滲んでハートに見えなくなる）。
                // 形が読める時間を長く取りたいので、明るさを保ってから最後に落とす
                fadeHold = 0.35f, fadeCurve = 1.6f,
            },

            // 型物（リング）: 環を正面に見せる
            new ShellPreset {
                name = "型物・リング", category = "割物", soundKey = "katamono",
                shape = ShellShape.Ring,
                starCount = 200, burstSpeed = 9f, dragTau = 0.68f, sagRatio = 0.15f,
                starLifetime = 2.4f, starSize = 0.384f,
                speedJitter = 0.05f,
                trailPerStar = 4, trailSpacing = 0.045f, trailLifetime = 0.6f,
                emissiveIntensity = 2.0f,
                colorA = new Color(0.6f, 0.95f, 1f),
                twinkleDepth = 0.12f, twinkleFrom = 0.6f,
                // ハートと同じ理由で burstStagger は 0
                fadeHold = 0.35f, fadeCurve = 1.6f,
            },

            // ══ ポカ物 ══

            // 柳: 玉が割れてから枝が垂れ下がるように光が落ちる
            new ShellPreset {
                name = "柳", category = "ポカ物", soundKey = "yanagi",
                shape = ShellShape.UpperHemisphere,
                launchViewportY = 0.72f, sizeMultiplier = 0.75f,
                starCount = 200, burstSpeed = 5.5f, dragTau = 0.86f, sagRatio = 1.3f,
                starLifetime = 3.7f, lifetimeJitter = 0.15f,
                starSize = 0.194f, shrinkFrom = 0.25f,
                trailPerStar = 14, trailSpacing = 0.075f, trailLifetime = 1.5f,
                trailSizeScale = 0.6f, trailBrightness = 0.75f,
                emissiveIntensity = 1.6f,
                colorA = new Color(1f, 0.72f, 0.30f),
                twinkleDepth = 0.3f, twinkleFrom = 0.35f,
                // 柳は落ち方が主役。寿命3.7秒あるので抵抗なしでは終盤で加速し続け、
                // 「垂れ下がる」ではなく「落下する」動きになっていた。
                // 抵抗を強めに入れて終端速度で枝が垂れるようにする。
                // 消え際も長く薄く残して枝の先が空に溶けるようにした
                fallDragTau = 1.4f,
                fadeHold = 0.1f, fadeCurve = 0.65f, trailFadeCurve = 0.8f,
                trailSink = 0.25f,
                riseTrailScale = 1.3f,
            },

            // 彩色柳: 落ちながら色が変わる
            new ShellPreset {
                name = "彩色柳", category = "ポカ物", soundKey = "yanagi",
                shape = ShellShape.UpperHemisphere,
                launchViewportY = 0.72f, sizeMultiplier = 0.75f,
                starCount = 200, burstSpeed = 5.5f, dragTau = 0.86f, sagRatio = 1.3f,
                starLifetime = 3.7f, starSize = 0.194f, shrinkFrom = 0.25f,
                trailPerStar = 14, trailSpacing = 0.075f, trailLifetime = 1.5f,
                trailBrightness = 0.75f,
                emissiveIntensity = 1.6f,
                colorA = new Color(1f, 0.75f, 0.35f),
                colorB = new Color(0.45f, 0.6f, 1f),
                colorShiftAt = 0.45f, colorShiftSpan = 0.3f,
                twinkleDepth = 0.3f,
                // 柳と同じ落ち方。ただし色変化を見せる必要があるので、
                // 変化が完了する u=0.75 まで明るさを残してから緩やかに落とす。
                // 変化菊より relight を控えめにしてあるのは、柳は
                // 「垂れながらゆっくり変わる」のが持ち味で、
                // 強く再点火させると変化が唐突に見えるため
                fallDragTau = 1.4f,
                colorShiftRelight = 1.2f,
                fadeHold = 0.35f, fadeCurve = 0.7f, trailFadeCurve = 0.8f,
                trailSink = 0.25f,
                twinkleFrom = 0.55f,
                riseTrailScale = 1.3f,
            },

            // 花雷: 強い光と閃光を伴う
            new ShellPreset {
                name = "花雷", category = "ポカ物", soundKey = "hanarai",
                shape = ShellShape.Sphere,
                // dragTau・starLifetime は他の型ほど伸ばしていない。
                // 「バンバンと雷のような」速い明滅・強い閃光が花雷の個性なので、
                // 拡散や燃え尽きまで遅くしすぎると個性が薄れる。伸ばすのは
                // trailLifetime（燃えかすの残光）だけ大きめにしてある
                starCount = 160, burstSpeed = 7.5f, dragTau = 0.27f, sagRatio = 0.3f,
                starLifetime = 0.95f, starSize = 0.127f, sizeJitter = 0.35f,
                trailPerStar = 2, trailSpacing = 0.03f, trailLifetime = 0.32f,
                emissiveIntensity = 2.8f,
                colorA = new Color(1f, 0.97f, 0.85f),
                twinkleDepth = 0.5f, twinkleHz = 26f, twinkleFrom = 0.1f,
                flashStrength = 0.9f, flashDuration = 0.1f,
                // 「バンバンと雷のように」が花雷。開花の一撃を白い芯で作り、
                // 終盤へ向けて明滅を強めて激しく消える。
                // 速く上がって即座に割れるほうが雷らしいので昇りも短くする
                flashCoreSize = 10f,
                twinkleRampUp = 1.2f,
                fadeHold = 0.15f, fadeCurve = 1.4f,
                riseTimeScale = 0.85f,
            },

            // ══ 小割物 ══

            // 千輪: 親玉が割れた一瞬あとに、小玉が一斉に開く
            new ShellPreset {
                name = "千輪菊", category = "小割物",
                // 小玉のポップ音（Burst/Senrin/ に2バリエーション）を単発の破裂音として使う。
                // 以前は crackleDelayOverride = childDelay で「遅延2枚目」として鳴らしていたが、
                // 1花火＝1音の方針に合わせてやめた
                soundKey = "senrin",
                shape = ShellShape.Sphere,
                // ── 星の数は「密度」で決める ──
                //   星が広がる面積に対して数が足りないと、花火ではなく
                //   散らばった粒に見える。目安は「隣接する星の間隔 ÷ 星の直径」で、
                //   花火に見えている菊は 3.9（星がほぼ隣と触れている）。
                //   千輪の親玉は 90個・サイズ0.067 のとき 19.8 で、
                //   星同士が直径の約20倍離れていた＝点の集まりにしか見えない。
                //   200個・サイズ0.11 で 8.1 まで詰めてある。
                //
                //   親玉のサイズを菊（0.20）より小さいままにしているのは
                //   「最初の爆発は小さくてよい」という意図を保つため。
                //   足りない密度はサイズではなく数で稼いでいる
                starCount = 200,
                // 親玉はほぼ元の速さのまま。「一瞬のポン」のあと小玉に主役が移るのが
                // 千輪の定義なので、ここを他の型ほど遅くすると小玉が開く前に
                // 間延びして見える。残光がほしいのは小玉（childLifetime）側
                // sizeMultiplier は 1.0 のまま（既定）にしてある。
                // ここを下げると全ての星が一律に縮み、「小玉は菊の大玉と同じ大きさ」という
                // childStarSize の指定が嘘になるため。画面に収めるのは
                // launchViewportY と小玉の散り／開きの側で調整する。
                // 実測（散り0.46 + 開き0.38 + 落下0.19 halfView、打上ばらつき±0.05込み）で
                // 最下点 viewportY 0.04 / 最上点 0.98
                // 小玉の落下を抑えたので、上下がほぼ対称になる位置へ寄せてある
                launchViewportY = 0.53f,
                burstSpeed = 5.5f, dragTau = 0.36f, sagRatio = 0.18f,
                starLifetime = 0.7f, starSize = 0.11f,
                // 星が生まれた瞬間の重なりを避けるための立ち上がり。
                // 小玉が「点火して膨らむ」ように見える
                sizeGrowIn = 0.10f,
                trailPerStar = 5, trailSpacing = 0.04f, trailLifetime = 0.4f,
                // ── 発光を落としてある ──
                //   星数を 30→70 に増やしたので、同じ場所に重なる星が倍以上になった。
                //   加算合成では重なるほど明るくなるため、発光倍率まで上げると
                //   小玉が飽和して「光りすぎたカラフルな玉」になる。
                //   密度を上げたぶん発光は下げるのが正しい向き
                emissiveIntensity = 1.6f,
                // ── 星ごとの大きさのばらつき ──
                //   小玉の星は以前サイズが完全に固定で、1,540粒すべてが
                //   同じ大きさで並んでいた（＝同じ粒を撒いたように見える）。
                //   親玉と共通のこの値でばらつかせる。実物の星も大きさは揃っていない
                sizeJitter = 0.45f,
                colorA = new Color(1f, 0.9f, 0.7f),
                // ── 主役は2段目の小玉 ──
                // 親玉の starSize（0.067）は「一瞬の小さなポン」を作るために小さくしてある。
                // 小玉をその比率で持つと主役のほうが小さくなる逆転が起きるので、
                // childStarSize で絶対値を指定する（菊の大玉と同じ 0.20）。
                //
                // 小玉の落下は childSagRatio（小玉自身の広がりと寿命で計算）で決まる。
                // 以前は親玉の重力を流用していたため、寿命が2倍以上ある小玉が
                // 画面の高さの半分を落下して下端から出ていた
                // 小玉1つあたりの星数。30個だと開いた球の面積に対して数が足りず、
                // 星同士が直径の8倍離れて「粒の集まり」に見えていた。
                // 70個で 4.2 になり、菊（3.9）とほぼ同じ密度になる
                childCount = 22, childDelay = 0.4f, childStarCount = 70,
                // 散り・開きとも約15%広げてある（散り0.52 + 開き0.43 halfView）。
                // これ以上広げると、散りと開きが同じ向きに揃った最悪ケースで
                // 上下の端に届いてしまう
                childScatterRatio = 0.78f, childBurstSpeed = 3.0f, childLifetime = 1.5f,
                // ── 小玉の星の大きさ ──
                //   一度 0.14 まで下げたが、これは「開いた瞬間に粒が重なって
                //   飽和した白い塊になる」対策だった。その役目は sizeGrowIn
                //  （生まれた瞬間は3割の大きさから始まる）が担っているので、
                //   サイズは密度の都合で決めてよい。
                //   0.18 に戻して星の間隔を詰めている
                childStarSize = 0.18f,
                // 落下を抑えたぶん、散り・開きを広げる余地を作っている
                childSagRatio = 0.32f,
                childRandomColor = true,
                // ── 明滅を早めから、深めに ──
                //   星ごとに位相がずれているので、明滅は「星ごとの明るさの違い」を
                //   作る役目も持つ。開いてすぐ（0.15）から効かせることで、
                //   全粒が同じ明るさで揃っている時間を無くす
                twinkleDepth = 0.35f, twinkleFrom = 0.15f,
                // burstStagger は 0 のまま。親玉の「一瞬のポン」が千輪の定義なので、
                // ここに時間差を入れると小玉に主役が移る前に間延びして見える。
                // 代わりに親玉の割れる瞬間だけ芯で光らせて「ポン」を強調する
                flashCoreSize = 6f, flashDuration = 0.09f,
            },

            // ══ 宇宙 ══
            // 星まつり企画向けの宇宙モード用。dragTau は既存の割物（0.35〜0.5）に
            // ほぼ揃えてあるので starSize も既存と同じ水準のまま（大きく崩していない）。
            // sagRatio は全て低め（0.05〜0.2）にして、実物の花火のような
            // 「垂れ・お辞儀」が出ないようにしてある（宇宙の形が歪んで見えるため）。

            // 銀河: 渦を巻きながら開く。奥行きが浅いので正面からは渦巻銀河に見える
            new ShellPreset {
                name = "銀河", category = "宇宙", soundKey = "galaxy",
                shape = ShellShape.SpiralGalaxy, spiralArms = 2,
                starCount = 280, burstSpeed = 8f, dragTau = 0.6f, sagRatio = 0.08f,
                speedJitter = 0.06f,              // 腕の形を保つのでばらつきは小さく
                starLifetime = 3.0f, starSize = 0.301f,
                trailPerStar = 5, trailSpacing = 0.06f, trailLifetime = 1.3f,
                trailBrightness = 0.65f,
                emissiveIntensity = 1.8f,
                colorA = new Color(0.55f, 0.25f, 0.85f),   // 紫
                colorB = new Color(0.25f, 0.45f, 1f),      // 青
                colorShiftAt = 0.4f, colorShiftSpan = 0.35f,
                twinkleDepth = 0.35f, twinkleHz = 10f, twinkleFrom = 0.55f,   // 星屑がまたたく星雲感
                // 渦の腕を輪郭で読ませる型なので burstStagger は 0。
                // 星雲らしく「じわっと薄れて消える」ようにフェードを長く取る。
                // 紫→青の変化が完了する u=0.75 まで明るさを残す
                colorShiftRelight = 1.2f,
                fadeHold = 0.4f, fadeCurve = 0.7f, trailFadeCurve = 0.85f,
                // 変化が完了する u=0.75 より前に縮み始めないよう遅らせる
                //（既定の 0.45 だと u>0.55 から縮んで2色目が小さくなる）
                shrinkFrom = 0.25f,
            },

            // 土星: 小さな本体球＋傾いた環。環は本体より大きい半径に別枠で描く
            new ShellPreset {
                name = "土星", category = "宇宙", soundKey = "planet",
                shape = ShellShape.RingedPlanet, ringTilt = 22f, ringRadius = 0.85f,
                starCount = 220, burstSpeed = 7.5f, dragTau = 0.6f, sagRatio = 0.08f,
                speedJitter = 0.05f,              // 本体と環の輪郭が滲まないようにばらつきを抑える
                starLifetime = 2.6f, starSize = 0.283f,
                trailPerStar = 3, trailSpacing = 0.05f, trailLifetime = 0.7f,
                emissiveIntensity = 1.8f,
                colorA = new Color(0.85f, 0.75f, 0.55f),   // 土星らしい砂色
                twinkleDepth = 0.18f, twinkleFrom = 0.55f,
                // 本体と環の輪郭で読ませるので burstStagger は 0。
                // 形が読める時間を長く取るため明るさを保ってから落とす
                fadeHold = 0.35f, fadeCurve = 1.5f,
            },

            // 彗星: 開花点で小さな火球ができ、そのまま真下へ落ちながら尾を引く。
            //
            // ── 尾は「形」ではなく「実際に通った軌跡」──
            //   以前は尾を形として並べていたが、全ての星が同時に原点から出る以上、
            //   それは静止した噴出にしかならず、開花の瞬間に完成形が出てしまっていた。
            //   今は driftDirection / driftRatio で玉全体を落下させ、
            //   その通り道に残る燃えかす（Trail グレイン）が尾になる。
            //   だから「咲いてから、進むにつれて尾が伸びる」動きになる。
            //
            // ── 打ち下ろし花火。降ってきて最下点で★に開く ──
            //   1. 画面上端に玉が現れる（昇りは出さない: skipRisePhase）
            //   2. 尾を引きながら加速して降りてくる（drift + 重力、軌跡が尾になる）
            //   3. 最下点で★の形に開く（千輪の小玉の仕組みを1発だけ使う）
            //
            // ── 2段目に千輪の仕組みを流用できる理由 ──
            //   小玉が生まれる位置は「親玉が childDelay の時点で居る場所」を
            //   親と同じ運動式で計算している（DisplacementAt に drift が入っている）。
            //   だから childCount = 1 / childScatterRatio = 0 にすれば、
            //   散らばらずに「降ってきた玉のいる位置」でちょうど1発開く。
            //   starLifetime = childDelay にしてあるので、
            //   玉が消えるのと★が開くのが同じ瞬間になる。
            //
            //   childShape = Star で、小玉の星が★の輪郭に並ぶ。
            //   小玉には drift を効かせていないので（ShellFireworkEffect 参照）、
            //   開いた★が下へ流れて崩れることはない。
            //
            // ── 1つの星＋1本の光の筋 ──
            //   降下中の見た目は「星を1つ出して、その軌跡を光の筋で描く」。
            //   尾は形として持たない（cometHeadRadius のコメント参照）。
            //
            // ── 星を3粒にしている理由 ──
            //   1粒だと billboard 1枚で、筋も1本の細い点線になって寂しい。
            //   cometHeadRadius 0.03 のごく小さなかたまりにすると、
            //   3粒が重なって1つの明るい星に見え、3本の軌跡も1本の筋に重なって
            //   密度と明るさだけが増す。
            //   speedJitter を 0 にしてあるのが要点で、ばらつきがあると
            //   3粒が落下中に離れて筋が3本に割れてしまう。
            //   lifetimeJitter も 0（3粒が別々に消えると星が欠けて見える）。
            //
            // ── 落下は重力（加速）が主役 ──
            //   等速の drift だけで落とすと、速度自体は速くても
            //   「落ちている」ではなく「一定速度で降ろされている」ように見える。
            //     sagRatio 0.90 … 主役。加速する落下
            //     driftRatio 0.30 … 開花直後から動き出すための初速。
            //                       0 にすると重力が初速0から始まり、
            //                       開花点で一瞬止まって見えてから落ち始める
            //   初速 0.56 → 終速 3.90 units/秒。菊の星の終速（0.79）の約5倍。
            //
            // ── 尾が先細りになる仕組み ──
            //   trailShrinkFrom 1.0 で、尾の粒は置かれた瞬間から縮み始める。
            //   古い粒＝星から遠い粒ほど細くなるので、
            //   星の位置がいちばん太く、離れるほど細く消えていく。
            //
            // ── 値の決め方 ──
            //   移動量は 横 +0.200 / 縦 -0.521 viewport（弧を描く）
            //   ★が開く場所 … 横は中央±0.32 の正規分布、縦は常に 0.50（画面中央）
            //   出現位置    … そこから移動量を引いた結果。u は -0.02〜0.62、v は約 1.02
            //                 （画面のすぐ外側から入ってくる）
            //   starLifetime = childDelay = 1.3 … 玉が消えるのと★が開くのが同じ瞬間
            //   speedJitter/lifetimeJitter 0 … 3粒がばらけると筋が割れ、星が欠ける
            //   trailPerStar × trailSpacing = 1.30秒 = starLifetime
            //                 … 降下の全区間に燃えかすを置く
            //   trailLifetime 0.9 … 尾の長さ。寿命より短くして
            //                       「頭に付いてくる有限の尾」にする（全部残すと上端まで線が伸びる）
            //   trailSink 0.05 … 筋は置かれた場所に留まってこそ「通った道」に見える
            //   childBurstSpeed 4.2 … ★の外接半径 0.42 halfView
            //                         （縦 0.19 / 横 0.11 viewport）
            //   childSagRatio 0.08 … ★が垂れて形が崩れないよう重力をほぼ切る
            //   childStarCount 120 … 10辺 × 12粒。辺の粒間隔 0.056 < 粒の直径 0.138 で連続
            //   burstSoundDelay = childDelay … 音を★が開く瞬間に合わせる
            //
            //   粒数 3×(1+160) + 120 = 603（上限 5,000）
            //   全体の尺 ≈ 4.1 秒
            new ShellPreset {
                name = "彗星", category = "宇宙", soundKey = "comet",
                shape = ShellShape.Comet, cometHeadRadius = 0.03f,
                skipRisePhase = true,                      // 昇りは出さない（打ち下ろし）
                // ── 斜めに入ってきて弧を描いて落ちる ──
                //   drift をほぼ横向きにして、縦方向は重力に任せている。
                //   drift の向きを斜め下にすると、縦の変位を drift が食ってしまい
                //   重力を効かせる余地が無くなって「加速する落下」に見えなくなる。
                //   横は drift、縦は重力、と役割を分けると弾道の弧になる。
                //
                //   進行方向は水平から 18度 → 69度 へ変化する。
                //   画面上の傾きは垂直から 21度（横 0.20 / 縦 0.52 viewport）。
                //   ビューポートは横に約1.78倍広いので、world 座標で同じだけ動かしても
                //   画面上では横の移動が縮んで見える。斜めに見せるには
                //   x 成分をかなり大きく取る必要がある
                driftDirection = new Vector3(0.95f, -0.31f, 0f), driftRatio = 0.83f,
                // ── 出現位置ではなく「★が開く場所」を基準に置く ──
                //   finalSpreadX で最終位置を正規分布（中央がいちばん出やすい）で決め、
                //   移動量を引いて出現位置を逆算する。着地点が必ず画面に収まる。
                //   σ 0.16 なので ±2σ = 中央 ±0.32 の範囲に着地する。
                //   ★の横半径 0.106 を足しても画面内（0.074〜0.926）に収まる。
                //
                //   launchViewportY 1.02 は「縦の移動量 0.521 を引くと
                //   最終位置がちょうど画面中央（0.50）になる」ように決めた値。
                //   1.0 を超えているので出現は画面のすぐ外側になり、
                //   彗星が枠の外から入ってくる動きになる
                launchViewportY = 1.02f, finalSpreadX = 0.16f,
                starCount = 3, burstSpeed = 8f, dragTau = 0.3f, sagRatio = 0.90f,
                speedJitter = 0f,
                starLifetime = 1.3f, lifetimeJitter = 0f,
                starSize = 0.45f, sizeJitter = 0.10f,
                // 降下中はずっと full の明るさ・大きさを保ち、★に入れ替わる直前だけ落とす。
                // 既定のまま（fadeHold 0.2 / shrinkFrom 0.45）だと
                // 落ちながらどんどん痩せて暗くなり、爆発の前に力尽きたように見える
                shrinkFrom = 0.15f,
                // ── 尾を「粒の列」ではなく「光の線」に見せる ──
                //   尾の粒は丸いフォールオフなので、間隔が直径と同じくらいだと
                //   数珠つなぎの粒に見えてしまう。直径の 1/4 まで詰めて
                //   常に4粒前後が重なるようにすると、加算合成で連続した光の線になる。
                //   終速 4.40 units/秒 × 間隔 0.0081秒 = 0.036 に対し
                //   粒の直径 0.141 なので、重なりは約3.9粒。
                //   trailSizeScale を下げて線を細くしてあるぶん、必要な密度は上がっている。
                //
                //   重なりが増えると加算で明るくなりすぎるので
                //   trailBrightness を 0.9 → 0.32 に下げて相殺している
                trailPerStar = 160, trailSpacing = 0.0081f, trailLifetime = 0.9f,
                trailSizeScale = 0.5f, trailBrightness = 0.32f, trailSink = 0.05f,
                trailShrinkFrom = 1.0f,                    // 尾を先細りにする
                emissiveIntensity = 2.4f,
                colorA = new Color(0.75f, 0.9f, 1f),       // 氷のような青白
                twinkleDepth = 0f,
                fadeHold = 0.85f, fadeCurve = 1.0f, trailFadeCurve = 0.9f,
                // ── 最下点で★に開く（2段目）──
                childCount = 1, childDelay = 1.3f, childShape = ShellShape.Star,
                childScatterRatio = 0f,                    // 散らさず、降ってきた位置そのもので開く
                childStarCount = 120, childBurstSpeed = 4.2f, childLifetime = 1.2f,
                childSagRatio = 0.08f, childStarSize = 0.22f,
                childRandomColor = false,                  // ★は単色のほうが形が読める
                burstSoundDelay = 1.3f,                    // 音を★が開く瞬間に合わせる
                starInnerRatio = 0.42f,
            },

            // 超新星: 花雷と同じ閃光の仕組みを流用した「ポンと白く光ってから広がる」爆発型。
            // 中心が一瞬で真っ白に飛ぶのが個性なので colorA も白に近づけてある
            new ShellPreset {
                name = "超新星", category = "宇宙", soundKey = "supernova",
                shape = ShellShape.Sphere,
                starCount = 280, burstSpeed = 10f, dragTau = 0.4f, sagRatio = 0.15f,
                starLifetime = 1.3f, starSize = 0.213f,
                trailPerStar = 2, trailSpacing = 0.035f, trailLifetime = 0.45f,
                emissiveIntensity = 3.0f,
                colorA = new Color(1f, 0.97f, 0.92f),      // ほぼ白
                twinkleDepth = 0.4f, twinkleHz = 20f, twinkleFrom = 0.15f,
                flashStrength = 1f, flashDuration = 0.15f,
                // 「中心が一瞬で真っ白に飛ぶ」のが超新星。芯を全型で一番大きくする。
                // 花雷と同じく終盤へ向けて明滅を強めて激しく消える
                flashCoreSize = 14f,
                burstStagger = 0.03f,
                twinkleRampUp = 1f,
                fadeHold = 0.2f, fadeCurve = 1.3f,
                riseTimeScale = 0.9f,
            },

            // UFO円盤: 円盤＋その上に載るドーム（操縦席）を正面に見せる型物。
            // 星を控えめにして輪郭が滲まないクリーンな幾何学的シルエットで読ませる
            //（尾も短めで円盤の縁を濁らせない）。
            //
            // 以前は円盤の縁だけだったため、正面からは単なる横長の楕円にしか
            // 見えず UFO として読めなかった。ドームを足して典型的なシルエットにしてある。
            // 星数を増やしてあるのは、輪郭を2つ（円盤＋ドーム）描くようになったぶん
            // 1本あたりの粒が減って線が途切れて見えるため
            new ShellPreset {
                name = "UFO円盤", category = "宇宙", soundKey = "saucer",
                shape = ShellShape.Saucer,
                discThickness = 0.12f, domeRadius = 0.35f, domeStarRatio = 0.3f,
                starCount = 180, burstSpeed = 7f, dragTau = 0.5f, sagRatio = 0.06f,
                speedJitter = 0.04f,              // 円盤の縁を鋭く保つ
                starLifetime = 1.8f, starSize = 0.203f,
                trailPerStar = 2, trailSpacing = 0.04f, trailLifetime = 0.4f,
                emissiveIntensity = 2.0f,
                colorA = new Color(0.55f, 1f, 0.95f),      // 金属的なシアン
                twinkleDepth = 0.15f, twinkleFrom = 0.6f,
                // 円盤の縁を鋭く保つのが眼目なので burstStagger は 0。
                // 幾何学的なシルエットを読ませる時間を長く取り、最後に一気に消す
                fadeHold = 0.4f, fadeCurve = 1.8f,
            },
        };
    }

    /// <summary>小玉に割り当てる色。千輪菊はさまざまな色の小玉を使う</summary>
    public static readonly Color[] ChildPalette =
    {
        new Color(1f,    0.40f, 0.35f),   // 赤
        new Color(1f,    0.80f, 0.35f),   // 橙
        new Color(0.95f, 1f,    0.45f),   // 黄
        new Color(0.40f, 1f,    0.55f),   // 緑
        new Color(0.45f, 0.75f, 1f),      // 青
        new Color(0.85f, 0.55f, 1f),      // 紫
        new Color(1f,    0.55f, 0.85f),   // 桃
    };
}
