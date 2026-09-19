using System;
using UnityEngine;
using UnityEngine.Events;

namespace CirrusFS.Sim
{
    /// Puts a chosen aircraft on a chosen runway.
    ///
    /// The whole trick is that the aircraft never moves to the airport - the airport moves to
    /// the aircraft. The scene origin is the only place with full float precision, so the
    /// region model is translated until the selected runway lands on (0, 0, 0) and the
    /// aircraft is spawned there facing down the runway. That also means the origin shift
    /// manager starts from a known state on every flight.
    ///
    /// Knows nothing about UI and nothing about the flight model; it takes a selection and
    /// returns the spawned GameObject.
    [DisallowMultipleComponent]
    public class FlightSpawner : MonoBehaviour
    {
        [Serializable] public class AircraftSpawnedEvent : UnityEvent<GameObject> { }

        [Header("Placement")]
        [Tooltip("Optional parent for instantiated aircraft. Must be an unrotated, unscaled " +
                 "transform at the origin, or leave it empty to spawn at scene root.")]
        public Transform aircraftParent;

        [Tooltip("Height the ground probe starts from when working out where the runway " +
                 "surface ended up, metres.")]
        public float groundProbeHeight = 3000f;

        [Tooltip("How far below the origin the ground probe still searches, metres.")]
        public float groundProbeDepth = 1000f;

        public LayerMask groundMask = ~0;

        [Tooltip("Hide every scenery region except the one being flown.")]
        public bool deactivateOtherRegions = true;

        [Header("Events")]
        public AircraftSpawnedEvent onAircraftSpawned = new AircraftSpawnedEvent();

        static readonly RaycastHit[] probeHits = new RaycastHit[32];

        /// The aircraft currently in the world, or null.
        public GameObject CurrentAircraft { get; private set; }

        public SceneryRegion CurrentRegion { get; private set; }

        public AirportRef CurrentAirport { get; private set; }

        /// True when the current aircraft was an object already in the scene rather than one
        /// instantiated from a prefab. It decides whether despawning destroys or just hides it.
        public bool CurrentAircraftFromScene { get; private set; }

        /// Places the scenery, spawns the aircraft on the chosen start and returns it, or null if
        /// anything in the chain was missing. Every failure is logged with the reason.
        ///
        /// The departure airport is placed by its own start marker, which is exact. Every other
        /// scenery in the scene is then placed by the same geodetic point through its *own*
        /// anchors, so none of them inherits another's error.
        public GameObject Spawn(AircraftEntry aircraft, AirportRef airport)
        {
            if (aircraft?.Data == null)
            {
                Debug.LogError("[FlightSpawner] No aircraft selected.");
                return null;
            }

            if (!airport.IsValid)
            {
                Debug.LogError("[FlightSpawner] No airport selected.");
                return null;
            }

            SceneryRegion scenery = ResolveScenery(airport.Airport);
            if (scenery == null) return null;

            if (!scenery.gameObject.activeSelf) scenery.gameObject.SetActive(true);

            // The streamer may have culled this scenery while the menu was up, using whatever
            // the camera was looking at. Alignment raycasts for the surface, so its colliders
            // have to be back before that runs.
            scenery.SetContentVisible(true);

            // Order matters: the scenery has to be placed before the aircraft, because the spawn
            // height is read off the aligned surface.
            if (!scenery.AlignStartToOrigin(airport.Airport, airport.Start)) return null;

            // Scene (0, 0, 0) is now this start. The coordinate was derived from the marker, so
            // it describes where the aircraft actually is rather than where a config guessed.
            WorldOrigin.SetReference(airport.Start.latitude, airport.Start.longitude,
                                     scenery.NorthYawDeg);

            PlaceOtherSceneries(airport, scenery);

            GameObject spawned = PlaceAircraft(aircraft, airport.Start, scenery);
            if (spawned == null) return null;

            CurrentAircraft = spawned;
            CurrentRegion = scenery;
            CurrentAirport = airport;

            Debug.Log($"[FlightSpawner] {aircraft.Data.DisplayName} on {airport.Airport.Icao} " +
                      $"{airport.Start.Label}, heading {airport.Start.HeadingDeg:F0} deg true. " +
                      $"{WorldOrigin.Describe()}");

            // Settle the world now rather than up to a tick later, so nothing distant is still
            // drawn on the first frame of the flight.
            SceneryStreamer streamer = FindFirstObjectByType<SceneryStreamer>();
            if (streamer != null) streamer.Refresh();

            onAircraftSpawned.Invoke(spawned);
            return spawned;
        }

        /// Removes the current aircraft: hidden if it belongs to the scene, destroyed if it was
        /// instantiated. The scenery is left where it is, ready to be realigned by the next
        /// spawn.
        public void Despawn()
        {
            if (CurrentAircraft == null) return;

            if (CurrentAircraftFromScene) CurrentAircraft.SetActive(false);
            else Destroy(CurrentAircraft);

            CurrentAircraft = null;
            CurrentAirport = default;
        }

        // ---- scenery -------------------------------------------------------------

