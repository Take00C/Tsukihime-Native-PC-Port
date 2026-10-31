"""What every archive, entry, movie and loose file of the PS4 release is for.

This module answers "what is this asset and who uses it" for TSUKIHIME -A piece
of blue glass moon- (PS4 CUSA47999 v01.01).  It does not decode pixels or audio
(see the image / audio modules); it classifies by name grammar, container
content (tsuki.sniff / tsuki.mzp) and the engine tables in eboot.bin
(tsuki.catalog_eboot).  Every rule here is documented, with the evidence, in
docs/formats/archives_ui_movies.md.

Main entry points
-----------------
* ``ARCHIVES``                    role of every archive file
* ``classify_name(name, archive)`` -> NameInfo  (pure name grammar)
* ``iter_catalog(image_dir)``      -> EntryInfo for every triplet entry, every
                                     allpac3 UI child and every standalone section
* ``nam_key`` / ``find_image``     the engine's NAM file-name search (VA 0xC3270);
                                     keys keep the extension ("IMG2511.MZP")
* ``script_image_key`` / ``stch_image_name`` / ``ScriptImageResolver``
                                     how a script image id ("img2511", "WHITE",
                                     "boy_a_00_00_n01") becomes an image-table record
                                     and then archive entries (VA 0x81760, 0x78410,
                                     name list 0x7F6800, 0x4A060/0x4A110)
* ``localize_parts_name``          the engine's "_<lang>" insertion (VA 0xC2AF0)
* ``ui_child_index``               the child clamp of the texture loader (VA 0xEB350)
* ``movie_index_for_language``     the engine's movie selection (VA 0x5E280)
* ``lang_index_from_system``       PS4 system language -> asset language

CLI::

    python -m tsuki.catalog summary            # per-archive category counts
    python -m tsuki.catalog name SIE_A_00_00_N01.MZP
    python -m tsuki.catalog movies [--lang en]
    python -m tsuki.catalog image img2511 boy_a_00_00_n01 [--aa 3 --bb 3]   # engine image lookup
    python -m tsuki.catalog export DIR [--no-ffprobe]   # CSV/JSON tables
"""
from __future__ import annotations

import csv
import hashlib
import json
import mmap
import re
import struct
import subprocess
from collections import Counter
from dataclasses import asdict, dataclass, field
from enum import Enum
from pathlib import Path
from typing import Any, Iterable, Iterator, Optional, Sequence, Union

from .mrg import MrgArchive
from .mzp import MzpArchive, MzpError
from .nam import NamTable
from .sniff import classify_mzp, image_header, sniff

__all__ = [
    "Category",
    "LANGS",
    "ARCHIVES",
    "ArchiveInfo",
    "ARCHIVE_IDS",
    "IMAGE_SEARCH_ORDER",
    "CHAR_CODES",
    "SIZE_CLASSES",
    "PARTS_MRG",
    "UI_PARTS_SETS",
    "MOVIE_NAMES",
    "NameInfo",
    "EntryInfo",
    "classify_name",
    "iter_catalog",
    "nam_key",
    "find_image",
    "ImageNameIndex",
    "pack_image_id",
    "unpack_image_id",
    "script_image_key",
    "stch_image_name",
    "ResolvedImage",
    "ScriptImageResolver",
    "localize_parts_name",
    "ui_child_index",
    "lang_index_from_system",
    "movie_index_for_language",
    "movie_file_for_language",
    "movie_info",
]


class Category(str, Enum):
    SOLID_COLOR = "solid_color"            # BLACK/BLUE/... 1920x1080 fills
    SCENE = "scene"                        # IMGnnnn: background or event CG (one namespace)
    SCENE_LAYER = "scene_layer"            # IMGnnnn_aa_bb: variant of IMGnnnn picked by STCH a9,a10
    GALLERY_IMAGE = "gallery_image"        # IMG3000..IMG4193: gallery / flowchart only, never scripted
    SPRITE = "sprite"                      # CCC_S_XX_YY_Pnn: character sprite (face of a body, or standalone)
    SPRITE_BODY = "sprite_body"            # ..._B: body that a paired sprite's face is drawn over
    SPRITE_LAYER = "sprite_layer"          # ..._aa_bb: sprite variant picked by STCH a9,a10
    UI_PARTS = "ui_parts"                  # allpac3 *_PARTS*.MRG: UI texture sheets (nested mrgd00)
    UI_PART = "ui_part"                    # one child image of a UI_PARTS archive
    UI_THUMBNAILS = "ui_thumbnails"        # GALLERY_PARTS_{CG,FC}_TH.MRG
    TRANSITION_RULE = "transition_rule"    # RULE_nn: 8-bit grey wipe masks
    CAUTION_SCREEN = "caution_screen"      # CAUTION*: boot notices
    PLACEHOLDER = "placeholder"            # DUMMY.MRG, CGMODE_TOP.MRG, *_TOP.MZP, allscr s2
    SYSTEM_SCREEN = "system_screen"        # parts.mrg entries (logo, save warnings, autosave)
    FONT_PAGE = "font_page"                # ft_*.mrg sections
    SCRIPT = "script"                      # allscr.mrg inverted-MZX bytecode
    SCRIPT_NAMES = "script_names"          # allscr.mrg s0 (NAM32)
    SCRIPT_LABELS = "script_labels"        # allscr.mrg s1 (nested label tables)
    TEXT_OFFSETS = "text_offsets"          # script_text.mrg even sections
    TEXT_STRINGS = "text_strings"          # script_text.mrg odd sections
    NAME_TABLE = "name_table"              # se_nam.mrg sections
    BGM = "bgm"
    SOUND_EFFECT = "sound_effect"          # vse.mrg
    SYSTEM_SOUND = "system_sound"          # vag.mrg (moon_se*_L/_R)
    VOICE = "voice"
    MOVIE = "movie"
    SAVE_ICON = "save_icon"                # data/if_<lang>.png
    SCREENSHOT_OVERLAY = "screenshot_overlay"  # image/corp.png
    UNKNOWN = "unknown"


# Asset language suffixes, index = engine language id (eboot relro VA 0x42C030).
LANGS: tuple[str, ...] = ("ja", "en", "zc", "zt", "ko")


@dataclass(frozen=True)
class ArchiveInfo:
    name: str
    files: tuple[str, ...]
    container: str
    role: str
    engine: str
    playgo_chunk: str
    languages: str


