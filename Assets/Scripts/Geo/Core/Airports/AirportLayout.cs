using System;
using System.Collections.Generic;

namespace CirrusFS.Geo
{
    /// A 2D vector in double: east/north metres in an airport's tangent plane.
    public readonly struct Double2
    {
        public readonly double x, y;
        public Double2(double x, double y) { this.x = x; this.y = y; }
        public static Double2 operator +(Double2 a, Double2 b) => new Double2(a.x + b.x, a.y + b.y);
        public static Double2 operator -(Double2 a, Double2 b) => new Double2(a.x - b.x, a.y - b.y);
        public static Double2 operator *(Double2 a, double s) => new Double2(a.x * s, a.y * s);
        public static double Dot(Double2 a, Double2 b) => a.x * b.x + a.y * b.y;
        public static double Cross(Double2 a, Double2 b) => a.x * b.y - a.y * b.x;
        public double Length => Math.Sqrt(x * x + y * y);
        public Double2 Normalized { get { double l = Length; return l > 0 ? new Double2(x / l, y / l) : new Double2(0, 0); } }
        public override string ToString() => $"({x:F1}, {y:F1})";
    }

    /// How big the cleared ground around runways is.
    [Serializable]
    public struct AirportFrameSettings
    {
        public float lateralMin;          // metres beside each runway edge, at least
        public float lateralWidthFactor;  // ... or this times the runway width
        public float endMarginShort;      // past each end, runways under 1200 m
        public float endMarginMedium;     // 1200 .. 1800 m
        public float endMarginLong;       // 1800 m and longer
        public float maxInfieldSize;      // the infield (hull) is used up to this diameter, metres
        public float maxInfieldAreaRatio; // ... and up to this times the runway frames' total area
        public float boundsMargin;        // added to the bounds for the shader's wavy edge, metres

        public static AirportFrameSettings Default => new AirportFrameSettings
        {
            lateralMin = 30f,
            lateralWidthFactor = 1f,
            endMarginShort = 60f,
            endMarginMedium = 100f,
            endMarginLong = 150f,
            maxInfieldSize = 8000f,
            maxInfieldAreaRatio = 6f,
            boundsMargin = 60f,
        };

        public float EndMargin(double length) =>
            length < 1200.0 ? endMarginShort : (length < 1800.0 ? endMarginMedium : endMarginLong);
    }

    /// A convex polygon, counter-clockwise, in an airport's tangent plane (x east, y north,
    /// metres). Edge k runs from Vertices[k] to Vertices[k + 1]; Normals point outward and
    /// Offsets[k] = dot(Normals[k], Vertices[k]), so a point p is inside where every
    /// dot(Normals[k], p) - Offsets[k] is negative.
    public sealed class FramePolygon
    {
        public Double2[] Vertices;
        public Double2[] Normals;
        public double[] Offsets;
        public Double2 Centre;
        public double Radius;

        public FramePolygon(IReadOnlyList<Double2> ccw)
        {
            int n = ccw.Count;
            Vertices = new Double2[n];
            Normals = new Double2[n];
            Offsets = new double[n];
            double cx = 0, cy = 0;
            for (int k = 0; k < n; k++)
            {
                Vertices[k] = ccw[k];
                cx += ccw[k].x;
                cy += ccw[k].y;
            }
            Centre = new Double2(cx / n, cy / n);
            for (int k = 0; k < n; k++)
            {
                Double2 a = Vertices[k], b = Vertices[(k + 1) % n];
                Double2 e = (b - a).Normalized;
                Normals[k] = new Double2(e.y, -e.x);   // right of a CCW edge = outside
                Offsets[k] = Double2.Dot(Normals[k], a);
                Radius = Math.Max(Radius, (a - Centre).Length);
            }
        }

        /// Signed distance to the edge lines, metres: negative inside (exact there), an
        /// underestimate outside near corners. Enough for "within so many metres".
        public double SignedDistance(Double2 p)
        {
            double d = double.MinValue;
            for (int k = 0; k < Normals.Length; k++) d = Math.Max(d, Double2.Dot(Normals[k], p) - Offsets[k]);
            return d;
        }

        public double Area
        {
            get
            {
                double a = 0;
                for (int k = 0; k < Vertices.Length; k++) a += Double2.Cross(Vertices[k], Vertices[(k + 1) % Vertices.Length]);
                return 0.5 * a;
            }
        }
    }

