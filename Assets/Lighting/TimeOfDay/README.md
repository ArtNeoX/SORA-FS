# Time of day

Sun, moon, sky, fog, ambient light, reflections and exposure for CirrusFS, in URP. One
`AtmosphereModel` (Hillaire 2020 / Bruneton) feeds everything: the sky, the distance haze, the
colour of sunlight and the ambient all come from the same numbers, so they agree at every time of
day and altitude.

## Setup

1. Drop `Prefabs/TimeOfDaySystem.prefab` into the scene. Remove any other Directional Light and
   anything else that sets `RenderSettings.skybox` or the sun (in Flight.unity: the old
   `Directional Light` with `GeoSky`).
2. Set `View Camera` on the TimeOfDaySystem, or leave it empty to use `Camera.main`. That camera
   needs **HDR on** and **Post Processing on** (URP camera settings).
3. The URP renderer (`URP_Renderer.asset`) must carry the **TimeOfDay** renderer feature, with its
   two shaders assigned (`Hidden/CirrusFS/TimeOfDay/AerialPerspective` and `.../ExposureMeter`).
   It is already set up in this project.
4. Lights the system does not drive itself (landing, taxi, strobe, runway, cockpit flood): add
   `PreExposedLight` and set its intensity. Glowing materials (displays, light lenses): add
   `PreExposedEmission`. Without these they are ~30x too dim at night (see Exposure).
5. Cockpit: put `InteriorAmbientZone` on the cockpit mesh (scale ~0.3). Otherwise the interior is
   lit by the whole open sky.

The prefab holds:

| Component | Does |
|---|---|
| `TimeOfDaySystem` | Profile, quality tier, weather inputs, the `AtmosphereModel` |
| `TimeOfDayClock` | UTC date and time, local nautical zone, time scale |
| `SunMoonController` | Ephemerides; one shadow-casting light, handed from sun to moon at −6° |
| `AtmosphereRenderer` | LUTs (compute, with a fragment fallback), sky material, fog globals |
| `EnvironmentLighting` | Sky cubemap, ambient SH (async readback), reflection probes |
| `ExposureController` | Metering, eye adaptation, pre-exposure |
| `TimeOfDayDebugControls` / `Overlay` | Shortcuts and the on-screen readout |

Children: `Sun`, `Moon`, `Sky Reflection`, `Scene Reflection (High tier)`, `Grading` (global
Volume: ACES, saturation −5, priority 100).

## Weather: tying fog to visibility

```csharp
// METAR visibility in km, and the inversion height (top of the haze layer) if known.
TimeOfDaySystem.Active.SetVisibility(metarVisibilityKm, hazeTopMetres);
```

Visibility sets the density of a haze layer from sea level up to `hazeTopMeters`, using
Koschmieder's law at the ICAO 5% contrast threshold (extinction = 3.0 / visibility). Above the
haze top the air is clear and the haze shows as a band on the horizon. Changes under 3%
(`profile.fog.rebuildFraction`) are ignored. Larger ones rebuild the LUTs spread over several
frames (8 on Low and Medium, 4 on High), so a weather change never causes a hitch.

The fog colour is not a setting. It comes from the haze's albedo and phase function
(`profile.fog.hazeAlbedo`, `hazeAnisotropy`): ~0.9 albedo for wet haze (white), ~0.7 for smoke
or dust (greyer).

Transparent shaders (glass, particles) are not covered by the full-screen fog pass. They apply the
same haze themselves:

```hlsl
#include "Assets/Lighting/TimeOfDay/Shaders/TimeOfDayAerialPerspective.hlsl"
color.rgb = TodApplyAerialPerspective(color.rgb, positionWS);
```

## Exposure

Pre-exposure: every light, the ambient, reflections, sky and fog are multiplied by the current
exposure P before rendering. The image is stored near mid-grey at any time of day, so moonlit
scenes do not band in the 32-bit HDR target. The atmosphere LUTs and
`EnvironmentLighting.Ambient` stay in scene units.

- Read P in C# with `ExposureController.PreExposure`. Use `ExposureController.Expose(value)` for
  any light or emission set from code.
- In shaders, use `TodPreExposure()` (from `AtmosphereCommon.hlsl`). A custom emissive shader
  multiplies its emission by it.
- **Metering:** the renderer feature reduces the HDR image to 32×18 cells (log mean per cell),
  read back asynchronously. The cells are averaged linearly, centre-weighted.
- **Adaptation:** `brightenSpeed` 3/s, `darkenSpeed` 1/s.
- **Key:** 0.18 by day, falling to `nightKey` 0.05 at night, so moonlight reads as night. Clear
  noon meters log2 L ≈ −1 and full moonlight ≈ −11.
