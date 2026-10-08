using CirrusFS.Sim;
using UnityEngine;

namespace CirrusFS.Flight.Avionics
{
    /// Airbus A320 Primary Flight Display. Put it on the CockpitData/PFD empty.
    ///
    /// Drawn so far: attitude sphere with pitch ladder, roll scale and sideslip index, speed
    /// tape with VMAX barber pole and speed trend, altitude tape with drum readout and ground
    /// ribbon, vertical speed scale, heading tape, radio altitude. The FMA, FD bars, ILS
    /// scales, selected targets and baro setting wait for the systems that feed them.
    ///
    /// Until terrain elevation exists the ground is world height 0, so altitude and radio
    /// altitude are the same number: the height of the lowest wheel bottom, which reads 0 on
    /// the ground as the real RA is calibrated to.
    ///
    /// Layout is in the pixels of the 826 x 842 reference artwork, y down.
    public class A320Pfd : GlassDisplay
    {
        const float FeetPerMetre = 3.28084f;
        const float FpmPerMs = 196.8504f;

        // ---- layout ----
        const float Cx = 358f, Cy = 428f;              // attitude centre; every index line is at Cy
        const float PitchPx = 8.9f;                    // per degree
        const float SphereR = 222f, SphereHalfW = 193f;
        const float LadderSpan = 168f;                 // ladder hidden beyond this, clear of the roll index
        const float TapeTop = 207f, TapeBot = 652f;
        const float SpdL = 10f, SpdR = 97f, KtPx = 5.25f;
        const float AltL = 612f, AltR = 680f, FtPx = 0.392f;
        const float VsiL = 762f, VsiR = 822f, VsiTop = 165f, VsiBot = 693f, VsiNeedleX = 786f;
        const float HdgL = 165f, HdgR = 550f, HdgTop = 765f, HdgBot = 830f, HdgPx = 8f;

        static readonly Color Sky = Rgb(26, 153, 240);
        static readonly Color Earth = Rgb(150, 90, 0);
        static readonly Color Scale = Rgb(118, 118, 118);
        static readonly Color White = Color.white;
        static readonly Color Green = Rgb(30, 240, 40);
        static readonly Color Yellow = Rgb(255, 255, 0);
        static readonly Color Amber = Rgb(255, 150, 0);
        static readonly Color Red = Rgb(255, 25, 25);
        static readonly Color Black = Color.black;

