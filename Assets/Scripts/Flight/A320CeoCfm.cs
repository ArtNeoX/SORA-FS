using UnityEngine;

namespace CirrusFS.Flight
{
    /// Airbus A320ceo with CFM56-5B4 engines, in the same SI / +Z-forward aircraft frame as
    /// FlightConfig. Geometry is read off Assets/Aircraft/A320CEO/objects/a320ceo.fbx, which
    /// imports at 1/1.12 of real size (the Roblox stud factor), so every mesh coordinate here
    /// is multiplied by 1.12. Every position is measured from the controller's ref point, which
    /// stands for the FBX origin: put the ref point where the model's origin sits (model at
    /// scale 1.12) and the rig lines up with it.
    ///
    /// Reference data: span 34.10 m, wing area 122.6 m^2, 25 deg quarter-chord sweep,
    /// 5.1 deg dihedral, HT 31.0 m^2, VT 21.5 m^2, 2 x CFM56-5B4 at 120.1 kN (27,000 lbf),
    /// MTOW 78 t / MLW 66 t / OEW ~42.6 t.
    public static class A320CeoCfm
    {
        public static FlightConfig Config() => new FlightConfig
        {
            airDensity = 1.225f,

            // Mid-weight line aircraft. CoM at ~28 % MAC (LEMAC z = 3.12, MAC 4.05 m),
            // which puts ~92 % of the weight on the mains, as on the real aircraft.
            massKg = 64000f,
            centerOfMass = new Vector3(0f, 0.1f, 1.99f),
            // Roskam transport-twin radii of gyration: Rx 0.25 b, Ry 0.38 L, Rz 0.46 (b+L)/2.
            inertiaTensor = new Vector3(3.26e6f, 4.34e6f, 1.16e6f),

            leverSlew = 0.25f,

            // The flap model scales lift linearly with deploy, but most of the A320's low-config
            // gain comes from the slats. These detents are therefore effective high-lift
            // positions, not flap-surface degrees: CONF 0, 1+F, 2, 3, FULL. They are set so
            // each one reproduces that config's CLmax (1+F ~1.5x clean, FULL ~2x clean).
            flapAngles = new[] { 0f, 20f, 26f, 30f, 40f },
            flapResponse = 0.25f,
            flapClFactor = 2.3f,
            flapCdFactor = 5.5f,
            flapWingAreaFactor = 1.12f,
            flapCpShift = 0.4f,

            // Spoilers 1-5. 20 / 40 = in-flight speedbrake half / full, 50 = ground spoilers.
            airbrkAngles = new[] { 0f, 20f, 40f, 50f },
            airbrkResponse = 1.5f,
            airbrkClFactor = 0.5f,
            airbrkCdFactor = 3.8f,
            airbrkCpShift = 0.6f,

            substeps = 8,
            dtMax = 0.033f,
            maxSpeedGuard = 5600f,
            maxOmegaGuard = 1000f,
            maxForce = 5e6f,
            maxTorque = 6e7f,

            pitchDamp = 2.0e6f,
            yawDamp = 2.0e6f,
            rollDamp = 1.5e6f,

            actuatorFreq = 8f,
            actuatorDamping = 1f,

            beamScale = 0.00004f,
            beamWidth = 0.15f,

            surfaces = new[]
            {
                // Equivalent trapezoid of the cranked wing: same span, area and MAC.
                // Root offset is the centreline quarter-chord.
                new Surface
                {
                    name = "MainWing",
                    rootOffset = new Vector3(0f, -0.3f, 5.26f),
                    span = 17.05f, rootChord = 5.8f, tipChord = 1.39f,
                    sweep = 25f, dihedral = 5.1f, incidence = 3f, twist = -4f,
                    stations = 16, verticalSpan = false, mirror = true,
                    // Ailerons from 80 % semi-span; controlDeg folds in the roll spoilers.
                    control = ControlRole.Aileron, controlDeg = 16f, ailStart = 0.8f,
                    flapStart = 0.13f, flapEnd = 0.78f,
                    airbrkStart = 0.26f, airbrkEnd = 0.76f,
                    clPerDeg = 0.082f, zeroLiftAlpha = -2f,
                    stallEndDeg = 15f, dropEndDeg = 30f,
                    cd0 = 0.0065f, kInduced = 0.040f,
                    clDrop = 0.75f, cdDrop = 0.85f, clFlat = 0.08f, cdFlat = 1.25f,
                },
                // Trimmable horizontal stabiliser. The blade-element model has no downwash,
                // so the lift slope carries the (1 - de/da) ~ 0.55 factor and the elevator
                // throw is scaled back up to match the real surface's authority.
                new Surface
                {
                    name = "HorizontalStab",
                    rootOffset = new Vector3(0f, 1.57f, -13.9f),
                    span = 6.22f, rootChord = 3.75f, tipChord = 1.2f,
                    sweep = 29f, dihedral = 6f, incidence = -1.5f, twist = 0f,
                    stations = 4, verticalSpan = false, mirror = true,
                    control = ControlRole.Elevator, controlDeg = 16f,
                    clPerDeg = 0.040f, zeroLiftAlpha = 0f,
                    stallEndDeg = 22f, dropEndDeg = 34f,
                    cd0 = 0.008f, kInduced = 0.060f,
                    clDrop = 0.60f, cdDrop = 0.60f, clFlat = 0.10f, cdFlat = 1.2f,
                },
                new Surface
                {
                    name = "VerticalStab",
                    rootOffset = new Vector3(0f, 2.3f, -13.3f),
                    span = 6.2f, rootChord = 5.5f, tipChord = 1.85f,
                    sweep = 31f, dihedral = 0f, incidence = 0f, twist = 0f,
                    stations = 3, verticalSpan = true, mirror = false,
                    control = ControlRole.Rudder, controlDeg = 15f,
                    clPerDeg = 0.050f, zeroLiftAlpha = 0f,
                    stallEndDeg = 18f, dropEndDeg = 32f,
                    cd0 = 0.008f, kInduced = 0.090f,
                    clDrop = 0.50f, cdDrop = 0.60f, clFlat = 0.10f, cdFlat = 1.2f,
                },
                // Fuselage + nacelle destabilising moment (Munk). Blade elements alone leave the
                // long A320 fuselage out and give a ~45 % MAC static margin; this lift-only
                // patch over the forward fuselage brings it to a realistic ~20 %. No drag: the
                // fuselage drag point already carries it.
                new Surface
                {
                    name = "FuselageMoment",
                    rootOffset = new Vector3(0f, 0.4f, 12f),
                    span = 1.98f, rootChord = 5f, tipChord = 5f,
                    sweep = 0f, dihedral = 0f, incidence = 0f, twist = 0f,
                    stations = 1, verticalSpan = false, mirror = true,
                    clPerDeg = 0.05f, zeroLiftAlpha = 0f,
                    stallEndDeg = 30f, dropEndDeg = 60f,
                    cd0 = 0f, kInduced = 0f,
                    clDrop = 1.5f, cdDrop = 0f, clFlat = 0.5f, cdFlat = 0f,
                },
            },

            // Cd*A in m^2 against S = 122.6: fuselage + misc ~0.0086, nacelles ~0.0026,
            // gear down ~0.017.
            dragPoints = new[]
            {
                new DragPoint { pos = new Vector3(0f, 0.4f, -0.5f), cd = 1.05f },
                new DragPoint { pos = new Vector3(-5.73f, -1.34f, 5.96f), cd = 0.16f },
                new DragPoint { pos = new Vector3(5.73f, -1.34f, 5.96f), cd = 0.16f },
                new DragPoint { pos = new Vector3(-3.77f, -2.0f, 0.96f), cd = 0.8f, isGear = true },
                new DragPoint { pos = new Vector3(3.77f, -2.0f, 0.96f), cd = 0.8f, isGear = true },
                new DragPoint { pos = new Vector3(0f, -2.2f, 13.25f), cd = 0.48f, isGear = true },
            },

            // CFM56-5B4: 120.1 kN TOGA, ~4.5 kN ground idle, idle -> TOGA in ~6 s.
            engines = new[]
            {
                new EngineDef
                {
                    name = "E1", pos = new Vector3(-5.73f, -1.34f, 5.96f), dir = Vector3.forward,
                    idleThrust = 4500f, maxThrust = 120100f, response = 0.5f,
                },
                new EngineDef
                {
                    name = "E2", pos = new Vector3(5.73f, -1.34f, 5.96f), dir = Vector3.forward,
                    idleThrust = 4500f, maxThrust = 120100f, response = 0.5f,
                },
            },
        };

