"""Area ``script``: golden data for the script front end (package C1, PACE 7.2 #4 part 1).

What the C# port in ``src/Tsuki.Engine/Vm/`` must reproduce, exported from ``tsuki.script``:

* the **segmentation** of every script (``parse_script``: segment bounds, the name, the marker ids), and
* **both argument readers** on every command segment: the inline readers (VA 0x76690 / 0x76780 / 0x76860), the object
  parser (VA 0xD690) with its expression evaluator (VA 0xDA50) and strtol getters (VA 0xE310 / 0xE3C0), and
  ``Command.value`` (the reader a command's handler uses).

Parts (``python -m oracle.script`` writes all of them; ``--parts`` selects):

``hashes``  ``script_hashes.json``: for each of the 549 scripts the SHA-256 of its canonical segment records
            (``seg_sha256``) and of its canonical argument records (``args_sha256``), the segment and command counts and
            the SHA-256 of the decoded bytes. ``--lines K`` prints the records of one script, for diffing against the C#
            side.
``fuzz``    ``fuzz.json``: random inputs with the oracle's answers for the raw functions (``inline_int``, ``inline_present``,
            ``obj_expr``, ``obj_strict_int``, ``obj_partial_int``, ``obj_split``) and for whole synthetic scripts.
``tables``  ``tables.json``: ``OBJ_COMMANDS`` and the constants of the front end.

Canonical records
-----------------
One record group per segment (decimal numbers, lower-case hex, fields separated by one space, ``-`` = none).  The C# test
helper ``tests/Tsuki.Tests.Data/Vm/CanonicalRecords.cs`` writes exactly the same text from the C# API.

``S idx start end nameoff key block sub``
    ``nameoff`` = offset of the ``_`` for a dispatchable command, else -1; ``key`` = hex of the four name bytes (``-`` when
    the segment has no complete name); ``block``/``sub`` = the marker ids as the engine decodes them (``-`` for non-markers).

For command segments only (``nameoff >= 0``):

``A idx trailer n args tags``
    bytes after the first ``)`` (hex), the argument count of ``Command.args``, the arguments (hex, comma separated) and
    their keyword tags (``-`` for none).
``I idx cells``
    ``i16/i32/present`` of the inline readers for argument indices 0 .. max(n, 1).
``O idx status count ranges``
    the object parser: ``ok`` / ``noterm`` / ``quote``; per argument ``start-end:has:expr:strict:partial`` with absolute
    script offsets, ``expr`` an int, ``n`` (empty argument: not evaluated) or ``u<code>`` (undefined), ``strict`` /
    ``partial`` an int or ``n``.
``V idx values``
    ``Command.value(i)`` for i < n: an int, ``n`` (absent), ``u`` (undefined) or ``e`` (argument list cannot be split).

Undefined codes: ``div`` division fault, ``un`` unary without operand, ``bin`` binary without operands, ``empty`` empty
stack, ``brace`` error inside ``{...}``.

Run ``PYTHONPATH=D:/TsukihimePort/tools .venv/Scripts/python.exe -m oracle.script`` (about a minute with all cores).
"""
from __future__ import annotations

import argparse
import hashlib
import multiprocessing as mp
import random
import sys
import time
from pathlib import Path
from typing import Any, Callable, Optional, Sequence

from . import GOLDEN_ROOT
from .manifest import AreaWriter, Producer, dump_json, verify_area

AREA = "script"
PARTS: tuple[str, ...] = ("hashes", "fuzz", "tables")
DESCRIPTION = ("script front end: segmentation and both argument readers of all 549 scripts (per-script hashes of the "
               "canonical records), reader fuzz cases, OBJ_COMMANDS")

SCRIPT_COUNT = 549
FUZZ_SEED = 20261025

_UNDEFINED_CODES = (
    ("division fault", "div"), ("unary operator without operand", "un"), ("binary operator without two operands", "bin"),
    ("empty evaluation stack", "empty"), ("error inside {...}", "brace"),
)


