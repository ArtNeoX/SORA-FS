using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// Ground where autogen must not stand (airport frames). `Touches` is asked once per
    /// patch, `Excludes` only for the candidates of patches it touches.
    public interface IAutogenExclusion
    {
        bool Touches(double latMin, double lonMin, double latMax, double lonMax);
        bool Excludes(double latDeg, double lonDeg);
    }

    /// Where autogen objects (trees, later buildings) stand, from the terrain tiles alone.
    ///
    /// The world is cut into patches of 1/128° × 1/128° (about 870 × 690 m at 37° N), each
    /// inside one terrain tile. A patch is a jittered grid of candidate points with the
    /// layer's density; a candidate is kept where the class cell under it is one of the
    /// layer's classes. Each candidate always draws the same random numbers from a generator
    /// seeded by the patch and layer, so a patch comes out identical every time it is built,
    /// on any machine, and nothing has to be stored.
    ///
    /// Positions are relative to the patch centre in its east/up/north frame (Unity's x/y/z
    /// at that point), computed through GeoMath in double, on the terrain's drawn surface:
    /// the triangles of its finest level (TerrainChunkBuilder.SurfaceHeight), which is also
    /// what the colliders use.
    public static class AutogenPlacement
    {
        public const int PatchesPerDegree = 128;
        public const double PatchSize = 1.0 / PatchesPerDegree;

        /// Instances per patch at most (two float4 each on the GPU).
        public const int MaxInstances = 65536;

        public struct Settings
        {
            public double densityPerHectare;
            public bool[] classMask;        // 256 entries: true = place on this class
            public float minScale, maxScale;
            public bool randomYaw;
            public int variants;            // texture variants to pick from
            public float tintVariation;     // ± brightness
            public float sink;              // metres pushed into the ground
            public uint seed;               // per layer
        }

        /// One built patch: frame, bounds (patch frame) and instances.
        /// Instance k is data[2k] = (x, y, z, yaw radians), data[2k + 1] = (scale, variant,
        /// thinning random 0..1, brightness).
        public sealed class Result
        {
            public Double3 Centre, East, North, Up;
            public Vector3 Min, Max;
            public readonly List<Vector4> Data = new List<Vector4>();
            public int Count => Data.Count / 2;
        }

        public static long Row(double latDeg) => (long)Math.Floor((latDeg + 90.0) * PatchesPerDegree);
        public static long Column(double lonDeg) =>
            ((long)Math.Floor((GeoMath.WrapLongitude(lonDeg) + 180.0) * PatchesPerDegree) % (360L * PatchesPerDegree)
             + 360L * PatchesPerDegree) % (360L * PatchesPerDegree);

        public static double SouthOf(long row) => row * PatchSize - 90.0;
        public static double WestOf(long column) => column * PatchSize - 180.0;

        /// Tile (south-west corner) that holds a patch.
        public static void TileOf(long row, long column, out int lat, out int lon)
        {
            lat = (int)Math.Floor(SouthOf(row) + 0.5 * PatchSize);
            lon = (int)Math.Floor(WestOf(column) + 0.5 * PatchSize);
        }

        /// Fills `r` for patch (row, column) of `tile`. Heights follow the terrain mesh with
        /// `meshVerticesPerDegree` vertices per degree (its finest level), or the tile's
        /// bilinear surface when that is 0. Returns the instance count.
        public static int Build(TerrainTileSource tile, long row, long column, in Settings s, Result r,
                                long meshVerticesPerDegree = 0, IAutogenExclusion exclusion = null,
                                float seaFloorAbove = float.NegativeInfinity)
        {
            r.Data.Clear();
            double lat0 = SouthOf(row), lon0 = WestOf(column);
            if (exclusion != null && !exclusion.Touches(lat0, lon0, lat0 + PatchSize, lon0 + PatchSize)) exclusion = null;
            double latC = lat0 + 0.5 * PatchSize, lonC = lon0 + 0.5 * PatchSize;

            float hC = tile.SampleHeight(latC, lonC);
            r.Centre = GeoMath.GeodeticToEcef(latC, lonC, hC);
            GeoMath.EnuBasis(latC, lonC, out r.East, out r.North, out r.Up);
            r.Min = r.Max = Vector3.zero;
            if (s.densityPerHectare <= 0.0 || s.classMask == null) return 0;

            // grid with the requested density: one jittered candidate per cell
            double phi = latC * GeoMath.Deg2Rad;
            double sinP = Math.Sin(phi);
            double w = Math.Sqrt(1.0 - GeoMath.E2 * sinP * sinP);
            double metresNorth = GeoMath.A * (1.0 - GeoMath.E2) / (w * w * w) * GeoMath.Deg2Rad * PatchSize;
            double metresEast = GeoMath.A / w * Math.Cos(phi) * GeoMath.Deg2Rad * PatchSize;
            double spacing = Math.Sqrt(10000.0 / s.densityPerHectare);
            int nx = Math.Max(1, (int)Math.Round(metresEast / spacing));
            int ny = Math.Max(1, (int)Math.Round(metresNorth / spacing));
            if ((long)nx * ny > MaxInstances)
            {
                double k = Math.Sqrt((double)nx * ny / MaxInstances);
                nx = Math.Max(1, (int)(nx / k));
                ny = Math.Max(1, (int)(ny / k));
            }

            var rng = new Rng(Hash((ulong)row * 0x9E3779B97F4A7C15UL ^ (ulong)column ^ ((ulong)s.seed << 40)));
            int variants = Math.Max(1, s.variants);
            bool first = true;

            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    // always draw all seven numbers, so a candidate's values never depend on
                    // whether an earlier one was kept
                    double fx = (i + rng.Next()) / nx;
                    double fy = (j + rng.Next()) / ny;
                    double yaw = rng.Next() * 2.0 * Math.PI;
                    double scale = rng.Next();
                    double variant = rng.Next();
                    double thin = rng.Next();
                    double tint = rng.Next();

                    double lat = lat0 + fy * PatchSize, lon = lon0 + fx * PatchSize;
                    if (!s.classMask[tile.ClassAt(lat, lon)]) continue;
                    if (exclusion != null && exclusion.Excludes(lat, lon)) continue;

                    double ground = meshVerticesPerDegree > 0
                        ? TerrainChunkBuilder.SurfaceHeight(tile, lat, lon, meshVerticesPerDegree)
                        : tile.SampleHeight(lat, lon);
                    // the exporter's sea floor is drawn as sea at sea level: nothing grows there
                    if (ground <= seaFloorAbove) continue;
                    double h = ground - s.sink;
                    Double3 rel = GeoMath.GeodeticToEcef(lat, lon, h) - r.Centre;
                    var p = new Vector3((float)Double3.Dot(rel, r.East), (float)Double3.Dot(rel, r.Up),
                                        (float)Double3.Dot(rel, r.North));

                    r.Data.Add(new Vector4(p.x, p.y, p.z, s.randomYaw ? (float)yaw : 0f));
                    r.Data.Add(new Vector4(Mathf.Lerp(s.minScale, s.maxScale, (float)scale),
                                           Math.Min(variants - 1, (int)(variant * variants)), (float)thin,
                                           1f + (float)(tint * 2.0 - 1.0) * s.tintVariation));

                    if (first) { r.Min = r.Max = p; first = false; }
                    else
                    {
                        r.Min = Vector3.Min(r.Min, p);
                        r.Max = Vector3.Max(r.Max, p);
                    }
                }
            }
            return r.Count;
        }

        static ulong Hash(ulong x)
        {
            // SplitMix64 finaliser
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        /// xorshift64*: small, fast and the same on every platform.
        struct Rng
        {
            ulong state;

            public Rng(ulong seed) { state = seed == 0 ? 0x2545F4914F6CDD1DUL : seed; }

            public double Next()
            {
                state ^= state >> 12;
                state ^= state << 25;
                state ^= state >> 27;
                return ((state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / (1UL << 53));
            }
        }
    }
}
