using System;

namespace CirrusFS.Geo
{
    public enum RunwayMarkingClass { None = 0, Visual = 1, NonPrecision = 2, Precision = 3 }

    /// What a runway gets painted with, decided from the OurAirports data alone (it has no
    /// marking type, so this follows what ICAO Annex 14 / FAA AC 150/5340-1 call for on runways
    /// of that kind). The terrain shader draws them; this is where the choices live, so they can
    /// be tested and shared with the frame layout (runoff areas lengthen the frame).
    public static class RunwayMarkings
    {
        /// Paved surfaces carry markings; grass, dirt, gravel, sand, snow and water do not.
        public static bool IsPaved(RunwaySurface s) =>
            s == RunwaySurface.Asphalt || s == RunwaySurface.Concrete || s == RunwaySurface.Unknown || s == RunwaySurface.Other;

        /// Precision: large or medium airport, lit, at least 1800 m by 30 m (an instrument
        /// runway an airliner lands on). Non-precision: lit, or at least 1200 m. Visual:
        /// any other paved runway.
        public static RunwayMarkingClass Classify(Runway r)
        {
            if (!IsPaved(r.Surface)) return RunwayMarkingClass.None;
            AirportType t = r.Airport != null ? r.Airport.Type : AirportType.Small;
            bool major = t == AirportType.Large || t == AirportType.Medium;
            if (major && r.Lighted && r.Length >= 1800f && r.Width >= 30f) return RunwayMarkingClass.Precision;
            if (r.Lighted || r.Length >= 1200f) return RunwayMarkingClass.NonPrecision;
            return RunwayMarkingClass.Visual;
        }

        /// Threshold stripes ("piano keys") for a runway width, ICAO Annex 14 table 5-1.
        public static int ThresholdStripes(float width)
        {
            if (width < 20f) return 4;
            if (width < 26f) return 6;
            if (width < 36f) return 8;
            if (width < 52f) return 12;
            return 16;
        }

        /// How much rubber the touchdown zones carry, 0..1: heavy at large airports, some at
        /// medium ones, a trace on small paved fields.
        public static float Rubber(Runway r)
        {
            if (!IsPaved(r.Surface)) return 0f;
            AirportType t = r.Airport != null ? r.Airport.Type : AirportType.Small;
            float lengthFactor = Math.Min(1f, Math.Max(0f, (r.Length - 800f) / 1700f));
            switch (t)
            {
                case AirportType.Large: return 1f;
                case AirportType.Medium: return 0.35f + 0.35f * lengthFactor;
                default: return 0.2f * lengthFactor;
            }
        }

        /// Paved area beyond a runway end, outward from the end: a white-arrow runoff (only
        /// where the data has no displaced threshold of its own), then a blast pad with yellow
        /// chevrons. Long runways at large airports get both, long ones at medium airports a
        /// blast pad. Metres.
        public static void Runoff(Runway r, bool highEnd, out float arrows, out float blastPad)
        {
            arrows = blastPad = 0f;
            if (!IsPaved(r.Surface)) return;
            AirportType t = r.Airport != null ? r.Airport.Type : AirportType.Small;
            float displaced = highEnd ? r.HeDisplaced : r.LeDisplaced;
            if (t == AirportType.Large && r.Length >= 2400f)
            {
                if (displaced < 1f) arrows = 90f;
                blastPad = 60f;
            }
            else if (t == AirportType.Large || (t == AirportType.Medium && r.Length >= 2000f))
            {
                blastPad = 45f;
            }
        }

        /// Longest paved runoff beyond either end, for the frame around the runway.
        public static float MaxRunoff(Runway r)
        {
            Runoff(r, false, out float a0, out float b0);
            Runoff(r, true, out float a1, out float b1);
            return Math.Max(a0 + b0, a1 + b1);
        }

        /// A designator as one number for the shader: runway number + 100 x letter
        /// (1 L, 2 R, 3 C) + 1000 when written with a leading zero ("09"). 0 = no number
        /// (e.g. "N", "H1").
        public static float Designator(string ident)
        {
            if (string.IsNullOrEmpty(ident)) return 0f;
            string s = ident.Trim().ToUpperInvariant();
            int n = 0, digits = 0;
            while (digits < s.Length && digits < 2 && s[digits] >= '0' && s[digits] <= '9')
            {
                n = n * 10 + (s[digits] - '0');
                digits++;
            }
            if (digits == 0 || n < 1 || n > 36) return 0f;
            int letter = 0;
            if (digits < s.Length)
            {
                switch (s[digits])
                {
                    case 'L': letter = 1; break;
                    case 'R': letter = 2; break;
                    case 'C': letter = 3; break;
                }
            }
            bool leadingZero = digits == 2 && s[0] == '0';
            return n + 100 * letter + (leadingZero ? 1000 : 0);
        }
    }
}
