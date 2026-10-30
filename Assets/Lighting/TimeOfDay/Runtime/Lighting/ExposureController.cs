using System;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CirrusFS.TimeOfDay
{
    /// Eye adaptation by pre-exposure.
    ///
    /// The scene's light units span ~14 stops between noon and a moonless night. Applying
    /// exposure after rendering would leave night scenes stored at the bottom of the 32-bit HDR
    /// target, where it has too few steps and posterizes. So exposure is applied before: every
    /// light, the ambient, the reflections and the sky are multiplied by the pre-exposure P,
    /// and the image is written already exposed, near mid-grey whatever the time of day.
    ///
    /// The atmosphere LUTs, the sky cubemap and EnvironmentLighting.Ambient stay in scene units
    /// (no P), so nothing has to be rebuilt as exposure changes. P is applied where light is
    /// handed to Unity or written to the camera target:
    ///  - SunMoonController (light intensity), EnvironmentLighting (ambient probe, reflection
    ///    probe intensity), InteriorAmbientZone;
    ///  - the sky and aerial-perspective shaders, through _TOD_Exposure / TodPreExposure();
    ///  - other lights and emissive materials through PreExposedLight / PreExposedEmission.
    ///
    /// Metering: the renderer feature reduces the view camera's HDR image to a 32x18 grid of
    /// mean log luminance (divided by the P it was rendered with), read back asynchronously.
    /// Before the first reading, or without the renderer feature, the scene luminance is
    /// estimated from the ambient and the sun instead.
    [ExecuteAlways]
    [DefaultExecutionOrder(250)]
    public class ExposureController : MonoBehaviour
    {
        public TimeOfDaySystem system;

        [Tooltip("True while the view is inside the cockpit: applies the profile's cockpit bias instead of the external one.")]
        public bool inCockpit;

        public const int MeterWidth = 32, MeterHeight = 18;

        /// The enabled controller, for camera code that sets inCockpit.
        public static ExposureController Active { get; private set; }

        /// The camera whose image is metered (the system's view camera).
        public static Camera MeteringCamera { get; private set; }
        /// Target the renderer feature writes the meter into; null while no controller runs.
        public static RTHandle MeterTarget { get; private set; }
        /// Frame the renderer feature last wrote the meter on, and how many writes so far.
        public static int MeterFrame { get; private set; } = -1;
        public static int MeterWrites { get; private set; }

        /// Called by the renderer feature when it has recorded a meter pass.
        public static void MeterWritten()
        {
            MeterFrame = Time.frameCount;
            MeterWrites++;
        }

        /// Current pre-exposure: scene light units to camera-target units. 1 when no controller runs.
        public static float PreExposure { get; private set; } = 1f;
        /// log2 of PreExposure.
        public static float ExposureValue { get; private set; }
        /// Increments whenever PreExposure changes. Lets things that apply it skip unchanged frames.
        public static int Version { get; private set; }

        /// Scene log2 luminance the controller is adapting to (centre-weighted), and whether it
        /// came from the meter (false: estimated from the lighting).
        public float SceneLog2Luminance { get; private set; }
        public bool Metered { get; private set; }
        public float TargetExposureValue { get; private set; }

        /// Scene light units to camera-target units, for code that sets light intensities or
        /// emission itself. Components: PreExposedLight, PreExposedEmission.
        public static float Expose(float intensity) => intensity * PreExposure;
        public static Color Expose(Color emission) => emission * PreExposure;

        static readonly int ExposureId = Shader.PropertyToID("_TOD_Exposure");
        static readonly ProfilerMarker Marker = new ProfilerMarker("TimeOfDay.Exposure");

        /// A meter reading older than this many frames is ignored (camera not rendering, feature
        /// missing) and the estimate takes over.
        const int StaleFrames = 30;

        SunMoonController sunMoon;
        RenderTexture meterTexture;
        NativeArray<float> readback;
        Action<AsyncGPUReadbackRequest> onReadback;
        bool readbackPending;
        int requestedWrite = -1, readFrame = -1;
        float meteredLog2;
        readonly float[] weights = new float[MeterWidth * MeterHeight];
        float weightSum, weightsFor = -1f;
        bool initialized;
        float current;
        double lastRealtime = -1.0;

        void OnEnable()
        {
            if (system == null) system = GetComponentInParent<TimeOfDaySystem>();
            Active = this;
            sunMoon = GetComponent<SunMoonController>();
            if (sunMoon == null && system != null) sunMoon = system.GetComponentInChildren<SunMoonController>();

            meterTexture = new RenderTexture(MeterWidth, MeterHeight, GraphicsFormat.R32_SFloat, GraphicsFormat.None)
            {
                name = "TOD ExposureMeter",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            meterTexture.Create();
            MeterTarget = RTHandles.Alloc(meterTexture);
            MeterFrame = -1;
            readback = new NativeArray<float>(MeterWidth * MeterHeight, Allocator.Persistent);
            onReadback ??= OnReadback;
            readbackPending = false;
            requestedWrite = readFrame = -1;
            initialized = false;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            if (readbackPending) AsyncGPUReadback.WaitAllRequests();
            readbackPending = false;
            if (readback.IsCreated) readback.Dispose();
            MeterTarget?.Release();
            MeterTarget = null;
            if (meterTexture != null)
            {
                meterTexture.Release();
                CoreUtils.Destroy(meterTexture);
                meterTexture = null;
            }
            MeteringCamera = null;
            PreExposure = 1f;
            ExposureValue = 0f;
            Version++;
            Shader.SetGlobalVector(ExposureId, Vector4.zero);   // shaders read 0 as "no pre-exposure"
        }

        // Before SunMoonController (300) and EnvironmentLighting (320), which apply P.
        void LateUpdate() => Refresh();

        /// Skips adaptation: exposure jumps to the current target. For cuts, teleports, time
        /// scrubbing, tools and tests.
        public void SnapExposure()
        {
            if (readbackPending) AsyncGPUReadback.WaitAllRequests();
            initialized = false;
            Refresh();
        }

        public void Refresh()
        {
            if (system == null) return;
            using (Marker.Auto())
            {
                MeteringCamera = system.ViewCamera;
                ExposureSettings e = system.profile != null ? system.profile.exposure : Defaults;
                if (e.centerWeight != weightsFor) BuildWeights(e.centerWeight);

                double now = Time.realtimeSinceStartupAsDouble;
                float dt = Application.isPlaying ? Time.unscaledDeltaTime
                         : lastRealtime < 0.0 ? 0f : (float)(now - lastRealtime);
                lastRealtime = now;

                RequestReadback();
                if (!Application.isPlaying && readbackPending) AsyncGPUReadback.WaitAllRequests();

                Metered = e.useMeter && readFrame >= 0 && Time.frameCount - readFrame <= StaleFrames;
                SceneLog2Luminance = Metered ? meteredLog2 : EstimateLog2Luminance();
                TargetExposureValue = TargetFor(SceneLog2Luminance, e);

                if (!initialized || !Application.isPlaying)
                {
                    // Edit mode has no meaningful frame time and the scene view scrubs the clock:
                    // follow the target directly.
                    current = TargetExposureValue;
                    initialized = true;
                }
                else
                {
                    float delta = TargetExposureValue - current;
                    // EV going down = the scene got brighter: the eye closes down quickly and opens
                    // up slowly, as it does.
                    float rate = delta < 0f ? e.brightenSpeed : e.darkenSpeed;
                    current += delta * (1f - Mathf.Exp(-rate * dt));
                }
                Publish(current);
            }
        }

        /// Exposure (log2 P) that maps a scene of mean log2 luminance `log2L` to the key, plus
        /// the compensation and view bias, within the profile's limits. The key falls from day to
        /// night: a moonlit scene should look dark, not like a grey day.
        public float TargetFor(float log2L, ExposureSettings e)
        {
            float night = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(e.nightLog2Luminance, e.dayLog2Luminance, log2L));
            float key = Mathf.Lerp(e.key, e.nightKey, night);
            float ev = Mathf.Log(key, 2f) - log2L + e.compensation + (inCockpit ? e.cockpitBias : e.externalBias);
            return Mathf.Clamp(ev, e.minExposure, e.maxExposure);
        }

        void Publish(float ev)
        {
            float p = Mathf.Pow(2f, ev);
            if (Mathf.Abs(p - PreExposure) > PreExposure * 1e-4f) Version++;
            PreExposure = p;
            ExposureValue = ev;
            Shader.SetGlobalVector(ExposureId, new Vector4(p, 1f / p, ev, 0f));
        }

        // ---- meter --------------------------------------------------------------------

        void RequestReadback()
        {
            if (readbackPending || meterTexture == null || MeterFrame < 0 || MeterWrites == requestedWrite) return;
            // Queued after the frame that wrote the meter, so the GPU has it by then.
            AsyncGPUReadback.RequestIntoNativeArray(ref readback, meterTexture, 0, onReadback);
            readbackPending = true;
            requestedWrite = MeterWrites;
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            readbackPending = false;
            if (request.hasError || !readback.IsCreated) return;

            // Cells hold log means (robust to a sun disc or a black pixel inside a cell); across
            // cells the mean is linear, as a light meter's is. A log mean over the whole image
            // is pulled down by every shadow and dark object and exposes daylight ~2 EV too bright.
            double sum = 0.0;
            for (int i = 0; i < weights.Length; i++)
            {
                float v = readback[i];
                if (!float.IsFinite(v)) continue;
                sum += weights[i] * Math.Pow(2.0, Mathf.Clamp(v, -20f, 8f));
            }
            meteredLog2 = (float)Math.Log(Math.Max(sum / weightSum, 1e-7), 2.0);
            readFrame = MeterFrame;
        }

        /// Centre-weighted: a Gaussian around the centre on top of a uniform floor, so the edges
        /// still count but the subject (the view ahead, the panel) counts most.
        void BuildWeights(float centerWeight)
        {
            weightsFor = centerWeight;
            weightSum = 0f;
            for (int y = 0; y < MeterHeight; y++)
                for (int x = 0; x < MeterWidth; x++)
                {
                    float u = (x + 0.5f) / MeterWidth - 0.5f;
                    float v = ((y + 0.5f) / MeterHeight - 0.5f) * MeterHeight / MeterWidth;
                    float g = Mathf.Exp(-(u * u + v * v) / (2f * 0.18f * 0.18f));
                    float w = (1f - centerWeight) + centerWeight * g;
                    weights[y * MeterWidth + x] = w;
                    weightSum += w;
                }
        }

        /// Without a meter reading: what the average surface would look like, from the ambient
        /// (the sky's mean radiance) plus the direct light on a mid-grey horizontal surface.
        float EstimateLog2Luminance()
        {
            SphericalHarmonicsL2 sh = EnvironmentLighting.Ambient;
            float ambient = 0.2126f * sh[0, 0] + 0.7152f * sh[1, 0] + 0.0722f * sh[2, 0];
            float direct = 0f;
            if (sunMoon != null)
            {
                ref readonly CelestialState s = ref sunMoon.State;
                Vector3 d = s.ShadowCaster == CelestialLight.Sun ? s.SunIlluminance : s.MoonIlluminance;
                Vector3 dir = s.ShadowCaster == CelestialLight.Sun ? s.SunDirection : s.MoonDirection;
                float cos = Mathf.Max(0f, Vector3.Dot(dir, s.Observer.UpUnity));
                direct = (0.2126f * d.x + 0.7152f * d.y + 0.0722f * d.z) * cos;
            }
            const float albedo = 0.18f;
            return Mathf.Log(Mathf.Max(albedo * (ambient + direct), 1e-7f), 2f);
        }

        static readonly ExposureSettings Defaults = new ExposureSettings();
    }
}
