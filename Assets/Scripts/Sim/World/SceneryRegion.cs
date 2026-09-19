using System;
using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Sim
{
    /// What a scenery is for. Both kinds are georeferenced and placed identically; the only
    /// difference is that an airport carries spawn points.
    public enum SceneryKind
    {
        /// Has a Starts object. You can begin a flight here.
        Airport,

        /// Terrain you fly over. Visual only.
        Region,
    }

    /// Marks the root of a scenery model, georeferences it from the anchors inside it, and
    /// places it so a chosen point lands on the scene origin.
    ///
    /// Put this on the parent transform of the whole model, keeping that root **yaw-only** - a
    /// tilted root makes north stop being horizontal, and the placement silently lands wrong.
    /// Everything positional comes from the model: anchors named after the coordinate they sit
    /// on say where it is, and with two or more of them the north and scale are solved too.
    [DisallowMultipleComponent]
    public class SceneryRegion : MonoBehaviour
    {
        static readonly List<SceneryRegion> active = new List<SceneryRegion>();
        static readonly RaycastHit[] probeHits = new RaycastHit[32];

        [Tooltip("Airport sceneries provide spawn points; region sceneries are visual only.")]
        public SceneryKind kind = SceneryKind.Airport;

        [Tooltip("ICAO for an airport scenery, or the region id. Must match sceneries.json.")]
        public string sceneryId = string.Empty;

        [Header("Georeference")]
        [Tooltip("Child objects named \"<latitude>, <longitude>\" sitting on the real point they " +
                 "name. Two or more also solve the model's north and scale.")]
        public bool useAnchors = true;

        [Tooltip("With two or more anchors, use the scale they imply instead of assuming the " +
                 "model is 1:1.")]
        public bool useSolvedScale = true;

        [Tooltip("Model units per real metre, used when the anchors cannot solve it.")]
        public float modelUnitsPerMetre = 1f;

        [Tooltip("Degrees the model's own north is rotated from this transform's local +Z.")]
        public float modelNorthOffsetDeg;

        [Header("Spawn markers")]
        [Tooltip("Added to a spawn marker's own yaw to get the direction the aircraft faces. " +
                 "Set it to 180 if your empties point back down the runway.")]
        public float markerHeadingOffsetDeg;

        [Tooltip("Width drawn for a runway discovered from markers, metres. Display only.")]
        public float markerRunwayWidthM = 45f;

        [Header("Streaming")]
        [Tooltip("Never culled, whatever the distance. For the wide backdrop.")]
        public bool alwaysLoaded;

        [Tooltip("Metres from the aircraft beyond which this scenery stops rendering, measured " +
                 "to the nearest point of its bounds. 0 means never cull.")]
        public float renderDistanceM;

        [Header("Vertical alignment")]
        [Tooltip("Raycast down onto the scenery and drop it so the surface lands on y = 0.")]
        public bool alignRunwaySurfaceToZero = true;

        [Tooltip("Height above the surface the alignment ray starts from, metres.")]
        public float probeHeight = 3000f;

        [Tooltip("Distance below the surface the alignment ray still searches, metres.")]
        public float probeDepth = 1000f;

        public LayerMask groundMask = ~0;

        [Tooltip("Height in this model's local space that represents sea level. Read when the " +
                 "scenery is placed by sea level rather than by a start.")]
        public float seaLevelLocalY;

        /// The start that was brought onto the scene origin, or null.
        public StartData AlignedStart { get; private set; }

        /// Scene Y the surface ended up at: 0 after a successful vertical alignment, NaN when it
        /// could not be found.
        public float RunwaySurfaceY { get; private set; } = float.NaN;

        /// The anchor the last measurement went through, if any.
        public GeoAnchor LastAnchorUsed { get; private set; }

        /// True heading the last resolved start faces, in world terms.
        public float LastStartHeading { get; private set; }

        public bool IsAirport => kind == SceneryKind.Airport;

        Renderer[] content;
        Collider[] contentColliders;
        bool contentVisible = true;

        public static IReadOnlyList<SceneryRegion> Active => active;

        /// World yaw that true north points along for this scenery.
        ///
        /// A model root is rarely left unrotated - an FBX out of Blender usually arrives turned -
        /// and that rotation turns the geography with it, so scene +Z stops being north.
        /// Everything geodetic goes through this rather than assuming +Z.
        public float NorthYawDeg =>
            (float)GeoMath.WrapHeading(transform.rotation.eulerAngles.y + modelNorthOffsetDeg);

        /// Rotation from the geodetic frame (+X east, +Z north) into this scenery's world frame.
        public Quaternion GeoToWorldRotation => Quaternion.Euler(0f, NorthYawDeg, 0f);

        void OnEnable() => active.Add(this);

        void OnDisable() => active.Remove(this);

        /// The scenery whose id matches, searching active ones first and then the whole scene,
        /// so one that starts disabled is still found.
        public static SceneryRegion Find(string sceneryId)
        {
            if (string.IsNullOrEmpty(sceneryId)) return null;

            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] != null &&
                    string.Equals(active[i].sceneryId, sceneryId, StringComparison.OrdinalIgnoreCase))
                {
                    return active[i];
                }
            }

            SceneryRegion[] all = FindObjectsByType<SceneryRegion>(FindObjectsInactive.Include,
                                                                   FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (string.Equals(all[i].sceneryId, sceneryId, StringComparison.OrdinalIgnoreCase))
                {
                    return all[i];
                }
            }

            return null;
        }

        /// Every scenery in the scene, including inactive ones.
        public static SceneryRegion[] FindAll()
        {
            return FindObjectsByType<SceneryRegion>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        }

        // ---- placement -----------------------------------------------------------

        /// Moves this scenery so the given start's empty lands on (0, 0, 0).
        ///
        /// The empty's world position is the answer outright - no projection, no survey data, no
        /// anchor error - so the aircraft ends up exactly where the modeller put it.
        public bool AlignStartToOrigin(AirportData airport, StartData start)
        {
            if (airport == null || start == null)
            {
                Debug.LogError($"[SceneryRegion] {name}: AlignStartToOrigin called with null data.", this);
                return false;
            }

            WarnIfTilted();
            LastAnchorUsed = default;

            Transform marker = start.MarkerTransform;
            if (marker == null)
            {
                Debug.LogError($"[SceneryRegion] {sceneryId}: start '{start.id}' has no marker object, " +
                               "so there is nothing to align to.", this);
                return false;
            }

            Vector3 markerWorld = marker.position;
            LastStartHeading = (float)GeoMath.WrapHeading(marker.eulerAngles.y + markerHeadingOffsetDeg);

            transform.position -= new Vector3(markerWorld.x, 0f, markerWorld.z);
            Physics.SyncTransforms();

            AlignedStart = start;
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
                    // No collider, but the marker was placed on the surface, so its own height is
                    // a better answer than leaving the model where it was authored.
                    transform.position -= new Vector3(0f, marker.position.y, 0f);
                    Physics.SyncTransforms();
                    RunwaySurfaceY = 0f;

                    Debug.LogWarning($"[SceneryRegion] {sceneryId}: no collider under {airport.Icao} " +
                                     $"{start.Label}, so the marker's own height was used. The aircraft " +
                                     "will sit at the right level but fall through.", this);
                }
            }

            Debug.Log($"[SceneryRegion] {sceneryId}: {airport.Icao} {start.Label} on the origin from " +
                      $"marker '{marker.name}', heading {LastStartHeading:F2}.", this);
            return true;
        }

        /// Places this scenery for a flight starting somewhere else: the given coordinate goes to
        /// the scene origin horizontally, and sea level to `seaLevelY`.
        ///
        /// Everything that is not the airport being departed from is placed this way, airports
        /// included - each through its own anchors, so no scenery inherits another's error.
        public bool AlignToOrigin(double latitudeDeg, double longitudeDeg, float seaLevelY)
        {
            WarnIfTilted();
            LastAnchorUsed = default;
            AlignedStart = null;
            RunwaySurfaceY = float.NaN;

            if (!TryLocate(latitudeDeg, longitudeDeg, out Vector3 world))
            {
                Debug.LogWarning($"[SceneryRegion] {sceneryId}: no anchors, so it cannot be placed. " +
                                 "Add a child named after the coordinate it sits on.", this);
                return false;
            }

            transform.position -= new Vector3(world.x, 0f, world.z);

            Vector3 position = transform.position;
            position.y = seaLevelY - seaLevelLocalY * transform.lossyScale.y;
            transform.position = position;
            Physics.SyncTransforms();

            return true;
        }

        /// World position of a geodetic coordinate, measured out from the nearest anchor.
        public bool TryLocate(double latitudeDeg, double longitudeDeg, out Vector3 world)
        {
            world = default;

            if (!TryGetAnchorFrame(out List<GeoAnchor> anchors, out float northYaw, out float unitsPerMetre))
            {
                return false;
            }

            GeoAnchor nearest = anchors[0];
            double nearestDistance = double.MaxValue;

            for (int i = 0; i < anchors.Count; i++)
            {
                double d = GeoMath.HaversineDistance(latitudeDeg, longitudeDeg,
                                                     anchors[i].Latitude, anchors[i].Longitude);
                if (d < nearestDistance)
                {
                    nearestDistance = d;
                    nearest = anchors[i];
                }
            }

            GeoMath.GeoToLocal(latitudeDeg, longitudeDeg, nearest.Latitude, nearest.Longitude,
                               out double east, out double north);

            var offset = new Vector3((float)east, 0f, (float)north);
            world = nearest.WorldPosition + Quaternion.Euler(0f, northYaw, 0f) * offset * unitsPerMetre;

            LastAnchorUsed = nearest;
            return true;
        }

        /// The geodetic coordinate of a world point. The inverse of TryLocate, and what lets a
        /// marker-placed start report a lat/lon for the map and the GPS.
        public bool TryGetGeoPosition(Vector3 world, out double latitudeDeg, out double longitudeDeg)
        {
            latitudeDeg = 0.0;
            longitudeDeg = 0.0;

            if (!TryGetAnchorFrame(out List<GeoAnchor> anchors, out float northYaw, out float unitsPerMetre))
            {
                return false;
            }

            GeoAnchor nearest = anchors[0];
            float best = float.MaxValue;

            for (int i = 0; i < anchors.Count; i++)
            {
                float d = Vector3.SqrMagnitude(anchors[i].WorldPosition - world);
                if (d < best)
                {
                    best = d;
                    nearest = anchors[i];
                }
            }

            Vector3 offset = Quaternion.Euler(0f, -northYaw, 0f) *
                             ((world - nearest.WorldPosition) / Mathf.Max(0.0001f, unitsPerMetre));

            GeoMath.LocalToGeo(offset.x, offset.z, nearest.Latitude, nearest.Longitude,
                               out latitudeDeg, out longitudeDeg);

            LastAnchorUsed = nearest;
            return true;
        }

        /// The anchors plus the frame they imply: which way north points and how many model units
        /// make a metre. One place, so placement and derivation cannot disagree.
        public bool TryGetAnchorFrame(out List<GeoAnchor> anchors, out float northYaw, out float unitsPerMetre)
        {
            anchors = useAnchors ? GeoAnchor.FindAll(transform) : new List<GeoAnchor>();
            northYaw = NorthYawDeg;
            unitsPerMetre = Mathf.Max(0.0001f, modelUnitsPerMetre);

            if (anchors.Count == 0) return false;

            if (anchors.Count >= 2 && TrySolveFromAnchors(anchors, out float solvedYaw, out float solvedScale))
            {
                northYaw = solvedYaw;
                if (useSolvedScale) unitsPerMetre = solvedScale;
            }

            return true;
        }

        /// North and scale implied by a pair of anchors: the rotation and length ratio between
        /// the line they make in the world and the line they make on the globe.
        bool TrySolveFromAnchors(List<GeoAnchor> anchors, out float northYaw, out float scale)
        {
            northYaw = NorthYawDeg;
            scale = Mathf.Max(0.0001f, modelUnitsPerMetre);

            GeoAnchor a = anchors[0];
            GeoAnchor b = anchors[1];

            Vector3 worldVector = b.WorldPosition - a.WorldPosition;
            worldVector.y = 0f;

            GeoMath.GeoToLocal(b.Latitude, b.Longitude, a.Latitude, a.Longitude,
                               out double east, out double north);
            var geoVector = new Vector3((float)east, 0f, (float)north);

            if (geoVector.magnitude < 1f || worldVector.magnitude < 0.001f)
            {
                Debug.LogWarning($"[SceneryRegion] {sceneryId}: anchors {a} and {b} are too close together " +
                                 "to solve north from.", this);
                return false;
            }

            northYaw = (float)GeoMath.WrapHeading(GeoMath.HeadingFromDirection(worldVector) -
                                                  GeoMath.HeadingFromDirection(geoVector));
            scale = worldVector.magnitude / geoVector.magnitude;
            return true;
        }

        // ---- spawn points declared by the model ----------------------------------

        /// The airport's spawn empties, turned into starts.
        ///
        /// The container's children *are* the starts: their names give the id and the kind, their
        /// positions are where the aircraft goes, and their facing is which way it points.
        /// Coordinates are derived from the anchors so the map and the readouts work, but nothing
        /// is read from the config - a start in the model is a start in the menu, and one that is
        /// not there, is not.
        public List<StartData> DiscoverStarts(AirportData airport)
        {
            var discovered = new List<StartData>();

            Transform container = FindStartsContainer(airport);
            if (container == null) return discovered;

            TryGetAnchorFrame(out _, out float northYaw, out _);

            foreach (Transform child in container)
            {
                string raw = child.name.Trim();
                if (string.IsNullOrEmpty(raw)) continue;

                var start = new StartData
                {
                    id = StartIdFromName(raw, out bool isRunway),
                    type = isRunway ? StartData.TypeRunway : StartData.TypeRamp,
                    marker = raw,
                    MarkerTransform = child,
                };

                // The empty's yaw is a world heading; a true heading takes the model's north out.
                float worldYaw = child.eulerAngles.y + markerHeadingOffsetDeg;
                double trueHeading = GeoMath.WrapHeading(worldYaw - northYaw);

                if (!TryGetGeoPosition(child.position, out double lat, out double lon))
                {
                    Debug.LogWarning($"[SceneryRegion] {sceneryId}: start '{raw}' has no anchor to derive " +
                                     "a coordinate from, so it will not show on the map.", this);
                }

                start.SetDerivedPosition(lat, lon, trueHeading);
                discovered.Add(start);
            }

            PairRunways(discovered);
            return discovered;
        }

        /// Where the spawn empties live: the airport's `starts_object` if it names one, else a
        /// child called "Starts".
        Transform FindStartsContainer(AirportData airport)
        {
            string wanted = airport != null && !string.IsNullOrWhiteSpace(airport.starts_object)
                ? airport.starts_object.Trim()
                : "Starts";

            foreach (Transform child in transform.GetComponentsInChildren<Transform>(true))
            {
                if (child != transform &&
                    string.Equals(child.name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

        /// Reads the kind off the name's prefix and strips it where that reads better.
        ///
        ///   "RWY 16R"   -> runway "16R",   shown as "RWY 16R"
        ///   "APRON 17"  -> ramp "APRON 17", shown as it stands
        ///
        /// The runway prefix goes because the label puts it back; a stand's does not, because
        /// "17" on its own says nothing.
        static string StartIdFromName(string raw, out bool isRunway)
        {
            isRunway = false;

            string upper = raw.ToUpperInvariant();

            if (upper.StartsWith("RWY", StringComparison.Ordinal) ||
                upper.StartsWith("RUNWAY", StringComparison.Ordinal))
            {
                isRunway = true;
                int cut = upper.StartsWith("RUNWAY", StringComparison.Ordinal) ? 6 : 3;
                string remainder = raw.Substring(cut).Trim();
                return string.IsNullOrEmpty(remainder) ? raw : remainder;
            }

            // Anything else is somewhere to park: APRON, RAMP, GATE, STAND, or a bare name.
            return raw;
        }

        /// "16R" and its reciprocal "34L" are two ends of one strip, so the distance between them
        /// is the length the map should draw. Unpaired runways get a label and no strip.
        ///
        /// The side letter is matched first and then ignored, because a model with a single
        /// runway is often labelled "06R" and "24R" rather than the "06R"/"24L" a real chart
        /// would use, and refusing to pair those would be pedantry.
        void PairRunways(List<StartData> starts)
        {
            foreach (StartData start in starts)
            {
                if (!start.IsRunway) continue;

                string reciprocal = ReciprocalRunwayId(start.id);
                if (reciprocal == null) continue;

                StartData other = starts.Find(s => s.IsRunway && s != start &&
                                                   string.Equals(s.id, reciprocal, StringComparison.OrdinalIgnoreCase))
                                  ?? starts.Find(s => s.IsRunway && s != start &&
                                                      SameRunwayNumber(s.id, reciprocal));
                if (other == null) continue;

                start.length_m = GeoMath.HaversineDistance(start.latitude, start.longitude,
                                                           other.latitude, other.longitude);
                start.width_m = markerRunwayWidthM;
            }
        }

        /// Do two designators share their number, whatever side letter they carry?
        static bool SameRunwayNumber(string a, string b)
        {
            return RunwayNumber(a) > 0 && RunwayNumber(a) == RunwayNumber(b);
        }

        static int RunwayNumber(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;

            int split = 0;
            while (split < id.Length && char.IsDigit(id[split])) split++;

            return split > 0 && int.TryParse(id.Substring(0, split), out int number) ? number : 0;
        }

        static string ReciprocalRunwayId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 3) return null;

            int split = 0;
            while (split < id.Length && char.IsDigit(id[split])) split++;

            if (split == 0 || !int.TryParse(id.Substring(0, split), out int number)) return null;

            string side = id.Substring(split).ToUpperInvariant();
            if (side.Length > 1) return null;
            if (side.Length == 1 && side != "L" && side != "C" && side != "R") return null;

            number += 18;
            if (number > 36) number -= 36;

            if (side == "L") side = "R";
            else if (side == "R") side = "L";

            return number.ToString("00") + side;
        }

        // ---- streaming -----------------------------------------------------------

        /// Turns the model's renderers and colliders on or off. The root stays active so the
        /// scenery can still be found, measured and re-enabled.
        public void SetContentVisible(bool visible)
        {
            if (content == null) CacheContent();
            if (contentVisible == visible) return;

            contentVisible = visible;

            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] != null) content[i].enabled = visible;
            }

            for (int i = 0; i < contentColliders.Length; i++)
            {
                if (contentColliders[i] != null) contentColliders[i].enabled = visible;
            }
        }

        public bool ContentVisible => contentVisible;

        void CacheContent()
        {
            content = GetComponentsInChildren<Renderer>(true);
            contentColliders = GetComponentsInChildren<Collider>(true);
        }

        /// Distance from a point to the nearest part of this scenery, horizontally. Zero when the
        /// point is inside its footprint.
        public float HorizontalDistanceTo(Vector3 point)
        {
            if (content == null) CacheContent();
            if (content.Length == 0) return Vector3.Distance(point, transform.position);

            Bounds bounds = content[0].bounds;
            for (int i = 1; i < content.Length; i++) bounds.Encapsulate(content[i].bounds);

            float dx = Mathf.Max(0f, Mathf.Max(bounds.min.x - point.x, point.x - bounds.max.x));
            float dz = Mathf.Max(0f, Mathf.Max(bounds.min.z - point.z, point.z - bounds.max.z));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // ---- helpers -------------------------------------------------------------

        /// The root this component sits on must be rotated about Y only.
        ///
        /// An FBX out of Blender arrives with a -90 degree X rotation, and if that lands on the
        /// root then north stops being horizontal: the geodetic delta's north component is mapped
        /// onto world up and thrown away by the X/Z placement, so the scenery lands in the wrong
        /// place with no other symptom. Put the model under a child that carries the tilt.
        void WarnIfTilted()
        {
            float tilt = Vector3.Angle(transform.up, Vector3.up);
            if (tilt <= 0.5f) return;

            Debug.LogError($"[SceneryRegion] {sceneryId}: the root is tilted {tilt:F1} degrees off vertical " +
                           $"(up = {transform.up}). Placement only works on a yaw-only root: north has to " +
                           "stay horizontal. Put the tilted model under a child.", this);
        }

        /// Highest surface directly under the scene origin that belongs to this scenery.
        bool TryFindSurfaceHeight(out float surfaceY)
        {
            surfaceY = 0f;

            var ray = new Ray(new Vector3(0f, probeHeight, 0f), Vector3.down);
            int count = Physics.RaycastNonAlloc(ray, probeHits, probeHeight + probeDepth,
                                                groundMask, QueryTriggerInteraction.Ignore);

            bool found = false;
            for (int i = 0; i < count; i++)
            {
                if (!probeHits[i].transform.IsChildOf(transform)) continue;

                if (!found || probeHits[i].point.y > surfaceY)
                {
                    surfaceY = probeHits[i].point.y;
                    found = true;
                }
            }

            return found;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.16f, 0.90f, 0.35f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, 50f);

            Gizmos.color = new Color(1f, 0.51f, 0.08f, 0.9f);
            Gizmos.DrawLine(new Vector3(0f, probeHeight, 0f), new Vector3(0f, -probeDepth, 0f));
        }
    }
}
