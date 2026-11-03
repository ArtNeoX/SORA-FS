using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using UnityEngine;

namespace CirrusFS.Geo
{
    // ---- manifest (written by SceneryDataGen/terrain_exporter_v2, format 2) ---------------

    [Serializable] public class TerrainManifest
    {
        public string format;          // "CirrusTerrain"
        public int version;            // 2
        public TerrainHeightInfo height;
        public TerrainClassInfo landClass;
        public float seaHeight;
        public int waterClass;
        public TerrainClassDef[] classes;
        public TerrainTileInfo[] tiles;
    }

    [Serializable] public class TerrainHeightInfo
    {
        public string file;            // "height.r16"
        public int samplesPerDegree;   // n; a tile has (n + 1)^2 vertices, edges shared
        public int samples;            // n + 1
        public float min, max;         // metres = min + v / 65535 * (max - min)
    }

    [Serializable] public class TerrainClassInfo
    {
        public string file;            // "class.u8"
        public int cellsPerDegree;     // m; a tile has m^2 cells, each owned by one tile
    }

    [Serializable] public class TerrainClassDef { public int id; public string name; public string color; }

    [Serializable] public class TerrainTileInfo
    {
        public string id;              // "+37+127"
        public int lat, lon;           // south-west corner
        public string path;            // relative to the manifest
        public float hMin, hMax, landFraction;
    }

    /// One 1° x 1° tile on disk. The height and class files are memory-mapped, so an open tile
    /// costs address space and OS page cache, not managed memory, and a coarse chunk that
    /// touches a few hundred samples reads only those pages.
    ///
    /// Indices: height vertex [i, j] is at lat + i/n, lon + j/n (i = 0 south, j = 0 west);
    /// class cell [i, j] covers lat + i/m .. lat + (i+1)/m.
    ///
    /// Where a tile meets a tile of a lower-priority (coarser) data folder, its heights fade
    /// into that folder's surface over a short margin, so the two meet exactly: see
    /// TerrainDataSet.Load and SampleHeight.
    public sealed class TerrainTileSource : IDisposable
    {
        public readonly int Lat, Lon;
        public readonly string Id, Directory;

        /// The exporter output folder (manifest) this tile came from, and that folder's rank:
        /// 0 is the most detailed, which wins where folders overlap.
        public readonly string DataFolder;
        public int Priority { get; internal set; }
        public readonly int N, M;                 // samples / cells per degree
        public readonly float MinHeight, MaxHeight; // this tile's actual range, metres
        public readonly int WaterClass;

        /// Unit vector from the Earth's centre through the tile centre, for distance tests.
        public readonly Double3 CentreDirection;

        readonly string heightPath, classPath;
        readonly float decodeMin, decodeScale;

        MemoryMappedFile heightFile, classFile;
        MemoryMappedViewAccessor heights, classes;

        public bool IsOpen => heights != null;
        public int LastUsedFrame;

        internal TerrainTileSource(string root, TerrainManifest manifest, TerrainTileInfo info)
        {
            Lat = info.lat;
            Lon = info.lon;
            Id = info.id;
            DataFolder = root;
            Directory = Path.Combine(root, info.path);
            N = manifest.height.samplesPerDegree;
            M = manifest.landClass.cellsPerDegree;
            MinHeight = info.hMin;
            MaxHeight = info.hMax;
            WaterClass = manifest.waterClass;
            decodeMin = manifest.height.min;
            decodeScale = (manifest.height.max - manifest.height.min) / 65535f;
            heightPath = Path.Combine(Directory, manifest.height.file);
            classPath = Path.Combine(Directory, manifest.landClass.file);
            CentreDirection = GeoMath.GeodeticToEcef(Lat + 0.5, Lon + 0.5, 0.0).Normalized;
        }

