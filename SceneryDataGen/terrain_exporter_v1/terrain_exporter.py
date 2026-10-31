#!/usr/bin/env python3
"""
Terrain Tile Exporter
=====================
Exports height and land-class tiles for a region, on a fixed projected grid,
in simple binary formats meant to be read by Unity (terrain + autogen).

Requirements (Windows/macOS/Linux, Python 3.9+):
    pip install rasterio fiona shapely pyproj pillow numpy

Run with the settings window:
    python terrain_exporter.py
Run headless from a saved settings file:
    python terrain_exporter.py --config my_settings.json --nogui

Output layout (see README.md for the full format description):
    <output>/manifest.json
    <output>/tiles/<col>_<row>/height.r16    uint16 LE, N x N vertices, row 0 = south
    <output>/tiles/<col>_<row>/class.u8      uint8, M x M cells, row 0 = south
    <output>/tiles/<col>_<row>/height.f32    (optional) float32 LE metres
    <output>/tiles/<col>_<row>/preview.png   (optional) north-up colour preview
"""

from __future__ import annotations

import argparse
import json
import math
import os
import pickle
import queue
import re
import sys
import threading
import time
import traceback
from concurrent.futures import ProcessPoolExecutor, as_completed
from dataclasses import asdict, dataclass, field, fields

import numpy as np

try:
    import fiona
    import rasterio
    import rasterio.merge
    import shapely
    from PIL import Image
    from pyproj import CRS as PCRS
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

FORMAT_VERSION = 1

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
70  -> 8    # snow / ice
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

PRESETS = {
    "South Korea (full + DMZ)": (124.5, 33.0, 131.0, 38.75),
    "Seoul / Gyeonggi": (126.4, 37.0, 127.6, 38.0),
    "Osan / Pyeongtaek test": (126.9, 36.95, 127.2, 37.2),
    "Busan": (128.8, 35.0, 129.3, 35.35),
    "Jeju": (126.1, 33.15, 127.0, 33.6),
}

RESAMPLING = {
    "cubic_spline": Resampling.cubic_spline,
    "cubic": Resampling.cubic,
    "bilinear": Resampling.bilinear,
    "lanczos": Resampling.lanczos,
    "nearest": Resampling.nearest,
}


@dataclass
class Settings:
    region_name: str = "Osan / Pyeongtaek test"
    min_lon: float = 126.9
    min_lat: float = 36.95
    max_lon: float = 127.2
    max_lat: float = 37.2
    epsg: int = 32652
    tile_size: float = 10240.0
    height_res: float = 10.0
    class_res: float = 10.0

    dem_files: list = field(default_factory=list)
    dem_resampling: str = "cubic_spline"
    landcover_files: list = field(default_factory=list)
    landcover_nodata: int = 0
    land_polygons: str = ""
    osm_file: str = ""
    osm_layer: str = "multipolygons"

    classes: str = DEFAULT_CLASSES
    lut: str = DEFAULT_LUT
    rules: str = DEFAULT_RULES
    default_class: int = 2
    water_class: int = 0
    rock_class: int = 8
    slope_rock_deg: float = 0.0

    output_dir: str = ""
    height_min: float = -200.0
    height_max: float = 2300.0
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
# Grid
# --------------------------------------------------------------------------

def utm_epsg_for(lon: float, lat: float) -> int:
    zone = int(math.floor((lon + 180) / 6)) + 1
    return (32600 if lat >= 0 else 32700) + zone


def region_tiles(s: Settings) -> list[tuple[int, int]]:
    xmin, ymin, xmax, ymax = transform_bounds(
        "EPSG:4326", f"EPSG:{s.epsg}", s.min_lon, s.min_lat, s.max_lon, s.max_lat,
        densify_pts=51)
    t = s.tile_size
    c0, c1 = math.floor(xmin / t), math.floor((xmax - 1e-6) / t)
    r0, r1 = math.floor(ymin / t), math.floor((ymax - 1e-6) / t)
    return [(c, r) for r in range(r0, r1 + 1) for c in range(c0, c1 + 1)]


def grid_dims(s: Settings) -> tuple[int, int]:
    n_h = round(s.tile_size / s.height_res)
    n_c = round(s.tile_size / s.class_res)
    return n_h + 1, n_c


