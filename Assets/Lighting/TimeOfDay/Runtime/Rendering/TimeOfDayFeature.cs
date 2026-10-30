using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.Serialization;

namespace CirrusFS.TimeOfDay
{
    /// The time-of-day system's URP renderer feature: two small passes.
    ///
    ///  - Aerial perspective (after the skybox, before transparents): replaces Unity's fog. Each
    ///    opaque pixel's distance comes from the depth texture, the haze from the aerial-
    ///    perspective LUT, and the result is blended onto the colour target (dst * T + inscatter),
    ///    so the colour buffer is never copied. With the renderer's depth copy at After Opaques,
    ///    the depth texture is a copy URP makes anyway.
    ///  - Exposure meter (before post-processing, view camera only): the image's mean log
    ///    luminance into a 32x18 target that ExposureController reads back.
    ///
    /// Transparent shaders (glass, particles) are not covered by a full-screen pass; they apply
    /// the same haze themselves through TimeOfDayAerialPerspective.hlsl.
    public class TimeOfDayFeature : ScriptableRendererFeature
    {
        [FormerlySerializedAs("shader")]
        [SerializeField] Shader aerialPerspectiveShader;
        [SerializeField] Shader exposureMeterShader;
        public RenderPassEvent passEvent = RenderPassEvent.AfterRenderingSkybox;

        Material aerialMaterial, meterMaterial;
        AerialPerspectivePass aerialPass;
        ExposureMeterPass meterPass;

        /// Tells shaders with their own fallback haze (the ocean) that this feature is applying
        /// the real one, so they do not haze twice.
        static readonly int FogActiveId = Shader.PropertyToID("_TOD_FogActive");

        public override void Create()
        {
            aerialPass = new AerialPerspectivePass { renderPassEvent = passEvent };
            meterPass = new ExposureMeterPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            bool fog = AtmosphereRenderer.AerialPerspectiveReady && aerialPerspectiveShader != null;
            Shader.SetGlobalFloat(FogActiveId, fog ? 1f : 0f);
            if (fog)
            {
                if (aerialMaterial == null) aerialMaterial = CoreUtils.CreateEngineMaterial(aerialPerspectiveShader);
                aerialPass.material = aerialMaterial;
                aerialPass.ConfigureInput(ScriptableRenderPassInput.Depth);
                renderer.EnqueuePass(aerialPass);
            }

            if (ExposureController.MeterTarget != null && exposureMeterShader != null)
            {
                if (meterMaterial == null) meterMaterial = CoreUtils.CreateEngineMaterial(exposureMeterShader);
                meterPass.material = meterMaterial;
                renderer.EnqueuePass(meterPass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            Shader.SetGlobalFloat(FogActiveId, 0f);
            CoreUtils.Destroy(aerialMaterial);
            CoreUtils.Destroy(meterMaterial);
            aerialMaterial = meterMaterial = null;
        }

        class AerialPerspectivePass : ScriptableRenderPass
        {
            public Material material;

            class PassData
            {
                public Material material;
            }

            public AerialPerspectivePass()
            {
                profilingSampler = new ProfilingSampler("TimeOfDay.AerialPerspective");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var camera = frameData.Get<UniversalCameraData>();
                if (camera.cameraType == CameraType.Preview || !resources.cameraDepthTexture.IsValid()) return;

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("TimeOfDay.AerialPerspective", out var data, profilingSampler))
                {
                    data.material = material;
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, 0, MeshTopology.Triangles, 3));
                }
            }
        }

        class ExposureMeterPass : ScriptableRenderPass
        {
            public Material material;

            class PassData
            {
                public Material material;
                public TextureHandle source;
            }

            public ExposureMeterPass()
            {
                profilingSampler = new ProfilingSampler("TimeOfDay.ExposureMeter");
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                if (camera.camera != ExposureController.MeteringCamera || ExposureController.MeterTarget == null) return;
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return; // nothing HDR to meter

                TextureHandle target = renderGraph.ImportTexture(ExposureController.MeterTarget);
                using (var builder = renderGraph.AddRasterRenderPass<PassData>("TimeOfDay.ExposureMeter", out var data, profilingSampler))
                {
                    data.material = material;
                    data.source = resources.activeColorTexture;
                    builder.UseTexture(data.source, AccessFlags.Read);
                    builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);   // its output is read back on the CPU, not by a later pass
                    builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                        Blitter.BlitTexture(ctx.cmd, d.source, new Vector4(1f, 1f, 0f, 0f), d.material, 0));
                }
                ExposureController.MeterWritten();
            }
        }
    }
}
