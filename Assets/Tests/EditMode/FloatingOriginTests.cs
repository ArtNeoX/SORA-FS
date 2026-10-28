using System;
using NUnit.Framework;
using UnityEngine;

namespace CirrusFS.Geo.Tests
{
    /// Exercises GeoOrigin the way the game does: float Unity state carried frame to frame,
    /// re-centred on the camera every 5 km, checked against a double-precision truth.
    public class FloatingOriginTests
    {
        [TearDown]
        public void ResetOrigin() => GeoOrigin.Reset(GeoMath.GeodeticToEcef(0.0, 0.0, 0.0));

        [Test]
        public void Origin_MapsToZero_AndAxesAreLocalEnu()
        {
            GeoOrigin.Reset(GeoMath.GeodeticToEcef(37.09, 127.03, 3000.0));

            Assert.Less(GeoOrigin.EcefToUnity(GeoOrigin.Ecef).magnitude, 1e-6f);

            Vector3 up = GeoOrigin.UpAt(Vector3.zero);
            Assert.Less(Vector3.Angle(up, Vector3.up), 1e-4f);

            // 1 km north is +Z, 1 km east is +X.
            Vector3 north = GeoOrigin.GeodeticToUnity(37.09 + 1000.0 / 111000.0, 127.03, 3000.0);
            Assert.Greater(north.z, 990f);
            Assert.Less(Mathf.Abs(north.x), 1f);

            Vector3 east = GeoOrigin.GeodeticToUnity(37.09, 127.03 + 1000.0 / 88900.0, 3000.0);
            Assert.Greater(east.x, 990f);
            Assert.Less(Mathf.Abs(east.z), 1f);
        }

        [Test]
        public void Recenter_FrameChangeAgreesWithDirectConversion()
        {
            GeoOrigin.Reset(GeoMath.GeodeticToEcef(37.09, 127.03, 3000.0));
            var random = new System.Random(3);

            for (int i = 0; i < 200; i++)
            {
                Double3 point = GeoOrigin.UnityToEcef(new Vector3(Rand(random, 4000), Rand(random, 4000), Rand(random, 4000)));
                Vector3 before = GeoOrigin.EcefToUnity(point);

                Double3 newOrigin = GeoOrigin.UnityToEcef(new Vector3(Rand(random, 6000), Rand(random, 3000), Rand(random, 6000)));
                GeoFrameChange change = GeoOrigin.Recenter(newOrigin);

                Vector3 viaChange = change.TransformPoint(before);
                Vector3 direct = GeoOrigin.EcefToUnity(point);

                Assert.Less((viaChange - direct).magnitude, 0.001f, $"step {i}");
                Assert.Less(Vector3.Angle(GeoOrigin.UpAt(Vector3.zero), Vector3.up), 1e-4f);
            }
        }

        static float Rand(System.Random r, float range) => (float)(r.NextDouble() * 2.0 - 1.0) * range;

