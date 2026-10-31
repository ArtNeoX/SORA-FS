# Terrain Tile Exporter

Exports **heights** and **land-class** tiles for any region on a fixed projected grid,
as plain binary files that Unity can read directly (`TerrainTiles.cs`).

## Install

Python 3.9+ (the python.org installer includes Tkinter on Windows), then:

```
pip install rasterio fiona shapely pyproj pillow numpy
```

## Run

```
python terrain_exporter.py                                 # settings window
python terrain_exporter.py --config my.json --nogui        # headless, from saved settings
```

Settings window tabs:

* **Region & grid**: lon/lat box (or a preset) and the tile grid. Keep CRS, tile size,
  resolutions and height range the same for every export of your world. Tile IDs are
  absolute (`col = floor(x / tileSize)`), so separate exports (Seoul now, Busan later)
  slot into the same folder and match at the edges. A live estimate shows tile count
  and disk size.
* **Sources**: DEM rasters, land-cover rasters, coastline polygons, OSM polygons.
* **Land classes**: class list (id, name, preview colour), the land-cover lookup table,
  and OSM override rules (all editable text).
* **Output**: folder, global height range, sea-floor height, previews, worker count.

Use **Save settings…** to keep a config; the same JSON works with `--nogui`.

## Source data

| What | Where | Notes |
|---|---|---|
| DEM | Copernicus GLO-30 (OpenTopography or the AWS `copernicus-dem-30m` bucket) | Add the whole folder of GeoTIFFs; tiles are mosaicked seamlessly |
| Land cover | ESA WorldCover 10 m (esa-worldcover.org) | Default lookup table is for WorldCover codes |
| Coastline | `land-polygons-split-4326.zip` from osmdata.openstreetmap.de | Without it, "land" = wherever the DEM has data |
| OSM | `south-korea-latest.osm.pbf` from Geofabrik | Read directly (layer `multipolygons`), or convert to GPKG once in QGIS for faster repeat runs |

## Output format (version 1)

```
<output>/manifest.json
<output>/tiles/<col>_<row>/height.r16    uint16 little-endian, N×N
<output>/tiles/<col>_<row>/class.u8      uint8, M×M
<output>/tiles/<col>_<row>/height.f32    optional float32 metres, N×N
<output>/tiles/<col>_<row>/preview.png   optional, north-up, for checking
```

* **Row order is south → north, columns west → east** (row 0 = south edge), the same as
  Unity's `TerrainData.SetHeights` `[z, x]`.
* **Heights are vertices.** N = tileSize/res + 1 (1025 by default). The first and last
  row and column lie exactly on the tile edges and are bit-identical to the neighbour's,
  so terrain seams close. `metres = min + value / 65535 * (max - min)`.
* **Land classes are cells.** M = tileSize/res (1024 by default); cell (0,0) covers the
  south-west corner. Values are your class IDs from the manifest.
* Tile `x, y` in the manifest is its south-west corner in projected metres (e.g. UTM 52N).
  Unity position = `(x - originX, heightMin, y - originY)` with your floating origin.
* The manifest also lists per tile: centre lat/lon, height min/max, land fraction and
  cell counts per class (handy for deciding what autogen a tile needs).

## How land class is decided (in order)

1. Land-cover value → class via the lookup table (nodata → *default class*).
2. Optional: slope above the threshold → *rock class*.
3. Outside the coastline → *water class*.
4. OSM rules, top to bottom; later rules win. Rules apply on land only, unless marked
   `+land` (e.g. tidal flats beyond the OSM coastline). A `+land` polygon also keeps real
   DEM heights instead of the sea floor.

Rule syntax: `key=value [& key=value] -> classId [+land]`. Values can be `*` or `a|b|c`;
tags stored in OSM's `other_tags` (like `crop`, `wetland`) are matched too.

## Unity

`TerrainTiles.cs` loads the manifest, heights (metres or 0–1), class grid, a point-filtered
R8 class texture for your shader, and has `TerrainTile.SampleHeight` / `ClassAt` for
autogen placement. `TerrainTiles.CreateTerrain(...)` builds a test Terrain for one tile.
