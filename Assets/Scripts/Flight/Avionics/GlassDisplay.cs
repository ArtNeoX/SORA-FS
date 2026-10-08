using UnityEngine;

namespace CirrusFS.Flight.Avionics
{
    /// Base for a cockpit display unit. Sits on the empty that marks the display's centre in
    /// the cockpit model (+Z into the panel, so the glass faces -Z towards the pilot), renders
    /// the subclass's symbology into a RenderTexture, adds a tweakable glow halo, and shows the
    /// result on an emissive quad child.
    ///
    /// Runs in edit mode too, so size, placement and glow can be tuned without flying.
    [ExecuteAlways]
    public abstract class GlassDisplay : MonoBehaviour
    {
        const int PassPrefilter = 1, PassBlur = 2, PassComposite = 3;
        const string ScreenName = "Screen";

        static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        static readonly int GlowTexId = Shader.PropertyToID("_GlowTex");
        static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        static readonly int BlurDirId = Shader.PropertyToID("_BlurDir");
        static readonly int ThresholdId = Shader.PropertyToID("_GlowThreshold");
        static readonly int GlowIntensityId = Shader.PropertyToID("_GlowIntensity");

        [Header("Screen")]
        [Tooltip("Quad the display is shown on. Created as a child called 'Screen' if empty.")]
        public MeshRenderer screen;
        [Tooltip("Material using CirrusFS/GlassDisplayScreen. Shared; the texture is set per renderer.")]
        public Material screenMaterial;
        [Tooltip("Visible display area, metres (A320 DU: about 0.16 x 0.16).")]
        public Vector2 screenSize = new Vector2(0.16f, 0.16f);
        [Tooltip("Lifts the glass off the empty towards the pilot, metres, to clear the panel mesh.")]
        public float surfaceOffset = 0.002f;

        [Header("Rendering")]
        [Range(256, 2048)] public int resolution = 1024;
        [Tooltip("MSAA samples for the symbology: 1, 2, 4 or 8.")]
        public int antiAliasing = 4;
        [Tooltip("Display refresh rate, Hz. 0 = every frame.")]
        [Min(0f)] public float refreshRate;
        public Shader canvasShader;
        [Tooltip("Display font. B612 (Airbus' own cockpit font, OFL) is the closest match if imported.")]
        public Font font;
        public FontStyle fontStyle = FontStyle.Normal;
        [Range(0.5f, 1.2f)] public float textWidthScale = 0.85f;

        [Header("Glow")]
        [Tooltip("Emitted brightness. 1 = display white at any scene exposure; above 1 also feeds HDRP bloom.")]
        [Min(0f)] public float brightness = 1.3f;
        [Tooltip("Strength of the halo around the symbology. 0 = off.")]
        [Min(0f)] public float glowIntensity = 0.7f;
        [Tooltip("Halo spread, in half-resolution texels per blur tap.")]
        [Range(0.25f, 8f)] public float glowRadius = 1.5f;
        [Tooltip("Only colour brighter than this glows; keeps the sky and tapes from fogging over.")]
        [Range(0f, 0.95f)] public float glowThreshold = 0.35f;
        [Range(1, 4)] public int glowIterations = 2;

        DisplayPainter painter;
        Material canvasMat;
        RenderTexture canvas, glowA, glowB, output;
        MaterialPropertyBlock mpb;
        float nextRender;
        bool screenDirty = true, painterDirty;

        public RenderTexture Output => output;

        /// Region of design space that fills the texture, y down.
        protected abstract Rect DesignRect { get; }

        /// Gather sensor data and run filters. Called every frame.
        protected abstract void Sample(float dt);

        /// Paint one frame of symbology.
        protected abstract void Draw(DisplayPainter p);

        protected virtual void OnEnable()
        {
            screenDirty = true;
            if (!Application.isPlaying) RenderNow();
        }

        protected virtual void OnDisable() => Release();

