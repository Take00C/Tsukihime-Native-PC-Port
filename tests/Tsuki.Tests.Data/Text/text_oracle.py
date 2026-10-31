"""Thin caller of the Python oracle for package A3a: script_text.mrg, the markup tokenizer and text_refs.

Everything is printed as JSON lines on stdout; nothing is written to disk. The C# tests
(tests/Tsuki.Tests.Data/Text/*.cs) run this script once per test process and compare record by record with
``Tsuki.Data.Text``. Hashes are the first 8 bytes of SHA-1 over a canonical text (16 hex digits); all byte strings are lower-case hex.

    python -I tests/Tsuki.Tests.Data/Text/text_oracle.py lines|fuzz|refs|rules

``lines``  per language: table facts, SHA-256 of the offset table and the blob, and for every id one 16-hex hash of each of
           ``engine`` (TextTable.engine_line), ``tok`` / ``tokscript`` (tokenize), ``runs`` (to_runs) and ``plain`` (plain_text in four
           variants), plus the markup census.
``fuzz``   seeded random ``expand_refs`` and ``tokenize`` / ``to_runs`` / ``plain_text`` cases.
``refs``   text_refs over the 549 scripts: per-script command hashes, ref index, message groups, ZD-only ids, census, voice counts,
           the classes of the unreferenced ids and a hash of every scene transcript.
``rules``  the small pure rules (language mapping, choice slots) over a grid of inputs.

Canonical forms (the C# test helper ``OracleCanon`` writes exactly the same text):

token   ``T:<text>:<raw>`` | ``R:<base>:<reading>:<raw>:<closed>:[<tokens>]:[<tokens>]`` | ``N:<raw>`` | ``I:<name>:<index>:<raw>`` |
        ``C:<code>:<arg or ->:<raw>`` | ``F:<id>:<raw>``   (byte strings as hex, tokens of a list joined by ``|``, inside a ruby by ``,``)
run     ``U:<kind>:<text>:<ruby>:<color>:<color_ext>:<style>:<flag_b>:<anim>:<nowrap>:<scale_w>:<scale_h>:<font_metric>:<value>:<seq>``
        (None is ``-``, floats are the 16 hex digits of the IEEE-754 double)
"""
from __future__ import annotations

import hashlib
import json
import random
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import text as T  # noqa: E402
from tsuki import text_refs as R  # noqa: E402


def h16(s: str) -> str:
    return hashlib.sha1(s.encode("utf-8", "surrogateescape")).digest()[:8].hex()


def hexs(s: str) -> str:
    return s.encode("utf-8", "surrogateescape").hex()


def emit(obj: object) -> None:
    sys.stdout.write(json.dumps(obj, separators=(",", ":")) + "\n")


def tok_canon(t: object) -> str:
    if isinstance(t, T.Text):
        return f"T:{hexs(t.text)}:{hexs(t.raw)}"
    if isinstance(t, T.Ruby):
        b = ",".join(tok_canon(x) for x in t.base_tokens)
        r = ",".join(tok_canon(x) for x in t.ruby_tokens)
        return f"R:{hexs(t.base)}:{hexs(t.ruby)}:{hexs(t.raw)}:{1 if t.closed else 0}:[{b}]:[{r}]"
    if isinstance(t, T.Newline):
        return f"N:{hexs(t.raw)}"
    if isinstance(t, T.Icon):
        return f"I:{t.name}:{t.index}:{hexs(t.raw)}"
    if isinstance(t, T.Code):
        arg = "-" if t.arg is None else hexs(t.arg)
        return f"C:{hexs(t.code)}:{arg}:{hexs(t.raw)}"
    if isinstance(t, T.Ref):
        return f"F:{t.line_id}:{hexs(t.raw)}"
    raise TypeError(type(t))


def toks_canon(tokens: list) -> str:
    return "|".join(tok_canon(t) for t in tokens)


def _f(x: float | None) -> str:
    return "-" if x is None else struct.pack(">d", x).hex()


def _n(x: object) -> str:
    return "-" if x is None else str(x)


def run_canon(r: T.Run) -> str:
    s = r.style
    return (f"U:{r.kind}:{hexs(r.text)}:{hexs(r.ruby)}:{_n(s.color)}:{s.color_ext}:{s.style}:{1 if s.flag_b else 0}:{s.anim}:"
            f"{1 if s.nowrap else 0}:{_f(s.scale_w)}:{_f(s.scale_h)}:{_n(s.font_metric)}:{_n(r.value)}:{r.seq}")


