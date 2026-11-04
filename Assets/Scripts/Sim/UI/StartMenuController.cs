using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CirrusFS.Geo;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CirrusFS.Sim
{
    /// The aircraft list, the airport search, the start-position fields and the Fly button.
    ///
    /// Owns nothing but the menu: it loads the aircraft catalogue, fills the ScrollRects from
    /// pools of row prefabs, reads a lat/lon/altitude/heading from the start panel, and raises
    /// FlyRequested. What happens on Fly is FlightSessionController's business.
    ///
    /// Airports: typing an ICAO/IATA code or a name lists matching airports
    /// (GlobeAirports.Database); picking one lists its runway ends, and picking an end fills the
    /// start fields with a spot on the runway centreline just past that end, facing down the
    /// runway, at ground level. The fields stay editable.
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
        public TMP_InputField airportSearch;
        public ScrollRect airportScroll;
        [Tooltip("Content transform the airport rows go under. Defaults to the ScrollRect's content.")]
        public RectTransform airportContent;
        public ScrollRect runwayScroll;
        [Tooltip("Content transform the runway-end rows go under. Defaults to the ScrollRect's content.")]
        public RectTransform runwayContent;
        [Tooltip("Row prefab for both lists (title, code, tag).")]
        public SelectionListItem airportItemPrefab;
        [Tooltip("Shows how many airports are loaded, or what the search found.")]
        public TMP_Text airportStatus;

        [Tooltip("Search results shown at most.")]
        public int maxAirportResults = 40;

        [Tooltip("Where on the runway the aircraft starts: this far past the runway end, metres.")]
        public float runwayStartOffset = 30f;

        [Tooltip("Start choice buttons: on the runway, or on final approach 3 / 10 NM out.")]
        public Button startOnRunwayButton;
        public Button startFinal3Button;
        public Button startFinal10Button;
        public Color startChoiceOn = new Color(0.16f, 0.42f, 0.85f, 0.95f);
        public Color startChoiceOff = new Color(0.10f, 0.13f, 0.18f, 0.85f);

        [Tooltip("Height over the landing threshold on the glidepath, metres (50 ft).")]
        public float thresholdCrossingHeight = 15f;

        [Tooltip("An approach start is never lower than this above the ground under it, metres.")]
        public float minimumApproachClearance = 100f;

        public enum RunwayStart { OnRunway, Final3Nm, Final10Nm }

        [Tooltip("Where a runway pick starts: on the runway, or on final 3 / 10 NM out on a 3 degree glidepath.")]
        public RunwayStart runwayStart = RunwayStart.OnRunway;

        const double MetresPerNauticalMile = 1852.0;

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

        /// One end of a runway to start from: landing (or taking off) on it means flying
        /// Heading from End toward the far end.
        public sealed class RunwayEnd
        {
            public Airport Airport;
            public Runway Runway;
            public bool HighEnd;
            public string Ident => HighEnd ? Runway.HeIdent : Runway.LeIdent;
            public double Heading => HighEnd ? Runway.HeHeading : Runway.LeHeading;
            public double Latitude => HighEnd ? Runway.HeLatitude : Runway.LeLatitude;
            public double Longitude => HighEnd ? Runway.HeLongitude : Runway.LeLongitude;
            public override string ToString() => $"{Airport.Code} {Ident}";
        }

        public Airport SelectedAirport { get; private set; }
        public RunwayEnd SelectedRunwayEnd { get; private set; }

        readonly List<SelectionListItem> aircraftItems = new List<SelectionListItem>();
        readonly List<SelectionListItem> airportItems = new List<SelectionListItem>();
        readonly List<SelectionListItem> runwayItems = new List<SelectionListItem>();
        List<AircraftEntry> aircraft = new List<AircraftEntry>();

        int boundAircraftCount;
        bool built;
        bool fieldsFilled;

        void Awake()
        {
            if (aircraftContent == null && aircraftScroll != null) aircraftContent = aircraftScroll.content;

            if (airportContent == null && airportScroll != null) airportContent = airportScroll.content;
            if (runwayContent == null && runwayScroll != null) runwayContent = runwayScroll.content;

            if (flyButton != null) flyButton.onClick.AddListener(HandleFlyClicked);
            if (aircraftSearch != null) aircraftSearch.onValueChanged.AddListener(_ => ApplyAircraftFilter());
            if (airportSearch != null)
            {
                airportSearch.onValueChanged.AddListener(_ => SearchAirports());
                airportSearch.onSubmit.AddListener(_ => SelectFirstAirportResult());
            }

            foreach (TMP_InputField field in SpawnFields())
            {
                field.contentType = TMP_InputField.ContentType.DecimalNumber;
                field.onValueChanged.AddListener(_ => HandleSpawnFieldEdited());
            }

            GlobeAirports.Loaded += HandleAirportsLoaded;

            if (startOnRunwayButton != null) startOnRunwayButton.onClick.AddListener(() => SetRunwayStart(RunwayStart.OnRunway));
            if (startFinal3Button != null) startFinal3Button.onClick.AddListener(() => SetRunwayStart(RunwayStart.Final3Nm));
            if (startFinal10Button != null) startFinal10Button.onClick.AddListener(() => SetRunwayStart(RunwayStart.Final10Nm));
            RefreshStartChoice();

            FillSpawnFields(defaultSpawn);
            UpdateFlyButton();
        }

        void OnDestroy()
        {
            if (flyButton != null) flyButton.onClick.RemoveListener(HandleFlyClicked);
            GlobeAirports.Loaded -= HandleAirportsLoaded;
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
            SearchAirports();
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

        // ---- airports ------------------------------------------------------------

        static AirportDatabase Airports => GlobeAirports.Active != null ? GlobeAirports.Active.Database : null;

        void HandleAirportsLoaded(AirportDatabase db)
        {
            SearchAirports();
            ReportStatus();
        }

        /// Lists the airports matching the search box (codes, then names), best first.
        void SearchAirports()
        {
            AirportDatabase db = Airports;
            string query = airportSearch != null ? airportSearch.text.Trim() : string.Empty;
            List<AirportDatabase.SearchResult> results = null;

            if (db == null)
            {
                SetAirportStatus(GlobeAirports.Active != null && GlobeAirports.Active.LoadError != null
                    ? "No airport data: " + GlobeAirports.Active.LoadError
                    : "Loading airports...");
            }
            else if (query.Length < 2)
            {
                SetAirportStatus($"{db.Airports.Count:N0} airports. Type an ICAO or IATA code, or a name.");
            }
            else
            {
                results = db.Search(query, maxAirportResults);
                SetAirportStatus(results.Count == 0 ? $"No airport matches \"{query}\"."
                                 : results.Count >= maxAirportResults ? $"First {results.Count} matches."
                                 : $"{results.Count} match{(results.Count == 1 ? "" : "es")}.");
            }

            int count = results?.Count ?? 0;
            if (!EnsureRows(airportItems, airportContent, count)) return;
            for (int i = 0; i < count; i++)
            {
                Airport a = results[i].Airport;
                SelectionListItem item = airportItems[i];
                item.SetVisible(true);
                item.Bind(new SelectionListItem.Content(a.Name, a.Code, AirportTag(a)), a, HandleAirportClicked);
                item.SetSelected(a == SelectedAirport);
            }
            for (int i = count; i < airportItems.Count; i++) airportItems[i].SetVisible(false);
            if (airportScroll != null) airportScroll.verticalNormalizedPosition = 1f;
        }

        static string AirportTag(Airport a) => $"{a.Runways.Count} rwy{(a.HasApproxRunways ? " ~" : "")}";

        static string AirportDetails(Airport a)
        {
            string place = string.Join(", ", new[] { a.Municipality, a.CountryName }
                                                 .Where(s => !string.IsNullOrEmpty(s)).Distinct());
            string elevation = float.IsNaN(a.Elevation) ? "" : $", elevation {a.Elevation:F0} m";
            string iata = string.IsNullOrEmpty(a.Iata) ? "" : $" / {a.Iata}";
            return $"{a.Code}{iata}  {place}{elevation}" +
                   (a.HasApproxRunways ? ". ~ runway positions are approximate." : "");
        }

        void SetAirportStatus(string text)
        {
            if (airportStatus != null) airportStatus.text = text;
        }

        /// Makes sure `rows` has at least `count` rows under `content`. False when the panel
        /// is not wired up.
        bool EnsureRows(List<SelectionListItem> rows, RectTransform content, int count)
        {
            if (airportItemPrefab == null || content == null) return false;
            while (rows.Count < count)
            {
                SelectionListItem item = Instantiate(airportItemPrefab, content);
                item.gameObject.name = $"{airportItemPrefab.name}_{rows.Count:D3}";
                rows.Add(item);
            }
            return true;
        }

        void SelectFirstAirportResult()
        {
            if (airportItems.Count > 0 && airportItems[0].gameObject.activeSelf)
                SelectAirport(airportItems[0].PayloadAs<Airport>());
        }

        void HandleAirportClicked(SelectionListItem item) => SelectAirport(item.PayloadAs<Airport>());

        /// Shows the airport's runway ends and starts on the first end of its longest runway.
        public void SelectAirport(Airport airport)
        {
            SelectedAirport = airport;
            foreach (SelectionListItem row in airportItems) row.SetSelected(ReferenceEquals(row.Payload, airport));
            if (airport != null) SetAirportStatus(AirportDetails(airport));

            var ends = new List<RunwayEnd>();
            if (airport != null)
            {
                var runways = new List<Runway>(airport.Runways);
                runways.Sort((x, y) => y.Length.CompareTo(x.Length));
                foreach (Runway r in runways)
                {
                    ends.Add(new RunwayEnd { Airport = airport, Runway = r, HighEnd = false });
                    if (!string.IsNullOrEmpty(r.HeIdent)) ends.Add(new RunwayEnd { Airport = airport, Runway = r, HighEnd = true });
                }
            }

            if (EnsureRows(runwayItems, runwayContent, ends.Count))
            {
                for (int i = 0; i < ends.Count; i++)
                {
                    RunwayEnd e = ends[i];
                    Runway r = e.Runway;
                    string title = $"{e.Ident}   {r.Length:N0} x {r.Width:N0} m {SurfaceName(r.Surface)}";
                    string tag = (r.Lighted ? "lit" : "") + (r.Approx ? (r.Lighted ? ", ~ position" : "~ position") : "");
                    runwayItems[i].SetVisible(true);
                    runwayItems[i].Bind(new SelectionListItem.Content(title, $"{e.Heading:000}°", tag), e, HandleRunwayClicked);
                }
                for (int i = ends.Count; i < runwayItems.Count; i++) runwayItems[i].SetVisible(false);
                if (runwayScroll != null) runwayScroll.verticalNormalizedPosition = 1f;
            }

            if (ends.Count > 0) SelectRunwayEnd(ends[0]);
            else
            {
                SelectedRunwayEnd = null;
                UpdateFlyButton();
            }
        }

        static string SurfaceName(RunwaySurface s) => s switch
        {
            RunwaySurface.Unknown => "",
            RunwaySurface.Other => "",
            _ => s.ToString().ToLowerInvariant(),
        };

        void HandleRunwayClicked(SelectionListItem item) => SelectRunwayEnd(item.PayloadAs<RunwayEnd>());

        /// Fills the start fields for a runway end, by `runwayStart`:
        /// - on the runway: on the centreline, runwayStartOffset past the runway end, facing
        ///   down the runway, at ground level (altitude 0 puts the aircraft on the ground;
        ///   FlightSpawner finds the surface height);
        /// - on final, 3 or 10 NM before the landing threshold (the displaced threshold where
        ///   there is one) on the extended centreline, on a 3 degree glidepath that crosses the
        ///   threshold at thresholdCrossingHeight. That is about 1,000 ft above the runway at
        ///   3 NM and 3,200 ft at 10 NM.
        public void SelectRunwayEnd(RunwayEnd end)
        {
            SelectedRunwayEnd = end;
            foreach (SelectionListItem row in runwayItems) row.SetSelected(ReferenceEquals(row.Payload, end));
            if (end == null)
            {
                UpdateFlyButton();
                return;
            }

            // the runway's direction at this end, in the end's own east/north plane
            GeoMath.EnuBasis(end.Latitude, end.Longitude, out Double3 east, out Double3 north, out _);
            Double3 endPoint = GeoMath.GeodeticToEcef(end.Latitude, end.Longitude, 0.0);
            Double3 farPoint = end.HighEnd
                ? GeoMath.GeodeticToEcef(end.Runway.LeLatitude, end.Runway.LeLongitude, 0.0)
                : GeoMath.GeodeticToEcef(end.Runway.HeLatitude, end.Runway.HeLongitude, 0.0);
            Double3 toFar = farPoint - endPoint;
            Double3 along = (east * Double3.Dot(toFar, east) + north * Double3.Dot(toFar, north)).Normalized;

            var spawn = defaultSpawn;
            spawn.heading = GeoMath.WrapHeading(end.Heading);
            if (runwayStart == RunwayStart.OnRunway)
            {
                double offset = Math.Min(runwayStartOffset, 0.25 * end.Runway.Length);
                GeoMath.EcefToGeodetic(endPoint + along * offset, out spawn.latitude, out spawn.longitude, out _);
                spawn.altitude = 0.0;
            }
            else
            {
                double miles = runwayStart == RunwayStart.Final3Nm ? 3.0 : 10.0;
                double displaced = Math.Max(0.0, end.HighEnd ? end.Runway.HeDisplaced : end.Runway.LeDisplaced);
                double back = miles * MetresPerNauticalMile - displaced;   // from the runway end
                GeoMath.EcefToGeodetic(endPoint - along * back, out spawn.latitude, out spawn.longitude, out _);

                double threshold = ThresholdElevation(end, endPoint + along * displaced);
                double tan = Math.Tan(SpawnPoint.GlidePathDeg * GeoMath.Deg2Rad);
                double altitude = threshold + thresholdCrossingHeight + miles * MetresPerNauticalMile * tan;
                // never into rising ground short of the runway
                if (GlobeTerrain.Active != null &&
                    GlobeTerrain.Active.TryGetSurfaceHeight(spawn.latitude, spawn.longitude, out float below))
                    altitude = Math.Max(altitude, below + minimumApproachClearance);
                spawn.altitude = Math.Round(altitude);
            }
            FillSpawnFields(spawn);
            UpdateFlyButton();
        }

        /// Ground height at the landing threshold: the terrain there (what the runway is drawn
        /// on), else the published elevation of that end, else the airport's, else 0.
        static double ThresholdElevation(RunwayEnd end, Double3 thresholdEcef)
        {
            GeoMath.EcefToGeodetic(thresholdEcef, out double lat, out double lon, out _);
            if (GlobeTerrain.Active != null && GlobeTerrain.Active.TryGetSurfaceHeight(lat, lon, out float h)) return h;
            float published = end.HighEnd ? end.Runway.HeElevation : end.Runway.LeElevation;
            if (!float.IsNaN(published)) return published;
            return float.IsNaN(end.Airport.Elevation) ? 0.0 : end.Airport.Elevation;
        }

        public void SetRunwayStart(RunwayStart start)
        {
            runwayStart = start;
            RefreshStartChoice();
            if (SelectedRunwayEnd != null) SelectRunwayEnd(SelectedRunwayEnd);
        }

        void RefreshStartChoice()
        {
            Paint(startOnRunwayButton, runwayStart == RunwayStart.OnRunway);
            Paint(startFinal3Button, runwayStart == RunwayStart.Final3Nm);
            Paint(startFinal10Button, runwayStart == RunwayStart.Final10Nm);
        }

        void Paint(Button button, bool on)
        {
            if (button == null || button.targetGraphic == null) return;
            button.targetGraphic.color = on ? startChoiceOn : startChoiceOff;
        }

        string StartChoiceName => runwayStart switch
        {
            RunwayStart.Final3Nm => "3 NM final",
            RunwayStart.Final10Nm => "10 NM final",
            _ => "on the runway",
        };

        /// The user typed into a start field: the start is theirs now, not a runway's.
        void HandleSpawnFieldEdited()
        {
            if (SelectedRunwayEnd != null)
            {
                SelectedRunwayEnd = null;
                foreach (SelectionListItem row in runwayItems) row.SetSelected(false);
            }
            UpdateFlyButton();
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
            if (SelectedRunwayEnd != null)
            {
                RunwayEnd e = SelectedRunwayEnd;
                string where = runwayStart == RunwayStart.OnRunway
                    ? "on the runway"
                    : $"{StartChoiceName}, {spawn.altitude * 3.28084:N0} ft MSL";
                selectionSummary.text = $"{aircraftPart}   |   {e.Airport.Code} {e.Airport.Name}, runway {e.Ident}, {where}" +
                                        (e.Runway.Approx ? " (approximate position)" : "");
                return;
            }
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
            spawn.approach = SelectedRunwayEnd != null && runwayStart != RunwayStart.OnRunway;
            FlyRequested?.Invoke(SelectedAircraft, spawn);
        }

        void ReportStatus()
        {
            if (statusText == null) return;

            AirportDatabase db = Airports;
            statusText.text = aircraft.Count == 0
                ? $"No aircraft found in {AircraftCatalog.RootPath}"
                : $"{aircraft.Count} aircraft - WGS84 globe" +
                  (db != null ? $", {db.Airports.Count:N0} airports" : "");
        }
    }
}
