using System;

namespace CirrusFS.Geo
{
    /// WGS84 geodesy, all in double. The single definition of where things are on Earth:
    /// the aircraft, the camera, the ocean and the terrain tiles still to come all reach
    /// ECEF through these functions and nothing else.
    ///
    /// Conventions:
    ///  - ECEF: right-handed, metres, origin at the Earth's centre, +X through (0 N, 0 E),
    ///    +Y through (0 N, 90 E), +Z through the North Pole. The Earth does not rotate.
    ///  - Latitude/longitude are geodetic degrees; height is metres above the WGS84 ellipsoid
    ///    (no geoid, so "sea level" here is h = 0 on the ellipsoid).
    ///  - Heading is degrees clockwise from true north; pitch is positive nose up; roll is
    ///    positive right wing down.
    public static class GeoMath
    {
        /// WGS84 semi-major axis, metres.
        public const double A = 6378137.0;

        /// WGS84 flattening.
        public const double F = 1.0 / 298.257223563;

        /// Semi-minor (polar) axis, metres: 6356752.314245...
        public const double B = A * (1.0 - F);

        /// First eccentricity squared, e^2 = f(2 - f).
        public const double E2 = F * (2.0 - F);

        /// Second eccentricity squared, e'^2 = e^2 / (1 - e^2).
        public const double Ep2 = E2 / (1.0 - E2);

        /// Standard gravity, m/s^2. Applied toward the Earth's centre.
        public const double StandardGravity = 9.80665;

        public const double Deg2Rad = Math.PI / 180.0;
        public const double Rad2Deg = 180.0 / Math.PI;

        // ---- geodetic <-> ECEF ---------------------------------------------------

        /// Prime-vertical radius of curvature N(lat).
        public static double PrimeVerticalRadius(double latitudeDeg)
        {
            double s = Math.Sin(latitudeDeg * Deg2Rad);
            return A / Math.Sqrt(1.0 - E2 * s * s);
        }

        public static Double3 GeodeticToEcef(double latitudeDeg, double longitudeDeg, double height)
        {
            double lat = latitudeDeg * Deg2Rad;
            double lon = longitudeDeg * Deg2Rad;
            double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
            double n = A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);

            return new Double3((n + height) * cosLat * Math.Cos(lon),
                               (n + height) * cosLat * Math.Sin(lon),
                               (n * (1.0 - E2) + height) * sinLat);
        }

        /// ECEF to geodetic. Bowring's parametric-latitude start, then fixed-point refinement
        /// until the latitude stops moving. Bowring alone is ~1e-10 rad for terrestrial heights;
        /// the refinement keeps it sub-millimetre out to geostationary distances and beyond.
        /// The height formula `p cos(lat) + z sin(lat) - a sqrt(1 - e^2 sin^2(lat))` is used
        /// rather than `p / cos(lat) - N` because it stays well conditioned at the poles.
        public static void EcefToGeodetic(Double3 ecef, out double latitudeDeg, out double longitudeDeg,
                                          out double height)
        {
            double x = ecef.x, y = ecef.y, z = ecef.z;
            double p = Math.Sqrt(x * x + y * y);

            if (p < 1e-9)
            {
                // On the polar axis longitude is undefined; report 0.
                latitudeDeg = z >= 0.0 ? 90.0 : -90.0;
                longitudeDeg = 0.0;
                height = Math.Abs(z) - B;
                return;
            }

            longitudeDeg = Math.Atan2(y, x) * Rad2Deg;

            double theta = Math.Atan2(z * A, p * B);
            double sinT = Math.Sin(theta), cosT = Math.Cos(theta);
            double lat = Math.Atan2(z + Ep2 * B * sinT * sinT * sinT,
                                    p - E2 * A * cosT * cosT * cosT);

            double h = 0.0;
            for (int i = 0; i < 8; i++)
            {
                double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
                double w = Math.Sqrt(1.0 - E2 * sinLat * sinLat);
                double n = A / w;
                h = p * cosLat + z * sinLat - A * w;

                double next = Math.Atan2(z, p * (1.0 - E2 * n / (n + h)));
                bool converged = Math.Abs(next - lat) < 1e-15;
                lat = next;
                if (converged) break;
            }

            {
                double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
                h = p * cosLat + z * sinLat - A * Math.Sqrt(1.0 - E2 * sinLat * sinLat);
            }

            latitudeDeg = lat * Rad2Deg;
            height = h;
        }

