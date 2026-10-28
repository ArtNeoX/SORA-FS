using UnityEngine;

namespace CirrusFS.Geo
{
    /// A small collision patch of the h = 0 ellipsoid that follows a target, so the landing
    /// gear's raycasts (and anything else with a collider) can touch the sea.
    ///
    /// The ocean you see is render-only and runs to planet scale; a MeshCollider of that is
    /// neither possible in float nor useful. This patch is built the same way as the ocean
    /// chunks - exact ellipsoid points in double, stored relative to the patch centre in its
    /// local frame - and is rebuilt under the target whenever it drifts off centre. It is
    /// switched off when the target is high enough that nothing could reach the water.
    [DefaultExecutionOrder(-900)]
    [DisallowMultipleComponent]
    public class WaterSurfaceCollider : MonoBehaviour
    {
        [Tooltip("What the patch follows. Set at runtime by whoever spawns the aircraft.")]
        public Transform target;

        [Tooltip("Patch edge length, metres.")]
        public float size = 1200f;

        [Tooltip("Grid cells along each edge.")]
        [Range(4, 128)] public int cells = 48;

        [Tooltip("Rebuild when the target is this far from the patch centre, metres.")]
        public float recentreDistance = 300f;

        [Tooltip("Above this height the patch is disabled, metres.")]
        public float activeBelowHeight = 1500f;

        [Tooltip("Physics material for the water surface (optional).")]
        public PhysicsMaterial surfaceMaterial;

        MeshCollider meshCollider;
        Mesh mesh;
        Double3 centreEcef;
        Double3 centreNorth, centreUp;
        bool built;

        public bool IsActive => meshCollider != null && meshCollider.enabled;

        void Awake()
        {
            meshCollider = gameObject.GetComponent<MeshCollider>();
            if (meshCollider == null) meshCollider = gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMaterial = surfaceMaterial;
            meshCollider.enabled = false;

            mesh = new Mesh { name = "Water contact patch" };
        }

        void OnEnable()
        {
            GeoOrigin.Recentered += HandleRecentered;
            GeoOrigin.OriginReset += HandleOriginReset;
        }

        void OnDisable()
        {
            GeoOrigin.Recentered -= HandleRecentered;
            GeoOrigin.OriginReset -= HandleOriginReset;
        }

        void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
        }

        void HandleRecentered(GeoFrameChange change)
        {
            if (built) Place();
        }

        void HandleOriginReset() => built = false;

        // Runs in FixedUpdate, ahead of the gear, so a new patch is in place before it raycasts.
        void FixedUpdate()
        {
            if (target == null || !GeoOrigin.IsSet)
            {
                meshCollider.enabled = false;
                return;
            }

            Double3 targetEcef = GeoOrigin.UnityToEcef(target.position);
            GeoMath.EcefToGeodetic(targetEcef, out double lat, out double lon, out double h);

            bool wanted = h < activeBelowHeight;
            if (!wanted)
            {
                meshCollider.enabled = false;
                return;
            }

            Double3 below = GeoMath.GeodeticToEcef(lat, lon, 0.0);
            if (!built || Double3.Distance(below, centreEcef) > recentreDistance)
            {
                Rebuild(lat, lon);
            }

            meshCollider.enabled = true;
        }

        void Rebuild(double lat, double lon)
        {
            centreEcef = GeoMath.GeodeticToEcef(lat, lon, 0.0);
            GeoMath.EnuBasis(lat, lon, out Double3 east, out Double3 north, out Double3 up);
            centreNorth = north;
            centreUp = up;

            int n = cells;
            var vertices = new Vector3[(n + 1) * (n + 1)];
            var indices = new int[n * n * 6];

            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++)
                {
                    double e = (i / (double)n - 0.5) * size;
                    double nn = (j / (double)n - 0.5) * size;

                    // Step out along the tangent plane, then drop onto the ellipsoid.
                    Double3 guess = centreEcef + east * e + north * nn;
                    Double3 surface = GeoMath.SurfacePointBelow(guess);
                    Double3 d = surface - centreEcef;

                    vertices[j * (n + 1) + i] = new Vector3((float)Double3.Dot(d, east),
                                                            (float)Double3.Dot(d, up),
                                                            (float)Double3.Dot(d, north));
                }
            }

            int k = 0;
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
                    indices[k++] = a; indices[k++] = c; indices[k++] = b;
                    indices[k++] = b; indices[k++] = c; indices[k++] = d;
                }
            }

            mesh.Clear();
            mesh.vertices = vertices;
            mesh.triangles = indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            built = true;
            Place();

            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = mesh;
            Physics.SyncTransforms();
        }

        void Place()
        {
            transform.SetPositionAndRotation(
                GeoOrigin.EcefToUnity(centreEcef),
                Quaternion.LookRotation(GeoOrigin.EcefDirToUnity(centreNorth), GeoOrigin.EcefDirToUnity(centreUp)));
        }
    }
}
