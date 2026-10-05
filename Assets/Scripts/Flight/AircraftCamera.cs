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
    ///              radius with a world-space, mouse-owned view direction.
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
            ReadZoom(cockpit);

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
            bool looking = mouse != null && mouse.rightButton.isPressed;

            if (looking)
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
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue() * orbitSensitivity;
                orbitYaw += delta.x;
                orbitPitch = Mathf.Clamp(orbitPitch - delta.y, minPitch, maxPitch);
            }

            cam.nearClipPlane = orbitalNearClip;
            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, orbitalFov, smoothness);

            // Purely world-space: the aircraft's rotation contributes nothing to the view
            // direction, so aerobatics spin the airframe inside a steady frame instead of
            // dragging the camera around with it.
            // The offset stays body-local so the airframe keeps its place in frame; only the
            // orbit centre tracks it, never the view direction.
            Vector3 focus = target.TransformPoint(focusOffset);
            Quaternion rot = Quaternion.Euler(orbitPitch, orbitYaw, 0f);

            transform.SetPositionAndRotation(focus + rot * new Vector3(0f, 0f, -distance), rot);
        }
    }
}
