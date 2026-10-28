using System;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// The floating origin on the globe: where Unity (0,0,0) is, and which way Unity's axes
    /// point, as an ECEF point plus the local east/north/up frame there.
    ///
    ///     Unity +X = east, +Y = up, +Z = north, all at the origin.
    ///
    /// This is the ONLY bridge between ECEF and Unity space. Anything placed in the world goes
    /// ECEF (double) -> EcefToUnity -> float Vector3, so every float in the scene is an offset
    /// from a nearby double. Nothing else may hold a float "world position".
    ///
    /// Handedness: ECEF and ENU are right-handed, Unity is left-handed. Mapping (E, N, U) onto
    /// (X, Z, Y) is a reflection, and it is applied consistently to every point and direction,
    /// so a rotation in one space stays a proper rotation in the other. Never convert a
    /// quaternion's components directly - convert its axes and rebuild it.
    public static class GeoOrigin
    {
        /// ECEF position of Unity (0,0,0).
        public static Double3 Ecef { get; private set; }

        /// ECEF unit vectors of Unity +X, +Z and +Y.
        public static Double3 East { get; private set; }
        public static Double3 North { get; private set; }
        public static Double3 Up { get; private set; }

        public static double Latitude { get; private set; }
        public static double Longitude { get; private set; }
        public static double Height { get; private set; }

        public static bool IsSet { get; private set; }

        /// Re-centres since the last Reset.
        public static int RecenterCount { get; private set; }

        /// Raised after the origin moved, with the transform that maps old Unity coordinates
        /// onto new ones. Anything holding Unity-space state applies it.
        public static event Action<GeoFrameChange> Recentered;

        /// Raised when the origin is placed from scratch (a spawn). There is no continuity
        /// with the old frame, so listeners re-derive their Unity state from ECEF.
        public static event Action OriginReset;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            IsSet = false;
            RecenterCount = 0;
            Recentered = null;
            OriginReset = null;
            Place(GeoMath.GeodeticToEcef(0.0, 0.0, 0.0));
        }

        static GeoOrigin()
        {
            Place(GeoMath.GeodeticToEcef(0.0, 0.0, 0.0));
        }

        // ---- moving the origin ---------------------------------------------------

        /// Puts the origin at `ecef` with no continuity: used at spawn.
        public static void Reset(Double3 ecef)
        {
            Place(ecef);
            IsSet = true;
            RecenterCount = 0;
            OriginReset?.Invoke();
        }

        /// Moves the origin to `ecef` and re-aligns the axes to the local frame there. Returns
        /// the old-to-new transform, which is also broadcast through Recentered.
        public static GeoFrameChange Recenter(Double3 ecef)
        {
            Double3 oldOrigin = Ecef, oldE = East, oldN = North, oldU = Up;

            Place(ecef);
            IsSet = true;
            RecenterCount++;

            var change = GeoFrameChange.Between(oldOrigin, oldE, oldN, oldU, Ecef, East, North, Up);
            Recentered?.Invoke(change);
            return change;
        }

        static void Place(Double3 ecef)
        {
            GeoMath.EcefToGeodetic(ecef, out double lat, out double lon, out double h);
            GeoMath.EnuBasis(lat, lon, out Double3 e, out Double3 n, out Double3 u);

            Ecef = ecef;
            East = e;
            North = n;
            Up = u;
            Latitude = lat;
            Longitude = lon;
            Height = h;
        }

        // ---- points --------------------------------------------------------------

        /// ECEF -> Unity, kept in double. Use this when the result will be offset again before
        /// narrowing (chunk centres, distances).
        public static Double3 EcefToUnityDouble(Double3 ecef)
        {
            Double3 d = ecef - Ecef;
            return new Double3(Double3.Dot(d, East), Double3.Dot(d, Up), Double3.Dot(d, North));
        }

        public static Vector3 EcefToUnity(Double3 ecef) => EcefToUnityDouble(ecef).ToVector3();

        public static Double3 UnityToEcef(Double3 unity) =>
            Ecef + East * unity.x + Up * unity.y + North * unity.z;

        public static Double3 UnityToEcef(Vector3 unity) => UnityToEcef(Double3.FromVector3(unity));

        public static Vector3 GeodeticToUnity(double latitudeDeg, double longitudeDeg, double height) =>
            EcefToUnity(GeoMath.GeodeticToEcef(latitudeDeg, longitudeDeg, height));

        public static void UnityToGeodetic(Vector3 unity, out double latitudeDeg, out double longitudeDeg,
                                           out double height) =>
            GeoMath.EcefToGeodetic(UnityToEcef(unity), out latitudeDeg, out longitudeDeg, out height);

        /// Height above the ellipsoid of a Unity point.
        public static double HeightAt(Vector3 unity)
        {
            GeoMath.EcefToGeodetic(UnityToEcef(unity), out _, out _, out double h);
            return h;
        }

        // ---- directions ----------------------------------------------------------

        public static Double3 EcefDirToUnityDouble(Double3 dir) =>
            new Double3(Double3.Dot(dir, East), Double3.Dot(dir, Up), Double3.Dot(dir, North));

        public static Vector3 EcefDirToUnity(Double3 dir) => EcefDirToUnityDouble(dir).ToVector3();

        public static Double3 UnityDirToEcef(Vector3 dir) => East * dir.x + Up * dir.y + North * dir.z;

        /// Geodetic up at a Unity point, as a Unity direction. Equals Vector3.up at the origin
        /// and tilts away from it with distance (about 0.045 deg at 5 km).
        public static Vector3 UpAt(Vector3 unity)
        {
            GeoMath.EnuBasisAt(UnityToEcef(unity), out _, out _, out Double3 up);
            return EcefDirToUnity(up);
        }

        /// Direction from a Unity point to the Earth's centre - the way gravity pulls.
        public static Vector3 DownToCentre(Vector3 unity) => EcefDirToUnity(-UnityToEcef(unity).Normalized);

        /// Unity rotation whose axes are the local east (X), up (Y) and north (Z) at an ECEF
        /// point: what "level, facing north" means there.
        public static Quaternion EnuRotationAt(Double3 ecef)
        {
            GeoMath.EnuBasisAt(ecef, out _, out Double3 n, out Double3 u);
            return Quaternion.LookRotation(EcefDirToUnity(n), EcefDirToUnity(u));
        }

        public static Quaternion EnuRotationAt(Vector3 unity) => EnuRotationAt(UnityToEcef(unity));

        // ---- attitude ------------------------------------------------------------

        /// Unity rotation for an attitude given by its ECEF body axes.
        public static Quaternion EcefAttitudeToUnity(Double3 forward, Double3 up) =>
            Quaternion.LookRotation(EcefDirToUnity(forward), EcefDirToUnity(up));

        public static Quaternion HeadingPitchRollToUnity(double latitudeDeg, double longitudeDeg,
                                                         double headingDeg, double pitchDeg, double rollDeg)
        {
            GeoMath.HeadingPitchRollToEcef(latitudeDeg, longitudeDeg, headingDeg, pitchDeg, rollDeg,
                                           out Double3 f, out Double3 u, out _);
            return EcefAttitudeToUnity(f, u);
        }

        /// Heading/pitch/roll of a Unity rotation at a Unity position.
        public static void UnityToHeadingPitchRoll(Vector3 position, Quaternion rotation,
                                                   out double headingDeg, out double pitchDeg, out double rollDeg)
        {
            GeoMath.EcefToGeodetic(UnityToEcef(position), out double lat, out double lon, out _);
            GeoMath.EcefToHeadingPitchRoll(lat, lon,
                                           UnityDirToEcef(rotation * Vector3.forward),
                                           UnityDirToEcef(rotation * Vector3.up),
                                           out headingDeg, out pitchDeg, out rollDeg);
        }

        public static string Describe() =>
            $"origin {Latitude:F6}, {Longitude:F6}, h {Height:F0} m ({RecenterCount} re-centre" +
            $"{(RecenterCount == 1 ? "" : "s")})";
    }

    /// Rigid transform from one origin frame to the next: p_new = M * p_old + t.
    /// Held in double so applying it to a point does not lose more than the final narrowing.
    public readonly struct GeoFrameChange
    {
        // Rows of M.
        readonly Double3 r0, r1, r2;

        /// Translation, Unity metres in the new frame.
        public readonly Double3 Translation;

        /// M as a quaternion, for rotating orientations and velocities.
        public readonly Quaternion Rotation;

        GeoFrameChange(Double3 r0, Double3 r1, Double3 r2, Double3 translation)
        {
            this.r0 = r0;
            this.r1 = r1;
            this.r2 = r2;
            Translation = translation;

            // Columns of M are where the old Unity axes land in the new frame.
            var newZ = new Vector3((float)r0.z, (float)r1.z, (float)r2.z);
            var newY = new Vector3((float)r0.y, (float)r1.y, (float)r2.y);
            Rotation = Quaternion.LookRotation(newZ, newY);
        }

        /// Unity point p_old is ECEF O1 + E1 x + U1 y + N1 z; the new Unity coordinates are
        /// that point dotted with (E2, U2, N2) after subtracting O2.
        public static GeoFrameChange Between(Double3 o1, Double3 e1, Double3 n1, Double3 u1,
                                             Double3 o2, Double3 e2, Double3 n2, Double3 u2)
        {
            // Old Unity axes, in ECEF: X = E1, Y = U1, Z = N1.
            var row0 = new Double3(Double3.Dot(e2, e1), Double3.Dot(e2, u1), Double3.Dot(e2, n1));
            var row1 = new Double3(Double3.Dot(u2, e1), Double3.Dot(u2, u1), Double3.Dot(u2, n1));
            var row2 = new Double3(Double3.Dot(n2, e1), Double3.Dot(n2, u1), Double3.Dot(n2, n1));

            Double3 d = o1 - o2;
            var t = new Double3(Double3.Dot(d, e2), Double3.Dot(d, u2), Double3.Dot(d, n2));

            return new GeoFrameChange(row0, row1, row2, t);
        }

        public Double3 TransformDirection(Double3 v) =>
            new Double3(Double3.Dot(r0, v), Double3.Dot(r1, v), Double3.Dot(r2, v));

        public Double3 TransformPoint(Double3 p) => TransformDirection(p) + Translation;

        public Vector3 TransformPoint(Vector3 p) => TransformPoint(Double3.FromVector3(p)).ToVector3();

        public Vector3 TransformDirection(Vector3 v) => TransformDirection(Double3.FromVector3(v)).ToVector3();

        public Quaternion TransformRotation(Quaternion q) => Rotation * q;

        /// How far Unity's axes turned in this change, degrees.
        public float AngleDeg => Quaternion.Angle(Quaternion.identity, Rotation);
    }
}
