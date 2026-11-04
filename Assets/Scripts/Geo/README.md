# CirrusFS.Geo: coordinate pipeline

The world is a true WGS84 globe. This folder is the single source of truth for where things
are. The aircraft, the camera, the ocean, and the terrain tiles still to come all go through
it the same way, and nothing else is allowed to.

```
lat / lon / h  ──GeoMath.GeodeticToEcef──▶  ECEF (double)  ──GeoOrigin.EcefToUnity──▶  Unity (float, near 0)
               ◀─GeoMath.EcefToGeodetic──                  ◀─GeoOrigin.UnityToEcef──
```

## 1. Geodetic → ECEF (`Core/GeoMath.cs`)

- Ellipsoid: WGS84, `a = 6378137.0 m`, `f = 1/298.257223563`, `b = a(1−f) = 6356752.314245 m`.
- **Height means height above the ellipsoid.** There is no geoid yet, so "sea level" is
  `h = 0` on the ellipsoid. If a tile source gives orthometric heights (above the geoid or
  MSL), it must add the geoid undulation before calling into this.
- ECEF is right-handed, in metres, with its origin at the Earth's centre. +X points through
  (0°N, 0°E), +Y through (0°N, 90°E), and +Z through the North Pole.
- The Earth is fixed: no rotation, no Coriolis. ECEF is an inertial frame here.
- `GeodeticToEcef(lat, lon, h)` uses the closed form. `EcefToGeodetic` starts from Bowring and
  refines until the latitude converges. Round-trip error is under 1 mm everywhere tested, from
  −500 m to 40,000 km, including the poles and the antimeridian (`Tests/EditMode`).
- `EnuBasis(lat, lon)` gives the local east/north/up unit vectors in ECEF. Up is the
  ellipsoid normal (geodetic), not the direction to the centre.
- `HeadingPitchRollToEcef` / `EcefToHeadingPitchRoll` convert between attitudes and ECEF axes.
  - Heading is degrees clockwise from true north.
  - Pitch is positive nose up.
  - Roll is positive right wing down.
  - Body axes follow Unity: right (+X), up (+Y), forward (+Z).

**Store world positions as `Double3` ECEF.** A float can't hold an Earth-scale coordinate:
at 6,400 km its spacing is 0.5 m.

## 2. ECEF → Unity (`Core/GeoOrigin.cs`)

`GeoOrigin` is the floating origin. It holds:

- `Ecef`, the ECEF point where Unity `(0,0,0)` is;
- `East`, `North`, `Up`, the local frame at that point.

Unity's axes are aligned to that frame:

```
Unity +X = East     Unity +Y = Up     Unity +Z = North      (at the origin)
unity = ( (p−O)·E , (p−O)·U , (p−O)·N )
```

Every float position in the scene is an offset from this nearby double point. Use these
conversions:

| Need | Call |
|---|---|
| Place an ECEF point | `GeoOrigin.EcefToUnity(ecef)` |
| Find where a Unity point is on Earth | `GeoOrigin.UnityToEcef(v)`, `UnityToGeodetic(...)` |
| Convert a direction (no translation) | `EcefDirToUnity` / `UnityDirToEcef` |
| Get local up at any Unity point | `GeoOrigin.UpAt(v)` (equals `Vector3.up` only at the origin) |
| Get "level, facing north" at a point | `GeoOrigin.EnuRotationAt(ecef)` |
| Convert an attitude | `HeadingPitchRollToUnity`, `UnityToHeadingPitchRoll` |

ECEF/ENU are right-handed and Unity is left-handed. The (E, N, U) → (X, Z, Y) mapping is a
reflection that is applied the same way to every point and direction, so rotations stay
proper rotations. **Don't convert quaternion components between frames.** Convert the
forward and up axes, then rebuild the rotation with `LookRotation`.

## 3. Re-centring (`Sim/World/FloatingOriginManager.cs`)