        /// The point on the ellipsoid surface (h = 0) directly below/above `ecef`.
        public static Double3 SurfacePointBelow(Double3 ecef)
        {
            EcefToGeodetic(ecef, out double lat, out double lon, out _);
            return GeodeticToEcef(lat, lon, 0.0);
        }

        /// Where the geocentric ray through `direction` meets the h = 0 ellipsoid. Used to build
        /// the ocean: exact, cheap, and no iteration.
        public static Double3 EllipsoidPointAlong(Double3 direction)
        {
            double t = 1.0 / Math.Sqrt(direction.x * direction.x / (A * A) +
                                       direction.y * direction.y / (A * A) +
                                       direction.z * direction.z / (B * B));
            return direction * t;
        }

        /// Outward ellipsoid normal (geodetic up) at a surface point.
        public static Double3 EllipsoidNormalAt(Double3 surfacePoint)
        {
            return new Double3(surfacePoint.x / (A * A),
                               surfacePoint.y / (A * A),
                               surfacePoint.z / (B * B)).Normalized;
        }

        // ---- local frame ---------------------------------------------------------

        /// East/north/up unit vectors, in ECEF, at a geodetic position. Up is the ellipsoid
        /// normal (geodetic), not the geocentric direction.
        public static void EnuBasis(double latitudeDeg, double longitudeDeg,
                                    out Double3 east, out Double3 north, out Double3 up)
        {
            double lat = latitudeDeg * Deg2Rad;
            double lon = longitudeDeg * Deg2Rad;
            double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
            double sinLon = Math.Sin(lon), cosLon = Math.Cos(lon);

            east = new Double3(-sinLon, cosLon, 0.0);
            north = new Double3(-sinLat * cosLon, -sinLat * sinLon, cosLat);
            up = new Double3(cosLat * cosLon, cosLat * sinLon, sinLat);
        }

        public static void EnuBasisAt(Double3 ecef, out Double3 east, out Double3 north, out Double3 up)
        {
            EcefToGeodetic(ecef, out double lat, out double lon, out _);
            EnuBasis(lat, lon, out east, out north, out up);
        }

        /// ECEF vector -> (east, north, up) components at a geodetic position.
        public static Double3 EcefToEnu(Double3 v, double latitudeDeg, double longitudeDeg)
        {
            EnuBasis(latitudeDeg, longitudeDeg, out Double3 e, out Double3 n, out Double3 u);
            return new Double3(Double3.Dot(v, e), Double3.Dot(v, n), Double3.Dot(v, u));
        }

        /// (east, north, up) components at a geodetic position -> ECEF vector.
        public static Double3 EnuToEcef(Double3 enu, double latitudeDeg, double longitudeDeg)
        {
            EnuBasis(latitudeDeg, longitudeDeg, out Double3 e, out Double3 n, out Double3 u);
            return e * enu.x + n * enu.y + u * enu.z;
        }

        // ---- attitude ------------------------------------------------------------

        /// Body axes in ECEF for an attitude given as heading/pitch/roll at a geodetic position.
        /// Axes follow Unity's body convention: right (+X), up (+Y), forward (+Z).
        public static void HeadingPitchRollToEcef(double latitudeDeg, double longitudeDeg,
                                                  double headingDeg, double pitchDeg, double rollDeg,
                                                  out Double3 forward, out Double3 up, out Double3 right)
        {
            double h = headingDeg * Deg2Rad, p = pitchDeg * Deg2Rad, r = rollDeg * Deg2Rad;
            double sinH = Math.Sin(h), cosH = Math.Cos(h);
            double sinP = Math.Sin(p), cosP = Math.Cos(p);
            double sinR = Math.Sin(r), cosR = Math.Cos(r);

            // In ENU (right-handed). Level right wing points along heading + 90.
            var f = new Double3(sinH * cosP, cosH * cosP, sinP);
            var right0 = new Double3(cosH, -sinH, 0.0);
            Double3 up0 = Double3.Cross(right0, f);

            // Positive roll drops the right wing: right gains a downward component.
            Double3 rightEnu = right0 * cosR - up0 * sinR;
            Double3 upEnu = up0 * cosR + right0 * sinR;

            forward = EnuToEcef(f, latitudeDeg, longitudeDeg);
            up = EnuToEcef(upEnu, latitudeDeg, longitudeDeg);
            right = EnuToEcef(rightEnu, latitudeDeg, longitudeDeg);
        }

