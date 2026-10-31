"""script_text.mrg: the game's text (all languages) and its inline markup.

File layout (see docs/formats/text.md for the full description and evidence)::

    script_text.mrg = mrgd00 container (tsuki.mzp) with 10 sections
    section 2k   : offset table, big-endian u32, for language k
    section 2k+1 : string blob, UTF-8, every string ends with "\\r\\n"
    k: 0 = ja, 1 = en, 2 = zc (Simplified Chinese), 3 = zt (Traditional
       Chinese), 4 = ko (placeholder: every string is U+3000 + CRLF)

    offset table = N string offsets, then blob_size, blob_size, 0xFFFFFFFF
                   (retail: exactly one 0xFFFFFFFF; mangetsu repacks write three)
    string i     = blob[off[i] : off[i+1]]  (always ends with CRLF)

Every language has the same N = 43,969 strings; id i is the same line in every
language.  Scripts (allscr.mrg) reference lines as ``$NNNNNN`` (6 decimal
digits) inside the argument of text commands such as ``_ZM.....($000003)``.

Engine reference (eboot.bin text VA; file offset = VA + 0x2280):
  * 0x788C0 opens allscr.mrg / script_text.mrg into archive slots 0x1F / 0x20.
  * 0x75F80 copies line ``id`` of language ``lang`` (sections 2*lang, 2*lang+1):
    id < 0 -> "" (NUL written); len = off[id+1] - off[id]; len < 3 -> nothing
    copied (no NUL written either); else min(len, 0x301) - 2 bytes + NUL.
  * 0x76040 expand(out, src, src_len, mode) expands ``$NNNNNN`` references
    (mode 0: plain memcpy of src_len bytes).  Output at most 0x2FC bytes.  The
    language is the low word of the option dword at VA 0x66C59C, read raw.
  * 0x3ED90 returns the layout language: the stored option if 0..3, else
    derived from the system language (0 -> 0 ja, 11 -> 2 zc, 10 -> 3 zt,
    else 1 en).  The option word ships as 0xFFFF and is replaced by that
    mapping at VA 0x6FE94.  The suffix table at VA 0x42C030 is ja, en, zc, zt, ko.
  * 0xC6C20 lays a string out (markup interpreter), byte by byte.  Ruby markup
    (<base|ruby>) is ignored when the layout language is English.

The markup tokenizer below follows the engine's layout pass 2 (VA 0xC76C0 ff.)
on the UTF-8 *bytes* of the string: how many bytes each code consumes, which
codes take parameters, the ruby state machine, etc.
deepLuna (BSD-2, Hakanaou) and mangetsu (MIT) were read for the container
layout and the ``$NNNNNN`` convention; this is an independent implementation.
"""
from __future__ import annotations

import mmap
import re
import struct
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Callable, Iterable, Iterator, Optional, Sequence, Union

from . import image_dir
from .mzp import MzpArchive

__all__ = [
    "LANGS", "SELECTABLE_LANGS", "LANG_NAMES", "LANG_INDEX", "ICON_NAMES", "COLOR_RGB565",
    "SCALE_TABLE", "ENGINE_LINE_MAX", "ENGINE_EXPAND_MAX", "MESSAGE_ARG_MAX",
    "CHOICE_NUMBER_BASE", "CHOICE_NUMBER_OVERFLOW", "CHOICE_SLOT_SIZE", "LANG_OPTION_UNSET",
    "TextFormatError", "TextTable", "ScriptText", "parse_offset_table", "lang_index",
    "system_language_to_text", "effective_languages", "choice_number_id", "choice_slot_text",
    "Text", "Ruby", "Newline", "Icon", "Code", "Ref", "Token", "CodeSpec", "CODE_SPECS",
    "tokenize", "iter_tokens", "plain_text", "Style", "Run", "to_runs", "glyph_flags", "expand_refs",
    "ruby_enabled", "markup_census",
]

# ---------------------------------------------------------------------------
# constants
# ---------------------------------------------------------------------------

LANGS: tuple[str, ...] = ("ja", "en", "zc", "zt", "ko")
SELECTABLE_LANGS: tuple[str, ...] = ("ja", "en", "zc", "zt")   # values 0..3 of the option
LANG_NAMES = {"ja": "Japanese", "en": "English", "zc": "Chinese (Simplified)",
              "zt": "Chinese (Traditional)", "ko": "Korean (placeholder, all blank)"}
LANG_INDEX = {name: i for i, name in enumerate(LANGS)}

SECTION_COUNT = 10
END_MARK = 0xFFFFFFFF
CRLF = b"\r\n"
ENGINE_LINE_MAX = 0x2FF        # bytes of one line the engine copies (0x301 - CRLF)
ENGINE_EXPAND_MAX = 0x2FC      # max bytes of a $-expanded message (VA 0x76040 stops once pos > 0x2FB)
MESSAGE_ARG_MAX = 0x300        # text-command copy loop cap (VA 0x92458) = src_len passed to 0x76040
CHOICE_NUMBER_BASE = 43960     # choice slot k < 9 gets "$0439NN" with id 43960 + k ("1．".."9．")
CHOICE_NUMBER_OVERFLOW = 43959  # slot k >= 9 gets id 43959 ("０．")
CHOICE_SLOT_SIZE = 0xFA        # bytes per choice slot (VA 0x174FB70 + k * 0xFA)
LANG_OPTION_UNSET = 0xFFFF     # shipped value of the language option word (VA 0x66C59C)

