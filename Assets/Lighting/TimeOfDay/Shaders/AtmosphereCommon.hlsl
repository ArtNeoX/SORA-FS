#ifndef TOD_ATMOSPHERE_COMMON_INCLUDED
#define TOD_ATMOSPHERE_COMMON_INCLUDED

// Shared definition of the atmosphere for every time-of-day shader: parameters, geometry,
// the participating medium, phase functions and the LUT parameterisations.
//
// Units are kilometres and 1/km. Positions are relative to the planet centre; "r" is the
// distance from the centre and "mu" the cosine of a ray's zenith angle at r.
//
// Precision: r is ~6378 km, so r*r - R*R loses everything for an observer a few metres up.
// Every such difference is written as (r - R) * (r + R), and the observer's own height is
// passed separately (_TOD_Radii.z) instead of being recovered from r.
//
// The parameterisations are Bruneton 2017 (transmittance) and Hillaire 2020 (multiple
// scattering, sky view), so the textures are interchangeable with those papers' code.

#define TOD_PI 3.14159265359

// ---- parameters (set by AtmosphereRenderer) -------------------------------------------

float4 _TOD_Radii;        // x: bottom radius, y: top radius, z: observer height above bottom, w: observer r
float4 _TOD_Rayleigh;     // rgb: scattering at sea level, a: scale height
float4 _TOD_Mie;          // x: scattering, y: extinction, z: scale height, w: anisotropy g
float4 _TOD_Ozone;        // rgb: absorption at the layer centre, a: unused
float4 _TOD_OzoneLayer;   // x: centre height, y: half width
float4 _TOD_Haze;         // x: extinction, y: scattering, z: layer top, w: top transition (weather haze)
float4 _TOD_HazePhase;    // x: haze anisotropy g
float4 _TOD_GroundAlbedo; // rgb

// Pre-exposure (set by ExposureController): x multiplier, y its inverse, z EV. Every shader that
// writes scene light into the camera target multiplies by it, so the HDR buffer always holds
// display-range values; at night the scene-referred values (~1e-5) would otherwise sit in the
// denormal range of the 32-bit HDR format and posterise once exposed. LUTs stay scene-referred.
float4 _TOD_Exposure;

float TodPreExposure() { return _TOD_Exposure.x > 0.0 ? _TOD_Exposure.x : 1.0; }

// Per-frame viewer state, Unity space (set by AtmosphereRenderer; unused by the LUT passes).
float4 _TOD_Up;           // xyz: observer up
float4 _TOD_SunDir;       // xyz: toward the sun, w: angular radius (rad)
float4 _TOD_MoonDir;      // xyz: toward the moon, w: angular radius (rad)

float4 _TOD_TransmittanceSize; // xy: size, zw: 1/size
float4 _TOD_MultiScatSize;
float4 _TOD_AerialSize;        // xyz: aerial-perspective LUT size, w: distance (km) of its last slice
float4 _TOD_SkyViewSize;       // xy: size, z: build height (km), w: nadir-to-horizon angle at that height (rad)

Texture2D<float4> _TOD_TransmittanceLut;
Texture2D<float4> _TOD_MultiScatLut;
SamplerState tod_linear_clamp_sampler;

#define TOD_BOTTOM (_TOD_Radii.x)
#define TOD_TOP (_TOD_Radii.y)

// ---- small helpers ---------------------------------------------------------------------

float TodSafeSqrt(float x) { return sqrt(max(0.0, x)); }

// Texel-centre remapping so a [0,1] parameter hits the first and last texel centres exactly.
float TodUnitToSubUv(float u, float resolution) { return (0.5 + u * (resolution - 1.0)) / resolution; }
float TodSubUvToUnit(float u, float resolution) { return (u * resolution - 0.5) / (resolution - 1.0); }

// ---- geometry ----------------------------------------------------------------------------

// Distance along (r, mu) to the top of the atmosphere, for r <= top.
float TodDistanceToTop(float r, float mu)
{
    float disc = r * r * mu * mu + (TOD_TOP - r) * (TOD_TOP + r);
    return max(0.0, -r * mu + TodSafeSqrt(disc));
}

