#ifndef TOD_ATMOSPHERE_INTEGRATE_INCLUDED
#define TOD_ATMOSPHERE_INTEGRATE_INCLUDED

// The integrators that fill the LUTs. Shared by the compute kernels and the fragment-shader
// fallback so both paths produce the same textures.

#include "AtmosphereCommon.hlsl"

float4 _TOD_SkyViewLight;   // x: storage scale (keeps twilight inside half-float range), w: light zenith cosine
float4 _TOD_SkyViewSamples; // x: min samples, y: max samples, z: distance (km) at which max is reached


// ---- transmittance ------------------------------------------------------------------------

float3 TodComputeTransmittance(float r, float mu)
{
    // Quadratic spacing, dense near the start where the air is thickest. Matches the CPU
    // model in AtmosphereModel.cs.
    const int steps = 40;
    float rayLength = TodDistanceToTop(r, mu);
    float3 depth = 0.0;
    [loop] for (int i = 0; i < steps; i++)
    {
        float a = (float)i / steps, b = (float)(i + 1) / steps;
        a *= a; b *= b;
        float t = 0.5 * (a + b) * rayLength;
        float dt = (b - a) * rayLength;
        float h = sqrt(t * t + 2.0 * r * mu * t + r * r) - TOD_BOTTOM;
        depth += TodSampleMedium(h).extinction * dt;
    }
    return exp(-depth);
}

// ---- single scattering march --------------------------------------------------------------

struct TodScatteringResult
{
    float3 luminance;      // in-scattered light, per unit of light illuminance
    float3 multiScatAs1;   // scattering seen by unit isotropic light (multiple-scattering LUT only)
    float3 transmittance;  // along the view ray
};

// ---- the source term --------------------------------------------------------------------

struct TodPhases
{
    float rayleigh, mie, haze;
};

TodPhases TodGetPhases(float cosTheta, bool usePhase)
{
    TodPhases ph;
    const float isotropic = 1.0 / (4.0 * TOD_PI);
    ph.rayleigh = usePhase ? TodRayleighPhase(cosTheta) : isotropic;
    ph.mie = usePhase ? TodMiePhase(cosTheta, _TOD_Mie.w) : isotropic;
    ph.haze = usePhase ? TodMiePhase(cosTheta, _TOD_HazePhase.x) : isotropic;
    return ph;
}

// Light that reaches a point inside the weather haze by diffusing through the haze above it.
// Single scattering plus Hillaire's multiple-scattering term let almost nothing through an
// optically thick layer (a 1.5 km layer at 0.5 km visibility has a vertical optical depth of
// ~9), while a real fog or overcast still passes 20-30% as diffuse light. Two-stream estimate
// of the total (direct + diffuse) transmission of the haze above, 1 / (1 + 0.75 (1-g) tau),
// minus the direct part the transmittance LUT already carries; lit by the sun at the haze top.
float3 TodHazeDiffuse(float height, float lightMu)
{
    float above = _TOD_Haze.z - height;
    if (_TOD_Haze.x <= 0.0 || above <= 0.0) return 0.0;

    float tau = _TOD_Haze.x * above;
    float slant = tau / max(lightMu, 0.05);
    float total = 1.0 / (1.0 + 0.75 * (1.0 - _TOD_HazePhase.x) * slant);
    float direct = exp(-slant);
    float3 atTop = TodTransmittanceToTop(TOD_BOTTOM + _TOD_Haze.z, lightMu);
    return atTop * max(0.0, total - direct);
}

// Light scattered toward the viewer per unit length at `p` (planet-centred, km), from a light
// of unit illuminance along `lightDir`, plus the medium there.
void TodScatteringSource(float3 p, float3 lightDir, TodPhases ph, bool useMultiScat, bool hazeDiffuse,
                         out float3 source, out float3 scattering, out float3 extinction)
{
    float pr = length(p);
    float lightMu = dot(p / pr, lightDir);
    float h = pr - TOD_BOTTOM;
    TodMedium medium = TodSampleMedium(h);

    float3 lightTransmittance = TodTransmittanceToTop(pr, lightMu); // zero in the planet's shadow
    scattering = medium.scatteringRayleigh + medium.scatteringMie + medium.scatteringHaze;
    float3 phaseScattering = medium.scatteringRayleigh * ph.rayleigh + medium.scatteringMie * ph.mie
                             + medium.scatteringHaze * ph.haze;

    source = lightTransmittance * phaseScattering;
    if (useMultiScat) source += TodMultiScattering(pr, lightMu) * scattering;
    if (hazeDiffuse) source += TodHazeDiffuse(h, lightMu) * scattering * (1.0 / (4.0 * TOD_PI));
    extinction = max(medium.extinction, 1e-9);
}

