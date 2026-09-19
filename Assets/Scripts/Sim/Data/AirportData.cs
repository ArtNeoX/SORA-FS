using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// An airport scenery: a model you can start a flight from.
    ///
    /// It is a scenery in its own right, not something nested inside a region - the only thing
    /// that separates it from a RegionScenery is that it carries spawn points. The model owns
    /// everything positional: the anchors georeference it, and the empties under its Starts
    /// object are the starts. What stays here is the handful of facts a model cannot state -
    /// what the place is called, how high it is, and its magnetic variation.
    [Serializable]
    public class AirportData
    {
        public string icao = string.Empty;
        public string name = string.Empty;

        /// Name of the model root carrying the SceneryRegion. Defaults to the ICAO.
        public string scene_object = string.Empty;

        /// Object inside the model holding the spawn empties. Defaults to "Starts".
        public string starts_object = string.Empty;

        /// Field elevation in metres. Informational for the aircraft - it is put on the surface
        /// by raycast - but it is what other sceneries put their sea level against.
        public double elevation_m;

        /// Magnetic variation in degrees, east positive. Japan is negative.
        public double magnetic_variation_deg;

        /// Free-form tag, e.g. "3D". Searchable; no column of its own.
        public string features = string.Empty;

        /// Discovered from the model at load time, never read from or written to the config.
        [NonSerialized] public List<StartData> starts = new List<StartData>();

        // ---- clean accessors -----------------------------------------------------

        public string Icao => string.IsNullOrEmpty(icao) ? "----" : icao.ToUpperInvariant();
        public string DisplayName => string.IsNullOrEmpty(name) ? Icao : name;

        public string SceneObject => string.IsNullOrWhiteSpace(scene_object) ? Icao : scene_object.Trim();

        /// Reference point the customize map is drawn around and the info block reads out.
        /// Derived from the starts, so no coordinate is ever typed into the config.
        public double Latitude { get; private set; }
        public double Longitude { get; private set; }
        public bool HasReference { get; private set; }

        public double ElevationFeet => elevation_m * 3.280839895;

        public string CoordinateLabel => HasReference
            ? $"{Math.Abs(Latitude):F2}°{(Latitude >= 0 ? "N" : "S")} / " +
              $"{Math.Abs(Longitude):F2}°{(Longitude >= 0 ? "E" : "W")}"
            : "position unknown";

        public string ElevationLabel => $"{ElevationFeet:F0} ft MSL";

        public string MagneticVariationLabel =>
            $"{Math.Abs(magnetic_variation_deg):00}°{(magnetic_variation_deg >= 0 ? "E" : "W")} " +
            "magnetic variation";

        /// Sets the reference point to the centre of the discovered starts.
        public void DeriveReferenceFromStarts()
        {
            HasReference = false;
            if (starts == null || starts.Count == 0) return;

            double lat = 0.0, lon = 0.0;
            int counted = 0;

            for (int i = 0; i < starts.Count; i++)
            {
                if (starts[i] == null || !starts[i].CoordinatesDerived) continue;

                lat += starts[i].latitude;
                lon += starts[i].longitude;
                counted++;
            }

            if (counted == 0) return;

            Latitude = lat / counted;
            Longitude = lon / counted;
            HasReference = true;
        }

        /// The start used when the player has not opened the customize panel: the first runway
        /// if there is one, otherwise whatever comes first.
        public StartData DefaultStart
        {
            get
            {
                if (starts == null || starts.Count == 0) return null;

                for (int i = 0; i < starts.Count; i++)
                {
                    if (starts[i] != null && starts[i].IsRunway) return starts[i];
                }

                return starts[0];
            }
        }

        public IEnumerable<StartData> StartsOfType(bool runways)
        {
            if (starts == null) yield break;

            for (int i = 0; i < starts.Count; i++)
            {
                if (starts[i] != null && starts[i].IsRunway == runways) yield return starts[i];
            }
        }

        public StartData FindStart(string startId)
        {
            if (string.IsNullOrEmpty(startId) || starts == null) return null;

            for (int i = 0; i < starts.Count; i++)
            {
                if (starts[i] != null &&
                    string.Equals(starts[i].id, startId, StringComparison.OrdinalIgnoreCase))
                {
                    return starts[i];
                }
            }

            return null;
        }

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(icao))
            {
                error = "airport has no icao";
                return false;
            }

            error = null;
            return true;
        }
    }

    /// A start together with the airport it belongs to. An airport is its own scenery now, so
    /// there is no third thing to carry.
    public readonly struct AirportRef
    {
        public readonly AirportData Airport;
        public readonly StartData Start;

        public AirportRef(AirportData airport, StartData start)
        {
            Airport = airport;
            Start = start;
        }

        public AirportRef(AirportData airport) : this(airport, airport?.DefaultStart)
        {
        }

        /// The same airport, started from somewhere else.
        public AirportRef WithStart(StartData start) => new AirportRef(Airport, start);

        public bool IsValid => Airport != null && Start != null;

        /// True when this refers to the same airport, whatever start is chosen.
        public bool SameAirport(AirportRef other) =>
            Airport != null && ReferenceEquals(Airport, other.Airport);

        public override string ToString() =>
            IsValid ? $"{Airport.Icao} {Start.Label}" : "<none>";
    }
}