def runs_canon(runs: list) -> str:
    return "|".join(run_canon(r) for r in runs)


def plain_canon(tokens: list, lang: str) -> str:
    return "|".join((
        hexs(T.plain_text(tokens, lang, ruby="drop")),
        hexs(T.plain_text(tokens, lang, ruby="paren")),
        hexs(T.plain_text(tokens, lang, ruby="markup")),
        hexs(T.plain_text(tokens, lang, ruby="drop", icons="drop")),
    ))


# ---------------------------------------------------------------------------------------------------------------------
# lines
# ---------------------------------------------------------------------------------------------------------------------

def mode_lines() -> None:
    st = T.ScriptText()
    emit({"k": "container", "issues": st.container_issues, "validate": st.validate(), "count": st.count,
          "end_marks": st.end_marks})
    for lang in T.LANGS:
        tab = st.table(lang)
        offs = tab.offsets
        emit({"k": "table", "lang": lang, "count": tab.count, "offsets": len(offs), "blob_len": len(tab.blob),
              "blob_sha256": hashlib.sha256(tab.blob).hexdigest(),
              "offsets_sha256": hashlib.sha256(struct.pack(f">{len(offs)}I", *offs)).hexdigest(),
              "validate": tab.validate(), "end_marks": st.end_marks[lang]})
        cats: dict[str, list[str]] = {"engine": [], "tok": [], "tokscript": [], "runs": [], "plain": []}
        ln = [tab.line(i) for i in range(tab.count)]
        for i in range(tab.count):
            cats["engine"].append(h16(tab.engine_line(i).hex()))
            toks = T.tokenize(ln[i], lang)
            cats["tok"].append(h16(toks_canon(toks)))
            cats["tokscript"].append(h16(toks_canon(T.tokenize(ln[i], lang, script=True))))
            cats["runs"].append(h16(runs_canon(T.to_runs(toks, lang))))
            cats["plain"].append(h16(plain_canon(toks, lang)))
        for cat, hs in cats.items():
            emit({"k": "lines", "lang": lang, "cat": cat, "n": len(hs), "h": "".join(hs)})
        # guard entries and one past the table: engine_line answers, the next id raises
        extra = []
        for i in (-5, -1, tab.count, tab.count + 1):
            extra.append([i, tab.engine_line(i).hex()])
        try:
            tab.engine_line(tab.count + 2)
            err = None
        except IndexError:
            err = "IndexError"
        emit({"k": "guards", "lang": lang, "lines": extra, "beyond": err})
        census = T.markup_census(ln, lang)
        emit({"k": "census", "lang": lang, "entries": [
            [hexs(key), v["count"], v["lines"], v["first_ids"]] for key, v in census.items()]})


# ---------------------------------------------------------------------------------------------------------------------
# fuzz
# ---------------------------------------------------------------------------------------------------------------------

def rand_bytes(rng: random.Random, n: int, high: bool) -> bytes:
    out = bytearray()
    for _ in range(n):
        r = rng.random()
        if high and r < 0.12:
            out.append(rng.choice([0x80, 0xBF, 0xC3, 0xE3, 0xE3, 0xF0, 0xFE, 0xFF, 0x81, 0xA0]))
        elif r < 0.5:
            out.append(rng.randrange(0x20, 0x7F))
        else:
            out += rng.choice(["あ", "い", "漢", "字", "日", "本", "ー", "、", "「", "　"]).encode("utf-8")
    return bytes(out)


EXPAND_PIECES = [b"abc", b" ", b"\x00", b"@n", b"^", "あいう".encode(), b"$00000", b"$", b"$00", b"$0000007"]