# Inline icon names, in engine order (table at VA 0x2D3580, 39 x 11 bytes;
# a copy used by the glyph lookup at VA 0x187C80).  Index = icon number.
ICON_NAMES: tuple[str, ...] = (
    ("heart", "heart2", "star", "star2", "edia", "dummy", "onpu")
    + tuple(f"ber{i:02d}" for i in range(31))
    + ("zap00",)
)
_ICON_INDEX = {f"[{n}]".encode(): i for i, n in enumerate(ICON_NAMES)}
_ICON_RE = re.compile(rb"\[(?:" + b"|".join(re.escape(n.encode()) for n in ICON_NAMES) + rb")\]")

# @c1..@c5 colour words (u16 table at VA 0x2D34C0; entry 0 unused, @c0 = default).
COLOR_RGB565 = {1: 0xF800, 2: 0x345F, 3: 0x1082, 4: 0xAD55, 5: 0x001F}
# @hN / @wN multiply the base glyph height / width by SCALE_TABLE[N] (floats at VA 0x2D3730).
SCALE_TABLE: tuple[float, ...] = (0.34375, 0.3723958432674408, 0.4010416567325592, 0.4296875,
                                  0.4583333432674408, 0.4869791567325592, 0.2291666716337204,
                                  0.2578125, 0.2864583134651184, 0.3151041865348816)

Buffer = Union[bytes, bytearray, memoryview, mmap.mmap]


class TextFormatError(ValueError):
    pass


def lang_index(lang: Union[str, int]) -> int:
    if isinstance(lang, int):
        if not 0 <= lang < len(LANGS):
            raise ValueError(f"language index {lang} out of range 0..{len(LANGS) - 1}")
        return lang
    try:
        return LANG_INDEX[lang]
    except KeyError:
        raise ValueError(f"unknown language {lang!r}; expected one of {LANGS}") from None


def ruby_enabled(lang: Union[str, int]) -> bool:
    """The engine parses <base|ruby> for every layout language except English."""
    return lang_index(lang) != LANG_INDEX["en"]


def system_language_to_text(system_language: Optional[int]) -> int:
    """VA 0x407F0 / the fallback of 0x3ED90: sceSystemServiceParamGetInt(LANG)
    -> text language.  ``None`` = the system call failed (engine: English)."""
    if system_language == 0:
        return 0
    if system_language == 11:
        return 2
    if system_language == 10:
        return 3
    return 1


def effective_languages(option_word: int, system_language: Optional[int]) -> tuple[int, int]:
    """(language used by ``$`` expansion, language used by the layout) for a
    stored option word (u16 at VA 0x66C59C).

    * At load (VA 0x6FE94) a negative word (shipped default 0xFFFF) is replaced
      by the system-language mapping.
    * ``$`` expansion (VA 0x76040) uses the word as is (signed 16-bit), so a
      value 4 (ko) selects the blank ko sections.
    * The layout / fonts (VA 0x3ED90) use the word only if it is 0..3, else the
      system-language mapping.
    The options screen (VA 0x406B0) only ever stores 0..3."""
    w = option_word & 0xFFFF
    signed = w - 0x10000 if w & 0x8000 else w
    if signed < 0:
        signed = system_language_to_text(system_language)
    layout = signed if 0 <= signed < 4 else system_language_to_text(system_language)
    return signed, layout


def choice_number_id(slot: int) -> int:
    """Line id of the number the choice handler (VA 0x993E0) puts in front of
    choice slot ``slot`` (0-based count of _SELR commands executed for the
    current menu, word at VA 0x1750646 - not the _SELR ``n`` argument)."""
    if slot < 0:
        raise ValueError("slot must be >= 0")
    return CHOICE_NUMBER_BASE + slot if slot < 9 else CHOICE_NUMBER_OVERFLOW


def choice_slot_text(slot: int, message: str) -> str:
    """Contents of choice slot ``slot`` before expansion: "$0439NN" + message
    (VA 0x99427..0x994E5: '$' + 6 digits, then strcat of _SELR argument 1).
    It is expanded with expand_refs(..., src_len=CHOICE_SLOT_SIZE) when shown."""
    return f"${choice_number_id(slot):06d}" + message


# ---------------------------------------------------------------------------
# container layer
# ---------------------------------------------------------------------------

def parse_offset_table(data: bytes, *, strict: bool = False) -> tuple[int, ...]:
    """Big-endian u32 entries before the first 0xFFFFFFFF end mark.

    Retail tables end with exactly one 0xFFFFFFFF; tables repacked by mangetsu
    end with three.  Any number >= 1 of end marks is accepted, as long as only
    end marks follow the first one (the engine reads only off[id] and
    off[id+1], so it accepts both).  ``strict=True`` demands the retail form."""
    if len(data) % 4:
        raise TextFormatError(f"offset table size {len(data)} is not a multiple of 4")
    n = len(data) // 4
    vals = struct.unpack(f">{n}I", data)
    try:
        end = vals.index(END_MARK)
    except ValueError:
        raise TextFormatError("offset table has no 0xFFFFFFFF end mark") from None
    if any(v != END_MARK for v in vals[end:]):
        raise TextFormatError(f"non-end-mark entries after the 0xFFFFFFFF at index {end}")
    if strict and end != n - 1:
        raise TextFormatError(f"{n - end} end marks; the retail layout has exactly one")
    if end < 2:
        raise TextFormatError("offset table shorter than the two blob_size guard entries")
    return vals[:end]


