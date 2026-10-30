// Skybox for the time-of-day system. Everything it draws comes from TimeOfDaySky.hlsl and the
// global state AtmosphereRenderer sets each frame; the material itself has no properties.
Shader "CirrusFS/TimeOfDay/Sky"
{
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "RenderPipeline" = "UniversalPipeline" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "TimeOfDaySky.hlsl"

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.direction = input.positionOS.xyz; // the skybox mesh is centred on the camera
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 v = normalize(input.direction);
                float pixelAngle = length(fwidth(v));
                float3 radiance = TodDitherForR11G11B10(TodSkyRadiance(v, pixelAngle) * TodPreExposure(), input.positionCS.xy);
                return half4(radiance, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
