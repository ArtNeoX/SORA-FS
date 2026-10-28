using System;
using System.Collections;
using System.Collections.Generic;
using CirrusFS.Geo;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace CirrusFS.Sim
{
    /// Keeps the CAMERA near Unity (0,0,0) on a round Earth.
    ///
    /// When the camera gets more than `thresholdMetres` from the origin, GeoOrigin is moved to
    /// the camera's ECEF position and its axes are re-aligned to the local east/up/north there.
    /// That is a translation AND a rotation of the whole Unity frame, so every scene root is
    /// carried through the same GeoFrameChange: position, rotation, and for rigidbodies the
    /// linear and angular velocity too. Objects that rebuild themselves from ECEF (the ocean,
    /// GeoBody) also listen to GeoOrigin.Recentered and re-derive their own state.
    ///
    /// Why the camera and not the aircraft: precision only matters where something is being
    /// looked at closely, and in the external view the camera can be 50,000 km from the
    /// aircraft. The aircraft's own position survives that because GeoBody keeps it in double.
    ///
    /// The care points carried over from the flat version:
    ///  - Interpolation is switched off for the teleport and restored after the next physics
    ///    step, or the body smears across the shift for one frame.
    ///  - Physics.SyncTransforms() runs right after, so the next raycast (landing gear) does
    ///    not query colliders at their pre-shift pose.
    ///
    /// Runs after the camera rig (which positions the camera in LateUpdate) and before the
    /// clip-plane and ocean updates, so the frame that renders is already re-centred.
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public class FloatingOriginManager : MonoBehaviour
    {
        [Serializable] public class RecenterEvent : UnityEvent<int> { }

        [Header("Tracking")]
        [Tooltip("The camera whose position decides re-centring. Defaults to Camera.main.")]
        public Transform trackedCamera;

        [Tooltip("The aircraft. Set by the session at runtime; its rigidbodies are carried with " +
                 "their velocities intact.")]
        public Transform trackedAircraft;

        [Tooltip("Distance of the camera from Unity (0,0,0) that triggers a re-centre, metres.")]
        public float thresholdMetres = 5000f;

        [Header("Behaviour")]
        [Tooltip("Carry every root object in the scene except those marked FloatingOriginIgnore.")]
        public bool autoCollectSceneRoots = true;

        [Tooltip("Clear trails on the aircraft so they do not draw a streak back to the old origin.")]
        public bool clearTrailsOnShift = true;

        public bool logShifts;

        [Header("Events")]
        [Tooltip("Fired after a re-centre with the new re-centre count.")]
        public RecenterEvent onRecentered = new RecenterEvent();

        public int RecenterCount => GeoOrigin.RecenterCount;

        readonly List<Rigidbody> dynamicBodies = new List<Rigidbody>();
        readonly List<Rigidbody> bodiesToRestore = new List<Rigidbody>();
        readonly List<RigidbodyInterpolation> interpolationToRestore = new List<RigidbodyInterpolation>();
        readonly List<Transform> shiftTargets = new List<Transform>();

        TrailRenderer[] aircraftTrails = Array.Empty<TrailRenderer>();
        Coroutine restoreRoutine;

        // ---- setup ---------------------------------------------------------------

        public void SetTrackedAircraft(Transform aircraft)
        {
            trackedAircraft = aircraft;
            dynamicBodies.Clear();
            aircraftTrails = Array.Empty<TrailRenderer>();

            if (aircraft != null)
            {
                dynamicBodies.AddRange(aircraft.root.GetComponentsInChildren<Rigidbody>(true));
                aircraftTrails = aircraft.root.GetComponentsInChildren<TrailRenderer>(true);
            }
        }

        public void RegisterDynamicBody(Rigidbody body)
        {
            if (body != null && !dynamicBodies.Contains(body)) dynamicBodies.Add(body);
        }

        public void UnregisterDynamicBody(Rigidbody body) => dynamicBodies.Remove(body);

        Transform ResolveCamera()
        {
            if (trackedCamera != null) return trackedCamera;
            Camera main = Camera.main;
            return main != null ? main.transform : null;
        }

        // ---- the check -----------------------------------------------------------

        void LateUpdate()
        {
            if (!GeoOrigin.IsSet) return;

            Transform cam = ResolveCamera();
            if (cam == null) return;

            if (cam.position.sqrMagnitude < thresholdMetres * thresholdMetres) return;

            RecenterOn(GeoOrigin.UnityToEcef(cam.position));
        }

        // ---- the re-centre -------------------------------------------------------

        /// Moves the origin to `ecef` and carries the whole scene with it. Public so a test or
        /// a teleport can force one.
        public void RecenterOn(Double3 ecef)
        {
            CollectShiftTargets();

            // 1. Freeze interpolation and capture velocities while they are still in the old frame.
            bodiesToRestore.Clear();
            interpolationToRestore.Clear();

            // The physics pose is captured separately from the transform: with interpolation the
            // transform lags the physics pose by up to a step, and moving the body from the
            // transform would throw that fraction of a step's travel away on every re-centre.
            var linear = new Vector3[dynamicBodies.Count];
            var angular = new Vector3[dynamicBodies.Count];
            var physicsPosition = new Vector3[dynamicBodies.Count];
            var physicsRotation = new Quaternion[dynamicBodies.Count];

            for (int i = 0; i < dynamicBodies.Count; i++)
            {
                Rigidbody body = dynamicBodies[i];
                if (body == null) continue;

                linear[i] = body.linearVelocity;
                angular[i] = body.angularVelocity;
                physicsPosition[i] = body.position;
                physicsRotation[i] = body.rotation;

                if (body.interpolation != RigidbodyInterpolation.None)
                {
                    bodiesToRestore.Add(body);
                    interpolationToRestore.Add(body.interpolation);
                    body.interpolation = RigidbodyInterpolation.None;
                }
            }

            // 2. Move the origin. Listeners that live in ECEF (ocean, GeoBody) update here.
            GeoFrameChange change = GeoOrigin.Recenter(ecef);

            // 3. Carry every root through the same rigid transform. This frame renders from these
            //    transforms, so an interpolated body keeps its interpolated look.
            for (int i = 0; i < shiftTargets.Count; i++)
            {
                Transform t = shiftTargets[i];
                t.SetPositionAndRotation(change.TransformPoint(t.position), change.TransformRotation(t.rotation));
            }

            // 4. Push the new poses into PhysX before anything raycasts...
            Physics.SyncTransforms();

            // 5. ...then put each body's true physics pose back on top of the transform-derived
            //    one, and turn its velocities into the new axes.
            for (int i = 0; i < dynamicBodies.Count; i++)
            {
                Rigidbody body = dynamicBodies[i];
                if (body == null) continue;

                body.position = change.TransformPoint(physicsPosition[i]);
                body.rotation = change.TransformRotation(physicsRotation[i]);
                body.linearVelocity = change.TransformDirection(linear[i]);
                body.angularVelocity = change.TransformDirection(angular[i]);
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

            onRecentered.Invoke(GeoOrigin.RecenterCount);

            if (logShifts)
            {
                Debug.Log($"[FloatingOrigin] Re-centre #{GeoOrigin.RecenterCount}: moved " +
                          $"{change.Translation.Magnitude / 1000.0:F2} km, turned {change.AngleDeg:F4} deg, " +
                          $"{shiftTargets.Count} roots. {GeoOrigin.Describe()}");
            }
        }

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
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    Scene scene = SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;

                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        if (ShouldShift(root)) shiftTargets.Add(root.transform);
                    }
                }
            }

            // The camera is carried even if it is marked ignore: it was placed this frame in the
            // old frame and has to render in the new one.
            Transform cam = ResolveCamera();
            if (cam != null && !IsCoveredByTarget(cam)) shiftTargets.Add(cam.root);

            if (trackedAircraft != null && !IsCoveredByTarget(trackedAircraft)) shiftTargets.Add(trackedAircraft.root);
        }

        bool ShouldShift(GameObject root)
        {
            if (root == gameObject) return false;
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

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.27f, 0.6f, 1f, 0.6f);
            Gizmos.DrawWireSphere(Vector3.zero, thresholdMetres);
        }
    }
}
