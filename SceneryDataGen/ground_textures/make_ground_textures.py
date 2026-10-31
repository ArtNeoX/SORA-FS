"""Starter ground textures for the CirrusFS terrain (seamless, procedural).

Writes 512 x 512 tiling PNGs into Assets/Textures/Terrain. They are placeholders made of
filtered noise, meant to be replaced by real aerial or ground textures; each one is
seamless because every pattern is built on a periodic grid (FFT-filtered noise, wrapped
distances).

    py -3.13 make_ground_textures.py [output folder]
"""
import os
import sys

import numpy as np
from PIL import Image

N = 512
rng = np.random.default_rng(20261031)

fy = np.fft.fftfreq(N)[:, None]
fx = np.fft.fftfreq(N)[None, :]
FREQ = np.sqrt(fx * fx + fy * fy)


def noise(cycles_lo, cycles_hi, power=1.0, aniso=(1.0, 1.0)):
    """Periodic band-pass noise, normalised to 0..1. cycles = repeats across the tile."""
    f = np.sqrt((fx * aniso[0]) ** 2 + (fy * aniso[1]) ** 2) * N
    amp = np.where((f >= cycles_lo) & (f <= cycles_hi), 1.0 / np.maximum(f, 1.0) ** power, 0.0)
    spec = np.fft.fft2(rng.standard_normal((N, N))) * amp
    out = np.real(np.fft.ifft2(spec))
    out -= out.min()
    return out / max(out.max(), 1e-9)


def shade(height, strength):
    """Lambert-like relief from a height field, light from the north-west; mean 1."""
    gx = np.roll(height, -1, 1) - np.roll(height, 1, 1)
    gy = np.roll(height, -1, 0) - np.roll(height, 1, 0)
    s = 1.0 + strength * (-gx * 0.7 + gy * 0.7)
    return s / s.mean()


def crowns(count, r_min, r_max):
    """Height field of round tree crowns at random points, wrapped at the edges."""
    h = np.zeros((N, N))
    yy, xx = np.mgrid[0:N, 0:N]
    for _ in range(count):
        cx, cy = rng.uniform(0, N, 2)
        r = rng.uniform(r_min, r_max)
        dx = (xx - cx + N / 2) % N - N / 2
        dy = (yy - cy + N / 2) % N - N / 2
        d2 = (dx * dx + dy * dy) / (r * r)
        h = np.maximum(h, np.sqrt(np.clip(1.0 - d2, 0.0, 1.0)) * rng.uniform(0.7, 1.0))
    return h


def colourise(t, dark, light):
    """Map 0..1 to a colour ramp between two sRGB colours."""
    dark = np.array(dark, float)
    light = np.array(light, float)
    t = np.clip(t, 0.0, 1.0)[..., None]
    return dark + (light - dark) * t


def save(folder, name, rgb):
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), "RGB")
    path = os.path.join(folder, name + ".png")
    img.save(path)
    print("wrote", path)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    folder = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "..", "Assets", "Textures", "Terrain")
    folder = os.path.normpath(folder)
    os.makedirs(folder, exist_ok=True)

    # Grass: fine blades over soft patches.
    t = noise(2, 12) * 0.45 + noise(30, 140) * 0.35 + noise(140, 256) * 0.2
    save(folder, "Ground_Grass", colourise(t, (70, 98, 44), (150, 170, 86)))

    # Broadleaf forest: big rounded crowns, deep shadow between them.
    h = crowns(260, 14, 26) + noise(40, 160) * 0.15
    t = 0.25 + 0.75 * h
    rgb = colourise(t * 0.6 + noise(2, 8) * 0.4, (22, 42, 18), (88, 120, 52)) * shade(h, 2.2)[..., None]
    save(folder, "Ground_Forest", rgb * np.where(h > 0.05, 1.0, 0.45)[..., None])

    # Conifer forest: small dense pointed crowns.
    h = crowns(900, 6, 11) ** 2.2
    rgb = colourise(h * 0.7 + noise(2, 8) * 0.3, (14, 30, 18), (64, 92, 50)) * shade(h, 2.6)[..., None]
    save(folder, "Ground_Conifer", rgb * np.where(h > 0.03, 1.0, 0.4)[..., None])

    # Shrub: scattered small bushes on dry grass.
    h = crowns(500, 5, 10)
    base = colourise(noise(20, 200), (120, 120, 72), (164, 156, 98))
    bush = colourise(noise(2, 10), (52, 70, 34), (92, 108, 52)) * shade(h, 2.0)[..., None]
    save(folder, "Ground_Shrub", np.where((h > 0.05)[..., None], bush, base))

    # Cropland: soft east-west rows in drifting strips.
    rows = 0.5 + 0.5 * np.sin(np.arange(N)[:, None] * 2 * np.pi * 48 / N + noise(1, 4) * 3.0)
    t = rows * 0.35 + noise(2, 10) * 0.4 + noise(60, 256) * 0.25
    save(folder, "Ground_Cropland", colourise(t, (132, 112, 66), (200, 180, 112)))

    # Rice paddy: a grid of flooded plots with lighter bunds between them.
    yy, xx = np.mgrid[0:N, 0:N]
    plot_x, plot_y = N // 4, N // 6
    bund = ((xx % plot_x) < 5) | ((yy % plot_y) < 4)
    cell = (xx // plot_x) * 7 + (yy // plot_y) * 3
    tone = (cell % 5) / 4.0
    t = tone * 0.5 + noise(20, 200) * 0.5
    plots = colourise(t, (90, 120, 60), (150, 176, 92))
    save(folder, "Ground_Paddy", np.where(bund[..., None], colourise(noise(40, 200), (150, 140, 96), (190, 176, 120)), plots))

    # Rock: cracked, layered stone.
    h = noise(2, 20, 1.6) * 0.7 + noise(20, 200) * 0.3
    cracks = 1.0 - np.exp(-np.abs(noise(4, 30) - 0.5) * 30.0)
    rgb = colourise(h, (86, 80, 72), (168, 160, 148)) * shade(h, 6.0)[..., None]
    save(folder, "Ground_Rock", rgb * (0.65 + 0.35 * cracks)[..., None])

    # Sand: fine grain over gentle ripples.
    ripples = 0.5 + 0.5 * np.sin((xx + noise(1, 3) * 60) * 2 * np.pi * 24 / N)
    t = ripples * 0.25 + noise(1, 6) * 0.35 + noise(150, 256) * 0.4
    save(folder, "Ground_Sand", colourise(t, (196, 178, 136), (236, 222, 184)))


if __name__ == "__main__":
    main()
