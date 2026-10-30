using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// Keeps a light's intensity in scene light units under pre-exposure: the Light component's
    /// intensity becomes `intensity` x ExposureController.PreExposure. Put it on every light the
    /// time-of-day system does not drive itself (landing, taxi, strobe, runway, cockpit flood
    /// lights), or they will be ~30x too dim at night and ~2x too bright at noon.
    ///
    /// Set `intensity` (here or from code) instead of Light.intensity.
    [ExecuteAlways]
    [RequireComponent(typeof(Light))]
    [DefaultExecutionOrder(340)]
    public class PreExposedLight : MonoBehaviour
    {
        [Tooltip("Intensity in scene light units (the sun outside the atmosphere is the profile's sunIntensity).")]
        [Min(0f)] public float intensity = 1f;

        Light target;
        int appliedVersion = -1;
        float appliedIntensity = -1f;

        void OnEnable()
        {
            target = GetComponent<Light>();
            appliedVersion = -1;
        }

        void Reset()
        {
            Light l = GetComponent<Light>();
            if (l != null) intensity = l.intensity;
        }

        void LateUpdate()
        {
            if (appliedVersion == ExposureController.Version && appliedIntensity == intensity) return;
            target.intensity = ExposureController.Expose(intensity);
            appliedVersion = ExposureController.Version;
            appliedIntensity = intensity;
        }
    }
}
