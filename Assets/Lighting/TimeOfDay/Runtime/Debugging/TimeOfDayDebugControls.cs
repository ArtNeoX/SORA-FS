using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.TimeOfDay
{
    /// Keyboard shortcuts for looking at the time-of-day system in play mode. Keys are chosen to
    /// stay clear of the sim's own (letters, digits, F1):
    ///
    ///   F8              overlay on / off
    ///   [ / ]           time -15 / +15 min (Shift: 1 hour)
    ///   F9              fast-forward: 1x, 60x, 600x, 3600x
    ///   F10             pause / resume the clock
    ///   F7              next quality tier
    ///   - / =           visibility halved / doubled
    ///   F6              snap exposure and ambient (skip adaptation after a jump)
    [DefaultExecutionOrder(-90)]
    public class TimeOfDayDebugControls : MonoBehaviour
    {
        public TimeOfDaySystem system;
        public TimeOfDayDebugOverlay overlay;
        [Tooltip("Tiers F7 cycles through.")]
        public TimeOfDayQuality[] tiers;
        public bool shortcuts = true;

        static readonly float[] TimeScales = { 1f, 60f, 600f, 3600f };

        void OnEnable()
        {
            if (system == null) system = GetComponentInParent<TimeOfDaySystem>();
            if (overlay == null) overlay = GetComponent<TimeOfDayDebugOverlay>();
        }

        void Update()
        {
            Keyboard k = Keyboard.current;
            if (!shortcuts || k == null || system == null || system.Clock == null) return;
            TimeOfDayClock clock = system.Clock;
            bool shift = k.shiftKey.isPressed;

            if (k.f8Key.wasPressedThisFrame && overlay != null) overlay.enabled = !overlay.enabled;
            if (k.leftBracketKey.wasPressedThisFrame) clock.Advance(shift ? -3600.0 : -900.0);
            if (k.rightBracketKey.wasPressedThisFrame) clock.Advance(shift ? 3600.0 : 900.0);
            if (k.f9Key.wasPressedThisFrame) CycleTimeScale(clock);
            if (k.f10Key.wasPressedThisFrame) clock.paused = !clock.paused;
            if (k.f7Key.wasPressedThisFrame) CycleTier();
            if (k.minusKey.wasPressedThisFrame) system.SetVisibility(system.visibilityKm * 0.5f);
            if (k.equalsKey.wasPressedThisFrame) system.SetVisibility(system.visibilityKm * 2f);
            if (k.f6Key.wasPressedThisFrame) Snap();
        }

        public void CycleTimeScale(TimeOfDayClock clock)
        {
            int next = 0;
            for (int i = 0; i < TimeScales.Length; i++)
                if (Mathf.Approximately(clock.timeScale, TimeScales[i])) next = (i + 1) % TimeScales.Length;
            clock.timeScale = TimeScales[next];
        }

        public void CycleTier()
        {
            if (tiers == null || tiers.Length == 0) return;
            int next = 0;
            for (int i = 0; i < tiers.Length; i++)
                if (tiers[i] == system.quality) next = (i + 1) % tiers.Length;
            system.SetQuality(tiers[next]);
        }

        public void Snap()
        {
            var env = system.GetComponentInChildren<EnvironmentLighting>();
            var exposure = system.GetComponentInChildren<ExposureController>();
            if (env != null) env.SnapAmbient();
            if (exposure != null) exposure.SnapExposure();
        }
    }
}