def _undefined_code(e: Exception) -> str:
    msg = str(e)
    for prefix, code in _UNDEFINED_CODES:
        if msg.startswith(prefix):
            return code
    raise ValueError(f"unknown ExprUndefined message {msg!r}")


def _hex(s: str) -> str:
    return s.encode("utf-8", "surrogateescape").hex()


# ---------------------------------------------------------------------------
# canonical records
# ---------------------------------------------------------------------------

def segment_line(c: Any) -> str:
    """The ``S`` record of a :class:`tsuki.script.Command`."""
    key = c.key.encode("latin-1").hex() if c.key else "-"
    block = "-" if c.block is None else str(c.block)
    sub = "-" if c.sub is None else str(c.sub)
    return f"S {c.index} {c.offset} {c.end} {c.name_offset} {key} {block} {sub}"


def argument_lines(c: Any) -> list[str]:
    """The ``A`` / ``I`` / ``O`` / ``V`` records of a command segment (nothing for a skipped segment)."""
    from tsuki.script import ExprUndefined, ScriptError, obj_expr, obj_partial_int, obj_split, obj_strict_int
    if c.name_offset < 0:
        return []
    i = c.index
    n = len(c.args)
    out: list[str] = []
    # A: text view
    args_hex = ",".join(_hex(a.raw) for a in c.args) if n else "-"
    tags = ",".join("-" if a.tag is None else str(a.tag) for a in c.args) if n else "-"
    out.append(f"A {i} {_hex(c.trailer) if c.trailer else '-'} {n} {args_hex} {tags}")
    # I: inline reader
    cells = " ".join(f"{c.int_arg(k, 16)}/{c.int_arg(k, 32)}/{int(c.present(k))}" for k in range(max(n, 1) + 1))
    out.append(f"I {i} {cells}")
    # O: object parser
    raw = c.raw_bytes
    status = "ok"
    ranges: list[tuple[int, int]] = []
    try:
        ranges = obj_split(raw, c.arg_start - 1)
    except ScriptError as e:
        status = "quote" if "quoted" in str(e) else "noterm"
    recs: list[str] = []
    for s, e in ranges:
        data = raw[s:e]
        has = e > s
        expr = "n"
        if has:
            try:
                expr = str(obj_expr(data))
            except ExprUndefined as ex:
                expr = "u" + _undefined_code(ex)
        strict = obj_strict_int(data)
        partial = obj_partial_int(data)
        recs.append(f"{c.offset + s}-{c.offset + e}:{int(has)}:{expr}:{'n' if strict is None else strict}:"
                    f"{'n' if partial is None else partial}")
    out.append(f"O {i} {status} {len(recs)}" + "".join(" " + r for r in recs))
    # V: Command.value
    vals: list[str] = []
    for k in range(n):
        try:
            v = c.value(k)
            vals.append("n" if v is None else str(v))
        except ExprUndefined:
            vals.append("u")
        except ScriptError:
            vals.append("e")
    out.append(f"V {i} " + (" ".join(vals) if vals else "-"))
    return out


def script_records(data: bytes) -> tuple[list[str], list[str], int]:
    """(segment records, argument records, number of command segments) of a decoded script."""
    from tsuki.script import parse_script
    cmds = parse_script(data)
    seg: list[str] = []
    arg: list[str] = []
    commands = 0
    for c in cmds:
        seg.append(segment_line(c))
        lines = argument_lines(c)
        if lines:
            commands += 1
        arg.extend(lines)
    return seg, arg, commands


def _digest(lines: Sequence[str]) -> str:
    h = hashlib.sha256()
    for ln in lines:
        h.update(ln.encode("ascii"))
        h.update(b"\n")
    return h.hexdigest()


# ---------------------------------------------------------------------------
# part: hashes
# ---------------------------------------------------------------------------