// Marches `dir` from `position` (planet-centred, km) and integrates light scattered toward
// the viewer from a directional light along `lightDir` with unit illuminance.
//  - usePhase: Rayleigh + Mie phase and the diffuse haze term (sky, aerial perspective);
//    false = isotropic, single scattering only (multiple-scattering LUT).
//  - useMultiScat: add the multiple-scattering LUT term.
//  - addGround: add the lit ground where the ray ends on it.
TodScatteringResult TodIntegrateScattering(float3 position, float3 dir, float3 lightDir,
                                           int minSamples, int maxSamples, float maxSamplesDistance,
                                           bool usePhase, bool useMultiScat, bool addGround)
{
    TodScatteringResult res;
    res.luminance = 0.0;
    res.multiScatAs1 = 0.0;
    res.transmittance = 1.0;

    if (!TodMoveToTop(position, dir)) return res;

    float r = length(position);
    float3 up = position / r;
    float mu = dot(up, dir);

    float tBottom = TodDistanceToBottom(r, mu);
    float tTop = TodDistanceToTop(r, mu);
    bool hitsGround = tBottom >= 0.0;
    float tMax = hitsGround ? tBottom : tTop;
    if (tMax <= 0.0) return res;

    float sampleCount = lerp((float)minSamples, (float)maxSamples, saturate(tMax / maxSamplesDistance));
    int steps = (int)ceil(sampleCount);
    TodPhases ph = TodGetPhases(dot(dir, lightDir), usePhase);

    float3 throughput = 1.0;
    [loop] for (int i = 0; i < steps; i++)
    {
        float a = (float)i / steps, b = (float)(i + 1) / steps;
        float t0 = a * a * tMax, t1 = b * b * tMax;
        float t = t0 + (t1 - t0) * 0.3;   // Hillaire's sample offset within the segment
        float dt = t1 - t0;

        float3 source, scattering, ext;
        TodScatteringSource(position + dir * t, lightDir, ph, useMultiScat, usePhase, source, scattering, ext);

        // Analytic integration over the segment assuming constant medium (Hillaire 2015).
        float3 segmentTransmittance = exp(-ext * dt);
        res.luminance += throughput * (source - source * segmentTransmittance) / ext;
        res.multiScatAs1 += throughput * (scattering - scattering * segmentTransmittance) / ext;
        throughput *= segmentTransmittance;
    }

    if (addGround && hitsGround)
    {
        float3 p = position + dir * tBottom;
        float3 pUp = normalize(p);
        float lightMu = dot(pUp, lightDir);
        float3 lightTransmittance = TodTransmittanceToTop(TOD_BOTTOM + 1e-4, lightMu);
        res.luminance += throughput * lightTransmittance * saturate(lightMu) * _TOD_GroundAlbedo.rgb / TOD_PI;
    }

    res.transmittance = throughput;
    return res;
}

// ---- LUT texels ---------------------------------------------------------------------------

float4 TodTransmittanceTexel(float2 uv)
{
    float r, mu;
    TodTransmittanceUvToRMu(uv, r, mu);
    return float4(TodComputeTransmittance(r, mu), 1.0);
}

// Multiple scattering: integrate second-order light over the sphere of directions (64 of
// them) and sum the geometric series of higher orders, Hillaire 2020 eq. 5-9.
#define TOD_MULTISCAT_SQRT_DIRECTIONS 8
#define TOD_MULTISCAT_DIRECTIONS (TOD_MULTISCAT_SQRT_DIRECTIONS * TOD_MULTISCAT_SQRT_DIRECTIONS)

// One of the 64 directions for a multiple-scattering texel. The compute path runs the
// directions as 64 threads and sums them in shared memory; the fragment path loops.
void TodMultiScatDirection(float2 uv, uint index, out float3 luminance, out float3 fms)
{
    float2 unit = float2(TodSubUvToUnit(uv.x, _TOD_MultiScatSize.x), TodSubUvToUnit(uv.y, _TOD_MultiScatSize.y));
    float sunMu = unit.x * 2.0 - 1.0;
    float r = TOD_BOTTOM + saturate(unit.y) * (TOD_TOP - TOD_BOTTOM);
    r = clamp(r, TOD_BOTTOM + 1e-3, TOD_TOP - 1e-3);

    float3 position = float3(0.0, 0.0, r);
    float3 sunDir = float3(TodSafeSqrt(1.0 - sunMu * sunMu), 0.0, sunMu);

    uint i = index % TOD_MULTISCAT_SQRT_DIRECTIONS;
    uint j = index / TOD_MULTISCAT_SQRT_DIRECTIONS;
    float theta = 2.0 * TOD_PI * (i + 0.5) / TOD_MULTISCAT_SQRT_DIRECTIONS;
    float cosPhi = 1.0 - 2.0 * (j + 0.5) / TOD_MULTISCAT_SQRT_DIRECTIONS;
    float sinPhi = TodSafeSqrt(1.0 - cosPhi * cosPhi);
    float3 dir = float3(sinPhi * cos(theta), sinPhi * sin(theta), cosPhi);

    TodScatteringResult s = TodIntegrateScattering(position, dir, sunDir, 20, 20, 1.0, false, false, true);
    luminance = s.luminance;
    fms = s.multiScatAs1;
}

