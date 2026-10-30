using System;
using CirrusFS.Geo;

namespace CirrusFS.TimeOfDay
{
    /// Where the Moon is at a given instant, geocentric.
    public struct MoonEphemeris
    {
        public double ApparentLongitude;   // degrees, ecliptic of date
        public double Latitude;            // degrees, ecliptic
        public double RightAscension;      // degrees, equator of date
        public double Declination;         // degrees
        public double DistanceKm;          // centre to centre

        /// Moon centre, ECEF metres. Subtract the observer's ECEF position for the topocentric
        /// direction: parallax is up to ~1 deg, two Moon diameters, so it matters.
        public Double3 EcefPosition;
    }

    /// Lunar position from Paul Schlyter's "How to compute planetary positions": Keplerian
    /// elements of date plus the twelve largest longitude, five latitude and two distance
    /// perturbations. Accurate to ~0.02 deg (checked against Meeus example 47.a), against the
    /// Moon's 0.5 deg diameter. Meeus ch. 47 in full is ~120 terms for a gain nobody can see.
    public static class LunarPosition
    {
        /// Earth equatorial radius the elements are expressed in, km.
        const double EarthRadiusKm = 6378.14;

        /// Moon mean radius, km.
        public const double RadiusKm = 1737.4;

        public static MoonEphemeris Compute(double julianDate)
        {
            const double d2r = GeoMath.Deg2Rad;
            double d = julianDate - 2451543.5; // Schlyter's day 0 = 1999 Dec 31, 00:00

            // Lunar orbit, equinox of date.
            double n = 125.1228 - 0.0529538083 * d;
            const double i = 5.1454;
            double w = 318.0634 + 0.1643573223 * d;
            const double a = 60.2666;
            const double e = 0.054900;
            double m = AstroTime.Wrap360(115.3654 + 13.0649929509 * d);

            double ecc = SolveKepler(m * d2r, e);
            double xv = a * (Math.Cos(ecc) - e);
            double yv = a * Math.Sqrt(1 - e * e) * Math.Sin(ecc);
            double v = Math.Atan2(yv, xv) * GeoMath.Rad2Deg;
            double r = Math.Sqrt(xv * xv + yv * yv);

            double nr = n * d2r, vw = (v + w) * d2r, ir = i * d2r;
            double xh = r * (Math.Cos(nr) * Math.Cos(vw) - Math.Sin(nr) * Math.Sin(vw) * Math.Cos(ir));
            double yh = r * (Math.Sin(nr) * Math.Cos(vw) + Math.Cos(nr) * Math.Sin(vw) * Math.Cos(ir));
            double zh = r * Math.Sin(vw) * Math.Sin(ir);

            double lon = Math.Atan2(yh, xh) * GeoMath.Rad2Deg;
            double lat = Math.Atan2(zh, Math.Sqrt(xh * xh + yh * yh)) * GeoMath.Rad2Deg;

            // Perturbations. Ms/Ls: Sun's mean anomaly and longitude; Lm: Moon's mean longitude;
            // D: mean elongation; F: argument of latitude.
            double ms = 356.0470 + 0.9856002585 * d;
            double ls = ms + 282.9404 + 4.70935e-5 * d;
            double lm = m + w + n;
            double dd = lm - ls;
            double f = lm - n;

            lon += -1.274 * Sin(m - 2 * dd)      // evection
                   + 0.658 * Sin(2 * dd)         // variation
                   - 0.186 * Sin(ms)             // yearly equation
                   - 0.059 * Sin(2 * m - 2 * dd)
                   - 0.057 * Sin(m - 2 * dd + ms)
                   + 0.053 * Sin(m + 2 * dd)
                   + 0.046 * Sin(2 * dd - ms)
                   + 0.041 * Sin(m - ms)
                   - 0.035 * Sin(dd)             // parallactic equation
                   - 0.031 * Sin(m + ms)
                   - 0.015 * Sin(2 * f - 2 * dd)
                   + 0.011 * Sin(m - 4 * dd);

            lat += -0.173 * Sin(f - 2 * dd)
                   - 0.055 * Sin(m - f - 2 * dd)
                   - 0.046 * Sin(m + f - 2 * dd)
                   + 0.033 * Sin(f + 2 * dd)
                   + 0.017 * Sin(2 * m + f);

            r += -0.58 * Math.Cos((m - 2 * dd) * d2r) - 0.46 * Math.Cos(2 * dd * d2r);

            // Apparent place: add nutation in longitude, use the true obliquity.
            double t = AstroTime.Centuries(julianDate);
            AstroTime.Nutation(t, out double dPsi, out double dEps);
            double lambda = AstroTime.Wrap360(lon + dPsi);
            AstroTime.EclipticToEquatorial(lambda, lat, AstroTime.MeanObliquity(t) + dEps,
                                           out double ra, out double dec);

            double distanceKm = r * EarthRadiusKm;
            double gast = AstroTime.GreenwichApparentSidereal(julianDate);

            return new MoonEphemeris
            {
                ApparentLongitude = lambda,
                Latitude = lat,
                RightAscension = ra,
                Declination = dec,
                DistanceKm = distanceKm,
                EcefPosition = AstroTime.EquatorialToEcef(ra, dec, gast) * (distanceKm * 1000.0),
            };
        }

        /// Phase angle (Sun-Moon-Earth), degrees: 0 at full moon, 180 at new moon.
        public static double PhaseAngle(in SunEphemeris sun, in MoonEphemeris moon)
        {
            Double3 moonDir = moon.EcefPosition.Normalized;
            double cosElongation = Math.Max(-1.0, Math.Min(1.0, Double3.Dot(sun.EcefDirection, moonDir)));
            double elongation = Math.Acos(cosElongation);
            double sunKm = sun.DistanceAu * SolarPosition.AuMetres / 1000.0;

            return Math.Atan2(sunKm * Math.Sin(elongation), moon.DistanceKm - sunKm * Math.Cos(elongation))
                   * GeoMath.Rad2Deg;
        }

        /// Illuminated fraction of the disc for a phase angle in degrees.
        public static double IlluminatedFraction(double phaseAngle) =>
            0.5 * (1.0 + Math.Cos(phaseAngle * GeoMath.Deg2Rad));

        /// Brightness relative to full moon. Allen's magnitude law, m = -12.73 + 0.026|i| +
        /// 4e-9 i^4, which carries the opposition surge: a half moon is ~1/11 of a full one, not
        /// 1/2 as the illuminated fraction would suggest.
        public static double RelativeBrightness(double phaseAngle)
        {
            double i = Math.Abs(phaseAngle);
            return Math.Pow(10.0, -0.4 * (0.026 * i + 4e-9 * i * i * i * i));
        }

        static double SolveKepler(double meanAnomaly, double e)
        {
            double ecc = meanAnomaly + e * Math.Sin(meanAnomaly) * (1.0 + e * Math.Cos(meanAnomaly));
            for (int k = 0; k < 4; k++)
                ecc -= (ecc - e * Math.Sin(ecc) - meanAnomaly) / (1.0 - e * Math.Cos(ecc));
            return ecc;
        }

        static double Sin(double degrees) => Math.Sin(degrees * GeoMath.Deg2Rad);
    }
}
