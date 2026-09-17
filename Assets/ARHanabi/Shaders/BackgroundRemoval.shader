Shader "Custom/BackgroundRemoval"
{
    Properties
    {
        _MainTex      ("Camera Texture",  2D)           = "white" {}
        _MaskTex      ("Mask Texture",    2D)           = "black" {}
        _Threshold    ("Threshold",       Range(0,1))   = 0.5
        _EdgeSoftness ("Edge Softness",   Range(0,0.5)) = 0.05
        _SegEnabled   ("Seg Enabled",     Float)        = 0

        // 左右反転（鏡像）。1 で反転。BackgroundRemovalEffect.SetMirrorX が入れる
        _FlipX        ("Flip Horizontally", Float)      = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "BackgroundRemoval"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_MaskTex); SAMPLER(sampler_MaskTex);

            uniform float _Threshold;
            uniform float _EdgeSoftness;
            uniform float _SegEnabled;
            uniform float _FlipX;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv          = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // ── 左右反転（鏡像）──
                //   来場者が自分を見る展示なので、鏡と同じ向きにしたいことがある。
                //   ⚠️ ここで反転したら、関節座標も同じだけ反転させないと
                //      骨格と花火の位置が本人からズレる。座標側の反転は
                //      PoseCoordinateUtil.MirrorX が持ち、両方を
                //      CameraBackgroundController.MirrorHorizontal がまとめて切り替える。
                //
                //   Quad の localScale.x を負にする手もあるが、面の裏表が入れ替わって
                //   背面カリングで消える上、CameraCircleMatte が scale を書き戻すので
                //   符号が失われる。UV でやるのが安全
                float2 uv = IN.uv;
                if (_FlipX > 0.5) uv.x = 1.0 - uv.x;

                half4 camColor = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv);

                if (_SegEnabled < 0.5)
                    return half4(camColor.rgb, 1.0);

                // マスクをサンプリング（上下反転）。
                // マスクはカメラ映像と同じ絵なので、左右反転も同じだけ掛ける
                float2 maskUV  = float2(uv.x, 1.0 - uv.y);
                float  maskVal = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, maskUV).r;

                float lo    = max(0.0, _Threshold - _EdgeSoftness);
                float hi    = min(1.0, _Threshold + _EdgeSoftness);
                float alpha = smoothstep(lo, hi, maskVal);

                // 人物 → カメラ映像、背景 → 黒
                return half4(camColor.rgb * alpha, 1.0);
            }
            ENDHLSL
        }
    }
}
