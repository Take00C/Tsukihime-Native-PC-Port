"""How allscr.mrg scripts reference script_text lines (text side only).

This is a light, regex-level scan of the decompressed scripts (the full
bytecode belongs to tsuki.script).  A script is a sequence of ``;``-separated
commands ``_NAME(args)``.  The commands that carry message text are::

    _ZM<5 hex>(<message>)            show a message                   (42,576 with refs)
    _MSAD(<message>)                 add text to the current message  (617 with refs)
    _SELR(<n>,<message>[,<locked>])  define a choice                  (206 with refs)
    _ZD<5 hex>(<message>)            disabled message: never shown    (17 with refs)

``<message>`` is markup text (see tsuki.text) in which ``$NNNNNN`` (exactly 6
decimal digits) is replaced by line NNNNNN of the current language
(engine VA 0x76040).

* _ZM/_ZD/_MSAD: the message is everything after '(' up to ``)``, the first
  ``@e`` / ``@n``, or 0x300 bytes (copy loop at VA 0x92458..0x924B0).  _ZM
  starts a new raw message buffer (VA 0x16537E0); _MSAD (VA 0x92B60) appends
  to it (total <= 0x300 bytes) and the whole buffer is expanded again.
* _ZD is a marker of kind 'D': the marker dispatcher (VA 0x98479) only acts on
  kinds 'M' and 'Y', so a _ZD message is never displayed.
* _SELR (handler VA 0x9936D): argument 1 is read with the generic string
  getter VA 0x76EA0, which stops at ``,`` ``)`` or ``;`` (and skips a 5-byte
  prefix starting with '`').  ``@e``/``@n`` are not special.  Argument 2 == 1
  sets a per-choice byte that the menu cursor code skips (VA 0xA6360 ff.).

Scripts are allscr.mrg sections 3.. (inverted MZX, see docs/formats/containers.md);
section 0 holds the script names.
"""
from __future__ import annotations

import mmap
import re
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Iterator, Optional, Union

from . import image_dir
from .mzp import MzpArchive
from .mzx import decompress
from .nam import parse_nam32
from .text import (CHOICE_NUMBER_OVERFLOW, LANGS, MESSAGE_ARG_MAX, Code,
                   ScriptText, choice_slot_text, plain_text, tokenize)

__all__ = ["TextCommand", "load_scripts", "iter_text_commands", "ref_index", "script_census",
           "scene_transcript", "voice_text_counts", "classify_unreferenced", "message_groups",
           "zd_only_ids", "IMAGE_TEXT_IDS", "UNREFERENCED_CLASSES"]

TEXT_CMD_RE = re.compile(r"^_(?:(ZM|ZD)([0-9a-fA-F]{5})|(MSAD)|(SELR))\((.*)\)\s*$", re.S)
VOICE_CMD_RE = re.compile(r"^_(VPLY|VPL2)\(")
REF_RE = re.compile(r"\$(\d{6})")
_END_RE = re.compile(r"@[en]")

# Lines that the opening script 00_02 shows as pre-rendered images instead of
# text commands: allpac0 IMGxxxx.MZP, four bands ja / en / zc / zt, put on
# screen with _STCH/_SCH2 + _STMA and voiced with _VPL2.  Verified by
# rendering every image (docs/formats/text.md 5.7).
IMAGE_TEXT_IDS: dict[int, str] = {
    12: "IMG2629", 13: "IMG2630", 14: "IMG2631", 15: "IMG2632", 16: "IMG2633",
    59: "IMG2634", 63: "IMG2635", 68: "IMG2636", 82: "IMG2459", 87: "IMG2460", 93: "IMG2461",
}
UNREFERENCED_CLASSES = ("choice-number", "image-text", "blank", "merged-fragment", "not-shown")


