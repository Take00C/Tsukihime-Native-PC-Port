"""Tests for tsuki.misc_seld: _SELD's per-line loop (docs/formats/vm_variables.md section 16).

Model tests always run.  Engine tests run the eboot's own Script_Step under unicorn (state
carried between the statements of a choice block) and compare it with the model; they skip
when eboot.bin or unicorn / capstone is missing.  Corpus tests skip without allscr.mrg.
Nothing is written to disk.  Runtime with the game: about 25 s.
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_misc_seld.py -q
"""
from __future__ import annotations

import faulthandler
import importlib.util
import sys
from pathlib import Path
from typing import Iterator, List, Tuple

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import game_dir, image_dir  # noqa: E402
from tsuki import misc_seld as ms  # noqa: E402
from tsuki import vm_vars as vv  # noqa: E402

_GD = game_dir()
EBOOT = (_GD / "eboot.bin") if _GD is not None else None
HAVE_EBOOT = EBOOT is not None and EBOOT.exists()
HAVE_EMU = importlib.util.find_spec("unicorn") is not None and importlib.util.find_spec("capstone") is not None
needs_emu = pytest.mark.skipif(not HAVE_EBOOT or not HAVE_EMU or image_dir() is None,
                               reason="eboot.bin, game data or unicorn/capstone not available")
needs_game = pytest.mark.skipif(image_dir() is None, reason="game data not found")


def F(i: int) -> int:
    return vv.VAR_F + 2 * i


def _ctx(**kw: object) -> ms.ChoiceContext:
    return ms.model_context(ms.Scenario(**kw))   # type: ignore[arg-type]


def _run(ctx: ms.ChoiceContext, text: str, stops: int = 1) -> Tuple[bytes, List[ms.Step]]:
    buf = (";" + text).encode("latin-1")
    return buf, ms.walk(ctx, buf, 1, {}, stops=stops)


# ---------------------------------------------------------------------------
# model
# ---------------------------------------------------------------------------

def test_slot_text() -> None:
    assert [ms.prefix_line(k) for k in (0, 1, 8, 9, 10, -1)] == [43960, 43961, 43968, 43959, 43959, 43959]
    assert ms.selr_slot_text(0, b"$042569") == b"$043960$042569"
    assert ms.selr_slot_text(9, b"") == b"$043959"
    b = b"_SELR(0,`001:$000123);"
    assert ms.selr_arg_text(b, 6) == b"$000123"
    assert ms.selr_arg_text(b"_SELR(1);", 6) == b""
    assert ms.selr_arg_text(b"_SELR(2,);", 6) == b""


def test_loop_gate_open() -> None:
    """Retail case: one kind-12 record per line, F799 = 1 only for the last line, the record
    text is the line slot, no Staging_SetSnapshot (the pushed record has kind 12)."""
    ctx = _ctx(f={823: 3})
    buf, steps = _run(ctx, "_SELR(0,$042569);_SELR(1,$042570);_SELR(2,$042571);_SELD(_D000,1);")
    seld = steps[-1]
    end = len(buf)
    assert seld.log == [("w", F(799), 0), ("r", F(929), 0), ("r", F(1102), 0),
                        ("w", F(799), 0), ("r", F(929), 0), ("r", F(1102), 0),
                        ("w", F(799), 1), ("r", F(929), 0), ("r", F(1102), 0)]
    lines = seld.lines or []
    assert [(ev.k, ev.f799, ev.pushed, ev.text) for ev in lines] == [
        (0, 0, True, b"$043960$042569"), (1, 0, True, b"$043961$042570"), (2, 1, True, b"$043962$042571")]
    assert all(ev.staging is None and ev.cleared_rewind_block for ev in lines)
    pushed = ctx.ring.pushed
    assert [(r.kind, r.f799) for r in pushed] == [(13, 0), (12, 0), (12, 0), (12, 1)]
    assert all(r.cmd_offset == r.resume_offset == end for r in pushed[1:])
    assert pushed[0].cmd_offset == 1 - 1 and pushed[0].resume_offset == len(";_SELR(0,$042569);")
    assert ctx.text_buffer == b"$043962$042571" and ctx.rewind_block == 0 and ctx.staging_calls == []
    assert ctx.vars.choice_count == 3 and ctx.vars.mem.peek(F(799)) == 1
    assert seld.effect is not None and seld.effect.yielded and seld.effect.choice_point == 1