// Distance along (r, mu) to the ground, or a negative number if the ray misses it.
float TodDistanceToBottom(float r, float mu)
{
    float disc = r * r * mu * mu - (r - TOD_BOTTOM) * (r + TOD_BOTTOM);
    if (mu >= 0.0 || disc < 0.0) return -1.0;
    return max(0.0, -r * mu - sqrt(disc));
}

bool TodRayHitsGround(float r, float mu)
{
    return mu < 0.0 && r * r * mu * mu - (r - TOD_BOTTOM) * (r + TOD_BOTTOM) >= 0.0;
}

// Nearest intersection of a ray with a centred sphere; -1 if none in front.
float TodRaySphereNearest(float3 origin, float3 dir, float radius)
{
    float b = dot(origin, dir);
    float c = dot(origin, origin) - radius * radius;
    float disc = b * b - c;
    if (disc < 0.0) return -1.0;
    float s = sqrt(disc);
    float t0 = -b - s, t1 = -b + s;
    if (t1 < 0.0) return -1.0;
    return t0 >= 0.0 ? t0 : t1;
}

// A viewer outside the atmosphere is moved to where its ray enters it. False if it never does.
bool TodMoveToTop(inout float3 position, float3 dir)
{
    float r = length(position);
    if (r <= TOD_TOP) return true;
    float t = TodRaySphereNearest(position, dir, TOD_TOP);
    if (t < 0.0) return false;
    float3 up = position / r;
    position = position + dir * t - up * 1e-3; // a metre inside, so the march starts in air
    return true;
}

// ---- medium -----------------------------------------------------------------------------

struct TodMedium
{
    float3 scatteringRayleigh;
    float scatteringMie;     // background aerosol
    float scatteringHaze;    // weather haze: its own phase (_TOD_HazePhase), less forward than Mie
    float3 extinction;
};

// Weather haze: a well-mixed boundary layer of uniform density from the ground up to its top,
// fading out smoothly across the transition. Matches AtmosphereModel.HazeProfile on the CPU.
float TodHazeProfile(float h)
{
    float t = saturate((h - (_TOD_Haze.z - 0.5 * _TOD_Haze.w)) / max(_TOD_Haze.w, 1e-3));
    return 1.0 - t * t * (3.0 - 2.0 * t);
}

TodMedium TodSampleMedium(float height)
{
    float h = max(height, 0.0);
    float rayleigh = exp(-h / _TOD_Rayleigh.a);
    float mie = exp(-h / _TOD_Mie.z);
    float ozone = max(0.0, 1.0 - abs(h - _TOD_OzoneLayer.x) / _TOD_OzoneLayer.y);
    float haze = TodHazeProfile(h);

    TodMedium m;
    m.scatteringRayleigh = _TOD_Rayleigh.rgb * rayleigh;
    m.scatteringMie = _TOD_Mie.x * mie;
    m.scatteringHaze = _TOD_Haze.y * haze;
    m.extinction = m.scatteringRayleigh + _TOD_Mie.y * mie + _TOD_Haze.x * haze + _TOD_Ozone.rgb * ozone;
    return m;
}

float TodRayleighPhase(float cosTheta)
{
    return 3.0 / (16.0 * TOD_PI) * (1.0 + cosTheta * cosTheta);
}

// Cornette-Shanks: Henyey-Greenstein with a Rayleigh-like term, better around the sun.
float TodMiePhase(float cosTheta, float g)
{
    float g2 = g * g;
    float k = 3.0 / (8.0 * TOD_PI) * (1.0 - g2) / (2.0 + g2);
    float denom = max(1e-4, 1.0 + g2 - 2.0 * g * cosTheta);
    return k * (1.0 + cosTheta * cosTheta) / (denom * sqrt(denom));
}

// ---- transmittance LUT (Bruneton 2017, 4.2) ---------------------------------------------

