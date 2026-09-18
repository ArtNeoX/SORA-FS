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

        /// Aligns the scenery, spawns the aircraft on the runway and returns it, or null if
        /// anything in the chain was missing. Every failure is logged with the reason.
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

            SceneryRegion region = ResolveRegion(airport.Scenery);
            if (region == null) return null;

            if (deactivateOtherRegions) DeactivateOtherRegions(region);
            if (!region.gameObject.activeSelf) region.gameObject.SetActive(true);

            // Order matters: the region has to be aligned before the aircraft is placed,
            // because the spawn height is read off the aligned surface.
            if (!region.AlignAirportToOrigin(airport.Scenery, airport.Airport)) return null;

            // Scene (0, 0, 0) is now this runway, with north along the region's yaw. Everything
            // geodetic is measured from here.
            WorldOrigin.SetReference(airport.Airport.Latitude, airport.Airport.Longitude,
                                     region.NorthYawDeg);

            GameObject spawned = PlaceAircraft(aircraft, airport.Airport, region);
            if (spawned == null) return null;

            CurrentAircraft = spawned;
            CurrentRegion = region;
            CurrentAirport = airport;

            Debug.Log($"[FlightSpawner] {aircraft.Data.DisplayName} on {airport.Airport.Icao} " +
                      $"{airport.Airport.RunwayLabel}, heading {airport.Airport.HeadingDeg:F0} deg. " +
                      $"{WorldOrigin.Describe()}");

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

        SceneryRegion ResolveRegion(SceneryData scenery)
        {
            // scene_object names an instance already in the scene; otherwise match on id.
            SceneryRegion region = SceneryRegion.Find(scenery.Id);

            if (region == null && !string.IsNullOrWhiteSpace(scenery.scene_object))
            {
                GameObject existing = GameObject.Find(scenery.scene_object);
                if (existing != null) region = existing.GetComponent<SceneryRegion>();
            }

            if (region == null && !string.IsNullOrWhiteSpace(scenery.prefab_resource_path))
            {
                var prefab = Resources.Load<GameObject>(scenery.prefab_resource_path);
                if (prefab != null)
                {
                    region = Instantiate(prefab).GetComponent<SceneryRegion>();
                    if (region != null) region.sceneryId = scenery.Id;
                }
            }

            if (region == null)
            {
                Debug.LogError($"[FlightSpawner] No SceneryRegion with id '{scenery.Id}' in the scene, " +
                               "and no prefab_resource_path to load one from. Add the component to the " +
                               "region model's root.");
            }

            return region;
        }

        void DeactivateOtherRegions(SceneryRegion keep)
        {
            SceneryRegion[] all = FindObjectsByType<SceneryRegion>(FindObjectsInactive.Include,
                                                                   FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != keep && all[i].gameObject.activeSelf) all[i].gameObject.SetActive(false);
            }
        }

        // ---- aircraft ------------------------------------------------------------

        GameObject PlaceAircraft(AircraftEntry aircraft, AirportData airport, SceneryRegion region)
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

            float surfaceY = ProbeSurfaceHeight();
            var position = new Vector3(0f, surfaceY + data.SpawnHeight, 0f);

            // The runway heading is a true heading, so it is applied in the region's frame:
            // scene +Z is north only for a region whose root is unrotated.
            Quaternion rotation = region.GeoToWorldRotation * airport.SpawnRotation;

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

        /// Height of the scenery surface at the origin. Zero when the region aligned its runway
        /// to y = 0, and the real height when it could not.
        float ProbeSurfaceHeight()
        {
            var ray = new Ray(new Vector3(0f, groundProbeHeight, 0f), Vector3.down);
            int count = Physics.RaycastNonAlloc(ray, probeHits, groundProbeHeight + groundProbeDepth,
                                                groundMask, QueryTriggerInteraction.Ignore);

            float best = 0f;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                if (CurrentAircraft != null && probeHits[i].transform.IsChildOf(CurrentAircraft.transform))
                {
                    continue;
                }

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
