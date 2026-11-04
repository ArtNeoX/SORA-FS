#ifndef CIRRUSFS_RUNWAY_PAINT_INCLUDED
#define CIRRUSFS_RUNWAY_PAINT_INCLUDED

// Runway pavement and markings, drawn procedurally in runway-local metres by the terrain
// shader. Needs _AirportRunways (5 float4 per runway, see AirportChunkPacker) declared before
// this file is included.
//
// Everything is box-filtered against the pixel's footprint in the runway's own axes (along,
// across): a mark is sharp up close and fades to its average coverage when it gets smaller
// than a pixel, so the stripes, joints and numbers neither shimmer nor vanish at a distance.
// Noise comes from _RunwayDetail sampled with explicit gradients, so its mip chain does the
// same for grain and rubber.
//
// Layout follows ICAO Annex 14 / FAA AC 150/5340-1, per runway end, in that end's frame:
//   u  metres from the runway end inward (negative: runoff beyond the end)
//   w  metres left of the centreline as seen on landing toward that end
// Glyphs are written to read upright from the approach: glyph x = -w, glyph y = u.

TEXTURE2D(_RunwayDetail); SAMPLER(sampler_RunwayDetail);

// ---- filtered coverage helpers --------------------------------------------------------

// Fraction of a pixel of width f centred on t that lies inside [a, b].
float BoxCov(float t, float a, float b, float f)
{
    f = max(f, 1e-3);
    return saturate((min(t + 0.5 * f, b) - max(t - 0.5 * f, a)) / f);
}

// Rectangle [x0, x1] x [y0, y1]; p and fp in the same axes.
float RectCov(float2 p, float x0, float x1, float y0, float y1, float2 fp)
{
    return BoxCov(p.x, x0, x1, fp.x) * BoxCov(p.y, y0, y1, fp.y);
}

// Integral of a pulse train of period 1 that is on over [0, duty).
float PulseIntegral(float x, float duty) { return floor(x) * duty + min(frac(x), duty); }

// Box-filtered pulse train: x and f in periods. Shifted to near zero first, so long runways
// do not lose the float precision a thin joint needs.
float PulseCov(float x, float f, float duty)
{
    f = max(f, 1e-4);
    x -= floor(x);
    return (PulseIntegral(x + 0.5 * f, duty) - PulseIntegral(x - 0.5 * f, duty)) / f;
}

// Coverage from a signed distance whose gradient is n (unit, in the same axes as fp).
float SdfCov(float sd, float2 n, float2 fp)
{
    return saturate(0.5 - sd / max(abs(n.x) * fp.x + abs(n.y) * fp.y, 1e-3));
}

float Hash21(float2 p)
{
    p = frac(p * float2(0.1031, 0.1030));
    p += dot(p, p.yx + 33.33);
    return frac((p.x + p.y) * p.x);
}

half4 RunwayNoise(float2 p, float metresPerRepeat, float2 dpx, float2 dpy, float seed)
{
    float s = 1.0 / metresPerRepeat;
    return SAMPLE_TEXTURE2D_GRAD(_RunwayDetail, sampler_RunwayDetail, p * s + seed * float2(0.618, 0.382),
                                 dpx * s, dpy * s);
}

// ---- glyphs: segment font for runway designators --------------------------------------
// Bits: 1 top, 2 upper right, 4 lower right, 8 bottom, 16 lower left, 32 upper left,
// 64 middle, 128 centre stem (the 1), 256 diagonal leg (the R).
static const uint kDigitMask[10] = { 0x3F, 0x80, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };

// p: metres from the glyph's lower-left corner (x right, y up as read). Cell dw x h,
// stroke s; fp is the pixel footprint in the same axes.
float GlyphCov(float2 p, float2 fp, uint mask, float dw, float h, float s)
{
    if (p.x < -fp.x || p.x > dw + fp.x || p.y < -fp.y || p.y > h + fp.y) return 0.0;
    float hm = 0.5 * h, c = 0.0;
    if (mask & 1u)   c = max(c, RectCov(p, 0, dw, h - s, h, fp));
    if (mask & 2u)   c = max(c, RectCov(p, dw - s, dw, hm, h, fp));
    if (mask & 4u)   c = max(c, RectCov(p, dw - s, dw, 0, hm, fp));
    if (mask & 8u)   c = max(c, RectCov(p, 0, dw, 0, s, fp));
    if (mask & 16u)  c = max(c, RectCov(p, 0, s, 0, hm, fp));
    if (mask & 32u)  c = max(c, RectCov(p, 0, s, hm, h, fp));
    if (mask & 64u)  c = max(c, RectCov(p, 0, dw, hm - 0.5 * s, hm + 0.5 * s, fp));
    if (mask & 128u) c = max(c, RectCov(p, 0.5 * (dw - s), 0.5 * (dw + s), 0, h, fp));
    if (mask & 256u)
    {
        // leg from the middle bar to the lower right corner
        float2 a = float2(0.45 * dw, hm), b = float2(dw - 0.5 * s, 0.5 * s);
        float2 ab = b - a, ap = p - a;
        float t = saturate(dot(ap, ab) / dot(ab, ab));
        float2 d = ap - ab * t;
        float len = max(length(d), 1e-4);
        c = max(c, SdfCov(len - 0.5 * s, d / len, fp));
    }
    return c;
}

// Designator code (RunwayMarkings.Designator): number + 100 x letter (1 L, 2 R, 3 C) + 1000
// with a leading zero. Digits on one line, the letter on a line below it (nearer the
// threshold). `base` is where the lowest line starts (u); returns the u of the top.
float DesignatorCov(float u, float w, float2 fp, float code, float base, float h, out float top)
{
    float number = fmod(code, 100.0);
    float letter = floor(fmod(code, 1000.0) / 100.0 + 0.01);
    bool zero = code >= 999.5;
    float dw = 0.5 * h, s = 0.12 * h, gap = 0.2 * h;
    float lineGap = 0.35 * h;
    float numberBase = base + (letter > 0.5 ? h + lineGap : 0.0);
    top = numberBase + h;
    if (code < 0.5) { top = base; return 0.0; }

    float2 g = float2(-w, u);    // read upright from the approach
    float2 fg = fp.yx;           // glyph x is across, glyph y is along
    float c = 0.0;
    if (u > base - fp.x && u < top + fp.x)
    {
        int tens = (int)floor(number / 10.0 + 0.01);
        int ones = (int)(number - tens * 10.0 + 0.5);
        bool two = tens > 0 || zero;
        // a 1 is only its stem wide, so "16" does not read as "1 6"
        float wTens = tens == 1 ? 2.0 * s : dw, wOnes = ones == 1 ? 2.0 * s : dw;
        float total = two ? wTens + gap + wOnes : wOnes;
        float x0 = -0.5 * total;
        if (two) c = max(c, GlyphCov(g - float2(x0, numberBase), fg, kDigitMask[tens], wTens, h, s));
        c = max(c, GlyphCov(g - float2(two ? x0 + wTens + gap : x0, numberBase), fg, kDigitMask[ones], wOnes, h, s));
        if (letter > 0.5)
        {
            uint mask = letter < 1.5 ? 0x38u : (letter < 2.5 ? (0x73u | 256u) : 0x39u);
            c = max(c, GlyphCov(g - float2(-0.5 * dw, base), fg, mask, dw, h, s));
        }
    }
    return c;
}

// Arrowhead pointing toward +u: apex at (wc, tip), base half width bw, length hl.
float ArrowheadCov(float u, float w, float2 fp, float wc, float tip, float bw, float hl)
{
    float x = abs(w - wc), y = u - tip;
    float sdBase = -(y + hl);
    float2 n = normalize(float2(hl, bw));
    float sdSide = dot(n, float2(x, y));
    // fp: (along, across); the half-planes are written in (across, along)
    return sdSide > sdBase ? SdfCov(sdSide, n, fp.yx) : SdfCov(sdBase, float2(0, 1), fp.yx);
}

// ---- the runway ------------------------------------------------------------------------