def test_loop_gate_shut_overwrites_newest_record() -> None:
    """F929 != 0: nothing is pushed; every line text overwrites the newest record, and with
    F823 <= 1 every line calls Staging_SetSnapshot(rec, top + 1, &stack[top], &stack[top-1])."""
    ctx = _ctx(f={929: 1, 823: 1}, scene_top=2, pre_records=5)
    _buf, steps = _run(ctx, "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);")
    lines = steps[-1].lines or []
    assert [ev.pushed for ev in lines] == [False, False]
    assert [ev.record for ev in lines] == [4, 4]
    assert ctx.ring.records[4].text == b"$043961$000002"
    assert [(c.record, c.n, c.stack_b, c.stack_c) for c in ctx.staging_calls] == [(4, 3, 2, 1), (4, 3, 2, 1)]
    assert steps[-1].log == [("w", F(799), 0), ("r", F(929), 1), ("r", F(823), 1), ("r", ms.SCENE_TOP_VA, 2),
                             ("w", F(799), 1), ("r", F(929), 1), ("r", F(823), 1), ("r", ms.SCENE_TOP_VA, 2)]
    ctx = _ctx(f={929: 1, 823: 2})
    _run(ctx, "_SELR(0,$000001);_SELD(_D000,);")
    assert ctx.staging_calls == [] and ctx.rewind_block == 0


def test_loop_redisplay_and_scan() -> None:
    ctx = _ctx(f={929: 1}, redisplay=1)
    _run(ctx, "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);")
    assert ctx.rewind_block is None and ctx.ring.records[7].text == b"$000000"
    assert ctx.vars.mem.peek(F(799)) == 1                       # F799 is written regardless
    ctx = _ctx(scan=True, f={799: 7})
    _buf, steps = _run(ctx, "_SELR(0,$000001);_SELD(_D000,);")
    assert steps[-1].lines == [] and steps[-1].log == [] and ctx.vars.mem.peek(F(799)) == 7


def test_count_not_reset_and_vm_vars_unchanged() -> None:
    """A second group without the choice UI in between loops over all registered lines; the
    vm_vars part of _SELD (Effect, target, id) is exactly vm_vars.execute's."""
    ctx = _ctx()
    _buf, steps = _run(ctx, "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,1);_SELR(5,$000003);_SELD(_D000,2);",
                       stops=2)
    assert len(steps[-1].lines or []) == 3 and ctx.vars.choice_values == [0, 1, 5]
    st = vv.VarState()
    for t in ("_SELR(0,$000001);", "_SELR(1,$000002);"):
        vv.execute_text(t, st)
    eff, _ = vv.execute_text("_SELD(_D000,1);", st)
    ctx = _ctx()
    _buf, steps = _run(ctx, "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,1);")
    assert steps[-1].effect == eff
    assert (ctx.vars.choice_kind, ctx.vars.choice_index, ctx.vars.choice_id, ctx.vars.choice_mode) == \
        (st.choice_kind, st.choice_index, st.choice_id, st.choice_mode)


def test_pgst_writes_f1122() -> None:
    ctx = _ctx()
    _buf, steps = _run(ctx, "_PGST(10011);_SELD(_D000,);")
    assert steps[0].log == [("w", F(1122), 10011)]


# ---------------------------------------------------------------------------
# engine
# ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def blocks() -> ms.BlockList:
    return list(ms.retail_blocks())


