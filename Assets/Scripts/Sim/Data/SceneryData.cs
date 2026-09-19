using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// A region scenery: terrain you fly over but cannot start from.
    ///
    /// Mechanically identical to an airport scenery - same component, same anchors, same
    /// georeferencing - and the only difference is that it carries no spawn points. A whole
    /// archipelago and a single airfield are the same kind of thing here; one just happens to
    /// have a runway modelled in it.
    [Serializable]
    public class RegionData
    {
        public string id = string.Empty;
        public string display_name = string.Empty;

        /// Name of the model root carrying the SceneryRegion. Defaults to the id.
        public string scene_object = string.Empty;

        /// Stay loaded whatever the distance. For the wide backdrop that should never pop out.
        public bool always_loaded;

        /// Metres from the aircraft beyond which this scenery stops rendering. 0 means never
        /// cull. Ignored when always_loaded is set.
        public float render_distance_m;

        public string Id => id;
        public string DisplayName => string.IsNullOrEmpty(display_name) ? id : display_name;
        public string SceneObject => string.IsNullOrWhiteSpace(scene_object) ? Id : scene_object.Trim();

        public bool IsValid(out string error)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                error = "region has no id";
                return false;
            }

            error = null;
            return true;
        }
    }

    /// Root of sceneries.json: two flat lists, no nesting. JsonUtility cannot deserialise a
    /// top-level array, so the file has to be an object wrapping them.
    [Serializable]
    public class SceneryDatabase
    {
        public List<AirportData> airports = new List<AirportData>();
        public List<RegionData> regions = new List<RegionData>();

        public int AirportCount => airports?.Count ?? 0;
        public int RegionCount => regions?.Count ?? 0;

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

        public RegionData FindRegion(string regionId)
        {
            if (string.IsNullOrEmpty(regionId) || regions == null) return null;

            for (int i = 0; i < regions.Count; i++)
            {
                if (regions[i] != null &&
                    string.Equals(regions[i].id, regionId, StringComparison.OrdinalIgnoreCase))
                {
                    return regions[i];
                }
            }

            return null;
        }

        /// Every airport that has at least one start, paired with its default one. This is what
        /// the airport list shows - an airport with no spawn points in its model is not
        /// something you can depart from, so it is not offered.
        public List<AirportRef> SelectableAirports()
        {
            var result = new List<AirportRef>();
            if (airports == null) return result;

            for (int i = 0; i < airports.Count; i++)
            {
                AirportData airport = airports[i];
                if (airport?.DefaultStart == null) continue;

                result.Add(new AirportRef(airport));
            }

            return result;
        }

        public AirportRef FindSelectableAirport(string icao)
        {
            AirportData airport = FindAirport(icao);
            return airport?.DefaultStart != null ? new AirportRef(airport) : default;
        }
    }
}