def validate(s: Settings, need_output=True) -> list[str]:
    errs = []
    if not (s.min_lon < s.max_lon and s.min_lat < s.max_lat):
        errs.append("Region: min lon/lat must be smaller than max lon/lat.")
    try:
        PCRS.from_epsg(int(s.epsg))
    except Exception:
        errs.append(f"EPSG:{s.epsg} is not a valid CRS.")
    for name, r in (("Height", s.height_res), ("Land class", s.class_res)):
        if r <= 0 or abs(s.tile_size / r - round(s.tile_size / r)) > 1e-6:
            errs.append(f"{name} resolution must divide the tile size exactly.")
    if s.height_max <= s.height_min:
        errs.append("Height max must be greater than height min.")
    if not s.dem_files:
        errs.append("Add at least one DEM file.")
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
# Vector preparation (runs once, cached for the workers)
# --------------------------------------------------------------------------

def _reprojector(src_crs, dst_crs):
    tr = Transformer.from_crs(src_crs, dst_crs, always_xy=True)

    def fn(coords):
        x, y = tr.transform(coords[:, 0], coords[:, 1])
        return np.column_stack([x, y])
    return fn


def _read_polys(path, layer, region_ll, clip_box, dst_crs, log, per_feature=None):
    """Yield (geom_in_dst_crs, props) for polygons intersecting the region."""
    kw = {"layer": layer} if layer else {}
    with fiona.open(path, **kw) as src:
        src_crs = src.crs_wkt or "EPSG:4326"
        bb = transform_bounds("EPSG:4326", src_crs, *region_ll, densify_pts=21)
        fn = _reprojector(src_crs, dst_crs)
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
    t = s.tile_size
    cols = [c for c, _ in tiles]
    rows = [r for _, r in tiles]
    clip_box = box(min(cols) * t - t, min(rows) * t - t,
                   (max(cols) + 2) * t, (max(rows) + 2) * t)
    # lon/lat bbox of the clip box, for the source filter
    region_ll = transform_bounds(f"EPSG:{s.epsg}", "EPSG:4326", *clip_box.bounds,
                                 densify_pts=21)
    dst = f"EPSG:{s.epsg}"
    data = {"land": [], "rules": []}

    if s.land_polygons:
        log(f"Reading land polygons: {os.path.basename(s.land_polygons)}")
        data["land"] = [g for g, _ in _read_polys(s.land_polygons, None, region_ll,
                                                  clip_box, dst, log)]
        log(f"  {len(data['land'])} land polygons in region")

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

        for g, props in _read_polys(s.osm_file, s.osm_layer or None, region_ll,
                                    clip_box, dst, log, keep):
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

