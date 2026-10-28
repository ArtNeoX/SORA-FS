using UnityEngine;

namespace CirrusFS.Geo
{
    /// Keeps the sun fixed relative to the Earth and fades the sky to black with altitude.
    ///
    /// Sun: the Earth does not rotate, so the sun is a fixed ECEF direction. At every spawn the
    /// light's authored rotation is re-applied in the new local frame (so the sun stands where
    /// the scene author put it, as seen from the spawn point); after that the floating origin
    /// carries the light through every re-centre like any other root, which keeps it fixed in
    /// ECEF. Fly far enough and the sun sets; from space there is a terminator.
    ///
    /// Sky: the procedural skybox is drawn around Unity's axes and knows nothing about
    /// altitude, so above the atmosphere it would still be a blue dome. Exposure and
    /// atmosphere thickness are faded out between `fadeStart` and `fadeEnd`. The material is
    /// cloned at runtime so the asset on disk is never modified.
    [DefaultExecutionOrder(250)]
    [RequireComponent(typeof(Light))]
    public class GeoSky : MonoBehaviour
    {
        [Tooltip("Camera whose height drives the sky fade. Defaults to Camera.main.")]
        public Camera viewCamera;

        [Tooltip("Height above the ellipsoid where the sky starts to darken, metres.")]
        public float fadeStart = 15000f;

        [Tooltip("Height where the sky is fully black, metres.")]
        public float fadeEnd = 150000f;

        [Range(0f, 1f)] public float spaceExposure = 0.02f;

        Quaternion authoredRotation;
        Material skyInstance;
        Material skyAsset;
        float baseExposure = 1f;
        float baseThickness = 1f;

        static readonly int ExposureId = Shader.PropertyToID("_Exposure");
        static readonly int ThicknessId = Shader.PropertyToID("_AtmosphereThickness");

        void Awake()
        {
            authoredRotation = transform.rotation;
        }

        void OnEnable()
        {
            GeoOrigin.OriginReset += HandleOriginReset;

            skyAsset = RenderSettings.skybox;
            if (skyAsset != null)
            {
                skyInstance = new Material(skyAsset) { name = skyAsset.name + " (runtime)" };
                if (skyInstance.HasProperty(ExposureId)) baseExposure = skyInstance.GetFloat(ExposureId);
                if (skyInstance.HasProperty(ThicknessId)) baseThickness = skyInstance.GetFloat(ThicknessId);
                RenderSettings.skybox = skyInstance;
            }
        }

        void OnDisable()
        {
            GeoOrigin.OriginReset -= HandleOriginReset;

            if (skyInstance != null)
            {
                if (RenderSettings.skybox == skyInstance) RenderSettings.skybox = skyAsset;
                Destroy(skyInstance);
                skyInstance = null;
            }
        }

        /// A spawn has no continuity with the previous frame: put the sun back where it was
        /// authored, relative to the new local horizon.
        void HandleOriginReset() => transform.rotation = authoredRotation;

        void LateUpdate()
        {
            if (skyInstance == null || !GeoOrigin.IsSet) return;

            Camera cam = viewCamera != null ? viewCamera : Camera.main;
            if (cam == null) return;

            float h = (float)GeoOrigin.HeightAt(cam.transform.position);
            float t = Mathf.InverseLerp(Mathf.Log(fadeStart), Mathf.Log(fadeEnd), Mathf.Log(Mathf.Max(h, 1f)));

            if (skyInstance.HasProperty(ExposureId))
                skyInstance.SetFloat(ExposureId, Mathf.Lerp(baseExposure, spaceExposure, t));
            if (skyInstance.HasProperty(ThicknessId))
                skyInstance.SetFloat(ThicknessId, Mathf.Lerp(baseThickness, 0.1f, t));
        }
    }
}