- **Views:** `cockpitBias` −0.7 EV, `externalBias` 0. `AircraftCamera` sets `inCockpit`.
- **Limits:** EV is clamped to [−4, +7]. The upper limit keeps a moonless night dark.
- **Cuts and teleports:** call `ExposureController.SnapExposure()` and
  `EnvironmentLighting.SnapAmbient()` to skip adaptation.

Tonemapping is ACES, on the prefab's Grading volume. Leave `Post Exposure` at 0: exposure is
handled before rendering.

## Tuning (profile: `Presets/TimeOfDayProfile_Earth.asset`)

| Want | Change |
|---|---|
| Brighter or darker overall | `exposure.compensation` (EV) |
| Darker nights | `exposure.nightKey` down, or `maxExposure` down |
| Brighter moonlit scenes | `sunMoon.moonIntensity` (deliberately lifted above physical) |
| Bluer or greyer haze | `fog.hazeAlbedo` |
| Whiter horizon away from the sun | `fog.hazeAnisotropy` down |
| Sharper haze top seen from above | `fog.hazeTransition` down |
| Fewer stars in twilight | `sky.starContrast` up |
| Cockpit too bright or dark | `InteriorAmbientZone.ambientScale`, `exposure.cockpitBias` |

Quality tiers (`Presets/TimeOfDayQuality_*.asset`) change only resolution and cadence, never
the look. Switch them at runtime with `TimeOfDaySystem.SetQuality(tier)`. Their shadow and LUT
settings are applied in play mode to a **runtime copy** of the URP asset; the asset on disk is not
modified.

| | Low | Medium | High |
|---|---|---|---|
| Sky-view LUT | 128×72 | 192×108 | 256×144 |
| Aerial LUT | 16×16×24 | 32×32×48 | 48×48×64 |
| Moonlit sky and haze | no | yes | yes |
| Reflection cube | 64 | 128 | 128 |
| Realtime scene probe | no | no | yes |
| Shadows | hard, 1.5 km, 1 cascade, 1024 | soft, 3 km, 2 cascades, 2048 | soft, 6 km, 2 cascades, 4096 |
| Grading LUT | 32 | 32 | 64 |

## Debug controls (play mode)

| Key | Action |
|---|---|
| F8 | overlay: time, sun and moon, exposure, fog, CPU and GPU cost per subsystem, time and visibility sliders |
| `[` `]` | time −15 / +15 min (Shift: 1 hour) |
| F9 | fast-forward 1× / 60× / 600× / 3600× |
| F10 | pause the clock |
| F7 | next quality tier |
| `-` `=` | visibility halved / doubled |
| F6 | snap exposure and ambient |

`Scenes/TimeOfDayTest.unity` is a test bench at Tokyo Haneda (RJTT) with the overlay on.
It has grey, white, black and chrome spheres, a pillar for checking shadow direction, a compass,
and distance markers at 1, 3, 10, 30 and 80 km.

## Cost

Measured on an RTX 3050 Laptop at 1080p (a GTX 1050 is roughly 3× slower):

| Item | GPU | When |
|---|---|---|
| Sky | 0.19–0.24 ms | every frame (sky pixels only) |
| Fog | 0.175 ms | every frame (ground pixels only; complements the sky) |
| Exposure meter | 0.01 ms | every frame |
| Sky-view LUT | 0.056 ms | when the sun moves 0.1° (Medium): every ~25 s |
| Aerial LUT | 0.063 ms | same |
| Multiple scattering | 0.022 ms × 8 frames | weather changes |
| Sky cube | 0.14 ms | with each sky LUT rebuild, and while exposure adapts |

CPU: about 25 µs per frame with no GC allocations. SH projection takes 37 µs per readback.

## Project changes this system relies on

- `URP.asset`: colour grading mode set to HDR, so the grading LUT does not band.
- `URP_Renderer.asset`: TimeOfDay renderer feature added. Depth copy changed to After Opaques,
  since the fog needs depth before transparents.
- Quality level "PC": Realtime Reflection Probes on (High tier's scene probe).
- `Ocean.shader`: skips its own fallback haze when `_TOD_FogActive` is set.

## Known limits

- No volumetric clouds or cloud shadows. The haze layer is the only weather.
- Moonlight is lifted far above its physical level (as in X-Plane) so night is readable. Stars
  are procedural, not a catalogue.
- Reflection probe intensity cannot carry exposure (Unity clamps it), so the sky cube is
  re-rendered when exposure drifts by ¼ stop.
- The ocean's deep-water colour is unlit and ignores exposure, so it glows faintly at night.
