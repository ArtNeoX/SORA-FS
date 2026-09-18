using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.Flight
{
    /// Blade-element flight controller. Runs the whole aero model in the aircraft's
    /// local frame (+Z fwd, +Y up, +X right), sums one net force and one net torque
    /// per FixedUpdate, and applies them to the Rigidbody.
    ///
    ///   Thrust E / Q    Pitch W / S    Roll A / D    Rudder Z / C
    ///   Flaps 1 / 2     Airbrakes 3 / 4    Wheel brake B
    [RequireComponent(typeof(Rigidbody))]
    public class AircraftController : MonoBehaviour
    {
        const float MetresPerSecondToKnots = 1.94384f;

        public FlightConfig config = new FlightConfig();

        [Serializable]
        public class ControlSurfaceVisual
        {
            public Transform hinge;
            public ControlRole role = ControlRole.None;
            public bool isFlap;
            public bool isAirbrake;
            [Tooltip("Local axis the surface rotates about.")]
            public Vector3 axis = Vector3.right;
            public float maxDeg = 20f;
            [HideInInspector] public Quaternion rest;
        }

        public ControlSurfaceVisual[] controlVisuals = Array.Empty<ControlSurfaceVisual>();

        Rigidbody rb;
        Station[] stations;
        EngineState[] engines;

        ActuatorSpring pitchSpring, rollSpring, yawSpring;

        float throttle;
        int flapIndex, airbrkIndex;
        float flapPos, airbrkPos;
        float maxFlap, maxAirbrk;

        // Display data captured on the final substep, in aircraft-local space.
        Vector3[] beamOrigin, beamLift, beamDrag, dragPointForce;
        ForceBeams beams;
        bool beamsVisible;

        static readonly Color LiftColor = new Color(0.16f, 0.90f, 0.35f);
        static readonly Color DragColor = new Color(0.92f, 0.24f, 0.20f);
        static readonly Color ParasiticColor = new Color(1f, 0.51f, 0.08f);

        struct EngineState
        {
            public EngineDef def;
            public Vector3 dir;
            public float current;
        }

        // ---- state the HUD reads -------------------------------------------------
        public float Throttle => throttle;
        public float AirspeedKnots { get; private set; }
        public float AngleOfAttackDeg { get; private set; }
        public float TotalLift { get; private set; }
        public float TotalDrag { get; private set; }
        public float TotalThrust { get; private set; }
        public float VerticalSpeed { get; private set; }
        public float FlapPos => flapPos;
        public float AirbrakePos => airbrkPos;
        public float WeightNewtons => config.massKg * Physics.gravity.magnitude;
        public bool MouseMode { get; private set; }
        public float MouseRoll { get; private set; }
        public float MousePitch { get; private set; }
        public bool Braking { get; private set; }

        /// Force-beam visualisation, toggled in play with M.
        public bool BeamsVisible
        {
            get => beamsVisible;
            private set
            {
                beamsVisible = value;
                if (beams != null) beams.Visible = value;
            }
        }

        /// Mouse-flight box in screen space, bottom-left origin (matches Mouse.current.position).
        public Rect MouseBox
        {
            get
            {
                float sz = config.mouseBoxSize;
                return new Rect((Screen.width - sz) * 0.5f, 20f, sz, sz);
            }
        }

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            rb.mass = config.massKg;
            rb.centerOfMass = config.centerOfMass;
            rb.inertiaTensorRotation = Quaternion.identity;
            rb.inertiaTensor = config.inertiaTensor;
            rb.linearDamping = 0f;
            rb.angularDamping = 0f;
            rb.maxAngularVelocity = 20f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            stations = Aerodynamics.Build(config);

            engines = new EngineState[config.engines.Length];
            for (int i = 0; i < engines.Length; i++)
            {
                var def = config.engines[i];
                engines[i] = new EngineState
                {
                    def = def,
                    dir = def.dir.normalized,
                    current = def.idleThrust,
                };
            }

            pitchSpring = new ActuatorSpring(config.actuatorFreq, config.actuatorDamping);
            rollSpring = new ActuatorSpring(config.actuatorFreq, config.actuatorDamping);
            yawSpring = new ActuatorSpring(config.actuatorFreq, config.actuatorDamping);

            maxFlap = Max(config.flapAngles);
            maxAirbrk = Max(config.airbrkAngles);

            beamOrigin = new Vector3[stations.Length];
            beamLift = new Vector3[stations.Length];
            beamDrag = new Vector3[stations.Length];
            dragPointForce = new Vector3[config.dragPoints.Length];

            beams = new ForceBeams("ForceBeams", transform,
                stations.Length * 2 + config.dragPoints.Length, config.beamWidth);
            BeamsVisible = config.showBeams;

            foreach (var v in controlVisuals)
                if (v.hinge) v.rest = v.hinge.localRotation;
        }

        static float Max(float[] values)
        {
            float m = 0f;
            foreach (float v in values) if (v > m) m = v;
            return m;
        }

        static bool Held(Key k) => Keyboard.current != null && Keyboard.current[k].isPressed;
        static bool Pressed(Key k) => Keyboard.current != null && Keyboard.current[k].wasPressedThisFrame;
        static float Axis(Key pos, Key neg) => (Held(pos) ? 1f : 0f) - (Held(neg) ? 1f : 0f);

        static bool IsInvalid(Vector3 v) =>
            float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
            float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);

        static Vector3 ClampMagnitude(Vector3 v, float max)
        {
            float mag = v.magnitude;
            return (mag > max && mag > 0f) ? v * (max / mag) : v;
        }

        void Update()
        {
            // Detent selectors step once per key press.
            if (Pressed(Key.Digit1)) flapIndex = Mathf.Min(flapIndex + 1, config.flapAngles.Length - 1);
            if (Pressed(Key.Digit2)) flapIndex = Mathf.Max(flapIndex - 1, 0);
            if (Pressed(Key.Digit3)) airbrkIndex = Mathf.Min(airbrkIndex + 1, config.airbrkAngles.Length - 1);
            if (Pressed(Key.Digit4)) airbrkIndex = Mathf.Max(airbrkIndex - 1, 0);

            Braking = Held(Key.B);
            if (Pressed(Key.M)) BeamsVisible = !BeamsVisible;

            UpdateMouseBox();
            UpdateControlVisuals();
        }

        void UpdateMouseBox()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            Rect box = MouseBox;
            Vector2 m = mouse.position.ReadValue();

            if (mouse.leftButton.wasPressedThisFrame && box.Contains(m))
                MouseMode = !MouseMode;

            if (!MouseMode) return;

            Vector2 half = box.size * 0.5f;
            Vector2 center = box.center;
            MouseRoll = Mathf.Clamp((m.x - center.x) / half.x, -1f, 1f);
            MousePitch = Mathf.Clamp((m.y - center.y) / half.y, -1f, 1f);
        }

        void FixedUpdate()
        {
            float h = Mathf.Min(Time.fixedDeltaTime, config.dtMax);
            if (h <= 0f) return;

            throttle = Mathf.Clamp01(throttle + Axis(Key.E, Key.Q) * config.leverSlew * h);

            float pitchTarget = MouseMode ? MousePitch : Axis(Key.W, Key.S);
            float rollTarget = MouseMode ? MouseRoll : Axis(Key.D, Key.A);
            float yawTarget = Axis(Key.C, Key.Z);

            Vector3 linVel = rb.linearVelocity;
            Vector3 angVel = rb.angularVelocity;
            if (IsInvalid(linVel) || IsInvalid(angVel) ||
                linVel.magnitude > config.maxSpeedGuard ||
                angVel.magnitude > config.maxOmegaGuard)
                return;

            Quaternion toLocal = Quaternion.Inverse(transform.rotation);
            Vector3 comLocal = rb.centerOfMass;

            float ap = pitchSpring.Update(h, pitchTarget);
            float ar = rollSpring.Update(h, rollTarget);
            float ay = yawSpring.Update(h, yawTarget);

            float flapTarget = config.flapAngles[flapIndex];
            float airbrkTarget = config.airbrkAngles[airbrkIndex];
            flapPos += (flapTarget - flapPos) * Mathf.Clamp01(h * config.flapResponse);
            airbrkPos += (airbrkTarget - airbrkPos) * Mathf.Clamp01(h * config.airbrkResponse);
            float flapDeploy = maxFlap > 0f ? flapPos / maxFlap : 0f;
            float airbrkDeploy = maxAirbrk > 0f ? airbrkPos / maxAirbrk : 0f;

            Vector3 linLocalStart = toLocal * linVel;
            Vector3 angLocalStart = toLocal * angVel;
            Vector3 windLocal = toLocal * config.windWorld;

            Vector3 linLocal = linLocalStart;
            Vector3 angLocal = angLocalStart;
            Vector3 accForce = Vector3.zero, accTorque = Vector3.zero;
            float lift = 0f, drag = 0f;
            float sh = h / config.substeps;

            for (int step = 0; step < config.substeps; step++)
            {
                bool last = step == config.substeps - 1;
                ComputeAero(comLocal, linLocal, angLocal, windLocal, ap, ar, ay,
                    flapDeploy, airbrkDeploy, last && beamsVisible,
                    out Vector3 f, out Vector3 t, out float tl, out float td);

                accForce += f;
                accTorque += t;
                lift += tl;
                drag += td;

                // Step the predicted state forward so later substeps see fresh airflow.
                if (!last)
                {
                    linLocal += f / config.massKg * sh;
                    angLocal += AngularAcceleration(t) * sh;
                }
            }

            float inv = 1f / config.substeps;
            Vector3 forceLocal = accForce * inv;
            Vector3 torqueLocal = accTorque * inv;
            TotalLift = lift * inv;
            TotalDrag = drag * inv;

            // Engines spool toward the commanded thrust; each folds in its own off-CoM
            // moment, so asymmetric thrust and engine-out yaw emerge for free.
            float thrust = 0f;
            for (int i = 0; i < engines.Length; i++)
            {
                ref EngineState e = ref engines[i];
                float target = e.def.idleThrust + (e.def.maxThrust - e.def.idleThrust) * throttle;
                e.current += (target - e.current) * Mathf.Clamp01(h * e.def.response);
                Vector3 tv = e.dir * e.current;
                forceLocal += tv;
                torqueLocal += Vector3.Cross(e.def.pos - comLocal, tv);
                thrust += e.current;
            }
            TotalThrust = thrust;

            // Speed-adaptive angular damping: fades out at speed so native aero stability takes over.
            float speedFactor = Mathf.Clamp(1f - linVel.magnitude / (config.maxSpeedGuard * 0.5f), 0.1f, 1f);
            torqueLocal += new Vector3(
                -config.pitchDamp * angLocalStart.x * speedFactor,
                -config.yawDamp * angLocalStart.y * speedFactor,
                -config.rollDamp * angLocalStart.z * speedFactor);

            forceLocal = ClampMagnitude(forceLocal, config.maxForce);
            torqueLocal = ClampMagnitude(torqueLocal, config.maxTorque);
            if (IsInvalid(forceLocal)) forceLocal = Vector3.zero;
            if (IsInvalid(torqueLocal)) torqueLocal = Vector3.zero;

            rb.AddForce(transform.rotation * forceLocal);
            rb.AddTorque(transform.rotation * torqueLocal);

            float speed = linVel.magnitude;
            AirspeedKnots = speed * MetresPerSecondToKnots;
            AngleOfAttackDeg = speed > 0.1f
                ? Mathf.Atan2(-linLocalStart.y, linLocalStart.z) * Mathf.Rad2Deg
                : 0f;
            VerticalSpeed = linVel.y;
        }

        /// I^-1 * torque, both in the aircraft's local frame.
        Vector3 AngularAcceleration(Vector3 torqueLocal)
        {
            Quaternion q = rb.inertiaTensorRotation;
            Vector3 t = Quaternion.Inverse(q) * torqueLocal;
            Vector3 I = rb.inertiaTensor;
            return q * new Vector3(t.x / I.x, t.y / I.y, t.z / I.z);
        }

        void ComputeAero(Vector3 comLocal, Vector3 linLocal, Vector3 angLocal, Vector3 windLocal,
            float ap, float ar, float ay, float flapDeploy, float airbrkDeploy, bool wantDisplay,
            out Vector3 netF, out Vector3 netT, out float totalLift, out float totalDrag)
        {
            netF = Vector3.zero;
            netT = Vector3.zero;
            totalLift = 0f;
            totalDrag = 0f;

            float flapClM = 1f + (config.flapClFactor - 1f) * flapDeploy;
            float flapCdM = 1f + (config.flapCdFactor - 1f) * flapDeploy;
            float flapAreaM = 1f + (config.flapWingAreaFactor - 1f) * flapDeploy;
            float abClM = 1f + (config.airbrkClFactor - 1f) * airbrkDeploy;
            float abCdM = 1f + (config.airbrkCdFactor - 1f) * airbrkDeploy;

            for (int i = 0; i < stations.Length; i++)
            {
                Station g = stations[i];
                Vector3 pointVel = linLocal + Vector3.Cross(angLocal, g.pos - comLocal);
                Vector3 rel = windLocal - pointVel;

                float defl = 0f;
                switch (g.role)
                {
                    case ControlRole.Elevator:
                        defl = config.elevatorSign * ap * g.side * g.surf.controlDeg * Mathf.Deg2Rad;
                        break;
                    case ControlRole.Aileron:
                        defl = config.aileronSign * ar * g.surf.controlDeg * Mathf.Deg2Rad;
                        break;
                    case ControlRole.Rudder:
                        defl = config.rudderSign * ay * g.surf.controlDeg * Mathf.Deg2Rad;
                        break;
                }

                Vector3 f = g.f, n = g.n;
                if (defl != 0f)
                {
                    f = Aerodynamics.Rot(f, g.s, defl);
                    n = Aerodynamics.Rot(n, g.s, defl);
                }

                Vector3 wperp = rel - g.s * Vector3.Dot(rel, g.s);
                float V = wperp.magnitude;
                if (V <= 0.1f)
                {
                    if (wantDisplay)
                    {
                        beamOrigin[i] = g.pos;
                        beamLift[i] = Vector3.zero;
                        beamDrag[i] = Vector3.zero;
                    }
                    continue;
                }

                Vector3 dragDir = wperp / V;
                float alpha = Mathf.Atan2(Vector3.Dot(wperp, n), -Vector3.Dot(wperp, f));
                Aerodynamics.Coefficients(alpha, g.surf, out float cl, out float cd);

                // Flaps add lift/drag/area, airbrakes spoil lift and add drag, each only on the
                // stations whose spanwise frac sits inside its configured band.
                float areaM = 1f;
                float cpShift = 0f;
                Surface sc = g.surf;
                if (flapDeploy > 0f && sc.HasFlap && g.frac >= sc.flapStart && g.frac <= sc.flapEnd)
                {
                    cl *= flapClM;
                    cd *= flapCdM;
                    areaM = flapAreaM;
                    cpShift += config.flapCpShift * flapDeploy;
                }
                if (airbrkDeploy > 0f && sc.HasAirbrake && g.frac >= sc.airbrkStart && g.frac <= sc.airbrkEnd)
                {
                    cl *= abClM;
                    cd *= abCdM;
                    cpShift -= config.airbrkCpShift * airbrkDeploy;
                }

                float q = 0.5f * config.airDensity * V * V;
                float S = g.chord * g.width * areaM;
                float liftN = q * S * cl;
                float dragN = q * S * cd;

                Vector3 liftDir = Vector3.Cross(g.s, dragDir);
                if (Vector3.Dot(liftDir, n) < 0f) liftDir = -liftDir;
                liftDir = liftDir.normalized;

                Vector3 liftVec = liftDir * liftN;
                Vector3 dragVec = dragDir * dragN;
                Vector3 force = liftVec + dragVec;

                // cpShift is positive aft, and aft is -Z here.
                Vector3 forcePos = cpShift != 0f ? g.pos + new Vector3(0f, 0f, -cpShift) : g.pos;

                netF += force;
                netT += Vector3.Cross(forcePos - comLocal, force);
                totalLift += liftN;
                totalDrag += dragN;

                if (wantDisplay)
                {
                    beamOrigin[i] = forcePos;
                    beamLift[i] = liftVec;
                    beamDrag[i] = dragVec;
                }
            }

            // Parasitic drag points, summed exactly like a station's drag but applied at the
            // point, so off-CoM drag makes the correct pitch and yaw moments.
            for (int i = 0; i < config.dragPoints.Length; i++)
            {
                DragPoint dp = config.dragPoints[i];
                Vector3 pointVel = linLocal + Vector3.Cross(angLocal, dp.pos - comLocal);
                Vector3 rel = windLocal - pointVel;
                float V = rel.magnitude;
                if (V <= 0.1f)
                {
                    if (wantDisplay) dragPointForce[i] = Vector3.zero;
                    continue;
                }

                float q = 0.5f * config.airDensity * V * V;
                Vector3 force = rel / V * (q * dp.cd);
                netF += force;
                netT += Vector3.Cross(dp.pos - comLocal, force);
                totalDrag += q * dp.cd;

                if (wantDisplay) dragPointForce[i] = force;
            }
        }

        void UpdateControlVisuals()
        {
            foreach (var v in controlVisuals)
            {
                if (!v.hinge) continue;

                float deg;
                if (v.isFlap) deg = flapPos;
                else if (v.isAirbrake) deg = airbrkPos;
                else
                {
                    switch (v.role)
                    {
                        case ControlRole.Aileron:
                            float side = v.hinge.localPosition.x >= 0f ? 1f : -1f;
                            deg = config.aileronSign * rollSpring.Position * v.maxDeg * side;
                            break;
                        case ControlRole.Elevator:
                            deg = config.elevatorSign * pitchSpring.Position * v.maxDeg;
                            break;
                        case ControlRole.Rudder:
                            deg = config.rudderSign * yawSpring.Position * v.maxDeg;
                            break;
                        default:
                            deg = 0f;
                            break;
                    }
                }

                v.hinge.localRotation = v.rest * Quaternion.AngleAxis(deg, v.axis);
            }
        }

        // Beams are placed here rather than in FixedUpdate so they track the interpolated
        // transform the aircraft is actually drawn at, instead of lagging a physics step.
        void LateUpdate()
        {
            if (!beamsVisible) return;

            float scale = config.beamScale;
            Quaternion rot = transform.rotation;
            int b = 0;

            for (int i = 0; i < stations.Length; i++)
            {
                Vector3 o = transform.TransformPoint(beamOrigin[i]);
                beams.Set(b++, o, o + rot * beamLift[i] * scale, LiftColor);
                beams.Set(b++, o, o + rot * beamDrag[i] * scale, DragColor);
            }
            for (int i = 0; i < config.dragPoints.Length; i++)
            {
                Vector3 o = transform.TransformPoint(config.dragPoints[i].pos);
                beams.Set(b++, o, o + rot * dragPointForce[i] * scale, ParasiticColor);
            }
        }
    }
}