@dataclass(frozen=True)
class TextTable:
    """One language: the offset table and the string blob."""
    lang: str
    offsets: tuple[int, ...]      # N string starts, blob_size, blob_size
    blob: bytes

    @property
    def count(self) -> int:
        """Number of string ids (the two trailing blob_size entries are guards)."""
        return len(self.offsets) - 2

    def raw(self, i: int) -> bytes:
        """Exact bytes of string ``i`` including the trailing CRLF."""
        if not 0 <= i < self.count:
            raise IndexError(f"{self.lang}: line id {i} out of range 0..{self.count - 1}")
        return self.blob[self.offsets[i]:self.offsets[i + 1]]

    def line_bytes(self, i: int) -> bytes:
        r = self.raw(i)
        return r[:-2] if r.endswith(CRLF) else r

    def line(self, i: int) -> str:
        return self.line_bytes(i).decode("utf-8")

    def engine_line(self, i: int) -> bytes:
        """What the engine's copy routine (VA 0x75F80) produces for id ``i``:
        b'' for a negative id; b'' if the entry is shorter than 3 bytes (the
        guard entries count and count+1 included), else min(len, 0x301) - 2
        bytes.  Ids past the table are read unchecked by the engine; here they
        raise IndexError."""
        if i < 0:
            return b""
        if i == len(self.offsets) - 1:
            return b""          # next entry is 0xFFFFFFFF: negative length as int32
        if i >= len(self.offsets):
            raise IndexError(f"{self.lang}: id {i} beyond the table")
        n = self.offsets[i + 1] - self.offsets[i]
        if n < 3:
            return b""
        return self.blob[self.offsets[i]:self.offsets[i] + min(n, 0x301) - 2]

    def validate(self) -> list[str]:
        issues: list[str] = []
        offs, size = self.offsets, len(self.blob)
        if len(offs) < 2 or offs[-1] != size or offs[-2] != size:
            issues.append(f"{self.lang}: table does not end with blob_size twice "
                          f"(last={offs[-2:]}, blob={size})")
        for i in range(len(offs) - 1):
            if offs[i + 1] < offs[i]:
                issues.append(f"{self.lang}: offsets decrease at {i}")
                break
        if offs and offs[0] != 0:
            issues.append(f"{self.lang}: first offset {offs[0]} != 0")
        blob = self.blob
        bad_end = [i for i in range(self.count)
                   if offs[i + 1] - offs[i] < 2 or blob[offs[i + 1] - 2:offs[i + 1]] != CRLF]
        if bad_end:
            issues.append(f"{self.lang}: {len(bad_end)} strings without CRLF, first {bad_end[:5]}")
        # with every string ending in CRLF, any extra CR or LF in the blob is inside a string
        if blob.count(b"\r") != self.count or blob.count(b"\n") != self.count:
            inner = [i for i in range(self.count) if b"\r" in self.line_bytes(i) or b"\n" in self.line_bytes(i)]
            issues.append(f"{self.lang}: {len(inner)} strings contain CR/LF before the end, first {inner[:5]}")
        try:
            self.blob.decode("utf-8")
        except UnicodeDecodeError as e:
            issues.append(f"{self.lang}: blob is not UTF-8: {e}")
        if offs and size and offs[self.count] != size:
            issues.append(f"{self.lang}: last string does not end at the blob end")
        return issues


class ScriptText:
    """All languages of script_text.mrg.  Opens the file read-only (mmap) and
    copies the 10 sections (12 MB total) into memory."""

    def __init__(self, path: Union[str, Path, None] = None) -> None:
        if path is None:
            d = image_dir()
            if d is None:
                raise FileNotFoundError("game folder not found (set TSUKI_GAME_DIR)")
            path = d / "script_text.mrg"
        self.path = Path(path)
        self.end_marks: dict[str, int] = {}
        with open(self.path, "rb") as f:
            mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
            try:
                arc = MzpArchive(mm)
                if arc.count != SECTION_COUNT:
                    raise TextFormatError(f"expected {SECTION_COUNT} sections, found {arc.count}")
                tables: dict[str, TextTable] = {}
                for k, lang in enumerate(LANGS):
                    tab = arc.read(2 * k)
                    offs = parse_offset_table(tab)
                    self.end_marks[lang] = len(tab) // 4 - len(offs)
                    tables[lang] = TextTable(lang, offs, arc.read(2 * k + 1))
                self.container_issues = arc.validate()
            finally:
                mm.close()
        self.tables = tables
        counts = {t.count for t in tables.values()}
        if len(counts) != 1:
            raise TextFormatError(f"languages have different line counts: "
                                  f"{ {k: t.count for k, t in tables.items()} }")
        self.count: int = counts.pop()

    # -- access -------------------------------------------------------------
    def table(self, lang: Union[str, int]) -> TextTable:
        return self.tables[LANGS[lang_index(lang)]]

    def get_line(self, lang: Union[str, int], line_id: int) -> str:
        """Line ``line_id`` of ``lang`` without its CRLF, markup left in place."""
        return self.table(lang).line(line_id)

    def get_raw(self, lang: Union[str, int], line_id: int) -> bytes:
        return self.table(lang).raw(line_id)

    def lines(self, lang: Union[str, int]) -> list[str]:
        t = self.table(lang)
        return [t.line(i) for i in range(t.count)]

    def find(self, lang: Union[str, int], needle: str) -> list[int]:
        t = self.table(lang)
        return [i for i in range(t.count) if needle in t.line(i)]

    def tokens(self, lang: Union[str, int], line_id: int) -> list["Token"]:
        return tokenize(self.get_line(lang, line_id), lang)

    def expand_bytes(self, arg: Union[str, bytes], lang: Union[str, int], *,
                     src_len: Optional[int] = None) -> bytes:
        """Engine-exact ``$NNNNNN`` expansion (VA 0x76040) of a message."""
        return expand_refs(arg, self.table(lang).engine_line, src_len=src_len)

    def expand(self, arg: Union[str, bytes], lang: Union[str, int], *,
               src_len: Optional[int] = None) -> str:
        """Expand ``$NNNNNN`` references in a script message (engine VA 0x76040),
        decoded as UTF-8 (a cut multi-byte character at the 0x2FC cap becomes U+FFFD)."""
        return self.expand_bytes(arg, lang, src_len=src_len).decode("utf-8", "replace")

    def validate(self) -> list[str]:
        issues = list(self.container_issues)
        for t in self.tables.values():
            issues += t.validate()
        for lang, n in self.end_marks.items():
            if n != 1:
                issues.append(f"{lang}: {n} 0xFFFFFFFF end marks (retail has 1)")
        return issues


