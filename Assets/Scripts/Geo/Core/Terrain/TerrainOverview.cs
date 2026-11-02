using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CirrusFS.Geo
{
    /// A coarse whole-world copy of the terrain: heights and land classes at 16 per degree
    /// (about 7 km), for the LOD levels above the 1° tiles (2° to 16° chunks seen from high up
    /// or from orbit). Without it, every visible tile would draw at least one chunk, about
    /// 13,000 for a hemisphere of world data.
    ///
    /// Built once from the drawn tiles (one sequential read per tile file, on a worker
    /// thread) and cached on disk, keyed by the data folders' manifests, so later runs load it
    /// in a fraction of a second. Cells without a tile are sea: `seaHeight` and the water class.
    public sealed class TerrainOverview
    {
        public const int PerDegree = 16;
        public const int Width = 360 * PerDegree, Height = 180 * PerDegree;
        const int FileVersion = 1;

        readonly short[] heights;   // (Width + 1) x (Height + 1) vertices, row 0 = 90° S, column 0 = 180° W
        readonly byte[] classes;    // Width x Height cells

        TerrainOverview(short[] heights, byte[] classes)
        {
            this.heights = heights;
            this.classes = classes;
        }

        /// Bilinear height, metres. Longitude wraps; latitude clamps at the poles.
        public float SampleHeight(double latDeg, double lonDeg)
        {
            double fy = (Math.Max(-90.0, Math.Min(90.0, latDeg)) + 90.0) * PerDegree;
            double fx = (GeoMath.WrapLongitude(lonDeg) + 180.0) * PerDegree;
            int y = Math.Min((int)fy, Height - 1), x = Math.Min((int)fx, Width - 1);
            float ty = (float)(fy - y), tx = (float)(fx - x);
            int stride = Width + 1;
            int o = y * stride + x;
            float a = heights[o] + (heights[o + 1] - heights[o]) * tx;
            float b = heights[o + stride] + (heights[o + stride + 1] - heights[o + stride]) * tx;
            return a + (b - a) * ty;
        }

        public byte ClassAt(double latDeg, double lonDeg)
        {
            int y = (int)Math.Floor((Math.Max(-90.0, Math.Min(90.0, latDeg)) + 90.0) * PerDegree);
            int x = (int)Math.Floor((GeoMath.WrapLongitude(lonDeg) + 180.0) * PerDegree);
            y = y < 0 ? 0 : (y >= Height ? Height - 1 : y);
            x = ((x % Width) + Width) % Width;
            return classes[y * Width + x];
        }

        /// Reads every tile into a new overview. Runs on a worker thread: touches only the
        /// tiles' files, never their memory maps. `progress` gets 0..1.
        public static TerrainOverview Build(IReadOnlyList<TerrainTileSource> tiles, float seaHeight, int waterClass,
                                            Action<float> progress, CancellationToken cancel)
        {
            var h = new short[(Width + 1) * (Height + 1)];
            var c = new byte[Width * Height];
            short sea = (short)Math.Round(seaHeight);
            for (int i = 0; i < h.Length; i++) h[i] = sea;
            for (int i = 0; i < c.Length; i++) c[i] = (byte)waterClass;

            // Several tiles at once: each read is small and waits on the disk, which an SSD
            // serves in parallel (one at a time took ~23 ms a tile, 10 minutes for the world).
            // Every tile writes only its own cells, so the threads never touch the same element.
            int done = 0;
            var options = new System.Threading.Tasks.ParallelOptions
            {
                CancellationToken = cancel,
                MaxDegreeOfParallelism = Math.Max(2, Math.Min(8, Environment.ProcessorCount)),
            };
            System.Threading.Tasks.Parallel.For(0, tiles.Count, options, t =>
            {
                try
                {
                    tiles[t].ReadOverview(PerDegree, h, c);
                }
                catch (IOException)
                {
                    // a tile that cannot be read stays sea in the overview; it still draws close up
                }
                int n = Interlocked.Increment(ref done);
                if ((n & 63) == 0) progress?.Invoke((float)n / tiles.Count);
            });
            progress?.Invoke(1f);
            return new TerrainOverview(h, c);
        }

        /// Cache key for a set of data folders: their manifests' paths, sizes and dates.
        public static string CacheKey(IEnumerable<string> folders)
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                void Mix(string s)
                {
                    foreach (char ch in s) hash = (hash ^ ch) * 1099511628211UL;
                }
                Mix("overview" + FileVersion + "/" + PerDegree);
                foreach (string folder in folders)
                {
                    string manifest = Path.Combine(folder, "manifest.json");
                    Mix(Path.GetFullPath(folder).ToLowerInvariant());
                    if (!File.Exists(manifest)) continue;
                    var info = new FileInfo(manifest);
                    Mix(info.Length.ToString());
                    Mix(info.LastWriteTimeUtc.Ticks.ToString());
                }
                return hash.ToString("x16");
            }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(FileVersion);
                w.Write(PerDegree);
                var bytes = new byte[heights.Length * 2];
                Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
                w.Write(bytes);
                w.Write(classes);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// The cached overview, or null if it is missing or from another version.
        public static TerrainOverview Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path)))
                {
                    if (r.ReadInt32() != FileVersion || r.ReadInt32() != PerDegree) return null;
                    var h = new short[(Width + 1) * (Height + 1)];
                    byte[] bytes = r.ReadBytes(h.Length * 2);
                    if (bytes.Length != h.Length * 2) return null;
                    Buffer.BlockCopy(bytes, 0, h, 0, bytes.Length);
                    byte[] c = r.ReadBytes(Width * Height);
                    if (c.Length != Width * Height) return null;
                    return new TerrainOverview(h, c);
                }
            }
            catch (IOException)
            {
                return null;
            }
        }
    }
}