    /// An airport's runways and the frame of cleared ground around them, in the tangent plane
    /// at its reference point. Over the few kilometres of an airport, the plane is within
    /// centimetres of the ellipsoid in position, which is all that matters for drawing
    /// rectangles on the terrain.
    ///
    /// The frame: every runway grows sideways by max(lateralMin, lateralWidthFactor x width)
    /// beyond each edge and by an end margin past each end. An airport with several runways
    /// clears the convex hull of all of them, so the infield between parallel or crossing
    /// runways is cleared too; unless that hull would be huge (runways far apart, or a stray
    /// approximate runway), in which case each runway keeps its own rectangle.
    public sealed class AirportLayout
    {
        /// Most edges a frame polygon may have (the shader reads one float4 per edge).
        public const int MaxEdges = 8;

        public readonly double OriginLatitude, OriginLongitude;
        readonly double metresPerDegLat, metresPerDegLon;

        /// Height the geometry is placed at, metres above the ellipsoid (MSL used as such,
        /// like the terrain): the lowest runway end, else the field elevation, else 0.
        public readonly float ReferenceHeight;

        /// True when ReferenceHeight comes from the data (not the 0 fallback).
        public readonly bool HasElevation;

        public readonly List<FramePolygon> Frames = new List<FramePolygon>();

        /// A runway as drawn (no margins), in the tangent plane.
        public struct RunwayRect
        {
            public Double2 Centre, Along;     // Along: unit vector from the le end to the he end
            public double HalfLength, HalfWidth;

            /// Signed distance to the rectangle, metres (negative inside).
            public double Distance(Double2 p)
            {
                Double2 q = p - Centre;
                double u = Math.Abs(Double2.Dot(q, Along)) - HalfLength;
                double v = Math.Abs(Double2.Cross(Along, q)) - HalfWidth;
                double ou = Math.Max(u, 0.0), ov = Math.Max(v, 0.0);
                return Math.Sqrt(ou * ou + ov * ov) + Math.Min(Math.Max(u, v), 0.0);
            }
        }

        public readonly List<RunwayRect> RunwayRects = new List<RunwayRect>();

        /// Farthest runway corner from the reference point, metres.
        public double RunwayExtent { get; private set; }

        /// True when some runway overlaps the box of ± `halfDegrees` latitude and longitude
        /// around a point (separating-axis test of the runway rectangle against the box).
        public bool RunwayOverlapsCell(double lat, double lon, double halfDegrees)
        {
            Double2 p = ToLocal(lat, lon);
            double bx = halfDegrees * metresPerDegLon, by = halfDegrees * metresPerDegLat;
            foreach (RunwayRect r in RunwayRects)
            {
                Double2 u = r.Along, v = new Double2(-u.y, u.x);
                Double2 d = r.Centre - p;
                double ex = r.HalfLength * Math.Abs(u.x) + r.HalfWidth * Math.Abs(v.x);
                double ey = r.HalfLength * Math.Abs(u.y) + r.HalfWidth * Math.Abs(v.y);
                if (Math.Abs(d.x) > bx + ex || Math.Abs(d.y) > by + ey) continue;
                if (Math.Abs(Double2.Dot(d, u)) > r.HalfLength + bx * Math.Abs(u.x) + by * Math.Abs(u.y)) continue;
                if (Math.Abs(Double2.Dot(d, v)) > r.HalfWidth + bx * Math.Abs(v.x) + by * Math.Abs(v.y)) continue;
                return true;
            }
            return false;
        }

        /// Signed distance to the nearest runway rectangle, metres; +inf without runways.
        public double RunwayDistance(double lat, double lon)
        {
            Double2 p = ToLocal(lat, lon);
            double best = double.PositiveInfinity;
            foreach (RunwayRect r in RunwayRects) best = Math.Min(best, r.Distance(p));
            return best;
        }

        /// Frames plus boundsMargin, degrees (longitude continuous across 180).
        public double MinLatitude, MaxLatitude, MinLongitude, MaxLongitude;

        public float LongestRunway { get; private set; }

        /// Narrowest frame width, metres: what decides whether a frame is worth drawing on a
        /// coarse chunk.
        public float NarrowestFrame { get; private set; }

        AirportLayout(double lat, double lon, float height, bool hasElevation)
        {
            OriginLatitude = lat;
            OriginLongitude = lon;
            ReferenceHeight = height;
            HasElevation = hasElevation;
            double phi = lat * GeoMath.Deg2Rad, s = Math.Sin(phi);
            double w = Math.Sqrt(1.0 - GeoMath.E2 * s * s);
            metresPerDegLat = GeoMath.A * (1.0 - GeoMath.E2) / (w * w * w) * GeoMath.Deg2Rad;
            metresPerDegLon = Math.Max(GeoMath.A / w * Math.Cos(phi) * GeoMath.Deg2Rad, 1.0);
        }

