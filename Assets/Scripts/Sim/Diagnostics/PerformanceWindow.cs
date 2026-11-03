using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CirrusFS.Geo;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;

namespace CirrusFS.Sim
{
    /// Performance readout, toggled with F3. Four tabs:
    ///
    /// - Frame: FPS, CPU main / render thread and GPU times (FrameTimingManager), which one
    ///   bounds the frame, a frame-time graph, and the renderer's draw counters.
    /// - Scripts: CPU time and GC allocation per script, from the Unity profiler's frame data
    ///   (ScriptProfiler; editor only), and the player loop's stages.
    /// - GPU: GPU time per render pass, from every command-buffer sample that records GPU time
    ///   (URP's passes, the time-of-day passes, clouds), found at run time.
    /// - Memory: graphics memory against the card's VRAM, what the terrain and autogen hold on
    ///   the GPU, and an on-demand list of the largest textures and meshes.
    ///
    /// Times are averaged over half a second; "max" is the worst frame in that window. While
    /// hidden it costs nothing: recorders are off and profiler recording is stopped.
    [DefaultExecutionOrder(400)]
    public class PerformanceWindow : MonoBehaviour
    {
        public Key toggleKey = Key.F3;
        public bool visible;

        public enum Tab { Frame, Scripts, Gpu, Memory }
        [Tooltip("Tab shown.")]
        public Tab tab;

        [Header("Layout")]
        public Rect windowRect = new Rect(20f, 80f, 540f, 600f);

        [Tooltip("Rows in the script and GPU pass lists.")]
        [Range(5, 40)] public int listRows = 18;

        [Tooltip("Frame-time graph scale, milliseconds at the top.")]
        public float graphMaxMs = 50f;

        static readonly string[] TabNames = { "Frame", "Scripts", "GPU", "Memory" };
        bool scriptsByClass;

        const int WindowId = 0x6E3;
        const float WindowSeconds = 0.5f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ---- frame timing ---------------------------------------------------------

        readonly FrameTiming[] timings = new FrameTiming[1];
        double sumFrame, sumMain, sumRender, sumGpu, sumPresent, maxFrame, maxMain, maxGpu;
        int timingFrames, gpuFrames, frameCount;
        float windowStart;
        double avgFrame, avgMain, avgRender, avgGpu, avgPresent, pubMaxFrame, pubMaxMain, pubMaxGpu;
        bool hasFrameTimings, hasGpuTime;

        // ---- counters -------------------------------------------------------------

        struct Counter
        {
            public ProfilerCategory category;
            public string name;
            public ProfilerRecorder recorder;
            public long Value => recorder.Valid ? recorder.LastValue : -1;
        }

        enum C
        {
            Batches, DrawCalls, SetPass, Triangles, Vertices, ShadowCasters,
            GfxUsed, GfxReserved, TextureMemory, TextureCount, MeshMemory, MeshCount, RenderTextures,
            Buffers, VideoMemory, TotalUsed, TotalReserved, GcUsed, GcAllocFrame, AppResident,
        }

        Counter[] counters;

        // ---- scripts and GPU passes -------------------------------------------------

        readonly ScriptProfiler scripts = new ScriptProfiler();
        readonly List<ScriptProfiler.Entry> methodEntries = new List<ScriptProfiler.Entry>();
        readonly List<ScriptProfiler.Entry> classEntries = new List<ScriptProfiler.Entry>();
        readonly List<ScriptProfiler.Entry> stageEntries = new List<ScriptProfiler.Entry>();
        int scriptFrames;

        sealed class GpuPass
        {
            public string name;
            public Recorder recorder;
            public double gpuSum, cpuSum, gpuMax;
        }

        // Samples that only wrap the others (the whole frame, the camera stack).
        static readonly HashSet<string> GpuContainers = new HashSet<string>
        {
            "FrameTime.GPU", "UniversalRenderTotal", "RenderCameraStack", "Camera.Render", "RecordRenderGraph",
            "ExecuteRenderGraph",
        };

        // URP's per-camera sample; shown as "camera: <name>".
        const string CameraPrefix = "UniversalRenderPipeline.RenderSingleCameraInternal: ";

        readonly List<GpuPass> gpuPasses = new List<GpuPass>();
        readonly HashSet<string> gpuNames = new HashSet<string>();
        readonly List<ProfilerRecorderHandle> handleScratch = new List<ProfilerRecorderHandle>();
        readonly List<GpuPass> gpuSorted = new List<GpuPass>();
        int lastHandleCount;
        float nextDiscovery;
        double cameraGpu;     // ms per frame, all cameras' passes

        // ---- published rows (rebuilt twice a second; OnGUI only draws them) ---------

        struct Row
        {
            public string name, a, b, c;
            public float fraction;
            public Color colour;
        }

        readonly List<Row> scriptRows = new List<Row>();
        readonly List<Row> stageRows = new List<Row>();
        readonly List<Row> gpuRows = new List<Row>();
        readonly List<Row> assetRows = new List<Row>();
        string frameText = "", scriptsHeader = "", gpuHeader = "", memoryText = "", terrainText = "", assetsHeader = "";
        float vramFraction, terrainFraction;
        Color terrainColour = Color.white;
        readonly StringBuilder sb = new StringBuilder(2048);