        public void Open()
        {
            if (IsOpen) return;
            heightFile = MemoryMappedFile.CreateFromFile(heightPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            heights = heightFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            classFile = MemoryMappedFile.CreateFromFile(classPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            classes = classFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }

        public void Dispose()
        {
            heights?.Dispose();
            heightFile?.Dispose();
            classes?.Dispose();
            classFile?.Dispose();
            heights = null;
            heightFile = null;
            classes = null;
            classFile = null;
        }

        public bool Contains(double latDeg, double lonDeg) =>
            latDeg >= Lat && latDeg <= Lat + 1 && lonDeg >= Lon && lonDeg <= Lon + 1;

        /// Height of vertex [i, j], metres above the ellipsoid. Indices are clamped to the tile.
        public float Vertex(int i, int j)
        {
            i = i < 0 ? 0 : (i > N ? N : i);
            j = j < 0 ? 0 : (j > N ? N : j);
            return decodeMin + heights.ReadUInt16(((long)i * (N + 1) + j) * 2) * decodeScale;
        }

        // ---- blending into a coarser neighbour ----------------------------------------

        /// An edge (or corner) of this tile that touches a tile of a lower-priority folder:
        /// within `Margin` degrees of it, heights fade into `Target`, the same cell from that
        /// folder, which matches the neighbour exactly along the edge.
        internal readonly struct EdgeBlend
        {
            public readonly int Dy, Dx;              // direction of the neighbour, -1..1
            public readonly TerrainTileSource Target;
            public readonly double Margin;

            public EdgeBlend(int dy, int dx, TerrainTileSource target, double margin)
            {
                Dy = dy;
                Dx = dx;
                Target = target;
                Margin = margin;
            }

            /// Distance in degrees from (u, v) in the tile (0..1) to that edge or corner.
            public double Distance(double u, double v)
            {
                double du = Dx > 0 ? 1.0 - u : (Dx < 0 ? u : 0.0);
                double dv = Dy > 0 ? 1.0 - v : (Dy < 0 ? v : 0.0);
                if (Dx != 0 && Dy != 0) return Math.Sqrt(du * du + dv * dv);
                return Dx != 0 ? du : dv;
            }
        }

        internal EdgeBlend[] Blends;

        /// True where this tile fades into a coarser folder near some edge.
        public bool BlendsAtEdges => Blends != null && Blends.Length > 0;

        /// Height of the drawn surface at a lat/lon: the tile's own bilinear surface, faded into
        /// the coarser folder near edges it shares with that folder (smoothstep, all of it on
        /// the edge itself). Everything that needs ground height goes through this.
        public float SampleHeight(double latDeg, double lonDeg)
        {
            float h = SampleOwnHeight(latDeg, lonDeg);
            if (Blends == null) return h;

            double u = lonDeg - Lon, v = latDeg - Lat;
            foreach (EdgeBlend b in Blends)
            {
                double d = b.Distance(u, v);
                if (d >= b.Margin) continue;
                TerrainTileSource target = b.Target;
                if (!target.IsOpen) target.Open();
                target.LastUsedFrame = LastUsedFrame;
                float coarse = target.SampleHeight(latDeg, lonDeg);
                double x = 1.0 - Math.Max(0.0, d) / b.Margin;
                double t = x * x * (3.0 - 2.0 * x);
                // exactly the coarse height on the edge, so the neighbour meets it bit for bit
                h = d <= 0.0 ? coarse : h + (float)t * (coarse - h);
            }
            return h;
        }

        /// Bilinear height at a lat/lon, clamped to the tile, without any edge blending. Exact
        /// at the vertices, and along an edge it only uses that edge's vertices, so both
        /// neighbours from the same folder agree on it.
        public float SampleOwnHeight(double latDeg, double lonDeg)
        {
            double fi = (latDeg - Lat) * N, fj = (lonDeg - Lon) * N;
            fi = fi < 0 ? 0 : (fi > N ? N : fi);
            fj = fj < 0 ? 0 : (fj > N ? N : fj);
            int i = Math.Min((int)fi, N - 1), j = Math.Min((int)fj, N - 1);
            float ti = (float)(fi - i), tj = (float)(fj - j);

            float h00 = Vertex(i, j), h01 = Vertex(i, j + 1);
            float h10 = Vertex(i + 1, j), h11 = Vertex(i + 1, j + 1);
            float a = h00 + (h01 - h00) * tj;
            float b = h10 + (h11 - h10) * tj;
            return a + (b - a) * ti;
        }

        /// Class id of the cell containing a lat/lon, clamped to the tile.
        public byte ClassAt(double latDeg, double lonDeg)
        {
            int i = (int)Math.Floor((latDeg - Lat) * M), j = (int)Math.Floor((lonDeg - Lon) * M);
            i = i < 0 ? 0 : (i >= M ? M - 1 : i);
            j = j < 0 ? 0 : (j >= M ? M - 1 : j);
            return classes.ReadByte((long)i * M + j);
        }
    }

    /// Every terrain tile found in one or more exporter output folders. Tiles open lazily on
    /// first use and close again when idle.
    ///
    /// Priority: the more detailed folder wins wherever folders overlap (more height samples
    /// per degree, then more class cells; equal detail keeps the order given). So a coarse
    /// whole-world folder and detailed regional folders (a city, a country) can sit side by
    /// side: each 1° cell draws its most detailed tile. Where a detailed tile borders a cell
    /// that only a coarser folder covers, its heights fade into that folder's surface over a
    /// few coarse samples (TerrainTileSource.SampleHeight), so there is no crack or step.
    public sealed class TerrainDataSet : IDisposable
    {
        /// The winning tile of every cell.
        public readonly List<TerrainTileSource> Tiles = new List<TerrainTileSource>();

        /// Winners and the tiles they cover (blend targets), for opening and closing.
        public readonly List<TerrainTileSource> AllTiles = new List<TerrainTileSource>();

        /// Exporter output folders in priority order, most detailed first.
        public readonly List<string> Folders = new List<string>();

        /// Class definitions of the first folder (colour palette for the shader).
        public TerrainClassDef[] Classes = Array.Empty<TerrainClassDef>();
        public int WaterClass;
        public float SeaHeight = -5f;

        readonly Dictionary<int, TerrainTileSource> byKey = new Dictionary<int, TerrainTileSource>();

        public static int Key(int lat, int lon) => (lat + 90) * 360 + (lon + 180);

        /// Loads the exporter output folders found at `folders`. Each entry may be an output
        /// folder itself (it holds manifest.json) and/or a parent whose direct subfolders are
        /// output folders, e.g. TerrainData/{Global, Korea, Tokyo}. Both count.
        public static TerrainDataSet Load(IEnumerable<string> folders, Action<string> log = null)
        {
            var set = new TerrainDataSet();

            // ---- find and read every manifest -------------------------------------------
            var found = new List<(string folder, TerrainManifest manifest, int order)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in folders)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var candidates = new List<string> { entry };
                if (System.IO.Directory.Exists(entry))
                {
                    var subs = new List<string>(System.IO.Directory.GetDirectories(entry));
                    subs.Sort(StringComparer.OrdinalIgnoreCase);
                    candidates.AddRange(subs);
                }

                bool any = false;
                foreach (string folder in candidates)
                {
                    string manifestPath = Path.Combine(folder, "manifest.json");
                    if (!File.Exists(manifestPath) || !seen.Add(Path.GetFullPath(folder))) continue;

                    var manifest = JsonUtility.FromJson<TerrainManifest>(File.ReadAllText(manifestPath));
                    if (manifest == null || manifest.version != 2 || manifest.height == null || manifest.landClass == null)
                    {
                        log?.Invoke($"{manifestPath} is not a format-2 terrain manifest; skipped.");
                        continue;
                    }
                    found.Add((folder, manifest, found.Count));
                    any = true;
                }
                if (!any) log?.Invoke($"No terrain manifest in {entry} or its subfolders");
            }

            // most detailed first; equal detail keeps the given order
            found.Sort((a, b) =>
            {
                int c = b.manifest.height.samplesPerDegree.CompareTo(a.manifest.height.samplesPerDegree);
                if (c == 0) c = b.manifest.landClass.cellsPerDegree.CompareTo(a.manifest.landClass.cellsPerDegree);
                return c != 0 ? c : a.order.CompareTo(b.order);
            });

            // ---- stack the tiles of every cell, best first -------------------------------
            var stacks = new Dictionary<int, List<TerrainTileSource>>();
            for (int priority = 0; priority < found.Count; priority++)
            {
                (string folder, TerrainManifest manifest, _) = found[priority];
                if (priority == 0)
                {
                    set.Classes = manifest.classes ?? Array.Empty<TerrainClassDef>();
                    set.WaterClass = manifest.waterClass;
                    set.SeaHeight = manifest.seaHeight;
                }
                else if (!SameClasses(set.Classes, manifest.classes))
                {
                    log?.Invoke($"Warning: {folder} has a different land-class list than {found[0].folder}; " +
                                "class ids are read with the first list's meaning.");
                }

                int added = 0, covered = 0;
                foreach (TerrainTileInfo info in manifest.tiles ?? Array.Empty<TerrainTileInfo>())
                {
                    var tile = new TerrainTileSource(folder, manifest, info) { Priority = priority };
                    int key = Key(info.lat, info.lon);
                    if (!stacks.TryGetValue(key, out List<TerrainTileSource> stack))
                    {
                        stacks.Add(key, stack = new List<TerrainTileSource>());
                        set.byKey.Add(key, tile);
                        set.Tiles.Add(tile);
                        added++;
                    }
                    else covered++;
                    stack.Add(tile);
                    set.AllTiles.Add(tile);
                }
                set.Folders.Add(folder);

                log?.Invoke($"Terrain: {folder} ({manifest.height.samplesPerDegree} samples/deg, " +
                            $"{manifest.landClass.cellsPerDegree} class cells/deg), priority {priority + 1}: " +
                            $"{added} tiles drawn" + (covered > 0 ? $", {covered} covered by more detailed data" : ""));
            }

            // ---- fade detailed tiles into coarser neighbours -----------------------------
            int blended = 0;
            foreach (TerrainTileSource tile in set.Tiles)
            {
                List<TerrainTileSource> stack = stacks[Key(tile.Lat, tile.Lon)];
                List<TerrainTileSource.EdgeBlend> blends = null;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dy == 0 && dx == 0) continue;
                        int nLat = tile.Lat + dy;
                        if (nLat < -90 || nLat > 89) continue;
                        int nLon = (int)GeoMath.WrapLongitude(tile.Lon + dx);
                        if (nLon >= 180) nLon -= 360;
                        TerrainTileSource neighbour = set.TileAt(nLat, nLon);
                        if (neighbour == null || neighbour.Priority <= tile.Priority) continue;

                        // this cell as drawn by the neighbour's folder meets the neighbour exactly
                        TerrainTileSource target = stack.Find(t => t.Priority == neighbour.Priority);
                        if (target == null) continue;
                        // eight coarse samples (0.02° for a 400/deg world folder), within reason
                        double margin = Math.Min(0.25, Math.Max(0.01, 8.0 / target.N));
                        (blends ??= new List<TerrainTileSource.EdgeBlend>()).Add(
                            new TerrainTileSource.EdgeBlend(dy, dx, target, margin));
                    }
                }
                if (blends == null) continue;
                tile.Blends = blends.ToArray();
                blended++;
            }
            if (blended > 0)
                log?.Invoke($"Terrain: {blended} detailed tiles fade into coarser data at their edges");

            return set;
        }

        static bool SameClasses(TerrainClassDef[] a, TerrainClassDef[] b)
        {
            a ??= Array.Empty<TerrainClassDef>();
            b ??= Array.Empty<TerrainClassDef>();
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].id != b[i].id || !string.Equals(a[i].name, b[i].name, StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        public TerrainTileSource TileAt(int lat, int lon) =>
            byKey.TryGetValue(Key(lat, lon), out TerrainTileSource t) ? t : null;

        /// Tile containing a point, opened. Null where there is no terrain (open sea).
        public TerrainTileSource TileContaining(double latDeg, double lonDeg)
        {
            int lat = (int)Math.Floor(latDeg), lon = (int)Math.Floor(GeoMath.WrapLongitude(lonDeg));
            if (lat >= 90) lat = 89;
            TerrainTileSource t = TileAt(lat, lon >= 180 ? lon - 360 : lon);
            Touch(t);
            return t;
        }

        public void Touch(TerrainTileSource tile)
        {
            if (tile == null) return;
            if (!tile.IsOpen) tile.Open();
            tile.LastUsedFrame = Time.frameCount;
        }

        /// Height at any lat/lon: from the tile there, or from `fallback` (clamped) where no
        /// tile exists. Used for normals that look across a tile edge.
        public float SampleHeight(double latDeg, double lonDeg, TerrainTileSource fallback)
        {
            if (fallback != null && fallback.Contains(latDeg, lonDeg)) return fallback.SampleHeight(latDeg, lonDeg);
            TerrainTileSource t = TileContaining(latDeg, lonDeg);
            return (t ?? fallback).SampleHeight(latDeg, lonDeg);
        }

        public byte ClassAt(double latDeg, double lonDeg, TerrainTileSource fallback)
        {
            if (fallback != null && fallback.Contains(latDeg, lonDeg) &&
                latDeg < fallback.Lat + 1 && lonDeg < fallback.Lon + 1)
                return fallback.ClassAt(latDeg, lonDeg);
            TerrainTileSource t = TileContaining(latDeg, lonDeg);
            return t != null ? t.ClassAt(latDeg, lonDeg) : (byte)WaterClass;
        }

        /// Ground height for spawning and readouts: terrain where there is a tile, else null.
        public bool TryGetHeight(double latDeg, double lonDeg, out float height)
        {
            TerrainTileSource t = TileContaining(latDeg, lonDeg);
            height = t != null ? t.SampleHeight(latDeg, lonDeg) : 0f;
            return t != null;
        }

        /// Closes tiles unused for `idleFrames`, keeping at most `keepOpen` open.
        public void CloseIdle(int idleFrames, int keepOpen)
        {
            int open = 0;
            foreach (TerrainTileSource t in AllTiles) if (t.IsOpen) open++;
            if (open <= keepOpen) return;

            int cutoff = Time.frameCount - idleFrames;
            foreach (TerrainTileSource t in AllTiles)
            {
                if (open <= keepOpen) break;
                if (t.IsOpen && t.LastUsedFrame < cutoff)
                {
                    t.Dispose();
                    open--;
                }
            }
        }

        public void Dispose()
        {
            foreach (TerrainTileSource t in AllTiles) t.Dispose();
        }
    }
}