ARCHIVES: dict[str, ArchiveInfo] = {a.name: a for a in (
    ArchiveInfo("allpac0", ("allpac0.hed", "allpac0.mrg", "allpac0.nam"), "HED8+NAM32+MRG, image MZPs",
                "full-size scene images (IMG), sprites and sprite bodies for the early part of the story, "
                "the 7 solid colours, ANIME_TOP placeholder",
                "archive id 0, HED slot 0x0A, NAM slot 4; searched last", "0 (initial)", "none"),
    ArchiveInfo("allpac1", ("allpac1.hed", "allpac1.mrg", "allpac1.nam"), "HED8+NAM32+MRG, image MZPs",
                "full-size scene images, scene layers, sprites (bulk of the game)",
                "archive id 1, HED slot 0x0B, NAM slot 5; opened only when PlayGo chunks 1 and 2 are present",
                "1 or 2 (alldata/alldata2) [H which]", "none"),
    ArchiveInfo("allpac1_2", ("allpac1_2.hed", "allpac1_2.mrg", "allpac1_2.nam"), "HED8+NAM32+MRG, image MZPs",
                "continuation of allpac1 (allpac1 is already 11 bank switches = format limit): sprite bodies (_B) "
                "and expression sprites, plus IMG3000-IMG4193 gallery-only images",
                "archive id 2, HED slot 0x0C, NAM slot 6; same PlayGo gate as allpac1; searched before allpac1",
                "1 or 2 [H which]", "none"),
    ArchiveInfo("allpac2", ("allpac2.hed", "allpac2.mrg", "allpac2.nam"), "HED8+NAM32+MRG, image MZPs",
                "reduced copies (scale 1/12.8) of every scene image / layer and of every sprite (paired "
                "sprites composited with their body); entry k == eboot image-table record k",
                "archive id 3, HED slot 0x0D, NAM slot 7; NOT in the name search; loaded as id 0x30000|k by "
                "VA 0xAB320 for the layer lists rebuilt by VA 0xAA2D0 (which always sets the flag at data "
                "VA 0x1762164 first)", "0", "none"),
    ArchiveInfo("allpac3", ("allpac3.hed", "allpac3.mrg", "allpac3.nam"), "HED8+NAM32+MRG, 71 nested UI archives + 65 images",
                "UI texture sheets per screen and language, gallery thumbnails, transition rules, caution screens, "
                "placeholders", "archive id 4, HED slot 0x0E, NAM slot 8; searched second", "0",
                "*_JA/_EN/_ZC/_ZT/_KO suffix on UI sets; CAUTION*_xx"),
    ArchiveInfo("allpac4", ("allpac4.hed", "allpac4.mrg", "allpac4.nam"), "HED8+NAM32+MRG, image MZPs",
                "late additions / overrides: IMG6000-IMG6026, IMG2637/2638(+layers), IMG5028-5030, IMG0570_09_09, "
                "IMG2607_01_01", "archive id 5, HED slot 0x0F, NAM slot 9; searched FIRST", "0", "none"),
    ArchiveInfo("parts", ("parts.hed", "parts.mrg"), "HED8, no NAM, 24 image MZPs",
                "system screens shown outside the scenario: TYPE-MOON logo, save-data warnings, autosave "
                "notice and icon, 'Saving...' captions, content disclaimer (5 languages each)",
                "slot 0x18, addressed by index", "0", "groups of 5 in LANGS order (ko = copy of en)"),
    ArchiveInfo("allscr", ("allscr.mrg",), "standalone mrgd00", "scenario scripts (s0 names, s1 labels, "
                "s2 placeholder image, s3.. inverted-MZX bytecode)", "loaded by name 'allscr.mrg'", "0", "none"),
    ArchiveInfo("script_text", ("script_text.mrg",), "standalone mrgd00", "dialogue strings, 5 language pairs",
                "loaded by name", "0", "pairs 0..4 = ja,en,zc,zt,ko slot [H for pair 4]"),
    ArchiveInfo("ft", ("ft_ja.mrg", "ft_en.mrg", "ft_zc.mrg", "ft_zt.mrg", "ft_ko.mrg"), "standalone mrgd00",
                "font glyph atlases (65 pages each)", "chosen by language suffix", "0", "one file per language"),
    ArchiveInfo("se_nam", ("se_nam.mrg",), "standalone mrgd00 of NAM32", "sound-effect name tables "
                "(section 2 names vse entries)", "slot 0x17", "0", "none"),
    ArchiveInfo("bgm", ("bgm.hed", "bgm.mrg"), "HED8 (no NAM), RIFF/ATRAC9", "background music", "slot 0x1A", "0", "none"),
    ArchiveInfo("vse", ("vse.hed", "vse.mrg"), "HED8, RIFF/ATRAC9", "scenario sound effects", "slots 0x1B/0x1C", "0", "none"),
    ArchiveInfo("vag", ("vag.hed", "vag.mrg"), "HED8, VAGp", "system sounds (moon_se*, L/R mono pairs)",
                "slots 0x1D/0x1E, all 50 voices preloaded at boot", "0", "none"),
    ArchiveInfo("voice", ("voice.hed", "voice.mrg", "voice.nam"), "HED4+NAM8, LV+MZX -> RIFF/ATRAC9",
                "character voices", "slot 0x13", "0", "Japanese audio only"),
    ArchiveInfo("voice2", ("voice2.hed", "voice2.mrg", "voice2.nam"), "HED4+NAM8", "2 dummy voice entries",
                "slot 0x15", "0", "none"),
    ArchiveInfo("movie", tuple(), "MP4 (H.264 + AAC) files in image/movie", "FMVs; see MOVIE_NAMES",
                "eboot movie table data VA 0xDC8C70; language remap VA 0x5E280", "0", "_en/_zc/_zt variants"),
    ArchiveInfo("data_if", ("data/if_ja.png", "data/if_en.png", "data/if_zc.png", "data/if_zt.png"), "PNG 228x128",
                "save-data icon (sce_sys/icon0.png of the save)", "path built at VA 0x41AD0, handed to the "
                "save-data object, written by sceSaveDataSaveIcon (VA 0x1214A5)", "0", "per language"),
    ArchiveInfo("corp", ("image/corp.png",), "PNG 131x48 indexed + tRNS", "TYPE-MOON watermark for "
                "system screenshots", "sceScreenShotSetOverlayImageWithOrigin(path, 0, 0, 3) at VA 0xEE587", "0", "none"),
)}

# Packed image id = archive_id << 16 | NAM index (engine: VA 0xC3270 / 0xC3520).
ARCHIVE_IDS: dict[int, str] = {0: "allpac0", 1: "allpac1", 2: "allpac1_2", 3: "allpac2", 4: "allpac3", 5: "allpac4"}
ARCHIVE_ID_BY_NAME: dict[str, int] = {v: k for k, v in ARCHIVE_IDS.items()}
IMAGE_SEARCH_ORDER: tuple[str, ...] = ("allpac4", "allpac3", "allpac1_2", "allpac1", "allpac0")
NAM_KEY_LEN = 28          # compared characters (0x1C)
NAM_SCAN_LIMIT = 0x5014   # records scanned per table

# 3-letter character codes -> (eboot char_id at image-table +0x38, name).  char_id is
# [E]; the names are inferred from the code letters and the CONF_PARTS voice list [H].
CHAR_CODES: dict[str, tuple[int, str]] = {
    "SIK": (1, "Shiki Tohno"), "ARU": (2, "Arcueid Brunestud"), "SIE": (3, "Ciel"),
    "NOE": (4, "Noel"), "ROA": (5, "Roa"), "HIS": (6, "Hisui"), "KOH": (7, "Kohaku"),
    "AKI": (8, "Akiha Tohno"), "ARI": (9, "Arihiko Inui"), "MAR": (10, "Mario"),
    "VRV": (11, "Vlov"), "AOK": (12, "Aoko Aozaki"), "BOY": (13, "boy (minor)"),
    "ARA": (14, "Professor Arach"), "AND": (15, "Ando"), "KAR": (16, "Karius"),
    "GOT": (17, "Gouto Saiki"), "SAT": (18, "Satsuki Yumizuka"), "MIO": (21, "Mio"),
    "KUR": (23, "unknown (KUR)"), "KRO": (24, "unknown (KRO)"), "Z01": (25, "extra Z01"),
    "Z02": (26, "extra Z02"), "Z11": (27, "extra Z11"), "Z12": (28, "extra Z12"),
    "Z20": (29, "extra Z20"), "Y00": (30, "extra Y00"),
}
# Sprite size/framing letter -> eboot size_class (image-table +0x3A) [E]; meaning of the letters [H].
SIZE_CLASSES: dict[str, int] = {"A": 0, "L": 1, "M": 2, "S": 3}

