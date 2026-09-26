Shader "Custom/ParticleColor"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags {
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // SRP Batcher 対応: マテリアル単位の uniform は必ず UnityPerMaterial に入れる。
            // これが CBUFFER の外にあると SRP Batcher の対象外になり、
            // PC_RPAsset / Mobile_RPAsset の m_UseSRPBatcher: 1 が効かなくなる。
            //
            // 補足（以前のコメントの誤りを訂正）:
            //   material.SetColor() は CBUFFER 内でも問題なく効く。
            //   マテリアルのプロパティは UnityPerMaterial 定数バッファへそのまま流し込まれるため。
            //   CBUFFER 化で効かなくなるのは MaterialPropertyBlock の方（SRP Batcher が
            //   マテリアル単位でバッファをキャッシュするため、Renderer ごとの上書きが無視される）。
            //   1粒ごとに色を変えたい場合は MaterialPropertyBlock ではなく
            //   ParticleUnlit.shader のように頂点カラーを使うこと。
            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return half4(_Color.r, _Color.g, _Color.b, _Color.a);
            }
            ENDHLSL
        }
    }
}