        // terrain build rate
        int lastBuildCount = -1;
        float lastBuildTime;

        // ---- drawing --------------------------------------------------------------

        const int GraphWidth = 256, GraphHeight = 72;
        Texture2D graph, whitePixel;
        readonly Color32[] column = new Color32[GraphHeight];
        int graphColumn;
        GUIStyle labelStyle, smallStyle, headerStyle;
        bool placed;

        static readonly Color CpuColour = new Color(0.35f, 0.85f, 1f);
        static readonly Color GpuColour = new Color(1f, 0.6f, 0.2f);
        static readonly Color MemColour = new Color(0.55f, 0.9f, 0.45f);
        static readonly Color WarnColour = new Color(1f, 0.8f, 0.3f);
        static readonly Color BadColour = new Color(1f, 0.4f, 0.35f);

        // ---- lifecycle ------------------------------------------------------------

        void Awake()
        {
            whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            whitePixel.SetPixel(0, 0, Color.white);
            whitePixel.Apply();

            graph = new Texture2D(GraphWidth, GraphHeight, TextureFormat.RGBA32, false)
            {
                name = "Performance graph",
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Point,
            };
            var clear = new Color32[GraphWidth * GraphHeight];
            for (int i = 0; i < clear.Length; i++) clear[i] = new Color32(8, 14, 24, 220);
            graph.SetPixels32(clear);
            graph.Apply(false);
        }

        void OnEnable()
        {
            if (visible) Begin();
        }

        void OnDisable() => End();

        void OnDestroy()
        {
            End();
            if (whitePixel != null) Destroy(whitePixel);
            if (graph != null) Destroy(graph);
        }

        bool running;

        void Begin()
        {
            if (running) return;
            running = true;

            counters = new Counter[Enum.GetValues(typeof(C)).Length];
            Set(C.Batches, ProfilerCategory.Render, "Batches Count");
            Set(C.DrawCalls, ProfilerCategory.Render, "Draw Calls Count");
            Set(C.SetPass, ProfilerCategory.Render, "SetPass Calls Count");
            Set(C.Triangles, ProfilerCategory.Render, "Triangles Count");
            Set(C.Vertices, ProfilerCategory.Render, "Vertices Count");
            Set(C.ShadowCasters, ProfilerCategory.Render, "Shadow Casters Count");
            Set(C.GfxUsed, ProfilerCategory.Memory, "Gfx Used Memory");
            Set(C.GfxReserved, ProfilerCategory.Memory, "Gfx Reserved Memory");
            Set(C.TextureMemory, ProfilerCategory.Memory, "Texture Memory");
            Set(C.TextureCount, ProfilerCategory.Memory, "Texture Count");
            Set(C.MeshMemory, ProfilerCategory.Memory, "Mesh Memory");
            Set(C.MeshCount, ProfilerCategory.Memory, "Mesh Count");
            Set(C.RenderTextures, ProfilerCategory.Render, "Render Textures Bytes");
            Set(C.Buffers, ProfilerCategory.Render, "Used Buffers Bytes");
            Set(C.VideoMemory, ProfilerCategory.Render, "Video Memory Bytes");
            Set(C.TotalUsed, ProfilerCategory.Memory, "Total Used Memory");
            Set(C.TotalReserved, ProfilerCategory.Memory, "Total Reserved Memory");
            Set(C.GcUsed, ProfilerCategory.Memory, "GC Used Memory");
            Set(C.GcAllocFrame, ProfilerCategory.Memory, "GC Allocated In Frame");
            Set(C.AppResident, ProfilerCategory.Memory, "App Resident Memory");

            scripts.Start();
            lastHandleCount = -1;
            nextDiscovery = 0f;
            ResetWindow();
        }

        void Set(C id, ProfilerCategory category, string name)
        {
            counters[(int)id] = new Counter
            {
                category = category,
                name = name,
                recorder = ProfilerRecorder.StartNew(category, name),
            };
        }

        void End()
        {
            if (!running) return;
            running = false;
            if (counters != null)
                for (int i = 0; i < counters.Length; i++) counters[i].recorder.Dispose();
            counters = null;
            scripts.Stop();
            foreach (GpuPass p in gpuPasses) if (p.recorder.isValid) p.recorder.enabled = false;
            gpuPasses.Clear();
            gpuNames.Clear();
        }

        long Value(C id) => counters != null ? counters[(int)id].Value : -1;

        // ---- per frame ------------------------------------------------------------

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[toggleKey].wasPressedThisFrame)
            {
                visible = !visible;
                if (visible) Begin();
                else End();
            }
            if (!visible) return;
            if (!running) Begin();

            SampleFrameTiming();
            scripts.Sample();
            SampleGpuPasses();
            frameCount++;

