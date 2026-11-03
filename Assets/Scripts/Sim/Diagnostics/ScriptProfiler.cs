using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor.Profiling;
using UnityEditorInternal;
#endif

namespace CirrusFS.Sim
{
    /// Per-script CPU cost, read from the Unity profiler's own frame data: every MonoBehaviour
    /// message (Update, LateUpdate, FixedUpdate, OnGUI, coroutines) and every script-category
    /// ProfilerMarker under the player loop, with the GC memory each one allocated. Also the
    /// player loop's top-level stages (scripts, physics, rendering, the editor's own loop).
    ///
    /// Editor only: while running it turns profiler recording on, as the Profiler window's
    /// Record button does, and turns it back off when stopped if it was off before. That adds
    /// its own overhead to the frame. In a player build IsSupported is false.
    public sealed class ScriptProfiler
    {
        public struct Entry
        {
            public string name;
            public double avgMs;     // per frame, over the frames since the last Publish
            public double maxMs;     // the worst single frame
            public double gcBytes;   // allocated per frame
            public double calls;     // per frame
        }

#if UNITY_EDITOR
        public bool IsSupported => true;
#else
        public bool IsSupported => false;
#endif

        public bool Running { get; private set; }

        sealed class Acc
        {
            public string name, owner;     // owner: the class, for grouping
            public double total, frameSum, max, gc, calls;
            public int frame = -1;

            public void Add(int frameIndex, double ms)
            {
                if (frame != frameIndex) Close(frameIndex);
                frameSum += ms;
                total += ms;
            }

            public void Close(int nextFrame)
            {
                if (frameSum > max) max = frameSum;
                frameSum = 0.0;
                frame = nextFrame;
            }

            public void Reset()
            {
                total = frameSum = max = gc = calls = 0.0;
                frame = -1;
            }
        }

        readonly Dictionary<string, Acc> methods = new Dictionary<string, Acc>();
        readonly Dictionary<string, Acc> stages = new Dictionary<string, Acc>();
        readonly Dictionary<string, Acc> classScratch = new Dictionary<string, Acc>();
        int frames;

#if UNITY_EDITOR
        enum Kind : byte { Other, PlayerLoop, Stage, Runner, Script, GcAlloc, EditorLoop }

        struct MarkerInfo
        {
            public Kind kind;
            public string name;
        }

        // Marker ids are stable for the editor session.
        readonly Dictionary<int, MarkerInfo> markers = new Dictionary<int, MarkerInfo>();

        // Depth-first walk of the raw samples: per open sample, the children still to come.
        int[] remaining = new int[64];
        Acc[] owners = new Acc[64];
        bool[] inLoop = new bool[64], inStage = new bool[64];
        Kind[] kinds = new Kind[64];

        bool enabledProfiler;
        int lastFrame = -1;

        // The player loop runs every MonoBehaviour message under one of these.
        static readonly HashSet<string> Runners = new HashSet<string>
        {
            "BehaviourUpdate", "LateBehaviourUpdate", "FixedBehaviourUpdate", "CoroutinesDelayedCalls",
        };

        // The player loop's systems ("Update.ScriptRunBehaviourUpdate",
        // "PostLateUpdate.FinishFrameRendering", ...), at whatever depth the editor nests them.
        static readonly string[] StagePrefixes =
        {
            "Initialization.", "EarlyUpdate.", "FixedUpdate.", "PreUpdate.", "Update.", "PreLateUpdate.",
            "PostLateUpdate.", "TimeUpdate.",
        };
#endif

        public void Start()
        {
            if (Running) return;
            Running = true;
            Clear();
#if UNITY_EDITOR
            markers.Clear();
            enabledProfiler = !ProfilerDriver.enabled;
            if (enabledProfiler) ProfilerDriver.enabled = true;
            lastFrame = ProfilerDriver.lastFrameIndex;
#endif
        }