class Exporter:
    def __init__(self, s: Settings, cache_path: str):
        self.s = s
        self.crs = rasterio.crs.CRS.from_epsg(s.epsg)
        self.classes = parse_classes(s.classes)
        self.lut = parse_lut(s.lut, s.default_class)
        self.rules = parse_rules(s.rules)
        self.palette = np.zeros((256, 3), np.uint8)
        self.palette[:] = (255, 0, 255)
        for c in self.classes:
            self.palette[c["id"]] = hex_rgb(c["color"])
        self.dem = self._open(s.dem_files)
        self.lc = self._open(s.landcover_files)
        with open(cache_path, "rb") as f:
            vec = pickle.load(f)
        self.land = vec["land"]
        self.land_tree = STRtree(self.land) if self.land else None
        self.rule_geoms = vec["rules"]
        self.rule_trees = [STRtree(g) if g else None for g in self.rule_geoms]
        self.land_rule_idx = [i for i, r in enumerate(self.rules) if r.as_land]
        self.to_ll = Transformer.from_crs(f"EPSG:{s.epsg}", "EPSG:4326", always_xy=True)

    def _open(self, paths):
        out = []
        for p in paths:
            ds = rasterio.open(p)
            out.append(ds)
        return out

    # -- raster sampling ---------------------------------------------------
    def _warp(self, datasets, bounds, shape_, transform, resampling, dtype, nodata):
        """Mosaic the sources covering `bounds` (target CRS) and warp onto the grid."""
        out = np.full(shape_, nodata, dtype=dtype)
        groups = {}
        for ds in datasets:
            groups.setdefault(ds.crs.to_string(), []).append(ds)
        for crs_str, group in groups.items():
            sb = transform_bounds(self.crs, group[0].crs, *bounds, densify_pts=21)
            px = max(abs(group[0].res[0]), abs(group[0].res[1]))
            m = 6 * px  # margin so cubic kernels see real data at tile borders
            sb = (sb[0] - m, sb[1] - m, sb[2] + m, sb[3] + m)
            hits = [d for d in group if not (d.bounds.right < sb[0] or d.bounds.left > sb[2]
                                              or d.bounds.top < sb[1] or d.bounds.bottom > sb[3])]
            if not hits:
                continue
            # snap the window to the source pixel lattice so merge() copies pixels
            # instead of shifting them by a fraction of a pixel
            gt = hits[0].transform
            rx, ry = abs(gt.a), abs(gt.e)
            sb = (gt.c + math.floor((sb[0] - gt.c) / rx) * rx,
                  gt.f - math.ceil((gt.f - sb[1]) / ry) * ry,
                  gt.c + math.ceil((sb[2] - gt.c) / rx) * rx,
                  gt.f - math.floor((gt.f - sb[3]) / ry) * ry)
            arr, tr = rasterio.merge.merge(hits, bounds=sb, res=(rx, ry),
                                           nodata=nodata, dtype=dtype)
            tmp = np.full(shape_, nodata, dtype=dtype)
            reproject(arr[0], tmp, src_transform=tr, src_crs=group[0].crs,
                      src_nodata=nodata, dst_transform=transform, dst_crs=self.crs,
                      dst_nodata=nodata, resampling=resampling,
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

    def _land_mask(self, shape_, transform, tile_box, valid_dem):
        if self.land_tree is None:
            land = valid_dem.copy()        # no coastline data: land = DEM coverage
        else:
            land = self._burn(self.land, self.land_tree, shape_, transform, tile_box)
        for i in self.land_rule_idx:
            land |= self._burn(self.rule_geoms[i], self.rule_trees[i], shape_,
                               transform, tile_box)
        return land

    # -- main --------------------------------------------------------------
    def export_tile(self, col: int, row: int) -> dict:
        s = self.s
        t, hr, cr = s.tile_size, s.height_res, s.class_res
        nh, nc = grid_dims(s)
        x0, y0 = col * t, row * t
        tile_id = f"{col}_{row}"
        tile_box = box(x0 - hr, y0 - hr, x0 + t + hr, y0 + t + hr)
        out_dir = os.path.join(s.output_dir, "tiles", tile_id)

        # Heights: vertex grid, samples exactly on tile edges (shared with neighbours)
        h_tr = from_origin(x0 - hr / 2, y0 + t + hr / 2, hr, hr)
        h_bounds = (x0 - hr / 2, y0 - hr / 2, x0 + t + hr / 2, y0 + t + hr / 2)
        NOD = -32768.0
        h = self._warp(self.dem, h_bounds, (nh, nh), h_tr,
                       RESAMPLING.get(s.dem_resampling, Resampling.cubic_spline),
                       np.float32, NOD)
        valid = h > -32000
        land_h = self._land_mask((nh, nh), h_tr, tile_box, valid)

        if s.skip_sea_tiles and not land_h.any():
            return {"id": tile_id, "skipped": True}

        h[~valid] = np.nan
        sea = ~land_h
        # sea: keep real bathymetry if the DEM has it, otherwise sea_height
        h[sea] = np.where(np.isnan(h[sea]), s.sea_height,
                          np.minimum(h[sea], s.sea_height))
        h[land_h & np.isnan(h)] = 0.0       # holes on land
        h = np.clip(h, s.height_min, s.height_max)

        # Land class: cell grid
        c_tr = from_origin(x0, y0 + t, cr, cr)
        if self.lc:
            nd = s.landcover_nodata
            raw = self._warp(self.lc, (x0, y0, x0 + t, y0 + t), (nc, nc), c_tr,
                             Resampling.nearest, np.int32, -2147483648)
            cls = self.lut[np.clip(raw, 0, 65535)].astype(np.uint8)
            cls[(raw == -2147483648) | (raw == nd)] = s.default_class
        else:
            cls = np.full((nc, nc), s.default_class, np.uint8)

        # height vertex index for every class cell centre (works for any res ratio)
        centres = (np.arange(nc) + 0.5) * cr
        vi = np.clip(np.rint(centres / hr).astype(int), 0, nh - 1)
        vrow, vcol = vi[:, None], vi[None, :]          # north-up rows

        if s.slope_rock_deg > 0:
            gy, gx = np.gradient(h, hr)
            slope = np.degrees(np.arctan(np.hypot(gx, gy)))
            cls[slope[vrow, vcol] > s.slope_rock_deg] = s.rock_class

        land_c = self._land_mask((nc, nc), c_tr, tile_box,
                                 valid[vrow, vcol])
        cls[~land_c] = s.water_class
        for rule, geoms, tree in zip(self.rules, self.rule_geoms, self.rule_trees):
            m = self._burn(geoms, tree, (nc, nc), c_tr, tile_box)
            if not rule.as_land:
                m &= land_c
            cls[m] = rule.cls

        # Write (flip so that row 0 = south, matching Unity TerrainData)
        os.makedirs(out_dir, exist_ok=True)
        q = np.rint((h - s.height_min) / (s.height_max - s.height_min) * 65535.0)
        np.flipud(q).astype("<u2").tofile(os.path.join(out_dir, "height.r16"))
        np.flipud(cls).astype(np.uint8).tofile(os.path.join(out_dir, "class.u8"))
        if s.write_float_heights:
            np.flipud(h).astype("<f4").tofile(os.path.join(out_dir, "height.f32"))
        if s.write_previews:
            self._preview(h, cls, vrow, vcol, os.path.join(out_dir, "preview.png"))

        lon, lat = self.to_ll.transform(x0 + t / 2, y0 + t / 2)
        counts = np.bincount(cls.ravel(), minlength=256)
        return {
            "id": tile_id, "col": col, "row": row, "x": x0, "y": y0,
            "lon": round(lon, 6), "lat": round(lat, 6),
            "hMin": round(float(h.min()), 2), "hMax": round(float(h.max()), 2),
            "landFraction": round(float(land_h.mean()), 4),
            "classes": {str(i): int(n) for i, n in enumerate(counts) if n},
        }

    def _preview(self, h, cls, vrow, vcol, path):
        hr = self.s.height_res
        gy, gx = np.gradient(h, hr)
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


def _init_worker(settings_dict, cache_path):
    global _WORKER
    _WORKER = Exporter(Settings.from_dict(settings_dict), cache_path)


def _run_tile(col, row):
    try:
        return _WORKER.export_tile(col, row)
    except Exception:
        return {"id": f"{col}_{row}", "error": traceback.format_exc()}


# --------------------------------------------------------------------------
# Export driver
# --------------------------------------------------------------------------

def manifest_header(s: Settings) -> dict:
    nh, nc = grid_dims(s)
    return {
        "format": "TerrainTiles",
        "version": FORMAT_VERSION,
        "crs": f"EPSG:{s.epsg}",
        "tileSize": s.tile_size,
        "height": {
            "file": "height.r16", "encoding": "uint16le",
            "samples": nh, "resolution": s.height_res,
            "min": s.height_min, "max": s.height_max,
            "decode": "meters = min + value / 65535 * (max - min)",
            "layout": "vertices; first/last row and column lie on the tile edges",
            "rowOrder": "south_to_north", "floatFile": "height.f32" if s.write_float_heights else "",
        },
        "landClass": {
            "file": "class.u8", "encoding": "uint8",
            "pixels": nc, "resolution": s.class_res,
            "layout": "cells; pixel (0,0) covers the south-west corner",
            "rowOrder": "south_to_north",
        },
        "seaHeight": s.sea_height,
        "waterClass": s.water_class,
        "classes": parse_classes(s.classes),
    }


def _grid_key(m: dict):
    return (m.get("crs"), m.get("tileSize"), m["height"]["samples"], m["height"]["min"],
            m["height"]["max"], m["landClass"]["pixels"])


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
        if _grid_key(old) != _grid_key(header):
            log("ERROR: the output folder already holds tiles on a different grid "
                "(CRS, tile size, resolution or height range differ). "
                "Pick another folder or delete manifest.json.")
            return False
        existing = {t["id"]: t for t in old.get("tiles", [])}
        regions = old.get("regions", [])
        log(f"Adding to existing export ({len(existing)} tiles).")

    tiles = region_tiles(s)
    if not s.overwrite:
        todo = [(c, r) for c, r in tiles if f"{c}_{r}" not in existing]
    else:
        todo = tiles
    log(f"Region '{s.region_name}': {len(tiles)} tiles on the grid, {len(todo)} to export.")
    if not todo:
        return True

    t0 = time.time()
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
        m["tiles"] = sorted(results.values(), key=lambda t: (t["row"], t["col"]))
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
        else:
            results[res["id"]] = res
        progress(done, len(todo))
        if done % 50 == 0:
            save_manifest()

    workers = max(1, int(s.workers))
    log(f"Exporting with {workers} worker(s)...")
    if workers == 1:
        _init_worker(asdict(s), cache_path)
        for c, r in todo:
            if cancel.is_set():
                break
            handle(_run_tile(c, r))
    else:
        with ProcessPoolExecutor(max_workers=workers, initializer=_init_worker,
                                 initargs=(asdict(s), cache_path)) as pool:
            futs = [pool.submit(_run_tile, c, r) for c, r in todo]
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
    root.title("Terrain Tile Exporter")
    root.geometry("900x760")
    root.minsize(760, 600)

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
    def row(parent, r, label, name, value, width=14, tip=None):
        ttk.Label(parent, text=label).grid(row=r, column=0, sticky="w", padx=6, pady=3)
        v = var_for(name, value)
        if isinstance(value, bool):
            w = ttk.Checkbutton(parent, variable=v)
        else:
            w = ttk.Entry(parent, textvariable=v, width=width)
        w.grid(row=r, column=1, sticky="w", padx=6, pady=3)
        if tip:
            ttk.Label(parent, text=tip, foreground="#777").grid(
                row=r, column=2, sticky="w", padx=6)
        return w

    def file_row(parent, r, label, name, value, save=False, folder=False, types=None):
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
        frm.pack(fill="both", expand=True, padx=8, pady=6)
        lb = tk.Listbox(frm, height=5, selectmode="extended")
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
                lb.insert("end", p)
            update_info()

        def add_folder():
            d = filedialog.askdirectory()
            if not d:
                return
            n = 0
            for base, _, files in os.walk(d):
                for f in sorted(files):
                    if f.lower().endswith(exts):
                        lb.insert("end", os.path.join(base, f))
                        n += 1
            log(f"Added {n} files from {d}")
            update_info()

        def remove():
            for i in reversed(lb.curselection()):
                lb.delete(i)

        for text, cmd in (("Add files…", add_files), ("Add folder…", add_folder),
                          ("Remove", remove), ("Clear", lambda: lb.delete(0, "end"))):
            ttk.Button(btns, text=text, command=cmd, width=12).pack(pady=2)

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
    reg = ttk.LabelFrame(tab, text="Region (WGS84 degrees)")
    reg.pack(fill="x", padx=8, pady=6)
    ttk.Label(reg, text="Preset").grid(row=0, column=0, sticky="w", padx=6, pady=3)
    preset = ttk.Combobox(reg, values=list(PRESETS), state="readonly", width=30)
    preset.grid(row=0, column=1, columnspan=2, sticky="w", padx=6, pady=3)

    def on_preset(_=None):
        name = preset.get()
        b = PRESETS[name]
        vars_["region_name"].set(name)
        for k, v in zip(("min_lon", "min_lat", "max_lon", "max_lat"), b):
            vars_[k].set(v)
        update_info()
    preset.bind("<<ComboboxSelected>>", on_preset)
    row(reg, 1, "Region name", "region_name", s.region_name, width=32)
    row(reg, 2, "Min longitude (west)", "min_lon", s.min_lon)
    row(reg, 3, "Min latitude (south)", "min_lat", s.min_lat)
    row(reg, 4, "Max longitude (east)", "max_lon", s.max_lon)
    row(reg, 5, "Max latitude (north)", "max_lat", s.max_lat)

    grd = ttk.LabelFrame(tab, text="Tile grid (keep identical for every export of the same world)")
    grd.pack(fill="x", padx=8, pady=6)
    row(grd, 0, "Projected CRS (EPSG)", "epsg", s.epsg, tip="32652 = UTM 52N (Korea)")

    def auto_utm():
        sv = collect()
        vars_["epsg"].set(utm_epsg_for((sv.min_lon + sv.max_lon) / 2,
                                       (sv.min_lat + sv.max_lat) / 2))
        update_info()
    ttk.Button(grd, text="Auto UTM from region", command=auto_utm).grid(
        row=0, column=3, padx=6)
    row(grd, 1, "Tile size (m)", "tile_size", s.tile_size, tip="10240 → 1025 height samples at 10 m")
    row(grd, 2, "Height resolution (m)", "height_res", s.height_res,
        tip="vertex spacing; must divide tile size")
    row(grd, 3, "Land-class resolution (m)", "class_res", s.class_res,
        tip="cell size; 10 m matches WorldCover")

    info = ttk.Label(tab, text="", foreground="#2a6", justify="left")
    info.pack(fill="x", padx=14, pady=10)

    # ---- Tab: Sources ----
    tab = ttk.Frame(nb)
    nb.add(tab, text="Sources")
    file_list(tab, "Elevation (DEM) rasters — e.g. Copernicus GLO-30 GeoTIFFs", "dem_files",
              s.dem_files, (".tif", ".tiff", ".vrt", ".img", ".hgt", ".dem"))
    opt = ttk.Frame(tab)
    opt.pack(fill="x", padx=8)
    ttk.Label(opt, text="DEM resampling").pack(side="left", padx=6)
    v = var_for("dem_resampling", s.dem_resampling)
    ttk.Combobox(opt, textvariable=v, values=list(RESAMPLING), state="readonly",
                 width=14).pack(side="left")
    file_list(tab, "Land-cover rasters — e.g. ESA WorldCover (optional)", "landcover_files",
              s.landcover_files, (".tif", ".tiff", ".vrt", ".img"))
    opt = ttk.Frame(tab)
    opt.pack(fill="x", padx=8)
    ttk.Label(opt, text="Land-cover nodata value").pack(side="left", padx=6)
    ttk.Entry(opt, textvariable=var_for("landcover_nodata", s.landcover_nodata),
              width=8).pack(side="left")
    vec = ttk.LabelFrame(tab, text="Vectors (optional)")
    vec.pack(fill="x", padx=8, pady=6)
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
        tip="global: identical for all tiles")
    row(hf, 1, "Height range max (m)", "height_max", s.height_max,
        tip="16-bit step = (max-min)/65535")
    row(hf, 2, "Sea floor height (m)", "sea_height", s.sea_height,
        tip="used where the DEM has no bathymetry")
    of = ttk.LabelFrame(tab, text="Options")
    of.pack(fill="x", padx=8, pady=6)
    row(of, 0, "Skip all-sea tiles", "skip_sea_tiles", s.skip_sea_tiles)
    row(of, 1, "Also write float32 heights", "write_float_heights", s.write_float_heights)
    row(of, 2, "Write preview PNGs", "write_previews", s.write_previews)
    row(of, 3, "Overwrite existing tiles", "overwrite", s.overwrite,
        tip="off = only export tiles missing from the manifest")
    row(of, 4, "Worker processes", "workers", s.workers, width=6,
        tip=f"this PC has {os.cpu_count()} CPU threads")

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
    worker: dict = {"thread": None}

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
                    done, total = a
                    pb.configure(maximum=total, value=done)
                    status.configure(text=f"{done} / {total} tiles")
                elif kind == "finished":
                    btn_export.configure(state="normal")
                    btn_cancel.configure(state="disabled")
                    status.configure(text="Finished" if a[0] else "Stopped / see log")
        except queue.Empty:
            pass
        root.after(100, poll)

    def update_info(*_):
        try:
            sv = collect()
            tiles = region_tiles(sv)
            nh, nc = grid_dims(sv)
            mb = len(tiles) * (nh * nh * 2 + nc * nc) / 1e6
            step = (sv.height_max - sv.height_min) / 65535 * 100
            info.configure(text=(
                f"{len(tiles)} tiles on the grid (before skipping sea).   "
                f"Heights {nh}×{nh}, land class {nc}×{nc} per tile.\n"
                f"≈ {mb:,.0f} MB raw if every tile is exported.   "
                f"Height precision {step:.1f} cm."), foreground="#2a6")
        except Exception as e:
            info.configure(text=f"⚠ {e}", foreground="#c33")

    for name in ("min_lon", "min_lat", "max_lon", "max_lat", "epsg", "tile_size",
                 "height_res", "class_res", "height_min", "height_max"):
        vars_[name].trace_add("write", lambda *_: root.after_idle(update_info))

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
        cancel.clear()
        btn_export.configure(state="disabled")
        btn_cancel.configure(state="normal")
        pb.configure(value=0)

        def job():
            ok = False
            try:
                ok = run_export(sv, log, lambda d, t: msgq.put(("progress", d, t)), cancel)
            except Exception:
                log(traceback.format_exc())
            msgq.put(("finished", ok))
        worker["thread"] = threading.Thread(target=job, daemon=True)
        worker["thread"].start()

    def do_cancel():
        cancel.set()
        log("Cancelling after the running tiles finish…")

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
    btn_export = ttk.Button(btns, text="Export", command=do_export)
    btn_export.pack(side="right", padx=4)
    ttk.Button(btns, text="Open output folder", command=open_out).pack(side="right", padx=4)

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
            print(f"\r{d}/{t} tiles", end="" if d < t else "\n", flush=True)
        ok = run_export(s, print, progress, threading.Event())
        sys.exit(0 if ok else 1)
    run_gui(s, a.config)


if __name__ == "__main__":
    main()