When the **camera** gets more than 1 km from Unity `(0,0,0)`, `GeoOrigin.Recenter` does two
things:

1. It moves the origin to the camera's ECEF position.
2. It re-aligns the axes to the local frame there, so +Y is up again at the camera.

The result is a `GeoFrameChange` (a rigid transform, held in double) that maps old Unity
coordinates onto new ones. It is broadcast as `GeoOrigin.Recentered`. Objects then update in
one of two ways:

- **Plain scene roots** are carried through that transform: position, rotation, and for
  rigidbodies velocity and angular velocity too. Interpolation is paused for one physics
  step, and the physics pose (not the interpolated one) is what gets moved.
- **Objects that know their own ECEF position** re-place themselves from it and are marked
  `FloatingOriginIgnore`. The ocean chunks and the water contact patch work this way, and
  tiles should too. This is exact and accumulates no error.

`GeoOrigin.OriginReset` fires on a spawn, when there is no continuity with the old frame.

**The aircraft (`World/GeoBody.cs`).** PhysX integrates in float, which is fine within a few
km of the origin. When the external camera is zoomed out to orbit, the origin follows the
camera and the aircraft can end up thousands of km away in Unity space. Past 20 km, `GeoBody`
takes over:

- it integrates the position itself in double, from the velocity PhysX produced;
- it writes the result back every step.

PhysX keeps forces, velocity and rotation, so the flight model is unchanged.

**Gravity.** `GeoBody` applies 9.80665 m/s² toward the Earth's centre. Unity's own gravity is
off on the aircraft.

## 4. Rendering at planet scale

- **Ocean (`World/Ocean/OceanSphere.cs`).** The ocean is a cube-sphere quadtree over the
  `h = 0` ellipsoid.
  - Vertices are computed in double, then stored as float offsets from each chunk's centre,
    in that chunk's local frame.
  - Chunks split only when their curvature error or float-precision error would exceed about
    ⅓ px.
  - Skirts hide level seams.
- **Depth (`World/GeoCameraRange.cs`).** One camera handles everything from 2 m to 50,000 km:
  - Rendering uses reversed-Z (Unity's default on D3D11/12, Vulkan and Metal, verified on in
    this project) with a 32-bit float depth buffer.
  - The far plane is the horizon distance from the camera, plus the horizon distance of 10 km
    terrain.
  - The near plane comes from the camera rig: 3 cm in the cockpit, 2 % of the orbit distance
    outside.
  - The far/near ratio stays near 1e7, well within reversed float depth.
- **Ocean shader (`World/Ocean/Ocean.shader`).**
  - Waves are two tiling normal maps, projected triplanar in **ECEF axes**. The origin's ECEF
    position is wrapped to one tile in double on the CPU, so waves are anchored to the Earth
    and never pop on a re-centre.
  - Wave strength fades with distance.
  - Shading combines Fresnel against sky reflection, a sun glint, and haze whose path length
    shrinks above the atmosphere.

## 5. Terrain tiles (DSF-style)

Terrain comes from `SceneryDataGen/terrain_exporter_v2`. Its README covers the workflow
and the file format. The exporter writes 1° × 1° tiles named like X-Plane's (`+37+127`)
into `<project>/TerrainData`, which git ignores.

**Data (`Core/Terrain/TerrainDataSet.cs`).**
- Reads `manifest.json` from each folder in `GlobeTerrain.dataFolders` and from each of its
  direct subfolders. The default is `TerrainData`, which typically holds `TerrainData/Global`
  plus regional folders.
- **Priority:** in every 1° cell the most detailed folder wins (more height samples per
  degree, then more class cells). Equal detail keeps the list order.
