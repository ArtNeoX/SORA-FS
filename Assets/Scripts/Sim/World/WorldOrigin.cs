using System;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Bookkeeping for where the Unity scene origin actually is on Earth.
    ///
    /// At spawn the origin is the selected runway. Every floating-origin shift moves the whole
    /// world underneath the aircraft, so without this the scene's (0,0,0) would slowly stop
    /// meaning anything. Offsets accumulate in double, so position readouts stay correct after
    /// hours of flight and thousands of kilometres of shifts.
    public static class WorldOrigin
    {
        public static bool HasReference { get; private set; }

        /// Geodetic position the scene origin had when the flight started.
        public static double ReferenceLatitude { get; private set; }
        public static double ReferenceLongitude { get; private set; }

        /// World yaw that true north points along, taken from the flown region. Scene +Z is
        /// north only when this is 0; a rotated scenery root turns the geography with it.
        public static float NorthYawDeg { get; private set; }

        /// Metres the scene origin has travelled east/north from the reference since then.
        public static double OffsetEast { get; private set; }
        public static double OffsetNorth { get; private set; }

        /// Metres the world has been translated vertically, if altitude shifting is enabled.
        public static double OffsetUp { get; private set; }

        public static int ShiftCount { get; private set; }

        /// Raised after every shift with the translation applied to world objects.
        public static event Action<Vector3> Shifted;

        /// Domain reload can be turned off in Enter Play Mode Options, which would carry these
        /// statics from one play session into the next. Reset them explicitly.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            HasReference = false;
            ReferenceLatitude = 0.0;
            ReferenceLongitude = 0.0;
            NorthYawDeg = 0f;
            OffsetEast = 0.0;
            OffsetNorth = 0.0;
            OffsetUp = 0.0;
            ShiftCount = 0;
            Shifted = null;
        }

        /// Declares that scene (0,0,0) is now this geodetic point, in a scene whose north
        /// points along `northYawDeg`. Called once per spawn.
        public static void SetReference(double latitudeDeg, double longitudeDeg, float northYawDeg = 0f)
        {
            ReferenceLatitude = latitudeDeg;
            ReferenceLongitude = longitudeDeg;
            NorthYawDeg = northYawDeg;
            OffsetEast = 0.0;
            OffsetNorth = 0.0;
            OffsetUp = 0.0;
            ShiftCount = 0;
            HasReference = true;
        }

        /// Records a shift. `worldTranslation` is what was added to every world object, so the
        /// origin itself moved by the opposite amount.
        public static void AccumulateShift(Vector3 worldTranslation)
        {
            Vector3 geo = WorldToGeoFrame(worldTranslation);

            OffsetEast -= geo.x;
            OffsetNorth -= geo.z;
            OffsetUp -= geo.y;
            ShiftCount++;

            Shifted?.Invoke(worldTranslation);
        }

        /// Geodetic position of a point in the current scene frame.
        public static void GetGeoPosition(Vector3 scenePosition, out double latitudeDeg, out double longitudeDeg)
        {
            if (!HasReference)
            {
                latitudeDeg = 0.0;
                longitudeDeg = 0.0;
                return;
            }

            Vector3 geo = WorldToGeoFrame(scenePosition);

            GeoMath.LocalToGeo(geo.x + OffsetEast,
                               geo.z + OffsetNorth,
                               ReferenceLatitude,
                               ReferenceLongitude,
                               out latitudeDeg,
                               out longitudeDeg);
        }

        /// Turns a scene-frame vector into the geodetic frame (+X east, +Z north).
        public static Vector3 WorldToGeoFrame(Vector3 world)
        {
            return NorthYawDeg == 0f ? world : Quaternion.Euler(0f, -NorthYawDeg, 0f) * world;
        }

        /// Turns a geodetic-frame vector into the scene frame.
        public static Vector3 GeoToWorldFrame(Vector3 geo)
        {
            return NorthYawDeg == 0f ? geo : Quaternion.Euler(0f, NorthYawDeg, 0f) * geo;
        }

        /// Position of a geodetic point in the current scene frame. The inverse of
        /// GetGeoPosition, for placing anything that is authored in lat/lon after the origin
        /// has already moved.
        public static Vector3 GetScenePosition(double latitudeDeg, double longitudeDeg)
        {
            if (!HasReference) return Vector3.zero;

            GeoMath.GeoToLocal(latitudeDeg, longitudeDeg, ReferenceLatitude, ReferenceLongitude,
                               out double east, out double north);

            return GeoToWorldFrame(new Vector3((float)(east - OffsetEast), 0f,
                                               (float)(north - OffsetNorth)));
        }

        /// Human-readable current origin, for HUDs and logs.
        public static string Describe()
        {
            if (!HasReference) return "origin unset";

            GetGeoPosition(Vector3.zero, out double lat, out double lon);
            return $"origin {lat:F6}, {lon:F6} ({ShiftCount} shift{(ShiftCount == 1 ? "" : "s")})";
        }
    }
}
