#!/usr/bin/env python3
"""Generate the nvim-winui-gui app icon (ICO + PNG + SVG).

The "N" outline is MEASURED from the reference Neovim logo (561x648 viewBox):
blue left stem + green body, everything on 45-degree bevels. The tile is a
Fluent-style superellipse (squircle); the "</>" of the Windows App SDK / WinUI
mark is used by the code-tag variant.

usage:
  python make_icon.py --preview DIR          # contact sheet + per-variant PNGs
  python make_icon.py --install v1           # write src/App/Assets/*
"""
import argparse, math, os
from PIL import Image, ImageChops, ImageDraw
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, ".."))
ASSETS = os.path.join(ROOT, "src", "App", "Assets")

P_GREEN = [(56, 104), (155, 5), (407, 390), (407, 4), (543, 141), (544, 505), (409, 641)]
P_BLUE = [(19, 143), (56, 104), (155, 254), (155, 641), (19, 505)]
BOX = (19.0, 4.0, 544.0, 641.0)
SLOPE = 0.6574

NV_GREEN = (0x57, 0xA1, 0x43)
NV_BLUE = (0x06, 0x74, 0xB3)
WHITE = (0xFF, 0xFF, 0xFF)
FLUENT_A = (0x0F, 0x6C, 0xBD)
FLUENT_B = (0x08, 0x45, 0x7F)
DARK_A = (0x1D, 0x23, 0x2C)
DARK_B = (0x0C, 0x10, 0x15)
BORDER = (0x55, 0x5F, 0x6D)

SS = 4
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]
PNG_SIZES = [16, 24, 32, 48, 64, 128, 256, 512, 1024]


def squircle_pts(cx, cy, r, n=4.0, steps=960):
    pts = []
    for i in range(steps):
        t = 2.0 * math.pi * i / steps
        ct, st = math.cos(t), math.sin(t)
        pts.append((cx + math.copysign(abs(ct) ** (2.0 / n), ct) * r,
                    cy + math.copysign(abs(st) ** (2.0 / n), st) * r))
    return pts


def squircle_mask(size, inset=0.055, n=4.0, ss=SS):
    S = size * ss
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon(squircle_pts(S / 2, S / 2, S / 2 - inset * S, n), fill=255)
    return m


def ring_mask(size, inset=0.055, w=0.012, n=4.0, ss=SS):
    S = size * ss
    o = Image.new("L", (S, S), 0)
    ImageDraw.Draw(o).polygon(squircle_pts(S / 2, S / 2, S / 2 - inset * S, n), fill=255)
    i = Image.new("L", (S, S), 0)
    ImageDraw.Draw(i).polygon(squircle_pts(S / 2, S / 2, S / 2 - inset * S - w * S, n), fill=255)
    return ImageChops.subtract(o, i)


def gradient(size, c1, c2, angle=45.0):
    a = math.radians(angle)
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    t = xx * math.cos(a) + yy * math.sin(a)
    t = (t - t.min()) / max(1e-6, (t.max() - t.min()))
    img = np.zeros((size, size, 3), np.float32)
    for i in range(3):
        img[..., i] = c1[i] + (c2[i] - c1[i]) * t
    return img


def _mapper(size, scale_h, dx, dy, ss):
    S = size * ss
    x0, y0, x1, y1 = BOX
    k = (scale_h * S) / (y1 - y0)
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
    ox, oy = S / 2.0 + dx * S, S / 2.0 + dy * S
    return S, lambda pt: (ox + (pt[0] - cx) * k, oy + (pt[1] - cy) * k)


def n_mask(size, scale_h, dx=0.0, dy=0.0, ss=SS):
    S, T = _mapper(size, scale_h, dx, dy, ss)
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    for poly in (P_GREEN, P_BLUE):
        d.polygon([T(pt) for pt in poly], fill=255)
    return m


def stem_mask(size, scale_h, dx=0.0, dy=0.0, ss=SS):
    S, T = _mapper(size, scale_h, dx, dy, ss)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon([T(pt) for pt in P_BLUE], fill=255)
    return m


def chevron_mask(size, side, vx, half_h, thick, ss=SS):
    S = size * ss
    cy = S / 2.0
    x = vx * S
    pts = [(x - side * half_h * S, cy - half_h * S), (x, cy), (x - side * half_h * S, cy + half_h * S)]
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).line(pts, fill=255, width=max(1, int(thick * S)), joint="curve")
    return m


