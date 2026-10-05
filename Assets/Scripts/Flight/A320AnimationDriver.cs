using UnityEngine;
using UnityEngine.InputSystem;

namespace CirrusFS.Flight
{
    /// Drives the A320ceo model's Animator (A320CEO.controller, built by
    /// CirrusFS > Build A320 Animator Controller) from the flight model and the gear.
    ///
    /// Every controller layer holds one imported clip played by motion time, so each Animator
    /// parameter is a clip position, frame / 60 (clips are 60 frames at 30 fps). Frame 0 of the
    /// centred and compression clips is the modelling pose and is never used. Spinners (fans,
    /// wheels) are looped here by advancing a phase; one clip cycle is half a revolution.
    ///
    ///   Gear G    Reverse R (on ground)
    ///
    /// Gear, reverse and stab trim are visual state only: the flight model has no retraction,
    /// reverse thrust or trim yet, so they change nothing in the physics.
    [DefaultExecutionOrder(100)]
    public class A320AnimationDriver : MonoBehaviour
    {
        /// Animator parameter, layer and clip name for every channel, all identical.
        public static readonly string[] Channels =
        {
            "AILERONS", "ELEVATORS", "RUDDER", "STAB_TRIM",
            "FLAPS", "ROLL_SPLR_L", "ROLL_SPLR_R", "GND_BRK",
            "ENG_1", "ENG_2", "REVERSE",
            "LDG", "MW_COMPRESS_L", "MW_COMPRESS_R", "NW_COMPRESS",
            "MW_SPIN_L", "MW_SPIN_R", "NW_SPIN", "NW_STEER",
            "WING_FLEX_L", "WING_FLEX_R",
        };

        const float ClipFrames = 60f;
        const float KnotsPerMs = 1.94384f;

        public Animator animator;
        public AircraftController controller;
        public LandingGearSuspension suspension;

        [Header("Flaps")]
        [Tooltip("Clip fraction (0..1) for each flap detent in config.flapAngles: CONF 0, 1+F, 2, 3, " +
                 "FULL. Real flap angles 0/10/15/20/40 deg over a 40 deg full-travel clip.")]
        public float[] flapVisual = { 0f, 0.25f, 0.375f, 0.5f, 1f };

        [Header("Spoilers (deg)")]
        [Tooltip("Spoiler angle the clip's frame 60 represents (ground spoiler full travel).")]
        public float spoilerClipDeg = 50f;
        public float rollSpoilerMaxDeg = 35f;
        public float speedbrakeMaxDeg = 40f;
        [Tooltip("Roll command below which the roll spoilers stay retracted.")]
        public float rollSpoilerDeadband = 0.05f;
        [Tooltip("Spoiler surface rate, deg/s.")]
        public float spoilerRate = 60f;
        [Tooltip("Throttle at or below which thrust counts as idle for ground spoilers.")]
        public float idleThrottle = 0.05f;

        [Header("Engines")]
        [Tooltip("Fan RPM at 100 % N1 (CFM56-5B: 5000).")]
        public float n1RpmAt100 = 5000f;
        [Tooltip("N1 at idle thrust, 0..1.")]
        public float idleN1 = 0.2f;
        [Tooltip("Multiplier on the drawn fan speed, to tame strobing at high RPM.")]
        public float fanVisualScale = 1f;
        public bool invertFanSpin;
        [Tooltip("Reverser door transit time, s.")]
        public float reverserTime = 1.5f;

        [Header("Landing gear")]
        public float gearRetractTime = 10f;
        public float gearExtendTime = 12f;
        [Tooltip("Strut stroke that maps onto the clip's fully-compressed frame 1, m.")]
        public float mainStroke = 0.5f;
        public float noseStroke = 0.45f;
        [Tooltip("Pin the nose strut's static (rideCompression) sink to this frame. 0 = plain linear.")]
        public float noseNeutralFrame = 38f;
        [Tooltip("Same for the mains. 0 = plain linear.")]
        public float mainNeutralFrame = 0f;
        public bool invertWheelSpin;
        [Tooltip("Free-wheel spin-down time constant once airborne, s.")]
        public float wheelSpinDown = 4f;
        [Tooltip("Mains are braked on retraction, as on the real aircraft; spin-down time, s.")]
        public float retractBrakeTime = 0.5f;