# parts.mrg (no NAM): index -> description (viewed; language groups follow LANGS, ko == en copy).
PARTS_MRG: tuple[tuple[int, int, str], ...] = (
    (0, 1, "16x16 transparent dummy"),
    (1, 1, "TYPE-MOON logo splash, 1920x1080, white background"),
    (2, 5, "save data corrupted / cannot be loaded message box (ja,en,zc,zt,ko)"),
    (7, 1, "autosave spinner icon 160x160"),
    (8, 5, "autosave notice: 'This game features an autosave function...' (ja,en,zc,zt,ko)"),
    (13, 5, "'Saving...' caption 128x56 (ja,en,zc,zt,ko)"),
    (18, 1, "glow/flare sprite 160x160"),
    (19, 5, "content disclaimer: 'This is a work of fiction...' (ja,en,zc,zt,ko)"),
)

# allpac3 UI sets: base name -> (eboot base name, purpose).  Children are image MZPs.
UI_PARTS_SETS: dict[str, tuple[str, str]] = {
    "CONF_PARTS": ("conf_parts.mrg", "config screen: tabs, buttons (3 states per sheet), sliders, key help, language selector"),
    "DISP_PARTS": ("disp_parts.mrg", "in-game HUD: text windows, control panel, button glyphs, saving icon, quick menu"),
    "MENU_PARTS": ("menu_parts.mrg", "system menu: Save/Load/Config/FlowChart/Title buttons, backgrounds, route list"),
    "MODEPNL_PARTS": ("modepnl_parts.mrg", "mode panel bar (identical in all languages)"),
    "SAVE_PARTS": ("save_parts.mrg", "save/load screen: route cards, digits, chapter list, AUTO SAVE tag"),
    "TITLE_PARTS": ("title_parts.mrg", "title screen: logo, menu items, PRESS ANY BUTTON, (C)TYPE-MOON, backgrounds"),
    "SELECT_PARTS": ("select_parts.mrg", "choice screen frames/backgrounds (language-neutral)"),
    "SELECTTXT_PARTS": ("selecttxt_parts.mrg", "82 choice texts pre-rendered as 1416x416 images (Japanese; identical in all 5 sets) + digit column (child 82, loaded into slot 97 via the child clamp)"),
    "BORDER_PARTS": ("border_parts.mrg", "border / frame textures at 20..512 px (type 11)"),
    "EFFECT_PARTS": ("effect_parts.mrg", "effect textures (3 dummies + 1920x128 strip)"),
    "NOISE_PARTS": ("noise_parts.mrg", "two 1920x1080 noise textures"),
    "GALLERY_PARTS_COMMON": ("gallery_parts_common.mrg", "gallery shared frame/background"),
    "GALLERY_PARTS_CG": ("gallery_parts_cg.mrg", "CG gallery headers"),
    "GALLERY_PARTS_CG_TH": ("gallery_parts_cg_th.mrg", "CG gallery thumbnails (1979)"),
    "GALLERY_PARTS_MUSIC": ("gallery_parts_music.mrg", "music room"),
    "GALLERY_PARTS_FC": ("gallery_parts_fc.mrg", "flowchart screen"),
    "GALLERY_PARTS_FC_TH": ("gallery_parts_fc_th.mrg", "flowchart scene thumbnails (495)"),
    "GALLERY_PARTS_MOVIE": ("gallery_parts_movie.mrg", "movie gallery"),
    "GALLERY_PARTS_ENDING": ("gallery_parts_ending.mrg", "ending list"),
}

_PHASE = ("01_00", "02_00", "03_00", "04_00", "05_00", "05_01", "06_00", "06_01", "07_00", "07_01",
          "08_00", "08_01", "09_00", "09_01", "10_00", "10_01", "10_02", "11_00", "11_01", "12_00",
          "12_01", "13_00", "13_01", "14_00", "14_01", "15_01a", "15_01b")
# Movie table order (eboot data VA 0xDC8C70); index == movie id.
MOVIE_NAMES: tuple[str, ...] = (
    ("titlebg1", "titlebg2", "titlebg3", "titlebg4", "prologue", "op1_arc", "op2_ciel", "phaseout")
    + tuple(f"phasetitle_{p}" for p in _PHASE)
    + ("ed01_arc", "ed02_ciel", "ed03_ciel", "TM_CI") + tuple(f"credit{i:02d}" for i in range(7))
    + ("prologue_en", "prologue_zc") + tuple(f"phasetitle_{p}_en" for p in _PHASE)
    + tuple(f"phasetitle_{p}_zc" for p in _PHASE)
    + ("op1_arc_en", "op2_ciel_en", "op1_arc_zc", "op2_ciel_zc", "prologue_zt")
    + tuple(f"phasetitle_{p}_zt" for p in _PHASE)
)
assert len(MOVIE_NAMES) == 134
PROLOGUE_IDS: tuple[int, ...] = (4, 46, 47, 106)   # text VA 0x200F70


# ---------------------------------------------------------------------------
# engine rules
# ---------------------------------------------------------------------------

def lang_index_from_system(system_language: int) -> int:
    """PS4 SCE_SYSTEM_SERVICE_PARAM_ID_LANG -> asset language (VA 0x3ED90/0x407F0):
    0 (Japanese) -> 0 ja, 11 (Chinese Simplified) -> 2 zc, 10 (Chinese Traditional)
    -> 3 zt, anything else -> 1 en.  The in-game language option (config word at
    data VA 0x66C59C) overrides this when it is < 4; 4 (ko) is never selected."""
    return {0: 0, 11: 2, 10: 3}.get(system_language, 1)


def movie_index_for_language(movie_id: int, lang: int) -> int:
    """Port of VA 0x5E280 (inlined again at 0x5E3D3).  ``movie_id`` is the
    language-neutral id used by the script command _TOMP(id); returns the
    movie-table index to play."""
    if movie_id == 4:                                   # prologue
        return PROLOGUE_IDS[lang] if 0 <= lang < 4 else 46
    if 8 <= movie_id <= 34:                             # phasetitle_*
        if lang == 0:
            return movie_id
        if lang == 3:
            return movie_id + 99                        # _zt
        if lang == 2:
            return movie_id + 67                        # _zc
        return movie_id + 40                            # _en (also lang 1 and >= 4)
    if movie_id in (5, 6):                              # op1_arc / op2_ciel
        if lang in (2, 3):
            return movie_id + 99                        # _zc (zt has no own file)
        return movie_id if lang == 0 else movie_id + 97  # _en
    return movie_id


def movie_file_for_language(movie_id: int, lang: int) -> str:
    return MOVIE_NAMES[movie_index_for_language(movie_id, lang)] + ".mp4"


def movie_info(index: int) -> dict[str, Any]:
    """Group / language / base id of a movie-table index."""
    name = MOVIE_NAMES[index]
    m = re.fullmatch(r"(.*?)(?:_(en|zc|zt))?", name)
    assert m is not None
    base, lang = m.group(1), m.group(2) or ""
    if base.startswith("titlebg"):
        group = "title background loop"
    elif base == "prologue":
        group = "prologue"
    elif base.startswith("op"):
        group = "opening"
    elif base.startswith("phasetitle"):
        group = "chapter title card"
    elif base == "phaseout":
        group = "chapter end"
    elif base.startswith("ed"):
        group = "ending"
    elif base.startswith("credit"):
        group = "staff credits"
    elif base == "TM_CI":
        group = "TYPE-MOON logo"
    else:
        group = "?"
    base_id = MOVIE_NAMES.index(base)
    return {"index": index, "name": name, "file": name + ".mp4", "base": base, "base_id": base_id,
            "lang": lang or "neutral", "group": group,
            "variants": {LANGS[i]: movie_file_for_language(base_id, i) for i in range(4)}}


