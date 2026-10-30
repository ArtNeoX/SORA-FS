using System;
using CirrusFS.Geo;

namespace CirrusFS.TimeOfDay
{
    /// Time scales and Earth orientation for the ephemerides. Everything is in double and in
    /// degrees unless a name says otherwise.
    ///
    /// Time scales: the ephemerides are formally in Terrestrial Time and Earth rotation in UT1.
    /// Both are fed UTC here. TT - UTC is ~69 s, which moves the Sun by 0.0008 deg and the Moon
    /// by 0.01 deg; UT1 - UTC is under 0.9 s. Neither is visible, so no leap-second table.
    ///
    /// Earth orientation: the globe in GeoMath does not rotate - ECEF is Earth-fixed. Celestial
    /// bodies are computed in the true equator-and-equinox-of-date frame and turned into ECEF by
    /// Greenwich apparent sidereal time, which is the same as rotating the Earth under them.
    /// Polar motion (< 0.5") is ignored.
    public static class AstroTime
    {
        /// Julian date of J2000.0 (2000 Jan 1, 12:00 TT).
        public const double J2000 = 2451545.0;

        /// Julian date of DateTime.MinValue (0001 Jan 1, 00:00, proleptic Gregorian).
        const double JdOfDateTimeZero = 1721425.5;

        public static double JulianDate(DateTime utc) =>
            JdOfDateTimeZero + utc.Ticks / (double)TimeSpan.TicksPerDay;

        public static DateTime ToDateTime(double julianDate) =>
            new DateTime((long)Math.Round((julianDate - JdOfDateTimeZero) * TimeSpan.TicksPerDay), DateTimeKind.Utc);

        /// Julian centuries since J2000.
        public static double Centuries(double julianDate) => (julianDate - J2000) / 36525.0;

        public static double Wrap360(double degrees)
        {
            double d = degrees % 360.0;
            return d < 0.0 ? d + 360.0 : d;
        }

        /// Greenwich mean sidereal time, degrees (Meeus 12.4).
        public static double GreenwichMeanSidereal(double julianDate)
        {
            double t = Centuries(julianDate);
            return Wrap360(280.46061837 + 360.98564736629 * (julianDate - J2000)
                           + t * t * (0.000387933 - t / 38710000.0));
        }

        /// Nutation in longitude and obliquity, degrees. The four largest terms of IAU 1980
        /// (Meeus ch. 22, 0.5" in dPsi, 0.1" in dEps).
        public static void Nutation(double centuries, out double deltaPsi, out double deltaEpsilon)
        {
            double t = centuries;
            double omega = (125.04452 - 1934.136261 * t) * GeoMath.Deg2Rad;
            double l = (280.4665 + 36000.7698 * t) * GeoMath.Deg2Rad;
            double lm = (218.3165 + 481267.8813 * t) * GeoMath.Deg2Rad;

            deltaPsi = (-17.20 * Math.Sin(omega) - 1.32 * Math.Sin(2 * l)
                        - 0.23 * Math.Sin(2 * lm) + 0.21 * Math.Sin(2 * omega)) / 3600.0;
            deltaEpsilon = (9.20 * Math.Cos(omega) + 0.57 * Math.Cos(2 * l)
                            + 0.10 * Math.Cos(2 * lm) - 0.09 * Math.Cos(2 * omega)) / 3600.0;
        }

        /// Mean obliquity of the ecliptic, degrees (Meeus 22.2).
        public static double MeanObliquity(double centuries)
        {
            double t = centuries;
            return 23.4392911 + t * (-0.0130041667 + t * (-1.6389e-7 + t * 5.0361e-7));
        }

        /// Greenwich apparent sidereal time, degrees: mean sidereal time plus the equation of
        /// the equinoxes.
        public static double GreenwichApparentSidereal(double julianDate)
        {
            double t = Centuries(julianDate);
            Nutation(t, out double dPsi, out double dEps);
            double eps = (MeanObliquity(t) + dEps) * GeoMath.Deg2Rad;
            return Wrap360(GreenwichMeanSidereal(julianDate) + dPsi * Math.Cos(eps));
        }

        /// Ecliptic (of date) to equatorial (of date), degrees.
        public static void EclipticToEquatorial(double longitude, double latitude, double obliquity,
                                                out double rightAscension, out double declination)
        {
            double l = longitude * GeoMath.Deg2Rad, b = latitude * GeoMath.Deg2Rad, e = obliquity * GeoMath.Deg2Rad;
            double sinL = Math.Sin(l), cosB = Math.Cos(b), sinB = Math.Sin(b);

            rightAscension = Wrap360(Math.Atan2(sinL * Math.Cos(e) - Math.Tan(b) * Math.Sin(e), Math.Cos(l)) * GeoMath.Rad2Deg);
            declination = Math.Asin(sinB * Math.Cos(e) + cosB * Math.Sin(e) * sinL) * GeoMath.Rad2Deg;
        }

        /// Unit ECEF direction of a body at (right ascension, declination) of date, for the
        /// given Greenwich apparent sidereal time. The body stands over geographic longitude
        /// RA - GAST.
        public static Double3 EquatorialToEcef(double rightAscension, double declination, double gast)
        {
            double lon = (rightAscension - gast) * GeoMath.Deg2Rad;
            double dec = declination * GeoMath.Deg2Rad;
            double cosDec = Math.Cos(dec);
            return new Double3(cosDec * Math.Cos(lon), cosDec * Math.Sin(lon), Math.Sin(dec));
        }
    }
}
