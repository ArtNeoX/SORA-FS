using System;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Where a flight starts: a geodetic position, an altitude above the WGS84 ellipsoid and a
    /// true heading. With no scenery loaded everything below is ocean at h = 0.
    [Serializable]
    public struct SpawnPoint
    {
        [Tooltip("Geodetic latitude, degrees, north positive.")]
        public double latitude;

        [Tooltip("Longitude, degrees, east positive.")]
        public double longitude;

        [Tooltip("Height above the WGS84 ellipsoid, metres.")]
        public double altitude;

        [Tooltip("True heading, degrees clockwise from north.")]
        public double heading;

        [Tooltip("True airspeed at spawn, knots. Used only when the spawn is airborne.")]
        public float speedKnots;

        public static SpawnPoint Default => new SpawnPoint
        {
            latitude = 37.09,
            longitude = 127.03,
            altitude = 3000.0,
            heading = 0.0,
            speedKnots = 250f,
        };

        public override string ToString() =>
            $"{latitude:F5}, {longitude:F5}, {altitude:F0} m, hdg {heading:F0}";
    }
}