def localize_parts_name(name: str, lang: int, system_language: int = 1) -> str:
    """Port of the UI-table loader at VA 0xC2AF0: strip a trailing ".mrg" or
    ".MRG" (only if strlen >= 5), append "_" + suffix + ".mrg".  ``lang`` is the
    asset language (0..4); for any other value the engine uses the system
    language instead (VA 0xC2C56: ``cmp ecx, 5; jb`` else ``call 0x407F0``),
    which is modelled by ``system_language`` (a PS4 language id, default 1 =
    English).  Names are then looked up case-insensitively, e.g.
    "conf_parts.mrg" -> "conf_parts_en.mrg" -> allpac3 "CONF_PARTS_EN.MRG"."""
    if not 0 <= lang < len(LANGS):
        lang = lang_index_from_system(system_language)
    base = name[:-4] if len(name) >= 5 and name[-4:] in (".mrg", ".MRG") else name
    return f"{base}_{LANGS[lang]}.mrg"


def ui_child_index(child: int, n_children: int) -> int:
    """Child actually loaded for a UI registration record (VA 0xEB350:
    ``n = count(archive); c = n - 1; if n > child: c = child`` with a signed
    compare), i.e. an index past the end selects the last child.  This is how
    the selecttxt record (child 89 in an 83-child archive) gets the digit column."""
    return child if n_children > child else n_children - 1


def nam_key(name: Union[str, bytes]) -> bytes:
    """Lookup key exactly as built by VA 0xC3270 (C3280..C32FE):

    * scan at most 256 bytes for NUL; the key starts after the last '\\' seen;
    * copy exactly 28 bytes (the engine reads past a NUL; bytes after the NUL
      never take part in the compare), turning 'a'..'z' into 'A'..'Z' unless the
      previous *output* byte is >= 0x80 (a Shift-JIS lead byte: ``test al,al;
      cmovs``);
    * the key is NUL-terminated at 28.

    Returned without the padding: the bytes before the first NUL (at most 28).
    ``str`` names are encoded as cp932 (identical to ASCII for every retail name).
    """
    raw = name if isinstance(name, bytes) else name.encode("cp932")
    raw = raw.split(b"\0", 1)[0]
    start = 0
    for i, ch in enumerate(raw[:256]):
        if ch == 0x5C:
            start = i + 1
    src = raw[start:start + NAM_KEY_LEN]
    out = bytearray()
    prev = 0
    for ch in src:
        o = ch - 0x20 if (0x61 <= ch <= 0x7A and prev < 0x80) else ch
        out.append(o)
        prev = o
    return bytes(out)


def _nam_match(rec_raw: bytes, key: bytes) -> bool:
    # strncmp(rec, key, 28) == 0 including the NUL (VA 0xC332D..0xC3359)
    rec = rec_raw[:NAM_KEY_LEN].split(b"\0", 1)[0]
    return rec == key


def pack_image_id(archive: str, index: int) -> int:
    return (ARCHIVE_ID_BY_NAME[archive] << 16) | index


def unpack_image_id(packed: int) -> tuple[str, int]:
    return ARCHIVE_IDS[(packed >> 16) & 0xF], packed & 0xFFFF


class ImageNameIndex:
    """Dictionary-backed equivalent of :func:`find_image` (same results, O(1)).
    Keys are file names with extension, like the engine's NAM search."""

    def __init__(self, nams: dict[str, NamTable]) -> None:
        self._maps: dict[str, dict[bytes, int]] = {}
        for arc, t in nams.items():
            m: dict[bytes, int] = {}
            for rec in t.records[:NAM_SCAN_LIMIT]:
                if rec.raw[:1] == b"\0":
                    break
                m.setdefault(rec.raw[:NAM_KEY_LEN].split(b"\0", 1)[0], rec.index)
            self._maps[arc] = m

    def find(self, name: Union[str, bytes], allpac1_available: bool = True) -> Optional[int]:
        key = nam_key(name)
        for arc in IMAGE_SEARCH_ORDER:
            if not allpac1_available and arc in ("allpac1", "allpac1_2"):
                continue
            m = self._maps.get(arc)
            if m is not None and key in m:
                return pack_image_id(arc, m[key])
        return None


def find_image(name: Union[str, bytes], nams: dict[str, NamTable], allpac1_available: bool = True) -> Optional[int]:
    """Engine NAM search (VA 0xC3270): search allpac4, allpac3, then (only if
    allpac1/allpac1_2 are open) allpac1_2, allpac1, then allpac0; return the
    packed id ``archive_id << 16 | index`` of the first match, or None (-1 in the
    engine).  allpac2 is never searched by name.

    The key is a FILE name including its extension ("IMG2511.MZP",
    "conf_parts_en.mrg"); a script image id such as "img2511" does not match
    any NAM record.  Script ids go through :class:`ScriptImageResolver`."""
    key = nam_key(name)
    order = [a for a in IMAGE_SEARCH_ORDER if allpac1_available or a not in ("allpac1", "allpac1_2")]
    for arc in order:
        t = nams.get(arc)
        if t is None:
            continue
        for rec in t.records[:NAM_SCAN_LIMIT]:
            if rec.raw[:1] == b"\0":            # 'cmp byte [rcx], 0; je' -> end of this table
                break
            if _nam_match(rec.raw, key):
                return pack_image_id(arc, rec.index)
    return None


# ---------------------------------------------------------------------------
# script image ids (STCH/SCH2/SPFC/SPRD/STCG) -> image-table record -> entries
# ---------------------------------------------------------------------------

SCRIPT_KEY_LEN = 32       # VA 0x78410 copies at most 32 bytes


def script_image_key(name: Union[str, bytes]) -> str:
    """Key built by NameTable_Lookup (VA 0x78410) with its upper-case flag set
    (every image call site passes ecx=1): copy at most 32 bytes, stop at NUL or
    '.', pass bytes >= 0x80 through unchanged, map the rest through the C
    library toupper table (C locale: only a-z change)."""
    raw = name if isinstance(name, bytes) else name.encode("cp932")
    out = bytearray()
    for ch in raw[:SCRIPT_KEY_LEN]:
        if ch == 0 or ch == 0x2E:
            break
        out.append(ch - 0x20 if 0x61 <= ch <= 0x7A else ch)
    return out.decode("latin-1")


def _two_digits(v: int) -> str:
    # VA 0x819D4..0x81A68: "_00_00" template, digits written only for v > 0,
    # ones digit v % 10, tens digit (v // 10) % 10
    return f"{v % 100:02d}" if v > 0 else "00"


def stch_image_name(base: str, aa: int = 0, bb: int = 0) -> str:
    """Name looked up by the STCH/SCH2 backend (VA 0x81760) for
    ``_STCH(layer, base, ..., a9=aa, a10=bb)``: when ``aa | bb`` is non-zero the
    suffix "_%02d_%02d" is appended (VA 0x819C7..0x81A79), so
    ``_STCH(4, img1696, ..., 11, 11)`` shows IMG1696_11_11 *instead of* IMG1696
    and ``_STCH(.., aok_l_21_00_n00, .., 3, 3)`` shows AOK_L_21_00_N00_03_03.
    (When a18 names a layer 0..39, aa/bb default to the suffix of the image
    currently on that layer; explicit a9/a10 still override.)"""
    if aa | bb:
        return f"{base}_{_two_digits(aa)}_{_two_digits(bb)}"
    return base