        protected virtual void OnValidate()
        {
            screenDirty = true;
            painterDirty = true;
            nextRender = 0f;
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        protected virtual void Update()
        {
            Sample(Application.isPlaying ? Time.deltaTime : 0f);
        }

        void LateUpdate()
        {
            if (Application.isPlaying && refreshRate > 0f)
            {
                if (Time.time < nextRender) return;
                nextRender = Time.time + 1f / refreshRate;
            }
            RenderNow();
        }

        /// Renders immediately, whatever the refresh rate.
        public void RenderNow()
        {
            if (!EnsureResources()) return;
            if (screenDirty) UpdateScreen();
            // Edit mode has no reliable Update before a render.
            if (!Application.isPlaying) Sample(0f);

            painter.textWidthScale = textWidthScale;
            painter.Begin(canvas, DesignRect, Color.black);
            try
            {
                Draw(painter);
            }
            finally
            {
                painter.End();
            }
            Composite();

            if (screen)
            {
                mpb ??= new MaterialPropertyBlock();
                screen.GetPropertyBlock(mpb);
                mpb.SetTexture(MainTexId, output);
                mpb.SetFloat(BrightnessId, brightness);
                screen.SetPropertyBlock(mpb);
            }
        }

        void Composite()
        {
            // Blit leaves its destination active; put back whatever was there.
            var prev = RenderTexture.active;
            try
            {
                Glow();
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

        void Glow()
        {
            if (glowIntensity <= 0f)
            {
                Graphics.Blit(canvas, output);
                return;
            }
            canvasMat.SetFloat(ThresholdId, glowThreshold);
            Graphics.Blit(canvas, glowA, canvasMat, PassPrefilter);
            for (int i = 0; i < glowIterations; i++)
            {
                // Each iteration widens the kernel so the halo has a long soft tail.
                float r = glowRadius * (i + 1);
                canvasMat.SetVector(BlurDirId, new Vector4(r, 0f));
                Graphics.Blit(glowA, glowB, canvasMat, PassBlur);
                canvasMat.SetVector(BlurDirId, new Vector4(0f, r));
                Graphics.Blit(glowB, glowA, canvasMat, PassBlur);
            }
            canvasMat.SetTexture(GlowTexId, glowA);
            canvasMat.SetFloat(GlowIntensityId, glowIntensity);
            Graphics.Blit(canvas, output, canvasMat, PassComposite);
        }

        bool EnsureResources()
        {
            if (!canvasShader) canvasShader = Shader.Find("Hidden/CirrusFS/GlassDisplayCanvas");
            if (!canvasShader) return false;
            if (!font) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            int res = Mathf.Clamp(resolution, 256, 2048);
            int aa = antiAliasing >= 8 ? 8 : antiAliasing >= 4 ? 4 : antiAliasing >= 2 ? 2 : 1;
            if (canvas && (canvas.width != res || canvas.antiAliasing != aa)) Release();

            if (painterDirty)
            {
                painter?.Dispose();
                painter = null;
                painterDirty = false;
            }
            if (!canvasMat) canvasMat = new Material(canvasShader) { hideFlags = HideFlags.HideAndDontSave };
            painter ??= new DisplayPainter(canvasShader, font, fontStyle);

            if (!canvas)
            {
                canvas = NewRT(res, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB, aa, false);
                glowA = NewRT(res / 2, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, 1, false);
                glowB = NewRT(res / 2, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, 1, false);
                output = NewRT(res, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear, 1, true);
            }
            return true;
        }

        RenderTexture NewRT(int size, RenderTextureFormat fmt, RenderTextureReadWrite rw, int aa, bool mips)
        {
            var rt = new RenderTexture(size, size, 0, fmt, rw)
            {
                name = name + " display",
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = aa,
                useMipMap = mips,
                autoGenerateMips = mips,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear,
                anisoLevel = mips ? 8 : 0,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        void Release()
        {
            painter?.Dispose();
            painter = null;
            DestroySafe(canvasMat);
            foreach (var rt in new[] { canvas, glowA, glowB, output })
            {
                if (!rt) continue;
                if (RenderTexture.active == rt) RenderTexture.active = null;
                rt.Release();
                DestroySafe(rt);
            }
            canvas = glowA = glowB = output = null;
        }

        static void DestroySafe(Object o)
        {
            if (!o) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        /// Creates the screen quad if missing and fits it to screenSize / surfaceOffset.
        void UpdateScreen()
        {
            screenDirty = false;
            if (!screen)
            {
                var existing = transform.Find(ScreenName);
                if (existing) screen = existing.GetComponent<MeshRenderer>();
            }
            if (!screen)
            {
                var go = new GameObject(ScreenName, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(transform, false);
                go.GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
                screen = go.GetComponent<MeshRenderer>();
            }

            screen.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            screen.receiveShadows = false;
            if (screenMaterial && screen.sharedMaterial != screenMaterial) screen.sharedMaterial = screenMaterial;

            // The built-in quad faces -Z with UV (0,0) bottom-left as seen from the pilot.
            var t = screen.transform;
            t.localPosition = new Vector3(0f, 0f, -surfaceOffset);
            t.localRotation = Quaternion.identity;
            t.localScale = new Vector3(screenSize.x, screenSize.y, 1f);
        }
    }
}
