using System;
using CirrusFS.Flight;
using CirrusFS.Geo;
using UnityEngine;
using UnityEngine.Events;

namespace CirrusFS.Sim
{
    /// Puts a chosen aircraft at a chosen lat/lon/altitude/heading on the globe.
    ///
    /// The floating origin is reset onto the spawn point first, so the aircraft starts at
    /// Unity (0,0,0) with +Y = local up and +Z = local north, and every later position is a
    /// re-centre away from there.
    ///
    /// Knows nothing about UI; it takes a selection and returns the spawned GameObject.
    [DisallowMultipleComponent]
    public class FlightSpawner : MonoBehaviour
    {
        [Serializable] public class AircraftSpawnedEvent : UnityEvent<GameObject> { }

        [Tooltip("Optional parent for instantiated aircraft. Leave empty to spawn at scene root, " +
                 "which the floating origin needs to carry it.")]
        public Transform aircraftParent;

        [Tooltip("At or below this height above the ground (terrain, or the sea where there is no " +
                 "terrain tile) the aircraft spawns at rest on the surface instead of flying, metres.")]
        public float airborneAbove = 50f;

        [Tooltip("Throttle set for an airborne spawn, 0..1, so it does not start by decelerating.")]
        [Range(0f, 1f)] public float airborneThrottle = 0.6f;

        [Header("Events")]
        public AircraftSpawnedEvent onAircraftSpawned = new AircraftSpawnedEvent();

        public GameObject CurrentAircraft { get; private set; }

        public SpawnPoint CurrentSpawn { get; private set; }

        /// True when the current aircraft was an object already in the scene rather than one
        /// instantiated from a prefab. It decides whether despawning destroys or just hides it.
        public bool CurrentAircraftFromScene { get; private set; }

        public GameObject Spawn(AircraftEntry aircraft, SpawnPoint spawn)
        {
            if (aircraft?.Data == null)
            {
                Debug.LogError("[FlightSpawner] No aircraft selected.");
                return null;
            }

            if (!GeoMath.IsValidLatitude(spawn.latitude) || !GeoMath.IsValidLongitude(spawn.longitude))
            {
                Debug.LogError($"[FlightSpawner] Spawn position {spawn} is not a valid lat/lon.");
                return null;
            }

            GameObject instance = Resolve(aircraft.Data);
            if (instance == null) return null;

            // The surface under the spawn point: terrain where a tile exists (the triangles the
            // gear will roll on, not the smoother data surface), never below the sea.
            float terrain = 0f;
            bool hasTerrain = GlobeTerrain.Active != null &&
                              GlobeTerrain.Active.TryGetSurfaceHeight(spawn.latitude, spawn.longitude, out terrain);
            // Airport ground counts as land even below sea level (Schiphol, the Caspian).
            bool onAirport = hasTerrain && GlobeAirports.Active != null && GlobeAirports.Active.Database != null &&
                             GlobeAirports.Active.Database.FrameAt(spawn.latitude, spawn.longitude) != null;
            double ground = onAirport ? terrain : Math.Max(0.0, terrain);
            bool onLand = hasTerrain && (terrain > 0f || onAirport);

            bool airborne = spawn.altitude > ground + airborneAbove;
            double height = airborne ? spawn.altitude : Math.Max(spawn.altitude, ground + aircraft.Data.SpawnHeight);

            // The origin goes exactly where the aircraft will be: Unity (0,0,0), local axes.
            Double3 ecef = GeoMath.GeodeticToEcef(spawn.latitude, spawn.longitude, height);
            GeoOrigin.Reset(ecef);

            // On final approach the aircraft is already descending along the glidepath, nose
            // up at its approach attitude.
            bool approach = airborne && spawn.approach;
            float speedKnots = approach ? aircraft.Data.approach_speed_kt : spawn.speedKnots;
            Quaternion heading = GeoOrigin.HeadingPitchRollToUnity(spawn.latitude, spawn.longitude,
                                                                   spawn.heading, 0.0, 0.0);
            Quaternion rotation = approach
                ? GeoOrigin.HeadingPitchRollToUnity(spawn.latitude, spawn.longitude, spawn.heading,
                                                    aircraft.Data.approach_pitch_deg, 0.0)
                : heading;
            float descent = approach ? (float)(SpawnPoint.GlidePathDeg * GeoMath.Deg2Rad) : 0f;
            Vector3 velocity = airborne
                ? heading * new Vector3(0f, -Mathf.Sin(descent), Mathf.Cos(descent)) * (speedKnots / 1.943844f)
                : Vector3.zero;

            // The pose is set while the object is inactive: an inactive Rigidbody has no
            // interpolation history to smear from.
            bool wasActive = instance.activeSelf;
            if (wasActive) instance.SetActive(false);

            if (!instance.TryGetComponent(out GeoBody geoBody)) geoBody = instance.AddComponent<GeoBody>();
            instance.transform.SetPositionAndRotation(Vector3.zero, rotation);
            instance.SetActive(true);

            geoBody.Teleport(ecef, rotation, velocity);

            instance.TryGetComponent(out AircraftController controller);
            instance.TryGetComponent(out A320AnimationDriver systems);
            if (approach)
            {
                // landing configuration: gear down, landing flaps, approach power
                if (controller != null)
                {
                    controller.PrimeThrottle(aircraft.Data.approach_throttle);
                    controller.PrimeFlaps(aircraft.Data.approach_flap_index);
                }
                if (systems != null) systems.SetGearExtended();
            }
            else if (airborne)
            {
                if (controller != null) controller.PrimeThrottle(airborneThrottle);
                if (systems != null) systems.SetGearRetracted();
            }
            else if (systems != null)
            {
                systems.SetGearExtended();   // the aircraft may be reused from a flight that ended gear up
            }

            Physics.SyncTransforms();

            CurrentAircraft = instance;
            CurrentSpawn = spawn;

            string how = approach ? $"on final approach, {speedKnots:F0} kt, gear down, flaps {(controller != null ? controller.FlapIndex : 0)}"
                       : airborne ? $"airborne, {speedKnots:F0} kt"
                       : onLand ? $"on the ground at {ground:F0} m" : "on the water";
            Debug.Log($"[FlightSpawner] {aircraft.Data.DisplayName} at {spawn} ({how}). {GeoOrigin.Describe()}");

            onAircraftSpawned.Invoke(instance);
            return instance;
        }

        /// Removes the current aircraft: hidden if it belongs to the scene, destroyed if it was
        /// instantiated.
        public void Despawn()
        {
            if (CurrentAircraft == null) return;

            if (CurrentAircraftFromScene) CurrentAircraft.SetActive(false);
            else Destroy(CurrentAircraft);

            CurrentAircraft = null;
        }

        GameObject Resolve(AircraftData data)
        {
            GameObject instance = AircraftCatalog.FindSceneObject(data);
            CurrentAircraftFromScene = instance != null;
            if (instance != null) return instance;

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
            return instance;
        }
    }
}
