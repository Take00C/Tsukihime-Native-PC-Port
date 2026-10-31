"""Draws the port's icon (package K20): original art, no game material.

A crescent moon in the launcher's "blue glass moon" look (src/Tsuki.Launcher/Themes/Theme.xaml: the crescent is a disc with a
second disc cut out, filled with the moon-blue gradient #5E8BFF -> #9CD2FF, with a soft glow and a small four-point sparkle),
on a deep indigo (#0A0F24) rounded square so that it still reads at 16 px. Every size is drawn on its own, supersampled, and
reduced with a Lanczos filter; nothing is a resized copy of another size.

Writes into assets/icon/:
  tsukihime.ico          16, 20, 24, 32, 40, 48, 64, 96, 128 as 32-bit BMP entries and 256 as a PNG entry (the Vista+ form)
  tsukihime_256.png      the 256 px picture (the one a store page or a README would show)
  tsukihime_16.png, tsukihime_32.png, tsukihime_48.png   the small sizes, for checking them by eye
  tsukihime_64.rgba      64 x 64 straight RGBA bytes, row-major, no header: embedded into Tsukihime.exe and handed to SDL_SetWindowIcon

Run:  D:/TsukihimePort/.venv/Scripts/python.exe tools/setup/make_icon.py [--out DIR]
"""
from __future__ import annotations

import argparse
import struct
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)

NIGHT_TOP = (0x0A, 0x0F, 0x24)
NIGHT_BOTTOM = (0x0E, 0x15, 0x32)
BORDER = (0x8F, 0xA6, 0xFF)
MOON_BLUE = (0x5E, 0x8B, 0xFF)
MOON_PALE = (0x9C, 0xD2, 0xFF)
SPARKLE = (0xE9, 0xED, 0xF8)


def _lerp(a: tuple[int, int, int], b: tuple[int, int, int], t: float) -> tuple[int, int, int]:
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))  # type: ignore[return-value]