        public void Stop()
        {
            if (!Running) return;
            Running = false;
#if UNITY_EDITOR
            if (enabledProfiler) ProfilerDriver.enabled = false;
            enabledProfiler = false;
#endif
        }

        void Clear()
        {
            foreach (Acc a in methods.Values) a.Reset();
            foreach (Acc a in stages.Values) a.Reset();
            frames = 0;
        }

        /// Reads the frames the profiler finished since the last call (at most `maxFrames`,
        /// the newest; older ones are skipped). Call once per frame.
        public void Sample(int maxFrames = 3)
        {
#if UNITY_EDITOR
            if (!Running) return;
            int last = ProfilerDriver.lastFrameIndex;
            if (last < lastFrame) lastFrame = last - 1;    // profiler data was cleared
            int first = System.Math.Max(lastFrame + 1, System.Math.Max(ProfilerDriver.firstFrameIndex, last - maxFrames + 1));
            for (int f = first; f <= last; f++) ReadFrame(f);
            lastFrame = last;
#endif
        }

        /// Averages over the frames read since the last call, sorted by cost, then starts a
        /// new window. `byMethod` lists each message on its own; `byClass` sums them per class.
        public int Publish(List<Entry> byMethod, List<Entry> byClass, List<Entry> loopStages)
        {
            int n = frames;
            byMethod.Clear();
            byClass.Clear();
            loopStages.Clear();
            if (n > 0)
            {
                foreach (Acc a in classScratch.Values) a.Reset();
                foreach (Acc a in methods.Values)
                {
                    a.Close(-1);
                    if (a.total <= 0.0 && a.gc <= 0.0) continue;
                    byMethod.Add(ToEntry(a, n));

                    if (!classScratch.TryGetValue(a.owner, out Acc c))
                        classScratch.Add(a.owner, c = new Acc { name = a.owner, owner = a.owner });
                    c.total += a.total;
                    c.max += a.max;        // upper bound: the methods' worst frames may differ
                    c.gc += a.gc;
                    c.calls += a.calls;
                }
                foreach (Acc c in classScratch.Values)
                    if (c.total > 0.0 || c.gc > 0.0) byClass.Add(ToEntry(c, n));
                foreach (Acc a in stages.Values)
                {
                    a.Close(-1);
                    if (a.total > 0.0) loopStages.Add(ToEntry(a, n));
                }
            }

            byMethod.Sort(ByCost);
            byClass.Sort(ByCost);
            loopStages.Sort(ByCost);
            Clear();
            return n;
        }

        static Entry ToEntry(Acc a, int frames) => new Entry
        {
            name = a.name,
            avgMs = a.total / frames,
            maxMs = a.max,
            gcBytes = a.gc / frames,
            calls = a.calls / frames,
        };

        static int ByCost(Entry a, Entry b) => b.avgMs.CompareTo(a.avgMs);