# ---------------------------------------------------------------------------
# $NNNNNN expansion (script level)
# ---------------------------------------------------------------------------

def expand_refs(arg: Union[str, bytes], fetch: Callable[[int], bytes],
                limit: int = ENGINE_EXPAND_MAX, *, src_len: Optional[int] = None,
                expand: bool = True) -> bytes:
    """Engine routine VA 0x76040 ``expand(out, src, src_len, mode)``.

    * ``expand=False`` (engine mode 0): plain copy of ``src_len`` bytes; the
      result is returned up to its first NUL.
    * ``src_len`` <= 0: empty result.  ``None`` = the whole of ``arg``.
    * Loop over source bytes: a '$' takes the 6 bytes after it as decimal
      digits *without checking them* (id = sum((b - 0x30) * 10^k)); i += 7.
      A negative id inserts nothing; otherwise ``fetch(id)`` (VA 0x75F80
      semantics, see TextTable.engine_line) is appended, cut so that the
      output does not exceed ``limit`` (0x2FC) bytes.  Any other byte is
      copied.  The loop stops when the output exceeds 0x2FB bytes, after
      copying a NUL, or when i reaches src_len.  The engine then writes a
      4-byte zero after the output.
    Bytes past the end of ``arg`` (a '$' within 6 bytes of the end) read as NUL
    here; the engine reads whatever follows in its buffer."""
    src = arg.encode("utf-8") if isinstance(arg, str) else bytes(arg)
    n = len(src) if src_len is None else src_len
    if not expand:
        return src[:max(n, 0)].split(b"\0", 1)[0]
    out = bytearray()
    if n <= 0:
        return b""

    def at(k: int) -> int:
        return src[k] if k < len(src) else 0

    i = 0
    while True:
        c = at(i)
        if c == 0x24:                                  # '$'
            val = 0
            for k in range(1, 7):
                val = val * 10 + at(i + k)
            val -= 0x30 * 111111
            i += 6
            if val >= 0:
                out += fetch(val)[:max(limit - len(out), 0)]
        else:
            out.append(c)
        if len(out) >= limit:                          # engine: pos > 0x2FB
            break
        if c == 0:
            break
        i += 1
        if i >= n:
            break
    return bytes(out).split(b"\0", 1)[0]


# ---------------------------------------------------------------------------
# markup tokens
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Text:
    text: str
    raw: str = ""

    def __post_init__(self) -> None:
        if not self.raw:
            object.__setattr__(self, "raw", self.text)


@dataclass(frozen=True)
class Ruby:
    """A ruby group.  ``base`` / ``ruby`` are the glyph texts of the two parts;
    ``base_tokens`` / ``ruby_tokens`` hold every token inside each part (codes,
    newlines and icons are processed inside groups by the engine; the retail
    data has none).  ``closed`` is False when the string ended inside the group."""
    base: str
    ruby: str
    raw: str = ""
    base_tokens: tuple["Token", ...] = field(default=(), compare=False, repr=False)
    ruby_tokens: tuple["Token", ...] = field(default=(), compare=False, repr=False)
    closed: bool = field(default=True, compare=False, repr=False)


@dataclass(frozen=True)
class Newline:
    raw: str = "^"


@dataclass(frozen=True)
class Icon:
    name: str
    index: int
    raw: str = ""


@dataclass(frozen=True)
class Code:
    """An ``@`` control code.  ``arg`` is the parameter text (digit(s), number
    or parenthesised group) or None."""
    code: str
    arg: Optional[str] = None
    raw: str = ""


@dataclass(frozen=True)
class Ref:
    """Script-level ``$NNNNNN`` reference to a script_text line."""
    line_id: int
    raw: str = ""


Token = Union[Text, Ruby, Newline, Icon, Code, Ref]


@dataclass(frozen=True)
class CodeSpec:
    code: str
    params: str        # none | byte | digits0-3 | number | escape | group
    name: str
    effect: str
    confidence: str    # E = read from the engine code, H = hypothesis, D = data usage only


