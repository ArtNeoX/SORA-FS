using System;
using UnityEngine;

namespace CirrusFS.Flight
{
    /// Raycast landing-gear suspension: spring + damper + tyre grip, one force per leg
    /// applied at the strut top so pitch and roll self-level naturally.
    ///
    /// It auto-tunes to the airframe: the static load on each leg is solved from geometry,
    /// so spring rates are proportional to the weight that leg actually carries (mains stiff,
    /// nose soft) and the aircraft sits level at its modelled height with no settling jump.
    [RequireComponent(typeof(Rigidbody))]
    public class LandingGearSuspension : MonoBehaviour
    {
        const float MaxNormalMult = 30f;
        const float MaxTotalMult = 45f;
        const float MinDt = 1f / 240f;
        const float MaxDt = 1f / 20f;

        [Serializable]
        public class Gear
        {
            public string name = "Gear";
            [Tooltip("Wheel centre at rest, in metres from the controller's ref point.")]
            public Vector3 mountLocal;
            public float radius = 0.28f;
            public float stiffnessScale = 1f;
            public float sideGripScale = 1f;
            [Tooltip("Optional visual wheel; telescopes along the strut.")]
            public Transform wheel;

            [NonSerialized] public float staticLoad;
            [NonSerialized] public float px, pz;
            // Live strut compression in metres (0 = unloaded) and ground contact, for visuals.
            [NonSerialized] public float compression;
            [NonSerialized] public bool grounded;
        }

        public bool enableSuspension = true;

        [Header("Tuning")]
        [Tooltip("Static sink under weight, metres. Smaller = firmer.")]
        public float rideCompression = 0.224f;
        [Tooltip("Strut-top height above the wheel centre, metres.")]
        public float mountHeight = 0.56f;
        [Tooltip("1 = critically damped (no bounce), >1 firmer.")]
        public float dampingRatio = 1f;
        [Tooltip("Lateral tyre grip coefficient. 0 = ice.")]
        public float sideGrip = 0.8f;
        [Tooltip("Rolling resistance when not braking.")]
        public float rollResist = 0.02f;
        [Tooltip("Longitudinal grip while the brake is held.")]
        public float brakeGrip = 0.9f;

        [Header("Debug")]
        [Tooltip("Metres of beam drawn per Newton. Beams are toggled with M on the controller.")]
        public float beamScale = 0.00029f;
        public float beamWidth = 0.06f;
        public bool animateWheels = true;

        public Gear[] gears =
        {
            new Gear { name = "LG", mountLocal = new Vector3(-1.72f, -0.865f, -4.676f), radius = 0.28f },
            new Gear { name = "NW", mountLocal = new Vector3(0f, -0.801f, -1.332f), radius = 0.224f },
            new Gear { name = "RG", mountLocal = new Vector3(1.72f, -0.865f, -4.676f), radius = 0.28f },
        };

        Rigidbody rb;
        AircraftController controller;
        ForceBeams beams;
        readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        static readonly Color GearColor = new Color(1f, 0.82f, 0.16f);

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            controller = GetComponent<AircraftController>();
            beams = new ForceBeams("GearBeams", transform, gears.Length, beamWidth);
        }

        void FixedUpdate()
        {
            if (!enableSuspension || gears.Length == 0) return;

            float h = Mathf.Clamp(Time.fixedDeltaTime, MinDt, MaxDt);
            float g = Physics.gravity.magnitude;
            float W = rb.mass * g;
            if (W <= 0f) return;

            bool braking = controller != null && controller.Braking;
            beams.Visible = controller != null && controller.BeamsVisible;
            float restLength = mountHeight + rideCompression;
            Vector3 com = rb.worldCenterOfMass;
            Vector3 upLocal = new Vector3(0f, mountHeight, 0f);
            Vector3 refOffset = RefOffset;

            foreach (var gr in gears)
            {
                Vector3 top = transform.TransformPoint(refOffset + gr.mountLocal + upLocal);
                gr.px = top.x;
                gr.pz = top.z;
            }
            SolveLoads(gears, com.x, com.z, W);

            Vector3 rightDir = FlatUnit(transform.right);
            Vector3 fwdDir = FlatUnit(transform.forward);
            float muLong = braking ? brakeGrip : rollResist;

            for (int i = 0; i < gears.Length; i++)
            {
                Gear gr = gears[i];
                Vector3 strutTop = transform.TransformPoint(refOffset + gr.mountLocal + upLocal);
                float maxLen = restLength + gr.radius;

                Vector3 force = Vector3.zero;
                float suspLength = restLength;
                bool grounded = false;
                Vector3 contact = Vector3.zero;

                if (gr.staticLoad > 0f && RaycastGround(strutTop, maxLen, out RaycastHit hit))
                {
                    float d = strutTop.y - hit.point.y;
                    float compression = Mathf.Clamp(restLength - (d - gr.radius), 0f, restLength);
                    suspLength = restLength - compression;

                    if (compression > 0f)
                    {
                        // Spring rate so this leg sinks exactly rideCompression under its share.
                        float k = gr.staticLoad / rideCompression * gr.stiffnessScale;
                        float m = gr.staticLoad / g;
                        float c = dampingRatio * 2f * Mathf.Sqrt(Mathf.Max(k * m, 0f));

                        Vector3 pointVel = rb.GetPointVelocity(hit.point);
                        float fUp = Mathf.Clamp(k * compression - c * pointVel.y, 0f, MaxNormalMult * gr.staticLoad);

                        // Tyre friction, bounded by the friction circle (mu * current normal).
                        Vector3 fric = Vector3.zero;
                        if (fUp > 0f)
                        {
                            Vector3 horiz = new Vector3(pointVel.x, 0f, pointVel.z);
                            float muSide = sideGrip * gr.sideGripScale;
                            float vSide = Vector3.Dot(horiz, rightDir);
                            float vFwd = Vector3.Dot(horiz, fwdDir);
                            float fSide = Mathf.Clamp(-vSide * m / h, -muSide * fUp, muSide * fUp);
                            float fFwd = Mathf.Clamp(-vFwd * m / h, -muLong * fUp, muLong * fUp);
                            fric = rightDir * fSide + fwdDir * fFwd;
                        }

                        force = new Vector3(0f, fUp, 0f) + fric;
                        force = ClampMagnitude(force, MaxTotalMult * gr.staticLoad);
                        if (IsInvalid(force)) force = Vector3.zero;

                        grounded = force.sqrMagnitude > 0f;
                        contact = hit.point;
                    }
                }

                gr.compression = restLength - suspLength;
                gr.grounded = grounded;

                if (force != Vector3.zero)
                    rb.AddForceAtPosition(force, strutTop);

                // Wheel telescopes to match live travel; airborne = full droop.
                if (animateWheels && gr.wheel)
                    gr.wheel.localPosition = refOffset + gr.mountLocal + new Vector3(0f, mountHeight - suspLength, 0f);

                // Rooted at the contact and drawn along the applied force; airborne legs
                // collapse to a zero-length beam so nothing is rendered.
                Vector3 tip = grounded ? contact + force * beamScale : contact;
                beams.Set(i, contact, tip, GearColor);
            }
        }

