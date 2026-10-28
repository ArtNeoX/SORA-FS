// Open-ocean surface for the WGS84 globe (URP).
//
// Waves: two tiling normal maps at different scales, scrolling in different directions,
// projected triplanar in ECEF axes so the pattern is anchored to the Earth rather than to the
// floating origin (it does not slide or pop on a re-centre). The ECEF offsets are wrapped per
// map on the CPU in double, so the UVs stay small numbers. Wave strength fades out with
// distance, so from altitude there is no visible tiling and from space the sea is smooth.
//
// Shading: Schlick Fresnel between a deep-water body colour (looking down) and the sky
// reflection (grazing), a tight sun glint plus a broad sheen, and haze toward the horizon
// whose path length shrinks once the camera is above the atmosphere.
Shader "CirrusFS/Ocean"
{
    Properties
    {
        [Header(Colour)]
        _DeepColor ("Deep water", Color) = (0.015, 0.090, 0.260, 1)
        _ScatterColor ("Sunlit scatter", Color) = (0.030, 0.200, 0.330, 1)
        _SkyFallback ("Sky (if no reflection probe)", Color) = (0.45, 0.62, 0.82, 1)
        _ReflectionStrength ("Sky reflection strength", Range(0, 1.5)) = 0.8

        [Header(Waves)]
        [NoScaleOffset][Normal] _WaveNormalA ("Wave normals A (small)", 2D) = "bump" {}
        _ScaleA ("A tile size (m)", Float) = 46
        _StrengthA ("A strength", Range(0, 2)) = 0.9
        _FadeA ("A fade distance (m)", Float) = 6000
        [NoScaleOffset][Normal] _WaveNormalB ("Wave normals B (swell)", 2D) = "bump" {}
        _ScaleB ("B tile size (m)", Float) = 211
        _StrengthB ("B strength", Range(0, 2)) = 0.7
        _FadeB ("B fade distance (m)", Float) = 30000

        [Header(Sun)]
        _GlintPower ("Glint sharpness", Float) = 900
        _GlintIntensity ("Glint intensity", Float) = 18
        _SheenPower ("Sheen sharpness", Float) = 60
        _SheenIntensity ("Sheen intensity", Float) = 0.15

        [Header(Haze)]
        _HazeColor ("Haze", Color) = (0.62, 0.72, 0.84, 1)
        _HazeDistance ("Haze e-folding distance (m)", Float) = 140000
        _AtmosphereHeight ("Atmosphere scale height (m)", Float) = 9000
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            // Skirts hang below chunk borders; draw both sides so whichever face a gap shows is
            // filled.
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_WaveNormalA); SAMPLER(sampler_WaveNormalA);
            TEXTURE2D(_WaveNormalB); SAMPLER(sampler_WaveNormalB);

            CBUFFER_START(UnityPerMaterial)
                half4 _DeepColor;
                half4 _ScatterColor;
                half4 _SkyFallback;
                half _ReflectionStrength;
                float _ScaleA, _StrengthA, _FadeA;
                float _ScaleB, _StrengthB, _FadeB;
                float _GlintPower, _GlintIntensity, _SheenPower, _SheenIntensity;
                half4 _HazeColor;
                float _HazeDistance, _AtmosphereHeight;
            CBUFFER_END

            // Set every frame by OceanSphere / OceanFrame.
            float4x4 _OceanUnityToEcef;   // rotation only: Unity world direction -> ECEF direction
            float3 _OceanUvOriginA;       // frac(originEcef / ScaleA) + scroll A, in tiles
            float3 _OceanUvOriginB;
            float _OceanCameraHeight;     // camera height above the ellipsoid, m

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }

            half3 SampleTangentNormal(TEXTURE2D_PARAM(tex, samp), float2 uv)
            {
                return UnpackNormal(SAMPLE_TEXTURE2D(tex, samp, uv));
            }

            // Triplanar (UDN blend) in ECEF axes. pTiles is the ECEF-relative position in tile
            // units, nEcef the geometric normal in ECEF.
            float3 TriplanarPerturbation(TEXTURE2D_PARAM(tex, samp), float3 pTiles, float3 nEcef, float3 w)
            {
                half3 tx = SampleTangentNormal(TEXTURE2D_ARGS(tex, samp), pTiles.yz);
                half3 ty = SampleTangentNormal(TEXTURE2D_ARGS(tex, samp), pTiles.zx);
                half3 tz = SampleTangentNormal(TEXTURE2D_ARGS(tex, samp), pTiles.xy);

                return w.x * float3(0, tx.x, tx.y) +
                       w.y * float3(ty.y, 0, ty.x) +
                       w.z * float3(tz.x, tz.y, 0);
            }

            half4 Frag(Varyings i, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                float3 geomN = normalize(i.normalWS);
                float3 toCamera = _WorldSpaceCameraPos - i.positionWS;
                float dist = length(toCamera);
                float3 V = toCamera / max(dist, 1e-4);

                // ---- waves ------------------------------------------------------------
                float3x3 toEcef = (float3x3)_OceanUnityToEcef;
                float3 pEcef = mul(toEcef, i.positionWS);
                float3 nEcef = mul(toEcef, geomN);

                float3 w = pow(abs(nEcef), 8.0);
                w /= (w.x + w.y + w.z);

                float fadeA = _StrengthA * exp(-dist / _FadeA);
                float fadeB = _StrengthB * exp(-dist / _FadeB);

                float3 perturb = 0;
                if (fadeA > 0.002)
                    perturb += fadeA * TriplanarPerturbation(TEXTURE2D_ARGS(_WaveNormalA, sampler_WaveNormalA),
                                                             pEcef / _ScaleA + _OceanUvOriginA, nEcef, w);
                if (fadeB > 0.002)
                    perturb += fadeB * TriplanarPerturbation(TEXTURE2D_ARGS(_WaveNormalB, sampler_WaveNormalB),
                                                             pEcef / _ScaleB + _OceanUvOriginB, nEcef, w);

                float3 nWaveEcef = normalize(nEcef + perturb);
                float3 N = normalize(mul(transpose(toEcef), nWaveEcef));

                // Seen from below (underwater, or through a skirt), shade as flat water.
                if (!isFrontFace && dot(geomN, V) < 0) N = geomN;

                // ---- light ------------------------------------------------------------
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light sun = GetMainLight(shadowCoord);
                float3 L = sun.direction;
                half3 sunColor = sun.color;
                half shadow = sun.shadowAttenuation;

                // How much daylight reaches this point at all: fades through the terminator.
                half day = saturate(dot(geomN, L) * 5.0 + 0.15);

                half NdotV = saturate(dot(N, V));
                half fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);

                float3 R = reflect(-V, N);
                // A reflection can only see the sky: fold any ray that points into the sea.
                R = dot(R, geomN) < 0 ? R - 2.0 * dot(R, geomN) * geomN : R;

                // 0 inside the atmosphere, 1 from ~2000 km up: from orbit the sea reads as a
                // dark blue disc, so the bright sky reflection and broad sheen are toned down.
                half space = saturate(log10(max(_OceanCameraHeight, 1.0) / 20000.0) / 2.0);

                half3 sky = GlossyEnvironmentReflection(R, 0.08h, 1.0h);
                sky = (dot(sky, 1) > 0.001 ? sky : _SkyFallback.rgb) * _ReflectionStrength * day * (1.0 - 0.85 * space);

                // Wave slopes facing the sun scatter more light out of the water and slopes
                // facing away less, which is what makes the pattern visible looking straight
                // down, where Fresnel leaves almost no reflection.
                half waveLight = saturate(0.55 + 2.2 * (dot(N, L) - dot(geomN, L)) + 0.45 * saturate(dot(geomN, L)));
                half3 body = _DeepColor.rgb * (0.25 + 0.75 * day) * (0.8 + 0.4 * waveLight) +
                             _ScatterColor.rgb * waveLight * shadow * sunColor * (1.0 - 0.4 * space);

                half3 color = lerp(body, sky, fresnel);

                float3 H = normalize(L + V);
                half NdotH = saturate(dot(N, H));
                half glint = pow(NdotH, _GlintPower) * _GlintIntensity +
                             pow(NdotH, _SheenPower) * _SheenIntensity * (1.0 - 0.8 * space);
                color += glint * fresnel * 4.0 * sunColor * shadow * saturate(dot(geomN, L) * 10.0);

                // ---- haze -------------------------------------------------------------
                // Path through air: the whole distance down low, only about an atmosphere's
                // worth once the camera is above it.
                float airFraction = min(1.0, _AtmosphereHeight / max(_OceanCameraHeight, 1.0));
                float path = dist * airFraction;
                half haze = 1.0 - exp(-path / _HazeDistance);
                color = lerp(color, _HazeColor.rgb * sunColor * day, haze);

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float4 DepthVert(float4 positionOS : POSITION) : SV_POSITION
            {
                return TransformObjectToHClip(positionOS.xyz);
            }

            half DepthFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex DNVert
            #pragma fragment DNFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DNVaryings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            DNVaryings DNVert(float4 positionOS : POSITION, float3 normalOS : NORMAL)
            {
                DNVaryings o;
                o.positionCS = TransformObjectToHClip(positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(normalOS);
                return o;
            }

            half4 DNFrag(DNVaryings i) : SV_Target { return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }

    FallBack Off
}