        static Acc Get(Dictionary<string, Acc> map, string name)
        {
            if (!map.TryGetValue(name, out Acc a))
            {
                int dot = name.IndexOf('.');
                map.Add(name, a = new Acc { name = name, owner = dot > 0 ? name.Substring(0, dot) : name });
            }
            return a;
        }

#if UNITY_EDITOR
        void ReadFrame(int frameIndex)
        {
            using (RawFrameDataView raw = ProfilerDriver.GetRawFrameDataView(frameIndex, 0))
            {
                if (!raw.valid) return;
                frames++;

                int count = raw.sampleCount;
                int sp = 0;
                for (int i = 0; i < count; i++)
                {
                    while (sp > 0 && remaining[sp - 1] == 0) sp--;
                    Kind parentKind = Kind.Other;
                    Acc owner = null;
                    bool underLoop = false, underStage = false;
                    if (sp > 0)
                    {
                        remaining[sp - 1]--;
                        parentKind = kinds[sp - 1];
                        owner = owners[sp - 1];
                        underLoop = inLoop[sp - 1];
                        underStage = inStage[sp - 1];
                    }

                    MarkerInfo info = Info(raw, raw.GetSampleMarkerId(i));
                    Acc mine = owner;

                    if (info.kind == Kind.EditorLoop && !underLoop)
                    {
                        Get(stages, "EditorLoop (editor only)").Add(frameIndex, raw.GetSampleTimeMs(i));
                    }
                    else if (underLoop)
                    {
                        if (info.kind == Kind.Stage && !underStage) Get(stages, info.name).Add(frameIndex, raw.GetSampleTimeMs(i));

                        if (info.kind == Kind.GcAlloc)
                        {
                            if (owner != null && raw.GetSampleMetadataCount(i) > 0) owner.gc += raw.GetSampleMetadataAsLong(i, 0);
                        }
                        else if (owner == null && (info.kind == Kind.Script || parentKind == Kind.Runner))
                        {
                            // outermost script sample: nested ones are already inside its time
                            mine = Get(methods, info.name);
                            mine.Add(frameIndex, raw.GetSampleTimeMs(i));
                            mine.calls++;
                        }
                    }

                    if (sp == remaining.Length) Grow();
                    remaining[sp] = raw.GetSampleChildrenCount(i);
                    kinds[sp] = info.kind;
                    owners[sp] = mine;
                    inLoop[sp] = underLoop || info.kind == Kind.PlayerLoop;
                    inStage[sp] = underStage || (underLoop && info.kind == Kind.Stage);
                    sp++;
                }
            }
        }

        MarkerInfo Info(RawFrameDataView raw, int markerId)
        {
            if (markers.TryGetValue(markerId, out MarkerInfo info)) return info;

            string name = raw.GetMarkerName(markerId) ?? "";
            Kind kind = Kind.Other;
            if (name == "PlayerLoop") kind = Kind.PlayerLoop;
            else if (name == "EditorLoop") kind = Kind.EditorLoop;
            else if (name == "GC.Alloc") kind = Kind.GcAlloc;
            else if (Runners.Contains(name)) kind = Kind.Runner;
            else if (name.EndsWith("]") && (name.Contains("[Invoke") || name.Contains("[Coroutine")))
                kind = Kind.Script;
            else if (raw.GetCategoryInfo(raw.GetMarkerCategoryIndex(markerId)).name == "Scripts")
                kind = Kind.Script;
            else
            {
                foreach (string prefix in StagePrefixes)
                    if (name.StartsWith(prefix, System.StringComparison.Ordinal)) kind = Kind.Stage;
            }

            info = new MarkerInfo { kind = kind, name = kind == Kind.Script ? CleanName(name) : name };
            markers.Add(markerId, info);
            return info;
        }

        /// "Assembly-CSharp.dll!CirrusFS.Geo::GlobeTerrain.LateUpdate() [Invoke]" (how Unity 6
        /// names script messages) to "GlobeTerrain.LateUpdate()".
        static string CleanName(string name)
        {
            int bang = name.IndexOf('!');
            if (bang >= 0) name = name.Substring(bang + 1);
            int ns = name.LastIndexOf("::", System.StringComparison.Ordinal);
            if (ns >= 0) name = name.Substring(ns + 2);
            // URP's C# frame, with the beginCameraRendering callbacks (autogen drawing) inside it
            if (name.StartsWith("RenderPipelineManager.DoRenderLoop_Internal", System.StringComparison.Ordinal))
                return "URP render loop (C#, incl. camera callbacks)";
            const string invoke = " [Invoke]";
            if (name.EndsWith(invoke, System.StringComparison.Ordinal)) name = name.Substring(0, name.Length - invoke.Length);
            return name;
        }

        void Grow()
        {
            int n = remaining.Length * 2;
            System.Array.Resize(ref remaining, n);
            System.Array.Resize(ref owners, n);
            System.Array.Resize(ref inLoop, n);
            System.Array.Resize(ref inStage, n);
            System.Array.Resize(ref kinds, n);
        }
#endif
    }
}