CODE_SPECS: dict[str, CodeSpec] = {s.code: s for s in [
    CodeSpec("a", "byte", "anim", "@aN: per-glyph animation/jitter mode N = byte-0x30 (global VA 0x178BE28; non-zero: each glyph that is not a dash-ligature piece gets random offsets via rand() and glyph flag bit 3); 0 = off; reset by @r; ignored when caller flag 0x40 is set", "E (visual effect H)"),
    CodeSpec("b", "none", "flag-b", "@b: sets glyph flag bit 16 (byte VA 0x178BE2C) on following glyphs; reset by @r. Used only together with @g", "E (visual effect H)"),
    CodeSpec("c", "byte", "colour", "@c0: back to the caller's default colour (and clears the @c6..9 value); @c1..@c5: colour word COLOR_RGB565[N]; @c6..@c9: stores N in glyph field +0x0A (secondary colour/effect index); any other byte (N = byte-0x30 as u8 >= 10) is ignored; @r resets colour and that value", "E"),
    CodeSpec("f", "byte", "font-metric", "@fN: replaces the layout's glyph metric (normally from VA 0x538F0) with dword table VA 0x2D3758[N] (480, 536, 524 for N=0..2); N = byte-0x30 is not range-checked", "E (meaning H)"),
    CodeSpec("g", "none", "style-g", "@g: text style 1 (glyph flag bit 2); in the data only the murder-impulse monologue (ids 4478..4620)", "E (look H)"),
    CodeSpec("h", "byte", "height", "@hN: glyph height scale = base * SCALE_TABLE[N] (ignored while global VA 0x177A200 != 0); reset by @r", "E"),
    CodeSpec("i", "none", "italic", "@i: English layout: italic (style 0->3, g 1->4, z 2->5); other languages: same as @g (style 1)", "E"),
    CodeSpec("k", "none", "key-wait", "@k: no-op for layout; marks a click wait (messages ending in '@k' are kept on the page; backlog check VA 0x55D01)", "E (layout), H (exact wait semantics)"),
    CodeSpec("l", "none", "seq-restore", "@l: restore the typewriter sequence counter saved by @u", "E"),
    CodeSpec("o", "none", "nowrap-on", "@o: disable automatic line wrapping (layout local rbp-0x88 = 1) until @p", "E"),
    CodeSpec("p", "none", "nowrap-off", "@p: re-enable automatic wrapping", "E"),
    CodeSpec("r", "none", "reset", "@r: reset style (italic removed: 4->1, 5->2, else 0), colour, @b, @a, @c6..9 value and the @h/@w/@s scales", "E"),
    CodeSpec("s", "number", "size", "@s<int>: x and y scale = int/64; reads at most 256 bytes: digits accumulate, '-' sets the sign, ',' ')' or NUL ends it and is consumed, other bytes are skipped; without a terminator 256 bytes are consumed", "E"),
    CodeSpec("t", "none", "seq-zero", "@t: reset the typewriter sequence counter to 0, so the following glyphs appear at the same time as the start of the message (simultaneous speech / side-by-side choices)", "E (counter) + data"),
    CodeSpec("u", "none", "seq-save", "@u: save the typewriter sequence counter", "E"),
    CodeSpec("w", "byte", "width", "@wN: glyph width scale = base * SCALE_TABLE[N] (same gating as @h)", "E"),
    CodeSpec("x", "digits0-3", "x-pos", "@x: continue at the x position where the previous message ended (new line if it no longer fits) - script-level 'glue'; @xN..: set x from 1-3 digits", "E (layout), H (digit units)"),
    CodeSpec("y", "digits0-3", "y-pos", "@yN..: always first stores 0 in the word at text-slot record +0x1E, then the 1-3 digit value if digits follow (so a bare @y clears it)", "E (meaning H)"),
    CodeSpec("z", "none", "style-z", "@z: text style 2 (glyph flags 0x100000)", "E (look H)"),
    CodeSpec("T", "none", "T", "@T: no-op for layout", "E"),
    CodeSpec("1", "escape", "escape-caret", "@1X: the next byte X is overwritten with '^' and drawn as a literal '^' glyph", "E"),
    CodeSpec("2", "escape", "escape-paren", "@2X: the next byte X is overwritten with ')' (lets ')' appear in a script argument)", "E"),
    CodeSpec("e", "none", "end", "@e: script level - ends the message text of _ZM/_ZD/_MSAD (end mode 2); everything after it is ignored. Not special in _SELR (its message ends at ',' ')' ';')", "E (parse), H (mode)"),
    CodeSpec("n", "none", "no-wait", "@n: script level - ends the message text of _ZM/_ZD/_MSAD (end mode 1: the next _MSAD continues the same message). Not special in _SELR", "E (parse), H (mode)"),
]}

_PLAIN_RUN = re.compile(rb"[^\x00$@^\[<|>]+")   # bytes that are never markup (fast path)
_BYTE_PARAM = frozenset(b"acfhw")
_NO_PARAM = frozenset(b"bgiklopr" b"tuzT")
_DIGITS = frozenset(b"0123456789")


def _dec(b: bytes) -> str:
    return b.decode("utf-8", "surrogateescape")


