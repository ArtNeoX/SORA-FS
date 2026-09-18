using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// Marks the root of a scenery model and does the one job that root has: move itself so
    /// that a chosen airport's runway sits exactly on the Unity scene origin.
    ///
    /// Put this on the parent transform of the whole region model. Its position is treated as
    /// the model's pivot, i.e. the point whose real-world coordinate is pivot_latitude /
    /// pivot_longitude in sceneries.json.
    [DisallowMultipleComponent]
    public class SceneryRegion : MonoBehaviour
    {
        static readonly List<SceneryRegion> active = new List<SceneryRegion>();
        static readonly RaycastHit[] probeHits = new RaycastHit[32];

        [Tooltip("Must match an 'id' in sceneries.json.")]
        public string sceneryId = "osaka_region";

        [Tooltip("Model units per real metre. 1 for a model imported at 1 unit = 1 m.")]
        public float modelUnitsPerMetre = 1f;

        [Tooltip("Degrees the model's own north is rotated from this transform's local +Z. " +
                 "0 for a model authored north-up. Use it when the export is turned, rather " +
                 "than rotating the root, which would turn the geography with it.")]
        public float modelNorthOffsetDeg;

        [Header("Vertical alignment")]
        [Tooltip("Raycast down onto the scenery and drop it so the runway surface lands on y = 0.")]
        public bool alignRunwaySurfaceToZero = true;

        [Tooltip("Height above the runway the alignment ray starts from, metres.")]
        public float probeHeight = 3000f;

        [Tooltip("Distance below the runway the alignment ray still searches, metres.")]
        public float probeDepth = 1000f;

        public LayerMask groundMask = ~0;

        /// The airport this region is currently aligned to, or null.
        public AirportData AlignedAirport { get; private set; }

        /// Scene Y the runway surface ended up at: 0 after a successful vertical alignment,
        /// NaN when the surface could not be found or alignment was off.
        public float RunwaySurfaceY { get; private set; } = float.NaN;

        public static IReadOnlyList<SceneryRegion> Active => active;

        /// World yaw that true north points along for this region.
        ///
        /// A region root is rarely left unrotated - an FBX that came out of Blender usually
        /// arrives turned - and that rotation turns the geography with it, so scene +Z stops
        /// being north. Everything geodetic goes through this instead of assuming +Z:
        /// the spawn heading, and WorldOrigin's position readouts.
        public float NorthYawDeg =>
            (float)GeoMath.WrapHeading(transform.rotation.eulerAngles.y + modelNorthOffsetDeg);

        /// Rotation from the geodetic frame (+X east, +Z north) into this region's world frame.
        public Quaternion GeoToWorldRotation => Quaternion.Euler(0f, NorthYawDeg, 0f);

        void OnEnable() => active.Add(this);

        void OnDisable() => active.Remove(this);

        /// The region whose id matches, searching active regions first and then the whole
        /// scene, so a region that starts disabled is still found.
        public static SceneryRegion Find(string sceneryId)
        {
            if (string.IsNullOrEmpty(sceneryId)) return null;

            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] != null && active[i].sceneryId == sceneryId) return active[i];
            }

            SceneryRegion[] all = FindObjectsByType<SceneryRegion>(FindObjectsInactive.Include,
                                                                   FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].sceneryId == sceneryId) return all[i];
            }

            return null;
        }

        /// Moves this region so the given airport's runway point lands on (0, 0, 0).
        ///
        /// Horizontally: the airport's offset from the model pivot is the geodetic delta, so
        /// the world position of that model point is TransformPoint(delta) and the region is
        /// translated by the negative of it. Going through TransformPoint rather than
        /// subtracting the delta directly means a rotated or scaled region root still lands
        /// correctly.
        ///
        /// Vertically: the surface height is whatever the model has there, so it is found by
        /// raycast and cancelled out. If nothing is hit - no colliders on the scenery - the
        /// region keeps its authored height and says so.
        public bool AlignAirportToOrigin(SceneryData scenery, AirportData airport)
        {
            if (scenery == null || airport == null)
            {
                Debug.LogError($"[SceneryRegion] {name}: AlignAirportToOrigin called with null data.");
                return false;
            }

            // The delta comes back in the geodetic frame; modelNorthOffsetDeg turns it into the
            // model's own local frame, and TransformPoint then applies the root's rotation and
            // scale - so a rotated or scaled region still lands the runway on the origin.
            Vector3 pivotToAirport = Quaternion.Euler(0f, modelNorthOffsetDeg, 0f) *
                                     scenery.PivotToAirport(airport) *
                                     Mathf.Max(0.0001f, modelUnitsPerMetre);
            Vector3 airportWorld = transform.TransformPoint(pivotToAirport);

            // X/Z only: the region's authored height is left alone here and dealt with by the
            // vertical pass below, which is the only step that knows where the surface is.
            transform.position -= new Vector3(airportWorld.x, 0f, airportWorld.z);
            Physics.SyncTransforms();

            AlignedAirport = airport;
            RunwaySurfaceY = float.NaN;

            if (alignRunwaySurfaceToZero)
            {
                if (TryFindSurfaceHeight(out float surfaceY))
                {
                    transform.position -= new Vector3(0f, surfaceY, 0f);
                    Physics.SyncTransforms();
                    RunwaySurfaceY = 0f;
                }
                else
                {
                    Debug.LogWarning($"[SceneryRegion] {sceneryId}: no collider under {airport.Icao} " +
                                     "within the probe range, so the runway height was left as authored. " +
                                     "Add colliders to the scenery, or turn off alignRunwaySurfaceToZero.");
                }
            }

            Debug.Log($"[SceneryRegion] {sceneryId}: {airport.Icao} aligned to origin. " +
                      $"Pivot offset {pivotToAirport.magnitude / 1000f:F2} km, " +
                      $"root now at {transform.position}.");
            return true;
        }

        /// Highest surface directly under the scene origin that belongs to this region.
        bool TryFindSurfaceHeight(out float surfaceY)
        {
            surfaceY = 0f;

            var ray = new Ray(new Vector3(0f, probeHeight, 0f), Vector3.down);
            int count = Physics.RaycastNonAlloc(ray, probeHits, probeHeight + probeDepth,
                                                groundMask, QueryTriggerInteraction.Ignore);

            bool found = false;
            for (int i = 0; i < count; i++)
            {
                // Ignore anything that is not part of this region - the aircraft may already be
                // parked at the origin from a previous flight.
                if (!probeHits[i].transform.IsChildOf(transform)) continue;

                if (!found || probeHits[i].point.y > surfaceY)
                {
                    surfaceY = probeHits[i].point.y;
                    found = true;
                }
            }

            return found;
        }

        /// Geodetic position of this region's pivot as it currently stands, for debugging an
        /// alignment that looks wrong.
        public void GetPivotGeoPosition(out double latitudeDeg, out double longitudeDeg)
        {
            WorldOrigin.GetGeoPosition(transform.position, out latitudeDeg, out longitudeDeg);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.16f, 0.90f, 0.35f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 50f);
            Gizmos.DrawLine(transform.position, Vector3.zero);

            Gizmos.color = new Color(1f, 0.51f, 0.08f, 0.9f);
            Gizmos.DrawLine(new Vector3(0f, probeHeight, 0f), new Vector3(0f, -probeDepth, 0f));
        }
    }
}
