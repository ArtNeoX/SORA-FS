using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace CirrusFS.TimeOfDay
{
    /// On-screen readout of the time-of-day system: time, sun and moon, exposure, fog, and what
    /// each subsystem costs (CPU from the profiler markers, GPU from the command-buffer samples,
    /// averaged over half a second). With a time-of-day slider and a visibility slider.
    ///
    /// Off by default (F8 with TimeOfDayDebugControls). While shown it rebuilds its text twice a
    /// second, the only allocation it makes; while hidden it costs nothing (the component is
    /// disabled, so OnGUI is not called and the profiler recorders are off).
    [DefaultExecutionOrder(400)]
    public class TimeOfDayDebugOverlay : MonoBehaviour
    {
        public TimeOfDaySystem system;
        [Tooltip("Show the time and visibility sliders.")]
        public bool sliders = true;

        // CPU: ProfilerMarkers around each subsystem's per-frame update.
        static readonly string[] CpuNames = { "TimeOfDay.SunMoon", "TimeOfDay.Atmosphere", "TimeOfDay.Environment", "TimeOfDay.Exposure" };
        static readonly string[] CpuLabels = { "sun/moon", "atmosphere", "environment", "exposure" };
        // GPU: every-frame passes first, then the occasional rebuilds (averaged per frame).
        static readonly string[] GpuNames =
        {
            "DrawSkybox", "TimeOfDay.AerialPerspective", "TimeOfDay.ExposureMeter",
            "TimeOfDay.SkyViewLut", "TimeOfDay.AerialLut", "TimeOfDay.BaseLutsSliced", "TimeOfDay.SkyCube",
        };
        static readonly string[] GpuLabels = { "sky", "fog", "meter", "sky LUT", "aerial LUT", "base LUTs", "sky cube" };

        Recorder[] cpu, gpu;
        double[] cpuSum, gpuSum;
        int frames;
        float windowStart;
        string text = "";
        readonly StringBuilder sb = new StringBuilder(1024);
        SunMoonController sunMoon;
        AtmosphereRenderer atmosphere;
        ExposureController exposure;
        GUIStyle style;
        string measured;
        float textHeight;
        Rect box = new Rect(10, 10, 360, 0);

        void OnEnable()
        {
            if (system == null) system = GetComponentInParent<TimeOfDaySystem>();
            if (system != null)
            {
                sunMoon = system.GetComponentInChildren<SunMoonController>();
                atmosphere = system.GetComponentInChildren<AtmosphereRenderer>();
                exposure = system.GetComponentInChildren<ExposureController>();
            }
            cpu = Start(CpuNames);
            gpu = Start(GpuNames);
            cpuSum = new double[cpu.Length];
            gpuSum = new double[gpu.Length];
            frames = 0;
            windowStart = Time.unscaledTime;
            text = "";
            measured = null;
            useGUILayout = false;
        }

        void OnDisable()
        {
            foreach (var r in cpu) if (r.isValid) r.enabled = false;
            foreach (var r in gpu) if (r.isValid) r.enabled = false;
        }

        static Recorder[] Start(string[] names)
        {
            var recorders = new Recorder[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                recorders[i] = Recorder.Get(names[i]);
                if (recorders[i].isValid) recorders[i].enabled = true;
            }
            return recorders;
        }

        static void Retry(Recorder[] recorders, string[] names)
        {
            for (int i = 0; i < recorders.Length; i++)
                if (!recorders[i].isValid)
                {
                    recorders[i] = Recorder.Get(names[i]);
                    if (recorders[i].isValid) recorders[i].enabled = true;
                }
        }

        void Update()
        {
            for (int i = 0; i < cpu.Length; i++) if (cpu[i].isValid) cpuSum[i] += cpu[i].elapsedNanoseconds;
            for (int i = 0; i < gpu.Length; i++) if (gpu[i].isValid) gpuSum[i] += gpu[i].gpuElapsedNanoseconds;
            frames++;
            if (Time.unscaledTime - windowStart < 0.5f) return;
            BuildText();
            System.Array.Clear(cpuSum, 0, cpuSum.Length);
            System.Array.Clear(gpuSum, 0, gpuSum.Length);
            frames = 0;
            windowStart = Time.unscaledTime;
        }

        void BuildText()
        {
            // Samples that had not run yet when the overlay opened (a LUT rebuild) get their
            // recorder once they exist.
            Retry(cpu, CpuNames);
            Retry(gpu, GpuNames);
            sb.Clear();
            if (system == null || system.Clock == null) { text = "No TimeOfDaySystem"; return; }
            TimeOfDayClock clock = system.Clock;
            sb.Append("Local ").Append(clock.Local.ToString("yyyy-MM-dd HH:mm")).Append("  (UTC ").Append(clock.Utc.ToString("HH:mm"))
              .Append(")  x").Append(clock.timeScale.ToString("0")).Append(clock.paused ? "  PAUSED" : "").Append('\n');

            if (sunMoon != null)
            {
                ref readonly CelestialState s = ref sunMoon.State;
                sb.Append("Observer ").Append(s.Observer.Latitude.ToString("F2")).Append(", ").Append(s.Observer.Longitude.ToString("F2"))
                  .Append("  h ").Append((s.Observer.Height / 0.3048).ToString("F0")).Append(" ft\n");
                sb.Append("Sun el ").Append(s.SunElevation.ToString("F2")).Append("  (horizon ").Append(s.HorizonElevation.ToString("F2"))
                  .Append(")   Moon el ").Append(s.MoonElevation.ToString("F1")).Append("  lit ").Append((s.MoonIlluminatedFraction * 100).ToString("F0"))
                  .Append("%\nShadow caster ").Append(s.ShadowCaster == CelestialLight.Sun ? "sun" : "moon").Append('\n');
            }

            sb.Append("Exposure EV ").Append(ExposureController.ExposureValue.ToString("F2")).Append("  P ").Append(ExposureController.PreExposure.ToString("G3"));
            if (exposure != null)
                sb.Append("  scene log2L ").Append(exposure.SceneLog2Luminance.ToString("F2")).Append(exposure.Metered ? " (meter)" : " (estimate)")
                  .Append(exposure.inCockpit ? "  cockpit" : "");
            sb.Append('\n');

            float haze = system.Atmosphere != null ? system.Atmosphere.Parameters.hazeExtinction * 1000f : 0f;
            sb.Append("Visibility ").Append(system.AppliedVisibilityKm.ToString("F1")).Append(" km  haze ").Append(haze.ToString("F3"))
              .Append("/km  top ").Append(system.hazeTopMeters.ToString("F0")).Append(" m");
            if (atmosphere != null) sb.Append("  fog range ").Append(atmosphere.AerialDistanceKm.ToString("F0")).Append(" km");
            sb.Append('\n');
            sb.Append("Tier ").Append(system.quality != null ? system.quality.name : "none").Append('\n');

            float n = Mathf.Max(1, frames);
            double cpuTotal = 0, gpuTotal = 0;
            sb.Append("CPU ms/frame:");
            for (int i = 0; i < cpu.Length; i++)
            {
                double ms = cpuSum[i] / n * 1e-6;
                cpuTotal += ms;
                sb.Append(i % 2 == 0 ? "\n  " : "   ").Append(CpuLabels[i]).Append(' ').Append(cpu[i].isValid ? ms.ToString("F3") : "n/a");
            }
            sb.Append("\n  total ").Append(cpuTotal.ToString("F3")).Append("  (budget 0.3)\n");
            if (SystemInfo.supportsGpuRecorder)
            {
                sb.Append("GPU ms/frame:");
                for (int i = 0; i < gpu.Length; i++)
                {
                    double ms = gpuSum[i] / n * 1e-6;
                    gpuTotal += ms;
                    sb.Append(i % 2 == 0 ? "\n  " : "   ").Append(GpuLabels[i]).Append(' ').Append(gpu[i].isValid ? ms.ToString("F3") : "n/a");
                }
                sb.Append("\n  total ").Append(gpuTotal.ToString("F3")).Append("  (budget 1.5; shadows not included)");
            }
            else sb.Append("GPU timing not supported on this device");
            text = sb.ToString();
        }

        void OnGUI()
        {
            if (style == null)
                style = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false, normal = { textColor = Color.white } };
            if (!ReferenceEquals(measured, text))
            {
                textHeight = style.CalcHeight(new GUIContent(text), box.width - 16);
                measured = text;
            }
            box.height = textHeight + 16 + (sliders ? 56 : 0);
            GUI.color = new Color(0, 0, 0, 0.7f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(box.x + 8, box.y + 8, box.width - 16, textHeight), text, style);

            if (!sliders || system == null || system.Clock == null) return;
            float y = box.y + 12 + textHeight;
            GUI.Label(new Rect(box.x + 8, y, 70, 20), "Time", style);
            float hours = system.Clock.LocalHours;
            float h = GUI.HorizontalSlider(new Rect(box.x + 80, y + 5, box.width - 96, 16), hours, 0f, 23.99f);
            if (Mathf.Abs(h - hours) > 1e-3f) system.Clock.SetLocalHours(h);

            y += 24;
            GUI.Label(new Rect(box.x + 8, y, 70, 20), "Visibility", style);
            float logVis = Mathf.Log10(system.visibilityKm);
            float v = GUI.HorizontalSlider(new Rect(box.x + 80, y + 5, box.width - 96, 16), logVis, -1f, Mathf.Log10(200f));
            if (Mathf.Abs(v - logVis) > 1e-3f) system.SetVisibility(Mathf.Pow(10f, v));
        }
    }
}