def tokenize(s: Union[str, bytes], lang: Union[str, int] = "ja", *, script: bool = False) -> list[Token]:
    """Split a string into markup tokens following the engine's layout pass 2.

    Works on the UTF-8 bytes of ``s`` like the engine: one-byte parameters,
    byte-counted windows (``@s`` 256, ``@X(...)`` 255).  Text that a parameter
    splits inside a multi-byte character is kept as undecodable bytes
    (surrogateescape) - never the case in the retail data.

    ``lang`` matters: ruby (<base|ruby>) is only parsed when the language is
    not English.  Inside a ruby group, codes/newlines/icons are tokenized too
    (Ruby.base_tokens / ruby_tokens).  With ``script=True`` the string is
    treated as a _ZM/_ZD/_MSAD message argument: ``$NNNNNN`` (6 digits)
    becomes :class:`Ref`, and ``@e`` / ``@n`` end the message (later text is
    returned as a final Text token after that Code token; the engine discards
    it)."""
    b = s.encode("utf-8") if isinstance(s, str) else bytes(s)
    ruby_on = ruby_enabled(lang)
    n = len(b)
    whole = _PLAIN_RUN.match(b)
    if whole and whole.end() == n:                   # fast path: no markup byte at all
        return [Text(_dec(b))]
    out: list[Token] = []
    buf = bytearray()
    state = 0                       # 0 normal, 1 ruby base, 2 ruby reading
    g_start = 0
    g_base: list[Token] = []
    g_ruby: list[Token] = []

    def target() -> list[Token]:
        return out if state == 0 else g_base if state == 1 else g_ruby

    def flush() -> None:
        if buf:
            t = _dec(bytes(buf))
            tgt = target()
            if tgt and isinstance(tgt[-1], Text):
                prev = tgt.pop()
                assert isinstance(prev, Text)
                tgt.append(Text(prev.text + t, prev.raw + t))
            else:
                tgt.append(Text(t))
            buf.clear()

    def emit(tok: Token) -> None:
        flush()
        target().append(tok)

    def close_group(end: int, closed: bool) -> None:
        nonlocal state
        flush()
        base = "".join(t.text for t in g_base if isinstance(t, Text))
        reading = "".join(t.text for t in g_ruby if isinstance(t, Text))
        out.append(Ruby(base, reading, _dec(b[g_start:end]), tuple(g_base), tuple(g_ruby), closed))
        g_base.clear()
        g_ruby.clear()
        state = 0

    i = 0
    while i < n:
        m_plain = _PLAIN_RUN.match(b, i)
        if m_plain:
            buf += m_plain.group(0)
            i = m_plain.end()
            continue
        c = b[i]
        if c == 0:
            break
        if script and c == 0x24 and i + 7 <= n and all(x in _DIGITS for x in b[i + 1:i + 7]):
            emit(Ref(int(b[i + 1:i + 7]), _dec(b[i:i + 7])))
            i += 7
            continue
        if c == 0x40:                                   # '@'
            if i + 1 >= n:
                # engine: '@' + NUL goes to the default handler, which skips 2
                # bytes and reads past the end of the string; stop here.
                emit(Code("", None, "@"))
                break
            k = b[i + 1]
            j = i + 2
            arg: Optional[str] = None
            if k in _BYTE_PARAM:
                arg = _dec(b[j:j + 1])
                j += 1
            elif k == 0x73:                             # 's'
                kk = j
                while kk < n and kk - j < 256 and b[kk] not in (0x2C, 0x29, 0):
                    kk += 1
                arg = _dec(b[j:kk])
                if kk < n and kk - j < 256:
                    j = kk + 1                          # terminator consumed
                else:
                    j = kk                              # 256-byte window or end of string
            elif k in (0x78, 0x79):                     # 'x', 'y'
                kk = j
                while kk < n and kk - j < 3 and b[kk] in _DIGITS:
                    kk += 1
                arg = _dec(b[j:kk]) or None
                j = kk
            elif k in (0x31, 0x32):                     # '1', '2'
                emit(Code(chr(k), None, _dec(b[i:i + 2])))
                if j < n:
                    target().append(Text("^" if k == 0x31 else ")", _dec(b[j:j + 1])))
                i = j + 1
                continue
            elif k in _NO_PARAM:
                pass
            elif j < n and b[j] == 0x28:                # default: optional "(...)" group
                window = b[j + 1:j + 1 + 255]
                close = -1
                for p, x in enumerate(window):
                    if x == 0:
                        break
                    if x == 0x29:
                        close = j + 1 + p
                        break
                if close != -1:
                    arg = _dec(b[j:close + 1])
                    j = close + 1
            code = chr(k) if k < 0x80 else _dec(bytes([k]))
            emit(Code(code, arg, _dec(b[i:j])))
            i = j
            if script and code in ("e", "n"):
                if state:
                    close_group(i, False)
                if i < n:
                    out.append(Text(_dec(b[i:])))
                break
            continue
        if c == 0x5E:                                   # '^'
            emit(Newline())
            i += 1
            continue
        if c == 0x5B:                                   # '['
            m = _ICON_RE.match(b, i)
            if m:
                emit(Icon(m.group(0)[1:-1].decode("ascii"), _ICON_INDEX[m.group(0)], _dec(m.group(0))))
                i = m.end()
                continue
        if ruby_on and c == 0x3C:                       # '<': base glyphs follow
            if state:
                close_group(i, False)
            else:
                flush()
            state, g_start = 1, i
            i += 1
            continue
        if ruby_on and c == 0x7C and state:             # '|'
            if state == 1:
                flush()
                state = 2                               # reading glyphs follow
            i += 1                                      # further '|' are swallowed
            continue
        if ruby_on and c == 0x3E:                       # '>'
            if state:
                close_group(i + 1, True)
            else:
                emit(Code(">", None, ">"))              # stray '>' is swallowed by the engine
            i += 1
            continue
        buf.append(c)
        i += 1
    if state:
        close_group(n, False)
    flush()
    return out


def iter_tokens(tokens: Iterable[Token]) -> Iterator[Token]:
    """All tokens, descending into ruby groups (group first, then its parts)."""
    for t in tokens:
        yield t
        if isinstance(t, Ruby):
            yield from iter_tokens(t.base_tokens)
            yield from iter_tokens(t.ruby_tokens)


def plain_text(s: Union[str, Sequence[Token]], lang: Union[str, int] = "ja", *,
               ruby: str = "drop", icons: str = "name") -> str:
    """Readable text: '^' -> newline, codes removed.  ``ruby``: 'drop' (base only),
    'paren' (base(ruby)) or 'markup' (keep <base|ruby>).  ``icons``: 'name'
    ([heart]) or 'drop'."""
    toks = tokenize(s, lang) if isinstance(s, str) else s
    parts: list[str] = []
    for t in toks:
        if isinstance(t, Text):
            parts.append(t.text)
        elif isinstance(t, Ruby):
            parts.append(t.base if ruby == "drop" else
                         f"{t.base}({t.ruby})" if ruby == "paren" else t.raw)
        elif isinstance(t, Newline):
            parts.append("\n")
        elif isinstance(t, Icon):
            if icons == "name":
                parts.append(t.raw)
        elif isinstance(t, Ref):
            parts.append(t.raw)
    return "".join(parts)


