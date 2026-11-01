using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CirrusFS.Geo
{
    /// Places autogen objects (trees for now) on the terrain by land class and draws them
    /// with GPU instancing.
    ///
    /// Patches (AutogenPlacement, 1/128° square) within a layer's range of the camera are
    /// built nearest first, within a per-frame time budget, and cached. Each patch is one
    /// instanced draw: its instances live in a GPU buffer, relative to the patch centre in
    /// the patch's east/up/north frame, and the patch matrix is rebuilt from ECEF for every
    /// camera, so the floating origin needs no handling here.
    ///
    /// The shader thins objects out between a layer's full-density range and its range (each
    /// object shrinks away at its own distance, so nothing pops), turns each by its random
    /// yaw, and lowers texture resolution with distance.
    [DefaultExecutionOrder(320)]
    [DisallowMultipleComponent]
    public class GlobeAutogen : MonoBehaviour
    {
        public static GlobeAutogen Active { get; private set; }

        [Tooltip("What to place where.")]
        public AutogenSet autogen;

        [Tooltip("Terrain to read land classes and heights from. Defaults to GlobeTerrain.Active.")]
        public GlobeTerrain terrain;

        [Tooltip("Camera the ranges are measured from. Defaults to Camera.main.")]
        public Camera viewCamera;

        [Header("Streaming")]
        [Tooltip("Most time spent building patches per frame, milliseconds.")]
        public float buildBudgetMs = 2f;

        [Tooltip("Patches kept built (nearest are reused when you turn back).")]
        public int cacheSize = 600;

        [Tooltip("Also draw in the editor's Scene view. Off keeps the editor fast in play mode: " +
                 "every view that draws the trees pays their full cost.")]
        public bool drawInSceneView;

        public int DrawnPatchCount { get; private set; }
        public int DrawnInstanceCount { get; private set; }
        public int CachedPatchCount => patches.Count;
        public float AverageBuildMs { get; private set; }

        static readonly int InstancesId = Shader.PropertyToID("_AutogenInstances");
        static readonly int PatchToWorldId = Shader.PropertyToID("_AutogenPatchToWorld");
        static readonly int ModelId = Shader.PropertyToID("_AutogenModel");
        static readonly int RangeId = Shader.PropertyToID("_AutogenRange");
        static readonly int AlbedoId = Shader.PropertyToID("_AutogenAlbedo");

        sealed class LayerRuntime
        {
            public AutogenSet.Layer def;
            public int index;
            public Mesh mesh;
            public Matrix4x4 model;            // mesh -> object space, lowest point at y = 0
            public float radius;               // object bounding radius at scale 1
            public Material material;
            public Texture albedo;
            public bool ownsMaterial;
            public AutogenPlacement.Settings settings;
            public readonly List<Patch> drawn = new List<Patch>();
        }

        sealed class Patch
        {
            public long key;
            public LayerRuntime layer;
            public Double3 centre, north, up;
            public Vector3 boundsCentre;       // patch frame
            public float boundsRadius;
            public int count;
            public GraphicsBuffer buffer;
            public int lastUsedFrame;
        }

        struct Candidate
        {
            public LayerRuntime layer;
            public long row, column, key;
            public double distance;
        }

        readonly List<LayerRuntime> layers = new List<LayerRuntime>();
        readonly Dictionary<long, Patch> patches = new Dictionary<long, Patch>();
        readonly List<Candidate> candidates = new List<Candidate>();
        readonly AutogenPlacement.Result scratch = new AutogenPlacement.Result();
        readonly System.Diagnostics.Stopwatch timer = new System.Diagnostics.Stopwatch();
        readonly List<long> evictScratch = new List<long>();
        MaterialPropertyBlock block;
        AutogenSet subscribed;
        TerrainDataSet layersBuiltFor;
        GlobeTerrain terrainInUse;
        bool dirty = true;

        // ---- lifecycle ------------------------------------------------------------

        void Awake() => block = new MaterialPropertyBlock();

        void OnEnable()
        {
            Active = this;
            RenderPipelineManager.beginCameraRendering += DrawForCamera;
        }

        void OnDisable()
        {
            if (Active == this) Active = null;
            RenderPipelineManager.beginCameraRendering -= DrawForCamera;
            if (subscribed != null) subscribed.Changed -= MarkDirty;
            subscribed = null;
            dirty = true;
        }

        void OnDestroy() => ClearAll();

        void MarkDirty() => dirty = true;

        void ClearAll()
        {
            foreach (Patch p in patches.Values) p.buffer?.Release();
            patches.Clear();
            foreach (LayerRuntime l in layers)
            {
                if (l.albedo != null) Destroy(l.albedo);
                if (l.ownsMaterial && l.material != null) Destroy(l.material);
            }
            layers.Clear();
        }

        // ---- per frame ------------------------------------------------------------

        void LateUpdate()
        {
            GlobeTerrain t = terrain != null ? terrain : GlobeTerrain.Active;
            terrainInUse = t;
            TerrainDataSet data = t != null ? t.Data : null;

            if (subscribed != autogen)
            {
                if (subscribed != null) subscribed.Changed -= MarkDirty;
                subscribed = autogen;
                if (subscribed != null) subscribed.Changed += MarkDirty;
                dirty = true;
            }
            if (data != layersBuiltFor) dirty = true;
            if (dirty)
            {
                ClearAll();
                if (data != null) BuildLayers(data);
                layersBuiltFor = data;
                dirty = false;
            }

            foreach (LayerRuntime l in layers) l.drawn.Clear();
            DrawnPatchCount = 0;
            DrawnInstanceCount = 0;
            if (data == null || layers.Count == 0 || !GeoOrigin.IsSet) return;

            Camera cam = viewCamera != null ? viewCamera : Camera.main;
            if (cam == null) return;

            Double3 camEcef = GeoOrigin.UnityToEcef(cam.transform.position);
            GeoMath.EcefToGeodetic(camEcef, out double camLat, out double camLon, out double camH);
            data.TryGetHeight(camLat, camLon, out float ground);
            double camAbove = Math.Max(0.0, camH - Math.Max(ground, 0f));

            candidates.Clear();
            foreach (LayerRuntime l in layers) Collect(data, l, camLat, camLon, camAbove);

            candidates.Sort((a, b) => a.distance.CompareTo(b.distance));
            timer.Restart();
            foreach (Candidate c in candidates)
            {
                if (timer.Elapsed.TotalMilliseconds > buildBudgetMs) break;
                double start = timer.Elapsed.TotalMilliseconds;
                Patch p = BuildPatch(data, c);
                patches[c.key] = p;
                AverageBuildMs += ((float)(timer.Elapsed.TotalMilliseconds - start) - AverageBuildMs) / 32f;
                if (p.count > 0)
                {
                    c.layer.drawn.Add(p);
                    DrawnPatchCount++;
                    DrawnInstanceCount += p.count;
                }
            }

            Evict();
        }

        /// Patches of layer `l` within its range: built ones go to the draw list, the rest
        /// become build candidates.
        void Collect(TerrainDataSet data, LayerRuntime l, double camLat, double camLon, double camAbove)
        {
            double range = l.def.range + l.radius * l.def.scaleRange.y;
            if (camAbove >= range) return;
            double horizontal = Math.Sqrt(range * range - camAbove * camAbove);

            double cosLat = Math.Max(0.01, Math.Cos(camLat * GeoMath.Deg2Rad));
            const double metresPerDegree = 111320.0;
            double dLat = horizontal / metresPerDegree + AutogenPlacement.PatchSize;
            double dLon = horizontal / (metresPerDegree * cosLat) + AutogenPlacement.PatchSize;
            double halfDiagonal = 0.5 * AutogenPlacement.PatchSize * metresPerDegree * Math.Sqrt(1.0 + cosLat * cosLat);

            long row0 = AutogenPlacement.Row(Math.Max(-89.999, camLat - dLat));
            long row1 = AutogenPlacement.Row(Math.Min(89.999, camLat + dLat));
            long cols = 360L * AutogenPlacement.PatchesPerDegree;
            long colC = AutogenPlacement.Column(camLon);
            long span = Math.Min(cols / 2, (long)Math.Ceiling(dLon * AutogenPlacement.PatchesPerDegree));

            for (long row = row0; row <= row1; row++)
            {
                double latC = AutogenPlacement.SouthOf(row) + 0.5 * AutogenPlacement.PatchSize;
                double dy = (latC - camLat) * metresPerDegree;
                for (long dc = -span; dc <= span; dc++)
                {
                    long column = ((colC + dc) % cols + cols) % cols;
                    double lonC = AutogenPlacement.WestOf(column) + 0.5 * AutogenPlacement.PatchSize;
                    double dx = GeoMath.WrapLongitude(lonC - camLon) * metresPerDegree * cosLat;
                    double flat = Math.Max(0.0, Math.Sqrt(dx * dx + dy * dy) - halfDiagonal);
                    double distance = Math.Sqrt(flat * flat + camAbove * camAbove);
                    if (distance > range) continue;

                    long key = (row * cols + column) * 8 + l.index;
                    if (patches.TryGetValue(key, out Patch p))
                    {
                        p.lastUsedFrame = Time.frameCount;
                        if (p.count == 0) continue;
                        l.drawn.Add(p);
                        DrawnPatchCount++;
                        DrawnInstanceCount += p.count;
                        continue;
                    }

                    AutogenPlacement.TileOf(row, column, out int tLat, out int tLon);
                    if (data.TileAt(tLat, tLon) == null) continue;
                    candidates.Add(new Candidate { layer = l, row = row, column = column, key = key, distance = distance });
                }
            }
        }

        Patch BuildPatch(TerrainDataSet data, Candidate c)
        {
            AutogenPlacement.TileOf(c.row, c.column, out int tLat, out int tLon);
            TerrainTileSource tile = data.TileAt(tLat, tLon);
            data.Touch(tile);
            // stand on the triangles the terrain draws (and collides with) at its finest level
            long meshK = terrainInUse != null ? (1L << terrainInUse.MaxLevelOf(tile)) * terrainInUse.resolution : 0;
            int count = AutogenPlacement.Build(tile, c.row, c.column, c.layer.settings, scratch, meshK);

            var p = new Patch
            {
                key = c.key,
                layer = c.layer,
                centre = scratch.Centre,
                north = scratch.North,
                up = scratch.Up,
                count = count,
                lastUsedFrame = Time.frameCount,
            };
            if (count > 0)
            {
                float grow = c.layer.radius * c.layer.def.scaleRange.y;
                p.boundsCentre = 0.5f * (scratch.Min + scratch.Max) + Vector3.up * (0.5f * grow);
                p.boundsRadius = 0.5f * (scratch.Max - scratch.Min).magnitude + grow;
                p.buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, scratch.Data.Count, 16);
                p.buffer.SetData(scratch.Data);
            }
            return p;
        }

        void Evict()
        {
            if (patches.Count <= cacheSize) return;
            evictScratch.Clear();
            foreach (KeyValuePair<long, Patch> kv in patches)
                if (kv.Value.lastUsedFrame < Time.frameCount) evictScratch.Add(kv.Key);
            evictScratch.Sort((a, b) => patches[a].lastUsedFrame.CompareTo(patches[b].lastUsedFrame));
            for (int i = 0; i < evictScratch.Count && patches.Count > cacheSize; i++)
            {
                patches[evictScratch[i]].buffer?.Release();
                patches.Remove(evictScratch[i]);
            }
        }

        // ---- drawing --------------------------------------------------------------

        void DrawForCamera(ScriptableRenderContext context, Camera cam)
        {
            if (cam.cameraType != CameraType.Game && !(drawInSceneView && cam.cameraType == CameraType.SceneView)) return;
            if (!GeoOrigin.IsSet) return;

            foreach (LayerRuntime l in layers)
            {
                if (l.drawn.Count == 0) continue;
                ShadowCastingMode shadows = l.def.castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                float far = l.def.range, near = Mathf.Min(l.def.fullDensityRange, far - 1f);

                foreach (Patch p in l.drawn)
                {
                    Vector3 position = GeoOrigin.EcefToUnity(p.centre);
                    Quaternion rotation = Quaternion.LookRotation(GeoOrigin.EcefDirToUnity(p.north),
                                                                  GeoOrigin.EcefDirToUnity(p.up));
                    Matrix4x4 patchToWorld = Matrix4x4.TRS(position, rotation, Vector3.one);

                    block.Clear();
                    block.SetBuffer(InstancesId, p.buffer);
                    block.SetMatrix(PatchToWorldId, patchToWorld);
                    block.SetMatrix(ModelId, l.model);
                    block.SetVector(RangeId, new Vector4(near, far, 1f / Mathf.Max(far - near, 1f), 0f));
                    block.SetTexture(AlbedoId, l.albedo);

                    var bounds = new Bounds(patchToWorld.MultiplyPoint3x4(p.boundsCentre), Vector3.one * (2f * p.boundsRadius));
                    Graphics.DrawMeshInstancedProcedural(l.mesh, 0, l.material, bounds, p.count, block,
                                                         shadows, true, gameObject.layer, cam);
                }
            }
        }

        // ---- layers ---------------------------------------------------------------

        void BuildLayers(TerrainDataSet data)
        {
            if (autogen == null || autogen.layers == null) return;
            for (int i = 0; i < autogen.layers.Length && i < 8; i++)
            {
                AutogenSet.Layer def = autogen.layers[i];
                if (def == null || !def.enabled || def.model == null) continue;

                MeshFilter filter = def.model.GetComponentInChildren<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                {
                    Debug.LogWarning($"[GlobeAutogen] Layer {def.name}: model {def.model.name} has no mesh.");
                    continue;
                }

                var mask = new bool[256];
                bool any = false;
                foreach (string name in def.classNames ?? Array.Empty<string>())
                {
                    int id = FindClass(data, name);
                    if (id < 0)
                    {
                        Debug.LogWarning($"[GlobeAutogen] Layer {def.name}: no class named \"{name}\" in the manifest.");
                        continue;
                    }
                    mask[id] = any = true;
                }
                if (!any) continue;

                // keep the model's import transform; put its lowest point on the ground
                Matrix4x4 m = filter.transform.localToWorldMatrix;
                Bounds b = filter.sharedMesh.bounds;
                float minY = float.MaxValue, radius = 0f;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents,
                        new Vector3((c & 1) != 0 ? 1 : -1, (c & 2) != 0 ? 1 : -1, (c & 4) != 0 ? 1 : -1)));
                    minY = Mathf.Min(minY, corner.y);
                    radius = Mathf.Max(radius, corner.magnitude);
                }
                Matrix4x4 model = Matrix4x4.Translate(new Vector3(0f, -minY, 0f)) * m;

                var l = new LayerRuntime
                {
                    def = def,
                    index = i,
                    mesh = filter.sharedMesh,
                    model = model,
                    radius = radius + Mathf.Abs(minY),
                    albedo = BuildTextureArray(def.variants, out int variants),
                };
                l.material = def.material;
                if (l.material == null)
                {
                    l.material = new Material(Shader.Find("CirrusFS/AutogenFoliage")) { hideFlags = HideFlags.DontSave };
                    l.ownsMaterial = true;
                }
                l.settings = new AutogenPlacement.Settings
                {
                    densityPerHectare = def.densityPerHectare,
                    classMask = mask,
                    minScale = def.scaleRange.x,
                    maxScale = def.scaleRange.y,
                    randomYaw = def.randomYaw,
                    variants = variants,
                    tintVariation = def.tintVariation,
                    sink = def.sink,
                    seed = (uint)(i + 1) * 2654435761u ^ (uint)StableHash(def.name),
                };
                layers.Add(l);
            }
        }

        static int FindClass(TerrainDataSet data, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            string wanted = name.Trim();
            foreach (TerrainClassDef c in data.Classes)
                if (string.Equals(c.name?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return c.id;
            return -1;
        }

        static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char ch in s ?? "") h = h * 31 + ch;
                return h;
            }
        }

        /// One texture array of the variants. Same size, format and mip count are copied on
        /// the GPU and stay compressed; otherwise they are resampled into an sRGB array.
        static Texture BuildTextureArray(Texture2D[] variants, out int count)
        {
            var list = new List<Texture2D>();
            foreach (Texture2D t in variants ?? Array.Empty<Texture2D>()) if (t != null) list.Add(t);
            count = Math.Max(1, list.Count);

            if (list.Count == 0)
            {
                var white = new Texture2DArray(4, 4, 1, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.DontSave };
                var pixels = new Color32[16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
                white.SetPixels32(pixels, 0);
                white.Apply(false, true);
                return white;
            }

            Texture2D first = list[0];
            bool same = true;
            foreach (Texture2D t in list)
                same &= t.width == first.width && t.height == first.height &&
                        t.graphicsFormat == first.graphicsFormat && t.mipmapCount == first.mipmapCount;

            if (same)
            {
                var array = new Texture2DArray(first.width, first.height, list.Count, first.graphicsFormat,
                                               first.mipmapCount > 1 ? TextureCreationFlags.MipChain : TextureCreationFlags.None,
                                               first.mipmapCount)
                {
                    name = "Autogen albedo",
                    hideFlags = HideFlags.DontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                };
                for (int i = 0; i < list.Count; i++) Graphics.CopyTexture(list[i], 0, array, i);
                return array;
            }

            Debug.LogWarning("[GlobeAutogen] Texture variants differ in size or format; resampling them uncompressed.");
            int size = Mathf.ClosestPowerOfTwo(Mathf.Max(first.width, first.height));
            var desc = new RenderTextureDescriptor(size, size, GraphicsFormat.R8G8B8A8_SRGB, 0, 1 + (int)Math.Round(Math.Log(size, 2)))
            {
                dimension = TextureDimension.Tex2DArray,
                volumeDepth = list.Count,
                useMipMap = true,
                autoGenerateMips = false,
            };
            var rt = new RenderTexture(desc)
            {
                name = "Autogen albedo",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };
            rt.Create();
            for (int i = 0; i < list.Count; i++) Graphics.Blit(list[i], rt, 0, i);
            rt.GenerateMips();
            return rt;
        }
    }
}
