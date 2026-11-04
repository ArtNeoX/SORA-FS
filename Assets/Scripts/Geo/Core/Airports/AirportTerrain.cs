using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// Class ids the airport frames can show, resolved from the terrain's class names.
    public struct AirportFrameClasses
    {
        public int grass, rock, sand, snow;

        public static AirportFrameClasses Resolve(TerrainDataSet data, string grassName, string rockName,
                                                  string sandName, string snowName)
        {
            return new AirportFrameClasses
            {
                grass = Find(data, grassName),
                rock = Find(data, rockName),
                sand = Find(data, sandName),
                snow = Find(data, snowName),
            };
        }

        static int Find(TerrainDataSet data, string name)
        {
            if (data == null || string.IsNullOrWhiteSpace(name)) return -1;
            foreach (TerrainClassDef c in data.Classes)
                if (string.Equals(c.name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) return c.id;
            return -1;
        }
    }

    /// Keeps coastal runways out of the sea. The exporter pushes vertices outside the coastline
    /// down to the sea floor (-5 m); with a few hundred metres between height samples, a runway
    /// along the shore (Princess Juliana, Incheon, Haneda...) then lies on the slope between a
    /// land vertex and a sea vertex, below the h = 0 ocean.
    ///
    /// Around each runway the ground is held at least `Floor` above the ellipsoid: the
    /// airport's elevation, clamped to 1..3 m. That is enough to clear the ocean and changes
    /// nothing on ground above 3 m, so inland terrain is untouched.
    ///
    /// Which points are lifted: exactly those whose one-cell neighbourhood (one height-sample
    /// spacing either way in latitude and longitude) overlaps a runway. Those are the mesh
    /// vertices of every triangle that lies under a runway (the mesh is never coarser than the
    /// data near the aircraft), so the whole runway clears the sea and nothing further out
    /// is raised: the land added to the coast is at most one cell wide.
    ///
    /// Airports whose published elevation is below 0.5 m (Schiphol at -3 m, the Caspian
    /// shore at -22 m) are real land below sea level and are left alone.
    public sealed class AirportHeightPatch : ITerrainHeightPatch
    {
        public const float MaxFloor = 3f;

        readonly List<Airport> airports = new List<Airport>();
        readonly List<double> boxes = new List<double>();   // per airport: lat min, lat max, lon min, lon max
        readonly double cellDegrees;

        /// `samplesPerDegree`: the tile's height samples per degree (its cell size).
        /// `tileLat`/`tileLon`, when given, drop airports whose lifted zone cannot reach that tile.
        public AirportHeightPatch(List<Airport> candidates, int samplesPerDegree, int tileLat = int.MinValue, int tileLon = 0)
        {
            cellDegrees = 1.0 / Math.Max(samplesPerDegree, 1);
            foreach (Airport a in candidates)
            {
                AirportLayout l = a.Layout;
                double r = l.RunwayExtent + 2.0 * cellDegrees * 111320.0;
                l.ToLatLon(new Double2(-r, -r), out double lat0, out double lon0);
                l.ToLatLon(new Double2(r, r), out double lat1, out double lon1);
                if (tileLat != int.MinValue && !(lat1 >= tileLat && lat0 <= tileLat + 1 &&
                                                 AirportLayout.LongitudeOverlaps(lon0, lon1, tileLon, tileLon + 1)))
                    continue;
                airports.Add(a);
                boxes.Add(lat0); boxes.Add(lat1); boxes.Add(lon0); boxes.Add(lon1);
            }
        }

        public int AirportCount => airports.Count;

        /// The ground an airport keeps above, or NaN when it is not lifted at all.
        public static float Floor(AirportLayout l)
        {
            float target = l.HasElevation ? l.ReferenceHeight : 2f;
            return target < 0.5f ? float.NaN : Math.Min(Math.Max(target, 1f), MaxFloor);
        }

        public float Apply(double lat, double lon, float h)
        {
            if (h >= MaxFloor) return h;
            float result = h;
            for (int k = 0; k < airports.Count; k++)
            {
                if (lat < boxes[4 * k] || lat > boxes[4 * k + 1] ||
                    !AirportLayout.LongitudeOverlaps(boxes[4 * k + 2], boxes[4 * k + 3], lon, lon)) continue;
                AirportLayout l = airports[k].Layout;
                float floor = Floor(l);
                if (!(floor > result)) continue;          // NaN or already high enough
                if (l.RunwayOverlapsCell(lat, lon, cellDegrees * 1.02)) result = floor;
            }
            return result;
        }

        /// Puts a patch on every tile (drawn or blend target) that an airport reaches, counting
        /// the airports of the eight neighbouring tiles too (the lifted zone can reach a little
        /// past a frame). Returns how many tiles got one.
        public static int Attach(TerrainDataSet data, AirportDatabase db)
        {
            int count = 0;
            var seen = new HashSet<Airport>();
            foreach (TerrainTileSource tile in data.AllTiles)
            {
                tile.HeightPatch = null;
                if (db == null) continue;
                List<Airport> list = null;
                seen.Clear();
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int lat = tile.Lat + dy;
                        if (lat < -90 || lat > 89) continue;
                        int lon = ((tile.Lon + dx + 180) % 360 + 360) % 360 - 180;
                        foreach (Airport a in db.AirportsInTile(lat, lon))
                        {
                            if (float.IsNaN(Floor(a.Layout)) || !seen.Add(a)) continue;
                            (list ??= new List<Airport>()).Add(a);
                        }
                    }
                }
                if (list == null) continue;
                var patch = new AirportHeightPatch(list, tile.N, tile.Lat, tile.Lon);
                if (patch.AirportCount == 0) continue;
                tile.HeightPatch = patch;
                count++;
            }
            return count;
        }
    }

    /// Packs the runways and frames over one terrain chunk into the arrays the terrain shader
    /// reads, in the chunk's local frame (x east, z north, metres from its centre).
    ///
    /// Runways: three float4 each.
    ///   [0] centre x, centre z, unit direction x, z (from the le end toward the he end)
    ///   [1] half length, half width, surface (RunwaySurface), flags (1 approx, 2 lighted)
    ///   [2] le displaced threshold, he displaced threshold, le number, he number (for markings)
    /// Frames: per polygon one header float4 (centre x, z, radius, edge count + 16 x class id)
    /// and then one float4 per edge (outward normal x, z, offset, 0): inside where every
    /// dot(n, p) - offset is negative.
    public sealed class AirportChunkPacker
    {
        public const int MaxRunways = 16;
        public const int RunwayVectors = 3 * MaxRunways;
        public const int FrameVectors = 96;

        public readonly Vector4[] RunwayData = new Vector4[RunwayVectors];
        public readonly Vector4[] FrameData = new Vector4[FrameVectors];
        public int RunwayCount, FrameVectorCount, AirportCount;

        readonly List<Airport> scratch = new List<Airport>();
        readonly Vector2[] vertexScratch = new Vector2[AirportLayout.MaxEdges + 4];

        /// Fills the arrays for chunk [lat0, lat0 + size] x [lon0, lon0 + size] of `tile`.
        /// Features narrower than `minPixels` (estimated as width x pixelsPerMetreAtSpacing /
        /// chunk spacing: the size on screen when the chunk is at its split distance) are
        /// left off coarse chunks. Returns true when anything was packed.
        public bool Pack(AirportDatabase db, TerrainDataSet data, AirportFrameClasses classes,
                         int tileLat, int tileLon, double lat0, double lon0, double size,
                         Double3 centre, Double3 east, Double3 north, double spacing,
                         double pixelsPerMetreAtSpacing, double minPixels)
        {
            RunwayCount = FrameVectorCount = AirportCount = 0;
            if (db == null) return false;

            scratch.Clear();
            foreach (Airport a in db.AirportsInTile(tileLat, tileLon))
                if (a.Layout.BoundsOverlap(lat0, lon0, lat0 + size, lon0 + size)) scratch.Add(a);
            if (scratch.Count == 0) return false;
            scratch.Sort((x, y) => y.Layout.LongestRunway.CompareTo(x.Layout.LongestRunway));

            double scale = pixelsPerMetreAtSpacing / Math.Max(spacing, 1e-3);
            foreach (Airport a in scratch)
            {
                AirportLayout l = a.Layout;
                if (l.NarrowestFrame * scale < minPixels) continue;
                if (a.FrameClass < 0) a.FrameClass = ChooseFrameClass(data, a, classes);
                double h = l.ReferenceHeight;
                bool packed = false;

                // frames
                if (a.FrameClass >= 0)
                {
                    foreach (FramePolygon f in l.Frames)
                    {
                        int n = f.Vertices.Length;
                        if (n > vertexScratch.Length) continue;
                        if (FrameVectorCount + 1 + n > FrameVectors) break;
                        Vector2[] v = vertexScratch;
                        Vector2 c = Vector2.zero;
                        for (int k = 0; k < n; k++)
                        {
                            l.ToLatLon(f.Vertices[k], out double vLat, out double vLon);
                            v[k] = ToChunk(vLat, vLon, h, centre, east, north);
                            c += v[k];
                        }
                        c /= n;
                        float radius = 0f;
                        for (int k = 0; k < n; k++) radius = Mathf.Max(radius, (v[k] - c).magnitude);
                        FrameData[FrameVectorCount++] = new Vector4(c.x, c.y, radius, n + 16 * a.FrameClass);
                        for (int k = 0; k < n; k++)
                        {
                            Vector2 e = (v[(k + 1) % n] - v[k]).normalized;
                            var normal = new Vector2(e.y, -e.x);
                            FrameData[FrameVectorCount++] = new Vector4(normal.x, normal.y, Vector2.Dot(normal, v[k]), 0f);
                        }
                        packed = true;
                    }
                }

                // runways
                foreach (Runway r in a.Runways)
                {
                    if (RunwayCount >= MaxRunways) break;
                    if (r.Surface == RunwaySurface.Water || r.Width * scale < minPixels) continue;
                    Vector2 le = ToChunk(r.LeLatitude, r.LeLongitude, h, centre, east, north);
                    Vector2 he = ToChunk(r.HeLatitude, r.HeLongitude, h, centre, east, north);
                    Vector2 axis = he - le;
                    float length = axis.magnitude;
                    if (length < 1f) continue;
                    Vector2 dir = axis / length, mid = 0.5f * (le + he);
                    int k = 3 * RunwayCount++;
                    RunwayData[k] = new Vector4(mid.x, mid.y, dir.x, dir.y);
                    RunwayData[k + 1] = new Vector4(0.5f * length, 0.5f * r.Width, (float)r.Surface,
                                                    (r.Approx ? 1f : 0f) + (r.Lighted ? 2f : 0f));
                    RunwayData[k + 2] = new Vector4(r.LeDisplaced, r.HeDisplaced, Number(r.LeIdent), Number(r.HeIdent));
                    packed = true;
                }
                if (packed) AirportCount++;
            }
            return RunwayCount > 0 || FrameVectorCount > 0;
        }

        static Vector2 ToChunk(double lat, double lon, double h, Double3 centre, Double3 east, Double3 north)
        {
            Double3 rel = GeoMath.GeodeticToEcef(lat, GeoMath.WrapLongitude(lon), h) - centre;
            return new Vector2((float)Double3.Dot(rel, east), (float)Double3.Dot(rel, north));
        }

        /// The number of a runway designator ("09L" -> 9), 0 when it has none.
        public static float Number(string ident)
        {
            if (string.IsNullOrEmpty(ident)) return 0f;
            int n = 0, digits = 0;
            foreach (char ch in ident)
            {
                if (ch < '0' || ch > '9') break;
                n = n * 10 + (ch - '0');
                digits++;
            }
            return digits > 0 && n <= 36 ? n : 0f;
        }

        /// The ground an airport's frame shows, from the land around it: rings at 1.5, 2.5 and
        /// 3.5 km. Mostly snow -> snow; mostly rock or sand -> whichever of them dominates;
        /// anything else -> grass. Water samples don't count.
        public static int ChooseFrameClass(TerrainDataSet data, Airport a, AirportFrameClasses ids)
        {
            if (data == null) return ids.grass;
            int land = 0, rock = 0, sand = 0, snow = 0;
            const int perRing = 24;
            foreach (double radius in new[] { 1500.0, 2500.0, 3500.0 })
            {
                for (int k = 0; k < perRing; k++)
                {
                    double ang = (k + 0.5) * (2.0 * Math.PI / perRing);
                    a.Layout.ToLatLon(new Double2(Math.Sin(ang) * radius, Math.Cos(ang) * radius), out double lat, out double lon);
                    if (lat <= -90.0 || lat >= 90.0) continue;
                    int id = data.ClassAt(lat, GeoMath.WrapLongitude(lon), null);
                    if (id == data.WaterClass) continue;
                    land++;
                    if (id == ids.rock) rock++;
                    else if (id == ids.sand) sand++;
                    else if (id == ids.snow) snow++;
                }
            }
            int fallback = ids.grass >= 0 ? ids.grass : -1;
            if (land == 0) return fallback;
            if (snow >= 0.5 * land && ids.snow >= 0) return ids.snow;
            if (rock + sand >= 0.4 * land)
            {
                if (rock >= sand && ids.rock >= 0) return ids.rock;
                if (ids.sand >= 0) return ids.sand;
                if (ids.rock >= 0) return ids.rock;
            }
            return fallback;
        }
    }
}
