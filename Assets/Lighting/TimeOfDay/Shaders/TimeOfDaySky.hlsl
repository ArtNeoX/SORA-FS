#ifndef TOD_SKY_INCLUDED
#define TOD_SKY_INCLUDED

// Radiance of the sky in any direction: the sky-view LUTs for sun and moon, airglow, the sun
// and moon discs and the stars. Used by the skybox and, later, by anything that has to see the
// sky (reflection cubemap, ambient). Unity-space directions; values in the scene's light
// units, where a diffuse white surface facing a light of intensity I shows I.

#include "AtmosphereCommon.hlsl"

// ---- per-frame state (set by AtmosphereRenderer; _TOD_Up/SunDir/MoonDir live in Common) -----

float4 _TOD_SunSkyScale;     // rgb: scales the sun sky-view LUT (pi * sun intensity)
float4 _TOD_MoonSkyScale;    // rgb: same for the moon, a: 1 if the moon LUT is in use
float4 _TOD_SunDiskRadiance; // rgb: centre radiance outside the atmosphere, a: limb darkening u
float4 _TOD_MoonParams;      // x: sunlit-surface scale, y: earthshine, z: draw moon (0/1)
float4 _TOD_Stars;           // x: brightness, y: density, z: fade luminance, w: twinkle
float4 _TOD_StarsContrast;   // x: star/sky contrast below which a star is lost in the sky glow
float4 _TOD_NightSky;        // rgb: airglow radiance
float4 _TOD_SkyBlend;        // x: previous -> current blend, y / z: 1 / storage scale of current / previous
float4x4 _TOD_UnityToCelestial;

Texture2D<float4> _TOD_SkyViewLut;
Texture2D<float4> _TOD_SkyViewLutPrev;
Texture2D<float4> _TOD_MoonSkyViewLut;

// ---- sky-view lookup ----------------------------------------------------------------------

float3 TodSkyFromLuts(float3 v, float viewZenithCos)
{
    float3 up = _TOD_Up.xyz;
    float zenith = TodViewZenithAngle(viewZenithCos);

    float2 uvSun = TodSkyViewParamsToUv(zenith, TodLightViewCos(v, _TOD_SunDir.xyz, up, viewZenithCos));
    // LUTs are stored pre-scaled (see AtmosphereRenderer.SkyViewStorageScale); y and z undo it.
    float3 current = _TOD_SkyViewLut.SampleLevel(tod_linear_clamp_sampler, uvSun, 0).rgb * _TOD_SkyBlend.y;
    float3 sky = current;
    [branch] if (_TOD_SkyBlend.x < 1.0)
    {
        float3 previous = _TOD_SkyViewLutPrev.SampleLevel(tod_linear_clamp_sampler, uvSun, 0).rgb * _TOD_SkyBlend.z;
        sky = lerp(previous, current, _TOD_SkyBlend.x);
    }
    sky *= _TOD_SunSkyScale.rgb;

    [branch] if (_TOD_MoonSkyScale.a > 0.5)
    {
        float2 uvMoon = TodSkyViewParamsToUv(zenith, TodLightViewCos(v, _TOD_MoonDir.xyz, up, viewZenithCos));
        sky += _TOD_MoonSkyViewLut.SampleLevel(tod_linear_clamp_sampler, uvMoon, 0).rgb * _TOD_MoonSkyScale.rgb;
    }
    return sky;
}

// ---- hashing / noise for stars and the moon --------------------------------------------

float TodHash(float3 p)
{
    p = frac(p * 0.3183099 + 0.1);
    p *= 17.0;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float TodValueNoise(float3 x)
{
    float3 i = floor(x);
    float3 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(lerp(TodHash(i + float3(0, 0, 0)), TodHash(i + float3(1, 0, 0)), f.x),
                     lerp(TodHash(i + float3(0, 1, 0)), TodHash(i + float3(1, 1, 0)), f.x), f.y),
                lerp(lerp(TodHash(i + float3(0, 0, 1)), TodHash(i + float3(1, 0, 1)), f.x),
                     lerp(TodHash(i + float3(0, 1, 1)), TodHash(i + float3(1, 1, 1)), f.x), f.y), f.z);
}

// ---- bodies ------------------------------------------------------------------------------

