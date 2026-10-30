using System;
using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// The single source of truth for what the atmosphere does to light. Every subsystem -
    /// sun and moon colour, sky, aerial perspective, ambient, reflections - reads from here so
    /// they can never disagree.
    ///
    /// This half is the CPU side: exact ray-marched transmittance, used for the handful of
    /// rays the CPU needs each frame (sun, moon). The GPU look-up tables are built from the
    /// same parameters by the LUT baker and must match these results.
    ///
    /// Geometry: r is the distance from the planet centre, mu the cosine of the ray's zenith
    /// angle at r. A ray that meets the ground has zero transmittance.
    public sealed class AtmosphereModel
    {
        AtmosphereParameters p;

        /// Bumped whenever the parameters change; caches compare against it.
        public int Version { get; private set; }

        public AtmosphereParameters Parameters => p;

        public AtmosphereModel(AtmosphereParameters parameters) => SetParameters(parameters);

        public void SetParameters(AtmosphereParameters parameters)
        {
            parameters.Sanitize();
            p = parameters;
            Version++;
        }

        /// Ray-march steps for CPU transmittance. 64 steps spaced quadratically (dense near the
        /// observer, where the air is thickest) agree with a 20000-step reference to within
        /// 0.2% from zenith to horizon at 0-12 km. Costs ~2 us per ray.
        const int TransmittanceSteps = 64;

        /// Transmittance from altitude `height` (metres above sea level) to the top of the
        /// atmosphere along a ray whose zenith cosine is `mu`. Linear RGB in [0, 1].
        public Vector3 Transmittance(double height, double mu)
        {
            double r = p.bottomRadius + Math.Max(0.0, height);
            mu = Math.Max(-1.0, Math.Min(1.0, mu));

            if (RayHitsGround(r, mu)) return Vector3.zero;

            // Above the atmosphere: carry the viewer along the ray to where it enters.
            double top = p.TopRadius;
            if (r > top)
            {
                double disc = r * r * (mu * mu - 1.0) + top * top;
                if (mu >= 0.0 || disc < 0.0) return Vector3.one; // never enters
                double t = -r * mu - Math.Sqrt(disc);
                double rEntry = top;
                mu = (r * mu + t) / rEntry;
                r = rEntry;
            }

            double length = DistanceToTop(r, mu);
            Vector3 tau = OpticalDepth(r, mu, length, TransmittanceSteps);
            return new Vector3(Mathf.Exp(-tau.x), Mathf.Exp(-tau.y), Mathf.Exp(-tau.z));
        }

        /// Transmittance of a ray grazing the horizon, the darkest a visible body ever gets.
        /// Used to keep a body that is partly below the horizon continuous rather than black.
        public Vector3 HorizonTransmittance(double height)
        {
            double r = p.bottomRadius + Math.Max(0.0, height);
            return Transmittance(height, HorizonMu(r) + 1e-6);
        }

        /// Extinction coefficient (1/m, RGB) at a height above sea level.
        public Vector3 Extinction(double height)
        {
            float h = (float)Math.Max(0.0, height);
            float rayleigh = Mathf.Exp(-h / p.rayleighScaleHeight);
            float mie = Mathf.Exp(-h / p.mieScaleHeight);
            float ozone = Mathf.Max(0f, 1f - Mathf.Abs(h - p.ozoneCenterHeight) / p.ozoneHalfWidth);
            float haze = (float)HazeProfile(h);
            return p.rayleighScattering * rayleigh + Vector3.one * (p.MieExtinction * mie + p.hazeExtinction * haze)
                   + p.ozoneAbsorption * ozone;
        }

        /// Density of the weather haze layer at a height, 0..1: uniform up to the layer top,
        /// smoothly gone across the transition. Matches TodHazeProfile in AtmosphereCommon.hlsl.
        public double HazeProfile(double height)
        {
            double t = (height - (p.hazeTopHeight - 0.5 * p.hazeTransition)) / p.hazeTransition;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return 1.0 - t * t * (3.0 - 2.0 * t);
        }

        /// Contrast threshold of meteorological optical range (ICAO): visibility V is where a
        /// black object's contrast falls to 5%, so extinction = -ln(0.05) / V ~ 3.0 / V.
        public const double VisibilityConstant = 2.9957;

        /// Haze-layer extinction (1/m) that gives a sea-level visibility of `visibilityKm` on top
        /// of the clear air. Zero when the clear air alone already limits visibility (~170 km).
        public static float HazeExtinctionForVisibility(AtmosphereParameters clear, double visibilityKm)
        {
            double total = VisibilityConstant / Math.Max(0.01, visibilityKm) * 0.001;
            return (float)Math.Max(0.0, total - clear.ClearAirExtinction);
        }

        /// Sea-level visibility (km) these parameters produce, the inverse of the above.
        public static double VisibilityKm(AtmosphereParameters p) =>
            VisibilityConstant / (p.ClearAirExtinction + p.hazeExtinction) * 0.001;

        /// Cosine of the zenith angle of the sea-level horizon seen from radius r (negative
        /// above the ground).
        public double HorizonMu(double r)
        {
            double s = p.bottomRadius / Math.Max(r, p.bottomRadius);
            return -Math.Sqrt(Math.Max(0.0, 1.0 - s * s));
        }

        public bool RayHitsGround(double r, double mu) =>
            mu < 0.0 && r * r * (mu * mu - 1.0) + (double)p.bottomRadius * p.bottomRadius >= 0.0;

        public double DistanceToTop(double r, double mu)
        {
            double top = p.TopRadius;
            if (r > top) r = top; // above the atmosphere: nothing left to cross
            double disc = r * r * (mu * mu - 1.0) + top * top;
            return Math.Max(0.0, -r * mu + Math.Sqrt(Math.Max(0.0, disc)));
        }

        Vector3 OpticalDepth(double r, double mu, double length, int steps)
        {
            double sumR = 0, sumM = 0, sumO = 0, sumH = 0;
            double inv = 1.0 / steps;

            for (int i = 0; i < steps; i++)
            {
                double a = i * inv, b = (i + 1) * inv;
                a *= a; b *= b;
                double t = 0.5 * (a + b) * length;
                double dt = (b - a) * length;
                double h = Math.Sqrt(t * t + 2.0 * r * mu * t + r * r) - p.bottomRadius;
                sumR += Math.Exp(-h / p.rayleighScaleHeight) * dt;
                sumM += Math.Exp(-h / p.mieScaleHeight) * dt;
                sumO += Math.Max(0.0, 1.0 - Math.Abs(h - p.ozoneCenterHeight) / p.ozoneHalfWidth) * dt;
                sumH += HazeProfile(h) * dt;
            }

            return p.rayleighScattering * (float)sumR
                   + Vector3.one * (float)(p.MieExtinction * sumM + p.hazeExtinction * sumH)
                   + p.ozoneAbsorption * (float)sumO;
        }
    }
}
