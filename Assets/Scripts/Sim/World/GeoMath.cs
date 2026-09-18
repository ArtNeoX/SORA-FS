using System;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Geodetic to scene-metre conversion for a local-tangent-plane ("flat Earth") scene.
    ///
    /// Scene frame: +X east, +Y up, +Z north, one unit = one metre. Headings are degrees
    /// clockwise from true north, which maps straight onto Unity's yaw.
    ///
    /// Everything is computed in double. A single-precision latitude already costs ~0.5 m of
    /// position at Osaka's latitude, and the pivot-to-airport deltas here run to tens of
    /// kilometres, so the inputs and the intermediate maths have to stay double; only the
    /// final Vector3 narrows to float (~8 mm of quantisation at 100 km, harmless).
    ///
    /// This is an equirectangular tangent-plane projection, not a true Mercator. Mercator
    /// stretches north-south by 1/cos(lat) - at 34.6 deg that is a 21% scale error unless it
    /// is divided back out - and it buys nothing at scene scale. Scale factors come from the
    /// WGS84 radii of curvature evaluated at the mean latitude of the two points, which cancels
    /// the first-order scale drift: a 30 km leg lands within a metre and a 100 km leg within a
    /// few metres, against an airport model whose own survey accuracy is no better.
    public static class GeoMath
    {
        /// WGS84 semi-major axis, metres.
        public const double EquatorialRadius = 6378137.0;

        /// WGS84 flattening.
        public const double Flattening = 1.0 / 298.257223563;

        /// First eccentricity squared, e^2 = f(2 - f).
        public static readonly double EccentricitySquared = Flattening * (2.0 - Flattening);

        const double Deg2Rad = Math.PI / 180.0;
        const double Rad2Deg = 180.0 / Math.PI;

        // ---- scale factors -------------------------------------------------------

        /// Metres per degree of latitude (meridional radius of curvature) at this latitude.
        public static double MetresPerDegreeLatitude(double latitudeDeg)
        {
            double s = Math.Sin(latitudeDeg * Deg2Rad);
            double w = 1.0 - EccentricitySquared * s * s;
            double meridional = EquatorialRadius * (1.0 - EccentricitySquared) / (w * Math.Sqrt(w));
            return meridional * Deg2Rad;
        }

        /// Metres per degree of longitude (prime-vertical radius times cos lat) at this latitude.
        public static double MetresPerDegreeLongitude(double latitudeDeg)
        {
            double lat = latitudeDeg * Deg2Rad;
            double s = Math.Sin(lat);
            double primeVertical = EquatorialRadius / Math.Sqrt(1.0 - EccentricitySquared * s * s);
            return primeVertical * Math.Cos(lat) * Deg2Rad;
        }

        // ---- geodetic to scene ---------------------------------------------------

        /// Offset of (lat, lon) from the reference point, in metres east and north.
        public static void GeoToLocal(double latitudeDeg, double longitudeDeg,
                                      double refLatitudeDeg, double refLongitudeDeg,
                                      out double east, out double north)
        {
            double meanLat = (latitudeDeg + refLatitudeDeg) * 0.5;
            east = WrapLongitudeDelta(longitudeDeg - refLongitudeDeg) * MetresPerDegreeLongitude(meanLat);
            north = (latitudeDeg - refLatitudeDeg) * MetresPerDegreeLatitude(meanLat);
        }

        /// Offset of (lat, lon) from the reference point as a scene vector (Y is always 0).
        public static Vector3 GeoToLocal(double latitudeDeg, double longitudeDeg,
                                         double refLatitudeDeg, double refLongitudeDeg)
        {
            GeoToLocal(latitudeDeg, longitudeDeg, refLatitudeDeg, refLongitudeDeg,
                       out double east, out double north);
            return new Vector3((float)east, 0f, (float)north);
        }

        // ---- scene to geodetic ---------------------------------------------------

        /// Geodetic position of a scene offset given in metres east/north of the reference.
        /// Latitude is solved twice so it uses the same mean-latitude scale as GeoToLocal and
        /// round-trips to within a millimetre.
        public static void LocalToGeo(double east, double north,
                                      double refLatitudeDeg, double refLongitudeDeg,
                                      out double latitudeDeg, out double longitudeDeg)
        {
            double firstGuess = refLatitudeDeg + north / MetresPerDegreeLatitude(refLatitudeDeg);
            double meanLat = (firstGuess + refLatitudeDeg) * 0.5;

            latitudeDeg = refLatitudeDeg + north / MetresPerDegreeLatitude(meanLat);
            longitudeDeg = WrapLongitude(refLongitudeDeg + east / MetresPerDegreeLongitude(meanLat));
        }

        /// Geodetic position of a scene point relative to the reference point.
        public static void LocalToGeo(Vector3 local,
                                      double refLatitudeDeg, double refLongitudeDeg,
                                      out double latitudeDeg, out double longitudeDeg)
        {
            LocalToGeo(local.x, local.z, refLatitudeDeg, refLongitudeDeg,
                       out latitudeDeg, out longitudeDeg);
        }

        // ---- headings ------------------------------------------------------------

        /// Rotation for a true heading in degrees clockwise from north. +Z is north, so the
        /// heading is the yaw directly.
        public static Quaternion HeadingToRotation(double headingDeg)
        {
            return Quaternion.Euler(0f, (float)WrapHeading(headingDeg), 0f);
        }

        /// True heading of a scene direction, degrees clockwise from north in [0, 360).
        public static float HeadingFromDirection(Vector3 direction)
        {
            if (Mathf.Approximately(direction.x, 0f) && Mathf.Approximately(direction.z, 0f)) return 0f;
            return (float)WrapHeading(Mathf.Atan2(direction.x, direction.z) * Rad2Deg);
        }

        // ---- wrapping and validation ---------------------------------------------

        /// Folds a longitude difference into [-180, 180] so a leg across the antimeridian does
        /// not come out as a 359-degree jump.
        public static double WrapLongitudeDelta(double deltaDeg)
        {
            deltaDeg %= 360.0;
            if (deltaDeg > 180.0) deltaDeg -= 360.0;
            else if (deltaDeg < -180.0) deltaDeg += 360.0;
            return deltaDeg;
        }

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

        public static bool IsValidLatitude(double latitudeDeg)
        {
            return !double.IsNaN(latitudeDeg) && latitudeDeg >= -90.0 && latitudeDeg <= 90.0;
        }

        public static bool IsValidLongitude(double longitudeDeg)
        {
            return !double.IsNaN(longitudeDeg) && longitudeDeg >= -180.0 && longitudeDeg <= 180.0;
        }

        // ---- reference -----------------------------------------------------------

        /// Great-circle distance in metres on a sphere of radius `a`. Used for the gross
        /// "is this airport even in this region" bound, not as a precision reference: a sphere
        /// cannot match the ellipsoid for every azimuth, and at mid latitudes an east-west leg
        /// comes out about 0.1% short of the ellipsoidal answer, so do not read a disagreement
        /// with GeoToLocal of that order as an error in the projection.
        public static double HaversineDistance(double latA, double lonA, double latB, double lonB)
        {
            double dLat = (latB - latA) * Deg2Rad;
            double dLon = WrapLongitudeDelta(lonB - lonA) * Deg2Rad;
            double sinLat = Math.Sin(dLat * 0.5);
            double sinLon = Math.Sin(dLon * 0.5);
            double h = sinLat * sinLat +
                       Math.Cos(latA * Deg2Rad) * Math.Cos(latB * Deg2Rad) * sinLon * sinLon;
            return 2.0 * EquatorialRadius * Math.Asin(Math.Sqrt(Math.Min(1.0, h)));
        }
    }
}
