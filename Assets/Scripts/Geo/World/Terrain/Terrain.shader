// Terrain chunks on the WGS84 globe (URP), fed by GlobeTerrain.
//
// Land class: every chunk carries a small R8 texture of class ids (one or two texels per
// data cell at the finest level, coarser above it, plus an apron of _TerrainClassApron
// texels from the neighbours); _ClassCells says how its texels sit on the data cells. Each
// class's share of a quadratic B-spline over the 3x3 nearest data cells gives a smooth field;
// the shares raised to _BorderCurve and normalised are the blend weights, so borders run
// along that field's contours (curves, not the cell grid) and a lone cell or a staircase no
// longer shows as squares at any distance. The lookup position is warped by Earth-anchored
// noise whose wavelength follows the cell size on screen (from derivatives), so borders
// wander at every LOD level without stippling. Colours come from the manifest's class list
// (global _TerrainPalette, linear RGB; alpha = water).
//
// Ground textures: a class may also have a tiling albedo (GlobeTerrain.classTextures), one
// layer of the global _TerrainClassAlbedo array. Each of the four cells is textured before
// the blend, so textures fade into each other exactly as the colours do. A texture either
// keeps the class colour and adds only its pattern (texture / its own average), or replaces
// it. A second, larger, transposed lookup mixed in by noise breaks up the repetition.
//
// Detail: tileable noise at 4096, 256 and 32 m, on coordinates anchored to the Earth (a
// Mercator grid, wrapped per chunk on the CPU in double), so it never slides with the
// floating origin. Ground textures use the same coordinates with a whole number of repeats
// per 4096 m, so they are seamless too. Steep slopes turn to rock (optionally textured).
//
// Lighting: main light with shadows plus the SH ambient, both pre-exposed by the time-of-day
// system. Distance haze is the time-of-day fog pass; the simple fallback haze here only runs
// without it (_TOD_FogActive = 0), as on the ocean.
//
// Water: the terrain draws its own water from the water class (sea, lakes, rivers), so the
// shoreline is wherever the class says, and the ocean sphere steps aside inside the tiles
// GlobeTerrain draws (Ocean/OceanCells.hlsl). The exporter's sea floor (-5 m) is raised in
// the vertex shader to _SeaSurfaceHeight (just above the ocean sphere's h = 0, which runs a
// couple of metres under each tile edge), so the sea inside tiles is flat at sea level and
// meets the open ocean. Water is shaded like the ocean (same wave maps, colours and sun, from
// the _Sea* globals of OceanSphere). Water-class ground that climbs a steep coast above sea
// level (where the land-cover's coastline and the coarser height grid disagree) shows the land
// around it, or rock, instead of water running up the hill. Land is never changed.
Shader "CirrusFS/Terrain"
{
    Properties
    {
        [Header(Colour)]
        _Brightness ("Albedo scale", Range(0, 2)) = 1
        _Saturation ("Saturation", Range(0, 1.5)) = 1.1

        [Header(Water)]
        _SeaSurfaceHeight ("Sea surface height (m)", Float) = 0.05
        _CoastClimbHeight ("Steep coastal water shows land above (m)", Float) = 1.5
        _CoastClimbSlope ("... on slopes steeper than (deg)", Range(0, 30)) = 3

        [Header(Class blending)]
        _BorderCurve ("Border sharpness (1 = soft blur)", Range(1, 12)) = 4
        _BlendWarp ("Border warp (cells, at most 2)", Range(0, 4)) = 1.5

        [Header(Ground textures)]
        _TextureAntiTiling ("Anti-tiling mix", Range(0, 1)) = 0.5

        [Header(Detail)]
        _DetailStrength ("Detail strength", Range(0, 1)) = 0.35
        _DetailFade ("Fine detail fade distance (m)", Float) = 5000

        [Header(Rock)]
        _RockColor ("Rock on steep slopes", Color) = (0.30, 0.28, 0.26, 1)
        _RockSlopeStart ("Rock starts at slope (deg)", Range(0, 90)) = 30
        _RockSlopeEnd ("All rock at slope (deg)", Range(0, 90)) = 45

        [Header(Airports)]
        _RunwayAsphalt ("Runway asphalt", Color) = (0.27, 0.27, 0.28, 1)
        _RunwayConcrete ("Runway concrete", Color) = (0.60, 0.59, 0.56, 1)
        _RunwayGrassTint ("Grass runway tint on the frame ground (linear)", Vector) = (1.05, 1.12, 0.85, 0)
        _RunwayDirt ("Runway dirt", Color) = (0.52, 0.42, 0.32, 1)
        _RunwayGravel ("Runway gravel", Color) = (0.56, 0.53, 0.49, 1)
        _RunwaySand ("Runway sand", Color) = (0.74, 0.68, 0.55, 1)
        _RunwaySnow ("Runway snow", Color) = (0.92, 0.93, 0.95, 1)
        _RunwayPaint ("Runway paint (white)", Color) = (0.86, 0.86, 0.84, 1)
        _RunwayPaintYellow ("Runway paint (yellow)", Color) = (0.93, 0.74, 0.12, 1)
        _RunwayRubber ("Rubber deposits", Color) = (0.13, 0.13, 0.135, 1)
        _RunwayRubberStrength ("Rubber strength", Range(0, 1)) = 0.92
        _FrameEdgeNoise ("Frame edge waviness (m)", Float) = 14
        _FrameEdgeSoftness ("Frame edge softness (m)", Float) = 6

        [Header(Fallback haze)]
        _HazeColor ("Haze", Color) = (0.62, 0.72, 0.84, 1)
        _HazeDistance ("Haze e-folding distance (m)", Float) = 140000
        _AtmosphereHeight ("Atmosphere scale height (m)", Float) = 9000
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half _Brightness, _Saturation;
            float _SeaSurfaceHeight, _CoastClimbHeight, _CoastClimbSlope;
            float _BorderCurve, _BlendWarp;
            float _TextureAntiTiling;
            float _DetailStrength, _DetailFade;
            half4 _RockColor;
            float _RockSlopeStart, _RockSlopeEnd;
            half4 _HazeColor;
            float _HazeDistance, _AtmosphereHeight;
            half4 _RunwayAsphalt, _RunwayConcrete, _RunwayGrassTint, _RunwayDirt, _RunwayGravel,
                  _RunwaySand, _RunwaySnow, _RunwayPaint, _RunwayPaintYellow, _RunwayRubber;
            half _RunwayRubberStrength;
            float _FrameEdgeNoise, _FrameEdgeSoftness;
        CBUFFER_END

        float _TerrainSeaHeight;    // exporter's sea floor, metres (GlobeTerrain)

        // The exporter's sea floor raised to the sea surface (every pass, so shadows and depth
        // agree with what is drawn). Sea vertices are flat, so their normal is up.
        void LiftSea(inout float3 positionOS, inout float3 normalOS, inout float height)
        {
            if (abs(height - _TerrainSeaHeight) < 0.3)
            {
                positionOS.y += _SeaSurfaceHeight - height;
                normalOS = float3(0, 1, 0);
                height = _SeaSurfaceHeight;
            }
        }
        ENDHLSL

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            // Skirts hang below the chunk borders; draw both sides so a gap is always filled.
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_ClassTex);                                     // per chunk
            float4 _ClassCells;    // per chunk: x data cells per texel (<= 1), yz cell offset of the chunk corner
            TEXTURE2D(_TerrainPalette);                               // 256 x 1, global
            TEXTURE2D(_TerrainDetail); SAMPLER(sampler_TerrainDetail); // global
            float _TerrainClassTexSize;                               // texels + 2 apron, global
            float _TerrainClassApron;                                 // apron texels per side
            TEXTURE2D_ARRAY(_TerrainClassAlbedo); SAMPLER(sampler_TerrainClassAlbedo); // global
            // per class id: x layer + 1 (0 = flat colour), y repeats per 4096 m, z strength,
            // w 1 = keep the class colour; tint linear RGB. Zero (unset) means flat colour.
            float4 _TerrainClassParams[256];
            float4 _TerrainClassTint[256];
            float4 _TerrainRockParams, _TerrainRockTint;               // steep slopes, same layout
            float _OceanCameraHeight;                                 // from OceanSphere
            float _TOD_FogActive;

            // The ocean's look, from OceanSphere (same maps and mapping as Ocean.shader)
            float4x4 _OceanUnityToEcef;
            float3 _OceanUvOriginA, _OceanUvOriginB;
            TEXTURE2D(_SeaWaveNormalA); SAMPLER(sampler_SeaWaveNormalA);
            TEXTURE2D(_SeaWaveNormalB); SAMPLER(sampler_SeaWaveNormalB);
            float4 _SeaWaveA, _SeaWaveB;      // tile size (m), strength, fade distance (m)
            half4 _SeaDeepColor, _SeaScatterColor, _SeaSkyFallback;
            float4 _SeaSun;                   // glint power, glint intensity, sheen power, sheen intensity
            float _SeaReflectionStrength;

            // Airports, per chunk (GlobeAirports / AirportChunkPacker), in chunk-local metres
            // (x east, z north). Unset (zero counts) on chunks without airports.
            float4 _AirportCounts;          // x runways, y frame float4s used
            float4 _AirportRunways[80];     // 5 per runway: centre.xz dir.xz | half length, half width, surface, flags | displaced, designators | runoff | marking class, rubber, stripes, seed
            float4 _AirportFrames[96];      // per polygon: centre.xz, radius, edges + 16 x class | then per edge: normal.xz, offset

            #include "RunwayPaint.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uvClass : TEXCOORD0;
                float3 uvDetail : TEXCOORD1;    // xy detail coordinates, z height (m)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 upWS : TEXCOORD2;
                float2 uvClass : TEXCOORD3;
                float3 uvDetail : TEXCOORD4;
                float2 positionOS : TEXCOORD5;  // chunk-local east, north (airports)
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                float3 positionOS = input.positionOS.xyz;
                float3 normalOS = input.normalOS;
                float height = input.uvDetail.z;
                LiftSea(positionOS, normalOS, height);
                VertexPositionInputs p = GetVertexPositionInputs(positionOS);
                o.positionCS = p.positionCS;
                o.positionOS = positionOS.xz;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(normalOS);
                // chunk-local +Y is the ellipsoid normal at the chunk centre
                o.upWS = TransformObjectToWorldDir(float3(0, 1, 0));
                o.uvClass = input.uvClass;
                o.uvDetail = float3(input.uvDetail.xy, height);
                return o;
            }

            // ---- water, shaded like Ocean.shader --------------------------------------------
            half3 SeaNormalSample(TEXTURE2D_PARAM(tex, samp), float2 uv) { return UnpackNormal(SAMPLE_TEXTURE2D(tex, samp, uv)); }

            float3 SeaTriplanar(TEXTURE2D_PARAM(tex, samp), float3 pTiles, float3 w)
            {
                half3 tx = SeaNormalSample(TEXTURE2D_ARGS(tex, samp), pTiles.yz);
                half3 ty = SeaNormalSample(TEXTURE2D_ARGS(tex, samp), pTiles.zx);
                half3 tz = SeaNormalSample(TEXTURE2D_ARGS(tex, samp), pTiles.xy);
                return w.x * float3(0, tx.x, tx.y) + w.y * float3(ty.y, 0, ty.x) + w.z * float3(tz.x, tz.y, 0);
            }

            // Lit water colour at a point, before haze: Fresnel between the deep body colour and
            // the sky reflection, plus glint and sheen. geomN = the water's flat normal (up).
            half3 SeaColor(float3 positionWS, float3 geomN, float3 V, float dist, Light sun)
            {
                float3x3 toEcef = (float3x3)_OceanUnityToEcef;
                float3 pEcef = mul(toEcef, positionWS);
                float3 nEcef = mul(toEcef, geomN);
                float3 w = pow(abs(nEcef), 8.0);
                w /= (w.x + w.y + w.z);

                float fadeA = _SeaWaveA.y * exp(-dist / max(_SeaWaveA.z, 1.0));
                float fadeB = _SeaWaveB.y * exp(-dist / max(_SeaWaveB.z, 1.0));
                float3 perturb = 0;
                if (fadeA > 0.002)
                    perturb += fadeA * SeaTriplanar(TEXTURE2D_ARGS(_SeaWaveNormalA, sampler_SeaWaveNormalA),
                                                    pEcef / max(_SeaWaveA.x, 0.01) + _OceanUvOriginA, w);
                if (fadeB > 0.002)
                    perturb += fadeB * SeaTriplanar(TEXTURE2D_ARGS(_SeaWaveNormalB, sampler_SeaWaveNormalB),
                                                    pEcef / max(_SeaWaveB.x, 0.01) + _OceanUvOriginB, w);
                float3 N = normalize(mul(transpose(toEcef), normalize(nEcef + perturb)));

                float3 L = sun.direction;
                half3 sunColor = sun.color;
                half shadow = sun.shadowAttenuation;
                half day = saturate(dot(geomN, L) * 5.0 + 0.15);
                half NdotV = saturate(dot(N, V));
                half fresnel = 0.02 + 0.98 * pow(1.0 - NdotV, 5.0);
                float3 R = reflect(-V, N);
                R = dot(R, geomN) < 0 ? R - 2.0 * dot(R, geomN) * geomN : R;
                half space = saturate(log10(max(_OceanCameraHeight, 1.0) / 20000.0) / 2.0);
                half3 sky = GlossyEnvironmentReflection(R, 0.08h, 1.0h);
                sky = (dot(sky, 1) > 0.001 ? sky : _SeaSkyFallback.rgb) * _SeaReflectionStrength * day * (1.0 - 0.85 * space);
                half waveLight = saturate(0.55 + 2.2 * (dot(N, L) - dot(geomN, L)) + 0.45 * saturate(dot(geomN, L)));
                half3 deepLight = SampleSH(geomN) + sunColor * shadow * saturate(dot(geomN, L));
                half3 body = _SeaDeepColor.rgb * deepLight * (0.8 + 0.4 * waveLight) +
                             _SeaScatterColor.rgb * waveLight * shadow * sunColor * (1.0 - 0.4 * space);
                half3 color = lerp(body, sky, fresnel);
                float3 H = normalize(L + V);
                half NdotH = saturate(dot(N, H));
                half glint = pow(NdotH, _SeaSun.x) * _SeaSun.y + pow(NdotH, _SeaSun.z) * _SeaSun.w * (1.0 - 0.8 * space);
                color += glint * fresnel * 4.0 * sunColor * shadow * saturate(dot(geomN, L) * 10.0);
                return color;
            }

            // Quadratic B-spline weights of cells -1, 0, +1 for an offset u (-0.5..0.5) from the
            // nearest cell centre. Smooth (C1) and only 3x3 lookups.
            float3 BSpline2(float u)
            {
                float a = 0.5 - u, b = 0.5 + u;
                return float3(0.5 * a * a, 0.75 - u * u, 0.5 * b * b);
            }

            // class texture size of this chunk (coarse chunks above the tiles use smaller ones)
            float ClassTexSize() { return _ClassCells.w > 0.0 ? _ClassCells.w : _TerrainClassTexSize; }

            uint ClassId(int2 texel)
            {
                int size = (int)ClassTexSize();
                texel = clamp(texel, 0, size - 1);
                return (uint)round(LOAD_TEXTURE2D(_ClassTex, texel).r * 255.0);
            }

            // Shared by every textured lookup in one pixel: Earth-anchored coordinates (one unit
            // = 4096 Mercator metres), their screen derivatives, and the anti-tiling mix.
            struct GroundUv { float2 uv, dx, dy; half macro; };

            half3 GroundTexture(half3 base, float4 p, half3 tint, GroundUv g)
            {
                float layer = p.x - 1.0;
                float r1 = p.y;
                float r2 = max(1.0, round(r1 * 0.2));   // whole repeats too, so still seamless
                half3 a = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TerrainClassAlbedo, sampler_TerrainClassAlbedo,
                                                      g.uv * r1, layer, g.dx * r1, g.dy * r1).rgb;
                half3 b = SAMPLE_TEXTURE2D_ARRAY_GRAD(_TerrainClassAlbedo, sampler_TerrainClassAlbedo,
                                                      g.uv.yx * r2, layer, g.dx.yx * r2, g.dy.yx * r2).rgb;
                half3 tex = lerp(a, b, g.macro);
                if (p.w > 0.5)
                {
                    // keep the class colour: the texture's pattern relative to its own average
                    half3 average = SAMPLE_TEXTURE2D_ARRAY_LOD(_TerrainClassAlbedo, sampler_TerrainClassAlbedo,
                                                               float2(0.5, 0.5), layer, 16).rgb;
                    tex = base * tex / max(average, 0.02);
                }
                return lerp(base, tex * tint, p.z);
            }

            half3 ClassAlbedo(uint id, half3 base, GroundUv g)
            {
                float4 p = _TerrainClassParams[id];
                half3 albedo = base;
                [branch] if (p.x > 0.5) albedo = GroundTexture(base, p, _TerrainClassTint[id].rgb, g);
                return albedo;
            }

            // ---- airports ---------------------------------------------------------------
            // Edge anti-aliasing must be anisotropic: on a low approach a pixel covers ~1 m across
            // the runway but 50 m or more along the view, so the edge width is the screen
            // footprint measured along the distance field's gradient (|g.dp/dx| + |g.dp/dy|),
            // not the pixel's largest extent.
            float FootprintAlong(float2 gradient, float2 dpx, float2 dpy)
            {
                return abs(dot(gradient, dpx)) + abs(dot(gradient, dpy));
            }

            // Nearest frame polygon: signed distance in metres (negative inside), its class, and
            // the gradient of the distance (the deciding edge's normal).
            // Polygons further than 80 m (by their bounding circle) are not looked at closely.
            void AirportFrame(float2 p, out float sd, out uint frameClass, out float2 gradient)
            {
                sd = 1e6;
                frameClass = 0xFFFF;
                gradient = float2(1, 0);
                int used = (int)_AirportCounts.y;
                int k = 0;
                [loop] for (int guard = 0; guard < 24 && k < used; guard++)
                {
                    float4 header = _AirportFrames[k];
                    int edges = (int)fmod(header.w + 0.25, 16.0);
                    float nearest = length(p - header.xy) - header.z;
                    if (nearest < min(sd, 80.0))
                    {
                        float d = -1e6;
                        float2 n = float2(1, 0);
                        [loop] for (int e = 1; e <= edges; e++)
                        {
                            float4 edge = _AirportFrames[k + e];
                            float de = dot(edge.xy, p) - edge.z;
                            if (de > d) { d = de; n = edge.xy; }
                        }
                        if (d < sd)
                        {
                            sd = d;
                            gradient = n;
                            frameClass = (uint)floor((header.w + 0.25) / 16.0);
                        }
                    }
                    k += 1 + edges;
                }
            }

            // Nearest runway: signed distance to its paved rectangle (metres, runoff areas beyond
            // the ends included), its surface and index, and the runway-local coordinates
            // (x along from the le end, y across from the centreline, left positive).
            void AirportRunway(float2 p, out float sd, out uint surface, out float2 local, out float2 gradient,
                               out int index)
            {
                sd = 1e6;
                surface = 0;
                local = 0;
                gradient = float2(1, 0);
                index = 0;
                int count = (int)_AirportCounts.x;
                [loop] for (int r = 0; r < count; r++)
                {
                    float4 a = _AirportRunways[5 * r];
                    float4 b = _AirportRunways[5 * r + 1];
                    float4 ro = _AirportRunways[5 * r + 3];
                    float2 along = a.zw, across = float2(-a.w, a.z);
                    float2 q = p - a.xy;
                    float2 v = float2(dot(q, along), dot(q, across));
                    float pre = ro.x + ro.y, post = ro.z + ro.w;
                    float2 vb = float2(v.x - 0.5 * (post - pre), v.y);
                    float2 dd = abs(vb) - float2(b.x + 0.5 * (pre + post), b.y);
                    float d = length(max(dd, 0.0)) + min(max(dd.x, dd.y), 0.0);
                    if (d < sd)
                    {
                        sd = d;
                        surface = (uint)(b.z + 0.5);
                        local = float2(v.x + b.x, v.y);
                        index = r;
                        // gradient of the box distance, in the runway's axes, then the chunk's
                        float2 o = max(dd, 0.0);
                        float2 gl = (o.x > 0.0 || o.y > 0.0) ? o / max(length(o), 1e-4)
                                                             : (dd.x > dd.y ? float2(1, 0) : float2(0, 1));
                        gl *= float2(vb.x < 0.0 ? -1.0 : 1.0, vb.y < 0.0 ? -1.0 : 1.0);
                        gradient = gl.x * along + gl.y * across;
                    }
                }
            }

            half3 RunwayAlbedo(uint surface, half3 frameAlbedo)
            {
                if (surface == 1) return _RunwayConcrete.rgb;
                if (surface == 2) return frameAlbedo * _RunwayGrassTint.rgb;
                if (surface == 3) return _RunwayDirt.rgb;
                if (surface == 4) return _RunwayGravel.rgb;
                if (surface == 5) return _RunwaySand.rgb;
                if (surface == 6) return _RunwaySnow.rgb;
                return _RunwayAsphalt.rgb;    // asphalt, other, unknown
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 N = normalize(i.normalWS);
                float3 up = normalize(i.upWS);
                float3 toCamera = _WorldSpaceCameraPos - i.positionWS;
                float dist = length(toCamera);
                float3 V = toCamera / max(dist, 1e-4);

                // ---- detail noise (Earth-anchored) ---------------------------------------
                half4 nLarge = SAMPLE_TEXTURE2D(_TerrainDetail, sampler_TerrainDetail, i.uvDetail.xy);
                half4 nMid = SAMPLE_TEXTURE2D(_TerrainDetail, sampler_TerrainDetail, i.uvDetail.xy * 16.0);
                half4 nFine = SAMPLE_TEXTURE2D(_TerrainDetail, sampler_TerrainDetail, i.uvDetail.xy * 128.0);
                half fineFade = saturate(1.0 - dist / _DetailFade);

                // ---- land class: four-texel blend with curved, warped borders -----------
                // Texel size in metres, from how fast each coordinate changes on screen. It is
                // ~30 m at the finest level, 185 m for a 600/deg world folder and up to
                // ~870 m on a whole-tile chunk. The warp takes the noise octaves whose
                // wavelength is near four texels: shorter ones break borders into a stipple,
                // longer ones only slide the whole grid and leave its squares.
                float detailRate = length(ddx(i.uvDetail.xy)) + length(ddy(i.uvDetail.xy));
                float classRate = length(ddx(i.uvClass)) + length(ddy(i.uvClass));
                float cellsPerTexel = _ClassCells.x > 0.0 ? _ClassCells.x : 1.0;
                float cellMetres = detailRate * 4096.0 / max(classRate * ClassTexSize() * cellsPerTexel, 1e-6);
                half4 nMacro = SAMPLE_TEXTURE2D(_TerrainDetail, sampler_TerrainDetail, i.uvDetail.xy * 4.0);
                float4 octave = log2(float4(4096.0, 1024.0, 256.0, 32.0) / (4.0 * max(cellMetres, 1.0)));
                octave = exp(-0.5 * octave * octave);
                octave.w *= fineFade;
                float2 noise = (nLarge.xy - 0.5) * octave.x + (nMacro.xy - 0.5) * octave.y
                             + (nMid.xy - 0.5) * octave.z + (nFine.zw - 0.5) * octave.w;
                float2 warp = noise * (1.5 / max(octave.x + octave.y + octave.z + octave.w, 1e-3)) * _BlendWarp;
                // the 3x3 lookup below reaches 1.5 cells out, plus the warp: stay in the apron
                float warpLimit = max(_TerrainClassApron * cellsPerTexel - 2.0, 0.0);
                warp *= min(1.0, warpLimit / max(length(warp), 1e-4));

                // Each class's share of a quadratic B-spline over the 3x3 nearest data cells:
                // a smooth field, so shapes follow its contours instead of the cell grid. A
                // lone cell becomes a small round dot, a 2x2 group a round blob, a staircase a
                // smooth diagonal. The lookup runs on the data's own cells (finer chunk texels
                // hold each cell once or twice, unevenly), reading each cell at its centre.
                // Up to four classes per pixel (a fifth is dropped).
                float2 q = i.uvClass * ClassTexSize() - _TerrainClassApron;   // texels from the chunk corner
                float2 t = q * cellsPerTexel + _ClassCells.yz + warp;               // cells from a cell edge
                float2 cell = floor(t);
                float3 wx = BSpline2(t.x - cell.x - 0.5), wy = BSpline2(t.y - cell.y - 0.5);
                uint4 ids = uint4(0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF);
                float4 share = 0.0;
                [unroll] for (int y = 0; y < 3; y++)
                {
                    [unroll] for (int x = 0; x < 3; x++)
                    {
                        float2 centre = (cell + float2(x - 1, y - 1) + 0.5 - _ClassCells.yz) / cellsPerTexel;
                        uint id = ClassId(int2(floor(centre)) + (int)_TerrainClassApron);
                        float w = wx[x] * wy[y];
                        if (id == ids.x) share.x += w;
                        else if (id == ids.y) share.y += w;
                        else if (id == ids.z) share.z += w;
                        else if (id == ids.w) share.w += w;
                        else if (ids.x == 0xFFFF) { ids.x = id; share.x = w; }
                        else if (ids.y == 0xFFFF) { ids.y = id; share.y = w; }
                        else if (ids.z == 0xFFFF) { ids.z = id; share.z = w; }
                        else if (ids.w == 0xFFFF) { ids.w = id; share.w = w; }
                    }
                }
                // Curved borders: raising the shares to a power sharpens the fade to a band
                // around the contour where two classes' shares are equal (1 = plain blur).
                float4 e = pow(max(share, 1e-6), _BorderCurve) * float4(ids != 0xFFFF);
                e /= max(e.x + e.y + e.z + e.w, 1e-6);

                uint4 pid = min(ids, 255u);
                half4 p0 = LOAD_TEXTURE2D(_TerrainPalette, int2(pid.x, 0));
                half4 p1 = LOAD_TEXTURE2D(_TerrainPalette, int2(pid.y, 0));
                half4 p2 = LOAD_TEXTURE2D(_TerrainPalette, int2(pid.z, 0));
                half4 p3 = LOAD_TEXTURE2D(_TerrainPalette, int2(pid.w, 0));

                GroundUv g;
                g.uv = i.uvDetail.xy;
                g.dx = ddx(g.uv);
                g.dy = ddy(g.uv);
                g.macro = smoothstep(0.35, 0.65, nLarge.g * 0.5 + nMid.r * 0.5) * _TextureAntiTiling;

                // texture only the classes that show (usually just one)
                half3 a0 = ClassAlbedo(pid.x, p0.rgb, g);
                half3 a1 = a0, a2 = a0, a3 = a0;
                [branch] if (e.y > 0.002) a1 = ClassAlbedo(pid.y, p1.rgb, g);
                [branch] if (e.z > 0.002) a2 = ClassAlbedo(pid.z, p2.rgb, g);
                [branch] if (e.w > 0.002) a3 = ClassAlbedo(pid.w, p3.rgb, g);

                half3 albedo = a0 * e.x + a1 * e.y + a2 * e.z + a3 * e.w;
                half4 isWater = half4(p0.a, p1.a, p2.a, p3.a);
                half water = dot(e, isWater);

                // The ground under the water's share of the blend: the land classes around it,
                // or rock where there are none. It shows where water-class ground climbs a steep
                // coast (below), and keeps the land colour from fading toward the water palette.
                half slope = degrees(acos(saturate(dot(N, up))));
                [branch] if (water > 0.002)
                {
                    half4 landE = e * (1.0 - isWater);
                    half landSum = landE.x + landE.y + landE.z + landE.w;
                    half3 waterAlbedo = a0 * (e.x * isWater.x) + a1 * (e.y * isWater.y) +
                                        a2 * (e.z * isWater.z) + a3 * (e.w * isWater.w);
                    half3 rockFill = _RockColor.rgb;
                    [branch] if (landSum < 0.99 && _TerrainRockParams.x > 0.5)
                        rockFill = GroundTexture(rockFill, _TerrainRockParams, _TerrainRockTint.rgb, g);
                    rockFill *= 0.8 + 0.4 * nMid.r;
                    half3 fill = landSum > 0.01
                        ? lerp(rockFill, (a0 * landE.x + a1 * landE.y + a2 * landE.z + a3 * landE.w) / landSum, saturate(landSum * 4.0))
                        : rockFill;
                    albedo += fill * water - waterAlbedo;
                }
                // water-class ground well above the water level on a slope: not water
                half climb = smoothstep(_CoastClimbHeight, _CoastClimbHeight * 2.0, i.uvDetail.z - _SeaSurfaceHeight)
                           * smoothstep(_CoastClimbSlope, _CoastClimbSlope * 2.0, slope);
                // lakes are flat at their own level, so only coastal ground climbs
                water *= 1.0 - climb;

                // ---- airports: frame ground, then the runway on top (below) --------------
                // The frame replaces whatever class is there with the airport's ground class;
                // its edge wanders with Earth-anchored noise. Derivatives are taken here, in
                // uniform control flow, and passed into the branch.
                float2 posOS = i.positionOS;
                float2 dPosX = ddx(posOS), dPosY = ddy(posOS);
                half frame = 0.0, runway = 0.0;
                half3 frameAlbedo = albedo;
                uint runwaySurface = 0;
                float2 runwayLocal = 0.0;
                int runwayIndex = 0;
                [branch] if (_AirportCounts.x + _AirportCounts.y > 0.5)
                {
                    float sdFrame;
                    uint frameClass;
                    float2 frameGradient;
                    AirportFrame(posOS, sdFrame, frameClass, frameGradient);
                    float edgeNoise = ((nMid.a - 0.5) * 2.0 + (nFine.a - 0.5) * 0.7 * fineFade) * _FrameEdgeNoise;
                    float soft = max(_FrameEdgeSoftness, FootprintAlong(frameGradient, dPosX, dPosY));
                    frame = (frameClass < 256) ? 1.0 - smoothstep(-soft, soft, sdFrame + edgeNoise) : 0.0;
                    [branch] if (frame > 0.001)
                    {
                        half4 pf = LOAD_TEXTURE2D(_TerrainPalette, int2(frameClass, 0));
                        frameAlbedo = ClassAlbedo(frameClass, pf.rgb, g);
                        albedo = lerp(albedo, frameAlbedo, frame);
                        water *= 1.0 - frame;
                    }

                    float sdRunway;
                    float2 runwayGradient;
                    AirportRunway(posOS, sdRunway, runwaySurface, runwayLocal, runwayGradient, runwayIndex);
                    // analytic coverage of the pixel: sharp up close, a faint line far away
                    runway = saturate(0.5 - sdRunway / max(FootprintAlong(runwayGradient, dPosX, dPosY), 1e-3));
                    water *= 1.0 - runway;
                }

                half luma = dot(albedo, half3(0.2126, 0.7152, 0.0722));
                albedo = lerp(luma.xxx, albedo, _Saturation) * _Brightness;

                half detail = (nLarge.r - 0.5) * 0.6 + (nMid.g - 0.5) * 0.5 + (nFine.b - 0.5) * 0.5 * fineFade;
                albedo *= 1.0 + detail * _DetailStrength * 2.0;

                // ---- rock on steep ground ------------------------------------------------
                half rock = smoothstep(_RockSlopeStart, _RockSlopeEnd, slope + (nMid.b - 0.5) * 8.0) * (1.0 - water) * (1.0 - frame);
                half3 rockAlbedo = _RockColor.rgb;
                [branch] if (rock > 0.0 && _TerrainRockParams.x > 0.5)
                    rockAlbedo = GroundTexture(rockAlbedo, _TerrainRockParams, _TerrainRockTint.rgb, g);
                albedo = lerp(albedo, rockAlbedo * (0.8 + 0.4 * nMid.r), rock);

                // the runway itself: colour by surface; paved ones get pavement texture, rubber
                // and markings (RunwayPaint.hlsl), the rest a little of the ground detail
                [branch] if (runway > 0.001)
                {
                    half frameLuma = dot(frameAlbedo, half3(0.2126, 0.7152, 0.0722));
                    half3 frameGround = lerp(frameLuma.xxx, frameAlbedo, _Saturation) * _Brightness;
                    half3 runwayAlbedo = RunwayAlbedo(runwaySurface, frameGround);
                    bool paved = runwaySurface == 0 || runwaySurface == 1 || runwaySurface >= 8;
                    [branch] if (paved)
                    {
                        float2 along = _AirportRunways[5 * runwayIndex].zw;
                        float2 acrossDir = float2(-along.y, along.x);
                        float2 dLx = float2(dot(dPosX, along), dot(dPosX, acrossDir));
                        float2 dLy = float2(dot(dPosY, along), dot(dPosY, acrossDir));
                        runwayAlbedo = RunwayPaint(runwayIndex, runwayLocal, dLx, dLy, runwayAlbedo, runwaySurface,
                                                   _RunwayPaint.rgb, _RunwayPaintYellow.rgb, _RunwayRubber.rgb,
                                                   _RunwayRubberStrength) * _Brightness;
                    }
                    else
                    {
                        if (runwaySurface != 2) runwayAlbedo *= _Brightness;
                        runwayAlbedo *= 1.0 + detail * 0.5;
                    }
                    albedo = lerp(albedo, runwayAlbedo, runway);
                }

                // ---- light ---------------------------------------------------------------
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light sun = GetMainLight(shadowCoord);
                float3 L = sun.direction;
                half NdotL = saturate(dot(N, L));
                half3 color = albedo * (SampleSH(N) + sun.color * sun.shadowAttenuation * NdotL);

                // ---- water: the sea, lakes and rivers, shaded like the ocean -------------
                [branch] if (water > 0.002)
                    color = lerp(color, SeaColor(i.positionWS, up, V, dist, sun), water);

                // ---- fallback haze (only without the time-of-day fog) --------------------
                float airFraction = min(1.0, _AtmosphereHeight / max(_OceanCameraHeight, 1.0));
                half haze = (1.0 - exp(-dist * airFraction / _HazeDistance)) * (1.0 - _TOD_FogActive);
                color = lerp(color, _HazeColor.rgb * sun.color * saturate(dot(up, L) * 5.0 + 0.15), haze);

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            float4 ShadowVert(float4 positionOS : POSITION, float3 normalOS : NORMAL, float3 uvDetail : TEXCOORD1) : SV_POSITION
            {
                float3 p = positionOS.xyz;
                float height = uvDetail.z;
                LiftSea(p, normalOS, height);
                float3 positionWS = TransformObjectToWorld(p);
                float3 normalWS = TransformObjectToWorldNormal(normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            half4 ShadowFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            float4 DepthVert(float4 positionOS : POSITION, float3 normalOS : NORMAL, float3 uvDetail : TEXCOORD1) : SV_POSITION
            {
                float3 p = positionOS.xyz;
                float height = uvDetail.z;
                LiftSea(p, normalOS, height);
                return TransformObjectToHClip(p);
            }

            half DepthFrag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex DNVert
            #pragma fragment DNFrag

            struct DNVaryings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };

            DNVaryings DNVert(float4 positionOS : POSITION, float3 normalOS : NORMAL, float3 uvDetail : TEXCOORD1)
            {
                DNVaryings o;
                float3 p = positionOS.xyz;
                float height = uvDetail.z;
                LiftSea(p, normalOS, height);
                o.positionCS = TransformObjectToHClip(p);
                o.normalWS = TransformObjectToWorldNormal(normalOS);
                return o;
            }

            half4 DNFrag(DNVaryings i) : SV_Target { return half4(normalize(i.normalWS), 0); }
            ENDHLSL
        }
    }

    FallBack Off
}
