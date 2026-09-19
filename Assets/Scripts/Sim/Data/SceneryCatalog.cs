using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Loads and validates sceneries.json from StreamingAssets.
    ///
    /// The file is small on purpose: it names the airports and regions and carries the handful of
    /// facts a model cannot state. Everything positional - where a scenery is, which way it
    /// faces, how big it is, where you can start from - comes from the models themselves.
    ///
    /// Validation is lenient in one direction only: a malformed entry is dropped with a clear
    /// error and the rest still loads, so one typo cannot leave the player staring at an empty
    /// menu. A malformed file fails loudly, because there is nothing sensible to fall back to.
    public static class SceneryCatalog
    {
        public const string FileName = "sceneries.json";

        public static string FilePath => Path.Combine(Application.streamingAssetsPath, FileName);

        /// Reads and validates the database. Returns an empty one plus an error string if the
        /// file is missing or unparseable; never returns null.
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
                error = $"'{path}' parsed to nothing. The root must be an object with \"airports\" " +
                        "and \"regions\" arrays - JsonUtility cannot read a top-level array.";
                Debug.LogError($"[SceneryCatalog] {error}");
                return new SceneryDatabase();
            }

            database.airports ??= new List<AirportData>();
            database.regions ??= new List<RegionData>();

            Validate(database);

            if (database.AirportCount == 0)
            {
                error = $"'{path}' contains no usable airports.";
                Debug.LogError($"[SceneryCatalog] {error}");
                return database;
            }

            error = null;
            return database;
        }

        /// Drops entries that cannot be used and logs exactly why.
        static void Validate(SceneryDatabase database)
        {
            for (int i = database.airports.Count - 1; i >= 0; i--)
            {
                AirportData airport = database.airports[i];
                string airportError = "null entry";

                if (airport == null || !airport.IsValid(out airportError))
                {
                    Debug.LogError($"[SceneryCatalog] Dropping airport: {airportError}");
                    database.airports.RemoveAt(i);
                    continue;
                }

                for (int j = 0; j < i; j++)
                {
                    if (database.airports[j] != null &&
                        string.Equals(database.airports[j].icao, airport.icao,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogError($"[SceneryCatalog] Dropping duplicate airport '{airport.Icao}'.");
                        database.airports.RemoveAt(i);
                        break;
                    }
                }
            }

            for (int i = database.regions.Count - 1; i >= 0; i--)
            {
                RegionData region = database.regions[i];
                string regionError = "null entry";

                if (region == null || !region.IsValid(out regionError))
                {
                    Debug.LogError($"[SceneryCatalog] Dropping region: {regionError}");
                    database.regions.RemoveAt(i);
                }
            }
        }
    }
}