        /// Gear legs at the mesh's static (loaded) wheel positions.
        public static void ApplyGear(LandingGearSuspension gear)
        {
            gear.rideCompression = 0.25f;
            gear.mountHeight = 2.0f;
            gear.dampingRatio = 1f;
            gear.sideGrip = 0.8f;
            gear.rollResist = 0.012f;
            gear.brakeGrip = 0.5f;
            gear.beamScale = 0.000004f;
            gear.beamWidth = 0.15f;

            var old = gear.gears;
            gear.gears = new[]
            {
                new LandingGearSuspension.Gear { name = "LG", mountLocal = new Vector3(-3.77f, -2.65f, 0.96f), radius = 0.54f },
                new LandingGearSuspension.Gear { name = "NW", mountLocal = new Vector3(0f, -2.80f, 13.25f), radius = 0.35f },
                new LandingGearSuspension.Gear { name = "RG", mountLocal = new Vector3(3.77f, -2.65f, 0.96f), radius = 0.54f },
            };
            // Keep whatever wheel visuals were wired up.
            for (int i = 0; i < gear.gears.Length && old != null && i < old.Length; i++)
                gear.gears[i].wheel = old[i].wheel;
        }

        /// Distance from the ref point to the bottom of the tyres, for aircraft.json (equal to
        /// gear_height_m while the ref point sits at the aircraft origin).
        public const float GearHeight = 3.19f;
    }
}
