using System;
using CirrusFS.Geo;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CirrusFS.TimeOfDay
{
    /// GPU side of the atmosphere: builds the LUTs and publishes the shader globals every sky,
    /// fog and lighting shader reads.
    ///
    /// LUT lifetimes:
    ///  - Transmittance (256x64) and multiple scattering (32x32) depend only on the atmosphere
    ///    parameters (profile + weather haze). The first build is immediate; later ones (a
    ///    visibility change) go into back buffers, the multiple-scattering LUT a few rows per
    ///    frame, and swap in when complete, so weather never causes a hitch.
    ///  - Sky view (per tier, e.g. 192x108) depends on the light's elevation and the viewer's
    ///    height, not on camera orientation or the light's azimuth. It is rebuilt when either
    ///    has moved past the tier's threshold and cross-faded from the previous one, so a slowly
    ///    moving sun costs one small dispatch every few seconds. A big jump (time scrub, spawn)
    ///    rebuilds at once with no fade.
    ///  - Aerial perspective (per tier, e.g. 32^3): the sky-view layout plus distance. Built
    ///    together with the sky view, so it has the same cadence and the same horizon.
    ///  - Moon sky view and moon aerial perspective: the same for moonlight, only while the moon
    ///    is up and the sun is not.
    [ExecuteAlways]
    [DefaultExecutionOrder(310)]
    public class AtmosphereRenderer : MonoBehaviour
    {
        public TimeOfDaySystem system;
        public SunMoonController sunMoon;

        [Header("Shaders")]
        public ComputeShader lutCompute;
        public Shader lutFragmentShader;

        [Header("Sky")]
        public Material skyMaterial;
        [Tooltip("Make the sky material the scene's skybox.")]
        public bool assignSkybox = true;

        public static readonly Vector2Int TransmittanceSize = new Vector2Int(256, 64);
        public static readonly Vector2Int MultiScatSize = new Vector2Int(32, 32);

        /// True while an enabled renderer has an aerial-perspective LUT ready and the profile
        /// wants fog. AerialPerspectiveFeature only draws when this is set.
        public static bool AerialPerspectiveReady { get; private set; }

        /// Sky-view ray-march sample count reaches its maximum at this ray length, km.
        const float MaxSamplesDistanceKm = 100f;

        // Build-side LUTs are 32-bit; the sky-view and aerial LUTs and a copy of transmittance
        // that every pixel samples are 16-bit (RGBA32F filtering runs at a fraction of the rate
        // and cost ~25% of the sky pass). Sky-view and aerial LUTs are stored pre-scaled so deep
        // twilight, ~1e-10 of daylight, still fits in a half float.
        RenderTexture transmittance, transmittanceHalf, multiScat;
        RenderTexture transmittanceNext, multiScatNext;
        RenderTexture skyView, skyViewPrev, moonSkyView;
        RenderTexture aerial, moonAerial;
        Material fragmentMaterial;
        CommandBuffer cmd;
        int kernelTransmittance, kernelMultiScat, kernelSkyView, kernelAerial;

        bool useCompute;
        GraphicsFormat format;
        Vector2Int skyViewSize;
        Vector3Int aerialSize;

        // Base LUT state: what the current textures were built from, and an in-flight rebuild.
        bool baseValid;
        int builtAtmosphereVersion = -1;
        int pendingVersion = -1;
        int pendingRow;
        AtmosphereParameters pendingParameters;
        bool baseSwapped;

        bool skyValid, moonSkyValid, aerialValid, moonAerialValid;
        float skyLightElevation, skyHeightKm, skyBlend = 1f;
        float skyScale = 1f, skyScalePrev = 1f, moonSkyLutScale = 1f;
        float moonLightElevation;
        float aerialDistanceKm = 400f;
        double lastRealtime = -1.0;

        static TimeOfDayQuality defaultQuality;
        static readonly ProfilerMarker Marker = new ProfilerMarker("TimeOfDay.Atmosphere");

        // ---- stats (debug overlay, tests) -----------------------------------------------

        public bool UsingCompute => useCompute;
        public int BaseLutBuilds { get; private set; }
        public int SkyViewBuilds { get; private set; }
        public int MoonSkyViewBuilds { get; private set; }
        public float SkyBlend => skyBlend;
        /// True while a weather/profile rebuild of the base LUTs is being spread over frames.
        public bool BaseRebuildInProgress => pendingVersion >= 0;
        public float AerialDistanceKm => aerialDistanceKm;
        public RenderTexture TransmittanceLut => transmittance;
        public RenderTexture MultiScatteringLut => multiScat;
        public RenderTexture SkyViewLut => skyView;
        public RenderTexture AerialLut => aerial;

        // ---- lifecycle --------------------------------------------------------------------

        void OnEnable()
        {
            if (system == null) system = GetComponentInParent<TimeOfDaySystem>();
            if (sunMoon == null) sunMoon = GetComponentInParent<SunMoonController>();
            cmd = new CommandBuffer { name = "TimeOfDay.AtmosphereLuts" };
            if (lutCompute != null)
            {
                kernelTransmittance = lutCompute.FindKernel("Transmittance");
                kernelMultiScat = lutCompute.FindKernel("MultiScattering");
                kernelSkyView = lutCompute.FindKernel("SkyView");
                kernelAerial = lutCompute.FindKernel("AerialPerspective");
            }
            lastRealtime = -1.0;
        }

        void OnDisable()
        {
            AerialPerspectiveReady = false;
            Shader.SetGlobalVector(Ids.AerialParams, Vector4.zero);
            ReleaseLuts();
            CoreUtils.Destroy(fragmentMaterial);
            fragmentMaterial = null;
            cmd?.Release();
            cmd = null;
        }

        void LateUpdate() => Refresh();

        /// Forces every LUT to be rebuilt, immediately, on the next refresh.
        public void Invalidate()
        {
            baseValid = false;
            pendingVersion = -1;
            skyValid = moonSkyValid = aerialValid = moonAerialValid = false;
        }

        /// Updates LUTs and globals now, rather than at the next LateUpdate. For tools that move
        /// the clock and need the sky in the same frame (scrubbing, debug overlay, tests).
        public void Refresh()
        {
            if (system == null || sunMoon == null || system.Atmosphere == null || cmd == null) return;

            using (Marker.Auto())
            {
                double now = Time.realtimeSinceStartupAsDouble;
                float dt = Application.isPlaying ? Time.unscaledDeltaTime
                         : lastRealtime < 0.0 ? 0f : (float)(now - lastRealtime);
                lastRealtime = now;

                ref readonly CelestialState s = ref sunMoon.State;
                TimeOfDayQuality q = system.quality != null ? system.quality : DefaultQuality;
                if (!EnsureResources(q)) return;

                AtmosphereModel model = system.Atmosphere;
                float heightKm = (float)Math.Max(0.0, s.Observer.Height) * 0.001f;

                cmd.Clear();
                PushAtmosphere(model.Parameters, heightKm);
                bool work = UpdateBaseLuts(q, model, heightKm);
                work |= UpdateSkyViews(q, in s, heightKm, dt);

                if (work)
                {
                    // The fragment path binds the 32-bit LUT globally while building; leave the
                    // 16-bit sampling copy bound for every other shader.
                    cmd.SetGlobalTexture(Ids.TransmittanceLut, transmittanceHalf);
                    Graphics.ExecuteCommandBuffer(cmd);
                }
                PublishSkyGlobals(in s);
                PublishAerialGlobals(q, in s);

                if (system.OwnsRenderSettings)
                {
                    if (assignSkybox && skyMaterial != null && RenderSettings.skybox != skyMaterial)
                        RenderSettings.skybox = skyMaterial;
                    // Unity's own fog would add a second, unrelated haze on top.
                    if (RenderSettings.fog) RenderSettings.fog = false;
                }
            }
        }

        static TimeOfDayQuality DefaultQuality
        {
            get
            {
                if (defaultQuality == null)
                {
                    defaultQuality = ScriptableObject.CreateInstance<TimeOfDayQuality>();
                    defaultQuality.hideFlags = HideFlags.HideAndDontSave;
                }
                return defaultQuality;
            }
        }

        // ---- resources --------------------------------------------------------------------

        bool EnsureResources(TimeOfDayQuality q)
        {
            bool wantCompute = q.useComputeShaders && lutCompute != null && SystemInfo.supportsComputeShaders;
            if (!wantCompute && lutFragmentShader == null) return false;
            Vector2Int wantSky = Vector2Int.Max(q.skyViewLutSize, new Vector2Int(16, 16));
            Vector3Int wantAerial = Vector3Int.Max(q.aerialLutSize, new Vector3Int(8, 8, 8));

            if (transmittance != null && transmittance.IsCreated() && wantCompute == useCompute &&
                wantSky == skyViewSize && wantAerial == aerialSize)
                return true;

            ReleaseLuts();
            useCompute = wantCompute;
            skyViewSize = wantSky;
            aerialSize = wantAerial;
            format = PickFormat(useCompute);

            if (!useCompute && fragmentMaterial == null)
                fragmentMaterial = CoreUtils.CreateEngineMaterial(lutFragmentShader);

            const GraphicsFormat half = GraphicsFormat.R16G16B16A16_SFloat;
            transmittance = CreateLut("TOD Transmittance", TransmittanceSize, format);
            transmittanceNext = CreateLut("TOD Transmittance (rebuild)", TransmittanceSize, format);
            transmittanceHalf = CreateLut("TOD Transmittance (sampling)", TransmittanceSize, half);
            multiScat = CreateLut("TOD MultiScattering", MultiScatSize, format);
            multiScatNext = CreateLut("TOD MultiScattering (rebuild)", MultiScatSize, format);
            skyView = CreateLut("TOD SkyView", skyViewSize, half);
            skyViewPrev = CreateLut("TOD SkyView (previous)", skyViewSize, half);
            moonSkyView = CreateLut("TOD MoonSkyView", skyViewSize, half);
            aerial = CreateLut3D("TOD AerialPerspective", aerialSize, half);
            moonAerial = CreateLut3D("TOD MoonAerialPerspective", aerialSize, half);
            baseValid = false;
            pendingVersion = -1;
            return true;
        }

        /// 32-bit float where it can be filtered: the LUTs hold values down to 1e-7 (deep
        /// twilight, transmittance at the horizon) that half floats would band. 16-bit otherwise.
        static GraphicsFormat PickFormat(bool compute)
        {
            const GraphicsFormat full = GraphicsFormat.R32G32B32A32_SFloat;
            bool ok = SystemInfo.IsFormatSupported(full, GraphicsFormatUsage.Linear) &&
                      SystemInfo.IsFormatSupported(full, GraphicsFormatUsage.Render) &&
                      (!compute || SystemInfo.IsFormatSupported(full, GraphicsFormatUsage.LoadStore));
            return ok ? full : GraphicsFormat.R16G16B16A16_SFloat;
        }

        RenderTexture CreateLut(string lutName, Vector2Int size, GraphicsFormat lutFormat) =>
            Create(lutName, new RenderTextureDescriptor(size.x, size.y, lutFormat, 0));

        RenderTexture CreateLut3D(string lutName, Vector3Int size, GraphicsFormat lutFormat) =>
            Create(lutName, new RenderTextureDescriptor(size.x, size.y, lutFormat, 0)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = size.z,
            });

        RenderTexture Create(string lutName, RenderTextureDescriptor desc)
        {
            desc.enableRandomWrite = useCompute;
            desc.useMipMap = false;
            desc.msaaSamples = 1;
            var rt = new RenderTexture(desc)
            {
                name = lutName,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        void ReleaseLuts()
        {
            Release(ref transmittance);
            Release(ref transmittanceNext);
            Release(ref transmittanceHalf);
            Release(ref multiScat);
            Release(ref multiScatNext);
            Release(ref skyView);
            Release(ref skyViewPrev);
            Release(ref moonSkyView);
            Release(ref aerial);
            Release(ref moonAerial);
            baseValid = false;
            skyValid = moonSkyValid = aerialValid = moonAerialValid = false;
        }

        static void Release(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release();
            CoreUtils.Destroy(rt);
            rt = null;
        }

        // ---- base LUTs --------------------------------------------------------------------

        const int PassTransmittance = 0, PassMultiScat = 1, PassSkyView = 2, PassAerial = 3;

        /// First build: both LUTs now. Later builds (the parameters changed): transmittance and
        /// the first rows of multiple scattering into the back buffers, the rest over the next
        /// frames, then swap. The rebuild keeps the parameters it started with, so a visibility
        /// that keeps changing cannot restart it forever; the next one starts when it completes.
        bool UpdateBaseLuts(TimeOfDayQuality q, AtmosphereModel model, float heightKm)
        {
            if (!baseValid)
            {
                cmd.BeginSample("TimeOfDay.BaseLuts");
                RunPass(PassTransmittance, transmittance, transmittance, multiScat);
                RunMultiScatRows(multiScat, transmittance, 0, MultiScatSize.y);
                cmd.Blit(transmittance, transmittanceHalf);
                cmd.EndSample("TimeOfDay.BaseLuts");
                baseValid = true;
                builtAtmosphereVersion = model.Version;
                pendingVersion = -1;
                skyValid = moonSkyValid = aerialValid = moonAerialValid = false;
                BaseLutBuilds++;
                return true;
            }

            if (pendingVersion < 0)
            {
                if (model.Version == builtAtmosphereVersion) return false;
                pendingVersion = model.Version;
                pendingParameters = model.Parameters;
                pendingRow = 0;
            }

            // The rebuild's own parameters for these passes, then the current ones again for
            // anything else built this frame.
            PushAtmosphere(pendingParameters, heightKm, globalsToo: false);
            cmd.BeginSample("TimeOfDay.BaseLutsSliced");
            if (pendingRow == 0) RunPass(PassTransmittance, transmittanceNext, transmittanceNext, multiScatNext);
            int rows = useCompute ? Mathf.CeilToInt((float)MultiScatSize.y / Mathf.Max(1, q.baseLutRebuildFrames))
                                  : MultiScatSize.y;
            RunMultiScatRows(multiScatNext, transmittanceNext, pendingRow, rows);
            pendingRow += rows;
            cmd.EndSample("TimeOfDay.BaseLutsSliced");
            PushAtmosphere(model.Parameters, heightKm, globalsToo: false);

            if (pendingRow >= MultiScatSize.y)
            {
                (transmittance, transmittanceNext) = (transmittanceNext, transmittance);
                (multiScat, multiScatNext) = (multiScatNext, multiScat);
                cmd.Blit(transmittance, transmittanceHalf);
                builtAtmosphereVersion = pendingVersion;
                pendingVersion = -1;
                baseSwapped = true;   // sky and aerial LUTs rebuild on the new base (with a fade)
                BaseLutBuilds++;
            }
            return true;
        }

        void RunMultiScatRows(RenderTexture target, RenderTexture transmittanceSource, int firstRow, int rows)
        {
            rows = Mathf.Min(rows, target.height - firstRow);
            if (rows <= 0) return;
            if (useCompute)
            {
                cmd.SetComputeVectorParam(lutCompute, Ids.OutputSize, new Vector4(target.width, target.height, firstRow, 0f));
                cmd.SetComputeTextureParam(lutCompute, kernelMultiScat, Ids.Output, target);
                cmd.SetComputeTextureParam(lutCompute, kernelMultiScat, Ids.TransmittanceLut, transmittanceSource);
                cmd.DispatchCompute(lutCompute, kernelMultiScat, target.width, rows, 1); // a group per texel
            }
            else
            {
                RunPass(PassMultiScat, target, transmittanceSource, multiScat);
            }
        }

        // ---- sky view and aerial perspective ----------------------------------------------

        /// Scale a sky-view LUT is stored with: roughly 1 / (its brightest texel), estimated from
        /// the light's elevation. Fitted to measured LUT values, which fall about 10x per 2.3 deg
        /// of depression (6.6e-4 at -4.2, 1.7e-10 at -19.6 deg). Only has to be right to a couple
        /// of orders of magnitude: it keeps values inside half-float range, nothing more.
        static float SkyViewStorageScale(float lightElevation)
        {
            if (lightElevation >= 0f) return 1f;
            float log10Peak = -0.4f - 0.43f * -lightElevation;
            return Mathf.Clamp(Mathf.Pow(10f, -log10Peak), 1f, 1e12f);
        }

        /// Builds a sky-view LUT and the matching aerial-perspective LUT for one light.
        void BuildSkyAndAerial(RenderTexture sky, RenderTexture aerialTarget, float lightElevation, float heightKm,
                               TimeOfDayQuality q, float storageScale, bool withAerial)
        {
            float mu = Mathf.Sin(lightElevation * Mathf.Deg2Rad);
            Push(Ids.SkyViewLight, new Vector4(storageScale, 0f, 0f, mu));
            Push(Ids.SkyViewSize, SkyViewSizeVector(heightKm));
            Push(Ids.SkyViewSamples, new Vector4(Mathf.Max(4, q.skyViewSamples.x),
                                                 Mathf.Max(q.skyViewSamples.x, q.skyViewSamples.y),
                                                 MaxSamplesDistanceKm, 0f));
            cmd.BeginSample("TimeOfDay.SkyViewLut");
            RunPass(PassSkyView, sky, transmittance, multiScat);
            cmd.EndSample("TimeOfDay.SkyViewLut");

            if (!withAerial) return;
            Push(Ids.AerialSize, new Vector4(aerialSize.x, aerialSize.y, aerialSize.z, aerialDistanceKm));
            Push(Ids.AerialSamples, new Vector4(q.aerialSamples, 0f, 0f, 0f));
            cmd.BeginSample("TimeOfDay.AerialLut");
            RunPass(PassAerial, aerialTarget, transmittance, multiScat);
            cmd.EndSample("TimeOfDay.AerialLut");
        }

        void RunPass(int pass, RenderTexture target, RenderTexture transmittanceSource, RenderTexture multiScatSource)
        {
            if (useCompute)
            {
                int kernel = pass == PassTransmittance ? kernelTransmittance
                           : pass == PassMultiScat ? kernelMultiScat
                           : pass == PassSkyView ? kernelSkyView : kernelAerial;
                cmd.SetComputeVectorParam(lutCompute, Ids.OutputSize, new Vector4(target.width, target.height, 0f, 0f));
                cmd.SetComputeTextureParam(lutCompute, kernel, pass == PassAerial ? Ids.Output3D : Ids.Output, target);
                if (pass != PassTransmittance) cmd.SetComputeTextureParam(lutCompute, kernel, Ids.TransmittanceLut, transmittanceSource);
                if (pass >= PassSkyView) cmd.SetComputeTextureParam(lutCompute, kernel, Ids.MultiScatLut, multiScatSource);
                if (pass == PassAerial)
                    cmd.DispatchCompute(lutCompute, kernel, (aerialSize.x + 7) / 8, (aerialSize.y + 7) / 8, 1); // a thread per column
                else
                    cmd.DispatchCompute(lutCompute, kernel, (target.width + 7) / 8, (target.height + 7) / 8, 1);
                return;
            }

            if (pass != PassTransmittance) cmd.SetGlobalTexture(Ids.TransmittanceLut, transmittanceSource);
            if (pass >= PassSkyView) cmd.SetGlobalTexture(Ids.MultiScatLut, multiScatSource);
            if (pass == PassAerial)
            {
                for (int slice = 0; slice < aerialSize.z; slice++)
                {
                    cmd.SetGlobalVector(Ids.OutputSize, new Vector4(aerialSize.x, aerialSize.y, slice, 0f));
                    cmd.SetRenderTarget(target, 0, CubemapFace.Unknown, slice);
                    cmd.DrawProcedural(Matrix4x4.identity, fragmentMaterial, pass, MeshTopology.Triangles, 3);
                }
                return;
            }
            cmd.SetGlobalVector(Ids.OutputSize, new Vector4(target.width, target.height, 0f, 0f));
            cmd.SetRenderTarget(target);
            cmd.DrawProcedural(Matrix4x4.identity, fragmentMaterial, pass, MeshTopology.Triangles, 3);
        }

        /// Decides whether the sky-view and aerial LUTs need rebuilding this frame. Returns true
        /// if it queued any work.
        bool UpdateSkyViews(TimeOfDayQuality q, in CelestialState s, float heightKm, float dt)
        {
            bool work = false;
            bool aerialWanted = AerialWanted;
            float sunElevation = (float)s.SunElevation;
            float heightChange = Mathf.Abs(heightKm - skyHeightKm) / Mathf.Max(skyHeightKm, 0.05f);
            float sunChange = Mathf.Abs(sunElevation - skyLightElevation);

            // The aerial LUT is only built while fog is wanted; turning fog on must build it.
            bool aerialMissing = aerialWanted && !aerialValid;
            bool need = !skyValid || baseSwapped || aerialMissing ||
                        sunChange > q.skyViewUpdateDegrees || heightChange > q.skyViewUpdateHeight;
            // A weather step is small, and the fog (which has no cross-fade) must not wait for a
            // sky fade still running from the previous step.
            bool snap = !skyValid || q.skyViewBlendSeconds <= 0f || aerialMissing ||
                        (baseSwapped && skyBlend < 1f) ||
                        sunChange > 4f * q.skyViewUpdateDegrees || heightChange > 4f * q.skyViewUpdateHeight;
            bool rebuilt = false;

            if (need && (snap || skyBlend >= 1f))
            {
                if (!snap)
                {
                    (skyView, skyViewPrev) = (skyViewPrev, skyView);
                    skyScalePrev = skyScale;
                }
                skyScale = SkyViewStorageScale(sunElevation);
                aerialDistanceKm = AerialDistance(heightKm);
                BuildSkyAndAerial(skyView, aerial, sunElevation, heightKm, q, skyScale, aerialWanted);
                aerialValid = aerialWanted;
                skyLightElevation = sunElevation;
                skyHeightKm = heightKm;
                skyValid = true;
                skyBlend = snap ? 1f : 0f;
                baseSwapped = false;
                SkyViewBuilds++;
                rebuilt = true;
                work = true;
            }
            skyBlend = q.skyViewBlendSeconds > 0f ? Mathf.Min(1f, skyBlend + dt / q.skyViewBlendSeconds) : 1f;

            // Moonlight: only while it can be seen. Shares the sun LUTs' build height and aerial
            // distance so one mapping fits all of them.
            if (MoonSkyRelevant(q, in s))
            {
                float moonElevation = (float)s.MoonElevation;
                if (!moonSkyValid || rebuilt || Mathf.Abs(moonElevation - moonLightElevation) > q.skyViewUpdateDegrees)
                {
                    moonSkyLutScale = SkyViewStorageScale(moonElevation);
                    BuildSkyAndAerial(moonSkyView, moonAerial, moonElevation, skyHeightKm, q, moonSkyLutScale,
                                      aerialWanted && q.moonlitAerial);
                    moonAerialValid = aerialWanted && q.moonlitAerial;
                    moonLightElevation = moonElevation;
                    moonSkyValid = true;
                    MoonSkyViewBuilds++;
                    work = true;
                }
            }
            // Leave the sun LUTs' parameters bound for anything reading them this frame.
            if (work) Push(Ids.SkyViewSize, SkyViewSizeVector(skyHeightKm));
            return work;
        }

        bool AerialWanted => system.profile == null || system.profile.fog.aerialPerspective;

        /// Distance covered by the aerial LUT's last slice: as far as anything can be seen from
        /// this height (the horizon, plus the horizon of 10 km terrain behind it), like the far
        /// plane GeoCameraRange sets. Slices are sqrt-spaced, so the first is ~0.4 km at sea level.
        static float AerialDistance(float heightKm)
        {
            double far = (GeoMath.HorizonDistance(heightKm * 1000.0) + GeoMath.HorizonDistance(10000.0)) * 1.05;
            return Mathf.Clamp((float)(far * 0.001), 50f, 5000f);
        }

        /// Sky-view LUT size, build height and the angle from the nadir up to the horizon at
        /// that height. Computed here in double: in float, r^2 - R^2 for a viewer a few metres
        /// up is pure rounding error, and every sky pixel would otherwise pay for the acos.
        Vector4 SkyViewSizeVector(float heightKm)
        {
            double bottom = system.Atmosphere.Parameters.bottomRadius * 0.001;
            double h = Math.Max(heightKm, 5e-4);
            double beta = Math.Acos(Math.Sqrt(h * (2.0 * bottom + h)) / (bottom + h));
            return new Vector4(skyViewSize.x, skyViewSize.y, heightKm, (float)beta);
        }

        static bool MoonSkyRelevant(TimeOfDayQuality q, in CelestialState s) =>
            q.moonlitSky && s.MoonElevation > -10.0 && s.SunElevation < 10.0;

        // ---- globals ----------------------------------------------------------------------

        void Push(int id, Vector4 value)
        {
            cmd.SetGlobalVector(id, value);
            if (useCompute) cmd.SetComputeVectorParam(lutCompute, id, value);
        }

        /// Atmosphere parameters in km and 1/km, for the LUT passes (through the command
        /// buffer) and, unless told otherwise, every other shader (globally).
        void PushAtmosphere(AtmosphereParameters p, float heightKm, bool globalsToo = true)
        {
            float bottom = p.bottomRadius * 0.001f;
            Set(Ids.Radii, new Vector4(bottom, p.TopRadius * 0.001f, heightKm, bottom + heightKm), globalsToo);
            Set(Ids.Rayleigh, new Vector4(p.rayleighScattering.x * 1000f, p.rayleighScattering.y * 1000f,
                                          p.rayleighScattering.z * 1000f, p.rayleighScaleHeight * 0.001f), globalsToo);
            Set(Ids.Mie, new Vector4(p.mieScattering * 1000f, p.MieExtinction * 1000f, p.mieScaleHeight * 0.001f, p.mieAnisotropy), globalsToo);
            Set(Ids.Ozone, new Vector4(p.ozoneAbsorption.x * 1000f, p.ozoneAbsorption.y * 1000f, p.ozoneAbsorption.z * 1000f, 0f), globalsToo);
            Set(Ids.OzoneLayer, new Vector4(p.ozoneCenterHeight * 0.001f, p.ozoneHalfWidth * 0.001f, 0f, 0f), globalsToo);
            Set(Ids.Haze, new Vector4(p.hazeExtinction * 1000f, p.hazeExtinction * p.hazeAlbedo * 1000f,
                                      p.hazeTopHeight * 0.001f, p.hazeTransition * 0.001f), globalsToo);
            Set(Ids.HazePhase, new Vector4(p.hazeAnisotropy, 0f, 0f, 0f), globalsToo);
            Set(Ids.GroundAlbedo, (Vector4)p.groundAlbedo, globalsToo);
            Set(Ids.TransmittanceSize, new Vector4(TransmittanceSize.x, TransmittanceSize.y, 1f / TransmittanceSize.x, 1f / TransmittanceSize.y), globalsToo);
            Set(Ids.MultiScatSize, new Vector4(MultiScatSize.x, MultiScatSize.y, 1f / MultiScatSize.x, 1f / MultiScatSize.y), globalsToo);
        }

        void Set(int id, Vector4 value, bool globalsToo)
        {
            if (globalsToo) Shader.SetGlobalVector(id, value);
            Push(id, value);
        }

        void PublishSkyGlobals(in CelestialState s)
        {
            SkySettings sky = system.profile != null ? system.profile.sky : DefaultSky;

            Shader.SetGlobalTexture(Ids.TransmittanceLut, transmittanceHalf);
            Shader.SetGlobalTexture(Ids.MultiScatLut, multiScat);
            Shader.SetGlobalTexture(Ids.SkyViewLut, skyView);
            Shader.SetGlobalTexture(Ids.SkyViewLutPrev, skyViewPrev);
            Shader.SetGlobalTexture(Ids.MoonSkyViewLut, moonSkyView);
            Shader.SetGlobalVector(Ids.SkyViewSize, SkyViewSizeVector(skyHeightKm));
            Shader.SetGlobalVector(Ids.SkyBlend, new Vector4(skyBlend, 1f / skyScale, 1f / skyScalePrev, 0f));

            Shader.SetGlobalVector(Ids.Up, s.Observer.UpUnity);

            float sunRadius = (float)(s.SunAngularRadius * GeoMath.Deg2Rad) * sky.sunDiskScale;
            float moonRadius = (float)(s.MoonAngularRadius * GeoMath.Deg2Rad) * sky.moonDiskScale;
            Shader.SetGlobalVector(Ids.SunDir, new Vector4(s.SunDirection.x, s.SunDirection.y, s.SunDirection.z, sunRadius));
            Shader.SetGlobalVector(Ids.MoonDir, new Vector4(s.MoonDirection.x, s.MoonDirection.y, s.MoonDirection.z, moonRadius));

            Vector3 sunSky = s.SunExoIlluminance * Mathf.PI;
            Vector3 moonSky = s.MoonExoIlluminance * (Mathf.PI / moonSkyLutScale);
            bool moonSkyOn = moonSkyValid && MoonSkyRelevant(system.quality != null ? system.quality : DefaultQuality, in s);
            Shader.SetGlobalVector(Ids.SunSkyScale, new Vector4(sunSky.x, sunSky.y, sunSky.z, 0f));
            Shader.SetGlobalVector(Ids.MoonSkyScale, new Vector4(moonSky.x, moonSky.y, moonSky.z, moonSkyOn ? 1f : 0f));

            // Disc radiance: the sun's illuminance spread over its (limb-darkened) disc, so an
            // enlarged disc delivers the same light. Unity's light intensity I corresponds to an
            // illuminance of pi * I, hence I / (radius^2 (1 - u/3)).
            float u = sky.sunLimbDarkening;
            float sunIntensity = s.SunExoIlluminance.x;
            float disc = Mathf.Min(sky.sunDiskMaxRadiance, sunIntensity / Mathf.Max(1e-8f, sunRadius * sunRadius * (1f - u / 3f)));
            Shader.SetGlobalVector(Ids.SunDiskRadiance, new Vector4(disc, disc, disc, u));

            float earthshine = sky.moonEarthshine * sunIntensity * (float)(1.0 - s.MoonIlluminatedFraction);
            Shader.SetGlobalVector(Ids.MoonParams, new Vector4(sunIntensity, earthshine, 1f, 0f));

            Shader.SetGlobalVector(Ids.Stars, new Vector4(sky.stars ? sky.starBrightness : 0f, sky.starDensity,
                                                          sky.starFadeLuminance, sky.starTwinkle));
            Shader.SetGlobalVector(Ids.StarsContrast, new Vector4(sky.starContrast, 0f, 0f, 0f));
            Shader.SetGlobalVector(Ids.NightSky, (Vector4)sky.nightSkyRadiance);
            Shader.SetGlobalMatrix(Ids.UnityToCelestial, UnityToCelestial(in s));
        }

        void PublishAerialGlobals(TimeOfDayQuality q, in CelestialState s)
        {
            bool on = AerialWanted && aerialValid;
            AerialPerspectiveReady = on;

            // Same units as the sky: LUT values are per unit light, stored pre-scaled.
            float sunScale = s.SunExoIlluminance.x * Mathf.PI / skyScale;
            bool moonOn = on && moonAerialValid && MoonSkyRelevant(q, in s);
            Vector3 moon = s.MoonExoIlluminance * (Mathf.PI / moonSkyLutScale);

            Shader.SetGlobalTexture(Ids.AerialLut, aerial);
            Shader.SetGlobalTexture(Ids.MoonAerialLut, moonAerial);
            Shader.SetGlobalVector(Ids.AerialSize, new Vector4(aerialSize.x, aerialSize.y, aerialSize.z, aerialDistanceKm));
            Shader.SetGlobalVector(Ids.AerialParams, new Vector4(on ? 1f : 0f, sunScale, moonOn ? 1f : 0f, 0f));
            Shader.SetGlobalVector(Ids.AerialMoonScale, new Vector4(moon.x, moon.y, moon.z, 0f));
        }

        /// Rotation from Unity directions to the celestial (true equator and equinox of date)
        /// frame: Unity axes -> ECEF through the observer frame, then ECEF -> celestial by a
        /// rotation of GAST about the pole.
        static Matrix4x4 UnityToCelestial(in CelestialState s)
        {
            double g = s.Gast * GeoMath.Deg2Rad;
            double c = Math.Cos(g), sn = Math.Sin(g);
            Vector3 Rot(Double3 e) => new Vector3((float)(e.x * c - e.y * sn), (float)(e.x * sn + e.y * c), (float)e.z);

            var m = Matrix4x4.identity;
            m.SetColumn(0, Rot(s.Observer.AxisX));
            m.SetColumn(1, Rot(s.Observer.AxisY));
            m.SetColumn(2, Rot(s.Observer.AxisZ));
            return m;
        }

        static readonly SkySettings DefaultSky = new SkySettings();

        static class Ids
        {
            public static readonly int Radii = Shader.PropertyToID("_TOD_Radii");
            public static readonly int Rayleigh = Shader.PropertyToID("_TOD_Rayleigh");
            public static readonly int Mie = Shader.PropertyToID("_TOD_Mie");
            public static readonly int Ozone = Shader.PropertyToID("_TOD_Ozone");
            public static readonly int OzoneLayer = Shader.PropertyToID("_TOD_OzoneLayer");
            public static readonly int Haze = Shader.PropertyToID("_TOD_Haze");
            public static readonly int HazePhase = Shader.PropertyToID("_TOD_HazePhase");
            public static readonly int GroundAlbedo = Shader.PropertyToID("_TOD_GroundAlbedo");
            public static readonly int TransmittanceSize = Shader.PropertyToID("_TOD_TransmittanceSize");
            public static readonly int MultiScatSize = Shader.PropertyToID("_TOD_MultiScatSize");
            public static readonly int SkyViewSize = Shader.PropertyToID("_TOD_SkyViewSize");
            public static readonly int SkyViewLight = Shader.PropertyToID("_TOD_SkyViewLight");
            public static readonly int SkyViewSamples = Shader.PropertyToID("_TOD_SkyViewSamples");
            public static readonly int AerialSize = Shader.PropertyToID("_TOD_AerialSize");
            public static readonly int AerialSamples = Shader.PropertyToID("_TOD_AerialSamples");
            public static readonly int AerialParams = Shader.PropertyToID("_TOD_AerialParams");
            public static readonly int AerialMoonScale = Shader.PropertyToID("_TOD_AerialMoonScale");
            public static readonly int TransmittanceLut = Shader.PropertyToID("_TOD_TransmittanceLut");
            public static readonly int MultiScatLut = Shader.PropertyToID("_TOD_MultiScatLut");
            public static readonly int SkyViewLut = Shader.PropertyToID("_TOD_SkyViewLut");
            public static readonly int SkyViewLutPrev = Shader.PropertyToID("_TOD_SkyViewLutPrev");
            public static readonly int MoonSkyViewLut = Shader.PropertyToID("_TOD_MoonSkyViewLut");
            public static readonly int AerialLut = Shader.PropertyToID("_TOD_AerialLut");
            public static readonly int MoonAerialLut = Shader.PropertyToID("_TOD_MoonAerialLut");
            public static readonly int Output = Shader.PropertyToID("_TOD_Output");
            public static readonly int Output3D = Shader.PropertyToID("_TOD_Output3D");
            public static readonly int OutputSize = Shader.PropertyToID("_TOD_OutputSize");
            public static readonly int Up = Shader.PropertyToID("_TOD_Up");
            public static readonly int SunDir = Shader.PropertyToID("_TOD_SunDir");
            public static readonly int MoonDir = Shader.PropertyToID("_TOD_MoonDir");
            public static readonly int SunSkyScale = Shader.PropertyToID("_TOD_SunSkyScale");
            public static readonly int MoonSkyScale = Shader.PropertyToID("_TOD_MoonSkyScale");
            public static readonly int SunDiskRadiance = Shader.PropertyToID("_TOD_SunDiskRadiance");
            public static readonly int MoonParams = Shader.PropertyToID("_TOD_MoonParams");
            public static readonly int Stars = Shader.PropertyToID("_TOD_Stars");
            public static readonly int StarsContrast = Shader.PropertyToID("_TOD_StarsContrast");
            public static readonly int NightSky = Shader.PropertyToID("_TOD_NightSky");
            public static readonly int SkyBlend = Shader.PropertyToID("_TOD_SkyBlend");
            public static readonly int UnityToCelestial = Shader.PropertyToID("_TOD_UnityToCelestial");
        }
    }
}
