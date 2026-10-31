"""Area ``textbox``: golden data for the C# port of ``Text_Layout`` (package B, PACE 7.2 #3).

The 601 frozen engine layouts (``samples/textbox/engine_layout_golden.json``, produced by running the
game's own function under unicorn) are read by the C# tests directly.  This area adds what a
"every line" differential needs and the repository cannot hold in plain form (it is derived from the
user's game files, local only):

``lines``   for every line of ja, en, zc and zt (4 x 43,969) and two call configurations, the SHA-1
            of the *canonical serialisation* of the block ``tsuki.textbox.layout_box`` returns.
            ``main`` = the ordinary full-screen message (flags 0, window 0); ``bubble`` = a
            speech bubble (flags 1 << 20, window 5).  20 bytes per line, line order.  The
            serialisation (:func:`canonical`) holds every field of the block, the break classes and
            the word-length table, with float32 values as their bit patterns, so a hash match is
            a record-for-record, bit-for-bit match.
``fonts``   ``font_overrun.json``: the glyph records the layouts read past the end of their table
            (the engine indexes the record arrays unchecked, fonts.md 5.2), if any line does so.
``synthetic``  ``cases.json``: the 2,772 synthetic edge cases that were verified against the game's own
            function under unicorn (textbox.md 10.3, scratch/textbox/verify_synth.py: all markup
            codes, tags, dashes, malformed ruby, 1,300-element strings, long kinsoku and English lines,
            x flags 0 / 0x2000 / 0x20000 / 0x4000 / 0x8000 / 0x40 / bit 20, x windows 0 / 5 / 42, x 4
            languages), each with the SHA-1 of its canonical block.
``fuzz``    ``cases.json``: 6,000 seeded random messages (markup tokens, ruby, tags, dashes, CJK
            punctuation, English words) with random call arguments (width, scales, spacing, leading,
            colour, flags, window, block, ``@x`` continue position), each with the SHA-1 of its
            canonical block.  The cases where the Python port itself raises are left out.

Cross-checks recorded in the manifest: the Python layouts reproduce the 601 engine goldens, and the
number of out-of-range record reads.

Run::

    python -m oracle.textbox                 # export + verify (area samples/golden/textbox)
    python -m oracle.textbox dump ja 3       # print the canonical dump of one line
    python -m oracle.textbox dump en 3 --config bubble
    python -m oracle.textbox synth-source --out tests/Tsuki.Tests.Synthetic/Text/SyntheticLayoutCases.cs

``run`` has the signature of the ``oracle.run`` area modules; the area is registered there with
``"textbox": "oracle.textbox"`` (oracle/run.py is not owned by this package).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import multiprocessing
import os
import random
import struct
import sys
import time
from pathlib import Path
from typing import Any, Callable, Iterable, Optional, Sequence

import numpy as np

from tsuki import PROJECT_ROOT, game_dir
from tsuki import textbox as tb
from tsuki.font import LANGS, Font
from tsuki.font_eboot import EbootFontTables
from tsuki.text import ScriptText

from . import GOLDEN_ROOT
from .manifest import AreaWriter, PartWriter, Producer, verify_area

__all__ = ["AREA", "PARTS", "MODULE", "DESCRIPTION", "CONFIGS", "LANG_COUNT", "canonical", "layout_line",
           "run", "export", "golden_mismatches"]

AREA = "textbox"
MODULE = "oracle.textbox"
PARTS: tuple[str, ...] = ("lines", "fonts", "synthetic", "fuzz")
DESCRIPTION = ("Text_Layout differential data: SHA-1 of the canonical layout block of every script line "
               "(ja, en, zc, zt) in two call configurations, and glyph-table overrun records")

#: call configurations: name -> keyword arguments of ``tsuki.textbox.layout_box``
CONFIGS: dict[str, dict[str, int]] = {
    "main": {"flags": 0, "window": 0},
    "bubble": {"flags": 1 << 20, "window": 5},
}
LANG_COUNT = 4
GOLDEN_601 = PROJECT_ROOT / "samples" / "textbox" / "engine_layout_golden.json"
CANONICAL_VERSION = 1
_CHUNK = 512
F32 = np.float32

_HEAD = struct.Struct("<I12i2I")
_REC = struct.Struct("<11i4I")


# ---------------------------------------------------------------------------
# canonical serialisation (the C# test project writes the same bytes)
# ---------------------------------------------------------------------------

def _bits(x: float) -> int:
    """IEEE-754 binary32 pattern of a value that is already a float32."""
    return int(struct.unpack("<I", struct.pack("<f", x))[0])


def canonical(b: tb.TextBlock) -> bytes:
    """Little-endian serialisation of every field of a layout block (version 1)::

        u32 version
        i32 n, nlines, box_width, width_arg, height, flags, spacing, leading, last_glyph_line,
            end_ctr, y_field, empty
        u32 sx bits, sy bits
        u16 line_glyphs[32]; u32 line_width bits[32]; u32 line_height bits[32]
        i32 record count
        per record: i32 index, flags, color, ext, line, tex, x, y, x2, y2, ctr; u32 dx, dy, sx, sy bits
        u8 classes[n]; u16 wordlen[n]
    """
    out = [_HEAD.pack(CANONICAL_VERSION, b.n, b.nlines, b.box_width, b.width_arg, b.height, b.flags,
                      b.spacing, b.leading, b.last_glyph_line, b.end_ctr, b.y_field, 1 if b.empty else 0,
                      _bits(b.sx), _bits(b.sy))]
    out.append(struct.pack("<32H", *b.line_glyphs))
    out.append(struct.pack("<32I", *[_bits(x) for x in b.line_width]))
    out.append(struct.pack("<32I", *[_bits(x) for x in b.line_height]))
    out.append(struct.pack("<i", len(b.records)))
    for r in b.records:
        out.append(_REC.pack(r.index, r.flags, r.color, r.ext, r.line, r.tex, r.x, r.y, r.x2, r.y2, r.ctr,
                             _bits(r.dx), _bits(r.dy), _bits(r.sx), _bits(r.sy)))
    out.append(bytes(b.classes[:b.n]))
    out.append(struct.pack(f"<{b.n}H", *b.wordlen[:b.n]))
    return b"".join(out)


def layout_line(font: Font, lang: int, line: bytes, config: str) -> tb.TextBlock:
    """The block ``Text_Layout`` builds for a script line in a call configuration of :data:`CONFIGS`."""
    return tb.layout_box(line, lang, font=font, **CONFIGS[config])


def describe(b: tb.TextBlock) -> str:
    """A readable dump of a block (what a failing C# comparison is checked against)."""
    lines = [f"n={b.n} nlines={b.nlines} box_width={b.box_width} last_glyph_line={b.last_glyph_line} "
             f"end_ctr={b.end_ctr} y_field={b.y_field} empty={b.empty}",
             f"sx={_bits(b.sx):#010x} sy={_bits(b.sy):#010x}"]
    for k in range(b.nlines):
        lines.append(f"line {k}: glyphs={b.line_glyphs[k]} width={_bits(b.line_width[k]):#010x} "
                     f"height={_bits(b.line_height[k]):#010x}")
    for r in b.records:
        lines.append(f"rec {r.index} fl={r.flags:#x} col={r.color:#x} ext={r.ext} line={r.line} tex={r.tex:#x} "
                     f"rect=({r.x},{r.y},{r.x2},{r.y2}) ctr={r.ctr} dx={_bits(r.dx):#010x} dy={_bits(r.dy):#010x} "
                     f"sx={_bits(r.sx):#010x} sy={_bits(r.sy):#010x}")
    lines.append("classes=" + bytes(b.classes[:b.n]).hex())
    lines.append("wordlen=" + ",".join(str(x) for x in b.wordlen[:b.n]))
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# workers
# ---------------------------------------------------------------------------

_STATE: dict[str, Any] = {}


def _init_worker(game: str) -> None:
    g = Path(game)
    tables = EbootFontTables.open(g / "eboot.bin")
    _STATE["fonts"] = {i: Font(LANGS[i], game=g, tables=tables) for i in range(LANG_COUNT)}
    st = ScriptText(g / "image" / "script_text.mrg")
    _STATE["lines"] = {i: [st.table(i).line_bytes(k) for k in range(st.table(i).count)] for i in range(LANG_COUNT)}


def _hash_chunk(job: tuple[str, int, int, int]) -> tuple[str, int, int, list[bytes], dict[str, int], list[list[Any]]]:
    config, lang, start, stop = job
    font = _STATE["fonts"][lang]
    lines = _STATE["lines"][lang]
    digests: list[bytes] = []
    stats = {"lines": 0, "elements": 0, "records": 0, "ruby_records": 0, "nlines": 0, "empty": 0, "oob": 0,
             "canonical_bytes": 0}
    oob: dict[tuple[str, int], list[Any]] = {}
    for k in range(start, stop):
        b = layout_line(font, lang, lines[k], config)
        data = canonical(b)
        digests.append(hashlib.sha1(data).digest())
        stats["lines"] += 1
        stats["elements"] += b.n
        stats["records"] += len(b.records)
        stats["ruby_records"] += sum(1 for r in b.records if r.flags & 2)
        stats["nlines"] += b.nlines
        stats["empty"] += 1 if b.empty else 0
        stats["canonical_bytes"] += len(data)
        for r in b.records:
            g = r.glyph
            if g is not None and not g.in_range and g.table is not None and g.index is not None:
                stats["oob"] += 1
                oob[(g.table, g.index)] = [g.table, g.index, g.tex, g.x, g.y, g.w, g.h]
    return config, lang, start, digests, stats, sorted(oob.values())


# ---------------------------------------------------------------------------
# the 601 engine goldens
# ---------------------------------------------------------------------------

def _f32bits(x: float) -> int:
    return _bits(x)


def golden_mismatches(fonts: dict[int, Font], texts: dict[int, list[bytes]]) -> tuple[int, int]:
    """(cases, mismatches) of the Python layouts against ``samples/textbox/engine_layout_golden.json``."""
    data = json.loads(GOLDEN_601.read_text())
    bad = 0
    for case in data["cases"]:
        lg = case["lang"]
        b = tb.layout_box(texts[lg][case["id"]], lg, font=fonts[lg], flags=case["flags"], window=case["window"])
        got = [[r.index, r.flags, r.color, r.ext, r.line, r.tex, r.x, r.y, r.x2, r.y2, r.ctr,
                _f32bits(r.dx), _f32bits(r.dy), _f32bits(r.sx), _f32bits(r.sy)] for r in b.records]
        ok = (got == case["records"] and b.n == case["n"] and b.nlines == case["nlines"]
              and [_f32bits(v) for v in b.line_height[:len(case["line_height"])]] == case["line_height"]
              and [_f32bits(v) for v in b.line_width[:len(case["line_width"])]] == case["line_width"])
        bad += 0 if ok else 1
    return len(data["cases"]), bad


# ---------------------------------------------------------------------------
# synthetic and fuzz cases
# ---------------------------------------------------------------------------

LONG_JA = "　" + "あいうえおかきくけこさしすせそたちつてとなにぬねの" * 3
LONG_EN = "  " + " ".join(["word", "longerword", "x", "supercalifragilistic", "a,b", "end."] * 12)
#: scratch/textbox/verify_synth.py CASES (unicorn-verified, textbox.md 10.3)
SYNTHETIC_CASES: list[str] = [
    "――――", "――@r――", "―@1^―", "@b――――@r――", "■■■", "─────",
    "<a|b|c>d", ">abc", "a|b", "<abc", "<<a|b>>", "<草|くさ>の<紙|かみ>", "<[heart]|はーと>x",
    "^" * 35 + "X", "A^B^^C", "^A", "@s12,AB", "@s-3)AB", "@x123AB", "@x12AB", "@x1AB", "@xAB",
    "@y5AB", "@y123AB", "@q(abc)AB", "@q(abcAB", "@c5AB", "@c7AB", "@c0AB", "@a1AB", "@f2AB",
    "@h3AB", "@w4AB", "@kAB", "@TAB", "@oAB^CD", "@pAB", "@tAB@uCD@lEF", "@2AB", "@1AB",
    "@iAB@r", "@g@iAB@rCD", "@z@iAB@rCD", "@gA@iB@iC@rD@rE",
    "[heart]A[ber00][ber01]B[zap00][heart", "[[heart]]", "[foo]", "A[edia]B[onpu][star2]",
    # a made-up two-item choice line of the game's shape (full-width numbers, a caret, an @t); no game text
    "é", "äâ°êë", "😀A", "ｱﾞﾟ", "「…」『』〜", "１．見本の選択肢を読み上げる。　^@t２．次へ進む。",
    # a made-up English sentence in quotes and angle brackets (the shape of a foreign-language line); no game text
    "\"<I shall paint the walls of the Blue Pavilion.>\"", "<->",
    LONG_JA, LONG_JA.replace("さ", "」"), LONG_JA.replace("こ", "「"), LONG_JA.replace("た", "っ"),
    LONG_JA.replace("く", "。」"), LONG_JA.replace("し", "<志|し>"), LONG_JA.replace("ぬ", "<縫|ぬいぬいぬいぬい>"),
    "　" + "<漢字|かんじかんじかんじかんじかんじ>" * 8, "　" + "あ" * 26 + "<漢|かんじかんじかんじかんじ>あいう",
    LONG_EN, LONG_EN.replace(" ", "  "), "  " + "abcdefghij" * 12, "  " + "word. " * 30, "  " + "\"Yes!\" " * 25,
    "  " + "a, b, c, " * 25, "  " + ("―" * 3 + " ") * 20,
    "@o" + LONG_JA, "@o" + LONG_EN, LONG_JA + "@x" + LONG_JA, "A" * 1300, "あ" * 1250,
]
SYNTHETIC_FLAGS: tuple[int, ...] = (0, 0x2000, 0x20000, 0x4000, 0x8000, 0x40, 1 << 20)
SYNTHETIC_WINDOWS: tuple[int, ...] = (0, 5, 42)
FUZZ_SEED = 20261025
FUZZ_CASES = 6000

_WORDS = ["The", "familiar", "scent", "of", "dust", "a", "I", "word", "longerword", "supercalifragilistic", "end",
          "x", "Yes", "no", "hangs", "in", "the", "air", "grime", "Tohno", "Shiki", "moon", "blue", "glass"]
_SEPS = [" ", " ", " ", "  ", ", ", ". ", "! ", "? ", "\"", "'", ".\"", "...", "-", ";", ":"]
_KANA = ["あ", "い", "う", "え", "お", "か", "き", "く", "け", "こ", "さ", "し", "す", "せ", "そ", "た", "ち", "つ", "て", "と",
         "な", "に", "ぬ", "ね", "の", "ゃ", "ゅ", "ょ", "っ", "ぁ", "ぃ", "ァ", "ッ", "ャ", "ー", "ヶ"]
_KANJI = list("誰彼眠頃夜月姫志貴遠野秋葉屋敷血鬼魔術")
_CJK_PUNCT = list("、。！？「」『』（）・…〜，．：；〉》】　") + ["‘", "’", "“", "”", "‧"]
_WIDE_OTHER = ["Ａ", "Ｂ", "１", "２", "±", "×", "÷", "é", "ä", "°", "ｱ", "ｲ", "ﾞ", "ﾟ", "😀", "ñ"]
_DASHES = ["―", "――", "■", "■■", "─", "───", "―@b―@r―"]
_RUBY = ["<漢字|かんじ>", "<志貴|しき>", "<月姫|つきひめ>", "<a|b|c>", "<[heart]|はーと>", "<縫|ぬいぬいぬいぬい>", ">", "|", "<", "<->",
         "<漢|かんじかんじかんじかんじ>", "<x|y>"]
_CODES = ["@a1", "@a0", "@b", "@c0", "@c1", "@c2", "@c3", "@c4", "@c5", "@c6", "@c9", "@f0", "@f1", "@f2", "@f3", "@h0", "@h3",
          "@h8", "@h9", "@w0", "@w5", "@w8", "@w9", "@s12,", "@s-3)", "@s30,", "@s0)", "@x1", "@x12", "@x123", "@x", "@y2",
          "@y45", "@z", "@g", "@i", "@r", "@o", "@p", "@t", "@u", "@l", "@k", "@T", "@1", "@2", "@q(abc)", "@q(ab", "@d(x)", "^",
          "^", "^^"]
_TAGS = ["[heart]", "[star]", "[ber03]", "[ber30]", "[zap00]", "[edia]", "[onpu]", "[foo]", "[", "[heart2]"]
_SCALE_30_24 = float(F32(F32(30.0) / F32(24.0)) * F32(0.74))


def _fuzz_text(rng: random.Random) -> str:
    n = max(1, int(rng.lognormvariate(3.2, 0.9)))
    kinds = rng.choices(["en", "ja", "mixed"], weights=[3, 4, 3])[0]
    out: list[str] = []
    for _ in range(n):
        r = rng.random()
        if r < 0.28:
            if kinds in ("en", "mixed"):
                out.append(rng.choice(_WORDS) + rng.choice(_SEPS))
            else:
                out.append(rng.choice(_KANA) * rng.randint(1, 6))
        elif r < 0.50:
            out.append("".join(rng.choice(_KANA + _KANJI) for _ in range(rng.randint(1, 12))))
        elif r < 0.58:
            out.append(rng.choice(_CJK_PUNCT))
        elif r < 0.62:
            out.append(rng.choice(_WIDE_OTHER))
        elif r < 0.66:
            out.append(rng.choice(_DASHES))
        elif r < 0.72:
            out.append(rng.choice(_RUBY))
        elif r < 0.84:
            out.append(rng.choice(_CODES))
        elif r < 0.87:
            out.append(rng.choice(_TAGS))
        elif r < 0.90:
            out.append(rng.choice(_SEPS))
        else:
            out.append("".join(rng.choice(_KANA + _KANJI + _CJK_PUNCT) for _ in range(rng.randint(8, 40))))
    return "".join(out)


def _fuzz_params(rng: random.Random) -> dict[str, Any]:
    return {
        "lang": rng.randrange(4),
        "flags": rng.choice([0, 0, 0, 0, 1 << 20, 0x2000, 0x20000, 0x4000, 0x8000, 0x40, 0x4000 | 0x8000, 0x2000 | (1 << 20)]),
        "window": rng.choice([0, 0, 0, 5, 42, 3, 1]),
        "width": rng.choice([474, 474, 474, 400, 600, 300, 574]),
        "height": 220,
        "spacing": rng.choice([0, 0, 0, 2, -1]),
        "leading": rng.choice([9, 9, 9, 0, 12]),
        "color": rng.choice([0xFFFF, 0xFFFF, 0xDC6D, 0x1234]),
        "sx": rng.choice([0.74, 0.74, 0.74, 0.5, 1.0, _SCALE_30_24]),
        "sy": rng.choice([0.74, 0.74, 0.74, 0.6, 1.0]),
        "block": rng.choice([4, 4, 4, 14, 79, 8]),
        "saved_x": rng.choice([0.0, 0.0, 100.0, 450.0, 560.0, 700.0]),
    }


def _case_json(lang: int, text: bytes, params: dict[str, Any], b: tb.TextBlock) -> dict[str, Any]:
    d = dict(params)
    d["lang"] = lang
    d["text"] = text.hex()
    d["n"] = b.n
    d["nlines"] = b.nlines
    d["sha1"] = hashlib.sha1(canonical(b)).hexdigest()
    return d


def _call(font: Font, lang: int, text: bytes, params: dict[str, Any],
          oob: Optional[dict[tuple[str, int], list[Any]]] = None) -> tb.TextBlock:
    kw = {k: v for k, v in params.items() if k != "lang"}
    b = tb.layout_box(text, lang, font=font, **kw)
    if oob is not None:
        for r in b.records:
            g = r.glyph
            if g is not None and not g.in_range and g.table is not None and g.index is not None:
                oob[(g.table, g.index)] = [g.table, g.index, g.tex, g.x, g.y, g.w, g.h]
    return b


def synthetic_cases(fonts: dict[int, Font], oob: dict[tuple[str, int], list[Any]]) -> list[dict[str, Any]]:
    """The 2,772 unicorn-verified edge cases with the hash of the Python block."""
    out: list[dict[str, Any]] = []
    for flags in SYNTHETIC_FLAGS:
        for window in SYNTHETIC_WINDOWS:
            if window and flags != 0:
                continue
            for lang in range(LANG_COUNT):
                for s in SYNTHETIC_CASES:
                    text = s.encode("utf-8")
                    params = {"flags": flags, "window": window}
                    out.append(_case_json(lang, text, params, _call(fonts[lang], lang, text, params, oob)))
    return out


def fuzz_cases(fonts: dict[int, Font], oob: dict[tuple[str, int], list[Any]]) -> tuple[list[dict[str, Any]], int]:
    """Seeded random cases; returns (cases, number skipped because the Python port raised)."""
    rng = random.Random(FUZZ_SEED)
    out: list[dict[str, Any]] = []
    skipped = 0
    while len(out) < FUZZ_CASES:
        params = _fuzz_params(rng)
        lang = params["lang"]
        text = _fuzz_text(rng).encode("utf-8")
        try:
            b = _call(fonts[lang], lang, text, params, oob)
        except (IndexError, ValueError):
            skipped += 1
            continue
        out.append(_case_json(lang, text, params, b))
    return out, skipped


# ---------------------------------------------------------------------------
# unit cases for tests/Tsuki.Tests.Synthetic (no game data at test time)
# ---------------------------------------------------------------------------

#: (name, lang, text, C# expression for the text or None, flags, window)
UNIT_CASES: list[tuple[str, int, str, Optional[str], int, int]] = [
    ("caretEmptyLine", 1, "A^B^^C", None, 0, 0),
    ("caretNoBreakFlag", 1, "A^B", None, 0x4000, 0),
    ("leadingCaret", 1, "^A", None, 0, 0),
    ("lineLimit", 1, "^" * 35 + "X", "new string('^', 35) + \"X\"", 0, 0),
    ("kinsokuWrap", 0, LONG_JA, "LongJa", 0, 0),
    ("kinsokuHang", 0, "\u3000" + "あ" * 27 + "」い", "\"\\u3000\" + new string('あ', 27) + \"」い\"", 0, 0),
    ("kinsokuOpenBracket", 0, LONG_JA.replace("こ", "「"), "LongJa.Replace(\"こ\", \"「\")", 0, 0),
    ("smallKana", 0, LONG_JA.replace("た", "っ"), "LongJa.Replace(\"た\", \"っ\")", 0, 0),
    ("ruby", 0, "<漢|あい>字", None, 0, 0),
    ("rubyHidden", 0, "<漢|あい>字", None, 0x2000, 0),
    ("rubyEnglishIsPlain", 1, "<A|B>C", None, 0, 0),
    ("dashJoin", 0, "――――", None, 0, 0),
    ("englishWrap", 1, "  " + "AB CD xy, E. " * 20, "Words", 0, 0),
    ("englishBubble", 1, "  " + "AB CD xy, E. " * 20, "Words", 1 << 20, 5),
    ("scaleCode", 1, "A@h8B@w9C@rD", None, 0, 0),
    ("colourAndStyle", 1, "A@c1B@zC@iD@rE", None, 0, 0),
    ("sCode", 1, "A@s12,B@s-3)C", None, 0, 0),
    ("xCode", 1, "A@x123B@x12C@xD", None, 0, 0),
    ("yCode", 1, "A@y45B", None, 0, 0),
    ("unknownGroup", 1, "A@q(abc)B@q(abC", None, 0, 0),
    ("noWrapCode", 1, "@o" + "ABCDE " * 40, "\"@o\" + string.Concat(Enumerable.Repeat(\"ABCDE \", 40))", 0, 0),
    ("typewriterCodes", 1, "A@tB@uC@lD", None, 0, 0),
    ("literalCaret", 1, "A@1^B", None, 0, 0),
    ("invalidLead", 0, "@1\u00e9A", None, 0, 0),
    ("budget", 1, "A" * 1300, "new string('A', 1300)", 0, 0),
    ("empty", 1, "", None, 0, 0),
    ("onlyCode", 1, "@r", None, 0, 0),
]


class _RecordingLookup:
    def __init__(self, name: str, inner: Any, log: dict[str, set[tuple[int, Optional[int]]]]) -> None:
        self._name, self._inner, self._log = name, inner, log

    def get(self, key: int) -> Optional[int]:
        idx = self._inner.get(key)
        self._log.setdefault(self._name, set()).add((key, idx))
        return idx  # type: ignore[no-any-return]

    def __getattr__(self, item: str) -> Any:
        return getattr(self._inner, item)


class _RecordingRecords:
    def __init__(self, name: str, inner: Any, log: dict[str, set[tuple[int, tuple[int, int, int, int, int]]]]) -> None:
        self._name, self._inner, self._log = name, inner, log

    def get(self, idx: int) -> Any:
        r = self._inner.get(idx)
        self._log.setdefault(self._name, set()).add((idx, (r.tex, r.x, r.y, r.w, r.h)))
        return r

    def __getattr__(self, item: str) -> Any:
        return getattr(self._inner, item)


def _cs_literal(text: str) -> str:
    out = []
    for ch in text:
        if ch == "\\":
            out.append("\\\\")
        elif ch == '"':
            out.append('\\"')
        elif ord(ch) < 0x20 or ch == "\u3000":
            out.append(f"\\u{ord(ch):04x}")
        else:
            out.append(ch)
    return '"' + "".join(out) + '"'


def unit_case_source(game: Path) -> str:
    """C# source of ``tests/Tsuki.Tests.Synthetic/Text/SyntheticLayoutCases.cs``: the unit cases with the
    SHA-1 of their canonical block, plus exactly the glyph-table entries the oracle read for them (so the
    synthetic font of the test needs no game data)."""
    tables = EbootFontTables.open(game / "eboot.bin")
    look_log: dict[str, set[tuple[int, Optional[int]]]] = {}
    rec_log: dict[str, set[tuple[int, tuple[int, int, int, int, int]]]] = {}
    tables.lookups = {k: _RecordingLookup(k, v, look_log) for k, v in tables.lookups.items()}  # type: ignore[assignment]
    tables.records = {k: _RecordingRecords(k, v, rec_log) for k, v in tables.records.items()}  # type: ignore[assignment]
    fonts = {i: Font(LANGS[i], game=game, tables=tables) for i in range(LANG_COUNT)}
    rows = []
    try:
        for name, lang, text, expr, flags, window in UNIT_CASES:
            b = tb.layout_box(text.encode("utf-8"), lang, font=fonts[lang], flags=flags, window=window)
            digest = hashlib.sha1(canonical(b)).hexdigest()
            glyphs = ",".join(str(x) for x in b.line_glyphs[:b.nlines])
            lit = expr if expr is not None else _cs_literal(text)
            rows.append(f'        new("{name}", Language.{["Ja", "En", "Zc", "Zt"][lang]}, {lit}, 0x{flags:x}, {window}, '
                        f'{b.n}, {b.nlines}, "{glyphs}", "{digest}"),')
    finally:
        for f in fonts.values():
            f.close()
    look_rows = [f'        ("{t}", 0x{k:x}, {idx if idx is not None else -1}),'
                 for t in sorted(look_log) for k, idx in sorted(look_log[t], key=lambda e: e[0])]
    rec_rows = [f'        ("{t}", {i}, {r[0]}, {r[1]}, {r[2]}, {r[3]}, {r[4]}),'
                for t in sorted(rec_log) for i, r in sorted(rec_log[t], key=lambda e: e[0])]
    unit = "\"あいうえおかきくけこさしすせそたちつてとなにぬねの\""
    return ("// <auto-generated> python -m oracle.textbox synth-source (UNIT_CASES in tools/oracle/textbox.py); do not edit. </auto-generated>\n"
            "using Tsuki.Engine.Contracts;\n\nnamespace Tsuki.Tests.Synthetic.Text;\n\n"
            "/// <summary>Unit cases of the text layout with the oracle's results, and the glyph-table entries it read.</summary>\n"
            "internal static class SyntheticLayoutCases\n{\n"
            f"    public const string LongJa = \"\\u3000\" + {unit} + {unit} + {unit};\n"
            "    public static readonly string Words = \"  \" + string.Concat(Enumerable.Repeat(\"AB CD xy, E. \", 20));\n\n"
            "    public sealed record Case(string Name, Language Lang, string Text, int Flags, int Window, int N, int NLines, string LineGlyphs, string Sha1);\n\n"
            "    public static readonly Case[] Cases =\n    [\n" + "\n".join(rows) + "\n    ];\n\n"
            "    /// <summary>Lookup entries read by the oracle: table, packed key, glyph index (-1 = missing).</summary>\n"
            "    public static readonly (string Table, ulong Key, int Index)[] Lookups =\n    [\n" + "\n".join(look_rows) + "\n    ];\n\n"
            "    /// <summary>Glyph records read by the oracle: table, index, tex, x, y, w, h.</summary>\n"
            "    public static readonly (string Table, int Index, int Tex, int X, int Y, int W, int H)[] Records =\n    [\n"
            + "\n".join(rec_rows) + "\n    ];\n}\n")


# ---------------------------------------------------------------------------
# export
# ---------------------------------------------------------------------------

def _producer(function: str, args: Optional[dict[str, Any]] = None, uses: Iterable[str] = ()) -> Producer:
    return Producer(MODULE, function, dict(args or {}), tuple(uses))


def export(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, procs: Optional[int] = None,
           log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Export the area to ``root / 'textbox'`` and return its manifest."""
    sel = list(PARTS) if parts is None else list(parts)
    bad = [p for p in sel if p not in PARTS]
    if bad:
        raise ValueError(f"unknown parts {bad}; choose from {list(PARTS)}")
    say = log if log is not None else (lambda s: None)
    game = Path(game)
    area = AreaWriter(Path(root), AREA, _producer("export", {"parts": sel, "configs": CONFIGS}),
                      description=DESCRIPTION, game_dir=game)
    # lines and fonts are produced by the same pass over the lines
    results: dict[str, dict[int, list[bytes]]] = {c: {lg: [] for lg in range(LANG_COUNT)} for c in CONFIGS}
    totals: dict[str, dict[int, dict[str, int]]] = {c: {lg: {} for lg in range(LANG_COUNT)} for c in CONFIGS}
    overrun: dict[tuple[str, int], list[Any]] = {}

    st = ScriptText(game / "image" / "script_text.mrg")
    counts = {lg: st.table(lg).count for lg in range(LANG_COUNT)}
    jobs = [(c, lg, s, min(s + _CHUNK, counts[lg])) for c in CONFIGS for lg in range(LANG_COUNT)
            for s in range(0, counts[lg], _CHUNK)]
    nproc = procs or max(1, min(14, (os.cpu_count() or 2) - 2))
    t0 = time.perf_counter()
    chunks: dict[tuple[str, int, int], list[bytes]] = {}
    with multiprocessing.get_context("spawn").Pool(nproc, _init_worker, (str(game),)) as pool:
        for config, lang, start, digests, stats, oob in pool.imap_unordered(_hash_chunk, jobs):
            chunks[(config, lang, start)] = digests
            acc = totals[config][lang]
            for k, v in stats.items():
                acc[k] = acc.get(k, 0) + v
            for rec in oob:
                overrun[(rec[0], rec[1])] = rec
    say(f"[textbox] {sum(len(v) for v in chunks.values()):,} layouts with {nproc} processes in "
        f"{time.perf_counter() - t0:.1f} s")
    for (config, lang, start), digests in sorted(chunks.items()):
        results[config][lang].extend(digests)

    # the 601 engine goldens, checked in this process
    tables = EbootFontTables.open(game / "eboot.bin")
    fonts_for_cases = {i: Font(LANGS[i], game=game, tables=tables) for i in range(LANG_COUNT)}
    texts = {i: [st.table(i).line_bytes(k) for k in range(counts[i])] for i in range(LANG_COUNT)}
    cases, mism = golden_mismatches(fonts_for_cases, texts)

    src_uses = ("tsuki.textbox.layout_box", "tsuki.textbox.classify", "tsuki.font.Font",
                "tsuki.font_eboot.EbootFontTables", "tsuki.text.ScriptText")
    if "lines" in sel:
        part = area.part("lines", "SHA-1 of the canonical layout block of every script line, per language and configuration")
        part.add_input(game / "eboot.bin")
        part.add_input(game / "image" / "script_text.mrg")
        for config in CONFIGS:
            for lg in range(LANG_COUNT):
                digests = results[config][lg]
                assert len(digests) == counts[lg]
                part.write_bytes(f"layout_{config}_{LANGS[lg]}.sha1", b"".join(digests), "bytes",
                                 _producer("export", {"config": config, "lang": LANGS[lg], "canonical": CANONICAL_VERSION}, src_uses),
                                 {"lines": counts[lg], "digest_bytes": 20, "layout_call": CONFIGS[config],
                                  "canonical_version": CANONICAL_VERSION})
                for k, v in sorted(totals[config][lg].items()):
                    part.count(f"{config}.{LANGS[lg]}.{k}", v)
        part.count("lines_per_language", counts[0])
        part.count("layouts", sum(len(r) for c in results.values() for r in c.values()))
        part.check("lines.per_language", [43969] * LANG_COUNT, [counts[lg] for lg in range(LANG_COUNT)],
                   "text.md 2 (43,969 lines in every language)")
        part.check("layouts.total", 4 * 43969 * len(CONFIGS), sum(len(r) for c in results.values() for r in c.values()),
                   "PACE 7.2 #3: 4 x 43,969 lines")
        part.check("golden601.cases", 601, cases, "samples/textbox/engine_layout_golden.json (unicorn, textbox.md 10.3)")
        part.check("golden601.mismatches", 0, mism, "tsuki.textbox against the engine layouts")
        part.finish()
    desc = ("text = UTF-8 hex; every other field is a layout_box argument (lang is the language index); "
            "sha1 = SHA-1 of the canonical block")
    syn_json = synthetic_cases(fonts_for_cases, overrun) if ("synthetic" in sel or "fonts" in sel) else []
    fuzz_json, fuzz_skipped = fuzz_cases(fonts_for_cases, overrun) if ("fuzz" in sel or "fonts" in sel) else ([], 0)
    if "fonts" in sel:
        part = area.part("fonts", "glyph records read past the end of their table")
        part.add_input(game / "eboot.bin")
        hits = [overrun[k] for k in sorted(overrun)]
        part.write_json("font_overrun.json", {
            "description": "records the layouts of 'lines', 'synthetic' and 'fuzz' read past the end of their table "
                           "(the engine reads the neighbouring bytes of the executable); entries = "
                           "[table, index, tex, x, y, w, h]",
            "hits": hits}, _producer("export", {}, ("tsuki.font_eboot.RecordTable.get",)))
        part.count("overrun_records", len(hits))
        part.count("overrun_reads_in_lines", sum(totals[c][lg].get("oob", 0) for c in CONFIGS for lg in range(LANG_COUNT)))
        part.finish()
    if "synthetic" in sel:
        part = area.part("synthetic", "unicorn-verified synthetic edge cases with the hash of the Python block")
        part.write_json("cases.json", {"description": desc, "canonical_version": CANONICAL_VERSION, "cases": syn_json},
                        _producer("synthetic_cases", {}, src_uses))
        part.count("cases", len(syn_json))
        part.check("cases", 9 * LANG_COUNT * len(SYNTHETIC_CASES), len(syn_json),
                   "textbox.md 10.3 (verify_synth.py: 2,772 cases)")
        part.finish()
    if "fuzz" in sel:
        part = area.part("fuzz", "seeded random messages and call arguments with the hash of the Python block")
        part.write_json("cases.json", {"description": desc, "canonical_version": CANONICAL_VERSION, "seed": FUZZ_SEED,
                                       "cases": fuzz_json},
                        _producer("fuzz_cases", {"seed": FUZZ_SEED, "cases": FUZZ_CASES}, src_uses))
        part.count("cases", len(fuzz_json))
        part.count("skipped_python_errors", fuzz_skipped)
        part.count("elements", sum(c["n"] for c in fuzz_json))
        part.check("cases", FUZZ_CASES, len(fuzz_json), "FUZZ_CASES")
        part.finish()
    for f in fonts_for_cases.values():
        f.close()
    return area.finish()


def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Entry point with the signature ``oracle.run`` expects of an area module (``quick`` is unused)."""
    return export(root, game, parts=parts, log=log)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m oracle.textbox", description=__doc__.split("\n")[0])
    sub = ap.add_subparsers(dest="cmd")
    d = sub.add_parser("dump", help="print the canonical dump of one line")
    d.add_argument("lang", choices=LANGS[:LANG_COUNT])
    d.add_argument("id", type=int)
    d.add_argument("--config", choices=sorted(CONFIGS), default="main")
    e = sub.add_parser("export", help="export and verify the area (the default)")
    e.add_argument("--out", default=str(GOLDEN_ROOT))
    e.add_argument("--game")
    e.add_argument("--procs", type=int)
    e.add_argument("--parts")
    sub.add_parser("verify", help="verify an existing export").add_argument("--out", default=str(GOLDEN_ROOT))
    ss = sub.add_parser("synth-source", help="print the C# source of the synthetic unit tests' cases and glyph data")
    ss.add_argument("--out", help="write to this file instead of stdout")
    a = ap.parse_args(argv)
    reconf = getattr(sys.stdout, "reconfigure", None)
    if reconf is not None:
        reconf(encoding="utf-8")
    g = Path(getattr(a, "game", None)) if getattr(a, "game", None) else game_dir()
    if a.cmd == "dump":
        if g is None:
            print("game folder not found (set TSUKI_GAME_DIR)", file=sys.stderr)
            return 2
        lg = LANGS.index(a.lang)
        st = ScriptText(g / "image" / "script_text.mrg")
        font = Font(a.lang, game=g, tables=EbootFontTables.open(g / "eboot.bin"))
        line = st.table(lg).line_bytes(a.id)
        b = layout_line(font, lg, line, a.config)
        print(describe(b))
        print("sha1", hashlib.sha1(canonical(b)).hexdigest())
        return 0
    if a.cmd == "synth-source":
        if g is None:
            print("game folder not found (set TSUKI_GAME_DIR)", file=sys.stderr)
            return 2
        src = unit_case_source(g)
        if a.out:
            Path(a.out).write_text(src, encoding="utf-8", newline="\n")
        else:
            print(src)
        return 0
    root = Path(getattr(a, "out", GOLDEN_ROOT))
    if a.cmd != "verify":
        if g is None or not (g / "image").is_dir():
            print("game folder not found (pass --game or set TSUKI_GAME_DIR)", file=sys.stderr)
            return 2
        parts = [p for p in a.parts.split(",") if p] if getattr(a, "parts", None) else None
        t0 = time.perf_counter()
        man = export(root, g, parts=parts, procs=getattr(a, "procs", None), log=print)
        size = sum(int(f["size"]) for f in man["files"])
        print(f"[{AREA}] wrote {len(man['files'])} files, {size:,} bytes in {time.perf_counter() - t0:.1f} s -> {root / AREA}")
    problems = verify_area(root / AREA)
    for p in problems:
        print(f"PROBLEM: {p}")
    print(f"[{AREA}] verify: {len(problems)} problems")
    return 0 if not problems else 1


if __name__ == "__main__":
    raise SystemExit(_main())
