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
             "菊・牡丹は 0.35 前後、柳は長めに伸ばす")]
    public float dragTau = 0.35f;

    [Tooltip("寿命いっぱいで「広がり半径の何倍」垂れるか。\n" +
             "絶対値の重力加速度で持つと、寿命の長い型（柳は3.2秒）で落下量が\n" +
             "画面の高さを何倍も超えてしまう。半径に対する比にすれば、\n" +
             "全体倍率を変えても形が崩れない。\n" +
             "菊 0.35 前後 / 冠 1.8 / 柳 2.5 / 型物 0.15 が目安")]
    public float sagRatio = 0.35f;

    /// <summary>
    /// sagRatio から実際の重力加速度を求める。
    ///   落下量 = ½·g·life² = sagRatio × 広がり半径(= burstSpeed·dragTau)
    /// </summary>
    public float EffectiveGravity
    {
        get
        {
            float life = Mathf.Max(0.05f, starLifetime);
            return 2f * sagRatio * burstSpeed * dragTau / (life * life);
        }
    }

    [Header("寿命とサイズ")]
    public float starLifetime = 1.6f;
    [Range(0f, 1f)] public float lifetimeJitter = 0.18f;
    public float starSize = 0.16f;
    [Range(0f, 1f)] public float sizeJitter = 0.25f;

    [Tooltip("残り寿命がこの割合を下回ってから縮み始める")]
    [Range(0.05f, 1f)] public float shrinkFrom = 0.45f;

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

    [Header("色")]
    public Color colorA = new Color(1f, 0.85f, 0.45f);
    [Tooltip("色変化の行き先。変化菊などで使う")]
    public Color colorB = new Color(0.35f, 0.65f, 1f);
    [Tooltip("寿命のこの割合を過ぎたら colorB へ変化する。0 で変化なし")]
    [Range(0f, 1f)] public float colorShiftAt = 0f;
    [Tooltip("色変化にかける時間の割合。小さいと瞬時に切り替わる")]
    [Range(0.01f, 1f)] public float colorShiftSpan = 0.25f;

    [Header("きらめき")]
    [Tooltip("明滅の深さ。0 で明滅なし")]
    [Range(0f, 1f)] public float twinkleDepth = 0.25f;
    public float twinkleHz = 14f;
    [Tooltip("経過がこの割合を過ぎてから明滅を始める。終盤だけ瞬かせると自然")]
    [Range(0f, 1f)] public float twinkleFrom = 0.4f;

    [Header("芯入り（内側の玉）")]
    [Tooltip("0 で芯なし。外郭に対する内側の玉の速度比")]
    [Range(0f, 1f)] public float coreSpeedRatio = 0f;
    [Tooltip("芯に回す星の割合")]
    [Range(0f, 0.8f)] public float coreStarRatio = 0.35f;
    public Color coreColor = new Color(1f, 0.35f, 0.45f);

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
    [Tooltip("小玉ごとに色を変えるか（千輪菊）")]
    public bool childRandomColor = true;

    [Header("閃光（花雷）")]
    [Tooltip("開いた瞬間の白い閃光の強さ。0 で無し")]
    [Range(0f, 1f)] public float flashStrength = 0f;
    public float flashDuration = 0.12f;

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

    [Tooltip("Comet の星のうち何割を「尾」側（一方向に伸びる側）へ回すか。\n" +
             "0 だと対称な玉になり彗星に見えず、1 だと頭（中心に残る明るい塊）が\n" +
             "無くなって単なる棒状の噴出になる。頭と尾の両方が要るので中間の値にする")]
    [Range(0f, 1f)] public float tailBias = 0.7f;

    /// <summary>この型を描き切るのに必要な秒数</summary>
    public float TotalLifetime
    {
        get
        {
            float own   = starLifetime * (1f + lifetimeJitter) + trailLifetime;
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
            return stars + trails + child;
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
    //   dragTau を上げると FireworkLauncher 側の正規化で scale が下がり、星の描画サイズが
    //   縮んでしまう（DisplacementAt のコメント参照）ため、starSize を dragTau と同じ
    //   倍率で必ず一緒に上げること。ここでは既にその対応済みの値を入れてある。
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
                colorA = new Color(1f, 0.82f, 0.40f),
                twinkleDepth = 0.22f, twinkleFrom = 0.45f,
            },

            // 変化菊: 花びらの先で色が変わる
            new ShellPreset {
                name = "変化菊", category = "割物", soundKey = "kiku",
                shape = ShellShape.Sphere,
                starCount = 340, burstSpeed = 9f, dragTau = 0.46f, sagRatio = 0.35f,
                starLifetime = 2.4f, starSize = 0.20f,
                trailPerStar = 7, trailSpacing = 0.05f, trailLifetime = 0.95f,
                colorA = new Color(0.35f, 1f, 0.55f),
                colorB = new Color(1f, 0.35f, 0.75f),
                colorShiftAt = 0.5f, colorShiftSpan = 0.18f,
                twinkleDepth = 0.25f, twinkleFrom = 0.5f,
            },

            // 牡丹: 尾を引かず、光の点が広がる
            new ShellPreset {
                name = "牡丹", category = "割物", soundKey = "botan",
                shape = ShellShape.Sphere,
                starCount = 300, burstSpeed = 8.5f, dragTau = 0.48f, sagRatio = 0.28f,
                starLifetime = 2.1f, starSize = 0.28f, sizeJitter = 0.2f,
                trailPerStar = 0,                       // 尾なしが牡丹の定義
                colorA = new Color(1f, 0.45f, 0.30f),
                twinkleDepth = 0.15f, twinkleFrom = 0.5f,
            },

            // 冠（かむろ）: 星が長く燃え、大きく流れ落ちて地面近くで消える
            new ShellPreset {
                name = "冠", category = "割物", soundKey = "kamuro",
                shape = ShellShape.Sphere,
                launchViewportY = 0.66f, sizeMultiplier = 0.85f,
                starCount = 240, burstSpeed = 7f, dragTau = 0.58f, sagRatio = 1.0f,
                starLifetime = 3.5f, lifetimeJitter = 0.12f,
                starSize = 0.20f, shrinkFrom = 0.3f,
                trailPerStar = 10, trailSpacing = 0.07f, trailLifetime = 1.3f,
                trailBrightness = 0.7f,
                colorA = new Color(1f, 0.78f, 0.38f),
                twinkleDepth = 0.18f, twinkleFrom = 0.55f,
            },

            // 芯入り菊: 外郭の中にもう一重の玉が見える
            new ShellPreset {
                name = "芯入り菊", category = "割物", soundKey = "kiku",
                shape = ShellShape.Sphere,
                starCount = 380, burstSpeed = 9.5f, dragTau = 0.49f, sagRatio = 0.35f,
                starLifetime = 2.4f, starSize = 0.20f,
                trailPerStar = 6, trailSpacing = 0.05f, trailLifetime = 0.85f,
                colorA = new Color(0.55f, 0.85f, 1f),
                coreSpeedRatio = 0.42f, coreStarRatio = 0.34f,
                coreColor = new Color(1f, 0.40f, 0.35f),
                twinkleDepth = 0.2f, twinkleFrom = 0.5f,
            },

            // 型物（ハート）: 光の点で形を描く
            new ShellPreset {
                name = "型物・ハート", category = "割物", soundKey = "katamono",
                shape = ShellShape.Heart,
                starCount = 260, burstSpeed = 8f, dragTau = 0.63f, sagRatio = 0.15f,
                starLifetime = 2.5f, starSize = 0.26f,
                speedJitter = 0.05f,                    // 形を保つのでばらつきは小さく
                trailPerStar = 3, trailSpacing = 0.04f, trailLifetime = 0.55f,
                colorA = new Color(1f, 0.35f, 0.55f),
                twinkleDepth = 0.12f, twinkleFrom = 0.6f,
            },

            // 型物（リング）: 環を正面に見せる
            new ShellPreset {
                name = "型物・リング", category = "割物", soundKey = "katamono",
                shape = ShellShape.Ring,
                starCount = 200, burstSpeed = 9f, dragTau = 0.68f, sagRatio = 0.15f,
                starLifetime = 2.4f, starSize = 0.26f,
                speedJitter = 0.05f,
                trailPerStar = 4, trailSpacing = 0.045f, trailLifetime = 0.6f,
                colorA = new Color(0.6f, 0.95f, 1f),
                twinkleDepth = 0.12f, twinkleFrom = 0.6f,
            },

            // ══ ポカ物 ══

            // 柳: 玉が割れてから枝が垂れ下がるように光が落ちる
            new ShellPreset {
                name = "柳", category = "ポカ物", soundKey = "yanagi",
                shape = ShellShape.UpperHemisphere,
                launchViewportY = 0.72f, sizeMultiplier = 0.75f,
                starCount = 200, burstSpeed = 5.5f, dragTau = 0.86f, sagRatio = 1.3f,
                starLifetime = 3.7f, lifetimeJitter = 0.15f,
                starSize = 0.17f, shrinkFrom = 0.25f,
                trailPerStar = 14, trailSpacing = 0.075f, trailLifetime = 1.5f,
                trailSizeScale = 0.6f, trailBrightness = 0.75f,
                colorA = new Color(1f, 0.72f, 0.30f),
                twinkleDepth = 0.3f, twinkleFrom = 0.35f,
            },

            // 彩色柳: 落ちながら色が変わる
            new ShellPreset {
                name = "彩色柳", category = "ポカ物", soundKey = "yanagi",
                shape = ShellShape.UpperHemisphere,
                launchViewportY = 0.72f, sizeMultiplier = 0.75f,
                starCount = 200, burstSpeed = 5.5f, dragTau = 0.86f, sagRatio = 1.3f,
                starLifetime = 3.7f, starSize = 0.17f, shrinkFrom = 0.25f,
                trailPerStar = 14, trailSpacing = 0.075f, trailLifetime = 1.5f,
                trailBrightness = 0.75f,
                colorA = new Color(1f, 0.75f, 0.35f),
                colorB = new Color(0.45f, 0.6f, 1f),
                colorShiftAt = 0.45f, colorShiftSpan = 0.3f,
                twinkleDepth = 0.3f, twinkleFrom = 0.35f,
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
                starLifetime = 0.95f, starSize = 0.26f, sizeJitter = 0.35f,
                trailPerStar = 2, trailSpacing = 0.03f, trailLifetime = 0.32f,
                colorA = new Color(1f, 0.97f, 0.85f),
                twinkleDepth = 0.5f, twinkleHz = 26f, twinkleFrom = 0.1f,
                flashStrength = 0.9f, flashDuration = 0.1f,
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
                starCount = 90,                          // 親玉の星は控えめ
                // 親玉はほぼ元の速さのまま。「一瞬のポン」のあと小玉に主役が移るのが
                // 千輪の定義なので、ここを他の型ほど遅くすると小玉が開く前に
                // 間延びして見える。残光がほしいのは小玉（childLifetime）側
                burstSpeed = 5.5f, dragTau = 0.36f, sagRatio = 0.3f,
                starLifetime = 0.7f, starSize = 0.14f,
                trailPerStar = 3, trailSpacing = 0.04f, trailLifetime = 0.4f,
                colorA = new Color(1f, 0.9f, 0.7f),
                childCount = 22, childDelay = 0.4f, childStarCount = 30,
                childScatterRatio = 0.6f, childBurstSpeed = 2.8f, childLifetime = 1.5f,
                childRandomColor = true,
                twinkleDepth = 0.2f, twinkleFrom = 0.4f,
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
                starLifetime = 3.0f, starSize = 0.26f,   // dragTau を上げた分 starSize も比例して上げてある
                trailPerStar = 5, trailSpacing = 0.06f, trailLifetime = 1.3f,
                trailBrightness = 0.65f,
                colorA = new Color(0.55f, 0.25f, 0.85f),   // 紫
                colorB = new Color(0.25f, 0.45f, 1f),      // 青
                colorShiftAt = 0.4f, colorShiftSpan = 0.35f,
                twinkleDepth = 0.35f, twinkleHz = 10f, twinkleFrom = 0.3f,   // 星屑がまたたく星雲感
            },

            // 土星: 小さな本体球＋傾いた環。環は本体より大きい半径に別枠で描く
            new ShellPreset {
                name = "土星", category = "宇宙", soundKey = "planet",
                shape = ShellShape.RingedPlanet, ringTilt = 22f, ringRadius = 0.85f,
                starCount = 220, burstSpeed = 7.5f, dragTau = 0.6f, sagRatio = 0.08f,
                speedJitter = 0.05f,              // 本体と環の輪郭が滲まないようにばらつきを抑える
                starLifetime = 2.6f, starSize = 0.26f,   // dragTau に合わせて starSize も同倍率
                trailPerStar = 3, trailSpacing = 0.05f, trailLifetime = 0.7f,
                colorA = new Color(0.85f, 0.75f, 0.55f),   // 土星らしい砂色
                twinkleDepth = 0.18f, twinkleFrom = 0.55f,
            },

            // 彗星: 大半の星が中心近くに残って明るい頭を作り、一部だけ一方向に尾を引く。
            // 尾を長く見せたいので trailPerStar・trailLifetime は他の宇宙系より長めにしてある
            new ShellPreset {
                name = "彗星", category = "宇宙", soundKey = "comet",
                shape = ShellShape.Comet, tailBias = 0.75f,
                starCount = 200, burstSpeed = 8f, dragTau = 0.5f, sagRatio = 0.1f,
                starLifetime = 2.2f, starSize = 0.22f,   // dragTau は僅かな増分なので starSize もわずかに上げる程度
                trailPerStar = 10, trailSpacing = 0.06f, trailLifetime = 1.1f,
                trailSizeScale = 0.5f, trailBrightness = 0.7f,
                colorA = new Color(0.75f, 0.9f, 1f),       // 氷のような青白
                twinkleDepth = 0.2f, twinkleFrom = 0.5f,
            },

            // 超新星: 花雷と同じ閃光の仕組みを流用した「ポンと白く光ってから広がる」爆発型。
            // 中心が一瞬で真っ白に飛ぶのが個性なので colorA も白に近づけてある
            new ShellPreset {
                name = "超新星", category = "宇宙", soundKey = "supernova",
                shape = ShellShape.Sphere,
                starCount = 280, burstSpeed = 10f, dragTau = 0.4f, sagRatio = 0.15f,
                starLifetime = 1.3f, starSize = 0.22f,
                trailPerStar = 2, trailSpacing = 0.035f, trailLifetime = 0.45f,
                colorA = new Color(1f, 0.97f, 0.92f),      // ほぼ白
                twinkleDepth = 0.4f, twinkleHz = 20f, twinkleFrom = 0.15f,
                flashStrength = 1f, flashDuration = 0.15f,
            },

            // UFO円盤: 薄い円盤を正面に見せる型物。星を控えめにして輪郭が滲まない
            // クリーンな幾何学的シルエットで読ませる（尾も短めで円盤の縁を濁らせない）
            new ShellPreset {
                name = "UFO円盤", category = "宇宙", soundKey = "saucer",
                shape = ShellShape.Saucer, discThickness = 0.08f,
                starCount = 140, burstSpeed = 7f, dragTau = 0.5f, sagRatio = 0.06f,
                speedJitter = 0.04f,              // 円盤の縁を鋭く保つ
                starLifetime = 1.8f, starSize = 0.24f,   // dragTau に合わせて starSize も比例させてある
                trailPerStar = 2, trailSpacing = 0.04f, trailLifetime = 0.4f,
                colorA = new Color(0.55f, 1f, 0.95f),      // 金属的なシアン
                twinkleDepth = 0.15f, twinkleFrom = 0.6f,
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
