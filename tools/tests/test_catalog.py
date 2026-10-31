"""Tests for tsuki.catalog / tsuki.catalog_eboot (archive roles, UI tables, movies).

Synthetic tests always run; real-data tests skip when the game folder is missing
(set TSUKI_GAME_DIR to override).  Run:
    cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_catalog.py -q
"""
from __future__ import annotations

import json
import shutil
import struct
import subprocess
import sys
from collections import Counter
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import game_dir, image_dir  # noqa: E402
from tsuki.catalog import (  # noqa: E402
    CHAR_CODES, IMAGE_SEARCH_ORDER, LANGS, MOVIE_NAMES, Category, ImageNameIndex, ScriptImageResolver,
    classify_name, find_image, iter_catalog, lang_index_from_system, localize_parts_name,
    movie_file_for_language, movie_index_for_language, nam_key, pack_image_id, script_image_key,
    stch_image_name, ui_child_index, unpack_image_id,
)
from tsuki.catalog_eboot import IMAGE_TABLE_SLOTS, UI_TABLES_UNREFERENCED, Eboot  # noqa: E402
from tsuki.mrg import MrgArchive  # noqa: E402
from tsuki.mzp import MzpArchive  # noqa: E402
from tsuki.nam import load_nam, parse_nam32  # noqa: E402

IMG = image_dir()
GAME = game_dir()
needs_game = pytest.mark.skipif(IMG is None or GAME is None, reason="game data not found")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("name,cat,extra", [
    ("BLACK.MZP", Category.SOLID_COLOR, {}),
    ("IMG0042.MZP", Category.SCENE, {"base": "IMG0042"}),
    ("IMG0042_03_03.MZP", Category.SCENE_LAYER, {"base": "IMG0042", "layer": "03_03"}),
    ("IMG3864.MZP", Category.GALLERY_IMAGE, {}),
    ("img2511", Category.SCENE, {}),
    ("AKI_A_00_00_N01.MZP", Category.SPRITE, {"char_code": "AKI", "char_id": 8, "size_class": "A"}),
    ("AKI_A_00_00_N00_B.MZP", Category.SPRITE_BODY, {"base": "AKI_A_00_00_N00"}),
    ("SIE_A_09_01_B00_B.MZP", Category.SPRITE_BODY, {"char_id": 3}),
    ("ARU_A_18_05_C01_32_32.MZP", Category.SPRITE_LAYER, {"layer": "32_32"}),
    ("Z20_S_63_04_N00.MZP", Category.SPRITE, {"char_id": 29, "size_class": "S"}),
    ("RULE_07.MZP", Category.TRANSITION_RULE, {}),
    ("CAUTION1_ZT.MZP", Category.CAUTION_SCREEN, {"lang": "zt"}),
    ("CAUTION2.MZP", Category.CAUTION_SCREEN, {"lang": "ja"}),
    ("CONF_PARTS_EN.MRG", Category.UI_PARTS, {"lang": "en", "base": "CONF_PARTS"}),
    ("GALLERY_PARTS_COMMON_KO.MRG", Category.UI_PARTS, {"lang": "ko", "base": "GALLERY_PARTS_COMMON"}),
    ("GALLERY_PARTS_FC_TH.MRG", Category.UI_THUMBNAILS, {"base": "GALLERY_PARTS_FC_TH"}),
    ("BORDER_PARTS.MRG", Category.UI_PARTS, {"lang": ""}),
    ("DUMMY.MRG", Category.PLACEHOLDER, {}),
    ("CGMODE_TOP.MRG", Category.PLACEHOLDER, {}),
    ("SELMAP_TOP.MZP", Category.PLACEHOLDER, {}),
    ("whatever.bin", Category.UNKNOWN, {}),
])
def test_classify_name(name: str, cat: Category, extra: dict) -> None:
    info = classify_name(name)
    assert info.category == cat
    for k, v in extra.items():
        assert getattr(info, k) == v, (k, getattr(info, k))


