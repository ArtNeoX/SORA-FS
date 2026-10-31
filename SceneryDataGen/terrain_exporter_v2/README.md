# Terrain Tile Exporter, format 2: 1° tiles for the WGS84 globe

This tool exports **height** and **land-class** tiles on a fixed grid of 1° × 1°
latitude/longitude cells, the same way X-Plane splits the world into DSF tiles. It
replaces v1 (`../terrain_exporter_v1`).

v1 built square tiles in one UTM zone. Those squares don't line up with anything once you
leave that zone, and South Korea alone spans two zones. Here every tile on Earth has one
fixed definition:

- Tile `+37+127` is lat 37…38 N, lon 127…128 E. Its name is its south-west corner.
- It is stored in `tiles/+30+120/+37+127/`, grouped in 10° folders like X-Plane.
- **Its shared edge vertices are bit-identical with its neighbours' edge vertices**, even
  when the tiles were exported on different days or for different regions. Tiles never
  overlap and never leave gaps.

## Install

You need Python 3.9 or newer. On this PC, Python 3.13 already has the packages: run it
with `py -3.13`. On another machine, install them with:

```
pip install rasterio fiona shapely pyproj pillow numpy
```

## Workflow

1. **Start the tool.** Either way works:
   - `py -3.13 terrain_exporter.py`, then **Load settings…** and pick `settings_korea.json`.
   - Run the tool and fill in the tabs yourself.
2. **Region & grid tab.** Pick a preset or type a box. The box only selects which whole 1°
   cells to export; the info line lists them.
   - Keep **Height samples per degree** and **Land-class cells per degree** the same for
     every export into the same output folder. The exporter refuses a mismatch.
