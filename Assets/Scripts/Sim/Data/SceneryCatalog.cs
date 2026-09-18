using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Loads and validates sceneries.json from StreamingAssets.
    ///
    /// Validation is deliberately lenient in one direction only: a malformed airport is
    /// dropped with a clear error and the rest of the file still loads, so a typo in one
    /// coordinate cannot leave the player staring at an empty menu. A malformed file, on the
    /// other hand, fails loudly - there is nothing sensible to fall back to.
    public static class SceneryCatalog
    {
        public const string FileName = "sceneries.json";

        /// Airports further from their region pivot than this are almost certainly a
        /// copy-paste error (wrong region, swapped lat/lon), not a real entry.
        public const double MaxPivotDistanceMetres = 400000.0;

        public static string FilePath => Path.Combine(Application.streamingAssetsPath, FileName);

        /// Reads and validates the database. Returns an empty database plus an error string if
        /// the file is missing or unparseable; never returns null.
        public static SceneryDatabase Load(out string error)
        {
            string path = FilePath;

            if (!File.Exists(path))
            {
                error = $"No scenery config at '{path}'.";
                Debug.LogError($"[SceneryCatalog] {error}");
                return new SceneryDatabase();
            }

            SceneryDatabase database;
            try
            {
                database = JsonUtility.FromJson<SceneryDatabase>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                error = $"'{path}' is not valid JSON: {e.Message}";
                Debug.LogError($"[SceneryCatalog] {error}");
                return new SceneryDatabase();
            }

            if (database == null)
            {
                error = $"'{path}' parsed to nothing. The root must be an object with a " +
                        "\"sceneries\" array - JsonUtility cannot read a top-level array.";
                Debug.LogError($"[SceneryCatalog] {error}");
                return new SceneryDatabase();
            }

            database.sceneries ??= new List<SceneryData>();
            Validate(database);

            if (database.SceneryCount == 0)
            {
                error = $"'{path}' contains no usable scenery.";
                Debug.LogError($"[SceneryCatalog] {error}");
                return database;
            }

            error = null;
            return database;
        }

        /// Drops entries that cannot be spawned and logs exactly why.
        static void Validate(SceneryDatabase database)
        {
            for (int s = database.sceneries.Count - 1; s >= 0; s--)
            {
                SceneryData scenery = database.sceneries[s];

                // Seeded, because the null check short-circuits before IsValid can assign it.
                string sceneryError = "null entry";

                if (scenery == null || !scenery.IsValid(out sceneryError))
                {
                    Debug.LogError($"[SceneryCatalog] Dropping scenery: {sceneryError}");
                    database.sceneries.RemoveAt(s);
                    continue;
                }

                scenery.airports ??= new List<AirportData>();

                for (int a = scenery.airports.Count - 1; a >= 0; a--)
                {
                    AirportData airport = scenery.airports[a];
                    string airportError = "null entry";

                    if (airport == null || !airport.IsValid(out airportError))
                    {
                        Debug.LogError($"[SceneryCatalog] {scenery.Id}: dropping airport: {airportError}");
                        scenery.airports.RemoveAt(a);
                        continue;
                    }

                    double distance = GeoMath.HaversineDistance(scenery.PivotLatitude, scenery.PivotLongitude,
                                                                airport.Latitude, airport.Longitude);
                    if (distance > MaxPivotDistanceMetres)
                    {
                        Debug.LogError($"[SceneryCatalog] {scenery.Id}: dropping {airport.Icao}, it is " +
                                       $"{distance / 1000.0:F0} km from the region pivot - check the " +
                                       "coordinates are not swapped or in the wrong region.");
                        scenery.airports.RemoveAt(a);
                    }
                }

                if (scenery.airports.Count == 0)
                {
                    Debug.LogWarning($"[SceneryCatalog] {scenery.Id} has no airports and will not be listed.");
                    database.sceneries.RemoveAt(s);
                }
            }
        }
    }
}
