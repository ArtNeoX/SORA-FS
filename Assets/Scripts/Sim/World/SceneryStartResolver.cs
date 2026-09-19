using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Fills every airport's start list from its scenery model, and its reference point from
    /// those starts.
    ///
    /// This runs when the menu loads the config, not when a flight begins, because the customize
    /// map has to show the starts before anything is spawned. That is safe: a marker's position
    /// is read relative to its own model's anchors, and both move together, so the derived
    /// coordinates come out the same whether the scenery has been placed yet or not.
    ///
    /// An airport whose model is missing, or has no Starts object, ends up with no starts and is
    /// simply not offered in the menu - which is the honest answer, since there is nowhere in it
    /// to begin a flight.
    public static class SceneryStartResolver
    {
        /// Returns how many airports the scene was able to serve.
        public static int Apply(SceneryDatabase database)
        {
            if (database?.airports == null) return 0;

            int resolved = 0;

            foreach (AirportData airport in database.airports)
            {
                if (airport == null) continue;

                airport.starts = new List<StartData>();

                SceneryRegion scenery = SceneryRegion.Find(airport.Icao)
                                        ?? SceneryRegion.Find(airport.SceneObject);

                if (scenery == null)
                {
                    Debug.LogWarning($"[StartResolver] {airport.Icao}: no scenery in the scene with that " +
                                     $"id (looked for '{airport.Icao}' and '{airport.SceneObject}').");
                    continue;
                }

                if (!scenery.IsAirport)
                {
                    Debug.LogWarning($"[StartResolver] {airport.Icao}: '{scenery.name}' is marked as a " +
                                     "region scenery, so it carries no starts. Set its kind to Airport.",
                                     scenery);
                    continue;
                }

                airport.starts = scenery.DiscoverStarts(airport);
                airport.DeriveReferenceFromStarts();

                if (airport.starts.Count == 0)
                {
                    Debug.LogWarning($"[StartResolver] {airport.Icao}: '{scenery.name}' has no Starts " +
                                     "object, so the airport cannot be departed from.", scenery);
                    continue;
                }

                Debug.Log($"[StartResolver] {airport.Icao}: {airport.starts.Count} start(s) from " +
                          $"'{scenery.name}'.", scenery);
                resolved++;
            }

            return resolved;
        }
    }
}
