# Airport Exporter: OurAirports → CirrusFS

This tool downloads the [OurAirports](https://ourairports.com/data/) database and writes a
cleaned copy that Unity reads at start. OurAirports is public domain and is updated every
night. It has no API; the data is published as CSV files at
`https://davidmegginson.github.io/ourairports-data/`.

The whole world is small: about 20 MB downloaded and **10.5 MB exported**. Run the tool again
whenever you want fresher data.

## Install

Python 3.9 or newer. Only the standard library is used (`py -3.13` works on this PC).

## Workflow

1. Run `py -3.13 airport_exporter.py`, then **Load settings…** and pick
   `settings_global.json`. It contains:
   - **Download folder** `G:/TerrainSources/OurAirports`.
   - **Output folder** `G:/Unity/CirrusFS/AirportData`. It is outside `Assets/` and ignored
     by git, like `TerrainData`.
   - The whole world, with small, medium and large airports.
2. Click **Export**. It first downloads any file that is missing or older than
   **Re-download after** (24 h), then writes the output. It takes about 20 s.
   **Download now** fetches fresh files regardless of their age.
3. Press Play in Unity. There's no import step.

Headless:

```
py -3.13 airport_exporter.py --config settings_global.json --nogui --download
```

## Filters (Source & filter tab)

| Setting | Default | Notes |
|---|---|---|
| Region box | whole world | Airports whose reference point is inside the box. Navaids are filtered by the same box. |
| Large / medium / small airports | on | |
| Heliports, seaplane bases, balloonports | off | Not drawn yet. |
| Closed airports, closed runways | off | |
| Water runways | off | Not drawn. |
| Approx runways | on | See below. |
| Minimum runway length | 0 m | |
| Airports without runways | off | They can't be drawn or spawned at. |

## What the cleaning does

- **Units.** Feet become metres. Headings are true headings, 0–360°.
- **Every runway has both end coordinates.**
  - **Surveyed** runways: OurAirports gives both ends. These are almost all runways at large
    and medium airports.
  - **Approx** runways: OurAirports gives only a length. These are mostly small airfields.
    The runway is centred on the airport reference point. Its heading comes from the true
    heading if listed. Otherwise it comes from the runway number plus the magnetic variation
    of the nearest navaid that lists one; on surveyed runways this method is off by a median
    of 2.9° (p90 6.6°). A compass designator such as `N` or `NE` is taken as a true heading.
    Single-runway airfields put their runway a median of 16 m from the reference point
    (p90 250 m), so approx runways are usually close to the real place.
  - **Stacking rule.** An approx runway within 30° of a runway already placed at the same
    airport is left out (longest first, surveyed first). That case is usually a parallel or a
    turf strip beside the paved one, and both would land on the same spot.
- **Bad surveyed ends become approx.** This happens when the ends are under 60 m or over
  7 km apart, or disagree with the declared length by more than ×0.4…×2.5.
- **Surface** is sorted into `asphalt, concrete, grass, dirt, gravel, sand, snow, water,
  other, unknown` from the free-text field. There are about 500 spellings. Australian
  single-letter codes (B, G, N, X, C, S) are read the ERSA way.
- **Width.** A missing or implausible width gets a default from the length and surface
  (15 / 23 / 30 / 45 m paved, 25–30 m unpaved). `width_source = default` marks it.
- **Elevations.** A missing runway-end elevation is taken from the other end, then from the
  airport.

A run on 2026-11-04 (whole world, defaults) gave:

- 29,164 airports: 24,135 small, 3,858 medium, 1,171 large.
- 33,894 runways: 14,306 surveyed, 19,588 approx.
- Surfaces: asphalt 13,191, grass 12,746, dirt 2,429, gravel 2,254, concrete 2,192,
  unknown 814.
- Left out: 829 closed runways, 350 stacked approx runways, 274 runways without a length,
  80 water runways, 61 runways without a heading.

## Output format (version 1)

```
<output>/manifest.json
<output>/airports.csv
<output>/runways.csv
<output>/airport-frequencies.csv
<output>/navaids.csv
```

The files are UTF-8 CSV with a header row and `\n` line ends. Values are quoted only when they
contain a comma or a quote. An empty value means unknown. `id` and `airport_id` are
OurAirports' own ids, so they stay stable between runs.

### airports.csv

| Column | Meaning |
|---|---|
| id | OurAirports airport id |
| ident | OurAirports ident (ICAO code where one exists, else a local/GPS code) |
| type | `large_airport`, `medium_airport`, `small_airport`, … |
| name | Name |
| lat, lon | Airport reference point, WGS84 degrees |
| elevation_m | Field elevation, metres MSL |
| country, country_name | ISO 3166 code and English name |
| region | ISO 3166-2 region code, e.g. `KR-41` |
| municipality | Served city |
| scheduled | 1 = scheduled airline service |
| icao, iata, gps, local | Codes, for search |
| keywords | OurAirports keywords (old names, other spellings), for search |
| runways | Runways exported for this airport |
| approx_runways | How many of those are approx |

### runways.csv

| Column | Meaning |
|---|---|
| id, airport_id, airport_ident | Runway id and its airport |
| le_ident, he_ident | Designators of the low and high end, e.g. `15L`, `33R` |
| le_lat, le_lon, he_lat, he_lon | End coordinates, WGS84 degrees. Always present |
| le_elevation_m, he_elevation_m | End elevations, metres MSL |
| le_heading, he_heading | True heading when landing on that end, degrees (from the coordinates) |
| length_m | Distance between the ends |
| declared_length_m | Length as listed by OurAirports |
| width_m, width_source | Width in metres; `data` or `default` |
| le_displaced_m, he_displaced_m | Displaced threshold at each end, metres |
| surface | Category (see above) |
| surface_raw | The original text |
| lighted | 1 = lit |
| position | `surveyed` or `approx` |

Landing on the `le` end means flying `le_heading`, from the `le` point toward the `he` point.

### airport-frequencies.csv

`id, airport_id, airport_ident, type (TWR, GND, ATIS, CTAF…), description, frequency_mhz`,
for the exported airports.

### navaids.csv

`id, ident, name, type (VOR, VOR-DME, NDB, DME, TACAN, VORTAC…), frequency_khz, lat, lon,
elevation_m, country, dme_frequency_khz, dme_channel, dme_lat, dme_lon, dme_elevation_m,
magnetic_variation_deg (east positive), usage, power, associated_airport`.

OurAirports has no ILS localizers or glideslopes.

### manifest.json

It holds:
- the format name and version, and the creation time;
- the source URL with each file's Last-Modified;
- the region and the settings used;
- counts by type and surface;
- the column lists.

## Unity

`Assets/Scripts/Geo/Core/Airports` loads `AirportData` from the project folder (beside the
player executable in a build). Runways are drawn by the terrain shader on the terrain surface,
with a cleared frame of ground around them. The start menu can search the airports by ICAO
code or name and spawn on a runway end.
