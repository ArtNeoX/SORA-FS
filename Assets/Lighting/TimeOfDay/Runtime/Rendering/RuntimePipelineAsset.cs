using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CirrusFS.TimeOfDay
{
    /// Applies a quality tier's shadow and grading settings to a runtime copy of the active URP
    /// asset, so the asset on disk (shared by every scene and quality level) is never edited.
    /// Play mode only: in the editor outside play mode the project's own asset stays in charge.
    ///
    /// The copy shares the original's renderer list, so renderer features (TimeOfDayFeature)
    /// carry over unchanged.
    public static class RuntimePipelineAsset
    {
        static UniversalRenderPipelineAsset copy;
        static RenderPipelineAsset previousQualityAsset;
        static bool installed;

        public static bool Installed => installed;

        public static void Apply(TimeOfDayQuality q)
        {
            if (q == null || !Application.isPlaying) return;
            if (!installed)
            {
                RenderPipelineAsset active = QualitySettings.renderPipeline != null
                    ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
                var source = active as UniversalRenderPipelineAsset;
                if (source == null) return;
                previousQualityAsset = QualitySettings.renderPipeline;
                copy = Object.Instantiate(source);
                copy.name = source.name + " (time of day runtime)";
                copy.hideFlags = HideFlags.DontSave;
                QualitySettings.renderPipeline = copy;
                installed = true;
            }

            copy.shadowDistance = q.shadowDistance;
            copy.shadowCascadeCount = Mathf.Clamp(q.shadowCascades, 1, 2);
            copy.cascade2Split = q.cascadeSplit;
            copy.mainLightShadowmapResolution = (int)q.shadowResolution;
            copy.colorGradingLutSize = (int)q.gradingLutSize;
        }

        /// Puts the project's asset back (on disable, and before leaving play mode).
        public static void Restore()
        {
            if (!installed) return;
            QualitySettings.renderPipeline = previousQualityAsset;
            if (copy != null) Object.Destroy(copy);
            copy = null;
            previousQualityAsset = null;
            installed = false;
        }
    }
}
