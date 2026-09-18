using System.Collections.Generic;
using UnityEngine;

namespace CirrusFS.Flight
{
    /// One blade-element station in the aircraft frame (+Z fwd, +Y up, +X right).
    public struct Station
    {
        public Surface surf;
        public float side;
        public bool vertical;
        public Vector3 pos;
        public Vector3 s;
        public Vector3 f;
        public Vector3 n;
        public float chord;
        public float width;
        public ControlRole role;
        public float frac;
    }

    /// Blade-element geometry + piecewise airfoil polar.
    public static class Aerodynamics
    {
        // Roblox's CFrame.fromAxisAngle turns right-handed about the axis. Mirroring that
        // -Z-forward frame onto Unity's +Z-forward one reverses the sense of every rotation,
        // so the angle is negated here to keep incidence and deflections physically identical.
        public static Vector3 Rot(Vector3 v, Vector3 axis, float radians)
        {
            return Quaternion.AngleAxis(-radians * Mathf.Rad2Deg, axis) * v;
        }

        /// Piecewise airfoil polar, hard-capped so coefficients can never run away.
        /// alpha is the local angle of attack in radians.
        public static void Coefficients(float alpha, Surface s, out float cl, out float cd)
        {
            float deg = alpha * Mathf.Rad2Deg - s.zeroLiftAlpha;
            float sign = deg >= 0f ? 1f : -1f;
            float a = Mathf.Abs(deg);

            if (a <= s.stallEndDeg)
            {
                // Linear range: straight lift line, parabolic drag bucket.
                cl = s.clPerDeg * deg;
                cd = s.cd0 + s.kInduced * cl * cl;
            }
            else if (a <= s.dropEndDeg)
            {
                // Stall range: lift eases down from the peak, drag climbs steeply.
                float peak = s.clPerDeg * s.stallEndDeg;
                float t = (a - s.stallEndDeg) / Mathf.Max(s.dropEndDeg - s.stallEndDeg, 1e-4f);
                cl = sign * (peak + (s.clDrop - peak) * (t * t * (3f - 2f * t)));
                float cdPeak = s.cd0 + s.kInduced * peak * peak;
                cd = cdPeak + (s.cdDrop - cdPeak) * (t * t);
            }
            else
            {
                // Extreme / glitch guard: flat-plate hard caps.
                cl = sign * s.clFlat;
                cd = s.cdFlat;
            }
        }

        /// Build immutable per-station geometry from the surface list.
        public static Station[] Build(FlightConfig config)
        {
            var stations = new List<Station>();

            foreach (var surf in config.surfaces)
            {
                bool vertical = surf.verticalSpan;
                int n = Mathf.Max(surf.stations, 1);
                float semi = surf.span;
                float dw = semi / n;
                float sweepT = Mathf.Tan(surf.sweep * Mathf.Deg2Rad);
                float dih = surf.dihedral * Mathf.Deg2Rad;
                float[] sides = surf.mirror ? new[] { 1f, -1f } : new[] { 1f };

                foreach (float side in sides)
                {
                    Vector3 s = vertical
                        ? Vector3.up
                        : new Vector3(side * Mathf.Cos(dih), Mathf.Sin(dih), 0f).normalized;

                    for (int i = 1; i <= n; i++)
                    {
                        float yc = (i - 0.5f) * dw;
                        float frac = yc / semi;
                        float chord = surf.rootChord + (surf.tipChord - surf.rootChord) * frac;
                        float inc = (surf.incidence + surf.twist * frac) * Mathf.Deg2Rad;

                        Vector3 f, nrm;
                        if (vertical)
                        {
                            f = Rot(Vector3.forward, s, inc);
                            nrm = Rot(Vector3.right, s, inc);
                        }
                        else
                        {
                            Vector3 n0 = new Vector3(-side * Mathf.Sin(dih), Mathf.Cos(dih), 0f).normalized;
                            f = Rot(Vector3.forward, s, side * inc);
                            nrm = Rot(n0, s, side * inc);
                        }

                        float horiz = vertical ? yc : yc * Mathf.Cos(dih);
                        // +Z is forward, so sweepback carries the station aft.
                        Vector3 pos = surf.rootOffset + s * yc + new Vector3(0f, 0f, -horiz * sweepT);

                        ControlRole role = ControlRole.None;
                        if (surf.control == ControlRole.Elevator || surf.control == ControlRole.Rudder)
                            role = surf.control;
                        else if (surf.control == ControlRole.Aileron && frac >= surf.ailStart)
                            role = ControlRole.Aileron;

                        stations.Add(new Station
                        {
                            surf = surf, side = side, vertical = vertical,
                            pos = pos, s = s, f = f, n = nrm,
                            chord = chord, width = dw, role = role, frac = frac,
                        });
                    }
                }
            }

            return stations.ToArray();
        }
    }
}

