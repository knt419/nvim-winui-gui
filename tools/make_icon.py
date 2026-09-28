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
    else:
        raise SystemExit("variant must be v1/v2/v3")
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
        grad = ('<linearGradient id="g" x1="0" y1="0" x2="1" y2="1">'
                '<stop offset="0" stop-color="#0F6CBD"/><stop offset="1" stop-color="#57A143"/></linearGradient>')
        body = '<path d="%s" fill="url(#g)"/>' % sq + '<path d="%s" fill="#FFFFFF"/>' % path_of(P_GREEN)
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
        for v in ("v1", "v2", "v3"):
            ascii_preview(build(v, 64), v)
    if a.preview:
        os.makedirs(a.preview, exist_ok=True)
        print("preview:", contact_sheet(("v1", "v2", "v3"), os.path.join(a.preview, "icon_variants.png")))
        for v in ("v1", "v2", "v3"):
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
