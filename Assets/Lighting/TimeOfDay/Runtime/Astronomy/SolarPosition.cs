using System;
using CirrusFS.Geo;

namespace CirrusFS.TimeOfDay
{
    /// Where the Sun is at a given instant, geocentric.
    public struct SunEphemeris
    {
        public double ApparentLongitude;   // degrees, ecliptic of date
        public double RightAscension;      // degrees, equator of date
        public double Declination;         // degrees
        public double DistanceAu;
        public double Gast;                // Greenwich apparent sidereal time used, degrees

        /// Unit vector from the Earth's centre toward the Sun, ECEF.
        public Double3 EcefDirection;

        /// Geographic point the Sun is overhead, degrees.
        public double SubsolarLatitude => Declination;
        public double SubsolarLongitude => AstroTime.Wrap360(RightAscension - Gast + 180.0) - 180.0;
    }

    /// Solar position from Meeus, "Astronomical Algorithms" ch. 25 (the algorithm behind the
    /// NOAA solar calculator). Accurate to ~0.01 deg between 1900 and 2100, which is 1/50 of
    /// the Sun's diameter. Parallax (8.8") is ignored: the Sun's direction is the same for
    /// every observer to well under a pixel.
    public static class SolarPosition
    {
        public const double AuMetres = 149597870700.0;

        /// Mean angular radius at 1 AU, degrees.
        public const double AngularRadiusAt1Au = 0.2666;

        public static SunEphemeris Compute(double julianDate)
        {
            double t = AstroTime.Centuries(julianDate);
            const double d2r = GeoMath.Deg2Rad;

            double l0 = 280.46646 + t * (36000.76983 + t * 0.0003032);
            double m = 357.52911 + t * (35999.05029 - t * 0.0001537);
            double e = 0.016708634 - t * (0.000042037 + t * 0.0000001267);

            double mr = m * d2r;
            double c = (1.914602 - t * (0.004817 + t * 0.000014)) * Math.Sin(mr)
                       + (0.019993 - t * 0.000101) * Math.Sin(2 * mr)
                       + 0.000289 * Math.Sin(3 * mr);

            double trueLongitude = l0 + c;
            double trueAnomaly = m + c;
            double r = 1.000001018 * (1 - e * e) / (1 + e * Math.Cos(trueAnomaly * d2r));

            // Apparent longitude: aberration (-20.5") and nutation in longitude, folded into
            // the two constants of Meeus 25.8.
            double omega = (125.04 - 1934.136 * t) * d2r;
            double lambda = trueLongitude - 0.00569 - 0.00478 * Math.Sin(omega);
            double epsilon = AstroTime.MeanObliquity(t) + 0.00256 * Math.Cos(omega);

            AstroTime.EclipticToEquatorial(lambda, 0.0, epsilon, out double ra, out double dec);
            double gast = AstroTime.GreenwichApparentSidereal(julianDate);

            return new SunEphemeris
            {
                ApparentLongitude = AstroTime.Wrap360(lambda),
                RightAscension = ra,
                Declination = dec,
                DistanceAu = r,
                Gast = gast,
                EcefDirection = AstroTime.EquatorialToEcef(ra, dec, gast),
            };
        }
    }
}
