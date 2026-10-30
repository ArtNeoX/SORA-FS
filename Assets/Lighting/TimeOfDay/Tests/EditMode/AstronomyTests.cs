using System;
using CirrusFS.Geo;
using NUnit.Framework;
using UnityEngine;

namespace CirrusFS.TimeOfDay.Tests
{
    /// Reference values are the worked examples in Meeus, "Astronomical Algorithms" (2nd ed.),
    /// plus a few dates whose answer is known from the calendar (equinox, solstice).
    public class AstronomyTests
    {
        static double Jd(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) =>
            AstroTime.JulianDate(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc));

        static double AngleDiff(double a, double b) => Math.Abs(((a - b) % 360.0 + 540.0) % 360.0 - 180.0);

        [Test]
        public void JulianDate_MatchesMeeus7a()
        {
            // Meeus 7.a: 1957 Oct 4.81 = JD 2436116.31
            double jd = AstroTime.JulianDate(new DateTime(1957, 10, 4, 0, 0, 0, DateTimeKind.Utc).AddDays(0.81));
            Assert.AreEqual(2436116.31, jd, 1e-6);
            Assert.AreEqual(AstroTime.J2000, Jd(2000, 1, 1, 12), 1e-9);
        }

        [Test]
        public void Sidereal_MatchesMeeus12()
        {
            Assert.AreEqual(197.693195, AstroTime.GreenwichMeanSidereal(Jd(1987, 4, 10)), 1e-5);      // 12.a mean
            Assert.AreEqual(197.692229, AstroTime.GreenwichApparentSidereal(Jd(1987, 4, 10)), 1e-4);  // 12.a apparent
            Assert.AreEqual(128.7378734, AstroTime.GreenwichMeanSidereal(Jd(1987, 4, 10, 19, 21)), 1e-5); // 12.b
        }

        [Test]
        public void Sun_MatchesMeeus25a()
        {
            SunEphemeris s = SolarPosition.Compute(2448908.5); // 1992 Oct 13.0
            Assert.AreEqual(199.90895, s.ApparentLongitude, 1e-4);
            Assert.AreEqual(198.38083, s.RightAscension, 1e-4);
            Assert.AreEqual(-7.78507, s.Declination, 1e-4);
            Assert.AreEqual(0.99766, s.DistanceAu, 1e-5);
        }

        [Test]
        public void Sun_DeclinationAtEquinoxAndSolstice()
        {
            Assert.AreEqual(0.0, SolarPosition.Compute(Jd(2024, 3, 20, 3, 6)).Declination, 0.01);
            Assert.AreEqual(23.4386, SolarPosition.Compute(Jd(2024, 6, 20, 20, 51)).Declination, 0.01);
        }

        [Test]
        public void Sun_SubsolarPointFollowsEquationOfTime()
        {
            // 2024 Mar 20 the equation of time is about -7.4 min: the sun crosses Greenwich at
            // ~12:07:24 UTC, so at 12:00 it stands ~1.85 deg east.
            SunEphemeris s = SolarPosition.Compute(Jd(2024, 3, 20, 12));
            Assert.AreEqual(1.85, s.SubsolarLongitude, 0.05);

            Double3 expected = GeoMath.GeodeticToEcef(s.SubsolarLatitude, s.SubsolarLongitude, 0.0).Normalized;
            Assert.Greater(Double3.Dot(expected, s.EcefDirection), Math.Cos(0.2 * GeoMath.Deg2Rad));
        }

        [Test]
        public void Moon_MatchesMeeus47a()
        {
            MoonEphemeris m = LunarPosition.Compute(2448724.5); // 1992 Apr 12.0
            Assert.Less(AngleDiff(m.ApparentLongitude, 133.167265), 0.03, "longitude");
            Assert.AreEqual(-3.229126, m.Latitude, 0.03, "latitude");
            Assert.AreEqual(368409.7, m.DistanceKm, 500.0, "distance");
            Assert.Less(AngleDiff(m.RightAscension, 134.688470), 0.03, "right ascension");
            Assert.AreEqual(13.768368, m.Declination, 0.03, "declination");
        }

        [Test]
        public void Moon_NewAtSolarEclipse_FullAtLunarEclipse()
        {
            double newMoon = LunarPosition.PhaseAngle(SolarPosition.Compute(Jd(2024, 4, 8, 18, 18)),
                                                      LunarPosition.Compute(Jd(2024, 4, 8, 18, 18)));
            double fullMoon = LunarPosition.PhaseAngle(SolarPosition.Compute(Jd(2025, 3, 14, 6, 59)),
                                                       LunarPosition.Compute(Jd(2025, 3, 14, 6, 59)));
            Assert.Greater(newMoon, 178.0);
            Assert.Less(fullMoon, 2.0);
            Assert.AreEqual(1.0, LunarPosition.RelativeBrightness(0.0), 1e-9);
            Assert.AreEqual(0.09, LunarPosition.RelativeBrightness(90.0), 0.01);
        }

        [Test]
        public void Moon_TopocentricParallaxIsAboutOneDegreeAtHorizon()
        {
            MoonEphemeris m = LunarPosition.Compute(2448724.5);
            Double3 moonDir = m.EcefPosition.Normalized;

            // Observer on the equator 90 deg of longitude away from the sub-lunar point: moon on
            // the horizon, where parallax is the full horizontal parallax, asin(Re / distance).
            double subLon = Math.Atan2(moonDir.y, moonDir.x) * GeoMath.Rad2Deg;
            Double3 observer = GeoMath.GeodeticToEcef(0.0, subLon + 90.0, 0.0);
            Double3 topo = (m.EcefPosition - observer).Normalized;
            double shift = Math.Acos(Double3.Dot(topo, moonDir)) * GeoMath.Rad2Deg;
            double horizontalParallax = Math.Asin(GeoMath.A / (m.DistanceKm * 1000.0)) * GeoMath.Rad2Deg;
            Assert.AreEqual(horizontalParallax, shift, 0.1);
        }