# ---------------------------------------------------------------------------
# styled runs (layout state machine, engine VA 0xC76C0 ff.)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Style:
    color: Optional[int] = None    # None = caller's default colour; else u16 colour word
    color_ext: int = 0             # @c6..@c9 value (glyph field +0x0A)
    style: int = 0                 # 0 normal, 1 g, 2 z, 3 italic, 4 g+italic, 5 z+italic
    flag_b: bool = False
    anim: int = 0
    nowrap: bool = False
    scale_w: Optional[float] = None   # multiplier of the base width (None = default)
    scale_h: Optional[float] = None
    font_metric: Optional[int] = None


@dataclass(frozen=True)
class Run:
    kind: str                      # text | ruby | icon | newline | wait | seq | pos | ref
    text: str = ""
    ruby: str = ""
    style: Style = field(default_factory=Style)
    value: Optional[int] = None    # icon index, ref id, seq op, x/y value
    seq: int = 0                   # typewriter sequence index of the first glyph


def glyph_flags(style: Style, *, ligature_piece: bool = False) -> int:
    """Glyph flag word the layout writes (VA 0xC790C..0xC7B39)."""
    f = 1 | (4 if style.style == 1 else 0)
    f |= {2: 0x100000, 3: 0x200000, 4: 0x400000, 5: 0x600000}.get(style.style, 0)
    if style.anim and not ligature_piece:
        f |= 8
    if style.flag_b:
        f |= 0x10000
    return f


def to_runs(tokens: Iterable[Token], lang: Union[str, int] = "ja") -> list[Run]:
    """Apply the control codes and return runs that carry their style.
    ``seq`` approximates the engine's per-glyph typewriter counter (starts at 1,
    +1 per glyph; ruby glyphs are not counted; @t sets it to 0).  Codes inside a
    ruby group take effect after the group's run."""
    english = lang_index(lang) == LANG_INDEX["en"]
    st = Style()
    seq, saved = 1, 1
    runs: list[Run] = []

    def code(t: Code) -> None:
        nonlocal st, seq, saved
        c, a = t.code, t.arg
        d = (ord(a) - 0x30) if a is not None and len(a) == 1 and a.isascii() else None
        if c == "c" and d is not None:
            d &= 0xFF                                   # engine: (byte - 0x30) as u8
            if d == 0:
                st = replace(st, color=None, color_ext=0)
            elif d <= 5:
                st = replace(st, color=COLOR_RGB565[d])
            elif d <= 9:
                st = replace(st, color_ext=d)
            # >= 10: ignored (VA 0xC8CD0)
        elif c == "g":
            st = replace(st, style=1)
        elif c == "z":
            st = replace(st, style=2)
        elif c == "i":
            st = replace(st, style={1: 4, 2: 5}.get(st.style, 3) if english else 1)
        elif c == "b":
            st = replace(st, flag_b=True)
        elif c == "a" and d is not None:
            st = replace(st, anim=d)
        elif c == "r":
            st = replace(st, style={4: 1, 5: 2}.get(st.style, 0), color=None, color_ext=0,
                         flag_b=False, anim=0, scale_w=None, scale_h=None)
        elif c == "o":
            st = replace(st, nowrap=True)
        elif c == "p":
            st = replace(st, nowrap=False)
        elif c == "h" and d is not None and 0 <= d < len(SCALE_TABLE):
            st = replace(st, scale_h=SCALE_TABLE[d])
        elif c == "w" and d is not None and 0 <= d < len(SCALE_TABLE):
            st = replace(st, scale_w=SCALE_TABLE[d])
        elif c == "f" and d is not None:
            st = replace(st, font_metric=d)
        elif c == "s" and a is not None:
            num = 0
            for ch in a:
                if "0" <= ch <= "9":
                    num = num * 10 + ord(ch) - 0x30
            v = (-num if "-" in a else num) / 64.0
            st = replace(st, scale_w=v, scale_h=v)
        elif c == "t":
            seq = 0
            runs.append(Run("seq", "t", style=st, value=0, seq=seq))
        elif c == "u":
            saved = seq
            runs.append(Run("seq", "u", style=st, value=seq, seq=seq))
        elif c == "l":
            seq = saved
            runs.append(Run("seq", "l", style=st, value=seq, seq=seq))
        elif c == "k":
            runs.append(Run("wait", "k", style=st, seq=seq))
        elif c in "xy":
            runs.append(Run("pos", c, style=st, value=int(a) if a else None, seq=seq))
        # e, n, T, '>' and unknown codes have no style effect

    for t in tokens:
        if isinstance(t, Text):
            runs.append(Run("text", t.text, style=st, seq=seq))
            seq += len(t.text)
        elif isinstance(t, Ruby):
            runs.append(Run("ruby", t.base, t.ruby, style=st, seq=seq))
            seq += len(t.base)
            for inner in iter_tokens(t.base_tokens + t.ruby_tokens):
                if isinstance(inner, Code):
                    code(inner)
        elif isinstance(t, Icon):
            runs.append(Run("icon", t.name, style=st, value=t.index, seq=seq))
            seq += 1
        elif isinstance(t, Newline):
            runs.append(Run("newline", style=st, seq=seq))
        elif isinstance(t, Ref):
            runs.append(Run("ref", t.raw, style=st, value=t.line_id, seq=seq))
        elif isinstance(t, Code):
            code(t)
    return runs


# ---------------------------------------------------------------------------
# census
# ---------------------------------------------------------------------------