        static Color Rgb(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

        [Header("Aircraft (found in parents if empty)")]
        public AircraftController aircraft;
        public A320AnimationDriver systems;
        public LandingGearSuspension gear;

        [Header("Air data")]
        [Tooltip("Magnetic variation, deg, east positive. Heading shown = true heading - this.")]
        public float magneticVariation;
        [Tooltip("Vertical speed smoothing time constant, s.")]
        [Min(0f)] public float vsSmoothing = 0.3f;
        [Tooltip("The speed trend arrow shows the speed this many seconds ahead.")]
        public float trendSeconds = 10f;
        [Tooltip("Acceleration smoothing behind the speed trend, s.")]
        [Min(0f)] public float trendSmoothing = 0.8f;
        [Tooltip("Sideslip index travel, design px per degree of sideslip.")]
        public float slipPxPerDeg = 4f;

        [Header("Speed limits (kt)")]
        public float vmo = 350f;
        [Tooltip("Gear extended limit, applied whenever the gear is not fully up.")]
        public float vle = 280f;
        [Tooltip("VFE per flap detent of config.flapAngles (CONF 0, 1+F, 2, 3, FULL). Entry 0 is unused.")]
        public float[] vfe = { 350f, 215f, 200f, 185f, 177f };

        Rigidbody rb;
        float ias, pitch, bank, heading, altFt, raFt, vsFpm, slipDeg, vmax;
        float prevIas, accel;
        bool havePrev;
        float cosB, sinB;

        protected override Rect DesignRect => new Rect(-8f, 0f, 842f, 842f);

        // ---- data -------------------------------------------------------------

        void ResolveAircraft()
        {
            if (!aircraft) aircraft = GetComponentInParent<AircraftController>();
            if (!aircraft) return;
            if (!rb) rb = aircraft.GetComponent<Rigidbody>();
            if (!systems) systems = aircraft.GetComponent<A320AnimationDriver>();
            if (!gear) gear = aircraft.GetComponent<LandingGearSuspension>();
        }

        protected override void Sample(float dt)
        {
            ResolveAircraft();
            Transform t = aircraft ? aircraft.transform : transform.root;

            Vector3 f = t.forward, r = t.right, u = t.up;
            pitch = Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            bank = Mathf.Atan2(-r.y, u.y) * Mathf.Rad2Deg;
            Vector3 g = WorldOrigin.WorldToGeoFrame(f);
            heading = Mathf.Repeat(Mathf.Atan2(g.x, g.z) * Mathf.Rad2Deg - magneticVariation, 360f);

            altFt = (WheelBottomHeight(t) + (float)WorldOrigin.OffsetUp) * FeetPerMetre;
            raFt = altFt;

            ias = aircraft ? aircraft.AirspeedKnots : 0f;
            Vector3 vel = rb && Application.isPlaying ? rb.linearVelocity : Vector3.zero;
            float vsTarget = vel.y * FpmPerMs;

            Vector3 vLocal = t.InverseTransformDirection(vel);
            slipDeg = vLocal.z > 10f ? Mathf.Atan2(vLocal.x, vLocal.z) * Mathf.Rad2Deg : 0f;

            if (dt > 0f)
            {
                vsFpm += (vsTarget - vsFpm) * Smooth(dt, vsSmoothing);
                // IAS only changes on physics steps, so per-frame differences are spiky; the
                // low-pass turns them back into the mean acceleration.
                float a = havePrev ? (ias - prevIas) / dt : 0f;
                accel += (a - accel) * Smooth(dt, trendSmoothing);
                prevIas = ias;
                havePrev = true;
            }
            else
            {
                vsFpm = vsTarget;
                accel = 0f;
            }

            vmax = vmo;
            if (aircraft && vfe != null)
            {
                int i = aircraft.FlapIndex;
                if (i > 0 && i < vfe.Length) vmax = Mathf.Min(vmax, vfe[i]);
            }
            if (!systems || systems.GearPosition < 1f) vmax = Mathf.Min(vmax, vle);
        }

        static float Smooth(float dt, float tau) => tau > 0f ? 1f - Mathf.Exp(-dt / tau) : 1f;

        /// World height of the lowest wheel bottom, taken from the airframe-fixed rest
        /// position so strut travel does not move it.
        float WheelBottomHeight(Transform t)
        {
            if (!aircraft || !gear || gear.gears == null || gear.gears.Length == 0) return t.position.y;
            Vector3 refOffset = aircraft.RefOffset;
            float min = float.MaxValue;
            foreach (var g in gear.gears)
                min = Mathf.Min(min, t.TransformPoint(refOffset + g.mountLocal - new Vector3(0f, g.radius, 0f)).y);
            return min;
        }

        // ---- drawing ----------------------------------------------------------

        protected override void Draw(DisplayPainter p)
        {
            cosB = Mathf.Cos(bank * Mathf.Deg2Rad);
            sinB = Mathf.Sin(bank * Mathf.Deg2Rad);

            DrawAttitude(p);
            DrawRollScale(p);
            DrawSpeedTape(p);
            DrawAltitudeTape(p);
            DrawVerticalSpeed(p);
            DrawHeadingTape(p);
            DrawAircraftSymbol(p);
            DrawRadioAltitude(p);
        }

        /// Attitude-frame point (u right, v down, from the centre) turned with the horizon.
        Vector2 Att(float u, float v) => new Vector2(Cx + u * cosB + v * sinB, Cy - u * sinB + v * cosB);

        /// Screen-fixed point at radius r, angle a clockwise from straight up.
        static Vector2 Polar(float r, float aDeg)
        {
            float a = aDeg * Mathf.Deg2Rad;
            return new Vector2(Cx + r * Mathf.Sin(a), Cy - r * Mathf.Cos(a));
        }

        static Vector2[] sphere;

        /// The sphere's outline: a circle with its sides cut off vertically.
        static Vector2[] Sphere()
        {
            if (sphere != null) return sphere;
            const int n = 120;
            sphere = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = 2f * Mathf.PI * i / n;
                sphere[i] = new Vector2(Cx + Mathf.Clamp(SphereR * Mathf.Sin(a), -SphereHalfW, SphereHalfW),
                                        Cy - SphereR * Mathf.Cos(a));
            }
            return sphere;
        }

