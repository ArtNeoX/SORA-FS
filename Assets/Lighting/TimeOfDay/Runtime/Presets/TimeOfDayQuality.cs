using System;
using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    public enum QualityTier { Low, Medium, High }
    public enum ShadowResolutionTier { _1024 = 1024, _2048 = 2048, _4096 = 4096 }
    public enum GradingLutSize { _32 = 32, _64 = 64 }

    /// The cost: resolution and update cadence of every subsystem. One asset per tier
    /// (Low / Medium / High); the profile decides the look, this decides how finely it is
    /// computed. Fields are added as each subsystem lands.
    [CreateAssetMenu(menuName = "CirrusFS/Time of Day/Quality Tier", fileName = "TimeOfDayQuality")]
    public class TimeOfDayQuality : ScriptableObject
    {
        public QualityTier tier = QualityTier.Medium;

        [Header("Shadows (applied in play mode to a runtime copy of the URP asset)")]
        [Tooltip("Shadow type for the sun or moon, whichever is casting.")]
        public LightShadows shadowType = LightShadows.Soft;
        [Tooltip("Shadow distance, metres. Beyond it nothing casts; at altitude the ground is past it anyway.")]
        [Min(50f)] public float shadowDistance = 3000f;
        [Tooltip("Shadow cascades: 1 or 2 (the budget allows no more).")]
        [Range(1, 2)] public int shadowCascades = 2;
        [Tooltip("Where the first cascade ends, as a fraction of the shadow distance. Small keeps the cockpit and " +
                 "the aircraft sharp.")]
        [Range(0.01f, 0.5f)] public float cascadeSplit = 0.08f;
        [Tooltip("Shadow map size for the sun or moon.")]
        public ShadowResolutionTier shadowResolution = ShadowResolutionTier._2048;

        [Header("Post")]
        [Tooltip("Colour grading LUT size (URP asset). 64 removes the faint banding of 32 in smooth skies.")]
        public GradingLutSize gradingLutSize = GradingLutSize._32;

        [Header("Atmosphere LUTs")]
        [Tooltip("Build LUTs with compute shaders when the GPU supports them; otherwise fragment shaders.")]
        public bool useComputeShaders = true;
        [Tooltip("Sky-view LUT resolution (azimuth x elevation). Texels are packed toward the horizon and the sun.")]
        public Vector2Int skyViewLutSize = new Vector2Int(192, 108);
        [Tooltip("Ray-march samples per sky-view texel: x for short rays (overhead), y for long ones (horizon).")]
        public Vector2Int skyViewSamples = new Vector2Int(16, 32);
        [Tooltip("Rebuild the sky-view LUT when the sun (or moon) has moved this many degrees in elevation.")]
        [Range(0.01f, 2f)] public float skyViewUpdateDegrees = 0.1f;
        [Tooltip("... or when the viewer's height has changed by this fraction.")]
        [Range(0.001f, 0.2f)] public float skyViewUpdateHeight = 0.01f;
        [Tooltip("Cross-fade between consecutive sky-view LUTs, seconds. 0 = switch instantly.")]
        [Range(0f, 2f)] public float skyViewBlendSeconds = 0.25f;
        [Tooltip("Second sky-view LUT lit by the moon (moonlit blue sky at night).")]
        public bool moonlitSky = true;
        [Tooltip("Frames a weather/profile rebuild of the multiple-scattering LUT is spread over.")]
        [Range(1, 32)] public int baseLutRebuildFrames = 8;

        [Header("Aerial perspective")]
        [Tooltip("Aerial-perspective LUT size: azimuth x elevation x distance slices.")]
        public Vector3Int aerialLutSize = new Vector3Int(32, 32, 32);
        [Tooltip("Ray-march steps per aerial-perspective slice (each column is marched once through all slices).")]
        [Range(1, 8)] public int aerialSamples = 3;
        [Tooltip("Moonlit haze at night (a second aerial-perspective LUT).")]
        public bool moonlitAerial = true;

        [Header("Reflections")]
        [Tooltip("Face size of the sky cubemap used for reflections and ambient.")]
        public int reflectionCubeSize = 128;
        [Tooltip("Realtime probe that also renders terrain (time-sliced, one face per frame). Needs Realtime Reflection Probes on in Quality settings.")]
        public bool sceneReflectionProbe;
        public int sceneProbeResolution = 128;
        [Tooltip("Layers the realtime probe renders. Leave the aircraft and cockpit out.")]
        public LayerMask sceneProbeCullingMask = ~0;
        [Tooltip("Re-render the realtime probe when the sun has moved this many degrees...")]
        public float sceneProbeUpdateDegrees = 0.5f;
        [Tooltip("... or the camera has travelled this far, metres.")]
        public float sceneProbeUpdateMeters = 2000f;
        [Tooltip("Never more often than this, seconds.")]
        public float sceneProbeMinInterval = 1f;

        [NonSerialized] int version;
        public int Version => version;
        void OnValidate() => version++;
    }
}