def _hash_job(job: tuple[str, int, int]) -> list[dict[str, Any]]:
    from tsuki.script import AllScr
    path, lo, hi = job
    out: list[dict[str, Any]] = []
    with AllScr(path) as scr:
        for k in range(lo, hi):
            raw = scr.raw(k)
            seg, arg, commands = script_records(raw)
            out.append({"script": k, "name": scr.names[k], "bytes": len(raw), "segments": len(seg),
                        "commands": commands, "raw_sha256": hashlib.sha256(raw).hexdigest(),
                        "seg_sha256": _digest(seg), "args_sha256": _digest(arg)})
    return out


def export_hashes(game: Path, procs: int, log: Callable[[str], None]) -> list[dict[str, Any]]:
    path = str(game / "image" / "allscr.mrg")
    step = 8
    jobs = [(path, lo, min(lo + step, SCRIPT_COUNT)) for lo in range(0, SCRIPT_COUNT, step)]
    t0 = time.perf_counter()
    rows: list[dict[str, Any]] = []
    if procs <= 1:
        for j in jobs:
            rows.extend(_hash_job(j))
    else:
        import importlib
        job_fn = importlib.import_module("oracle.script")._hash_job      # picklable by qualified name under -m
        with mp.get_context("spawn").Pool(procs) as pool:
            for part in pool.imap_unordered(job_fn, jobs):
                rows.extend(part)
    rows.sort(key=lambda r: r["script"])
    log(f"[script] hashed {len(rows)} scripts, {sum(r['segments'] for r in rows):,} segments "
        f"in {time.perf_counter() - t0:.1f} s")
    return rows


# ---------------------------------------------------------------------------
# part: fuzz
# ---------------------------------------------------------------------------

def _rand_bytes(rng: random.Random, alphabet: bytes, lo: int, hi: int) -> bytes:
    return bytes(rng.choice(alphabet) for _ in range(rng.randint(lo, hi)))