        public Double2 ToLocal(double lat, double lon)
        {
            double dLon = lon - OriginLongitude;
            if (dLon > 180.0) dLon -= 360.0;
            else if (dLon < -180.0) dLon += 360.0;
            return new Double2(dLon * metresPerDegLon, (lat - OriginLatitude) * metresPerDegLat);
        }

        public void ToLatLon(Double2 p, out double lat, out double lon)
        {
            lat = OriginLatitude + p.y / metresPerDegLat;
            lon = OriginLongitude + p.x / metresPerDegLon;   // may run past 180; callers wrap
        }

        /// Signed distance to the nearest frame, metres (negative inside); +inf without frames.
        public double FrameDistance(double lat, double lon)
        {
            Double2 p = ToLocal(lat, lon);
            double best = double.PositiveInfinity;
            foreach (FramePolygon f in Frames)
            {
                if ((p - f.Centre).Length - f.Radius > best) continue;
                best = Math.Min(best, f.SignedDistance(p));
            }
            return best;
        }

        public bool BoundsContain(double lat, double lon)
        {
            if (lat < MinLatitude || lat > MaxLatitude) return false;
            return LongitudeOverlaps(MinLongitude, MaxLongitude, lon, lon);
        }

        public bool BoundsOverlap(double latMin, double lonMin, double latMax, double lonMax) =>
            latMax >= MinLatitude && latMin <= MaxLatitude && LongitudeOverlaps(MinLongitude, MaxLongitude, lonMin, lonMax);

        /// Longitude intervals overlap, allowing for either to be shifted by 360 degrees.
        public static bool LongitudeOverlaps(double a0, double a1, double b0, double b1)
        {
            for (int k = -1; k <= 1; k++)
            {
                double s = k * 360.0;
                if (b1 + s >= a0 && b0 + s <= a1) return true;
            }
            return false;
        }

        // ---- building ----------------------------------------------------------------------

        public static AirportLayout Build(Airport airport, in AirportFrameSettings fs)
        {
            float height = float.NaN;
            foreach (Runway r in airport.Runways)
            {
                if (!float.IsNaN(r.LeElevation)) height = float.IsNaN(height) ? r.LeElevation : Math.Min(height, r.LeElevation);
                if (!float.IsNaN(r.HeElevation)) height = float.IsNaN(height) ? r.HeElevation : Math.Min(height, r.HeElevation);
            }
            if (float.IsNaN(height)) height = airport.Elevation;
            bool hasElevation = !float.IsNaN(height);

            var layout = new AirportLayout(airport.Latitude, airport.Longitude, hasElevation ? height : 0f, hasElevation);
            var rects = new List<Double2[]>();
            double rectArea = 0.0;
            float narrowest = float.MaxValue;

            foreach (Runway r in airport.Runways)
            {
                Double2 le = layout.ToLocal(r.LeLatitude, r.LeLongitude);
                Double2 he = layout.ToLocal(r.HeLatitude, r.HeLongitude);
                Double2 axis = he - le;
                double length = axis.Length;
                if (length < 1.0) continue;
                Double2 along = axis * (1.0 / length);
                Double2 across = new Double2(-along.y, along.x);        // left of along
                double halfW = 0.5 * r.Width + Math.Max(fs.lateralMin, fs.lateralWidthFactor * r.Width);
                // Paved runoff (arrows, blast pad) beyond each end is part of the paved
                // rectangle, and the frame grows by the longer of the two.
                RunwayMarkings.Runoff(r, false, out float leArrows, out float leBlast);
                RunwayMarkings.Runoff(r, true, out float heArrows, out float heBlast);
                double leRunoff = leArrows + leBlast, heRunoff = heArrows + heBlast;
                double halfL = 0.5 * length + fs.EndMargin(length) + Math.Max(leRunoff, heRunoff);
                Double2 c = (le + he) * 0.5;
                layout.RunwayRects.Add(new RunwayRect
                {
                    Centre = c + along * (0.5 * (heRunoff - leRunoff)),
                    Along = along,
                    HalfLength = 0.5 * (length + leRunoff + heRunoff),
                    HalfWidth = 0.5 * r.Width,
                });
                layout.RunwayExtent = Math.Max(layout.RunwayExtent, c.Length + Math.Sqrt(0.25 * length * length + 0.25 * r.Width * r.Width));
                // counter-clockwise: right-back, right-front, left-front, left-back
                rects.Add(new[]
                {
                    c - along * halfL - across * halfW,
                    c + along * halfL - across * halfW,
                    c + along * halfL + across * halfW,
                    c - along * halfL + across * halfW,
                });
                rectArea += 4.0 * halfW * halfL;
                narrowest = Math.Min(narrowest, (float)(2.0 * halfW));
                layout.LongestRunway = Math.Max(layout.LongestRunway, (float)length);
            }
            if (rects.Count == 0) return layout;
            layout.NarrowestFrame = narrowest;

            bool infield = false;
            if (rects.Count > 1)
            {
                var points = new List<Double2>();
                foreach (Double2[] rc in rects) points.AddRange(rc);
                List<Double2> hull = ConvexHull(points);
                double diameter = 0.0;
                for (int i = 0; i < hull.Count; i++)
                    for (int j = i + 1; j < hull.Count; j++)
                        diameter = Math.Max(diameter, (hull[i] - hull[j]).Length);
                var hullPolygon = new FramePolygon(Simplify(hull, MaxEdges));
                if (diameter <= fs.maxInfieldSize && hullPolygon.Area <= fs.maxInfieldAreaRatio * rectArea)
                {
                    layout.Frames.Add(hullPolygon);
                    infield = true;
                }
            }
            if (!infield)
                foreach (Double2[] rc in rects) layout.Frames.Add(new FramePolygon(rc));

            // bounds
            double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
            foreach (FramePolygon f in layout.Frames)
            {
                foreach (Double2 v in f.Vertices)
                {
                    minE = Math.Min(minE, v.x); maxE = Math.Max(maxE, v.x);
                    minN = Math.Min(minN, v.y); maxN = Math.Max(maxN, v.y);
                }
            }
            double m = fs.boundsMargin;
            layout.ToLatLon(new Double2(minE - m, minN - m), out layout.MinLatitude, out layout.MinLongitude);
            layout.ToLatLon(new Double2(maxE + m, maxN + m), out layout.MaxLatitude, out layout.MaxLongitude);
            return layout;
        }

