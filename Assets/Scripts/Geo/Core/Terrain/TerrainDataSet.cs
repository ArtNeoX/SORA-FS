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
    public sealed class TerrainTileSource : IDisposable
    {
        public readonly int Lat, Lon;
        public readonly string Id, Directory;
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

        /// Bilinear height at a lat/lon, clamped to the tile. Exact at the vertices, and along
        /// an edge it only uses that edge's vertices, so both neighbours agree on it.
        public float SampleHeight(double latDeg, double lonDeg)
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

    /// Every terrain tile found in one or more exporter output folders. Folders are searched in
    /// order, so a fine regional folder listed before a coarse world folder wins where both
    /// have a tile. Tiles open lazily on first use and close again when idle.
    public sealed class TerrainDataSet : IDisposable
    {
        public readonly List<TerrainTileSource> Tiles = new List<TerrainTileSource>();

        /// Class definitions of the first folder (colour palette for the shader).
        public TerrainClassDef[] Classes = Array.Empty<TerrainClassDef>();
        public int WaterClass;

        readonly Dictionary<int, TerrainTileSource> byKey = new Dictionary<int, TerrainTileSource>();

        public static int Key(int lat, int lon) => (lat + 90) * 360 + (lon + 180);

        public static TerrainDataSet Load(IEnumerable<string> folders, Action<string> log = null)
        {
            var set = new TerrainDataSet();
            bool first = true;
            foreach (string folder in folders)
            {
                string manifestPath = Path.Combine(folder, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    log?.Invoke($"No terrain manifest at {manifestPath}");
                    continue;
                }

                var manifest = JsonUtility.FromJson<TerrainManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || manifest.version != 2 || manifest.height == null || manifest.landClass == null)
                {
                    log?.Invoke($"{manifestPath} is not a format-2 terrain manifest; skipped.");
                    continue;
                }

                if (first)
                {
                    set.Classes = manifest.classes ?? Array.Empty<TerrainClassDef>();
                    set.WaterClass = manifest.waterClass;
                    first = false;
                }

                int added = 0;
                foreach (TerrainTileInfo info in manifest.tiles ?? Array.Empty<TerrainTileInfo>())
                {
                    int key = Key(info.lat, info.lon);
                    if (set.byKey.ContainsKey(key)) continue;

                    var tile = new TerrainTileSource(folder, manifest, info);
                    set.byKey.Add(key, tile);
                    set.Tiles.Add(tile);
                    added++;
                }

                log?.Invoke($"Terrain: {added} tiles from {folder} ({manifest.height.samplesPerDegree} samples/deg, " +
                            $"{manifest.landClass.cellsPerDegree} class cells/deg)");
            }

            return set;
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
            foreach (TerrainTileSource t in Tiles) if (t.IsOpen) open++;
            if (open <= keepOpen) return;

            int cutoff = Time.frameCount - idleFrames;
            foreach (TerrainTileSource t in Tiles)
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
            foreach (TerrainTileSource t in Tiles) t.Dispose();
        }
    }
}
