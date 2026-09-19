using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CirrusFS.Sim
{
    /// The two selection panels and the Fly button.
    ///
    /// Owns nothing but the menu: it loads the catalogues, fills the two ScrollRects from a
    /// pool of row prefabs, tracks one selection in each, and raises FlyRequested. What happens
    /// on Fly is FlightSessionController's business.
    ///
    /// Rows are pooled rather than destroyed and rebuilt, because an airport list is the kind
    /// of thing that goes from four entries to four thousand the moment a real scenery library
    /// is plugged in.
    [DisallowMultipleComponent]
    public class StartMenuController : MonoBehaviour
    {
        [Header("Aircraft panel")]
        public ScrollRect aircraftScroll;
        [Tooltip("Content transform the rows go under. Defaults to the ScrollRect's content.")]
        public RectTransform aircraftContent;
        public SelectionListItem aircraftItemPrefab;
        public TMP_InputField aircraftSearch;

        [Header("Airport panel")]
        public ScrollRect airportScroll;
        public RectTransform airportContent;
        public SelectionListItem airportItemPrefab;
        public TMP_InputField airportSearch;

        [Header("Controls")]
        public Button flyButton;
        public Button customizeButton;
        public AirportCustomizePanel customizePanel;
        public TMP_Text selectionSummary;
        public TMP_Text statusText;

        [Header("Behaviour")]
        [Tooltip("Select the first aircraft and airport automatically once the lists load.")]
        public bool autoSelectFirst = true;

        [Tooltip("Rebuild the lists every time the menu is shown, so edits to the JSON are " +
                 "picked up without restarting play mode.")]
        public bool reloadOnEnable = true;

        /// Raised when Fly is clicked with a valid selection.
        public event Action<AircraftEntry, AirportRef> FlyRequested;

        public AircraftEntry SelectedAircraft { get; private set; }

        public AirportRef SelectedAirport { get; private set; }

        /// The loaded scenery config, after the models have filled in their starts.
        public SceneryDatabase Database => sceneries;

        readonly List<SelectionListItem> aircraftItems = new List<SelectionListItem>();
        readonly List<SelectionListItem> airportItems = new List<SelectionListItem>();

        List<AircraftEntry> aircraft = new List<AircraftEntry>();
        SceneryDatabase sceneries = new SceneryDatabase();

        // Rows past these counts are pooled leftovers holding stale payloads, and are never
        // shown or searched.
        int boundAircraftCount;
        int boundAirportCount;
        bool built;

        void Awake()
        {
            if (aircraftContent == null && aircraftScroll != null) aircraftContent = aircraftScroll.content;
            if (airportContent == null && airportScroll != null) airportContent = airportScroll.content;

            if (flyButton != null) flyButton.onClick.AddListener(HandleFlyClicked);
            if (customizeButton != null) customizeButton.onClick.AddListener(HandleCustomizeClicked);
            if (customizePanel != null) customizePanel.Confirmed += HandleStartConfirmed;
            if (aircraftSearch != null) aircraftSearch.onValueChanged.AddListener(_ => ApplyAircraftFilter());
            if (airportSearch != null) airportSearch.onValueChanged.AddListener(_ => ApplyAirportFilter());

            UpdateFlyButton();
        }

        void OnDestroy()
        {
            if (flyButton != null) flyButton.onClick.RemoveListener(HandleFlyClicked);
            if (customizeButton != null) customizeButton.onClick.RemoveListener(HandleCustomizeClicked);
            if (customizePanel != null) customizePanel.Confirmed -= HandleStartConfirmed;
        }

        void OnEnable()
        {
            if (!built || reloadOnEnable) Rebuild();
        }

        // ---- building ------------------------------------------------------------

        /// Reloads both catalogues from disk and refills the lists, keeping the current
        /// selection if it still exists.
        public void Rebuild()
        {
            string previousAircraftId = SelectedAircraft?.Id;
            string previousAirportIcao = SelectedAirport.IsValid ? SelectedAirport.Airport.icao : null;
            string previousStartId = SelectedAirport.IsValid ? SelectedAirport.Start.id : null;

            aircraft = AircraftCatalog.LoadAll();
            sceneries = SceneryCatalog.Load(out string sceneryError);

            // The models get the final say on where you can start. This has to happen before the
            // lists are built, because the customize map draws the starts it finds here.
            SceneryStartResolver.Apply(sceneries);

            BuildAircraftList();
            BuildAirportList();

            SelectedAircraft = null;
            SelectedAirport = default;

            if (previousAircraftId != null) SelectAircraftById(previousAircraftId);

            if (previousAirportIcao != null && SelectAirportByIcao(previousAirportIcao))
            {
                // Keep the customized start across a reload, not just the airport.
                StartData sameStart = SelectedAirport.Airport.FindStart(previousStartId);
                if (sameStart != null) SelectAirport(SelectedAirport.WithStart(sameStart));
            }

            if (autoSelectFirst)
            {
                if (SelectedAircraft == null && aircraft.Count > 0) SelectAircraft(aircraft[0]);

                if (!SelectedAirport.IsValid)
                {
                    List<AirportRef> all = sceneries.SelectableAirports();
                    if (all.Count > 0) SelectAirport(all[0]);
                }
            }

            RefreshSelectionVisuals();
            ApplyAircraftFilter();
            ApplyAirportFilter();
            ReportStatus(sceneryError);

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

            EnsurePool(aircraftItems, aircraftItemPrefab, aircraftContent, aircraft.Count);

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
            HideSurplus(aircraftItems, boundAircraftCount);
        }

        void BuildAirportList()
        {
            if (airportItemPrefab == null || airportContent == null)
            {
                Debug.LogError("[StartMenu] Airport panel is not wired up: needs an item prefab and a " +
                               "content transform.");
                return;
            }

            // One row per airport. Which runway end or stand to start from is chosen in the
            // customize panel, so a field with two modelled ends is still a single entry here.
            List<AirportRef> airports = sceneries.SelectableAirports();

            // Name first, then ICAO: List.Sort is unstable, so two fields sharing a name would
            // otherwise swap places between rebuilds.
            airports.Sort((a, b) =>
            {
                int byName = string.Compare(a.Airport.DisplayName, b.Airport.DisplayName,
                                            StringComparison.OrdinalIgnoreCase);
                return byName != 0
                    ? byName
                    : string.Compare(a.Airport.Icao, b.Airport.Icao, StringComparison.OrdinalIgnoreCase);
            });

            EnsurePool(airportItems, airportItemPrefab, airportContent, airports.Count);

            for (int i = 0; i < airports.Count; i++)
            {
                AirportRef reference = airports[i];
                SelectionListItem item = airportItems[i];

                item.SetVisible(true);

                // Name and ICAO only. `features` still goes into the search key below, it just
                // has no column of its own.
                item.Bind(new SelectionListItem.Content(reference.Airport.DisplayName,
                                                        reference.Airport.Icao,
                                                        null),
                          reference,
                          HandleAirportClicked);
                item.AppendSearchKey(reference.Airport.features);
            }

            boundAirportCount = airports.Count;
            HideSurplus(airportItems, boundAirportCount);
        }

        void EnsurePool(List<SelectionListItem> pool, SelectionListItem prefab, RectTransform parent, int needed)
        {
            while (pool.Count < needed)
            {
                SelectionListItem item = Instantiate(prefab, parent);
                item.gameObject.name = $"{prefab.name}_{pool.Count:D4}";
                pool.Add(item);
            }
        }

        static void HideSurplus(List<SelectionListItem> pool, int used)
        {
            for (int i = used; i < pool.Count; i++) pool[i].SetVisible(false);
        }

        // ---- selection -----------------------------------------------------------

        void HandleAircraftClicked(SelectionListItem item)
        {
            SelectAircraft(item.PayloadAs<AircraftEntry>());
            RefreshSelectionVisuals();
        }

        void HandleAirportClicked(SelectionListItem item)
        {
            SelectAirport(item.PayloadAs<AirportRef>());
            RefreshSelectionVisuals();
        }

        public void SelectAircraft(AircraftEntry entry)
        {
            SelectedAircraft = entry;
            UpdateFlyButton();
        }

        public void SelectAirport(AirportRef reference)
        {
            SelectedAirport = reference;
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

        public bool SelectAirportByIcao(string icao)
        {
            AirportRef found = sceneries.FindSelectableAirport(icao);
            if (!found.IsValid) return false;

            SelectAirport(found);
            return true;
        }

        void RefreshSelectionVisuals()
        {
            for (int i = 0; i < boundAircraftCount; i++)
            {
                aircraftItems[i].SetSelected(ReferenceEquals(aircraftItems[i].Payload, SelectedAircraft));
            }

            for (int i = 0; i < boundAirportCount; i++)
            {
                AirportRef reference = airportItems[i].PayloadAs<AirportRef>();
                airportItems[i].SetSelected(SelectedAirport.SameAirport(reference));
            }

            UpdateSummary();
        }

        // ---- filtering -----------------------------------------------------------

        void ApplyAircraftFilter() => Filter(aircraftItems, aircraftSearch, boundAircraftCount);

        void ApplyAirportFilter() => Filter(airportItems, airportSearch, boundAirportCount);

        static void Filter(List<SelectionListItem> pool, TMP_InputField search, int boundCount)
        {
            string term = search != null ? search.text.Trim().ToLowerInvariant() : string.Empty;

            for (int i = 0; i < pool.Count; i++)
            {
                bool bound = i < boundCount;
                pool[i].SetVisible(bound && pool[i].Matches(term));
            }
        }

        // ---- fly -----------------------------------------------------------------

        void UpdateFlyButton()
        {
            if (flyButton != null)
            {
                flyButton.interactable = SelectedAircraft != null && SelectedAirport.IsValid;
            }

            // Customize needs an airport but not an aircraft: picking a gate before picking
            // what to sit on it is a perfectly normal order.
            if (customizeButton != null) customizeButton.interactable = SelectedAirport.IsValid;

            UpdateSummary();
        }

        void HandleCustomizeClicked()
        {
            if (customizePanel == null)
            {
                Debug.LogWarning("[StartMenu] No customize panel assigned.");
                return;
            }

            if (!SelectedAirport.IsValid) return;

            customizePanel.Open(SelectedAirport);
        }

        void HandleStartConfirmed(AirportRef reference)
        {
            if (!reference.IsValid) return;

            SelectAirport(reference);
            RefreshSelectionVisuals();
        }

        void UpdateSummary()
        {
            if (selectionSummary == null) return;

            if (SelectedAircraft == null && !SelectedAirport.IsValid)
            {
                selectionSummary.text = "Select an aircraft and a departure airport.";
                return;
            }

            string aircraftPart = SelectedAircraft != null
                ? SelectedAircraft.Data.DisplayName
                : "<select an aircraft>";

            string airportPart = SelectedAirport.IsValid
                ? $"{SelectedAirport.Airport.Icao} {SelectedAirport.Airport.DisplayName} - " +
                  $"{SelectedAirport.Start.Label}"
                : "<select an airport>";

            selectionSummary.text = $"{aircraftPart}   |   {airportPart}";
        }

        void HandleFlyClicked()
        {
            if (SelectedAircraft == null || !SelectedAirport.IsValid)
            {
                // The button should not be interactable here; this only fires if something
                // drives it directly.
                Debug.LogWarning("[StartMenu] Fly clicked without a complete selection.");
                return;
            }

            FlyRequested?.Invoke(SelectedAircraft, SelectedAirport);
        }

        void ReportStatus(string sceneryError)
        {
            if (statusText == null) return;

            if (!string.IsNullOrEmpty(sceneryError))
            {
                statusText.text = sceneryError;
                return;
            }

            if (aircraft.Count == 0)
            {
                statusText.text = $"No aircraft found in {AircraftCatalog.RootPath}";
                return;
            }

            int airportCount = sceneries.SelectableAirports().Count;
            int regionCount = sceneries.RegionCount;
            statusText.text = $"{aircraft.Count} aircraft - {airportCount} airport" +
                              $"{(airportCount == 1 ? "" : "s")} - " +
                              $"{regionCount} region{(regionCount == 1 ? "" : "s")}";
        }
    }
}