- **Edge blending:** where a detailed tile borders a cell that only a coarser folder
  covers, the detailed tile's heights fade into that folder's surface for the same cell
  (smoothstep over 8 coarse samples, at most 0.25°). They become exactly the coarse
  surface on the edge.
  - That coarse surface is the neighbour's own edge, so the two meet bit for bit.
  - On real data, Korea at 1200/° against the 400/° base, the step would have been up to
    33.5 m; it is now 0.0000 m at all 1,604 edge points tested.
  - The covered coarse tiles stay loaded as blend targets (`AllTiles`).
- Tile files are memory-mapped and opened on first use, so an open tile costs page cache,
  not managed memory. Idle tiles are closed again.
- `SampleHeight` is the tile's bilinear surface. `ClassAt` returns the class cell.

**Mesh (`Core/Terrain/TerrainChunkBuilder.cs`).** Each tile is a quadtree. Level L is a
grid of 2^L × 2^L chunks, each with (R+1)² vertices (R = 32).
- Vertex positions come from integers: `lat = (tileLat·K + y·R + j) / K`, with K = 2^L·R.
  A shared vertex therefore gets the same double in both chunks, and in both tiles.
- Each vertex goes through `GeoMath` in double and is stored relative to the chunk centre
  in the chunk's E/U/N frame, exactly as ocean chunks are (§4).
- Normals use central differences in the vertex's own ENU frame. Across a tile edge they
  read the neighbouring tile.
- Skirts cover cracks between chunks of different levels.
- Each chunk carries a (128+16)² texture of class IDs. The 8-texel border
  (`ClassApron`) comes from the neighbours, so the shader's warped lookup never runs off
  the edge.

**Streaming (`World/Terrain/GlobeTerrain.cs`).**
- **Loaded window, as X-Plane does with DSF tiles.** Only `tilesWide` × `tilesHigh` 1° tiles
  around the focus are drawn (4 × 3 by default). With an even width the window shifts when
  the focus crosses a tile's middle, so the aircraft is always in one of the middle
  columns. Beyond the window there is only the ocean sphere; tiles that leave it stop
  drawing and their chunks age out of the cache.
  - The coarse 2°–16° chunks from a whole-world overview (commit 177ed89) were removed: no
    terrain is drawn outside the window.
- **Splitting.** A chunk splits while its vertex spacing, seen from the **focus**, exceeds
  `maxVertexAngle / lodQuality` (about 4 px at quality 1). It stops at the tile's data
  resolution.
  - The focus is the aircraft: `FlightSessionController` sets `GlobeTerrain.focus` at spawn.
    Zooming the camera out to the globe keeps the detail at the aircraft, not under the
    camera. Without an aircraft the focus is the camera.
  - **`lodQuality`** (0.25–4) is the detail slider for quality settings: about 0.5 low,
    1 medium, 2 high.
  - Cells within `immediateRadius` of the focus are built at once, outside the per-frame
    budget.
- Tiles and chunks behind the camera's horizon are skipped. The horizon test allows for
  each chunk's own height.
- Only the coarsest (16°) chunks stay cached forever. Everything else is evicted beyond
  `cacheSize`.
- Chunks are placed through `GeoOrigin` and re-placed on `Recentered`/`OriginReset`. The
  root carries `FloatingOriginIgnore`.
- `GlobeTerrain.Active.TryGetHeight(lat, lon)` answers ground-height queries. The spawner
  uses it to put a low spawn on the ground; the F1 window uses it to show AGL.

