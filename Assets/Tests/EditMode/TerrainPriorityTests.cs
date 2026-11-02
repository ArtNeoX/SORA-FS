using System;
using System.IO;
using NUnit.Framework;

namespace CirrusFS.Geo.Tests
{
    /// Two exporter folders side by side under one parent, as a world base plus a detailed
    /// region: "A_Coarse" (4 samples/deg) has +37+127 and +37+128, "B_Fine" (8 samples/deg)
    /// only +37+127, on a surface 50 m higher. The fine tile must win its cell, and its edge
    /// with the coarse +37+128 must match bit for bit.
    public class TerrainPriorityTests
    {
        const float HMin = -500f, HMax = 9000f;

        string parent;
        TerrainDataSet set;

        static double Truth(double lat, double lon) => 300.0 + 120.0 * (lat - 37.0) - 80.0 * (lon - 127.0);

        [SetUp]
        public void WriteFolders()
        {
            parent = Path.Combine(Path.GetTempPath(), "CirrusTerrainPriority_" + Guid.NewGuid().ToString("N"));
            WriteFolder(Path.Combine(parent, "A_Coarse"), 4, 2, 0.0, (37, 127), (37, 128));
            WriteFolder(Path.Combine(parent, "B_Fine"), 8, 4, 50.0, (37, 127));
            set = TerrainDataSet.Load(new[] { parent });
        }

        [TearDown]
        public void Cleanup()
        {
            set?.Dispose();
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }

        static string Name(int lat, int lon) => $"{(lat < 0 ? "-" : "+")}{Math.Abs(lat):00}{(lon < 0 ? "-" : "+")}{Math.Abs(lon):000}";

        static void WriteFolder(string root, int n, int m, double offset, params (int lat, int lon)[] tiles)
        {
            var entries = new string[tiles.Length];
            for (int t = 0; t < tiles.Length; t++)
            {
                (int lat, int lon) = tiles[t];
                string rel = $"tiles/+30+120/{Name(lat, lon)}";
                string dir = Path.Combine(root, rel);
                Directory.CreateDirectory(dir);
                var h = new byte[(n + 1) * (n + 1) * 2];
                for (int i = 0, k = 0; i <= n; i++)
                {
                    for (int j = 0; j <= n; j++, k += 2)
                    {
                        double metres = Truth((lat * (double)n + i) / n, (lon * (double)n + j) / n) + offset;
                        ushort v = (ushort)Math.Round((metres - HMin) / (HMax - HMin) * 65535.0);
                        h[k] = (byte)(v & 0xFF);
                        h[k + 1] = (byte)(v >> 8);
                    }
                }
                File.WriteAllBytes(Path.Combine(dir, "height.r16"), h);
                File.WriteAllBytes(Path.Combine(dir, "class.u8"), new byte[m * m]);
                entries[t] = $"{{\"id\":\"{Name(lat, lon)}\",\"lat\":{lat},\"lon\":{lon},\"path\":\"{rel}\"," +
                             "\"hMin\":0,\"hMax\":1000,\"landFraction\":1}";
            }
            File.WriteAllText(Path.Combine(root, "manifest.json"),
                "{\"format\":\"CirrusTerrain\",\"version\":2," +
                $"\"height\":{{\"file\":\"height.r16\",\"samplesPerDegree\":{n},\"samples\":{n + 1},\"min\":{HMin},\"max\":{HMax}}}," +
                $"\"landClass\":{{\"file\":\"class.u8\",\"cellsPerDegree\":{m}}}," +
                "\"seaHeight\":-5,\"waterClass\":0," +
                "\"classes\":[{\"id\":0,\"name\":\"Water\",\"color\":\"#2b5f8f\"}]," +
                "\"tiles\":[" + string.Join(",", entries) + "]}");
        }

        [Test]
        public void MoreDetailedFolderWins_WhateverItsName()
        {
            Assert.AreEqual(2, set.Folders.Count);
            StringAssert.EndsWith("B_Fine", set.Folders[0], "the finer folder ranks first");
            Assert.AreEqual(8, set.TileAt(37, 127).N, "fine tile drawn where both folders have one");
            Assert.AreEqual(4, set.TileAt(37, 128).N, "coarse tile where only the world has one");
            Assert.AreEqual(2, set.Tiles.Count, "one drawn tile per cell");
            Assert.AreEqual(3, set.AllTiles.Count, "the covered coarse tile is kept as the blend target");
        }

        [Test]
        public void SharedEdgeWithCoarserFolder_MatchesExactly()
        {
            TerrainTileSource fine = set.TileAt(37, 127), coarse = set.TileAt(37, 128);
            Assert.IsTrue(fine.BlendsAtEdges);
            Assert.IsFalse(coarse.BlendsAtEdges, "the coarser side keeps its own surface");
            set.Touch(fine);
            set.Touch(coarse);
            for (int k = 0; k <= 16; k++)
            {
                double lat = 37.0 + k / 16.0;
                Assert.AreEqual(coarse.SampleHeight(lat, 128.0), fine.SampleHeight(lat, 128.0), 0f, $"edge at lat {lat}");
            }
        }

        [Test]
        public void AwayFromThatEdge_FineSurfaceIsUntouched()
        {
            TerrainTileSource fine = set.TileAt(37, 127);
            set.Touch(fine);
            // 0.6° from the east edge, beyond the 0.25° margin: the fine data, 50 m above the coarse
            Assert.AreEqual(fine.SampleOwnHeight(37.5, 127.4), fine.SampleHeight(37.5, 127.4), 0f);
            Assert.AreEqual(Truth(37.5, 127.4) + 50.0, fine.SampleHeight(37.5, 127.4), 0.3);
            // inside the margin it lies between the two
            float mid = fine.SampleHeight(37.5, 127.9);
            Assert.Less(mid, fine.SampleOwnHeight(37.5, 127.9));
            Assert.Greater(mid, set.AllTiles.Find(t => t.N == 4 && t.Lon == 127).SampleOwnHeight(37.5, 127.9));
        }
    }
}