def diagonal_mask(size, half_h, width, ss=SS):
    S = size * ss
    c = S / 2.0
    h, w = half_h * S, width * S
    tx, bx = c - SLOPE * h, c + SLOPE * h
    pts = [(tx - w / 2, c - h), (tx + w / 2, c - h), (bx + w / 2, c + h), (bx - w / 2, c + h)]
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon(pts, fill=255)
    return m


def over(base, color, mask):
    b = np.asarray(base).astype(np.float32)
    m = (np.asarray(mask).astype(np.float32) / 255.0)[..., None]
    c = np.array(color, np.float32)[None, None, :]
    out = b.copy()
    out[..., :3] = b[..., :3] * (1 - m) + c * m
    out[..., 3] = np.clip(b[..., 3] + 255.0 * m[..., 0], 0, 255)
    return Image.fromarray(out.astype(np.uint8), "RGBA")


def tile_base(size, c1, c2, inset=0.055, angle=45.0):
    g = gradient(size, c1, c2, angle)
    alpha = np.asarray(squircle_mask(size, inset=inset).resize((size, size), Image.LANCZOS))
    out = np.zeros((size, size, 4), np.uint8)
    out[..., :3] = g.astype(np.uint8)
    out[..., 3] = alpha
    return Image.fromarray(out, "RGBA")


def down(mask, size):
    return mask.resize((size, size), Image.LANCZOS)


# ---- WinUI reference tile (hexagon) measured from image_0b53b2 -------------
# flat top/bottom edges + pointed left/right vertices at mid height, radial blue gradient,
# white "<" ">" chevrons whose arms run parallel to the hexagon's slanted sides.
HEX_ASPECT = 357.0 / 311.0          # width : height
REF_IN = (0x5A, 0xA5, 0xE8)         # brightest gradient sample (#519FE6)
REF_OUT = (0x0B, 0x47, 0xA5)        # navy edge (#0C48A5)
HEX_SLOPE = 0.591                   # dx/dy of the slanted sides and of the chevron arms


def hexagon_pts(cx, cy, w, h):
    ew = 0.249 * w
    eh = 0.5 * h
    return [(cx - ew, cy - eh), (cx + ew, cy - eh), (cx + 0.5 * w, cy),
            (cx + ew, cy + eh), (cx - ew, cy + eh), (cx - 0.5 * w, cy)]


def hexagon_mask(size, width_frac=0.92, ss=SS):
    S = size * ss
    w = width_frac * S
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon(hexagon_pts(S / 2, S / 2, w, w / HEX_ASPECT), fill=255)
    return m


def radial_gradient(size, c_in, c_out, center=(0.58, 0.34), gamma=1.1):
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
    x = xx / max(1, size - 1) - center[0]
    y = yy / max(1, size - 1) - center[1]
    d = np.sqrt(x * x + y * y) / math.hypot(max(center[0], 1 - center[0]), max(center[1], 1 - center[1]))
    t = np.clip(d, 0, 1) ** gamma
    img = np.zeros((size, size, 3), np.float32)
    for i in range(3):
        img[..., i] = c_in[i] + (c_out[i] - c_in[i]) * t
    return img


def hex_tile_base(size, width_frac=0.92, c_in=REF_IN, c_out=REF_OUT):
    g = radial_gradient(size, c_in, c_out)
    alpha = np.asarray(hexagon_mask(size, width_frac).resize((size, size), Image.LANCZOS))
    out = np.zeros((size, size, 4), np.uint8)
    out[..., :3] = g.astype(np.uint8)
    out[..., 3] = alpha
    return Image.fromarray(out, "RGBA")


def ref_chevron_mask(size, width_frac=0.92, ss=SS):
    """The reference's white "<" ">" pair (arms parallel to the hexagon sides, flat ends)."""
    S = size * ss
    w = width_frac * S
    h = w / HEX_ASPECT
    th = 0.143 * w
    hh = 0.405 * h
    cx = cy = S / 2.0
    m = Image.new("L", (S, S), 0)
    d = ImageDraw.Draw(m)
    for side in (-1, 1):
        vx = cx + side * (0.5 * w - 0.064 * w)
        dx = -side * HEX_SLOPE * hh
        d.polygon([(vx, cy), (vx + dx, cy - hh), (vx + dx - side * th, cy - hh),
                   (vx - side * th, cy), (vx + dx - side * th, cy + hh), (vx + dx, cy + hh)], fill=255)
    return m


