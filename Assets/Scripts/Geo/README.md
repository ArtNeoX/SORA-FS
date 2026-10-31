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
- Reads `manifest.json` from each folder in `GlobeTerrain.dataFolders`. Earlier folders
  win, so a fine regional folder can sit in front of a coarse world folder.
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
- Each chunk carries a (64+2)² texture of class IDs. The extra one-texel border comes
  from the neighbours.

**Streaming (`World/Terrain/GlobeTerrain.cs`).**
- A chunk splits while its vertex spacing, seen from the camera, exceeds `maxVertexAngle`
  (about 4 px). It stops at the tile's data resolution.
- Tiles and chunks behind the horizon are skipped. The horizon test allows for each
  chunk's own height.
- Chunks are placed through `GeoOrigin` and re-placed on `Recentered`/`OriginReset`. The
  root carries `FloatingOriginIgnore`.
- `GlobeTerrain.Active.TryGetHeight(lat, lon)` answers ground-height queries. The spawner
  uses it to put a low spawn on the ground; the F1 window uses it to show AGL.

**Shader (`World/Terrain/Terrain.shader`).**
- It blends the colours of the four nearest class cells (palette from the manifest) with a
  noise-warped lookup, so land types fade into each other.
- Detail noise is anchored to the Earth on a Mercator grid (conformal, so nothing is
  sheared), wrapped per chunk in double.
- Steep slopes turn to rock.
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
- Every visible tile draws at least one chunk, so a whole-world data set seen from orbit
  would need a coarser tile level above 1°.
- Class textures at coarse levels are point-sampled.
- Ground textures have a seam along the antimeridian (180°), where the detail grid wraps.
- The softest class blend is about one class cell wide (~30 m at 3600 cells/°).
- Chunks are built on the main thread, about 3 ms each, within a 4 ms budget per frame.

## Tests

`Assets/Tests/EditMode` (assembly `CirrusFS.Geo.Tests`, run from the Test Runner, EditMode):

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