@dataclass(frozen=True)
class TextCommand:
    script: int          # script index k (allscr section k+3)
    name: str            # script name from allscr section 0
    pos: int             # command index inside the script
    op: str              # ZM, ZD, MSAD, SELR
    label: str           # the 5 hex chars after ZM/ZD ("" otherwise)
    arg: str             # ZM/ZD/MSAD: full argument text; SELR: argument 1 only
    choice: Optional[int] = None        # SELR argument 0 (n)
    extra: tuple[str, ...] = ()         # SELR arguments 2.. (as written)

    @property
    def message(self) -> str:
        """The part of the argument the engine keeps: _ZM/_ZD/_MSAD cut at the
        first @e / @n and at 0x300 bytes; _SELR argument 1 as is."""
        if self.op == "SELR":
            return self.arg
        m = _END_RE.search(self.arg)
        msg = self.arg[:m.start()] if m else self.arg
        raw = msg.encode("utf-8")
        if len(raw) > MESSAGE_ARG_MAX:
            msg = raw[:MESSAGE_ARG_MAX].decode("utf-8", "ignore")
        return msg

    @property
    def end_code(self) -> str:
        """'e' / 'n' / '' (closing parenthesis); always '' for _SELR."""
        if self.op == "SELR":
            return ""
        m = _END_RE.search(self.arg)
        return m.group(0)[1] if m else ""

    @property
    def end_mode(self) -> Optional[int]:
        """Engine end mode: 0 ')', 1 '@n', 2 '@e'; None for _SELR."""
        if self.op == "SELR":
            return None
        return {"": 0, "n": 1, "e": 2}[self.end_code]

    @property
    def displayed(self) -> bool:
        """False for _ZD (disabled message, VA 0x98479)."""
        return self.op != "ZD"

    @property
    def locked(self) -> bool:
        """_SELR third argument == 1 (choice the menu cursor skips) [E-partial / H]."""
        return self.op == "SELR" and bool(self.extra) and self.extra[0].strip() == "1"

    @property
    def refs(self) -> list[int]:
        return [int(x) for x in REF_RE.findall(self.message)]

    def choice_slot(self, slot: int) -> str:
        """_SELR: the slot string the engine builds for menu position ``slot``."""
        if self.op != "SELR":
            raise ValueError("not a _SELR command")
        return choice_slot_text(slot, self.message)


def load_scripts(path: Union[str, Path, None] = None) -> list[tuple[str, str]]:
    """[(name, decompressed script text)] for every script in allscr.mrg."""
    if path is None:
        d = image_dir()
        if d is None:
            raise FileNotFoundError("game folder not found (set TSUKI_GAME_DIR)")
        path = d / "allscr.mrg"
    out: list[tuple[str, str]] = []
    with open(path, "rb") as f:
        mm = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        try:
            arc = MzpArchive(mm)
            names = parse_nam32(arc.read(0)).names
            for k, name in enumerate(names):
                raw = decompress(arc.read(k + 3), invert=True)
                out.append((name, raw.decode("utf-8")))
        finally:
            mm.close()
    return out


def _selr_args(arg: str) -> list[str]:
    """Arguments of _SELR as the generic getters (VA 0x76EA0 / 0x76690) see them."""
    parts = arg.split(",")
    if len(parts) > 1 and parts[1].startswith("`"):
        parts[1] = parts[1][5:]
    return parts


def iter_text_commands(scripts: list[tuple[str, str]]) -> Iterator[TextCommand]:
    for k, (name, src) in enumerate(scripts):
        for pos, cmd in enumerate(src.split(";")):
            m = TEXT_CMD_RE.match(cmd.strip())
            if not m:
                continue
            op = m.group(1) or m.group(3) or m.group(4)
            arg = m.group(5)
            choice: Optional[int] = None
            extra: tuple[str, ...] = ()
            if op == "SELR":
                parts = _selr_args(arg)
                head = parts[0].strip()
                choice = int(head) if head.lstrip("-").isdigit() else None
                arg = parts[1] if len(parts) > 1 else ""
                extra = tuple(parts[2:])
            yield TextCommand(k, name, pos, op, m.group(2) or "", arg, choice, extra)


def ref_index(cmds: Iterable[TextCommand]) -> dict[int, list[TextCommand]]:
    idx: dict[int, list[TextCommand]] = {}
    for c in cmds:
        for r in c.refs:
            idx.setdefault(r, []).append(c)
    return idx