# ---- Neovim N split into its parts (same measured geometry) ----------------
# The N's diagonal band, clipped at the right stem's left edge (x=407):
#   L1 = its left edge (the blue stem's right edge in the upper half)
#   L2 = its right edge (from the top vertex down to the stem)
DIAG_BAND = [(57, 105), (155, 5), (407, 390), (407, 637.5)]
# The N's outer silhouette (union of stem + body), for outline/stroke rendering.
N_OUTLINE = [(19, 143), (155, 5), (407, 390), (407, 4), (543, 141), (544, 505),
             (409, 641), (155, 254), (155, 641), (19, 505)]


def _poly_mask(size, scale_h, poly, dx=0.0, dy=0.0, ss=SS):
    S, T = _mapper(size, scale_h, dx, dy, ss)
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon([T(pt) for pt in poly], fill=255)
    return m


def diag_mask(size, scale_h, dx=0.0, dy=0.0, ss=SS):
    return _poly_mask(size, scale_h, DIAG_BAND, dx, dy, ss)


def stroke_mask(mask, k):
    """Ring of `k` supersampled pixels inside the mask's silhouette."""
    from PIL import ImageFilter
    return ImageChops.subtract(mask, mask.filter(ImageFilter.MinFilter(2 * k + 1)))


def bracket_mask(size, side, vx_frac, half_h_frac, th_frac, slope=HEX_SLOPE, cap="h", ss=SS):
    """One WinUI-style white angle bracket: vertex at vx_frac, arms at `slope`.

    cap="h" -> arm ends cut horizontally (as in the WinUI reference image);
    cap="v" -> arm ends cut vertically, so the tip is a vertical edge of `th/slope` height
               and the arm's reach inward ends at the outer corner instead of th further in.
    """
    S = size * ss
    c = S / 2.0
    vx = c + side * vx_frac * S
    hh = half_h_frac * S
    th = th_frac * S
    dx = -side * slope * hh
    if cap == "v":
        dy = th / slope
        pts = [(vx, c), (vx + dx, c - hh), (vx + dx, c - hh + dy),
               (vx - side * th, c), (vx + dx, c + hh - dy), (vx + dx, c + hh)]
    else:
        pts = [(vx, c), (vx + dx, c - hh), (vx + dx - side * th, c - hh),
               (vx - side * th, c), (vx + dx - side * th, c + hh), (vx + dx, c + hh)]
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).polygon(pts, fill=255)
    return m


def n_scale(size):
    """Optical sizing: enlarge the N on small raster sizes so the strokes stay legible."""
    return 0.60 if size >= 64 else (0.68 if size >= 32 else 0.76)


