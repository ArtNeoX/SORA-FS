using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// Airports on the globe: loads AirportData (SceneryDataGen/airport_exporter) on a worker
    /// thread at start, then
    ///  - gives GlobeTerrain the runways and frames of each chunk it builds; the terrain shader
    ///    draws them on the terrain surface itself, so they follow it at every level of detail;
    ///  - lifts sea-floor heights inside frames (runways on reclaimed land);
    ///  - keeps autogen (trees) off airport ground.
    /// The start menu searches the same database for spawning.
    ///
    /// Chunks, colliders and autogen patches built before the data was ready are rebuilt once
    /// it is, so it does not matter which finishes first.
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public class GlobeAirports : MonoBehaviour
    {
        public static GlobeAirports Active { get; private set; }

        /// Raised on the main thread once the database is loaded.
        public static event Action<AirportDatabase> Loaded;

        [Tooltip("Exporter output folder (holds airports.csv and runways.csv). Relative paths are " +
                 "relative to the project folder (the folder holding the player executable in a build).")]
        public string dataFolder = "AirportData";

        [Tooltip("Terrain the airports are drawn on. Defaults to GlobeTerrain.Active.")]
        public GlobeTerrain terrain;

        [Header("Frame (cleared ground around runways)")]
        [Tooltip("lateralMin / lateralWidthFactor: ground cleared beside each runway edge, the larger of the " +
                 "two (metres, or times the runway width). endMargin*: past each runway end, by runway " +
                 "length (under 1200 m / under 1800 m / longer). maxInfieldSize / maxInfieldAreaRatio: an " +
                 "airport with several runways clears the hull around all of them while it is at most " +
                 "this wide and this many times the runway frames' area. Changes apply live in play mode.")]
        public AirportFrameSettings frame = AirportFrameSettings.Default;

        [Tooltip("Terrain class names a frame can show. The airport's surroundings pick one: " +
                 "mostly snow, mostly rock or sand, or else grass.")]
        public string grassClass = "Grass";
        public string rockClass = "Rock / bare";
        public string sandClass = "Beach / sand";
        public string snowClass = "Snow / ice";

        [Tooltip("Trees keep this far from frames, metres.")]
        public float autogenMargin = 15f;

        [Header("Level of detail")]
        [Tooltip("Runways and frames narrower than this on screen (when their chunk is at its " +
                 "split distance) are left off coarse chunks, pixels.")]
        public float minPixels = 0.5f;

        static readonly int CountsId = Shader.PropertyToID("_AirportCounts");
        static readonly int RunwaysId = Shader.PropertyToID("_AirportRunways");
        static readonly int FramesId = Shader.PropertyToID("_AirportFrames");

        public AirportDatabase Database { get; private set; }
        public bool IsLoaded => Database != null;
        public string LoadError { get; private set; }

        /// Tiles with a height patch, for the debug readouts.
        public int PatchedTiles { get; private set; }

        Task<AirportDatabase> loading;
        TerrainDataSet preparedFor;
        float settingsChangedAt = -1f;
        AirportFrameClasses classIds;
        readonly AirportChunkPacker packer = new AirportChunkPacker();

        void Awake()
        {
            string folder = Path.IsPathRooted(dataFolder)
                ? dataFolder
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", dataFolder));
            AirportFrameSettings fs = frame;
            if (!File.Exists(Path.Combine(folder, "airports.csv")))
            {
                LoadError = $"No airports.csv in {folder}. Run SceneryDataGen/airport_exporter.";
                Debug.LogWarning("[GlobeAirports] " + LoadError);
                return;
            }
            loading = Task.Run(() => AirportDatabase.Load(folder, fs));
        }

        void OnEnable()
        {
            Active = this;
            Shader.SetGlobalTexture(RunwayDetailTexture.Id, RunwayDetailTexture.Get());
        }

        // Inspector edits in play mode: re-lay out the airports once the values stop changing.
        void OnValidate()
        {
            if (Application.isPlaying) settingsChangedAt = Time.realtimeSinceStartup;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
        }

        void Update()
        {
            if (loading != null && loading.IsCompleted)
            {
                if (loading.IsFaulted)
                {
                    LoadError = loading.Exception?.GetBaseException().Message;
                    Debug.LogError("[GlobeAirports] Loading failed: " + loading.Exception?.GetBaseException());
                }
                else
                {
                    Database = loading.Result;
                    Database.ExclusionMargin = autogenMargin;
                    Debug.Log($"[GlobeAirports] {Database.Airports.Count:N0} airports, {Database.Runways.Count:N0} runways " +
                              $"from {Database.Folder} ({Database.LoadMilliseconds:F0} ms on a worker thread)");
                    Loaded?.Invoke(Database);
                }
                loading = null;
            }

            if (Database != null && settingsChangedAt >= 0f && Time.realtimeSinceStartup - settingsChangedAt > 0.3f)
            {
                settingsChangedAt = -1f;
                // OnValidate also fires for unrelated edits and on entering play mode
                bool frameChanged = !Database.FrameSettings.Equals(frame);
                bool marginChanged = Database.ExclusionMargin != autogenMargin;
                if (frameChanged || marginChanged)
                {
                    Database.ExclusionMargin = autogenMargin;
                    if (frameChanged) Database.RebuildLayouts(frame);
                    preparedFor = null;     // patches, chunks, colliders and autogen again
                    Debug.Log($"[GlobeAirports] Frames rebuilt with the new settings ({Database.LoadMilliseconds:F0} ms)");
                }
            }

            GlobeTerrain t = Terrain;
            if (Database != null && t != null && t.Data != null && t.Data != preparedFor) Prepare(t);
        }

        GlobeTerrain Terrain => terrain != null ? terrain : GlobeTerrain.Active;

        /// Hooks the database into a terrain data set: height patches, frame classes, and a
        /// rebuild of everything built without them.
        void Prepare(GlobeTerrain t)
        {
            preparedFor = t.Data;
            classIds = AirportFrameClasses.Resolve(t.Data, grassClass, rockClass, sandClass, snowClass);
            if (classIds.grass < 0)
                Debug.LogWarning($"[GlobeAirports] No terrain class named \"{grassClass}\": frames are not drawn.");
            foreach (Airport a in Database.Airports) a.FrameClass = -1;
            PatchedTiles = AirportHeightPatch.Attach(t.Data, Database);

            t.RebuildChunks();
            foreach (TerrainCollision c in FindObjectsByType<TerrainCollision>(FindObjectsSortMode.None)) c.Rebuild();
            if (GlobeAutogen.Active != null) GlobeAutogen.Active.ClearPatches();
        }

        /// Sets the chunk's airport arrays on `block` (GlobeTerrain, before SetPropertyBlock).
        /// Returns false, leaving the block alone, when no airport reaches the chunk.
        public bool FillChunk(MaterialPropertyBlock block, TerrainTileSource tile, double lat0, double lon0,
                              double size, TerrainChunkData d, double pixelsPerMetreAtSpacing)
        {
            if (Database == null || preparedFor == null) return false;
            if (!packer.Pack(Database, preparedFor, classIds, tile.Lat, tile.Lon, lat0, lon0, size,
                             d.Centre, d.East, d.North, d.Spacing, pixelsPerMetreAtSpacing, minPixels))
                return false;
            block.SetVector(CountsId, new Vector4(packer.RunwayCount, packer.FrameVectorCount, packer.AirportCount, 0f));
            block.SetVectorArray(RunwaysId, packer.RunwayData);
            block.SetVectorArray(FramesId, packer.FrameData);
            return true;
        }
    }
}