        [Test]
        public void Refraction_HalfDegreeAtHorizon_ZeroAtZenith_Continuous()
        {
            // Saemundsson takes the true elevation: 29' at true 0 deg, and 34' for a body whose
            // apparent place is on the horizon (true elevation ~ -0.57 deg).
            Assert.AreEqual(0.48, Refraction.Degrees(0.0, 0.0), 0.01);
            Assert.AreEqual(0.0, -0.57 + Refraction.Degrees(-0.57, 0.0), 0.01);
            Assert.Less(Refraction.Degrees(89.0, 0.0), 0.001);
            Assert.AreEqual(0.0, Refraction.Degrees(-7.0, 0.0), 1e-9);

            double prev = Refraction.Degrees(-8.0, 0.0);
            for (double e = -8.0; e <= 10.0; e += 0.01)
            {
                double r = Refraction.Degrees(e, 0.0);
                Assert.Less(Math.Abs(r - prev), 0.01, $"jump at {e:F2}");
                prev = r;
            }
        }

        [Test]
        public void Transmittance_ClearSkyValues()
        {
            var model = new AtmosphereModel(AtmosphereParameters.Earth);
            Vector3 zenith = model.Transmittance(0.0, 1.0);
            Vector3 horizon = model.HorizonTransmittance(0.0);

            // Sea-level zenith: ~0.94 red, ~0.87 green, ~0.76 blue (ozone + Rayleigh + Mie).
            Assert.AreEqual(0.94, zenith.x, 0.01);
            Assert.AreEqual(0.87, zenith.y, 0.01);
            Assert.AreEqual(0.76, zenith.z, 0.01);
            // Grazing: deep red, blue essentially gone.
            Assert.Greater(horizon.x, horizon.y * 5f);
            Assert.Less(horizon.z, 1e-3f);
            // Below the horizon: nothing.
            Assert.AreEqual(Vector3.zero, model.Transmittance(0.0, -0.1));
            // Thinner air above: more light at 12 km than at sea level.
            Assert.Greater(model.Transmittance(12000.0, 0.1).z, model.Transmittance(0.0, 0.1).z);
        }

        [Test]
        public void Transmittance_FromAboveTheAtmosphere()
        {
            var model = new AtmosphereModel(AtmosphereParameters.Earth);
            // Looking up from orbit: nothing in the way.
            Assert.AreEqual(Vector3.one, model.Transmittance(400000.0, 1.0));
            // Grazing the limb from orbit: through the atmosphere and out again, so dimmer than
            // straight down to space but not black. Horizon from 400 km is ~19.8 deg below level.
            Vector3 limb = model.Transmittance(400000.0, -Math.Sin(19.0 * GeoMath.Deg2Rad));
            Assert.Greater(limb.x, 0.01f);
            Assert.Less(limb.x, 1.0f);
            // Straight down hits the ground.
            Assert.AreEqual(Vector3.zero, model.Transmittance(400000.0, -1.0));
        }

        [Test]
        public void Haze_GivesTheRequestedVisibility()
        {
            AtmosphereParameters clear = AtmosphereParameters.Earth;
            foreach (double km in new[] { 0.5, 2.0, 10.0, 40.0 })
            {
                AtmosphereParameters p = clear;
                p.hazeExtinction = AtmosphereModel.HazeExtinctionForVisibility(clear, km);
                var model = new AtmosphereModel(p);
                // Koschmieder with the ICAO 5% threshold: extinction(550 nm) * V = 3.0 at sea level.
                Assert.AreEqual(AtmosphereModel.VisibilityConstant / (km * 1000.0), model.Extinction(0.0).y, 1e-3 / (km * 1000.0), km + " km");
                Assert.AreEqual(km, AtmosphereModel.VisibilityKm(p), km * 1e-4);
                // Above the layer only the clear air is left.
                Assert.AreEqual(new AtmosphereModel(clear).Extinction(3000.0).y, model.Extinction(3000.0).y, 1e-9);
            }
            // Clearer than clear air: no haze at all.
            Assert.AreEqual(0f, AtmosphereModel.HazeExtinctionForVisibility(clear, 200.0));
        }

        [Test]
        public void Haze_DimsAndReddensTheLowSun()
        {
            AtmosphereParameters clear = AtmosphereParameters.Earth;
            AtmosphereParameters hazy = clear;
            hazy.hazeExtinction = AtmosphereModel.HazeExtinctionForVisibility(clear, 5.0);
            double mu = Math.Sin(5.0 * GeoMath.Deg2Rad);
            Vector3 a = new AtmosphereModel(clear).Transmittance(0.0, mu);
            Vector3 b = new AtmosphereModel(hazy).Transmittance(0.0, mu);
            Assert.Less(b.y, a.y * 0.5f);
            // From above the haze top the same sun is barely affected by it.
            Vector3 c = new AtmosphereModel(hazy).Transmittance(3000.0, mu);
            Assert.Greater(c.y, b.y * 3f);
        }

        [Test]
        public void Clock_StaysExactOverAYearOfFastForward()
        {
            var go = new GameObject("clock");
            try
            {
                var clock = go.AddComponent<TimeOfDayClock>();
                clock.zone = TimeOfDayClock.Zone.Utc;
                clock.SetUtc(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                for (int i = 0; i < 365 * 24 * 60; i++) clock.Advance(60.0); // a year in 1-min steps
                Assert.AreEqual(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), clock.Utc);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