def fuzz_cases(seed: int = FUZZ_SEED, scale: float = 1.0) -> dict[str, Any]:
    from tsuki.script import (ExprUndefined, ScriptError, inline_int, inline_present, obj_expr, obj_partial_int,
                              obj_split, obj_strict_int)
    rng = random.Random(seed)

    def n(count: int) -> int:
        return max(1, int(count * scale))

    cases: dict[str, Any] = {}

    # inline readers on raw bytes: [buf hex, p0, "i16/i32/present" for index 0..5 or 0..3, space separated]
    alphabet = b"0123456789012345+-,();`:.xab \x00;),"
    inline: list[Any] = []
    for _ in range(n(12000)):
        buf = _rand_bytes(rng, alphabet, 0, 40)
        p0 = rng.randint(0, min(len(buf), 8))
        inline.append([buf.hex(), p0, " ".join(f"{inline_int(buf, p0, k, 16)}/{inline_int(buf, p0, k, 32)}/"
                                                f"{int(inline_present(buf, p0, k))}" for k in range(6))])
    # a few long skips: 500-byte limit per argument, 16-byte value limit
    for _ in range(n(300)):
        buf = (b"x" * rng.randint(400, 620)) + rng.choice([b",", b")", b";", b""]) + _rand_bytes(rng, alphabet, 0, 30)
        p0 = rng.randint(0, 3)
        inline.append([buf.hex(), p0, " ".join(f"{inline_int(buf, p0, k, 16)}/{inline_int(buf, p0, k, 32)}/"
                                                f"{int(inline_present(buf, p0, k))}" for k in range(4))])
    cases["inline"] = inline

    # expressions: [arg hex, "value" | "u<code>"]
    expr_alpha = b"0123456789+-*/%<>=!&|^~{}.x` a"
    exprs: list[Any] = []
    for _ in range(n(12000)):
        a = _rand_bytes(rng, expr_alpha, 0, 14)
        try:
            exprs.append([a.hex(), str(obj_expr(a))])
        except ExprUndefined as e:
            exprs.append([a.hex(), "u" + _undefined_code(e)])
    # structured: operands separated by operators, braces, big numbers
    ops = [b"+", b"-", b"*", b"/", b"%", b"<<", b">>", b"<", b"<=", b">", b">=", b"==", b"!=", b"&", b"^", b"|", b"&&", b"||"]
    for _ in range(n(8000)):
        parts: list[bytes] = []
        for _k in range(rng.randint(1, 5)):
            t = rng.random()
            if t < 0.15:
                parts.append(rng.choice([b"-", b"!", b"~", b"+"]))
            if t > 0.9:
                parts.append(b"{" + str(rng.randint(0, 99)).encode() + rng.choice(ops) + str(rng.randint(0, 99)).encode() + b"}")
            else:
                parts.append(str(rng.choice([0, 1, 2, 3, 7, 31, 32, 33, 255, 2147483647, 2147483648, 4294967295,
                                             99999999999999999999, rng.randint(0, 5000)])).encode())
            parts.append(rng.choice(ops))
        if rng.random() < 0.8:
            parts.pop()
        a = b"".join(parts)
        try:
            exprs.append([a.hex(), str(obj_expr(a))])
        except ExprUndefined as e:
            exprs.append([a.hex(), "u" + _undefined_code(e)])
    cases["expr"] = exprs

    # strtol getters: [arg hex, "strict" | "n", "partial" | "n"]
    int_alpha = b" \t\n\x0b\x0c\r+-0123456789012345.x"
    ints: list[Any] = []
    for _ in range(n(6000)):
        a = _rand_bytes(rng, int_alpha, 0, 17)
        st, pa = obj_strict_int(a), obj_partial_int(a)
        ints.append([a.hex(), "n" if st is None else str(st), "n" if pa is None else str(pa)])
    cases["int"] = ints

    # object-parser split: [buf hex, sep, "status count start-end ..."]
    split_alpha = b'ab,);"( 12;,'
    splits: list[Any] = []
    for _ in range(n(8000)):
        buf = _rand_bytes(rng, split_alpha, 1, 30)
        if rng.random() < 0.05:
            buf = b"(" + b"1," * rng.randint(18, 25) + b"1);"
        sep = rng.randint(0, len(buf))
        try:
            r = obj_split(buf, sep)
            splits.append([buf.hex(), sep, f"ok {len(r)}" + "".join(f" {a}-{b}" for a, b in r)])
        except ScriptError as e:
            splits.append([buf.hex(), sep, ("quote" if "quoted" in str(e) else "noterm") + " 0"])
    cases["split"] = splits

    # whole synthetic scripts: [script hex, seg records, arg records | null when parse_script raises]
    cases["scripts"] = _fuzz_scripts(rng, n(1500))
    return cases


_NAMES = ["WKST", "STCH", "SEPL", "TOMP", "CLO3", "ZM", "ZZ", "JUMP", "IF__", "STCC", "WTTM", "FADS", "VPLY", "SCH2",
          "STGS", "MPLY", "SELA", "RTM_", "STZ4", "XYZ0", "abcd"]
_ARGS = ["0", "1", "-1", "240", "258-18", "154-19", "242.25", "2000.0", "F823", "eq", "img1728", "`017:100", "`001:4000",
         "", "0x000000", "CC", "START", "10-3-2", "{10-3}-2", "2*-3", "40000", "1/0", "12345678901234567890", '"a,b"',
         "$000107@k@e", " 7", "5 ", "-12"]


