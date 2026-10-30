// Exposure metering: each texel of a small target (32x18) holds the mean log2 luminance of its
// cell of the camera image, from a 4x4 grid of bilinear taps. The image is pre-exposed, so the
// current pre-exposure is divided out: the meter reads the scene's own luminance and exposure
// changes cannot feed back into it. ExposureController reads the target back and averages it.
Shader "Hidden/CirrusFS/TimeOfDay/ExposureMeter"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "Meter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _TOD_Exposure;

            float Frag(Varyings input) : SV_Target
            {
                // The output texel's cell in source uv: 4x4 taps spread across it.
                float2 cell = 1.0 / float2(32.0, 18.0);
                float2 origin = input.texcoord - 0.5 * cell;
                float preExposure = _TOD_Exposure.x > 0.0 ? _TOD_Exposure.x : 1.0;
                float sum = 0.0;
                [unroll] for (int y = 0; y < 4; y++)
                    [unroll] for (int x = 0; x < 4; x++)
                    {
                        float2 uv = origin + (float2(x, y) + 0.5) * 0.25 * cell;
                        float3 c = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
                        float lum = dot(c, float3(0.2126, 0.7152, 0.0722)) / preExposure;
                        sum += log2(max(lum, 1e-7));
                    }
                return sum / 16.0;
            }
            ENDHLSL
        }
    }
}
