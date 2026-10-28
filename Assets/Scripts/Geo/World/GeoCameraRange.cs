using UnityEngine;

namespace CirrusFS.Geo
{
    /// Sets the camera's far plane from its height above the ellipsoid, every frame.
    ///
    /// Depth strategy (one camera, no stacking):
    ///  - Reversed-Z. Unity already renders reversed-Z on D3D11/12, Vulkan and Metal, and the
    ///    URP renderer here uses a 32-bit float depth buffer, so precision is spread evenly in
    ///    log-space rather than piled up at the near plane. That alone covers a near/far ratio
    ///    of ~1e7 to 1e8 without z-fighting.
    ///  - Dynamic planes. The far plane is the distance to the geometric horizon from the
    ///    camera plus the horizon distance of the highest terrain that could poke over it
    ///    (10 km), so it is ~360 km at the water and ~51,000 km from 50,000 km out. The near
    ///    plane is the camera rig's: 3 cm in the cockpit, growing with orbit distance outside.
    ///    The ratio therefore never exceeds ~1.2e7, well inside what reversed float depth holds.
    ///
    /// A second "far" camera would only be needed if near geometry and planet-scale geometry
    /// had to share a frame at extreme ratios (cockpit at 3 cm looking at the full disc from
    /// orbit); the rig never puts the cockpit there, so one camera is enough.
    [DefaultExecutionOrder(200)]
    [RequireComponent(typeof(Camera))]
    public class GeoCameraRange : MonoBehaviour
    {
        [Tooltip("Highest surface the far plane must reach over the horizon, metres.")]
        public float maxTerrainHeight = 10000f;

        [Tooltip("Extra margin on the far plane.")]
        public float farMargin = 1.05f;

        [Tooltip("Smallest far plane, metres.")]
        public float minFar = 20000f;

        [Tooltip("Largest allowed far / near ratio. Near is pushed out to respect it.")]
        public float maxDepthRatio = 1.0e8f;

        Camera cam;

        /// Camera height above the ellipsoid, metres, as of the last update.
        public double CameraHeight { get; private set; }

        void Awake() => cam = GetComponent<Camera>();

        void LateUpdate()
        {
            if (!GeoOrigin.IsSet) return;

            CameraHeight = GeoOrigin.HeightAt(transform.position);

            double far = (GeoMath.HorizonDistance(CameraHeight) + GeoMath.HorizonDistance(maxTerrainHeight)) * farMargin;
            cam.farClipPlane = Mathf.Max(minFar, (float)far);

            float minNear = cam.farClipPlane / maxDepthRatio;
            if (cam.nearClipPlane < minNear) cam.nearClipPlane = minNear;
        }
    }
}
