using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// One scenery region: a single large model whose pivot sits at a known lat/lon, plus
    /// every airport modelled inside it.
    ///
    /// Field names are the JSON wire format (see AirportData for why they are snake_case).
    [Serializable]
    public class SceneryData
    {
        public string id = string.Empty;
        public string display_name = string.Empty;

        /// Geodetic coordinate of the model's own pivot - the point that sits at the scenery
        /// root transform's position. Every airport offset in this region is measured from it.
        public double pivot_latitude;
        public double pivot_longitude;

        /// Name of the SceneryRegion component's id in the loaded scene. Left empty, the
        /// region is matched on `id`.
        public string scene_object = string.Empty;

        /// Optional Resources path used when the region is not already present in the scene.
        public string prefab_resource_path = string.Empty;

        public List<AirportData> airports = new List<AirportData>();

        // ---- clean accessors -----------------------------------------------------

        public string Id => id;
        public string DisplayName => string.IsNullOrEmpty(display_name) ? id : display_name;
        public double PivotLatitude => pivot_latitude;
        public double PivotLongitude => pivot_longitude;

        /// Offset from the model pivot to an airport, in scene metres, before the region's own
        /// transform is applied. This is the delta the spawner cancels out.
        public Vector3 PivotToAirport(AirportData airport)
        {
            return GeoMath.GeoToLocal(airport.Latitude, airport.Longitude,
                                      pivot_latitude, pivot_longitude);
        }

        public AirportData FindAirport(string icao)
        {
            if (string.IsNullOrEmpty(icao) || airports == null) return null;

            for (int i = 0; i < airports.Count; i++)
            {
                if (airports[i] != null &&
                    string.Equals(airports[i].icao, icao, StringComparison.OrdinalIgnoreCase))
                {
                    return airports[i];
                }
            }

            return null;
        }

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "scenery has no id";
                return false;
            }

            if (!GeoMath.IsValidLatitude(pivot_latitude) || !GeoMath.IsValidLongitude(pivot_longitude))
            {
                error = $"{id}: pivot coordinate out of range ({pivot_latitude}, {pivot_longitude})";
                return false;
            }

            error = null;
            return true;
        }
    }

    /// Root of sceneries.json. JsonUtility cannot deserialise a top-level array, so the file
    /// has to be an object with this one member.
    [Serializable]
    public class SceneryDatabase
    {
        public List<SceneryData> sceneries = new List<SceneryData>();

        public int SceneryCount => sceneries?.Count ?? 0;

        public SceneryData FindScenery(string sceneryId)
        {
            if (string.IsNullOrEmpty(sceneryId) || sceneries == null) return null;

            for (int i = 0; i < sceneries.Count; i++)
            {
                if (sceneries[i] != null &&
                    string.Equals(sceneries[i].id, sceneryId, StringComparison.OrdinalIgnoreCase))
                {
                    return sceneries[i];
                }
            }

            return null;
        }

        /// Every airport in the database, flattened and paired with its region, ready for the
        /// airport list panel.
        public List<AirportRef> AllAirports()
        {
            var result = new List<AirportRef>();
            if (sceneries == null) return result;

            for (int s = 0; s < sceneries.Count; s++)
            {
                SceneryData scenery = sceneries[s];
                if (scenery?.airports == null) continue;

                for (int a = 0; a < scenery.airports.Count; a++)
                {
                    if (scenery.airports[a] != null)
                    {
                        result.Add(new AirportRef(scenery, scenery.airports[a]));
                    }
                }
            }

            return result;
        }

        public AirportRef FindAirport(string icao)
        {
            if (string.IsNullOrEmpty(icao) || sceneries == null) return default;

            for (int s = 0; s < sceneries.Count; s++)
            {
                AirportData airport = sceneries[s]?.FindAirport(icao);
                if (airport != null) return new AirportRef(sceneries[s], airport);
            }

            return default;
        }
    }
}
