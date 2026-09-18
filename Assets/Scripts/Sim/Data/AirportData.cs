using System;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// One spawn point inside a scenery region: an airport, and the runway threshold the
    /// aircraft starts on.
    ///
    /// The fields are the JSON wire format and are named exactly as the keys are spelled in
    /// sceneries.json - JsonUtility matches on the field name and has no attribute for
    /// renaming, so snake_case survives here and the rest of the code uses the properties
    /// below.
    [Serializable]
    public class AirportData
    {
        public string icao = string.Empty;
        public string name = string.Empty;

        /// Threshold (or any point on the runway) the aircraft is placed at.
        public double runway_latitude;
        public double runway_longitude;

        /// True heading of the runway in degrees, i.e. the direction of take-off roll.
        public double runway_heading;

        /// Runway designator shown in the menu, e.g. "01L". Optional.
        public string runway_id = string.Empty;

        /// Free-form tag for the list's right-hand column, e.g. "3D" or "Grass Strip".
        public string features = string.Empty;

        /// Field elevation in metres. Informational: the aircraft is put on the scenery
        /// surface by raycast, not by this number.
        public double elevation_m;

        // ---- clean accessors -----------------------------------------------------

        public string Icao => string.IsNullOrEmpty(icao) ? "----" : icao.ToUpperInvariant();
        public string DisplayName => string.IsNullOrEmpty(name) ? Icao : name;
        public double Latitude => runway_latitude;
        public double Longitude => runway_longitude;
        public double HeadingDeg => GeoMath.WrapHeading(runway_heading);

        public string RunwayLabel =>
            string.IsNullOrEmpty(runway_id) ? $"RWY {HeadingDeg:F0}" : $"RWY {runway_id}";

        /// Rotation the aircraft is spawned with: nose down the runway.
        public Quaternion SpawnRotation => GeoMath.HeadingToRotation(HeadingDeg);

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(icao))
            {
                error = "airport has no icao";
                return false;
            }

            if (!GeoMath.IsValidLatitude(runway_latitude) || !GeoMath.IsValidLongitude(runway_longitude))
            {
                error = $"{Icao}: runway coordinate out of range ({runway_latitude}, {runway_longitude})";
                return false;
            }

            if (double.IsNaN(runway_heading))
            {
                error = $"{Icao}: runway_heading is NaN";
                return false;
            }

            error = null;
            return true;
        }
    }

    /// An airport together with the scenery region that contains it. The menu selects one of
    /// these; the airport alone is not enough to place anything, because the delta is measured
    /// from its parent scenery's pivot.
    public readonly struct AirportRef
    {
        public readonly SceneryData Scenery;
        public readonly AirportData Airport;

        public AirportRef(SceneryData scenery, AirportData airport)
        {
            Scenery = scenery;
            Airport = airport;
        }

        public bool IsValid => Scenery != null && Airport != null;

        public override string ToString() =>
            IsValid ? $"{Airport.Icao} ({Scenery.Id})" : "<none>";
    }
}