**Shader (`World/Terrain/Terrain.shader`).**
- **Land-class borders are curves, not cells.** For each pixel the shader takes each
  class's share of a quadratic B-spline over the 3×3 nearest *data* cells. The shares are
  raised to **Border sharpness** (`_BorderCurve`; 1 = soft blur, default 4) and used as
  blend weights.
  - Borders follow the contours of that smooth field: a lone cell becomes a small round
    dot, a staircase a smooth diagonal, a one-cell river a winding line.
  - The lookup runs on the data's own cells, whatever the chunk's texel size. GlobeTerrain
    passes `_ClassCells` (cells per texel and the cell offset of the chunk's corner) for
    each chunk. Mid-altitude chunks hold each 185 m cell in one or two texels, unevenly, and
    blending in texels left irregular steps.
- **Border warp** moves the lookup with Earth-anchored noise. The noise octaves are
  weighted by how close their wavelength (4096, 1024, 256 or 32 m) is to four cells, using
  the cell size on screen, so borders bend at every LOD without stippling.
- Palette colours come from the manifest. Water is the palette alpha.
- Detail noise is anchored to the Earth on a Mercator grid (conformal, so nothing is
  sheared), wrapped per chunk in double.
- Steep slopes turn to rock.
- **Water is drawn by the terrain.** The water class (WorldCover: sea, lakes and rivers alike)
  is the water, so the shoreline is wherever the class says. This replaces two surfaces that
  disagreed: the ocean sphere's h = 0 line and the class border. Their mismatch gave puddles and
  a second shoreline.
  - **Sea level:** the vertex shader raises the exporter's −5 m sea floor to `_SeaSurfaceHeight`
    (5 cm) in every pass, so the sea is flat at sea level. Lakes keep their own height.
  - **Look:** water is shaded like the ocean (`SeaColor`: the same wave maps, triplanar ECEF
    mapping, colours and sun) from the `_Sea*` globals that `OceanSphere` publishes. Colours are
    sent as linear vectors, because a global colour isn't converted the way a material Color is.
  - **Steep coasts:** water-class ground more than `_CoastClimbHeight` (1.5 m) above the water on
    slopes over `_CoastClimbSlope` (3°) shows the land around it, or rock. Steep coasts are where
    the land cover's coastline and the 280 m height grid disagree. Land pixels are never changed.
  - **The ocean steps aside:** `GlobeTerrain` publishes the 1° cells it drew this frame
    (`_TerrainDrawnCells`, at most 16), and `Ocean.shader` discards its pixels inside them
    (`Ocean/OceanCells.hlsl`, every pass). The ocean only shows on open-sea tiles and beyond the
    loaded window. It runs about 2 m under each cell edge, below the terrain's water, so no gap
    can open there.
  - **Below sea level:** land under 0 m (polders, low parts of Osaka) now shows as land; before,
    the global ocean covered it. Autogen skips the sea floor.
  - **Collision:** unchanged. The sea floor is still −5 m and `WaterSurfaceCollider` is the sea
    surface.
- Lighting is the main light plus SH ambient. Haze comes from the time-of-day fog pass.

**Ground textures (`World/Terrain/TerrainClassTextures.cs`).** Optional tiling albedo per
land class. The asset is `Assets/Textures/Terrain/TerrainClassTextures.asset`, assigned to
`GlobeTerrain.classTextures`. Edits apply live in play mode.
- Each entry names a class from the manifest (case-insensitive) and sets a texture, its
  ground size in metres, a tint and a strength.
- **Colour from: Class colour** keeps the exporter's colour and adds only the texture's
  light and dark pattern (texture ÷ its own average). From far away the ground is exactly
  the class colour. **Texture** uses the texture's own colours.
- `steepSlopes` textures the rock band on steep ground.
- Textures are packed at run time into one sRGB `Texture2DArray`, so any size or format
  works. They're laid on the detail grid with a whole number of repeats per 4096 m, so they
  stay seamless across chunks. The size is exact at `referenceLatitude` and scales with
  cos(ref)/cos(lat) elsewhere. Sizes above about 3 km all become one repeat per 4096 m.
- Each blended cell is textured before the blend, so textures fade into each other like the
  colours do. A second, five-times-larger, transposed lookup is mixed in by noise to hide
  repetition (material: **Anti-tiling mix**).
- Starter textures come from `SceneryDataGen/ground_textures/make_ground_textures.py`.
  They're procedural placeholders; replace them with real seamless textures.

**Collision (`World/Terrain/TerrainCollision.cs`).**
- It builds MeshColliders for the 3 × 3 finest-level chunks under the aircraft, from the
  same builder, so the gear touches the drawn surface. This doesn't depend on the render
  LOD.
- Over the sea, the terrain sits at −5 m, under the h = 0 water patch, whose hit is
  nearer.
- The gear raycasts toward `-GeoOrigin.UpAt(point)`.

**Heights** are the exporter's (Copernicus EGM2008, about mean sea level), used as
ellipsoid heights, the same assumption as the h = 0 ocean (§1).

**Known limits.**
- The overview is built from the drawn tiles at 16/°. A detailed regional tile's edge
  blending isn't in it, which doesn't matter at that scale.
- Sea floor: the terrain draws its sea at sea level itself, and the ocean sphere steps aside
  inside drawn tiles (see "Water is drawn by the terrain" above). Small islands still have a
  stair-stepped outline from the 280 m height grid.
- Class textures are point-sampled on coarse chunks, where a texel spans several data
  cells. They're smoothed in texels there.
- Ground textures have a seam along the antimeridian (180°), where the detail grid wraps.
- A pixel blends at most four land classes. A fifth class in the same 3×3 cells is
  dropped.
- Chunks are built on the main thread, about 3 ms each, within a 4 ms budget per frame.

## 6. Autogen (objects by land class)

`World/Autogen/GlobeAutogen.cs` places objects on the terrain by land class. For now that's
the trees on "Forest". It's configured by `Assets/Resources/Autogen/AutogenSet.asset`; edits
apply live in play mode.

**Placement (`Core/Autogen/AutogenPlacement.cs`).**
- The world is cut into patches of 1/128° (about 870 × 690 m at 37° N). Each patch lies
  inside one terrain tile.
- A patch is a jittered grid of candidates at the layer's density. A candidate is kept where
  the class cell under it is one of the layer's classes.
- Each candidate always draws the same seven random numbers from a generator seeded by the
  patch and the layer. That gives its position, yaw, size, texture variant, thinning value
  and brightness. A tree is therefore always in the same place, and nothing is stored.
- Heights come from `TerrainChunkBuilder.SurfaceHeight`: the triangles of the tile's finest
  terrain level, with the same diagonal the builder uses. This is the surface that's drawn
  and that the colliders use. The bilinear data surface isn't used here: the mesh grid
  (2048 vertices/°) doesn't line up with the height samples (1200/°), so on hills the two
  differ by up to ~6 m. In a play test, all 2,577 trees within 600 m were 0.000 m from the
  collider surface.
- Positions are relative to the patch centre in its east/up/north frame, computed in double.
- Within each layer's range the terrain is always at its finest level (it splits to that
  level out to about 9 km).