def _fuzz_scripts(rng: random.Random, count: int) -> list[Any]:
    from tsuki.script import ScriptError, parse_script
    out: list[Any] = []
    # the 0x300-byte window: the ';' falls on every offset around the limit, with and without a name at the start
    for off in range(0x2FC, 0x304):
        for body in (b"x" * off + b";", b"_STCC(" + b"x" * (off - 6) + b";", b"_" + b"x" * (off - 1) + b";"):
            data = body + b"_STCC(1);"
            try:
                seg, arg, _commands = script_records(data)
                out.append([data.hex(), seg, arg])
            except ScriptError:
                out.append([data.hex(), None, None])
    for _ in range(count):
        stmts: list[bytes] = []
        for _k in range(rng.randint(1, 6)):
            name = rng.choice(_NAMES)
            t = rng.random()
            if name in ("ZM", "ZZ"):
                s = ("_" + name + "".join(rng.choice("0123456789abcdefA") for _ in range(5))).encode()
                body = "(" + ",".join(rng.choice(_ARGS) for _ in range(rng.randint(0, 2))) + ")"
            else:
                s = ("_" + name).encode()
                body = rng.choice("(((((," if t < 0.05 else "(") + ",".join(
                    rng.choice(_ARGS) for _ in range(rng.randint(0, 6))) + ")"
            if t > 0.93:
                body += rng.choice(["#)", "gb)", "x", ""])
            s += body.encode("utf-8")
            if t > 0.03:
                s += b";"
            if 0.4 < t < 0.45:
                s = b"junk" + s                      # text before the '_'
            if 0.45 < t < 0.47:
                s = s.replace(b"_", b"", 1)         # no '_' at all
            stmts.append(s)
        data = b"".join(stmts)
        if rng.random() < 0.05:
            data += b"X" * rng.randint(0x2F0, 0x340)         # a segment around the 0x300 limit
            if rng.random() < 0.5:
                data += b";_STCC(1);"
        try:
            seg, arg, _commands = script_records(data)
            out.append([data.hex(), seg, arg])
        except ScriptError:
            out.append([data.hex(), None, None])
    return out


# ---------------------------------------------------------------------------
# generated C# cases for the synthetic tests (no game data, no golden files)
# ---------------------------------------------------------------------------

SYNTHETIC_SEED = FUZZ_SEED + 1
SYNTHETIC_SCALE = 0.03


