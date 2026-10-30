// Atmosphere LUT generation, fragment fallback for GPUs or APIs without compute shaders.
// Same integrators as AtmosphereLuts.compute; each pass draws one full-screen triangle into
// the LUT. The texel's uv comes from SV_Position, which addresses texels the same way
// sampling does on every API, so no flip is needed.
Shader "Hidden/CirrusFS/TimeOfDay/AtmosphereLuts"
{
    SubShader
    {
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
        #pragma target 3.5
        #include "AtmosphereIntegrate.hlsl"

        float4 _TOD_OutputSize;

        float4 Vert(uint id : SV_VertexID) : SV_Position
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            return float4(uv * 2.0 - 1.0, 0.0, 1.0);
        }

        float2 TexelUv(float4 positionCS) { return positionCS.xy / _TOD_OutputSize.xy; }
        ENDHLSL

        Pass
        {
            Name "Transmittance"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(float4 p : SV_Position) : SV_Target { return TodTransmittanceTexel(TexelUv(p)); }
            ENDHLSL
        }

        Pass
        {
            Name "MultiScattering"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(float4 p : SV_Position) : SV_Target { return TodMultiScatTexel(TexelUv(p)); }
            ENDHLSL
        }

        Pass
        {
            Name "SkyView"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(float4 p : SV_Position) : SV_Target { return TodSkyViewTexel(TexelUv(p)); }
            ENDHLSL
        }

        // One draw per slice of the 3D LUT; _TOD_OutputSize.z is the slice being drawn.
        Pass
        {
            Name "AerialPerspective"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Frag(float4 p : SV_Position) : SV_Target
            {
                return TodAerialTexel(p.xy / _TOD_AerialSize.xy, (int)_TOD_OutputSize.z);
            }
            ENDHLSL
        }
    }
}
