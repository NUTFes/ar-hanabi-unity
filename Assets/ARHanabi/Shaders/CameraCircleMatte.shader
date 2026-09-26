// ===== CameraCircleMatte =====
// hanabi画面のカメラ映像を「下から立ち上がる半楕円のドーム」に見せるための黒い幕。
// CameraCircleMatte.cs がメインカメラの子に張る1枚の Quad へ適用する専用シェーダー。
//
// ── 形について（当初は中央の真円だった）──
//   最初は画面中央の真円で作ったが、宇宙モードのコックピット枠との整合性を取るため
//   「下から立ち上がる半楕円」に変えた。枠の窓開口部は
//   「アーチ状の上端＋ほぼ平らな下端」（uv.y 0.088〜0.985 / uv.x 0.017〜0.983）で、
//   もともとドーム型をしている。ドームをその内側に収めると枠と衝突せず、
//   ドームの平らな底辺はコンソール（uv.y < 0.09）が隠してくれる。
//   絵としても「窓の向こうに地上の人がいて、その上に花火が上がる黒い夜空が広がる」
//   という本物の花火大会に近い構図になる。
//
//   _BaseY / _DomeWidth / _DomeHeight は独立なので、
//   _BaseY = 0.5 かつ _DomeWidth = _DomeHeight にすれば元の中央の真円にも戻せる。
//
// ── 役割は「外周をぼかすこと」と「ドームの外に空を描くこと」──
//   「丸の外を塗りつぶす」土台は C# 側が作っている。カメラの ClearFlags を
//   SolidColor の黒にし、背景 Quad を縮めているので、映像の外側は最初から黒。
//   このシェーダーは、映像の縁とその外側との間に広いグラデーションを敷いて
//   「どこが境界か分からない」状態を作りつつ、外側に空の絵を描く。
//
//   逆に言うと、C# 側の黒クリアを省くとフェードの行き先が Unity 既定の青い
//   スカイボックスになり、青いハローの輪ができて要件を真っ向から壊す。
//   この2つは必ずセットで有効化する必要がある。
//
// ── ドームの外は「黒」ではなく「空」──
//   当初は外側を真っ黒にしていた（要件は「花火が埋もれないよう暗くする」だけだった）。
//   ただ真っ黒だと、宇宙モードでコックピットの窓から外を覗いているのに
//   窓の外に何も無い、という絵になってしまう。そこで外側に空を描く。
//   ・通常モード … 夜空（地平side が僅かに明るい紺のグラデーション＋町明かりのにじみ＋控えめな星）
//   ・宇宙モード … 宇宙（ほぼ黒に星雲の色を薄く乗せ、星を多く・明るく、ゆっくり流す）
//
//   絵の切替は C# 側（CameraCircleMatte.cs）が SpaceModeController.MasterEnabled を見て
//   プリセットの値をまとめて流し込む形にしている。シェーダーはモードを知らない。
//   キーワードで分岐させると宇宙用と夜空用でバリアントが2倍になるし、
//   「夜空にも星雲を少しだけ乗せたい」のような中間の詰めができなくなるため。
//
//   ⚠️ 空は必ず「加算合成の花火より十分暗い」値に留めること。
//      空が明るいほど花火が埋もれる（この演出を入れた元々の目的が消える）。
//      既定値はどれも輝度 0.01〜0.06 程度に収めてある。空全体を薄めたいときは
//      C# 側の skyBrightness を下げる（0 にすれば従来どおりの真っ黒に戻る）。
//
// ── なぜ加算合成ではなく通常のアルファブレンドなのか ──
//   ParticleAdditive は「暗い空に光を足す」ので加算が正しいが、こちらは逆に
//   「映像の上に黒を乗せて消していく」のが仕事。加算では黒（＝何も足さない）を
//   乗せても映像が消えないため、Blend SrcAlpha OneMinusSrcAlpha を使う。
//
// ── 描画順（Queue = Transparent のまま上げない理由）──
//   この幕はカメラ前方 12 に置く。花火の粒は距離 2〜8（Queue=Transparent, ZWrite Off）。
//   同じ Transparent キュー内では奥のものから先に描かれるので、
//   奥にいるこの幕が先に描かれ、花火はその上に乗る
//   ＝「花火は黒い部分にもみ出して良い」という要件がそのまま満たされる。
//   キューを上げてしまうと花火より後に描かれて花火を隠してしまうので上げない。
//   （宇宙モードのコックピット枠は Transparent+100 / 距離1 なので更に手前のまま）
//
// ── アスペクト補正 ──
//   Quad は視錐台にフィットさせるので UV 0..1 が画面全体に対応する。
//   そのまま length(uv-0.5) で円を描くと画面の縦横比のぶん楕円になるため、
//   C# 側から渡す _Aspect で横方向だけ引き伸ばして真円に戻す。
Shader "Custom/CameraCircleMatte"
{
    Properties
    {
        // ── ドーム（下から立ち上がる半楕円）の形 ──
        // 単位は「画面の高さ」。uv.y = 0 が画面の下端、1 が上端。
        // 既定では底辺を画面下端(0)に置くので、楕円の下半分は画面外に出て
        // 「下から立ち上がる半楕円」になる。
        // ⚠️ C# 側はこの3つから映像Quadの大きさと位置を自動で導出する。
        //    ここだけ変えれば映像とドームの縁が揃ったまま形が変わる。
        _BaseY ("Dome Base Y (uv)", Range(-0.3, 0.5)) = 0.0
        _DomeWidth ("Dome Semi-Width (screen heights)", Range(0.05, 1.2)) = 0.56
        _DomeHeight ("Dome Semi-Height (screen heights)", Range(0.05, 1.2)) = 0.60

        // 楕円の縁に対するグラデーションの幅（半径比）。大きいほど境界が分からなくなる。
        // 0 にすると輪郭がはっきり出て「切り抜いた感」が出る。
        // ドームは丸より大きいので、同じ比率でもぼけ幅の実寸は広くなる。
        // 既定 0.25 は「縁の0.75倍までは鮮明、1.25倍で完全に黒」という配分。
        // これを上げるほど映像Quadも自動的に大きくなる（C#側がフェード外側まで覆うため）
        _Feather ("Feather (ratio of radius)", Range(0, 1)) = 0.25

        // ドームの内側で映像をどれだけ空の色に沈めるか。0 = 映像そのまま。
        // 加算合成の花火は背景が明るいほど埋もれるので、花火を目立たせたいときに
        // 0.15〜0.25 まで上げる（既定は 0 ＝ 見た目を変えない）。
        // 空の色は真っ黒に近いので、実質「黒を乗せる」のと同じに見える
        _InnerDim ("Inner Dim (0 = off)", Range(0, 1)) = 0.0

        // 画面の縦横比。C# 側が毎フレーム camera.aspect を代入する
        _Aspect ("Aspect (set from script)", Float) = 1.7777778

        // ── ドームの外に描く空 ──
        // 既定値は夜空。宇宙モードのときは C# 側が宇宙用プリセットで上書きする。
        // ⚠️ 単位は「画面の高さ」。_BaseY と同じ座標系なので、地平のにじみは
        //    ドームの底辺から立ち上がる（ドームの形を変えても追従する）

        // 空全体の強さ。0 にすると従来どおりの真っ黒に戻る（緊急時の逃げ道）
        _SkyBrightness ("Sky Brightness (0 = pure black)", Range(0, 1)) = 1.0

        // 縦のグラデーション。地平（ドームの底辺）側と天頂側の2色を補間する
        _SkyHorizonColor ("Sky Horizon Color", Color) = (0.055, 0.075, 0.13, 1)
        _SkyZenithColor  ("Sky Zenith Color",  Color) = (0.008, 0.012, 0.03, 1)

        // 地平のにじみ（夜空の町明かり）。宇宙モードでは強さ 0 にして消す
        _SkyGlowColor    ("Horizon Glow Color", Color) = (0.35, 0.28, 0.18, 1)
        _SkyGlowStrength ("Horizon Glow Strength", Range(0, 1)) = 0.10
        _SkyGlowHeight   ("Horizon Glow Height (screen heights)", Range(0.02, 1)) = 0.18

        // 星雲（宇宙モードの色味）。夜空では強さ 0
        _NebulaColor    ("Nebula Color", Color) = (0.30, 0.16, 0.55, 1)
        _NebulaStrength ("Nebula Strength", Range(0, 1)) = 0.0
        _NebulaScale    ("Nebula Scale", Range(0.5, 8)) = 2.2

        // 星。密度はセル分割数（画面の高さあたり）で、出現率と合わせて実際の数が決まる
        _StarDensity    ("Star Density (cells per screen height)", Range(10, 300)) = 60
        _StarChance     ("Star Chance (per cell)", Range(0, 1)) = 0.13
        _StarBrightness ("Star Brightness", Range(0, 3)) = 0.55
        _StarSize       ("Star Radius (screen heights)", Range(0.0005, 0.02)) = 0.0028
        _StarTwinkle    ("Star Twinkle (0 = steady)", Range(0, 1)) = 0.5
        _StarDrift      ("Star Drift (screen heights / sec)", Range(0, 0.05)) = 0.0
        _StarColor      ("Star Color", Color) = (1, 0.97, 0.9, 1)
    }
    SubShader
    {
        Tags {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
        }

        // 映像の上に黒を乗せて消すので、通常のアルファブレンド
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // SRP Batcher 対応: マテリアル単位の uniform は必ず UnityPerMaterial に入れる
            // （BackgroundRemoval.shader が裸の uniform を並べてバッチを割っている反面教師）
            CBUFFER_START(UnityPerMaterial)
                float _BaseY;
                float _DomeWidth;
                float _DomeHeight;
                float _Feather;
                float _InnerDim;
                float _Aspect;

                float  _SkyBrightness;
                float4 _SkyHorizonColor;
                float4 _SkyZenithColor;
                float4 _SkyGlowColor;
                float  _SkyGlowStrength;
                float  _SkyGlowHeight;
                float4 _NebulaColor;
                float  _NebulaStrength;
                float  _NebulaScale;
                float  _StarDensity;
                float  _StarChance;
                float  _StarBrightness;
                float  _StarSize;
                float  _StarTwinkle;
                float  _StarDrift;
                float4 _StarColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // ── 空を描くための小道具 ──
            //
            // テクスチャを持たずに手続きで描く。星や星雲の絵を素材として用意すると
            // 「画面の縦横比が変わると星が伸びる」「解像度に依存して滲む」問題が出るし、
            // 展示先のPCごとに素材の管理が要る。ここは全部式で作って
            // パラメータだけ Inspector から詰められる形にしておく。

            // セル座標から 0..1 の疑似乱数を作る。同じ座標なら常に同じ値なので
            // 「どのセルに星があるか」がフレーム間で動かない（＝星がちらつかない）
            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            // 値ノイズ。格子の乱数を滑らかに補間する（星雲のもや用）
            float ValueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);   // smoothstep 相当。格子が四角く出るのを防ぐ

                float a = Hash21(i);
                float b = Hash21(i + float2(1.0, 0.0));
                float c = Hash21(i + float2(0.0, 1.0));
                float d = Hash21(i + float2(1.0, 1.0));

                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 3オクターブで足す。1オクターブだともやが単調で「布」に見える
            float Fbm(float2 p)
            {
                return 0.532 * ValueNoise(p)
                     + 0.266 * ValueNoise(p * 2.03 + 7.1)
                     + 0.133 * ValueNoise(p * 4.01 + 13.7);
            }

            // 星を1つ描く。座標は「画面の高さ = 1」の単位（アスペクト補正済み）
            float3 Stars(float2 p)
            {
                if (_StarBrightness <= 0.0 || _StarChance <= 0.0) return float3(0.0, 0.0, 0.0);

                // 宇宙モードでは星をゆっくり流して「航行中」を出す。
                // セル分割の前に座標をずらすので、星は形を保ったまま平行移動する
                p.y += _Time.y * _StarDrift;

                float2 st   = p * _StarDensity;
                float2 cell = floor(st);
                float2 f    = frac(st);

                // 1セルにつき星は最大1つ。セルの端に寄ると隣のセルの星と近づいて
                // 対になって見えるので、0.15〜0.85 の内側に置く
                float r1 = Hash21(cell);
                float r2 = Hash21(cell + 19.19);
                float r3 = Hash21(cell + 71.71);

                // 出現判定。r3 が閾値を超えたセルにだけ星を置く
                float exists = step(1.0 - _StarChance, r3);
                if (exists <= 0.0) return float3(0.0, 0.0, 0.0);

                float2 center = float2(0.15 + r1 * 0.7, 0.15 + r2 * 0.7);

                // 距離は「画面の高さ」単位に戻して測る。こうしておけば密度を変えても
                // 星の見た目の大きさは変わらない（_StarSize だけで決まる）
                float d = length(f - center) / max(_StarDensity, 1e-4);

                // 中心が明るく、縁が急に落ちる点光。3乗は「芯があって滲みが短い」形
                float core = saturate(1.0 - d / max(_StarSize, 1e-5));
                core = core * core * core;

                // 明るさもセルごとにばらす。全部同じ明るさだと人工的な格子に見える
                float scale = lerp(0.35, 1.0, r1);

                // 瞬き。位相をセルごとにずらすので、画面全体が同時に明滅しない
                float phase   = r2 * 6.2831853;
                float twinkle = 1.0 - _StarTwinkle * 0.5 * (1.0 - sin(_Time.y * 2.1 + phase));

                return _StarColor.rgb * (core * scale * twinkle * _StarBrightness);
            }

            // ドームの外に見える空。
            // 引数は「画面の高さ = 1」に揃えた座標。x はアスペクト補正済み、
            // y は uv.y そのまま（＝ _BaseY と同じ座標系なので地平の計算に直接使える）
            float3 SkyColor(float2 p)
            {
                float uvY = p.y;

                // 1. 縦のグラデーション。ドームの底辺を地平と見なして天頂へ向かって暗くする。
                //    地平の位置を _BaseY に合わせているので、ドームの形を変えても
                //    「地面際が明るい」関係が崩れない
                float t = saturate((uvY - _BaseY) / max(1.0 - _BaseY, 1e-4));
                float3 col = lerp(_SkyHorizonColor.rgb, _SkyZenithColor.rgb, smoothstep(0.0, 1.0, t));

                // 2. 星雲のもや（宇宙モード）。閾値を切って濃淡を作らないと
                //    画面全体が一様に紫に染まって「宇宙」ではなく「色被り」に見える
                if (_NebulaStrength > 0.0)
                {
                    float n = Fbm(p * _NebulaScale + 3.7);
                    col += _NebulaColor.rgb * (smoothstep(0.42, 0.95, n) * _NebulaStrength);
                }

                // 3. 地平のにじみ（夜空の町明かり）。指数で落とすので上に行くほど急に消える
                if (_SkyGlowStrength > 0.0)
                {
                    float h    = max(uvY - _BaseY, 0.0);
                    float glow = exp(-h / max(_SkyGlowHeight, 1e-4));
                    col += _SkyGlowColor.rgb * (glow * _SkyGlowStrength);
                }

                // 4. 星
                col += Stars(p);

                return col * _SkyBrightness;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv          = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                // 横方向は _Aspect を掛けて「画面の高さ」を単位に揃える。
                // こうしておけば _DomeWidth / _DomeHeight を同じ単位で比較でき、
                // ウィンドウの縦横比が変わってもドームの形が崩れない
                float dx = (IN.uv.x - 0.5) * _Aspect;
                float dy = IN.uv.y - _BaseY;

                // 楕円の内外を表す量。e = 1 がちょうど楕円の縁
                float2 q = float2(dx / max(_DomeWidth,  1e-4),
                                  dy / max(_DomeHeight, 1e-4));
                float  e = length(q);

                // ── なぜ「下半分」を特別扱いしないのか ──
                //   dy を2乗して使うので、この式は楕円の下半分も「内側」と判定する。
                //   しかし C# 側は映像Quadの下端を画面の外（既定で uv.y = -0.04）へ
                //   置いているため、底辺より下に映像は存在せず、黒クリアがそのまま見える。
                //   結果として画面には上半分＝「下から立ち上がる半楕円」だけが現れる。
                //   ここで if (uv.y < _BaseY) a = 1 のように切ると、底辺に横一直線の
                //   はっきりした境界ができてしまい「境界が分からない」要件を壊す。
                float inner = 1.0 - _Feather;
                float outer = 1.0 + _Feather;

                float a = lerp(_InnerDim, 1.0, smoothstep(inner, outer, e));

                // ── 空はドームの内外を問わず同じ式で計算する ──
                //   「外側だけ空を描く」と分岐したくなるが、そうすると縁で
                //   空の色と黒がぶつかって輪郭が出る。色は常に空にしておき、
                //   どれだけ見えるかはアルファ（＝ドームからの距離）に任せる。
                //   ドームの内側は a=0 なので空は一切寄与せず映像がそのまま出る。
                //   フェード帯では映像から空へそのまま溶ける（境界が出ない）。
                //
                //   星も同じアルファで乗るため、フェード帯の星は薄くなる。
                //   ドームの縁の外側で星が急に現れないので、これも都合が良い。
                float3 sky = SkyColor(float2(dx, IN.uv.y));

                return half4(sky, a);
            }
            ENDHLSL
        }
    }
}