**Streaming and drawing.**
- Patches within a layer's range are built nearest first, within `buildBudgetMs` per frame.
  Built patches are cached (`cacheSize`).
- Each patch is one `DrawMeshInstancedProcedural` call, issued per camera in
  `beginCameraRendering` for game cameras. The editor's Scene view gets them only with
  `drawInSceneView`. Instances are in a GPU buffer. The patch matrix is rebuilt from
  ECEF every time it is drawn, so re-centring needs no handling.

**Layer settings (`AutogenSet`).**
- Classes, model and texture variants.
- Density in objects per hectare.
- Full-density range and range: between the two, objects thin out. Each object shrinks away
  at its own distance, so nothing pops.
- Scale range, random yaw, ± brightness, sink depth and shadows.
- The model keeps its import transform, and its lowest point is placed on the ground.

**Shader (`World/Autogen/AutogenFoliage.shader`).**
- Alpha-cutout foliage with wrap lighting around local up, sun shining through the leaves,
  and darker shading toward the trunk.
- Shadow caster, depth and depth-normals passes all clip on the texture's alpha.
- **Texture detail by distance:** beyond *Full-resolution texture up to (m)*, the mip level
  is raised by *Mip levels dropped per distance doubling* for every doubling of distance.
  It's never finer than the GPU's own choice.