        [Header("Nose wheel steering (deg)")]
        [Tooltip("Steering angle the clip's frames 1 / 60 represent (tiller limit).")]
        public float steerClipDeg = 75f;
        [Tooltip("Rudder pedal steering authority.")]
        public float pedalSteerDeg = 6f;
        [Tooltip("Below this groundspeed the rudder keys act as the tiller (full steerClipDeg), kt.")]
        public float tillerBelowKts = 10f;
        [Tooltip("Pedal authority fades out between 40 kt and this speed, kt.")]
        public float pedalFadeOutKts = 130f;
        public float steerRate = 30f;

        [Header("Wing flex")]
        [Tooltip("Wing lift / weight that reaches frame 60. Frame 1 is zero lift (parked).")]
        public float flexMaxG = 2.5f;
        [Tooltip("Wing bending natural frequency, Hz.")]
        public float flexFrequency = 1.2f;
        [Tooltip("Wing bending damping ratio; < 1 lets the wings bounce on touchdown.")]
        public float flexDamping = 0.25f;

        [Header("Inputs (visual only)")]
        [Range(-1f, 1f)]
        [Tooltip("Stabiliser trim, +1 = full nose up, -1 = full nose down.")]
        public float stabTrim;

        // ---- state other systems can read --------------------------------------
        public bool GearLeverDown { get; private set; } = true;
        /// 0 = fully extended, 1 = fully retracted.
        public float GearPosition { get; private set; }
        public bool GearDownLocked => GearPosition <= 0f;
        public bool ReverseSelected { get; private set; }
        public float ReverserPosition { get; private set; }
        public bool GroundSpoilersActive { get; private set; }

        Rigidbody rb;
        int[] ids;
        float[] fanPhase = new float[2];
        float mwSpinL, mwSpinR, nwSpin;
        float omegaL, omegaR, omegaN;
        float splrL, splrR, gndSplr, steer;
        float flex, flexVel;
        bool flexInit;

        enum Ch
        {
            Ailerons, Elevators, Rudder, StabTrim,
            Flaps, RollSplrL, RollSplrR, GndBrk,
            Eng1, Eng2, Reverse,
            Ldg, MwCompressL, MwCompressR, NwCompress,
            MwSpinL, MwSpinR, NwSpin, NwSteer,
            WingFlexL, WingFlexR,
        }

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (!controller) controller = GetComponent<AircraftController>();
            if (!suspension) suspension = GetComponent<LandingGearSuspension>();
            if (!animator) animator = GetComponentInChildren<Animator>();

            ids = new int[Channels.Length];
            for (int i = 0; i < Channels.Length; i++) ids[i] = Animator.StringToHash(Channels[i]);
        }

        static bool Pressed(Key k) => Keyboard.current != null && Keyboard.current[k].wasPressedThisFrame;

        void Set(Ch ch, float frame) =>
            animator.SetFloat(ids[(int)ch], Mathf.Clamp(frame, 0f, ClipFrames) / ClipFrames);

        /// -1..1 onto frames 1..60 with 30 as neutral.
        static float Centered(float v) =>
            v < 0f ? 30f + 29f * Mathf.Max(v, -1f) : 30f + 30f * Mathf.Min(v, 1f);