            if (Time.unscaledTime - windowStart >= WindowSeconds) Publish();
        }

        void SampleFrameTiming()
        {
            FrameTimingManager.CaptureFrameTimings();
            double frame = Time.unscaledDeltaTime * 1000.0, main = 0.0, gpu = 0.0;
            if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].cpuFrameTime > 0.0)
            {
                FrameTiming t = timings[0];
                hasFrameTimings = true;
                frame = t.cpuFrameTime;
                main = t.cpuMainThreadFrameTime;
                sumMain += main;
                sumRender += t.cpuRenderThreadFrameTime;
                sumPresent += t.cpuMainThreadPresentWaitTime;
                if (main > maxMain) maxMain = main;
                if (t.gpuFrameTime > 0.0)
                {
                    gpu = t.gpuFrameTime;
                    hasGpuTime = true;
                    sumGpu += gpu;
                    gpuFrames++;
                    if (gpu > maxGpu) maxGpu = gpu;
                }
                timingFrames++;
            }

            sumFrame += Time.unscaledDeltaTime * 1000.0;
            if (frame > maxFrame) maxFrame = frame;
            DrawGraphColumn(Time.unscaledDeltaTime * 1000.0, main, gpu);
        }

        void DrawGraphColumn(double frameMs, double mainMs, double gpuMs)
        {
            float scale = GraphHeight / Mathf.Max(graphMaxMs, 1f);
            int frameH = Mathf.Clamp((int)(frameMs * scale), 0, GraphHeight);
            int mainY = mainMs > 0.0 ? Mathf.Clamp((int)(mainMs * scale), 0, GraphHeight - 1) : -1;
            int gpuY = gpuMs > 0.0 ? Mathf.Clamp((int)(gpuMs * scale), 0, GraphHeight - 1) : -1;
            int line60 = (int)(16.667f * scale), line30 = (int)(33.333f * scale);

            Color32 bg = new Color32(8, 14, 24, 220), bar = new Color32(70, 90, 120, 255);
            Color32 refLine = new Color32(110, 110, 110, 255), cpu = CpuColour, gpu = GpuColour;
            for (int y = 0; y < GraphHeight; y++)
            {
                Color32 c = y < frameH ? bar : bg;
                if (y == line60 || y == line30) c = refLine;
                column[y] = c;
            }
            if (mainY >= 0) column[mainY] = cpu;
            if (gpuY >= 0)
            {
                column[gpuY] = gpu;
                if (gpuY + 1 < GraphHeight) column[gpuY + 1] = gpu;
            }

            graph.SetPixels32(graphColumn, 0, 1, GraphHeight, column);
            graph.Apply(false);
            graphColumn = (graphColumn + 1) % GraphWidth;
        }

        void SampleGpuPasses()
        {
            if (Time.unscaledTime >= nextDiscovery)
            {
                nextDiscovery = Time.unscaledTime + 5f;
                DiscoverGpuPasses();
            }
            for (int i = 0; i < gpuPasses.Count; i++)
            {
                GpuPass p = gpuPasses[i];
                double gpu = p.recorder.gpuElapsedNanoseconds * 1e-6;
                p.gpuSum += gpu;
                p.cpuSum += p.recorder.elapsedNanoseconds * 1e-6;
                if (gpu > p.gpuMax) p.gpuMax = gpu;
            }
        }

        /// Every marker that records GPU time (render passes from command-buffer samples).
        /// Repeated every few seconds while the marker count grows: render-graph passes and
        /// occasional rebuilds register theirs the first time they run.
        void DiscoverGpuPasses()
        {
            if (!SystemInfo.supportsGpuRecorder) return;
            handleScratch.Clear();
            ProfilerRecorderHandle.GetAvailable(handleScratch);
            if (handleScratch.Count == lastHandleCount) return;
            lastHandleCount = handleScratch.Count;

            foreach (ProfilerRecorderHandle h in handleScratch)
            {
                ProfilerRecorderDescription d = ProfilerRecorderHandle.GetDescription(h);
                if ((d.Flags & MarkerFlags.SampleGPU) == 0 || GpuContainers.Contains(d.Name) || !gpuNames.Add(d.Name)) continue;
                Recorder r = Recorder.Get(d.Name);
                if (!r.isValid) continue;
                r.enabled = true;
                string name = d.Name.StartsWith(CameraPrefix, StringComparison.Ordinal)
                    ? "camera: " + d.Name.Substring(CameraPrefix.Length)
                    : d.Name;
                gpuPasses.Add(new GpuPass { name = name, recorder = r });
            }
        }

        void ResetWindow()
        {
            sumFrame = sumMain = sumRender = sumGpu = sumPresent = maxFrame = maxMain = maxGpu = 0.0;
            timingFrames = gpuFrames = frameCount = 0;
            foreach (GpuPass p in gpuPasses) p.gpuSum = p.cpuSum = p.gpuMax = 0.0;
            windowStart = Time.unscaledTime;
        }

        // ---- publishing -----------------------------------------------------------

        void Publish()
        {
            int n = Mathf.Max(frameCount, 1);
            float seconds = Time.unscaledTime - windowStart;
            avgFrame = sumFrame / n;
            pubMaxFrame = maxFrame;
            avgMain = timingFrames > 0 ? sumMain / timingFrames : 0.0;
            avgRender = timingFrames > 0 ? sumRender / timingFrames : 0.0;
            avgPresent = timingFrames > 0 ? sumPresent / timingFrames : 0.0;
            avgGpu = gpuFrames > 0 ? sumGpu / gpuFrames : 0.0;
            pubMaxMain = maxMain;
            pubMaxGpu = maxGpu;

            BuildGpuRows(n);    // first: the frame text uses the per-camera GPU time
            BuildFrameText(frameCount / Mathf.Max(seconds, 1e-3f));
            BuildScriptRows();
            BuildMemoryText();
            ResetWindow();
        }

        void BuildFrameText(float fps)
        {
            sb.Clear();
            sb.AppendFormat(Inv, "<b>{0:F1} FPS</b>   frame {1:F2} ms   (worst {2:F2} ms)\n", fps, avgFrame, pubMaxFrame);
            if (hasFrameTimings)
            {
                sb.AppendFormat(Inv, "CPU main thread   {0,6:F2} ms   (worst {1:F2})\n", avgMain, pubMaxMain);
                sb.AppendFormat(Inv, "CPU render thread {0,6:F2} ms\n", avgRender);
                if (hasGpuTime) sb.AppendFormat(Inv, "GPU               {0,6:F2} ms   (worst {1:F2})\n", avgGpu, pubMaxGpu);
                else sb.Append("GPU               n/a (this graphics API reports no GPU frame time)\n");
                if (cameraGpu > 0.0)
                    sb.AppendFormat(Inv, "GPU, cameras      {0,6:F2} ms   (sum of the camera passes, GPU tab)\n", cameraGpu);
                sb.AppendFormat(Inv, "Main thread waiting for present {0:F2} ms\n", avgPresent);
                sb.Append(BoundText()).Append('\n');
            }
            else
            {
                sb.Append("CPU/GPU split n/a: enable Player Settings > Other > Frame Timing Stats\n" +
                          "for builds (the editor always has it).\n");
            }

            sb.AppendFormat(Inv, "\nBatches {0}   draw calls {1}   SetPass {2}\n",
                            Count(Value(C.Batches)), Count(Value(C.DrawCalls)), Count(Value(C.SetPass)));
            sb.AppendFormat(Inv, "Triangles {0}   vertices {1}   shadow casters {2}\n",
                            Count(Value(C.Triangles)), Count(Value(C.Vertices)), Count(Value(C.ShadowCasters)));
            sb.AppendFormat(Inv, "GC allocated this frame {0}   GC heap {1}\n",
                            Bytes(Value(C.GcAllocFrame)), Bytes(Value(C.GcUsed)));

            sb.AppendFormat(Inv, "\nQuality \"{0}\"   {1}x{2}   vSync {3}   target {4}\n",
                            QualitySettings.names[QualitySettings.GetQualityLevel()], Screen.width, Screen.height,
                            QualitySettings.vSyncCount, Application.targetFrameRate <= 0 ? "unlimited" : Application.targetFrameRate + " fps");
            sb.AppendFormat(Inv, "{0} ({1}, {2} MB VRAM)\n{3}, {4} threads",
                            SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceType, SystemInfo.graphicsMemorySize,
                            SystemInfo.processorType, SystemInfo.processorCount);
            frameText = sb.ToString();
        }

        string BoundText()
        {
            // The frame-timing GPU figure can lag or miss work in the editor; the camera
            // samples are measured directly, so trust whichever is larger.
            double cpu = Math.Max(avgMain, avgRender), gpu = Math.Max(hasGpuTime ? avgGpu : 0.0, cameraGpu);
            string bound;
            if (gpu > cpu * 1.1)
                bound = "<color=#ff9933>GPU-bound</color>: the GPU takes longer than the CPU threads";
            else if (avgRender > avgMain * 1.1)
                bound = "<color=#5ad8ff>CPU-bound (render thread)</color>: draw call submission";
            else
                bound = "<color=#5ad8ff>CPU-bound (main thread)</color>: scripts, physics, culling";
            return Application.isEditor
                ? bound + "\n(in the editor the main thread also runs the editor: EditorLoop in the Scripts tab)"
                : bound;
        }

        void BuildScriptRows()
        {
            if (scripts.IsSupported) scriptFrames = scripts.Publish(methodEntries, classEntries, stageEntries);
            FillScriptRows();
        }

        void FillScriptRows()
        {
            scriptRows.Clear();
            stageRows.Clear();
            if (!scripts.IsSupported)
            {
                scriptsHeader = "Per-script times need the Unity Editor (they come from the profiler's frame data).";
                return;
            }

            List<ScriptProfiler.Entry> list = scriptsByClass ? classEntries : methodEntries;
            double total = 0.0;
            foreach (ScriptProfiler.Entry e in methodEntries) total += e.avgMs;

            scriptsHeader = scriptFrames == 0
                ? "Waiting for profiler frames..."
                : string.Format(Inv, "Scripts {0:F2} ms per frame in {1} {2}, over {3} frames. Profiler recording is on " +
                                     "while this window is open and adds its own overhead.",
                                total, list.Count, scriptsByClass ? "classes" : "methods", scriptFrames);

            double top = list.Count > 0 ? Math.Max(list[0].avgMs, 0.01) : 1.0;
            for (int i = 0; i < list.Count && i < listRows; i++)
            {
                ScriptProfiler.Entry e = list[i];
                scriptRows.Add(new Row
                {
                    name = e.name,
                    a = e.avgMs.ToString("F3", Inv),
                    b = e.maxMs.ToString("F2", Inv),
                    c = e.gcBytes > 0.5 ? Bytes((long)e.gcBytes) : "",
                    fraction = (float)(e.avgMs / top),
                    colour = CpuColour,
                });
            }

            double stageTop = stageEntries.Count > 0 ? Math.Max(stageEntries[0].avgMs, 0.01) : 1.0;
            for (int i = 0; i < stageEntries.Count && i < 10; i++)
            {
                ScriptProfiler.Entry e = stageEntries[i];
                stageRows.Add(new Row
                {
                    name = e.name,
                    a = e.avgMs.ToString("F3", Inv),
                    b = e.maxMs.ToString("F2", Inv),
                    c = "",
                    fraction = (float)(e.avgMs / stageTop),
                    colour = new Color(0.7f, 0.75f, 0.85f),
                });
            }
        }

        void BuildGpuRows(int frames)
        {
            gpuRows.Clear();
            cameraGpu = 0.0;
            if (!SystemInfo.supportsGpuRecorder)
            {
                gpuHeader = "This platform does not record GPU time per pass.";
                return;
            }

            gpuSorted.Clear();
            bool sceneView = false;
            foreach (GpuPass p in gpuPasses)
            {
                if (p.gpuSum <= 0.0) continue;
                gpuSorted.Add(p);
                if (p.name == "camera: SceneCamera") sceneView = true;
                if (p.name.StartsWith("camera: ", StringComparison.Ordinal)) cameraGpu += p.gpuSum / frames;
            }
            gpuSorted.Sort((x, y) => y.gpuSum.CompareTo(x.gpuSum));

            gpuHeader = string.Format(Inv,
                "GPU frame {0}. {1} passes timed (of {2} GPU markers). Samples can nest: a camera row " +
                "includes its passes, so the column does not add up to the frame.{3}",
                hasGpuTime ? avgGpu.ToString("F2", Inv) + " ms" : "n/a", gpuSorted.Count, gpuPasses.Count,
                sceneView ? "\n<color=#ffcc4d>The Scene view is rendering too and is counted in every pass and in the " +
                            "GPU frame. Hide it (show only the Game view) to measure the game alone.</color>" : "");

            double top = gpuSorted.Count > 0 ? Math.Max(gpuSorted[0].gpuSum / frames, 0.01) : 1.0;
            for (int i = 0; i < gpuSorted.Count && i < listRows; i++)
            {
                GpuPass p = gpuSorted[i];
                double avg = p.gpuSum / frames;
                gpuRows.Add(new Row
                {
                    name = p.name,
                    a = avg.ToString("F3", Inv),
                    b = p.gpuMax.ToString("F2", Inv),
                    c = (p.cpuSum / frames).ToString("F3", Inv),
                    fraction = (float)(avg / top),
                    colour = GpuColour,
                });
            }
        }

        void BuildMemoryText()
        {
            long vram = SystemInfo.graphicsMemorySize * 1024L * 1024L;
            long gfxUsed = Value(C.GfxUsed);

            sb.Clear();
            sb.AppendFormat(Inv, "<b>VRAM</b> {0} on {1}\n", Bytes(vram), SystemInfo.graphicsDeviceName);
            if (gfxUsed >= 0)
            {
                sb.AppendFormat(Inv, "Unity graphics memory used {0}, reserved {1}  ({2:P0} of VRAM)\n",
                                Bytes(gfxUsed), Bytes(Value(C.GfxReserved)), vram > 0 ? (double)gfxUsed / vram : 0.0);
                sb.AppendFormat(Inv, "  textures {0} ({1})   meshes {2} ({3})\n  render textures {4}   buffers {5}\n",
                                Bytes(Value(C.TextureMemory)), Count(Value(C.TextureCount)),
                                Bytes(Value(C.MeshMemory)), Count(Value(C.MeshCount)),
                                Bytes(Value(C.RenderTextures)), Bytes(Value(C.Buffers)));
                if (Application.isEditor) sb.Append("  (in the editor these include the editor's own textures)\n");
            }
            else
            {
                sb.AppendFormat(Inv, "Graphics memory counters need the editor or a development build.\n" +
                                     "Render textures {0}   buffers {1}\n", Bytes(Value(C.RenderTextures)), Bytes(Value(C.Buffers)));
            }
            sb.AppendFormat(Inv, "System memory used {0}, reserved {1}, resident {2}, GC heap {3}",
                            Bytes(Value(C.TotalUsed)), Bytes(Value(C.TotalReserved)), Bytes(Value(C.AppResident)),
                            Bytes(Value(C.GcUsed)));
            memoryText = sb.ToString();
            vramFraction = vram > 0 && gfxUsed > 0 ? Mathf.Clamp01((float)((double)gfxUsed / vram)) : 0f;

            BuildTerrainText(vram, gfxUsed);
        }

        void BuildTerrainText(long vram, long gfxUsed)
        {
            sb.Clear();
            GlobeTerrain terrain = GlobeTerrain.Active;
            terrainFraction = 0f;
            terrainColour = MemColour;

            if (terrain == null)
            {
                sb.Append("<b>Terrain</b>: no GlobeTerrain in the scene.\n");
            }
            else
            {
                GlobeTerrain.MemoryStats m = terrain.GetMemoryStats();
                long total = m.TotalBytes;
                long perChunk = m.chunks > 0 ? (m.meshBytes + m.classTextureBytes) / m.chunks : 0;
                long fullCache = perChunk * Math.Max(terrain.cacheSize, m.chunks) + m.sharedTextureBytes;

                sb.AppendFormat(Inv, "<b>Terrain</b> {0} on the GPU", Bytes(total));
                if (gfxUsed > 0) sb.AppendFormat(Inv, " = {0:P0} of Unity graphics memory", (double)total / gfxUsed);
                if (vram > 0) sb.AppendFormat(Inv, ", {0:P1} of VRAM", (double)total / vram);
                sb.Append('\n');
                sb.AppendFormat(Inv, "  chunks {0} cached (cacheSize {1}), {2} drawn, {3} tiles loaded ({4} x {5} window)\n",
                                m.chunks, terrain.cacheSize, m.shownChunks, terrain.VisibleTileCount, terrain.tilesWide, terrain.tilesHigh);
                sb.AppendFormat(Inv, "  meshes {0}   class textures {1}   shared textures {2}\n",
                                Bytes(m.meshBytes), Bytes(m.classTextureBytes), Bytes(m.sharedTextureBytes));
                sb.AppendFormat(Inv, "  {0} per chunk ({1} quads, {2} class texels a side); a full cache is {3}\n",
                                Bytes(perChunk), terrain.resolution, terrain.classTexels, Bytes(fullCache));
                sb.AppendFormat(Inv, "  enabled: {0} triangles, {1} of mesh, before camera culling; shadow casting {2}\n",
                                Count(m.shownTriangles), Bytes(m.shownMeshBytes),
                                terrain.castShadows != UnityEngine.Rendering.ShadowCastingMode.Off ? "on" : "off");

                float now = Time.unscaledTime;
                float rate = lastBuildCount >= 0 && now > lastBuildTime ? (terrain.BuildCount - lastBuildCount) / (now - lastBuildTime) : 0f;
                lastBuildCount = terrain.BuildCount;
                lastBuildTime = now;
                sb.AppendFormat(Inv, "  builds {0:F0}/s, {1:F2} ms each, {2:F2} ms in the last frame",
                                rate, terrain.AverageBuildMs, terrain.BuildMsLastFrame);
                sb.Append('\n');

                terrainFraction = vram > 0 ? Mathf.Clamp01((float)((double)total / vram)) : 0f;
                if (vram > 0 && fullCache > vram / 4)
                {
                    terrainColour = fullCache > vram / 2 ? BadColour : WarnColour;
                    sb.AppendFormat(Inv, "  <color=#{0}>A full chunk cache would take {1:P0} of this card's VRAM.</color>\n",
                                    ColorUtility.ToHtmlStringRGB(terrainColour), (double)fullCache / vram);
                }
            }

            GlobeAutogen autogen = GlobeAutogen.Active;
            if (autogen != null)
            {
                autogen.GetGpuBytes(out long buffers, out long textures);
                sb.AppendFormat(Inv, "<b>Autogen</b> {0} instance buffers + {1} textures   {2} patches cached " +
                                     "(cacheSize {3}), {4} drawn, {5} objects",
                                Bytes(buffers), textures > 0 ? Bytes(textures) : "n/a", autogen.CachedPatchCount,
                                autogen.cacheSize, autogen.DrawnPatchCount, Count(autogen.DrawnInstanceCount));
            }
            terrainText = sb.ToString();
        }

        /// The largest textures and meshes loaded right now. Terrain chunks and their class
        /// textures are summed into one row each. Editor and development builds only.
        void ScanAssets()
        {
            assetRows.Clear();
            var rows = new List<(string name, long bytes, int count, Color colour)>();
            long texTotal = 0, meshTotal = 0, chunkMeshes = 0, chunkTextures = 0;
            int chunkMeshCount = 0, chunkTextureCount = 0;

            foreach (Texture t in Resources.FindObjectsOfTypeAll<Texture>())
            {
                long bytes = Profiler.GetRuntimeMemorySizeLong(t);
                texTotal += bytes;
                if (t.name.StartsWith("Terrain ", StringComparison.Ordinal) && t.name.EndsWith(" classes", StringComparison.Ordinal))
                {
                    chunkTextures += bytes;
                    chunkTextureCount++;
                }
                else
                {
                    rows.Add(($"{(string.IsNullOrEmpty(t.name) ? "(unnamed)" : t.name)}  {t.width}x{t.height} {t.graphicsFormat}",
                              bytes, 1, MemColour));
                }
            }
            foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                long bytes = Profiler.GetRuntimeMemorySizeLong(mesh);
                meshTotal += bytes;
                if (mesh.name.StartsWith("Terrain ", StringComparison.Ordinal))
                {
                    chunkMeshes += bytes;
                    chunkMeshCount++;
                }
                else
                {
                    rows.Add(($"{(string.IsNullOrEmpty(mesh.name) ? "(unnamed)" : mesh.name)}  mesh, {mesh.vertexCount} verts",
                              bytes, 1, CpuColour));
                }
            }
            if (chunkMeshCount > 0) rows.Add(($"Terrain chunk meshes  x{chunkMeshCount}", chunkMeshes, chunkMeshCount, WarnColour));
            if (chunkTextureCount > 0) rows.Add(($"Terrain class textures  x{chunkTextureCount}", chunkTextures, chunkTextureCount, WarnColour));
            rows.Sort((x, y) => y.bytes.CompareTo(x.bytes));

            if (texTotal + meshTotal == 0)
            {
                assetsHeader = "Asset sizes need the editor or a development build.";
                return;
            }

            assetsHeader = string.Format(Inv, "Loaded textures {0}, meshes {1} (profiler sizes, including CPU copies of " +
                                              "readable assets{2})", Bytes(texTotal), Bytes(meshTotal),
                                         Application.isEditor ? " and editor assets" : "");
            long top = rows.Count > 0 ? Math.Max(rows[0].bytes, 1) : 1;
            for (int i = 0; i < rows.Count && i < listRows; i++)
            {
                assetRows.Add(new Row
                {
                    name = rows[i].name,
                    a = Bytes(rows[i].bytes),
                    b = "",
                    c = "",
                    fraction = (float)rows[i].bytes / top,
                    colour = rows[i].colour,
                });
            }
        }

        /// Everything the tabs show, as plain text (the window's Copy button puts it on the
        /// clipboard, for pasting into a bug report).
        public string Report()
        {
            var r = new StringBuilder(4096);
            r.AppendLine("== Frame ==").AppendLine(StripTags(frameText));
            r.AppendLine().AppendLine("== Scripts (" + (scriptsByClass ? "by class" : "by method") + ") ==").AppendLine(scriptsHeader);
            AppendRows(r, scriptRows, "avg ms", "max ms", "GC/frame");
            r.AppendLine("-- player loop stages --");
            AppendRows(r, stageRows, "avg ms", "max ms", null);
            r.AppendLine().AppendLine("== GPU ==").AppendLine(gpuHeader);
            AppendRows(r, gpuRows, "GPU ms", "max ms", "CPU ms");
            r.AppendLine().AppendLine("== Memory ==").AppendLine(StripTags(memoryText)).AppendLine(StripTags(terrainText));
            if (assetRows.Count > 0)
            {
                r.AppendLine(assetsHeader);
                AppendRows(r, assetRows, "size", null, null);
            }
            return r.ToString();
        }

        static void AppendRows(StringBuilder r, List<Row> rows, string a, string b, string c)
        {
            foreach (Row row in rows)
            {
                r.Append("  ").Append(row.name).Append(" | ").Append(a).Append(' ').Append(row.a);
                if (b != null) r.Append(" | ").Append(b).Append(' ').Append(row.b);
                if (c != null && row.c.Length > 0) r.Append(" | ").Append(c).Append(' ').Append(row.c);
                r.AppendLine();
            }
        }

        static string StripTags(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "");

        // ---- GUI ------------------------------------------------------------------

        void OnGUI()
        {
            if (!visible) return;

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = true };
                labelStyle.normal.textColor = new Color(0.85f, 0.95f, 1f);
                smallStyle = new GUIStyle(labelStyle) { fontSize = 12, wordWrap = false, clipping = TextClipping.Clip };
                headerStyle = new GUIStyle(smallStyle) { fontStyle = FontStyle.Bold };
                headerStyle.normal.textColor = new Color(0.6f, 0.7f, 0.8f);
            }

            // First show: bottom left, clear of the HUD (top left) and the globe window (top right).
            if (!placed)
            {
                windowRect.y = Mathf.Max(0f, Screen.height - windowRect.height - 20f);
                placed = true;
            }

            windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "Performance  (F3)");
        }

        void DrawWindow(int id)
        {
            const float pad = 10f;
            float width = windowRect.width - pad * 2f;
            float y = 24f;

            tab = (Tab)GUI.Toolbar(new Rect(pad, y, width - 70f, 22f), (int)tab, TabNames);
            if (GUI.Button(new Rect(pad + width - 64f, y, 64f, 22f), "Copy")) GUIUtility.systemCopyBuffer = Report();
            y += 30f;

            switch (tab)
            {
                case Tab.Frame:
                    y = Text(frameText, pad, y, width);
                    y += 4f;
                    var graphRect = new Rect(pad, y, width, 90f);
                    GUI.DrawTextureWithTexCoords(graphRect, graph, new Rect((float)graphColumn / GraphWidth, 0f, 1f, 1f));
                    y = graphRect.yMax + 2f;
                    GUI.Label(new Rect(pad, y, width, 18f), string.Format(Inv,
                        "<color=#5ad8ff>CPU main</color>  <color=#ff9933>GPU</color>  bars: frame time   lines: 16.7 / 33.3 ms   top: {0:0} ms",
                        graphMaxMs), smallStyle);
                    y += 20f;
                    break;

                case Tab.Scripts:
                    if (GUI.Button(new Rect(pad, y, 180f, 20f), scriptsByClass ? "Grouped by class" : "Each method"))
                    {
                        scriptsByClass = !scriptsByClass;
                        FillScriptRows();
                    }
                    y += 26f;
                    y = Text(scriptsHeader, pad, y, width);
                    y = Table(scriptRows, "script", "avg ms", "max ms", "GC/frame", pad, y, width);
                    y += 8f;
                    GUI.Label(new Rect(pad, y, width, 18f), "<b>Player loop stages</b>", smallStyle);
                    y += 18f;
                    y = Table(stageRows, "stage", "avg ms", "max ms", "", pad, y, width);
                    break;

                case Tab.Gpu:
                    y = Text(gpuHeader, pad, y, width);
                    y = Table(gpuRows, "pass", "GPU ms", "max ms", "CPU ms", pad, y, width);
                    break;

                case Tab.Memory:
                    y = Text(memoryText, pad, y, width);
                    y = Bar(pad, y, width, vramFraction, MemColour);
                    y += 6f;
                    y = Text(terrainText, pad, y, width);
                    y = Bar(pad, y, width, terrainFraction, terrainColour);
                    y += 6f;
                    if (GUI.Button(new Rect(pad, y, 260f, 20f), "Scan largest textures and meshes")) ScanAssets();
                    y += 26f;
                    if (assetsHeader.Length > 0)
                    {
                        y = Text(assetsHeader, pad, y, width);
                        y = Table(assetRows, "asset", "size", "", "", pad, y, width);
                    }
                    break;
            }

            windowRect.height = y + pad;
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 20f));
        }

        float Text(string text, float x, float y, float width)
        {
            if (string.IsNullOrEmpty(text)) return y;
            float h = labelStyle.CalcHeight(new GUIContent(text), width);
            GUI.Label(new Rect(x, y, width, h), text, labelStyle);
            return y + h + 2f;
        }

        float Bar(float x, float y, float width, float fraction, Color colour)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(new Rect(x, y, width, 6f), whitePixel);
            GUI.color = colour;
            GUI.DrawTexture(new Rect(x, y, width * Mathf.Clamp01(fraction), 6f), whitePixel);
            GUI.color = Color.white;
            return y + 10f;
        }

        float Table(List<Row> rows, string nameHeader, string aHeader, string bHeader, string cHeader,
                    float x, float y, float width)
        {
            const float rowH = 17f, colA = 62f, colB = 56f, colC = 70f;
            float nameW = width - colA - colB - colC;
            float ax = x + nameW, bx = ax + colA, cx = bx + colB;

            GUI.Label(new Rect(x, y, nameW, rowH), nameHeader, headerStyle);
            GUI.Label(new Rect(ax, y, colA, rowH), aHeader, headerStyle);
            GUI.Label(new Rect(bx, y, colB, rowH), bHeader, headerStyle);
            GUI.Label(new Rect(cx, y, colC, rowH), cHeader, headerStyle);
            y += rowH;

            foreach (Row r in rows)
            {
                GUI.color = new Color(r.colour.r, r.colour.g, r.colour.b, 0.22f);
                GUI.DrawTexture(new Rect(x, y + 2f, nameW * Mathf.Clamp01(r.fraction), rowH - 3f), whitePixel);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + 2f, y, nameW - 4f, rowH), r.name, smallStyle);
                GUI.Label(new Rect(ax, y, colA, rowH), r.a, smallStyle);
                GUI.Label(new Rect(bx, y, colB, rowH), r.b, smallStyle);
                GUI.Label(new Rect(cx, y, colC, rowH), r.c, smallStyle);
                y += rowH;
            }
            return y;
        }

        // ---- formatting -----------------------------------------------------------

        static string Bytes(long bytes)
        {
            if (bytes < 0) return "n/a";
            if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("F1", Inv) + " KB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("F1", Inv) + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", Inv) + " GB";
        }

        static string Count(long n)
        {
            if (n < 0) return "n/a";
            if (n < 10000) return n.ToString(Inv);
            if (n < 1000000) return (n / 1000.0).ToString("F1", Inv) + " k";
            return (n / 1000000.0).ToString("F2", Inv) + " M";
        }
    }
}
