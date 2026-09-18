using System;
using UnityEngine;

namespace CirrusFS.Flight
{
    public enum ControlRole { None, Elevator, Aileron, Rudder }

    /// Aircraft frame: +Z forward, +Y up, +X right. Lengths in metres, angles in degrees.
    [Serializable]
    public class Surface
    {
        public string name = "Surface";

        [Header("Geometry")]
        public Vector3 rootOffset;
        public float span = 1f;
        public float rootChord = 1f;
        public float tipChord = 1f;
        public float sweep;
        public float dihedral;
        public float incidence;
        public float twist;
        [Min(1)] public int stations = 4;
        public bool verticalSpan;
        public bool mirror = true;

        [Header("Control")]
        public ControlRole control = ControlRole.None;
        public float controlDeg = 15f;
        [Range(0f, 1f)] public float ailStart = 0.5f;
        [Range(0f, 1f)] public float flapStart;
        [Range(0f, 1f)] public float flapEnd;
        [Range(0f, 1f)] public float airbrkStart;
        [Range(0f, 1f)] public float airbrkEnd;

        [Header("Piecewise polar")]
        public float clPerDeg = 0.11f;
        public float zeroLiftAlpha;
        public float stallEndDeg = 12f;
        public float dropEndDeg = 30f;
        public float cd0 = 0.02f;
        public float kInduced = 0.045f;
        public float clDrop = 0.8f;
        public float cdDrop = 0.7f;
        public float clFlat = 0.10f;
        public float cdFlat = 1.2f;

        public bool HasFlap => flapEnd > flapStart;
        public bool HasAirbrake => airbrkEnd > airbrkStart;
    }

    [Serializable]
    public class DragPoint
    {
        public Vector3 pos;
        [Tooltip("Effective drag area Cd*A in m^2.")]
        public float cd = 0.1f;
        public bool isGear;
    }

    [Serializable]
    public class EngineDef
    {
        public string name = "E1";
        public Vector3 pos;
        public Vector3 dir = Vector3.forward;
        public float idleThrust = 392f;
        public float maxThrust = 13327f;
        [Tooltip("Spool rate, 1/s.")]
        public float response = 1f;
    }

    /// Every tweakable value for the flight model, in SI units.
    [Serializable]
    public class FlightConfig
    {
        [Header("World")]
        public float airDensity = 1.225f;
        public Vector3 windWorld = Vector3.zero;

        [Header("Mass properties")]
        public float massKg = 2222f;
        public Vector3 centerOfMass = new Vector3(0f, 0.174f, 0.446f);
        [Tooltip("x = pitch, y = yaw, z = roll (kg*m^2).")]
        public Vector3 inertiaTensor = new Vector3(2630f, 7590f, 6210f);

        [Header("Control signs")]
        public float elevatorSign = -1f;
        public float aileronSign = -1f;
        public float rudderSign = 1f;

        [Header("Throttle")]
        [Tooltip("Lever travel per second, 0..1.")]
        public float leverSlew = 0.25f;

        [Header("Flaps / airbrakes")]
        public float[] flapAngles = { 0f, 15f, 40f };
        public float flapResponse = 2f;
        public float flapClFactor = 3f;
        public float flapCdFactor = 4.5f;
        public float flapWingAreaFactor = 1.1f;
        [Tooltip("Metres the flapped stations' force slides aft at full flap.")]
        public float flapCpShift = 0.896f;

        public float[] airbrkAngles = { 0f, 20f, 40f };
        public float airbrkResponse = 2f;
        public float airbrkClFactor = 0.72f;
        public float airbrkCdFactor = 2.75f;
        [Tooltip("Metres the airbrake stations' force slides forward at full deploy.")]
        public float airbrkCpShift = 0.28f;

        [Header("Integration")]
        [Min(1)] public int substeps = 8;
        public float dtMax = 0.033f;
        public float maxSpeedGuard = 5600f;
        public float maxOmegaGuard = 1000f;
        public float maxForce = 1.6e6f;
        public float maxTorque = 4e6f;

        [Header("Angular damping (N*m per rad/s)")]
        public float pitchDamp = 70244f;
        public float yawDamp = 70244f;
        public float rollDamp = 35122f;

