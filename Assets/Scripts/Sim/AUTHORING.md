# Adding content

How to add aircraft and scenery. For how the code fits together, see [README.md](README.md).

```
StreamingAssets/
  sceneries.json                    names the airports and regions
  Aircraft/<Folder>/aircraft.json   one folder per aircraft
  Aircraft/<Folder>/icon.png
```

Both files are read at runtime, not baked. The menu reloads them every time it opens, so you can
edit JSON with the game running, press Escape, and see the change.

`sceneries.json` is deliberately small. **Everything positional lives in the models**: where a
scenery is, which way it faces, how big it is, and where you can start from. The config only
carries the handful of facts a model cannot state.

---

## Two kinds of scenery

Both use the same `SceneryRegion` component and are placed identically. The only difference:

| kind | carries | placed by |
|---|---|---|
| **Airport** | spawn points under a `Starts` object | its own start marker, exactly |
| **Region** | nothing; terrain you fly over | its own anchors, from the departure coordinate |

There is no nesting — an airport is not "inside" a region. They are siblings, and each is
georeferenced independently, so no scenery inherits another's error.

---

## Georeferencing: anchors

Put an empty inside the model on a point whose real coordinate you know, and name it after that
coordinate:

```
35.556428, 139.772047
```

`SceneryRegion` scans its children for names that parse as a coordinate pair. Nothing is derived
from the model's origin, its scale or its parents — the anchor's world position already has all
of that baked in.

- Latitude then longitude, **dot as the decimal point**, comma or space between. A prefix is
  fine as long as both numbers carry a decimal point. Parsing is invariant-culture, so a machine
  with a comma decimal separator still reads it correctly.
- Only the empty's **position** is used. Blender bakes an empty's display size into its scale and
  often gives it an arbitrary rotation; both are ignored.
- **One anchor** fixes position only.
- **Two or more** also solve north and scale, which is what you want. Scatter three across the
  model; placement measures from whichever is nearest, so a long model does not inherit the
  error of an anchor at the far end.
- `useSolvedScale` makes the solved scale authoritative. Leave it on.

**The root must be yaw-only.** An FBX out of Blender arrives with a −90° X rotation; if that
lands on the root, north stops being horizontal and the model lands wrong with no other symptom.
Put the tilted model under a child. The component logs an error if its root is tilted.

**Every scenery must agree on north.** Each model maps its geography through its own rotation, so
two whose solved norths disagree sit at an angle to each other. The spawner compares the solved
values and warns.

---

## Starts come from the model

Hang a `Starts` object off the airport model with an empty for each spawn point, named with a
kind prefix and pointed the way the aircraft should face:

```
RJTT/
  35.556428, 139.772047      <- anchors
  35.546372, 139.828651
  35.536524, 139.785731
  Starts/
    RWY 16R                  <- spawn points
    RWY 34L
    APRON 17
```

Whatever is under `Starts` **is** the start list. Those names become the menu entries, those
positions are where the aircraft lands, and those facings are which way it points. There is no
`starts` array in the config at all.

- **`RWY` prefix** marks a runway and is stripped, so `RWY 16R` shows as `RWY 16R` (the label
  puts it back). Anything else is somewhere to park and keeps its full name, so `APRON 17` reads
  as written — `17` on its own would say nothing.
- **Runway length** for the map comes from pairing reciprocal designators and measuring between
  the two empties. `16R` pairs with `34L`; the side letter is matched first and then ignored, so
  a single runway labelled `06R`/`24R` still pairs. Unpaired runways get a label and no strip.
  Width is display-only, from `markerRunwayWidthM`.
- **Coordinates are derived** from each empty through the anchors, so the customize map, the info
  block and the GPS all work without a coordinate ever being typed.
- **The airport's own reference point** — what the map is centred on — is the average of its
  starts. Also never typed.
- An airport whose model has no `Starts` object is not offered in the menu, because there is
  nowhere in it to begin a flight.

Name the container something else with `"starts_object": "SpawnPoints"` on the airport.

---

## sceneries.json

```json
{
  "airports": [
    {
      "icao": "RJTT",
      "name": "Tokyo Haneda",
      "scene_object": "RJTT",
      "elevation_m": 6.4,
      "magnetic_variation_deg": -7.9,
      "features": "3D"
    }
  ],
  "regions": [
    {
      "id": "japan_region",
      "display_name": "Japan",
      "scene_object": "japan_global",
      "always_loaded": true,
      "render_distance_m": 0
    }
  ]
}
```

`scene_object` defaults to the ICAO or the region id, so it can usually be left out.
`magnetic_variation_deg` is east-positive, so Japan is negative. `elevation_m` is informational
for the aircraft — it is put on the surface by raycast — but it is what other sceneries set their
sea level against, so it is worth getting roughly right.

Set `sceneryId` on each model's `SceneryRegion` to match, and its `kind`.

---

## Streaming

`SceneryStreamer` on SimManagers switches distant scenery off and near scenery back on. It
drives every scenery from one place rather than each checking itself, because something that has
disabled itself no longer runs code to notice you coming back.

- **`renderDistanceM`** on the region: metres from the aircraft, measured to the nearest point of
  the model's bounds, beyond which it stops drawing. 0 means never cull.
- **`alwaysLoaded`** overrides it. For the wide backdrop that should never pop out.
- Only renderers and colliders are switched; the roots stay active so they can be found, measured
  and brought back.
- `hysteresisM` stops a scenery sitting on the boundary from flickering: it must get that much
  further away before it goes, but come all the way inside before it returns.
- The spawner calls `Refresh()` after placing everything, so the world is settled on the first
  frame rather than up to a tick later.

The measurement is to the model's bounds, not its origin, so a region the size of Japan counts
as near while you are anywhere over it.

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
  the only thing stopping it spawning inside the runway. Read it off the flight config rather
  than guessing: it is `-(refPoint.y + mountLocal.y) + radius`, since gear legs are measured from
  the controller's ref point. For the SF50 (ref point at the origin) that is `0.865 + 0.28` = `1.145`.
- The prefab needs `Rigidbody`, `AircraftController` and `LandingGearSuspension` on its root.
- `id` defaults to the folder name. Icons load from disk, so they need no Unity import settings.

---

## Watch the projection

Satellite exports are usually **Web Mercator**, which is a uniform scale of `1/cos(latitude)` —
about 1.23 at Tokyo. A model left in those units is 23% oversized: a 3000 m runway becomes 3700 m
of pavement, and every distance and flight time runs long.

The anchors detect it. Load the model, look at the solved scale, and if it is not ~1.0 scale the
root by the reciprocal — 0.81 for Japan. RJTT and RJBB are corrected this way.

Check it whenever you import something new, and check it at both ends of a large export: Mercator
varies with latitude, so a country-wide model cannot be fixed with a single number.

---

## Measuring coordinates from a model

If the model contains recognisable geometry, measure it: take the runway mesh's vertices,
transform them to world, take the principal axis of the horizontal covariance, and subtract the
root's yaw to get the heading in the model's frame. Sanity-check it against the real runway
number.

For a satellite-terrain export with nothing to measure, place three anchors on spots you can
recognise - coastlines read well from altitude - and let the solver do the rest. That is what
the anchors are for, and it is why three of them beat one.
