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

        [Tooltip("Below this height above the ellipsoid the aircraft spawns at rest on the water " +
                 "instead of flying, metres.")]
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

            bool airborne = spawn.altitude > airborneAbove;
            double height = airborne ? spawn.altitude : Math.Max(spawn.altitude, aircraft.Data.SpawnHeight);

            // The origin goes exactly where the aircraft will be: Unity (0,0,0), local axes.
            Double3 ecef = GeoMath.GeodeticToEcef(spawn.latitude, spawn.longitude, height);
            GeoOrigin.Reset(ecef);

            Quaternion rotation = GeoOrigin.HeadingPitchRollToUnity(spawn.latitude, spawn.longitude,
                                                                    spawn.heading, 0.0, 0.0);
            Vector3 velocity = airborne
                ? rotation * Vector3.forward * (spawn.speedKnots / 1.943844f)
                : Vector3.zero;

            // The pose is set while the object is inactive: an inactive Rigidbody has no
            // interpolation history to smear from.
            bool wasActive = instance.activeSelf;
            if (wasActive) instance.SetActive(false);

            if (!instance.TryGetComponent(out GeoBody geoBody)) geoBody = instance.AddComponent<GeoBody>();
            instance.transform.SetPositionAndRotation(Vector3.zero, rotation);
            instance.SetActive(true);

            geoBody.Teleport(ecef, rotation, velocity);

            if (airborne)
            {
                if (instance.TryGetComponent(out AircraftController controller)) controller.PrimeThrottle(airborneThrottle);
                if (instance.TryGetComponent(out A320AnimationDriver systems)) systems.SetGearRetracted();
            }

            Physics.SyncTransforms();

            CurrentAircraft = instance;
            CurrentSpawn = spawn;

            Debug.Log($"[FlightSpawner] {aircraft.Data.DisplayName} at {spawn} " +
                      $"({(airborne ? $"airborne, {spawn.speedKnots:F0} kt" : "on the water")}). " +
                      $"{GeoOrigin.Describe()}");

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
