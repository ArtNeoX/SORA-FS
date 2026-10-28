using System;
using NUnit.Framework;
using UnityEngine;

namespace CirrusFS.Geo.Tests
{
    public class GeoMathTests
    {
        const double Mm = 0.001;

        [Test]
        public void EquatorPrimeMeridian_IsSemiMajorAxis()
        {
            Double3 p = GeoMath.GeodeticToEcef(0.0, 0.0, 0.0);

            Assert.AreEqual(6378137.0, p.x, 1e-9);
            Assert.AreEqual(0.0, p.y, 1e-9);
            Assert.AreEqual(0.0, p.z, 1e-9);
        }

        [Test]
        public void NorthPole_IsSemiMinorAxis()
        {
            Double3 p = GeoMath.GeodeticToEcef(90.0, 0.0, 0.0);

            Assert.AreEqual(0.0, p.x, Mm);
            Assert.AreEqual(0.0, p.y, Mm);
            Assert.AreEqual(6356752.314, p.z, Mm);
        }

        [Test]
        public void RoundTrip_RandomPoints_SubMillimetre()
        {
            var random = new System.Random(20261029);
            double worst = 0.0;

            for (int i = 0; i < 20000; i++)
            {
                double lat = random.NextDouble() * 180.0 - 90.0;
                double lon = random.NextDouble() * 360.0 - 180.0;

                // Mostly flight-sim heights, plus a tail out to 40,000 km for the space view.
                double h = i % 4 == 0
                    ? random.NextDouble() * 4.0e7
                    : random.NextDouble() * 20000.0 - 500.0;

                worst = Math.Max(worst, RoundTripError(lat, lon, h));
            }

            Assert.Less(worst, Mm, $"worst round-trip error {worst * 1000.0:F6} mm");
        }

        [Test]
        public void RoundTrip_PolesEquatorAntimeridian_SubMillimetre()
        {
            double[] lats = { -90.0, -89.9999999, -45.0, 0.0, 1e-9, 45.0, 89.9999999, 90.0 };
            double[] lons = { -180.0, -179.9999999, 0.0, 90.0, 179.9999999 };
            double[] heights = { -100.0, 0.0, 2.0, 3000.0, 400000.0, 4.0e7 };

            foreach (double lat in lats)
            foreach (double lon in lons)
            foreach (double h in heights)
            {
                double error = RoundTripError(lat, lon, h);
                Assert.Less(error, Mm, $"({lat}, {lon}, {h}): {error * 1000.0:F6} mm");
            }
        }

        /// Position error in metres of geodetic -> ECEF -> geodetic, measured as horizontal arc
        /// plus height difference so it does not hide behind the conversion it is testing.
        static double RoundTripError(double lat, double lon, double h)
        {
            Double3 ecef = GeoMath.GeodeticToEcef(lat, lon, h);
            GeoMath.EcefToGeodetic(ecef, out double lat2, out double lon2, out double h2);

            double radius = GeoMath.PrimeVerticalRadius(lat) + h;
            double dLat = (lat2 - lat) * GeoMath.Deg2Rad * radius;
            double dLon = GeoMath.WrapLongitude(lon2 - lon);
            dLon = Math.Abs(lat) > 89.99999999 ? 0.0 : dLon * GeoMath.Deg2Rad * radius * Math.Cos(lat * GeoMath.Deg2Rad);

            return Math.Sqrt(dLat * dLat + dLon * dLon) + Math.Abs(h2 - h);
        }

        [Test]
        public void EnuBasis_IsOrthonormalAndRightHanded()
        {
            GeoMath.EnuBasis(37.09, 127.03, out Double3 e, out Double3 n, out Double3 u);

            Assert.AreEqual(1.0, e.Magnitude, 1e-12);
            Assert.AreEqual(1.0, n.Magnitude, 1e-12);
            Assert.AreEqual(1.0, u.Magnitude, 1e-12);
            Assert.AreEqual(0.0, Double3.Dot(e, n), 1e-12);
            Assert.AreEqual(0.0, Double3.Dot(n, u), 1e-12);

            Double3 cross = Double3.Cross(e, n);
            Assert.AreEqual(1.0, Double3.Dot(cross, u), 1e-12, "E x N must be U");
        }

        [Test]
        public void HeadingPitchRoll_RoundTrips()
        {
            var random = new System.Random(7);

            for (int i = 0; i < 2000; i++)
            {
                double lat = random.NextDouble() * 170.0 - 85.0;
                double lon = random.NextDouble() * 360.0 - 180.0;
                double heading = random.NextDouble() * 360.0;
                double pitch = random.NextDouble() * 170.0 - 85.0;
                double roll = random.NextDouble() * 358.0 - 179.0;

                GeoMath.HeadingPitchRollToEcef(lat, lon, heading, pitch, roll, out Double3 f, out Double3 u, out _);
                GeoMath.EcefToHeadingPitchRoll(lat, lon, f, u, out double h2, out double p2, out double r2);

                Assert.AreEqual(0.0, Mathf.DeltaAngle((float)heading, (float)h2), 1e-3);
                Assert.AreEqual(pitch, p2, 1e-6);
                Assert.AreEqual(0.0, Mathf.DeltaAngle((float)roll, (float)r2), 1e-3);
            }
        }

        [Test]
        public void HeadingConvention_ZeroIsNorthNinetyIsEast()
        {
            GeoMath.EnuBasis(37.0, 127.0, out Double3 e, out Double3 n, out Double3 u);

            GeoMath.HeadingPitchRollToEcef(37.0, 127.0, 0.0, 0.0, 0.0, out Double3 f0, out Double3 up0, out Double3 r0);
            Assert.AreEqual(1.0, Double3.Dot(f0, n), 1e-12);
            Assert.AreEqual(1.0, Double3.Dot(up0, u), 1e-12);
            Assert.AreEqual(1.0, Double3.Dot(r0, e), 1e-12);

            GeoMath.HeadingPitchRollToEcef(37.0, 127.0, 90.0, 0.0, 0.0, out Double3 f90, out _, out _);
            Assert.AreEqual(1.0, Double3.Dot(f90, e), 1e-12);

            // Positive roll is right wing down.
            GeoMath.HeadingPitchRollToEcef(37.0, 127.0, 0.0, 0.0, 30.0, out _, out _, out Double3 rRoll);
            Assert.Less(Double3.Dot(rRoll, u), 0.0);
        }

        [Test]
        public void Dms_Formats()
        {
            Assert.AreEqual("37°05'24.00\"N", GeoMath.FormatDms(37.09, true));
            Assert.AreEqual("127°01'48.00\"E", GeoMath.FormatDms(127.03, false));
            Assert.AreEqual("33°52'00.00\"S", GeoMath.FormatDms(-33.866666666667, true));
        }
    }
}
