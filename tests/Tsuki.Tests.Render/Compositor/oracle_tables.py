"""Oracle side of ExportModelTests and OracleTableTests (D1): compose's export model and its tables,
for comparison with the C# compositor.

    python oracle_tables.py export <folder>   in.f32 (float32 values) -> export.f32 (compose.export_mrt0)
                                              and export.u8 (compose.to_u8 of it)
    python oracle_tables.py tables <folder>   compares the C# dumps in <folder> with compose; prints the
                                              counts and exits 1 on any mismatch

Run with PYTHONPATH=tools.  Files are little-endian; the formats are written by the C# tests.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

from tsuki import compose as cp


def export(folder: Path) -> int:
    x = np.fromfile(folder / "in.f32", "<f4")
    y = cp.export_mrt0(x).astype("<f4")
    y.tofile(folder / "export.f32")
    cp.to_u8(y).astype(np.uint8).tofile(folder / "export.u8")
    changed = int(np.count_nonzero(~np.isnan(x) & (np.clip(np.nan_to_num(x, nan=0, posinf=1, neginf=0), 0, 1) != y)))
    print(f"export_mrt0 of {x.size} values; {changed} changed by the fp16 rounding")
    return 0


EFFECT = np.dtype([("word", "<i4"), ("e", "<i4"), ("flag", "<i4"), ("mode", "<i4"), ("state", "<i4"),
                   ("strength", "<f4"), ("p2", "<f4")])
RULE = np.dtype([("p", "<f4"), ("lo", "<f4"), ("hi", "<f4"), ("inv", "<f4"), ("alpha", "<i4")])
STEP = np.dtype([("kind", "<i4"), ("ms", "<i4"), ("step", "<f4")])
FADS = np.dtype([("kind", "<i4"), ("c15", "<i4"), ("surface", "<i4")])   # surface: -1 none, -2 not decoded


def tables(folder: Path) -> int:
    bad = 0

    eff = np.fromfile(folder / "effects.bin", EFFECT)
    m = 0
    for r in eff:
        ref = cp.effect(int(r["word"]))
        got = (int(r["e"]), bool(r["flag"]), int(r["mode"]), int(r["state"]), np.float32(r["strength"]), np.float32(r["p2"]))
        exp = (ref.e, bool(ref.flag), ref.shader_mode, ref.blend_state, np.float32(ref.strength), np.float32(ref.p2))
        if got != exp:
            if m < 10:
                print(f"effect word {int(r['word'])}: C# {got} compose {exp}")
            m += 1
    print(f"effect words: {eff.size}, mismatches {m}")
    bad += m

    rule = np.fromfile(folder / "rule.bin", RULE)
    m = 0
    for r in rule:
        p = float(r["p"])
        exp = tuple(np.float32(v) for v in cp.rule_params(p))
        got = (np.float32(r["lo"]), np.float32(r["hi"]), np.float32(r["inv"]))
        a = int(round(float(cp.fads_vertex_alpha(p)) * 255))
        if got != exp or int(r["alpha"]) != a:
            if m < 10:
                print(f"p {p!r}: C# {got} alpha {int(r['alpha'])}, compose {exp} alpha {a}")
            m += 1
    print(f"rule params / cross-fade bytes: {rule.size} progress values, mismatches {m}")
    bad += m

    step = np.fromfile(folder / "step.bin", STEP)
    m = 0
    for r in step:
        exp = cp.fads_step(int(r["ms"]), int(r["kind"]))
        if np.float32(r["step"]) != np.float32(exp):
            if m < 10:
                print(f"fads_step({int(r['ms'])}, {int(r['kind'])}): C# {float(r['step'])!r} compose {float(exp)!r}")
            m += 1
    print(f"fads_step: {step.size} values, mismatches {m}")
    bad += m

    fads = np.fromfile(folder / "fads.bin", FADS)
    m = 0
    for r in fads:
        k = int(r["kind"])
        try:
            s = cp.fads_capture(k)
            exp_s = -1 if s is None else int(s)
        except ValueError:
            exp_s = -2
        exp = (1 if k in cp.CAPTURE15_TYPES else 0, exp_s)
        got = (int(r["c15"]), int(r["surface"]))
        if got != exp:
            if m < 10:
                print(f"_FADS type {k}: C# (capture15, surface) {got} compose {exp}")
            m += 1
    print(f"_FADS types: {fads.size}, mismatches {m}")
    bad += m
    return 1 if bad else 0


def main(argv: list) -> int:
    if len(argv) == 3 and argv[1] == "export":
        return export(Path(argv[2]))
    if len(argv) == 3 and argv[1] == "tables":
        return tables(Path(argv[2]))
    print(__doc__, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
