using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CirrusFS.Geo
{
    /// The WGS84 ellipsoid surface at h = 0, drawn as an infinite ocean.
    ///
    /// Structure: a cube-sphere quadtree. Six root faces, each split into four children
    /// wherever its curvature or float-precision error would be visible from the camera (see
    /// WantsSplit): chunks of a few km under a camera at the water, whole faces from orbit.
    /// Nodes behind the horizon are skipped.
    ///
    /// Precision: every vertex is computed in double ECEF, then stored as a float offset from
    /// its own chunk centre in that chunk's local east/up/north frame. The chunk's transform is
    /// placed through GeoOrigin (centre -> Unity, local frame -> rotation), so the only large
    /// float in the pipeline is a far chunk's position, where a metre is sub-pixel. There is
    /// never one planet-sized mesh in float.
    ///
    /// Seams: neighbouring chunks of different levels share their corner vertices exactly
    /// (same cube coordinate, same double maths), but a coarse edge is a straight chord under
    /// the curved surface while its finer neighbour follows the curve. Every chunk therefore
    /// carries a skirt - a strip hanging down from its border, deep enough to cover the chord
    /// sag of a neighbour two levels coarser - so no gap can open between levels. A bigger
    /// jump than that is possible, but the split test already holds every chunk's sag under
    /// maxAngularError, so any gap it left would be a fraction of a pixel.
    ///
    /// The floating origin does not move the chunks as plain transforms; on every re-centre
    /// they are re-placed from their stored ECEF centres, so no error accumulates.
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public class OceanSphere : MonoBehaviour
    {
        [Tooltip("Ocean material (CirrusFS/Ocean shader).")]
        public Material material;

        [Tooltip("Camera the LOD follows. Defaults to Camera.main.")]
        public Camera viewCamera;

        [Header("Level of detail")]
        [Tooltip("Quads along each chunk edge.")]
        [Range(8, 64)] public int resolution = 32;

        [Tooltip("Largest geometric error a chunk may show, as an angle seen from the camera " +
                 "(radians). 3e-4 is about a third of a pixel at 1080p and 60 deg FOV.")]
        public float maxAngularError = 3e-4f;

        [Tooltip("Deepest quadtree level, a safety cap. 18 is ~20 m chunks; the error test " +
                 "rarely needs more than ~12 even at the water.")]
        [Range(4, 22)] public int maxLevel = 18;

        [Tooltip("Chunk meshes built per frame at most, so a fast zoom spreads its cost.")]
        public int buildBudgetPerFrame = 16;

        [Tooltip("Chunks kept in memory after they leave the view.")]
        public int cacheSize = 900;

        [Header("Rendering")]
        public ShadowCastingMode castShadows = ShadowCastingMode.Off;
        public bool receiveShadows = true;

        [Header("Wave scroll")]
        [Tooltip("Drift of the small wave map, m/s, in ECEF axes (only the direction matters " +
                 "per projection plane).")]
        public Vector3 scrollA = new Vector3(0.9f, 0.5f, -0.35f);

        [Tooltip("Drift of the swell map, m/s, in ECEF axes. A different direction from A so " +
                 "the two layers slide across each other.")]
        public Vector3 scrollB = new Vector3(-0.45f, 0.8f, 0.5f);

        static readonly int UnityToEcefId = Shader.PropertyToID("_OceanUnityToEcef");
        static readonly int UvOriginAId = Shader.PropertyToID("_OceanUvOriginA");
        static readonly int UvOriginBId = Shader.PropertyToID("_OceanUvOriginB");
        static readonly int CameraHeightId = Shader.PropertyToID("_OceanCameraHeight");
        static readonly int ScaleAId = Shader.PropertyToID("_ScaleA");
        static readonly int ScaleBId = Shader.PropertyToID("_ScaleB");

        double scrollTime;

        /// Chunks drawn last frame.
        public int VisibleChunkCount { get; private set; }

        /// Deepest level drawn last frame.
        public int DeepestVisibleLevel { get; private set; }

        public int CachedChunkCount => chunks.Count;

        // Face frames in ECEF: outward normal and the two in-face axes.
        static readonly Double3[] FaceNormal =
        {
            new Double3(1, 0, 0), new Double3(-1, 0, 0),
            new Double3(0, 1, 0), new Double3(0, -1, 0),
            new Double3(0, 0, 1), new Double3(0, 0, -1),
        };

        static readonly Double3[] FaceU =
        {
            new Double3(0, 1, 0), new Double3(0, -1, 0),
            new Double3(-1, 0, 0), new Double3(1, 0, 0),
            new Double3(0, 1, 0), new Double3(0, 1, 0),
        };

        static readonly Double3[] FaceV =
        {
            new Double3(0, 0, 1), new Double3(0, 0, 1),
            new Double3(0, 0, 1), new Double3(0, 0, 1),
            new Double3(-1, 0, 0), new Double3(1, 0, 0),
        };

        sealed class Chunk
        {
            public long key;
            public int level;
            public Double3 centre;      // ECEF, on the ellipsoid
            public Double3 north, up;   // local frame at the centre
            public double radius;       // bounding radius around the centre
            public GameObject go;
            public MeshRenderer renderer;
            public Mesh mesh;
            public int lastUsedFrame;
            public bool shown;
        }

        readonly Dictionary<long, Chunk> chunks = new Dictionary<long, Chunk>();
        readonly List<Chunk> shownLastFrame = new List<Chunk>();
        readonly List<Chunk> shownThisFrame = new List<Chunk>();
        readonly List<long> evictScratch = new List<long>();

        Double3 cameraEcef;
        double cameraDistance;
        double horizonCos;
        int buildsThisFrame;

        // Shared scratch buffers for mesh building.
        Vector3[] vertexBuffer;
        Vector3[] normalBuffer;
        int[] indexBuffer;

        void OnEnable()
        {
            GeoOrigin.Recentered += HandleRecentered;
            GeoOrigin.OriginReset += HandleOriginReset;
        }

        void OnDisable()
        {
            GeoOrigin.Recentered -= HandleRecentered;
            GeoOrigin.OriginReset -= HandleOriginReset;
        }

        void OnDestroy()
        {
            foreach (Chunk chunk in chunks.Values) DestroyChunk(chunk);
            chunks.Clear();
        }

        void HandleRecentered(GeoFrameChange change) => PlaceAllChunks();

        void HandleOriginReset() => PlaceAllChunks();

        void PlaceAllChunks()
        {
            foreach (Chunk chunk in chunks.Values) PlaceChunk(chunk);
        }

        void PlaceChunk(Chunk chunk)
        {
            chunk.go.transform.SetPositionAndRotation(
                GeoOrigin.EcefToUnity(chunk.centre),
                Quaternion.LookRotation(GeoOrigin.EcefDirToUnity(chunk.north), GeoOrigin.EcefDirToUnity(chunk.up)));
        }

        // ---- per frame -----------------------------------------------------------

        void LateUpdate()
        {
            if (material == null || !GeoOrigin.IsSet) return;

            Camera cam = viewCamera != null ? viewCamera : Camera.main;
            if (cam == null) return;

            cameraEcef = GeoOrigin.UnityToEcef(cam.transform.position);
            cameraDistance = cameraEcef.Magnitude;

            // The horizon cone uses the surface radius right under the camera, so it is tight
            // near the water; AboveHorizon adds each node's own angular size, plus a margin for
            // the ellipsoid curving away more slowly toward the equator.
            double surfaceRadius = GeoMath.SurfacePointBelow(cameraEcef).Magnitude;
            horizonCos = cameraDistance > surfaceRadius ? surfaceRadius / cameraDistance : -1.0;

            buildsThisFrame = 0;
            shownThisFrame.Clear();
            DeepestVisibleLevel = 0;

            for (int face = 0; face < 6; face++) Traverse(face, 0, 0, 0);

            // Hide what was shown last frame and is not any more.
            for (int i = 0; i < shownLastFrame.Count; i++)
            {
                Chunk chunk = shownLastFrame[i];
                if (chunk.lastUsedFrame != Time.frameCount && chunk.shown)
                {
                    chunk.renderer.enabled = false;
                    chunk.shown = false;
                }
            }

            shownLastFrame.Clear();
            shownLastFrame.AddRange(shownThisFrame);
            VisibleChunkCount = shownThisFrame.Count;

            Evict();
            UpdateShaderGlobals();
        }

        /// The shader projects the wave maps in ECEF axes. It gets the Unity-to-ECEF rotation,
        /// and the origin's ECEF position plus the accumulated scroll, wrapped to one tile in
        /// double here, so its own arithmetic only ever sees small numbers.
        void UpdateShaderGlobals()
        {
            var toEcef = Matrix4x4.identity;
            toEcef.SetColumn(0, ToVector4(GeoOrigin.East));
            toEcef.SetColumn(1, ToVector4(GeoOrigin.Up));
            toEcef.SetColumn(2, ToVector4(GeoOrigin.North));
            Shader.SetGlobalMatrix(UnityToEcefId, toEcef);

            scrollTime += Time.deltaTime;

            double scaleA = material.HasProperty(ScaleAId) ? material.GetFloat(ScaleAId) : 46.0;
            double scaleB = material.HasProperty(ScaleBId) ? material.GetFloat(ScaleBId) : 211.0;

            Shader.SetGlobalVector(UvOriginAId, WrappedTiles(GeoOrigin.Ecef, scrollA, scaleA));
            Shader.SetGlobalVector(UvOriginBId, WrappedTiles(GeoOrigin.Ecef, scrollB, scaleB));
            GeoMath.EcefToGeodetic(cameraEcef, out _, out _, out double cameraHeight);
            Shader.SetGlobalFloat(CameraHeightId, (float)Math.Max(0.0, cameraHeight));
        }

        Vector4 WrappedTiles(Double3 origin, Vector3 scroll, double scale)
        {
            scale = Math.Max(scale, 0.01);
            return new Vector4((float)Frac((origin.x + scroll.x * scrollTime) / scale),
                               (float)Frac((origin.y + scroll.y * scrollTime) / scale),
                               (float)Frac((origin.z + scroll.z * scrollTime) / scale),
                               0f);
        }

        static double Frac(double v) => v - Math.Floor(v);

        static Vector4 ToVector4(Double3 v) => new Vector4((float)v.x, (float)v.y, (float)v.z, 0f);

        void Traverse(int face, int level, int x, int y)
        {
            Chunk node = GetOrBuild(face, level, x, y, force: level == 0);
            if (node == null) return;

            if (!AboveHorizon(node)) return;

            if (level < maxLevel && WantsSplit(node))
            {
                // Only descend when all four children are ready, or the parent would vanish
                // for a frame while they build.
                bool ready = true;
                for (int i = 0; i < 4 && ready; i++)
                {
                    if (GetOrBuild(face, level + 1, x * 2 + (i & 1), y * 2 + (i >> 1), force: false) == null) ready = false;
                }

                if (ready)
                {
                    for (int i = 0; i < 4; i++) Traverse(face, level + 1, x * 2 + (i & 1), y * 2 + (i >> 1));
                    return;
                }
            }

            Show(node);
        }

        /// The surface is a smooth ellipsoid, so a chunk only has to be finer when one of its
        /// two errors would show from where the camera is:
        ///  - curvature: each quad is a flat chord under the curved surface, sagging by
        ///    L^2 / 8R for a quad of length L;
        ///  - precision: vertices are floats relative to the chunk centre, good to about
        ///    1.2e-7 of the chunk's size.
        /// Both are compared, as angles, against maxAngularError at the chunk's nearest
        /// distance. Near the water this resolves to chunks of a few km, from orbit to whole
        /// cube faces, with only a handful of chunks per level in between.
        bool WantsSplit(Chunk node)
        {
            double d = Math.Max(Double3.Distance(cameraEcef, node.centre) - node.radius, 0.5);
            double quad = 2.0 * node.radius / resolution;
            double sag = quad * quad / (8.0 * GeoMath.B);
            double precision = node.radius * 1.2e-7;

            return Math.Max(sag, precision) / d > maxAngularError;
        }

        /// Conservative horizon cull: the node's angular extent seen from the Earth's centre,
        /// against the angle to the camera's horizon.
        bool AboveHorizon(Chunk node)
        {
            if (horizonCos < 0.0) return true;

            double cosToNode = Double3.Dot(node.centre.Normalized, cameraEcef / cameraDistance);
            double angleToNode = Math.Acos(Math.Max(-1.0, Math.Min(1.0, cosToNode)));
            // 0.2 deg (~22 km) of slack covers the ellipsoid's departure from the sphere used here.
            double horizonAngle = Math.Acos(horizonCos) + 0.0035;
            double nodeAngle = node.radius / GeoMath.B;

            return angleToNode - nodeAngle < horizonAngle;
        }

        void Show(Chunk node)
        {
            node.lastUsedFrame = Time.frameCount;
            if (!node.shown)
            {
                node.renderer.enabled = true;
                node.shown = true;
            }

            shownThisFrame.Add(node);
            if (node.level > DeepestVisibleLevel) DeepestVisibleLevel = node.level;
        }

        // ---- cache ---------------------------------------------------------------

        static long Key(int face, int level, int x, int y) =>
            ((long)face << 61) | ((long)level << 55) | ((long)x << 27) | (long)y;

        Chunk GetOrBuild(int face, int level, int x, int y, bool force)
        {
            long key = Key(face, level, x, y);
            if (chunks.TryGetValue(key, out Chunk chunk))
            {
                chunk.lastUsedFrame = Math.Max(chunk.lastUsedFrame, Time.frameCount - 1);
                return chunk;
            }

            if (!force && buildsThisFrame >= buildBudgetPerFrame) return null;

            buildsThisFrame++;
            chunk = Build(face, level, x, y);
            chunk.key = key;
            chunks.Add(key, chunk);
            return chunk;
        }

        void Evict()
        {
            if (chunks.Count <= cacheSize) return;

            evictScratch.Clear();
            int cutoff = Time.frameCount - 2;
            foreach (Chunk chunk in chunks.Values)
            {
                if (chunk.level > 0 && chunk.lastUsedFrame < cutoff && !chunk.shown) evictScratch.Add(chunk.key);
            }

            evictScratch.Sort((a, b) => chunks[a].lastUsedFrame.CompareTo(chunks[b].lastUsedFrame));

            int excess = chunks.Count - cacheSize;
            for (int i = 0; i < evictScratch.Count && i < excess; i++)
            {
                DestroyChunk(chunks[evictScratch[i]]);
                chunks.Remove(evictScratch[i]);
            }
        }

        static void DestroyChunk(Chunk chunk)
        {
            if (chunk.mesh != null) Destroy(chunk.mesh);
            if (chunk.go != null) Destroy(chunk.go);
        }

        // ---- building ------------------------------------------------------------

        /// Equal-angle cube-to-sphere warp: spreads vertices evenly instead of bunching them at
        /// face centres.
        static double Warp(double s) => Math.Tan(s * Math.PI * 0.25);

        static Double3 SurfacePoint(int face, double s, double t)
        {
            Double3 dir = FaceNormal[face] + FaceU[face] * Warp(s) + FaceV[face] * Warp(t);
            return GeoMath.EllipsoidPointAlong(dir);
        }

        Chunk Build(int face, int level, int x, int y)
        {
            int n = resolution;
            double size = 2.0 / (1 << level);
            double s0 = -1.0 + x * size;
            double t0 = -1.0 + y * size;

            Double3 centre = SurfacePoint(face, s0 + size * 0.5, t0 + size * 0.5);
            GeoMath.EcefToGeodetic(centre, out double lat, out double lon, out _);
            GeoMath.EnuBasis(lat, lon, out Double3 east, out Double3 north, out Double3 up);

            int gridCount = (n + 1) * (n + 1);
            int skirtCount = 4 * n;
            int vertexCount = gridCount + skirtCount;
            int indexCount = n * n * 6 + skirtCount * 6;

            if (vertexBuffer == null || vertexBuffer.Length != vertexCount)
            {
                vertexBuffer = new Vector3[vertexCount];
                normalBuffer = new Vector3[vertexCount];
                indexBuffer = new int[indexCount];
            }

            double radius = 0.0;
            double maxChord = 0.0;

            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++)
                {
                    Double3 p = SurfacePoint(face, s0 + size * i / n, t0 + size * j / n);
                    Double3 d = p - centre;
                    Double3 normal = GeoMath.EllipsoidNormalAt(p);

                    int v = j * (n + 1) + i;
                    vertexBuffer[v] = ToLocal(d, east, north, up);
                    normalBuffer[v] = ToLocal(normal, east, north, up);

                    radius = Math.Max(radius, d.Magnitude);
                    if (i > 0) maxChord = Math.Max(maxChord, (vertexBuffer[v] - vertexBuffer[v - 1]).magnitude);
                }
            }

            // A neighbour two levels coarser has chords four times as long, so sixteen times the
            // sag; hang the skirt below that.
            double sag = maxChord * maxChord / (8.0 * GeoMath.B);
            float skirtDepth = (float)(16.0 * sag + 1.0);

            int index = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i;
                    int b = a + 1;
                    int c = a + (n + 1);
                    int d = c + 1;

                    // Clockwise seen from above (Unity's front face), in the local E/U/N frame.
                    indexBuffer[index++] = a; indexBuffer[index++] = c; indexBuffer[index++] = b;
                    indexBuffer[index++] = b; indexBuffer[index++] = c; indexBuffer[index++] = d;
                }
            }

            // Skirt: walk the border once, anticlockwise in (i, j), and drop a copy of each
            // border vertex. Two triangles per border segment, double-sided by winding both ways
            // would be wasteful; one side faces outward, which is the side a gap shows.
            int skirtStart = gridCount;
            int k = 0;
            int[] border = BorderLoop(n);
            for (k = 0; k < skirtCount; k++)
            {
                int src = border[k];
                Vector3 normal = normalBuffer[src];
                vertexBuffer[skirtStart + k] = vertexBuffer[src] - normal * skirtDepth;
                normalBuffer[skirtStart + k] = normal;
            }

            for (k = 0; k < skirtCount; k++)
            {
                int top0 = border[k];
                int top1 = border[(k + 1) % skirtCount];
                int bot0 = skirtStart + k;
                int bot1 = skirtStart + (k + 1) % skirtCount;

                indexBuffer[index++] = top0; indexBuffer[index++] = top1; indexBuffer[index++] = bot0;
                indexBuffer[index++] = top1; indexBuffer[index++] = bot1; indexBuffer[index++] = bot0;
            }

            var mesh = new Mesh { name = $"Ocean F{face} L{level} ({x},{y})" };
            mesh.SetVertices(vertexBuffer, 0, vertexCount);
            mesh.SetNormals(normalBuffer, 0, vertexCount);
            mesh.SetIndices(indexBuffer, 0, indexCount, MeshTopology.Triangles, 0, true);
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);

            var go = new GameObject(mesh.name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.layer = gameObject.layer;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = castShadows;
            renderer.receiveShadows = receiveShadows;
            renderer.enabled = false;

            var chunk = new Chunk
            {
                level = level,
                centre = centre,
                north = north,
                up = up,
                radius = radius,
                go = go,
                renderer = renderer,
                mesh = mesh,
                lastUsedFrame = Time.frameCount,
            };

            PlaceChunk(chunk);
            return chunk;
        }

        static Vector3 ToLocal(Double3 v, Double3 east, Double3 north, Double3 up) =>
            new Vector3((float)Double3.Dot(v, east), (float)Double3.Dot(v, up), (float)Double3.Dot(v, north));

        static readonly Dictionary<int, int[]> borderCache = new Dictionary<int, int[]>();

        /// Grid indices of the border, as one closed loop: bottom row, right column, top row
        /// reversed, left column reversed.
        static int[] BorderLoop(int n)
        {
            if (borderCache.TryGetValue(n, out int[] loop)) return loop;

            loop = new int[4 * n];
            int k = 0;
            for (int i = 0; i < n; i++) loop[k++] = i;                               // j = 0
            for (int j = 0; j < n; j++) loop[k++] = j * (n + 1) + n;                 // i = n
            for (int i = n; i > 0; i--) loop[k++] = n * (n + 1) + i;                 // j = n
            for (int j = n; j > 0; j--) loop[k++] = j * (n + 1);                     // i = 0

            borderCache.Add(n, loop);
            return loop;
        }
    }
}
