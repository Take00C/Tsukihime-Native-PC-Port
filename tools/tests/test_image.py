"""Tests for tsuki.image (image MZP decoding) and tsuki.image_catalog.

Synthetic tests always run; real-data tests skip when the game folder is missing
(set TSUKI_GAME_DIR to override the default location).
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_image.py -q
"""
from __future__ import annotations

import hashlib
import mmap
import struct
import sys
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import game_dir, image_dir, mzx  # noqa: E402
from tsuki import image as timg  # noqa: E402
from tsuki.mrg import MrgArchive  # noqa: E402
from tsuki.mzp import MzpArchive  # noqa: E402

IMG = image_dir()
needs_game = pytest.mark.skipif(IMG is None, reason="game data not found")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def _hdr(w: int, h: int, tw: int, th: int, bt: int, depth: int, attr: int = 0,
         palette: bytes = b"", flags: bytes | None = None) -> bytes:
    tx, ty = -(-w // tw), -(-h // th)
    if flags is None:
        flags = b"\x01" * (tx * ty)
    return struct.pack("<8H", w, h, tw, th, tx, ty, bt, depth | attr << 8) + palette + flags


def _mzp(sections: list[bytes]) -> bytes:
    """Minimal mrgd00 container (records point at 8-byte aligned data)."""
    n = len(sections)

    recs, body = [], b""
    for s in sections:
        off = len(body)
        so, bo = divmod(off, 0x800)
        ss = -(-(bo + len(s)) // 0x800)
        recs.append(struct.pack("<4H", so, bo, ss, len(s) & 0xFFFF))
        body += s + b"\xff" * (8 - len(s) % 8)
    return b"mrgd00" + struct.pack("<H", n) + b"".join(recs) + body


def test_csm1_table() -> None:
    t = timg.csm1_unswizzle_table()
    assert list(t[:32]) == list(range(8)) + list(range(16, 24)) + list(range(8, 16)) + list(range(24, 32))
    assert np.array_equal(t[t], np.arange(256))          # involution
    assert list(t[224:256]) == [i + 224 for i in list(range(8)) + list(range(16, 24)) + list(range(8, 16)) + list(range(24, 32))]


def test_palette_alpha_rules() -> None:
    raw = bytes([1, 2, 3, 0x00, 4, 5, 6, 0x7F, 7, 8, 9, 0x80, 1, 1, 1, 0xFF] + [0] * (4 * 12))
    p = timg.palette_type1_rgba(raw, 16)                  # 16 colours: no unswizzle
    assert p[:4, 3].tolist() == [0x00, 0xFE, 0xFF, 0xFE]  # 0xFF wraps to 0xFE like the engine
    assert p[1, :3].tolist() == [4, 5, 6]
    h = timg.palette_hep_rgba(raw[:16])
    assert h[:, 3].tolist() == [0x00, 0xFE, 0xFE, 0xFE]   # clamp to 0x7F, then *2


def test_pal8_unswizzle_applied() -> None:
    pal = bytearray(1024)
    for i in range(256):
        pal[4 * i:4 * i + 4] = bytes([i, 0, 0, 0x7F])
    p = timg.palette_type1_rgba(bytes(pal), 256)
    assert p[8, 0] == 16 and p[16, 0] == 8 and p[0, 0] == 0 and p[31, 0] == 31


def test_rgb565_lo_alpha_roundtrip() -> None:
    rng = np.random.default_rng(5)
    rgba = rng.integers(0, 256, (4, 8, 4), dtype=np.uint8)
    r, g, b, a = (rgba[..., i].astype(np.uint16).ravel() for i in range(4))
    word = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3)
    lo = ((r & 7) << 5) | ((g & 3) << 3) | (b & 7)
    data = word.astype("<u2").tobytes() + lo.astype(np.uint8).tobytes() + a.astype(np.uint8).tobytes()
    hdr = timg.parse_header(_hdr(8, 4, 8, 4, 11, 0x14))
    assert hdr.format == timg.PixelFormat.RGB565_LO_A and hdr.tile_bytes == len(data)
    assert np.array_equal(timg.decode_tile(hdr, data), rgba)


def test_pal4_nibble_order() -> None:
    pal = b"".join(bytes([i * 16, 0, 0, 0x80]) for i in range(16))
    hdr = timg.parse_header(_hdr(4, 2, 4, 2, 1, 0x00, palette=pal))
    idx, p = timg.decode_tile_indexed(hdr, bytes([0x21, 0x43, 0x65, 0x87]))
    assert idx.tolist() == [[1, 2, 3, 4], [5, 6, 7, 8]]
    assert p[1].tolist() == [16, 0, 0, 0xFF]


def test_pal4_linear_stream_odd_tile() -> None:
    """Engine VA 0xD7040: one linear stream of n//2 bytes, byte i -> texels 2i (low) and 2i+1
    (high) across row boundaries; with an odd texel count the last texel is not written."""
    pal = b"".join(bytes([i * 16, 0, 0, 0x80]) for i in range(16))
    hdr = timg.parse_header(_hdr(3, 3, 3, 3, 1, 0x00, palette=pal))
    assert hdr.tile_bytes == 4
    idx, _ = timg.decode_tile_indexed(hdr, bytes([0x21, 0x43, 0x65, 0x87]))
    assert idx.tolist() == [[1, 2, 3], [4, 5, 6], [7, 8, 0]]


def test_header_palette_rule() -> None:
    """Engine VA 0xD8DC7..0xD8DFB: bmp_type 1/2 palette is 16 entries for depth code 0, 256 for
    codes 1 and 7, and absent for any other code (flags follow the 16-byte header)."""
    assert len(timg.parse_header(_hdr(8, 8, 8, 8, 1, 0x00, palette=b"\0" * 64)).palette) == 64
    assert len(timg.parse_header(_hdr(8, 8, 8, 8, 1, 0x07, palette=b"\0" * 1024)).palette) == 1024
    h = timg.parse_header(_hdr(8, 8, 8, 8, 1, 0x04, flags=b"\x02"))
    assert h.palette == b"" and h.tile_flags == b"\x02" and h.header_size == 17
    with pytest.raises(timg.ImageError):
        timg.parse_header(_hdr(8, 8, 8, 8, 1, 0x04, palette=b"\0" * 1024))


def test_catalog_band_arithmetic() -> None:
    from tsuki.image_catalog import CatalogEntry, crop_rect, lang_index
    e = CatalogEntry(2438, 0, "img2431.MZP", None, 0, 0, 946, 858, 2838, 2560, 473, 429,
                     0x0B, 0, 0, 2, 0, 0)
    assert e.has_language_bands and not e.is_variant and e.band_stride == 2794
    assert [e.source_rect(x) for x in ("ja", "en", "zc", "zt")] == [
        (0, 0, 2838, 2560), (0, 2794, 2838, 5354), (0, 5588, 2838, 8148), (0, 8382, 2838, 10942)]
    assert e.thumb_rect("en") == (0, 200, 222, 400) and e.flags_a == 0x0200000B
    assert lang_index("ZT") == 3 and lang_index(1) == 1
    with pytest.raises(ValueError):
        lang_index("ko")
    plain = CatalogEntry(0, 0, "BLACK.mzp", None, 0, 0, 640, 362, 1920, 1080, 320, 181, 0x0B, 3, 0, 0, 0, 0)
    assert plain.source_rect("ja") == plain.source_rect("zt") == (0, 0, 1920, 1080) and plain.band_stride == 0
    img = np.full((4, 4, 4), 7, np.uint8)
    c = crop_rect(img, (2, 1, 6, 3))                     # partly outside: transparent padding
    assert c.shape == (2, 4, 4) and c[:, :2].min() == 7 and c[:, 2:].max() == 0


def test_header_validation() -> None:
    good = _hdr(100, 50, 64, 64, 1, 0x11, palette=b"\0" * 1024)
    assert timg.parse_header(good).ntiles == 2
    with pytest.raises(timg.ImageError):
        timg.parse_header(good[:-1])                       # missing a flag byte
    with pytest.raises(timg.ImageError):
        timg.parse_header(struct.pack("<8H", 100, 50, 64, 64, 1, 1, 1, 0x11) + b"\0" * 1025)
    with pytest.raises(timg.ImageError):
        _ = timg.parse_header(_hdr(8, 8, 8, 8, 5, 0x11)).format   # unknown type


def _hep_tile(tw: int, th: int, value: int, alpha: int = 0x7F, unk: int = 2) -> bytes:
    pix = bytes([value]) * (tw * th)
    pal = b"".join(bytes([i, 255 - i, 7, alpha]) for i in range(256))
    tex0 = (0x13 << 20) | ((tw.bit_length() - 1) << 26) | ((th.bit_length() - 1) << 30) | (1 << 34)
    head = struct.pack("<4sIQIIII", b"HEP\0", 32 + len(pix) + 1024, tex0, 0x10, tw, th, unk)
    return head + pix + pal


def test_border_assembly_and_decode_image() -> None:
    tw, th = 8, 4
    # 2x2 tiles of 8x4 with a 1-px border: displayed 12x4 (stored 16x8)
    hdr_b = _hdr(16, 8, tw, th, 12, 0x11, attr=1, flags=bytes([1, 2, 0, 1]))
    tiles = [_hep_tile(tw, th, v) for v in (10, 20, 30, 40)]
    blob = _mzp([hdr_b] + [mzx.compress_literal(t) for t in tiles])
    img = timg.decode_image(MzpArchive(blob))
    assert img.size == (12, 4)
    assert img.skipped_tiles == [2]
    r = img.rgba
    assert r[0, 0].tolist() == [10, 245, 7, 0xFE] and r[0, 6].tolist() == [20, 235, 7, 0xFE]
    assert r[2, 0, 3] == 0 and r[3, 11].tolist() == [40, 215, 7, 0xFE]
    hh = timg.parse_hep_header(tiles[0])
    assert (hh.psm, 1 << hh.tw_log2, 1 << hh.th_log2) == (0x13, tw, th)


def test_compose_over() -> None:
    base = np.zeros((4, 4, 4), np.uint8)
    base[...] = (0, 0, 255, 255)
    lay = np.zeros((2, 2, 4), np.uint8)
    lay[0, 0] = (255, 0, 0, 255)
    lay[0, 1] = (255, 0, 0, 128)
    out = timg.compose(base, lay, (1, 1))
    assert out[1, 1].tolist() == [255, 0, 0, 255]
    assert out[1, 2, 3] == 255 and 126 <= out[1, 2, 0] <= 129 and 126 <= out[1, 2, 2] <= 129
    assert out[0, 0].tolist() == [0, 0, 255, 255] and out[2, 1].tolist() == [0, 0, 255, 255]


def test_split_sprite_name() -> None:
    s = timg.split_sprite_name("ARU_L_81_00_N00_B.MZP")
    assert s is not None and s.is_base and s.group == "ARU_L_81_00" and s.face == "N00"
    s = timg.split_sprite_name("SIK_A_34_00_N00_26_26.MZP")
    assert s is not None and s.suffix == ("26", "26") and not s.is_base
    assert timg.split_sprite_name("IMG0007_04_04.MZP") is None


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

def _sha(a: np.ndarray) -> str:
    return hashlib.sha1(np.ascontiguousarray(a).tobytes()).hexdigest()


@pytest.fixture(scope="module")
def arcs():
    if IMG is None:
        pytest.skip("game data not found")
    d = {n: MrgArchive(IMG / n) for n in ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts")}
    yield d
    for a in d.values():
        a.close()


@needs_game
def test_headers_small_archives(arcs) -> None:
    from collections import Counter
    from tsuki.sniff import classify_mzp
    c: Counter = Counter()
    for an in ("allpac3", "allpac4", "parts"):
        a = arcs[an]
        for e in a.entries:
            m = MzpArchive(a.read(e.index))
            subs = [m.child(i) for i in range(m.count)] if classify_mzp(m) == "archive" else [m]
            for s in subs:
                h = timg.parse_header(s.read(0))
                assert s.count == 1 + h.ntiles
                c[(an, h.format.value)] += 1
    assert c == Counter({("allpac3", "pal8"): 3498, ("allpac3", "rgb565lo+a"): 252, ("allpac3", "pal4"): 14,
                         ("allpac4", "hep8"): 33, ("allpac4", "rgb565lo+a"): 3, ("allpac4", "pal4"): 1,
                         ("parts", "pal8"): 23, ("parts", "pal4"): 1})


@needs_game
def test_black_and_white_fills(arcs) -> None:
    img = timg.load(arcs["allpac0"], "BLACK.MZP")
    assert img.size == (1920, 1080) and img.header.format == timg.PixelFormat.PAL8
    assert np.all(img.rgba == np.array([0, 0, 0, 0xFE], np.uint8))
    w = timg.load(arcs["allpac2"], "WHITE.MZP")
    assert w.size == (150, 84) and np.all(w.rgba == np.array([255, 255, 255, 0xFE], np.uint8))
    # allpac0 BLUE.MZP: stored white, every tile flagged empty -> the engine draws nothing
    b = timg.load(arcs["allpac0"], "BLUE.MZP")
    assert b.skipped_tiles == list(range(20)) and int(b.rgba[..., 3].max()) == 0


@needs_game
def test_hep_sprite_composite(arcs) -> None:
    a = arcs["allpac1_2"]
    face = timg.load(a, "ARU_L_81_00_N00.MZP")
    base = timg.load(a, "ARU_L_81_00_N00_B.MZP")
    h = face.header
    assert (h.format, h.border, h.width, h.height, face.size) == (timg.PixelFormat.HEP8, 1, 640, 768, (630, 762))
    assert base.size == (756, 1524)
    assert face.skipped_tiles == [0, 1, 2, 3, 4, 5, 10, 11]
    comp = timg.compose(base.rgba, face.rgba)
    # every face pixel lands where the base has a hole or identical content: the composite is opaque
    # inside the face's eyes (red iris around x~300, y~440)
    eye = comp[430:450, 295:305]
    assert eye[..., 3].min() >= 0xFE and eye[..., 0].mean() > 120 and eye[..., 1].mean() < 40
    assert _sha(face.rgba) == "a01a7f89507ae1c5cc8dd98d0e06a5a0687760fb"


@needs_game
def test_rgb565_image(arcs) -> None:
    img = timg.load(arcs["allpac1"], "IMG5004.MZP")      # white "END" caption
    assert img.header.format == timg.PixelFormat.RGB565_LO_A and img.size == (240, 87)
    vis = img.rgba[img.rgba[..., 3] > 200]
    assert len(vis) > 1000 and vis[:, :3].min() >= 200
    assert _sha(img.rgba) == "36b342857f8d2178916f0942ede2700b6eb911f9"


@needs_game
def test_tile_flags_match_alpha(arcs) -> None:
    img = timg.load(arcs["allpac4"], "IMG2637.MZP", decode_empty=True)
    h = img.header
    for t in range(h.ntiles):
        x0, y0 = h.tile_origin(t)
        reg = img.rgba[y0:y0 + h.inner_h, x0:x0 + h.inner_w, 3]
        if h.tile_flags[t] == timg.TileFlag.OPAQUE:
            assert reg.min() == 0xFE
        elif h.tile_flags[t] == timg.TileFlag.EMPTY:
            assert reg.max() <= 8


@needs_game
def test_thumbnail_matches_full_image(arcs) -> None:
    """allpac2 holds 1/12.8-scale PAL8 copies; the CSM1-unswizzled palette must match the
    downscaled HEP original much better than the stored order."""
    from PIL import Image
    small = timg.load(arcs["allpac2"], "IMG2464.MZP")
    big = timg.load(arcs["allpac1"], "IMG2464.MZP").rgba
    w, h = small.size
    ref = np.array(Image.fromarray(big[:round(h * 12.8), :round(w * 12.8)]).resize((w, h), Image.BOX)).astype(int)
    hdr = small.header
    raw = np.frombuffer(hdr.palette, np.uint8).reshape(256, 4)
    idx = np.frombuffer(mzx.decompress(MzpArchive(arcs["allpac2"].read(arcs["allpac2"].find("IMG2464.MZP").index)).read(1)),
                        np.uint8).reshape(hdr.tile_h, hdr.tile_w)[:h, :w]
    plain = raw[idx].astype(int)                       # stored order, same alpha rule
    plain[..., 3] = np.where(raw[idx][..., 3] == 0x80, 255, (raw[idx][..., 3].astype(int) * 2) & 0xFF)

    def premul(a: np.ndarray) -> np.ndarray:
        a = a.astype(float)
        return np.concatenate([a[..., :3] * a[..., 3:4] / 255.0, a[..., 3:4]], -1)

    err_engine = np.abs(premul(small.rgba) - premul(ref)).mean()
    err_plain = np.abs(premul(plain) - premul(ref)).mean()
    assert err_engine < 3.0 and err_plain > 3 * err_engine


@needs_game
def test_font_page_alpha_ramp() -> None:
    with open(IMG / "ft_en.mrg", "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm).child(64)
            hdr = timg.parse_header(arc.read(0))
            pal = timg.palette_type1_rgba(hdr.palette, 256)
            assert np.all(np.diff(pal[:, 3].astype(int)) >= 0)            # monotonic only after unswizzle
            assert pal[0, 3] == 0 and pal[255, 3] == 0xFE and np.all(pal[:, :3] == 255)
            img = timg.decode_image(arc)
            assert img.size == (1024, 1024)
            del arc
        finally:
            mm.close()


@needs_game
def test_image_catalog() -> None:
    import re
    from collections import Counter

    from tsuki.image_catalog import find_catalog, load_catalog, load_picture_names
    from tsuki.nam import load_nam
    gd = game_dir()
    assert gd is not None
    data = (gd / "eboot.bin").read_bytes()
    assert find_catalog(data) == (0x3D78A0, 14744, 31)      # engine base data VA 0x671900, bound id <= 0x39B6
    cat = load_catalog(data)
    names = load_nam(IMG / "allpac2.nam").names
    assert len(cat) == 14744 == len(names) - 1
    assert all(e.thumb_name == names[e.index].upper() for e in cat)
    e = next(e for e in cat if e.name1 and e.name1.upper() == "AKI_A_00_00_N01.MZP")
    assert e.name0.upper() == "AKI_A_00_00_N00_B.MZP" and (e.width, e.height) == (1828, 6357)
    assert cat[0].name0 == "BLACK.mzp" and (cat[0].width, cat[0].height, cat[0].w3, cat[0].h3) == (1920, 1080, 640, 362)
    assert cat[0].file_offset == 0x3D78A0 and cat[0].flags_a == 0x30B
    # pic_flags (+0x3F): bit 0 <=> name ends in _HH_VV, bit 1 = language bands
    assert Counter(e.pic_flags for e in cat) == Counter({0: 7316, 1: 7262, 2: 140, 3: 26})
    variant = re.compile(r"_\d\d_\d\d\.MZP$")
    assert all(e.is_variant == bool(variant.search(e.thumb_name)) for e in cat)
    assert all((e.layer_x, e.layer_y) == (0, 0) and e.kind == 0x0B for e in cat)
    # scale_class (+0x42) == sprite size letter A/L/M/S; char_id (+0x40): one id per character
    sprites = [(e, timg.split_sprite_name(e.thumb_name)) for e in cat]
    sprites = [(e, sn) for e, sn in sprites if sn is not None]
    assert len(sprites) == 8553 and all(e.scale_class == "ALMS".index(sn.size) for e, sn in sprites)
    ids: dict[int, set[str]] = {}
    for e, sn in sprites:
        assert (e.char_id == 0) == e.is_variant
        ids.setdefault(e.char_id, set()).add(sn.char)
    assert all(len(v) == 1 for k, v in ids.items() if k) and ids[2] == {"ARU"} and ids[3] == {"SIE"}
    assert load_picture_names(data) == [e.thumb_name[:-4] for e in cat]


@needs_game
def test_catalog_resolver_render(arcs) -> None:
    from tsuki.image_catalog import CatalogResolver, load_catalog
    gd = game_dir()
    assert gd is not None and IMG is not None
    cat = load_catalog(gd / "eboot.bin")
    e = next(e for e in cat if e.name1 and e.name1.upper() == "ARU_L_81_00_N00.MZP")
    with CatalogResolver(IMG) as r:
        assert r.locate("aru_l_81_00_n00_b.mzp") == ("allpac1_2", 1)
        rgba = r.render(e, crop_to_logical=True)
        assert rgba.shape == (1279, 717, 4)
        a = arcs["allpac1_2"]
        manual = timg.compose(timg.load(a, "ARU_L_81_00_N00_B.MZP").rgba, timg.load(a, "ARU_L_81_00_N00.MZP").rgba)
        assert np.array_equal(rgba, manual[:1279, :717])
        missing = [x for x in cat if any(n not in r.where for n in x.layers)]
        assert [x.index for x in missing] == [11574, 11667, 12536]


@needs_game
def test_language_band_render() -> None:
    """pic_flags bit 1: four language versions stacked at a stride of ceil(height/254)*254
    (engine VA 0xC2620); the allpac2 thumbnail stacks them at int(height/12.8 + 0.5)."""
    from PIL import Image

    from tsuki.image_catalog import LANGS, CatalogResolver, load_catalog
    gd = game_dir()
    assert gd is not None and IMG is not None
    cat = load_catalog(gd / "eboot.bin")
    e = next(x for x in cat if x.thumb_name == "IMG2587.MZP")   # "Fin" caption, 310x105 per band
    assert e.has_language_bands and e.band_stride == 254 and e.source_rect("en") == (0, 254, 310, 359)
    with CatalogResolver(IMG) as r:
        full = r.render(e)
        assert full.shape[:2] == (4 * 254, 510)
        vis = full[..., 3] > 8
        for L in range(4):                       # content inside [L*254, L*254 + 105) only
            assert vis[L * 254:L * 254 + e.height].any() and not vis[L * 254 + e.height:(L + 1) * 254].any()
        assert not vis[:, e.width:].any()
        bands = [r.render(e, crop_to_logical=True, lang=x) for x in LANGS]
        assert all(b.shape == (105, 310, 4) for b in bands) and not np.array_equal(bands[0], bands[1])
        assert np.array_equal(bands[1], full[254:359, :310])
        thumb = r.thumbnail(e)
        tb = [r.thumbnail(e, x).astype(float) for x in LANGS]
        assert thumb.shape[0] == 4 * tb[0].shape[0] == 4 * int(105 * 0.078125 + 0.5)

        def small(a: np.ndarray) -> np.ndarray:
            return np.array(Image.fromarray(a, "RGBA").resize((tb[0].shape[1], tb[0].shape[0]), Image.BOX)).astype(float)

        err = np.array([[np.abs(tb[i] - small(bands[j])).mean() for j in range(4)] for i in range(4)])
        assert all(err[i, i] < np.delete(err[i], i).min() for i in range(4))