        [Header("Control-surface actuator spring")]
        public float actuatorFreq = 12f;
        public float actuatorDamping = 1f;

        [Header("Debug")]
        [Tooltip("Initial state of the force beams; toggled in play with M.")]
        public bool showBeams;
        [Tooltip("Metres of beam drawn per Newton.")]
        public float beamScale = 0.001f;
        [Tooltip("Beam thickness in metres.")]
        public float beamWidth = 0.06f;
        public bool showHud = true;
        public float mouseBoxSize = 200f;

        [Header("Lifting surfaces")]
        public Surface[] surfaces =
        {
            new Surface
            {
                name = "MainWing",
                rootOffset = new Vector3(0f, -0.56f, -3.752f),
                span = 5.88f, rootChord = 2.24f, tipChord = 1.12f,
                sweep = 4f, dihedral = 4f, incidence = 2f, twist = -2f,
                stations = 10, verticalSpan = false, mirror = true,
                control = ControlRole.Aileron, controlDeg = 14f, ailStart = 0.7f,
                flapStart = 0.05f, flapEnd = 0.7f,
                airbrkStart = 0.2f, airbrkEnd = 0.6f,
                clPerDeg = 0.076f, zeroLiftAlpha = -0.5f,
                stallEndDeg = 12f, dropEndDeg = 32f,
                cd0 = 0.026f, kInduced = 0.030f,
                clDrop = 0.70f, cdDrop = 0.85f, clFlat = 0.08f, cdFlat = 1.25f,
            },
            new Surface
            {
                name = "HorizontalStab",
                rootOffset = new Vector3(0f, 0.28f, -7.84f),
                span = 3.36f, rootChord = 0.896f, tipChord = 0.56f,
                sweep = 24f, dihedral = 35f, incidence = -1f, twist = 0f,
                stations = 3, verticalSpan = false, mirror = true,
                control = ControlRole.Elevator, controlDeg = 18f,
                clPerDeg = 0.09f, zeroLiftAlpha = 0f,
                stallEndDeg = 14f, dropEndDeg = 30f,
                cd0 = 0.012f, kInduced = 0.060f,
                clDrop = 0.60f, cdDrop = 0.60f, clFlat = 0.10f, cdFlat = 1.2f,
            },
            new Surface
            {
                name = "VerticalStab",
                rootOffset = new Vector3(0f, 0.28f, -7.84f),
                span = 2.8f, rootChord = 1.12f, tipChord = 0.672f,
                sweep = 20f, dihedral = 0f, incidence = 0f, twist = 0f,
                stations = 2, verticalSpan = true, mirror = false,
                control = ControlRole.Rudder, controlDeg = 22f,
                clPerDeg = 0.08f, zeroLiftAlpha = 0f,
                stallEndDeg = 16f, dropEndDeg = 30f,
                cd0 = 0.012f, kInduced = 0.090f,
                clDrop = 0.50f, cdDrop = 0.60f, clFlat = 0.10f, cdFlat = 1.2f,
            },
        };

        [Header("Parasitic drag points")]
        public DragPoint[] dragPoints =
        {
            new DragPoint { pos = new Vector3(0f, 0.168f, -4.446f), cd = 0.5f },
            new DragPoint { pos = new Vector3(0f, 1.12f, -7.84f), cd = 0.2f },
            new DragPoint { pos = new Vector3(-1.12f, -0.885f, -4.726f), cd = 0.15f, isGear = true },
            new DragPoint { pos = new Vector3(1.12f, -0.885f, -4.726f), cd = 0.15f, isGear = true },
            new DragPoint { pos = new Vector3(0f, -0.851f, -1.042f), cd = 0.1f, isGear = true },
        };

        [Header("Engines")]
        public EngineDef[] engines =
        {
            new EngineDef
            {
                name = "E1",
                pos = new Vector3(0f, 1.028f, -7.371f),
                dir = new Vector3(0f, 0.09532f, 0.99545f),
                idleThrust = 392f, maxThrust = 13327f, response = 1f,
            },
        };
    }
}

