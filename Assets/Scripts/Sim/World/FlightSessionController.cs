using CirrusFS.Flight;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.Sim
{
    /// Wires the menu to the world: takes the Fly request, spawns through FlightSpawner, hands
    /// the new aircraft to the camera, the HUD and the floating-origin manager, and swaps the
    /// menu out for the flight.
    ///
    /// This is the only script that knows about both halves, which keeps CirrusFS.Flight free
    /// of any menu dependency and the menu free of any flight-model dependency.
    [DisallowMultipleComponent]
    public class FlightSessionController : MonoBehaviour
    {
        [Header("Systems")]
        public StartMenuController menu;
        public FlightSpawner spawner;
        public FloatingOriginManager floatingOrigin;

        [Header("Scene objects")]
        [Tooltip("Root of the menu canvas, hidden once a flight starts.")]
        public GameObject menuRoot;

        [Tooltip("Optional HUD root, shown only in flight.")]
        public GameObject hudRoot;

        [Tooltip("Chase camera. Found automatically if left empty.")]
        public AircraftCamera chaseCamera;

        [Tooltip("HUD component whose aircraft reference is repointed on spawn. Found " +
                 "automatically if left empty.")]
        public FlightHud hud;

        [Header("Behaviour")]
        [Tooltip("Escape returns to the menu and parks the aircraft.")]
        public bool escapeReturnsToMenu = true;

        [Tooltip("Lock and hide the cursor while flying. Off by default: the flight model's " +
                 "mouse-control box needs a real cursor position.")]
        public bool lockCursorInFlight;

        public bool InFlight { get; private set; }

        void Awake()
        {
            if (chaseCamera == null) chaseCamera = FindFirstObjectByType<AircraftCamera>();
            if (hud == null) hud = FindFirstObjectByType<FlightHud>();

            if (menu == null) menu = FindFirstObjectByType<StartMenuController>(FindObjectsInactive.Include);
            if (spawner == null) spawner = FindFirstObjectByType<FlightSpawner>();
            if (floatingOrigin == null) floatingOrigin = FindFirstObjectByType<FloatingOriginManager>();
        }

        void OnEnable()
        {
            if (menu != null) menu.FlyRequested += HandleFlyRequested;
        }

        void OnDisable()
        {
            if (menu != null) menu.FlyRequested -= HandleFlyRequested;
        }

        void Start()
        {
            ShowMenu();
        }

        void Update()
        {
            if (!escapeReturnsToMenu || !InFlight) return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) ShowMenu();
        }

        // ---- transitions ---------------------------------------------------------

        void HandleFlyRequested(AircraftEntry aircraft, AirportRef airport)
        {
            if (spawner == null)
            {
                Debug.LogError("[FlightSession] No FlightSpawner assigned; cannot start a flight.");
                return;
            }

            GameObject spawned = spawner.Spawn(aircraft, airport);
            if (spawned == null)
            {
                // The spawner has already logged why. Stay in the menu rather than dropping the
                // player into an empty scene.
                return;
            }

            if (floatingOrigin != null) floatingOrigin.SetTrackedAircraft(spawned.transform);
            else Debug.LogWarning("[FlightSession] No FloatingOriginManager: precision will degrade past 10 km.");

            if (chaseCamera != null) chaseCamera.target = spawned.transform;

            if (hud != null)
            {
                var controller = spawned.GetComponent<AircraftController>();
                if (controller != null) hud.aircraft = controller;
            }

            EnterFlight();
        }

        void EnterFlight()
        {
            InFlight = true;

            if (menuRoot != null) menuRoot.SetActive(false);
            if (hudRoot != null) hudRoot.SetActive(true);

            Cursor.lockState = lockCursorInFlight ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockCursorInFlight;
        }

        /// Returns to the menu and parks the aircraft. The scenery stays put; the next Fly
        /// realigns it.
        public void ShowMenu()
        {
            InFlight = false;

            if (floatingOrigin != null) floatingOrigin.SetTrackedAircraft(null);
            if (spawner != null) spawner.Despawn();

            // The HUD is IMGUI and draws whenever it has an aircraft, so it has to be cut loose
            // or it paints over the menu. Clearing the camera target leaves the camera parked on
            // the last view, which makes a reasonable backdrop.
            if (hud != null) hud.aircraft = null;
            if (chaseCamera != null) chaseCamera.target = null;

            if (hudRoot != null) hudRoot.SetActive(false);
            if (menuRoot != null) menuRoot.SetActive(true);

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
