using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace CirrusFS.Flight.EditorTools
{
    /// Builds A320CEO.controller from the clips imported with the A320 model: one override layer
    /// per A320AnimationDriver channel, each a single state whose clip is scrubbed by a float
    /// parameter of the same name (motion time, 0..1 = frame 0..60). Write Defaults is off so
    /// no layer touches what it doesn't animate. Re-run it after re-exporting the model.
    ///
    /// The model is found by its clips, not its file name: the newest model under ModelFolder
    /// that carries every channel clip. Every A320AnimationDriver in the open scenes then gets
    /// the controller on its Animator.
    public static class A320AnimatorBuilder
    {
        public const string ModelFolder = "Assets/Aircraft/A320CEO";
        public const string ControllerPath = "Assets/Aircraft/A320CEO/A320CEO.controller";

        /// Newest model under ModelFolder holding every channel clip, or null.
        public static string FindModelPath()
        {
            return AssetDatabase.FindAssets("t:Model", new[] { ModelFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p =>
                {
                    var names = AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>()
                        .Select(c => c.name).ToList();
                    return A320AnimationDriver.Channels.All(names.Contains);
                })
                .OrderByDescending(p => System.IO.File.GetLastWriteTimeUtc(p))
                .FirstOrDefault();
        }

        [MenuItem("CirrusFS/Build A320 Animator Controller")]
        public static void Build()
        {
            string modelPath = FindModelPath();
            if (modelPath == null)
            {
                Debug.LogError($"A320 animator: no model under {ModelFolder} has all of these clips: " +
                               string.Join(", ", A320AnimationDriver.Channels));
                return;
            }

            // Duplicate names (Blender's ".001" leftovers aside) keep the first clip.
            var clips = AssetDatabase.LoadAllAssetsAtPath(modelPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview"))
                .GroupBy(c => c.name)
                .ToDictionary(g => g.Key, g => g.First());

            // Rebuilt in place so Animators already pointing at the asset keep their reference.
            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (ctrl == null)
                ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            while (ctrl.parameters.Length > 0) ctrl.RemoveParameter(0);
            while (ctrl.layers.Length > 1) ctrl.RemoveLayer(ctrl.layers.Length - 1);
            var baseMachine = ctrl.layers[0].stateMachine;
            foreach (var s in baseMachine.states) baseMachine.RemoveState(s.state);

            // The controller always has one base layer; the first channel takes it over.
            bool first = true;
            foreach (string ch in A320AnimationDriver.Channels)
            {
                if (!clips.TryGetValue(ch, out AnimationClip clip))
                {
                    Debug.LogWarning($"A320 animator: no clip named {ch} in {modelPath}; channel skipped.");
                    continue;
                }

                ctrl.AddParameter(ch, AnimatorControllerParameterType.Float);

                AnimatorControllerLayer layer;
                if (first)
                {
                    var layers = ctrl.layers;
                    layers[0].name = ch;
                    ctrl.layers = layers;
                    layer = layers[0];
                    first = false;
                }
                else
                {
                    layer = new AnimatorControllerLayer
                    {
                        name = ch,
                        defaultWeight = 1f,
                        blendingMode = AnimatorLayerBlendingMode.Override,
                        stateMachine = new AnimatorStateMachine { name = ch, hideFlags = HideFlags.HideInHierarchy },
                    };
                    AssetDatabase.AddObjectToAsset(layer.stateMachine, ctrl);
                    ctrl.AddLayer(layer);
                }

                var state = layer.stateMachine.AddState(ch);
                state.motion = clip;
                state.writeDefaultValues = false;
                state.timeParameterActive = true;
                state.timeParameter = ch;
                layer.stateMachine.defaultState = state;
            }

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();

            // Hook the controller up wherever a driver is waiting for it.
            int wired = 0;
            foreach (var drv in Object.FindObjectsByType<A320AnimationDriver>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var anim = drv.animator ? drv.animator : drv.GetComponentInChildren<Animator>(true);
                if (!anim) continue;
                Undo.RecordObject(anim, "Assign A320 animator controller");
                Undo.RecordObject(drv, "Assign A320 animator");
                anim.runtimeAnimatorController = ctrl;
                anim.applyRootMotion = false;
                drv.animator = anim;
                EditorUtility.SetDirty(anim);
                EditorUtility.SetDirty(drv);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(drv.gameObject.scene);
                wired++;
            }

            Debug.Log($"A320 animator: built {ctrl.layers.Length} layers from {modelPath} at " +
                      $"{ControllerPath}; assigned to {wired} animator(s).");
        }
    }

    /// The FBX exporter bakes every object into every action, so each clip arrives with ~1100
    /// curves holding the rest pose. With 21 override layers those would stamp over one another,
    /// so curves that never move are stripped on import and each clip keeps only its own parts.
    class A320ClipPostprocessor : AssetPostprocessor
    {
        const float Epsilon = 1e-5f;

        public override uint GetVersion() => 2;

        void OnPostprocessAnimation(GameObject root, AnimationClip clip)
        {
            // Any model in the A320 folder, whatever the export is named.
            if (!assetPath.StartsWith(A320AnimatorBuilder.ModelFolder + "/", System.StringComparison.OrdinalIgnoreCase)) return;

            // A property group (e.g. m_LocalRotation.xyzw) is kept whole if any component moves.
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var moving = new System.Collections.Generic.HashSet<string>();
            foreach (var b in bindings)
                if (!IsConstant(AnimationUtility.GetEditorCurve(clip, b)))
                    moving.Add(Group(b));

            foreach (var b in bindings)
                if (!moving.Contains(Group(b)))
                    AnimationUtility.SetEditorCurve(clip, b, null);
        }

        static string Group(EditorCurveBinding b)
        {
            string prop = b.propertyName;
            int dot = prop.LastIndexOf('.');
            return b.path + "|" + b.type.Name + "|" + (dot >= 0 ? prop.Substring(0, dot) : prop);
        }

        static bool IsConstant(AnimationCurve c)
        {
            if (c == null || c.length == 0) return true;
            float v0 = c.keys[0].value;
            foreach (var k in c.keys)
                if (Mathf.Abs(k.value - v0) > Epsilon) return false;
            return true;
        }
    }
}
