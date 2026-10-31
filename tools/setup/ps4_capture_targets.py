"""Find where each PS4 capture checkpoint of docs/status/ps4_captures.md occurs in the scripts.

The capture list asks the user to screenshot or film effects on a real PS4.  This tool finds,
for every checkpoint id, the first statements in the retail scripts that show the effect, in
script order (the allscr order: chapter by chapter, ARC route before CIEL route).  It reads
allscr.mrg read-only and prints, per hit:

    script name and index, scene id, F023 (chapter: ui_text 358 + F023), F022 (flowchart
    scene: ui_text 396 + F022), statement index and byte offset, "message k of n" (the hit
    comes after the k-th `_ZM` of the script), the text ids of the last message before it,
    and the statement itself (only for commands whose arguments are numbers or asset names).

The default output (Markdown or JSON) holds numbers and engine identifiers only, so it may be
pasted into docs/status.  ``--show-text LANG`` adds, for the person at the console, the chapter
name, the flowchart scene title and the last message before each hit in that language.  That is
game text: it is for the local terminal only, never for documents or commits.

    PYTHONPATH=D:/TsukihimePort/tools .venv/Scripts/python.exe tools/setup/ps4_capture_targets.py
    ... ps4_capture_targets.py --ids E01,T04 --limit 3 --show-text ja

Excluded scripts: 00_02 (reached only through the debug menu JUMP00, RR 7.1), TEST_SCRIPT and
JUMP*.  The extras (QA_*, QACALL, ARCHIVE, STAFFROLL) are searched after the story scripts.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from typing import Callable, Optional

from tsuki.script import AllScr, Command

EXCLUDED = re.compile(r"^(00_02|TEST_SCRIPT|JUMP\w*)$")
EXTRAS = re.compile(r"^(QA_\d+|QACALL|ARCHIVE|STAFFROLL)$")
# Commands whose raw statement is safe to print (numbers, picture / SE names, F indices).
PRINTABLE = {
    "FADS", "FADE", "STBR", "STEC", "SNX2", "SNX3", "SQK2", "SHK2", "SSW2", "SHOK", "MSQK", "SHAF",
    "STCH", "STTR", "STCF", "STNS", "STRA", "SPFC", "SPRD", "SPRC", "WKST", "FCSP", "FCLN", "STMA",
    "VPLY", "MPLY",
}
TEXT_COMMANDS = {"ZM", "ZD", "MSAD", "SELR"}
F_SET = re.compile(r"^F0*(\d+),eq,(-?\d+)$")


@dataclass
class Ctx:
    """Running state of one script while it is scanned in statement order."""
    f: dict[int, int] = field(default_factory=dict)  # literal F stores seen so far (WKST(Fn,eq,v))
    shown: bool = False          # a message is on screen (since the last _ZM, until _CLO2/3/4)
    msgs_on_page: int = 0        # _ZM statements since the last _PGST
    zm_before: int = 0           # _ZM statements before the current one
    last_text: tuple[int, ...] = ()

    def fv(self, n: int) -> int:
        return self.f.get(n, 0)


Pred = Callable[[Command, Ctx], bool]


@dataclass(frozen=True)
class Target:
    tid: str
    what: str
    pred: Pred
    extras_too: bool = False     # also accept hits in the extras when the story has fewer


def _fads(t: int, rule: Optional[int] = None, min_ms: int = 0) -> Pred:
    def p(c: Command, x: Ctx) -> bool:
        if c.key != "FADS" or c.value(0) != t:
            return False
        ms = c.value(1) or 0
        if ms < min_ms:
            return False
        return rule is None or c.value(2) == rule
    return p


def _stbr(mode: int, flag: bool) -> Pred:
    def p(c: Command, x: Ctx) -> bool:
        return c.key == "STBR" and c.value(1) == mode and ((c.value(3) == 1) == flag)
    return p


def _key(key: str, arg0: Optional[int] = None, with_args: Optional[bool] = None) -> Pred:
    def p(c: Command, x: Ctx) -> bool:
        if c.key != key:
            return False
        if arg0 is not None and c.value(0) != arg0:
            return False
        if with_args is not None and c.obj_has(1) != with_args:
            return False
        return True
    return p


def _stec(ident: int) -> Pred:
    return lambda c, x: c.key == "STEC" and c.value(1) == ident


def _crop(a17: int) -> Pred:
    return lambda c, x: c.key == "STCH" and c.obj_has(14) and c.expr_arg(17) == a17


def _zm_code(code: str) -> Pred:
    return lambda c, x: c.name == "ZM" and code in c.raw


def _zm_ids(lo: int, hi: int, only: Optional[set[int]] = None) -> Pred:
    def p(c: Command, x: Ctx) -> bool:
        if c.name != "ZM":
            return False
        return any(lo <= t <= hi and (only is None or t in only) for t in c.text_ids)
    return p


def _window_on_page(n: int, f1104: int) -> Pred:
    """The n-th message of a page while the window picture is drawn (F1104 = 0) or hidden (1)."""
    return lambda c, x: c.name == "ZM" and x.msgs_on_page == n - 1 and x.fv(1104) == f1104 and x.fv(1105) <= 2


def _text_on_screen_fads(t: int, min_ms: int) -> Pred:
    base = _fads(t, None, min_ms)
    return lambda c, x: base(c, x) and x.shown and x.msgs_on_page > 0 and x.fv(1104) == 0


def _fads_window_arg(v: int) -> Pred:
    return lambda c, x: c.key == "FADS" and c.obj_has(3) and c.value(3) == v and x.shown and x.msgs_on_page > 0


def _f_literal(c: Command) -> Optional[tuple[int, int]]:
    """(n, v) for a literal store ``_WKST(Fn,eq,v)``, else None."""
    if c.key != "WKST" or not c.args:
        return None
    m = F_SET.match(",".join(a.raw for a in c.args))
    return (int(m.group(1)), int(m.group(2))) if m else None


def _f_store(n: int, lo: int, hi: int) -> Pred:
    def p(c: Command, x: Ctx) -> bool:
        s = _f_literal(c)
        return s is not None and s[0] == n and lo <= s[1] <= hi
    return p


TARGETS: tuple[Target, ...] = (
    # --- M4 effect checkpoints -------------------------------------------------------------
    Target("E01", "first rule wipe _FADS(2,ms,rule)", _fads(2)),
    Target("E02.26", "rule wipe with RULE_26 (most used)", _fads(2, 26)),
    Target("E02.27", "rule wipe with RULE_27", _fads(2, 27)),
    Target("E02.17", "rule wipe with RULE_17", _fads(2, 17)),
    Target("E02.28", "rule wipe with RULE_28", _fads(2, 28)),
    Target("E03", "cross-fade _FADS(0, >=500 ms) while a message is on screen", _text_on_screen_fads(0, 500)),
    Target("E04.1", "_FADS with window argument 1 while a message is on screen", _fads_window_arg(1)),
    Target("E04.2", "_FADS with window argument 2 while a message is on screen", _fads_window_arg(2)),
    Target("E05", "_STBR mode 17 (ps_dodge5), no flag", _stbr(17, False)),
    Target("E05f", "_STBR mode 17 with flag (4th argument 1)", _stbr(17, True)),
    Target("E06", "_STBR mode 5 (nexl dodge), no flag", _stbr(5, False)),
    Target("E06f", "_STBR mode 5 with flag", _stbr(5, True)),
    Target("E07.7f", "_STBR mode 7 (multiplicative) with flag", _stbr(7, True)),
    Target("E07.9f", "_STBR mode 9 (screen) with flag", _stbr(9, True)),
    Target("E07.10f", "_STBR mode 10 (sub) with flag", _stbr(10, True)),
    Target("E08.11", "_STBR mode 11 (opening)", _stbr(11, False)),
    Target("E08.19", "_STBR mode 19 (opening)", _stbr(19, False)),
    Target("E08.22", "_STBR mode 22 (opening)", _stbr(22, False)),
    Target("E08.23", "_STBR mode 23 (opening)", _stbr(23, False)),
    Target("E08.24", "_STBR mode 24 (opening)", _stbr(24, False)),
    Target("E09.1", "_STEC 1 (solid colour silhouette)", _stec(1)),
    Target("E09.2", "_STEC 2 (grey, swapped luma weights)", _stec(2)),
    Target("E09.3", "_STEC 3 (sepia)", _stec(3)),
    Target("E09.4", "_STEC 4 (negative)", _stec(4)),
    Target("E09.77", "_STEC 77 (grey, BT.709 weights)", _stec(77)),
    Target("E09.44", "_STEC 44 (colour grade, most used)", _stec(44)),
    Target("E10.2", "_SNX2 brightness", _key("SNX2")),
    Target("E10.3", "_SNX3 contrast", _key("SNX3")),
    Target("E11", "_SQK2 layer quake", _key("SQK2")),
    Target("E12", "_SHK2 layer shake", _key("SHK2")),
    Target("E13", "_SSW2 layer swing", _key("SSW2")),
    Target("E14.SHOK", "_SHOK screen quake", _key("SHOK")),
    Target("E14.MSQK", "_MSQK message-window quake", _key("MSQK"), extras_too=True),
    Target("E15", "_FADE(40) screen effect 4, default angle", _key("FADE", 40, with_args=False)),
    Target("E15a", "_FADE(40,angle,preset)", _key("FADE", 40, with_args=True)),
    Target("E16.0", "crop / mask layer, edge style a17 = 0", _crop(0)),
    Target("E16.1", "crop layer, a17 = 1", _crop(1)),
    Target("E16.2", "crop layer, a17 = 2", _crop(2)),
    Target("E16.7", "crop layer, a17 = 7", _crop(7)),
    Target("E16.6", "crop layer, a17 = 6 (single use)", _crop(6)),
    Target("E17", "_STTR per-layer rule", _key("STTR")),
    Target("E18", "_STCF (reader not traced)", _key("STCF")),
    Target("E19.STNS", "_STNS noise", _key("STNS")),
    Target("E19.STRA", "_STRA (no reader found)", _key("STRA")),
    Target("E20", "_SPFC special picture", _key("SPFC")),
    # --- text and window checkpoints -------------------------------------------------------
    Target("T01", "@g style run (text ids 4478..4620)", _zm_ids(4478, 4620)),
    Target("T02", "@g@b (text ids 4527, 4529, 4531, 4533)", _zm_ids(4527, 4533, {4527, 4529, 4531, 4533})),
    Target("T03.c1", "@c1 colour word in a message", _zm_code("@c1")),
    Target("T03.c2", "@c2", _zm_code("@c2")),
    Target("T03.c3", "@c3", _zm_code("@c3")),
    Target("T03.c4", "@c4", _zm_code("@c4")),
    Target("T03.c7", "@c7 (extension value)", _zm_code("@c7")),
    Target("T04", "4th message of a page, window picture drawn (F1104 = 0)", _window_on_page(4, 0)),
    Target("T05", "4th message of a page, window picture hidden (F1104 = 1)", _window_on_page(4, 1)),
    Target("T06", "speech-bubble window (F1105 = 3..32)", _f_store(1105, 3, 32), extras_too=True),
    Target("T07", "dark overlay window 1 or 2 (F1105 = 1..2)", _f_store(1105, 1, 2)),
    Target("T08.FCLN", "_FCLN full-screen narration layout", _key("FCLN")),
    Target("T08.FCSP", "_FCSP", _key("FCSP")),
    Target("T09", "@t simultaneous text (ids 9328, 23613, 36678)", _zm_ids(9328, 36678, {9328, 23613, 36678})),
    Target("T10", "first story choice menu _SELD", _key("SELD")),
    # --- sound ------------------------------------------------------------------------------
    Target("A01", "first voiced line _VPLY (voice over BGM, ducking)", _key("VPLY")),
    # --- prompts (M7) ----------------------------------------------------------------------
    Target("M04", "_SELA prompt", _key("SELA")),
    Target("M05", "_SELS prompt", _key("SELS"), extras_too=True),
)


@dataclass
class Hit:
    tid: str
    script: int
    name: str
    scene: int
    f023: int
    f022: int
    stmt: int
    offset: int
    msg: int
    msgs: int
    last_text: tuple[int, ...]
    statement: str


def _statement(c: Command) -> str:
    if c.name in TEXT_COMMANDS or c.key in {"SELD", "SELA", "SELS"}:
        ids = " ".join(f"${t:06d}" for t in c.text_ids)
        return f"_{c.name}" + (f" [{ids}]" if ids else "")
    if c.key in PRINTABLE:
        return c.raw.strip()[:110]
    return f"_{c.key}"


def _script_order(a: AllScr) -> list[int]:
    story = [i for i, n in enumerate(a.names) if not EXCLUDED.match(n) and not EXTRAS.match(n)]
    extras = [i for i, n in enumerate(a.names) if EXTRAS.match(n)]
    return story + extras


def _update(c: Command, x: Ctx) -> None:
    s = _f_literal(c)
    if s is not None:
        x.f[s[0]] = s[1]
    if c.name == "ZM":
        x.zm_before += 1
        x.msgs_on_page += 1
        x.shown = True
        x.last_text = tuple(c.text_ids)
    elif c.name == "MSAD":
        x.last_text = x.last_text + tuple(c.text_ids)
    elif c.name == "PGST":
        x.msgs_on_page = 0
    elif c.key in ("CLO2", "CLO3", "CLO4"):
        x.shown = False


def find(limit: int, ids: Optional[set[str]]) -> tuple[list[Hit], dict[str, int]]:
    targets = [t for t in TARGETS if ids is None or t.tid in ids]
    hits: dict[str, list[Hit]] = {t.tid: [] for t in targets}
    totals: dict[str, int] = {t.tid: 0 for t in targets}
    with AllScr() as a:
        for i in _script_order(a):
            name = a.names[i]
            extra = bool(EXTRAS.match(name))
            cmds = a.commands(i)
            n_zm = sum(1 for c in cmds if c.name == "ZM")
            x = Ctx()
            first: dict[int, int] = {}
            for c in cmds:
                s = _f_literal(c)
                if s is not None and s[0] in (22, 23):
                    first.setdefault(s[0], s[1])
            f022, f023 = first.get(22, -1), first.get(23, -1)
            for c in cmds:
                for t in targets:
                    if not t.pred(c, x):
                        continue
                    totals[t.tid] += 1
                    lst = hits[t.tid]
                    story_hits = sum(1 for h in lst if not EXTRAS.match(h.name))
                    if len(lst) < limit and (not extra or t.extras_too or story_hits == 0):
                        lst.append(Hit(t.tid, i, name, a.scene_id(i), f023, f022, c.index, c.offset,
                                       x.zm_before, n_zm, x.last_text, _statement(c)))
                _update(c, x)
    out = [h for t in targets for h in hits[t.tid]]
    return out, totals


def _markdown(hits: list[Hit], totals: dict[str, int]) -> str:
    what = {t.tid: t.what for t in TARGETS}
    lines = [
        "| id | checkpoint | uses | script (index) | scene | F023 | F022 | stmt | offset | after message | last text ids | statement |",
        "|---|---|---:|---|---:|---:|---:|---:|---|---|---|---|",
    ]
    seen: set[str] = set()
    for h in hits:
        first = h.tid not in seen
        seen.add(h.tid)
        ids = " ".join(f"${t:06d}" for t in h.last_text) or "-"
        stmt = h.statement.replace("|", "\\|")
        lines.append(
            f"| {h.tid if first else ''} | {what[h.tid] if first else ''} | {totals[h.tid] if first else ''} "
            f"| {h.name} ({h.script}) | {h.scene} | {h.f023} | {h.f022} | {h.stmt} | 0x{h.offset:x} "
            f"| {h.msg} of {h.msgs} | {ids} | `{stmt}` |")
    for tid, n in totals.items():
        if tid not in seen:
            lines.append(f"| {tid} | {what[tid]} | {n} | (no retail occurrence) | | | | | | | | |")
    return "\n".join(lines)


def _show_text(hits: list[Hit], lang: str) -> None:
    from eboot import eboot_path
    from eboot.selfelf import Eboot
    from eboot.uitext import read_table
    from tsuki.text import ScriptText, plain_text

    p = eboot_path()
    if p is None:
        raise SystemExit("eboot.bin not found (set TSUKI_GAME_DIR)")
    ui = read_table(Eboot.open(p))[lang]
    st = ScriptText()
    print(f"# LOCAL ONLY: game text in '{lang}' for finding the spots on the PS4. "
          "Do not paste it into documents, commits or chats.", file=sys.stderr)
    for h in hits:
        chapter = ui[358 + h.f023].text if 0 < h.f023 and 358 + h.f023 < len(ui) else "?"
        scene = ui[396 + h.f022].text if 0 < h.f022 and 396 + h.f022 < len(ui) else "?"
        msg = " / ".join(plain_text(st.get_line(lang, t), lang) for t in h.last_text) or "(no message yet)"
        print(f"{h.tid:9} {h.name}: chapter '{chapter}', scene '{scene}', message {h.msg} of {h.msgs}; "
              f"last line before it: {msg}")


def main(argv: Optional[list[str]] = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--limit", type=int, default=2, help="hits per checkpoint (default 2)")
    ap.add_argument("--ids", help="comma-separated checkpoint ids (default: all)")
    ap.add_argument("--json", action="store_true", help="JSON instead of Markdown")
    ap.add_argument("--show-text", metavar="LANG", choices=("ja", "en", "zc", "zt"),
                    help="also print chapter, scene title and the last line before each hit (LOCAL ONLY)")
    args = ap.parse_args(argv)
    ids = set(args.ids.split(",")) if args.ids else None
    if ids is not None:
        unknown = ids - {t.tid for t in TARGETS}
        if unknown:
            ap.error(f"unknown ids: {', '.join(sorted(unknown))}")
    hits, totals = find(args.limit, ids)
    if args.json:
        print(json.dumps({"totals": totals, "hits": [h.__dict__ for h in hits]}, indent=1))
    else:
        print(_markdown(hits, totals))
    if args.show_text:
        _show_text(hits, args.show_text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