3. **Sources tab.**
   1. Set the **Download folder**. Use somewhere outside the Unity project, such as
      `G:/TerrainSources`.
   2. Click **Download sources**. It fetches:
      - Copernicus DEM tiles for the region **plus a one-tile ring around it** (edge
        vertices need the neighbour's data to match it later). Sea cells have no DEM
        file and are skipped.
      - ESA WorldCover land cover.
      - The OSM coastline. This is 930 MB and only needed once, for the whole world.
      - The Geofabrik OSM extract named in **OSM extract**, if any.

      Files you already have are reused, and the file lists fill themselves in.
4. **Land classes tab.** Usually leave it alone. If you edit the class list, do it
   before the first export: class IDs are part of the world definition.
5. **Output tab.** Set the **Output folder**. Use `G:/Unity/CirrusFS/TerrainData`, which
   is outside `Assets/` and ignored by git.
6. Click **Export tiles**. Check a few `preview.png` files in the tile folders. A preview
   is north-up: hillshaded colour by land class.
7. **Adding more of the world later.** Change the region, download its sources, and
   export into **the same output folder**. New tiles are added to `manifest.json` and
   match the existing ones at the edges.
   - Turn off **Overwrite existing tiles** to only fill in tiles that are missing.
   - Re-export a region (with overwrite on) after downloading a neighbour's DEM, so the
     tiles along that side are rebuilt from both sides' data.

Headless, from saved settings:

```
py -3.13 terrain_exporter.py --config settings_korea.json --nogui --download
```

### Choosing a resolution (fixed per output folder)

| Height samples / ° | Spacing at 37° N | Per tile | Korea (~30 land tiles) | Whole world (~19 k land tiles) |
|---|---|---|---|---|
| 3600 (1″, Copernicus native) | 31 × 25 m | 26 MB | 0.8 GB | 490 GB |
| 1200 (3″, default) | 92 × 74 m | 2.9 MB | 90 MB | 55 GB |
| 300 | 370 × 300 m | 0.2 MB | 5 MB | 3.5 GB |

Land class adds `m²` bytes per tile. That is 13 MB at the default of 3600 cells per
degree, or 0.36 MB at 600.

A practical setup is one coarse world folder plus a fine folder for the area you fly in.
The Unity loader can prefer the fine folder where it has a tile.

## Source data

All of these are free and need no account. The download button uses these URLs.

| What | Source | Notes |
|---|---|---|
| DEM | Copernicus GLO-30 (1″) or GLO-90 (3″), AWS open data | 1° GeoTIFFs. Samples sit on whole arcseconds, the same lattice as our vertices |
| Land cover | ESA WorldCover 2021 v200, 10 m | 3° GeoTIFFs. The default lookup table is for its codes |
| Coastline | `land-polygons-split-4326.zip` from osmdata.openstreetmap.de | Without it, land is wherever the DEM is above 0 m |
| OSM | Geofabrik extract, e.g. `asia/south-korea` | Read directly (layer `multipolygons`). Converting it once to GPKG in QGIS makes repeat runs faster |

You can add other DEMs (SRTM, ALOS, national DEMs in any projection) by hand. Each
source resolution is mosaicked separately and only warped once, so the edges stay exact.

## Output format (version 2)

```
<output>/manifest.json
<output>/tiles/<10° group>/<tile>/height.r16    uint16 little-endian, (n+1) × (n+1)
<output>/tiles/<10° group>/<tile>/class.u8      uint8, m × m
<output>/tiles/<10° group>/<tile>/height.f32    optional float32 metres, (n+1) × (n+1)
<output>/tiles/<10° group>/<tile>/preview.png   optional, north-up, for checking
```

### Heights

- Heights are vertices, `n` = samples per degree.
- Vertex `[i, j]` is at `lat + i/n`, `lon + j/n`. Compute it as `(lat·n + i) / n` so the
  shared edges get the same double.
- Row 0 is the south edge and column 0 is the west edge.
- The first and last rows and columns lie exactly on the degree lines and equal the
  neighbour's.
- Decode with `metres = min + value / 65535 · (max − min)`. The range is global: by
  default −500 … 9000 m, a 14.5 cm step.

### Land classes

- Land classes are cells, `m` = cells per degree.
- Cell `[i, j]` covers `lat + i/m … lat + (i+1)/m` (and the same for longitude), so
  every cell belongs to exactly one tile.
- Values are the class IDs listed in the manifest.

### Vertical datum

- Copernicus heights are above the EGM2008 geoid (≈ mean sea level).
- They are used as **heights above the WGS84 ellipsoid with no geoid correction**, the
  same assumption the `h = 0` ocean makes.
- So the coast meets the ocean. If a geoid is added later, it must be applied to the
  ocean and the terrain together.

### Sea

- Vertices outside the coastline are pushed to `seaHeight` (−5 m), under the ocean
  surface.
- Tiles that are all sea are not written: the globe ocean already covers them.

### Manifest

`manifest.json` holds:

- the grid definition, height encoding and class list;
- the regions exported so far;
- for each tile: `id`, `lat`, `lon`, `path`, height min/max, land fraction, and cell
  counts per class.

## How land class is decided (in order)

1. The land-cover value is mapped to a class through the lookup table. Nodata becomes
   the *default class*.
2. Optional: cells steeper than the slope threshold become the *rock class*.
3. Cells outside the coastline become the *water class*.
4. OSM rules are applied top to bottom; later rules win.
   - Rules apply on land only, unless marked `+land` (for example tidal flats beyond the
     OSM coastline).
   - A `+land` polygon also keeps real DEM heights instead of the sea floor.

Rule syntax is `key=value [& key=value] -> classId [+land]`. Values can be `*` or
`a|b|c`. Tags stored in OSM's `other_tags` (like `crop` or `wetland`) are matched too.

Class transitions are smoothed in the terrain shader by blending neighbouring cells. The
file stores hard class IDs, so the blend can be tuned without re-exporting.

## Unity

Export into `G:/Unity/CirrusFS/TerrainData` and press Play: the `Terrain` object in
`Flight.unity` (`GlobeTerrain`) reads `TerrainData/manifest.json` at start. No import step,
and no Unity restart after exporting more tiles (a new play session picks them up).

- The loader lives in `Assets/Scripts/Geo` (`Core/Terrain`, `World/Terrain`). It is described in
  `Assets/Scripts/Geo/README.md` §5: quadtree LOD per tile, chunks placed through the floating
  origin, collision under the aircraft, class-blending shader.
- Several folders can be listed in `GlobeTerrain.dataFolders` (earlier ones win), e.g. a fine
  Korea folder in front of a coarse world folder.
- F1 in flight shows the ground height, AGL, current tile and the terrain chunk counts.
- `TerrainTiles.cs` here is a minimal standalone reader of the format, kept for reference.
