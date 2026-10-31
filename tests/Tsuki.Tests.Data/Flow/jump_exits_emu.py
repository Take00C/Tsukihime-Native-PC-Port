"""Package C2 evidence: where ScriptStep goes after a jump's label lookup (eboot under unicorn).

The label lookup VA 0x794D0 is stubbed to return a pointer to ``_WKST(F7,eq,99);_GOTO(X);`` and the
statement under test is followed by ``_WKST(F8,eq,77);_GOTO(Y);``.  ScriptStep then runs on until it
returns (VA 0x9B516; the trailing GOTOs make it return).  A write of F7 means the loop continued at the
label in the same call, F8 means it continued with the next statement, no write means the statement
itself returned (yield) with the cursor at the label.

Measured on CUSA47999 v01.01 (docs/status/wave2/C2.md):
    _IF__ false -> yield at the label      _GOTO -> yield at the label
    _GOTS       -> continue at the label   _IFNX(,LBL) -> continue at the label
    _IFNX(,)    -> next statement          _IF__ true -> next statement

    python -I tests/Tsuki.Tests.Data/Flow/jump_exits_emu.py      (needs eboot.bin, unicorn, capstone)
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))

from tsuki import misc_vm_variables_errata as mv  # noqa: E402
from tsuki import vm_vars as vv  # noqa: E402

emu = mv.VarEmu(mv._eboot())
mv.LABEL_LOOKUP = -1                      # VarEmu stops at the lookup by default; run through it instead
TARGET = mv.BUF + 0x200
for stmt, f1 in ((b"_GOTS(_D001,A,B);", 0), (b"_IFNX(,LBL);", 0), (b"_IFNX(,);", 0), (b"_IF__(F1,eq,1,LBL);", 1),
                 (b"_IF__(F1,eq,1,LBL);", 0), (b"_GOTO(LBL);", 0)):
    emu._prepare(stmt + b"_WKST(F8,eq,77);_GOTO(Y);", mv.pattern, {vv.VAR_F + 2: f1}, None, None)
    emu.uc.mem_write(TARGET, b"_WKST(F7,eq,99);_GOTO(X);\0")
    emu.redirects = {mv.SNAPSHOT_BODY: mv.SNAPSHOT_EXIT}
    emu.go_va, emu.stops = mv.MAIN_GO, (mv.MAIN_YIELD,)
    emu.stubs = {0x794D0: TARGET}
    emu.set_reg("RDI", mv.BUF)
    emu._start(mv.MAIN_ENTRY, 2_000_000)
    r = emu._finish()
    w = [(vv.address_name(a), v) for k, a, v in r.log if k == "w"]
    print(stmt.decode(), "F1=%d" % f1, "end=", r.end and hex(r.end), "writes=", w, "err=", r.err)