float2 TodTransmittanceRMuToUv(float r, float mu)
{
    float H = sqrt((TOD_TOP - TOD_BOTTOM) * (TOD_TOP + TOD_BOTTOM));
    float rho = TodSafeSqrt((r - TOD_BOTTOM) * (r + TOD_BOTTOM));
    float d = TodDistanceToTop(r, mu);
    float dMin = TOD_TOP - r;
    float dMax = rho + H;
    float xMu = (d - dMin) / max(dMax - dMin, 1e-6);
    float xR = rho / H;
    return float2(TodUnitToSubUv(saturate(xMu), _TOD_TransmittanceSize.x),
                  TodUnitToSubUv(saturate(xR), _TOD_TransmittanceSize.y));
}

void TodTransmittanceUvToRMu(float2 uv, out float r, out float mu)
{
    float xMu = TodSubUvToUnit(uv.x, _TOD_TransmittanceSize.x);
    float xR = TodSubUvToUnit(uv.y, _TOD_TransmittanceSize.y);
    float H = sqrt((TOD_TOP - TOD_BOTTOM) * (TOD_TOP + TOD_BOTTOM));
    float rho = H * xR;
    r = sqrt(rho * rho + TOD_BOTTOM * TOD_BOTTOM);
    float dMin = TOD_TOP - r;
    float dMax = rho + H;
    float d = dMin + xMu * (dMax - dMin);
    mu = d <= 0.0 ? 1.0 : (H * H - rho * rho - d * d) / (2.0 * r * d);
    mu = clamp(mu, -1.0, 1.0);
}

// Transmittance from (r, mu) to the top of the atmosphere. Zero if the ray hits the ground.
float3 TodTransmittanceToTop(float r, float mu)
{
    float2 uv = TodTransmittanceRMuToUv(min(r, TOD_TOP), mu);
    float3 t = _TOD_TransmittanceLut.SampleLevel(tod_linear_clamp_sampler, uv, 0).rgb;
    return TodRayHitsGround(r, mu) ? 0.0 : t;
}

// Same, for any viewer: one outside the atmosphere is first carried along the ray to where
// it enters. A ray that misses the atmosphere entirely is fully transmitted.
float3 TodTransmittanceToSpace(float r, float mu)
{
    float3 p = float3(0.0, 0.0, r);
    float3 dir = float3(TodSafeSqrt(1.0 - mu * mu), 0.0, mu);
    bool enters = TodMoveToTop(p, dir);
    float rEntry = length(p);
    float3 t = TodTransmittanceToTop(rEntry, dot(p / rEntry, dir));
    return enters ? t : 1.0;
}

// ---- multiple-scattering LUT (Hillaire 2020, 5.5.2) ---------------------------------------

float2 TodMultiScatUv(float r, float sunMu)
{
    float2 uv = float2(sunMu * 0.5 + 0.5, (r - TOD_BOTTOM) / (TOD_TOP - TOD_BOTTOM));
    return float2(TodUnitToSubUv(saturate(uv.x), _TOD_MultiScatSize.x),
                  TodUnitToSubUv(saturate(uv.y), _TOD_MultiScatSize.y));
}

float3 TodMultiScattering(float r, float sunMu)
{
    return _TOD_MultiScatLut.SampleLevel(tod_linear_clamp_sampler, TodMultiScatUv(r, sunMu), 0).rgb;
}

// ---- sky-view LUT (Hillaire 2020, 5.3) ---------------------------------------------------
//
// Latitude/longitude around the observer's up. v in [0, 0.5) is sky, [0.5, 1] is ground,
// with texels packed toward the horizon (u^2 mapping) where the sky changes fastest. u is
// the azimuth from the light, packed toward it. The horizon angle is the true one for the
// observer's height, so the horizon dips below 0 deg as the observer climbs.

// beta is the angle from the nadir up to the horizon, for the height the LUT was built at.
// The CPU computes it once per build (_TOD_SkyViewSize.w, double precision) so no pixel pays
// for the acos.
void TodSkyViewHorizon(out float zenithHorizonAngle, out float beta)
{
    beta = _TOD_SkyViewSize.w;
    zenithHorizonAngle = TOD_PI - beta;
}

float TodViewZenithAngle(float viewZenithCos) { return acos(clamp(viewZenithCos, -1.0, 1.0)); }