def test_localize_parts_name() -> None:
    # port of VA 0xC2AF0
    assert localize_parts_name("conf_parts.mrg", 1) == "conf_parts_en.mrg"
    assert localize_parts_name("TITLE_PARTS.MRG", 0) == "TITLE_PARTS_ja.mrg"
    assert localize_parts_name("a.mrg", 3) == "a_zt.mrg"           # len 5 -> stripped
    assert localize_parts_name(".mrg", 2) == ".mrg_zc.mrg"         # len 4 -> kept
    assert localize_parts_name("x.mzp", 4) == "x.mzp_ko.mrg"       # other extensions kept
    # lang outside 0..4: the engine substitutes the system language (VA 0xC2C56 -> 0x407F0)
    assert localize_parts_name("conf_parts.mrg", 7) == "conf_parts_en.mrg"
    assert localize_parts_name("conf_parts.mrg", -1, system_language=10) == "conf_parts_zt.mrg"
    assert localize_parts_name("conf_parts.mrg", 9, system_language=0) == "conf_parts_ja.mrg"


def test_ui_child_index() -> None:
    # VA 0xEB350 clamps the child to the last one (signed compare)
    assert ui_child_index(89, 83) == 82
    assert ui_child_index(82, 83) == 82
    assert ui_child_index(0, 83) == 0
    assert ui_child_index(5, 5) == 4


def test_nam_key() -> None:
    assert nam_key("dir\\sub\\img0001.mzp") == b"IMG0001.MZP"
    assert nam_key("a" * 40) == b"A" * 28
    assert nam_key("Caution_En.mzp") == b"CAUTION_EN.MZP"
    # a byte after an output byte >= 0x80 is not upper-cased (Shift-JIS trail byte, VA 0xC32E8)
    assert nam_key(b"\x82a" + b"b.mzp") == b"\x82aB.MZP"
    assert nam_key(b"x\x81bc") == b"X\x81bC"
    # the backslash scan only looks at the first 256 bytes
    assert nam_key(b"q" * 300 + b"\\abc") == b"Q" * 28
    assert nam_key(b"ab\0cd") == b"AB"


def test_script_image_key_and_stch_name() -> None:
    # VA 0x78410: up to '.', at most 32 bytes, upper case
    assert script_image_key("img2511") == "IMG2511"
    assert script_image_key("boy_a_00_00_n01.mzp") == "BOY_A_00_00_N01"
    assert script_image_key("x" * 40) == "X" * 32
    # VA 0x81760: a9/a10 append "_%02d_%02d" when either is non-zero
    assert stch_image_name("img1696", 11, 11) == "img1696_11_11"
    assert stch_image_name("img2277", 0, 20) == "img2277_00_20"
    assert stch_image_name("img0342", 0, 0) == "img0342"
    assert stch_image_name("img0001", 123, 7) == "img0001_23_07"
    assert stch_image_name("img0001", -3, 4) == "img0001_00_04"


def test_lang_index_from_system() -> None:
    assert [lang_index_from_system(x) for x in (0, 1, 10, 11, 9, 2)] == [0, 1, 3, 2, 1, 1]


def test_movie_language_selection() -> None:
    assert len(MOVIE_NAMES) == 134
    # prologue
    assert [movie_file_for_language(4, l) for l in range(4)] == [
        "prologue.mp4", "prologue_en.mp4", "prologue_zc.mp4", "prologue_zt.mp4"]
    # openings: zt falls back to the zc file
    assert [movie_file_for_language(5, l) for l in range(4)] == [
        "op1_arc.mp4", "op1_arc_en.mp4", "op1_arc_zc.mp4", "op1_arc_zc.mp4"]
    assert movie_file_for_language(6, 3) == "op2_ciel_zc.mp4"
    # every phasetitle maps to its own language file
    for mid in range(8, 35):
        base = MOVIE_NAMES[mid]
        assert base.startswith("phasetitle_")
        for l in range(1, 4):
            assert MOVIE_NAMES[movie_index_for_language(mid, l)] == f"{base}_{LANGS[l]}"
    # language-neutral ids are unchanged
    for mid in (0, 1, 2, 3, 7, 35, 36, 37, 38, 39, 45):
        assert all(movie_index_for_language(mid, l) == mid for l in range(5))
    # out-of-range language behaves like English
    assert movie_index_for_language(4, 4) == 46 and movie_index_for_language(8, 4) == 48


def _nam(names: list[str]) -> bytes:
    out = b""
    for n in names:
        out += n.encode().ljust(30, b"\0") + b"\r\n"
    return out + b"\0" * 32