        void DrawAttitude(DisplayPainter p)
        {
            float hy = pitch * PitchPx;
            p.PushClipPolygon(Sphere());

            p.Quad(Att(-1500f, -3000f), Att(1500f, -3000f), Att(1500f, hy), Att(-1500f, hy), Sky);
            p.Quad(Att(-1500f, hy), Att(1500f, hy), Att(1500f, 3000f), Att(-1500f, 3000f), Earth);
            p.Line(Att(-1500f, hy), Att(1500f, hy), 3f, White);

            // Ladder every 2.5 deg: short, 5 deg medium, 10 deg long and labelled.
            for (int k = -36; k <= 36; k++)
            {
                if (k == 0) continue;
                float deg = k * 2.5f;
                float v = hy - deg * PitchPx;
                if (Mathf.Abs(v) > LadderSpan) continue;
                bool ten = k % 4 == 0, five = k % 2 == 0;
                float half = ten ? 50f : five ? 26f : 13f;
                p.Line(Att(-half, v), Att(half, v), 3f, White);
                if (ten)
                {
                    string s = Num(Mathf.Abs(Mathf.RoundToInt(deg)));
                    Vector2 l = Att(-half - 34f, v), r = Att(half + 34f, v);
                    p.Text(s, l.x, l.y, 20f, White, TextAlign.Center, bank);
                    p.Text(s, r.x, r.y, 20f, White, TextAlign.Center, bank);
                }
            }

            // Normal-law pitch limits, green '=' at +30 / -15.
            PitchLimit(p, 30f, hy);
            PitchLimit(p, -15f, hy);

            p.PopClip();
        }

        void PitchLimit(DisplayPainter p, float deg, float hy)
        {
            float v = hy - deg * PitchPx;
            for (int side = -1; side <= 1; side += 2)
            for (int row = -1; row <= 1; row += 2)
                p.Line(Att(side * 32f, v + row * 3.5f), Att(side * 50f, v + row * 3.5f), 2.5f, Green);
        }

        void DrawRollScale(DisplayPainter p)
        {
            // Fixed scale: ticks at 10/20/30, lines at 45, green '=' bank limits at 67.
            for (int s = -1; s <= 1; s += 2)
            {
                for (int i = 1; i <= 3; i++)
                {
                    float a = s * i * 10f * Mathf.Deg2Rad;
                    var d = new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
                    var tn = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 4f;
                    var c = new Vector2(Cx, Cy);
                    p.Polyline(2f, White, true, c + d * 225f - tn, c + d * 225f + tn, c + d * 240f + tn, c + d * 240f - tn);
                }
                p.Line(Polar(224f, s * 45f), Polar(248f, s * 45f), 3f, White);

                float b = s * 67f * Mathf.Deg2Rad;
                var off = new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * 3.5f;
                p.Line(Polar(222f, s * 67f) + off, Polar(238f, s * 67f) + off, 2.5f, Green);
                p.Line(Polar(222f, s * 67f) - off, Polar(238f, s * 67f) - off, 2.5f, Green);
            }

            // Fixed zero index.
            p.Polyline(2.5f, Yellow, true, Polar(221f, 0f), new Vector2(Cx + 13f, Cy - 241f), new Vector2(Cx - 13f, Cy - 241f));

            // Roll index turns with the horizon; the sideslip trapezoid slides under it.
            p.Polyline(2.5f, Yellow, true, Att(0f, -216f), Att(13f, -192f), Att(-13f, -192f));
            float sx = Mathf.Clamp(slipDeg * slipPxPerDeg, -30f, 30f);
            p.Polyline(2.5f, Yellow, true, Att(sx - 17f, -188f), Att(sx + 17f, -188f), Att(sx + 23f, -177f), Att(sx - 23f, -177f));
        }