        /// Straight, level flight for 500 km at 250 m/s and 60 fps, the camera 300 m behind.
        /// The aircraft's Unity pose is integrated in float exactly as physics would, and
        /// carried through every re-centre by the frame change. Checks:
        ///  - jitter: the per-frame position error never jumps by a millimetre;
        ///  - drift: total position error after 500 km stays under a metre;
        ///  - horizon: the carried attitude stays level against the true local frame, and
        ///    Unity +Y is local up at the camera after every re-centre.
        [Test]
        public void Fly500Km_NoJitter_HorizonStaysLevel()
        {
            const double lat0 = 37.09, lon0 = 127.03, alt = 3000.0;
            const double speed = 250.0, dt = 1.0 / 60.0, distance = 500000.0;
            const double headingDeg = 60.0;
            const float threshold = 5000f;

            // Truth, in double: fly along the local forward, holding altitude and wings level.
            Double3 truthPos = GeoMath.GeodeticToEcef(lat0, lon0, alt);
            GeoMath.HeadingPitchRollToEcef(lat0, lon0, headingDeg, 0.0, 0.0, out Double3 truthFwd, out Double3 truthUp, out _);

            GeoOrigin.Reset(truthPos);

            // Game state, in float.
            Vector3 aircraft = Vector3.zero;
            Quaternion attitude = GeoOrigin.EcefAttitudeToUnity(truthFwd, truthUp);
            Vector3 cameraOffsetBody = new Vector3(0f, 40f, -300f);

            double previousError = 0.0, worstJump = 0.0, worstError = 0.0;
            float worstTilt = 0f, worstCameraUp = 0f;
            int recentres = 0;
            int steps = (int)(distance / (speed * dt));

            for (int i = 0; i < steps; i++)
            {
                // Truth step: move, then hold altitude and re-level against the new local frame.
                Double3 oldPos = truthPos, oldFwd = truthFwd, oldUp = truthUp;
                truthPos += truthFwd * (speed * dt);
                GeoMath.EcefToGeodetic(truthPos, out double lat, out double lon, out _);
                truthPos = GeoMath.GeodeticToEcef(lat, lon, alt);
                GeoMath.EnuBasis(lat, lon, out _, out _, out Double3 up);
                truthFwd = (truthFwd - up * Double3.Dot(truthFwd, up)).Normalized;
                truthUp = up;

                // Game step: physics adds the displacement and the turn in the current frame.
                aircraft += GeoOrigin.EcefDirToUnity(truthPos - oldPos);
                Quaternion before = GeoOrigin.EcefAttitudeToUnity(oldFwd, oldUp);
                Quaternion after = GeoOrigin.EcefAttitudeToUnity(truthFwd, truthUp);
                attitude = Quaternion.Normalize(after * Quaternion.Inverse(before) * attitude);

                // Camera rig, then the re-centre check, as in LateUpdate.
                Vector3 camera = aircraft + attitude * cameraOffsetBody;
                if (camera.magnitude > threshold)
                {
                    GeoFrameChange change = GeoOrigin.Recenter(GeoOrigin.UnityToEcef(camera));
                    aircraft = change.TransformPoint(aircraft);
                    attitude = change.TransformRotation(attitude);
                    camera = change.TransformPoint(camera);
                    recentres++;

                    worstCameraUp = Mathf.Max(worstCameraUp, Vector3.Angle(GeoOrigin.UpAt(camera), Vector3.up));
                }

                double error = Double3.Distance(GeoOrigin.UnityToEcef(aircraft), truthPos);
                worstError = Math.Max(worstError, error);
                if (i > 0) worstJump = Math.Max(worstJump, Math.Abs(error - previousError));
                previousError = error;

                if (i % 600 == 0 || i == steps - 1)
                {
                    GeoOrigin.UnityToHeadingPitchRoll(aircraft, attitude, out _, out double pitch, out double roll);
                    worstTilt = Mathf.Max(worstTilt, Mathf.Max(Mathf.Abs((float)pitch), Mathf.Abs((float)roll)));
                    Assert.Less(aircraft.magnitude, threshold + 1000f, "aircraft stayed near the origin");
                }
            }

            TestContext.WriteLine($"{steps} frames, {recentres} re-centres, worst error {worstError * 1000.0:F2} mm, " +
                                  $"worst frame-to-frame jump {worstJump * 1000.0:F3} mm, worst tilt {worstTilt:F5} deg, " +
                                  $"worst camera up {worstCameraUp:F5} deg");

            Assert.Greater(recentres, 90, "about one re-centre per 5 km");
            Assert.Less(worstJump, 0.001, "jitter: per-frame error jumped by a millimetre or more");
            Assert.Less(worstError, 1.0, "drift: float integration lost more than a metre over 500 km");
            Assert.Less(worstTilt, 0.01f, "horizon: attitude drifted off level through re-centres");
            Assert.Less(worstCameraUp, 1e-3f, "horizon: Unity +Y is not local up after a re-centre");
        }
    }
}
