// ParticleColor.shader と同名の "Custom/ParticleColor" を宣言していたため、
// Shader.Find("Custom/ParticleColor") がどちらを返すか不定になっていた（色が壊れる原因）。
// 名前を分離したうえで、ParticleSystem の頂点カラー（Particle.startColor）を
// そのまま出力する実装にしている。ImageFireworkEffect が SetParticles() で
// 1粒ごとに違う色を入れるため、uniform 単色では絵の色が再現できない。
// _BaseColor は全体の色味を調整するための任意のティントで、既定値 (1,1,1,1) では無変化。
Shader "Custom/ParticleUnlit"
{
    Properties
    {
        // 頂点カラーに掛けるティント。既定値 (1,1,1,1) では無変化
        _BaseColor ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent"
            "RenderPipeline"  = "UniversalPipeline"
        }

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
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color      : COLOR;      // PS の startColor はここに入る
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.color       = IN.color;   // startColor をそのまま渡す
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                // 頂点カラー（1粒ごとの色）× ティント
                return half4(IN.color * _BaseColor);
            }
            ENDHLSL
        }
    }
}
