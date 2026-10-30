using System;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CirrusFS.TimeOfDay
{
    /// Ambient light and reflections from the time-of-day sky, without DynamicGI and without
    /// rendering the scene into a probe every frame.
    ///
    ///  - Sky cubemap: the atmosphere LUTs (sky and ground, no sun or moon disc) rendered into a
    ///    small cubemap whenever the sky LUTs change, a few thousand pixels. It is the
    ///    reflection source on every tier, through a Custom reflection probe that follows the
    ///    camera, and the source of the ambient.
    ///  - Ambient: spherical harmonics (L2) projected on the CPU from an asynchronous readback of
    ///    one low mip of that cubemap (8x8 per face), convolved with the cosine lobe, and eased
    ///    into RenderSettings.ambientProbe over a fraction of a second, so it never pops.
    ///  - High tier: a realtime probe that also sees terrain, rendered one face per frame
    ///    (IndividualFaces) when the sun has moved or the camera has travelled far enough.
    [ExecuteAlways]
    [DefaultExecutionOrder(320)]
    public class EnvironmentLighting : MonoBehaviour
    {
        public TimeOfDaySystem system;
        public AtmosphereRenderer atmosphere;
        public SunMoonController sunMoon;
        public Shader skyCubeShader;

        [Tooltip("Custom probe that shows the sky cubemap everywhere. Follows the camera.")]
        public ReflectionProbe skyProbe;
        [Tooltip("Realtime probe that also renders the scene (High tier). Follows the camera.")]
        public ReflectionProbe sceneProbe;

        /// Incremented whenever the applied ambient changes; InteriorAmbientZone watches it.
        public static int AmbientVersion { get; private set; }

        /// The ambient currently applied (before any interior scaling).
        public static SphericalHarmonicsL2 Ambient { get; private set; }

        // Probe volume: large enough to contain anything drawn around the camera.
        const float ProbeSize = 200000f;
        const int ReadbackMipSize = 8;

        RenderTexture skyCube, sceneCube;
        Material cubeMaterial;
        CommandBuffer cmd;
        int lastSkyBuilds = -1, lastMoonBuilds = -1;
        bool cubeValid;
        float cubeExposure = 1f, readbackExposure = 1f;

        NativeArray<ushort> readback;
        bool readbackPending, readbackWanted;
        Action<AsyncGPUReadbackRequest> onReadback;
        Vector3[] texelDirections;
        float[] texelSolidAngles;
        SphericalHarmonicsL2 current, target;
        bool haveAmbient;

        int sceneProbeRender = -1;
        float sceneProbeSunElevation;
        CirrusFS.Geo.Double3 sceneProbeEcef;
        double sceneProbeTime = -1e9;
        bool sceneProbeSeeded;
        float sceneProbeExposure = 1f;
        double lastRealtime = -1.0;

        static readonly ProfilerMarker Marker = new ProfilerMarker("TimeOfDay.Environment");
        static readonly int CubeFaceId = Shader.PropertyToID("_TOD_CubeFace");

        // ---- stats ------------------------------------------------------------------------

        public int CubeRenders { get; private set; }
        /// Of those, re-renders because exposure drifted from the cube's (only while adapting).
        public int ExposureCubeRenders { get; private set; }
        public int AmbientUpdates { get; private set; }
        public int SceneProbeRenders { get; private set; }
        public RenderTexture SkyCube => skyCube;

        // ---- lifecycle --------------------------------------------------------------------

        void OnEnable()
        {
            if (system == null) system = GetComponentInParent<TimeOfDaySystem>();
            if (atmosphere == null) atmosphere = GetComponentInParent<AtmosphereRenderer>();
            if (sunMoon == null) sunMoon = GetComponentInParent<SunMoonController>();
            cmd = new CommandBuffer { name = "TimeOfDay.SkyCube" };
            onReadback = OnReadback;
            readback = new NativeArray<ushort>(ReadbackMipSize * ReadbackMipSize * 6 * 4, Allocator.Persistent);
            BuildTexelTable();
            cubeValid = false;
            haveAmbient = false;
            readbackPending = false;
            lastRealtime = -1.0;
        }

        void OnDisable()
        {
            if (readbackPending) AsyncGPUReadback.WaitAllRequests();
            if (readback.IsCreated) readback.Dispose();
            Release(ref skyCube);
            Release(ref sceneCube);
            CoreUtils.Destroy(cubeMaterial);
            cubeMaterial = null;
            cmd?.Release();
            cmd = null;
        }

        void LateUpdate() => Refresh();

        /// Skips the ease: the ambient jumps to the latest sky. For time scrubbing, tools and
        /// tests; normal running eases over AmbientSettings.blendSeconds.
        public void SnapAmbient()
        {
            if (readbackPending) AsyncGPUReadback.WaitAllRequests();
            if (!haveAmbient) return;
            current = target;
            appliedOnce = false;
        }

        public void Refresh()
        {
            if (system == null || atmosphere == null || sunMoon == null || cmd == null || skyCubeShader == null) return;
            using (Marker.Auto())
            {
                double now = Time.realtimeSinceStartupAsDouble;
                float dt = Application.isPlaying ? Time.unscaledDeltaTime
                         : lastRealtime < 0.0 ? 0f : (float)(now - lastRealtime);
                lastRealtime = now;

                TimeOfDayQuality q = system.quality;
                AmbientSettings a = system.profile != null ? system.profile.ambient : DefaultAmbient;
                int size = q != null ? q.reflectionCubeSize : 128;
                EnsureCube(ref skyCube, "TOD SkyCube", size);

                // The cube follows the sky's LUT rebuilds, once each has finished cross-fading in
                // (reflections and ambient are far too soft for the fade itself to matter): one
                // render every few seconds for a slowly moving sun.
                bool skyChanged = !cubeValid || atmosphere.SkyViewBuilds != lastSkyBuilds ||
                                  atmosphere.MoonSkyViewBuilds != lastMoonBuilds;
                // The cube carries the pre-exposure it was rendered with (reflection intensities
                // are clamped to 1, so they cannot make up a rising exposure). Re-render when
                // exposure has drifted a quarter stop from it: only while the eye is adapting.
                bool exposureDrift = cubeValid && Mathf.Abs(Mathf.Log(ExposureController.PreExposure / cubeExposure, 2f)) > 0.25f;
                if (exposureDrift && !(skyChanged && atmosphere.SkyBlend >= 1f))
                {
                    RenderSkyCube();
                    ExposureCubeRenders++;
                }
                if (skyChanged && atmosphere.SkyBlend >= 1f)
                {
                    RenderSkyCube();
                    lastSkyBuilds = atmosphere.SkyViewBuilds;
                    lastMoonBuilds = atmosphere.MoonSkyViewBuilds;
                    cubeValid = true;
                    readbackWanted = true;
                }

                if (readbackWanted && !readbackPending)
                {
                    int mip = Mathf.Max(0, (int)Math.Round(Math.Log(skyCube.width / (double)ReadbackMipSize, 2)));
                    AsyncGPUReadback.RequestIntoNativeArray(ref readback, skyCube, mip, 0, ReadbackMipSize, 0, ReadbackMipSize,
                                                            0, 6, GraphicsFormat.R16G16B16A16_SFloat, onReadback);
                    readbackExposure = cubeExposure;
                    readbackPending = true;
                    readbackWanted = false;
                }
                // Edit mode has no player loop to deliver the callback promptly.
                if (!Application.isPlaying && readbackPending) AsyncGPUReadback.WaitAllRequests();

                BlendAmbient(dt, a);
                UpdateProbes(q, a);
            }
        }

        static readonly AmbientSettings DefaultAmbient = new AmbientSettings();

        // ---- sky cubemap ------------------------------------------------------------------

        void EnsureCube(ref RenderTexture cube, string cubeName, int size)
        {
            size = Mathf.ClosestPowerOfTwo(Mathf.Clamp(size, 16, 512));
            if (cube != null && cube.width == size && cube.IsCreated()) return;
            Release(ref cube);
            var desc = new RenderTextureDescriptor(size, size, GraphicsFormat.R16G16B16A16_SFloat, 0)
            {
                dimension = TextureDimension.Cube,
                useMipMap = true,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            cube = new RenderTexture(desc)
            {
                name = cubeName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            cube.Create();
            if (cube == skyCube) cubeValid = false;
            else sceneProbeSeeded = false;
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            CoreUtils.Destroy(rt);
            rt = null;
        }

        void RenderSkyCube()
        {
            cubeExposure = ExposureController.PreExposure;
            cmd.Clear();
            cmd.BeginSample("TimeOfDay.SkyCube");
            if (cubeMaterial == null) cubeMaterial = CoreUtils.CreateEngineMaterial(skyCubeShader);
            for (int face = 0; face < 6; face++)
            {
                cmd.SetRenderTarget(skyCube, 0, (CubemapFace)face);
                cmd.SetGlobalVector(CubeFaceId, new Vector4(face, skyCube.width, cubeExposure, 0f));
                cmd.DrawProcedural(Matrix4x4.identity, cubeMaterial, 0, MeshTopology.Triangles, 3);
            }
            // Box-filtered mips stand in for a GGX convolution: the sky is smooth enough that
            // the difference is not visible. Faces and mips together: ~0.14 ms on an RTX 3050
            // Laptop, once per sky rebuild. (A compute path writing all mips at once measured
            // slower: Unity cannot bind a cubemap as a UAV, and the 48 copies back cost more.)
            cmd.GenerateMips(skyCube);
            cmd.EndSample("TimeOfDay.SkyCube");
            Graphics.ExecuteCommandBuffer(cmd);
            CubeRenders++;
        }

        // ---- ambient (spherical harmonics) ------------------------------------------------

        /// Direction and solid angle of every texel of the readback mip, same face convention
        /// as SkyCube.shader (u right, v down, rows in memory order).
        void BuildTexelTable()
        {
            int n = ReadbackMipSize;
            texelDirections = new Vector3[6 * n * n];
            texelSolidAngles = new float[6 * n * n];
            int i = 0;
            for (int face = 0; face < 6; face++)
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++, i++)
                    {
                        float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                        texelDirections[i] = FaceDirection(face, u, v);
                        float d = 1f + u * u + v * v;
                        texelSolidAngles[i] = 4f / (n * n) / (d * Mathf.Sqrt(d));
                    }
        }

        public static Vector3 FaceDirection(int face, float u, float v)
        {
            Vector3 d = face switch
            {
                0 => new Vector3(1f, -v, -u),
                1 => new Vector3(-1f, -v, u),
                2 => new Vector3(u, 1f, v),
                3 => new Vector3(u, -1f, -v),
                4 => new Vector3(u, -v, 1f),
                _ => new Vector3(-u, -v, -1f),
            };
            return d.normalized;
        }

        void OnReadback(AsyncGPUReadbackRequest request)
        {
            readbackPending = false;
            if (request.hasError || !readback.IsCreated) return;
            // Back to scene units: the ambient is pre-exposed where it is applied.
            target = ProjectToSH(readback, texelDirections, texelSolidAngles) * (1f / readbackExposure);
            if (!haveAmbient) { current = target; haveAmbient = true; }
            AmbientUpdates++;
        }

        /// Projects radiance onto L2 spherical harmonics and convolves it with the clamped
        /// cosine, divided by pi: the result evaluates to what a white diffuse surface facing a
        /// direction shows, which is how Unity's ambient probe is stored.
        ///
        /// Unity's SphericalHarmonicsL2 holds coefficients premultiplied by the basis
        /// normalisation and evaluates the bare polynomials (1, y, z, x, xy, yz, 3z^2-1, xz,
        /// x^2-y^2): AddAmbientLight(white) gives c0 = 1. So each projection weight is the
        /// normalisation squared times the polynomial, times the band's cosine factor.
        public static SphericalHarmonicsL2 ProjectToSH(NativeArray<ushort> halfRgba, Vector3[] dirs, float[] solidAngles)
        {
            // Normalisation squared (K^2) times the zonal convolution with cos+ / pi (1, 2/3, 1/4).
            const float k0 = 0.0795775f;            // 1 / (4 pi)
            const float k1 = 0.2387324f * 2f / 3f;  // 3 / (4 pi)
            const float k2 = 1.1936621f * 0.25f;    // 15 / (4 pi)
            const float k6 = 0.0994718f * 0.25f;    // 5 / (16 pi)
            const float k8 = 0.2984155f * 0.25f;    // 15 / (16 pi)
            // Accumulate in a plain array: SphericalHarmonicsL2's indexer is a call per access,
            // and 27 of them per texel made this ~150 us in the editor.
            float[] acc = Scratch;
            Array.Clear(acc, 0, acc.Length);
            for (int i = 0; i < dirs.Length; i++)
            {
                float r = Mathf.HalfToFloat(halfRgba[i * 4]);
                float g = Mathf.HalfToFloat(halfRgba[i * 4 + 1]);
                float b = Mathf.HalfToFloat(halfRgba[i * 4 + 2]);
                Vector3 d = dirs[i];
                float w = solidAngles[i];
                float y0 = k0;
                float y1 = k1 * d.y, y2 = k1 * d.z, y3 = k1 * d.x;
                float y4 = k2 * d.x * d.y, y5 = k2 * d.y * d.z;
                float y6 = k6 * (3f * d.z * d.z - 1f);
                float y7 = k2 * d.x * d.z, y8 = k8 * (d.x * d.x - d.y * d.y);
                r *= w; g *= w; b *= w;
                acc[0] += r * y0; acc[1] += r * y1; acc[2] += r * y2; acc[3] += r * y3; acc[4] += r * y4;
                acc[5] += r * y5; acc[6] += r * y6; acc[7] += r * y7; acc[8] += r * y8;
                acc[9] += g * y0; acc[10] += g * y1; acc[11] += g * y2; acc[12] += g * y3; acc[13] += g * y4;
                acc[14] += g * y5; acc[15] += g * y6; acc[16] += g * y7; acc[17] += g * y8;
                acc[18] += b * y0; acc[19] += b * y1; acc[20] += b * y2; acc[21] += b * y3; acc[22] += b * y4;
                acc[23] += b * y5; acc[24] += b * y6; acc[25] += b * y7; acc[26] += b * y8;
            }

            var sh = new SphericalHarmonicsL2();
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 9; k++)
                    sh[c, k] = acc[c * 9 + k];
            return sh;
        }

        static readonly float[] Scratch = new float[27];

        void BlendAmbient(float dt, AmbientSettings a)
        {
            if (!haveAmbient) return;
            bool settled = current == target && appliedIntensity == a.intensity && appliedOnce;
            if (settled && appliedExposure == ExposureController.Version &&
                (!system.OwnsRenderSettings || RenderSettings.ambientProbe == Ambient * ExposureController.PreExposure)) return;

            float k = a.blendSeconds > 0f ? 1f - Mathf.Exp(-dt / a.blendSeconds) : 1f;
            current = current * (1f - k) + target * k;
            if (MaxDifference(current, target) < 1e-6f) current = target;   // converged: stop updating
            if (!settled)
            {
                Ambient = current * a.intensity;
                appliedIntensity = a.intensity;
                appliedOnce = true;
                AmbientVersion++;
            }
            appliedExposure = ExposureController.Version;

            if (!system.OwnsRenderSettings) return;
            if (RenderSettings.ambientMode != AmbientMode.Skybox) RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.ambientProbe = Ambient * ExposureController.PreExposure;
        }

        float appliedIntensity = -1f;
        int appliedExposure = -1;
        bool appliedOnce;

        static float MaxDifference(SphericalHarmonicsL2 a, SphericalHarmonicsL2 b)
        {
            float m = 0f;
            for (int c = 0; c < 3; c++)
                for (int i = 0; i < 9; i++)
                    m = Mathf.Max(m, Mathf.Abs(a[c, i] - b[c, i]));
            return m;
        }

        // ---- reflection probes ------------------------------------------------------------

        void UpdateProbes(TimeOfDayQuality q, AmbientSettings a)
        {
            Camera cam = system.ViewCamera;
            Vector3 eye = cam != null ? cam.transform.position : transform.position;

            if (skyProbe != null)
            {
                skyProbe.mode = ReflectionProbeMode.Custom;
                skyProbe.customBakedTexture = skyCube;
                skyProbe.importance = 0;
                skyProbe.size = Vector3.one * ProbeSize;
                skyProbe.intensity = a.reflectionIntensity * ExposureController.PreExposure / cubeExposure;
                skyProbe.transform.position = eye;
            }

            if (system.OwnsRenderSettings)
            {
                // Objects outside every probe fall back to the scene's default reflection.
                if (RenderSettings.defaultReflectionMode != DefaultReflectionMode.Custom)
                    RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
                if (RenderSettings.customReflectionTexture != skyCube) RenderSettings.customReflectionTexture = skyCube;
                // Clamped to 1 by Unity; the cube's own exposure keeps the ratio near 1.
                RenderSettings.reflectionIntensity = Mathf.Min(1f, a.reflectionIntensity * ExposureController.PreExposure / cubeExposure);
            }

            UpdateSceneProbe(q, a, eye);
        }

        /// High tier: a realtime probe that sees terrain. Seeded with the sky cube so it is never
        /// black, then re-rendered one face per frame when the sun has moved or the camera has
        /// travelled past the tier's thresholds.
        void UpdateSceneProbe(TimeOfDayQuality q, AmbientSettings a, Vector3 eye)
        {
            if (sceneProbe == null) return;
            bool wanted = q != null && q.sceneReflectionProbe && QualitySettings.realtimeReflectionProbes;
            if (sceneProbe.gameObject.activeSelf != wanted) sceneProbe.gameObject.SetActive(wanted);
            if (!wanted) return;

            EnsureCube(ref sceneCube, "TOD SceneCube", q.sceneProbeResolution);
            if (!sceneProbeSeeded)
            {
                for (int face = 0; face < 6; face++)
                    for (int mip = 0; mip < Mathf.Min(skyCube.mipmapCount, sceneCube.mipmapCount); mip++)
                        if (skyCube.width >> mip == sceneCube.width >> mip)
                            Graphics.CopyTexture(skyCube, face, mip, sceneCube, face, mip);
                sceneProbeSeeded = true;
                sceneProbeExposure = cubeExposure;
                sceneProbeRender = -1;
                sceneProbeTime = -1e9;
            }

            sceneProbe.mode = ReflectionProbeMode.Realtime;
            sceneProbe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            sceneProbe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
            sceneProbe.realtimeTexture = sceneCube;
            sceneProbe.resolution = sceneCube.width;
            sceneProbe.hdr = true;
            sceneProbe.cullingMask = q.sceneProbeCullingMask;
            sceneProbe.importance = 1;
            sceneProbe.size = Vector3.one * ProbeSize;
            // The probe's faces were rendered pre-exposed by the P of the time; rescale to now.
            sceneProbe.intensity = a.reflectionIntensity * ExposureController.PreExposure / sceneProbeExposure;
            sceneProbe.transform.position = eye;

            if (sceneProbeRender >= 0 && !sceneProbe.IsFinishedRendering(sceneProbeRender)) return;

            ref readonly CelestialState s = ref sunMoon.State;
            double now = Time.realtimeSinceStartupAsDouble;
            bool sunMoved = Mathf.Abs((float)s.SunElevation - sceneProbeSunElevation) > q.sceneProbeUpdateDegrees;
            bool travelled = CirrusFS.Geo.Double3.Distance(s.Observer.Ecef, sceneProbeEcef) > q.sceneProbeUpdateMeters;
            bool exposed = Mathf.Abs(Mathf.Log(ExposureController.PreExposure / sceneProbeExposure, 2f)) > 0.5f;
            if ((sunMoved || travelled || exposed || sceneProbeRender < 0) && now - sceneProbeTime > q.sceneProbeMinInterval)
            {
                sceneProbeRender = sceneProbe.RenderProbe(sceneCube);
                sceneProbeExposure = ExposureController.PreExposure;
                sceneProbeSunElevation = (float)s.SunElevation;
                sceneProbeEcef = s.Observer.Ecef;
                sceneProbeTime = now;
                SceneProbeRenders++;
            }
        }
    }
}
