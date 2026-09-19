using System;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// One place an aircraft can be put down at an airport: a runway threshold or a parking
    /// spot. This is what the spawner actually aligns to - the airport itself is only a
    /// grouping, and its own coordinate is just the centre the map is drawn around.
    ///
    /// Field names are the JSON wire format (see AirportData for why they are snake_case).
    [Serializable]
    public class StartData
    {
        public const string TypeRunway = "runway";
        public const string TypeRamp = "ramp";

        /// Runway designator ("27") or parking name ("Apron D").
        public string id = string.Empty;

        /// "runway" or "ramp". A string rather than an enum because JsonUtility writes enums
        /// as integers, which would make the config unreadable.
        public string type = TypeRamp;

        /// Name of an empty inside the scenery model to spawn on. When set, it wins over the
        /// coordinates below: the aircraft is placed at that object's position, facing the way
        /// it faces, and the coordinates are *derived* from it rather than read.
        ///
        /// This is the accurate option. A coordinate has to survive the survey data, the
        /// imagery's own georeferencing and the anchor's placement by eye, and each of those
        /// costs a few metres. An empty sitting on the painted centreline is exactly on the
        /// painted centreline. Leave it blank to place by coordinate instead.
        ///
        /// Defaults to `id` when the model names its empties after the runway.
        public string marker = string.Empty;

        /// Filled in from the config, or derived from the marker at load time.
        public double latitude;
        public double longitude;

        /// True heading the aircraft faces, degrees clockwise from north. Derived from the
        /// marker's own facing when one is used.
        public double heading;

        /// Runways only: used to draw the strip on the map, measured from this threshold along
        /// the heading. Ignored for ramps.
        public double length_m;
        public double width_m = 45.0;

        /// The empty this start was discovered from, when it came out of the model. Set at
        /// runtime and never serialised - it is the exact object, so no name lookup can pick
        /// the wrong one.
        [NonSerialized] public Transform MarkerTransform;

        public bool IsRunway => string.Equals(type, TypeRunway, StringComparison.OrdinalIgnoreCase);

        /// True when the config names a specific object. A missing one is then an error rather
        /// than a reason to fall back on coordinates.
        public bool HasExplicitMarker => !string.IsNullOrWhiteSpace(marker);

        /// The object to look for. An explicit `marker` wins; otherwise the start's own id, so
        /// an empty called "16R" backs a start called "16R" with nothing else to write. If no
        /// such object exists, placement falls back to the coordinates.
        public string MarkerName => HasExplicitMarker ? marker : id;

        /// Set once the marker has been found, so the map and the readouts have something to
        /// draw even though the config carried no coordinate.
        public bool CoordinatesDerived { get; private set; }

        public void SetDerivedPosition(double latitudeDeg, double longitudeDeg, double headingDeg)
        {
            latitude = latitudeDeg;
            longitude = longitudeDeg;
            heading = headingDeg;
            CoordinatesDerived = true;
        }

        public string DisplayName => string.IsNullOrEmpty(id) ? (IsRunway ? "RWY" : "Ramp") : id;

        /// How the start reads in the selection summary, e.g. "RWY 27" or "Apron D".
        public string Label => IsRunway ? $"RWY {DisplayName}" : DisplayName;

        public double HeadingDeg => GeoMath.WrapHeading(heading);

        /// Rotation the aircraft is spawned with, in the geodetic frame. The region's own
        /// north yaw is applied on top of this by the spawner.
        public Quaternion SpawnRotation => GeoMath.HeadingToRotation(HeadingDeg);

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "start has no id";
                return false;
            }

            // A marker start carries no coordinate of its own - the model supplies it.
            if (!HasExplicitMarker)
            {
                if (!GeoMath.IsValidLatitude(latitude) || !GeoMath.IsValidLongitude(longitude))
                {
                    error = $"{id}: coordinate out of range ({latitude}, {longitude})";
                    return false;
                }

                if (double.IsNaN(heading))
                {
                    error = $"{id}: heading is NaN";
                    return false;
                }
            }

            error = null;
            return true;
        }
    }
}