        SceneryRegion ResolveScenery(AirportData airport)
        {
            SceneryRegion scenery = SceneryRegion.Find(airport.Icao)
                                    ?? SceneryRegion.Find(airport.SceneObject);

            if (scenery == null)
            {
                GameObject existing = GameObject.Find(airport.SceneObject);
                if (existing != null) scenery = existing.GetComponent<SceneryRegion>();
            }

            if (scenery == null)
            {
                Debug.LogError($"[FlightSpawner] {airport.Icao}: no SceneryRegion in the scene with that " +
                               $"id or object name ('{airport.SceneObject}').");
            }

            return scenery;
        }

        /// Places every other scenery for this flight - other airports as well as regions.
        ///
        /// Each is placed through its own anchors from the same geodetic point, so nothing
        /// inherits the departure airport's error. What is too far away to matter gets switched
        /// off by SceneryStreamer a moment later, not here.
        void PlaceOtherSceneries(AirportRef airport, SceneryRegion flown)
        {
            // The departure surface sits on y = 0, so sea level belongs at minus the field
            // elevation - that is what keeps a coastline at the right height under the airport.
            var seaLevelY = (float)-airport.Airport.elevation_m;

            foreach (SceneryRegion scenery in SceneryRegion.FindAll())
            {
                if (scenery == null || scenery == flown) continue;

                if (!scenery.gameObject.activeSelf) scenery.gameObject.SetActive(true);

                // A scene has exactly one north. Each model maps its geography through its own
                // rotation, so two sceneries whose norths disagree end up at an angle to each
                // other. Compare what the *anchors* solve, not the inspector field - a model
                // with anchors does not need the field set, and reading it would cry wolf.
                scenery.TryGetAnchorFrame(out _, out float sceneryNorth, out _);
                flown.TryGetAnchorFrame(out _, out float flownNorth, out _);

                if (Mathf.Abs(Mathf.DeltaAngle(sceneryNorth, flownNorth)) > 1f)
                {
                    Debug.LogWarning($"[FlightSpawner] '{scenery.sceneryId}' puts north at " +
                                     $"{sceneryNorth:F1} deg but '{flown.sceneryId}' uses " +
                                     $"{flownNorth:F1} deg. Rotate them to match, or set " +
                                     "modelNorthOffsetDeg, or the two will not line up.", scenery);
                }

                scenery.AlignToOrigin(airport.Start.latitude, airport.Start.longitude, seaLevelY);
            }
        }

        // ---- aircraft ------------------------------------------------------------

        GameObject PlaceAircraft(AircraftEntry aircraft, StartData start, SceneryRegion region)
        {
            AircraftData data = aircraft.Data;

            GameObject instance = AircraftCatalog.FindSceneObject(data);
            bool fromScene = instance != null;
            CurrentAircraftFromScene = fromScene;

            if (!fromScene)
            {
                GameObject prefab = AircraftCatalog.LoadPrefab(data);
                if (prefab == null)
                {
                    Debug.LogError($"[FlightSpawner] {data.Id}: nothing to spawn. Set scene_object to an " +
                                   "object in the scene, or prefab_resource_path to a prefab under a " +
                                   "Resources folder.");
                    return null;
                }

                instance = Instantiate(prefab, aircraftParent);
                instance.name = data.Id;
            }

            // The pose is set while the object is inactive: an inactive Rigidbody has no
            // interpolation history to smear from, so it comes up already at the runway rather
            // than sliding in from wherever it was parked.
            bool wasActive = instance.activeSelf;
            if (wasActive) instance.SetActive(false);

            float surfaceY = ProbeSurfaceHeight(region);
            var position = new Vector3(0f, surfaceY + data.SpawnHeight, 0f);

            // The region resolved this during alignment: a marker's own facing, or the start's
            // true heading turned into the region's frame. Either way it is already a world
            // heading, because scene +Z is north only for a region whose root is unrotated.
            Quaternion rotation = Quaternion.Euler(0f, region.LastStartHeading, 0f);

            instance.transform.SetPositionAndRotation(position, rotation);
            instance.SetActive(true);

            if (instance.TryGetComponent(out Rigidbody body))
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;

                // Keeps the transform and the body pose in agreement before the first
                // FixedUpdate reads either.
                body.position = position;
                body.rotation = rotation;
            }

            Physics.SyncTransforms();
            return instance;
        }

        /// Height of the flown region's surface at the origin. Zero when the region aligned its
        /// runway to y = 0, and the real height when it could not.
        ///
        /// Only hits belonging to the flown region count: a backdrop mesh crossing the origin
        /// would otherwise decide where the aircraft sits, which is exactly the wrong answer.
        float ProbeSurfaceHeight(SceneryRegion region)
        {
            var ray = new Ray(new Vector3(0f, groundProbeHeight, 0f), Vector3.down);
            int count = Physics.RaycastNonAlloc(ray, probeHits, groundProbeHeight + groundProbeDepth,
                                                groundMask, QueryTriggerInteraction.Ignore);

            float best = 0f;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                if (region != null && !probeHits[i].transform.IsChildOf(region.transform)) continue;

                if (!found || probeHits[i].point.y > best)
                {
                    best = probeHits[i].point.y;
                    found = true;
                }
            }

            if (!found)
            {
                Debug.LogWarning("[FlightSpawner] Nothing under the origin to spawn on - the aircraft " +
                                 "will be placed at y = 0. Check the scenery has colliders on the layers " +
                                 "in groundMask.");
            }

            return best;
        }
    }
}
