using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.Flight
{
    /// Flight readout plus the X-Plane style mouse-control box. Presentation only;
    /// the controller owns the mouse state and box geometry.
    public class FlightHud : MonoBehaviour
    {
        public AircraftController aircraft;

        static readonly Color Readout = new Color(0.47f, 1f, 0.71f);
        static readonly Color Dim = new Color(0.63f, 0.63f, 0.71f);
        static readonly Color Panel = new Color(0.03f, 0.04f, 0.055f, 0.8f);
        static readonly Color BoxFill = new Color(0.024f, 0.031f, 0.047f, 0.45f);
        static readonly Color Crosshair = new Color(0.27f, 0.27f, 0.35f, 1f);

        Texture2D pixel;
        GUIStyle readoutStyle, labelStyle;

        void Awake()
        {
            pixel = new Texture2D(1, 1);
            pixel.SetPixel(0, 0, Color.white);
            pixel.Apply();
        }

        void OnDestroy()
        {
            if (pixel) Destroy(pixel);
        }

        void EnsureStyles()
        {
            if (readoutStyle != null) return;

            readoutStyle = new GUIStyle(GUI.skin.label)
            {
                font = Font.CreateDynamicFontFromOSFont("Consolas", 15),
                fontSize = 15,
                alignment = TextAnchor.UpperLeft,
                richText = false,
            };
            readoutStyle.normal.textColor = Readout;

            labelStyle = new GUIStyle(readoutStyle) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            labelStyle.normal.textColor = Dim;
        }

        void Fill(Rect r, Color c)
        {
            Color prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, pixel);
            GUI.color = prev;
        }

        void Outline(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        void OnGUI()
        {
            if (!aircraft) return;
            EnsureStyles();

            if (aircraft.config.showHud) DrawReadout();
            DrawMouseBox();
        }

        void DrawReadout()
        {
            var panel = new Rect(14f, 14f, 262f, 156f);
            Fill(panel, Panel);

            float weight = aircraft.WeightNewtons;
            string text = string.Format(
                "AIRSPEED {0,7:F1} kts\n" +
                "AOA      {1,7:F1} deg\n" +
                "LIFT   {2,9:F0} N ({3,3:F0}%W)\n" +
                "DRAG   {4,9:F0} N\n" +
                "THRUST {5,9:F0} N  THR {6,3:F0}%\n" +
                "VS     {7,9:F1} m/s\n" +
                "FLAP {8,5:F1}   AIRBRK {9,5:F1}",
                aircraft.AirspeedKnots,
                aircraft.AngleOfAttackDeg,
                aircraft.TotalLift,
                weight > 0f ? aircraft.TotalLift / weight * 100f : 0f,
                aircraft.TotalDrag,
                aircraft.TotalThrust,
                aircraft.Throttle * 100f,
                aircraft.VerticalSpeed,
                aircraft.FlapPos,
                aircraft.AirbrakePos);

            GUI.Label(new Rect(panel.x + 12f, panel.y + 8f, panel.width - 20f, panel.height - 14f), text, readoutStyle);
        }

        void DrawMouseBox()
        {
            // Controller works in bottom-left origin screen space; GUI is top-left.
            Rect screenBox = aircraft.MouseBox;
            var box = new Rect(screenBox.x, Screen.height - screenBox.y - screenBox.height,
                screenBox.width, screenBox.height);

            Fill(box, BoxFill);
            Outline(box, aircraft.MouseMode || aircraft.MouseBoxDragging ? Readout : Crosshair,
                aircraft.MouseBoxDragging ? 3f : 2f);

            float inset = 10f;
            Fill(new Rect(box.x + inset, box.center.y, box.width - inset * 2f, 1f), Crosshair);
            Fill(new Rect(box.center.x, box.y + inset, 1f, box.height - inset * 2f), Crosshair);

            if (aircraft.MouseMode)
            {
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    Vector2 m = mouse.position.ReadValue();
                    var dot = new Rect(m.x - 6f, Screen.height - m.y - 6f, 12f, 12f);
                    Fill(dot, Readout);
                }
            }

            // Wider than the box so the longer text never wraps on a small box.
            GUI.Label(new Rect(box.center.x - 160f, box.y - 20f, 320f, 18f),
                aircraft.MouseBoxDragging ? "MOVING (release right button)"
                : aircraft.MouseMode ? "MOUSE FLIGHT: ON (click, right-drag to move)"
                : "MOUSE FLIGHT: OFF (click, right-drag to move)", labelStyle);
        }
    }
}

