// Autogen foliage (URP), drawn by GlobeAutogen with procedural instancing: one draw per
// patch, instances read from _AutogenInstances by SV_InstanceID.
//
// Instance k: [2k] = (x, y, z, yaw) in the patch's east/up/north frame, [2k + 1] = (scale,
// texture variant, thinning random, brightness). The model goes through _AutogenModel (its
// import transform, lowest point on y = 0), then yaw, scale and the patch matrix.
//
// Distance: between _AutogenRange.x (full density) and .y (range) the share of objects drawn
// falls to zero; each object compares its own random number with that share and shrinks to
// nothing over a short band, so thinning never pops. Shrunk objects collapse to a point and
// draw no pixels.
//
// Texture detail by distance: beyond _TexFullResDistance the texture's mip level is forced
// up by _TexLodPerDoubling for every doubling of distance (never finer than the GPU would
// pick anyway), so far trees read smaller mips: less texture bandwidth and less shimmer.
//
// Lighting: foliage is lit as a volume, around the local up axis (wrap lighting), plus sun
// shining through the leaves when it is behind them, darker toward the trunk. Haze comes
// from the time-of-day fog pass, as on the terrain.
Shader "CirrusFS/AutogenFoliage"
{
    Properties
    {
        [Header(Look)]
        _Color ("Tint", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha cutoff", Range(0.05, 0.95)) = 0.45
        _Wrap ("Light wrap (soft foliage)", Range(0, 1)) = 0.45
        _Translucency ("Sun through leaves", Range(0, 1)) = 0.35
        _BottomShade ("Darker toward the trunk", Range(0, 1)) = 0.35

        [Header(Texture detail by distance)]
        _TexFullResDistance ("Full-resolution texture up to (m)", Float) = 120
        _TexLodPerDoubling ("Mip levels dropped per distance doubling", Range(0, 2)) = 1

        [Header(Fallback haze)]
        _HazeColor ("Haze", Color) = (0.62, 0.72, 0.84, 1)
        _HazeDistance ("Haze e-folding distance (m)", Float) = 140000
        _AtmosphereHeight ("Atmosphere scale height (m)", Float) = 9000
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _Color;
            half _Cutoff, _Wrap, _Translucency, _BottomShade;
            float _TexFullResDistance, _TexLodPerDoubling;
            half4 _HazeColor;
            float _HazeDistance, _AtmosphereHeight;
        CBUFFER_END

        // per draw, from GlobeAutogen's MaterialPropertyBlock
        StructuredBuffer<float4> _AutogenInstances;
        float4x4 _AutogenPatchToWorld;
        float4x4 _AutogenModel;
        float4 _AutogenRange;   // x full-density range, y range, z 1 / (y - x)
        TEXTURE2D_ARRAY(_AutogenAlbedo); SAMPLER(sampler_AutogenAlbedo);

        struct Placed
        {
            float3 positionWS;
            float3 normalWS;
            float3 upWS;
            float variant;
            float brightness;
        };

        Placed PlaceInstance(uint id, float3 positionOS, float3 normalOS)
        {
            float4 a = _AutogenInstances[id * 2];
            float4 b = _AutogenInstances[id * 2 + 1];

            float3 p = mul(_AutogenModel, float4(positionOS, 1.0)).xyz;
            float3 n = mul((float3x3)_AutogenModel, normalOS);
            float s, c;
            sincos(a.w, s, c);
            p = float3(c * p.x + s * p.z, p.y, -s * p.x + c * p.z);
            n = float3(c * n.x + s * n.z, n.y, -s * n.x + c * n.z);

            // distance thinning: keep the share `keep` of objects, shrinking the rest away
            float3 rootWS = mul(_AutogenPatchToWorld, float4(a.xyz, 1.0)).xyz;
            float dist = distance(rootWS, _WorldSpaceCameraPos);
            float keep = 1.0 - saturate((dist - _AutogenRange.x) * _AutogenRange.z);
            float grow = saturate((keep - b.z) * 12.0);

            Placed o;
            o.positionWS = mul(_AutogenPatchToWorld, float4(a.xyz + p * (b.x * grow), 1.0)).xyz;
            o.normalWS = normalize(mul((float3x3)_AutogenPatchToWorld, n));
            o.upWS = normalize(mul((float3x3)_AutogenPatchToWorld, float3(0.0, 1.0, 0.0)));
            o.variant = b.y;
            o.brightness = b.w;
            return o;
        }

        half4 SampleFoliage(float2 uv, float variant, float dist)
        {
            float hardware = _AutogenAlbedo.CalculateLevelOfDetail(sampler_AutogenAlbedo, uv);
            float byDistance = log2(max(dist / max(_TexFullResDistance, 1.0), 1.0)) * _TexLodPerDoubling;
            return SAMPLE_TEXTURE2D_ARRAY_LOD(_AutogenAlbedo, sampler_AutogenAlbedo, uv, variant, max(hardware, byDistance));
        }
        ENDHLSL

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            float _OceanCameraHeight;
            float _TOD_FogActive;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 upWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                nointerpolation float2 variantBrightness : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Placed p = PlaceInstance(input.instanceID, input.positionOS.xyz, input.normalOS);
                Varyings o;
                o.positionWS = p.positionWS;
                o.positionCS = TransformWorldToHClip(p.positionWS);
                o.upWS = p.upWS;
                o.uv = input.uv;
                o.variantBrightness = float2(p.variant, p.brightness);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 toCamera = _WorldSpaceCameraPos - i.positionWS;
                float dist = length(toCamera);
                float3 V = toCamera / max(dist, 1e-4);

                half4 tex = SampleFoliage(i.uv, i.variantBrightness.x, dist);
                clip(tex.a - _Cutoff);
                half3 albedo = tex.rgb * _Color.rgb * i.variantBrightness.y;

                float3 up = normalize(i.upWS);
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light sun = GetMainLight(shadowCoord);
                float3 L = sun.direction;

                half shade = lerp(1.0 - _BottomShade, 1.0, saturate(i.uv.y));
                half wrapped = saturate((dot(up, L) + _Wrap) / (1.0 + _Wrap));
                half through = pow(saturate(dot(-V, L)), 4.0) * _Translucency;
                half3 light = SampleSH(up) * shade + sun.color * sun.shadowAttenuation * (wrapped * shade + through);
                half3 color = albedo * light;

                // fallback haze (only without the time-of-day fog), as on the terrain
                float airFraction = min(1.0, _AtmosphereHeight / max(_OceanCameraHeight, 1.0));
                half haze = (1.0 - exp(-dist * airFraction / _HazeDistance)) * (1.0 - _TOD_FogActive);
                color = lerp(color, _HazeColor.rgb * sun.color * saturate(dot(up, L) * 5.0 + 0.15), haze);

                return half4(color, 1.0);
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
            #pragma target 4.5
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float variant : TEXCOORD1;
            };

            ShadowVaryings ShadowVert(float4 positionOS : POSITION, float3 normalOS : NORMAL, float2 uv : TEXCOORD0,
                                      uint instanceID : SV_InstanceID)
            {
                Placed p = PlaceInstance(instanceID, positionOS.xyz, normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - p.positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(p.positionWS, p.normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                ShadowVaryings o;
                o.positionCS = positionCS;
                o.uv = uv;
                o.variant = p.variant;
                return o;
            }

            half4 ShadowFrag(ShadowVaryings i) : SV_Target
            {
                half a = SAMPLE_TEXTURE2D_ARRAY(_AutogenAlbedo, sampler_AutogenAlbedo, i.uv, i.variant).a;
                clip(a - _Cutoff);
                return 0;
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
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                nointerpolation float variant : TEXCOORD2;
            };

            DepthVaryings DepthVert(float4 positionOS : POSITION, float3 normalOS : NORMAL, float2 uv : TEXCOORD0,
                                    uint instanceID : SV_InstanceID)
            {
                Placed p = PlaceInstance(instanceID, positionOS.xyz, normalOS);
                DepthVaryings o;
                o.positionWS = p.positionWS;
                o.positionCS = TransformWorldToHClip(p.positionWS);
                o.uv = uv;
                o.variant = p.variant;
                return o;
            }

            half DepthFrag(DepthVaryings i) : SV_Target
            {
                clip(SampleFoliage(i.uv, i.variant, distance(i.positionWS, _WorldSpaceCameraPos)).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DNVert
            #pragma fragment DNFrag

            struct DNVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 upWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                nointerpolation float variant : TEXCOORD3;
            };

            DNVaryings DNVert(float4 positionOS : POSITION, float3 normalOS : NORMAL, float2 uv : TEXCOORD0,
                              uint instanceID : SV_InstanceID)
            {
                Placed p = PlaceInstance(instanceID, positionOS.xyz, normalOS);
                DNVaryings o;
                o.positionWS = p.positionWS;
                o.positionCS = TransformWorldToHClip(p.positionWS);
                o.upWS = p.upWS;
                o.uv = uv;
                o.variant = p.variant;
                return o;
            }

            half4 DNFrag(DNVaryings i) : SV_Target
            {
                clip(SampleFoliage(i.uv, i.variant, distance(i.positionWS, _WorldSpaceCameraPos)).a - _Cutoff);
                return half4(normalize(i.upWS), 0.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