def mode_fuzz() -> None:
    st = T.ScriptText()
    rng = random.Random(20261025)
    count = st.count
    long_ids = sorted(range(count), key=lambda i: -len(st.table("ja").engine_line(i)))[:40]
    n_expand = 6000
    for case in range(n_expand):
        lang = rng.choice(T.LANGS)
        tab = st.table(lang)
        parts = bytearray()
        for _ in range(rng.randrange(0, 12)):
            r = rng.random()
            if r < 0.35:
                kind = rng.random()
                if kind < 0.45:
                    i = rng.randrange(0, count)
                elif kind < 0.65:
                    i = rng.choice(long_ids)
                elif kind < 0.8:
                    i = rng.choice([0, 1, count - 1, count, count + 1])
                elif kind < 0.9:
                    i = count + 2 + rng.randrange(0, 5)
                else:
                    i = rng.randrange(0, 1_000_000)
                parts += b"$%06d" % i
            elif r < 0.5:
                parts += b"$" + bytes(rng.choice([0x20, 0x2F, 0x30, 0x31, 0x39, 0x3A, 0x41, 0x7F, 0xFF]) for _ in range(rng.randrange(0, 8)))
            elif r < 0.7:
                parts += rng.choice(EXPAND_PIECES)
            else:
                parts += rand_bytes(rng, rng.randrange(1, 40), True)
        src = bytes(parts)
        r = rng.random()
        if r < 0.4:
            srclen = None
        elif r < 0.5:
            srclen = T.MESSAGE_ARG_MAX
        elif r < 0.55:
            srclen = T.CHOICE_SLOT_SIZE
        else:
            srclen = rng.randrange(-2, len(src) + 6)
        do_expand = rng.random() > 0.08
        try:
            out = T.expand_refs(src, tab.engine_line, src_len=srclen, expand=do_expand)
            emit({"k": "x", "lang": lang, "src": src.hex(), "srclen": srclen, "expand": do_expand, "out": out.hex()})
        except IndexError:
            emit({"k": "x", "lang": lang, "src": src.hex(), "srclen": srclen, "expand": do_expand, "err": "IndexError"})

    frags = [b"@c1", b"@c0", b"@c9", b"@c:", b"@cX", b"@h3", b"@h9", b"@hA", b"@w2", b"@a1", b"@a", b"@f2", b"@f",
             b"@s128,", b"@s-64)", b"@s12345678901234567890,", b"@s", b"@s0,", b"@s-0,", b"@s1-2x3,", b"@x12", b"@y", b"@x1234", b"@x",
             b"@t", b"@u", b"@l", b"@k", b"@e", b"@n", b"@g", b"@z", b"@i", b"@r", b"@b", b"@o", b"@p", b"@T", b"@1^", b"@2)", b"@1", b"@2",
             b"@q(abc)", b"@q(12)", b"@q(", b"@e(x)", b"@", b"@@", b"^", b"^^", b"[heart]", b"[ber05]", b"[zap00]", b"[bad]",
             b"[heart2]", b"[star]", b"[", b"<", b">", b"|", b"||", b"$000123", b"$12", b"$abcdef", b"$000123456", b"%", b"\x00",
             b" ", b"abc", b"Hello, world", "あ".encode(), "漢字".encode(), "日本語".encode(), b"\x80", b"\xe3", b"\xe3\x81",
             "<漢字|かんじ>".encode(), "<日|に>".encode(), b"<ab|cd>", b"<ab", b"<a|b|c>", b"<a<b|c>", b"a>b", b"@c1<x|y>@r",
             b"<@c1a|@gb>", b"<[heart]|^>", b"@c" + "あ".encode(), b"@w" + "い".encode(),
             b"@s" + b"7" * 255, b"@s" + b"7" * 256, b"@s" + b"9" * 300 + b",", b"@q(" + b"x" * 254 + b")", b"@q(" + b"x" * 255 + b")",
             b"@q(" + b"x" * 300 + b")", b"@q(\x00)", b"@x999", b"@y0123456"]
    n_tok = 9000
    for case in range(n_tok):
        lang = rng.choice(T.LANGS)
        script = rng.random() < 0.4
        parts = bytearray()
        for _ in range(rng.randrange(0, 14)):
            parts += rng.choice(frags) if rng.random() < 0.8 else rand_bytes(rng, rng.randrange(1, 8), True)
        src = bytes(parts)
        try:
            toks = T.tokenize(src, lang, script=script)
            emit({"k": "t", "lang": lang, "script": script, "src": src.hex(), "tok": toks_canon(toks),
                  "runs": runs_canon(T.to_runs(toks, lang)), "plain": plain_canon(toks, lang)})
        except Exception as e:  # noqa: BLE001
            emit({"k": "t", "lang": lang, "script": script, "src": src.hex(), "err": type(e).__name__ + ": " + str(e)})
    emit({"k": "end", "expand": n_expand, "tok": n_tok})


# ---------------------------------------------------------------------------------------------------------------------
# refs
# ---------------------------------------------------------------------------------------------------------------------

def cmd_canon(c: R.TextCommand) -> str:
    return "|".join((
        str(c.script), c.name, str(c.pos), c.op, c.label, hexs(c.arg), _n(c.choice), ",".join(hexs(e) for e in c.extra),
        hexs(c.message), c.end_code, _n(c.end_mode), "1" if c.displayed else "0", "1" if c.locked else "0",
        ",".join(str(r) for r in c.refs)))