@pytest.fixture(scope="module")
def emu() -> Iterator[ms.SeldEmu]:
    was = faulthandler.is_enabled()
    faulthandler.disable()          # unicorn handles access violations internally
    try:
        yield ms.SeldEmu(EBOOT)
    finally:
        if was and sys.__stderr__ is not None:
            faulthandler.enable(file=sys.__stderr__)


@needs_emu
def test_engine_synthetic_cases(emu: ms.SeldEmu) -> None:
    """Every SYNTHETIC_CASES entry (gate shut three ways, re-display, F823, scene top, scene
    jump, scan mode, effect bits, 11 lines, argument forms, two groups, ring wrap): engine and
    model agree on every logged variable access, push, record, slot and global."""
    res = ms.synthetic_check(emu)
    assert len(res) == len(ms.SYNTHETIC_CASES)
    assert [(n, d) for n, d in res if d] == []


@needs_emu
def test_engine_worked_example(emu: ms.SeldEmu, blocks: ms.BlockList) -> None:
    """Script 4's first choice (vm_variables.md 16.4), run from its _ATSV(1)."""
    b, s, labels = next(x for x in blocks if x[0].script == 4)
    sc = ms.scenario_for(b, 0, "retail")
    diffs, run, ctx, steps = ms.compare_block(emu, s, b.start, labels, sc, b.script)
    assert diffs == []
    assert [(c, r, k) for c, r, k, body in run.pushes if body] == [
        (46577, 46587, 7), (46586, 46604, 13), (46653, 46653, 12), (46653, 46653, 12), (46653, 46653, 12)]
    assert [(run.records[i].kind, run.records[i].f799, run.records[i].text) for i in (10, 11, 12)] == [
        (12, 0, b"$043960$042569"), (12, 0, b"$043961$042570"), (12, 1, b"$043962$042571")]
    assert run.text_buffer == b"$043962$042571" and run.rewind_block == 0 and run.staging == []
    assert run.count == 3 and run.f799 == 1


@needs_emu
def test_engine_retail_sample(emu: ms.SeldEmu, blocks: ms.BlockList) -> None:
    """Every 12th retail block, _IF__ variables 0 / 1, four flag scenarios: 0 mismatches (the
    CLI ``retail --flags all`` runs all 170 blocks and 17 scenarios)."""
    res = ms.retail_check(emu, ("retail", "F929=1", "re-display, F929=1", "F929=1, F823=1, top=-1"),
                          (0, 1), every=12, blocks=blocks)
    assert res.runs == 120
    assert res.mismatches == 0, res.first
    assert res.seld_staging > 0 and res.pushes12 > 0


@needs_emu
def test_engine_conditional_blocks(emu: ms.SeldEmu, blocks: ms.BlockList) -> None:
    """The retail blocks with _IF__ / _GOTO between the lines (scripts 10, 43, 70, 211, 361,
    433, 492), each with the condition variables 0, 1 and 5."""
    n = 0
    for b, s, labels in blocks:
        if not b.if_vars:
            continue
        for a in (0, 1, 5):
            sc = ms.scenario_for(b, a, "retail")
            diffs, _run, _ctx, _steps = ms.compare_block(emu, s, b.start, labels, sc, b.script)
            assert diffs == [], (b.script, a, diffs)
            n += 1
    assert n == 21


# ---------------------------------------------------------------------------
# corpus
# ---------------------------------------------------------------------------

@needs_game
def test_pending_census(blocks: ms.BlockList) -> None:
    c = ms.pending_census((0,), blocks)
    assert c["blocks"] == 170 and c["with_atsv1"] == 83 and c["with_if"] == 7
    per = c["pending_lines"]["0"]                       # type: ignore[index]
    assert min(per) >= 1 and max(per) == 10 and sum(per.values()) == 170
    assert c["pending_total"]["0"] == 814               # type: ignore[index]
