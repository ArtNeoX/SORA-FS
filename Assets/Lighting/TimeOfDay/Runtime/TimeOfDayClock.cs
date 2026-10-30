using System;
using CirrusFS.Geo;
using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// Simulation date and time. UTC is authoritative; local time is only for display and for
    /// scrubbing, using the nautical time zone of the viewer's longitude (round(lon / 15) h),
    /// which is what a pilot's "local time" means over open ocean and close enough on land.
    ///
    /// Stored as whole days since 2000-01-01 plus seconds of day, so fast-forwarding for days
    /// never loses precision the way a single growing double would.
    [ExecuteAlways]
    [DefaultExecutionOrder(-90)]
    public class TimeOfDayClock : MonoBehaviour
    {
        public enum StartTime { Inspector, SystemClock }
        public enum Zone { Utc, Local }

        [Tooltip("Inspector: start at the date and time below. SystemClock: start at the computer's UTC time.")]
        public StartTime startFrom = StartTime.Inspector;

        [Tooltip("Whether the date and hours below are UTC or local (nautical zone of the viewer).")]
        public Zone zone = Zone.Local;

        [SerializeField, Range(1900, 2100)] int year = 2026;
        [SerializeField, Range(1, 12)] int month = 6;
        [SerializeField, Range(1, 31)] int day = 21;
        [SerializeField, Range(0f, 24f)] float hours = 9f;

        [Header("Running")]
        [Tooltip("Simulated seconds per real second. 1 = real time; 3600 = an hour per second.")]
        public float timeScale = 1f;
        public bool paused;
        [Tooltip("Advance the clock in edit mode too (sky preview).")]
        public bool previewInEditMode;

        [Header("Where, when GeoOrigin is not set")]
        [Tooltip("Location used in edit mode and in scenes that never spawn an aircraft. Default RJTT.")]
        public double fallbackLatitude = 35.5533;
        public double fallbackLongitude = 139.7811;

        static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const double EpochJulianDate = 2451544.5;

        long dayIndex;          // whole UTC days since Epoch
        double secondsOfDay;    // [0, 86400)
        double lastRealtime = -1.0;
        bool initialised;

        /// Julian date (UTC) of the current simulation instant.
        public double JulianDate
        {
            get { EnsureInitialised(); return EpochJulianDate + dayIndex + secondsOfDay / 86400.0; }
        }

        public DateTime Utc
        {
            get { EnsureInitialised(); return Epoch.AddDays(dayIndex).AddSeconds(secondsOfDay); }
        }

        /// Longitude used for the local zone: the floating origin's when set, else the fallback.
        public double ZoneLongitude => ObserverFrame.GeoOriginLive ? GeoOrigin.Longitude : fallbackLongitude;

        public int ZoneOffsetHours => (int)Math.Round(GeoMath.WrapLongitude(ZoneLongitude) / 15.0);

        public DateTime Local => Utc.AddHours(ZoneOffsetHours);

        /// Local time of day, hours [0, 24).
        public float LocalHours
        {
            get
            {
                double h = secondsOfDay / 3600.0 + ZoneOffsetHours;
                h %= 24.0;
                return (float)(h < 0 ? h + 24.0 : h);
            }
        }

        public void SetUtc(DateTime utc)
        {
            utc = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
            TimeSpan sinceEpoch = utc - Epoch;
            dayIndex = (long)Math.Floor(sinceEpoch.TotalDays);
            secondsOfDay = (sinceEpoch - TimeSpan.FromDays(dayIndex)).TotalSeconds;
            initialised = true;
            MirrorToFields();
        }

        /// Moves to `localHours` on the current local date.
        public void SetLocalHours(float localHours)
        {
            DateTime local = Local;
            DateTime target = local.Date.AddHours(Mathf.Clamp(localHours, 0f, 23.99999f));
            SetUtc(DateTime.SpecifyKind(target.AddHours(-ZoneOffsetHours), DateTimeKind.Utc));
        }

        /// Advances by simulated seconds (negative goes back).
        public void Advance(double seconds)
        {
            EnsureInitialised();
            secondsOfDay += seconds;
            if (secondsOfDay >= 86400.0 || secondsOfDay < 0.0)
            {
                long days = (long)Math.Floor(secondsOfDay / 86400.0);
                dayIndex += days;
                secondsOfDay -= days * 86400.0;
            }
        }

        void OnEnable()
        {
            initialised = false;
            lastRealtime = -1.0;
            EnsureInitialised();
            GeoOrigin.OriginReset += HandleOriginReset;
        }

        void OnDisable() => GeoOrigin.OriginReset -= HandleOriginReset;

        /// A spawn can move the viewer across time zones. In local mode the local date and time
        /// are kept (a 09:00 departure stays a morning departure wherever it is); UTC moves.
        void HandleOriginReset()
        {
            if (zone == Zone.Local && startFrom == StartTime.Inspector) ApplyFields();
        }

        void OnValidate()
        {
            // Inspector edit: the fields win.
            if (initialised) ApplyFields();
        }

        void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            double realDelta = lastRealtime < 0.0 ? 0.0 : now - lastRealtime;
            lastRealtime = now;

            if (paused) return;

            if (Application.isPlaying)
            {
                Advance(Time.deltaTime * (double)timeScale);
                MirrorToFields();
            }
            else if (previewInEditMode)
            {
                Advance(Math.Min(realDelta, 0.25) * timeScale);
                MirrorToFields();
#if UNITY_EDITOR
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
            }
        }

        void EnsureInitialised()
        {
            if (initialised) return;
            if (startFrom == StartTime.SystemClock && Application.isPlaying) SetUtc(DateTime.UtcNow);
            else ApplyFields();
        }

        void ApplyFields()
        {
            int d = Mathf.Clamp(day, 1, DateTime.DaysInMonth(year, month));
            var date = new DateTime(year, month, d, 0, 0, 0, DateTimeKind.Utc);
            DateTime utc = date.AddHours(Mathf.Clamp(hours, 0f, 23.99999f));
            if (zone == Zone.Local) utc = utc.AddHours(-ZoneOffsetHours);

            TimeSpan sinceEpoch = utc - Epoch;
            dayIndex = (long)Math.Floor(sinceEpoch.TotalDays);
            secondsOfDay = (sinceEpoch - TimeSpan.FromDays(dayIndex)).TotalSeconds;
            initialised = true;
        }

        /// Keeps the inspector showing the running time. Writes plain fields only, so it
        /// allocates nothing and does not dirty the scene.
        void MirrorToFields()
        {
            DateTime t = zone == Zone.Local ? Local : Utc;
            year = t.Year;
            month = t.Month;
            day = t.Day;
            hours = (float)t.TimeOfDay.TotalHours;
        }
    }
}
