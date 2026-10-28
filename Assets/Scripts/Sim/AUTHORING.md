# Adding content

How to add aircraft. For how the code fits together, see [README.md](README.md); for the
coordinate pipeline that future terrain tiles must follow, see [../Geo/README.md](../Geo/README.md).

```
StreamingAssets/
  Aircraft/<Folder>/aircraft.json   one folder per aircraft
  Aircraft/<Folder>/icon.png
```

The files are read at runtime, not baked. The menu reloads them every time it opens, so you can
edit JSON with the game running, press Escape, and see the change.

There is no scenery any more: the world is a WGS84 globe covered by ocean. Terrain will arrive as
streamed tiles placed through `CirrusFS.Geo`, not as hand-placed models.

---

## Adding an aircraft

1. Make `StreamingAssets/Aircraft/<Folder>/` with `aircraft.json` and `icon.png`.
2. Either put the prefab under a `Resources` folder and set `prefab_resource_path`, or put the
   object in the scene and set `scene_object`.

```json
{
  "id": "Sr22",
  "name": "Cirrus SR22",
  "manufacturer": "Cirrus",
  "variant": "G6 Default",
  "icon": "icon.png",
  "prefab_resource_path": "Aircraft/Sr22",
  "gear_height_m": 0.62,
  "spawn_clearance_m": 0.05
}
```

- **`gear_height_m`** is the distance from the aircraft's origin to the bottom of the tyres, and
  what puts its wheels on the water for a surface spawn. Read it off the flight config rather
  than guessing: it is `-(refPoint.y + mountLocal.y) + radius`, since gear legs are measured from
  the controller's ref point. For the SF50 (ref point at the origin) that is `0.865 + 0.28` = `1.145`.
- The prefab needs `Rigidbody`, `AircraftController` and `LandingGearSuspension` on its root.
- `id` defaults to the folder name. Icons load from disk, so they need no Unity import settings.
- `GeoBody` is added at spawn if the prefab does not have one; it switches Unity gravity off and
  applies gravity toward the Earth's centre instead.
