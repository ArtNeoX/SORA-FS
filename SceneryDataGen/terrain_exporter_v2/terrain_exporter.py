#!/usr/bin/env python3
"""
Terrain Tile Exporter (format 2: 1-degree geographic tiles)
===========================================================
Exports height and land-class tiles on a fixed 1 deg x 1 deg latitude/longitude grid,
the way X-Plane splits the world into DSF tiles. Every tile on Earth has one fixed
definition, so tiles exported at different times, for different regions, meet exactly
and never overlap.

Requirements (Windows/macOS/Linux, Python 3.9+):
    pip install rasterio fiona shapely pyproj pillow numpy

Run with the settings window:
    python terrain_exporter.py
Run headless from a saved settings file (optionally fetching the sources first):
    python terrain_exporter.py --config my_settings.json --nogui [--download]

Output layout (see README.md for the full format description):
    <output>/manifest.json
    <output>/tiles/+30+120/+37+127/height.r16   uint16 LE, (n+1) x (n+1) vertices, row 0 = south
    <output>/tiles/+30+120/+37+127/class.u8     uint8, m x m cells, row 0 = south
    <output>/tiles/+30+120/+37+127/height.f32   (optional) float32 LE metres
    <output>/tiles/+30+120/+37+127/preview.png  (optional) north-up colour preview
"""

from __future__ import annotations

import argparse
import json
import math
import os
import pickle
import queue
import re
import ssl
import sys
import threading
import time
import traceback
import urllib.error
import urllib.request
import zipfile
from collections import OrderedDict
from concurrent.futures import ProcessPoolExecutor, as_completed
from dataclasses import asdict, dataclass, field, fields

import numpy as np

try:
    import fiona
    import rasterio
    import rasterio.merge
    import shapely
    from PIL import Image
    from pyproj import Transformer
    from rasterio.features import rasterize
    from rasterio.transform import from_origin
    from rasterio.warp import Resampling, reproject, transform_bounds
    from shapely.geometry import box, shape
    from shapely.strtree import STRtree
except ImportError as e:  # pragma: no cover
    print(f"Missing dependency: {e}\n"
          "Install with:  pip install rasterio fiona shapely pyproj pillow numpy")
    sys.exit(1)

# GDAL can read .osm.pbf; Fiona just doesn't list it by default.
fiona.supported_drivers.setdefault("OSM", "r")

FORMAT_VERSION = 2
WGS84 = "EPSG:4326"

# WGS84 ellipsoid, for metric slopes (same constants as CirrusFS.Geo.GeoMath)
WGS84_A = 6378137.0
WGS84_E2 = (1 / 298.257223563) * (2 - 1 / 298.257223563)

# --------------------------------------------------------------------------
# Defaults
# --------------------------------------------------------------------------

DEFAULT_CLASSES = """\
# id, name, preview colour
0,  Water,             #2b5f8f
1,  Rice paddy,        #9fbf5a
2,  Cropland,          #c9b56a
3,  Greenhouses,       #e4e4dc
4,  Grass,             #8fb865
5,  Forest,            #2f5f2a
6,  Conifer forest,    #244a26
7,  Shrub,             #6f8a45
8,  Rock / bare,       #9a8f80
9,  Urban dense,       #6d6a68
10, Urban low,         #a39a90
11, Industrial,        #8a8078
12, Apartment complex, #c8b8a8
13, Airfield,          #b4c48a
14, Tidal flat,        #8a7d66
15, Beach / sand,      #e2d6a6
16, Wetland,           #5f7f5f
17, Snow / ice,        #eef2f6
"""

# ESA WorldCover codes -> class ids above. Other land-cover rasters: edit this.
DEFAULT_LUT = """\
# source value -> class id   (ESA WorldCover 2021)
10  -> 5    # tree cover
20  -> 7    # shrubland
30  -> 4    # grassland
40  -> 2    # cropland
50  -> 10   # built-up
60  -> 8    # bare / sparse
70  -> 17   # snow / ice
80  -> 0    # permanent water
90  -> 16   # herbaceous wetland
95  -> 16   # mangroves
100 -> 4    # moss / lichen
"""

# OSM polygon overrides, applied top to bottom (later lines win).
DEFAULT_RULES = """\
# key=value [& key=value ...] -> class [+land]
#   value may be * (any) or a|b|c.  Tags in other_tags are found too.
#   +land : polygon counts as land even outside the coastline (tidal flats)
landuse=farmland & crop=rice|paddy                     -> 1
landuse=paddy                                         -> 1
landuse=greenhouse_horticulture                       -> 3
landuse=plant_nursery                                 -> 3
landuse=residential                                   -> 10
landuse=commercial|retail                             -> 9
landuse=industrial|port|railway                       -> 11
leisure=golf_course                                   -> 4
natural=bare_rock|scree                               -> 8
natural=wetland                                       -> 16
natural=wetland & wetland=tidalflat                   -> 14 +land
natural=beach|sand                                    -> 15 +land
aeroway=aerodrome                                     -> 13
natural=water                                         -> 0
landuse=reservoir|basin                               -> 0
waterway=riverbank                                    -> 0
"""

# Whole-degree boxes (min lon, min lat, max lon, max lat). Tiles are 1 x 1 degree cells;
# a box only selects which cells to export.
PRESETS = {
    "Spawn tile (Osan, +37+127)": (127.0, 37.0, 128.0, 38.0),
    "Osan / Pyeongtaek": (126.0, 36.0, 128.0, 38.0),
    "Seoul / Gyeonggi": (126.0, 37.0, 128.0, 38.0),
    "Busan / Gimhae": (128.0, 35.0, 130.0, 36.0),
    "Jeju": (126.0, 33.0, 127.0, 34.0),
    "South Korea (all)": (124.0, 33.0, 131.0, 39.0),
}

RESAMPLING = {
    "auto": None,  # average when shrinking the source, cubic when enlarging it
    "average": Resampling.average,
    "cubic": Resampling.cubic,
    "cubic_spline": Resampling.cubic_spline,
    "bilinear": Resampling.bilinear,
    "lanczos": Resampling.lanczos,
    "nearest": Resampling.nearest,
    "mode": Resampling.mode,
}

COPERNICUS = {  # product -> (bucket, resolution code in the file name)
    "GLO-30": ("https://copernicus-dem-30m.s3.amazonaws.com", "10"),
    "GLO-90": ("https://copernicus-dem-90m.s3.amazonaws.com", "30"),
}
WORLDCOVER_URL = ("https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/"
                  "ESA_WorldCover_10m_2021_v200_{}_Map.tif")
LAND_POLYGONS_URL = "https://osmdata.openstreetmap.de/download/land-polygons-split-4326.zip"
GEOFABRIK_URL = "https://download.geofabrik.de/{}-latest.osm.pbf"