        // Gear legs share the flight model's ref point, so moving it carries them along.
        Vector3 RefOffset
        {
            get
            {
                if (controller == null) controller = GetComponent<AircraftController>();
                return controller != null ? controller.RefOffset : Vector3.zero;
            }
        }

        // Wheels at rest (yellow) and strut tops (white), relative to the ref point.
        void OnDrawGizmosSelected()
        {
            Vector3 r = RefOffset;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            foreach (var gr in gears)
            {
                Vector3 c = r + gr.mountLocal;
                Gizmos.color = GearColor;
                Gizmos.DrawWireSphere(c, gr.radius);
                Gizmos.color = Color.white;
                Gizmos.DrawLine(c, c + new Vector3(0f, mountHeight, 0f));
            }
        }

        /// Nearest ground hit below the strut, skipping the aircraft's own colliders.
        bool RaycastGround(Vector3 origin, float maxLen, out RaycastHit best)
        {
            best = default;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, hitBuffer, maxLen,
                ~0, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                if (hitBuffer[i].collider.transform.IsChildOf(transform)) continue;
                if (hitBuffer[i].distance >= nearest) continue;
                nearest = hitBuffer[i].distance;
                best = hitBuffer[i];
                found = true;
            }
            return found;
        }

        /// Static vertical load on each leg. Exact for the usual 3-point layout via Cramer's
        /// rule on [sumF = W; moment_X = 0; moment_Z = 0]; any other count splits equally.
        /// Negative shares (CoM outside the footprint) are floored and the set renormalised.
        static void SolveLoads(Gear[] gears, float comX, float comZ, float W)
        {
            int n = gears.Length;
            if (n == 3 && W > 0f)
            {
                Gear g1 = gears[0], g2 = gears[1], g3 = gears[2];
                float b1 = g1.px - comX, b2 = g2.px - comX, b3 = g3.px - comX;
                float c1 = g1.pz - comZ, c2 = g2.pz - comZ, c3 = g3.pz - comZ;
                float D = (b2 * c3 - b3 * c2) - (b1 * c3 - b3 * c1) + (b1 * c2 - b2 * c1);

                if (Mathf.Abs(D) > 1e-6f)
                {
                    var loads = new[]
                    {
                        W * (b2 * c3 - b3 * c2) / D,
                        W * (b3 * c1 - b1 * c3) / D,
                        W * (b1 * c2 - b2 * c1) / D,
                    };
                    float floor = 0.02f * W;
                    float sum = 0f;
                    for (int i = 0; i < 3; i++)
                    {
                        if (loads[i] < floor) loads[i] = floor;
                        sum += loads[i];
                    }
                    if (sum > 0f)
                        for (int i = 0; i < 3; i++)
                            gears[i].staticLoad = loads[i] * W / sum;
                    return;
                }
            }

            float eq = n > 0 ? W / n : 0f;
            foreach (var gr in gears) gr.staticLoad = eq;
        }

        static Vector3 FlatUnit(Vector3 v)
        {
            Vector3 f = new Vector3(v.x, 0f, v.z);
            return f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.zero;
        }

        static Vector3 ClampMagnitude(Vector3 v, float max)
        {
            float mag = v.magnitude;
            return (mag > max && mag > 0f) ? v * (max / mag) : v;
        }

        static bool IsInvalid(Vector3 v) =>
            float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
            float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
    }
}

