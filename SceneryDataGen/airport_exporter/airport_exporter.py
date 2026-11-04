#!/usr/bin/env python3
"""
Airport Exporter
================
Downloads the OurAirports database (public domain, updated nightly) and writes a cleaned
copy for CirrusFS: airports.csv, runways.csv, airport-frequencies.csv and navaids.csv,
plus manifest.json. The Unity side (Assets/Scripts/Geo/Core/Airports) reads only these
cleaned files.

What "cleaned" means (see README.md for every column):
  - SI units: metres, true headings in degrees.
  - Every exported runway has both end coordinates. Runways that OurAirports lists without
    them (mostly small airfields) get "approx" ends: centred on the airport reference
    point, heading from the true heading or the runway number plus magnetic variation.
  - Surfaces are sorted into a few categories (asphalt, concrete, grass, ...).
  - Missing widths get a default from the runway length; broken rows are dropped.

Requirements: Python 3.9+, standard library only.

Run with the settings window:
    python airport_exporter.py
Run headless from a saved settings file (downloading first):
    python airport_exporter.py --config settings_global.json --nogui --download
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import os
import queue
import re
import ssl
import sys
import threading
import time
import traceback
import urllib.error
import urllib.request
from collections import Counter, defaultdict
from dataclasses import asdict, dataclass, fields
from datetime import datetime, timezone

FORMAT = "cirrusfs-airports"
FORMAT_VERSION = 1

SOURCE_BASE = "https://davidmegginson.github.io/ourairports-data/"
SOURCE_FILES = ("airports.csv", "runways.csv", "airport-frequencies.csv", "navaids.csv",
                "countries.csv")

FT = 0.3048

# WGS84 (same constants as CirrusFS.Geo.GeoMath)
WGS84_A = 6378137.0
WGS84_E2 = (1 / 298.257223563) * (2 - 1 / 298.257223563)

AIRPORT_TYPES = ("large_airport", "medium_airport", "small_airport", "heliport",
                 "seaplane_base", "balloonport", "closed")

SURFACES = ("asphalt", "concrete", "grass", "dirt", "gravel", "sand", "snow", "water",
            "other", "unknown")

# Approx runways: one whose heading is within this of a runway already placed at the
# same airport would most likely stack on it (a parallel, or the turf strip beside the
# paved one), so it is left out.
STACK_ANGLE_DEG = 30.0


@dataclass
class Settings:
    download_dir: str = ""
    max_age_hours: float = 24.0

    region_name: str = "Whole world"
    min_lon: float = -180.0
    min_lat: float = -90.0
    max_lon: float = 180.0
    max_lat: float = 90.0

    large_airports: bool = True
    medium_airports: bool = True
    small_airports: bool = True
    heliports: bool = False
    seaplane_bases: bool = False
    balloonports: bool = False
    closed_airports: bool = False
    closed_runways: bool = False
    water_runways: bool = False
    approx_runways: bool = True
    min_length_m: float = 0.0
    airports_without_runways: bool = False

    output_dir: str = ""
    write_frequencies: bool = True
    write_navaids: bool = True

    @classmethod
    def from_dict(cls, d: dict) -> "Settings":
        names = {f.name for f in fields(cls)}
        return cls(**{k: v for k, v in d.items() if k in names})

    def wanted_types(self) -> set:
        t = set()
        for name, flag in (("large_airport", self.large_airports),
                           ("medium_airport", self.medium_airports),
                           ("small_airport", self.small_airports),
                           ("heliport", self.heliports),
                           ("seaplane_base", self.seaplane_bases),
                           ("balloonport", self.balloonports),
                           ("closed", self.closed_airports)):
            if flag:
                t.add(name)
        return t

    def in_region(self, lat: float, lon: float) -> bool:
        return self.min_lat <= lat <= self.max_lat and self.min_lon <= lon <= self.max_lon


def validate(s: Settings, need_output=True) -> list[str]:
    errs = []
    if not s.download_dir:
        errs.append("Choose a download folder.")
    if need_output and not s.output_dir:
        errs.append("Choose an output folder.")
    if s.min_lat >= s.max_lat or s.min_lon >= s.max_lon:
        errs.append("The region box is empty (min must be below max).")
    if not s.wanted_types():
        errs.append("Select at least one airport type.")
    return errs


# --------------------------------------------------------------------------
# Download
# --------------------------------------------------------------------------

_SSL_CONTEXT = None


def _ssl_context():
    """Certificates from certifi when it is installed, else the system store."""
    global _SSL_CONTEXT
    if _SSL_CONTEXT is None:
        try:
            import certifi
            _SSL_CONTEXT = ssl.create_default_context(cafile=certifi.where())
        except ImportError:
            _SSL_CONTEXT = ssl.create_default_context()
    return _SSL_CONTEXT


def _fetch(url: str, path: str, log, cancel: threading.Event) -> str:
    """Downloads url to path; returns the server's Last-Modified header (or "")."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": "CirrusFS-airport-exporter"})
    part = path + ".part"
    try:
        with urllib.request.urlopen(req, timeout=60, context=_ssl_context()) as r, \
                open(part, "wb") as f:
            modified = r.headers.get("Last-Modified") or ""
            while not cancel.is_set():
                chunk = r.read(1 << 20)
                if not chunk:
                    break
                f.write(chunk)
    except BaseException:
        if os.path.exists(part):
            os.remove(part)
        raise
    if cancel.is_set():
        os.remove(part)
        raise RuntimeError("cancelled")
    os.replace(part, path)
    return modified