@dataclass(frozen=True)
class ResolvedImage:
    """What the engine draws for one script image id."""
    key: str                        # script_image_key(name)
    index: int                      # image-table / name-list index k (-1: unknown name)
    file: Optional[str] = None      # record.file (drawn first)
    overlay: Optional[str] = None   # record.overlay (drawn over file), paired sprites only
    file_id: Optional[int] = None   # packed id of file (VA 0x4A060 -> 0xC3270), None = missing
    overlay_id: Optional[int] = None  # packed id of overlay (VA 0x4A110 -> 0xC3270)
    reduced_id: Optional[int] = None  # 0x30000 | k: the allpac2 copy (thumbnail path, VA 0xAB3A0)

    @property
    def found(self) -> bool:
        return self.index >= 0


class ScriptImageResolver:
    """Engine-faithful resolution of script image ids.

    1. key = :func:`script_image_key` (up to the first '.', upper case);
    2. k = position of key in the embedded name list (data VA 0x7F6800, a
       std::map built lazily by VA 0x780D0; 0xFFFF = not found);
    3. record = image table[k]; the engine resolves ``record.file`` and
       ``record.overlay`` (full file names with extension) with the NAM search
       of :func:`find_image` and caches the ids at record +0x20 / +0x24.

    ``names`` is :meth:`tsuki.catalog_eboot.Eboot.image_names`, ``table`` is
    :meth:`~tsuki.catalog_eboot.Eboot.image_table`, ``index`` an
    :class:`ImageNameIndex` over the five searchable NAM tables.
    """

    def __init__(self, names: Sequence[str], table: Sequence[Any], index: "ImageNameIndex") -> None:
        self._map: dict[str, int] = {}
        for k, n in enumerate(names):
            self._map.setdefault(n, k)     # std::map insert keeps the first key
        self._table = table
        self._index = index

    @classmethod
    def from_game(cls, game: Path) -> "ScriptImageResolver":
        from .catalog_eboot import Eboot
        from .nam import load_nam
        with Eboot(game / "eboot.bin") as eb:
            names, table = eb.image_names(), eb.image_table()
        img = game / "image"
        nams = {a: load_nam(img / f"{a}.nam") for a in IMAGE_SEARCH_ORDER}
        return cls(names, table, ImageNameIndex(nams))

    def index_of(self, name: Union[str, bytes]) -> int:
        return self._map.get(script_image_key(name), -1)

    def resolve(self, name: Union[str, bytes], aa: int = 0, bb: int = 0) -> ResolvedImage:
        """Resolve ``name`` as STCH/SCH2 would with a9=aa, a10=bb (pass 0, 0 for
        SPFC/SPRD/STCG and for a plain STCH)."""
        full = stch_image_name(name if isinstance(name, str) else name.decode("cp932"), aa, bb)
        key = script_image_key(full)
        k = self._map.get(key, -1)
        if k < 0 or k >= len(self._table):
            return ResolvedImage(key, -1)
        r = self._table[k]
        return ResolvedImage(key, k, r.file, r.overlay, self._index.find(r.file),
                             self._index.find(r.overlay) if r.overlay else None,
                             pack_image_id("allpac2", k))


# ---------------------------------------------------------------------------
# name grammar
# ---------------------------------------------------------------------------

_SPRITE_RE = re.compile(
    r"(?P<char>[A-Z]{3}|[ZY]\d\d)_(?P<size>[ALMS])_(?P<group>\d\d)_(?P<pose>\d\d)_"
    r"(?P<vletter>[NABC])(?P<expr>\d\d)(?:_(?P<body>B)|_(?P<la>\d\d)_(?P<lb>\d\d))?")
_SCENE_RE = re.compile(r"IMG(?P<num>\d{4})(?:_(?P<la>\d\d)_(?P<lb>\d\d))?")
_LANG_RE = re.compile(r"(?P<base>.+?)_(?P<lang>JA|EN|ZC|ZT|KO|TH)")
_COLORS = {"BLACK", "BLUE", "GREEN", "ORANGE", "RED", "WHITE", "YELLOW"}


@dataclass
class NameInfo:
    name: str
    category: Category
    lang: str = ""                    # ja/en/zc/zt/ko when the name carries one
    base: str = ""                    # name the entry belongs to (scene/sprite base, UI set)
    char_code: str = ""
    char_id: int = 0
    char_name: str = ""
    size_class: str = ""
    layer: str = ""                   # "aa_bb" for layers
    detail: str = ""


def classify_name(name: str, archive: Optional[str] = None) -> NameInfo:
    """Classify an asset by its NAM name (extension optional, case-insensitive).
    ``archive`` refines a few cases (allpac2 = reduced copy, parts.mrg has no names)."""
    up = name.upper()
    stem = up.rpartition(".")[0] or up
    if stem in _COLORS:
        return NameInfo(name, Category.SOLID_COLOR, detail="1920x1080 solid fill")
    if stem in ("DUMMY", "CGMODE_TOP"):
        return NameInfo(name, Category.PLACEHOLDER, detail="128-byte 16x16 transparent image MZP despite .MRG")
    if stem.endswith("_TOP"):
        return NameInfo(name, Category.PLACEHOLDER, detail="128x96 white image (1216 bytes, shared)")
    m = _SCENE_RE.fullmatch(stem)
    if m:
        num = int(m.group("num"))
        if m.group("la"):
            return NameInfo(name, Category.SCENE_LAYER, base=f"IMG{m.group('num')}",
                            layer=f"{m.group('la')}_{m.group('lb')}",
                            detail="variant of the base shown instead of it by _STCH/_SCH2(..., base, ..., a9=aa, a10=bb)")
        if 3000 <= num <= 4193:
            return NameInfo(name, Category.GALLERY_IMAGE, base=stem,
                            detail="allpac1_2 only, never referenced by scripts, listed by gallery tables")
        return NameInfo(name, Category.SCENE, base=stem, detail="background or event CG")
    m = _SPRITE_RE.fullmatch(stem)
    if m:
        code = m.group("char")
        cid, cname = CHAR_CODES.get(code, (0, "unknown"))
        base = f"{code}_{m.group('size')}_{m.group('group')}_{m.group('pose')}_{m.group('vletter')}{m.group('expr')}"
        info = NameInfo(name, Category.SPRITE, base=base, char_code=code, char_id=cid, char_name=cname,
                        size_class=m.group("size"))
        if m.group("body"):
            info.category = Category.SPRITE_BODY
            info.detail = "body; drawn under the face of every image-table record whose file it is"
        elif m.group("la"):
            info.category = Category.SPRITE_LAYER
            info.layer = f"{m.group('la')}_{m.group('lb')}"
            info.detail = "variant of the base sprite shown instead of it by _STCH/_SCH2(..., base, ..., a9=aa, a10=bb)"
        else:
            info.detail = ("script-visible sprite name; draw its image-table record: file, then overlay if set "
                           "(3,529 records pair a _B body with this face, 1,221 are standalone)")
        return info
    if stem.startswith("RULE_"):
        return NameInfo(name, Category.TRANSITION_RULE, detail="1920x1080 8-bit grey wipe mask")
    if stem.startswith("CAUTION"):
        m2 = re.fullmatch(r"CAUTION(\d?)(?:_(JA|EN|ZC|ZT))?", stem)
        lang = (m2.group(2) or "JA").lower() if m2 else ""
        kind = {"": "fiction/violence disclaimer", "1": "recording/streaming notice",
                "2": "disclaimer + recording notice"}.get(m2.group(1) if m2 else "", "?")
        return NameInfo(name, Category.CAUTION_SCREEN, lang=lang, detail=kind)
    m = _LANG_RE.fullmatch(stem)
    setname, lang = (m.group("base"), m.group("lang")) if m else (stem, "")
    if setname in UI_PARTS_SETS or f"{setname}_{lang}" in UI_PARTS_SETS:
        if lang == "TH":
            return NameInfo(name, Category.UI_THUMBNAILS, base=f"{setname}_TH",
                            detail=UI_PARTS_SETS[f"{setname}_TH"][1])
        return NameInfo(name, Category.UI_PARTS, lang=lang.lower(), base=setname,
                        detail=UI_PARTS_SETS[setname][1])
    return NameInfo(name, Category.UNKNOWN)


