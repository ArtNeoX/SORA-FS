using UnityEngine;

namespace CirrusFS.Flight
{
    /// Drives the looping jet sample from the throttle lever: pitch ramps across
    /// minPitch..maxPitch and volume across idleVolume..1, both eased so the turbine
    /// spools rather than snapping.
    [RequireComponent(typeof(AudioSource))]
    public class EngineAudio : MonoBehaviour
    {
        public AircraftController aircraft;

        [Header("Pitch")]
        public float minPitch = 0.1f;
        public float maxPitch = 1f;
        [Tooltip("How fast the turbine revs up and down, 1/s.")]
        public float pitchSmoothSpeed = 8f;

        [Header("Volume")]
        public float idleVolume = 0.3f;
        public float throttleVolume = 0.7f;

        AudioSource source;

        void Awake()
        {
            source = GetComponent<AudioSource>();
            if (!aircraft) aircraft = GetComponentInParent<AircraftController>();

            source.loop = true;
            source.pitch = minPitch;
            source.volume = idleVolume;
        }

        void OnEnable()
        {
            if (source.clip && !source.isPlaying) source.Play();
        }

        void Update()
        {
            if (!aircraft) return;

            float throttle = Mathf.Clamp01(aircraft.Throttle);
            float targetPitch = minPitch + (maxPitch - minPitch) * throttle;

            source.pitch += (targetPitch - source.pitch) * Mathf.Clamp01(Time.deltaTime * pitchSmoothSpeed);
            source.volume = idleVolume + throttleVolume * throttle;
        }
    }
}