def run_downloads(s: Settings, log, progress, cancel: threading.Event, force=False) -> bool:
    """Fetches the OurAirports files that are missing or older than max_age_hours."""
    meta_path = os.path.join(s.download_dir, "_download.json")
    meta = {}
    if os.path.exists(meta_path):
        try:
            with open(meta_path, encoding="utf-8") as f:
                meta = json.load(f)
        except (OSError, ValueError):
            meta = {}

    for k, name in enumerate(SOURCE_FILES):
        if cancel.is_set():
            return False
        path = os.path.join(s.download_dir, name)
        age_h = (time.time() - os.path.getmtime(path)) / 3600 if os.path.exists(path) else None
        if not force and age_h is not None and age_h < s.max_age_hours:
            log(f"  {name}: have it ({age_h:.1f} h old)")
        else:
            log(f"  downloading {name}")
            try:
                modified = _fetch(SOURCE_BASE + name, path, log, cancel)
            except Exception as e:
                if os.path.exists(path):
                    log(f"  {name}: download failed ({e}); keeping the old copy")
                else:
                    log(f"  {name}: download failed ({e})")
                    return False
            else:
                meta[name] = {"url": SOURCE_BASE + name, "last_modified": modified,
                              "downloaded": _utc_now(),
                              "bytes": os.path.getsize(path)}
        progress(k + 1, len(SOURCE_FILES))

    with open(meta_path, "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)
    return True


def _utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# --------------------------------------------------------------------------
# Parsing helpers
# --------------------------------------------------------------------------

def _read_csv(path: str) -> list[dict]:
    with open(path, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def _num(x) -> float | None:
    if x is None:
        return None
    x = str(x).strip()
    if not x:
        return None
    try:
        v = float(x)
    except ValueError:
        return None
    return v if math.isfinite(v) else None


def _fmt(v, digits: int) -> str:
    """Fixed decimals without a trailing '.0' mess; '' for None."""
    if v is None:
        return ""
    s = f"{v:.{digits}f}"
    if "." in s:
        s = s.rstrip("0").rstrip(".")
    return "0" if s == "-0" else s


# Australian ERSA single-letter codes (B bitumen, G gravel, N natural, X clay, C concrete,
# S sand). Elsewhere a bare G means grass.
_AU_LETTERS = {"B": "asphalt", "G": "gravel", "N": "dirt", "X": "dirt", "C": "concrete",
               "S": "sand"}
_LETTERS = {"A": "asphalt", "B": "asphalt", "C": "concrete", "G": "grass", "S": "sand"}

_TOKENS = [
    ("water", r"WATER|WAT"),
    ("snow", r"SNOW|SNO|ICE"),
    ("concrete", r"CONC|CON|CONCRETE|BETON|PCC|CEMENT"),
    ("asphalt", r"ASPH|ASP|ASPHALT|ASFALT|ASFALTO|BIT|BITUM|BITUMEN|BITUMINOUS|TAR|TARMAC|MAC|"
                r"MACADAM|PAVED|SEALED|HARD|PEM|COP|COM|PER|TRTD|TREATED|OILGRAVEL|AC"),
    ("grass", r"TURF|GRS|GRASS|GRAS|GRASSED|GRE|SOD|GRAMA"),
    ("gravel", r"GVL|GRAVEL|GRVL|GRV|GRAV|COR|CORAL|CALICHE|CRUSHED|CINDERS|SHELLS|SLAG|"
               r"STONE"),
    ("dirt", r"DIRT|EARTH|CLAY|CLA|SOIL|LAT|LATERITE|GROUND|TER|TERRA|SAIBRO|PIÇARRA|PIÇ|"
             r"PICARRA|UNPAVED|ROUGH|GRADED|COMPACTED|BROWN|RED"),
    ("sand", r"SAND|SAN"),
    ("other", r"MATS|PSP|MET|METAL|STEEL|ALUM|ALUMINUM|WOOD|DECK|ROOF|ROOFTOP|ROOF-TOP|PAD|"
              r"PIERCED"),
    ("unknown", r"UNK|U|X|XUG|N|L|GR"),
]
_TOKEN_RE = [(cat, re.compile(r"(?:" + pat + r")\Z")) for cat, pat in _TOKENS]


def surface_category(raw: str, country: str) -> str:
    s = raw.strip().upper()
    if not s:
        return "unknown"
    if len(s) == 1:
        table = _AU_LETTERS if country == "AU" else _LETTERS
        return table.get(s, "unknown")
    for token in re.split(r"[\s/,;+\-]+", s):
        if not token:
            continue
        for cat, rx in _TOKEN_RE:
            if rx.match(token):
                if cat != "unknown":
                    return cat
                break
    return "unknown"


_COMPASS = {"N": 0, "NNE": 22.5, "NE": 45, "ENE": 67.5, "E": 90, "ESE": 112.5, "SE": 135,
            "SSE": 157.5, "S": 180, "SSW": 202.5, "SW": 225, "WSW": 247.5, "W": 270,
            "WNW": 292.5, "NW": 315, "NNW": 337.5}


def ident_heading(ident: str) -> tuple[float | None, bool]:
    """Heading from a runway designator: (degrees, is_magnetic). '09L' -> (90, True),
    'NE' -> (45, False)."""
    s = ident.strip().upper()
    m = re.fullmatch(r"(\d{1,2})[LRCWGSUAT]?", s)
    if m:
        n = int(m.group(1))
        if 1 <= n <= 36:
            return n * 10.0, True
        return None, True
    if s in _COMPASS:
        return float(_COMPASS[s]), False
    return None, False


# --------------------------------------------------------------------------
# Geodesy on the WGS84 ellipsoid (short distances: a few km)
# --------------------------------------------------------------------------

def _radii(lat: float) -> tuple[float, float]:
    """Metres per radian north (meridian) and east (parallel) at a latitude."""
    phi = math.radians(lat)
    w = math.sqrt(1 - WGS84_E2 * math.sin(phi) ** 2)
    m = WGS84_A * (1 - WGS84_E2) / w ** 3
    n = WGS84_A / w
    return m, n * math.cos(phi)


def offset(lat: float, lon: float, heading_deg: float, dist_m: float) -> tuple[float, float]:
    """The point dist_m from (lat, lon) along a true heading (local tangent plane, evaluated
    at the midpoint so the error stays far below a metre over a few km)."""
    h = math.radians(heading_deg)
    dn, de = dist_m * math.cos(h), dist_m * math.sin(h)
    m, p = _radii(lat + math.degrees(dn / _radii(lat)[0]) * 0.5)
    lat2 = lat + math.degrees(dn / m)
    lon2 = lon + math.degrees(de / max(p, 1.0))
    if lon2 > 180:
        lon2 -= 360
    elif lon2 < -180:
        lon2 += 360
    return lat2, lon2


def distance_heading(lat1, lon1, lat2, lon2) -> tuple[float, float]:
    """Distance (m) and true heading (deg) from point 1 to point 2, tangent plane at the
    midpoint."""
    dlon = lon2 - lon1
    if dlon > 180:
        dlon -= 360
    elif dlon < -180:
        dlon += 360
    m, p = _radii(0.5 * (lat1 + lat2))
    dn = math.radians(lat2 - lat1) * m
    de = math.radians(dlon) * p
    return math.hypot(dn, de), (math.degrees(math.atan2(de, dn)) + 360.0) % 360.0


def angle_between_lines(a: float, b: float) -> float:
    """Smallest angle between two undirected lines with headings a and b, 0..90."""
    d = abs((a - b) % 180.0)
    return min(d, 180.0 - d)


class Declination:
    """Magnetic variation from the nearest navaid that lists one (east positive).

    Good to a couple of degrees, which is plenty here: a runway number only gives the
    magnetic heading to the nearest 10 degrees anyway."""

    def __init__(self, navaids: list[dict]):
        self.bins = defaultdict(list)
        for n in navaids:
            lat, lon, var = (_num(n.get("latitude_deg")), _num(n.get("longitude_deg")),
                             _num(n.get("magnetic_variation_deg")))
            if lat is None or lon is None or var is None or abs(var) > 90:
                continue
            self.bins[(int(math.floor(lat / 5)), int(math.floor(lon / 5)))].append(
                (lat, lon, var))

    def at(self, lat: float, lon: float, max_km: float = 1500.0) -> float | None:
        bi, bj = int(math.floor(lat / 5)), int(math.floor(lon / 5))
        best, best_d = None, max_km * 1000.0
        for ring in range(0, 4):
            for di in range(-ring, ring + 1):
                for dj in range(-ring, ring + 1):
                    if max(abs(di), abs(dj)) != ring:
                        continue
                    for nlat, nlon, var in self.bins.get((bi + di, (bj + dj + 36) % 72 - 36), ()):
                        d, _ = distance_heading(lat, lon, nlat, nlon)
                        if d < best_d:
                            best, best_d = var, d
            if best is not None and ring >= 1:
                break
        return best


def default_width(length_m: float, surface: str) -> float:
    if surface in ("grass", "dirt", "sand", "snow"):
        return 25.0 if length_m < 1200 else 30.0
    if length_m < 800:
        return 15.0
    if length_m < 1500:
        return 23.0
    if length_m < 2400:
        return 30.0
    return 45.0


# --------------------------------------------------------------------------
# Export
# --------------------------------------------------------------------------

AIRPORT_COLUMNS = ["id", "ident", "type", "name", "lat", "lon", "elevation_m", "country",
                   "country_name", "region", "municipality", "scheduled", "icao", "iata",
                   "gps", "local", "keywords", "runways", "approx_runways"]

RUNWAY_COLUMNS = ["id", "airport_id", "airport_ident", "le_ident", "he_ident",
                  "le_lat", "le_lon", "he_lat", "he_lon", "le_elevation_m", "he_elevation_m",
                  "le_heading", "he_heading", "length_m", "declared_length_m", "width_m",
                  "width_source", "le_displaced_m", "he_displaced_m", "surface", "surface_raw",
                  "lighted", "position"]

FREQUENCY_COLUMNS = ["id", "airport_id", "airport_ident", "type", "description",
                     "frequency_mhz"]

NAVAID_COLUMNS = ["id", "ident", "name", "type", "frequency_khz", "lat", "lon",
                  "elevation_m", "country", "dme_frequency_khz", "dme_channel", "dme_lat",
                  "dme_lon", "dme_elevation_m", "magnetic_variation_deg", "usage", "power",
                  "associated_airport"]


def _ft_to_m(x) -> float | None:
    v = _num(x)
    return None if v is None else v * FT


def _surveyed_ends(r: dict, declared_m: float | None, skip: Counter):
    """(le_lat, le_lon, he_lat, he_lon) when the row has usable end coordinates."""
    vals = [_num(r.get(k)) for k in ("le_latitude_deg", "le_longitude_deg",
                                     "he_latitude_deg", "he_longitude_deg")]
    if any(v is None for v in vals):
        return None
    la1, lo1, la2, lo2 = vals
    if not (-90 <= la1 <= 90 and -90 <= la2 <= 90 and -180 <= lo1 <= 180 and -180 <= lo2 <= 180):
        skip["surveyed ends out of range"] += 1
        return None
    d, _ = distance_heading(la1, lo1, la2, lo2)
    if d < 60.0 or d > 7000.0:
        skip["surveyed ends implausibly close/far, re-placed as approx"] += 1
        return None
    if declared_m and (d < 0.4 * declared_m or d > 2.5 * declared_m):
        skip["surveyed ends disagree with length, re-placed as approx"] += 1
        return None
    return la1, lo1, la2, lo2


def _approx_heading(r: dict, lat: float, lon: float, decl: Declination) -> float | None:
    """True heading of the low end from the row's headings or designators."""
    h = _num(r.get("le_heading_degT"))
    if h is not None and 0 <= h <= 360:
        return h % 360.0
    h = _num(r.get("he_heading_degT"))
    if h is not None and 0 <= h <= 360:
        return (h + 180.0) % 360.0
    for key, flip in (("le_ident", 0.0), ("he_ident", 180.0)):
        hd, magnetic = ident_heading(r.get(key) or "")
        if hd is None:
            continue
        if magnetic:
            var = decl.at(lat, lon)
            hd += var if var is not None else 0.0
        return (hd + flip) % 360.0
    return None


def run_export(s: Settings, log, progress, cancel: threading.Event) -> bool:
    t0 = time.time()
    src = {name: os.path.join(s.download_dir, name) for name in SOURCE_FILES}
    missing = [n for n, p in src.items() if not os.path.exists(p) and n != "countries.csv"]
    if missing:
        log("Missing source files: " + ", ".join(missing) + ". Press Download first.")
        return False

    log("Reading OurAirports...")
    airports_in = _read_csv(src["airports.csv"])
    runways_in = _read_csv(src["runways.csv"])
    freqs_in = _read_csv(src["airport-frequencies.csv"])
    navaids_in = _read_csv(src["navaids.csv"])
    countries = {}
    if os.path.exists(src["countries.csv"]):
        countries = {c["code"]: c["name"] for c in _read_csv(src["countries.csv"])}
    log(f"  {len(airports_in):,} airports, {len(runways_in):,} runways, "
        f"{len(freqs_in):,} frequencies, {len(navaids_in):,} navaids")
    progress(1, 5)

    decl = Declination(navaids_in)
    types = s.wanted_types()
    skip = Counter()

    # ---- airports that pass the type and region filters --------------------------
    airports = {}
    for a in airports_in:
        if a.get("type") not in types:
            continue
        lat, lon = _num(a.get("latitude_deg")), _num(a.get("longitude_deg"))
        if lat is None or lon is None or not (-90 <= lat <= 90 and -180 <= lon <= 180):
            skip["airport without a valid position"] += 1
            continue
        if not s.in_region(lat, lon):
            continue
        airports[a["id"]] = (a, lat, lon)

    # ---- runways ------------------------------------------------------------------
    by_airport = defaultdict(list)
    for r in runways_in:
        if r.get("airport_ref") in airports:
            by_airport[r["airport_ref"]].append(r)

    runways_out: dict[str, list[dict]] = defaultdict(list)
    n_surveyed = n_approx = 0
    for k, (aid, rows) in enumerate(by_airport.items()):
        if cancel.is_set():
            return False
        a, alat, alon = airports[aid]
        country = a.get("iso_country", "")
        airport_elev = _ft_to_m(a.get("elevation_ft"))
        placed_headings: list[float] = []
        pending_approx = []

        for r in rows:
            if r.get("closed") == "1" and not s.closed_runways:
                skip["closed runway"] += 1
                continue
            surface = surface_category(r.get("surface") or "", country)
            if surface == "water" and not s.water_runways:
                skip["water runway"] += 1
                continue
            declared = _ft_to_m(r.get("length_ft"))
            if declared is not None and declared <= 0:
                declared = None
            ends = _surveyed_ends(r, declared, skip)
            if ends is None:
                pending_approx.append((r, surface, declared))
                continue
            out = _runway_row(r, a, surface, declared, ends, "surveyed", airport_elev)
            if out["length_m_f"] < s.min_length_m:
                skip["shorter than the minimum length"] += 1
                continue
            placed_headings.append(out["le_heading_f"])
            runways_out[aid].append(out)
            n_surveyed += 1

        if not s.approx_runways:
            skip["runway without end coordinates"] += len(pending_approx)
            pending_approx = []

        # longest first, so the main runway wins a stacking conflict
        pending_approx.sort(key=lambda t: -(t[2] or 0.0))
        for r, surface, declared in pending_approx:
            if declared is None or declared < 60.0 or declared > 7000.0:
                skip["approx runway without a usable length"] += 1
                continue
            heading = _approx_heading(r, alat, alon, decl)
            if heading is None:
                skip["approx runway without a heading"] += 1
                continue
            if any(angle_between_lines(heading, h) < STACK_ANGLE_DEG for h in placed_headings):
                skip["approx runway that would stack on another"] += 1
                continue
            if declared < s.min_length_m:
                skip["shorter than the minimum length"] += 1
                continue
            la1, lo1 = offset(alat, alon, heading, -0.5 * declared)
            la2, lo2 = offset(alat, alon, heading, 0.5 * declared)
            out = _runway_row(r, a, surface, declared, (la1, lo1, la2, lo2), "approx", airport_elev)
            placed_headings.append(out["le_heading_f"])
            runways_out[aid].append(out)
            n_approx += 1

        if k % 2000 == 0:
            progress(1 + k / max(len(by_airport), 1) * 2, 5)
    progress(3, 5)

    # ---- airports out ---------------------------------------------------------------
    airports_out = []
    for aid, (a, lat, lon) in airports.items():
        rws = runways_out.get(aid, [])
        if not rws and not s.airports_without_runways:
            continue
        airports_out.append({
            "id": aid,
            "ident": a.get("ident", "").strip(),
            "type": a.get("type", ""),
            "name": a.get("name", "").strip(),
            "lat": _fmt(lat, 7),
            "lon": _fmt(lon, 7),
            "elevation_m": _fmt(_ft_to_m(a.get("elevation_ft")), 1),
            "country": a.get("iso_country", ""),
            "country_name": countries.get(a.get("iso_country", ""), ""),
            "region": a.get("iso_region", ""),
            "municipality": a.get("municipality", "").strip(),
            "scheduled": "1" if a.get("scheduled_service") == "yes" else "0",
            "icao": a.get("icao_code", "").strip(),
            "iata": a.get("iata_code", "").strip(),
            "gps": a.get("gps_code", "").strip(),
            "local": a.get("local_code", "").strip(),
            "keywords": a.get("keywords", "").strip(),
            "runways": str(len(rws)),
            "approx_runways": str(sum(1 for r in rws if r["position"] == "approx")),
        })
    kept = {a["id"] for a in airports_out}
    airports_out.sort(key=lambda a: int(a["id"]) if a["id"].isdigit() else 0)

    runway_rows = [r for aid in sorted(runways_out, key=lambda x: int(x) if x.isdigit() else 0)
                   if aid in kept for r in runways_out[aid]]

    # ---- frequencies and navaids -----------------------------------------------------
    freq_rows = []
    if s.write_frequencies:
        for f in freqs_in:
            if f.get("airport_ref") not in kept:
                continue
            mhz = _num(f.get("frequency_mhz"))
            if mhz is None or mhz <= 0:
                continue
            freq_rows.append({"id": f.get("id", ""), "airport_id": f["airport_ref"],
                              "airport_ident": f.get("airport_ident", ""),
                              "type": f.get("type", "").strip().upper(),
                              "description": f.get("description", "").strip(),
                              "frequency_mhz": _fmt(mhz, 3)})

    navaid_rows = []
    if s.write_navaids:
        for n in navaids_in:
            lat, lon = _num(n.get("latitude_deg")), _num(n.get("longitude_deg"))
            if lat is None or lon is None or not s.in_region(lat, lon):
                continue
            navaid_rows.append({
                "id": n.get("id", ""), "ident": n.get("ident", "").strip(),
                "name": n.get("name", "").strip(), "type": n.get("type", "").strip(),
                "frequency_khz": _fmt(_num(n.get("frequency_khz")), 1),
                "lat": _fmt(lat, 7), "lon": _fmt(lon, 7),
                "elevation_m": _fmt(_ft_to_m(n.get("elevation_ft")), 1),
                "country": n.get("iso_country", ""),
                "dme_frequency_khz": _fmt(_num(n.get("dme_frequency_khz")), 1),
                "dme_channel": n.get("dme_channel", "").strip(),
                "dme_lat": _fmt(_num(n.get("dme_latitude_deg")), 7),
                "dme_lon": _fmt(_num(n.get("dme_longitude_deg")), 7),
                "dme_elevation_m": _fmt(_ft_to_m(n.get("dme_elevation_ft")), 1),
                "magnetic_variation_deg": _fmt(_num(n.get("magnetic_variation_deg")), 3),
                "usage": n.get("usageType", "").strip(),
                "power": n.get("power", "").strip(),
                "associated_airport": n.get("associated_airport", "").strip(),
            })
    progress(4, 5)

    # ---- write ---------------------------------------------------------------------
    os.makedirs(s.output_dir, exist_ok=True)
    _write_csv(os.path.join(s.output_dir, "airports.csv"), AIRPORT_COLUMNS, airports_out)
    _write_csv(os.path.join(s.output_dir, "runways.csv"), RUNWAY_COLUMNS, runway_rows)
    _write_csv(os.path.join(s.output_dir, "airport-frequencies.csv"), FREQUENCY_COLUMNS, freq_rows)
    _write_csv(os.path.join(s.output_dir, "navaids.csv"), NAVAID_COLUMNS, navaid_rows)

    source_meta = {}
    meta_path = os.path.join(s.download_dir, "_download.json")
    if os.path.exists(meta_path):
        with open(meta_path, encoding="utf-8") as f:
            source_meta = json.load(f)

    by_type = Counter(a["type"] for a in airports_out)
    by_surface = Counter(r["surface"] for r in runway_rows)
    manifest = {
        "format": FORMAT,
        "version": FORMAT_VERSION,
        "created": _utc_now(),
        "source": {"name": "OurAirports (public domain)", "url": SOURCE_BASE,
                   "files": source_meta},
        "region": {"name": s.region_name, "min_lon": s.min_lon, "min_lat": s.min_lat,
                   "max_lon": s.max_lon, "max_lat": s.max_lat},
        "settings": asdict(s),
        "counts": {"airports": len(airports_out), "runways": len(runway_rows),
                   "runways_surveyed": sum(1 for r in runway_rows if r["position"] == "surveyed"),
                   "runways_approx": sum(1 for r in runway_rows if r["position"] == "approx"),
                   "frequencies": len(freq_rows), "navaids": len(navaid_rows),
                   "airports_by_type": dict(by_type), "runways_by_surface": dict(by_surface)},
        "surfaces": list(SURFACES),
        "columns": {"airports.csv": AIRPORT_COLUMNS, "runways.csv": RUNWAY_COLUMNS,
                    "airport-frequencies.csv": FREQUENCY_COLUMNS, "navaids.csv": NAVAID_COLUMNS},
    }
    with open(os.path.join(s.output_dir, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    progress(5, 5)

    size = sum(os.path.getsize(os.path.join(s.output_dir, n))
               for n in ("airports.csv", "runways.csv", "airport-frequencies.csv",
                         "navaids.csv", "manifest.json"))
    log(f"Wrote {len(airports_out):,} airports and {len(runway_rows):,} runways "
        f"({manifest['counts']['runways_surveyed']:,} surveyed, "
        f"{manifest['counts']['runways_approx']:,} approx), {len(freq_rows):,} frequencies, "
        f"{len(navaid_rows):,} navaids: {size / 1e6:.1f} MB in {s.output_dir} "
        f"({time.time() - t0:.1f} s)")
    log("  airports: " + ", ".join(f"{t} {n:,}" for t, n in by_type.most_common()))
    log("  surfaces: " + ", ".join(f"{t} {n:,}" for t, n in by_surface.most_common()))
    if skip:
        log("  left out: " + "; ".join(f"{why} {n:,}" for why, n in skip.most_common()))
    return True


def _runway_row(r: dict, a: dict, surface: str, declared: float | None, ends, position: str,
                airport_elev: float | None) -> dict:
    la1, lo1, la2, lo2 = ends
    length, heading = distance_heading(la1, lo1, la2, lo2)
    width = _ft_to_m(r.get("width_ft"))
    width_source = "data"
    if width is None or width < 3.0 or width > 150.0 or width > 0.5 * length:
        width = default_width(length, surface)
        width_source = "default"
    le_elev = _ft_to_m(r.get("le_elevation_ft"))
    he_elev = _ft_to_m(r.get("he_elevation_ft"))
    if le_elev is None:
        le_elev = he_elev if he_elev is not None else airport_elev
    if he_elev is None:
        he_elev = le_elev
    le_disp = _ft_to_m(r.get("le_displaced_threshold_ft"))
    he_disp = _ft_to_m(r.get("he_displaced_threshold_ft"))
    # a displaced threshold can never eat more than half the runway
    le_disp = min(max(le_disp, 0.0), 0.5 * length) if le_disp is not None else None
    he_disp = min(max(he_disp, 0.0), 0.5 * length) if he_disp is not None else None
    return {
        "id": r.get("id", ""),
        "airport_id": r.get("airport_ref", ""),
        "airport_ident": a.get("ident", "").strip(),
        "le_ident": (r.get("le_ident") or "").strip(),
        "he_ident": (r.get("he_ident") or "").strip(),
        "le_lat": _fmt(la1, 7), "le_lon": _fmt(lo1, 7),
        "he_lat": _fmt(la2, 7), "he_lon": _fmt(lo2, 7),
        "le_elevation_m": _fmt(le_elev, 1), "he_elevation_m": _fmt(he_elev, 1),
        "le_heading": _fmt(heading, 2), "he_heading": _fmt((heading + 180.0) % 360.0, 2),
        "length_m": _fmt(length, 1), "declared_length_m": _fmt(declared, 1),
        "width_m": _fmt(width, 1), "width_source": width_source,
        "le_displaced_m": _fmt(le_disp, 1), "he_displaced_m": _fmt(he_disp, 1),
        "surface": surface, "surface_raw": (r.get("surface") or "").strip(),
        "lighted": "1" if r.get("lighted") == "1" else "0",
        "position": position,
        # helpers, not written
        "length_m_f": length, "le_heading_f": heading,
    }


def _write_csv(path: str, columns: list[str], rows: list[dict]) -> None:
    part = path + ".part"
    with open(part, "w", encoding="utf-8", newline="") as f:
        w = csv.DictWriter(f, fieldnames=columns, extrasaction="ignore", lineterminator="\n")
        w.writeheader()
        w.writerows(rows)
    os.replace(part, path)


# --------------------------------------------------------------------------
# GUI
# --------------------------------------------------------------------------

PRESETS = {
    "Whole world": (-180.0, -90.0, 180.0, 90.0),
    "South Korea": (124.0, 33.0, 131.0, 39.0),
    "Japan": (122.0, 24.0, 146.0, 46.0),
    "Europe": (-25.0, 34.0, 45.0, 72.0),
    "Contiguous USA": (-125.0, 24.0, -66.0, 50.0),
}


def run_gui(initial: Settings, config_path: str | None):
    import tkinter as tk
    from tkinter import filedialog, messagebox, ttk
    from tkinter.scrolledtext import ScrolledText

    root = tk.Tk()
    root.title("Airport Exporter (OurAirports)")
    root.geometry("820x700")
    root.minsize(700, 560)

    vars_: dict[str, tk.Variable] = {}

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
        return Settings.from_dict(d)

    def apply(s: Settings):
        for f in fields(Settings):
            if f.name in vars_:
                vars_[f.name].set(getattr(s, f.name))

    def row(parent, r, label, name, value, width=14, tip=None):
        ttk.Label(parent, text=label).grid(row=r, column=0, sticky="w", padx=6, pady=3)
        v = var_for(name, value)
        w = ttk.Checkbutton(parent, variable=v) if isinstance(value, bool) else \
            ttk.Entry(parent, textvariable=v, width=width)
        w.grid(row=r, column=1, sticky="w", padx=6, pady=3)
        if tip:
            ttk.Label(parent, text=tip, foreground="#777").grid(row=r, column=2, sticky="w", padx=6)

    def folder_row(parent, r, label, name, value):
        ttk.Label(parent, text=label).grid(row=r, column=0, sticky="w", padx=6, pady=3)
        v = var_for(name, value)
        ttk.Entry(parent, textvariable=v).grid(row=r, column=1, sticky="ew", padx=6, pady=3)

        def browse():
            p = filedialog.askdirectory()
            if p:
                v.set(p)
        ttk.Button(parent, text="Browse…", command=browse).grid(row=r, column=2, padx=6)
        parent.columnconfigure(1, weight=1)

    nb = ttk.Notebook(root)
    nb.pack(fill="both", expand=True, padx=8, pady=(8, 4))
    s = initial

    # ---- Tab: Source & filter ----
    tab = ttk.Frame(nb)
    nb.add(tab, text="Source & filter")
    src = ttk.LabelFrame(tab, text="OurAirports download (public domain, updated nightly)")
    src.pack(fill="x", padx=8, pady=6)
    folder_row(src, 0, "Download folder", "download_dir", s.download_dir)
    row(src, 1, "Re-download after (hours)", "max_age_hours", s.max_age_hours, width=8,
        tip="files newer than this are reused")

    reg = ttk.LabelFrame(tab, text="Region (WGS84 degrees)")
    reg.pack(fill="x", padx=8, pady=6)
    ttk.Label(reg, text="Preset").grid(row=0, column=0, sticky="w", padx=6, pady=3)
    preset = ttk.Combobox(reg, values=list(PRESETS), state="readonly", width=24)
    preset.grid(row=0, column=1, sticky="w", padx=6, pady=3)

    def on_preset(_=None):
        name = preset.get()
        vars_["region_name"].set(name)
        for k, v in zip(("min_lon", "min_lat", "max_lon", "max_lat"), PRESETS[name]):
            vars_[k].set(v)
    preset.bind("<<ComboboxSelected>>", on_preset)
    row(reg, 1, "Region name", "region_name", s.region_name, width=26)
    row(reg, 2, "Min longitude (west)", "min_lon", s.min_lon)
    row(reg, 3, "Min latitude (south)", "min_lat", s.min_lat)
    row(reg, 4, "Max longitude (east)", "max_lon", s.max_lon)
    row(reg, 5, "Max latitude (north)", "max_lat", s.max_lat)

    flt = ttk.LabelFrame(tab, text="What to export")
    flt.pack(fill="x", padx=8, pady=6)
    row(flt, 0, "Large airports", "large_airports", s.large_airports)
    row(flt, 1, "Medium airports", "medium_airports", s.medium_airports)
    row(flt, 2, "Small airports", "small_airports", s.small_airports)
    row(flt, 3, "Heliports", "heliports", s.heliports, tip="helipads are not drawn yet")
    row(flt, 4, "Seaplane bases", "seaplane_bases", s.seaplane_bases)
    row(flt, 5, "Balloonports", "balloonports", s.balloonports)
    row(flt, 6, "Closed airports", "closed_airports", s.closed_airports)
    row(flt, 7, "Closed runways", "closed_runways", s.closed_runways)
    row(flt, 8, "Water runways", "water_runways", s.water_runways)
    row(flt, 9, "Approx runways", "approx_runways", s.approx_runways,
        tip="runways without end coordinates, placed on the airport reference point")
    row(flt, 10, "Minimum runway length (m)", "min_length_m", s.min_length_m, width=8)
    row(flt, 11, "Airports without runways", "airports_without_runways",
        s.airports_without_runways, tip="not drawn and not spawnable")

    # ---- Tab: Output ----
    tab = ttk.Frame(nb)
    nb.add(tab, text="Output")
    out = ttk.LabelFrame(tab, text="Destination")
    out.pack(fill="x", padx=8, pady=6)
    folder_row(out, 0, "Output folder", "output_dir", s.output_dir)
    of = ttk.LabelFrame(tab, text="Files")
    of.pack(fill="x", padx=8, pady=6)
    row(of, 0, "airport-frequencies.csv", "write_frequencies", s.write_frequencies,
        tip="for the exported airports")
    row(of, 1, "navaids.csv", "write_navaids", s.write_navaids, tip="inside the region")

    # ---- bottom ----
    bottom = ttk.Frame(root)
    bottom.pack(fill="both", padx=8, pady=4)
    bar = ttk.Frame(bottom)
    bar.pack(fill="x")
    pb = ttk.Progressbar(bar, mode="determinate")
    pb.pack(side="left", fill="x", expand=True, padx=(0, 8))
    status = ttk.Label(bar, text="Ready", width=20)
    status.pack(side="left")
    logbox = ScrolledText(bottom, height=10, font=("Consolas", 9), state="disabled")
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
                    done, total = a
                    pb.configure(maximum=max(total, 1), value=done)
                    status.configure(text=f"{done:.0f} / {total}")
                elif kind == "finished":
                    for b in job_buttons:
                        b.configure(state="normal")
                    btn_cancel.configure(state="disabled")
                    status.configure(text="Finished" if a[0] else "Stopped / see log")
        except queue.Empty:
            pass
        root.after(100, poll)

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

    def progress(d, t):
        msgq.put(("progress", d, t))

    def checked(need_output):
        try:
            sv = collect()
        except ValueError as e:
            messagebox.showerror("Settings", str(e))
            return None
        errs = validate(sv, need_output=need_output)
        if errs:
            messagebox.showerror("Settings", "\n".join(errs))
            return None
        return sv

    def do_download():
        sv = checked(False)
        if sv:
            start_job(lambda: run_downloads(sv, log, progress, cancel, force=True))

    def do_export():
        sv = checked(True)
        if sv:
            start_job(lambda: run_downloads(sv, log, progress, cancel) and
                      run_export(sv, log, progress, cancel))

    def do_cancel():
        cancel.set()
        log("Cancelling...")

    def save_cfg():
        p = filedialog.asksaveasfilename(defaultextension=".json",
                                         filetypes=[("Settings", "*.json")],
                                         initialfile="airport_export_settings.json")
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
    btn_download = ttk.Button(btns, text="Download now", command=do_download)
    btn_download.pack(side="right", padx=4)
    ttk.Button(btns, text="Open output folder", command=open_out).pack(side="right", padx=4)
    job_buttons = (btn_export, btn_download)

    if config_path:
        log(f"Loaded settings from {config_path}")
    poll()
    root.mainloop()


# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--config", help="settings JSON (as saved from the window)")
    ap.add_argument("--nogui", action="store_true", help="export without opening the window")
    ap.add_argument("--download", action="store_true",
                    help="with --nogui: fetch files that are missing or too old first")
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
        errs = validate(s)
        if errs:
            print("\n".join(errs))
            sys.exit(2)
        progress = lambda d, t: None  # noqa: E731
        cancel = threading.Event()
        if a.download and not run_downloads(s, print, progress, cancel):
            sys.exit(1)
        sys.exit(0 if run_export(s, print, progress, cancel) else 1)
    run_gui(s, a.config)


if __name__ == "__main__":
    main()
