using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace CirrusFS.Sim
{
    /// Keeps the aircraft near (0, 0, 0) by moving the world instead of the aircraft.
    ///
    /// Why it is needed: a float has ~7 significant digits, so at 10 km from the origin the
    /// spacing between representable positions is about 1 mm, and by 100 km it is a centimetre.
    /// The blade-element model integrates small forces at 240 Hz against wheel contacts of a
    /// few millimetres, so that quantisation shows up as jitter in the suspension and noise in
    /// the aero forces long before anything looks visually wrong.
    ///
    /// The shift itself is one subtraction applied to every world object, on the frame the
    /// threshold is crossed. Velocities, angular velocities and accumulated forces all live in
    /// their own vectors and are untouched by a pure translation, so the physics state carries
    /// across unchanged. The two things that do need care:
    ///
    ///  - Interpolation. A Rigidbody set to Interpolate blends between its last two physics
    ///    poses, so teleporting it makes it smear 10 km across one frame. Interpolation is
    ///    switched off for the teleport and restored after the next physics step, once the
    ///    buffer refills with post-shift poses.
    ///  - Transform sync. Physics.autoSyncTransforms is off by default, so colliders keep their
    ///    pre-shift pose until Physics.SyncTransforms() is called. Without it the first
    ///    raycast after a shift - the landing gear's, for one - queries stale geometry.
    ///
    /// Execution order runs this before the default order so the chase camera, which rebuilds
    /// its pose from the aircraft every LateUpdate, reads the already-shifted position and the
    /// shift frame renders identically to any other.
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public class FloatingOriginManager : MonoBehaviour
    {
        [Serializable] public class ShiftEvent : UnityEvent<Vector3> { }

        [Header("Tracking")]
        [Tooltip("The aircraft to keep near the origin. Set by the spawner at runtime.")]
        public Transform trackedAircraft;

        [Tooltip("Horizontal distance from the origin that triggers a shift, metres. Altitude " +
                 "is ignored: climbing does not degrade precision the way ground track does.")]
        public float thresholdMetres = 10000f;

        [Header("Behaviour")]
        [Tooltip("Keep the aircraft's altitude through a shift. With this off the aircraft is " +
                 "snapped to a literal (0, 0, 0), which also drops it to sea level.")]
        public bool preserveAltitude = true;

        [Tooltip("Shift every root object in the scene except the aircraft and anything marked " +
                 "FloatingOriginIgnore. With this off, only extraRoots and registered " +
                 "FloatingOriginShiftable objects move.")]
        public bool autoCollectSceneRoots = true;

        [Tooltip("Extra transforms to shift. Only needed when auto-collection is off.")]
        public List<Transform> extraRoots = new List<Transform>();

        [Tooltip("Clear trails on the aircraft so they do not draw a 10 km streak back to the " +
                 "old origin.")]
        public bool clearTrailsOnShift = true;

        public bool logShifts = true;

        [Header("Events")]
        [Tooltip("Fired after a shift with the translation that was applied to the world.")]
        public ShiftEvent onOriginShifted = new ShiftEvent();

        /// Total distance the world has been moved, metres. Useful as a sanity readout.
        public double TotalShiftedMetres { get; private set; }

        public int ShiftCount { get; private set; }

        readonly List<Rigidbody> dynamicBodies = new List<Rigidbody>();
        readonly List<Rigidbody> bodiesToRestore = new List<Rigidbody>();
        readonly List<RigidbodyInterpolation> interpolationToRestore = new List<RigidbodyInterpolation>();
        readonly List<Vector3> capturedLinear = new List<Vector3>();
        readonly List<Vector3> capturedAngular = new List<Vector3>();
        readonly List<Transform> shiftTargets = new List<Transform>();

        TrailRenderer[] aircraftTrails = Array.Empty<TrailRenderer>();
        Transform aircraftRoot;
        Coroutine restoreRoutine;

        // ---- setup ---------------------------------------------------------------

        /// Points the manager at a freshly spawned aircraft and caches everything on it that a
        /// shift has to touch.
        public void SetTrackedAircraft(Transform aircraft)
        {
            trackedAircraft = aircraft;
            aircraftRoot = aircraft != null ? aircraft.root : null;

            dynamicBodies.Clear();
            aircraftTrails = Array.Empty<TrailRenderer>();

            if (aircraftRoot != null)
            {
                dynamicBodies.AddRange(aircraftRoot.GetComponentsInChildren<Rigidbody>(true));
                aircraftTrails = aircraftRoot.GetComponentsInChildren<TrailRenderer>(true);
            }
        }

        /// Registers a rigidbody outside the aircraft - AI traffic, dropped stores - so it is
        /// teleported with its velocity intact rather than moved as a plain transform.
        public void RegisterDynamicBody(Rigidbody body)
        {
            if (body != null && !dynamicBodies.Contains(body)) dynamicBodies.Add(body);
        }

        public void UnregisterDynamicBody(Rigidbody body)
        {
            dynamicBodies.Remove(body);
        }

        // ---- the check -----------------------------------------------------------

        void LateUpdate()
        {
            if (trackedAircraft == null) return;

            Vector3 position = trackedAircraft.position;
            float horizontalSqr = position.x * position.x + position.z * position.z;

            if (horizontalSqr < thresholdMetres * thresholdMetres) return;

            // Move the world by the negative of where the aircraft got to, which lands the
            // aircraft on the origin exactly. Altitude is left alone by default: it costs no
            // precision and dropping it would put the aircraft at sea level mid-flight.
            var translation = new Vector3(-position.x,
                                          preserveAltitude ? 0f : -position.y,
                                          -position.z);

            ApplyShift(translation);
        }

        // ---- the shift -----------------------------------------------------------

        /// Translates the whole world by `translation` and the aircraft with it. Public so a
        /// spawn, a teleport or a test can force one.
        public void ApplyShift(Vector3 translation)
        {
            if (translation.sqrMagnitude <= Mathf.Epsilon) return;

            CollectShiftTargets();

            // 1. Freeze interpolation and note the velocities before anything moves. Each
            //    Rigidbody is only ever moved by its transform, never by both a parent move and
            //    an rb.position write, which would shift it twice.
            bodiesToRestore.Clear();
            interpolationToRestore.Clear();
            capturedLinear.Clear();
            capturedAngular.Clear();

            for (int i = 0; i < dynamicBodies.Count; i++)
            {
                Rigidbody body = dynamicBodies[i];
                capturedLinear.Add(body != null ? body.linearVelocity : Vector3.zero);
                capturedAngular.Add(body != null ? body.angularVelocity : Vector3.zero);

                if (body != null && body.interpolation != RigidbodyInterpolation.None)
                {
                    bodiesToRestore.Add(body);
                    interpolationToRestore.Add(body.interpolation);
                    body.interpolation = RigidbodyInterpolation.None;
                }
            }

            // 2. Move the world. Every target is a root of its own hierarchy, so children -
            //    including child rigidbodies - come along exactly once.
            for (int i = 0; i < shiftTargets.Count; i++)
            {
                shiftTargets[i].position += translation;
            }

            // 3. Move the aircraft. This is the same translation, which puts it back on the
            //    origin: it is the one object the world is being moved around.
            if (aircraftRoot != null) aircraftRoot.position += translation;

            // 4. Push the new poses into PhysX. Until this runs the colliders are still where
            //    they were, and the next raycast - the landing gear's, among others - would
            //    query stale geometry.
            Physics.SyncTransforms();

            // 5. A pure translation cannot change a velocity, but they are written back
            //    explicitly so this still holds if a teleport is ever resolved differently.
            for (int i = 0; i < dynamicBodies.Count; i++)
            {
                Rigidbody body = dynamicBodies[i];
                if (body == null) continue;

                body.linearVelocity = capturedLinear[i];
                body.angularVelocity = capturedAngular[i];
            }

            if (clearTrailsOnShift)
            {
                for (int i = 0; i < aircraftTrails.Length; i++)
                {
                    if (aircraftTrails[i] != null) aircraftTrails[i].Clear();
                }
            }

            if (bodiesToRestore.Count > 0 && isActiveAndEnabled)
            {
                if (restoreRoutine != null) StopCoroutine(restoreRoutine);
                restoreRoutine = StartCoroutine(RestoreInterpolation());
            }

            ShiftCount++;
            TotalShiftedMetres += translation.magnitude;

            WorldOrigin.AccumulateShift(translation);
            onOriginShifted.Invoke(translation);

            if (logShifts)
            {
                Debug.Log($"[FloatingOrigin] Shift #{ShiftCount}: world moved {translation} " +
                          $"({translation.magnitude / 1000f:F2} km), {shiftTargets.Count} roots and " +
                          $"{dynamicBodies.Count} bodies. {WorldOrigin.Describe()}");
            }
        }

        /// Interpolation is restored only after a physics step has run, so the interpolation
        /// buffer refills with post-shift poses instead of blending from the old ones.
        IEnumerator RestoreInterpolation()
        {
            yield return new WaitForFixedUpdate();

            for (int i = 0; i < bodiesToRestore.Count; i++)
            {
                if (bodiesToRestore[i] != null) bodiesToRestore[i].interpolation = interpolationToRestore[i];
            }

            bodiesToRestore.Clear();
            interpolationToRestore.Clear();
            restoreRoutine = null;
        }

        // ---- target collection ---------------------------------------------------

        void CollectShiftTargets()
        {
            shiftTargets.Clear();

            if (autoCollectSceneRoots)
            {
                Scene scene = SceneManager.GetActiveScene();
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (ShouldShift(root)) shiftTargets.Add(root.transform);
                }
            }

            for (int i = 0; i < extraRoots.Count; i++)
            {
                Transform target = extraRoots[i];
                if (target != null && !shiftTargets.Contains(target)) shiftTargets.Add(target);
            }

            IReadOnlyList<FloatingOriginShiftable> shiftables = FloatingOriginShiftable.Registered;
            for (int i = 0; i < shiftables.Count; i++)
            {
                FloatingOriginShiftable shiftable = shiftables[i];
                if (shiftable == null) continue;

                Transform target = shiftable.transform;

                // Skip anything already covered by a root that is being shifted, or it would
                // move twice.
                if (IsCoveredByTarget(target)) continue;

                shiftTargets.Add(target);

                if (shiftable.hasDynamicBodies)
                {
                    foreach (Rigidbody body in target.GetComponentsInChildren<Rigidbody>(true))
                    {
                        RegisterDynamicBody(body);
                    }
                }
            }

            // A registered body that sits outside both the aircraft and every shifted root - a
            // pooled object parented to nothing, say - would otherwise be left 10 km behind.
            for (int i = 0; i < dynamicBodies.Count; i++)
            {
                Rigidbody body = dynamicBodies[i];
                if (body == null) continue;

                Transform bodyRoot = body.transform.root;
                if (aircraftRoot != null && bodyRoot == aircraftRoot) continue;
                if (IsCoveredByTarget(bodyRoot)) continue;

                shiftTargets.Add(bodyRoot);
            }
        }

        bool ShouldShift(GameObject root)
        {
            if (root == gameObject) return false;
            if (aircraftRoot != null && root.transform == aircraftRoot) return false;
            if (root.GetComponent<FloatingOriginIgnore>() != null) return false;

            return true;
        }

        bool IsCoveredByTarget(Transform candidate)
        {
            for (int i = 0; i < shiftTargets.Count; i++)
            {
                if (candidate == shiftTargets[i] || candidate.IsChildOf(shiftTargets[i])) return true;
            }

            return false;
        }

        // ---- editor readout ------------------------------------------------------

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.27f, 0.6f, 1f, 0.6f);

            const int segments = 64;
            Vector3 previous = new Vector3(thresholdMetres, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                var next = new Vector3(Mathf.Cos(angle) * thresholdMetres, 0f,
                                       Mathf.Sin(angle) * thresholdMetres);
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