// r: runway index; p: (along from the le end, across, left positive) in metres; dpx, dpy:
// its screen derivatives. Returns the pavement albedo, markings included.
half3 RunwayPaint(int r, float2 p, float2 dpx, float2 dpy, half3 pavement, uint surface,
                  half3 white, half3 yellow, half3 rubberColor, half rubberStrength)
{
    float4 b = _AirportRunways[5 * r + 1];
    float4 c = _AirportRunways[5 * r + 2];
    float4 ro = _AirportRunways[5 * r + 3];
    float4 m = _AirportRunways[5 * r + 4];
    float L = 2.0 * b.x, W = 2.0 * b.y, hw = b.y;
    float seed = m.w;
    int cls = (int)(m.x + 0.5);
    float2 fp = abs(dpx) + abs(dpy);          // footprint: along, across (m)

    // this end's frame
    bool high = p.x > 0.5 * L;
    float u = high ? L - p.x : p.x;
    float w = high ? -p.y : p.y;
    float aw = abs(w);
    float T = high ? c.y : c.x;               // threshold (displaced)
    float designator = high ? c.w : c.z;
    float arrows = high ? ro.z : ro.x;
    float blast = high ? ro.w : ro.y;
    float uT = u - T;
    bool inBlast = blast > 0.5 && u < -arrows;

    // ---- pavement: lanes, panels or slabs, grain, stains ------------------------------
    half4 nGrain = RunwayNoise(p, 10.24, dpx, dpy, seed);          // 4 cm texels
    half4 nStain = RunwayNoise(p, 320.0, dpx, dpy, seed + 0.5);    // 20 m blobs in B
    half3 col = pavement;
    float across = p.y + hw;                                       // 0 at the right edge (le frame)
    if (surface == 1)
    {
        // concrete slabs, ~6 m along, lanes of ~7.5 m, dark sealed joints
        float laneW = W / max(round(W / 7.5), 1.0);
        float slabL = 6.0;
        float2 slab = floor(float2(p.x / slabL, across / laneW));
        float fade = saturate(1.5 - 1.5 * max(fp.x / slabL, fp.y / laneW));
        float h1 = Hash21(slab + seed), h2 = Hash21(slab.yx * 1.7 + seed);
        col *= 1.0 + ((h1 - 0.5) * 0.16 + (Hash21(floor(slab / 3.0) + seed * 3.1) - 0.5) * 0.08) * fade;
        col *= half3(1.0 + (h2 - 0.5) * 0.04 * fade, 1.0, 1.0 - (h2 - 0.5) * 0.04 * fade);
        float jw = 0.14;
        float joint = max(PulseCov((p.x + 0.5 * jw) / slabL, fp.x / slabL, jw / slabL),
                          PulseCov((across + 0.5 * jw) / laneW, fp.y / laneW, jw / laneW));
        col *= 1.0 - 0.32 * joint;
        col *= 0.93 + 0.14 * nGrain.r;
        col *= 0.86 + 0.28 * nStain.b;
    }
    else
    {
        // asphalt: paving lanes of ~6 m laid in long panels, faint joints, repair patches
        float laneW = W / max(round(W / 6.0), 1.0);
        float lane = floor(across / laneW);
        float panelL = 90.0;
        float panel = floor(p.x / panelL + Hash21(float2(lane, seed)));
        float fade = saturate(1.5 - 1.5 * max(fp.x / panelL, fp.y / laneW));
        col *= 1.0 + (Hash21(float2(lane, panel) + seed) - 0.5) * 0.13 * fade;
        float jw = 0.12;
        col *= 1.0 - 0.12 * PulseCov((across + 0.5 * jw) / laneW, fp.y / laneW, jw / laneW);
        // repair patches: one cell in eight along each lane
        float cellL = 40.0;
        float cell = floor(p.x / cellL);
        float hp = Hash21(float2(lane * 7.0 + 3.0, cell) + seed);
        if (hp < 0.08)
        {
            float x0 = cell * cellL + 2.0 + hp * 100.0, x1 = x0 + 6.0 + hp * 160.0;
            float patchCov = RectCov(float2(p.x, across), x0, min(x1, (cell + 1.0) * cellL - 1.0),
                                     lane * laneW + 0.4, (lane + 1.0) * laneW - 0.4, fp);
            col *= 1.0 - patchCov * (hp < 0.045 ? 0.15 : -0.08);
        }
        col *= 0.9 + 0.2 * nGrain.r;
        col *= 0.84 + 0.32 * nStain.b;
    }
    if (inBlast) col *= 0.85;                 // older, unpaved-looking blast pad asphalt

    // ---- rubber: dense streaky deposits in the touchdown zone, light tracks elsewhere ---
    half rubberDark = 0.0;
    [branch] if (m.y > 0.001 && !inBlast)
    {
        float along = smoothstep(40.0, 240.0, uT) * (1.0 - smoothstep(650.0, 1250.0, uT))
                    * (1.0 - smoothstep(0.32 * L, 0.46 * L, u));
        // heaviest in the main gear tracks (~4.5 m either side), fading out to ~0.4 W
        // and a second band for wide-body main gear (~7.5 m)
        float band = min(0.42 * W, 16.0);
        float lateral = (1.0 - smoothstep(0.5 * band, band, aw))
                      * saturate(0.45 + 0.45 * exp(-(aw - 4.5) * (aw - 4.5) / 5.0)
                                      + 0.3 * exp(-(aw - 7.5) * (aw - 7.5) / 6.0));
        float amount = m.y * along * lateral;
        float tracks = m.y * 0.1 * (1.0 - smoothstep(1.5, 7.0, aw)) * step(0.0, u);
        [branch] if (amount + tracks > 0.002)
        {
            // streaks: noise stretched along the runway at three widths, slightly slanted
            float slant = (Hash21(float2(seed, 5.0)) - 0.5) * 0.004;
            float wS = w + uT * slant;
            half fine = RunwayNoise(float2(wS / 0.16, uT / 11.0), 128.0, float2(dpx.y / 0.16, dpx.x / 11.0),
                                    float2(dpy.y / 0.16, dpy.x / 11.0), seed).g;
            half mid = RunwayNoise(float2(wS / 0.6, uT / 35.0), 32.0, float2(dpx.y / 0.6, dpx.x / 35.0),
                                   float2(dpy.y / 0.6, dpy.x / 35.0), seed + 0.2).a;
            half coarse = RunwayNoise(float2(w / 2.5, uT / 140.0), 16.0, float2(dpx.y / 2.5, dpx.x / 140.0),
                                      float2(dpy.y / 2.5, dpy.x / 140.0), seed + 0.4).b;
            half grit = RunwayNoise(float2(wS / 0.05, uT / 4.0), 256.0, float2(dpx.y / 0.05, dpx.x / 4.0),
                                    float2(dpy.y / 0.05, dpy.x / 4.0), seed + 0.6).r;
            // Zero-mean streak field, linear in the (mip-filtered) noise, so where the
            // streaks are smaller than a pixel it averages out instead of aliasing. Light
            // rubber shows as separate streaks, heavy rubber as a dark zone with lighter ones.
            float streaks = (fine - 0.5) * 1.6 + (grit - 0.5) * 1.1 + (mid - 0.5) * 1.2 + (coarse - 0.5) * 0.8;
            float dark = saturate(streaks + 1.2 * amount - 0.5) * saturate(amount * 4.0);
            dark = max(dark, tracks * saturate(1.0 + 2.0 * streaks));
            rubberDark = dark * rubberStrength;
            col = lerp(col, rubberColor, rubberDark);
        }
    }

    // ---- markings --------------------------------------------------------------------
    half paint = 0.0, paintYellow = 0.0;
    [branch] if (cls > 0)
    {
        float keyLen = W >= 45.0 ? 45.0 : 30.0;
        float preLen = T > 0.5 ? T : arrows;          // displaced area / arrow runoff before T
        bool keys = cls >= 2;
        // threshold bar where the threshold is displaced or has a runoff before it, and on
        // visual runways (their only threshold marking)
        if (preLen > 0.5 || !keys)
            paint = max(paint, BoxCov(uT, 0.0, 3.0, fp.x) * BoxCov(aw, 0.0, hw - 0.6, fp.y));
        float afterThreshold = 4.0;
        if (keys)
        {
            // piano keys: N stripes, double gap at the centreline, 3 m clear of the edges
            float N = m.z, n = 0.5 * N;
            float sw = min((W - 6.0) / (2.0 * N), 1.8);
            float acrossKeys = PulseCov((aw - sw) / (2.0 * sw), fp.y / (2.0 * sw), 0.5)
                             * BoxCov(aw, sw, 2.0 * n * sw, fp.y);
            paint = max(paint, acrossKeys * BoxCov(uT, 6.0, 6.0 + keyLen, fp.x));
            afterThreshold = 6.0 + keyLen;
        }

        // designator, 12 m past the threshold marking
        float h = clamp(0.4 * W, 6.0, 18.0);
        float desigTop;
        paint = max(paint, DesignatorCov(uT, w, fp, designator, afterThreshold + 12.0, h, desigTop));

        // centreline: 30 m dashes, 20 m gaps, symmetric about the runway's middle
        float cw = cls == 3 ? 0.9 : (cls == 2 ? 0.45 : 0.3);
        paint = max(paint, PulseCov((p.x - 0.5 * L) / 50.0 + 0.3, fp.x / 50.0, 0.6)
                         * BoxCov(w, -0.5 * cw, 0.5 * cw, fp.y) * BoxCov(uT, desigTop + 12.0, 1e5, fp.x));

        // aiming point and touchdown zone
        float aimIn = clamp(0.2 * W, 4.0, 11.25), aimW = clamp(0.2 * W, 3.0, 10.0);
        float aimStart = L >= 2400.0 ? 400.0 : (L >= 1500.0 ? 300.0 : 250.0);
        float aimLen = L >= 2400.0 ? 60.0 : 45.0;
        bool aim = cls >= 2 && L >= 1200.0;
        if (aim)
            paint = max(paint, BoxCov(uT, aimStart, aimStart + aimLen, fp.x) * BoxCov(aw, aimIn, aimIn + aimW, fp.y));
        [branch] if (cls == 3 && uT > 100.0 && uT < 1150.0)
        {
            int groups = L >= 2400.0 ? 6 : 4, g = 0;
            [loop] for (int k = 1; k <= 7 && g < groups; k++)
            {
                float s0 = 150.0 * k;
                if (aim && s0 < aimStart + aimLen && s0 + 22.5 > aimStart) continue;
                float bars = g < 2 ? 3.0 : (g < 4 ? 2.0 : 1.0);
                g++;
                float acrossBars = PulseCov((aw - aimIn) / 3.3, fp.y / 3.3, 1.8 / 3.3)
                                 * BoxCov(aw, aimIn, aimIn + bars * 3.3 - 1.5, fp.y);
                paint = max(paint, acrossBars * BoxCov(uT, s0, s0 + 22.5, fp.x));
            }
        }

        // side stripes
        if (cls == 3 || (cls == 2 && W >= 30.0))
        {
            float ew = W >= 30.0 ? 0.9 : 0.45;
            paint = max(paint, BoxCov(aw, hw - 0.3 - ew, hw - 0.3, fp.y) * BoxCov(u, 0.0, 1e5, fp.x));
        }

        // displaced threshold / arrow runoff: arrows on the centreline, arrowheads at the bar
        [branch] if (preLen > 0.5 && uT < 0.0 && uT > -preLen - 1.0)
        {
            float na = max(2.0, floor(W / 11.0));
            float slot = clamp(floor((w / W + 0.5) * na), 0.0, na - 1.0);
            float wc = ((slot + 0.5) / na - 0.5) * W;
            paint = max(paint, ArrowheadCov(uT, w, fp, wc, -3.0, 1.8, 9.0));
            if (preLen > 35.0)
            {
                // arrows 30 m apart, pointing at the threshold
                float a0 = -preLen + 6.0, period = 30.0;
                float idx = floor((uT - a0) / period);
                float tip = a0 + idx * period + 24.0;
                if (tip < -15.0 && idx >= 0.0)
                {
                    float headCov = ArrowheadCov(uT, w, fp, 0.0, tip, 2.0, 9.0);
                    float shaftCov = BoxCov(uT, tip - 24.0, tip - 8.5, fp.x) * BoxCov(w, -0.45, 0.45, fp.y);
                    paint = max(paint, max(headCov, shaftCov));
                }
            }
        }

        // blast pad: yellow chevrons pointing at the runway, 45 degrees
        [branch] if (inBlast && blast > 0.5)
        {
            float s = -arrows - u;
            float chevron = s - aw;
            float spacing = 20.0, cwid = 0.9 * 1.4142;
            paintYellow = PulseCov((chevron - 6.0) / spacing, (fp.x + fp.y) / spacing, cwid / spacing)
                        * BoxCov(aw, 0.0, hw - 1.0, fp.y) * BoxCov(s, 1.0, blast - 1.0, fp.x);
        }
    }

    // worn paint, partly under the rubber it shares the touchdown zone with
    half wear = (0.84 + 0.16 * RunwayNoise(p, 128.0, dpx, dpy, seed + 0.8).a) * (0.9 + 0.1 * nGrain.r);
    half3 paintWhite = lerp(white * wear, rubberColor * 1.5, rubberDark * 0.6);
    col = lerp(col, paintWhite, paint);
    col = lerp(col, yellow * wear, paintYellow);
    return col;
}

#endif