def _cs(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def emit_synthetic(path: Path, seed: int = SYNTHETIC_SEED, scale: float = SYNTHETIC_SCALE) -> int:
    """Write ``OracleReaderCases.g.cs``: a small random sample of the fuzz cases (own seed) as C# arrays, so that
    ``tests/Tsuki.Tests.Synthetic/Vm`` compares the C# readers with the oracle without game data or golden files.
    Returns the number of cases."""
    cases = fuzz_cases(seed, scale)
    out = [
        "// <auto-generated>",
        "// Written by tools/oracle/script.py (python -m oracle.script --emit-synthetic <this file>) from tsuki.script;",
        f"// seed {seed}, scale {scale}. Random synthetic inputs only, no game data. Do not edit: regenerate.",
        "// </auto-generated>",
        "namespace Tsuki.Tests.Synthetic.Vm;",
        "",
        "internal static class OracleReaderCases",
        "{",
        "    /// <summary>Buffer (hex), p0, \"i16/i32/present\" for the indices 0.. as the oracle reads them.</summary>",
        "    public static readonly (string Buf, int P0, string Want)[] Inline =",
        "    [",
    ]
    out += [f"        ({_cs(b)}, {p0}, {_cs(w)})," for b, p0, w in cases["inline"]]
    out += ["    ];", "", "    /// <summary>Argument (hex) and the oracle's value or u&lt;code&gt;.</summary>",
            "    public static readonly (string Arg, string Want)[] Expr =", "    ["]
    out += [f"        ({_cs(a)}, {_cs(w)})," for a, w in cases["expr"]]
    out += ["    ];", "", "    /// <summary>Argument (hex), strict and partial strtol or n.</summary>",
            "    public static readonly (string Arg, string Strict, string Partial)[] Int =", "    ["]
    out += [f"        ({_cs(a)}, {_cs(st)}, {_cs(pa)})," for a, st, pa in cases["int"]]
    out += ["    ];", "", "    /// <summary>Buffer (hex), separator index, \"status count start-end ...\".</summary>",
            "    public static readonly (string Buf, int Sep, string Want)[] Split =", "    ["]
    out += [f"        ({_cs(b)}, {sep}, {_cs(w)})," for b, sep, w in cases["split"]]
    out += ["    ];", "", "    /// <summary>Script (hex) and its S records (segment start/end/name offset/key/marker ids) joined by '|', or \"error\".</summary>",
            "    public static readonly (string Script, string Want)[] Segments =", "    ["]
    for hexs, seg, _arg in cases["scripts"]:
        out.append(f"        ({_cs(hexs)}, {_cs('error' if seg is None else '|'.join(seg))}),")
    out += ["    ];", "}", ""]
    Path(path).write_text("\n".join(out), encoding="utf-8", newline="\n")
    return sum(len(cases[k]) for k in ("inline", "expr", "int", "split", "scripts"))


# ---------------------------------------------------------------------------
# area
# ---------------------------------------------------------------------------

def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Callable[[str], None] = print, procs: int = 0, fuzz_scale: float = 1.0) -> dict[str, Any]:
    """Export the area into ``<root>/script`` (the signature the runner expects; ``quick`` is unused)."""
    from tsuki.script import MAX_STATEMENT, OBJ_COMMANDS
    wanted = list(parts) if parts else list(PARTS)
    for p in wanted:
        if p not in PARTS:
            raise ValueError(f"unknown part {p!r}; known: {', '.join(PARTS)}")
    area = AreaWriter(root, AREA, Producer("oracle.script", "run", {"parts": wanted}, ("tsuki.script.parse_script",)),
                      description=DESCRIPTION, game_dir=game)
    if "hashes" in wanted:
        pw = area.part("hashes", "per-script SHA-256 of the canonical segment and argument records")
        pw.add_input(game / "image" / "allscr.mrg", full_hash=True)
        rows = export_hashes(game, procs if procs else max(1, (mp.cpu_count() or 2) - 2), log)
        total = sum(r["segments"] for r in rows)
        pw.write_json("script_hashes.json", {
            "canonical_records": __doc__.split("Canonical records")[1].split("Undefined codes")[0].strip("\n- "),
            "scripts": rows,
            "totals": {"scripts": len(rows), "segments": total, "commands": sum(r["commands"] for r in rows),
                       "bytes": sum(r["bytes"] for r in rows)}},
            Producer("oracle.script", "export_hashes", {"scripts": SCRIPT_COUNT},
                     ("tsuki.script.AllScr.raw", "tsuki.script.parse_script", "tsuki.script.Command.int_arg",
                      "tsuki.script.Command.present", "tsuki.script.obj_split", "tsuki.script.obj_expr",
                      "tsuki.script.obj_strict_int", "tsuki.script.obj_partial_int", "tsuki.script.Command.value")))
        pw.count("scripts", len(rows))
        pw.count("segments", total)
        pw.check("scripts", SCRIPT_COUNT, len(rows), "script.md 1: 549 scripts")
        pw.check("segments", 1_083_168, total, "script.md 1 and ARCHITECTURE 10.3: 1,083,168 statements")
        pw.check("bytes", 19_617_273, sum(r["bytes"] for r in rows), "script.md 1: 19,617,273 decoded bytes")
        pw.finish()
    if "fuzz" in wanted:
        pw = area.part("fuzz", "random inputs with the oracle's answers for the raw readers and for synthetic scripts")
        cases = fuzz_cases(FUZZ_SEED, fuzz_scale)
        pw.write_json("fuzz.json", {"seed": FUZZ_SEED, "scale": fuzz_scale, **cases},
                      Producer("oracle.script", "fuzz_cases", {"seed": FUZZ_SEED, "scale": fuzz_scale},
                               ("tsuki.script.inline_int", "tsuki.script.inline_present", "tsuki.script.obj_expr",
                                "tsuki.script.obj_strict_int", "tsuki.script.obj_partial_int", "tsuki.script.obj_split",
                                "tsuki.script.parse_script")))
        for k, v in cases.items():
            pw.count(k, len(v))
        pw.finish()
    if "tables" in wanted:
        pw = area.part("tables", "OBJ_COMMANDS and the front-end constants")
        pw.write_json("tables.json", {"obj_commands": sorted(OBJ_COMMANDS), "max_statement": MAX_STATEMENT},
                      Producer("oracle.script", "run", {"part": "tables"}, ("tsuki.script.OBJ_COMMANDS",)))
        pw.check("obj_commands", 52, len(OBJ_COMMANDS), "script.md 3.2: the 52 object-parser commands")
        pw.check("max_statement", 0x300, MAX_STATEMENT, "script.md 3.1: at most 0x300 bytes per segment")
        pw.finish()
    return area.finish()


