// Full-screen aerial perspective for opaque geometry, drawn by AerialPerspectiveFeature after
// the skybox and before transparents. Reads only the depth texture: the result is applied by
// blending (Blend One SrcAlpha: dst * T + inscatter), so the colour buffer is never copied.
// The sky itself is skipped; the sky shader already includes the whole path through the air.
Shader "Hidden/CirrusFS/TimeOfDay/AerialPerspective"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        Blend One SrcAlpha

        Pass
        {
            Name "AerialPerspective"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "TimeOfDayAerialPerspective.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(uint id : SV_VertexID)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(id);
                o.uv = GetFullScreenTriangleTexCoord(id);
                return o;
            }

            // See TodDitherForR11G11B10 in TimeOfDaySky.hlsl: half a storage step of noise so
            // smooth fog gradients do not band in the 32-bit HDR target.
            float3 Dither(float3 color, float2 pixel)
            {
                float n = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) - 0.5;
                return color * (1.0 + n * float3(1.0 / 64.0, 1.0 / 64.0, 1.0 / 32.0));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float rawDepth = SampleSceneDepth(input.uv);
            #if UNITY_REVERSED_Z
                if (rawDepth <= 0.0) discard;   // sky
            #else
                if (rawDepth >= 1.0) discard;
            #endif
                float3 positionWS = ComputeWorldSpacePosition(input.uv, rawDepth, UNITY_MATRIX_I_VP);
                float3 toSurface = positionWS - _WorldSpaceCameraPos;
                float d = length(toSurface);
                if (d < 20.0) discard;          // cockpit and anything within 20 m: no haze to speak of

                float4 ap = TodAerialPerspective(toSurface / d, d * 0.001);
                return float4(Dither(ap.rgb * TodPreExposure(), input.positionCS.xy), ap.a);
            }
            ENDHLSL
        }
    }
}
