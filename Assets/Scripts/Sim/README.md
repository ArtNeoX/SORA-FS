# CirrusFS.Sim — aircraft / airport spawner

Selection menu, geodetic placement and floating origin. Namespace `CirrusFS.Sim`, one
assembly with `CirrusFS.Flight` (only `FlightSessionController` references the flight model).

```
Data/                     Config and catalogues
  AircraftData.cs         aircraft.json wire format + AircraftEntry (data + icon)
  AirportData.cs          one runway spawn point + AirportRef (airport paired with its region)
  SceneryData.cs          one region + SceneryDatabase (root of sceneries.json)
  AircraftCatalog.cs      scans StreamingAssets/Aircraft/*/aircraft.json, loads icons
  SceneryCatalog.cs       loads and validates StreamingAssets/sceneries.json
World/
  GeoMath.cs              lat/lon <-> scene metres, double precision, WGS84 curvature
  WorldOrigin.cs          where scene (0,0,0) is on Earth, including accumulated shifts
  SceneryRegion.cs        on the region model root; moves itself so a runway lands on (0,0,0)
  FlightSpawner.cs        aligns the region, spawns the aircraft on the runway
  FloatingOriginManager.cs  10 km horizontal threshold, shifts the world in LateUpdate
  FloatingOriginShiftable.cs / FloatingOriginIgnore.cs   opt-in / opt-out markers
  FlightSessionController.cs  menu <-> flight glue (camera, HUD, origin manager)
UI/
  SelectionListItem.cs    one row/tile; select, hover and search states
  StartMenuController.cs  fills both ScrollRects, owns the selection and the Fly button
```

## Scene setup

`Flight.unity` is already wired:

- **MenuCanvas** — `StartMenuController`, two `ScrollRect`s (grid of aircraft tiles, list of
  airport rows), a search box each, summary/status lines and the Fly button.
- **SimManagers** — `FlightSpawner`, `FloatingOriginManager`, `FlightSessionController`.
- **osaka_scenery** — `SceneryRegion`, `sceneryId = osaka_region`.
- **CirrusVision** — left in the scene but **inactive**; the spawner activates it on Fly.
  `Main Camera`'s `AircraftCamera.target` and `FlightHud.aircraft` start empty and are pointed
  at the aircraft on spawn.
- `FloatingOriginIgnore` sits on the canvases, the EventSystem, the camera and SimManagers.

Item prefabs are `Assets/Prefabs/UI/AircraftTile.prefab` and `AirportRow.prefab`. Their Button
*Transition* is **None** on purpose — `SelectionListItem` drives the background colour itself,
and the Button's own tint would overwrite the selected state on pointer exit.

To add a second region: drop the model in, put `SceneryRegion` on its root with a matching
`sceneryId`, and add the block to `sceneries.json`.

## Adding an aircraft

```
Assets/StreamingAssets/Aircraft/<Folder>/
  aircraft.json
  icon.png
```

`StreamingAssets` rather than `Assets/Aircraft` because it is the only folder that is still a
readable directory in a player build. Keep the art where it is; `aircraft.json` points at it.
`id` defaults to the folder name. `gear_height_m` is the distance from the aircraft origin down
to the bottom of the tyres — for the SF50 that is `0.865 + 0.28 = 1.145`, straight out of
`LandingGearSuspension.gears`.

On Android and WebGL, `StreamingAssets` is not a filesystem path and the folder scan returns
nothing; those platforms need a manifest file read through `UnityWebRequest`.

## Notes on the maths

- `GeoMath` is an **equirectangular tangent plane**, not a true Mercator. Mercator stretches
  north–south by `1/cos(lat)`, which at Osaka is a 21% error unless it is divided back out.
  Scale factors use the WGS84 radii of curvature at the mean latitude of the two points, which
  holds a 30 km leg to within a metre and a 100 km leg to within a few.
- `GeoMath.HaversineDistance` is a *spherical* reference used for the "wrong region" bound in
  `SceneryCatalog`. It reads ~0.1% short of the ellipsoidal answer on an east–west leg at this
  latitude (≈20 m over the Osaka pivot → RJBE leg); that is the sphere, not the projection.
- Coordinates and all intermediate maths are `double`. A `float` latitude alone is worth about
  0.5 m of error at this latitude.
- Geodetic frame: **+X east, +Z north**, so a true heading is a yaw directly.

## Scene north is not +Z here

`osaka_scenery` has a **270° Y rotation** on its root, which turns the geography with it. The
runway mesh measures **084.6 / 264.6 in the model's own frame** — Kobe 09/27, correct — but
**354.6 / 174.6 in world space**. So true north in this scene points along world **−X**.

`SceneryRegion.NorthYawDeg` carries that (root yaw + `modelNorthOffsetDeg`), and both the spawn
heading and `WorldOrigin`'s position readouts go through it. Config headings stay true
headings; nothing has to know the root is turned. Zeroing the root rotation instead also works —
`NorthYawDeg` becomes 0 and nothing else changes.

### Values measured from the model

The pivot in the original brief (34.622225, 135.438131) put the runway ~19.6 km from where it
actually sits in the model — the longitude looks like a slip. Working back from the runway
geometry, assuming the mesh is real RJBE:

| | value |
|---|---|
| Implied pivot | **34.629253, 135.223367** |
| Runway, model frame | 084.59 / 264.59 |
| Runway offset from pivot | 48.9 m east, 404.6 m north |
| Region root scale | **0.80** (so 1 real metre = 0.8 scene metres) |

`sceneries.json` now uses these, and RJBE 09/27 spawn on the runway centreline facing the
right way. Two things worth deciding:

- **The 0.8 root scale** means anything placed by lat/lon is 20% compressed, so the aircraft
  covers ground 25% faster than the scenery implies and the 10 km origin threshold is 12.5 km
  of modelled geography. `kobe_1to1` cancels it back to 1.0 with its own 1.25 scale, so the
  airport itself is 1:1 and only the surrounding region is compressed.
- **Colliders.** The region has 17 mesh renderers and exactly one collider — the 6 km
  `Ground` box under Kobe. Off that box there is nothing to land on, and
  `alignRunwaySurfaceToZero` will warn and leave the region at its authored height.

## Floating origin

`FloatingOriginManager` watches the aircraft's horizontal distance from the origin in
`LateUpdate` and, on the frame it passes `thresholdMetres` (10 km), translates the world by
`-aircraftPosition` so the aircraft lands back on the origin. Ordering and physics details are
documented in the script header.

**One deliberate deviation from the brief:** the shift is horizontal only. Snapping the
aircraft to a literal `(0, 0, 0)` would also zero its altitude and drop it to sea level
mid-flight, and altitude does not cost precision the way ground track does. Set
`preserveAltitude = false` for the literal behaviour.