// Cosine of the azimuth between a view direction and a light, both projected on the
// observer's horizontal plane: the u axis of the sky-view and aerial-perspective LUTs.
float TodLightViewCos(float3 v, float3 lightDir, float3 up, float viewZenithCos)
{
    float3 vh = v - up * viewZenithCos;
    float3 lh = lightDir - up * dot(lightDir, up);
    float denom = sqrt(dot(vh, vh) * dot(lh, lh));
    return denom > 1e-8 ? clamp(dot(vh, lh) / denom, -1.0, 1.0) : 1.0;
}

// The mapping in [0,1]^2, independent of texture size: the aerial-perspective LUT uses the
// same layout at its own resolution. `zenith` is the view zenith angle (TodViewZenithAngle),
// so callers looking up several LUTs for one direction pay for one acos.
float2 TodSkyViewParamsToUnit(float zenith, float lightViewCos)
{
    float zh, beta;
    TodSkyViewHorizon(zh, beta);

    float2 uv;
    if (zenith <= zh)
    {
        float c = 1.0 - sqrt(saturate(1.0 - zenith / zh));
        uv.y = c * 0.5;
    }
    else
    {
        float c = sqrt(saturate((zenith - zh) / beta));
        uv.y = c * 0.5 + 0.5;
    }
    uv.x = sqrt(saturate(-lightViewCos * 0.5 + 0.5));
    return uv;
}

float2 TodSkyViewParamsToUv(float zenith, float lightViewCos)
{
    float2 uv = TodSkyViewParamsToUnit(zenith, lightViewCos);
    return float2(TodUnitToSubUv(uv.x, _TOD_SkyViewSize.x), TodUnitToSubUv(uv.y, _TOD_SkyViewSize.y));
}

void TodSkyViewUnitToParams(float2 uv, out float viewZenithCos, out float lightViewCos)
{
    float zh, beta;
    TodSkyViewHorizon(zh, beta);

    float zenith;
    if (uv.y < 0.5)
    {
        float c = 1.0 - 2.0 * uv.y;
        zenith = zh * (1.0 - c * c);
    }
    else
    {
        float c = 2.0 * uv.y - 1.0;
        zenith = zh + beta * c * c;
    }
    viewZenithCos = cos(zenith);
    float x = uv.x * uv.x;
    lightViewCos = -(2.0 * x - 1.0);
}

void TodSkyViewUvToParams(float2 uv, out float viewZenithCos, out float lightViewCos)
{
    uv = float2(TodSubUvToUnit(uv.x, _TOD_SkyViewSize.x), TodSubUvToUnit(uv.y, _TOD_SkyViewSize.y));
    TodSkyViewUnitToParams(uv, viewZenithCos, lightViewCos);
}

// Distance (km) an aerial-perspective column covers. For a direction that meets the ground it
// is the distance to the ground, so a surface on the ground always reads its column's last
// slice and neighbouring columns agree; measured in plain km instead, a column just steeper
// than a pixel had already reached the ground while one just shallower had not yet entered the
// haze, and blending them drew rings on hazy ground seen from altitude. Other directions use the
// fixed far distance. Shared by the LUT build and the lookup so both always agree.
float TodAerialDistanceScale(float viewZenithCos)
{
    float h = max(_TOD_SkyViewSize.z, 5e-4);
    float r = TOD_BOTTOM + h;
    float disc = r * r * viewZenithCos * viewZenithCos - h * (2.0 * TOD_BOTTOM + h); // stable r^2 - R^2
    float tBottom = (viewZenithCos < 0.0 && disc >= 0.0) ? -r * viewZenithCos - sqrt(disc) : -1.0;
    return tBottom > 0.0 ? min(tBottom, _TOD_AerialSize.w) : _TOD_AerialSize.w;
}

// Unit vectors for a sky-view parameter pair, in the frame the LUTs are built in: observer up
// along +z, the light at azimuth 0 (in the x-z plane).
float3 TodSkyViewDirection(float viewZenithCos, float lightViewCos)
{
    float viewSin = TodSafeSqrt(1.0 - viewZenithCos * viewZenithCos);
    float lightViewSin = TodSafeSqrt(1.0 - lightViewCos * lightViewCos);
    return float3(viewSin * lightViewCos, viewSin * lightViewSin, viewZenithCos);
}

#endif
