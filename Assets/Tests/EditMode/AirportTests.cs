using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CirrusFS.Geo.Tests
{
    public class AirportTests
    {
        // ---- CSV ---------------------------------------------------------------------------

        [Test]
        public void CsvReaderHandlesQuotesCommasAndLineBreaks()
        {
            string text = "a,b,c\r\n1,\"two, three\",\"say \"\"hi\"\"\"\n\n4,\"multi\nline\",\r\n";
            var csv = new CsvReader(new StringReader(text));
            Assert.AreEqual(1, csv.Column("b"));
            Assert.IsTrue(csv.ReadRow());
            Assert.AreEqual("1", csv[0]);
            Assert.AreEqual("two, three", csv[1]);
            Assert.AreEqual("say \"hi\"", csv[2]);
            Assert.IsTrue(csv.ReadRow());
            Assert.AreEqual("4", csv[0]);
            Assert.AreEqual("multi\nline", csv[1]);
            Assert.AreEqual("", csv[2]);
            Assert.IsFalse(csv.ReadRow());
        }

        // ---- layout ------------------------------------------------------------------------

        static Airport MakeAirport(double lat, double lon, params (double heading, double length, float width, double eastOffset)[] runways)
        {
            var a = new Airport { Id = "1", Ident = "TEST", Icao = "TEST", Name = "Test Field", Latitude = lat, Longitude = lon, Elevation = 12f };
            var origin = new AirportLayoutProbe(lat, lon);
            int n = 0;
            foreach (var r in runways)
            {
                double h = r.heading * Math.PI / 180.0;
                var c = new Double2(r.eastOffset, 0);
                var d = new Double2(Math.Sin(h), Math.Cos(h)) * (0.5 * r.length);
                origin.ToLatLon(c - d, out double laLat, out double laLon);
                origin.ToLatLon(c + d, out double heLat, out double heLon);
                a.Runways.Add(new Runway
                {
                    Id = (++n).ToString(), Airport = a, LeIdent = "09", HeIdent = "27",
                    LeLatitude = laLat, LeLongitude = laLon, HeLatitude = heLat, HeLongitude = heLon,
                    LeElevation = 12f, HeElevation = 14f, Length = (float)r.length, Width = r.width,
                    LeHeading = (float)r.heading, HeHeading = (float)((r.heading + 180) % 360),
                    Surface = RunwaySurface.Asphalt,
                });
            }
            return a;
        }

        /// Tangent-plane conversions at a point, the same formulas AirportLayout uses.
        sealed class AirportLayoutProbe
        {
            readonly double lat0, lon0, mLat, mLon;
            public AirportLayoutProbe(double lat, double lon)
            {
                lat0 = lat; lon0 = lon;
                double phi = lat * GeoMath.Deg2Rad, s = Math.Sin(phi), w = Math.Sqrt(1 - GeoMath.E2 * s * s);
                mLat = GeoMath.A * (1 - GeoMath.E2) / (w * w * w) * GeoMath.Deg2Rad;
                mLon = GeoMath.A / w * Math.Cos(phi) * GeoMath.Deg2Rad;
            }
            public void ToLatLon(Double2 p, out double lat, out double lon) { lat = lat0 + p.y / mLat; lon = lon0 + p.x / mLon; }
        }

        [Test]
        public void SingleRunwayFrameHasTheMargins()
        {
            Airport a = MakeAirport(37.0, 127.0, (90.0, 2000.0, 45f, 0.0));
            AirportLayout l = AirportLayout.Build(a, AirportFrameSettings.Default);
            Assert.AreEqual(1, l.Frames.Count);
            FramePolygon f = l.Frames[0];
            Assert.AreEqual(4, f.Vertices.Length);

            // 2000 m long east-west: frame runs 1000 + 150 m each way, 22.5 + 45 m each side
            Assert.AreEqual(-67.5, f.SignedDistance(new Double2(0, 0)), 0.5);
            Assert.AreEqual(0.0, f.SignedDistance(new Double2(1150, 0)), 0.5);
            Assert.AreEqual(0.0, f.SignedDistance(new Double2(0, 67.5)), 0.5);
            Assert.Greater(f.SignedDistance(new Double2(0, 100)), 30.0);
            Assert.AreEqual(12f, l.ReferenceHeight);
            Assert.IsTrue(l.HasElevation);

            // lat/lon bounds cover the frame plus the margin
            Assert.IsTrue(l.BoundsContain(37.0, 127.0 + 1150.0 / 89000.0));
            Assert.IsFalse(l.BoundsContain(37.01, 127.0));
            Assert.Less(l.FrameDistance(37.0, 127.0), 0.0);
        }

        [Test]
        public void ParallelRunwaysClearTheInfield()
        {
            // two parallel runways 600 m apart: the point between them is in the frame
            Airport a = MakeAirport(37.0, 127.0, (0.0, 3000.0, 45f, -300.0), (0.0, 3000.0, 45f, 300.0));
            AirportLayout l = AirportLayout.Build(a, AirportFrameSettings.Default);
            Assert.AreEqual(1, l.Frames.Count, "one hull for the whole airport");
            Assert.Less(l.Frames[0].SignedDistance(new Double2(0, 0)), -100.0);
        }

        [Test]
        public void FarApartRunwaysKeepTheirOwnFrames()
        {
            Airport a = MakeAirport(37.0, 127.0, (0.0, 1500.0, 30f, -6000.0), (0.0, 1500.0, 30f, 6000.0));
            AirportLayout l = AirportLayout.Build(a, AirportFrameSettings.Default);
            Assert.AreEqual(2, l.Frames.Count);
            Assert.Greater(l.FrameDistance(37.0, 127.0), 1000.0);
        }

        [Test]
        public void SimplifiedHullStillContainsEveryPoint()
        {
            var rng = new System.Random(7);
            for (int trial = 0; trial < 50; trial++)
            {
                var pts = new List<Double2>();
                for (int i = 0; i < 40; i++) pts.Add(new Double2(rng.NextDouble() * 4000 - 2000, rng.NextDouble() * 3000 - 1500));
                List<Double2> hull = AirportLayout.ConvexHull(pts);
                List<Double2> simple = AirportLayout.Simplify(hull, AirportLayout.MaxEdges);
                Assert.LessOrEqual(simple.Count, AirportLayout.MaxEdges);
                var poly = new FramePolygon(simple);
                Assert.Greater(poly.Area, 0.0, "counter-clockwise");
                foreach (Double2 p in pts) Assert.LessOrEqual(poly.SignedDistance(p), 1e-6);
            }
        }

        // ---- database ----------------------------------------------------------------------

        static string WriteTestData()
        {
            string dir = Path.Combine(Path.GetTempPath(), "CirrusFS_AirportTests");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "airports.csv"),
                "id,ident,type,name,lat,lon,elevation_m,country,country_name,region,municipality,scheduled,icao,iata,gps,local,keywords,runways,approx_runways\n" +
                "1,RKSI,large_airport,Incheon International Airport,37.4691,126.451,7,KR,South Korea,KR-28,Incheon,1,RKSI,ICN,RKSI,,,1,0\n" +
                "2,LSZH,large_airport,Zürich Airport,47.458,8.548,432,CH,Switzerland,CH-ZH,Zürich,1,LSZH,ZRH,LSZH,,,1,0\n" +
                "3,KR-0001,small_airport,\"Incheon Glider Field, North\",37.6,126.6,20,KR,South Korea,KR-28,Incheon,0,,,,,,1,1\n" +
                "4,XNOR,small_airport,No Runway Strip,10,10,5,XX,Nowhere,XX-1,,0,,,,,,0,0\n");
            File.WriteAllText(Path.Combine(dir, "runways.csv"),
                "id,airport_id,airport_ident,le_ident,he_ident,le_lat,le_lon,he_lat,he_lon,le_elevation_m,he_elevation_m,le_heading,he_heading,length_m,declared_length_m,width_m,width_source,le_displaced_m,he_displaced_m,surface,surface_raw,lighted,position\n" +
                "10,1,RKSI,15L,33R,37.4848,126.4347,37.4572,126.4580,7,7,144.1,324.1,3768,3750,60,data,,,asphalt,ASP,1,surveyed\n" +
                "20,2,LSZH,14,32,47.4822,8.5345,47.4646,8.5560,426,422,137.3,317.3,3300,3300,60,data,,,concrete,CON,1,surveyed\n" +
                "30,3,KR-0001,09,27,37.6,126.594,37.6,126.606,20,20,90,270,1060,1060,25,default,,,grass,TURF,0,approx\n");
            return dir;
        }

        [Test]
        public void LoadsAndSearchesByCodeAndName()
        {
            AirportDatabase db = AirportDatabase.Load(WriteTestData(), AirportFrameSettings.Default);
            Assert.AreEqual(4, db.Airports.Count);
            Assert.AreEqual(3, db.Runways.Count);
            Airport rksi = db.FindById("1");
            Assert.AreEqual(1, rksi.Runways.Count);
            Assert.AreEqual(RunwaySurface.Asphalt, rksi.Runways[0].Surface);
            Assert.IsTrue(db.Runways[2].Approx);

            List<AirportDatabase.SearchResult> r = db.Search("rksi", 10);
            Assert.AreEqual("RKSI", r[0].Airport.Ident);
            r = db.Search("ICN", 10);
            Assert.AreEqual("RKSI", r[0].Airport.Ident);

            // name words: the big airport ranks first, both Incheon fields are found
            r = db.Search("incheon", 10);
            Assert.AreEqual(2, r.Count);
            Assert.AreEqual("RKSI", r[0].Airport.Ident);

            // accents fold, several words must all match, airports without runways are left out
            Assert.AreEqual("LSZH", db.Search("zurich", 10)[0].Airport.Ident);
            Assert.AreEqual("KR-0001", db.Search("glider incheon", 10)[0].Airport.Ident);
            Assert.AreEqual(0, db.Search("no runway", 10).Count);

            // the tile index and the frame test
            Assert.IsTrue(db.AirportsInTile(37, 126).Contains(rksi));
            Assert.AreSame(rksi, db.FrameAt(37.4710, 126.4463));
            Assert.IsNull(db.FrameAt(37.30, 126.30));
            Assert.IsTrue(db.Touches(37.47, 126.44, 37.48, 126.45));
            Assert.IsFalse(db.Touches(37.10, 126.10, 37.11, 126.11));
        }

        [Test]
        public void ChunkPackerPutsTheRunwayInChunkCoordinates()
        {
            AirportDatabase db = AirportDatabase.Load(WriteTestData(), AirportFrameSettings.Default);
            Airport rksi = db.FindById("1");
            rksi.FrameClass = 4;   // skip the terrain lookup (no terrain in this test)

            // a 1/8 degree chunk around Incheon, centred at 7 m
            double lat0 = 37.375, lon0 = 126.375, size = 0.125;
            double latC = lat0 + size / 2, lonC = lon0 + size / 2;
            Double3 centre = GeoMath.GeodeticToEcef(latC, lonC, 7.0);
            GeoMath.EnuBasis(latC, lonC, out Double3 east, out Double3 north, out _);

            var packer = new AirportChunkPacker();
            Assert.IsTrue(packer.Pack(db, null, default, 37, 126, lat0, lon0, size, centre, east, north,
                                      200.0, 6.0, 0.5));
            Assert.AreEqual(1, packer.RunwayCount);
            Assert.Greater(packer.FrameVectorCount, 4);

            Runway r = rksi.Runways[0];
            Double3 mid = 0.5 * (GeoMath.GeodeticToEcef(r.LeLatitude, r.LeLongitude, 7.0) +
                                 GeoMath.GeodeticToEcef(r.HeLatitude, r.HeLongitude, 7.0)) - centre;
            Vector4 a = packer.RunwayData[0], b = packer.RunwayData[1];
            Assert.AreEqual(Double3.Dot(mid, east), a.x, 0.5);
            Assert.AreEqual(Double3.Dot(mid, north), a.y, 0.5);
            // pointing south-east, along the bearing between the (rounded) test coordinates
            double mLat = 110990.0, mLon = 88800.0;   // metres per degree near 37.47 N
            double expected = Math.Atan2((r.HeLongitude - r.LeLongitude) * mLon, (r.HeLatitude - r.LeLatitude) * mLat) * 180.0 / Math.PI;
            float heading = Mathf.Atan2(a.z, a.w) * Mathf.Rad2Deg;
            Assert.AreEqual((expected + 360.0) % 360.0, (heading + 360f) % 360f, 0.3);
            Assert.AreEqual(30f, b.y, 0.01f);              // half width
            Assert.AreEqual((float)RunwaySurface.Asphalt, b.z);
            Assert.AreEqual(15f, packer.RunwayData[2].z);  // le number

            // frame header: edges + 16 x class
            Vector4 header = packer.FrameData[0];
            Assert.AreEqual(4, Mathf.RoundToInt(header.w) / 16);

            // a far-away coarse chunk drops a 60 m runway (sub-pixel there)
            Assert.IsFalse(packer.Pack(db, null, default, 37, 126, 37.0, 126.0, 1.0, centre, east, north,
                                       1700.0, 6.0, 0.5) && packer.RunwayCount > 0);
        }

        [Test]
        public void HeightPatchKeepsCoastalRunwaysAboveTheSea()
        {
            Airport a = MakeAirport(37.0, 127.0, (90.0, 2000.0, 45f, 0.0));   // field at 12 m
            a.Layout = AirportLayout.Build(a, AirportFrameSettings.Default);
            var patch = new AirportHeightPatch(new List<Airport> { a }, 400);  // global tiles: 278 m cells
            Assert.AreEqual(3f, patch.Apply(37.0, 127.0, -5f));           // sea floor under the runway
            Assert.AreEqual(3f, patch.Apply(37.0, 127.0, -1.5f));         // between land and sea vertices (TNCM)
            Assert.AreEqual(5f, patch.Apply(37.0, 127.0, 5f));            // ground above 3 m: never touched
            Assert.AreEqual(-5f, patch.Apply(37.05, 127.0, -5f));         // 5.5 km away: unchanged

            // a vertex one cell (278 m) beside the runway edge carries triangles under it: lifted.
            // Two cells out it carries none: unchanged, so no more than a cell of new coast.
            Assert.AreEqual(3f, patch.Apply(37.0 + 290.0 / 110990.0, 127.0, -5f));
            Assert.AreEqual(-5f, patch.Apply(37.0 + 330.0 / 110990.0, 127.0, -5f));
            // east-west cells are 227 m at 37 N
            Assert.AreEqual(3f, patch.Apply(37.0, 127.0 + (1000.0 + 200.0) / 89000.0, -5f));   // past the end, within a cell
            Assert.AreEqual(-5f, patch.Apply(37.0, 127.0 + (1000.0 + 260.0) / 89000.0, -5f));

            // an airport that really is below sea level (Schiphol, the Caspian) keeps its ground
            Airport low = MakeAirport(37.0, 127.0, (90.0, 2000.0, 45f, 0.0));
            foreach (Runway r in low.Runways) { r.LeElevation = -22f; r.HeElevation = -22f; }
            low.Layout = AirportLayout.Build(low, AirportFrameSettings.Default);
            var lowPatch = new AirportHeightPatch(new List<Airport> { low }, 400);
            Assert.AreEqual(-24f, lowPatch.Apply(37.0, 127.0, -24f));
            Assert.AreEqual(-1f, lowPatch.Apply(37.0, 127.0, -1f));
        }

        [Test]
        public void RunwayNumbers()
        {
            Assert.AreEqual(9f, AirportChunkPacker.Number("09L"));
            Assert.AreEqual(33f, AirportChunkPacker.Number("33R"));
            Assert.AreEqual(0f, AirportChunkPacker.Number("N"));
            Assert.AreEqual(0f, AirportChunkPacker.Number("H1"));
        }
    }
}
