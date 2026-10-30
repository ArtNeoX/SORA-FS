using UnityEngine;
using UnityEngine.Rendering;

namespace CirrusFS.TimeOfDay
{
    /// Dims the sky's ambient light for everything under this object: put it on the cockpit.
    ///
    /// URP has no way to occlude ambient light inside a moving interior without baked data, so
    /// an unlit cockpit would glow with the full sky. This gives its renderers their own copy of
    /// the ambient probe scaled by `ambientScale` (CustomProvided light probes through a
    /// MaterialPropertyBlock), while the sun still lights it directly through the windows with
    /// shadows. Updates only when the ambient changes.
    ///
    /// Cost: a per-renderer property block takes those renderers out of SRP batching. Fine for
    /// a cockpit's few hundred draws; do not put it on scenery.
    [ExecuteAlways]
    [DefaultExecutionOrder(330)]
    public class InteriorAmbientZone : MonoBehaviour
    {
        [Tooltip("Fraction of the outside ambient that reaches the interior. ~0.2-0.4 for a cockpit with a large canopy.")]
        [Range(0f, 1f)] public float ambientScale = 0.3f;

        [Tooltip("Tint of the light that does get in (e.g. through tinted glass).")]
        public Color tint = Color.white;

        Renderer[] renderers;
        MaterialPropertyBlock block;
        readonly SphericalHarmonicsL2[] probe = new SphericalHarmonicsL2[1];
        int appliedVersion = -1, appliedExposure = -1;
        float appliedScale = -1f;
        Color appliedTint;

        void OnEnable()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            block ??= new MaterialPropertyBlock();
            foreach (var r in renderers) if (r != null) r.lightProbeUsage = LightProbeUsage.CustomProvided;
            appliedVersion = -1;
        }

        void OnDisable()
        {
            if (renderers == null) return;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;
                r.SetPropertyBlock(null);
            }
        }

        /// Call after adding or removing renderers under the zone at runtime.
        public void Rescan()
        {
            OnDisable();
            OnEnable();
        }

        void LateUpdate()
        {
            if (renderers == null) return;
            if (EnvironmentLighting.AmbientVersion == appliedVersion && ExposureController.Version == appliedExposure &&
                ambientScale == appliedScale && tint == appliedTint)
                return;

            SphericalHarmonicsL2 sh = EnvironmentLighting.Ambient * ExposureController.PreExposure;
            for (int i = 0; i < 9; i++)
            {
                sh[0, i] *= ambientScale * tint.r;
                sh[1, i] *= ambientScale * tint.g;
                sh[2, i] *= ambientScale * tint.b;
            }
            probe[0] = sh;

            foreach (var r in renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(block);
                block.CopySHCoefficientArraysFrom(probe);
                r.SetPropertyBlock(block);
            }
            appliedVersion = EnvironmentLighting.AmbientVersion;
            appliedExposure = ExposureController.Version;
            appliedScale = ambientScale;
            appliedTint = tint;
        }
    }
}
