using System;
using UnityEngine;

namespace CirrusFS.Geo
{
    /// Double-precision 3-vector. Every world position in CirrusFS is one of these, in ECEF
    /// metres; a float Vector3 only ever holds a position relative to the floating origin.
    [Serializable]
    public struct Double3 : IEquatable<Double3>
    {
        public double x;
        public double y;
        public double z;

        public Double3(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static readonly Double3 Zero = new Double3(0.0, 0.0, 0.0);

        public double SqrMagnitude => x * x + y * y + z * z;
        public double Magnitude => Math.Sqrt(x * x + y * y + z * z);

        public Double3 Normalized
        {
            get
            {
                double m = Magnitude;
                return m > 0.0 ? new Double3(x / m, y / m, z / m) : Zero;
            }
        }

        public static double Dot(Double3 a, Double3 b) => a.x * b.x + a.y * b.y + a.z * b.z;

        /// Right-handed cross product (ECEF is right-handed).
        public static Double3 Cross(Double3 a, Double3 b) =>
            new Double3(a.y * b.z - a.z * b.y,
                        a.z * b.x - a.x * b.z,
                        a.x * b.y - a.y * b.x);

        public static double Distance(Double3 a, Double3 b) => (a - b).Magnitude;

        public static Double3 operator +(Double3 a, Double3 b) => new Double3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Double3 operator -(Double3 a, Double3 b) => new Double3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Double3 operator -(Double3 a) => new Double3(-a.x, -a.y, -a.z);
        public static Double3 operator *(Double3 a, double s) => new Double3(a.x * s, a.y * s, a.z * s);
        public static Double3 operator *(double s, Double3 a) => new Double3(a.x * s, a.y * s, a.z * s);
        public static Double3 operator /(Double3 a, double s) => new Double3(a.x / s, a.y / s, a.z / s);

        /// Narrows to float. Only meaningful for small vectors: offsets from the floating origin,
        /// directions, or vertex positions relative to a nearby chunk centre.
        public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);

        public static Double3 FromVector3(Vector3 v) => new Double3(v.x, v.y, v.z);

        public bool Equals(Double3 other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is Double3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(x, y, z);

        public override string ToString() => $"({x:F3}, {y:F3}, {z:F3})";
    }
}