def build(variant, size):
    if variant == "v1":
        img = tile_base(size, FLUENT_A, NV_GREEN, inset=0.055, angle=45.0)
        img = over(img, WHITE, down(n_mask(size, n_scale(size)), size))
    elif variant == "v2":
        img = tile_base(size, DARK_A, DARK_B, inset=0.055, angle=90.0)
        img = over(img, BORDER, down(ring_mask(size, inset=0.055, w=0.012), size))
        img = over(img, NV_GREEN, down(n_mask(size, n_scale(size)), size))   # body first
        img = over(img, NV_BLUE, down(stem_mask(size, n_scale(size)), size))    # then the blue stem on top
    elif variant == "v3":
        img = tile_base(size, FLUENT_A, FLUENT_B, inset=0.055, angle=90.0)
        # the green N-slash goes BEHIND the white code chevrons
        img = over(img, NV_GREEN, down(diagonal_mask(size, 0.200, 0.090), size))
        img = over(img, WHITE, down(chevron_mask(size, -1, 0.200, 0.175, 0.065), size))
        img = over(img, WHITE, down(chevron_mask(size, +1, 0.800, 0.175, 0.065), size))
    elif variant == "v4":     # WinUI reference hexagon tile, white N
        img = hex_tile_base(size)
        img = over(img, WHITE, down(n_mask(size, n_scale(size)), size))
    elif variant == "v5":     # hexagon tile: reference "< >" tag + the N's diagonal as a green slash
        img = hex_tile_base(size)
        img = over(img, NV_GREEN, down(diagonal_mask(size, 0.245, 0.135), size))
        img = over(img, WHITE, down(ref_chevron_mask(size), size))
    elif variant == "v6":     # proven squircle tile, but in the reference's radial blue gradient
        img = tile_base(size, REF_IN, REF_OUT, inset=0.055, angle=45.0)
        img = over(img, WHITE, down(n_mask(size, n_scale(size)), size))
    elif variant == "v7":     # v5 palette + the Neovim N outline: white stems, green diagonal
        img = hex_tile_base(size)
        img = over(img, WHITE, down(n_mask(size, n_scale(size)), size))
        img = over(img, NV_GREEN, down(diag_mask(size, n_scale(size)), size))
    elif variant == "v8":     # v5 palette, Neovim's own colour placement: green left stem
        img = hex_tile_base(size)
        img = over(img, WHITE, down(n_mask(size, n_scale(size)), size))
        img = over(img, NV_GREEN, down(stem_mask(size, n_scale(size)), size))
    elif variant == "v9":     # v5 palette, the Neovim N as a hollow outline (green diagonal inside)
        img = hex_tile_base(size)
        img = over(img, NV_GREEN, down(diag_mask(size, n_scale(size)), size))
        ring = stroke_mask(n_mask(size, n_scale(size)), max(3, int(0.028 * size * SS)))
        img = over(img, WHITE, down(ring, size))
    elif variant == "v10":    # v1 tile + white N flanked by white "< >" brackets
        img = tile_base(size, FLUENT_A, NV_GREEN, inset=0.055, angle=45.0)
        if size <= 24:
            # optical sizing: below 24px the brackets only add mush, so drop them and bolden the N
            img = over(img, WHITE, down(n_mask(size, 0.68), size))
        else:
            img = over(img, WHITE, down(n_mask(size, 0.54 if size >= 64 else 0.58), size))
            # brackets: arm ends cut VERTICALLY (cap="v"), so the tip's vertical edge is
            # th/slope ~= 0.13 of the tile tall; vertex pulled in to 0.400 to rebalance the position
            # (a vertical cap shortens the arm's inward reach by th).
            hh = 0.150 if size >= 64 else 0.155
            th = 0.075 if size >= 64 else 0.082
            for side in (-1, 1):
                img = over(img, WHITE, down(bracket_mask(size, side, 0.400, hh, th, cap="v"), size))
    elif variant == "v11":    # v1 tile + full-size white N, larger brackets tucked behind it
        img = tile_base(size, FLUENT_A, NV_GREEN, inset=0.055, angle=45.0)
        img = over(img, WHITE, down(n_mask(size, 0.60), size))
        for side in (-1, 1):
            img = over(img, WHITE, down(bracket_mask(size, side, 0.40, 0.24, 0.075), size))
    elif variant == "v12":    # v10 proportions kept at EVERY size (no optical drop of the brackets)
        img = tile_base(size, FLUENT_A, NV_GREEN, inset=0.055, angle=45.0)
        img = over(img, WHITE, down(n_mask(size, 0.54), size))
        for side in (-1, 1):
            img = over(img, WHITE, down(bracket_mask(size, side, 0.400, 0.150, 0.075, cap="v"), size))
    elif variant in ("v13", "v14", "v15"):
        # vertical caps, three tuned readings of the same idea.  16-24px: same optical rule as v10
        # (drop the brackets, bolden the N) -- otherwise the 1-2px gap between N and brackets mushes.
        img = tile_base(size, FLUENT_A, NV_GREEN, inset=0.055, angle=45.0)
        if size <= 24:
            img = over(img, WHITE, down(n_mask(size, 0.68), size))
        else:
            img = over(img, WHITE, down(n_mask(size, 0.54 if size >= 64 else 0.58), size))
            if variant == "v13":      # thinner stroke: the tip's cut is shorter (th/slope)
                vx, hh, th, sl = 0.400, 0.150, 0.050, HEX_SLOPE
            elif variant == "v14":    # longer arms: the cut is a smaller share of the arm
                vx, hh, th, sl = 0.400, 0.220, 0.065, HEX_SLOPE
            else:  # v15            # 45-degree arms: same thickness, much shorter vertical cut
                vx, hh, th, sl = 0.425, 0.135, 0.075, 1.0
            if size < 64:
                th += 0.008            # keep the sub-64px strokes readable
            for side in (-1, 1):
                img = over(img, WHITE, down(bracket_mask(size, side, vx, hh, th, slope=sl, cap="v"), size))
    else:
        raise SystemExit("variant must be v1..v15")
    return img


