using System;
using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// Physical description of the atmosphere: a spherical shell with exponential Rayleigh and
    /// Mie layers and a tent-shaped ozone layer. Defaults are Earth's, as used by Hillaire 2020
    /// ("A Scalable and Production Ready Sky and Atmosphere Rendering Technique") and Bruneton.
    ///
    /// Coefficients are per metre. The shell is a sphere of the WGS84 equatorial radius; the
    /// ellipsoid's 21 km of flattening matters to geodesy, not to the colour of the sky.
    [Serializable]
    public struct AtmosphereParameters
    {
        [Tooltip("Planet radius at sea level, metres.")]
        public float bottomRadius;

        [Tooltip("Top of the atmosphere above sea level, metres.")]
        public float thickness;

        [Header("Rayleigh (air molecules)")]
        [Tooltip("Scattering at sea level, 1/m, linear RGB (680/550/440 nm).")]
        public Vector3 rayleighScattering;
        public float rayleighScaleHeight;

        [Header("Mie (aerosols, haze)")]
        [Tooltip("Scattering at sea level, 1/m. Weather/visibility scales this.")]
        public float mieScattering;
        [Tooltip("Absorption at sea level, 1/m. Extinction = scattering + absorption.")]
        public float mieAbsorption;
        public float mieScaleHeight;
        [Range(0f, 0.99f), Tooltip("Henyey-Greenstein / Cornette-Shanks asymmetry: the forward glow around the sun.")]
        public float mieAnisotropy;

        [Header("Ozone (absorbs only; keeps twilight blue)")]
        public Vector3 ozoneAbsorption;
        public float ozoneCenterHeight;
        public float ozoneHalfWidth;

        [Header("Weather haze (set from visibility at runtime)")]
        [Tooltip("Extinction of the weather haze layer, 1/m. Normally derived from visibility by TimeOfDaySystem.")]
        public float hazeExtinction;
        [Range(0.5f, 1f), Tooltip("Fraction of haze extinction that is scattering (the rest is absorbed). ~0.9 for wet haze, lower for smoke.")]
        public float hazeAlbedo;
        [Range(0f, 0.95f), Tooltip("Haze phase asymmetry. Haze droplets scatter more sideways than background aerosol, which keeps a hazy horizon bright under a low sun.")]
        public float hazeAnisotropy;
        [Tooltip("Top of the haze layer above sea level, metres (the boundary-layer inversion).")]
        public float hazeTopHeight;
        [Tooltip("Thickness of the fade at the top of the haze layer, metres.")]
        public float hazeTransition;

        [Header("Ground")]
        [Tooltip("Average albedo of the ground below, linear RGB. Lights the sky from beneath.")]
        public Color groundAlbedo;

        public float TopRadius => bottomRadius + thickness;
        public float MieExtinction => mieScattering + mieAbsorption;

        public static AtmosphereParameters Earth => new AtmosphereParameters
        {
            bottomRadius = 6378137f,
            thickness = 100000f,
            rayleighScattering = new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f),
            rayleighScaleHeight = 8000f,
            mieScattering = 3.996e-6f,
            mieAbsorption = 0.444e-6f,
            mieScaleHeight = 1200f,
            mieAnisotropy = 0.8f,
            ozoneAbsorption = new Vector3(0.650e-6f, 1.881e-6f, 0.085e-6f),
            ozoneCenterHeight = 25000f,
            ozoneHalfWidth = 15000f,
            hazeExtinction = 0f,
            hazeAlbedo = 0.9f,
            hazeAnisotropy = 0.65f,
            hazeTopHeight = 1500f,
            hazeTransition = 600f,
            groundAlbedo = new Color(0.1f, 0.1f, 0.1f, 1f),
        };

        /// Clamps values that would break the integrators (zero heights, negative coefficients).
        public void Sanitize()
        {
            bottomRadius = Mathf.Max(1000f, bottomRadius);
            thickness = Mathf.Max(1000f, thickness);
            rayleighScattering = Vector3.Max(rayleighScattering, Vector3.zero);
            rayleighScaleHeight = Mathf.Max(10f, rayleighScaleHeight);
            mieScattering = Mathf.Max(0f, mieScattering);
            mieAbsorption = Mathf.Max(0f, mieAbsorption);
            mieScaleHeight = Mathf.Max(10f, mieScaleHeight);
            ozoneAbsorption = Vector3.Max(ozoneAbsorption, Vector3.zero);
            ozoneHalfWidth = Mathf.Max(10f, ozoneHalfWidth);
            hazeExtinction = Mathf.Max(0f, hazeExtinction);
            hazeAlbedo = Mathf.Clamp(hazeAlbedo, 0.5f, 1f);
            hazeAnisotropy = Mathf.Clamp(hazeAnisotropy, 0f, 0.95f);
            hazeTopHeight = Mathf.Max(0f, hazeTopHeight);
            hazeTransition = Mathf.Max(10f, hazeTransition);
        }

        /// Extinction at sea level without weather haze, 1/m, green channel (550 nm): what
        /// sets the clear-air visibility limit.
        public float ClearAirExtinction => rayleighScattering.y + MieExtinction;
    }
}