        /// Strut compression onto frames 60 (unloaded) .. 1 (fully compressed). With a neutral
        /// frame, the static sink lands exactly on it and each side of it is linear.
        static float CompressionFrame(float c, float stroke, float staticSink, float neutralFrame)
        {
            if (neutralFrame > 1f && neutralFrame < ClipFrames && staticSink > 0f && staticSink < stroke)
                return c <= staticSink
                    ? Mathf.Lerp(ClipFrames, neutralFrame, c / staticSink)
                    : Mathf.Lerp(neutralFrame, 1f, (c - staticSink) / (stroke - staticSink));
            return Mathf.Lerp(ClipFrames, 1f, stroke > 0f ? c / stroke : 0f);
        }

        void Update()
        {
            if (!animator || !controller || animator.runtimeAnimatorController == null) return;
            float dt = Time.deltaTime;

            var gears = suspension ? suspension.gears : null;
            var gL = FindGear(gears, "LG");
            var gR = FindGear(gears, "RG");
            var gN = FindGear(gears, "NW");
            bool onL = gL != null && gL.grounded, onR = gR != null && gR.grounded, onN = gN != null && gN.grounded;
            bool anyMain = onL || onR;
            bool weightOnWheels = anyMain || onN;
            Vector3 vel = rb ? rb.linearVelocity : Vector3.zero;
            float groundKts = Vector3.Dot(vel, transform.forward) * KnotsPerMs;

            UpdateGearLever(dt, weightOnWheels);
            UpdateReverser(dt, anyMain);

            // ---- primary flight controls ----
            Set(Ch.Ailerons, Centered(controller.RollActuator));
            Set(Ch.Elevators, Centered(-controller.PitchActuator)); // frame 1 = pitch up
            Set(Ch.Rudder, Centered(controller.YawActuator));
            Set(Ch.StabTrim, Centered(-stabTrim));                   // frame 1 = nose up

            // ---- high lift ----
            Set(Ch.Flaps, FlapFraction() * ClipFrames);

            // ---- spoilers ----
            // Ground spoilers need both mains down, or one main down with wheel speed > 72 kt.
            UpdateSpoilers(dt, (onL && onR) || (anyMain && Mathf.Abs(groundKts) > 72f));
            Set(Ch.RollSplrL, splrL / spoilerClipDeg * ClipFrames);
            Set(Ch.RollSplrR, splrR / spoilerClipDeg * ClipFrames);
            Set(Ch.GndBrk, gndSplr / spoilerClipDeg * ClipFrames);

            // ---- engines ----
            for (int i = 0; i < 2; i++)
            {
                float rpm = EngineN1(i) * n1RpmAt100 * fanVisualScale;
                // One clip cycle is half a revolution.
                fanPhase[i] = Mathf.Repeat(fanPhase[i] + rpm / 60f * 2f * dt * (invertFanSpin ? -1f : 1f), 1f);
            }
            Set(Ch.Eng1, fanPhase[0] * ClipFrames);
            Set(Ch.Eng2, fanPhase[1] * ClipFrames);
            Set(Ch.Reverse, ReverserPosition * ClipFrames);

            // ---- landing gear ----
            Set(Ch.Ldg, GearPosition * ClipFrames);
            float sink = suspension ? suspension.rideCompression : 0f;
            Set(Ch.MwCompressL, CompressionFrame(gL != null ? gL.compression : 0f, mainStroke, sink, mainNeutralFrame));
            Set(Ch.MwCompressR, CompressionFrame(gR != null ? gR.compression : 0f, mainStroke, sink, mainNeutralFrame));
            Set(Ch.NwCompress, CompressionFrame(gN != null ? gN.compression : 0f, noseStroke, sink, noseNeutralFrame));

            bool braked = !GearLeverDown;
            mwSpinL = SpinPhase(mwSpinL, ref omegaL, gL, dt, braked);
            mwSpinR = SpinPhase(mwSpinR, ref omegaR, gR, dt, braked);
            nwSpin = SpinPhase(nwSpin, ref omegaN, gN, dt, braked);
            Set(Ch.MwSpinL, mwSpinL * ClipFrames);
            Set(Ch.MwSpinR, mwSpinR * ClipFrames);
            Set(Ch.NwSpin, nwSpin * ClipFrames);

            UpdateSteering(dt, onN, Mathf.Abs(groundKts));
            Set(Ch.NwSteer, Centered(steer / steerClipDeg));

            // ---- wing flex ----
            UpdateFlex(dt);
            Set(Ch.WingFlexL, Mathf.Lerp(1f, ClipFrames, flex));
            Set(Ch.WingFlexR, Mathf.Lerp(1f, ClipFrames, flex));
        }