def message_groups(cmds: Iterable[TextCommand]) -> list[list[TextCommand]]:
    """Displayed messages as the engine's raw buffer sees them: a _ZM followed by
    the _MSAD commands that append to it (until the next _ZM/_ZD, same script).
    _SELR does not touch the buffer; a _ZD starts no group (never displayed)."""
    groups: list[list[TextCommand]] = []
    cur: Optional[list[TextCommand]] = None
    for c in cmds:
        if c.op == "SELR":
            continue
        if c.op == "ZD":
            cur = None
        elif c.op == "ZM" or cur is None or cur[0].script != c.script:
            cur = [c]
            groups.append(cur)
        else:
            cur.append(c)
    return groups


def zd_only_ids(cmds: Iterable[TextCommand]) -> list[int]:
    """Line ids referenced only by disabled _ZD messages (so never displayed)."""
    idx = ref_index(cmds)
    return sorted(i for i, v in idx.items() if all(c.op == "ZD" for c in v))


def script_census(cmds: list[TextCommand]) -> dict[str, Counter[str]]:
    """Counts of opcodes, argument shapes and markup codes in message arguments."""
    ops: Counter[str] = Counter()
    shapes: Counter[str] = Counter()
    codes: Counter[str] = Counter()
    for c in cmds:
        ops[c.op] += 1
        if c.op == "SELR":
            tail = "".join("," + x for x in c.extra)
            body = REF_RE.sub("$N", c.arg) if c.refs else "<label or inline text>"
            shapes[f"SELR(<n>,{body}{tail})"] += 1
        elif c.refs:
            shapes[f"{c.op}(" + REF_RE.sub("$N", c.arg) + ")"] += 1
        else:
            shapes[f"{c.op}(<inline text>)"] += 1
        for t in tokenize(c.arg, "ja", script=c.op != "SELR"):
            if isinstance(t, Code):
                codes["@" + t.code + (t.arg or "")] += 1
    return {"ops": ops, "shapes": shapes, "codes": codes}


def scene_transcript(st: ScriptText, scripts: list[tuple[str, str]], name: str, lang: str) -> list[str]:
    """Readable transcript of one script: every text command, expanded.
    Choices are shown without the engine's number prefix (it depends on which
    _SELR commands the _IF__ branches execute; see TextCommand.choice_slot)."""
    out: list[str] = []
    for c in iter_text_commands([s for s in scripts if s[0] == name]):
        text = st.expand(c.message, lang)
        shown = plain_text(text, lang, ruby="paren").replace("\n", " / ")
        if c.op == "SELR":
            prefix = f"> choice {c.choice}{' (locked)' if c.locked else ''}: "
        else:
            prefix = {"ZM": "", "ZD": "[ZD] ", "MSAD": "+ "}[c.op]
        ids = ",".join(f"{r:06d}" for r in c.refs)
        out.append(f"{ids or '------'}\t{prefix}{shown}")
    return out


def voice_text_counts(scripts: list[tuple[str, str]]) -> dict[str, Counter[int]]:
    """For every _VPLY / _VPL2: how many text commands follow it before the next
    voice command (or the end of the script).  {"VPLY": Counter, "VPL2": Counter}."""
    res: dict[str, Counter[int]] = {"VPLY": Counter(), "VPL2": Counter()}
    for _, src in scripts:
        cur: Optional[str] = None
        n = 0
        for cmd in src.split(";"):
            c = cmd.strip()
            v = VOICE_CMD_RE.match(c)
            if v:
                if cur is not None:
                    res[cur][n] += 1
                cur, n = v.group(1), 0
            elif TEXT_CMD_RE.match(c):
                n += 1
        if cur is not None:
            res[cur][n] += 1
    return res