def counter_entries(c: dict) -> list:
    return [[hexs(k), v] for k, v in sorted(c.items(), key=lambda kv: kv[0].encode("utf-8", "surrogateescape"))]


def mode_refs() -> None:
    scripts = R.load_scripts()
    st = T.ScriptText()
    cmds = list(R.iter_text_commands(scripts))
    emit({"k": "scripts", "n": len(scripts), "commands": len(cmds), "names_sha": h16("|".join(n for n, _ in scripts))})
    by_script: dict[int, list[str]] = {}
    for c in cmds:
        by_script.setdefault(c.script, []).append(h16(cmd_canon(c)))
    for k in range(len(scripts)):
        hs = by_script.get(k, [])
        emit({"k": "cmds", "script": k, "n": len(hs), "h": "".join(hs)})
    idx = R.ref_index(cmds)
    rows = [f"{i}=" + ",".join(f"{c.script}.{c.pos}" for c in v) for i, v in sorted(idx.items())]
    emit({"k": "ref_index", "n": len(idx), "total": sum(len(v) for v in idx.values()), "h": h16("\n".join(rows))})
    groups = R.message_groups(cmds)
    emit({"k": "groups", "n": len(groups), "h": h16("\n".join(",".join(f"{c.script}.{c.pos}" for c in g) for g in groups))})
    emit({"k": "zd_only", "ids": R.zd_only_ids(cmds)})
    cen = R.script_census(cmds)
    for key in ("ops", "shapes", "codes"):
        emit({"k": "census_" + key, "entries": counter_entries(cen[key])})
    vt = R.voice_text_counts(scripts)
    emit({"k": "voice", "vply": sorted(vt["VPLY"].items()), "vpl2": sorted(vt["VPL2"].items())})
    unref = R.classify_unreferenced(st, cmds)
    emit({"k": "unref", "entries": [[i, c] for i, c in sorted(unref.items())]})
    for lang in T.LANGS[:4]:
        hs = []
        for name, _ in scripts:
            hs.append(h16("\n".join(R.scene_transcript(st, scripts, name, lang))))
        emit({"k": "transcripts", "lang": lang, "n": len(hs), "h": "".join(hs)})
    selr = [c for c in cmds if c.op == "SELR"]
    emit({"k": "selr", "n": len(selr), "locked": sum(1 for c in selr if c.locked),
          "slots": [[c.script, c.pos, c.choice_slot(s).encode("utf-8").hex()] for c in selr[:6] for s in (0, 8, 9, 12)]})
    emit({"k": "end"})


# ---------------------------------------------------------------------------------------------------------------------
# rules
# ---------------------------------------------------------------------------------------------------------------------

def mode_rules() -> None:
    sysl = [None, 0, 1, 2, 5, 9, 10, 11, 12, -1]
    emit({"k": "sys", "entries": [[s, T.system_language_to_text(s)] for s in sysl]})
    words = [0, 1, 2, 3, 4, 5, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF, 0x1_0003, 0x1_FFFF, 0xABCD]
    emit({"k": "eff", "entries": [[w, s, list(T.effective_languages(w, s))] for w in words for s in sysl]})
    emit({"k": "choice", "entries": [[k, T.choice_number_id(k), T.choice_slot_text(k, "あ").encode("utf-8").hex()] for k in range(0, 30)]})
    emit({"k": "consts", "icons": list(T.ICON_NAMES), "colors": [[k, v] for k, v in sorted(T.COLOR_RGB565.items())],
          "scale": [_f(x) for x in T.SCALE_TABLE], "engine_line_max": T.ENGINE_LINE_MAX, "expand_max": T.ENGINE_EXPAND_MAX,
          "arg_max": T.MESSAGE_ARG_MAX, "number_base": T.CHOICE_NUMBER_BASE, "number_overflow": T.CHOICE_NUMBER_OVERFLOW,
          "slot_size": T.CHOICE_SLOT_SIZE, "unset": T.LANG_OPTION_UNSET, "ruby": [[l, T.ruby_enabled(l)] for l in T.LANGS],
          "image_text_ids": [[k, v] for k, v in sorted(R.IMAGE_TEXT_IDS.items())], "classes": list(R.UNREFERENCED_CLASSES)})


MODES = {"lines": mode_lines, "fuzz": mode_fuzz, "refs": mode_refs, "rules": mode_rules}

if __name__ == "__main__":
    if len(sys.argv) != 2 or sys.argv[1] not in MODES:
        raise SystemExit(f"usage: text_oracle.py {'|'.join(MODES)}")
    MODES[sys.argv[1]]()