        void DrawSpeedTape(DisplayPainter p)
        {
            // The scale starts at 30 kt and stays there below it.
            float spd = Mathf.Max(ias, 30f);
            p.FillRect(SpdL, TapeTop, SpdR, TapeBot, Scale);

            p.PushClipRect(SpdL, TapeTop, SpdR + 18f, TapeBot);
            int lo = Mathf.FloorToInt((spd - 50f) / 10f) * 10;
            for (int v = Mathf.Max(lo, 30); v <= spd + 50f; v += 10)
            {
                float y = Cy - (v - spd) * KtPx;
                p.Line(new Vector2(84f, y), new Vector2(SpdR, y), 3f, White);
                if (v % 20 == 0) p.Text(Num(v), 72f, y, 22f, White, TextAlign.Right);
            }

            // VMAX barber pole, red and black, scrolling with the scale.
            float yv = Cy - (vmax - spd) * KtPx;
            if (yv > TapeTop)
                for (float y = yv; y > TapeTop; y -= 24f)
                    p.FillRect(SpdR + 2f, y - 12f, SpdR + 16f, y, Red);
            p.PopClip();

            p.Line(new Vector2(SpdR, TapeTop), new Vector2(SpdR, TapeBot), 2.5f, White);
            p.Line(new Vector2(SpdL, TapeTop), new Vector2(SpdR + 16f, TapeTop), 2.5f, White);
            p.Line(new Vector2(SpdL, TapeBot), new Vector2(SpdR + 16f, TapeBot), 2.5f, White);

            // Speed trend: shown once it exceeds 2 kt.
            float trend = accel * trendSeconds;
            if (Mathf.Abs(trend) >= 2f && ias >= 30f)
            {
                float yEnd = Mathf.Clamp(Cy - trend * KtPx, TapeTop, TapeBot);
                float back = trend > 0f ? 10f : -10f;
                p.Line(new Vector2(90f, Cy), new Vector2(90f, yEnd), 3f, Yellow);
                p.Line(new Vector2(90f, yEnd), new Vector2(83f, yEnd + back), 3f, Yellow, true);
                p.Line(new Vector2(90f, yEnd), new Vector2(97f, yEnd + back), 3f, Yellow, true);
            }

            // Speed reference.
            p.Line(new Vector2(-8f, Cy), new Vector2(SpdR + 4f, Cy), 4.5f, Yellow);
            p.Triangle(new Vector2(SpdR + 2f, Cy), new Vector2(SpdR + 27f, Cy - 12f), new Vector2(SpdR + 27f, Cy + 12f), Yellow);
        }

        void DrawAltitudeTape(DisplayPainter p)
        {
            float a = altFt;
            p.FillRect(AltL, TapeTop, AltR, TapeBot, Scale);

            p.PushClipRect(AltL, TapeTop, AltR, TapeBot);
            int lo = Mathf.FloorToInt((a - 600f) / 100f) * 100;
            for (int v = lo; v <= a + 600f; v += 100)
            {
                float y = Cy - (v - a) * FtPx;
                p.Line(new Vector2(AltR - 12f, y), new Vector2(AltR, y), 3f, White);
                if (v % 500 == 0 && v >= 0) p.Text(Pad3(v / 100), AltL + 6f, y, 22f, White);
            }

            // Ground reference ribbon, from the radio altimeter.
            float gy = Cy + raFt * FtPx;
            if (gy < TapeBot) p.FillRect(AltR - 14f, Mathf.Max(gy, TapeTop), AltR, TapeBot, Red);
            p.PopClip();

            p.Line(new Vector2(AltR, TapeTop), new Vector2(AltR, TapeBot), 2f, White);
            p.Line(new Vector2(AltL, TapeTop), new Vector2(AltR, TapeTop), 2.5f, White);
            p.Line(new Vector2(AltL, TapeBot), new Vector2(AltR, TapeBot), 2.5f, White);

            DrawAltitudeWindow(p, a);
            p.Line(new Vector2(556f, Cy), new Vector2(AltL - 2f, Cy), 4.5f, Yellow);
        }

