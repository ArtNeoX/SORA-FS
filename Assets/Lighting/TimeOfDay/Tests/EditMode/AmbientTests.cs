using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace CirrusFS.TimeOfDay.Tests
{
    /// The ambient projection must produce what Unity's lighting expects from
    /// RenderSettings.ambientProbe: for each normal, the cosine-weighted average radiance
    /// (irradiance / pi) of the environment.
    public class AmbientTests
    {
        const int N = 32;

        static (Vector3[] dirs, float[] solidAngles) Table()
        {
            var dirs = new Vector3[6 * N * N];
            var sa = new float[6 * N * N];
            int i = 0;
            for (int f = 0; f < 6; f++)
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++, i++)
                    {
                        float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                        dirs[i] = EnvironmentLighting.FaceDirection(f, u, v);
                        float d = 1f + u * u + v * v;
                        sa[i] = 4f / (N * N) / (d * Mathf.Sqrt(d));
                    }
            return (dirs, sa);
        }

        static SphericalHarmonicsL2 Project(System.Func<Vector3, float> radiance)
        {
            var (dirs, sa) = Table();
            var data = new NativeArray<ushort>(dirs.Length * 4, Allocator.Temp);
            for (int i = 0; i < dirs.Length; i++)
            {
                ushort h = Mathf.FloatToHalf(radiance(dirs[i]));
                data[i * 4] = h; data[i * 4 + 1] = h; data[i * 4 + 2] = h; data[i * 4 + 3] = Mathf.FloatToHalf(1f);
            }
            var sh = EnvironmentLighting.ProjectToSH(data, dirs, sa);
            data.Dispose();
            return sh;
        }

        static float Eval(SphericalHarmonicsL2 sh, Vector3 n)
        {
            var o = new Color[1];
            sh.Evaluate(new[] { n }, o);
            return o[0].r;
        }

        [Test]
        public void TexelSolidAnglesCoverTheSphere()
        {
            var (_, sa) = Table();
            float sum = 0f;
            foreach (float s in sa) sum += s;
            Assert.AreEqual(4f * Mathf.PI, sum, 0.01f);
        }

        [Test]
        public void UniformSky_GivesItsRadianceInEveryDirection()
        {
            var sh = Project(_ => 0.7f);
            foreach (var n in new[] { Vector3.up, Vector3.down, Vector3.right, Vector3.forward })
                Assert.AreEqual(0.7f, Eval(sh, n), 0.01f);
        }

        [Test]
        public void BrightUpperHemisphere_MatchesTheCosineIntegral()
        {
            // L = 1 above the horizon, 0 below. Exact irradiance/pi: 1 up, 0.5 sideways, 0 down;
            // order-2 harmonics ring by a few percent.
            var sh = Project(w => w.y > 0f ? 1f : 0f);
            Assert.AreEqual(1f, Eval(sh, Vector3.up), 0.08f);
            Assert.AreEqual(0.5f, Eval(sh, Vector3.right), 0.03f);
            Assert.AreEqual(0f, Eval(sh, Vector3.down), 0.08f);
        }
    }
}
