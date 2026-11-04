using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// Collision for the terrain under a target (the aircraft): MeshColliders for the 3 x 3
    /// finest-level chunks around it, built with the same TerrainChunkBuilder code as the
    /// rendered chunks, so the gear rolls on exactly the surface that is drawn up close.
    ///
    /// Independent of the render LOD: with the camera zoomed out to orbit the drawn ground
    /// is coarse, but the aircraft still lands on full-resolution ground. Pieces re-place
    /// themselves from ECEF on every re-centre; the root must carry FloatingOriginIgnore.
    /// Over the sea the terrain lies below the water patch (WaterSurfaceCollider), whose hit
    /// is nearer, so the two do not interfere.
    [DefaultExecutionOrder(-890)]
    [DisallowMultipleComponent]
    public class TerrainCollision : MonoBehaviour
    {
        [Tooltip("What the collision follows. Set at runtime by whoever spawns the aircraft.")]
        public Transform target;

        [Tooltip("Terrain to collide with. Defaults to GlobeTerrain.Active.")]
        public GlobeTerrain terrain;

        [Tooltip("Colliders exist only while the target is less than this above the ground, metres.")]
        public float activeBelowHeightAboveGround = 1500f;

        [Tooltip("Physics material for the ground (optional).")]
        public PhysicsMaterial surfaceMaterial;

        sealed class Piece
        {
            public Double3 centre, north, up;
            public GameObject go;
            public Mesh mesh;
            public bool wanted;
        }

        readonly Dictionary<long, Piece> pieces = new Dictionary<long, Piece>();
        readonly TerrainChunkData buildData = new TerrainChunkData();
        readonly List<long> removeScratch = new List<long>();

        public int PieceCount => pieces.Count;

        void OnEnable()
        {
            GeoOrigin.Recentered += HandleRecentered;
            GeoOrigin.OriginReset += HandleOriginReset;
        }

        void OnDisable()
        {
            GeoOrigin.Recentered -= HandleRecentered;
            GeoOrigin.OriginReset -= HandleOriginReset;
            Clear();
        }

        void HandleRecentered(GeoFrameChange change)
        {
            foreach (Piece p in pieces.Values) Place(p);
        }

        void HandleOriginReset()
        {
            foreach (Piece p in pieces.Values) Place(p);
        }

        // Runs ahead of the gear in FixedUpdate, so new ground is in place before it raycasts.
        void FixedUpdate()
        {
            GlobeTerrain t = terrain != null ? terrain : GlobeTerrain.Active;
            if (target == null || t == null || t.Data == null || !GeoOrigin.IsSet)
            {
                Clear();
                return;
            }

            GeoMath.EcefToGeodetic(GeoOrigin.UnityToEcef(target.position), out double lat, out double lon, out double h);
            TerrainDataSet data = t.Data;
            if (!data.TryGetHeight(lat, lon, out float ground)) ground = 0f;
            if (h - ground > activeBelowHeightAboveGround)
            {
                Clear();
                return;
            }

            foreach (Piece p in pieces.Values) p.wanted = false;

            bool changed = false;
            TerrainTileSource here = data.TileContaining(lat, lon);
            double size = here != null ? 1.0 / (1 << t.MaxLevelOf(here)) : 1.0 / 64;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    double pLat = lat + dy * size, pLon = lon + dx * size;
                    TerrainTileSource tile = data.TileContaining(pLat, pLon);
                    if (tile == null) continue;

                    int level = t.MaxLevelOf(tile);
                    int count = 1 << level;
                    int x = Math.Max(0, Math.Min(count - 1, (int)Math.Floor((pLon - tile.Lon) * count)));
                    int y = Math.Max(0, Math.Min(count - 1, (int)Math.Floor((pLat - tile.Lat) * count)));
                    long key = ((long)TerrainDataSet.Key(tile.Lat, tile.Lon) << 40) | ((long)level << 32) | ((long)x << 16) | (long)y;

                    if (!pieces.TryGetValue(key, out Piece piece))
                    {
                        piece = Build(t, tile, level, x, y);
                        pieces.Add(key, piece);
                        changed = true;
                    }
                    piece.wanted = true;
                }
            }

            removeScratch.Clear();
            foreach (KeyValuePair<long, Piece> kv in pieces) if (!kv.Value.wanted) removeScratch.Add(kv.Key);
            foreach (long key in removeScratch)
            {
                Destroy(pieces[key]);
                pieces.Remove(key);
                changed = true;
            }

            if (changed) Physics.SyncTransforms();
        }

        Piece Build(GlobeTerrain t, TerrainTileSource tile, int level, int x, int y)
        {
            TerrainChunkData d = buildData;
            TerrainChunkBuilder.Build(t.Data, tile, level, x, y, t.resolution, t.classTexels, false, d);

            var mesh = new Mesh { name = $"Terrain collision {tile.Id} L{level} ({x},{y})" };
            mesh.SetVertices(d.Vertices, 0, d.VertexCount);
            mesh.SetIndices(d.Indices, 0, d.IndexCount, MeshTopology.Triangles, 0, true);
            mesh.RecalculateBounds();

            var go = new GameObject(mesh.name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;
            var col = go.AddComponent<MeshCollider>();
            col.sharedMaterial = surfaceMaterial;

            var piece = new Piece { centre = d.Centre, north = d.North, up = d.Up, go = go, mesh = mesh };
            Place(piece);
            col.sharedMesh = mesh;
            return piece;
        }

        static void Place(Piece p)
        {
            p.go.transform.SetPositionAndRotation(
                GeoOrigin.EcefToUnity(p.centre),
                Quaternion.LookRotation(GeoOrigin.EcefDirToUnity(p.north), GeoOrigin.EcefDirToUnity(p.up)));
        }

        static void Destroy(Piece p)
        {
            if (p.go != null) UnityEngine.Object.Destroy(p.go);
            if (p.mesh != null) UnityEngine.Object.Destroy(p.mesh);
        }

        /// Drops the colliders; they are rebuilt in the next physics step (terrain heights
        /// changed, e.g. airport height patches).
        public void Rebuild() => Clear();

        void Clear()
        {
            if (pieces.Count == 0) return;
            foreach (Piece p in pieces.Values) Destroy(p);
            pieces.Clear();
        }
    }
}