        /// Yellow window: hundreds and up on a big odometer, the last two digits on a drum in
        /// 20 ft steps. The odometer rolls through the last 20 ft before each hundred.
        void DrawAltitudeWindow(DisplayPainter p, float a)
        {
            const float winT = Cy - 21f, winB = Cy + 21f;
            const float drumL = AltR, drumR = AltR + 48f, drumT = Cy - 38f, drumB = Cy + 38f;
            const float digitH = 34f, rowH = 42f, drumH = 18f, drumPitch = 24f;

            p.FillRect(AltL, winT, drumL, winB, Black);
            p.FillRect(drumL, drumT, drumR, drumB, Black);

            float abs = Mathf.Abs(a);
            int n = (int)(abs / 100f);
            float tens = abs - n * 100f;
            float roll = tens > 80f ? (tens - 80f) / 20f : 0f;
            float digitW = p.MeasureText("0", digitH);

            p.PushClipRect(AltL, winT + 1.5f, drumL, winB - 1.5f);
            bool carry = true;
            for (int i = 0, pow = 1; i < 3; i++, pow *= 10)
            {
                int d = n / pow % 10;
                bool rolls = roll > 0f && carry;
                carry &= d == 9;
                float x = drumL - 4f - digitW * (i + 0.5f);
                float off = rolls ? roll * rowH : 0f;
                if (n >= pow || (i == 0 && n > 0)) p.Text(Num(d), x, Cy + off, digitH, Green, TextAlign.Center);
                if (rolls && n + 1 >= pow) p.Text(Num((d + 1) % 10), x, Cy + off - rowH, digitH, Green, TextAlign.Center);
            }
            if (a < -10f) p.Text("NEG", AltL + 4f, Cy, 13f, White);
            p.PopClip();

            p.PushClipRect(drumL + 1.5f, drumT + 1.5f, drumR - 1.5f, drumB - 1.5f);
            float v20 = abs / 20f;
            int k0 = Mathf.FloorToInt(v20);
            for (int k = k0 - 2; k <= k0 + 2; k++)
            {
                int val = ((k * 20) % 100 + 100) % 100;
                p.Text(Pad2(val), (drumL + drumR) * 0.5f, Cy - (k - v20) * drumPitch, drumH, Green, TextAlign.Center);
            }
            p.PopClip();

            p.Polyline(3f, Yellow, true,
                new Vector2(AltL, winT), new Vector2(drumL, winT), new Vector2(drumL, drumT), new Vector2(drumR, drumT),
                new Vector2(drumR, drumB), new Vector2(drumL, drumB), new Vector2(drumL, winB), new Vector2(AltL, winB));
        }

        static readonly Vector2[] VsiShape =
        {
            new Vector2(VsiL, VsiTop), new Vector2(795f, VsiTop), new Vector2(VsiR, 225f),
            new Vector2(VsiR, 633f), new Vector2(795f, VsiBot), new Vector2(VsiL, VsiBot),
        };

        static readonly int[] VsiMajor = { 1000, 2000, 6000 }, VsiMinor = { 500, 1500 };

        /// Non-linear A320 V/S scale: 0-1000 fpm gets most of the travel, 2000-6000 the least.
        static float VsY(float fpm)
        {
            float a = Mathf.Abs(fpm);
            float px = a <= 1000f ? a * 0.141f
                     : a <= 2000f ? 141f + (a - 1000f) * 0.051f
                     : 192f + Mathf.Min(a - 2000f, 4000f) * 0.01325f;
            return Cy - Mathf.Sign(fpm) * px;
        }

        void DrawVerticalSpeed(DisplayPainter p)
        {
            p.FillPolygon(VsiShape, Scale);
            for (int s = -1; s <= 1; s += 2)
            {
                foreach (int m in VsiMajor)
                {
                    float y = VsY(s * m);
                    p.Line(new Vector2(778f, y), new Vector2(790f, y), 4f, White);
                    p.Text(Num(m / 1000), 769f, y, 17f, White, TextAlign.Center);
                }
                foreach (int m in VsiMinor)
                {
                    float y = VsY(s * m);
                    p.Line(new Vector2(783f, y), new Vector2(790f, y), 3f, White);
                }
            }
            p.Line(new Vector2(760f, Cy), new Vector2(792f, Cy), 4.5f, Yellow);

            // Needle: points from a virtual pivot right of the screen to the value at the scale.
            float vs = vsFpm;
            bool amber = Mathf.Abs(vs) > 6000f || (vs < -2000f && raFt < 2500f) || (vs < -1200f && raFt < 1000f);
            Color c = amber ? Amber : Green;
            var tip = new Vector2(VsiNeedleX, VsY(Mathf.Clamp(vs, -6000f, 6000f)));
            var pivot = new Vector2(880f, Cy);
            p.Line(tip, Vector2.Lerp(tip, pivot, (VsiR - tip.x) / (pivot.x - tip.x)), 4f, c);

            // Readout in hundreds of fpm beside the needle tip, from 200 fpm.
            if (Mathf.Abs(vs) >= 200f)
            {
                string s = Pad2(Mathf.RoundToInt(Mathf.Abs(vs) / 100f));
                float w = Mathf.Max(36f, p.MeasureText(s, 18f) + 8f);
                float y0 = vs < 0f ? tip.y + 4f : tip.y - 28f;
                y0 = Mathf.Clamp(y0, VsiTop + 2f, VsiBot - 26f);
                p.FillRect(VsiR - 2f - w, y0, VsiR - 2f, y0 + 24f, Black);
                p.Text(s, VsiR - 2f - w * 0.5f, y0 + 12f, 18f, c, TextAlign.Center);
            }
        }