def test_find_image_search_order() -> None:
    nams = {
        "allpac0": parse_nam32(_nam(["BLACK.MZP", "IMG0001.MZP", "DUMMY.MRG"])),
        "allpac1": parse_nam32(_nam(["IMG0002.MZP", "IMG0001.MZP"])),
        "allpac1_2": parse_nam32(_nam(["IMG0002.MZP"])),
        "allpac3": parse_nam32(_nam(["DUMMY.MRG"])),
        "allpac4": parse_nam32(_nam(["IMG9999.MZP"])),
    }
    idx = ImageNameIndex(nams)
    for name, expect in (("img0001.mzp", ("allpac1", 1)), ("IMG0002.MZP", ("allpac1_2", 0)),
                         ("dummy.mrg", ("allpac3", 0)), ("IMG9999.MZP", ("allpac4", 0)),
                         ("black.mzp", ("allpac0", 0)), ("nope.mzp", None)):
        got = find_image(name, nams)
        assert got == idx.find(name)
        assert (unpack_image_id(got) if got is not None else None) == expect
    # while allpac1/allpac1_2 are not open (PlayGo), the search skips them
    got = find_image("IMG0001.MZP", nams, allpac1_available=False)
    assert got is not None and unpack_image_id(got) == ("allpac0", 1)
    assert pack_image_id("allpac2", 7) == 0x30007


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@needs_game
def test_eboot_small_tables() -> None:
    assert GAME is not None
    with Eboot(GAME / "eboot.bin") as eb:
        assert eb.lang_suffixes() == list(LANGS)
        assert eb.prologue_ids() == [4, 46, 47, 106]
        movies = eb.movie_table()
    assert [m.movie_id for m in movies] == list(range(134))
    assert [m.file_name[:-4] for m in movies] == list(MOVIE_NAMES)
    on_disk = {p.name for p in (GAME / "image" / "movie").iterdir()}
    assert on_disk == {m.file_name for m in movies}


