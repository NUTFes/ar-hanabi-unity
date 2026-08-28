// ===== SpaceCraft =====
// UFO（宇宙船）／エイリアンの見た目を、テクスチャ資産を1枚も使わずに
// パーティクルクアッドの UV から手続き的に描くシェーダー。
//
// ── ParticleAdditive と同じ骨格にした理由 ──
//   ImageFireworkEffect / LaunchTrailEffect など、この項目のすべての動的エフェクトは
//   「ParticleSystem 1個 + Billboard + SetParticles()」で描かれており、SpriteRenderer は
//   このコードベースに1件も存在しない（意図的な統一であって漏れではない）。
//   UfoEntity もこの流儀に合わせるため、描画側も ParticleAdditive.shader の
//   URP タグ・CBUFFER・instancing 配線をそのまま流用し、Properties と frag だけを書き換えた。
//
// ── 加算合成にした理由 ──
//   花火と同じ「暗い夜空に光が足し合わされる」画面に UFO も同居させるため、
//   通常のアルファブレンドより加算のほうが「発光する未来的な機体」に見え、
//   かつ花火のパーティクルと同じブレンドモードなので深度ソートの破綻が起きにくい
//   （両方とも Billboard の半透明クアッドで、奥行きも同程度の距離感で描かれる）。
//
// ── _Kind で2形状を切り替える理由 ──
//   UFO 1体につき GameObject 1個・Material 1個（UfoEntity.PrepareMaterial() で
//   `new Material(shader)` して個別インスタンス化している）なので、
//   マテリアルごとに独立して _Kind を持たせても他個体と干渉しない。
//   パーティクルの頂点属性（startColor 等）に形状情報を積む必要がないため単純になる。
//
// ── 形状をUV距離だけで作る理由 ──
//   円盤・ドーム・頭・目、すべて「中心からの距離を軸ごとに引き伸ばした楕円判定」で
//   十分に読める絵になる。SDFの厳密な合成（smooth min 等）までは不要で、
//   小さく表示される前提（画面に対してUFOは花火の星よりわずかに大きい程度）なので
//   輪郭の厳密さよりも「初見で何のシルエットか分かる」ことを優先した。
Shader "Custom/SpaceCraft"
{
    Properties
    {
        // 機体全体に掛けるティント。UFOの金属的な質感やエイリアンの肌色を単色で表現する
        _BaseColor ("Base Color", Color) = (0.55, 0.85, 0.95, 1)

        // 0 = 空飛ぶ円盤（ソーサー型）, 1 = 丸頭のエイリアン
        // float にしているのは Properties の Range/Int だと SetFloat から
        // そのまま渡せる素朴な数値であることを明示するため（enumはシェーダー側に持たない）
        _Kind ("Shape Kind (0=Saucer, 1=Alien)", Float) = 0

        // 縁の running light が明滅する速さ（Hz）。_Time.y に掛けて sin を回す
        _GlowPulseHz ("Glow Pulse Speed (Hz)", Float) = 1.5
    }
    SubShader
    {
        Tags {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
        }

        // 花火のパーティクルと同じ加算合成。奥のUFOや星を隠さず光を足し合わせる
        Blend SrcAlpha One
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

            // SRP Batcher 対応: マテリアル単位の uniform は必ず UnityPerMaterial に入れる。
            // _Time はUnity組み込みのグローバルuniformなので、ここには入れない
            // （Core.hlsl が既に宣言済みで、マテリアルごとの値ではないため）
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float  _Kind;
                float  _GlowPulseHz;
            CBUFFER_END

            #define TAU 6.28318530718

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;       // PS の startColor はここに入る
                float2 uv         : TEXCOORD0;   // Billboard クアッドの 0..1 UV
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color       = IN.color;   // startColor をそのまま渡す
                OUT.uv          = IN.uv;
                return OUT;
            }

            // 中心 center、半径 radius(x,y) の楕円の「内側度」を返す。
            // 1 = 中心, 0 = 輪郭上, 負 = 外側（呼び出し側で saturate して使う）
            float EllipseInside(float2 uv, float2 center, float2 radius)
            {
                float2 d = (uv - center) / max(radius, 1e-4);
                return 1.0 - dot(d, d);
            }

            // 空飛ぶ円盤（ソーサー型）のシルエット + 縁を回る running light
            half4 SaucerColor(float2 uv, half4 vcol)
            {
                // 円盤本体: 縦に潰した幅広の楕円。クアッド中央よりやや下寄りに置く
                float body = EllipseInside(uv, float2(0.5, 0.42), float2(0.46, 0.16));

                // ドーム: 本体よりひと回り小さく、中央よりやや上に重ねる
                float dome = EllipseInside(uv, float2(0.5, 0.54), float2(0.22, 0.16));

                float shape = saturate(max(body, dome) * 6.0);

                // ── running light ──
                // 円盤の縁（本体楕円の輪郭付近）に3つ、位相をずらして置く。
                // 個々の光点も楕円距離判定で作り、明滅は _Time.y（秒）× Hz の sin で駆動する
                float glow = 0.0;
                {
                    float2 lightUV[3] = {
                        float2(0.5 - 0.40, 0.42),
                        float2(0.5 + 0.40, 0.42),
                        float2(0.5,        0.26),
                    };
                    [unroll]
                    for (int i = 0; i < 3; i++)
                    {
                        float dot_ = saturate(EllipseInside(uv, lightUV[i], float2(0.045, 0.045)) * 10.0);
                        // 位相を光ごとにずらして、一斉点滅ではなく順番に光る印象にする
                        float phase = i * (TAU / 3.0);
                        float pulse = 0.5 + 0.5 * sin(_Time.y * _GlowPulseHz * TAU + phase);
                        glow += dot_ * pulse;
                    }
                }

                half3 rgb = vcol.rgb * _BaseColor.rgb + glow.xxx * half3(1.0, 0.95, 0.8);
                half  a   = vcol.a * _BaseColor.a * shape + glow;
                return half4(rgb, a);
            }

            // 丸頭のエイリアンのシルエット（頭 + 大きな目2つ）。
            // running light は持たず、ぼんやりした明滅のみを申し訳程度に加える（簡潔さ優先）
            half4 AlienColor(float2 uv, half4 vcol)
            {
                // 頭: 上半分いっぱいに使う、縦にやや長い楕円
                float head = EllipseInside(uv, float2(0.5, 0.55), float2(0.32, 0.38));
                float headShape = saturate(head * 6.0);

                // 目: 頭の中央よりやや上、左右に離して配置。目は黒く抜くので頭より手前に評価する
                float eyeL = saturate(EllipseInside(uv, float2(0.5 - 0.15, 0.58), float2(0.09, 0.13)) * 8.0);
                float eyeR = saturate(EllipseInside(uv, float2(0.5 + 0.15, 0.58), float2(0.09, 0.13)) * 8.0);
                float eyes = max(eyeL, eyeR);

                // ほのかな明滅。running light ほど主張しない弱い係数にしてある
                float shimmer = 0.85 + 0.15 * sin(_Time.y * _GlowPulseHz * TAU);

                half3 skinColor = vcol.rgb * _BaseColor.rgb * shimmer;
                half3 eyeColor  = half3(0.02, 0.02, 0.03); // 目はほぼ黒。頭の地色を打ち消して抜く
                half3 rgb = lerp(skinColor, eyeColor, eyes);

                half a = vcol.a * _BaseColor.a * headShape;
                return half4(rgb, a);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 vcol = half4(IN.color);
                half4 c = (_Kind > 0.5) ? AlienColor(IN.uv, vcol) : SaucerColor(IN.uv, vcol);
                return c;
            }
            ENDHLSL
        }
    }
}
