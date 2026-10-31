// Reader for tiles written by terrain_exporter.py (format version 2: 1° x 1° geographic tiles).
// Reference for the format. The globe terrain loader (Assets/Scripts/Geo) is built on it; it
// turns every vertex into ECEF with GeoMath.GeodeticToEcef(lat, lon, height).
using System;
using System.IO;
using UnityEngine;

[Serializable] public class TerrainManifest
{
    public string format;          // "CirrusTerrain"
    public int version;            // 2
    public GridInfo grid;
    public string verticalDatum;
    public HeightInfo height;
    public ClassInfo landClass;
    public float seaHeight;
    public int waterClass;
    public ClassDef[] classes;
    public TileInfo[] tiles;
}

[Serializable] public class GridInfo
{
    public string crs;             // "EPSG:4326"
    public int tileDegrees;        // 1
}

[Serializable] public class HeightInfo
{
    public string file;            // "height.r16"
    public int samplesPerDegree;   // n
    public int samples;            // n + 1 vertices per side, edges shared with neighbours
    public float min, max;         // metres = min + v / 65535 * (max - min)
    public string floatFile;       // "height.f32" or ""
}

[Serializable] public class ClassInfo
{
    public string file;            // "class.u8"
    public int cellsPerDegree;     // m
    public int pixels;             // m cells per side
}

[Serializable] public class ClassDef { public int id; public string name; public string color; }

[Serializable] public class TileInfo
{
    public string id;              // "+37+127"
    public int lat, lon;           // south-west corner, whole degrees
    public string path;            // "tiles/+30+120/+37+127", relative to the manifest
    public float hMin, hMax, landFraction;
}

/// One loaded tile. Arrays are [i, j] with i = 0 at the SOUTH edge, j = 0 at the WEST edge.
public class TerrainTile
{
    public TileInfo info;
    public float[,] heights;       // metres above the ellipsoid, (n+1) x (n+1)
    public byte[,] classes;        // class ids, m x m
    int n, m;

    /// Geodetic position of vertex [i, j]. Computed as (lat * n + i) / n so a shared edge
    /// vertex gets the same double from both tiles.
    public void VertexLatLon(int i, int j, out double latDeg, out double lonDeg)
    {
        latDeg = (info.lat * (double)n + i) / n;
        lonDeg = (info.lon * (double)n + j) / n;
    }

    /// Height in metres at a lat/lon inside this tile, bilinear between vertices.
    public float SampleHeight(double latDeg, double lonDeg)
    {
        double fi = Math.Min(Math.Max((latDeg - info.lat) * n, 0.0), n - 1e-9);
        double fj = Math.Min(Math.Max((lonDeg - info.lon) * n, 0.0), n - 1e-9);
        int i = (int)fi, j = (int)fj;
        float ti = (float)(fi - i), tj = (float)(fj - j);
        float a = Mathf.Lerp(heights[i, j], heights[i, j + 1], tj);
        float b = Mathf.Lerp(heights[i + 1, j], heights[i + 1, j + 1], tj);
        return Mathf.Lerp(a, b, ti);
    }

    /// Land class of the cell containing a lat/lon inside this tile.
    public byte ClassAt(double latDeg, double lonDeg)
    {
        int i = Math.Min(Math.Max((int)Math.Floor((latDeg - info.lat) * m), 0), m - 1);
        int j = Math.Min(Math.Max((int)Math.Floor((lonDeg - info.lon) * m), 0), m - 1);
        return classes[i, j];
    }

    public static TerrainTile Load(string root, TerrainManifest manifest, TileInfo t)
    {
        string dir = Path.Combine(root, t.path);
        return new TerrainTile
        {
            info = t,
            n = manifest.height.samplesPerDegree,
            m = manifest.landClass.cellsPerDegree,
            heights = TerrainTiles.LoadHeightsMeters(dir, manifest),
            classes = TerrainTiles.LoadClasses(dir, manifest),
        };
    }
}

public static class TerrainTiles
{
    public static TerrainManifest LoadManifest(string root) =>
        JsonUtility.FromJson<TerrainManifest>(File.ReadAllText(Path.Combine(root, "manifest.json")));

    /// "+37+127" for the tile whose south-west corner is 37 N, 127 E.
    public static string TileName(int lat, int lon) =>
        (lat < 0 ? "-" : "+") + Math.Abs(lat).ToString("00") +
        (lon < 0 ? "-" : "+") + Math.Abs(lon).ToString("000");

    /// The tile containing a point.
    public static string TileNameAt(double latDeg, double lonDeg) =>
        TileName((int)Math.Floor(latDeg), (int)Math.Floor(lonDeg));

    public static float[,] LoadHeightsMeters(string tileDir, TerrainManifest m)
    {
        int n = m.height.samples;
        byte[] raw = File.ReadAllBytes(Path.Combine(tileDir, m.height.file));
        float lo = m.height.min, scale = (m.height.max - m.height.min) / 65535f;
        var h = new float[n, n];
        for (int i = 0, k = 0; i < n; i++)
            for (int j = 0; j < n; j++, k += 2)
                h[i, j] = lo + (raw[k] | (raw[k + 1] << 8)) * scale;
        return h;
    }

    public static byte[,] LoadClasses(string tileDir, TerrainManifest m)
    {
        int p = m.landClass.pixels;
        byte[] raw = File.ReadAllBytes(Path.Combine(tileDir, m.landClass.file));
        var c = new byte[p, p];
        Buffer.BlockCopy(raw, 0, c, 0, p * p);
        return c;
    }

    /// Class map as a point-filtered R8 texture (texel (0,0) = south-west), for shaders.
    public static Texture2D LoadClassTexture(string tileDir, TerrainManifest m)
    {
        int p = m.landClass.pixels;
        var tex = new Texture2D(p, p, TextureFormat.R8, false, true)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
        tex.LoadRawTextureData(File.ReadAllBytes(Path.Combine(tileDir, m.landClass.file)));
        tex.Apply(false, false);
        return tex;
    }
}
