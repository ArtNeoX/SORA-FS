using CirrusFS.Geo;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.Flight
{
    /// Two-position camera for the aircraft, toggled with V.
    ///
    ///   Cockpit  - rigidly bolted to the Views/Cockpit anchor inside the airframe, looking
    ///              along the anchor's +Z. Hold the right mouse button to turn your head:
    ///              the position never moves, only the view direction.
    ///   Orbital  - the original chase view: follows the aircraft's position at a fixed
    ///              radius with a mouse-owned view direction. Yaw and pitch are measured in the
    ///              local horizon frame at the aircraft (not Unity's axes, which turn with every
    ///              floating-origin re-centre), so the view holds still over the globe.
    ///              "+" / "-" (and the numpad keys) multiply / divide the distance by
    ///              `zoomStep`, from `minDistance` out to `maxDistance`. Past a few hundred
    ///              kilometres the aim drifts from the aircraft to the Earth's centre, so at
    ///              full distance the whole globe sits in the middle of the frame.
    ///
    /// Cockpit is the default. The two modes keep their own FOV and near clip, so switching
    /// back and forth does not carry one view's zoom into the other.
    [RequireComponent(typeof(Camera))]
    public class AircraftCamera : MonoBehaviour
    {
        public enum ViewMode
        {
            Cockpit,
            Orbital
        }

        public Transform target;
        [Tooltip("Aim point in aircraft-local metres. Orbital view only.")]
        public Vector3 focusOffset = new Vector3(0f, 0.5f, -4.4f);
        public float distance = 330f;

        [Header("Mode")]
        [Tooltip("View the camera starts in.")]
        public ViewMode startMode = ViewMode.Cockpit;
        [Tooltip("Key that toggles cockpit / orbital.")]
        public Key toggleKey = Key.V;

        [Header("FOV zoom")]
        public float minFov = 5f;
        public float maxFov = 70f;
        public float fovIncrement = 5f;
        [Range(0f, 1f)] public float smoothness = 0.15f;

        [Header("Orbit distance")]
        [Tooltip("Factor applied per + / - press.")]
        public float zoomStep = 1.5f;
        public float minDistance = 10f;
        [Tooltip("50,000 km: far enough to see the whole Earth.")]
        public float maxDistance = 5.0e7f;
        [Tooltip("Distance at which the aim starts moving from the aircraft to the Earth's centre, m.")]
        public float globeAimStart = 2.0e5f;
        [Tooltip("Distance by which the aim is fully on the Earth's centre, m.")]
        public float globeAimEnd = 2.0e7f;

        [Header("Orbit")]
        public float orbitSensitivity = 0.2f;
        public float minPitch = -85f;
        public float maxPitch = 85f;

        [Header("Cockpit")]
        [Tooltip("Path to the eye anchor under the aircraft root. The camera sits on it and " +
                 "looks along its +Z.")]
        public string cockpitViewPath = "Views/Cockpit";

        [Tooltip("Overrides the path lookup when it is set and sits under the target.")]
        public Transform cockpitView;

        [Tooltip("Field of view the cockpit starts at. Scroll still zooms from there.")]
        public float cockpitFov = 60f;

        [Tooltip("Near clip while in the cockpit. Small, or the panel is sliced off.")]
        public float cockpitNearClip = 0.03f;

        [Header("Cockpit head look")]
        [Tooltip("Degrees of head turn per unit of mouse delta.")]
        public float lookSensitivity = 0.15f;

        [Tooltip("How quickly the head catches up to the mouse. Same 0..1 feel as the FOV " +
                 "smoothing: lower is heavier.")]
        [Range(0.01f, 1f)] public float lookSmoothness = 0.2f;

        [Tooltip("Head turn limits relative to straight ahead, degrees.")]
        public float maxLookYaw = 160f;
        public float minLookPitch = -75f;
        public float maxLookPitch = 75f;

        [Tooltip("Let go of the right mouse button and the head swings back to forward.")]
        public bool recenterOnRelease = true;

        [Tooltip("Smoothing used for that swing back. Usually softer than the look-around " +
                 "value so the head settles rather than snaps.")]
        [Range(0.01f, 1f)] public float recenterSmoothness = 0.08f;

        /// Selected view. Cockpit falls back to orbital while the target has no eye anchor.
        public ViewMode Mode { get; private set; }

        Camera cam;

        // Per-mode FOV, so the cockpit's zoom and the chase view's zoom are independent.
        float orbitalFov;
        float cockpitTargetFov;
        float orbitalNearClip;

        // Orbital state: world-space, mouse-owned.
        float orbitYaw;
        float orbitPitch = 12f;

        // Cockpit head state: offsets from the anchor's forward, in anchor-local degrees.
        float headYawTarget;
        float headPitchTarget;
        float headYaw;
        float headPitch;

        Transform resolvedCockpitView;
        Transform resolvedFor;
        bool warnedMissingAnchor;

        AircraftController targetController;
        Transform controllerFor;

        void Awake()
        {
            cam = GetComponent<Camera>();
            orbitalFov = cam.fieldOfView;
            orbitalNearClip = cam.nearClipPlane;
            cockpitTargetFov = cockpitFov;

            Mode = startMode;
            if (Mode == ViewMode.Cockpit) cam.fieldOfView = cockpitTargetFov;
        }

        void LateUpdate()
        {
            if (!target) return;

            ResolveCockpitView();
            ReadToggle();

            bool cockpit = Mode == ViewMode.Cockpit && resolvedCockpitView != null;
            // Exposure meters a dark panel differently from the outside; tell it which view this is.
            var exposure = CirrusFS.TimeOfDay.ExposureController.Active;
            if (exposure != null) exposure.inCockpit = cockpit;
            ReadZoom(cockpit);
            if (!cockpit) ReadDistanceKeys();

            if (cockpit) UpdateCockpit();
            else UpdateOrbital();
        }

        // ---- mode ----------------------------------------------------------------

        public void ToggleMode() =>
            SetMode(Mode == ViewMode.Cockpit ? ViewMode.Orbital : ViewMode.Cockpit);

        /// Snaps FOV to the incoming mode's zoom instead of easing into it, so the switch does
        /// not sweep through every angle in between on the way.
        public void SetMode(ViewMode mode)
        {
            if (Mode == mode) return;

            Mode = mode;
            headYaw = headPitch = headYawTarget = headPitchTarget = 0f;

            if (cam != null)
                cam.fieldOfView = mode == ViewMode.Cockpit ? cockpitTargetFov : orbitalFov;
        }

        /// The right button belongs to the mouse box while it is being dragged.
        bool MouseBoxDragging()
        {
            if (controllerFor != target)
            {
                controllerFor = target;
                targetController = target ? target.GetComponent<AircraftController>() : null;
            }
            return targetController && targetController.MouseBoxDragging;
        }

        void ReadToggle()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[toggleKey].wasPressedThisFrame) ToggleMode();
        }

        // ---- shared zoom ---------------------------------------------------------

        void ReadZoom(bool cockpit)
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            float scroll = mouse.scroll.ReadValue().y;
            if (scroll == 0f) return;

            float fov = cockpit ? cockpitTargetFov : orbitalFov;
            fov = scroll > 0f
                ? Mathf.Max(minFov, fov - fovIncrement)
                : Mathf.Min(maxFov, fov + fovIncrement);

            if (cockpit) cockpitTargetFov = fov;
            else orbitalFov = fov;
        }

        /// "+" / "-" on the main row ("=" is the unshifted "+") or the numpad.
        void ReadDistanceKeys()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.equalsKey.wasPressedThisFrame || keyboard.numpadPlusKey.wasPressedThisFrame)
                distance /= zoomStep;
            if (keyboard.minusKey.wasPressedThisFrame || keyboard.numpadMinusKey.wasPressedThisFrame)
                distance *= zoomStep;

            distance = Mathf.Clamp(distance, minDistance, maxDistance);
        }

        // ---- cockpit -------------------------------------------------------------

        /// Finds the eye anchor once per target. An explicitly assigned cockpitView wins;
        /// otherwise the path is looked up under the aircraft root.
        void ResolveCockpitView()
        {
            if (resolvedFor == target) return;

            resolvedFor = target;
            warnedMissingAnchor = false;

            if (cockpitView != null && cockpitView.IsChildOf(target))
            {
                resolvedCockpitView = cockpitView;
                return;
            }

            resolvedCockpitView = string.IsNullOrEmpty(cockpitViewPath)
                ? null
                : target.Find(cockpitViewPath);
        }

        void UpdateCockpit()
        {
            var mouse = Mouse.current;
            bool dragging = MouseBoxDragging();
            bool looking = mouse != null && mouse.rightButton.isPressed && !dragging;

            if (dragging)
            {
                // Hold the head where it is while the mouse box moves.
            }
            else if (looking)
            {
                Vector2 delta = mouse.delta.ReadValue() * lookSensitivity;
                headYawTarget = Mathf.Clamp(headYawTarget + delta.x, -maxLookYaw, maxLookYaw);
                headPitchTarget = Mathf.Clamp(headPitchTarget - delta.y, minLookPitch, maxLookPitch);
            }
            else if (recenterOnRelease)
            {
                headYawTarget = 0f;
                headPitchTarget = 0f;
            }

            float ease = looking || !recenterOnRelease ? lookSmoothness : recenterSmoothness;
            headYaw = Mathf.Lerp(headYaw, headYawTarget, ease);
            headPitch = Mathf.Lerp(headPitch, headPitchTarget, ease);

            cam.nearClipPlane = cockpitNearClip;
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, cockpitTargetFov, smoothness);

            // The head rides the airframe exactly - no lag on roll, pitch or yaw, or the
            // horizon detaches from the canopy frame. Only the look-around offset is eased.
            transform.SetPositionAndRotation(
                resolvedCockpitView.position,
                resolvedCockpitView.rotation * Quaternion.Euler(headPitch, headYaw, 0f));
        }

        // ---- orbital -------------------------------------------------------------

        void UpdateOrbital()
        {
            if (Mode == ViewMode.Cockpit && !warnedMissingAnchor)
            {
                warnedMissingAnchor = true;
                Debug.LogWarning($"[AircraftCamera] '{target.name}' has no '{cockpitViewPath}' " +
                                 "anchor; falling back to the orbital view.", target);
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed && !MouseBoxDragging())
            {
                Vector2 delta = mouse.delta.ReadValue() * orbitSensitivity;
                orbitYaw += delta.x;
                orbitPitch = Mathf.Clamp(orbitPitch - delta.y, minPitch, maxPitch);
            }

            // Near plane grows with distance so depth precision follows the view out to orbit,
            // but always stays well inside the aircraft's own distance.
            cam.nearClipPlane = Mathf.Max(orbitalNearClip, distance * 0.02f);
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, orbitalFov, smoothness);

            // The aircraft's rotation contributes nothing to the view direction, so aerobatics
            // spin the airframe inside a steady frame instead of dragging the camera around
            // with it. That frame is the local horizon at the aircraft.
            // The offset stays body-local so the airframe keeps its place in frame; only the
            // orbit centre tracks it, never the view direction.
            Vector3 focus = target.TransformPoint(focusOffset);
            Quaternion horizon = GeoOrigin.EnuRotationAt(focus);
            Quaternion rot = horizon * Quaternion.Euler(orbitPitch, orbitYaw, 0f);
            Vector3 position = focus + rot * new Vector3(0f, 0f, -distance);

            // Far out, aim progressively at the Earth's centre so the globe is framed whole.
            float aimT = Mathf.InverseLerp(Mathf.Log(globeAimStart), Mathf.Log(globeAimEnd),
                                           Mathf.Log(Mathf.Max(distance, 1f)));
            aimT = aimT * aimT * (3f - 2f * aimT);
            if (aimT > 0f)
            {
                Vector3 toFocus = (focus - position).normalized;
                Vector3 toCentre = (GeoOrigin.EcefToUnity(Double3.Zero) - position).normalized;
                Vector3 aim = Vector3.Slerp(toFocus, toCentre, aimT);
                rot = Quaternion.LookRotation(aim, horizon * Vector3.up);
            }

            transform.SetPositionAndRotation(position, rot);
        }
    }
}