        static LandingGearSuspension.Gear FindGear(LandingGearSuspension.Gear[] gears, string name)
        {
            if (gears == null) return null;
            foreach (var g in gears) if (g.name == name) return g;
            return null;
        }

        // Retraction is inhibited with weight on any wheel.
        void UpdateGearLever(float dt, bool weightOnWheels)
        {
            if (Pressed(Key.G))
            {
                if (!GearLeverDown) GearLeverDown = true;
                else if (!weightOnWheels) GearLeverDown = false;
            }
            float target = GearLeverDown ? 0f : 1f;
            float time = GearLeverDown ? gearExtendTime : gearRetractTime;
            GearPosition = Mathf.MoveTowards(GearPosition, target, time > 0f ? dt / time : 1f);
        }

        // Reversers only unlock on the ground; airborne they stow whatever is selected.
        void UpdateReverser(float dt, bool onGround)
        {
            if (Pressed(Key.R)) ReverseSelected = !ReverseSelected;
            float target = ReverseSelected && onGround ? 1f : 0f;
            ReverserPosition = Mathf.MoveTowards(ReverserPosition, target,
                reverserTime > 0f ? dt / reverserTime : 1f);
        }

        /// Physics flap position mapped through flapVisual, piecewise between detents.
        float FlapFraction()
        {
            float[] angles = controller.config.flapAngles;
            float pos = controller.FlapPos;
            if (flapVisual == null || angles == null || flapVisual.Length != angles.Length || angles.Length < 2)
            {
                float max = angles != null && angles.Length > 0 ? angles[angles.Length - 1] : 0f;
                return max > 0f ? Mathf.Clamp01(pos / max) : 0f;
            }
            for (int i = 1; i < angles.Length; i++)
                if (pos <= angles[i] || i == angles.Length - 1)
                    return Mathf.Clamp01(Mathf.Lerp(flapVisual[i - 1], flapVisual[i],
                        Mathf.InverseLerp(angles[i - 1], angles[i], pos)));
            return 0f;
        }

        /// A320 spoiler logic. Spoiler 1 (GND_BRK) is ground-only. Spoilers 2-5 (ROLL_SPLR) take
        /// the speedbrake symmetrically plus the roll order on the down-going wing; when the sum
        /// exceeds the travel limit, the opposite wing retracts so the roll differential is kept.
        /// Ground spoilers extend every panel fully, overriding roll and speedbrake. Without an
        /// arming switch, they trigger on the ground with thrust at idle and either the
        /// speedbrake lever in its last (ground) detent or reverse selected.
        void UpdateSpoilers(float dt, bool groundSpoilerWow)
        {
            float[] ab = controller.config.airbrkAngles;
            bool groundDetent = ab != null && ab.Length > 1 && controller.AirbrakeIndex == ab.Length - 1;
            GroundSpoilersActive = groundSpoilerWow && controller.Throttle <= idleThrottle &&
                                   (groundDetent || ReverseSelected);

            float tgtL, tgtR, tgtGnd;
            if (GroundSpoilersActive)
            {
                tgtL = tgtR = tgtGnd = spoilerClipDeg;
            }
            else
            {
                float sb = Mathf.Min(controller.AirbrakePos, speedbrakeMaxDeg);
                float r = controller.RollActuator;
                float roll = Mathf.Max(0f, Mathf.Abs(r) - rollSpoilerDeadband) / (1f - rollSpoilerDeadband)
                             * rollSpoilerMaxDeg;
                // Rolling right lowers the right wing, so its spoilers rise.
                float down = sb + roll, up = sb;
                float limit = Mathf.Max(speedbrakeMaxDeg, rollSpoilerMaxDeg);
                if (down > limit)
                {
                    up = Mathf.Max(0f, up - (down - limit));
                    down = limit;
                }
                tgtR = r >= 0f ? down : up;
                tgtL = r >= 0f ? up : down;
                tgtGnd = 0f;
            }

            float step = spoilerRate * dt;
            splrL = Mathf.MoveTowards(splrL, tgtL, step);
            splrR = Mathf.MoveTowards(splrR, tgtR, step);
            gndSplr = Mathf.MoveTowards(gndSplr, tgtGnd, step);
        }

