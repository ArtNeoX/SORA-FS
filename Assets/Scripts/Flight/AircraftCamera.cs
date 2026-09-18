using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.Flight
{
    /// Orbit camera locked to a fixed radius, with scroll-wheel FOV zoom.
    /// It follows the aircraft's position only: the view direction is world-space and owned
    /// entirely by the mouse, so the airframe's pitch, yaw and roll never swing the camera.
    /// Hold the right mouse button to orbit.
    [RequireComponent(typeof(Camera))]
    public class AircraftCamera : MonoBehaviour
    {
        public Transform target;
        [Tooltip("Aim point in aircraft-local metres.")]
        public Vector3 focusOffset = new Vector3(0f, 0.5f, -4.4f);
        public float distance = 28f;

        [Header("FOV zoom")]
        public float minFov = 5f;
        public float maxFov = 70f;
        public float fovIncrement = 5f;
        [Range(0f, 1f)] public float smoothness = 0.15f;

        [Header("Orbit")]
        public float orbitSensitivity = 0.2f;
        public float minPitch = -85f;
        public float maxPitch = 85f;

        Camera cam;
        float targetFov;
        float orbitYaw;
        float orbitPitch = 12f;

        void Awake()
        {
            cam = GetComponent<Camera>();
            targetFov = cam.fieldOfView;
        }

        void LateUpdate()
        {
            if (!target) return;

            var mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll > 0f) targetFov = Mathf.Max(minFov, targetFov - fovIncrement);
                else if (scroll < 0f) targetFov = Mathf.Min(maxFov, targetFov + fovIncrement);

                if (mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue() * orbitSensitivity;
                    orbitYaw += delta.x;
                    orbitPitch = Mathf.Clamp(orbitPitch - delta.y, minPitch, maxPitch);
                }
            }

            cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, targetFov, smoothness);

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

