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

A tile holds `(n+1)² × 2` bytes of heights plus `m²` bytes of land class. The world has
**26,475 land tiles** (Copernicus' own count; open-sea cells are not exported).

| Use | Height / ° (spacing) | Class / ° (cell) | Per tile | World |
|---|---|---|---|---|
| Detailed region (`settings_korea.json`) | 1200 (~90 m) | 3600 (~30 m) | 15.8 MB | 420 GB |
| **Global base (`settings_global.json`)** | 400 (~280 m) | 600 (~185 m) | 0.68 MB | **18 GB** |
| Smaller global base | 300 (~370 m) | 600 (~185 m) | 0.54 MB | 14 GB |

Previews add about 2 MB per tile; the global settings turn them off.

## Global base plus detailed regions

The intended setup, for a small install that still covers the world:

```
TerrainData/
  Global/     whole world at 400 / 600   (~18 GB)
  Korea/      detailed region at 1200 / 3600
  Tokyo/      ...
```

The Unity loader reads every output folder inside `TerrainData` and **draws the most
detailed tile of each 1° cell**. Where a detailed tile borders a global-only cell, its
heights fade into the global surface over about 2 km, so the two meet without a step. Each
folder has its own resolution; only the class list must be the same (keep the defaults).

**Exporting the world**

1. Load `settings_global.json`. It selects the whole world, GLO-90, no OSM, no previews,
   output `TerrainData/Global`, and **Overwrite off**, so a cancelled run resumes where it
   stopped.
2. **Download sources**, once. 71 GB of GLO-90 DEM (26,475 files) and 124 GB of WorldCover
   (2,651 files), so about 196 GB of temporary disk. The download uses Copernicus' tile
   list and the WorldCover listing, so open-sea cells are never requested, and it fetches
   6 files at once (**Parallel** on the Sources tab). Cancel and press it again at any
   time: finished files are kept.
3. **Export tiles.** Roughly 0.4 s per tile with 4 workers, so 3–4 hours for the world.
   It runs in 10° blocks, each reading only its own coastline, and saves the manifest
   every 30 s.
4. After the export, the 196 GB of sources can be deleted. Keep them only if you plan to
   re-export at another resolution.

**Adding a detailed region** (a city, a country): use `settings_korea.json` as a
template, set the region box, and set the output folder to a new folder inside
`TerrainData`, such as `TerrainData/Tokyo`. Use GLO-30 and an OSM extract for the best
detail.

**Exporting at world scale.**
- The coastline shapefile is converted once to a GeoPackage beside it, which has a spatial
  index, so each block reads only its own polygons.
- Source headers are cached in `<output>/_source_index.json`.
- Land cover is read from WorldCover's built-in overviews when the output is much coarser
  than 10 m, for example the 8× overview for 600 cells/°.

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

Export into `G:/Unity/CirrusFS/TerrainData` or a folder inside it, and press Play. The
`Terrain` object in `Flight.unity` (`GlobeTerrain`) reads every `manifest.json` in
`TerrainData` and its direct subfolders at start. There's no import step, and no Unity
restart after exporting more tiles: a new play session picks them up.

- The loader lives in `Assets/Scripts/Geo` (`Core/Terrain`, `World/Terrain`). It is described in
  `Assets/Scripts/Geo/README.md` §5: quadtree LOD per tile, chunks placed through the floating
  origin, collision under the aircraft, class-blending shader.
- The most detailed folder wins in every cell, whatever the folders are called. Equal
  detail falls back to the order in `GlobeTerrain.dataFolders`.
- F1 in flight shows the ground height, AGL, current tile and the terrain chunk counts.
- `TerrainTiles.cs` here is a minimal standalone reader of the format, kept for reference.
