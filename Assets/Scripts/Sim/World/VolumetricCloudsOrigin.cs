using System.Collections.Generic;
using CirrusFS.Geo;
using UnityEngine;
using UnityEngine.Rendering;

namespace CirrusFS.Sim
{
    /// Keeps the volumetric cloud layer still while the floating origin re-centres.
    ///
    /// The clouds are raymarched in planet space and, with localClouds off, anchor their noise
    /// to the camera's absolute world position:
    ///
    ///     positionPS.xz += _WorldSpaceCameraPos.xz;    // VolumetricCloudsUtilities.hlsl
    ///
    /// A re-centre moves the camera back to (0,0,0), so that term would jump by the whole
    /// re-centre distance and the noise would be sampled somewhere else: the layer snaps.
    /// `_CloudsOriginOffset` hands the shader back what the re-centre took away - the vector
    /// from the spawn origin to the current origin, in the current Unity axes, computed in
    /// double from ECEF and only narrowed on the way to the GPU.
    ///
    /// The cloud shader is a flat-layer model: it does not know the axes also turn by a few
    /// hundredths of a degree per re-centre. That rotates the noise field about the camera by
    /// the same tiny angle (centimetres at cloud distance), which is invisible. A proper
    /// spherical cloud shell belongs with the terrain work.
    ///
    /// `_CloudsOriginShiftDelta` carries the translation for the re-centre frame only, so the
    /// temporal reprojection can undo it; it is cleared once that frame has rendered.
    ///
    /// Note: the volumetric clouds renderer feature is not on the active URP renderer at the
    /// moment, so this only matters once clouds are switched back on.
    public static class VolumetricCloudsOrigin
    {
        static readonly int originOffsetId = Shader.PropertyToID("_CloudsOriginOffset");
        static readonly int originShiftDeltaId = Shader.PropertyToID("_CloudsOriginShiftDelta");

        static Double3 spawnOrigin;
        static Vector3 offset;
        static int shiftFrame = -1;
        static bool hooked;

        /// Metres the cloud noise is sampled away from the current scene frame. Exposed for
        /// debug readouts.
        public static Vector3 Offset => offset;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            // GeoOrigin clears its events at SubsystemRegistration, which runs before this, so
            // with domain reload off we re-subscribe to clean events.
            spawnOrigin = GeoOrigin.Ecef;
            offset = Vector3.zero;
            shiftFrame = -1;

            GeoOrigin.Recentered -= OnRecentered;
            GeoOrigin.Recentered += OnRecentered;
            GeoOrigin.OriginReset -= OnOriginReset;
            GeoOrigin.OriginReset += OnOriginReset;

            if (!hooked)
            {
                RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
                hooked = true;
            }

            PushOffset();
            Shader.SetGlobalVector(originShiftDeltaId, Vector4.zero);
        }

        static void OnOriginReset()
        {
            spawnOrigin = GeoOrigin.Ecef;
            offset = Vector3.zero;
            PushOffset();
        }

        static void OnRecentered(GeoFrameChange change)
        {
            offset = GeoOrigin.EcefDirToUnity(GeoOrigin.Ecef - spawnOrigin);
            PushOffset();

            // Consumed by the denoise pass, for this frame only.
            shiftFrame = Time.frameCount;
            Vector3 t = change.Translation.ToVector3();
            Shader.SetGlobalVector(originShiftDeltaId, new Vector4(t.x, t.y, t.z, 0f));
        }

        /// Cleared at the start of a later frame's rendering rather than at the end of this
        /// one, because a frame can render several contexts and every one must see the shift.
        static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (shiftFrame == -1 || Time.frameCount == shiftFrame) return;

            shiftFrame = -1;
            Shader.SetGlobalVector(originShiftDeltaId, Vector4.zero);
        }

        static void PushOffset()
        {
            Shader.SetGlobalVector(originOffsetId, new Vector4(offset.x, offset.y, offset.z, 0f));
        }
    }
}
