using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CirrusFS.Sim
{
    /// Plan view of an airport, drawn from the same geodetic coordinates the spawner uses.
    ///
    /// Nothing here is authored art: runways are strips laid from their threshold along their
    /// true heading, stands are dots, and both come straight out of sceneries.json. If a start
    /// looks wrong on this map it will be wrong on the ground too, which is most of the point
    /// of drawing it.
    ///
    /// Projection is GeoMath's local tangent plane, so north is +Y on the map and a true
    /// heading is a clockwise rotation from it. The view auto-fits the airport's own extent,
    /// and the zoom multiplies that fit.
    public class AirportMapView : MonoBehaviour
    {
        [Header("Wiring")]
        [Tooltip("Clipped area the map is drawn inside. Defaults to this RectTransform.")]
        public RectTransform mapArea;

        [Tooltip("Container that is scaled by the zoom. Created at runtime if left empty.")]
        public RectTransform content;

        [Header("Prefabs (plain UI Images / TMP text)")]
        public Image runwayPrefab;
        public Image markerPrefab;
        public TMP_Text labelPrefab;

        [Header("Appearance")]
        public Color runwayColor = new Color(0.75f, 0.78f, 0.82f);
        public Color markerColor = new Color(0.55f, 0.60f, 0.68f);
        public Color selectedColor = new Color(0.20f, 0.60f, 1f);
        public Color labelColor = new Color(0.45f, 0.62f, 0.85f);

        [Tooltip("Fraction of the view left as margin around the airport's extent.")]
        [Range(0f, 0.45f)] public float padding = 0.16f;

        [Tooltip("Metres of extent assumed when an airport has only one start to fit to.")]
        public float minimumExtentMetres = 400f;

        public float markerSize = 11f;
        public float selectedMarkerSize = 15f;

        [Header("Zoom")]
        public float zoom = 1f;
        public float minZoom = 0.4f;
        public float maxZoom = 6f;
        public float zoomStep = 1.35f;

        readonly List<GameObject> spawned = new List<GameObject>();

        AirportData airport;
        StartData selected;
        float metresToPixels;

        void Awake()
        {
            if (mapArea == null) mapArea = (RectTransform)transform;
            EnsureContent();
        }

        void EnsureContent()
        {
            if (content != null) return;

            var go = new GameObject("MapContent", typeof(RectTransform));
            content = (RectTransform)go.transform;
            content.SetParent(mapArea, false);
            content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f);
            content.pivot = new Vector2(0.5f, 0.5f);
            content.sizeDelta = Vector2.zero;
            content.anchoredPosition = Vector2.zero;
        }

        /// Draws the airport and highlights one start. Safe to call every time the selection
        /// changes; the previous drawing is thrown away.
        public void Show(AirportData airportData, StartData selectedStart)
        {
            airport = airportData;
            selected = selectedStart;
            Redraw();
        }

        public void SetSelected(StartData start)
        {
            selected = start;
            Redraw();
        }

        public void ZoomIn() => SetZoom(zoom * zoomStep);

        public void ZoomOut() => SetZoom(zoom / zoomStep);

        public void SetZoom(float value)
        {
            zoom = Mathf.Clamp(value, minZoom, maxZoom);
            if (content != null) content.localScale = new Vector3(zoom, zoom, 1f);
        }

        void Redraw()
        {
            EnsureContent();
            Clear();

            if (airport?.starts == null || airport.starts.Count == 0) return;

            metresToPixels = ComputeScale();
            content.localScale = new Vector3(zoom, zoom, 1f);

            // Runways first so stands and labels sit on top of them.
            for (int i = 0; i < airport.starts.Count; i++)
            {
                if (airport.starts[i] != null && airport.starts[i].IsRunway) DrawRunway(airport.starts[i]);
            }

            for (int i = 0; i < airport.starts.Count; i++)
            {
                StartData start = airport.starts[i];
                if (start == null) continue;

                if (start.IsRunway) DrawRunwayLabel(start);
                else DrawStand(start);
            }

            if (selected != null && !selected.IsRunway) DrawSelectedLabel(selected);
        }

        /// Pixels per metre that fits every start, plus each runway's far end, inside the view.
        ///
        /// East and north are fitted separately and the tighter of the two wins, so a runway
        /// lying across the view uses the full width instead of being squeezed to the height.
        float ComputeScale()
        {
            float half = minimumExtentMetres * 0.5f;
            var extent = new Vector2(half, half);

            for (int i = 0; i < airport.starts.Count; i++)
            {
                StartData start = airport.starts[i];
                if (start == null) continue;

                Grow(ref extent, ToMetres(start.latitude, start.longitude));

                if (start.IsRunway && start.length_m > 0.0) Grow(ref extent, RunwayFarEnd(start));
            }

            Rect rect = mapArea.rect;
            float usableWidth = rect.width * (1f - padding * 2f);
            float usableHeight = rect.height * (1f - padding * 2f);

            return Mathf.Min(usableWidth * 0.5f / Mathf.Max(1f, extent.x),
                             usableHeight * 0.5f / Mathf.Max(1f, extent.y));
        }

        static void Grow(ref Vector2 extent, Vector2 metres)
        {
            extent.x = Mathf.Max(extent.x, Mathf.Abs(metres.x));
            extent.y = Mathf.Max(extent.y, Mathf.Abs(metres.y));
        }

        /// Metres east/north of the airport reference point.
        Vector2 ToMetres(double latitude, double longitude)
        {
            GeoMath.GeoToLocal(latitude, longitude, airport.Latitude, airport.Longitude,
                               out double east, out double north);
            return new Vector2((float)east, (float)north);
        }

        Vector2 RunwayFarEnd(StartData runway)
        {
            Vector2 threshold = ToMetres(runway.latitude, runway.longitude);
            float heading = (float)runway.HeadingDeg * Mathf.Deg2Rad;

            // Heading is clockwise from north, and north is +Y on the map.
            return threshold + new Vector2(Mathf.Sin(heading), Mathf.Cos(heading)) * (float)runway.length_m;
        }

        void DrawRunway(StartData runway)
        {
            if (runwayPrefab == null || runway.length_m <= 0.0) return;

            Image strip = Instantiate(runwayPrefab, content);
            strip.name = "Runway_" + runway.id;
            strip.color = ReferenceEquals(runway, selected) ? selectedColor : runwayColor;

            var rt = (RectTransform)strip.transform;

            // Pivot at the threshold end so the strip runs from it along the heading.
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = ToMetres(runway.latitude, runway.longitude) * metresToPixels;
            rt.sizeDelta = new Vector2(Mathf.Max(2f, (float)runway.width_m * metresToPixels),
                                       (float)runway.length_m * metresToPixels);

            // UI rotation is counter-clockwise, headings are clockwise.
            rt.localEulerAngles = new Vector3(0f, 0f, -(float)runway.HeadingDeg);

            spawned.Add(strip.gameObject);
        }

        void DrawRunwayLabel(StartData runway)
        {
            if (labelPrefab == null) return;

            TMP_Text label = Instantiate(labelPrefab, content);
            label.name = "Label_" + runway.id;
            label.text = runway.DisplayName;
            label.color = ReferenceEquals(runway, selected) ? selectedColor : labelColor;

            var rt = (RectTransform)label.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);

            // Just outside the threshold, opposite the take-off direction, so the two ends of
            // one runway never collide.
            float heading = (float)runway.HeadingDeg * Mathf.Deg2Rad;
            Vector2 outward = -new Vector2(Mathf.Sin(heading), Mathf.Cos(heading));
            rt.anchoredPosition = ToMetres(runway.latitude, runway.longitude) * metresToPixels + outward * 22f;

            spawned.Add(label.gameObject);
        }

        void DrawStand(StartData stand)
        {
            if (markerPrefab == null) return;

            bool isSelected = ReferenceEquals(stand, selected);

            Image dot = Instantiate(markerPrefab, content);
            dot.name = "Stand_" + stand.id;
            dot.color = isSelected ? selectedColor : markerColor;

            var rt = (RectTransform)dot.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = ToMetres(stand.latitude, stand.longitude) * metresToPixels;

            float size = isSelected ? selectedMarkerSize : markerSize;
            rt.sizeDelta = new Vector2(size, size);

            spawned.Add(dot.gameObject);
        }

        void DrawSelectedLabel(StartData stand)
        {
            if (labelPrefab == null) return;

            TMP_Text label = Instantiate(labelPrefab, content);
            label.name = "SelectedLabel";
            label.text = stand.DisplayName;
            label.color = Color.white;

            var rt = (RectTransform)label.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = ToMetres(stand.latitude, stand.longitude) * metresToPixels +
                                  new Vector2(0f, selectedMarkerSize);

            spawned.Add(label.gameObject);
        }

        void Clear()
        {
            for (int i = 0; i < spawned.Count; i++)
            {
                if (spawned[i] != null) Destroy(spawned[i]);
            }

            spawned.Clear();
        }
    }
}
