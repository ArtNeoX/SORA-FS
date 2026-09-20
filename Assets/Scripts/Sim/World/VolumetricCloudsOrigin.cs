using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CirrusFS.Sim
{
    /// Keeps the volumetric cloud layer still while the floating origin moves the world under it.
    ///
    /// The clouds are raymarched in planet space and, with localClouds off, anchor their noise
    /// to the camera's absolute world position:
    ///
    ///     positionPS.xz += _WorldSpaceCameraPos.xz;    // VolumetricCloudsUtilities.hlsl
    ///
    /// A shift moves the camera and the world together, so nothing changes on screen except
    /// that term, which jumps by the whole shift distance - ten kilometres by default. The
    /// noise field is sampled somewhere completely different and the entire cloud layer snaps
    /// in a single frame.
    ///
    /// The fix is to hand the shader back what the shift took away. `_CloudsOriginOffset` holds
    /// the negated sum of every shift, so the noise keeps being sampled in the world the flight
    /// started in and a shift changes nothing the raymarch can see.
    ///
    /// Two frames' worth of care beyond that:
    ///
    ///  - The offset is accumulated in double and only narrowed to float on the way to the GPU.
    ///    Accumulating in float would drift, and drift here reads as the clouds slowly sliding.
    ///    The float the shader receives still quantises: at 10,000 km of ground track its
    ///    spacing is about a metre, well under the finest erosion detail, and it degrades
    ///    smoothly rather than snapping.
    ///  - The clouds accumulate temporally, and the reprojection multiplies this frame's world
    ///    position by last frame's view-projection matrix. On a shift frame those two live in
    ///    worlds a shift apart, so the motion vector is wrong by the full shift distance, the
    ///    history lookup lands off screen and the accumulation is dropped: one frame of raw,
    ///    noisy raymarch. `_CloudsOriginShiftDelta` carries the translation for exactly that
    ///    frame so the shader can undo it before reprojecting, and is cleared once the frame
    ///    has rendered.
    ///
    /// No setup: it attaches itself to WorldOrigin at load and needs nothing in the scene.
    /// Shift compensation only applies to global clouds. With localClouds on, the layer is
    /// already anchored to the camera horizontally and a shift never moved it to begin with,
    /// so the shader leaves that path alone.
    public static class VolumetricCloudsOrigin
    {
        static readonly int originOffsetId = Shader.PropertyToID("_CloudsOriginOffset");
        static readonly int originShiftDeltaId = Shader.PropertyToID("_CloudsOriginShiftDelta");

        static double offsetX;
        static double offsetY;
        static double offsetZ;
        static int shiftFrame = -1;
        static bool hooked;

        /// Metres the cloud noise is sampled away from the current scene frame: the negated sum
        /// of every shift so far. Exposed for debug readouts.
        public static Vector3 Offset => new Vector3((float)offsetX, (float)offsetY, (float)offsetZ);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Install()
        {
            // WorldOrigin clears its Shifted event at SubsystemRegistration, which runs before
            // this, so with domain reload off we re-subscribe to a clean event rather than
            // stacking a second handler on the old one.
            offsetX = 0.0;
            offsetY = 0.0;
            offsetZ = 0.0;
            shiftFrame = -1;

            WorldOrigin.Shifted -= OnOriginShifted;
            WorldOrigin.Shifted += OnOriginShifted;

            if (!hooked)
            {
                RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
                hooked = true;
            }

            PushOffset();
            Shader.SetGlobalVector(originShiftDeltaId, Vector4.zero);
        }

        /// `worldTranslation` is what was added to every world object, so a point that was at p
        /// is now at p + translation. Sampling the noise at (position - accumulated translation)
        /// puts it back where it was.
        static void OnOriginShifted(Vector3 worldTranslation)
        {
            offsetX -= worldTranslation.x;
            offsetY -= worldTranslation.y;
            offsetZ -= worldTranslation.z;

            PushOffset();

            // Consumed by the denoise pass, for this frame only.
            shiftFrame = Time.frameCount;
            Shader.SetGlobalVector(originShiftDeltaId,
                                   new Vector4(worldTranslation.x, worldTranslation.y,
                                               worldTranslation.z, 0f));
        }

        /// Clearing the delta is gated on the frame rather than done at the end of rendering,
        /// because a frame can render more than one context - a game view and a scene view, or
        /// a reflection probe - and every one of them has to see the shift. Clearing at the
        /// start of a later frame's rendering leaves the shift frame intact and guarantees the
        /// frame after it reprojects with a zero delta, which is what it needs.
        static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (shiftFrame == -1 || Time.frameCount == shiftFrame) return;

            shiftFrame = -1;
            Shader.SetGlobalVector(originShiftDeltaId, Vector4.zero);
        }

        static void PushOffset()
        {
            Shader.SetGlobalVector(originOffsetId,
                                   new Vector4((float)offsetX, (float)offsetY, (float)offsetZ, 0f));
        }
    }
}
