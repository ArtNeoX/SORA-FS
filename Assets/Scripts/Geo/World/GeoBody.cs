using UnityEngine;

namespace CirrusFS.Geo
{
    /// A rigidbody that lives on the globe: its position is ECEF in double, and gravity pulls
    /// it toward the Earth's centre instead of along Unity's fixed -Y.
    ///
    /// Near the origin (the normal case, camera within a few hundred metres) PhysX owns the
    /// position and this just reads it back into ECEF each step - float is ample at a few km.
    ///
    /// Far from the origin (the external camera zoomed out to orbit, origin following it) a
    /// float position can no longer resolve one physics step of travel: at 50,000 km the float
    /// spacing is 4 m against ~5 m of travel per step. So past `doublePrecisionBeyond` the
    /// body integrates its own position in double from the velocity PhysX produced, and writes
    /// the result back as the Rigidbody position every step. PhysX still owns forces, velocity
    /// and rotation, so the flight model is untouched.
    [DefaultExecutionOrder(-1000)]
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class GeoBody : MonoBehaviour
    {
        [Tooltip("Apply standard gravity (9.80665 m/s^2) toward the Earth's centre. Unity's own " +
                 "gravity is switched off on this body.")]
        public bool applyGravity = true;

        [Tooltip("Distance from the floating origin beyond which the position is integrated in " +
                 "double instead of trusted to PhysX, metres.")]
        public float doublePrecisionBeyond = 20000f;

        Rigidbody rb;

        // Position before the step about to run (set in FixedUpdate).
        Double3 ecefBeforeStep;
        bool farMode;
        bool stepped;

        /// Current position in ECEF.
        public Double3 Ecef => farMode || rb == null
            ? ecefBeforeStep
            : GeoOrigin.UnityToEcef(rb.position);

        public bool UsingDoublePrecisionIntegration => farMode;

        public Rigidbody Body => rb;

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            rb.useGravity = false;
        }

        void OnEnable()
        {
            rb.useGravity = false;
            ecefBeforeStep = GeoOrigin.UnityToEcef(rb.position);
            farMode = false;
            stepped = false;
        }

        /// Places the body at an ECEF position and attitude with a Unity-frame velocity. Used at
        /// spawn, after GeoOrigin.Reset.
        public void Teleport(Double3 ecef, Quaternion unityRotation, Vector3 unityVelocity)
        {
            if (rb == null) rb = GetComponent<Rigidbody>();

            Vector3 position = GeoOrigin.EcefToUnity(ecef);
            transform.SetPositionAndRotation(position, unityRotation);
            rb.position = position;
            rb.rotation = unityRotation;
            rb.linearVelocity = unityVelocity;
            rb.angularVelocity = Vector3.zero;

            ecefBeforeStep = ecef;
            farMode = false;
            stepped = false;
        }

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;

            if (farMode && stepped)
            {
                // PhysX moved the body by v * dt with the post-step velocity; redo that in double.
                ecefBeforeStep += GeoOrigin.UnityDirToEcef(rb.linearVelocity) * dt;
                rb.position = GeoOrigin.EcefToUnity(ecefBeforeStep);
            }
            else
            {
                ecefBeforeStep = GeoOrigin.UnityToEcef(rb.position);
            }

            stepped = true;
            farMode = GeoOrigin.EcefToUnityDouble(ecefBeforeStep).SqrMagnitude >
                      (double)doublePrecisionBeyond * doublePrecisionBeyond;

            if (applyGravity && !rb.isKinematic)
            {
                Vector3 down = GeoOrigin.EcefDirToUnity(-ecefBeforeStep.Normalized);
                rb.AddForce(down * (float)GeoMath.StandardGravity, ForceMode.Acceleration);
            }
        }
    }
}
