# CirrusFS.Sim — aircraft / airport spawner

Selection menu, geodetic placement and floating origin. Namespace `CirrusFS.Sim`, one
assembly with `CirrusFS.Flight` (only `FlightSessionController` references the flight model).

**Adding aircraft, airports or scenery? See [AUTHORING.md](AUTHORING.md).** This file covers how
the code fits together.

```
Data/                     Config and catalogues
  AircraftData.cs         aircraft.json wire format + AircraftEntry (data + icon)
  StartData.cs            one spawn point: runway threshold or parking stand
  AirportData.cs          one airport scenery + AirportRef (airport + start)
  SceneryData.cs          RegionData + SceneryDatabase (root of sceneries.json)
  AircraftCatalog.cs      scans StreamingAssets/Aircraft/*/aircraft.json, loads icons
  SceneryCatalog.cs       loads and validates StreamingAssets/sceneries.json
World/
  GeoMath.cs              lat/lon <-> scene metres, double precision, WGS84 curvature
  GeoAnchor.cs            an empty named after the coordinate it sits on
  WorldOrigin.cs          where scene (0,0,0) is on Earth, including accumulated shifts
  SceneryRegion.cs        on a model root; georeferences it and places it on the origin
  SceneryStartResolver.cs fills each airport's starts from its model, at menu load
  SceneryStreamer.cs      switches distant scenery off and near scenery on
  FlightSpawner.cs        places every scenery, spawns the aircraft on the chosen start
  FloatingOriginManager.cs  10 km horizontal threshold, shifts the world in LateUpdate
  FloatingOriginShiftable.cs / FloatingOriginIgnore.cs   opt-in / opt-out markers
  FlightSessionController.cs  menu <-> flight glue (camera, HUD, origin manager)
UI/
  SelectionListItem.cs      one row/tile; select, hover and search states
  StartMenuController.cs    fills both ScrollRects, owns the selection and the Fly button
  AirportCustomizePanel.cs  RAMP / RUNWAY start picker, opened by Customize
  AirportMapView.cs         plan view drawn from the starts' own positions
```

## Airports and starts

There are two kinds of scenery and they are siblings, not nested: an **airport** carries spawn
points, a **region** is terrain you fly over. Both use `SceneryRegion` and are georeferenced the
same way, by anchors inside the model.

An airport is listed **once**, showing its name and ICAO. Where to start from is a separate
choice, and the list comes from the model: the children of its `Starts` object are the starts.
Each is placed at its empty's exact position and facing, and its coordinate is *derived* from
that through the anchors — so the map, the info block and the GPS work without a coordinate ever
being typed into the config.

**Customize** opens the plan view: RAMP / RUNWAY tabs on the left, the layout on the right
drawn from those same coordinates, and the airport's position, elevation and magnetic
variation underneath. Confirm applies the start; backing out leaves the menu's choice alone.
Nothing on that map is authored art, so a start that is wrong on the map is wrong on the
ground too — which is most of the point of drawing it.

## Scene setup

`Flight.unity` is already wired:

- **MenuCanvas** — `StartMenuController`, two `ScrollRect`s (grid of aircraft tiles, list of
  airport rows), a search box each, summary/status lines and the Fly button.
- **SimManagers** — `FlightSpawner`, `FloatingOriginManager`, `SceneryStreamer`,
  `FlightSessionController`.
- **RJTT / RJBB** — airport sceneries (`SceneryRegion`, kind Airport), each with anchors and
  a `Starts` object. **japan_global** — a region scenery, always loaded.
- **CirrusVision** — left in the scene but **inactive**; the spawner activates it on Fly.
  `Main Camera`'s `AircraftCamera.target` and `FlightHud.aircraft` start empty and are pointed
  at the aircraft on spawn.
- `FloatingOriginIgnore` sits on the canvases, the EventSystem, the camera and SimManagers.

Item prefabs are `Assets/Prefabs/UI/AircraftTile.prefab` and `AirportRow.prefab`. Their Button
*Transition* is **None** on purpose — `SelectionListItem` drives the background colour itself,
and the Button's own tint would overwrite the selected state on pointer exit.

To add scenery: drop the model in, put `SceneryRegion` on its root, set its `kind` and
`sceneryId`, and add the entry to `sceneries.json`. See [AUTHORING.md](AUTHORING.md).

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
  north–south by `1/cos(lat)`, which at Tokyo is a 23% error unless it is divided back out.
  Scale factors use the WGS84 radii of curvature at the mean latitude of the two points, which
  holds a 30 km leg to within a metre and a 100 km leg to within a few.
- `GeoMath.HaversineDistance` is a *spherical* reference, used for picking the nearest anchor
  and for sanity checks. It reads ~0.1% short of the ellipsoidal answer on an east–west leg at
  this latitude; that is the sphere, not the projection.
- Coordinates and all intermediate maths are `double`. A `float` latitude alone is worth about
  0.5 m of error at this latitude.
- Geodetic frame: **+X east, +Z north**, so a true heading is a yaw directly.

## Scene north is not +Z here

Models arrive turned. `RJTT` needs `modelNorthOffsetDeg = 180`; all three sceneries in the scene
solve to north ~179.9 deg from their anchors.

`SceneryRegion.NorthYawDeg` is the inspector value (root yaw + offset), but anything with two or
more anchors **solves** its north instead, and that solved value is what placement uses. Config
headings stay true headings; nothing has to know a root is turned.

### Scale: watch for Web Mercator

Satellite exports come out in Web Mercator, a uniform stretch of `1/cos(latitude)` - 1.229 at
Tokyo. Left uncorrected a model is 23% oversized: a 3000 m runway becomes 3700 m of pavement and
every distance runs long.

The anchors measure it. `RJTT` and `RJBB` carry a **0.81** root scale that cancels it, and both
now solve to ~1.0. Check the solved scale whenever a new model is imported, and check it at both
ends of a large export - Mercator varies with latitude, so one number cannot fix a country-wide
model.

### Colliders

Only what has a collider can be landed on. `alignRunwaySurfaceToZero` raycasts for the surface,
falling back to the start marker's own height when there is none - the aircraft then sits at the
right level but falls through.

## Floating origin

`FloatingOriginManager` watches the aircraft's horizontal distance from the origin in
`LateUpdate` and, on the frame it passes `thresholdMetres` (10 km), translates the world by
`-aircraftPosition` so the aircraft lands back on the origin. Ordering and physics details are
documented in the script header.

**One deliberate deviation from the brief:** the shift is horizontal only. Snapping the
aircraft to a literal `(0, 0, 0)` would also zero its altitude and drop it to sea level
mid-flight, and altitude does not cost precision the way ground track does. Set
`preserveAltitude = false` for the literal behaviour.
