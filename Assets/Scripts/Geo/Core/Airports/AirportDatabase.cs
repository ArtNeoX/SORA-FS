using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CirrusFS.Geo
{
    /// The airports written by SceneryDataGen/airport_exporter (AirportData/airports.csv and
    /// runways.csv; frequencies and navaids are read on first use).
    ///
    /// Load is pure C# with no Unity calls, so it can run on a worker thread. It parses
    /// everything, builds every airport's layout (runway rectangles and frame), and indexes the
    /// airports by the 1° terrain tiles their frames touch.
    public sealed class AirportDatabase : IAutogenExclusion
    {
        public const string Format = "cirrusfs-airports";

        public readonly List<Airport> Airports = new List<Airport>();
        public readonly List<Runway> Runways = new List<Runway>();
        public string Folder { get; private set; }
        public AirportFrameSettings FrameSettings { get; private set; }

        /// How long Load took, milliseconds.
        public double LoadMilliseconds { get; private set; }

        /// Autogen stays this far outside the frames, metres (tree crowns, the wavy edge).
        public double ExclusionMargin = 15.0;

        readonly Dictionary<int, List<Airport>> byTile = new Dictionary<int, List<Airport>>();
        readonly Dictionary<string, Airport> byId = new Dictionary<string, Airport>(StringComparer.Ordinal);
        static readonly List<Airport> None = new List<Airport>();

        List<AirportFrequency> frequencies;
        List<Navaid> navaids;
        readonly object lazyLock = new object();

        // ---- loading ---------------------------------------------------------------------

        /// Reads `folder` (holding airports.csv and runways.csv). Throws when they are missing.
        public static AirportDatabase Load(string folder, AirportFrameSettings frameSettings)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var db = new AirportDatabase { Folder = folder, FrameSettings = frameSettings };

            using (var reader = new StreamReader(Path.Combine(folder, "airports.csv"), Encoding.UTF8))
            {
                var csv = new CsvReader(reader);
                int id = csv.Column("id"), ident = csv.Column("ident"), type = csv.Column("type"),
                    name = csv.Column("name"), lat = csv.Column("lat"), lon = csv.Column("lon"),
                    elev = csv.Column("elevation_m"), country = csv.Column("country"),
                    countryName = csv.Column("country_name"), region = csv.Column("region"),
                    city = csv.Column("municipality"), scheduled = csv.Column("scheduled"),
                    icao = csv.Column("icao"), iata = csv.Column("iata"), gps = csv.Column("gps"),
                    local = csv.Column("local"), keywords = csv.Column("keywords");
                while (csv.ReadRow())
                {
                    double la = CsvReader.Double(csv[lat]), lo = CsvReader.Double(csv[lon]);
                    if (double.IsNaN(la) || double.IsNaN(lo)) continue;
                    var a = new Airport
                    {
                        Index = db.Airports.Count,
                        Id = csv[id],
                        Ident = csv[ident],
                        Type = ParseType(csv[type]),
                        Name = csv[name],
                        Latitude = la,
                        Longitude = lo,
                        Elevation = CsvReader.Float(csv[elev]),
                        Country = csv[country],
                        CountryName = csv[countryName],
                        Region = csv[region],
                        Municipality = csv[city],
                        Scheduled = csv[scheduled] == "1",
                        Icao = csv[icao],
                        Iata = csv[iata],
                        Gps = csv[gps],
                        Local = csv[local],
                        Keywords = csv[keywords],
                    };
                    db.Airports.Add(a);
                    db.byId[a.Id] = a;
                }
            }

            using (var reader = new StreamReader(Path.Combine(folder, "runways.csv"), Encoding.UTF8))
            {
                var csv = new CsvReader(reader);
                int id = csv.Column("id"), airport = csv.Column("airport_id"),
                    leId = csv.Column("le_ident"), heId = csv.Column("he_ident"),
                    leLat = csv.Column("le_lat"), leLon = csv.Column("le_lon"),
                    heLat = csv.Column("he_lat"), heLon = csv.Column("he_lon"),
                    leElev = csv.Column("le_elevation_m"), heElev = csv.Column("he_elevation_m"),
                    leHdg = csv.Column("le_heading"), heHdg = csv.Column("he_heading"),
                    length = csv.Column("length_m"), width = csv.Column("width_m"),
                    leDisp = csv.Column("le_displaced_m"), heDisp = csv.Column("he_displaced_m"),
                    surface = csv.Column("surface"), lighted = csv.Column("lighted"),
                    position = csv.Column("position");
                while (csv.ReadRow())
                {
                    if (!db.byId.TryGetValue(csv[airport], out Airport a)) continue;
                    var r = new Runway
                    {
                        Id = csv[id],
                        Airport = a,
                        LeIdent = csv[leId],
                        HeIdent = csv[heId],
                        LeLatitude = CsvReader.Double(csv[leLat]),
                        LeLongitude = CsvReader.Double(csv[leLon]),
                        HeLatitude = CsvReader.Double(csv[heLat]),
                        HeLongitude = CsvReader.Double(csv[heLon]),
                        LeElevation = CsvReader.Float(csv[leElev]),
                        HeElevation = CsvReader.Float(csv[heElev]),
                        LeHeading = CsvReader.Float(csv[leHdg], 0f),
                        HeHeading = CsvReader.Float(csv[heHdg], 0f),
                        Length = CsvReader.Float(csv[length], 0f),
                        Width = CsvReader.Float(csv[width], 30f),
                        LeDisplaced = CsvReader.Float(csv[leDisp], 0f),
                        HeDisplaced = CsvReader.Float(csv[heDisp], 0f),
                        Surface = ParseSurface(csv[surface]),
                        Lighted = csv[lighted] == "1",
                        Approx = csv[position] == "approx",
                    };
                    if (double.IsNaN(r.LeLatitude) || double.IsNaN(r.LeLongitude) ||
                        double.IsNaN(r.HeLatitude) || double.IsNaN(r.HeLongitude)) continue;
                    a.Runways.Add(r);
                    db.Runways.Add(r);
                }
            }

            foreach (Airport a in db.Airports)
            {
                a.Layout = AirportLayout.Build(a, frameSettings);
                a.SearchText = Fold($"{a.Name} {a.Municipality} {a.CountryName} {a.Keywords}");
                a.SearchCodes = new[] { Fold(a.Ident), Fold(a.Icao), Fold(a.Iata), Fold(a.Gps), Fold(a.Local) };
                db.IndexByTile(a);
            }

            db.LoadMilliseconds = timer.Elapsed.TotalMilliseconds;
            return db;
        }

        static AirportType ParseType(string s)
        {
            switch (s)
            {
                case "large_airport": return AirportType.Large;
                case "medium_airport": return AirportType.Medium;
                case "small_airport": return AirportType.Small;
                case "heliport": return AirportType.Heliport;
                case "seaplane_base": return AirportType.SeaplaneBase;
                case "balloonport": return AirportType.Balloonport;
                case "closed": return AirportType.Closed;
                default: return AirportType.Other;
            }
        }

        static RunwaySurface ParseSurface(string s)
        {
            switch (s)
            {
                case "asphalt": return RunwaySurface.Asphalt;
                case "concrete": return RunwaySurface.Concrete;
                case "grass": return RunwaySurface.Grass;
                case "dirt": return RunwaySurface.Dirt;
                case "gravel": return RunwaySurface.Gravel;
                case "sand": return RunwaySurface.Sand;
                case "snow": return RunwaySurface.Snow;
                case "water": return RunwaySurface.Water;
                case "other": return RunwaySurface.Other;
                default: return RunwaySurface.Unknown;
            }
        }

        /// Lays every airport out again with other frame settings (runway margins, infield
        /// limits) and rebuilds the tile index. Main thread; a few hundred milliseconds for the
        /// world. Frame classes are reset, so they are picked again on next use.
        public void RebuildLayouts(AirportFrameSettings frameSettings)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            FrameSettings = frameSettings;
            byTile.Clear();
            foreach (Airport a in Airports)
            {
                a.Layout = AirportLayout.Build(a, frameSettings);
                a.FrameClass = -1;
                IndexByTile(a);
            }
            LoadMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        void IndexByTile(Airport a)
        {
            AirportLayout l = a.Layout;
            if (l == null || l.Frames.Count == 0) return;
            int lat0 = (int)Math.Floor(l.MinLatitude), lat1 = (int)Math.Floor(l.MaxLatitude);
            int lon0 = (int)Math.Floor(l.MinLongitude), lon1 = (int)Math.Floor(l.MaxLongitude);
            for (int lat = Math.Max(-90, lat0); lat <= Math.Min(89, lat1); lat++)
            {
                for (int lon = lon0; lon <= lon1; lon++)
                {
                    int w = ((lon + 180) % 360 + 360) % 360 - 180;
                    int key = TerrainDataSet.Key(lat, w);
                    if (!byTile.TryGetValue(key, out List<Airport> list)) byTile.Add(key, list = new List<Airport>());
                    list.Add(a);
                }
            }
        }

        // ---- queries ---------------------------------------------------------------------

        public Airport FindById(string id) => id != null && byId.TryGetValue(id, out Airport a) ? a : null;

        /// Airports whose frames reach into the 1° tile with south-west corner (lat, lon).
        public List<Airport> AirportsInTile(int lat, int lon) =>
            byTile.TryGetValue(TerrainDataSet.Key(lat, lon), out List<Airport> list) ? list : None;

        /// The airport whose frame holds a point, or null.
        public Airport FrameAt(double lat, double lon, double margin = 0.0)
        {
            int tLat = (int)Math.Floor(lat), tLon = (int)Math.Floor(GeoMath.WrapLongitude(lon));
            if (tLon >= 180) tLon -= 360;
            foreach (Airport a in AirportsInTile(Math.Min(89, tLat), tLon))
            {
                if (!a.Layout.BoundsContain(lat, lon)) continue;
                if (a.Layout.FrameDistance(lat, lon) < margin) return a;
            }
            return null;
        }

        // IAutogenExclusion: nothing grows on airport ground
        public bool Touches(double latMin, double lonMin, double latMax, double lonMax)
        {
            int tLat = (int)Math.Floor(0.5 * (latMin + latMax)), tLon = (int)Math.Floor(0.5 * (lonMin + lonMax));
            foreach (Airport a in AirportsInTile(Math.Min(89, tLat), tLon))
                if (a.Layout.BoundsOverlap(latMin, lonMin, latMax, lonMax)) return true;
            return false;
        }

        public bool Excludes(double lat, double lon) => FrameAt(lat, lon, ExclusionMargin) != null;

        // ---- search ----------------------------------------------------------------------

        public struct SearchResult
        {
            public Airport Airport;
            public int Score;
        }

        /// Airports matching a query, best first: an exact code (ICAO, IATA, ident, GPS or
        /// local), then code prefixes, then names whose words start with every query word,
        /// then names that merely contain them. Bigger airports win ties. Only airports with
        /// runways are returned.
        public List<SearchResult> Search(string query, int maxResults)
        {
            var results = new List<SearchResult>();
            string q = Fold(query ?? "").Trim();
            if (q.Length == 0) return results;
            string[] words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            bool exactCode = false;
            foreach (Airport a in Airports)
            {
                if (a.Runways.Count == 0) continue;
                int score = ScoreCodes(a, q);
                if (score >= 1000) exactCode = true;
                if (score == 0) score = ScoreText(a, words);
                if (score == 0) continue;
                score += a.Type == AirportType.Large ? 40 : a.Type == AirportType.Medium ? 20 : 0;
                if (a.Scheduled) score += 10;
                results.Add(new SearchResult { Airport = a, Score = score });
            }
            // A code that matched exactly ("RKSO") makes names that merely contain it
            // ("Worksop") noise; word-start name matches stay.
            if (exactCode) results.RemoveAll(r => r.Score < 300);

            // equal scores: the longest runway first (Heathrow before Luton for "london")
            results.Sort((x, y) =>
            {
                int c = y.Score.CompareTo(x.Score);
                if (c == 0) c = y.Airport.Layout.LongestRunway.CompareTo(x.Airport.Layout.LongestRunway);
                return c != 0 ? c : string.CompareOrdinal(x.Airport.Code, y.Airport.Code);
            });
            if (results.Count > maxResults) results.RemoveRange(maxResults, results.Count - maxResults);
            return results;
        }

        static int ScoreCodes(Airport a, string q)
        {
            int best = 0;
            foreach (string code in a.SearchCodes)
            {
                if (string.IsNullOrEmpty(code)) continue;
                if (code == q) best = Math.Max(best, 1000);
                else if (q.Length >= 2 && code.StartsWith(q, StringComparison.Ordinal)) best = Math.Max(best, 600);
            }
            return best;
        }

        static int ScoreText(Airport a, string[] words)
        {
            int score = 300;
            string text = a.SearchText;
            foreach (string w in words)
            {
                int at = text.IndexOf(w, StringComparison.Ordinal);
                if (at < 0) return 0;
                bool wordStart = false;
                for (; at >= 0; at = text.IndexOf(w, at + 1, StringComparison.Ordinal))
                {
                    if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) { wordStart = true; break; }
                }
                if (!wordStart) score = Math.Min(score, 100);
            }
            return score;
        }

        /// Upper case without accents, so "Zurich" finds "Zürich".
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
            {
                UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        // ---- frequencies and navaids (read on first use) -----------------------------------

        public List<AirportFrequency> FrequenciesOf(Airport airport)
        {
            var list = new List<AirportFrequency>();
            foreach (AirportFrequency f in Frequencies) if (f.AirportId == airport.Id) list.Add(f);
            return list;
        }

        public List<AirportFrequency> Frequencies
        {
            get
            {
                lock (lazyLock)
                {
                    if (frequencies != null) return frequencies;
                    frequencies = new List<AirportFrequency>();
                    string path = Path.Combine(Folder, "airport-frequencies.csv");
                    if (!File.Exists(path)) return frequencies;
                    using var reader = new StreamReader(path, Encoding.UTF8);
                    var csv = new CsvReader(reader);
                    int ap = csv.Column("airport_id"), type = csv.Column("type"),
                        desc = csv.Column("description"), mhz = csv.Column("frequency_mhz");
                    while (csv.ReadRow())
                        frequencies.Add(new AirportFrequency
                        {
                            AirportId = csv[ap], Type = csv[type], Description = csv[desc],
                            Megahertz = CsvReader.Double(csv[mhz], 0.0),
                        });
                    return frequencies;
                }
            }
        }

        public List<Navaid> Navaids
        {
            get
            {
                lock (lazyLock)
                {
                    if (navaids != null) return navaids;
                    navaids = new List<Navaid>();
                    string path = Path.Combine(Folder, "navaids.csv");
                    if (!File.Exists(path)) return navaids;
                    using var reader = new StreamReader(path, Encoding.UTF8);
                    var csv = new CsvReader(reader);
                    int id = csv.Column("id"), ident = csv.Column("ident"), name = csv.Column("name"),
                        type = csv.Column("type"), khz = csv.Column("frequency_khz"), lat = csv.Column("lat"),
                        lon = csv.Column("lon"), elev = csv.Column("elevation_m"), country = csv.Column("country"),
                        channel = csv.Column("dme_channel"), variation = csv.Column("magnetic_variation_deg"),
                        assoc = csv.Column("associated_airport");
                    while (csv.ReadRow())
                    {
                        double la = CsvReader.Double(csv[lat]), lo = CsvReader.Double(csv[lon]);
                        if (double.IsNaN(la) || double.IsNaN(lo)) continue;
                        navaids.Add(new Navaid
                        {
                            Id = csv[id], Ident = csv[ident], Name = csv[name], Type = csv[type],
                            FrequencyKhz = CsvReader.Double(csv[khz], 0.0), Latitude = la, Longitude = lo,
                            Elevation = CsvReader.Float(csv[elev]), Country = csv[country],
                            DmeChannel = csv[channel], MagneticVariation = CsvReader.Float(csv[variation]),
                            AssociatedAirport = csv[assoc],
                        });
                    }
                    return navaids;
                }
            }
        }
    }
}