@needs_game
def test_eboot_image_table_matches_allpac2() -> None:
    assert GAME is not None and IMG is not None
    with Eboot(GAME / "eboot.bin") as eb:
        tab = eb.image_table()
    assert len(tab) == 14744
    assert tab[0].va == 0x671900 and tab[9958].va == 0x671900 + 9958 * 0x48
    with Eboot(GAME / "eboot.bin") as eb:
        empty = [eb._image_slot(k) for k in range(14744, IMAGE_TABLE_SLOTS)]
        names = eb.image_names()
        old = eb.name_list(0x7A8B10)
    assert IMAGE_TABLE_SLOTS == 14775 and all(e is None for e in empty)    # 31 empty slots
    # script-name list (data VA 0x7F6800): name k == record k without extension, upper case
    assert len(names) == 14745 and names[-1] == ""
    assert all(names[r.index] == r.name.rsplit(".", 1)[0].upper() for r in tab)
    assert len(old) == 9959 and old[:9958] == names[:9958]
    # geometry: a = floor(w/3), b = floor(h*362/1080) for every record
    assert all(r.a == r.w // 3 and r.b == r.h * 362 // 1080 for r in tab)
    assert all(r.b3c == 0x0B for r in tab)
    a2 = load_nam(IMG / "allpac2.nam").names
    assert len(a2) == 14745 and a2[-1] == "DUMMY.MRG"
    assert all(r.name.upper() == a2[r.index].upper() for r in tab)
    paired = [r for r in tab if r.overlay]
    assert len(paired) == 3529
    assert all(r.file.upper().endswith("_B.MZP") for r in paired)
    assert all(classify_name(r.overlay or "").category == Category.SPRITE for r in paired)
    # 1,221 sprites are standalone: no overlay, file == the sprite itself
    sprites = [r for r in tab if classify_name(r.name).category == Category.SPRITE]
    standalone = [r for r in sprites if not r.overlay]
    assert len(sprites) == 4750 and len(standalone) == 1221
    assert all(r.file == r.name for r in standalone)
    assert not any(r.overlay for r in tab if classify_name(r.name).category != Category.SPRITE)
    assert Counter(r.layer_kind for r in tab) == Counter({0: 7316, 1: 7262, 2: 140, 3: 26})
    assert sorted(r.file for r in tab if r.flags == 3) == sorted(
        f"{c}.mzp" for c in ("BLACK", "BLUE", "GREEN", "ORANGE", "RED", "WHITE", "YELLOW"))
    # char_id / size_class agree with the name grammar on every sprite record
    for r in sprites:
        ni = classify_name(r.name)
        assert r.char_id == CHAR_CODES[ni.char_code][0]
        assert r.size_class == "ALMS".index(ni.size_class)
    # every name resolves through the engine search order except 3 dangling bodies
    nams = {a: load_nam(IMG / f"{a}.nam") for a in IMAGE_SEARCH_ORDER}
    idx = ImageNameIndex(nams)
    missing = sorted({n for r in tab for n in (r.file, r.overlay) if n and idx.find(n) is None})
    assert missing == ["ARU_M_03_35_N00_B.MZP", "ARU_S_03_35_N00_B.MZP", "NOE_L_60_03_N00_B.MZP"]


@needs_game
def test_allpac2_is_reduced_copy() -> None:
    """allpac2 entry k = image-table record k at 1/12.8 (kinds 2/3: height x4)."""
    assert GAME is not None and IMG is not None
    with Eboot(GAME / "eboot.bin") as eb:
        tab = eb.image_table()
    exceptions = []
    with MrgArchive(IMG / "allpac2") as a2:
        for r in tab:                           # all 14,744 records
            e = a2.entries[r.index]
            m = MzpArchive(a2.mm, e.offset, e.size)
            sw, sh = struct.unpack("<2H", m.head(0, 4))
            exp_w = int(r.w / 12.8 + 0.5)
            exp_h = int(r.h / 12.8 + 0.5) * (1 if r.layer_kind < 2 else 4)
            if r.name.upper() in ("IMG7000.MZP", "IMG7001.MZP"):
                exp_w, exp_h = 150, 84
            if (sw, sh) != (exp_w, exp_h):
                exceptions.append((r.name, sw, sh, exp_w, exp_h))
    assert exceptions == [("img2583.MZP", 164, 200, 215, 200)]


@needs_game
def test_script_image_resolver() -> None:
    """Script ids resolve like the engine: name list 0x7F6800 -> record -> NAM search."""
    assert GAME is not None and IMG is not None
    res = ScriptImageResolver.from_game(GAME)
    # plain scene: one file
    r = res.resolve("img2511")
    assert (r.index, r.file, r.overlay) == (2518, "img2511.MZP", None)
    assert r.file_id is not None and unpack_image_id(r.file_id) == ("allpac1", 2157)
    assert r.reduced_id == 0x30000 | 2518
    w = res.resolve("WHITE").file_id
    assert w is not None and unpack_image_id(w) == ("allpac0", 5)
    # paired sprite: body + face
    r = res.resolve("boy_a_00_00_n01")
    assert (r.file, r.overlay) == ("BOY_A_00_00_N00_B.MZP", "BOY_A_00_00_N01.MZP")
    assert r.file_id is not None and r.overlay_id is not None
    # standalone sprite: one full image, no body
    r = res.resolve("AKI_A_00_04_N01")
    assert r.overlay is None and r.file == "AKI_A_00_04_N01.MZP"
    assert r.file_id is not None and unpack_image_id(r.file_id) == ("allpac1", 8424)
    # STCH a9/a10 select the variant instead of the base
    r = res.resolve("img1696", 11, 11)
    assert r.key == "IMG1696_11_11" and r.file == "img1696_11_11.MZP"
    assert res.resolve("img0342", 0, 0).key == "IMG0342"
    # missing body of a paired record -> file_id None (engine: -1), face still found
    r = res.resolve("NOE_L_60_03_N01")
    assert r.file == "NOE_L_60_03_N00_B.MZP" and r.file_id is None and r.overlay_id is not None
    assert res.resolve("no_such_image").index == -1
    # the NAM search itself needs the extension
    nams = {a: load_nam(IMG / f"{a}.nam") for a in IMAGE_SEARCH_ORDER}
    assert find_image("img2511", nams) is None and find_image("IMG2511.MZP", nams) == 0x1086D


@needs_game
def test_ui_texture_tables_resolve() -> None:
    assert GAME is not None and IMG is not None
    with Eboot(GAME / "eboot.bin") as eb:
        tables = {t.va: t for t in eb.ui_tables()}
    conf = tables[0x66C9A0].records
    assert [r.tex_slot for r in conf] == list(range(361, 381))
    assert [r.child for r in conf] == list(range(20))
    assert {r.name for r in conf} == {"conf_parts.mrg"}
    nams = {a: load_nam(IMG / f"{a}.nam") for a in IMAGE_SEARCH_ORDER}
    idx = ImageNameIndex(nams)
    neutral = {0x781B70, 0x8730F0}              # loaded via 0xEBCB0 without "_<lang>"
    absent = {0x78B2C0} | set(UI_TABLES_UNREFERENCED)   # extra_parts / ed1_parts: archives not on disk
    for va in absent:
        for r in tables[va].records:
            assert all(idx.find(localize_parts_name(r.name, l)) is None for l in range(5))
    assert {r.child2 for va, t in tables.items() if va not in absent for r in t.records} == {-1}
    direct = clamped = runtime = 0
    with MrgArchive(IMG / "allpac3") as a3:
        counts = {e.index: len(MzpArchive(a3.mm, e.offset, e.size)) for e in a3.entries}
        for va, t in tables.items():
            if va in absent:
                continue
            for r in t.records:
                names = [r.name] if va in neutral else [localize_parts_name(r.name, l) for l in range(4)]
                for n in names:
                    pid = idx.find(n)
                    assert pid is not None, n
                    arc, i = unpack_image_id(pid)
                    assert arc == "allpac3"
                    if 0 <= r.child < counts[i]:
                        direct += 1
                    elif va == 0x8731A0 and r.child == 89:
                        # selecttxt record 0: child 89 of 83 -> VA 0xEB350 loads the last child
                        assert counts[i] == 83 and ui_child_index(r.child, counts[i]) == 82
                        clamped += 1
                    else:
                        # selecttxt record 1: child -1 in the file, rewritten by VA 0xA5FB2..0xA5FC1
                        assert (va, r.child, r.tex_slot) == (0x8731A0, -1, 98), (n, r.child)
                        runtime += 1
    assert (direct, clamped, runtime) == (699, 4, 4)


@needs_game
def test_catalog_small_archives() -> None:
    assert IMG is not None
    cats = Counter((e.archive, e.child >= 0, e.category) for e in iter_catalog(IMG, ["allpac3", "allpac4", "parts"]))
    assert cats[("allpac3", False, "transition_rule")] == 49
    assert cats[("allpac3", False, "caution_screen")] == 12
    assert cats[("allpac3", False, "ui_parts")] == 69
    assert cats[("allpac3", False, "ui_thumbnails")] == 2
    assert cats[("allpac3", False, "placeholder")] == 4
    assert cats[("allpac3", True, "ui_part")] + cats[("allpac3", True, "ui_thumbnail")] == 3699
    assert cats[("parts", False, "system_screen")] == 23
    assert cats[("allpac4", False, "scene")] == 32
    assert not any(c == "unknown" for (_, _, c) in cats)


@needs_game
def test_all_allpac_names_classified() -> None:
    assert IMG is not None
    c: Counter[str] = Counter()
    for a in ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4"):
        for n in load_nam(IMG / f"{a}.nam").names:
            c[classify_name(n, a).category.value] += 1
    assert c["unknown"] == 0
    assert c["gallery_image"] == 1024
    assert c["sprite_body"] == 998
    assert c["solid_color"] == 14


@needs_game
def test_loose_pngs() -> None:
    assert GAME is not None
    for rel, wh in (("data/if_ja.png", (228, 128)), ("data/if_en.png", (228, 128)),
                    ("data/if_zc.png", (228, 128)), ("data/if_zt.png", (228, 128)),
                    ("image/corp.png", (131, 48))):
        b = (GAME / rel).read_bytes()
        assert b[:8] == b"\x89PNG\r\n\x1a\n"
        assert struct.unpack(">II", b[16:24]) == wh
    assert (GAME / "data/if_zc.png").read_bytes() == (GAME / "data/if_zt.png").read_bytes()
    with Eboot(GAME / "eboot.bin") as eb:
        assert eb.cstr(0x2D66B8) == "/app0/image/corp.png"
        assert eb.cstr(0x1A682A) == "/app0/data/if_"


@needs_game
@pytest.mark.skipif(shutil.which("ffprobe") is None, reason="ffprobe not on PATH")
def test_ffprobe_one_movie() -> None:
    assert GAME is not None
    p = GAME / "image" / "movie" / "TM_CI.mp4"
    r = subprocess.run(["ffprobe", "-v", "error", "-print_format", "json", "-show_streams", str(p)],
                       capture_output=True, text=True, check=True)
    st = json.loads(r.stdout)["streams"]
    v = next(s for s in st if s["codec_type"] == "video")
    a = next(s for s in st if s["codec_type"] == "audio")
    assert (v["codec_name"], v["width"], v["height"], v["pix_fmt"]) == ("h264", 1920, 1080, "yuv420p")
    assert (a["codec_name"], a["channels"], a["sample_rate"]) == ("aac", 2, "48000")