# ---------------------------------------------------------------------------
# whole-catalog iteration (needs the game files)
# ---------------------------------------------------------------------------

@dataclass
class EntryInfo:
    archive: str
    index: int
    child: int                         # -1 for top-level entries/sections
    name: str
    category: str
    lang: str
    base: str
    variant: str                       # full / reduced / ""
    content: str                       # container-level format (sniff / classify_mzp)
    width: int = 0
    height: int = 0
    image_type: str = ""               # "type_a,type_b" from the image header
    size: int = 0
    detail: str = ""
    extra: dict[str, Any] = field(default_factory=dict)


_TRIPLETS = ("allpac0", "allpac1", "allpac1_2", "allpac2", "allpac3", "allpac4", "parts",
             "bgm", "vse", "vag", "voice", "voice2")
_STANDALONE = ("allscr", "script_text", "se_nam", "ft_ja", "ft_en", "ft_zc", "ft_zt", "ft_ko")


def _parts_category(index: int) -> tuple[str, str, str]:
    for start, n, desc in PARTS_MRG:
        if start <= index < start + n:
            lang = LANGS[index - start] if n == 5 else ""
            if index == 0:
                return Category.PLACEHOLDER.value, lang, desc
            return Category.SYSTEM_SCREEN.value, lang, desc
    return Category.UNKNOWN.value, "", ""


def _img_fields(head16: bytes) -> tuple[int, int, str]:
    h = image_header(head16)
    if h is None:
        return 0, 0, ""
    return h["w"], h["h"], f"{h['type'][0]},{h['type'][1]}"


def _iter_triplet(img: Path, arcname: str, children: bool) -> Iterator[EntryInfo]:
    with MrgArchive(img / arcname) as a:
        for e in a.entries:
            head = a.head(e.index, 512)
            fmt = sniff(head, e.size)
            name = e.name or ""
            w = h = 0
            itype = ""
            content = fmt
            mz: Optional[MzpArchive] = None
            if fmt == "mzp":
                try:
                    mz = MzpArchive(a.mm, e.offset, e.size)
                    content = "mzp/" + classify_mzp(mz)
                    if len(mz):
                        w, h, itype = _img_fields(mz.head(0, 16))
                except MzpError as exc:
                    content = f"mzp/error:{exc}"
            if arcname.startswith("allpac"):
                ni = classify_name(name, arcname)
                cat, lang, base, detail = ni.category.value, ni.lang, ni.base, ni.detail
                variant = "reduced" if arcname == "allpac2" else "full"
            elif arcname == "parts":
                cat, lang, detail = _parts_category(e.index)
                base, variant = "", ""
            else:
                cat = {"bgm": Category.BGM, "vse": Category.SOUND_EFFECT, "vag": Category.SYSTEM_SOUND,
                       "voice": Category.VOICE, "voice2": Category.VOICE}[arcname].value
                lang, base, variant, detail = "", "", "", ""
                if arcname == "voice2":
                    detail = "dummy"
            yield EntryInfo(arcname, e.index, -1, name, cat, lang, base, variant, content, w, h, itype,
                            e.size, detail)
            if children and mz is not None and content == "mzp/archive":
                setinfo = classify_name(name, arcname)
                for ci in range(len(mz)):
                    ch = mz.child(ci)
                    cw, chh, cty = _img_fields(ch.head(0, 16)) if len(ch) else (0, 0, "")
                    cat2 = Category.UI_PART.value
                    if setinfo.category == Category.UI_THUMBNAILS:
                        cat2 = "ui_thumbnail"
                    yield EntryInfo(arcname, e.index, ci, f"{name}#{ci}", cat2, setinfo.lang, setinfo.base,
                                    "", "mzp/" + classify_mzp(ch), cw, chh, cty, mz.sections[ci].size, "")