def classify_unreferenced(st: ScriptText, cmds: Iterable[TextCommand]) -> dict[int, str]:
    """Class of every line id that no text command references:

    * choice-number   43959..43968, built by the choice handler (text.choice_number_id)
    * image-text      shown as a pre-rendered image in 00_02 (IMAGE_TEXT_IDS)
    * blank           ja line '■' or blank (en/zc/zt empty or blank)
    * merged-fragment the ja text is repeated at the end of the previous line or
                      the start of the next one (all 15 English '<->' lines)
    * not-shown       anything else (cut or duplicated passages) [D]"""
    used = ref_index(cmds)
    ja = st.table("ja")
    res: dict[int, str] = {}
    for i in range(st.count):
        if i in used:
            continue
        s = ja.line(i).strip("　 ")
        prev = ja.line(i - 1).strip("　 ") if i > 0 else ""
        nxt = ja.line(i + 1).strip("　 ") if i + 1 < st.count else ""
        if i >= CHOICE_NUMBER_OVERFLOW:
            res[i] = "choice-number"
        elif i in IMAGE_TEXT_IDS:
            res[i] = "image-text"
        elif s in ("", "■"):
            res[i] = "blank"
        elif prev.endswith(s) or nxt.startswith(s):
            res[i] = "merged-fragment"
        else:
            res[i] = "not-shown"
    return res


def _main(argv: Optional[list[str]] = None) -> int:
    import argparse

    ap = argparse.ArgumentParser(prog="python -m tsuki.text_refs",
                                 description="Text references in allscr.mrg scripts")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("summary", help="coverage of script_text ids and markup counts")
    sc = sub.add_parser("scene", help="readable transcript of one script")
    sc.add_argument("name")
    sc.add_argument("--lang", default="en", choices=LANGS)
    w = sub.add_parser("where", help="which commands reference a line id")
    w.add_argument("ids", nargs="+", type=int)
    args = ap.parse_args(argv)

    scripts = load_scripts()
    st = ScriptText()
    cmds = list(iter_text_commands(scripts))
    if args.cmd == "summary":
        idx = ref_index(cmds)
        refs = sum(len(c.refs) for c in cmds)
        unref = classify_unreferenced(st, cmds)
        bad = [i for i in idx if i >= st.count]
        print(f"scripts {len(scripts)}, text commands {len(cmds)}, $-refs {refs}, "
              f"distinct ids {len(idx)}, ids >= {st.count}: {len(bad)}")
        print(f"unreferenced ids ({len(unref)}): {dict(Counter(unref.values()))}")
        for cls in UNREFERENCED_CLASSES:
            print(f"  {cls}: {[i for i, v in unref.items() if v == cls]}")
        print(f"ids referenced more than once: {sorted(i for i, v in idx.items() if len(v) > 1)}")
        print(f"ids referenced only by disabled _ZD messages: {zd_only_ids(cmds)}")
        groups = message_groups(cmds)
        longest = {lg: max(len(st.expand_bytes("".join(c.message for c in g), lg)) for g in groups)
                   for lg in LANGS[:4]}
        print(f"displayed messages (_ZM + appended _MSAD): {len(groups)}, longest expanded: {longest}")
        selr = [c for c in cmds if c.op == "SELR"]
        print(f"_SELR: {len(selr)}, with a 3rd argument: {sum(1 for c in selr if c.extra)} "
              f"(locked: {sum(1 for c in selr if c.locked)})")
        cen = script_census(cmds)
        for key in ("ops", "codes"):
            print(f"{key}: {dict(cen[key].most_common())}")
        vt = voice_text_counts(scripts)
        for op, cnt in vt.items():
            print(f"text commands after each _{op} (before the next voice command): "
                  f"{dict(sorted(cnt.items()))}")
        print("argument shapes:")
        for s, n in cen["shapes"].most_common(40):
            print(f"  {n:7d} {s}")
    elif args.cmd == "scene":
        for row in scene_transcript(st, scripts, args.name, args.lang):
            print(row)
    elif args.cmd == "where":
        idx = ref_index(cmds)
        unref = classify_unreferenced(st, cmds)
        for i in args.ids:
            for c in idx.get(i, []):
                print(f"{i:06d}\t{c.name}\t#{c.pos}\t{c.op}{c.label}({c.arg})")
            if i not in idx:
                print(f"{i:06d}\t(not referenced by any script: {unref.get(i, '?')})")
    return 0


if __name__ == "__main__":
    import sys as _sys
    if hasattr(_sys.stdout, "reconfigure"):
        _sys.stdout.reconfigure(encoding="utf-8")  # type: ignore[union-attr]
    raise SystemExit(_main())
