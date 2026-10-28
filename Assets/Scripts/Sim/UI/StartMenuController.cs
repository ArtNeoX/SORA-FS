using System;
using System.Collections.Generic;
using System.Globalization;
using CirrusFS.Geo;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CirrusFS.Sim
{
    /// The aircraft list, the start-position fields and the Fly button.
    ///
    /// Owns nothing but the menu: it loads the aircraft catalogue, fills the ScrollRect from a
    /// pool of row prefabs, reads a lat/lon/altitude/heading from the start panel, and raises
    /// FlyRequested. What happens on Fly is FlightSessionController's business.
    [DisallowMultipleComponent]
    public class StartMenuController : MonoBehaviour
    {
        [Header("Aircraft panel")]
        public ScrollRect aircraftScroll;
        [Tooltip("Content transform the rows go under. Defaults to the ScrollRect's content.")]
        public RectTransform aircraftContent;
        public SelectionListItem aircraftItemPrefab;
        public TMP_InputField aircraftSearch;

        [Header("Start position panel")]
        public TMP_InputField latitudeInput;
        public TMP_InputField longitudeInput;
        [Tooltip("Height above the WGS84 ellipsoid, metres.")]
        public TMP_InputField altitudeInput;
        public TMP_InputField headingInput;

        [Tooltip("Values the fields start with.")]
        public SpawnPoint defaultSpawn = SpawnPoint.Default;

        [Header("Controls")]
        public Button flyButton;
        public TMP_Text selectionSummary;
        public TMP_Text statusText;

        [Header("Behaviour")]
        [Tooltip("Select the first aircraft automatically once the list loads.")]
        public bool autoSelectFirst = true;

        [Tooltip("Rebuild the list every time the menu is shown, so edits to the JSON are " +
                 "picked up without restarting play mode.")]
        public bool reloadOnEnable = true;

        /// Raised when Fly is clicked with a valid selection.
        public event Action<AircraftEntry, SpawnPoint> FlyRequested;

        public AircraftEntry SelectedAircraft { get; private set; }

        readonly List<SelectionListItem> aircraftItems = new List<SelectionListItem>();
        List<AircraftEntry> aircraft = new List<AircraftEntry>();

        int boundAircraftCount;
        bool built;
        bool fieldsFilled;

        void Awake()
        {
            if (aircraftContent == null && aircraftScroll != null) aircraftContent = aircraftScroll.content;

            if (flyButton != null) flyButton.onClick.AddListener(HandleFlyClicked);
            if (aircraftSearch != null) aircraftSearch.onValueChanged.AddListener(_ => ApplyAircraftFilter());

            foreach (TMP_InputField field in SpawnFields())
            {
                field.contentType = TMP_InputField.ContentType.DecimalNumber;
                field.onValueChanged.AddListener(_ => UpdateFlyButton());
            }

            FillSpawnFields(defaultSpawn);
            UpdateFlyButton();
        }

        void OnDestroy()
        {
            if (flyButton != null) flyButton.onClick.RemoveListener(HandleFlyClicked);
        }

        void OnEnable()
        {
            if (!built || reloadOnEnable) Rebuild();
        }

        IEnumerable<TMP_InputField> SpawnFields()
        {
            if (latitudeInput != null) yield return latitudeInput;
            if (longitudeInput != null) yield return longitudeInput;
            if (altitudeInput != null) yield return altitudeInput;
            if (headingInput != null) yield return headingInput;
        }

        // ---- building ------------------------------------------------------------

        /// Reloads the aircraft catalogue from disk and refills the list, keeping the current
        /// selection if it still exists.
        public void Rebuild()
        {
            string previousAircraftId = SelectedAircraft?.Id;

            aircraft = AircraftCatalog.LoadAll();
            BuildAircraftList();

            SelectedAircraft = null;
            if (previousAircraftId != null) SelectAircraftById(previousAircraftId);
            if (autoSelectFirst && SelectedAircraft == null && aircraft.Count > 0) SelectAircraft(aircraft[0]);

            RefreshSelectionVisuals();
            ApplyAircraftFilter();
            ReportStatus();

            built = true;
        }

        void BuildAircraftList()
        {
            if (aircraftItemPrefab == null || aircraftContent == null)
            {
                Debug.LogError("[StartMenu] Aircraft panel is not wired up: needs an item prefab and a " +
                               "content transform.");
                return;
            }

            while (aircraftItems.Count < aircraft.Count)
            {
                SelectionListItem item = Instantiate(aircraftItemPrefab, aircraftContent);
                item.gameObject.name = $"{aircraftItemPrefab.name}_{aircraftItems.Count:D4}";
                aircraftItems.Add(item);
            }

            for (int i = 0; i < aircraft.Count; i++)
            {
                AircraftEntry entry = aircraft[i];
                SelectionListItem item = aircraftItems[i];

                item.SetVisible(true);
                item.Bind(new SelectionListItem.Content(entry.Data.ListTitle,
                                                        entry.Data.ListSubtitle,
                                                        null,
                                                        entry.IconSprite),
                          entry,
                          HandleAircraftClicked);
            }

            boundAircraftCount = aircraft.Count;
            for (int i = boundAircraftCount; i < aircraftItems.Count; i++) aircraftItems[i].SetVisible(false);
        }

        // ---- selection -----------------------------------------------------------

        void HandleAircraftClicked(SelectionListItem item)
        {
            SelectAircraft(item.PayloadAs<AircraftEntry>());
            RefreshSelectionVisuals();
        }

        public void SelectAircraft(AircraftEntry entry)
        {
            SelectedAircraft = entry;
            UpdateFlyButton();
        }

        public bool SelectAircraftById(string id)
        {
            for (int i = 0; i < aircraft.Count; i++)
            {
                if (string.Equals(aircraft[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    SelectAircraft(aircraft[i]);
                    return true;
                }
            }

            return false;
        }

        void RefreshSelectionVisuals()
        {
            for (int i = 0; i < boundAircraftCount; i++)
            {
                aircraftItems[i].SetSelected(ReferenceEquals(aircraftItems[i].Payload, SelectedAircraft));
            }

            UpdateSummary();
        }

        void ApplyAircraftFilter()
        {
            string term = aircraftSearch != null ? aircraftSearch.text.Trim().ToLowerInvariant() : string.Empty;

            for (int i = 0; i < aircraftItems.Count; i++)
            {
                bool bound = i < boundAircraftCount;
                aircraftItems[i].SetVisible(bound && aircraftItems[i].Matches(term));
            }
        }

        // ---- start position ------------------------------------------------------

        public void FillSpawnFields(SpawnPoint spawn)
        {
            SetField(latitudeInput, spawn.latitude, "F5");
            SetField(longitudeInput, spawn.longitude, "F5");
            SetField(altitudeInput, spawn.altitude, "F0");
            SetField(headingInput, spawn.heading, "F0");
            fieldsFilled = true;
        }

        static void SetField(TMP_InputField field, double value, string format)
        {
            if (field != null) field.SetTextWithoutNotify(value.ToString(format, CultureInfo.InvariantCulture));
        }

        /// The spawn the fields describe, or false with a reason if one of them does not parse
        /// or is out of range. A field that is not wired up keeps the default.
        public bool TryReadSpawn(out SpawnPoint spawn, out string problem)
        {
            spawn = defaultSpawn;
            problem = null;
            if (!fieldsFilled) return true;

            if (!TryField(latitudeInput, ref spawn.latitude, "latitude", ref problem)) return false;
            if (!TryField(longitudeInput, ref spawn.longitude, "longitude", ref problem)) return false;
            if (!TryField(altitudeInput, ref spawn.altitude, "altitude", ref problem)) return false;
            if (!TryField(headingInput, ref spawn.heading, "heading", ref problem)) return false;

            if (!GeoMath.IsValidLatitude(spawn.latitude)) { problem = "Latitude must be -90 to 90."; return false; }
            if (!GeoMath.IsValidLongitude(spawn.longitude)) { problem = "Longitude must be -180 to 180."; return false; }
            if (spawn.altitude < -500.0 || spawn.altitude > 5.0e7) { problem = "Altitude is out of range."; return false; }

            spawn.heading = GeoMath.WrapHeading(spawn.heading);
            return true;
        }

        static bool TryField(TMP_InputField field, ref double value, string name, ref string problem)
        {
            if (field == null) return true;

            string text = field.text.Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                value = parsed;
                return true;
            }

            problem = $"Enter a number for {name}.";
            return false;
        }

        // ---- fly -----------------------------------------------------------------

        void UpdateFlyButton()
        {
            bool spawnOk = TryReadSpawn(out _, out string problem);

            if (flyButton != null) flyButton.interactable = SelectedAircraft != null && spawnOk;

            UpdateSummary(problem);
        }

        void UpdateSummary(string problem = null)
        {
            if (selectionSummary == null) return;

            if (problem != null)
            {
                selectionSummary.text = problem;
                return;
            }

            string aircraftPart = SelectedAircraft != null
                ? SelectedAircraft.Data.DisplayName
                : "<select an aircraft>";

            TryReadSpawn(out SpawnPoint spawn, out _);
            selectionSummary.text = $"{aircraftPart}   |   {GeoMath.FormatDms(spawn.latitude, true)} " +
                                    $"{GeoMath.FormatDms(spawn.longitude, false)}, {spawn.altitude:F0} m, " +
                                    $"heading {spawn.heading:F0}";
        }

        void HandleFlyClicked()
        {
            if (SelectedAircraft == null || !TryReadSpawn(out SpawnPoint spawn, out string problem))
            {
                Debug.LogWarning("[StartMenu] Fly clicked without a complete selection.");
                return;
            }

            spawn.speedKnots = defaultSpawn.speedKnots;
            FlyRequested?.Invoke(SelectedAircraft, spawn);
        }

        void ReportStatus()
        {
            if (statusText == null) return;

            statusText.text = aircraft.Count == 0
                ? $"No aircraft found in {AircraftCatalog.RootPath}"
                : $"{aircraft.Count} aircraft - WGS84 globe, ocean only";
        }
    }
}
