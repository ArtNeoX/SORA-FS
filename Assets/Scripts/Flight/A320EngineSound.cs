using System;
using UnityEngine;

namespace CirrusFS.Flight
{
    /// Layered CFM56-5B engine sound for the A320. Three looping samples (idle, mid,
    /// buzzsaw) are crossfaded and pitched from the engines' actual thrust, not the
    /// lever, so the sound follows spool-up/down lag. Each layer has its own volume
    /// and pitch curves over thrust percent (0 = idle thrust, 100 = TOGA).
    public class A320EngineSound : MonoBehaviour
    {
        [Serializable]
        public class Layer
        {
            public string name;
            [Tooltip("Path under a Resources folder, without extension.")]
            public string resourcePath;
            [Tooltip("Volume (0..1) over thrust percent (0..100).")]
            public AnimationCurve volume;
            [Tooltip("Pitch multiplier over thrust percent (0..100).")]
            public AnimationCurve pitch;
            [Range(0f, 1f)] public float gain = 1f;

            [NonSerialized] public AudioSource source;
        }

        public AircraftController aircraft;

        [Header("Thrust")]
        [Tooltip("How fast the sound follows thrust changes, 1/s. Thrust already spools, " +
                 "this just takes the edge off per-frame steps.")]
        public float smoothSpeed = 6f;
        [Tooltip("Read-only: smoothed thrust percent the curves are evaluated at.")]
        [SerializeField] float thrustPercent;

        [Header("Output")]
        [Range(0f, 1f)] public float masterVolume = 1f;
        [Range(0f, 1f)] public float spatialBlend = 0f;
        public float minDistance = 10f;
        public float maxDistance = 2000f;

        // Defaults: idle fades out over 0-50, mid swells to 50 and fades by 100,
        // buzzsaw fades in over 50-100.
        public Layer[] layers =
        {
            new Layer
            {
                name = "Idle", resourcePath = "Sounds/CFM56B/cfm56b_idle",
                volume = Ramp(0f, 1f, 50f, 0f),
                pitch = Ramp(0f, 1f, 50f, 1.25f),
            },
            new Layer
            {
                name = "Mid", resourcePath = "Sounds/CFM56B/cfm56b_mid_",
                volume = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(50f, 1f), new Keyframe(100f, 0f)),
                pitch = Ramp(0f, 0.75f, 100f, 1.25f),
            },
            new Layer
            {
                name = "Buzzsaw", resourcePath = "Sounds/CFM56B/cfm56b_buzzsaw",
                volume = Ramp(50f, 0f, 100f, 1f),
                pitch = Ramp(50f, 0.85f, 100f, 1.1f),
            },
        };

        static AnimationCurve Ramp(float x0, float y0, float x1, float y1) =>
            AnimationCurve.EaseInOut(x0, y0, x1, y1);

        void Awake()
        {
            if (!aircraft) aircraft = GetComponentInParent<AircraftController>();

            foreach (var layer in layers)
            {
                var clip = Resources.Load<AudioClip>(layer.resourcePath);
                if (!clip)
                {
                    Debug.LogWarning($"A320EngineSound: no clip at Resources/{layer.resourcePath}", this);
                    continue;
                }

                var go = new GameObject("Layer_" + layer.name);
                go.transform.SetParent(transform, false);
                var src = go.AddComponent<AudioSource>();
                src.clip = clip;
                src.loop = true;
                src.playOnAwake = false;
                src.volume = 0f;
                // Random start so the loops don't phase against each other.
                src.time = UnityEngine.Random.Range(0f, clip.length);
                layer.source = src;
            }
        }

        void OnEnable()
        {
            foreach (var layer in layers)
                if (layer.source && !layer.source.isPlaying) layer.source.Play();
        }

        void OnDisable()
        {
            foreach (var layer in layers)
                if (layer.source) layer.source.Stop();
        }

        /// Mean engine thrust as 0..100 between idle and max thrust.
        float TargetThrustPercent()
        {
            int n = aircraft.EngineCount;
            if (n == 0) return 0f;

            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                var def = aircraft.config.engines[i];
                float span = def.maxThrust - def.idleThrust;
                if (span > 0f) sum += Mathf.Clamp01((aircraft.EngineThrust(i) - def.idleThrust) / span);
            }
            return sum / n * 100f;
        }

        void Update()
        {
            if (!aircraft) return;

            float target = TargetThrustPercent();
            thrustPercent += (target - thrustPercent) * Mathf.Clamp01(Time.deltaTime * smoothSpeed);

            foreach (var layer in layers)
            {
                var src = layer.source;
                if (!src) continue;

                src.volume = Mathf.Clamp01(layer.volume.Evaluate(thrustPercent)) * layer.gain * masterVolume;
                src.pitch = Mathf.Max(0.01f, layer.pitch.Evaluate(thrustPercent));
                src.spatialBlend = spatialBlend;
                src.minDistance = minDistance;
                src.maxDistance = maxDistance;
            }
        }
    }
}
