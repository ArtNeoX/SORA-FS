using System;
using CirrusFS.Geo;
using UnityEngine;

namespace CirrusFS.TimeOfDay
{
    /// The viewer on the globe: where they are in ECEF, which way is up for them, and how ECEF
    /// directions map into the current Unity frame.
    ///
    /// The floating origin re-centres and re-rotates the Unity frame, so Unity +Y is "up" only
    /// at the origin. The observer's own up is the ellipsoid normal under the camera and is the
    /// one every elevation angle here is measured against.
    public struct ObserverFrame
    {
        public Double3 Ecef;
        public double Latitude, Longitude, Height;

        /// Geodetic up at the observer, ECEF.
        public Double3 Up;

        /// Unity +X / +Y / +Z expressed in ECEF.
        Double3 axisX, axisY, axisZ;
        public Double3 AxisX => axisX;
        public Double3 AxisY => axisY;
        public Double3 AxisZ => axisZ;

        /// True when built from GeoOrigin; false when built from a fallback location (edit
        /// mode, or a scene that never spawns).
        public bool FromGeoOrigin;

        public Vector3 ToUnity(Double3 ecefDirection) =>
            new Vector3((float)Double3.Dot(ecefDirection, axisX),
                        (float)Double3.Dot(ecefDirection, axisY),
                        (float)Double3.Dot(ecefDirection, axisZ));

        public Vector3 UpUnity => ToUnity(Up);

        /// Whether GeoOrigin describes the world. Only in play mode: in the editor nothing
        /// places it, and EditMode tests leave it "set" at (0 N, 0 E) until the next domain
        /// reload, which would put the edit-mode sky over the Gulf of Guinea.
        public static bool GeoOriginLive => Application.isPlaying && GeoOrigin.IsSet;

        /// Elevation of an ECEF direction above the observer's local horizontal, degrees.
        public double ElevationOf(Double3 ecefDirection) =>
            Math.Asin(Math.Max(-1.0, Math.Min(1.0, Double3.Dot(ecefDirection, Up)))) * GeoMath.Rad2Deg;

        /// Elevation of the sea-level horizon, degrees (negative above h = 0): the dip.
        public double HorizonElevation
        {
            get
            {
                double r = GeoMath.A + Math.Max(0.0, Height);
                return -Math.Acos(GeoMath.A / r) * GeoMath.Rad2Deg;
            }
        }

        /// Observer at a Unity position in the current GeoOrigin frame.
        public static ObserverFrame FromOrigin(Vector3 unityPosition)
        {
            var f = new ObserverFrame
            {
                Ecef = GeoOrigin.UnityToEcef(unityPosition),
                axisX = GeoOrigin.East,
                axisY = GeoOrigin.Up,
                axisZ = GeoOrigin.North,
                FromGeoOrigin = true,
            };
            f.FillGeodetic();
            return f;
        }

        /// Observer when GeoOrigin is not set: Unity (0,0,0) is taken to sit on the ellipsoid
        /// at (latitude, longitude) with the usual east/up/north axes.
        public static ObserverFrame FromLocation(double latitude, double longitude, Vector3 unityPosition)
        {
            GeoMath.EnuBasis(latitude, longitude, out Double3 e, out Double3 n, out Double3 u);
            Double3 origin = GeoMath.GeodeticToEcef(latitude, longitude, 0.0);
            var f = new ObserverFrame
            {
                Ecef = origin + e * unityPosition.x + u * unityPosition.y + n * unityPosition.z,
                axisX = e,
                axisY = u,
                axisZ = n,
                FromGeoOrigin = false,
            };
            f.FillGeodetic();
            return f;
        }

        void FillGeodetic()
        {
            GeoMath.EcefToGeodetic(Ecef, out Latitude, out Longitude, out Height);
            GeoMath.EnuBasis(Latitude, Longitude, out _, out _, out Up);
        }
    }

    /// Atmospheric refraction near the horizon.
    public static class Refraction
    {
        /// Refraction for a body at true (airless) elevation `elevation` degrees, seen from
        /// `height` metres, in degrees. Saemundsson's formula (0.57 deg at the horizon at sea
        /// level), scaled by pressure through an 8.4 km scale height.
        ///
        /// Below -1 deg the formula diverges; there the value is held and faded to zero by -6
        /// deg so the result stays continuous. From altitude a body can be seen a few degrees
        /// below the horizontal; the true refraction there is somewhat larger than this, which
        /// shifts sunset by under a minute.
        public static double Degrees(double elevation, double height)
        {
            double h = Math.Max(elevation, -1.0);
            double arcmin = 1.02 / Math.Tan((h + 10.3 / (h + 5.11)) * GeoMath.Deg2Rad);
            double pressure = Math.Exp(-Math.Max(0.0, height) / 8400.0);
            double fade = Smoothstep(-6.0, -1.0, elevation);
            return Math.Max(0.0, arcmin / 60.0 * pressure * fade);
        }

        /// Lifts an ECEF direction toward `up` by the refraction at its elevation.
        public static Double3 Apply(Double3 direction, Double3 up, double height)
        {
            double sinE = Double3.Dot(direction, up);
            Double3 horizontal = direction - up * sinE;
            double cosE = horizontal.Magnitude;
            if (cosE < 1e-9) return direction; // at the zenith refraction is zero anyway

            double e = Math.Atan2(sinE, cosE) * GeoMath.Rad2Deg;
            double apparent = (e + Degrees(e, height)) * GeoMath.Deg2Rad;
            return up * Math.Sin(apparent) + horizontal * (Math.Cos(apparent) / cosE);
        }

        static double Smoothstep(double a, double b, double x)
        {
            double t = Math.Max(0.0, Math.Min(1.0, (x - a) / (b - a)));
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