def _vertical_gradient(size: int, top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    img = Image.new("RGBA", (size, size))
    px = img.load()
    for y in range(size):
        c = _lerp(top, bottom, y / max(1, size - 1))
        for x in range(size):
            px[x, y] = (*c, 255)
    return img


def _rounded_square(size: int, inset: float, radius: float) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    a = inset * size
    d.rounded_rectangle((a, a, size - a, size - a), radius=radius * size, fill=255)
    return mask


def _disc(size: int, cx: float, cy: float, r: float) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.ellipse(((cx - r) * size, (cy - r) * size, (cx + r) * size, (cy + r) * size), fill=255)
    return mask


def _sparkle(size: int, cx: float, cy: float, r: float) -> Image.Image:
    """A four-point star: the launcher's SparkleGeometry (cubic arms pinched at 10 % of the radius), as a polygon."""
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    pts = []
    pinch = 0.10
    # walk the outline: tip, pinch, tip, pinch ... with a smooth curve approximated by 12 points per arm
    for arm in range(4):
        ang0 = arm * 90.0
        for k in range(12):
            t = k / 12.0
            # from tip (t = 0) towards the pinch (t = 1): radius falls like the Bezier of the WPF geometry (0.6, 1.6, 6 control values)
            rr = r * ((1 - t) ** 3 + 3 * (1 - t) ** 2 * t * 0.27 + 3 * (1 - t) * t * t * 0.1 + t ** 3 * pinch)
            ang = ang0 + 45.0 * t
            import math

            pts.append(((cx + rr * math.cos(math.radians(ang))) * size, (cy + rr * math.sin(math.radians(ang))) * size))
    d.polygon(pts, fill=255)
    return mask


def draw(size: int) -> Image.Image:
    """The icon at `size` pixels: drawn at a supersampled size and reduced."""
    ss = max(4, 2048 // size)
    big = size * ss
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))

    # the indigo rounded square (slightly lighter at the bottom, as the launcher's background is) with a thin pale border
    inset = 0.02
    radius = 0.21
    plate_mask = _rounded_square(big, inset, radius)
    plate = _vertical_gradient(big, NIGHT_TOP, NIGHT_BOTTOM)
    canvas.paste(plate, (0, 0), plate_mask)
    border_w = max(1.0, 0.012 * big)
    inner_mask = _rounded_square(big, inset + border_w / big, radius - border_w / big * 0.8)
    border_only = Image.new("L", (big, big), 0)
    border_only.paste(plate_mask)
    border_only = Image.composite(Image.new("L", (big, big), 0), border_only, inner_mask)
    border_layer = Image.new("RGBA", (big, big), (*BORDER, 0))
    border_layer.putalpha(border_only.point(lambda v: int(v * 0.38)))
    canvas.alpha_composite(border_layer)

    # the crescent: disc A less disc B (the launcher's proportions: radii 17 and 14.5 on a 44 canvas, the bite up and to the right)
    ax, ay, ar = 0.455, 0.515, 0.335
    bx, by, br = 0.635, 0.395, 0.285
    crescent = _disc(big, ax, ay, ar)
    bite = _disc(big, bx, by, br)
    crescent = Image.composite(Image.new("L", (big, big), 0), crescent, bite)

    # the glow behind it: the crescent itself, blurred and tinted moon blue
    glow = crescent.filter(ImageFilter.GaussianBlur(radius=0.07 * big))
    glow_layer = Image.new("RGBA", (big, big), (*MOON_BLUE, 0))
    glow_layer.putalpha(glow.point(lambda v: int(v * 0.62)))
    canvas.alpha_composite(Image.composite(glow_layer, Image.new("RGBA", (big, big), (0, 0, 0, 0)), plate_mask))

    # the moon: pale at the top, blue at the bottom
    moon = _vertical_gradient(big, MOON_PALE, MOON_BLUE)
    moon_layer = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    moon_layer.paste(moon, (0, 0), crescent)
    canvas.alpha_composite(moon_layer)

    # the sparkle, in the bite, with a faint halo of its own
    sx, sy, sr = 0.745, 0.60, 0.105 if size >= 24 else 0.12
    star = _sparkle(big, sx, sy, sr)
    halo = star.filter(ImageFilter.GaussianBlur(radius=0.03 * big))
    halo_layer = Image.new("RGBA", (big, big), (*MOON_PALE, 0))
    halo_layer.putalpha(halo.point(lambda v: int(v * 0.5)))
    canvas.alpha_composite(halo_layer)
    star_layer = Image.new("RGBA", (big, big), (*SPARKLE, 0))
    star_layer.putalpha(star)
    canvas.alpha_composite(star_layer)

    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def _bmp_entry(img: Image.Image) -> bytes:
    """A 32-bit BGRA DIB with a doubled height and an empty AND mask, as an .ico stores it."""
    w, h = img.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, w * h * 4 + ((w + 31) // 32) * 4 * h, 0, 0, 0, 0)
    px = img.load()
    rows = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            rows += bytes((b, g, r, a))
    mask_row = ((w + 31) // 32) * 4
    return header + bytes(rows) + bytes(mask_row * h)


def _png_entry(img: Image.Image) -> bytes:
    import io

    buf = io.BytesIO()
    img.save(buf, "PNG", optimize=True)
    return buf.getvalue()


def write_ico(path: Path, images: dict[int, Image.Image]) -> None:
    entries = []
    for s in SIZES:
        data = _png_entry(images[s]) if s >= 256 else _bmp_entry(images[s])
        entries.append((s, data))
    out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
    offset = 6 + 16 * len(entries)
    for s, data in entries:
        dim = 0 if s >= 256 else s
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in entries:
        out += data
    path.write_bytes(out)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=str(Path(__file__).resolve().parents[2] / "assets" / "icon"))
    a = ap.parse_args()
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    images = {s: draw(s) for s in SIZES}
    write_ico(out / "tsukihime.ico", images)
    images[256].save(out / "tsukihime_256.png", "PNG", optimize=True)
    for s in (16, 32, 48):
        images[s].save(out / f"tsukihime_{s}.png", "PNG", optimize=True)
    (out / "tsukihime_64.rgba").write_bytes(images[64].tobytes("raw", "RGBA"))
    for s in SIZES:
        print(f"{s:4d} px: {images[s].getbbox()}")
    print(f"wrote {out / 'tsukihime.ico'} ({(out / 'tsukihime.ico').stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
