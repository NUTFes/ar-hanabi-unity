// ===== CockpitFrame =====
// 宇宙モード用「コックピット窓」の縁飾り。CockpitFrameOverlay.cs が
// メインカメラの子に張る 1 枚の Quad へ適用する専用シェーダー。
//
// ── なぜテクスチャ資産ではなく手続き（UV数式）で描くのか ──
//   ParticleAdditive と同じ考え方で、画像を1枚も増やさずに縁取り・リベット・
//   コーナー補強を UV から作る。解像度非依存で、境界の太さ等をパラメータで
//   その場で調整できる（アセット差し替えの往復が要らない）。
//
// ── なぜ加算合成ではなく通常のアルファブレンドなのか ──
//   ParticleAdditive は「暗い空に光を足す」効果なので加算が正しいが、
//   このフレームは逆に「中心が完全に透けて奥の花火・背景・骨格が見える」
//   ことが要件（コックピットの窓越しに外を見ている絵にしたい）。
//   加算合成では黒（アルファ0）を表現できず中心が必ず何かしら光ってしまうため、
//   Blend SrcAlpha OneMinusSrcAlpha の通常アルファブレンドを使い、
//   中心のアルファを実質 0 にすることで「素通し」を実現する。
//
// ── Queue を Transparent+100 にした理由 ──
//   花火のパーティクル（ParticleAdditive/ParticleUnlit）は Queue=Transparent、
//   ZWrite Off で描かれる。フレームは常にそれらの手前（カメラに最も近い一定距離）に
//   置かれる前提だが、ZWrite Off 同士は描画順がそのまま重なり順になるため、
//   単純に描画キューを1つ後ろにずらすだけで「花火より必ず後に描かれる
//   ＝必ず手前に乗る」ことを ZTest のトリックなしに保証できる。
//
// ── _UseTexture / _FrameTex について（将来の差し替え用フック）──
//   企画側で本物のコックピット枠画像（PNG）が用意できた場合に備え、
//   テクスチャを割り当てるだけで手続き描画から画像描画へ切り替えられるように
//   しておく。CockpitFrameOverlay 側で _FrameTex に非nullのテクスチャを
//   セットすると同時に _UseTexture を 1 にする想定（コード変更不要）。
Shader "Custom/CockpitFrame"
{
    Properties
    {
        // 将来、本物のコックピット枠画像に差し替える場合の受け皿。
        // 既定は "white"（使わない限りテクスチャ計算のコストは無視できる）
        _FrameTex ("Frame Texture (fallback override)", 2D) = "white" {}

        // 0 = 手続き描画（既定）、1 = _FrameTex のアルファをそのまま使う
        _UseTexture ("Use Texture Instead Of Procedural", Float) = 0

        // 縁・コーナー補強・リベットの金属色
        _FrameColor ("Frame Color", Color) = (0.15, 0.18, 0.22, 1)

        // 縁の太さ（UV単位）。大きいほど窓が狭くなる
        _BorderThickness ("Border Thickness", Range(0.02, 0.3)) = 0.09

        // リベット（縁の内側に並ぶ小さな丸鋲）の間隔（UV単位）
        _RivetSpacing ("Rivet Spacing", Float) = 0.06

        // 縁の内側エッジに沿う細い発光ライン、およびリベットのハイライトに使う色
        _GlowColor ("Glow Color", Color) = (0.3, 0.7, 1, 1)

        // 中心部にもうっすら掛かる周辺減光の強さ。0でオフ
        _VignetteStrength ("Vignette Strength", Range(0, 1)) = 0.25

        // 走査線風の薄い横縞の強さ。0でオフ
        _ScanlineStrength ("Scanline Strength", Range(0, 1)) = 0.06
    }
    SubShader
    {
        Tags {
            "RenderType"      = "Transparent"
            "Queue"           = "Transparent+100"
            "RenderPipeline"  = "UniversalPipeline"
        }

        // 通常のアルファブレンド。中心（アルファ0）は完全に素通しになる
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

            // テクスチャ／サンプラーは SRP Batcher の対象外なので CBUFFER の外に置く
            TEXTURE2D(_FrameTex);
            SAMPLER(sampler_FrameTex);

            // SRP Batcher 対応: マテリアル単位の uniform は必ず UnityPerMaterial に入れる。
            // ここに裸の uniform を1つでも外へ出すと、他のマテリアルとバッチが割れてしまう
            // （BackgroundRemoval.shader が過去にこれをやってしまった反面教師）
            CBUFFER_START(UnityPerMaterial)
                float4 _FrameTex_ST;
                float  _UseTexture;
                float4 _FrameColor;
                float  _BorderThickness;
                float  _RivetSpacing;
                float4 _GlowColor;
                float  _VignetteStrength;
                float  _ScanlineStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;   // 組み込み Quad は 0..1 の UV を持つ
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

            // コーナーの斜め補強材。corner から画面中心へ向かう対角線に沿う
            // 帯状のマスクを作る。実機の窓枠でよく見る「四隅の補強ステー」の表現
            float CornerStrutMask(float2 uv, float2 corner, float halfWidth, float reach)
            {
                float2 dir  = normalize(float2(0.5, 0.5) - corner); // 角→中心方向
                float2 perp = float2(-dir.y, dir.x);
                float2 local = uv - corner;

                float along = dot(local, dir);     // 対角線に沿った角からの距離
                float perpD = abs(dot(local, perp)); // 対角線からの垂直距離（帯の太さ判定）

                // それぞれ smoothstep でアンチエイリアスしつつ、
                // 「角のすぐ内側から reach まで」の範囲だけ帯を出す
                float band   = 1.0 - smoothstep(halfWidth * 0.7, halfWidth, perpD);
                float reachF = 1.0 - smoothstep(reach * 0.85, reach, along);
                float startF = smoothstep(-0.015, 0.015, along);
                return band * reachF * startF;
            }

            // 縁に沿って並ぶ丸いリベット。frac() で「一定間隔ごとの最寄りの格子中心」
            // からの距離を測るだけで、ループなしに等間隔配置ができる
            float RivetsAlongEdge(float alongCoord, float acrossCoord, float acrossLinePos,
                                   float spacing, float radius)
            {
                float cell       = frac(alongCoord / spacing + 0.5) - 0.5; // 格子内でのオフセット(-0.5..0.5)
                float alongLocal = cell * spacing;                        // UV単位に戻す
                float acrossLocal = acrossCoord - acrossLinePos;
                float d = length(float2(alongLocal, acrossLocal));
                return 1.0 - smoothstep(radius * 0.65, radius, d);
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float2 uv = IN.uv;

                // ── 差し替え用フック ──
                // 本物の枠画像が _FrameTex に割り当てられていれば、そのアルファを
                // そのまま使う（手続き計算は一切行わない）。静的分岐だが、
                // このシェーダーは画面全体で1枚しか描かないのでコストは問題にならない
                if (_UseTexture > 0.5)
                {
                    half4 texCol = SAMPLE_TEXTURE2D(_FrameTex, sampler_FrameTex, uv);
                    return texCol;
                }

                // ── 縁（枠）──
                // 4辺それぞれへの距離のうち最小のものが「最も近い辺までの距離」。
                // これが _BorderThickness より小さい領域が枠になる
                float edgeDist = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                float aa = 0.0025; // アンチエイリアス幅
                float borderMask = 1.0 - smoothstep(_BorderThickness - aa, _BorderThickness + aa, edgeDist);

                // ── 四隅の斜め補強材 ──
                float strutHalf  = _BorderThickness * 0.32;
                float strutReach = _BorderThickness * 2.4; // 枠の外から窓側へ少し食い込む長さ
                float strut = 0.0;
                strut = max(strut, CornerStrutMask(uv, float2(0, 0), strutHalf, strutReach));
                strut = max(strut, CornerStrutMask(uv, float2(1, 0), strutHalf, strutReach));
                strut = max(strut, CornerStrutMask(uv, float2(0, 1), strutHalf, strutReach));
                strut = max(strut, CornerStrutMask(uv, float2(1, 1), strutHalf, strutReach));

                // ── リベット ──
                // 縁の内側寄り（枠の厚みの内側65%あたり）に一列だけ並べる
                float rivetRadius = max(0.004, _BorderThickness * 0.12);
                float rivetLine   = _BorderThickness * 0.65;
                float rivets = 0.0;
                rivets = max(rivets, RivetsAlongEdge(uv.x, uv.y, rivetLine,             _RivetSpacing, rivetRadius)); // 下辺
                rivets = max(rivets, RivetsAlongEdge(uv.x, uv.y, 1.0 - rivetLine,       _RivetSpacing, rivetRadius)); // 上辺
                rivets = max(rivets, RivetsAlongEdge(uv.y, uv.x, rivetLine,             _RivetSpacing, rivetRadius)); // 左辺
                rivets = max(rivets, RivetsAlongEdge(uv.y, uv.x, 1.0 - rivetLine,       _RivetSpacing, rivetRadius)); // 右辺

                // 枠・補強材・リベットをまとめた「構造物」マスク（ここは不透明な金属として塗る）
                float structural = saturate(borderMask + strut + rivets);

                // ── 縁の内側エッジに沿う細い発光ライン ──
                // edgeDist がちょうど _BorderThickness に一致する等高線上だけ光らせる。
                // ブレンドは通常アルファのままだが、_GlowColor 自体を明るい色にしておけば
                // 「加算っぽい」発光感は十分出せる
                float glowWidth = 0.006;
                float glow = 1.0 - smoothstep(0.0, glowWidth, abs(edgeDist - _BorderThickness));
                glow *= _GlowColor.a;

                // ── 周辺減光（ビネット）── 中心が0、四隅に向かうほど強くなる。
                // 透明な窓の中にもごく僅かにHUDらしい陰影を足すだけなので、極力弱くする
                float2 centered = uv - 0.5;
                float vignetteR = saturate(length(centered) * 1.4142136);
                float vignette = pow(vignetteR, 2.0) * _VignetteStrength;

                // ── 走査線 ── 横方向の薄い縞。UV基準なので解像度に依存しない
                float scanline = (sin(uv.y * 320.0) * 0.5 + 0.5) * _ScanlineStrength;

                // 構造物の上には周辺減光/走査線を重ねない
                // （不透明な金属の見た目を邪魔しないため、かつどうせ見えない）
                float ambientAlpha = (vignette + scanline) * (1.0 - structural);

                half3 color = _FrameColor.rgb;
                color = lerp(color, _GlowColor.rgb, glow); // 内側エッジだけ発光色に寄せる

                half alpha = structural * _FrameColor.a;
                alpha = saturate(alpha + glow + ambientAlpha);

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
