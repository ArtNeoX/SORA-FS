using System;
using CirrusFS.Geo;
using Unity.Profiling;
using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    public enum CelestialLight { Sun, Moon }

    /// Everything the other subsystems need to know about the sky this frame.
    public struct CelestialState
    {
        public double JulianDate;
        /// Greenwich apparent sidereal time, degrees: turns ECEF into the celestial frame.
        public double Gast;
        public ObserverFrame Observer;

        /// Unit vectors toward the bodies, Unity space, apparent (refracted).
        public Vector3 SunDirection, MoonDirection;
        /// Same, ECEF. The moon's is topocentric (from the observer, not the Earth's centre).
        public Double3 SunEcef, MoonEcef;

        /// Apparent elevations above the observer's horizontal plane, degrees.
        public double SunElevation, MoonElevation;
        /// Elevation of the visible sea-level horizon, degrees (negative when above h = 0).
        public double HorizonElevation;
        public double SunAboveHorizon => SunElevation - HorizonElevation;

        public double MoonPhaseAngle;         // 0 = full, 180 = new
        public double MoonIlluminatedFraction;
        public double SunAngularRadius, MoonAngularRadius; // degrees

        /// Atmospheric transmittance toward each body (RGB, includes the part of the disc
        /// below the horizon).
        public Vector3 SunTransmittance, MoonTransmittance;

        /// Light each body delivers at the observer, linear RGB in URP intensity units.
        public Vector3 SunIlluminance, MoonIlluminance;

        /// Light each body delivers above the atmosphere (no transmittance, no handoff fade),
        /// URP intensity units. What the sky is lit with.
        public Vector3 SunExoIlluminance, MoonExoIlluminance;

        public CelestialLight ShadowCaster;
        /// 0..1 fade of moonlight after the handoff.
        public float MoonFade;
    }

    /// Places the sun and moon from date, time and position, colours them by the atmosphere,
    /// and hands the single shadow-casting directional light from sun to moon at dusk.
    ///
    /// Runs after the camera has moved and after any floating-origin re-centre (which rotates
    /// the Unity frame), so the light directions are always in the current frame.
    [ExecuteAlways]
    [DefaultExecutionOrder(300)]
    public class SunMoonController : MonoBehaviour
    {
        public TimeOfDaySystem system;
        public Light sunLight;
        public Light moonLight;

        CelestialState state;
        public ref readonly CelestialState State => ref state;

        static readonly ProfilerMarker Marker = new ProfilerMarker("TimeOfDay.SunMoon");

        /// Below this the light is treated as off (no shadow map rendered).
        const float OffThreshold = 1e-5f;

        void OnEnable()
        {
            if (system == null) system = GetComponentInParent<TimeOfDaySystem>();
            PrepareLight(sunLight);
            PrepareLight(moonLight);
        }

        void LateUpdate() => Refresh();

        /// Recomputes and applies now, rather than at the next LateUpdate. For tools that move
        /// the clock and need the result in the same frame (scrubbing, debug overlay, tests).
        public void Refresh()
        {
            if (system == null || system.Atmosphere == null || system.Clock == null) return;
            using (Marker.Auto())
            {
                Compute(system.Clock.JulianDate);
                ApplyLights();
            }
        }

        void Compute(double jd)
        {
            Camera cam = system.ViewCamera;
            Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
            TimeOfDayClock clock = system.Clock;

            ObserverFrame obs = ObserverFrame.GeoOriginLive
                ? ObserverFrame.FromOrigin(eye)
                : ObserverFrame.FromLocation(clock.fallbackLatitude, clock.fallbackLongitude, eye);

            SunEphemeris sun = SolarPosition.Compute(jd);
            MoonEphemeris moon = LunarPosition.Compute(jd);

            Double3 moonTopo = moon.EcefPosition - obs.Ecef;
            double moonDistance = moonTopo.Magnitude;

            Double3 sunDir = Refraction.Apply(sun.EcefDirection, obs.Up, obs.Height);
            Double3 moonDir = Refraction.Apply(moonTopo / moonDistance, obs.Up, obs.Height);

            state.JulianDate = jd;
            state.Gast = sun.Gast;
            state.Observer = obs;
            state.SunEcef = sunDir;
            state.MoonEcef = moonDir;
            state.SunDirection = obs.ToUnity(sunDir);
            state.MoonDirection = obs.ToUnity(moonDir);
            state.SunElevation = obs.ElevationOf(sunDir);
            state.MoonElevation = obs.ElevationOf(moonDir);
            state.HorizonElevation = obs.HorizonElevation;
            state.SunAngularRadius = SolarPosition.AngularRadiusAt1Au / sun.DistanceAu;
            state.MoonAngularRadius = Math.Asin(LunarPosition.RadiusKm * 1000.0 / moonDistance) * GeoMath.Rad2Deg;
            state.MoonPhaseAngle = LunarPosition.PhaseAngle(sun, moon);
            state.MoonIlluminatedFraction = LunarPosition.IlluminatedFraction(state.MoonPhaseAngle);

            AtmosphereModel atmosphere = system.Atmosphere;
            state.SunTransmittance = DiscTransmittance(atmosphere, obs.Height, state.SunElevation, state.SunAngularRadius);
            state.MoonTransmittance = DiscTransmittance(atmosphere, obs.Height, state.MoonElevation, state.MoonAngularRadius);

            SunMoonSettings s = system.profile != null ? system.profile.sunMoon : Defaults;

            // Handoff: once the sun is this far below the visible horizon it has no direct light
            // left, so switching the shadow caster there cannot be seen. Moonlight then fades in
            // from zero, so the moon's arrival cannot be seen either.
            double handoff = Math.Min(s.handoffElevation, -1.0);
            double past = handoff - state.SunAboveHorizon;
            state.ShadowCaster = past > 0.0 ? CelestialLight.Moon : CelestialLight.Sun;
            state.MoonFade = Smoothstep(0f, s.handoffFade, (float)past);

            state.SunExoIlluminance = Vector3.one * s.sunIntensity;
            state.SunIlluminance = state.SunTransmittance * s.sunIntensity;

            float moonExo = s.moonIntensity * (float)LunarPosition.RelativeBrightness(state.MoonPhaseAngle);
            Vector3 tint = new Vector3(s.moonTint.r, s.moonTint.g, s.moonTint.b);
            state.MoonExoIlluminance = tint * moonExo;
            state.MoonIlluminance = Vector3.Scale(state.MoonTransmittance, state.MoonExoIlluminance) * state.MoonFade;
        }

        /// Transmittance toward a disc of angular radius `radius` whose centre is at
        /// `elevation`, including how much of it is still above the visible horizon. Smooth
        /// through sunset: the colour comes from the grazing ray, the dimming from the part of
        /// the disc that has gone.
        static Vector3 DiscTransmittance(AtmosphereModel atmosphere, double height, double elevation, double radius)
        {
            double r = atmosphere.Parameters.bottomRadius + Math.Max(0.0, height);
            double horizonMu = atmosphere.HorizonMu(r);
            double horizonElevation = Math.Asin(horizonMu) * GeoMath.Rad2Deg;

            double d = (elevation - horizonElevation) / Math.Max(radius, 1e-4);
            if (d <= -1.0) return Vector3.zero;

            double mu = Math.Sin(elevation * GeoMath.Deg2Rad);
            Vector3 t = mu > horizonMu + 1e-6
                ? atmosphere.Transmittance(height, mu)
                : atmosphere.HorizonTransmittance(height);

            if (d >= 1.0) return t;
            // Area of a circle above a chord at signed distance d (in radii) from its centre.
            double visible = (Math.Acos(-d) + d * Math.Sqrt(1.0 - d * d)) / Math.PI;
            return t * (float)visible;
        }

        void ApplyLights()
        {
            Light active = state.ShadowCaster == CelestialLight.Sun ? sunLight : moonLight;
            Light inactive = active == sunLight ? moonLight : sunLight;

            Vector3 illuminance = state.ShadowCaster == CelestialLight.Sun ? state.SunIlluminance : state.MoonIlluminance;
            Vector3 direction = state.ShadowCaster == CelestialLight.Sun ? state.SunDirection : state.MoonDirection;

            if (active != null)
            {
                float intensity = Mathf.Max(illuminance.x, Mathf.Max(illuminance.y, illuminance.z));
                bool lit = intensity > OffThreshold;

                if (!active.enabled) active.enabled = true;
                SetRotation(active.transform, direction, state.Observer.UpUnity);

                // Unity light colours are sRGB-encoded; URP linearises them, so encode here.
                Color color = lit
                    ? new Color(illuminance.x / intensity, illuminance.y / intensity, illuminance.z / intensity).gamma
                    : Color.black;
                if (active.color != color) active.color = color;
                // Pre-exposed (see ExposureController); `lit` is decided in scene units.
                float exposed = ExposureController.Expose(intensity);
                if (active.intensity != exposed) active.intensity = exposed;

                LightShadows shadows = lit && system.quality != null ? system.quality.shadowType
                                     : lit ? LightShadows.Soft : LightShadows.None;
                if (active.shadows != shadows) active.shadows = shadows;

                float strength = active == moonLight
                    ? (system.profile != null ? system.profile.sunMoon.moonShadowStrength : Defaults.moonShadowStrength) * state.MoonFade
                    : 1f;
                if (active.shadowStrength != strength) active.shadowStrength = strength;

                if (system.OwnsRenderSettings && RenderSettings.sun != active) RenderSettings.sun = active;
            }

            if (inactive != null && inactive.enabled) inactive.enabled = false;
        }

        static void SetRotation(Transform t, Vector3 towardBody, Vector3 up)
        {
            Vector3 forward = -towardBody;
            if (forward.sqrMagnitude < 1e-8f) return;
            Vector3 hint = Mathf.Abs(Vector3.Dot(forward.normalized, up)) > 0.999f ? Vector3.forward : up;
            Quaternion q = Quaternion.LookRotation(forward, hint);
            if (Quaternion.Angle(t.rotation, q) > 1e-4f) t.rotation = q;
        }

        static void PrepareLight(Light light)
        {
            if (light == null) return;
            light.type = LightType.Directional;
            // Colour comes from the atmosphere; a colour temperature on top would double-tint.
            light.useColorTemperature = false;
        }

        static float Smoothstep(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        static readonly SunMoonSettings Defaults = new SunMoonSettings();
    }
}