def _iter_standalone(img: Path, stem: str) -> Iterator[EntryInfo]:
    p = img / f"{stem}.mrg"
    with open(p, "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            for s in arc.sections:
                head = arc.head(s.index, 512)
                fmt = sniff(head, s.size)
                w = h = 0
                itype = ""
                lang = ""
                if stem == "allscr":
                    cat = {0: Category.SCRIPT_NAMES, 1: Category.SCRIPT_LABELS, 2: Category.PLACEHOLDER}.get(
                        s.index, Category.SCRIPT).value
                elif stem == "script_text":
                    cat = (Category.TEXT_OFFSETS if s.index % 2 == 0 else Category.TEXT_STRINGS).value
                    lang = LANGS[s.index // 2]
                elif stem == "se_nam":
                    cat = Category.NAME_TABLE.value
                else:
                    cat = Category.FONT_PAGE.value
                    lang = stem[3:]
                if fmt == "mzp":
                    ch = arc.child(s.index)
                    fmt = "mzp/" + classify_mzp(ch)
                    if len(ch):
                        w, h, itype = _img_fields(ch.head(0, 16))
                yield EntryInfo(stem, s.index, -1, "", cat, lang, "", "", fmt, w, h, itype, s.size, "")
        finally:
            mm.close()


def iter_catalog(img: Path, archives: Optional[Sequence[str]] = None, children: bool = True) -> Iterator[EntryInfo]:
    """Classify every entry of the given archives (default: all), including the
    children of allpac3's nested UI archives (``child`` >= 0).  Only headers are
    read (no MZX decoding), so a full run takes a few seconds."""
    for arc in archives or (_TRIPLETS + _STANDALONE):
        if arc in _TRIPLETS:
            yield from _iter_triplet(img, arc, children)
        elif arc in _STANDALONE:
            yield from _iter_standalone(img, arc)
        else:
            raise KeyError(arc)


# ---------------------------------------------------------------------------
# export (samples/ui_misc)
# ---------------------------------------------------------------------------

def _write_csv(path: Path, rows: Iterable[dict[str, Any]], fields: Sequence[str]) -> int:
    n = 0
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(fields), extrasaction="ignore")
        w.writeheader()
        for r in rows:
            w.writerow(r)
            n += 1
    return n


def _ffprobe(path: Path) -> dict[str, Any]:
    r = subprocess.run(["ffprobe", "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
                        str(path)], capture_output=True, text=True, check=True)
    j = json.loads(r.stdout)
    v: dict[str, Any] = next((s for s in j["streams"] if s["codec_type"] == "video"), {})
    a: dict[str, Any] = next((s for s in j["streams"] if s["codec_type"] == "audio"), {})
    return {
        "container": j["format"]["format_name"], "brand": j["format"].get("tags", {}).get("major_brand", ""),
        "duration_s": round(float(j["format"]["duration"]), 3), "bytes": int(j["format"]["size"]),
        "bitrate_kbps": round(int(j["format"].get("bit_rate", 0)) / 1000),
        "vcodec": v.get("codec_name", ""), "vprofile": v.get("profile", ""), "vlevel": v.get("level", ""),
        "width": v.get("width", 0), "height": v.get("height", 0), "fps": v.get("r_frame_rate", ""),
        "pix_fmt": v.get("pix_fmt", ""), "frames": v.get("nb_frames", ""),
        "acodec": a.get("codec_name", "none"), "aprofile": a.get("profile", ""),
        "channels": a.get("channels", 0), "sample_rate": a.get("sample_rate", ""),
        "created": j["format"].get("tags", {}).get("creation_time", ""),
        "encoder": j["format"].get("tags", {}).get("encoder", ""),
    }


_PEAK_RE = re.compile(r"Peak level dB:\s*(-?inf|-?[0-9.]+)")


def audio_peak_db(path: Path) -> Optional[float]:
    """Overall peak level (dBFS) of the first audio stream, decoded with
    ffmpeg's astats filter; -inf for digital silence, None if no audio stream."""
    r = subprocess.run(["ffmpeg", "-hide_banner", "-nostats", "-v", "info", "-i", str(path), "-vn",
                        "-map", "0:a:0?", "-af", "astats=measure_perchannel=none", "-f", "null", "-"],
                       capture_output=True, text=True, check=True)
    peaks = _PEAK_RE.findall(r.stderr)
    if not peaks:
        return None
    return float(peaks[-1])


def export(outdir: Path, game: Path, use_ffprobe: bool = True) -> dict[str, int]:
    """Write the CSV/JSON tables of samples/ui_misc (needs the game folder)."""
    from .catalog_eboot import Eboot
    from .nam import load_nam

    outdir.mkdir(parents=True, exist_ok=True)
    img = game / "image"
    counts: dict[str, int] = {}

    entries = list(iter_catalog(img))
    fields = ["archive", "index", "child", "name", "category", "lang", "base", "variant", "content",
              "width", "height", "image_type", "size", "detail"]
    counts["entries.csv"] = _write_csv(outdir / "entries.csv", (asdict(e) for e in entries), fields)

    # per archive x category summary
    summ: Counter[tuple[str, str, str]] = Counter((e.archive, "child" if e.child >= 0 else "entry", e.category)
                                                  for e in entries)
    counts["category_summary.csv"] = _write_csv(
        outdir / "category_summary.csv",
        ({"archive": a, "level": lv, "category": c, "count": n} for (a, lv, c), n in sorted(summ.items())),
        ["archive", "level", "category", "count"])

    # inner formats (container level)
    fm: Counter[tuple[str, str]] = Counter((e.content, e.image_type) for e in entries)
    counts["inner_formats.csv"] = _write_csv(
        outdir / "inner_formats.csv",
        ({"content": c, "image_type": t, "count": n} for (c, t), n in sorted(fm.items(), key=lambda kv: -kv[1])),
        ["content", "image_type", "count"])

    # archive roles
    counts["archives.csv"] = _write_csv(outdir / "archives.csv", (
        {**asdict(a), "files": " ".join(a.files)} for a in ARCHIVES.values()),
        ["name", "files", "container", "role", "engine", "playgo_chunk", "languages"])

    # UI parts children with per-language identity
    ui = [e for e in entries if e.archive == "allpac3" and e.child >= 0]
    with MrgArchive(img / "allpac3") as a:
        hashes: dict[tuple[int, int], str] = {}
        for e in ui:
            ent = a.entries[e.index]
            top = MzpArchive(a.mm, ent.offset, ent.size)
            s = top.sections[e.child]
            hashes[(e.index, e.child)] = hashlib.sha1(
                a.mm[ent.offset + s.offset:ent.offset + s.offset + s.size]).hexdigest()[:16]
    by_slot: dict[tuple[str, int], dict[str, str]] = {}
    for e in ui:
        by_slot.setdefault((e.base, e.child), {})[e.lang or "-"] = hashes[(e.index, e.child)]
    rows = []
    for e in ui:
        hs = by_slot[(e.base, e.child)]
        same = sorted(k for k, v in hs.items() if v == hashes[(e.index, e.child)] and k != (e.lang or "-"))
        rows.append({**asdict(e), "sha1_16": hashes[(e.index, e.child)], "identical_to": " ".join(same)})
    counts["ui_parts.csv"] = _write_csv(outdir / "ui_parts.csv", rows,
                                        ["archive", "index", "child", "name", "base", "lang", "content", "width",
                                         "height", "image_type", "size", "sha1_16", "identical_to"])

    # parts.mrg
    counts["parts_mrg.csv"] = _write_csv(outdir / "parts_mrg.csv",
                                         (asdict(e) for e in entries if e.archive == "parts"),
                                         ["index", "category", "lang", "width", "height", "image_type", "size",
                                          "detail"])

    # eboot tables
    nams = {an: load_nam(img / f"{an}.nam") for an in IMAGE_SEARCH_ORDER}
    index = ImageNameIndex(nams)
    with Eboot(game / "eboot.bin") as eb:
        itab = eb.image_table()
        inames = eb.image_names()
        counts["eboot_image_table.csv"] = _write_csv(outdir / "eboot_image_table.csv", (
            {**asdict(r), "va": hex(r.va), "logical_name": r.name, "script_name": inames[r.index],
             "paired": int(r.paired)} for r in itab),
            ["index", "va", "script_name", "logical_name", "file", "overlay", "paired", "w", "h", "a", "b", "c",
             "d", "flags", "layer_kind", "char_id", "size_class"])
        ui_tables = eb.ui_tables()
        with MrgArchive(img / "allpac3") as a3:
            a3_children = {e.index: len(MzpArchive(a3.mm, e.offset, e.size)) for e in a3.entries}
        neutral = {0x781B70, 0x8730F0}
        trows = []
        for t in ui_tables:
            for r in t.records:
                nm = r.name if t.va in neutral else localize_parts_name(r.name, 1)
                pid = index.find(nm)
                arc, ai = unpack_image_id(pid) if pid is not None else ("", -1)
                n_ch = a3_children.get(ai, 0) if arc == "allpac3" else 0
                trows.append({"table_va": hex(t.va), "record_va": hex(r.va), "tex_slot": r.tex_slot,
                              "name": r.name, "child": r.child, "child2": r.child2,
                              "nam_name_en": nm.upper() if pid is not None else "",
                              "allpac3_index": ai, "children": n_ch,
                              "loaded_child": ui_child_index(r.child, n_ch) if pid is not None else ""})
        counts["ui_texture_slots.csv"] = _write_csv(outdir / "ui_texture_slots.csv", trows,
                                                    ["table_va", "record_va", "tex_slot", "name", "child", "child2",
                                                     "nam_name_en", "allpac3_index", "children", "loaded_child"])
        mtab = eb.movie_table()
        langs = eb.lang_suffixes()
        prologue = eb.prologue_ids()

    # movies
    mrows = []
    for mv in mtab:
        info = movie_info(mv.movie_id)
        row: dict[str, Any] = {"movie_id": mv.movie_id, "file": mv.file_name, "eboot_path": mv.path,
                               "group": info["group"], "lang": info["lang"], "base": info["base"],
                               "base_id": info["base_id"],
                               "selected_for": " ".join(l for l, f in info["variants"].items()
                                                        if f == mv.file_name)}
        if use_ffprobe:
            mp = game / "image" / "movie" / mv.file_name
            row.update(_ffprobe(mp))
            if row["acodec"] != "none":
                pk = audio_peak_db(mp)
                row["audio_peak_db"] = "" if pk is None else ("-inf" if pk == float("-inf") else round(pk, 2))
                row["audio_silent"] = int(pk == float("-inf"))
        mrows.append(row)
    mfields = ["movie_id", "file", "group", "base", "base_id", "lang", "selected_for", "container", "brand",
               "duration_s", "bytes", "bitrate_kbps", "vcodec", "vprofile", "vlevel", "width", "height", "fps",
               "pix_fmt", "frames", "acodec", "aprofile", "channels", "sample_rate", "audio_peak_db",
               "audio_silent", "created", "encoder", "eboot_path"]
    counts["movies.csv"] = _write_csv(outdir / "movies.csv", mrows, mfields)

    # language selection matrix for movies
    sel = [{"movie_id": b, "base": MOVIE_NAMES[b], **{LANGS[l]: movie_file_for_language(b, l) for l in range(4)}}
           for b in range(46)]
    counts["movie_language_matrix.csv"] = _write_csv(outdir / "movie_language_matrix.csv", sel,
                                                     ["movie_id", "base", "ja", "en", "zc", "zt"])

    # loose files
    loose = []
    for rel, cat, desc in (("data/if_ja.png", Category.SAVE_ICON, "save-data icon, ja"),
                           ("data/if_en.png", Category.SAVE_ICON, "save-data icon, en (also default)"),
                           ("data/if_zc.png", Category.SAVE_ICON, "save-data icon, zc (byte-identical to zt)"),
                           ("data/if_zt.png", Category.SAVE_ICON, "save-data icon, zt"),
                           ("image/corp.png", Category.SCREENSHOT_OVERLAY, "TYPE-MOON watermark on screenshots")):
        p = game / rel
        b = p.read_bytes()
        w, h = struct.unpack(">II", b[16:24])
        loose.append({"file": rel, "category": cat.value, "bytes": len(b), "width": w, "height": h,
                      "png_color_type": b[25], "sha1_16": hashlib.sha1(b).hexdigest()[:16], "detail": desc})
    counts["loose_files.csv"] = _write_csv(outdir / "loose_files.csv", loose,
                                           ["file", "category", "bytes", "width", "height", "png_color_type",
                                            "sha1_16", "detail"])

    meta: dict[str, Any] = {
        "lang_suffixes": langs, "prologue_ids": prologue, "image_search_order": list(IMAGE_SEARCH_ORDER),
        "archive_ids": ARCHIVE_IDS, "char_codes": CHAR_CODES, "size_classes": SIZE_CLASSES,
        "movie_count": len(mtab), "image_table_records": len(itab), "image_name_list_records": len(inames),
        "ui_tables": [{"va": hex(t.va), "records": len(t.records)} for t in ui_tables]}
    counts["engine_tables.json"] = 1
    # script-name list == image table (name k == stem of record k's logical name)
    meta["name_list_mismatches"] = [k for k, r in enumerate(itab)
                                    if inames[k] != r.name.rsplit(".", 1)[0].upper()]
    counts["name_list_mismatches"] = len(meta["name_list_mismatches"])
    paired = sum(r.paired for r in itab)
    sprites = sum(classify_name(r.name).category == Category.SPRITE for r in itab)
    meta["sprite_records"] = {"sprites": sprites, "paired": paired, "standalone": sprites - paired}
    # nam lookup self-check (every allpac name resolves to itself unless shadowed)
    shadowed = []
    for arcname in IMAGE_SEARCH_ORDER:
        for rec in nams[arcname].records:
            pid = index.find(rec.name)
            if pid != pack_image_id(arcname, rec.index):
                shadowed.append(f"{arcname}:{rec.name}")
    counts["nam_lookup_shadowed"] = len(shadowed)
    # every eboot image-table file/overlay name resolves through the search order
    unresolved = sorted({n for r in itab for n in (r.file, r.overlay) if n and index.find(n) is None})
    counts["image_table_unresolved"] = len(unresolved)
    meta["nam_lookup_shadowed"] = shadowed
    meta["image_table_unresolved"] = unresolved
    # script ids never match a NAM key directly (extension missing); through the
    # resolver every record's script name resolves to itself
    res = ScriptImageResolver(inames, itab, index)
    meta["script_name_selfcheck_failures"] = [n for k, n in enumerate(inames[:len(itab)]) if res.index_of(n) != k]
    counts["script_name_selfcheck_failures"] = len(meta["script_name_selfcheck_failures"])
    (outdir / "engine_tables.json").write_text(json.dumps(meta, indent=1), encoding="utf-8")
    return counts


def _main(argv: Optional[list[str]] = None) -> int:
    import argparse
    from . import game_dir, image_dir

    ap = argparse.ArgumentParser(prog="python -m tsuki.catalog", description=__doc__.split("\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    s1 = sub.add_parser("summary")
    s1.add_argument("--archive", action="append")
    s2 = sub.add_parser("name")
    s2.add_argument("names", nargs="+")
    s3 = sub.add_parser("movies")
    s3.add_argument("--lang", choices=LANGS[:4])
    s4 = sub.add_parser("export")
    s4.add_argument("outdir")
    s4.add_argument("--no-ffprobe", action="store_true")
    s5 = sub.add_parser("image", help="resolve script image ids like the engine (STCH a9/a10 via --aa/--bb)")
    s5.add_argument("names", nargs="+")
    s5.add_argument("--aa", type=int, default=0)
    s5.add_argument("--bb", type=int, default=0)
    args = ap.parse_args(argv)

    if args.cmd == "name":
        for n in args.names:
            print(json.dumps(asdict(classify_name(n)), default=str))
        return 0
    if args.cmd == "movies":
        for i in range(len(MOVIE_NAMES)):
            info = movie_info(i)
            if args.lang and info["variants"][args.lang] != info["file"]:
                continue
            print(f"{i:3d} {info['file']:<28} {info['group']:<22} lang={info['lang']}")
        return 0
    img = image_dir()
    game = game_dir()
    if img is None or game is None:
        print("game folder not found (set TSUKI_GAME_DIR)")
        return 2
    if args.cmd == "summary":
        c: Counter[tuple[str, str]] = Counter()
        for e in iter_catalog(img, args.archive):
            c[(e.archive + ("/child" if e.child >= 0 else ""), e.category)] += 1
        for (a, cat), n in sorted(c.items()):
            print(f"{a:<16} {cat:<20} {n}")
        return 0
    if args.cmd == "image":
        res = ScriptImageResolver.from_game(game)
        for n in args.names:
            r = res.resolve(n, args.aa, args.bb)
            ids = {k: (None if v is None else (lambda t: f"{t[0]}#{t[1]}")(unpack_image_id(v)))
                   for k, v in (("file_id", r.file_id), ("overlay_id", r.overlay_id), ("reduced_id", r.reduced_id))}
            print(json.dumps({"key": r.key, "index": r.index, "file": r.file, "overlay": r.overlay, **ids}))
        return 0
    if args.cmd == "export":
        counts = export(Path(args.outdir), game, use_ffprobe=not args.no_ffprobe)
        for k, v in counts.items():
            print(f"{k}: {v}")
        return 0
    return 1


if __name__ == "__main__":
    raise SystemExit(_main())
