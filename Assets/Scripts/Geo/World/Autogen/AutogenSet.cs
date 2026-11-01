using System;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// What autogen places on which land class. Assign the asset to GlobeAutogen.
    ///
    /// Each layer puts one model on one or more land classes (names from the exporter's
    /// class list), at a density, out to a range, with random size, yaw and texture variant.
    /// Placement is deterministic (AutogenPlacement), so the same tree is always in the same
    /// spot. Edits apply live in play mode; the layer's patches are rebuilt.
    [CreateAssetMenu(menuName = "CirrusFS/Autogen Set", fileName = "AutogenSet")]
    public class AutogenSet : ScriptableObject
    {
        [Serializable]
        public class Layer
        {
            public string name = "Trees";
            public bool enabled = true;

            [Tooltip("Land classes to place on, as named in the exporter's class list. Not case-sensitive.")]
            public string[] classNames = { "Forest" };

            [Header("Model")]
            [Tooltip("Model or prefab with one MeshFilter. Its import transform (scale, rotation) is " +
                     "kept, and its lowest point is put on the ground.")]
            public GameObject model;

            [Tooltip("Texture variants (same size and format keeps them compressed). Each object picks one at random.")]
            public Texture2D[] variants = Array.Empty<Texture2D>();

            [Tooltip("Material with the CirrusFS/AutogenFoliage shader (cutoff, lighting, texture detail by distance).")]
            public Material material;

            [Header("Density")]
            [Tooltip("Objects per hectare (100 × 100 m) where the class covers the ground fully.")]
            [Min(0f)] public float densityPerHectare = 40f;

            [Header("Range")]
            [Tooltip("Full density out to this distance from the camera, metres.")]
            [Min(0f)] public float fullDensityRange = 1500f;

            [Tooltip("Nothing beyond this distance, metres. Between the two ranges objects thin out, " +
                     "each shrinking away rather than popping.")]
            [Min(10f)] public float range = 4000f;

            [Header("Variation")]
            [Tooltip("Random size, as a multiple of the model's size (min, max).")]
            public Vector2 scaleRange = new Vector2(0.7f, 1.2f);

            [Tooltip("Random rotation about the vertical axis.")]
            public bool randomYaw = true;

            [Tooltip("± brightness per object.")]
            [Range(0f, 0.5f)] public float tintVariation = 0.12f;

            [Tooltip("Metres pushed into the ground, so objects on a slope don't float on the downhill side.")]
            [Min(0f)] public float sink = 0.4f;

            [Header("Rendering")]
            public bool castShadows = true;
        }

        public Layer[] layers = { new Layer() };

        /// Raised when the asset is edited, so a running GlobeAutogen rebuilds.
        public event Action Changed;

        void OnValidate()
        {
            foreach (Layer layer in layers ?? Array.Empty<Layer>())
            {
                if (layer == null) continue;
                layer.range = Mathf.Max(layer.range, 10f);
                layer.fullDensityRange = Mathf.Clamp(layer.fullDensityRange, 0f, layer.range);
                layer.scaleRange.x = Mathf.Max(0.01f, layer.scaleRange.x);
                layer.scaleRange.y = Mathf.Max(layer.scaleRange.x, layer.scaleRange.y);
            }
            Changed?.Invoke();
        }
    }
}