        /// Heading/pitch/roll of an attitude given by its ECEF forward and up axes, measured in
        /// the local frame at a geodetic position.
        public static void EcefToHeadingPitchRoll(double latitudeDeg, double longitudeDeg,
                                                  Double3 forward, Double3 up,
                                                  out double headingDeg, out double pitchDeg, out double rollDeg)
        {
            Double3 f = EcefToEnu(forward.Normalized, latitudeDeg, longitudeDeg);
            Double3 u = EcefToEnu(up.Normalized, latitudeDeg, longitudeDeg);

            pitchDeg = Math.Asin(Math.Max(-1.0, Math.Min(1.0, f.z))) * Rad2Deg;

            // Straight up or down, heading is carried by the up vector instead.
            double h = Math.Abs(f.z) < 0.999999
                ? Math.Atan2(f.x, f.y)
                : Math.Atan2(-u.x * Math.Sign(f.z), -u.y * Math.Sign(f.z));
            headingDeg = WrapHeading(h * Rad2Deg);

            var right0 = new Double3(Math.Cos(h), -Math.Sin(h), 0.0);
            Double3 up0 = Double3.Cross(right0, f);
            rollDeg = Math.Atan2(Double3.Dot(u, right0), Double3.Dot(u, up0)) * Rad2Deg;
        }

        // ---- wrapping and formatting ---------------------------------------------

        /// Folds a longitude into [-180, 180).
        public static double WrapLongitude(double longitudeDeg)
        {
            double wrapped = (longitudeDeg + 180.0) % 360.0;
            if (wrapped < 0.0) wrapped += 360.0;
            return wrapped - 180.0;
        }

        /// Folds a heading into [0, 360).
        public static double WrapHeading(double headingDeg)
        {
            double wrapped = headingDeg % 360.0;
            if (wrapped < 0.0) wrapped += 360.0;
            return wrapped;
        }

        public static bool IsValidLatitude(double latitudeDeg) =>
            !double.IsNaN(latitudeDeg) && latitudeDeg >= -90.0 && latitudeDeg <= 90.0;

        public static bool IsValidLongitude(double longitudeDeg) =>
            !double.IsNaN(longitudeDeg) && longitudeDeg >= -180.0 && longitudeDeg <= 180.0;

        /// 37.09 -> "37°05'24.00\"N".
        public static string FormatDms(double degrees, bool isLatitude)
        {
            char hemi = isLatitude ? (degrees >= 0.0 ? 'N' : 'S') : (degrees >= 0.0 ? 'E' : 'W');
            double v = Math.Abs(degrees);
            int d = (int)Math.Floor(v);
            double minutes = (v - d) * 60.0;
            int m = (int)Math.Floor(minutes);
            double s = (minutes - m) * 60.0;

            // Rounding 59.995" up must carry, or it prints 60.00".
            if (s >= 59.995) { s = 0.0; m++; }
            if (m >= 60) { m = 0; d++; }

            // Invariant: a comma-decimal locale would otherwise print 24,00".
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "{0}°{1:00}'{2:00.00}\"{3}", d, m, s, hemi);
        }

        /// Distance from a point `height` metres above a sphere of the equatorial radius to its
        /// geometric horizon. Used for far-plane sizing, not navigation.
        public static double HorizonDistance(double height)
        {
            double h = Math.Max(0.0, height);
            return Math.Sqrt(h * (2.0 * A + h));
        }
    }
}