// Uniform sphere sampling weights each direction 4pi/N, times the isotropic phase 1/4pi: 1/N.
// Then the geometric series of higher orders, L2 / (1 - f_ms).
float4 TodMultiScatResolve(float3 luminanceSum, float3 fmsSum)
{
    float3 secondOrder = luminanceSum / TOD_MULTISCAT_DIRECTIONS;
    float3 fms = fmsSum / TOD_MULTISCAT_DIRECTIONS;
    return float4(secondOrder / max(1.0 - fms, 1e-3), 1.0);
}

float4 TodMultiScatTexel(float2 uv)
{
    float3 lumSum = 0.0, fmsSum = 0.0;
    [loop] for (uint k = 0; k < TOD_MULTISCAT_DIRECTIONS; k++)
    {
        float3 l, f;
        TodMultiScatDirection(uv, k, l, f);
        lumSum += l;
        fmsSum += f;
    }
    return TodMultiScatResolve(lumSum, fmsSum);
}

// Sky radiance for one sky-view texel, for a light at zenith cosine _TOD_SkyViewLight.w and
// azimuth 0, seen from the observer's radius.
float4 TodSkyViewTexel(float2 uv)
{
    float viewZenithCos, lightViewCos;
    TodSkyViewUvToParams(uv, viewZenithCos, lightViewCos);
    float3 dir = TodSkyViewDirection(viewZenithCos, lightViewCos);

    float lightMu = _TOD_SkyViewLight.w;
    float3 lightDir = float3(TodSafeSqrt(1.0 - lightMu * lightMu), 0.0, lightMu);

    float3 position = float3(0.0, 0.0, TOD_BOTTOM + max(_TOD_SkyViewSize.z, 5e-4));
    TodScatteringResult s = TodIntegrateScattering(position, dir, lightDir,
                                                   (int)_TOD_SkyViewSamples.x, (int)_TOD_SkyViewSamples.y,
                                                   _TOD_SkyViewSamples.z, true, true, true);
    return float4(s.luminance * _TOD_SkyViewLight.x, 1.0);
}

// ---- aerial perspective LUT ----------------------------------------------------------------
//
// Light scattered toward the viewer between it and a surface, and the transmittance to that
// surface. Laid out like the sky view (azimuth from the light, view zenith packed toward the
// horizon, same build height) with distance as the third axis, sqrt-spaced so the near field
// gets most of the slices. Independent of camera orientation, so it is rebuilt with the sky
// view rather than every frame (Hillaire's froxel version is per camera, per frame).
//
// Each column (one view direction) is one march from the camera outward, a few uniform steps
// per slice, recording the running result at every slice. Neighbouring slices therefore share
// their samples; integrating each slice separately put the samples somewhere different every
// time and showed as rings on hazy ground seen from altitude. The compute path marches a column
// once and writes every slice; the fragment path repeats the same march up to its own slice.

float4 _TOD_AerialSamples;  // x: ray-march steps per slice

struct TodAerialRay
{
    float3 position, dir, lightDir;
    TodPhases phases;
    float tEnter, tEnd;     // where the ray is in the air: from the camera, km
    float distanceScale;    // distance of the last slice (TodAerialDistanceScale)
    float t;                // marched this far
    float3 luminance, transmittance;
};

