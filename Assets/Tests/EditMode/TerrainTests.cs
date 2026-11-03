using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CirrusFS.Geo.Tests
{
    /// The terrain data layer and chunk builder, on two tiny synthetic tiles written in the
    /// exporter's format 2 (+37+127 and +37+128, sharing the lon = 128 edge).
    public class TerrainTests
    {
        const int N = 8;        // height samples per degree
        const int M = 4;        // class cells per degree
        const float HMin = -500f, HMax = 9000f;

        string root;
        TerrainDataSet set;

        static double Truth(double lat, double lon) => 300.0 + 120.0 * (lat - 37.0) - 80.0 * (lon - 127.0);

        [SetUp]
        public void WriteTiles()
        {
            root = Path.Combine(Path.GetTempPath(), "CirrusTerrainTest_" + Guid.NewGuid().ToString("N"));
            WriteTile(37, 127);
            WriteTile(37, 128);

            string tiles =
                Tile(37, 127) + "," + Tile(37, 128);
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                "{\"format\":\"CirrusTerrain\",\"version\":2," +
                $"\"height\":{{\"file\":\"height.r16\",\"samplesPerDegree\":{N},\"samples\":{N + 1},\"min\":{HMin},\"max\":{HMax}}}," +
                $"\"landClass\":{{\"file\":\"class.u8\",\"cellsPerDegree\":{M}}}," +
                "\"seaHeight\":-5,\"waterClass\":0," +
                "\"classes\":[{\"id\":0,\"name\":\"Water\",\"color\":\"#2b5f8f\"},{\"id\":5,\"name\":\"Forest\",\"color\":\"#2f5f2a\"}]," +
                "\"tiles\":[" + tiles + "]}");

            set = TerrainDataSet.Load(new[] { root });
        }

        [TearDown]
        public void Cleanup()
        {
            set?.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        static string Name(int lat, int lon) => $"{(lat < 0 ? "-" : "+")}{Math.Abs(lat):00}{(lon < 0 ? "-" : "+")}{Math.Abs(lon):000}";

        static string Tile(int lat, int lon) =>
            $"{{\"id\":\"{Name(lat, lon)}\",\"lat\":{lat},\"lon\":{lon},\"path\":\"tiles/+30+120/{Name(lat, lon)}\",\"hMin\":0,\"hMax\":1000,\"landFraction\":1}}";

        void WriteTile(int lat, int lon)
        {
            string dir = Path.Combine(root, "tiles", "+30+120", Name(lat, lon));
            Directory.CreateDirectory(dir);

            var h = new byte[(N + 1) * (N + 1) * 2];
            for (int i = 0, k = 0; i <= N; i++)
            {
                for (int j = 0; j <= N; j++, k += 2)
                {
                    double metres = Truth((lat * (double)N + i) / N, (lon * (double)N + j) / N);
                    ushort v = (ushort)Math.Round((metres - HMin) / (HMax - HMin) * 65535.0);
                    h[k] = (byte)(v & 0xFF);
                    h[k + 1] = (byte)(v >> 8);
                }
            }
            File.WriteAllBytes(Path.Combine(dir, "height.r16"), h);

            // class of cell [i, j] = i * M + j, to check orientation (row 0 = south)
            var c = new byte[M * M];
            for (int i = 0; i < M; i++)
                for (int j = 0; j < M; j++)
                    c[i * M + j] = (byte)(i * M + j);
            File.WriteAllBytes(Path.Combine(dir, "class.u8"), c);
        }

        const float Step = (HMax - HMin) / 65535f;

        [Test]
        public void Load_FindsTilesAndDecodesHeights()
        {
            Assert.AreEqual(2, set.Tiles.Count);
            TerrainTileSource t = set.TileContaining(37.5, 127.5);
            Assert.AreEqual("+37+127", t.Id);
            Assert.IsNull(set.TileContaining(36.5, 127.5), "no tile south of the data");

            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                    Assert.AreEqual(Truth(37 + i / (double)N, 127 + j / (double)N), t.Vertex(i, j), Step, $"vertex {i},{j}");
        }

        [Test]
        public void SampleHeight_IsBilinearAndAgreesAcrossTheEdge()
        {
            // a plane is reproduced exactly by bilinear interpolation (to the 16-bit step)
            var rng = new System.Random(5);
            for (int s = 0; s < 200; s++)
            {
                double lat = 37 + rng.NextDouble(), lon = 127 + 2 * rng.NextDouble();
                Assert.AreEqual(Truth(lat, lon), set.SampleHeight(lat, lon, null), 2 * Step);
            }

            TerrainTileSource a = set.TileAt(37, 127), b = set.TileAt(37, 128);
            set.Touch(a);
            set.Touch(b);
            for (int s = 0; s <= 20; s++)
            {
                double lat = 37 + s / 20.0;
                Assert.AreEqual(a.SampleHeight(lat, 128.0), b.SampleHeight(lat, 128.0), 0f, "shared edge");
            }
        }

        [Test]
        public void ClassAt_RowZeroIsSouth()
        {
            TerrainTileSource t = set.TileContaining(37.1, 127.1);
            for (int i = 0; i < M; i++)
                for (int j = 0; j < M; j++)
                    Assert.AreEqual(i * M + j, t.ClassAt(37 + (i + 0.5) / M, 127 + (j + 0.5) / M), $"cell {i},{j}");

            Assert.AreEqual(0, set.ClassAt(36.5, 127.5, null), "no tile: water class");
        }

        [Test]
        public void MaxUsefulLevel_StopsAtTheData()
        {
            TerrainTileSource t = set.TileAt(37, 127);
            Assert.AreEqual(0, TerrainChunkBuilder.MaxUsefulLevel(t, 8, 4)); // 8 samples, 4 cells already
            Assert.AreEqual(2, TerrainChunkBuilder.MaxUsefulLevel(t, 2, 1)); // 2 * 4 = 8 samples, 1 * 4 = 4 cells
        }

        static Double3 ToEcef(TerrainChunkData d, int v) =>
            d.Centre + d.East * d.Vertices[v].x + d.Up * d.Vertices[v].y + d.North * d.Vertices[v].z;

        [Test]
        public void Chunks_MeetExactlyAcrossTileAndChunkEdges()
        {
            const int r = 16, tex = 8;
            TerrainTileSource a = set.TileAt(37, 127), b = set.TileAt(37, 128);
            var da = new TerrainChunkData();
            var db = new TerrainChunkData();

            // level 1: A's east chunk (x = 1) against B's west chunk (x = 0), across lon 128
            TerrainChunkBuilder.Build(set, a, 1, 1, 0, r, tex, true, da);
            TerrainChunkBuilder.Build(set, b, 1, 0, 0, r, tex, true, db);
            double worst = 0.0;
            for (int j = 0; j <= r; j++)
                worst = Math.Max(worst, Double3.Distance(ToEcef(da, j * (r + 1) + r), ToEcef(db, j * (r + 1))));
            Assert.Less(worst, 0.002, "tile edge vertices differ by " + worst + " m");

            // level 2 inside one tile: chunk (0,0) against its north neighbour (0,1)
            TerrainChunkBuilder.Build(set, a, 2, 0, 0, r, tex, true, da);
            TerrainChunkBuilder.Build(set, a, 2, 0, 1, r, tex, true, db);
            worst = 0.0;
            for (int i = 0; i <= r; i++)
                worst = Math.Max(worst, Double3.Distance(ToEcef(da, r * (r + 1) + i), ToEcef(db, i)));
            Assert.Less(worst, 0.002, "chunk edge vertices differ by " + worst + " m");

            // every vertex lies at its geodetic height
            for (int v = 0; v < (r + 1) * (r + 1); v++)
            {
                GeoMath.EcefToGeodetic(ToEcef(da, v), out double lat, out double lon, out double h);
                Assert.AreEqual(Truth(lat, lon), h, 0.05 + 2 * Step, $"vertex {v} height");
            }
        }

        [Test]
        public void Normals_TiltDownhill()
        {
            // Truth rises 120 m per degree north and falls 80 m per degree east, so the normal
            // leans south and east of local up.
            var d = new TerrainChunkData();
            TerrainChunkBuilder.Build(set, set.TileAt(37, 127), 0, 0, 0, 8, 4, false, d);
            int v = 4 * 9 + 4;   // centre vertex
            Vector3 n = d.Normals[v];
            Assert.Greater(n.y, 0.99f, "mostly up");
            Assert.Less(n.z, 0f, "uphill to the north: normal leans south");
            Assert.Greater(n.x, 0f, "downhill to the east: normal leans east");

            double slopeN = 120.0 / (GeoMath.Deg2Rad * 6.36e6);
            Assert.AreEqual(-slopeN, n.z / n.y, slopeN * 0.05, "north slope magnitude");
        }

        // ---- autogen placement ------------------------------------------------------
        // Class cell [1, 1] of +37+127 has id 5 ("Forest"): lat 37.25..37.5, lon 127.25..127.5.

        static AutogenPlacement.Settings ForestSettings(double density)
        {
            var mask = new bool[256];
            mask[5] = true;
            return new AutogenPlacement.Settings
            {
                densityPerHectare = density, classMask = mask, minScale = 0.8f, maxScale = 1.2f,
                randomYaw = true, variants = 3, tintVariation = 0.1f, sink = 0.5f, seed = 7,
            };
        }

        [Test]
        public void Autogen_SamePatchSameTrees()
        {
            TerrainTileSource tile = set.TileContaining(37.3, 127.3);
            long row = AutogenPlacement.Row(37.3), col = AutogenPlacement.Column(127.3);
            var a = new AutogenPlacement.Result();
            var b = new AutogenPlacement.Result();
            AutogenPlacement.Build(tile, row, col, ForestSettings(40), a);
            AutogenPlacement.Build(tile, row, col, ForestSettings(40), b);
            Assert.Greater(a.Count, 0);
            CollectionAssert.AreEqual(a.Data, b.Data);
        }

        [Test]
        public void Autogen_OnlyOnItsClass_AtTheRequestedDensity()
        {
            TerrainTileSource tile = set.TileContaining(37.3, 127.3);
            var r = new AutogenPlacement.Result();

            // patch inside the forest cell: every candidate is kept
            AutogenPlacement.Build(tile, AutogenPlacement.Row(37.3), AutogenPlacement.Column(127.3), ForestSettings(40), r);
            double km = 111.0 * AutogenPlacement.PatchSize;
            double hectares = km * km * Math.Cos(37.3 * GeoMath.Deg2Rad) * 100.0;
            Assert.AreEqual(40.0 * hectares, r.Count, 40.0 * hectares * 0.1, "density");

            // patch inside a cell of another class: none
            AutogenPlacement.Build(tile, AutogenPlacement.Row(37.1), AutogenPlacement.Column(127.1), ForestSettings(40), r);
            Assert.AreEqual(0, r.Count);

            // variant, scale and yaw in range
            AutogenPlacement.Build(tile, AutogenPlacement.Row(37.3), AutogenPlacement.Column(127.3), ForestSettings(40), r);
            for (int k = 0; k < r.Count; k++)
            {
                Vector4 a = r.Data[2 * k], b = r.Data[2 * k + 1];
                Assert.That(a.w, Is.InRange(0f, 2f * Mathf.PI));
                Assert.That(b.x, Is.InRange(0.8f, 1.2f));
                Assert.That(b.y, Is.EqualTo(0f).Or.EqualTo(1f).Or.EqualTo(2f));
                Assert.That(b.z, Is.InRange(0f, 1f));
            }
        }

        [Test]
        public void SurfaceHeight_FollowsTheChunkTriangles()
        {
            TerrainTileSource tile = set.TileContaining(37.3, 127.3);
            const long k = 16;   // level 1, R = 8
            // exact at the grid vertices
            Assert.AreEqual(tile.SampleHeight(37 + 3.0 / k, 127 + 5.0 / k),
                            TerrainChunkBuilder.SurfaceHeight(tile, 37 + 3.0 / k, 127 + 5.0 / k, k), 1e-3f);
            // on the shared diagonal (south-east to north-west) it is the mean of its two ends
            float b = tile.SampleHeight(37 + 3.0 / k, 127 + 6.0 / k), c = tile.SampleHeight(37 + 4.0 / k, 127 + 5.0 / k);
            Assert.AreEqual(0.5f * (b + c), TerrainChunkBuilder.SurfaceHeight(tile, 37 + 3.5 / k, 127 + 5.5 / k, k), 1e-3f);
        }

        [Test]
        public void Autogen_StandsOnTheSurface_InsideThePatch()
        {
            TerrainTileSource tile = set.TileContaining(37.3, 127.3);
            long row = AutogenPlacement.Row(37.3), col = AutogenPlacement.Column(127.3);
            var r = new AutogenPlacement.Result();
            AutogenPlacement.Build(tile, row, col, ForestSettings(10), r);
            double south = AutogenPlacement.SouthOf(row), west = AutogenPlacement.WestOf(col);

            for (int k = 0; k < r.Count; k += 7)
            {
                Vector4 p = r.Data[2 * k];
                Double3 ecef = r.Centre + r.East * p.x + r.Up * p.y + r.North * p.z;
                GeoMath.EcefToGeodetic(ecef, out double lat, out double lon, out double h);
                Assert.That(lat, Is.InRange(south, south + AutogenPlacement.PatchSize));
                Assert.That(lon, Is.InRange(west, west + AutogenPlacement.PatchSize));
                Assert.AreEqual(tile.SampleHeight(lat, lon) - 0.5, h, 0.02, "height on the surface, minus sink");
            }
        }
    }
}