_LITERAL_RE = re.compile(r"[<>|\[\]@^$%]")


def markup_census(lines: Sequence[str], lang: Union[str, int]) -> dict[str, dict[str, object]]:
    """Count every markup construct in ``lines``: {key: {count, lines, first_ids}}.
    Tokens inside ruby groups are counted too (key prefixed 'in ruby: ')."""
    stats: dict[str, dict[str, object]] = {}

    def add(key: str, i: int) -> None:
        e = stats.setdefault(key, {"count": 0, "lines": 0, "first_ids": [], "_last": -1})
        e["count"] = int(e["count"]) + 1  # type: ignore[call-overload]
        if e["_last"] != i:
            e["lines"] = int(e["lines"]) + 1  # type: ignore[call-overload]
            ids = e["first_ids"]
            assert isinstance(ids, list)
            if len(ids) < 5:
                ids.append(i)
            e["_last"] = i

    def visit(toks: Iterable[Token], i: int, prefix: str) -> None:
        for t in toks:
            if isinstance(t, Ruby):
                add(prefix + "ruby <base|ruby>", i)
                if not t.closed:
                    add(prefix + "ruby group not closed", i)
                visit(t.base_tokens, i, "in ruby: ")
                visit(t.ruby_tokens, i, "in ruby: ")
            elif isinstance(t, Newline):
                add(prefix + "^ (newline)", i)
            elif isinstance(t, Icon):
                add(prefix + f"[{t.name}] (icon {t.index})", i)
            elif isinstance(t, Code):
                key = f"@{t.code}{t.arg or ''}" if t.code != ">" else "> (stray, swallowed)"
                add(prefix + key, i)
            elif isinstance(t, Text):
                for ch in _LITERAL_RE.findall(t.text):
                    add(prefix + f"literal '{ch}'", i)

    for i, line in enumerate(lines):
        visit(tokenize(line, lang), i, "")
    for e in stats.values():
        e.pop("_last", None)
    return dict(sorted(stats.items()))


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _main(argv: Optional[list[str]] = None) -> int:
    import argparse
    import json
    import sys

    ap = argparse.ArgumentParser(prog="python -m tsuki.text",
                                 description="Read script_text.mrg (all languages)")
    ap.add_argument("--file", help="path to script_text.mrg (default: game folder)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("get", help="print line(s) by id in one or all languages")
    g.add_argument("ids", nargs="+", type=int)
    g.add_argument("--lang", default="all")
    g.add_argument("--tokens", action="store_true", help="also print the markup tokens")
    d = sub.add_parser("dump", help="write 'id<TAB>raw line' for a range")
    d.add_argument("lang")
    d.add_argument("--start", type=int, default=0)
    d.add_argument("--count", type=int, default=0, help="0 = all")
    d.add_argument("--plain", action="store_true", help="strip markup (ruby as base(ruby))")
    d.add_argument("-o", "--out")
    f = sub.add_parser("find", help="ids whose line contains TEXT")
    f.add_argument("lang")
    f.add_argument("text")
    sub.add_parser("info", help="counts and validation")
    c = sub.add_parser("census", help="markup census as JSON")
    c.add_argument("--lang", default="all")
    e = sub.add_parser("expand", help="expand a script message the way the engine does")
    e.add_argument("message")
    e.add_argument("--lang", default="en")
    args = ap.parse_args(argv)

    st = ScriptText(args.file)
    out = sys.stdout
    if args.cmd == "get":
        langs = LANGS if args.lang == "all" else (args.lang,)
        for i in args.ids:
            for lg in langs:
                line = st.get_line(lg, i)
                print(f"{i:06d} {lg}: {line}", file=out)
                if args.tokens:
                    for tok in tokenize(line, lg):
                        print(f"        {tok!r}", file=out)
    elif args.cmd == "dump":
        tab = st.table(args.lang)
        end = tab.count if args.count == 0 else min(tab.count, args.start + args.count)
        rows = []
        for i in range(args.start, end):
            line = tab.line(i)
            if args.plain:
                line = plain_text(line, args.lang, ruby="paren").replace("\n", "\\n")
            rows.append(f"{i:06d}\t{line}")
        text = "\n".join(rows) + "\n"
        if args.out:
            Path(args.out).write_text(text, encoding="utf-8", newline="\n")
        else:
            out.write(text)
    elif args.cmd == "find":
        for i in st.find(args.lang, args.text):
            print(f"{i:06d}\t{st.get_line(args.lang, i)}", file=out)
    elif args.cmd == "info":
        print(f"{st.path}: {st.count} lines per language", file=out)
        for lg in LANGS:
            tb = st.table(lg)
            empty = sum(1 for i in range(tb.count) if not tb.line_bytes(i))
            print(f"  {lg}: table entries {len(tb.offsets) + st.end_marks[lg]}, "
                  f"end marks {st.end_marks[lg]}, blob {len(tb.blob)} bytes, "
                  f"lines {tb.count}, empty {empty}", file=out)
        issues = st.validate()
        print(f"validation issues: {len(issues)}", file=out)
        for msg in issues:
            print("  " + msg, file=out)
    elif args.cmd == "census":
        langs = LANGS if args.lang == "all" else (args.lang,)
        res = {lg: markup_census(st.lines(lg), lg) for lg in langs}
        json.dump(res, out, ensure_ascii=False, indent=1)
        print(file=out)
    elif args.cmd == "expand":
        print(st.expand(args.message, args.lang), file=out)
    return 0


if __name__ == "__main__":
    import sys as _sys
    if hasattr(_sys.stdout, "reconfigure"):
        _sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    raise SystemExit(_main())