- Texture variants of the same size and format are copied into one array on the GPU and stay
  compressed (DXT5). The tree textures import with *mip maps preserve coverage*, so canopies
  don't thin out in the far mips.

## 7. Airports (runways and cleared frames)

**Data.**
- `SceneryDataGen/airport_exporter` writes `AirportData/` (gitignored) from OurAirports.
  The CSV schema is in its README.
- `GlobeAirports` (`World/Airports`, on the `Airports` object in Flight.unity) loads the data
  on a worker thread at start, about 1.4 s in the editor for the whole world.
- The data lives in `AirportDatabase` (`Core/Airports`, no Unity calls). It holds the records,
  a 1° tile index, and the ICAO/IATA/name search the start menu uses.

**Layout (`AirportLayout`).** Each airport is laid out in the tangent plane at its reference
point.
- **Runway rectangles** come from the two end coordinates.
- **Frame:** each runway is widened by max(30 m, 1 x width) beside each edge and by
  60/100/150 m past each end (runways under 1200 m / under 1800 m / longer).
- **Infield:** an airport with several runways clears the convex hull of all of them. The hull
  is cut down to at most 8 edges, only growing, never shrinking. It is used only while its
  diameter is under 8 km and its area is under 6x the runway frames' area; otherwise each
  runway keeps its own frame.
- **Ground class:** a frame shows one class, picked from rings 1.5–3.5 km around the airport.
  Mostly snow gives Snow / ice; mostly rock or sand gives whichever of the two dominates; anything
  else gives Grass.

**Drawing.** Runways are part of the terrain surface, so they follow the elevation at every LOD
and there's nothing to z-fight.
- When `GlobeTerrain` builds a chunk, `AirportChunkPacker` writes the runways and frames that
  reach it into the chunk's MaterialPropertyBlock:
  - `_AirportCounts`;
  - `_AirportRunways[80]`: 16 runways, 5 float4 each (centre and direction; half sizes,
    surface, flags; displaced thresholds and designators; runoff per end; marking class,
    rubber, threshold stripes, seed);
  - `_AirportFrames[96]`: per polygon one header and one float4 per edge.
- Coordinates are chunk-local east/north metres.
- `Terrain.shader` tests each pixel against them:
  - The frame replaces the class blend with the frame class. Its edge is waved by Earth-anchored
    noise (`_FrameEdgeNoise`), and it also switches off steep-slope rock and the sea-floor clip.
  - The runway is drawn on top in a colour per surface (`_Runway*`), with analytic pixel coverage,
    so it fades instead of flickering far away.
  - Edge anti-aliasing is anisotropic: the edge width is the pixel's footprint along the distance
    field's gradient. On a low approach a pixel spans ~1 m across the runway but 50 m or more
    along the view, so taking the pixel's largest extent blurred the whole runway away.
  - `AirportRunway` gives runway-local coordinates (along from the le end, across from the
    centreline, left positive). Its box includes the runoff beyond each end.
