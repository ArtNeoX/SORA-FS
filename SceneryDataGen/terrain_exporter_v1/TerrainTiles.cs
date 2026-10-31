// Reader for tiles written by terrain_exporter.py (format version 1).
// Drop into Assets/Scripts. No dependencies beyond UnityEngine.
using System;
using System.IO;
using UnityEngine;

[Serializable] public class TerrainManifest
{
    public string format;
    public int version;
    public string crs;
    public double tileSize;
    public HeightInfo height;
    public ClassInfo landClass;
    public float seaHeight;
    public int waterClass;
    public ClassDef[] classes;
    public TileInfo[] tiles;
}

[Serializable] public class HeightInfo
{
    public string file;        // "height.r16"
    public int samples;        // e.g. 1025 (vertices, edges shared with neighbours)
    public float resolution;   // metres between vertices
    public float min, max;     // metres = min + v/65535 * (max - min)
    public string floatFile;   // "height.f32" or ""
}

[Serializable] public class ClassInfo
{
    public string file;        // "class.u8"
    public int pixels;         // e.g. 1024 (cells)
    public float resolution;   // metres per cell
}

[Serializable] public class ClassDef { public int id; public string name; public string color; }

[Serializable] public class TileInfo
{
    public string id;          // "col_row"
    public int col, row;
    public double x, y;        // south-west corner in projected metres (manifest.crs)
    public double lon, lat;    // tile centre
    public float hMin, hMax, landFraction;
}

/// One loaded tile. Arrays are [z, x] with z = 0 at the SOUTH edge, x = 0 at the WEST edge.
public class TerrainTile
{
    public TileInfo info;
    public float[,] heights;   // metres, samples x samples
    public byte[,] classes;    // class ids, pixels x pixels
    float size, hRes, cRes;

    /// Height in metres at tile-local position (0..tileSize, 0..tileSize), bilinear.
    public float SampleHeight(float localX, float localZ)
    {
        int n = heights.GetLength(0);
        float fx = Mathf.Clamp(localX / hRes, 0, n - 1.001f);
        float fz = Mathf.Clamp(localZ / hRes, 0, n - 1.001f);
        int x0 = (int)fx, z0 = (int)fz;
        float tx = fx - x0, tz = fz - z0;
        float a = Mathf.Lerp(heights[z0, x0], heights[z0, x0 + 1], tx);
        float b = Mathf.Lerp(heights[z0 + 1, x0], heights[z0 + 1, x0 + 1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    /// Land class at tile-local position (nearest cell).
    public byte ClassAt(float localX, float localZ)
    {
        int m = classes.GetLength(0);
        int cx = Mathf.Clamp((int)(localX / cRes), 0, m - 1);
        int cz = Mathf.Clamp((int)(localZ / cRes), 0, m - 1);
        return classes[cz, cx];
    }

    public static TerrainTile Load(string root, TerrainManifest m, TileInfo t)
    {
        string dir = Path.Combine(root, "tiles", t.id);
        var tile = new TerrainTile
        {
            info = t,
            size = (float)m.tileSize,
            hRes = m.height.resolution,
            cRes = m.landClass.resolution,
            heights = TerrainTiles.LoadHeightsMeters(dir, m),
            classes = TerrainTiles.LoadClasses(dir, m),
        };
        return tile;
    }
}

public static class TerrainTiles
{
    public static TerrainManifest LoadManifest(string root) =>
        JsonUtility.FromJson<TerrainManifest>(File.ReadAllText(Path.Combine(root, "manifest.json")));

    /// Normalised 0..1 heights, ready for TerrainData.SetHeights(0, 0, h).
    /// Use terrainData.size = (tileSize, max - min, tileSize) and put the Terrain at y = min.
    public static float[,] LoadHeights01(string tileDir, TerrainManifest m)
    {
        int n = m.height.samples;
        byte[] raw = File.ReadAllBytes(Path.Combine(tileDir, m.height.file));
        var h = new float[n, n];
        for (int z = 0, i = 0; z < n; z++)
            for (int x = 0; x < n; x++, i += 2)
                h[z, x] = (raw[i] | (raw[i + 1] << 8)) / 65535f;
        return h;
    }

    public static float[,] LoadHeightsMeters(string tileDir, TerrainManifest m)
    {
        float[,] h = LoadHeights01(tileDir, m);
        float lo = m.height.min, range = m.height.max - m.height.min;
        int n = m.height.samples;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                h[z, x] = lo + h[z, x] * range;
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

    /// Quick test: build a Unity Terrain for one tile. 'origin' is the projected
    /// coordinate that maps to Unity (0,0,0) — your floating origin.
    public static Terrain CreateTerrain(string root, TerrainManifest m, TileInfo t, double originX, double originY)
    {
        string dir = Path.Combine(root, "tiles", t.id);
        var data = new TerrainData
        {
            heightmapResolution = m.height.samples,
            size = new Vector3((float)m.tileSize, m.height.max - m.height.min, (float)m.tileSize),
        };
        data.SetHeights(0, 0, LoadHeights01(dir, m));
        var go = Terrain.CreateTerrainGameObject(data);
        go.name = "Tile_" + t.id;
        go.transform.position = new Vector3((float)(t.x - originX), m.height.min, (float)(t.y - originY));
        return go.GetComponent<Terrain>();
    }
}