// Sun disc with limb darkening. `pixelAngle` is one pixel's angular size, for an anti-aliased rim.
float3 TodSunDisc(float3 v, float pixelAngle)
{
    float radius = _TOD_SunDir.w;
    float x = length(v - _TOD_SunDir.xyz) / radius; // chord ~ angle, and keeps precision near 0
    if (x >= 1.0 + pixelAngle / radius) return 0.0;
    float mu = TodSafeSqrt(1.0 - x * x);
    float rim = saturate((1.0 - x) * radius / max(pixelAngle, 1e-6) + 0.5);
    return _TOD_SunDiskRadiance.rgb * (1.0 - _TOD_SunDiskRadiance.a * (1.0 - mu)) * rim;
}

// Moon disc, lit by the sun (Lommel-Seeliger, so a full moon stays bright to its limb) plus
// earthshine on the dark side. Maria are procedural noise in a frame tied to celestial north.
// Returns coverage in w so the moon can hide the sun (eclipses) and the stars.
float4 TodMoonDisc(float3 v, float pixelAngle)
{
    float radius = _TOD_MoonDir.w;
    float3 m = _TOD_MoonDir.xyz;
    float3 offset = v - m;
    float x = length(offset) / radius;
    if (_TOD_MoonParams.z < 0.5 || x >= 1.0 + pixelAngle / radius) return 0.0;

    float3 toViewer = -m;
    float3 tangent = (offset - m * dot(offset, m)) / radius;           // in-disc position, |t| = x
    float3 n = normalize(tangent + toViewer * TodSafeSqrt(1.0 - saturate(dot(tangent, tangent))));

    float mu0 = saturate(dot(n, _TOD_SunDir.xyz));                     // sun incidence
    float muV = saturate(dot(n, toViewer));                            // emission toward us
    float sunlit = mu0 > 0.0 ? 2.0 * mu0 / (mu0 + muV + 1e-4) : 0.0;  // = 1 at full-moon centre

    float3 north = _TOD_UnityToCelestial[2].xyz;                       // celestial pole, Unity space
    float3 right = normalize(cross(north, toViewer) + float3(1e-5, 0, 0));
    float3 upAxis = cross(toViewer, right);
    float3 local = float3(dot(n, right), dot(n, upAxis), dot(n, toViewer));
    float maria = TodValueNoise(local * 2.3 + 7.1) * 0.65 + TodValueNoise(local * 7.0 + 3.3) * 0.35;
    float albedo = lerp(0.075, 0.15, smoothstep(0.35, 0.65, maria));

    float earthshine = _TOD_MoonParams.y * muV;
    float rim = saturate((1.0 - x) * radius / max(pixelAngle, 1e-6) + 0.5);
    float3 radiance = albedo * (_TOD_MoonParams.x * sunlit + earthshine) * float3(1.0, 0.97, 0.92);
    return float4(radiance, rim);
}