- **Markings and pavement** (`RunwayPaint.hlsl`, rules in `Core/Airports/RunwayMarkings.cs`).
  OurAirports has no marking type, so it is derived per runway:
  - Class: precision (large/medium airport, lit, >= 1800 x 30 m), non-precision (lit or
    >= 1200 m), visual (other paved). Grass, dirt, gravel, sand, snow and water get none.
  - Threshold: piano keys by width (4/6/8/12/16, ICAO table 5-1), 30 m long, 45 m on
    runways 45 m or wider. Visual runways get a single threshold bar instead.
  - Designator 12 m past the keys: segment font, number above the L/C/R letter, read upright
    from the approach. The leading zero is drawn when the data writes one ("09").
  - Centreline dashes (30 m on, 20 m off), aiming point (ICAO distances by length), touchdown
    zone bars 3-3-2-2-1-1 on precision runways, side stripes.
  - Displaced thresholds: bar, arrowheads and centreline arrows. Large airports with runways of
    2400 m or more also get a 90 m arrow runoff before the end where there is none in the
    data, and long runways get a blast pad (60 / 45 m) with yellow chevrons. The frame and
    the height patch's paved rectangle grow by the runoff.
  - Pavement: concrete slabs (~6 x 7.5 m, sealed joints, per-slab tone), asphalt lanes and
    panels with repair patches, grain and stains.
  - Rubber: streaks in the touchdown zone, heaviest in the main gear tracks, scaled by
    airport type and length (`RunwayMarkings.Rubber`).
  - All of it is box-filtered against the pixel footprint in runway axes, and the noise
    (`_RunwayDetail`, from `RunwayDetailTexture`) is sampled with explicit gradients. So
    marks fade to their average coverage instead of shimmering.
  - Material knobs: `_RunwayPaint`, `_RunwayPaintYellow`, `_RunwayRubber`, `_RunwayRubberStrength`.
- Runways and frames under `minPixels` on screen are left off coarse chunks. Chunks without
  airports skip the whole branch.

**Ground under the airport.**
- **Height patch.** `AirportHeightPatch` is set as `TerrainTileSource.HeightPatch` on every tile
  an airport reaches.
  - **The problem:** with 280 m between global height samples, a shore runway such as TNCM's lies
    on the slope between a land vertex and a −5 m sea vertex, below the h = 0 ocean.
  - **The fix:** around the runway, ground is kept at least the airport elevation, clamped to
    1–3 m. The lifted points are exactly those whose ±1 height-cell box overlaps a runway: the
    vertices of every triangle under it. So the coast gains at most one cell of land.
  - **Left alone:** ground above 3 m (all inland terrain) and airports published below 0.5 m (real
    land below sea level, such as Schiphol or the Caspian).
  - Rendering, collision, autogen and spawning all read it through `SampleHeight`.
  - **Limit:** coarse chunks beyond about 35 km don't sample the lifted points, so a shore runway
    can sit under the sea in the far distance until the finest level takes over.
- **Frame size** is `GlobeAirports.frame`. Edits in play mode re-lay out every airport after
  0.3 s (about 0.3 s of work) and rebuild the chunks; edits there are lost when play mode ends.
- **Autogen.** Autogen asks `IAutogenExclusion` (the database), so trees stay 15 m clear of
  frames.
- **Late load.** If the data arrives after chunks, colliders or autogen patches were built, those
  are rebuilt.

## Tests

`Assets/Tests/EditMode` (assembly `CirrusFS.Geo.Tests`, run from the Test Runner, EditMode):

- Airports (`AirportTests`):
  - the CSV reader;
  - frame margins and the infield hull;
  - that hull simplification never loses area;
  - search ranking and accent folding;
  - chunk-local runway packing;
  - that the height patch leaves land below sea level alone.

- `GeodeticToEcef(0,0,0) = (6378137, 0, 0)`, and `(90,0,0) = (0, 0, 6356752.314)` to ±1 mm.
- Geodetic → ECEF → geodetic round trip under 1 mm, at 20,000 random points plus poles,
  equator and antimeridian edge cases.
- ENU basis is orthonormal and right-handed. Heading/pitch/roll round-trips, and the sign
  conventions hold.
- 500 km of straight, level flight at 60 fps with the camera 300 m behind, re-centring on the
  camera every 1 km (499 re-centres). Results:
  - The largest frame-to-frame position jump is 0.07 mm, so there is no jitter.
  - Attitude stays level to better than 1e-5°.
  - Unity +Y is local up after every re-centre.
  - Total float-integration drift is about 23 mm over 500 km. That is the cost of PhysX
    integrating in float near the origin, not of the re-centring; the smaller 1 km threshold
    keeps positions smaller and cut it tenfold from the 26 cm a 5 km threshold gave.
