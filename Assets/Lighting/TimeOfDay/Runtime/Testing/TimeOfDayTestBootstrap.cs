using CirrusFS.Geo;
using UnityEngine;

namespace CirrusFS.TimeOfDay.Testing
{
    /// Test-scene stand-in for the flight spawner: puts the floating origin on the globe at a
    /// chosen place so the time-of-day system runs exactly as it does in flight.
    [DefaultExecutionOrder(-200)]
    public class TimeOfDayTestBootstrap : MonoBehaviour
    {
        public double latitude = 35.5533;
        public double longitude = 139.7811;

        void Awake()
        {
            if (!GeoOrigin.IsSet) GeoOrigin.Reset(GeoMath.GeodeticToEcef(latitude, longitude, 0.0));
        }
    }
}
