#ifndef TOD_AERIAL_PERSPECTIVE_INCLUDED
#define TOD_AERIAL_PERSPECTIVE_INCLUDED

// Aerial perspective (distance haze and fog) from the time-of-day atmosphere. The full-screen
// pass applies it to every opaque surface; a custom transparent shader includes this file and
// calls TodApplyAerialPerspective on its colour so glass, water and particles fade the same way.
//
//     #include "Assets/Lighting/TimeOfDay/Shaders/TimeOfDayAerialPerspective.hlsl"
//     color.rgb = TodApplyAerialPerspective(color.rgb, positionWS);
//
// Requires URP's Core.hlsl (for _WorldSpaceCameraPos) to be included first.

#include "AtmosphereCommon.hlsl"

float4 _TOD_AerialParams;      // x: 1 if the fog is on, y: sun scale, z: 1 if moonlit haze is on
float4 _TOD_AerialMoonScale;   // rgb: moon scale (tinted)
Texture3D<float4> _TOD_AerialLut;
Texture3D<float4> _TOD_MoonAerialLut;

// In-scattered light (rgb, scene light units) and transmittance (a) between the camera and a
// surface `distanceKm` away in direction `dirWS`. Apply as color * a + rgb.
float4 TodAerialPerspective(float3 dirWS, float distanceKm)
{
    if (_TOD_AerialParams.x < 0.5) return float4(0.0, 0.0, 0.0, 1.0);

    float3 up = _TOD_Up.xyz;
    float viewZenithCos = dot(dirWS, up);
    float zenith = TodViewZenithAngle(viewZenithCos);
    float w = sqrt(saturate(distanceKm / TodAerialDistanceScale(viewZenithCos)));
    float slice = TodUnitToSubUv(w, _TOD_AerialSize.z);

    float2 unitSun = TodSkyViewParamsToUnit(zenith, TodLightViewCos(dirWS, _TOD_SunDir.xyz, up, viewZenithCos));
    float3 uvwSun = float3(TodUnitToSubUv(unitSun.x, _TOD_AerialSize.x), TodUnitToSubUv(unitSun.y, _TOD_AerialSize.y), slice);
    float4 sun = _TOD_AerialLut.SampleLevel(tod_linear_clamp_sampler, uvwSun, 0);
    float3 inscatter = sun.rgb * _TOD_AerialParams.y;

    [branch] if (_TOD_AerialParams.z > 0.0)
    {
        float2 unitMoon = TodSkyViewParamsToUnit(zenith, TodLightViewCos(dirWS, _TOD_MoonDir.xyz, up, viewZenithCos));
        float3 uvwMoon = float3(TodUnitToSubUv(unitMoon.x, _TOD_AerialSize.x), TodUnitToSubUv(unitMoon.y, _TOD_AerialSize.y), slice);
        inscatter += _TOD_MoonAerialLut.SampleLevel(tod_linear_clamp_sampler, uvwMoon, 0).rgb * _TOD_AerialMoonScale.rgb;
    }
    return float4(inscatter, sun.a);
}

// `color` is the surface's lit colour as written to the camera target (already pre-exposed,
// since the lights are); the in-scattered light is pre-exposed here to match.
float3 TodApplyAerialPerspective(float3 color, float3 positionWS)
{
    float3 toSurface = positionWS - _WorldSpaceCameraPos;
    float d = length(toSurface);
    float4 ap = TodAerialPerspective(toSurface / max(d, 1e-4), d * 0.001);
    return color * ap.a + ap.rgb * TodPreExposure();
}

#endif
