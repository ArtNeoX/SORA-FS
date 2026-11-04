using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CirrusFS.Geo
{
    public enum AirportType { Large, Medium, Small, Heliport, SeaplaneBase, Balloonport, Closed, Other }

    /// Runway surface category from SceneryDataGen/airport_exporter. The numeric values go to
    /// the terrain shader, so keep them in step with Terrain.shader.
    public enum RunwaySurface { Asphalt = 0, Concrete = 1, Grass = 2, Dirt = 3, Gravel = 4, Sand = 5, Snow = 6, Water = 7, Other = 8, Unknown = 9 }

    /// One row of AirportData/airports.csv, plus the airport's runways and (built at load)
    /// its ground layout.
    public sealed class Airport
    {
        public int Index;                 // position in AirportDatabase.Airports
        public string Id, Ident, Name, Country, CountryName, Region, Municipality;
        public string Icao, Iata, Gps, Local, Keywords;
        public AirportType Type;
        public bool Scheduled;
        public double Latitude, Longitude;
        public float Elevation = float.NaN;  // metres MSL, NaN when unknown
        public readonly List<Runway> Runways = new List<Runway>();

        /// Runway rectangles and the cleared frame around them, in the airport's own tangent
        /// plane. Built once at load.
        public AirportLayout Layout;

        /// Terrain class the frame shows (grass, rock, sand or snow, picked from the land
        /// around the airport). -1 until first needed.
        public int FrameClass = -1;

        internal string SearchText;       // folded, upper case: name, city, country, keywords
        internal string[] SearchCodes;    // folded, upper case: ident, ICAO, IATA, GPS, local

        /// The code a pilot would use: ICAO where there is one, else the OurAirports ident.
        public string Code => !string.IsNullOrEmpty(Icao) ? Icao : Ident;

        public bool HasApproxRunways
        {
            get
            {
                foreach (Runway r in Runways) if (r.Approx) return true;
                return false;
            }
        }

        public override string ToString() => $"{Code} {Name}";
    }

    /// One row of AirportData/runways.csv. "le" is the low-numbered end: landing on it means
    /// flying LeHeading, from the le point toward the he point.
    public sealed class Runway
    {
        public string Id, LeIdent, HeIdent;
        public Airport Airport;
        public double LeLatitude, LeLongitude, HeLatitude, HeLongitude;
        public float LeElevation = float.NaN, HeElevation = float.NaN;   // metres MSL
        public float LeHeading, HeHeading;                                // true, degrees
        public float Length, Width;                                       // metres
        public float LeDisplaced, HeDisplaced;                            // metres
        public RunwaySurface Surface;
        public bool Lighted;

        /// Placed by the exporter on the airport reference point (OurAirports has no end
        /// coordinates for it): the position can be off by a few hundred metres.
        public bool Approx;

        public string Designator => string.IsNullOrEmpty(HeIdent) ? LeIdent : LeIdent + "/" + HeIdent;

        public override string ToString() => $"{Airport?.Code} {Designator}";
    }

    public sealed class AirportFrequency
    {
        public string AirportId, Type, Description;
        public double Megahertz;
    }

    public sealed class Navaid
    {
        public string Id, Ident, Name, Type, Country, AssociatedAirport, DmeChannel;
        public double Latitude, Longitude, FrequencyKhz;
        public float Elevation = float.NaN, MagneticVariation = float.NaN;
    }

    /// A small RFC 4180 CSV reader: quoted fields, doubled quotes, separators and line breaks
    /// inside quotes. The exporter writes UTF-8 with a header row.
    public sealed class CsvReader
    {
        readonly TextReader reader;
        readonly StringBuilder field = new StringBuilder(64);
        readonly List<string> row = new List<string>(32);
        readonly Dictionary<string, int> columns = new Dictionary<string, int>(StringComparer.Ordinal);

        public CsvReader(TextReader reader)
        {
            this.reader = reader;
            if (ReadRow())
                for (int i = 0; i < row.Count; i++) columns[row[i].Trim().TrimStart('﻿')] = i;
        }

        /// Index of a header column, or -1.
        public int Column(string name) => columns.TryGetValue(name, out int i) ? i : -1;

        public int FieldCount => row.Count;

        /// Field `index` of the current row; "" when the column is missing or the row short.
        public string this[int index] => index >= 0 && index < row.Count ? row[index] : "";

        /// Advances to the next record. False at the end of the input.
        public bool ReadRow()
        {
            row.Clear();
            field.Clear();
            int c = reader.Read();
            if (c < 0) return false;

            bool quoted = false, any = false;
            while (true)
            {
                if (c < 0)
                {
                    row.Add(field.ToString());
                    return true;
                }
                char ch = (char)c;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            reader.Read();
                            field.Append('"');
                        }
                        else quoted = false;
                    }
                    else field.Append(ch);
                }
                else if (ch == '"' && field.Length == 0) quoted = true;
                else if (ch == ',')
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (ch == '\r')
                {
                    // \r\n or a lone \r ends the record
                    if (reader.Peek() == '\n') reader.Read();
                    row.Add(field.ToString());
                    return true;
                }
                else if (ch == '\n')
                {
                    if (!any && field.Length == 0 && row.Count == 0)
                    {
                        // blank line: skip it
                        c = reader.Read();
                        if (c < 0) return false;
                        continue;
                    }
                    row.Add(field.ToString());
                    return true;
                }
                else field.Append(ch);
                any = true;
                c = reader.Read();
            }
        }

        public static double Double(string s, double fallback = double.NaN) =>
            double.TryParse(s, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;

        public static float Float(string s, float fallback = float.NaN) => (float)Double(s, fallback);
    }
}