@dataclass
class Settings:
    region_name: str = "Spawn tile (Osan, +37+127)"
    min_lon: float = 127.0
    min_lat: float = 37.0
    max_lon: float = 128.0
    max_lat: float = 38.0
    height_per_deg: int = 1200
    class_per_deg: int = 3600

    dem_files: list = field(default_factory=list)
    dem_resampling: str = "auto"
    landcover_files: list = field(default_factory=list)
    landcover_resampling: str = "mode"
    landcover_nodata: int = 0
    land_polygons: str = ""
    osm_file: str = ""
    osm_layer: str = "multipolygons"

    download_dir: str = ""
    dem_product: str = "GLO-30"
    download_landcover: bool = True
    download_coastline: bool = True
    osm_extract: str = ""

    classes: str = DEFAULT_CLASSES
    lut: str = DEFAULT_LUT
    rules: str = DEFAULT_RULES
    default_class: int = 2
    water_class: int = 0
    rock_class: int = 8
    slope_rock_deg: float = 0.0

    output_dir: str = ""
    height_min: float = -500.0
    height_max: float = 9000.0
    sea_height: float = -5.0
    skip_sea_tiles: bool = True
    write_float_heights: bool = False
    write_previews: bool = True
    overwrite: bool = True
    workers: int = max(1, (os.cpu_count() or 2) // 2)

    @classmethod
    def from_dict(cls, d: dict) -> "Settings":
        names = {f.name for f in fields(cls)}
        return cls(**{k: v for k, v in d.items() if k in names})


# --------------------------------------------------------------------------
# Parsing helpers
# --------------------------------------------------------------------------

def _strip_comment(line: str) -> str:
    # '#' starts a comment, except in a colour like '#aabbcc' after a comma
    out = re.sub(r"(^|\s)#(?![0-9a-fA-F]{6}\b).*$", "", line)
    return out.strip()


def parse_classes(text: str) -> list[dict]:
    out = []
    for raw in text.splitlines():
        line = _strip_comment(raw)
        if not line:
            continue
        parts = [p.strip() for p in line.split(",")]
        if len(parts) < 2:
            raise ValueError(f"Bad class line: {raw!r}  (expected: id, name, #colour)")
        cid = int(parts[0])
        if not 0 <= cid <= 254:
            raise ValueError(f"Class id must be 0..254: {raw!r}")
        colour = parts[2] if len(parts) > 2 and parts[2] else "#ff00ff"
        out.append({"id": cid, "name": parts[1], "color": colour})
    if not out:
        raise ValueError("No classes defined")
    return out


def parse_lut(text: str, default_class: int) -> np.ndarray:
    lut = np.full(65536, default_class, dtype=np.uint8)
    for raw in text.splitlines():
        line = _strip_comment(raw)
        if not line:
            continue
        m = re.match(r"^(-?\d+)\s*(?:->|=)\s*(\d+)$", line)
        if not m:
            raise ValueError(f"Bad lookup line: {raw!r}  (expected: 40 -> 2)")
        lut[int(m.group(1)) & 0xFFFF] = int(m.group(2))
    return lut


@dataclass
class Rule:
    conds: list          # [(key, set(values) or None for *)]
    cls: int
    as_land: bool
    text: str


def parse_rules(text: str) -> list[Rule]:
    rules = []
    for raw in text.splitlines():
        line = _strip_comment(raw)
        if not line:
            continue
        m = re.match(r"^(.+?)\s*->\s*(\d+)\s*(\+land)?$", line)
        if not m:
            raise ValueError(f"Bad rule: {raw!r}  (expected: landuse=industrial -> 11)")
        conds = []
        for c in m.group(1).split("&"):
            c = c.strip()
            if "=" not in c:
                raise ValueError(f"Bad condition {c!r} in rule {raw!r}")
            k, v = (s.strip() for s in c.split("=", 1))
            conds.append((k, None if v == "*" else set(v.split("|"))))
        rules.append(Rule(conds, int(m.group(2)), bool(m.group(3)), line))
    return rules


_HSTORE = re.compile(r'"((?:[^"\\]|\\.)*)"=>"((?:[^"\\]|\\.)*)"')


def feature_tags(props: dict) -> dict:
    tags = {k: v for k, v in props.items() if v is not None and k != "other_tags"}
    other = props.get("other_tags")
    if other:
        tags.update(dict(_HSTORE.findall(other)))
    return {k: str(v) for k, v in tags.items()}


def rule_matches(rule: Rule, tags: dict) -> bool:
    for k, vals in rule.conds:
        v = tags.get(k)
        if v is None or (vals is not None and v not in vals):
            return False
    return True


def hex_rgb(c: str) -> tuple:
    c = c.lstrip("#")
    try:
        return tuple(int(c[i:i + 2], 16) for i in (0, 2, 4))
    except ValueError:
        return (255, 0, 255)


# --------------------------------------------------------------------------
# Grid: fixed 1 x 1 degree tiles, named by their south-west corner
# --------------------------------------------------------------------------

def tile_name(lat: int, lon: int) -> str:
    """X-Plane style: '+37+127', '-34+018', '+40-074'."""
    return f"{lat:+03d}{lon:+04d}"


def tile_dir(lat: int, lon: int) -> str:
    """Tile folder under the output root, grouped in 10 x 10 degree folders."""
    group = tile_name(math.floor(lat / 10) * 10, math.floor(lon / 10) * 10)
    return f"tiles/{group}/{tile_name(lat, lon)}"


def region_tiles(s: Settings) -> list[tuple[int, int]]:
    """Every 1-degree cell the region box touches, as (lat, lon) of its south-west corner."""
    lat0 = max(-90, math.floor(s.min_lat))
    lat1 = min(89, math.ceil(s.max_lat) - 1)
    lon0 = max(-180, math.floor(s.min_lon))
    lon1 = min(179, math.ceil(s.max_lon) - 1)
    return [(la, lo) for la in range(lat0, lat1 + 1) for lo in range(lon0, lon1 + 1)]


def spacing_m(per_deg: int, lat: float) -> tuple[float, float]:
    """North-south and east-west sample spacing in metres at a latitude."""
    phi = math.radians(lat)
    w = math.sqrt(1 - WGS84_E2 * math.sin(phi) ** 2)
    step = math.radians(1.0 / per_deg)
    return WGS84_A * (1 - WGS84_E2) / w ** 3 * step, WGS84_A / w * math.cos(phi) * step


def validate(s: Settings, need_output=True, need_dem=True) -> list[str]:
    errs = []
    if not (s.min_lon < s.max_lon and s.min_lat < s.max_lat):
        errs.append("Region: min lon/lat must be smaller than max lon/lat.")
    if not (-180 <= s.min_lon and s.max_lon <= 180 and -90 <= s.min_lat and s.max_lat <= 90):
        errs.append("Region: longitudes must be within -180..180 and latitudes within -90..90.")
    if not 2 <= int(s.height_per_deg) <= 3600:
        errs.append("Height samples per degree must be 2..3600.")
    if not 2 <= int(s.class_per_deg) <= 12000:
        errs.append("Land-class cells per degree must be 2..12000.")
    if s.height_max <= s.height_min:
        errs.append("Height max must be greater than height min.")
    for name, val in (("DEM", s.dem_resampling), ("Land-cover", s.landcover_resampling)):
        if val not in RESAMPLING:
            errs.append(f"{name} resampling '{val}' is not one of {', '.join(RESAMPLING)}.")
    if need_dem and not s.dem_files:
        errs.append("Add at least one DEM file (or download them on the Sources tab).")
    for f in s.dem_files + s.landcover_files + [s.land_polygons, s.osm_file]:
        if f and not os.path.exists(f):
            errs.append(f"File not found: {f}")
    if need_output and not s.output_dir:
        errs.append("Choose an output folder.")
    try:
        classes = parse_classes(s.classes)
        ids = {c["id"] for c in classes}
        for label, v in (("Default", s.default_class), ("Water", s.water_class),
                         ("Rock", s.rock_class)):
            if v not in ids:
                errs.append(f"{label} class {v} is not in the class list.")
        parse_lut(s.lut, s.default_class)
        for r in parse_rules(s.rules):
            if r.cls not in ids:
                errs.append(f"Rule '{r.text}' uses unknown class {r.cls}.")
    except ValueError as e:
        errs.append(str(e))
    return errs


# --------------------------------------------------------------------------
# Source downloads (public data, no account needed)
# --------------------------------------------------------------------------

def _ns(lat: int) -> str:
    return f"{'N' if lat >= 0 else 'S'}{abs(lat):02d}"


def _ew(lon: int) -> str:
    return f"{'E' if lon >= 0 else 'W'}{abs(lon):03d}"


def source_downloads(s: Settings) -> list[tuple[str, str, str]]:
    """(kind, url, local path) for everything the region needs.

    DEM tiles include a one-tile ring around the region: the height samples on a tile's
    edge average source pixels from both sides, so the neighbour's DEM must be present
    for the edge to come out identical to the one the neighbour will get later."""
    tiles = region_tiles(s)
    jobs = []
    host, code = COPERNICUS[s.dem_product]
    ring = sorted({(la + dy, (lo + dx + 180) % 360 - 180)
                   for la, lo in tiles for dy in (-1, 0, 1) for dx in (-1, 0, 1)
                   if -90 <= la + dy <= 89})
    for la, lo in ring:
        name = f"Copernicus_DSM_COG_{code}_{_ns(la)}_00_{_ew(lo)}_00_DEM"
        jobs.append(("dem", f"{host}/{name}/{name}.tif",
                     os.path.join(s.download_dir, f"copernicus_{s.dem_product.lower()}",
                                  name + ".tif")))
    if s.download_landcover:
        cells = sorted({(math.floor(la / 3) * 3, math.floor(lo / 3) * 3) for la, lo in tiles})
        for la, lo in cells:
            key = _ns(la) + _ew(lo)
            jobs.append(("landcover", WORLDCOVER_URL.format(key),
                         os.path.join(s.download_dir, "worldcover",
                                      f"ESA_WorldCover_10m_2021_v200_{key}_Map.tif")))
    if s.download_coastline:
        jobs.append(("coastline", LAND_POLYGONS_URL,
                     os.path.join(s.download_dir, "land-polygons-split-4326.zip")))
    if s.osm_extract.strip():
        ex = s.osm_extract.strip().strip("/")
        jobs.append(("osm", GEOFABRIK_URL.format(ex),
                     os.path.join(s.download_dir, "osm", ex.split("/")[-1] + "-latest.osm.pbf")))
    return jobs


_SSL_CONTEXT = None


def _ssl_context():
    """Certificates from certifi when it is installed (it comes with rasterio's dependencies).

    Python on Windows otherwise trusts only what is in the Windows certificate store, which
    can hold an outdated Let's Encrypt root: every Let's Encrypt site (osmdata.openstreetmap.de,
    download.geofabrik.de) then fails with "certificate has expired" although its certificate
    is valid."""
    global _SSL_CONTEXT
    if _SSL_CONTEXT is None:
        try:
            import certifi
            _SSL_CONTEXT = ssl.create_default_context(cafile=certifi.where())
        except ImportError:
            _SSL_CONTEXT = ssl.create_default_context()
    return _SSL_CONTEXT


def _fetch(url: str, path: str, log, cancel: threading.Event) -> bool:
    """Download url to path. False if the server has no such file (open-sea DEM tiles)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": "CirrusFS-terrain-exporter"})
    part = path + ".part"
    try:
        with urllib.request.urlopen(req, timeout=60, context=_ssl_context()) as r, open(part, "wb") as f:
            total = int(r.headers.get("Content-Length") or 0)
            log(f"  downloading {os.path.basename(path)} ({total / 1e6:,.0f} MB)")
            while True:
                if cancel.is_set():
                    break
                chunk = r.read(1 << 20)
                if not chunk:
                    break
                f.write(chunk)
    except urllib.error.HTTPError as e:
        if e.code in (403, 404):
            return False
        raise
    except BaseException:
        if os.path.exists(part):
            os.remove(part)     # never leave a half file that looks finished later
        raise
    if cancel.is_set():
        os.remove(part)
        return False
    os.replace(part, path)
    return True


def _find_shp(folder: str) -> str:
    for base, _, files in os.walk(folder):
        for f in files:
            if f.lower().endswith(".shp"):
                return os.path.join(base, f)
    return ""


def run_downloads(s: Settings, log, progress, cancel: threading.Event) -> dict | None:
    """Fetch the region's sources into s.download_dir (existing files are reused).
    Returns the files to use: {"dem_files", "landcover_files", "land_polygons", "osm_file"}."""
    if not s.download_dir:
        log("ERROR: choose a download folder first.")
        return None
    if s.dem_product not in COPERNICUS:
        log(f"ERROR: unknown DEM product {s.dem_product!r}.")
        return None
    jobs = source_downloads(s)
    log(f"{len(jobs)} source files for '{s.region_name}' -> {s.download_dir}")
    found = {"dem_files": [], "landcover_files": [], "land_polygons": "", "osm_file": ""}
    no_data = 0
    failed = []
    for i, (kind, url, path) in enumerate(jobs):
        if cancel.is_set():
            log("Download cancelled.")
            return None
        progress(i, len(jobs))
        missing_marker = path + ".nodata"   # remembered 404s (open sea), so reruns skip them
        if os.path.exists(missing_marker):
            no_data += 1
            continue
        if not os.path.exists(path):
            try:
                fetched = _fetch(url, path, log, cancel)
            except (urllib.error.URLError, OSError) as e:
                # one unreachable server must not cost the files the others can give
                log(f"  FAILED {os.path.basename(path)}: {getattr(e, 'reason', e)}")
                failed.append((os.path.basename(path), url))
                continue
            if not fetched:
                if cancel.is_set():
                    continue
                open(missing_marker, "w").close()
                no_data += 1
                continue
        if kind == "dem":
            found["dem_files"].append(path)
        elif kind == "landcover":
            found["landcover_files"].append(path)
        elif kind == "osm":
            found["osm_file"] = path
        elif kind == "coastline":
            folder = os.path.join(s.download_dir, "land-polygons-split-4326")
            shp = _find_shp(folder)
            if not shp:
                log("  unpacking land polygons ...")
                with zipfile.ZipFile(path) as z:
                    z.extractall(s.download_dir)
                shp = _find_shp(folder)
            found["land_polygons"] = shp
    progress(len(jobs), len(jobs))
    log(f"Sources ready: {len(found['dem_files'])} DEM, {len(found['landcover_files'])} land "
        f"cover, coastline {'yes' if found['land_polygons'] else 'no'}, "
        f"OSM {'yes' if found['osm_file'] else 'no'}"
        f"{f'  ({no_data} cells have no DEM: open sea)' if no_data else ''}.")
    if failed:
        log(f"{len(failed)} file(s) could not be downloaded; press Download again to retry, "
            "or fetch them in a browser into the same folder:")
        for name, url in failed:
            log(f"  {name}  <-  {url}")
    return found


def merge_found(s: Settings, found: dict) -> None:
    for key in ("dem_files", "landcover_files"):
        cur = getattr(s, key)
        cur.extend(p for p in found[key] if p not in cur)
    for key in ("land_polygons", "osm_file"):
        if found[key]:
            setattr(s, key, found[key])


# --------------------------------------------------------------------------
# Source index: opened once, so workers only open the files a tile touches
# --------------------------------------------------------------------------

def index_rasters(paths: list[str]) -> list[dict]:
    out = []
    for p in paths:
        with rasterio.open(p) as ds:
            out.append({"path": p, "crs": ds.crs.to_wkt(), "bounds": tuple(ds.bounds),
                        "res": (abs(ds.transform.a), abs(ds.transform.e)),
                        "geographic": bool(ds.crs.is_geographic), "dtype": ds.dtypes[0]})
    return out


# --------------------------------------------------------------------------
# Vector preparation (runs once, cached for the workers)
# --------------------------------------------------------------------------

def _reprojector(src_crs, dst_crs):
    tr = Transformer.from_crs(src_crs, dst_crs, always_xy=True)

    def fn(coords):
        x, y = tr.transform(coords[:, 0], coords[:, 1])
        return np.column_stack([x, y])
    return fn


def _read_polys(path, layer, clip_box, log, per_feature=None):
    """Yield (geom in lon/lat, props) for polygons intersecting clip_box (lon/lat)."""
    kw = {"layer": layer} if layer else {}
    with fiona.open(path, **kw) as src:
        src_crs = src.crs_wkt or WGS84
        bb = transform_bounds(WGS84, src_crs, *clip_box.bounds, densify_pts=21)
        fn = _reprojector(src_crs, WGS84)
        n = 0
        for feat in src.filter(bbox=bb):
            if feat.geometry is None:
                continue
            props = dict(feat.properties)
            if per_feature is not None and not per_feature(props):
                continue
            g = shape(feat.geometry)
            if g.geom_type not in ("Polygon", "MultiPolygon"):
                continue
            g = shapely.transform(g, fn)
            if not g.is_valid:
                g = shapely.make_valid(g)
            g = g.intersection(clip_box)
            if g.is_empty:
                continue
            n += 1
            if n % 20000 == 0:
                log(f"  ... {n} polygons")
            yield g, props


def prepare_vectors(s: Settings, tiles, cache_path: str, log) -> None:
    lats = [la for la, _ in tiles]
    lons = [lo for _, lo in tiles]
    clip_box = box(min(lons) - 0.1, min(lats) - 0.1, max(lons) + 1.1, max(lats) + 1.1)
    data = {"land": [], "rules": []}

    if s.land_polygons:
        log(f"Reading land polygons: {os.path.basename(s.land_polygons)}")
        data["land"] = [g for g, _ in _read_polys(s.land_polygons, None, clip_box, log)]
        log(f"  {len(data['land'])} land polygons in region")
    else:
        log("No coastline file: land = DEM above 0 m. Add land polygons for a clean shore.")

    rules = parse_rules(s.rules)
    data["rules"] = [[] for _ in rules]
    if s.osm_file and rules:
        log(f"Reading OSM polygons: {os.path.basename(s.osm_file)} "
            f"(layer '{s.osm_layer or 'default'}')")
        keys = {k for r in rules for k, _ in r.conds}

        def keep(props):  # cheap pre-filter before geometry work
            if "other_tags" in props and props["other_tags"]:
                return True
            return any(props.get(k) for k in keys)

        for g, props in _read_polys(s.osm_file, s.osm_layer or None, clip_box, log, keep):
            tags = feature_tags(props)
            for i, r in enumerate(rules):
                if rule_matches(r, tags):
                    data["rules"][i].append(g)
        for r, gs in zip(rules, data["rules"]):
            log(f"  {len(gs):6d}  {r.text}")

    with open(cache_path, "wb") as f:
        pickle.dump(data, f, protocol=pickle.HIGHEST_PROTOCOL)


# --------------------------------------------------------------------------
# Per-tile export (runs in worker processes)
# --------------------------------------------------------------------------

def metric_gradient(h: np.ndarray, lat_top: float, step_deg: float):
    """dh/dx (east) and dh/dy (along rows, north-up array) in metres per metre."""
    lats = np.radians(lat_top - np.arange(h.shape[0]) * step_deg)
    w = np.sqrt(1 - WGS84_E2 * np.sin(lats) ** 2)
    step = math.radians(step_deg)
    dy = WGS84_A * (1 - WGS84_E2) / w ** 3 * step
    dx = np.maximum(WGS84_A / w * np.cos(lats) * step, 1e-3)
    gy, gx = np.gradient(h)
    return gx / dx[:, None], gy / dy[:, None]


class Exporter:
    MAX_OPEN = 48

    def __init__(self, s: Settings, cache_path: str, dem_index, lc_index):
        self.s = s
        self.classes = parse_classes(s.classes)
        self.lut = parse_lut(s.lut, s.default_class)
        self.rules = parse_rules(s.rules)
        self.palette = np.zeros((256, 3), np.uint8)
        self.palette[:] = (255, 0, 255)
        for c in self.classes:
            self.palette[c["id"]] = hex_rgb(c["color"])
        self.dem = dem_index
        self.lc = lc_index
        self.open_files: OrderedDict = OrderedDict()
        with open(cache_path, "rb") as f:
            vec = pickle.load(f)
        self.land = vec["land"]
        self.land_tree = STRtree(self.land) if self.land else None
        self.rule_geoms = vec["rules"]
        self.rule_trees = [STRtree(g) if g else None for g in self.rule_geoms]
        self.land_rule_idx = [i for i, r in enumerate(self.rules) if r.as_land]

        # land cover keeps the source's integer type when the nodata value fits in it
        nd = s.landcover_nodata
        dt = np.dtype(lc_index[0]["dtype"]) if lc_index else np.dtype(np.int32)
        if dt.kind not in "iu" or not np.iinfo(dt).min <= nd <= np.iinfo(dt).max:
            dt = np.dtype(np.int32)
        self.lc_dtype = dt

    def _ds(self, path):
        ds = self.open_files.pop(path, None) or rasterio.open(path)
        self.open_files[path] = ds
        while len(self.open_files) > self.MAX_OPEN:
            self.open_files.popitem(last=False)[1].close()
        return ds

    def close_files(self):
        """Closed after every tile; datasets left open crash the worker's exit."""
        while self.open_files:
            self.open_files.popitem()[1].close()

    # -- raster sampling ---------------------------------------------------
    def _warp(self, index, bounds, shape_, transform, resampling, dtype, nodata, continuous):
        """Mosaic the sources around `bounds` (lon/lat) and warp them onto the grid.

        Seams: a sample on a shared tile edge has the same lon/lat in both tiles and sees
        the same source pixels (the window always reaches past the edge), and the warp
        uses the exact transform, so both tiles get the same value."""
        out = np.full(shape_, nodata, dtype=dtype)
        dst_px = abs(transform.a)
        # One mosaic per pixel lattice (CRS, pixel size, pixel phase). Within a lattice
        # merge() only copies pixels; resampling happens once, in the exact warp below.
        # Mixed lattices (Copernicus gets coarser east-west above 50° N) are never
        # resampled onto each other, because that would depend on the read window and
        # two neighbouring tiles would disagree on their shared edge.
        groups = {}
        for e in index:
            rx, ry = e["res"]
            key = (e["crs"], round(rx, 12), round(ry, 12),
                   round((e["bounds"][0] / rx) % 1.0, 4), round((e["bounds"][3] / ry) % 1.0, 4))
            groups.setdefault(key, []).append(e)
        # finest lattice first: where two lattices both have data, the finer one wins
        for key in sorted(groups, key=lambda k: (k[1] * k[2], k)):
            group = groups[key]
            crs_wkt = key[0]
            rx, ry = group[0]["res"]
            pad = 3 * dst_px
            sb = transform_bounds(WGS84, crs_wkt, bounds[0] - pad, bounds[1] - pad,
                                  bounds[2] + pad, bounds[3] + pad, densify_pts=21)
            m = 4 * max(rx, ry)  # extra source pixels so kernels see real data at the border
            sb = (sb[0] - m, sb[1] - m, sb[2] + m, sb[3] + m)
            hits = [e for e in group if not (e["bounds"][2] < sb[0] or e["bounds"][0] > sb[2]
                                             or e["bounds"][3] < sb[1] or e["bounds"][1] > sb[3])]
            if not hits:
                continue
            # snap the window to the lattice so merge() copies whole pixels
            left, top = hits[0]["bounds"][0], hits[0]["bounds"][3]
            sb = (left + math.floor((sb[0] - left) / rx) * rx,
                  top - math.ceil((top - sb[1]) / ry) * ry,
                  left + math.ceil((sb[2] - left) / rx) * rx,
                  top - math.floor((top - sb[3]) / ry) * ry)
            datasets = [self._ds(e["path"]) for e in hits]
            arr, tr = rasterio.merge.merge(datasets, bounds=sb, res=(rx, ry),
                                           nodata=nodata, dtype=dtype)

            rs = resampling
            if rs is None:  # auto
                geo = hits[0]["geographic"]
                src_deg = max(rx, ry) if geo else max(rx, ry) / 111320.0
                rs = Resampling.average if dst_px >= src_deg * 0.999 else Resampling.cubic
            tmp = np.full(shape_, nodata, dtype=dtype)
            reproject(arr[0], tmp, src_transform=tr, src_crs=crs_wkt,
                      src_nodata=nodata, dst_transform=transform, dst_crs=WGS84,
                      dst_nodata=nodata, resampling=rs,
                      tolerance=0.0)  # exact transform -> identical shared edges
            fill = (out == nodata) & (tmp != nodata)
            out[fill] = tmp[fill]
        return out

    def _burn(self, geoms, tree, shape_, transform, tile_box):
        if tree is None:
            return np.zeros(shape_, bool)
        idx = tree.query(tile_box)
        if len(idx) == 0:
            return np.zeros(shape_, bool)
        shapes = [(geoms[i], 1) for i in idx]
        return rasterize(shapes, out_shape=shape_, transform=transform, fill=0,
                         dtype=np.uint8).astype(bool)

    def _land_mask(self, shape_, transform, tile_box, fallback):
        if self.land_tree is None:
            land = fallback.copy()          # no coastline data: land = DEM above 0 m
        else:
            land = self._burn(self.land, self.land_tree, shape_, transform, tile_box)
        for i in self.land_rule_idx:
            land |= self._burn(self.rule_geoms[i], self.rule_trees[i], shape_,
                               transform, tile_box)
        return land

    # -- main --------------------------------------------------------------
    def export_tile(self, lat: int, lon: int) -> dict:
        s = self.s
        n, m = int(s.height_per_deg), int(s.class_per_deg)
        d, cd = 1.0 / n, 1.0 / m
        tid = tile_name(lat, lon)
        rel = tile_dir(lat, lon)
        out_dir = os.path.join(s.output_dir, *rel.split("/"))
        tile_box = box(lon - d, lat - d, lon + 1 + d, lat + 1 + d)

        # Heights: (n+1)^2 vertices. Pixel centres sit on the vertices, so the first and
        # last rows/columns lie exactly on the degree lines shared with the neighbours.
        h_tr = from_origin(lon - d / 2, lat + 1 + d / 2, d, d)
        h_bounds = (lon - d / 2, lat - d / 2, lon + 1 + d / 2, lat + 1 + d / 2)
        NOD = -32768.0
        h = self._warp(self.dem, h_bounds, (n + 1, n + 1), h_tr,
                       RESAMPLING.get(s.dem_resampling), np.float32, NOD, continuous=True)
        valid = h > -32000
        above0 = valid & (h > 0)
        land_h = self._land_mask((n + 1, n + 1), h_tr, tile_box, above0)

        if s.skip_sea_tiles and not land_h.any():
            return {"id": tid, "skipped": True}

        h[~valid] = np.nan
        sea = ~land_h
        # sea: keep real bathymetry if the DEM has it, otherwise sea_height
        h[sea] = np.where(np.isnan(h[sea]), s.sea_height,
                          np.minimum(h[sea], s.sea_height))
        h[land_h & np.isnan(h)] = 0.0       # holes on land
        h = np.clip(h, s.height_min, s.height_max)

        # Land class: m^2 cells, each owned by exactly one tile. Read in strips so a
        # 10 m land-cover source never has to be held for the whole degree at once.
        c_tr = from_origin(lon, lat + 1, cd, cd)
        if self.lc:
            nd = s.landcover_nodata
            src_px = min(max(e["res"]) if e["geographic"] else max(e["res"]) / 111320.0
                         for e in self.lc)
            rows = max(1, int(m * 40e6 * src_px * src_px))
            raw = np.empty((m, m), self.lc_dtype)
            for r0 in range(0, m, rows):
                r1 = min(m, r0 + rows)
                top, bottom = lat + 1 - r0 * cd, lat + 1 - r1 * cd
                raw[r0:r1] = self._warp(self.lc, (lon, bottom, lon + 1, top), (r1 - r0, m),
                                        from_origin(lon, top, cd, cd),
                                        RESAMPLING.get(s.landcover_resampling, Resampling.mode),
                                        self.lc_dtype, nd, continuous=False)
            cls = self.lut[np.clip(raw.astype(np.int64), 0, 65535)].astype(np.uint8)
            cls[raw == nd] = s.default_class
        else:
            cls = np.full((m, m), s.default_class, np.uint8)

        # height vertex nearest to every class cell centre (works for any ratio)
        vi = np.clip(np.rint((np.arange(m) + 0.5) * n / m).astype(int), 0, n)
        vrow, vcol = vi[:, None], vi[None, :]          # north-up rows

        if s.slope_rock_deg > 0:
            gx, gy = metric_gradient(h, lat + 1, d)
            slope = np.degrees(np.arctan(np.hypot(gx, gy)))
            cls[slope[vrow, vcol] > s.slope_rock_deg] = s.rock_class

        land_c = self._land_mask((m, m), c_tr, tile_box, above0[vrow, vcol])
        cls[~land_c] = s.water_class
        for rule, geoms, tree in zip(self.rules, self.rule_geoms, self.rule_trees):
            mask = self._burn(geoms, tree, (m, m), c_tr, tile_box)
            if not rule.as_land:
                mask &= land_c
            cls[mask] = rule.cls

        # Write (flip so that row 0 = south)
        os.makedirs(out_dir, exist_ok=True)
        q = np.rint((h - s.height_min) / (s.height_max - s.height_min) * 65535.0)
        np.flipud(q).astype("<u2").tofile(os.path.join(out_dir, "height.r16"))
        np.flipud(cls).astype(np.uint8).tofile(os.path.join(out_dir, "class.u8"))
        f32 = os.path.join(out_dir, "height.f32")
        if s.write_float_heights:
            np.flipud(h).astype("<f4").tofile(f32)
        elif os.path.exists(f32):
            os.remove(f32)
        if s.write_previews:
            self._preview(h, cls, vrow, vcol, lat, os.path.join(out_dir, "preview.png"))

        counts = np.bincount(cls.ravel(), minlength=256)
        return {
            "id": tid, "lat": lat, "lon": lon, "path": rel,
            "hMin": round(float(h.min()), 2), "hMax": round(float(h.max()), 2),
            "landFraction": round(float(land_h.mean()), 4),
            "classes": {str(i): int(c) for i, c in enumerate(counts) if c},
        }

    def _preview(self, h, cls, vrow, vcol, lat, path):
        gx, gy = metric_gradient(h, lat + 1, 1.0 / self.s.height_per_deg)
        # simple hillshade, light from north-west
        az, alt = math.radians(315), math.radians(45)
        slope = np.arctan(np.hypot(gx, gy))
        aspect = np.arctan2(-gx, gy)
        shade = (np.sin(alt) * np.cos(slope) +
                 np.cos(alt) * np.sin(slope) * np.cos(az - aspect))
        shade = np.clip(0.45 + 0.75 * shade, 0.25, 1.2)[vrow, vcol]
        rgb = np.clip(self.palette[cls].astype(np.float32) * shade[..., None], 0, 255)
        img = Image.fromarray(rgb.astype(np.uint8), "RGB")
        if img.width > 1024:
            img = img.resize((1024, 1024), Image.BILINEAR)
        img.save(path, optimize=True)


_WORKER: Exporter | None = None


def _init_worker(settings_dict, cache_path, dem_index, lc_index):
    global _WORKER
    _WORKER = Exporter(Settings.from_dict(settings_dict), cache_path, dem_index, lc_index)


def _run_tile(lat, lon):
    try:
        return _WORKER.export_tile(lat, lon)
    except Exception:
        return {"id": tile_name(lat, lon), "error": traceback.format_exc()}
    finally:
        _WORKER.close_files()


# --------------------------------------------------------------------------
# Export driver
# --------------------------------------------------------------------------

def manifest_header(s: Settings) -> dict:
    n, m = int(s.height_per_deg), int(s.class_per_deg)
    return {
        "format": "CirrusTerrain",
        "version": FORMAT_VERSION,
        "grid": {
            "crs": WGS84,
            "tileDegrees": 1,
            "naming": "south-west corner, X-Plane style: +37+127 = lat 37..38 N, lon 127..128 E",
            "folders": "tiles/<10-degree group>/<tile>, e.g. tiles/+30+120/+37+127",
        },
        "verticalDatum": "DEM heights (Copernicus: EGM2008) used as heights above the WGS84 "
                         "ellipsoid, no geoid correction, matching the h = 0 ocean",
        "height": {
            "file": "height.r16", "encoding": "uint16le",
            "samplesPerDegree": n, "samples": n + 1,
            "min": s.height_min, "max": s.height_max,
            "decode": "meters = min + value / 65535 * (max - min)",
            "layout": "vertices; vertex [i, j] is at lat + i/n, lon + j/n; the first and last "
                      "row and column lie on the tile edges and equal the neighbour's",
            "rowOrder": "south_to_north",
            "floatFile": "height.f32" if s.write_float_heights else "",
        },
        "landClass": {
            "file": "class.u8", "encoding": "uint8",
            "cellsPerDegree": m, "pixels": m,
            "layout": "cells; cell [i, j] covers lat + i/m .. lat + (i+1)/m, "
                      "lon + j/m .. lon + (j+1)/m",
            "rowOrder": "south_to_north",
        },
        "seaHeight": s.sea_height,
        "waterClass": s.water_class,
        "classes": parse_classes(s.classes),
    }


def _grid_key(m: dict):
    return (m.get("format"), m.get("version"), m["height"].get("samplesPerDegree"),
            m["height"]["min"], m["height"]["max"], m["landClass"].get("cellsPerDegree"),
            tuple((c["id"], c["name"]) for c in m.get("classes", [])))


def run_export(s: Settings, log, progress, cancel: threading.Event) -> bool:
    errs = validate(s)
    if errs:
        for e in errs:
            log("ERROR: " + e)
        return False

    os.makedirs(os.path.join(s.output_dir, "tiles"), exist_ok=True)
    manifest_path = os.path.join(s.output_dir, "manifest.json")
    header = manifest_header(s)

    existing = {}
    regions = []
    if os.path.exists(manifest_path):
        with open(manifest_path, encoding="utf-8") as f:
            old = json.load(f)
        if old.get("version") != FORMAT_VERSION or _grid_key(old) != _grid_key(header):
            log("ERROR: the output folder already holds tiles made with different grid "
                "settings (samples per degree, height range, class list) or by an older "
                "exporter. Every tile of one world must match: restore those settings, or "
                "pick another folder.")
            return False
        existing = {t["id"]: t for t in old.get("tiles", [])}
        regions = old.get("regions", [])
        log(f"Adding to existing export ({len(existing)} tiles).")

    tiles = region_tiles(s)
    if not s.overwrite:
        todo = [(la, lo) for la, lo in tiles if tile_name(la, lo) not in existing]
    else:
        todo = tiles
    log(f"Region '{s.region_name}': {len(tiles)} one-degree tiles, {len(todo)} to export.")
    if not todo:
        return True

    t0 = time.time()
    log("Indexing sources ...")
    dem_index = index_rasters(s.dem_files)
    lc_index = index_rasters(s.landcover_files)
    cache_path = os.path.join(s.output_dir, "_vector_cache.pkl")
    prepare_vectors(s, todo, cache_path, log)
    if cancel.is_set():
        return False

    results = dict(existing)
    done = skipped = failed = 0

    def save_manifest():
        m = dict(header)
        m["regions"] = regions + [{
            "name": s.region_name,
            "bbox": [s.min_lon, s.min_lat, s.max_lon, s.max_lat],
            "exported": time.strftime("%Y-%m-%d %H:%M:%S"),
        }]
        m["tiles"] = sorted(results.values(), key=lambda t: (t["lat"], t["lon"]))
        tmp = manifest_path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(m, f, indent=1)
        os.replace(tmp, manifest_path)

    def handle(res):
        nonlocal done, skipped, failed
        done += 1
        if "error" in res:
            failed += 1
            log(f"Tile {res['id']} FAILED:\n{res['error']}")
        elif res.get("skipped"):
            skipped += 1
            results.pop(res["id"], None)
            log(f"  {res['id']}  all sea, skipped")
        else:
            results[res["id"]] = res
            log(f"  {res['id']}  land {res['landFraction'] * 100:5.1f} %   "
                f"heights {res['hMin']:.0f} .. {res['hMax']:.0f} m")
        progress(done, len(todo))
        if done % 20 == 0:
            save_manifest()

    workers = max(1, min(int(s.workers), len(todo)))
    log(f"Exporting with {workers} worker(s)...")
    init_args = (asdict(s), cache_path, dem_index, lc_index)
    if workers == 1:
        _init_worker(*init_args)
        for la, lo in todo:
            if cancel.is_set():
                break
            handle(_run_tile(la, lo))
    else:
        with ProcessPoolExecutor(max_workers=workers, initializer=_init_worker,
                                 initargs=init_args) as pool:
            futs = [pool.submit(_run_tile, la, lo) for la, lo in todo]
            for fut in as_completed(futs):
                handle(fut.result())
                if cancel.is_set():
                    for f in futs:
                        f.cancel()
                    break

    save_manifest()
    try:
        os.remove(cache_path)
    except OSError:
        pass
    status = "Cancelled" if cancel.is_set() else "Done"
    log(f"{status}: {done - skipped - failed} written, {skipped} all-sea skipped, "
        f"{failed} failed, {time.time() - t0:.0f}s. Manifest: {manifest_path}")
    return failed == 0 and not cancel.is_set()


# --------------------------------------------------------------------------
# GUI (Tkinter)
# --------------------------------------------------------------------------

def run_gui(initial: Settings, config_path: str | None):
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk
    from tkinter.scrolledtext import ScrolledText

    root = tk.Tk()
    root.title("Terrain Tile Exporter (1° tiles)")
    root.geometry("940x800")
    root.minsize(780, 640)

    vars_: dict[str, tk.Variable] = {}
    texts: dict[str, tk.Text] = {}
    lists: dict[str, tk.Listbox] = {}

    def var_for(name, value):
        if isinstance(value, bool):
            v = tk.BooleanVar(value=value)
        elif isinstance(value, int):
            v = tk.IntVar(value=value)
        elif isinstance(value, float):
            v = tk.DoubleVar(value=value)
        else:
            v = tk.StringVar(value=value)
        vars_[name] = v
        return v

    def collect() -> Settings:
        d = {}
        for k, v in vars_.items():
            try:
                d[k] = v.get()
            except tk.TclError:
                raise ValueError(f"Invalid value in field '{k}'")
        for k, t in texts.items():
            d[k] = t.get("1.0", "end-1c")
        for k, lb in lists.items():
            d[k] = list(lb.get(0, "end"))
        return Settings.from_dict(d)

    def apply(s: Settings):
        for f in fields(Settings):
            val = getattr(s, f.name)
            if f.name in vars_:
                vars_[f.name].set(val)
            elif f.name in texts:
                texts[f.name].delete("1.0", "end")
                texts[f.name].insert("1.0", val)
            elif f.name in lists:
                lists[f.name].delete(0, "end")
                for p in val:
                    lists[f.name].insert("end", p)
        update_info()

    # ---- layout helpers ----
    def row(parent, r, label, name, value, width=14, tip=None, choices=None):
        ttk.Label(parent, text=label).grid(row=r, column=0, sticky="w", padx=6, pady=3)
        v = var_for(name, value)
        if isinstance(value, bool):
            w = ttk.Checkbutton(parent, variable=v)
        elif choices:
            w = ttk.Combobox(parent, textvariable=v, values=choices, width=width - 2)
        else:
            w = ttk.Entry(parent, textvariable=v, width=width)
        w.grid(row=r, column=1, sticky="w", padx=6, pady=3)
        if tip:
            ttk.Label(parent, text=tip, foreground="#777").grid(
                row=r, column=2, sticky="w", padx=6)
        return w

    def file_row(parent, r, label, name, value, folder=False, types=None):
        ttk.Label(parent, text=label).grid(row=r, column=0, sticky="w", padx=6, pady=3)
        v = var_for(name, value)
        ttk.Entry(parent, textvariable=v).grid(row=r, column=1, sticky="ew", padx=6, pady=3)

        def browse():
            if folder:
                p = filedialog.askdirectory()
            else:
                p = filedialog.askopenfilename(filetypes=types or [("All files", "*.*")])
            if p:
                v.set(p)
        ttk.Button(parent, text="Browse…", command=browse).grid(row=r, column=2, padx=6)
        parent.columnconfigure(1, weight=1)

    def file_list(parent, label, name, value, exts):
        frm = ttk.LabelFrame(parent, text=label)
        frm.pack(fill="both", expand=True, padx=8, pady=4)
        lb = tk.Listbox(frm, height=4, selectmode="extended")
        lb.pack(side="left", fill="both", expand=True, padx=6, pady=6)
        lists[name] = lb
        for p in value:
            lb.insert("end", p)
        btns = ttk.Frame(frm)
        btns.pack(side="right", fill="y", padx=6, pady=6)

        def add_files():
            for p in filedialog.askopenfilenames(
                    filetypes=[("Rasters", " ".join("*" + e for e in exts)),
                               ("All files", "*.*")]):
                if p not in lb.get(0, "end"):
                    lb.insert("end", p)
            update_info()

        def add_folder():
            d = filedialog.askdirectory()
            if not d:
                return
            n = 0
            have = set(lb.get(0, "end"))
            for base, _, files in os.walk(d):
                for f in sorted(files):
                    p = os.path.join(base, f)
                    if f.lower().endswith(exts) and p not in have:
                        lb.insert("end", p)
                        n += 1
            log(f"Added {n} files from {d}")
            update_info()

        def remove():
            for i in reversed(lb.curselection()):
                lb.delete(i)

        for text, cmd in (("Add files…", add_files), ("Add folder…", add_folder),
                          ("Remove", remove), ("Clear", lambda: lb.delete(0, "end"))):
            ttk.Button(btns, text=text, command=cmd, width=12).pack(pady=1)

    def text_box(parent, label, name, value, height=10):
        frm = ttk.LabelFrame(parent, text=label)
        frm.pack(fill="both", expand=True, padx=8, pady=6)
        t = ScrolledText(frm, height=height, font=("Consolas", 10), undo=True)
        t.pack(fill="both", expand=True, padx=4, pady=4)
        t.insert("1.0", value)
        texts[name] = t

    nb = ttk.Notebook(root)
    nb.pack(fill="both", expand=True, padx=8, pady=(8, 4))
    s = initial

    # ---- Tab: Region & grid ----
    tab = ttk.Frame(nb)
    nb.add(tab, text="Region & grid")
    reg = ttk.LabelFrame(tab, text="Region (WGS84 degrees). Tiles are whole 1° × 1° cells; "
                                   "the box picks which cells to export.")
    reg.pack(fill="x", padx=8, pady=6)
    ttk.Label(reg, text="Preset").grid(row=0, column=0, sticky="w", padx=6, pady=3)
    preset = ttk.Combobox(reg, values=list(PRESETS), state="readonly", width=32)
    preset.grid(row=0, column=1, columnspan=2, sticky="w", padx=6, pady=3)

    def on_preset(_=None):
        name = preset.get()
        b = PRESETS[name]
        vars_["region_name"].set(name)
        for k, v in zip(("min_lon", "min_lat", "max_lon", "max_lat"), b):
            vars_[k].set(v)
        update_info()
    preset.bind("<<ComboboxSelected>>", on_preset)
    row(reg, 1, "Region name", "region_name", s.region_name, width=34)
    row(reg, 2, "Min longitude (west)", "min_lon", s.min_lon)
    row(reg, 3, "Min latitude (south)", "min_lat", s.min_lat)
    row(reg, 4, "Max longitude (east)", "max_lon", s.max_lon)
    row(reg, 5, "Max latitude (north)", "max_lat", s.max_lat)

    grd = ttk.LabelFrame(tab, text="Tile resolution (keep identical for every export into the "
                                   "same output folder)")
    grd.pack(fill="x", padx=8, pady=6)
    row(grd, 0, "Height samples per degree", "height_per_deg", s.height_per_deg,
        choices=[300, 600, 1200, 1800, 3600],
        tip="3600 = 1″ (~30 m, Copernicus native), 1200 = 3″ (~90 m)")
    row(grd, 1, "Land-class cells per degree", "class_per_deg", s.class_per_deg,
        choices=[600, 1200, 1800, 3600, 7200],
        tip="3600 = 1″ (~30 m); WorldCover itself is 12000")

    info = ttk.Label(tab, text="", foreground="#2a6", justify="left")
    info.pack(fill="x", padx=14, pady=10)

    # ---- Tab: Sources ----
    tab = ttk.Frame(nb)
    nb.add(tab, text="Sources")
    dl = ttk.LabelFrame(tab, text="Download public data for the region (free, no account)")
    dl.pack(fill="x", padx=8, pady=4)
    file_row(dl, 0, "Download folder", "download_dir", s.download_dir, folder=True)
    opts = ttk.Frame(dl)
    opts.grid(row=1, column=0, columnspan=3, sticky="w", padx=4)
    ttk.Label(opts, text="DEM").pack(side="left", padx=(2, 4))
    ttk.Combobox(opts, textvariable=var_for("dem_product", s.dem_product),
                 values=list(COPERNICUS), state="readonly", width=8).pack(side="left")
    ttk.Checkbutton(opts, text="Land cover (WorldCover)",
                    variable=var_for("download_landcover", s.download_landcover)).pack(
        side="left", padx=8)
    ttk.Checkbutton(opts, text="Coastline (930 MB, once)",
                    variable=var_for("download_coastline", s.download_coastline)).pack(
        side="left", padx=8)
    ttk.Label(opts, text="OSM extract").pack(side="left", padx=(8, 4))
    ttk.Entry(opts, textvariable=var_for("osm_extract", s.osm_extract), width=18).pack(
        side="left")
    ttk.Label(opts, text="e.g. asia/south-korea", foreground="#777").pack(side="left", padx=4)

    file_list(tab, "Elevation (DEM) rasters, e.g. Copernicus GLO-30 GeoTIFFs", "dem_files",
              s.dem_files, (".tif", ".tiff", ".vrt", ".img", ".hgt", ".dem"))
    opt = ttk.Frame(tab)
    opt.pack(fill="x", padx=8)
    ttk.Label(opt, text="DEM resampling").pack(side="left", padx=6)
    ttk.Combobox(opt, textvariable=var_for("dem_resampling", s.dem_resampling),
                 values=["auto", "average", "cubic", "cubic_spline", "bilinear", "lanczos"],
                 state="readonly", width=14).pack(side="left")
    ttk.Label(opt, text="auto = average when shrinking, cubic when enlarging",
              foreground="#777").pack(side="left", padx=6)
    file_list(tab, "Land-cover rasters, e.g. ESA WorldCover (optional)", "landcover_files",
              s.landcover_files, (".tif", ".tiff", ".vrt", ".img"))
    opt = ttk.Frame(tab)
    opt.pack(fill="x", padx=8)
    ttk.Label(opt, text="Land-cover resampling").pack(side="left", padx=6)
    ttk.Combobox(opt, textvariable=var_for("landcover_resampling", s.landcover_resampling),
                 values=["mode", "nearest"], state="readonly", width=10).pack(side="left")
    ttk.Label(opt, text="nodata value").pack(side="left", padx=(12, 4))
    ttk.Entry(opt, textvariable=var_for("landcover_nodata", s.landcover_nodata),
              width=8).pack(side="left")
    vec = ttk.LabelFrame(tab, text="Vectors (optional)")
    vec.pack(fill="x", padx=8, pady=4)
    vtypes = [("Vector data", "*.gpkg *.shp *.pbf *.geojson *.json"), ("All files", "*.*")]
    file_row(vec, 0, "Land polygons (coastline)", "land_polygons", s.land_polygons, types=vtypes)
    file_row(vec, 1, "OSM polygons (GPKG / .osm.pbf)", "osm_file", s.osm_file, types=vtypes)
    ttk.Label(vec, text="OSM layer name").grid(row=2, column=0, sticky="w", padx=6, pady=3)
    ttk.Entry(vec, textvariable=var_for("osm_layer", s.osm_layer), width=20).grid(
        row=2, column=1, sticky="w", padx=6)

    # ---- Tab: Land classes ----
    tab = ttk.Frame(nb)
    nb.add(tab, text="Land classes")
    pan = ttk.PanedWindow(tab, orient="horizontal")
    pan.pack(fill="both", expand=True)
    left, right = ttk.Frame(pan), ttk.Frame(pan)
    pan.add(left, weight=1)
    pan.add(right, weight=1)
    text_box(left, "Classes (id, name, #colour)", "classes", s.classes, 14)
    text_box(left, "Land-cover lookup (source value -> class id)", "lut", s.lut, 10)
    text_box(right, "OSM override rules (top to bottom, later wins)", "rules", s.rules, 20)
    cr = ttk.LabelFrame(right, text="Special classes")
    cr.pack(fill="x", padx=8, pady=6)
    row(cr, 0, "Default (no data) class", "default_class", s.default_class, width=6)
    row(cr, 1, "Water / sea class", "water_class", s.water_class, width=6)
    row(cr, 2, "Rock class", "rock_class", s.rock_class, width=6)
    row(cr, 3, "Slope → rock above (°)", "slope_rock_deg", s.slope_rock_deg, width=6,
        tip="0 = off (do it in the shader)")

    # ---- Tab: Output ----
    tab = ttk.Frame(nb)
    nb.add(tab, text="Output")
    out = ttk.LabelFrame(tab, text="Destination")
    out.pack(fill="x", padx=8, pady=6)
    file_row(out, 0, "Output folder", "output_dir", s.output_dir, folder=True)
    hf = ttk.LabelFrame(tab, text="Heights")
    hf.pack(fill="x", padx=8, pady=6)
    row(hf, 0, "Height range min (m)", "height_min", s.height_min,
        tip="global: identical for all tiles (-500 covers the Dead Sea)")
    row(hf, 1, "Height range max (m)", "height_max", s.height_max,
        tip="9000 covers Everest; 16-bit step = (max-min)/65535")
    row(hf, 2, "Sea floor height (m)", "sea_height", s.sea_height,
        tip="below the h = 0 ocean, used where the DEM has no bathymetry")
    of = ttk.LabelFrame(tab, text="Options")
    of.pack(fill="x", padx=8, pady=6)
    row(of, 0, "Skip all-sea tiles", "skip_sea_tiles", s.skip_sea_tiles,
        tip="the globe ocean covers them")
    row(of, 1, "Also write float32 heights", "write_float_heights", s.write_float_heights)
    row(of, 2, "Write preview PNGs", "write_previews", s.write_previews)
    row(of, 3, "Overwrite existing tiles", "overwrite", s.overwrite,
        tip="off = only export tiles missing from the manifest")
    row(of, 4, "Worker processes", "workers", s.workers, width=6,
        tip=f"this PC has {os.cpu_count()} CPU threads; each worker needs ~1 GB")

    # ---- bottom: buttons, progress, log ----
    bottom = ttk.Frame(root)
    bottom.pack(fill="both", padx=8, pady=4)
    bar = ttk.Frame(bottom)
    bar.pack(fill="x")
    pb = ttk.Progressbar(bar, mode="determinate")
    pb.pack(side="left", fill="x", expand=True, padx=(0, 8))
    status = ttk.Label(bar, text="Ready", width=26)
    status.pack(side="left")
    logbox = ScrolledText(bottom, height=9, font=("Consolas", 9), state="disabled")
    logbox.pack(fill="both", expand=True, pady=4)
    btns = ttk.Frame(root)
    btns.pack(fill="x", padx=8, pady=(0, 8))

    msgq: queue.Queue = queue.Queue()
    cancel = threading.Event()

    def log(msg):
        msgq.put(("log", msg))

    def poll():
        try:
            while True:
                kind, *a = msgq.get_nowait()
                if kind == "log":
                    logbox.configure(state="normal")
                    logbox.insert("end", a[0] + "\n")
                    logbox.see("end")
                    logbox.configure(state="disabled")
                elif kind == "progress":
                    done, total, unit = a
                    pb.configure(maximum=max(total, 1), value=done)
                    status.configure(text=f"{done} / {total} {unit}")
                elif kind == "found":
                    found = a[0]
                    for key in ("dem_files", "landcover_files"):
                        have = set(lists[key].get(0, "end"))
                        for p in found[key]:
                            if p not in have:
                                lists[key].insert("end", p)
                    for key in ("land_polygons", "osm_file"):
                        if found[key]:
                            vars_[key].set(found[key])
                elif kind == "finished":
                    for b in job_buttons:
                        b.configure(state="normal")
                    btn_cancel.configure(state="disabled")
                    status.configure(text="Finished" if a[0] else "Stopped / see log")
        except queue.Empty:
            pass
        root.after(100, poll)

    def update_info(*_):
        try:
            sv = collect()
            tiles = region_tiles(sv)
            n, m = int(sv.height_per_deg), int(sv.class_per_deg)
            mid = (sv.min_lat + sv.max_lat) / 2
            hy, hx = spacing_m(n, mid)
            cy, cx = spacing_m(m, mid)
            mb = len(tiles) * ((n + 1) ** 2 * 2 + m * m) / 1e6
            step = (sv.height_max - sv.height_min) / 65535 * 100
            names = " ".join(tile_name(la, lo) for la, lo in tiles[:10])
            if len(tiles) > 10:
                names += f" … (+{len(tiles) - 10})"
            info.configure(text=(
                f"{len(tiles)} tiles: {names}\n"
                f"Heights {n + 1}×{n + 1} per tile, {hy:.0f} m N-S × {hx:.0f} m E-W apart at "
                f"{mid:.1f}°.   Land class {m}×{m}, {cy:.0f} × {cx:.0f} m cells.\n"
                f"≈ {mb:,.0f} MB raw if every tile is land.   "
                f"Height precision {step:.1f} cm."), foreground="#2a6")
        except Exception as e:
            info.configure(text=f"⚠ {e}", foreground="#c33")

    for name in ("min_lon", "min_lat", "max_lon", "max_lat", "height_per_deg",
                 "class_per_deg", "height_min", "height_max"):
        vars_[name].trace_add("write", lambda *_: root.after_idle(update_info))

    def start_job(fn):
        cancel.clear()
        for b in job_buttons:
            b.configure(state="disabled")
        btn_cancel.configure(state="normal")
        pb.configure(value=0)

        def job():
            ok = False
            try:
                ok = fn()
            except Exception:
                log(traceback.format_exc())
            msgq.put(("finished", ok))
        threading.Thread(target=job, daemon=True).start()

    def do_export():
        try:
            sv = collect()
        except ValueError as e:
            messagebox.showerror("Settings", str(e))
            return
        errs = validate(sv)
        if errs:
            messagebox.showerror("Settings", "\n".join(errs))
            return
        start_job(lambda: run_export(
            sv, log, lambda d, t: msgq.put(("progress", d, t, "tiles")), cancel))

    def do_download():
        try:
            sv = collect()
        except ValueError as e:
            messagebox.showerror("Settings", str(e))
            return
        errs = validate(sv, need_output=False, need_dem=False)
        if not sv.download_dir:
            errs.append("Choose a download folder.")
        if errs:
            messagebox.showerror("Settings", "\n".join(errs))
            return

        def fn():
            found = run_downloads(sv, log, lambda d, t: msgq.put(("progress", d, t, "files")),
                                  cancel)
            if found is None:
                return False
            msgq.put(("found", found))
            return True
        start_job(fn)

    def do_cancel():
        cancel.set()
        log("Cancelling after the running step finishes…")

    def save_cfg():
        p = filedialog.asksaveasfilename(defaultextension=".json",
                                         filetypes=[("Settings", "*.json")],
                                         initialfile="terrain_export_settings.json")
        if p:
            with open(p, "w", encoding="utf-8") as f:
                json.dump(asdict(collect()), f, indent=2)
            log(f"Saved settings to {p}")

    def load_cfg():
        p = filedialog.askopenfilename(filetypes=[("Settings", "*.json")])
        if p:
            with open(p, encoding="utf-8") as f:
                apply(Settings.from_dict(json.load(f)))
            log(f"Loaded settings from {p}")

    def open_out():
        p = vars_["output_dir"].get()
        if p and os.path.isdir(p):
            if sys.platform.startswith("win"):
                os.startfile(p)  # type: ignore[attr-defined]
            elif sys.platform == "darwin":
                os.system(f'open "{p}"')
            else:
                os.system(f'xdg-open "{p}"')

    ttk.Button(btns, text="Load settings…", command=load_cfg).pack(side="left")
    ttk.Button(btns, text="Save settings…", command=save_cfg).pack(side="left", padx=4)
    ttk.Button(btns, text="Reset defaults", command=lambda: apply(Settings())).pack(side="left")
    btn_cancel = ttk.Button(btns, text="Cancel", command=do_cancel, state="disabled")
    btn_cancel.pack(side="right")
    btn_export = ttk.Button(btns, text="Export tiles", command=do_export)
    btn_export.pack(side="right", padx=4)
    btn_download = ttk.Button(btns, text="Download sources", command=do_download)
    btn_download.pack(side="right", padx=4)
    ttk.Button(btns, text="Open output folder", command=open_out).pack(side="right", padx=4)
    job_buttons = (btn_export, btn_download)

    if config_path:
        log(f"Loaded settings from {config_path}")
    update_info()
    poll()
    root.mainloop()


# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--config", help="settings JSON (as saved from the window)")
    ap.add_argument("--nogui", action="store_true", help="export without opening the window")
    ap.add_argument("--download", action="store_true",
                    help="with --nogui: fetch the region's sources first and use them")
    ap.add_argument("--write-default-config", metavar="PATH",
                    help="write a settings JSON with the defaults and exit")
    a = ap.parse_args()

    if a.write_default_config:
        with open(a.write_default_config, "w", encoding="utf-8") as f:
            json.dump(asdict(Settings()), f, indent=2)
        print(f"Wrote {a.write_default_config}")
        return

    s = Settings()
    if a.config:
        with open(a.config, encoding="utf-8") as f:
            s = Settings.from_dict(json.load(f))

    if a.nogui:
        def progress(d, t):
            print(f"\r{d}/{t}", end="" if d < t else "\n", flush=True)
        if a.download:
            found = run_downloads(s, print, progress, threading.Event())
            if found is None:
                sys.exit(1)
            merge_found(s, found)
        ok = run_export(s, print, progress, threading.Event())
        sys.exit(0 if ok else 1)
    run_gui(s, a.config)


if __name__ == "__main__":
    main()