TodAerialRay TodAerialBegin(float2 unit)
{
    float viewZenithCos, lightViewCos;
    TodSkyViewUnitToParams(unit, viewZenithCos, lightViewCos);

    TodAerialRay ray;
    ray.dir = TodSkyViewDirection(viewZenithCos, lightViewCos);
    float lightMu = _TOD_SkyViewLight.w;
    ray.lightDir = float3(TodSafeSqrt(1.0 - lightMu * lightMu), 0.0, lightMu);
    ray.position = float3(0.0, 0.0, TOD_BOTTOM + max(_TOD_SkyViewSize.z, 5e-4));
    ray.phases = TodGetPhases(dot(ray.dir, ray.lightDir), true);
    ray.t = 0.0;
    ray.luminance = 0.0;
    ray.transmittance = 1.0;

    // Entry into the air (non-zero only for a viewer above it) and the end: ground or space.
    float3 entry = ray.position;
    bool enters = TodMoveToTop(entry, ray.dir);
    ray.tEnter = enters ? length(entry - ray.position) : 0.0;
    float rIn = length(entry);
    float muIn = dot(entry / rIn, ray.dir);
    float tBottom = TodDistanceToBottom(rIn, muIn);
    ray.tEnd = !enters ? 0.0 : ray.tEnter + (tBottom >= 0.0 ? tBottom : TodDistanceToTop(rIn, muIn));
    ray.distanceScale = TodAerialDistanceScale(viewZenithCos);
    return ray;
}

void TodAerialMarch(inout TodAerialRay ray, float a, float b, int steps)
{
    float dt = (b - a) / steps;
    [loop] for (int i = 0; i < steps; i++)
    {
        float3 source, scattering, ext;
        TodScatteringSource(ray.position + ray.dir * (a + (i + 0.5) * dt), ray.lightDir, ray.phases,
                            true, true, source, scattering, ext);
        float3 segmentTransmittance = exp(-ext * dt);
        ray.luminance += ray.transmittance * (source - source * segmentTransmittance) / ext;
        ray.transmittance *= segmentTransmittance;
    }
}

// Distances along the ray (from the camera) where it crosses the top of the haze layer, or a
// huge value where it does not.
float2 TodHazeTopCrossings(TodAerialRay ray)
{
    float h = ray.position.z - TOD_BOTTOM;              // camera height (the ray starts on +z)
    float top = _TOD_Haze.z;
    float b = dot(ray.position, ray.dir);
    float c = (h - top) * (2.0 * TOD_BOTTOM + h + top); // |o|^2 - (R + top)^2, stably
    float disc = b * b - c;
    if (_TOD_Haze.x <= 0.0 || disc <= 0.0) return 1e9;
    float s = sqrt(disc);
    float t0 = -b - s, t1 = -b + s;
    return float2(t0 > 0.0 ? t0 : 1e9, t1 > 0.0 ? t1 : 1e9);
}

// Marches from where the ray is to `tTarget` (km from the camera) in `steps` uniform steps,
// clipped to the stretch of the ray that is in the air. A step that would straddle the top of
// the haze layer is split there: seen from above, the layer is reached at the far end of a
// long ray where steps are kilometres long, and whether a sample fell just above or just below
// the top flipped from one view direction to the next, drawing rings on hazy ground.
void TodAerialAdvance(inout TodAerialRay ray, float tTarget, int steps)
{
    float a = max(ray.t, ray.tEnter);
    float b = min(tTarget, ray.tEnd);
    ray.t = tTarget;
    if (b <= a) return;

    float2 cross = TodHazeTopCrossings(ray);
    float s0 = clamp(min(cross.x, cross.y), a, b);
    float s1 = clamp(max(cross.x, cross.y), a, b);
    if (s0 > a) TodAerialMarch(ray, a, s0, steps);
    if (s1 > s0) TodAerialMarch(ray, s0, s1, steps);
    if (b > s1) TodAerialMarch(ray, s1, b, steps);
}

// Distance (km) of a slice centre: sqrt spacing, slice 0 at the camera, the last at the ray's
// distance scale.
float TodAerialSliceDistance(TodAerialRay ray, int slice)
{
    float w = slice / (_TOD_AerialSize.z - 1.0);
    return w * w * ray.distanceScale;
}

// One transmittance for all channels, so the fog can be applied with plain alpha blending
// (colour * T + inscatter) and never has to read the colour buffer. Hillaire does the same.
float4 TodAerialOutput(TodAerialRay ray)
{
    return float4(ray.luminance * _TOD_SkyViewLight.x, dot(ray.transmittance, float3(0.2126, 0.7152, 0.0722)));
}

// Fragment path: one texel, marched from the camera through every slice up to its own.
float4 TodAerialTexel(float2 uv, int slice)
{
    float2 unit = float2(TodSubUvToUnit(uv.x, _TOD_AerialSize.x), TodSubUvToUnit(uv.y, _TOD_AerialSize.y));
    TodAerialRay ray = TodAerialBegin(unit);
    int steps = max(1, (int)_TOD_AerialSamples.x);
    [loop] for (int k = 1; k <= slice; k++) TodAerialAdvance(ray, TodAerialSliceDistance(ray, k), steps);
    return TodAerialOutput(ray);
}

#endif