def ascii_preview(img, label):
    im = img.resize((22, 22), Image.LANCZOS)
    print("--- %s @22px" % label)
    for y in range(22):
        line = ""
        for x in range(22):
            r, g, b, a = im.getpixel((x, y))
            v = (r + g + b) / 3.0
            if a < 128:
                line += " "
            elif v > 195:
                line += "#"
            elif v > 110:
                line += "+"
            elif b > r + 30 and b > g + 30:
                line += "B"
            elif g > r + 18 and g > b + 18:
                line += "g"
            else:
                line += "."
        print(line)


def contact_sheet(variants, path, bg=(0x2B, 0x2F, 0x36)):
    sizes = [256, 64, 32, 16]
    pad, top = 16, 26
    W = pad + len(variants) * (256 + pad)
    H = top + sum(s + pad for s in sizes) + pad
    sh = Image.new("RGB", (W, H), bg)
    d = ImageDraw.Draw(sh)
    for ci, v in enumerate(variants):
        x = pad + ci * (256 + pad)
        d.text((x, 8), v, fill=(255, 255, 255))
        y = top
        for s in sizes:
            im = build(v, s)
            sh.paste(im, (x, y), im)
            d.text((x + s + 10, y + s // 2), "%dpx" % s, fill=(190, 195, 205))
            y += s + pad
    sh.save(path)
    return path


def svg_for(variant, path, s=512):
    if variant in ("v7", "v8", "v9"):
        def pp(pts):
            return "M " + " L ".join("%.2f %.2f" % pt for pt in pts) + " Z"
        w = 0.92 * s
        tile = pp(hexagon_pts(s / 2, s / 2, w, w / HEX_ASPECT))
        grad = ('<radialGradient id="g" cx="0.58" cy="0.34" r="1.05">'
                '<stop offset="0" stop-color="#5AA5E8"/><stop offset="1" stop-color="#0B47A5"/></radialGradient>')
        c = s / 2.0
        x0, y0, x1, y1 = BOX
        k = n_scale(s) * s / (y1 - y0)
        mcx, mcy = (x0 + x1) / 2.0, (y0 + y1) / 2.0

        def np(poly):
            return "M " + " L ".join("%.2f %.2f" % (c + (pt[0] - mcx) * k, c + (pt[1] - mcy) * k)
                                     for pt in poly) + " Z"

        body = '<path d="%s" fill="url(#g)"/>' % tile
        n_white = '<path d="%s" fill="#FFFFFF"/>' % np(P_GREEN) + '<path d="%s" fill="#FFFFFF"/>' % np(P_BLUE)
        if variant == "v7":
            body += n_white + '<path d="%s" fill="#57A143"/>' % np(DIAG_BAND)
        elif variant == "v8":
            body += n_white + '<path d="%s" fill="#57A143"/>' % np(P_BLUE)
        else:
            body += ('<path d="%s" fill="#57A143"/>' % np(DIAG_BAND)
                     + '<path d="%s" fill="none" stroke="#FFFFFF" stroke-width="%.1f" stroke-linejoin="miter"/>'
                     % (np(N_OUTLINE), 0.056 * s))
        open(path, "w", encoding="utf-8", newline="\n").write(
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d">\n'
            '  <defs>%s</defs>\n  %s\n</svg>\n' % (s, s, s, s, grad, body))
        return path
    if variant in ("v10", "v11", "v12", "v13", "v14", "v15"):
        # bracketed variants: squircle tile + white N + white chevrons.
        # the vertical-cut polygon here must stay identical to bracket_mask(cap="v").
        prm = {"v10": (0.400, 0.150, 0.075, HEX_SLOPE, "v"), "v11": (0.40, 0.24, 0.075, HEX_SLOPE, "h"),
               "v12": (0.400, 0.150, 0.075, HEX_SLOPE, "v"), "v13": (0.400, 0.150, 0.050, HEX_SLOPE, "v"),
               "v14": (0.400, 0.220, 0.065, HEX_SLOPE, "v"), "v15": (0.425, 0.135, 0.075, 1.0, "v")}
        vx_f, hh_f, th_f, sl, cap = prm[variant]
        nsc = 0.60 if variant == "v11" else 0.54
        c = s / 2.0
        x0, y0, x1, y1 = BOX
        k = nsc * s / (y1 - y0)
        hh, th = hh_f * s, th_f * s
        grad = ('<linearGradient id="g" x1="0" y1="0" x2="1" y2="1">'
                '<stop offset="0" stop-color="#0F6CBD"/><stop offset="1" stop-color="#57A143"/></linearGradient>')
        body = '<path d="%s" fill="url(#g)"/>' % ("M " + " L ".join(
            "%.2f %.2f" % pt for pt in squircle_pts(s / 2, s / 2, s / 2 - 0.055 * s, 4.0, 192)) + " Z")
        mcx, mcy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        for poly in (P_GREEN, P_BLUE):
            body += '<path d="%s" fill="#FFFFFF"/>' % ("M " + " L ".join(
                "%.2f %.2f" % (c + (pt[0] - mcx) * k, c + (pt[1] - mcy) * k) for pt in poly) + " Z")
        for side in (-1, 1):
            vx = c + side * vx_f * s
            dx = -side * sl * hh
            if cap == "v":
                dy = th / sl
                poly = [(vx, c), (vx + dx, c - hh), (vx + dx, c - hh + dy), (vx - side * th, c),
                        (vx + dx, c + hh - dy), (vx + dx, c + hh)]
            else:
                poly = [(vx, c), (vx + dx, c - hh), (vx + dx - side * th, c - hh), (vx - side * th, c),
                        (vx + dx - side * th, c + hh), (vx + dx, c + hh)]
            body += '<path d="%s" fill="#FFFFFF"/>' % ("M " + " L ".join("%.2f %.2f" % pt for pt in poly) + " Z")
        open(path, "w", encoding="utf-8", newline="\n").write(
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d">\n'
            '  <defs>%s</defs>\n  %s\n</svg>\n' % (s, s, s, s, grad, body))
        return path
    if variant in ("v4", "v5", "v6"):
        def pp(pts):
            return "M " + " L ".join("%.2f %.2f" % pt for pt in pts) + " Z"
        grad = ('<radialGradient id="g" cx="0.58" cy="0.34" r="1.05">'
                '<stop offset="0" stop-color="#5AA5E8"/><stop offset="1" stop-color="#0B47A5"/></radialGradient>')
        if variant == "v6":
            tile = pp(squircle_pts(s / 2, s / 2, s / 2 - 0.055 * s, 4.0, 192))
        else:
            w = 0.92 * s
            tile = pp(hexagon_pts(s / 2, s / 2, w, w / HEX_ASPECT))
        body = '<path d="%s" fill="url(#g)"/>' % tile
        c = s / 2.0
        if variant == "v5":
            w = 0.92 * s
            h = w / HEX_ASPECT
            th, hh = 0.143 * w, 0.405 * h
            h2, wd = 0.245 * s, 0.135 * s
            tx, bx = c - SLOPE * h2, c + SLOPE * h2
            body += '<path d="%s" fill="#57A143"/>' % pp([(tx - wd / 2, c - h2), (tx + wd / 2, c - h2),
                                                          (bx + wd / 2, c + h2), (bx - wd / 2, c + h2)])
            for side in (-1, 1):
                vx = c + side * (0.5 * w - 0.064 * w)
                dx = -side * HEX_SLOPE * hh
                body += '<path d="%s" fill="#FFFFFF"/>' % pp([(vx, c), (vx + dx, c - hh), (vx + dx - side * th, c - hh),
                                                              (vx - side * th, c), (vx + dx - side * th, c + hh), (vx + dx, c + hh)])
        else:
            x0, y0, x1, y1 = BOX
            k = n_scale(s) * s / (y1 - y0)
            mcx, mcy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
            for poly in ((P_GREEN, P_BLUE) if variant == "v4" else (P_GREEN,)):
                body += '<path d="%s" fill="#FFFFFF"/>' % ("M " + " L ".join(
                    "%.2f %.2f" % (c + (pt[0] - mcx) * k, c + (pt[1] - mcy) * k) for pt in poly) + " Z")
        open(path, "w", encoding="utf-8", newline="\n").write(
            '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d">\n'
            '  <defs>%s</defs>\n  %s\n</svg>\n' % (s, s, s, s, grad, body))
        return path
    inset = 0.055
    r = s / 2 - inset * s
    pts = squircle_pts(s / 2, s / 2, r, 4.0, 192)
    sq = "M " + " L ".join("%.2f %.2f" % pt for pt in pts) + " Z"
    x0, y0, x1, y1 = BOX
    k = (0.60 * s) / (y1 - y0)
    cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0

    def path_of(poly):
        return "M " + " L ".join("%.2f %.2f" % (s / 2 + (pt[0] - cx) * k, s / 2 + (pt[1] - cy) * k)
                                 for pt in poly) + " Z"

    if variant == "v2":
        grad = ('<linearGradient id="g" x1="0" y1="0" x2="0" y2="1">'
                '<stop offset="0" stop-color="#1D232C"/><stop offset="1" stop-color="#0C1015"/></linearGradient>')
        body = ('<path d="%s" fill="url(#g)"/>' % sq
                + '<path d="%s" fill="none" stroke="#555F6D" stroke-width="%.1f"/>' % (sq, 0.012 * s)
                + '<path d="%s" fill="#0674B3"/>' % path_of(P_BLUE)
                + '<path d="%s" fill="#57A143"/>' % path_of(P_GREEN))
    else:
        # v1/v3 fallback: the squircle tile + the white N silhouette.  (v3's SVG still draws the N
        # rather than its code chevrons -- no vector output for those yet.)
        grad = ('<linearGradient id="g" x1="0" y1="0" x2="1" y2="1">'
                '<stop offset="0" stop-color="#0F6CBD"/><stop offset="1" stop-color="#57A143"/></linearGradient>')
        body = '<path d="%s" fill="url(#g)"/>' % sq
        for poly in (P_GREEN, P_BLUE):
            body += '<path d="%s" fill="#FFFFFF"/>' % path_of(poly)
    svg = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d">\n'
           '  <defs>%s</defs>\n  %s\n</svg>\n' % (s, s, s, s, grad, body))
    open(path, "w", encoding="utf-8", newline="\n").write(svg)
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", metavar="DIR")
    ap.add_argument("--install", metavar="VARIANT")
    ap.add_argument("--ascii", action="store_true")
    a = ap.parse_args()

    if a.ascii:
        for v in ("v1", "v2", "v3", "v4", "v5", "v6", "v7", "v8", "v9", "v10", "v11", "v12", "v13", "v14", "v15"):
            ascii_preview(build(v, 64), v)
    if a.preview:
        os.makedirs(a.preview, exist_ok=True)
        vs = ("v1", "v2", "v3", "v4", "v5", "v6", "v7", "v8", "v9", "v10", "v11", "v12", "v13", "v14", "v15")
        print("preview:", contact_sheet(vs, os.path.join(a.preview, "icon_variants.png")))
        for v in vs:
            build(v, 512).save(os.path.join(a.preview, "%s_512.png" % v))
            build(v, 256).save(os.path.join(a.preview, "%s_256.png" % v))
    if a.install:
        v = a.install
        os.makedirs(ASSETS, exist_ok=True)
        build(v, 256).save(os.path.join(ASSETS, "appicon.png"))
        imgs = [build(v, s) for s in ICO_SIZES]
        ico = os.path.join(ASSETS, "appicon.ico")
        try:
            imgs[-1].save(ico, format="ICO", sizes=[(i.width, i.height) for i in imgs], append_images=imgs[:-1])
        except Exception as e:
            print("append_images failed (%s); falling back" % e)
            imgs[-1].save(ico, format="ICO", sizes=[(i.width, i.height) for i in imgs])
        svg_for(v, os.path.join(ASSETS, "appicon.svg"))
        print("installed:", v, "->", ASSETS)


if __name__ == "__main__":
    main()
