using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CirrusFS.Sim
{
    /// Start picker for one airport: the RAMP / RUNWAY list on the left, the plan view on the
    /// right, and Confirm.
    ///
    /// It edits a pending selection and only reports it on Confirm, so backing out of the panel
    /// leaves the menu's choice alone.
    [DisallowMultipleComponent]
    public class AirportCustomizePanel : MonoBehaviour
    {
        [Header("Header and info")]
        public TMP_Text airportTitle;
        public TMP_Text coordinateText;
        public TMP_Text elevationText;
        public TMP_Text variationText;

        [Header("Starts list")]
        public Button rampTabButton;
        public Button runwayTabButton;
        public TMP_Text rampTabLabel;
        public TMP_Text runwayTabLabel;
        public ScrollRect startScroll;
        public RectTransform startContent;
        public SelectionListItem startItemPrefab;

        [Header("Map")]
        public AirportMapView map;
        public Button zoomInButton;
        public Button zoomOutButton;

        [Header("Controls")]
        public Button confirmButton;
        public Button closeButton;

        [Header("Tab colours")]
        public Color activeTabColor = new Color(0.88f, 0.91f, 0.95f);
        public Color inactiveTabColor = new Color(0.45f, 0.49f, 0.56f);

        /// Raised on Confirm with the start the player settled on.
        public event Action<AirportRef> Confirmed;

        public AirportRef Current { get; private set; }

        readonly List<SelectionListItem> items = new List<SelectionListItem>();

        bool showingRamps;
        int boundCount;

        void Awake()
        {
            if (startContent == null && startScroll != null) startContent = startScroll.content;

            if (confirmButton != null) confirmButton.onClick.AddListener(HandleConfirm);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (rampTabButton != null) rampTabButton.onClick.AddListener(() => SetTab(true));
            if (runwayTabButton != null) runwayTabButton.onClick.AddListener(() => SetTab(false));
            if (zoomInButton != null && map != null) zoomInButton.onClick.AddListener(map.ZoomIn);
            if (zoomOutButton != null && map != null) zoomOutButton.onClick.AddListener(map.ZoomOut);

            // The panel is left inactive in the scene rather than hidden from here. Awake only
            // runs on the first activation, which is the one Open() performs - hiding here
            // would close the panel during the call that opened it.
        }

        void OnDestroy()
        {
            if (confirmButton != null) confirmButton.onClick.RemoveListener(HandleConfirm);
            if (closeButton != null) closeButton.onClick.RemoveListener(Close);
        }

        /// Opens the panel on an airport, with its current start pre-selected.
        public void Open(AirportRef reference)
        {
            if (!reference.IsValid)
            {
                Debug.LogWarning("[CustomizePanel] Open called without a valid airport.");
                return;
            }

            Current = reference;
            gameObject.SetActive(true);

            if (airportTitle != null)
            {
                airportTitle.text = $"{reference.Airport.DisplayName}  ·  {reference.Airport.Icao}";
            }

            if (coordinateText != null) coordinateText.text = reference.Airport.CoordinateLabel;
            if (elevationText != null) elevationText.text = reference.Airport.ElevationLabel;
            if (variationText != null) variationText.text = reference.Airport.MagneticVariationLabel;

            // Open on the tab the current start belongs to, so the highlight is visible.
            SetTab(!reference.Start.IsRunway);

            if (map != null) map.Show(reference.Airport, reference.Start);
        }

        public void Close() => gameObject.SetActive(false);

        // ---- tabs and list -------------------------------------------------------

        void SetTab(bool ramps)
        {
            showingRamps = ramps;

            if (rampTabLabel != null) rampTabLabel.color = ramps ? activeTabColor : inactiveTabColor;
            if (runwayTabLabel != null) runwayTabLabel.color = ramps ? inactiveTabColor : activeTabColor;

            BuildList();
        }

        void BuildList()
        {
            if (startItemPrefab == null || startContent == null || !Current.IsValid) return;

            var starts = new List<StartData>(Current.Airport.StartsOfType(!showingRamps));

            while (items.Count < starts.Count)
            {
                SelectionListItem item = Instantiate(startItemPrefab, startContent);
                item.gameObject.name = $"Start_{items.Count:D3}";
                items.Add(item);
            }

            for (int i = 0; i < starts.Count; i++)
            {
                StartData start = starts[i];
                items[i].SetVisible(true);
                items[i].Bind(new SelectionListItem.Content(start.DisplayName), start, HandleStartClicked);
                items[i].SetSelected(ReferenceEquals(start, Current.Start));
            }

            for (int i = starts.Count; i < items.Count; i++) items[i].SetVisible(false);

            boundCount = starts.Count;

            if (startScroll != null) startScroll.verticalNormalizedPosition = 1f;
        }

        void HandleStartClicked(SelectionListItem item)
        {
            var start = item.PayloadAs<StartData>();
            if (start == null) return;

            Current = Current.WithStart(start);

            for (int i = 0; i < boundCount; i++)
            {
                items[i].SetSelected(ReferenceEquals(items[i].Payload, start));
            }

            if (map != null) map.SetSelected(start);
        }

        void HandleConfirm()
        {
            if (Current.IsValid) Confirmed?.Invoke(Current);
            Close();
        }
    }
}
