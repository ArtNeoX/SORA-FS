using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CirrusFS.Geo
{
    /// Streams the 1° terrain tiles written by SceneryDataGen/terrain_exporter_v2 onto the
    /// WGS84 globe.
    ///
    /// Only a window of `tilesWide` x `tilesHigh` 1° tiles around the focus (the aircraft) is
    /// loaded and drawn, as X-Plane loads its DSF tiles: 4 x 3 by default, so the focus is
    /// always in one of the middle tiles. The window moves with the focus; tiles that leave it
    /// stop drawing and their chunks age out of the cache. Beyond it there is only the ocean.
    ///
    /// Each tile is its own quadtree of chunks (see TerrainChunkBuilder): the whole degree at
    /// level 0, split by two per level down to where vertex spacing reaches the height data.
    /// A chunk splits while its vertex spacing, seen from the focus, would be wider than
    /// `maxVertexAngle`; so the ground under the aircraft is at full data resolution and a
    /// tile 100 km away is a handful of chunks. Chunks over the horizon (allowing for their
    /// own height) are not drawn.
    ///
    /// Like the ocean, chunks know their ECEF centre and re-place themselves on every
    /// floating-origin re-centre; the root must carry FloatingOriginIgnore.
    ///
    /// Each chunk gets a small class-id texture. The shader blends the four nearest cells,
    /// with a noise-warped lookup, so land types fade into each other instead of showing the
    /// 30 m cell grid. Classes listed in `classTextures` also get a tiling ground texture.
    [DefaultExecutionOrder(310)]
    [DisallowMultipleComponent]
    public class GlobeTerrain : MonoBehaviour
    {
        /// The terrain in the scene, for ground-height queries (spawning, readouts).
        public static GlobeTerrain Active { get; private set; }

        [Tooltip("Exporter output folders, or parents of them (their direct subfolders are read " +
                 "too, e.g. TerrainData/Global and TerrainData/Tokyo). In every 1° cell the most " +
                 "detailed folder wins. Relative paths are relative to the project folder (the " +
                 "folder holding the player executable in a build).")]
        public string[] dataFolders = { "TerrainData" };

        [Tooltip("Terrain material (CirrusFS/Terrain shader).")]
        public Material material;

        [Tooltip("Camera the LOD follows. Defaults to Camera.main.")]
        public Camera viewCamera;

        [Header("Loaded tiles")]
        [Tooltip("1° tiles loaded east-west around the focus. With an even count the window " +
                 "shifts when the focus crosses a tile's middle, so it is never in an edge tile.")]
        [Range(1, 12)] public int tilesWide = 4;

        [Tooltip("1° tiles loaded north-south around the focus.")]
        [Range(1, 12)] public int tilesHigh = 3;

        [Header("Level of detail")]
        [Tooltip("Quads along each chunk edge.")]
        [Range(8, 128)] public int resolution = 64;

        [Tooltip("Class texels along each chunk edge.")]
        [Range(16, 256)] public int classTexels = 128;

        [Tooltip("Largest vertex spacing a chunk may show, as an angle seen from the focus " +
                 "(radians). 0.006 is about 6 px at 1080p and 60 deg FOV.")]
        public float maxVertexAngle = 0.006f;

        [Tooltip("Terrain detail for quality settings: 0.5 = low, 1 = medium, 2 = high. Divides " +
                 "maxVertexAngle, so higher values split chunks further out.")]
        [Range(0.25f, 4f)] public float lodQuality = 1f;

        [Tooltip("What the detail follows: the aircraft (set by the session at spawn), so zooming " +
                 "the camera out to the globe does not move the detail. Falls back to the camera.")]
        public Transform focus;

        [Tooltip("Deepest quadtree level allowed (a tile also stops where its data runs out).")]
        [Range(0, 10)] public int maxLevel = 8;

        [Tooltip("Chunk meshes built per frame at most.")]
        public int buildBudgetPerFrame = 6;

        [Tooltip("Stop building chunks for this frame once this much time is spent, ms (a " +
                 "chunk takes about 3 ms). At least one is built per frame.")]
        public float buildBudgetMs = 4f;

        [Tooltip("Tiles nearer than this are built at once (whole-tile chunk) when they come " +
                 "into view, so the ground never pops in under the aircraft, metres.")]
        public float immediateRadius = 150000f;

        [Tooltip("Chunks kept in memory after they leave the view.")]
        public int cacheSize = 1500;

        [Tooltip("Tile files kept open when idle.")]
        public int keepTilesOpen = 16;

        [Header("Rendering")]
        public ShadowCastingMode castShadows = ShadowCastingMode.On;
        public bool receiveShadows = true;

        [Tooltip("Ground textures per land class (optional). Without it every class is flat colour.")]
        public TerrainClassTextures classTextures;

        [Tooltip("Airports drawn on the terrain (runways and their frames). Defaults to GlobeAirports.Active.")]
        public GlobeAirports airports;
        static readonly int ClassTexId = Shader.PropertyToID("_ClassTex");
        static readonly int PaletteId = Shader.PropertyToID("_TerrainPalette");
        static readonly int DetailId = Shader.PropertyToID("_TerrainDetail");
        static readonly int ClassTexSizeId = Shader.PropertyToID("_TerrainClassTexSize");
        static readonly int ClassApronId = Shader.PropertyToID("_TerrainClassApron");
        static readonly int ClassCellsId = Shader.PropertyToID("_ClassCells");
        static readonly int SeaHeightId = Shader.PropertyToID("_TerrainSeaHeight");
        static readonly int ClassAlbedoId = Shader.PropertyToID("_TerrainClassAlbedo");
        static readonly int ClassParamsId = Shader.PropertyToID("_TerrainClassParams");
        static readonly int ClassTintId = Shader.PropertyToID("_TerrainClassTint");
        static readonly int RockParamsId = Shader.PropertyToID("_TerrainRockParams");
        static readonly int RockTintId = Shader.PropertyToID("_TerrainRockTint");
        public TerrainDataSet Data { get; private set; }

        public int VisibleChunkCount { get; private set; }
        public int DeepestVisibleLevel { get; private set; }
        public int VisibleTileCount { get; private set; }
        public int CachedChunkCount => chunks.Count;

        /// Mean time of the last 64 chunk builds, milliseconds (data, mesh and texture).
        public float AverageBuildMs { get; private set; }

        /// Chunks built since start, and the time spent building in the last frame, ms.
        public int BuildCount { get; private set; }
        public float BuildMsLastFrame => buildMsThisFrame;

        /// What the terrain holds on the GPU, for the performance window. Bytes are counted
        /// from what was uploaded: vertex and index buffers, the class textures, and the
        /// shared textures (palette, detail noise, the ground texture array with its mips).
        public struct MemoryStats
        {
            public int chunks, shownChunks;
            public long meshBytes, classTextureBytes, sharedTextureBytes;
            public long shownTriangles, shownMeshBytes;
            public long TotalBytes => meshBytes + classTextureBytes + sharedTextureBytes;
        }

        /// Walks the chunk cache (up to cacheSize entries): call a few times a second, not
        /// every frame.
        public MemoryStats GetMemoryStats()
        {
            var stats = new MemoryStats { chunks = chunks.Count, shownChunks = shownLastFrame.Count };
            foreach (Chunk chunk in chunks.Values)
            {
                stats.meshBytes += chunk.meshBytes;
                stats.classTextureBytes += chunk.classTexBytes;
            }
            foreach (Chunk chunk in shownLastFrame)
            {
                stats.shownTriangles += chunk.triangles;
                stats.shownMeshBytes += chunk.meshBytes;
            }

            if (palette != null) stats.sharedTextureBytes += palette.width * 4L;
            if (detail != null) stats.sharedTextureBytes += detail.width * (long)detail.height * 4L * 4 / 3;
            if (albedoArray != null)
                stats.sharedTextureBytes += albedoArray.width * (long)albedoArray.height * 4L * albedoArray.volumeDepth * 4 / 3;
            return stats;
        }

        readonly System.Diagnostics.Stopwatch buildTimer = new System.Diagnostics.Stopwatch();

        sealed class Chunk
        {
            public long key;
            public TerrainTileSource tile;
            public int level;                  // tile quadtree level
            public Double3 centre, north, up;
            public double radius, spacing;
            public float maxHeight;
            public GameObject go;
            public MeshRenderer renderer;
            public Mesh mesh;
            public Texture2D classTex;
            public long meshBytes, classTexBytes, triangles;
            public int lastUsedFrame;
            public bool shown;
        }

        readonly Dictionary<long, Chunk> chunks = new Dictionary<long, Chunk>();
        readonly List<Chunk> shownLastFrame = new List<Chunk>();
        readonly List<Chunk> shownThisFrame = new List<Chunk>();
        readonly List<long> evictScratch = new List<long>();
        readonly Dictionary<TerrainTileSource, int> tileMaxLevel = new Dictionary<TerrainTileSource, int>();
        readonly TerrainChunkData buildData = new TerrainChunkData();

        MaterialPropertyBlock block;
        Texture2D palette, detail;
        RenderTexture albedoArray;
        TerrainClassTextures subscribedTextures;
        bool texturesDirty;
        readonly Vector4[] classParams = new Vector4[256];
        readonly Vector4[] classTint = new Vector4[256];
        readonly List<Texture2D> albedoLayers = new List<Texture2D>();
        readonly HashSet<string> warnedClassNames = new HashSet<string>();
        Double3 cameraEcef, focusEcef;
        double cameraDistance, cameraHorizonAngle;
        int buildsThisFrame;
        float buildMsThisFrame;

        // ---- lifecycle ------------------------------------------------------------

        void Awake()
        {
            block = new MaterialPropertyBlock();
            LoadData();
        }

        void OnEnable()
        {
            Active = this;
            SubscribeTextures();
            GeoOrigin.Recentered += HandleRecentered;
            GeoOrigin.OriginReset += HandleOriginReset;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            if (subscribedTextures != null) subscribedTextures.Changed -= MarkTexturesDirty;
            subscribedTextures = null;
            GeoOrigin.Recentered -= HandleRecentered;
            GeoOrigin.OriginReset -= HandleOriginReset;
        }

        void OnDestroy()
        {
            foreach (Chunk chunk in chunks.Values) DestroyChunk(chunk);
            chunks.Clear();
            Data?.Dispose();
            if (palette != null) Destroy(palette);
            if (detail != null) Destroy(detail);
            if (albedoArray != null)
            {
                albedoArray.Release();
                Destroy(albedoArray);
            }
        }

        /// Drops every built chunk; they are rebuilt as they come into view, the ones near the
        /// focus at once. For when what goes into a chunk changed (airports loaded).
        public void RebuildChunks()
        {
            foreach (Chunk chunk in chunks.Values) DestroyChunk(chunk);
            chunks.Clear();
            shownLastFrame.Clear();
        }

        /// Reads the manifests. Safe to call again after exporting more tiles.
        public void LoadData()
        {
            RebuildChunks();
            tileMaxLevel.Clear();
            Data?.Dispose();

            var folders = new List<string>();
            foreach (string f in dataFolders)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                folders.Add(Path.IsPathRooted(f) ? f : Path.GetFullPath(Path.Combine(Application.dataPath, "..", f)));
            }

            Data = TerrainDataSet.Load(folders, msg => Debug.Log("[GlobeTerrain] " + msg));
            foreach (TerrainTileSource tile in Data.Tiles)
                tileMaxLevel[tile] = Math.Min(maxLevel, TerrainChunkBuilder.MaxUsefulLevel(tile, resolution, classTexels));
            Shader.SetGlobalFloat(SeaHeightId, Data.SeaHeight);

            BuildPalette();
            if (detail == null) detail = TerrainDetailTexture.Create(256);
            Shader.SetGlobalTexture(PaletteId, palette);
            Shader.SetGlobalTexture(DetailId, detail);
            Shader.SetGlobalFloat(ClassTexSizeId, classTexels + 2 * TerrainChunkBuilder.ClassApron);
            Shader.SetGlobalFloat(ClassApronId, TerrainChunkBuilder.ClassApron);
            ApplyClassTextures();
        }

        /// Class colours from the manifest (linear RGB); alpha marks water.
        void BuildPalette()
        {
            if (palette == null)
            {
                palette = new Texture2D(256, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "Terrain class palette",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }

            var colours = new Color32[256];
            for (int i = 0; i < 256; i++) colours[i] = new Color32(255, 0, 255, 0);
            foreach (TerrainClassDef c in Data.Classes)
            {
                if (c.id < 0 || c.id > 255 || !ColorUtility.TryParseHtmlString(c.color, out Color col)) continue;
                Color lin = col.linear;
                colours[c.id] = new Color32((byte)(lin.r * 255f + 0.5f), (byte)(lin.g * 255f + 0.5f),
                                            (byte)(lin.b * 255f + 0.5f), (byte)(c.id == Data.WaterClass ? 255 : 0));
            }
            palette.SetPixels32(colours);
            palette.Apply(false, false);
        }

        // ---- ground textures ------------------------------------------------------

        void SubscribeTextures()
        {
            if (subscribedTextures == classTextures) return;
            if (subscribedTextures != null) subscribedTextures.Changed -= MarkTexturesDirty;
            subscribedTextures = classTextures;
            if (subscribedTextures != null) subscribedTextures.Changed += MarkTexturesDirty;
            texturesDirty = true;
        }

        void MarkTexturesDirty() => texturesDirty = true;

        /// Packs the set's textures into one sRGB texture array and publishes the per-class
        /// parameters: x = layer + 1 (0 = flat colour), y = repeats per detail period,
        /// z = strength, w = 1 to keep the class colour.
        void ApplyClassTextures()
        {
            texturesDirty = false;
            Array.Clear(classParams, 0, classParams.Length);
            for (int i = 0; i < classTint.Length; i++) classTint[i] = Vector4.one;
            Vector4 rockParams = Vector4.zero, rockTint = Vector4.one;
            albedoLayers.Clear();

            TerrainClassTextures set = classTextures;
            if (set != null && Data != null)
            {
                double refCos = Math.Cos(set.referenceLatitude * GeoMath.Deg2Rad);
                foreach (TerrainClassTextures.Entry entry in set.classes)
                {
                    if (entry == null || entry.albedo == null) continue;
                    int id = FindClass(entry.className);
                    if (id < 0)
                    {
                        if (warnedClassNames.Add(entry.className ?? ""))
                            Debug.LogWarning($"[GlobeTerrain] Ground texture {entry.albedo.name}: no class named " +
                                             $"\"{entry.className}\" in the manifest.");
                        continue;
                    }
                    classParams[id] = TextureParams(entry, refCos);
                    classTint[id] = entry.tint.linear;
                }
                if (set.steepSlopes != null && set.steepSlopes.albedo != null)
                {
                    rockParams = TextureParams(set.steepSlopes, refCos);
                    rockTint = set.steepSlopes.tint.linear;
                }
            }

            BuildAlbedoArray(set != null ? set.textureSize : 16);
            Shader.SetGlobalTexture(ClassAlbedoId, albedoArray);
            Shader.SetGlobalVectorArray(ClassParamsId, classParams);
            Shader.SetGlobalVectorArray(ClassTintId, classTint);
            Shader.SetGlobalVector(RockParamsId, rockParams);
            Shader.SetGlobalVector(RockTintId, rockTint);        }

        Vector4 TextureParams(TerrainClassTextures.Entry entry, double refCos)
        {
            int layer = albedoLayers.IndexOf(entry.albedo);
            if (layer < 0)
            {
                layer = albedoLayers.Count;
                albedoLayers.Add(entry.albedo);
            }

            // The detail grid is Mercator metres (true size / cos lat). A whole number of
            // repeats per period keeps the texture seamless across chunk offsets.
            double mercatorSize = Math.Max(entry.sizeMetres, 0.5f) / refCos;
            double repeats = Math.Max(1.0, Math.Round(TerrainChunkBuilder.DetailPeriod / mercatorSize));
            bool keepColour = entry.colourFrom == TerrainClassTextures.ColourSource.ClassColour;
            return new Vector4(layer + 1, (float)repeats, Mathf.Clamp01(entry.strength), keepColour ? 1f : 0f);
        }

        int FindClass(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string wanted = name.Trim();
            foreach (TerrainClassDef c in Data.Classes)
                if (string.Equals(c.name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return c.id;
            return -1;
        }

        void BuildAlbedoArray(int size)
        {
            int count = Math.Max(1, albedoLayers.Count);
            if (albedoLayers.Count == 0) size = 4;
            int mips = 1 + (int)Math.Round(Math.Log(size, 2));

            if (albedoArray == null || albedoArray.width != size || albedoArray.volumeDepth != count)
            {
                if (albedoArray != null)
                {
                    albedoArray.Release();
                    Destroy(albedoArray);
                }
                var desc = new RenderTextureDescriptor(size, size, GraphicsFormat.R8G8B8A8_SRGB, 0, mips)
                {
                    dimension = TextureDimension.Tex2DArray,
                    volumeDepth = count,
                    useMipMap = true,
                    autoGenerateMips = false,
                };
                albedoArray = new RenderTexture(desc)
                {
                    name = "Terrain class albedo",
                    hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 8,
                };
                albedoArray.Create();
            }

            if (albedoLayers.Count == 0) Graphics.Blit(Texture2D.whiteTexture, albedoArray, 0, 0);
            for (int i = 0; i < albedoLayers.Count; i++) Graphics.Blit(albedoLayers[i], albedoArray, 0, i);
            albedoArray.GenerateMips();
        }

        // ---- floating origin ------------------------------------------------------

        void HandleRecentered(GeoFrameChange change) => PlaceAllChunks();

        void HandleOriginReset() => PlaceAllChunks();

        void PlaceAllChunks()
        {
            foreach (Chunk chunk in chunks.Values) PlaceChunk(chunk);
        }

        static void PlaceChunk(Chunk chunk)
        {
            chunk.go.transform.SetPositionAndRotation(
                GeoOrigin.EcefToUnity(chunk.centre),
                Quaternion.LookRotation(GeoOrigin.EcefDirToUnity(chunk.north), GeoOrigin.EcefDirToUnity(chunk.up)));
        }

        // ---- queries --------------------------------------------------------------

        /// Finest level built for a tile (where its data runs out, or maxLevel).
        public int MaxLevelOf(TerrainTileSource tile) =>
            tileMaxLevel.TryGetValue(tile, out int level)
                ? level
                : Math.Min(maxLevel, TerrainChunkBuilder.MaxUsefulLevel(tile, resolution, classTexels));

        /// Terrain height above the ellipsoid at a point, metres. False where there is no tile.
        public bool TryGetHeight(double latitudeDeg, double longitudeDeg, out float height)
        {
            height = 0f;
            return Data != null && Data.TryGetHeight(latitudeDeg, longitudeDeg, out height);
        }

        /// Height of the surface that is drawn and collided with up close (the triangles of the
        /// tile's finest level), metres. Differs from TryGetHeight by up to a few metres
        /// between vertices; use it to put things on the ground, such as a spawning aircraft.
        public bool TryGetSurfaceHeight(double latitudeDeg, double longitudeDeg, out float height)
        {
            height = 0f;
            TerrainTileSource tile = Data?.TileContaining(latitudeDeg, longitudeDeg);
            if (tile == null) return false;
            long verticesPerDegree = (1L << MaxLevelOf(tile)) * resolution;
            height = TerrainChunkBuilder.SurfaceHeight(tile, latitudeDeg, GeoMath.WrapLongitude(longitudeDeg), verticesPerDegree);
            return true;
        }

        // ---- per frame ------------------------------------------------------------

        void LateUpdate()
        {
            SubscribeTextures();
            if (texturesDirty && Data != null) ApplyClassTextures();
            if (material == null || Data == null || !GeoOrigin.IsSet) return;

            Camera cam = viewCamera != null ? viewCamera : Camera.main;
            if (cam == null) return;

            // What is visible is the camera's business; how detailed it is follows the focus
            // (the aircraft), so zooming the camera out to the globe keeps the detail where the
            // aircraft is instead of moving it under the camera.
            cameraEcef = GeoOrigin.UnityToEcef(cam.transform.position);
            cameraDistance = cameraEcef.Magnitude;
            double cameraHeight = cameraDistance - GeoMath.SurfacePointBelow(cameraEcef).Magnitude;
            cameraHorizonAngle = HorizonAngle(cameraHeight);
            focusEcef = focus != null ? GeoOrigin.UnityToEcef(focus.position) : cameraEcef;
            Double3 focusDir = focusEcef.Normalized;

            buildsThisFrame = 0;
            buildMsThisFrame = 0f;
            shownThisFrame.Clear();
            DeepestVisibleLevel = 0;
            VisibleTileCount = 0;

            // the tilesWide x tilesHigh window of 1° tiles around the focus, nothing else
            GeoMath.EcefToGeodetic(focusEcef, out double focusLat, out double focusLon, out _);
            int wide = Math.Max(1, tilesWide), high = Math.Max(1, tilesHigh);
            int lat0 = (int)Math.Floor(focusLat - 0.5 * high + 0.5);
            int lon0 = (int)Math.Floor(focusLon - 0.5 * wide + 0.5);
            Double3 cameraDir = cameraEcef / cameraDistance;
            for (int row = 0; row < high; row++)
            {
                int lat = lat0 + row;
                if (lat < -90 || lat > 89) continue;
                for (int col = 0; col < wide && col < 360; col++)
                {
                    int lon = (int)GeoMath.WrapLongitude(lon0 + col);
                    if (lon >= 180) lon -= 360;
                    TerrainTileSource tile = Data.TileAt(lat, lon);
                    if (tile == null) continue;

                    // the tile's angular radius is under 0.8° (half a degree diagonal), 0.0135 rad
                    double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Double3.Dot(tile.CentreDirection, cameraDir))));
                    if (angle - 0.0135 > cameraHorizonAngle + HorizonAngle(tile.MaxHeight)) continue;

                    VisibleTileCount++;
                    Traverse(tile, 0, 0, 0, NearFocus(tile.CentreDirection, focusDir, 0.0135));
                }
            }

            for (int i = 0; i < shownLastFrame.Count; i++)
            {
                Chunk chunk = shownLastFrame[i];
                if (chunk.lastUsedFrame != Time.frameCount && chunk.shown)
                {
                    chunk.renderer.enabled = false;
                    chunk.shown = false;
                }
            }

            shownLastFrame.Clear();
            shownLastFrame.AddRange(shownThisFrame);
            VisibleChunkCount = shownThisFrame.Count;

            Evict();
            Data.CloseIdle(120, keepTilesOpen);
        }

        /// Angle at the Earth's centre between a point `height` above the surface and its
        /// horizon (sphere of the semi-minor axis: slightly generous).
        static double HorizonAngle(double height) =>
            height > 0.0 ? Math.Acos(GeoMath.B / (GeoMath.B + height)) : 0.0;

        /// True when a cell (centre direction, angular radius in radians) lies within
        /// immediateRadius of the focus: built at once, outside the per-frame budget, so the
        /// ground under the aircraft never waits.
        bool NearFocus(Double3 centreDir, Double3 focusDir, double angularRadius)
        {
            double angle = Math.Acos(Math.Max(-1.0, Math.Min(1.0, Double3.Dot(centreDir, focusDir))));
            return (angle - angularRadius) * GeoMath.A < immediateRadius;
        }

        void Traverse(TerrainTileSource tile, int level, int x, int y, bool force)
        {
            Chunk node = GetOrBuild(tile, level, x, y, force);
            if (node == null) return;
            if (!AboveHorizon(node)) return;

            if (level < tileMaxLevel[tile] && WantsSplit(node))
            {
                bool ready = true;
                for (int i = 0; i < 4 && ready; i++)
                {
                    if (GetOrBuild(tile, level + 1, x * 2 + (i & 1), y * 2 + (i >> 1), false) == null) ready = false;
                }

                if (ready)
                {
                    for (int i = 0; i < 4; i++) Traverse(tile, level + 1, x * 2 + (i & 1), y * 2 + (i >> 1), false);
                    return;
                }
            }

            Show(node);
        }

        /// Split while the vertex spacing would look wider than maxVertexAngle, or the float
        /// offsets in the chunk would be visibly coarse.
        bool WantsSplit(Chunk node)
        {
            double d = Math.Max(Double3.Distance(focusEcef, node.centre) - node.radius, 1.0);
            double dCamera = Math.Max(Double3.Distance(cameraEcef, node.centre) - node.radius, 1.0);
            double precision = node.radius * 1.2e-7;
            return node.spacing / d > maxVertexAngle / Math.Max(lodQuality, 0.05f) || precision / dCamera > 3e-4;
        }

        /// Visible if the chunk's top can rise above the camera's horizon.
        bool AboveHorizon(Chunk node)
        {
            double cosToNode = Double3.Dot(node.centre.Normalized, cameraEcef / cameraDistance);
            double angleToNode = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosToNode)));
            double nodeAngle = node.radius / GeoMath.B;
            return angleToNode - nodeAngle < cameraHorizonAngle + HorizonAngle(node.maxHeight) + 0.0035;
        }

        void Show(Chunk node)
        {
            node.lastUsedFrame = Time.frameCount;
            if (!node.shown)
            {
                node.renderer.enabled = true;
                node.shown = true;
            }

            shownThisFrame.Add(node);
            if (node.level > DeepestVisibleLevel) DeepestVisibleLevel = node.level;
        }

        // ---- cache ----------------------------------------------------------------

        static long Key(TerrainTileSource tile, int level, int x, int y) =>
            ((long)TerrainDataSet.Key(tile.Lat, tile.Lon) << 40) | ((long)level << 32) | ((long)x << 16) | (long)y;

        Chunk GetOrBuild(TerrainTileSource tile, int level, int x, int y, bool force)
        {
            long key = Key(tile, level, x, y);
            if (chunks.TryGetValue(key, out Chunk chunk))
            {
                chunk.lastUsedFrame = Math.Max(chunk.lastUsedFrame, Time.frameCount - 1);
                return chunk;
            }

            if (!force && !BudgetLeft()) return null;

            buildsThisFrame++;
            buildTimer.Restart();
            chunk = Build(tile, level, x, y);
            CountBuild();
            chunk.key = key;
            chunks.Add(key, chunk);
            return chunk;
        }

        bool BudgetLeft() =>
            buildsThisFrame < buildBudgetPerFrame && !(buildsThisFrame > 0 && buildMsThisFrame >= buildBudgetMs);

        void CountBuild()
        {
            float ms = (float)buildTimer.Elapsed.TotalMilliseconds;
            buildMsThisFrame += ms;
            BuildCount++;
            AverageBuildMs += (ms - AverageBuildMs) / 64f;
        }

        void Evict()
        {
            if (chunks.Count <= cacheSize) return;

            evictScratch.Clear();
            int cutoff = Time.frameCount - 2;
            foreach (Chunk chunk in chunks.Values)
            {
                if (chunk.lastUsedFrame < cutoff && !chunk.shown) evictScratch.Add(chunk.key);
            }

            evictScratch.Sort((a, b) => chunks[a].lastUsedFrame.CompareTo(chunks[b].lastUsedFrame));

            int excess = chunks.Count - cacheSize;
            for (int i = 0; i < evictScratch.Count && i < excess; i++)
            {
                DestroyChunk(chunks[evictScratch[i]]);
                chunks.Remove(evictScratch[i]);
            }
        }

        static void DestroyChunk(Chunk chunk)
        {
            if (chunk.mesh != null) Destroy(chunk.mesh);
            if (chunk.classTex != null) Destroy(chunk.classTex);
            if (chunk.go != null) Destroy(chunk.go);
        }

        // ---- building -------------------------------------------------------------

        /// How a chunk's class texels sit on the tile's data cells, for the shader's smooth
        /// blend: x = cells per texel, (y, z) = the cell coordinate of the chunk's south-west
        /// corner modulo 1. Where texels are coarser than the cells, the texels act as cells.
        Vector4 ClassCells(TerrainTileSource tile, int level, int x, int y)
        {
            float texSize = classTexels + 2 * TerrainChunkBuilder.ClassApron;
            double texelsPerDegree = (double)(1 << level) * classTexels;
            double cellsPerTexel = tile.M / texelsPerDegree;
            if (cellsPerTexel >= 1.0) return new Vector4(1f, 0f, 0f, texSize);
            double cellsPerChunk = (double)tile.M / (1 << level);
            double ox = x * cellsPerChunk, oy = y * cellsPerChunk;
            return new Vector4((float)cellsPerTexel, (float)(ox - Math.Floor(ox)), (float)(oy - Math.Floor(oy)), texSize);
        }

        Chunk Build(TerrainTileSource tile, int level, int x, int y)
        {
            Data.Touch(tile);
            TerrainChunkData d = buildData;
            TerrainChunkBuilder.Build(Data, tile, level, x, y, resolution, classTexels, true, d);
            double size = 1.0 / (1 << level);
            Chunk chunk = MakeChunk(d, $"Terrain {tile.Id} L{level} ({x},{y})", ClassCells(tile, level, x, y),
                                    tile, tile.Lat + y * size, tile.Lon + x * size, size);
            chunk.tile = tile;
            chunk.level = level;
            return chunk;
        }

        Chunk MakeChunk(TerrainChunkData d, string name, Vector4 classCells, TerrainTileSource tile,
                        double lat0, double lon0, double size)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(d.Vertices, 0, d.VertexCount);
            mesh.SetNormals(d.Normals, 0, d.VertexCount);
            mesh.SetUVs(0, d.ClassUv, 0, d.VertexCount);
            mesh.SetUVs(1, d.DetailUv, 0, d.VertexCount);
            mesh.SetIndices(d.Indices, 0, d.IndexCount, MeshTopology.Triangles, 0, true);
            mesh.RecalculateBounds();
            long meshBytes = (long)d.IndexCount * (mesh.indexFormat == IndexFormat.UInt32 ? 4 : 2);
            for (int s = 0; s < mesh.vertexBufferCount; s++) meshBytes += (long)mesh.GetVertexBufferStride(s) * d.VertexCount;
            mesh.UploadMeshData(true);

            var classTex = new Texture2D(d.ClassTextureSize, d.ClassTextureSize, TextureFormat.R8, false, true)
            {
                name = mesh.name + " classes",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            classTex.SetPixelData(d.ClassTexels, 0);
            classTex.Apply(false, true);

            var go = new GameObject(mesh.name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = castShadows;
            renderer.receiveShadows = receiveShadows;
            block.Clear();
            block.SetTexture(ClassTexId, classTex);
            block.SetVector(ClassCellsId, classCells);
            // A chunk at its split distance shows its vertex spacing at maxVertexAngle / lodQuality
            // radians; a pixel is about 1 mrad (1080p, 60 deg): so a metre there is this many pixels.
            GlobeAirports a = airports != null ? airports : GlobeAirports.Active;
            if (a != null)
                a.FillChunk(block, tile, lat0, lon0, size, d, maxVertexAngle / Math.Max(lodQuality, 0.05f) / 0.001);
            renderer.SetPropertyBlock(block);
            renderer.enabled = false;

            var chunk = new Chunk
            {
                centre = d.Centre,
                north = d.North,
                up = d.Up,
                radius = d.Radius,
                spacing = d.Spacing,
                maxHeight = d.MaxHeight,
                go = go,
                renderer = renderer,
                mesh = mesh,
                classTex = classTex,
                meshBytes = meshBytes,
                classTexBytes = (long)d.ClassTextureSize * d.ClassTextureSize,
                triangles = d.IndexCount / 3,
                lastUsedFrame = Time.frameCount,
            };

            PlaceChunk(chunk);
            return chunk;
        }
    }

    /// Tileable value noise for the terrain shader's detail layers: four independent octaves
    /// in R, G, B, A, each wrapping at the texture edge.
    static class TerrainDetailTexture
    {
        public static Texture2D Create(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "Terrain detail noise",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };

            var pixels = new Color32[size * size];
            int[] cells = { 4, 8, 16, 32 };
            var values = new float[4][];
            for (int c = 0; c < 4; c++)
            {
                var rng = new System.Random(1234 + c);
                values[c] = new float[cells[c] * cells[c]];
                for (int i = 0; i < values[c].Length; i++) values[c][i] = (float)rng.NextDouble();
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // two octaves per channel (cells and 2x cells) for a less blobby look
                    byte Channel(int c) => (byte)(Mathf.Clamp01(Sample(values[c], cells[c], x, y, size) * 0.7f +
                                                                Sample(values[c], cells[c], x * 2, y * 2, size) * 0.3f) * 255f);
                    pixels[y * size + x] = new Color32(Channel(0), Channel(1), Channel(2), Channel(3));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        static float Sample(float[] grid, int cells, int px, int py, int size)
        {
            float fx = (float)px / size * cells, fy = (float)py / size * cells;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float Get(int gx, int gy) => grid[((gy % cells + cells) % cells) * cells + ((gx % cells + cells) % cells)];
            float a = Mathf.Lerp(Get(x0, y0), Get(x0 + 1, y0), tx);
            float b = Mathf.Lerp(Get(x0, y0 + 1), Get(x0 + 1, y0 + 1), tx);
            return Mathf.Lerp(a, b, ty);
        }
    }
}
