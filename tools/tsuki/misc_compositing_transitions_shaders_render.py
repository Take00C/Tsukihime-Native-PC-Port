"""Acceptance renders for docs/formats/compositing.md (writes PNGs into samples/compose/).

Pictures are read from the user's game folder through tsuki.catalog / tsuki.image; the
compositing is tsuki.compose (shader + blend-state math) and the CPU colour effect of
tsuki.misc_compositing_transitions_shaders.  Pictures are the catalog's logical pictures with
their anchor at the script position (_STCH x, y, zoom 1000), the placement of script.md 4.5;
exact layer geometry (zoom, shake margins, draw order by priority) is the layers document's.
"""
from __future__ import annotations

from pathlib import Path
from typing import Dict, List, Optional, Tuple, Union

import numpy as np

from . import compose as cp

PathLike = Union[str, Path]
W, H = 1920, 1080


def _game(game: Optional[PathLike]) -> Path:
    from tsuki import game_dir
    return Path(game) if game else game_dir()


class Pictures:
    """Script picture id (``img2345``) -> logical picture (catalog record, cropped to its
    width x height) and its anchor; archive entries (``RULE_01.MZP``) as stored."""

    def __init__(self, game: Optional[PathLike] = None) -> None:
        from tsuki.catalog import ScriptImageResolver
        from tsuki.image_catalog import CatalogResolver, load_catalog
        self.game = _game(game)
        self.res = ScriptImageResolver.from_game(self.game)
        self.cat = load_catalog(self.game / "eboot.bin")
        self.cr = CatalogResolver(self.game / "image")
        self._cache: Dict[str, Tuple[np.ndarray, Tuple[int, int]]] = {}

    def entry(self, archive: str, key: Union[int, str]) -> np.ndarray:
        from tsuki import image
        return image.load(self.game / "image" / archive, key).rgba

    def script(self, name: str) -> Tuple[np.ndarray, Tuple[int, int]]:
        """(RGBA uint8 of the logical picture, anchor in picture pixels)."""
        if name in self._cache:
            return self._cache[name]
        k = self.res.resolve(name).index
        if k < 0:
            raise FileNotFoundError(name)
        e = self.cat[k]
        rgba = self.cr.render(e, crop_to_logical=True)
        anchor = (3 * e.anchor_x, round(e.anchor_y * e.height / max(1, e.h3)))
        self._cache[name] = (rgba, anchor)
        return self._cache[name]


