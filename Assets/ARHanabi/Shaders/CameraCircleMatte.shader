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
// ── 役割は「外周をぼかすこと」だけ ──
//   「丸の外を黒くする」のはこのシェーダーの仕事ではない。
//   C# 側がカメラの ClearFlags を SolidColor の黒にし、背景 Quad を縮めているので、
//   映像の外側は最初から黒になっている。このシェーダーがやるのは、映像の縁と
//   その黒との間に広いグラデーションを敷いて「どこが境界か分からない」状態を作ること。
//
//   逆に言うと、C# 側の黒クリアを省くとフェードの行き先が Unity 既定の青い
//   スカイボックスになり、青いハローの輪ができて要件を真っ向から壊す。
//   この2つは必ずセットで有効化する必要がある。
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

        // ドームの内側に乗せる黒の濃さ。0 = 映像そのまま。
        // 加算合成の花火は背景が明るいほど埋もれるので、花火を目立たせたいときに
        // 0.15〜0.25 まで上げる（既定は 0 ＝ 見た目を変えない）
        _InnerDim ("Inner Dim (0 = off)", Range(0, 1)) = 0.0

        // 画面の縦横比。C# 側が毎フレーム camera.aspect を代入する
        _Aspect ("Aspect (set from script)", Float) = 1.7777778
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

                return half4(0.0, 0.0, 0.0, a);
            }
            ENDHLSL
        }
    }
}