// Procedural star field in celestial coordinates, so it turns with sidereal time. Cube-face
// grid; each cell holds at most one star with a power-law brightness (many faint, few bright)
// and a colour from orange to blue-white. Drawn as a pixel-sized Gaussian, so stars stay one
// pixel wide at any zoom.
float3 TodStars(float3 v, float pixelAngle, float viewZenithCos, float skyLuminance)
{
    if (_TOD_Stars.x <= 0.0) return 0.0;

    float3 c = mul((float3x3)_TOD_UnityToCelestial, v);
    float3 a = abs(c);
    float face; float2 uv; float major;
    if (a.x >= a.y && a.x >= a.z) { face = c.x > 0 ? 0 : 1; uv = c.yz; major = a.x; }
    else if (a.y >= a.z)          { face = c.y > 0 ? 2 : 3; uv = c.xz; major = a.y; }
    else                          { face = c.z > 0 ? 4 : 5; uv = c.xy; major = a.z; }
    uv /= major;

    const float cells = 256.0;
    float2 cell = floor((uv * 0.5 + 0.5) * cells);
    float3 seed = float3(cell, face * 311.0);
    if (TodHash(seed) > _TOD_Stars.y) return 0.0;

    float2 jitter = 0.2 + 0.6 * float2(TodHash(seed + 17.3), TodHash(seed + 41.9));
    float2 starUv = (cell + jitter) / cells * 2.0 - 1.0;
    float3 s;
    if (face < 2)      s = float3(face == 0 ? 1 : -1, starUv.x, starUv.y);
    else if (face < 4) s = float3(starUv.x, face == 2 ? 1 : -1, starUv.y);
    else               s = float3(starUv.x, starUv.y, face == 4 ? 1 : -1);
    s = normalize(s);

    float d = length(c - s) / max(pixelAngle, 1e-6);
    float shape = exp(-d * d * 1.6);
    if (shape < 1e-3) return 0.0;

    float u = max(TodHash(seed + 73.1), 1e-5);
    float brightness = min(1.0, 0.001 * pow(u, -0.667)) * _TOD_Stars.x;   // N(>b) ~ b^-1.5

    // Each star is seen only while it stands out from the sky behind it: faint ones vanish in
    // moonlight or twilight, bright ones last until the sky is much brighter.
    float contrast = brightness / max(skyLuminance, 1e-7);
    float visible = saturate((contrast - _TOD_StarsContrast.x) / max(_TOD_StarsContrast.x, 1e-6));
    if (visible <= 0.0) return 0.0;

    float temp = TodHash(seed + 5.7);
    float3 tint = lerp(float3(1.0, 0.72, 0.5), float3(0.72, 0.84, 1.0), temp * temp);

    float low = saturate(1.0 - viewZenithCos * 2.0);          // twinkle grows toward the horizon
    float twinkle = 1.0 + _TOD_Stars.w * low * sin(_Time.y * (7.0 + 9.0 * temp) + 40.0 * u);
    return brightness * visible * tint * shape * twinkle;
}

// The camera's 32-bit HDR target (R11G11B10) keeps 6 mantissa bits in red and green and 5 in
// blue, so a smooth sky is stored in 1.5-3% steps. At night exposure lifts those steps into
// visible bands. Dithering by half a step before the write turns them into invisible grain.
float3 TodDitherForR11G11B10(float3 color, float2 pixel)
{
    float n = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715)))) - 0.5;
    return color * (1.0 + n * float3(1.0 / 64.0, 1.0 / 64.0, 1.0 / 32.0));
}

// ---- everything --------------------------------------------------------------------------

// The sky without sun, moon or stars: what reflections and ambient light see. (The sun's own
// highlight and light come from the directional light; a disc in the reflection cubemap would
// count it twice.)
float3 TodSkyBackground(float3 v, float viewZenithCos, out bool ground)
{
    float3 sky = TodSkyFromLuts(v, viewZenithCos);
    ground = TodRayHitsGround(_TOD_Radii.w, viewZenithCos);
    if (!ground)
    {
        // Airglow: a faint floor so a moonless night is dark blue, not black. Brighter toward
        // the horizon (longer path through the emitting layer), gone above it.
        float airglowHeight = saturate(1.0 - _TOD_Radii.z / 100.0);
        sky += _TOD_NightSky.rgb * lerp(1.8, 1.0, saturate(viewZenithCos)) * airglowHeight;
    }
    return sky;
}

float3 TodSkyRadiance(float3 v, float pixelAngle)
{
    float viewZenithCos = dot(v, _TOD_Up.xyz);
    bool ground;
    float3 sky = TodSkyBackground(v, viewZenithCos, ground);
    float r = _TOD_Radii.w;

    if (!ground)
    {
        float4 moon = TodMoonDisc(v, pixelAngle);
        float3 behindMoon = TodSunDisc(v, pixelAngle);

        // Stars: per-star contrast against the sky (TodStars), plus a hard ceiling so even the
        // brightest are gone by daylight.
        float skyLuminance = dot(sky, float3(0.2126, 0.7152, 0.0722));
        float starVisibility = saturate(1.0 - skyLuminance / max(_TOD_Stars.z, 1e-6));
        [branch] if (starVisibility > 0.0) behindMoon += TodStars(v, pixelAngle, viewZenithCos, skyLuminance) * starVisibility;

        // Transmittance toward space only where a disc or a star is actually drawn: most of the
        // sky has neither, and this is the costliest lookup in the shader.
        float3 bodies = lerp(behindMoon, moon.rgb, moon.a);
        [branch] if (any(bodies > 0.0)) sky += TodTransmittanceToSpace(r, viewZenithCos) * bodies;
    }
    return sky;
}

#endif