def _print_lines(game: Path, k: int, limit: Optional[int]) -> None:
    from tsuki.script import AllScr
    with AllScr(game / "image" / "allscr.mrg") as scr:
        seg, arg, _c = script_records(scr.raw(k))
    # interleave per statement like the C# helper's debug dump: S then A I O V
    byidx: dict[int, list[str]] = {}
    for ln in arg:
        byidx.setdefault(int(ln.split(" ", 2)[1]), []).append(ln)
    shown = 0
    for ln in seg:
        print(ln)
        for a in byidx.get(int(ln.split(" ", 2)[1]), []):
            print(a)
        shown += 1
        if limit is not None and shown >= limit:
            break


def main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m oracle.script", description=__doc__.split("\n")[0])
    ap.add_argument("--out", default=str(GOLDEN_ROOT), help="golden root (default: samples/golden)")
    ap.add_argument("--game", help="CUSA47999 folder (default: TSUKI_GAME_DIR or the tsuki default)")
    ap.add_argument("--parts", help=f"comma-separated subset of {', '.join(PARTS)}")
    ap.add_argument("--procs", type=int, default=0, help="worker processes for the hashes part (default: cores - 2)")
    ap.add_argument("--fuzz-scale", type=float, default=1.0, help="scale of the fuzz case counts")
    ap.add_argument("--verify", action="store_true", help="only verify an existing export")
    ap.add_argument("--emit-synthetic", metavar="FILE", help="write the C# case tables for tests/Tsuki.Tests.Synthetic/Vm and exit")
    ap.add_argument("--lines", type=int, metavar="K", help="print the canonical records of script K and exit")
    ap.add_argument("--limit", type=int, help="with --lines: at most this many statements")
    a = ap.parse_args(argv)
    reconf = getattr(sys.stdout, "reconfigure", None)
    if reconf is not None:
        reconf(encoding="utf-8")
    if a.emit_synthetic:
        n = emit_synthetic(Path(a.emit_synthetic))
        print(f"[{AREA}] wrote {n} synthetic cases -> {a.emit_synthetic}")
        return 0
    from tsuki import game_dir
    game = Path(a.game) if a.game else game_dir()
    if a.lines is not None:
        if game is None:
            print("game folder not found", file=sys.stderr)
            return 2
        _print_lines(game, a.lines, a.limit)
        return 0
    root = Path(a.out)
    if not a.verify:
        if game is None or not (game / "image").is_dir():
            print("game folder not found (pass --game or set TSUKI_GAME_DIR)", file=sys.stderr)
            return 2
        parts = [p for p in a.parts.split(",") if p] if a.parts else None
        t0 = time.perf_counter()
        man = run(root, game, parts=parts, log=print, procs=a.procs, fuzz_scale=a.fuzz_scale)
        print(f"[{AREA}] wrote {len(man['files'])} files in {time.perf_counter() - t0:.1f} s -> {root / AREA}")
    problems = verify_area(root / AREA)
    for p in problems:
        print(f"PROBLEM: {p}")
    print(f"[{AREA}] verify: {len(problems)} problems")
    return 0 if not problems else 1


if __name__ == "__main__":
    raise SystemExit(main())