def placed(pic: Union[np.ndarray, Tuple[np.ndarray, Tuple[int, int]]], x: int = 240,
           y: int = 136) -> np.ndarray:
    """Float RGBA screen-sized layer with the picture's anchor at script position (x, y)
    (zoom 1000; engine x' = trunc(4x/3), y' = trunc(4y/3) in the 640-wide space, 3 px per
    unit; script.md 4.5).  A bare array (no anchor) is centred."""
    if isinstance(pic, tuple):
        rgba, (ax, ay) = pic
        left = 3 * (4 * x // 3) - ax
        top = 3 * (4 * y // 3) - ay
    else:
        rgba = pic
        left, top = (W - rgba.shape[1]) // 2, (H - rgba.shape[0]) // 2
    out = np.zeros((H, W, 4), np.float32)
    h, w = rgba.shape[:2]
    x0, y0 = max(0, left), max(0, top)
    x1, y1 = min(W, left + w), min(H, top + h)
    if x1 > x0 and y1 > y0:
        out[y0:y1, x0:x1] = cp.to_float(rgba[y0 - top:y1 - top, x0 - left:x1 - left])
    return out


centred = placed


def _save(rgb: np.ndarray, path: Path) -> Path:
    from PIL import Image
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(cp.to_u8(rgb)[..., :3], "RGB").save(path)
    return path


def _sheet(tiles: List[Tuple[str, np.ndarray]], path: Path, scale: int = 4) -> Path:
    """Side-by-side downscaled tiles with a caption strip (for quick viewing)."""
    from PIL import Image, ImageDraw
    w, h = W // scale, H // scale
    sheet = Image.new("RGB", (w * len(tiles), h + 18), (32, 32, 32))
    d = ImageDraw.Draw(sheet)
    for k, (cap, img) in enumerate(tiles):
        im = Image.fromarray(cp.to_u8(img)[..., :3], "RGB").resize((w, h), Image.BILINEAR)
        sheet.paste(im, (k * w, 18))
        d.text((k * w + 4, 3), cap, fill=(255, 255, 255))
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(path)
    return path


def scene(layers: List[Tuple[np.ndarray, float, int, bool]]) -> np.ndarray:
    """Draw (picture, opacity, STBR mode, STBR flag) layers in order onto a black screen."""
    rt = np.zeros((H, W, 4), np.float32)
    for pic, alpha, mode, flag in layers:
        rt = cp.draw_layer(rt, pic, alpha, mode, flag)
    return rt


def render_all(out: Path, game: Optional[PathLike] = None) -> List[Path]:
    from tsuki.misc_compositing_transitions_shaders import read_stec_tables, stec_apply_rgba
    from eboot import eboot_path
    pics = Pictures(game)
    written: List[Path] = []

    # 1. rule wipe RULE_01, IMG1685 (night room) -> IMG1692, remaining progress 0.75/0.5/0.25
    old = scene([(centred(pics.script("img1685")), 1.0, 0, False)])
    new = scene([(centred(pics.script("img1692")), 1.0, 0, False)])
    rule = centred(pics.entry("allpac3", "RULE_01.MZP"))
    tiles = [("rule RULE_01", rule)]
    for done in (25, 50, 75):
        fr = cp.rule_wipe(old, new, rule, 1.0 - done / 100.0)
        written.append(_save(fr, out / f"rule01_wipe_{done:02d}.png"))
        tiles.append((f"wipe {done}% done", fr))
    written.append(_sheet(tiles, out / "rule01_wipe_sheet.png"))
    xf = cp.crossfade(old, new, 0.5)
    written.append(_save(xf, out / "crossfade_50.png"))

    # 2. _STBR 17 (ps_dodge5) from 13_01_02_CIEL13_7B statements 1481..1486:
    #    layer 24 img2182 alpha 255 (normal), layer 25 img1519 alpha 255 _STBR(25,15) (ps_diff5),
    #    layer 26 img0795 alpha 144 _STBR(26,17); (the later layers 27.. of that montage omitted;
    #    layer 24 sits at y 131 instead of 136, i.e. 5 units higher, ignored here)
    l24 = placed(pics.script("img2182"), 240, 131)
    l25 = centred(pics.script("img1519"))
    l26 = centred(pics.script("img0795"))
    s2 = scene([(l24, 1.0, 0, False), (l25, 1.0, 15, False)])
    s17 = scene([(l24, 1.0, 0, False), (l25, 1.0, 15, False), (l26, 144 / 255, 17, False)])
    s17n = scene([(l24, 1.0, 0, False), (l25, 1.0, 15, False), (l26, 144 / 255, 0, False)])
    written.append(_save(s17, out / "stbr17_ciel13_7b.png"))
    bg = centred(pics.script("img1685"))
    demo = scene([(bg, 1.0, 0, False), (l26, 144 / 255, 17, False)])
    written.append(_save(demo, out / "stbr17_demo_img0795_on_img1685.png"))
    written.append(_sheet([("img2182", scene([(l24, 1.0, 0, False)])), ("+img1519 STBR 15", s2),
                           ("+img0795 a144 normal", s17n), ("+img0795 a144 STBR 17", s17),
                           ("demo: img0795 STBR 17 on img1685", demo)],
                          out / "stbr17_sheet.png"))

    # 3. _STBR 11 (ps_add) from 05_00_ARC05_5 statement 3498:
    #    layer 4 img2162 alpha 255, layer 5 img2230 alpha 255, _STBR(5,11)
    base = centred(pics.script("img2162"))
    ov = centred(pics.script("img2230"))
    s0 = scene([(base, 1.0, 0, False)])
    s11 = scene([(base, 1.0, 0, False), (ov, 1.0, 11, False)])
    s11n = scene([(base, 1.0, 0, False), (ov, 1.0, 0, False)])
    written.append(_save(s11, out / "stbr11_arc05_5.png"))
    written.append(_sheet([("img2162", s0), ("+img2230 normal", s11n),
                           ("+img2230 STBR 11", s11)], out / "stbr11_sheet.png"))

    # 4. sepia: the (unreachable) sepia 2DTex shader with the engine defaults (Cb -0.1,
    #    Cr 0.1, blend 1.0; DisplaySurface ctor 0xEF81E / 0xEF826) and the CPU effect that the
    #    scripts actually use (_STEC 3 / 78 = luma -> sepia LUT, 2 = grey, 4 = negative)
    src = pics.script("img1685")[0]
    pic = placed(pics.script("img1685"))
    sep = cp.ps_sepia_2dtex(pic, np.ones(4, np.float32), (-0.1, 0.1, 1.0, 0.0))
    written.append(_save(sep, out / "sepia_shader_img1685.png"))
    tabs = read_stec_tables(eboot_path())
    tiles = [("img1685", pic), ("sepia shader", sep)]
    for eid in (78, 3, 2, 4):
        img = placed((stec_apply_rgba(src, eid, tabs=tabs), pics.script("img1685")[1]))
        written.append(_save(img, out / f"stec{eid:02d}_img1685.png"))
        tiles.append((f"STEC {eid}", img))
    written.append(_sheet(tiles, out / "sepia_stec_sheet.png"))
    return written
