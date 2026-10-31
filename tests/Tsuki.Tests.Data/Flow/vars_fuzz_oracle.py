"""Thin caller of the Python oracle for package C2: the 61,000 fuzz cases of vm_variables.md 10.

The cases are the ones ``tsuki.misc_vm_variables_errata.fuzz`` compares between the engine
(unicorn) and ``tsuki.vm_vars``: ``fuzz 1000 20261025`` and ``fuzz 20000 1|2|3`` (the same
generator ``rand_case``, the same address filter ``model_addresses_ok``, so the same 61,000
statements).  For each case this script prints one JSON line with the inputs and the result of
``tsuki.vm_vars`` (the model the engine equals on these cases): every variable read and write in
program order, the label, the argument buffer, the choice globals, yield / snapshot / UI flags and
the flowchart call.  The first line is a header with the memory layout of the initial state (fill window, pattern, the
Script_Prefetch copies); the last line is a summary.  ``tests/Tsuki.Tests.Data/Flow/VarsFuzzTests.cs``
runs the C# port on the same
inputs and compares every field.  Nothing is written to disk.

Initial memory (VarEmu.initial_word): the overrides, then for Script_Prefetch the copies of
var_F / var_Z / var_G, then the pattern fill inside FILL_LO..FILL_HI, else the eboot image of the
logged window.  That window (VA 0x1620000..0x1660000) lies past the file-backed part of the data
segment (it ends at DATA_VA + DATA_LEN = 0xDD66E0), i.e. in bss, so its image is all zero; the
script asserts that layout instead of loading the eboot.

    python -I tests/Tsuki.Tests.Data/Flow/vars_fuzz_oracle.py [--engine] [--limit N]

``--engine`` also runs every case through the engine's own code under unicorn
(``compare_one``; needs the game folder's eboot.bin, unicorn and capstone, about 20 min) and
reports the engine-vs-model mismatches in the summary line.  ``--limit N`` stops each run after N
cases (smoke tests).  The script puts the repository's tools/ folder on sys.path itself, so it
runs under ``python -I``.
"""
from __future__ import annotations

import json
import random
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import misc_vm_variables_errata as mv
from tsuki import vm_vars as vv

RUNS = ((1000, 20261025), (20000, 1), (20000, 2), (20000, 3))


class ImageEmu:
    """The part of VarEmu the model side needs: initial_word over an all-zero window image."""

    initial_word = mv.VarEmu.initial_word

    def window_word0(self, va: int) -> int:
        return 0


def _check_layout() -> None:
    assert mv.LOG_LO >= mv.DATA_VA + mv.DATA_LEN, "the logged window is not bss"
    assert mv.LOG_HI <= mv.DATA_VA + mv.DATA_MEMSZ, "the logged window is outside the data segment"


def _case_json(seed: int, i: int, text: str, pre: bool, f823: int, argbuf: bytes,
               init: Dict[int, int], flags: mv.Flags, eff: vv.Effect, st: vv.VarState) -> Dict[str, Any]:
    return {
        "seed": seed, "i": i, "text": text, "pre": pre, "f823": f823, "argbuf": argbuf.hex(),
        "init": [[va, v & 0xFFFF] for va, v in init.items()],
        "scan": flags.scan, "skip": flags.skip, "block": flags.block,
        "handled": eff.handled,
        "log": [[k, va, v] for k, va, v in st.mem.log],
        "goto": None if eff.goto is None else eff.goto.hex(),
        "yielded": eff.yielded, "snapshot": eff.snapshot, "choice_ui": eff.choice_ui,
        "choice_point": eff.choice_point,
        "argbuf_out": bytes(st.argbuf).hex(),
        "kind": st.choice_kind, "index": st.choice_index & 0xFFFF, "id": st.choice_id & 0xFFFF,
        "mode": st.choice_mode, "count": st.choice_count,
        "values": [v & 0xFFFF for v in st.choice_values],
        "locked": [bool(x) for x in st.choice_locked],
    }


def main(argv: List[str]) -> int:
    engine = "--engine" in argv
    limit: Optional[int] = None
    if "--limit" in argv:
        limit = int(argv[argv.index("--limit") + 1])
    _check_layout()
    emu: Any = ImageEmu()
    real: Any = None
    if engine:
        real = mv.VarEmu(mv._eboot())
        if any(real.window0):
            raise SystemExit("the eboot image of the logged window is not all zero")
    out = sys.stdout
    out.write(json.dumps({"header": True, "log_lo": mv.LOG_LO, "fill_lo": mv.FILL_LO, "fill_hi": mv.FILL_HI,
                          "save_areas": [list(a) for a in mv.SAVE_AREAS],
                          "pattern": "0x2000 + ((va - log_lo) // 2) % 0xC000 inside fill_lo..fill_hi, else 0",
                          "pattern_check": [[va, mv.pattern(va)] for va in (mv.FILL_LO, vv.VAR_F, vv.VAR_G,
                                                                             mv.FILL_HI - 2)]}) + "\n")
    total = skipped = mismatches = 0
    t0 = time.time()
    for n, seed in RUNS:
        if limit is not None:
            n = min(n, limit)
        rng = random.Random(seed)
        run = 0
        while run < n:
            text, pre, f823, argbuf, small, flags = mv.rand_case(rng)
            if not mv.model_addresses_ok(emu, text, pre, f823, argbuf, small, flags):
                skipped += 1
                continue
            stmt = text.encode("latin-1") + b"_DMMY();"
            init = {vv.VAR_F + 2 * vv.MSG_INDEX_VAR: f823}
            init.update(small)
            eff, st = mv._model_run(emu, stmt, mv.pattern, init, argbuf, pre, flags)
            if real is not None and mv.compare_one(real, text, pre, f823, argbuf, small, flags):
                mismatches += 1
            out.write(json.dumps(_case_json(seed, run, text, pre, f823, argbuf, init, flags, eff, st),
                                 separators=(",", ":")))
            out.write("\n")
            run += 1
            total += 1
    out.write(json.dumps({"summary": True, "cases": total, "skipped": skipped, "engine": engine,
                          "engine_mismatches": mismatches if engine else None,
                          "seconds": round(time.time() - t0, 1),
                          "runs": [[n, s] for n, s in RUNS]}) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