        void DrawHeadingTape(DisplayPainter p)
        {
            p.FillRect(HdgL, HdgTop, HdgR, HdgBot, Scale);
            p.PushClipRect(HdgL, HdgTop, HdgR, HdgBot);
            int lo = Mathf.FloorToInt((heading - 30f) / 5f) * 5;
            for (int d = lo; d <= heading + 30f; d += 5)
            {
                float x = Cx + (d - heading) * HdgPx;
                bool ten = (d % 10 + 10) % 10 == 0;
                p.Line(new Vector2(x, HdgTop), new Vector2(x, HdgTop + (ten ? 16f : 8f)), 3f, White);
                if (!ten) continue;
                int label = (d % 360 + 360) % 360 / 10;
                p.Text(Num(label), x, HdgTop + 34f, label % 3 == 0 ? 25f : 19f, White, TextAlign.Center);
            }
            p.PopClip();
            p.Line(new Vector2(HdgL, HdgTop), new Vector2(HdgR, HdgTop), 2f, White);
            p.Line(new Vector2(Cx, 742f), new Vector2(Cx, 792f), 4.5f, Yellow);
        }

        static readonly Vector2[] WingL =
        {
            new Vector2(176f, 422f), new Vector2(257f, 422f), new Vector2(257f, 458f),
            new Vector2(243f, 458f), new Vector2(243f, 434f), new Vector2(176f, 434f),
        };
        static readonly Vector2[] WingR = Mirror(WingL);
        static readonly Vector2[] CentreSquare =
        {
            new Vector2(Cx - 7f, Cy - 7f), new Vector2(Cx + 7f, Cy - 7f),
            new Vector2(Cx + 7f, Cy + 7f), new Vector2(Cx - 7f, Cy + 7f),
        };

        static Vector2[] Mirror(Vector2[] pts)
        {
            var m = new Vector2[pts.Length];
            for (int i = 0; i < pts.Length; i++) m[pts.Length - 1 - i] = new Vector2(2f * Cx - pts[i].x, pts[i].y);
            return m;
        }

        void DrawAircraftSymbol(DisplayPainter p)
        {
            // Black-filled, yellow-outlined wings and centre square.
            p.FillRect(176f, 422f, 257f, 434f, Black);
            p.FillRect(243f, 434f, 257f, 458f, Black);
            p.FillRect(2f * Cx - 257f, 422f, 2f * Cx - 176f, 434f, Black);
            p.FillRect(2f * Cx - 257f, 434f, 2f * Cx - 243f, 458f, Black);
            p.FillPolygon(CentreSquare, Black);
            p.Polyline(WingL, 3f, Yellow, true);
            p.Polyline(WingR, 3f, Yellow, true);
            p.Polyline(CentreSquare, 3f, Yellow, true);
        }

        void DrawRadioAltitude(DisplayPainter p)
        {
            if (raFt >= 2500f) return;
            float ra = Mathf.Max(raFt, 0f);
            int shown = ra < 5f ? Mathf.RoundToInt(ra)
                      : ra < 50f ? Mathf.RoundToInt(ra / 5f) * 5
                      : Mathf.RoundToInt(ra / 10f) * 10;
            p.Text(Num(shown), Cx, 620f, 30f, Green, TextAlign.Center);
        }

        // ---- cached number strings, so a frame allocates nothing ----

        static readonly string[] num = new string[3000], pad2 = new string[100], pad3 = new string[1000];

        static string Num(int v) =>
            v >= 0 && v < num.Length ? num[v] ??= v.ToString() : v.ToString();

        static string Pad2(int v) =>
            v >= 0 && v < pad2.Length ? pad2[v] ??= v.ToString("00") : v.ToString("00");

        static string Pad3(int v) =>
            v >= 0 && v < pad3.Length ? pad3[v] ??= v.ToString("000") : v.ToString("000");
    }
}
