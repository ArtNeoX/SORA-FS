// Terrain chunks on the WGS84 globe (URP), fed by GlobeTerrain.
//
// Land class: every chunk carries a small R8 texture of class ids (one texel per class cell
// at the finest level, plus a one-texel apron from the neighbours). The fragment blends the
// colours of the four nearest cells by their bilinear weights, sharpened a little, with the
// lookup position warped by detail noise, so borders between land types are soft and
// irregular rather than a 30 m staircase. Colours come from the manifest's class list
// (global _TerrainPalette, linear RGB; alpha = water).
//
// Ground textures: a class may also have a tiling albedo (GlobeTerrain.classTextures), one
// layer of the global _TerrainClassAlbedo array. Each of the four cells is textured before
// the blend, so textures fade into each other exactly as the colours do. A texture either
// keeps the class colour and adds only its pattern (texture / its own average), or replaces
// it. A second, larger, transposed lookup mixed in by noise breaks up the repetition.
//
// Detail: tileable noise at 4096, 256 and 32 m, on coordinates anchored to the Earth (a
// Mercator grid, wrapped per chunk on the CPU in double), so it never slides with the
// floating origin. Ground textures use the same coordinates with a whole number of repeats
// per 4096 m, so they are seamless too. Steep slopes turn to rock (optionally textured).
//
// Lighting: main light with shadows plus the SH ambient, both pre-exposed by the time-of-day
// system. Distance haze is the time-of-day fog pass; the simple fallback haze here only runs
// without it (_TOD_FogActive = 0), as on the ocean.
Shader "CirrusFS/Terrain"
{
    Properties
    {
        [Header(Colour)]
        _Brightness ("Albedo scale", Range(0, 2)) = 1
        _Saturation ("Saturation", Range(0, 1.5)) = 1.1
        _WaterColor ("Inland water", Color) = (0.02, 0.06, 0.10, 1)
        _WaterGlint ("Water sun glint", Float) = 8
        _ShoreColor ("Shore (water cells on low ground)", Color) = (0.45, 0.40, 0.30, 1)
        _ShoreHeight ("Shore below height (m)", Float) = 3

        [Header(Class blending)]
        _BlendSharpness ("Blend sharpness (1 = linear)", Range(0.1, 4)) = 1.2
        _BlendWarp ("Border warp (cells)", Range(0, 10)) = 0.9

        [Header(Ground textures)]
        _TextureAntiTiling ("Anti-tiling mix", Range(0, 1)) = 0.5

        [Header(Detail)]
        _DetailStrength ("Detail strength", Range(0, 1)) = 0.35
        _DetailFade ("Fine detail fade distance (m)", Float) = 5000

        [Header(Rock)]
        _RockColor ("Rock on steep slopes", Color) = (0.30, 0.28, 0.26, 1)
        _RockSlopeStart ("Rock starts at slope (deg)", Range(0, 90)) = 30
        _RockSlopeEnd ("All rock at slope (deg)", Range(0, 90)) = 45

        [Header(Fallback haze)]
        _HazeColor ("Haze", Color) = (0.62, 0.72, 0.84, 1)
        _HazeDistance ("Haze e-folding distance (m)", Float) = 140000
        _AtmosphereHeight ("Atmosphere scale height (m)", Float) = 9000
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half _Brightness, _Saturation;
            half4 _WaterColor;
            float _WaterGlint;
            half4 _ShoreColor;
            float _ShoreHeight;
            float _BlendSharpness, _BlendWarp;
            float _TextureAntiTiling;
            float _DetailStrength, _DetailFade;
            half4 _RockColor;
            float _RockSlopeStart, _RockSlopeEnd;
            half4 _HazeColor;
            float _HazeDistance, _AtmosphereHeight;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            // Skirts hang below the chunk borders; draw both sides so a gap is always filled.
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_ClassTex);                                     // per chunk
            TEXTURE2D(_TerrainPalette);                               // 256 x 1, global
            TEXTURE2D(_TerrainDetail); SAMPLER(sampler_TerrainDetail); // global
            float _TerrainClassTexSize;                               // texels + 2, global
            TEXTURE2D_ARRAY(_TerrainClassAlbedo); SAMPLER(sampler_TerrainClassAlbedo); // global
            // per class id: x layer + 1 (0 = flat colour), y repeats per 4096 m, z strength,
            // w 1 = keep the class colour; tint linear RGB. Zero (unset) means flat colour.
            float4 _TerrainClassParams[256];
            float4 _TerrainClassTint[256];
            float4 _TerrainRockParams, _TerrainRockTint;               // steep slopes, same layout
            float _OceanCameraHeight;                                 // from OceanSphere
            float _TOD_FogActive;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uvClass : TEXCOORD0;
                float3 uvDetail : TEXCOORD1;    // xy detail coordinates, z height (m)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 upWS : TEXCOORD2;
                float2 uvClass : TEXCOORD3;
                float3 uvDetail : TEXCOORD4;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                // chunk-local +Y is the ellipsoid normal at the chunk centre
                o.upWS = TransformObjectToWorldDir(float3(0, 1, 0));
                o.uvClass = input.uvClass;
                o.uvDetail = input.uvDetail;
                return o;
            }

            uint ClassId(int2 texel)
            {
                int size = (int)_TerrainClassTexSize;
                texel = clamp(texel, 0, size - 1);
                return (uint)round(LOAD_TEXTURE2D(_ClassTex, texel).r * 255.0);
            }

            // Shared by every textured lookup in one pixel: Earth-anchored coordinates (one unit
            // = 4096 Mercator metres), their screen derivatives, and the anti-tiling mix.
            struct GroundUv { float2 uv, dx, dy; half macro; };

            half3 GroundTexture(half3 base, float4 p, half3 tint, GroundUv g)
            {
                float layer = p.x - 1.0;
                float r1 = p.y;
                float r2 = max(1.0, round(r1 * 0.2));   // whole repeats too, so still seamless
                half3 a = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TerrainClassAlbedo, sampler_TerrainClassAlbedo,
                                                      g.uv * r1, layer, g.dx * r1, g.dy * r1).rgb;
                half3 b = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TerrainClassAlbedo, sampler_TerrainClassAlbedo,
                                                      g.uv.yx * r2, layer, g.dx.yx * r2, g.dy.yx * r2).rgb;
                half3 tex = lerp(a, b, g.macro);
                if (p.w > 0.5)
                {
                    // keep the class colour: the texture's pattern relative to its own average
                    half3 average = SAMPLE_TEXTURE2D_ARRAY_LOD(_TerrainClassAlbedo, sampler_TerrainClassAlbedo,
                                                               float2(0.5, 0.5), layer, 16).rgb;
                    tex = base * tex / max(average, 0.02);
                }
                return lerp(base, tex * tint, p.z);
            }

            half3 ClassAlbedo(uint id, half3 base, GroundUv g)
            {
                float4 p = _TerrainClassParams[id];
                half3 albedo = base;
                [branch] if (p.x > 0.5) albedo = GroundTexture(base, p, _TerrainClassTint[id].rgb, g);
                return albedo;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 N = normalize(i.normalWS);
                float3 up = normalize(i.upWS);
                float3 toCamera = _WorldSpaceCameraPos - i.positionWS;
                float dist = length(toCamera);
                float3 V = toCamera / max(dist, 1e-4);

                // ---- detail noise (Earth-anchored) ---------------------------------------
                half4 nLarge = SAMPLE_TEXTURE2D(_TerrainDetail, sampler_TerrainDetail, i.uvDetail.xy);
                half4 nMid = SAMPLE_TEXTURE2D(_TerrainDetail, sampler_TerrainDetail, i.uvDetail.xy * 16.0);
                half4 nFine = SAMPLE_TEXTURE2D(_TerrainDetail, sampler_TerrainDetail, i.uvDetail.xy * 128.0);
                half fineFade = saturate(1.0 - dist / _DetailFade);

                // ---- land class: four-cell blend with warped borders ---------------------
                float2 warp = ((nMid.xy - 0.5) + (nFine.zw - 0.5) * 0.5 * fineFade) * _BlendWarp;
                float2 t = i.uvClass * _TerrainClassTexSize - 0.5 + warp;
                float2 cell = floor(t);
                float2 f = saturate((t - cell - 0.5) * _BlendSharpness + 0.5);
                int2 c = int2(cell);
                uint id00 = ClassId(c);
                uint id10 = ClassId(c + int2(1, 0));
                uint id01 = ClassId(c + int2(0, 1));
                uint id11 = ClassId(c + int2(1, 1));
                half4 p00 = LOAD_TEXTURE2D(_TerrainPalette, int2(id00, 0));
                half4 p10 = LOAD_TEXTURE2D(_TerrainPalette, int2(id10, 0));
                half4 p01 = LOAD_TEXTURE2D(_TerrainPalette, int2(id01, 0));
                half4 p11 = LOAD_TEXTURE2D(_TerrainPalette, int2(id11, 0));

                GroundUv g;
                g.uv = i.uvDetail.xy;
                g.dx = ddx(g.uv);
                g.dy = ddy(g.uv);
                g.macro = smoothstep(0.35, 0.65, nLarge.g * 0.5 + nMid.r * 0.5) * _TextureAntiTiling;

                // texture each distinct class once (usually all four cells are the same class)
                half3 a00 = ClassAlbedo(id00, p00.rgb, g);
                half3 a10 = a00, a01 = a00, a11 = a00;
                [branch] if (id10 != id00) a10 = ClassAlbedo(id10, p10.rgb, g);
                [branch] if (id01 == id10) a01 = a10;
                else if (id01 != id00) a01 = ClassAlbedo(id01, p01.rgb, g);
                [branch] if (id11 == id10) a11 = a10;
                else if (id11 == id01) a11 = a01;
                else if (id11 != id00) a11 = ClassAlbedo(id11, p11.rgb, g);

                half3 albedo = lerp(lerp(a00, a10, f.x), lerp(a01, a11, f.x), f.y);
                half water = lerp(lerp(p00.a, p10.a, f.x), lerp(p01.a, p11.a, f.x), f.y);
                half luma = dot(albedo, half3(0.2126, 0.7152, 0.0722));
                albedo = lerp(luma.xxx, albedo, _Saturation) * _Brightness;

                half detail = (nLarge.r - 0.5) * 0.6 + (nMid.g - 0.5) * 0.5 + (nFine.b - 0.5) * 0.5 * fineFade;
                albedo *= 1.0 + detail * _DetailStrength * 2.0;

                // ---- rock on steep ground ------------------------------------------------
                half slope = degrees(acos(saturate(dot(N, up))));
                half rock = smoothstep(_RockSlopeStart, _RockSlopeEnd, slope + (nMid.b - 0.5) * 8.0) * (1.0 - water);
                half3 rockAlbedo = _RockColor.rgb;
                [branch] if (rock > 0.0 && _TerrainRockParams.x > 0.5)
                    rockAlbedo = GroundTexture(rockAlbedo, _TerrainRockParams, _TerrainRockTint.rgb, g);
                albedo = lerp(albedo, rockAlbedo * (0.8 + 0.4 * nMid.r), rock);

                // Water cells are both lakes and the sea. Sea ground lies under the ocean
                // surface, so the only sea "water" ever seen is the blend reaching onto the
                // beach: on low ground, show it as shore instead of a dark rim. Lakes sit on
                // their own (higher) ground and stay water.
                half shore = 1.0 - saturate((i.uvDetail.z - _ShoreHeight) / max(_ShoreHeight, 0.1));
                albedo = lerp(albedo, lerp(_WaterColor.rgb, _ShoreColor.rgb, shore), water);
                water *= 1.0 - shore;

                // ---- light ---------------------------------------------------------------
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light sun = GetMainLight(shadowCoord);
                float3 L = sun.direction;
                half NdotL = saturate(dot(N, L));
                half3 color = albedo * (SampleSH(N) + sun.color * sun.shadowAttenuation * NdotL);

                // inland water: a sun glint, Fresnel-weighted
                half fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(up, V)), 5.0);
                half glint = pow(saturate(dot(up, normalize(L + V))), 400.0) * _WaterGlint;
                color += water * glint * fresnel * 4.0 * sun.color * sun.shadowAttenuation;

                // ---- fallback haze (only without the time-of-day fog) --------------------
                float airFraction = min(1.0, _AtmosphereHeight / max(_OceanCameraHeight, 1.0));
                half haze = (1.0 - exp(-dist * airFraction / _HazeDistance)) * (1.0 - _TOD_FogActive);
                color = lerp(color, _HazeColor.rgb * sun.color * saturate(dot(up, L) * 5.0 + 0.15), haze);

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            float4 ShadowVert(float4 positionOS : POSITION, float3 normalOS : NORMAL) : SV_POSITION
            {
                float3 positionWS = TransformObjectToWorld(positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 ShadowFrag() : SV_Target { return 0; }
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
