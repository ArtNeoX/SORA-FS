using System;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// Ground textures for the terrain's land classes. Assign the asset to GlobeTerrain.
    ///
    /// Each entry names a class from the exporter's class list (manifest.json) and gives it a
    /// tiling albedo texture. Classes without an entry stay flat colour. Textures are laid on
    /// a conformal (Mercator) grid anchored to the Earth, so they never slide with the
    /// floating origin and are never stretched; their size is true at `referenceLatitude`
    /// and shrinks by cos(ref)/cos(lat) away from it (about 5 % across Korea).
    ///
    /// Edits apply live in play mode.
    [CreateAssetMenu(menuName = "CirrusFS/Terrain Class Textures", fileName = "TerrainClassTextures")]
    public class TerrainClassTextures : ScriptableObject
    {
        public enum ColourSource
        {
            [Tooltip("Keep the class colour from the exporter; the texture adds only its light and " +
                     "dark pattern. Far away the ground is exactly the class colour.")]
            ClassColour,

            [Tooltip("Use the texture's own colours (times the tint). Far away the ground turns " +
                     "the texture's average colour.")]
            Texture,
        }

        [Serializable]
        public class Entry
        {
            [Tooltip("Class name as in the exporter's class list, e.g. \"Forest\". Not case-sensitive.")]
            public string className = "";

            [Tooltip("Tiling albedo texture (seamless). Any size and format; it is resampled to " +
                     "the set's texture size.")]
            public Texture2D albedo;

            [Tooltip("Ground size of one repeat of the texture, metres.")]
            [Min(0.5f)] public float sizeMetres = 16f;

            public ColourSource colourFrom = ColourSource.ClassColour;

            [Tooltip("Multiplies the result (white = unchanged).")]
            public Color tint = Color.white;

            [Tooltip("0 = flat class colour, 1 = full texture.")]
            [Range(0f, 1f)] public float strength = 1f;
        }

        [Tooltip("Width and height every texture is resampled to in the GPU array.")]
        public int textureSize = 1024;

        [Tooltip("Latitude where `sizeMetres` is exact (degrees).")]
        [Range(-80f, 80f)] public float referenceLatitude = 37.5f;

        public Entry[] classes = Array.Empty<Entry>();

        [Tooltip("Texture for steep slopes (the material's rock colour band). Class name is ignored; " +
                 "ClassColour mode keeps the material's rock colour.")]
        public Entry steepSlopes = new Entry { className = "(steep slopes)", sizeMetres = 24f };

        /// Raised when the asset is edited, so a running GlobeTerrain rebuilds its array.
        public event Action Changed;

        void OnValidate()
        {
            textureSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(textureSize, 16)), 16, 4096);
            Changed?.Invoke();
        }
    }
}
