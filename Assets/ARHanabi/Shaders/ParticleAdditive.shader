// ===== ParticleAdditive =====
// 画像花火（ImageFireworkEffect）専用の描画シェーダー。
//
// ── なぜ ParticleUnlit と別に用意したのか ──
//   ParticleUnlit は頂点カラーをそのまま出すだけで UV を読まない。
//   ParticleSystem の Billboard は正方形のクアッドなので、UV を使わないと
//   1粒が「塗りつぶしの正方形」として描かれる。粒を大きくすると
//   モザイク画のタイルのように見えてしまい、まったく花火に見えなかった。
//
//   ここでは UV から手続き的に丸いフォールオフを作る。テクスチャ資産を
//   1枚も増やさずに、正方形が中心の明るい光の玉になる。
//
//   さらにブレンドを加算合成（Blend SrcAlpha One）にしてある。
//   花火は「暗い夜空に光が足し合わされる」現象なので、通常のアルファブレンド
//   （手前の粒が奥の粒を隠す）ではなく加算のほうが density の高いところが
//   自然に白く飽和して火球らしくなる。
//
//   ParticleUnlit のほうは SkeletonMaterial（スケルトンの線）が使っており、
//   線が加算で光ってしまうと困るので、あちらは変更していない。
Shader "Custom/ParticleAdditive"
{
    Properties
    {
        // 頂点カラーに掛けるティント。既定値 (1,1,1,1) では無変化
        _BaseColor ("Tint", Color) = (1,1,1,1)

        // 中心の明るさの寄り具合。大きいほど縁が急に暗くなり、粒が小さく締まって見える
        _Falloff ("Falloff Power", Range(0.5, 8)) = 2
    }
    SubShader
    {
        Tags {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
        }

        // 加算合成。奥の粒を隠さず光を足し合わせる
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

            // SRP Batcher 対応: マテリアル単位の uniform は必ず UnityPerMaterial に入れる
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float  _Falloff;
            CBUFFER_END

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

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                // クアッド中心からの距離で丸く抜く。
                // 中心 (0.5, 0.5) からの距離を 0..1 に直し、外周で 0 になるようにする
                half dist = length(IN.uv - 0.5) * 2.0;
                half fall = saturate(1.0 - dist);
                fall = pow(fall, _Falloff);   // 縁の落ち方を調整して光の玉らしくする

                // 頂点カラー（1粒ごとの色）× ティント。
                // 加算合成なので、アルファにフォールオフを掛ければ縁が自然に消える
                half4 c = half4(IN.color * _BaseColor);
                c.a *= fall;
                return c;
            }
            ENDHLSL
        }
    }
}