        /// Andrew's monotone chain; counter-clockwise, no repeated end point.
        public static List<Double2> ConvexHull(List<Double2> points)
        {
            var p = new List<Double2>(points);
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            if (p.Count < 3) return p;
            var hull = new Double2[2 * p.Count];
            int k = 0;
            for (int i = 0; i < p.Count; i++)
            {
                while (k >= 2 && Double2.Cross(hull[k - 1] - hull[k - 2], p[i] - hull[k - 2]) <= 0) k--;
                hull[k++] = p[i];
            }
            for (int i = p.Count - 2, t = k + 1; i >= 0; i--)
            {
                while (k >= t && Double2.Cross(hull[k - 1] - hull[k - 2], p[i] - hull[k - 2]) <= 0) k--;
                hull[k++] = p[i];
            }
            var result = new List<Double2>(k - 1);
            for (int i = 0; i < k - 1; i++) result.Add(hull[i]);
            return result;
        }

        /// Cuts a convex CCW polygon down to `maxEdges` edges without losing any of it: an edge
        /// is removed by extending its two neighbours until they meet, choosing each time the
        /// edge whose removal adds the least area.
        public static List<Double2> Simplify(List<Double2> polygon, int maxEdges)
        {
            var p = new List<Double2>(polygon);
            while (p.Count > maxEdges)
            {
                int n = p.Count, best = -1;
                double bestArea = double.MaxValue;
                Double2 bestPoint = default;
                for (int i = 0; i < n; i++)
                {
                    // remove edge (i, i+1): intersect lines (i-1 -> i) and (i+2 -> i+1)
                    Double2 a0 = p[(i - 1 + n) % n], a1 = p[i];
                    Double2 b0 = p[(i + 2) % n], b1 = p[(i + 1) % n];
                    Double2 da = a1 - a0, db = b1 - b0;
                    double denom = Double2.Cross(da, db);
                    if (Math.Abs(denom) < 1e-9) continue;
                    double t = Double2.Cross(b0 - a0, db) / denom;
                    if (t < 1.0) continue;                           // must meet beyond a1
                    Double2 x = a0 + da * t;
                    if (Double2.Dot(x - b1, b1 - b0) < 0) continue;  // and beyond b1
                    double area = 0.5 * Math.Abs(Double2.Cross(b1 - a1, x - a1));
                    if (area < bestArea)
                    {
                        bestArea = area;
                        best = i;
                        bestPoint = x;
                    }
                }
                if (best < 0) break;   // cannot happen for a convex polygon with more than 4 edges
                int j = (best + 1) % n;
                p[best] = bestPoint;
                p.RemoveAt(j);
            }
            return p;
        }
    }
}
