using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// Root of the time-of-day prefab. Owns the presets, the weather inputs and the one
    /// AtmosphereModel every subsystem reads; the subsystems are components on this object or
    /// its children.
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(TimeOfDayClock))]
    public class TimeOfDaySystem : MonoBehaviour
    {
        public TimeOfDayProfile profile;
        public TimeOfDayQuality quality;

        [Tooltip("Camera that is the observer. Defaults to Camera.main.")]
        public Camera viewCamera;

        [Header("Weather")]
        [Tooltip("Meteorological visibility at sea level, km (METAR visibility). Sets the density of the haze layer. " +
                 "Clear air alone limits visibility to ~170 km; anything above that means no haze.")]
        [Range(0.1f, 200f)] public float visibilityKm = 40f;
        [Tooltip("Top of the haze layer above sea level, metres: the boundary-layer inversion. Above it the air is clear " +
                 "and the haze is seen as a band on the horizon.")]
        [Range(0f, 6000f)] public float hazeTopMeters = 1500f;

        /// The enabled system. There is one per scene.
        public static TimeOfDaySystem Active { get; private set; }

        public AtmosphereModel Atmosphere { get; private set; }
        public TimeOfDayClock Clock { get; private set; }

        public Camera ViewCamera => viewCamera != null ? viewCamera : Camera.main;

        /// RenderSettings (skybox, sun) belong to the active scene. In the editor a system in an
        /// additively opened scene must not rewrite another scene's lighting settings.
        public bool OwnsRenderSettings =>
            Application.isPlaying || gameObject.scene == UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        /// Visibility the atmosphere is currently built for (it trails visibilityKm by up to the
        /// profile's rebuild fraction).
        public float AppliedVisibilityKm { get; private set; }

        int profileVersion = -1;
        TimeOfDayProfile builtFrom;
        float builtHazeTop = -1f;

        void OnEnable()
        {
            if (Active != null && Active != this)
                Debug.LogWarning($"Two TimeOfDaySystems are enabled; '{name}' takes over from '{Active.name}'.", this);
            Active = this;
            Clock = GetComponent<TimeOfDayClock>();
            SyncAtmosphere(force: true);
            appliedQuality = null;
            SyncPipeline();
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            RuntimePipelineAsset.Restore();
        }

        void Update() => Refresh();

        /// Applies profile and weather changes now rather than at the next Update (tools, tests).
        public void Refresh()
        {
            SyncAtmosphere(force: false);
            SyncPipeline();
        }

        /// Switches quality tier at runtime (settings menu, debug overlay).
        public void SetQuality(TimeOfDayQuality tier)
        {
            if (tier != null) quality = tier;
        }

        TimeOfDayQuality appliedQuality;
        int appliedQualityVersion = -1;

        /// Shadow and grading settings go to a runtime copy of the URP asset (play mode only).
        void SyncPipeline()
        {
            if (!Application.isPlaying || quality == null) return;
            if (appliedQuality == quality && appliedQualityVersion == quality.Version && RuntimePipelineAsset.Installed) return;
            RuntimePipelineAsset.Apply(quality);
            appliedQuality = quality;
            appliedQualityVersion = quality.Version;
        }

        /// Weather entry point, e.g. a METAR's visibility (and, if known, the inversion height).
        /// Takes effect at the next rebuild, which the renderer spreads over a few frames.
        public void SetVisibility(float km, float hazeTop = -1f)
        {
            visibilityKm = Mathf.Clamp(km, 0.1f, 200f);
            if (hazeTop >= 0f) hazeTopMeters = hazeTop;
        }

        /// Rebuilds the model's parameters when the profile changes or the weather has moved
        /// far enough to be worth a rebuild.
        void SyncAtmosphere(bool force)
        {
            AtmosphereParameters clear = profile != null ? profile.atmosphere : AtmosphereParameters.Earth;
            FogSettings fog = profile != null ? profile.fog : DefaultFog;
            int version = profile != null ? profile.Version : 0;

            bool profileChanged = builtFrom != profile || profileVersion != version;
            float visibilityChange = AppliedVisibilityKm > 0f
                ? Mathf.Abs(Mathf.Log(visibilityKm / AppliedVisibilityKm))
                : float.MaxValue;
            bool weatherChanged = visibilityChange > fog.rebuildFraction ||
                                  Mathf.Abs(hazeTopMeters - builtHazeTop) > 25f;

            if (Atmosphere != null && !force && !profileChanged && !weatherChanged) return;

            AtmosphereParameters p = clear;
            p.hazeAlbedo = fog.hazeAlbedo;
            p.hazeAnisotropy = fog.hazeAnisotropy;
            p.hazeTransition = fog.hazeTransition;
            p.hazeTopHeight = hazeTopMeters;
            p.hazeExtinction = AtmosphereModel.HazeExtinctionForVisibility(clear, visibilityKm);

            if (Atmosphere == null) Atmosphere = new AtmosphereModel(p);
            else Atmosphere.SetParameters(p);

            builtFrom = profile;
            profileVersion = version;
            builtHazeTop = hazeTopMeters;
            AppliedVisibilityKm = visibilityKm;
        }

        static readonly FogSettings DefaultFog = new FogSettings();
    }
}