        /// N1 as a fraction of 100 %, from the engine's spooled thrust. Thrust grows roughly
        /// with N1 squared, hence the square root. Replace this once engines model RPM.
        public float EngineN1(int i)
        {
            var defs = controller.config.engines;
            if (defs == null || i >= defs.Length) return 0f;
            float t = Mathf.InverseLerp(defs[i].idleThrust, defs[i].maxThrust, controller.EngineThrust(i));
            return Mathf.Lerp(idleN1, 1f, Mathf.Sqrt(t));
        }

        /// Rolling wheels track the groundspeed at the hub; airborne ones spin down, quickly
        /// once the gear is retracting (mains are braked in the well).
        float SpinPhase(float phase, ref float omega, LandingGearSuspension.Gear g, float dt, bool braked)
        {
            if (g == null) return phase;
            if (g.grounded && g.radius > 0f && rb)
            {
                Vector3 hub = transform.TransformPoint(controller.RefOffset + g.mountLocal);
                omega = Vector3.Dot(rb.GetPointVelocity(hub), transform.forward) / g.radius;
            }
            else
            {
                float tau = braked ? retractBrakeTime : wheelSpinDown;
                omega *= tau > 0f ? Mathf.Exp(-dt / tau) : 0f;
            }
            // omega rad/s -> half-revolution clip cycles per second is omega / pi.
            float cycles = omega / Mathf.PI * dt * (invertWheelSpin ? -1f : 1f);
            return Mathf.Repeat(phase + cycles, 1f);
        }

        /// Tiller-like full authority while taxiing slowly, pedal authority (+-6 deg) above,
        /// fading to nothing by pedalFadeOutKts. Airborne, the nose wheel centres.
        void UpdateSteering(float dt, bool noseOnGround, float kts)
        {
            float authority;
            if (kts <= tillerBelowKts) authority = steerClipDeg;
            else if (kts <= 40f)
                authority = Mathf.Lerp(steerClipDeg, pedalSteerDeg, Mathf.InverseLerp(tillerBelowKts, 40f, kts));
            else authority = pedalSteerDeg * (1f - Mathf.InverseLerp(40f, pedalFadeOutKts, kts));

            float target = noseOnGround && GearLeverDown ? controller.YawActuator * authority : 0f;
            steer = Mathf.MoveTowards(steer, target, steerRate * dt);
        }

        /// Wing lift / weight through a lightly damped spring, so the wings settle and bounce.
        void UpdateFlex(float dt)
        {
            float w = controller.WeightNewtons;
            float g = w > 0f ? controller.TotalLift / w : 0f;
            float target = flexMaxG > 0f ? Mathf.Clamp01(g / flexMaxG) : 0f;
            if (!flexInit)
            {
                flex = target;
                flexVel = 0f;
                flexInit = true;
                return;
            }
            float omega = 2f * Mathf.PI * flexFrequency;
            float h = Mathf.Min(dt, 0.05f);
            flexVel += (omega * omega * (target - flex) - 2f * flexDamping * omega * flexVel) * h;
            flex = Mathf.Clamp01(flex + flexVel * h);
        }
    }
}
