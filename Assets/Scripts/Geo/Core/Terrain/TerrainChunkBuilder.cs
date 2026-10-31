using System;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// Output of one chunk build, reused between builds. Positions and normals are in the
    /// chunk's local frame: offsets from `Centre` along (East, Up, North), as for ocean chunks.
    public sealed class TerrainChunkData
    {
        public int VertexCount, IndexCount;
        public Vector3[] Vertices, Normals;
        public Vector2[] ClassUv;

        /// x, y: Earth-anchored detail coordinates, a Mercator grid in DetailPeriod units
        /// (conformal: textures keep their shape, scaled by 1/cos lat); z: height above the
        /// ellipsoid, metres (the shader uses it to tell shore from lake).
        public Vector3[] DetailUv;
        public int[] Indices;

        /// (texels + 2)^2 class ids, row 0 = south, with a one-texel apron taken from the
        /// neighbouring cells (the next tile's, where it exists) so the shader's blend runs
        /// across chunk and tile borders.
        public byte[] ClassTexels;
        public int ClassTextureSize;

        public Double3 Centre, East, North, Up;
        public double Radius;         // bounding sphere around Centre (grid, not skirts)
        public double Spacing;        // longest distance between neighbouring vertices, m
        public float MinHeight, MaxHeight;

        internal double[] heightGrid;   // (R + 3)^2, one vertex of apron each side
    }

    /// Builds the mesh for chunk (level, x, y) of a 1° terrain tile.
    ///
    /// A tile's quadtree: level L splits the tile into 2^L x 2^L chunks; chunk x counts east
    /// from the tile's west edge and y counts north from its south edge. Every chunk is a
    /// (R + 1)^2 vertex grid whose vertex (i, j) sits at
    ///
    ///     lat = (tileLat * K + y * R + j) / K,   lon = (tileLon * K + x * R + i) / K,   K = 2^L * R
    ///
    /// computed from integers, so a vertex on a shared border gets the same double in both
    /// chunks, and in both tiles at a tile edge. Heights are the tile's bilinear surface (the
    /// exporter makes shared tile edges identical), placed through GeoMath in double and then
    /// stored relative to the chunk centre. Two chunks at the same level therefore meet
    /// exactly; chunks at different levels meet within their skirts.
    public static class TerrainChunkBuilder
    {
        /// Period of the detail texture coordinates, metres. The shader's finer octaves use
        /// integer fractions of it, so they stay seamless from chunk to chunk.
        public const double DetailPeriod = 4096.0;

        /// Deepest level worth building for a tile: where vertex spacing reaches the height
        /// samples and class texels reach the class cells.
        public static int MaxUsefulLevel(TerrainTileSource tile, int resolution, int texels)
        {
            int level = 0;
            while (level < 12 && ((1 << level) * resolution < tile.N || (1 << level) * texels < tile.M)) level++;
            return level;
        }

        public static void Build(TerrainDataSet set, TerrainTileSource tile, int level, int x, int y,
                                 int resolution, int texels, bool skirts, TerrainChunkData d)
        {
            set.Touch(tile);
            int r = resolution;
            int gridCount = (r + 1) * (r + 1);
            int skirtCount = skirts ? 4 * r : 0;
            int vertexCount = gridCount + skirtCount;
            int indexCount = r * r * 6 + skirtCount * 6;
            int texSize = texels + 2;

            if (d.Vertices == null || d.Vertices.Length != vertexCount)
            {
                d.Vertices = new Vector3[vertexCount];
                d.Normals = new Vector3[vertexCount];
                d.ClassUv = new Vector2[vertexCount];
                d.DetailUv = new Vector3[vertexCount];
                d.Indices = new int[indexCount];
            }
            if (d.ClassTexels == null || d.ClassTexels.Length != texSize * texSize) d.ClassTexels = new byte[texSize * texSize];
            if (d.heightGrid == null || d.heightGrid.Length != (r + 3) * (r + 3)) d.heightGrid = new double[(r + 3) * (r + 3)];

            d.VertexCount = vertexCount;
            d.IndexCount = indexCount;
            d.ClassTextureSize = texSize;

            long k = (1L << level) * r;                 // vertices per degree at this level
            long iBase = tile.Lon * k + x * r;          // global vertex index of the chunk's west column
            long jBase = tile.Lat * k + y * r;          // ... and south row
            double size = 1.0 / (1 << level);
            double lat0 = tile.Lat + y * size, lon0 = tile.Lon + x * size;

            // ---- heights, with a one-vertex apron for the normals ----------------------
            int g = r + 3;
            double[] hg = d.heightGrid;
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int j = -1; j <= r + 1; j++)
            {
                double lat = (double)(jBase + j) / k;
                for (int i = -1; i <= r + 1; i++)
                {
                    double lon = (double)(iBase + i) / k;
                    float h = set.SampleHeight(lat, lon, tile);
                    hg[(j + 1) * g + (i + 1)] = h;
                    if (i >= 0 && i <= r && j >= 0 && j <= r)
                    {
                        if (h < minH) minH = h;
                        if (h > maxH) maxH = h;
                    }
                }
            }
            d.MinHeight = minH;
            d.MaxHeight = maxH;

            // ---- chunk frame -----------------------------------------------------------
            double latC = lat0 + size * 0.5, lonC = lon0 + size * 0.5;
            Double3 centre = GeoMath.GeodeticToEcef(latC, lonC, 0.5 * (minH + maxH));
            GeoMath.EnuBasis(latC, lonC, out Double3 ce, out Double3 cn, out Double3 cu);
            d.Centre = centre;
            d.East = ce;
            d.North = cn;
            d.Up = cu;

            // detail coordinates: Mercator metres (equator scale), offset by a multiple of the
            // period chosen from the chunk centre (any multiple keeps neighbours seamless).
            // Conformal, so tiling textures are never sheared or stretched.
            double metresPerRad = GeoMath.A;
            double offX = Math.Floor(lonC * GeoMath.Deg2Rad * metresPerRad / DetailPeriod) * DetailPeriod;
            double offY = Math.Floor(MercatorY(latC) / DetailPeriod) * DetailPeriod;

            double stepRad = GeoMath.Deg2Rad / k;
            double radius = 0.0;

            for (int j = 0; j <= r; j++)
            {
                double lat = (double)(jBase + j) / k;
                double phi = lat * GeoMath.Deg2Rad;
                double sinP = Math.Sin(phi), cosP = Math.Cos(phi);
                double w = Math.Sqrt(1.0 - GeoMath.E2 * sinP * sinP);
                double primeN = GeoMath.A / w;
                double meridianM = GeoMath.A * (1.0 - GeoMath.E2) / (w * w * w);
                double dEast = Math.Max(2.0 * stepRad * primeN * cosP, 1e-3);
                double dNorth = 2.0 * stepRad * meridianM;
                float detailY = (float)((MercatorY(lat) - offY) / DetailPeriod);

                for (int i = 0; i <= r; i++)
                {
                    double lon = (double)(iBase + i) / k;
                    double lam = lon * GeoMath.Deg2Rad;
                    double sinL = Math.Sin(lam), cosL = Math.Cos(lam);
                    double h = hg[(j + 1) * g + (i + 1)];

                    var p = new Double3((primeN + h) * cosP * cosL,
                                        (primeN + h) * cosP * sinL,
                                        (primeN * (1.0 - GeoMath.E2) + h) * sinP);

                    // normal from central differences in the vertex's own east/north/up
                    double dhE = (hg[(j + 1) * g + (i + 2)] - hg[(j + 1) * g + i]) / dEast;
                    double dhN = (hg[(j + 2) * g + (i + 1)] - hg[j * g + (i + 1)]) / dNorth;
                    var e = new Double3(-sinL, cosL, 0.0);
                    var n = new Double3(-sinP * cosL, -sinP * sinL, cosP);
                    var u = new Double3(cosP * cosL, cosP * sinL, sinP);
                    Double3 normal = (e * -dhE + n * -dhN + u).Normalized;

                    int v = j * (r + 1) + i;
                    Double3 rel = p - centre;
                    d.Vertices[v] = ToLocal(rel, ce, cn, cu);
                    d.Normals[v] = ToLocal(normal, ce, cn, cu);
                    d.ClassUv[v] = new Vector2((float)((i * (double)texels / r + 1.0) / texSize),
                                               (float)((j * (double)texels / r + 1.0) / texSize));
                    d.DetailUv[v] = new Vector3((float)((lam * metresPerRad - offX) / DetailPeriod), detailY, (float)h);

                    double m2 = rel.SqrMagnitude;
                    if (m2 > radius) radius = m2;
                }
            }
            d.Radius = Math.Sqrt(radius);

            // longest neighbour distance: east-west on the equatorward edge, or north-south
            double eqLat = Math.Abs(lat0) < Math.Abs(lat0 + size) ? lat0 : lat0 + size;
            double eastSpacing = size * GeoMath.Deg2Rad * GeoMath.PrimeVerticalRadius(eqLat) *
                                 Math.Cos(eqLat * GeoMath.Deg2Rad) / r;
            double northSpacing = size * GeoMath.Deg2Rad * GeoMath.A / r;
            d.Spacing = Math.Max(eastSpacing, northSpacing);

            // ---- triangles -------------------------------------------------------------
            int index = 0;
            for (int j = 0; j < r; j++)
            {
                for (int i = 0; i < r; i++)
                {
                    int a = j * (r + 1) + i, b = a + 1, c = a + (r + 1), e = c + 1;
                    // clockwise seen from above in the local E/U/N frame (Unity's front face)
                    d.Indices[index++] = a; d.Indices[index++] = c; d.Indices[index++] = b;
                    d.Indices[index++] = b; d.Indices[index++] = c; d.Indices[index++] = e;
                }
            }

            // ---- skirts ----------------------------------------------------------------
            // Hang a strip below the border, deep enough to cover the height error of a
            // neighbour one or two levels coarser (a fraction of its own vertex spacing on
            // steep ground) plus the curvature sag of its chords.
            if (skirts)
            {
                double sag = 4.0 * d.Spacing * d.Spacing / (8.0 * GeoMath.B);
                float depth = (float)(0.25 * d.Spacing + 16.0 * sag + 2.0);
                int[] border = BorderLoop(r);
                for (int s = 0; s < skirtCount; s++)
                {
                    int src = border[s];
                    int bj = src / (r + 1), bi = src % (r + 1);
                    double lat = (double)(jBase + bj) / k, lon = (double)(iBase + bi) / k;
                    GeoMath.EnuBasis(lat, lon, out _, out _, out Double3 up);
                    int dst = gridCount + s;
                    d.Vertices[dst] = d.Vertices[src] - ToLocal(up, ce, cn, cu) * depth;
                    d.Normals[dst] = d.Normals[src];
                    d.ClassUv[dst] = d.ClassUv[src];
                    d.DetailUv[dst] = d.DetailUv[src];
                }

                for (int s = 0; s < skirtCount; s++)
                {
                    int top0 = border[s], top1 = border[(s + 1) % skirtCount];
                    int bot0 = gridCount + s, bot1 = gridCount + (s + 1) % skirtCount;
                    d.Indices[index++] = top0; d.Indices[index++] = top1; d.Indices[index++] = bot0;
                    d.Indices[index++] = top1; d.Indices[index++] = bot1; d.Indices[index++] = bot0;
                }
            }

            // ---- class texels (cell centres, plus a one-texel apron) -------------------
            double texel = size / texels;
            for (int b = 0; b < texSize; b++)
            {
                double lat = lat0 + (b - 0.5) * texel;
                for (int a = 0; a < texSize; a++)
                {
                    double lon = lon0 + (a - 0.5) * texel;
                    d.ClassTexels[b * texSize + a] = set.ClassAt(lat, lon, tile);
                }
            }
        }

        /// Spherical Mercator northing, metres at equator scale. Clamped short of the poles.
        public static double MercatorY(double latDeg)
        {
            double phi = Math.Max(-89.5, Math.Min(89.5, latDeg)) * GeoMath.Deg2Rad;
            return GeoMath.A * Math.Log(Math.Tan(0.25 * Math.PI + 0.5 * phi));
        }

        static Vector3 ToLocal(Double3 v, Double3 east, Double3 north, Double3 up) =>
            new Vector3((float)Double3.Dot(v, east), (float)Double3.Dot(v, up), (float)Double3.Dot(v, north));

        static int[] borderLoop;
        static int borderLoopN;

        /// Grid indices of the border as one closed loop.
        static int[] BorderLoop(int n)
        {
            if (borderLoop != null && borderLoopN == n) return borderLoop;
            var loop = new int[4 * n];
            int k = 0;
            for (int i = 0; i < n; i++) loop[k++] = i;
            for (int j = 0; j < n; j++) loop[k++] = j * (n + 1) + n;
            for (int i = n; i > 0; i--) loop[k++] = n * (n + 1) + i;
            for (int j = n; j > 0; j--) loop[k++] = j * (n + 1);
            borderLoop = loop;
            borderLoopN = n;
            return loop;
        }
    }
}
