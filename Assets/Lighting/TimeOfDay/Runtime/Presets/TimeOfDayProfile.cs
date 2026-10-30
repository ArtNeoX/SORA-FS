using System;
using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// The look: what the atmosphere is made of and how bright the sun and moon are. Shared by
    /// all quality tiers, so Low and High show the same sky, just computed more or less finely.
    [CreateAssetMenu(menuName = "CirrusFS/Time of Day/Profile", fileName = "TimeOfDayProfile")]
    public class TimeOfDayProfile : ScriptableObject
    {
        public AtmosphereParameters atmosphere = AtmosphereParameters.Earth;
        public SunMoonSettings sunMoon = new SunMoonSettings();
        public SkySettings sky = new SkySettings();
        public FogSettings fog = new FogSettings();
        public AmbientSettings ambient = new AmbientSettings();
        public ExposureSettings exposure = new ExposureSettings();

        [NonSerialized] int version;

        /// Changes whenever the asset is edited, so running systems can pick edits up live.
        public int Version => version;

        void OnValidate() => version++;
    }

    [Serializable]
    public class SunMoonSettings
    {
        [Tooltip("URP intensity of the sun outside the atmosphere. This sets the scene's light scale: " +
                 "a clear noon at sea level delivers ~80% of it. Exposure brings it back to display range.")]
        [Min(0f)] public float sunIntensity = 3f;

        [Tooltip("URP intensity of a full moon outside the atmosphere. Physically ~2.5e-6 of the sun; " +
                 "this is deliberately lifted so moonlit scenes stay readable (as X-Plane does).")]
        [Min(0f)] public float moonIntensity = 0.03f;

        [Tooltip("Tint of moonlight, linear. Moonlight is really slightly warmer than sunlight; the blue " +
                 "is the eye's night (Purkinje) shift, which is what people expect a moonlit scene to look like.")]
        public Color moonTint = new Color(0.72f, 0.82f, 1.0f, 1f);

        [Header("Sun -> moon handoff")]
        [Tooltip("Sun elevation below the visible horizon, degrees, where the moon takes over as the " +
                 "single shadow-casting light. The sun has no direct light left there, so nothing pops.")]
        [Range(-12f, -1f)] public float handoffElevation = -6f;

        [Tooltip("Degrees past the handoff over which moonlight and its shadows fade in.")]
        [Range(0.5f, 6f)] public float handoffFade = 3f;

        [Range(0f, 1f)] public float moonShadowStrength = 0.8f;
    }

    [Serializable]
    public class AmbientSettings
    {
        [Tooltip("Multiplier on the sky's ambient light. 1 is physically consistent with the sun and sky.")]
        [Min(0f)] public float intensity = 1f;
        [Tooltip("Seconds over which the ambient eases to a new sky. Hides the steps between sky rebuilds.")]
        [Range(0f, 5f)] public float blendSeconds = 0.75f;
        [Tooltip("Multiplier on sky reflections.")]
        [Min(0f)] public float reflectionIntensity = 1f;
    }

    [Serializable]
    public class ExposureSettings
    {
        [Tooltip("Meter the rendered image. Off: expose from the lighting alone (sun and ambient), which ignores " +
                 "what is actually in view (a dark hangar, a bright cloud deck).")]
        public bool useMeter = true;

        [Tooltip("Displayed brightness of the scene's average in daylight (0.18 = mid-grey, what ACES expects).")]
        [Range(0.05f, 0.5f)] public float key = 0.18f;
        [Tooltip("Same at night. Lower than the day key so a moonlit scene reads as night, not as a dull day.")]
        [Range(0.01f, 0.3f)] public float nightKey = 0.05f;
        [Tooltip("Metered scene log2 luminance at and above which the day key applies (clear noon is ~-1, " +
                 "the sun 5 deg up ~-3.5).")]
        public float dayLog2Luminance = -5f;
        [Tooltip("Metered scene log2 luminance at and below which the night key applies (full moonlight ~-11).")]
        public float nightLog2Luminance = -10f;

        [Tooltip("EV added to the metered exposure. Positive: brighter.")]
        [Range(-4f, 4f)] public float compensation = 0f;
        [Tooltip("EV added while inside the cockpit. Negative keeps the outside from blowing out when the dark " +
                 "panel fills the meter.")]
        [Range(-4f, 4f)] public float cockpitBias = -0.7f;
        [Tooltip("EV added in external views.")]
        [Range(-4f, 4f)] public float externalBias = 0f;

        [Tooltip("Limits on log2 of the pre-exposure. The lower limit stops snow and cloud tops going grey, the " +
                 "upper one stops a moonless night being lifted into a grey murk.")]
        public float minExposure = -4f;
        public float maxExposure = 7f;

        [Tooltip("Adaptation rate when the scene gets brighter, 1/s (the eye closes down fast).")]
        [Range(0.1f, 20f)] public float brightenSpeed = 3f;
        [Tooltip("Adaptation rate when the scene gets darker, 1/s (and opens up slowly).")]
        [Range(0.05f, 20f)] public float darkenSpeed = 1f;

        [Tooltip("Share of the meter weight given to the centre of the view, 0..1.")]
        [Range(0f, 1f)] public float centerWeight = 0.6f;
    }

    [Serializable]
    public class FogSettings
    {
        [Tooltip("Apply aerial perspective (distance haze / fog) to the scene. The sky always includes it.")]
        public bool aerialPerspective = true;
        [Range(0.5f, 1f), Tooltip("Fraction of weather-haze extinction that is scattering. ~0.9 wet haze (white), ~0.7 smoke or dust (greyer).")]
        public float hazeAlbedo = 0.9f;
        [Range(0f, 0.95f), Tooltip("Haze phase asymmetry g. Lower: more light scattered sideways, a brighter, whiter hazy horizon away from the sun. Background aerosol uses 0.8.")]
        public float hazeAnisotropy = 0.65f;
        [Min(10f), Tooltip("Thickness of the fade at the top of the haze layer, metres. Smaller: a sharper haze top seen from above.")]
        public float hazeTransition = 600f;
        [Range(0.005f, 0.5f), Tooltip("Rebuild the atmosphere when visibility has changed by this fraction. Each rebuild is spread over several frames.")]
        public float rebuildFraction = 0.03f;
    }

    [Serializable]
    public class SkySettings
    {
        [Header("Sun disc")]
        [Tooltip("Multiplier on the sun's true angular size (0.53 deg). Above 1 reads as glare.")]
        [Range(0.5f, 4f)] public float sunDiskScale = 1f;
        [Tooltip("Limb darkening: how much darker the disc's edge is than its centre.")]
        [Range(0f, 1f)] public float sunLimbDarkening = 0.6f;
        [Tooltip("Cap on the disc's radiance. The true value overflows the 32-bit HDR buffer; " +
                 "this keeps bloom and exposure finite while staying far brighter than anything else.")]
        [Min(1f)] public float sunDiskMaxRadiance = 30000f;

        [Header("Moon disc")]
        [Range(0.5f, 4f)] public float moonDiskScale = 1f;
        [Tooltip("Earthshine on the dark part of the moon, relative to full sunlight on it (lifted, as moonlight is).")]
        [Range(0f, 0.05f)] public float moonEarthshine = 0.004f;

        [Header("Stars")]
        public bool stars = true;
        [Tooltip("Radiance of the brightest stars. Fainter ones follow a power law down to 1/1000 of this.")]
        [Min(0f)] public float starBrightness = 0.3f;
        [Tooltip("Fraction of sky cells that hold a star (~16k stars over the sphere at 0.04).")]
        [Range(0f, 0.2f)] public float starDensity = 0.04f;
        [Tooltip("Sky luminance above which no star shows, however bright (keeps them out of daylight).")]
        [Min(1e-5f)] public float starFadeLuminance = 0.05f;
        [Tooltip("Star-to-sky contrast a star needs to be seen. Higher: fewer stars in moonlight and twilight.")]
        [Range(0.01f, 2f)] public float starContrast = 0.15f;
        [Range(0f, 1f)] public float starTwinkle = 0.3f;

        [Header("Night sky")]
        [Tooltip("Airglow: the faint light of the upper atmosphere that keeps a moonless sky from going black.")]
        public Color nightSkyRadiance = new Color(0.0006f, 0.0009f, 0.0018f, 1f);
    }
}
