using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.Geo
{
    /// Geodetic readout and world mini-map, toggled with F1.
    ///
    /// Everything shown is read from the coordinate core: position from the target's GeoBody
    /// (ECEF, double), lat/lon/height from GeoMath, heading and ground speed in the local
    /// frame at the aircraft. The map is an equirectangular image (NASA Blue Marble, bundled,
    /// no network): longitude maps linearly to x and latitude to y, so the marker is exact at
    /// any zoom. Scroll over the map, or use its buttons, to zoom; it stays centred on the
    /// aircraft once zoomed in.
    [DefaultExecutionOrder(400)]
    public class GeoDebugWindow : MonoBehaviour
    {
        [Tooltip("The aircraft. Set at runtime by the session.")]
        public Transform target;

        [Tooltip("Camera for the distance readout. Defaults to Camera.main.")]
        public Camera viewCamera;

        [Tooltip("Equirectangular world image, 2:1, -180..180 lon left to right, 90..-90 lat " +
                 "top to bottom.")]
        public Texture2D worldMap;

        public Key toggleKey = Key.F1;
        public bool visible;

        [Header("Layout")]
        public Rect windowRect = new Rect(20f, 20f, 440f, 520f);
        public float maxZoom = 256f;

        float zoom = 1f;
        Texture2D whitePixel;
        GUIStyle labelStyle;
        readonly StringBuilder text = new StringBuilder(512);

        // Sampled in LateUpdate so the readout matches the rendered frame.
        double lat, lon, height, headingDeg, groundSpeed, cameraDistance;
        Double3 ecef;
        bool hasTarget;

        const int WindowId = 0x6E0;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        void Awake()
        {
            whitePixel = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
            whitePixel.SetPixel(0, 0, Color.white);
            whitePixel.Apply();
        }

        void OnDestroy()
        {
            if (whitePixel != null) Destroy(whitePixel);
        }

        void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[toggleKey].wasPressedThisFrame) visible = !visible;
        }

        void LateUpdate()
        {
            hasTarget = target != null && GeoOrigin.IsSet;
            if (!visible || !hasTarget) return;

            var body = target.GetComponent<GeoBody>();
            ecef = body != null ? body.Ecef : GeoOrigin.UnityToEcef(target.position);
            GeoMath.EcefToGeodetic(ecef, out lat, out lon, out height);

            Vector3 position = target.position;
            Quaternion toLocal = Quaternion.Inverse(GeoOrigin.EnuRotationAt(position));
            Vector3 forward = toLocal * target.forward;
            headingDeg = GeoMath.WrapHeading(Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg);

            Rigidbody rb = body != null ? body.Body : target.GetComponent<Rigidbody>();
            Vector3 velocity = rb != null ? toLocal * rb.linearVelocity : Vector3.zero;
            groundSpeed = new Vector2(velocity.x, velocity.z).magnitude;

            Camera cam = viewCamera != null ? viewCamera : Camera.main;
            cameraDistance = cam != null ? Vector3.Distance(cam.transform.position, position) : 0.0;
        }

        void OnGUI()
        {
            if (!visible) return;

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = false };
                labelStyle.normal.textColor = new Color(0.85f, 0.95f, 1f);
            }

            // First show: top-right, clear of the flight HUD readout in the top-left.
            if (!placed)
            {
                windowRect.x = Mathf.Max(0f, Screen.width - windowRect.width - 20f);
                placed = true;
            }

            windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "Globe debug  (F1)");
        }

        bool placed;

        void DrawWindow(int id)
        {
            const float pad = 10f;
            float width = windowRect.width - pad * 2f;

            GUI.Label(new Rect(pad, 22f, width, 200f), BuildText(), labelStyle);

            var mapRect = new Rect(pad, 228f, width, width * 0.5f);
            DrawMap(mapRect);

            float y = mapRect.yMax + 6f;
            if (GUI.Button(new Rect(pad, y, 32f, 22f), "+")) zoom = Mathf.Min(maxZoom, zoom * 2f);
            if (GUI.Button(new Rect(pad + 36f, y, 32f, 22f), "-")) zoom = Mathf.Max(1f, zoom / 2f);
            if (GUI.Button(new Rect(pad + 72f, y, 60f, 22f), "World")) zoom = 1f;
            GUI.Label(new Rect(pad + 140f, y + 2f, 200f, 22f), $"zoom x{zoom:0}   (scroll on map)", labelStyle);

            windowRect.height = y + 32f;
            GUI.DragWindow(new Rect(0f, 0f, windowRect.width, 20f));
        }

        string BuildText()
        {
            text.Clear();

            if (!hasTarget)
            {
                text.Append("No aircraft.\n");
                text.Append(GeoOrigin.Describe());
                return text.ToString();
            }

            text.AppendFormat(Inv, "Lat   {0,12:F7}   {1}\n", lat, GeoMath.FormatDms(lat, true));
            text.AppendFormat(Inv, "Lon   {0,12:F7}   {1}\n", lon, GeoMath.FormatDms(lon, false));
            text.AppendFormat(Inv, "Alt   {0:F1} m  ({1:F0} ft)  above WGS84 ellipsoid\n", height, height * 3.28084);
            text.AppendFormat(Inv, "Hdg   {0:F1}° true      GS  {1:F1} kt  ({2:F1} m/s)\n",
                              headingDeg, groundSpeed * 1.943844, groundSpeed);
            text.AppendFormat(Inv, "ECEF  x {0:F2}\n      y {1:F2}\n      z {2:F2}\n", ecef.x, ecef.y, ecef.z);
            text.AppendFormat(Inv, "Camera distance  {0}\n", FormatDistance(cameraDistance));
            text.AppendFormat(Inv, "Origin re-centres  {0}   (origin {1:F4}, {2:F4})",
                              GeoOrigin.RecenterCount, GeoOrigin.Latitude, GeoOrigin.Longitude);

            return text.ToString();
        }

        static string FormatDistance(double metres) =>
            metres < 10000.0 ? $"{metres:F1} m" : $"{metres / 1000.0:N0} km";

        // ---- map -----------------------------------------------------------------

        void DrawMap(Rect rect)
        {
            Event e = Event.current;
            if (e.type == EventType.ScrollWheel && rect.Contains(e.mousePosition))
            {
                zoom = Mathf.Clamp(zoom * (e.delta.y < 0f ? 1.5f : 1f / 1.5f), 1f, maxZoom);
                e.Use();
            }

            GUI.color = new Color(0.02f, 0.05f, 0.1f);
            GUI.DrawTexture(rect, whitePixel);
            GUI.color = Color.white;

            // Aircraft in map UV: u east from -180, v north from -90 (GUI texcoords run bottom-up).
            float u = hasTarget ? (float)((lon + 180.0) / 360.0) : 0.5f;
            float v = hasTarget ? (float)((lat + 90.0) / 180.0) : 0.5f;

            float spanU = 1f / zoom, spanV = 1f / zoom;
            float u0 = zoom <= 1f ? 0f : u - spanU * 0.5f;
            float v0 = zoom <= 1f ? 0f : Mathf.Clamp(v - spanV * 0.5f, 0f, 1f - spanV);

            if (worldMap != null)
            {
                GUI.DrawTextureWithTexCoords(rect, worldMap, new Rect(u0, v0, spanU, spanV));
            }
            else
            {
                GUI.Label(rect, "  No world map assigned.", labelStyle);
            }

            if (!hasTarget) return;

            // Marker position: u may wrap across the antimeridian relative to the view.
            float du = Mathf.Repeat(u - u0, 1f);
            var marker = new Vector2(rect.x + du / spanU * rect.width,
                                     rect.yMax - (v - v0) / spanV * rect.height);

            if (!rect.Contains(marker)) return;

            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot((float)headingDeg, marker);

            GUI.color = Color.yellow;
            GUI.DrawTexture(new Rect(marker.x - 1f, marker.y - 18f, 2f, 18f), whitePixel);                  // heading line
            GUI.DrawTexture(new Rect(marker.x - 5f, marker.y - 18f, 10f, 2f), whitePixel);                  // arrow head bar
            GUI.DrawTexture(new Rect(marker.x - 3f, marker.y - 21f, 6f, 3f), whitePixel);
            GUI.matrix = saved;

            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(marker.x - 4f, marker.y - 4f, 8f, 8f), whitePixel);
            GUI.color = new Color(1f, 0.25f, 0.2f);
            GUI.DrawTexture(new Rect(marker.x - 3f, marker.y - 3f, 6f, 6f), whitePixel);
            GUI.color = Color.white;
        }
    }
}
