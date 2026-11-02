# CirrusFS.Sim — menu, spawner, floating origin

Selection menu, spawning on the globe, and the floating-origin manager. Namespace
`CirrusFS.Sim`, one assembly with `CirrusFS.Flight`. Coordinates come from `CirrusFS.Geo`; the
pipeline every position goes through is described in [../Geo/README.md](../Geo/README.md).

**Adding aircraft? See [AUTHORING.md](AUTHORING.md).**

```
Data/
  AircraftData.cs         aircraft.json wire format + AircraftEntry (data + icon)
  AircraftCatalog.cs      scans StreamingAssets/Aircraft/*/aircraft.json, loads icons
  SpawnPoint.cs           lat / lon / ellipsoid altitude / true heading / speed
World/
  FlightSpawner.cs        resets GeoOrigin onto the spawn point, places the aircraft
  FloatingOriginManager.cs  re-centres on the camera past 1 km: moves AND re-rotates the frame
  FloatingOriginIgnore.cs marker for roots the manager must not carry (UI, self-placing objects)
  FlightSessionController.cs  menu <-> flight glue (camera, HUD, water patch, debug window)
  VolumetricCloudsOrigin.cs   keeps cloud noise still through re-centres
UI/
  SelectionListItem.cs    one row/tile; select, hover and search states
  StartMenuController.cs  aircraft list, start-position fields, Fly button
```

## Scene setup

`Flight.unity`:

- **MenuCanvas** — `StartMenuController`: the aircraft grid with its search box, and the
  **StartPanel** with latitude, longitude, altitude (m above the WGS84 ellipsoid) and heading
  fields. Defaults are 37.09 N, 127.03 E, 3000 m, heading 0.
- **SimManagers** — `FlightSpawner`, `FloatingOriginManager` (tracks `Main Camera`, 1 km),
  `FlightSessionController`, `GeoDebugWindow` (F1).
- **Ocean** — `OceanSphere`, the h = 0 ellipsoid as an LOD quadtree.
- **WaterContact** — `WaterSurfaceCollider`, a 1.2 km collision patch of the sea that follows
  the aircraft so the gear has something to touch.
- **Terrain** — `GlobeTerrain`: streams the exporter's 1° tiles from `<project>/TerrainData`
  as LOD chunks on the globe (see `Geo/README.md` §5).
- **TerrainContact** — `TerrainCollision`: full-resolution ground colliders under the aircraft.
- **Main Camera** — `AircraftCamera` and `GeoCameraRange` (far plane from altitude).
  - V toggles cockpit and external view.
  - In external view, holding + / - zooms from 10 m to 50,000 km. The zoom is exponential
    (`zoomRate` e-folds a second, speed proportional to the distance) and eased by
    `zoomSmoothing`.
- **Directional Light** — `GeoSky`: the sun is fixed in ECEF, the sky fades to black with
  altitude.
- **A320CEO** — the aircraft, with `GeoBody` (ECEF position in double, gravity toward the
  Earth's centre; Unity gravity off).
- `FloatingOriginIgnore` sits on the canvases, the EventSystem, SimManagers, the camera (which
  the manager carries explicitly), Ocean, WaterContact, Terrain and TerrainContact.

Where no terrain tile has been exported, everything is ocean. A spawn more than 50 m above the
ground (terrain height where there is a tile, else the sea) starts in flight at 250 kt with the
gear up; otherwise the aircraft starts at rest on the ground or the water.

## Floating origin

`FloatingOriginManager.LateUpdate` runs after the camera rig. When the camera is more than
`thresholdMetres` (1 km) from Unity (0,0,0) it calls `GeoOrigin.Recenter` with the camera's
ECEF position, which also re-aligns Unity's axes to the local east/up/north there, and carries
every scene root through the resulting `GeoFrameChange`. Rigidbodies keep their physics pose,
velocity and angular velocity (rotated into the new axes); interpolation is paused for one step.
Details are in the script header and in `Geo/README.md`.
