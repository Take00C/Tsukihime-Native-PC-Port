"""``_SELD``'s per-line loop: what the choice command does for every pending ``_SELR`` line.

Reference: docs/formats/vm_variables.md section 16 (eboot CUSA47999 v01.01, text VAs).
This module ADDS to ``tsuki.vm_vars`` without changing it: ``vm_vars._seld`` models the
target decode, the choice id and the yield; the loop that runs before them
(Script_Step VA 0x9B215..0x9B430) is modelled here.

For k = 0 .. count-1 (count = s16 0x1750646, the ``_SELR`` lines registered since the
last reset; the loop re-reads it every iteration)::

    F[799] = (k == count - 1)                         # 0x9B273..0x9B281, 16-bit store
    History_Snapshot(stmt_end, stmt_end, kind 12)     # 0x9B288; gate vm_vars.snapshot_gate;
                                                      # a 4th argument (ecx = F799) is ignored
    if byte 0x174D9A8 == 0:                           # "re-displayed by a rewind" flag (the
                                                      # push clears it, so after a push: always)
        rec = ring[cur]                               # the record just pushed, or (gate shut)
                                                      # the newest older record
        text buffer 0x174D9B0[0..0xFA) = slot k       # slot k = 0x174FB70 + 0xFA*k
        rec[+0x38FE..+0x39F6) = slot k [0..0xF8); rec[+0x39F6] = u16 0
        if rec.blocking == 0 and rec.kind not in (11, 12) and s16(F823) <= 1:
            top = s16(word 0x164B1C0)                 # scene-stack top
            Staging_SetSnapshot(rec, top + 1, top >= 0 ? &stack[top] : 0,
                                top > 0 ? &stack[top - 1] : 0)          # 0x73980
        dword 0xDF29D4 = 0                            # (rewind start is refused while != 0)

With the snapshot gate open (retail) every line is pushed as a kind-12 history record whose
command and resume offsets are both the end of the ``_SELD`` statement, whose F799 copy is 1
only for the last line and whose text is the line text ``"$0439NN" + argument 1`` (the
``_SELR`` slot, :func:`selr_slot_text`).  Because the pushed record has kind 12,
Staging_SetSnapshot is never called from the loop in that case.

Three layers:

* the model: :class:`HistoryRing` (only the record fields the choice commands touch),
  :class:`ChoiceContext` (vm_vars.VarState + ring + the globals above), :func:`selr`,
  :func:`seld`, :func:`seld_line_loop`, :func:`atsv_choice_point` and the walker
  :func:`walk` that runs a choice block (``_ATSV(1)``, ``_SELR``, ``_IF__``, ``_GOTO``,
  markers, ``_SELD``) statement by statement;
* :class:`SeldEmu`: the engine's own Script_Step (VA 0x95E60) on the same block under unicorn
  (``tsuki.misc_adv_system_state_emu.AdvEmu``, VEX executed in Python), with the state
  carried from statement to statement;
* :func:`compare_block` / :func:`retail_blocks` / :func:`retail_check`: engine vs model on the
  170 retail ``_SELD`` blocks and on flag scenarios (gate shut, re-display, F823, scene top,
  blocking effects, scene jump, scan mode).

CLI::

    python -m tsuki.misc_seld model "_SELR(0,$1);_SELR(1,$2);_SELD(_D000,1);" [--set F929=1]
    python -m tsuki.misc_seld census
    python -m tsuki.misc_seld retail [--limit N] [--json OUT]
    python -m tsuki.misc_seld example
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import TYPE_CHECKING, Callable, Dict, Iterator, List, Optional, Sequence, Tuple, Union

from tsuki import vm_vars as vv
from tsuki.script import inline_int

if TYPE_CHECKING:
    from tsuki.misc_adv_system_state_emu import AdvEmu
    from tsuki.script import AllScr

__all__ = [
    "LINE_SLOTS_VA", "LINE_SLOT_SIZE", "LINE_SLOT_COUNT", "REDISPLAY_VA", "TEXT_BUFFER_VA",
    "REWIND_BLOCK_VA", "SCENE_TOP_VA", "SCENE_JUMP_VA", "LAST_LINE_VAR", "KIND_SELD", "KIND_SELR",
    "KIND_CHOICE_SAVE", "prefix_line", "selr_slot_text", "selr_arg_text", "RingRecord", "HistoryRing",
    "StagingCall", "LineEvent", "ChoiceContext", "Step", "history_push", "staging_allowed",
    "seld_line_loop", "seld", "selr", "atsv_choice_point", "statement_at", "walk", "SeldEmu",
    "EngineRun", "Scenario", "compare_block", "retail_blocks", "retail_check", "pending_census",
]

# --------------------------------------------------------------------------------------
# addresses and constants (CUSA47999 v01.01)
# --------------------------------------------------------------------------------------
SELD_HANDLER = 0x9B1DC                 # Script_Step's _SELD case
SELD_LOOP = (0x9B215, 0x9B430)         # count test .. last instruction of the per-line loop
LINE_SLOTS_VA = 0x174FB70              # _SELR line slots, 0xFA bytes each
LINE_SLOT_SIZE = 0xFA
LINE_SLOT_COUNT = 11                   # 0x174FB70 + 11 * 0xFA = 0x1750626 < values 0x1750630
CHOICE_COUNT_VA = 0x1750646            # u16: _SELR lines registered
REDISPLAY_VA = 0x174D9A8               # u8: 1 after a rewind re-displayed a line (History_Push -> 0)
TEXT_BUFFER_VA = 0x174D9B0             # current message text (0xFA bytes)
REWIND_BLOCK_VA = 0xDF29D4             # dword: != 0 refuses the rewind start (0x61775)
SCENE_TOP_VA = 0x164B1C0               # i16: scene-stack top (-1 = empty)
SCENE_JUMP_VA = 0x120A6D2              # u16: "next scene" skip running
LAST_LINE_VAR = 799                    # F799: 1 = last message of the page / last choice line
KIND_SELD, KIND_SELR, KIND_CHOICE_SAVE = 12, 13, 7
RING_RECORDS = 800
REC_SIZE = 0x3A10
REC_TEXT, REC_TEXT_COPY, REC_TEXT_END = 0x38FE, 0xF8, 0x39F6
REC_BLOCKING, REC_KIND = 0x3638, 0x24
PREFIX_FIRST, PREFIX_TENTH = 43960, 43959      # ScriptText "1. " .. "9. ", "0. "
STAGING_VA = 0x73980
HISTORY_PUSH_VA = 0x7AF90
HISTORY_BODY = (0x7AFDD, 0x7B4B0)      # History_Push after its gate (accesses filtered from logs)
SCRIPT_STEP_VA = 0x95E60
DISPATCH_VA = 0x95F4E                  # jmp rdx: r14 = statement name (after '_')
LABEL_LOOKUP_VA = 0x794D0
SCENE_JUMP_END_VA = 0x7E620
SCENE_STACK_PUSH_VA = 0x7E2E0
SCENE_STACK_VA = 0x120A700
F799_S8_SLOT = vv.HISTORY_S8.index(LAST_LINE_VAR)   # record +0x3501 + slot: s8 copy of F799
F823 = vv.MSG_INDEX_VAR
PAGE_VAR = 1122                        # F1122: written by _PGST(n) (VA 0x97852)
EFFECT_MASK = 0x3FFF                   # 14 effect slots (0x5C680(i), i < 14)


def _s16(v: int) -> int:
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def _cstr(b: bytes) -> bytes:
    i = b.find(b"\0")
    return b if i < 0 else b[:i]


# --------------------------------------------------------------------------------------
# _SELR line slots
# --------------------------------------------------------------------------------------

def prefix_line(k: int) -> int:
    """ScriptText line of the number prefix of slot k (VA 0x993E0..0x9940A: ``edx = 0xABB8 +
    k; cmp r15, 9; cmovae edx, 0xABB7``, an unsigned compare of the s16 slot index): 43960 + k
    ("1. ".."9. ") for k = 0..8, 43959 ("0. ") otherwise."""
    return PREFIX_FIRST + k if 0 <= k < 9 else PREFIX_TENTH


def selr_arg_text(buf: bytes, p0: int, index: int = 1) -> bytes:
    """Argument ``index`` as VA 0x76EA0 copies it for ``_SELR`` (VA 0x993D4): argument start as
    the inline reader finds it (missing -> empty), a leading backtick skips 5 bytes, then bytes
    up to the first ')' ',' ';'."""
    p = vv.arg_start(buf, p0, index) if index > 0 else p0
    if p is None:
        return b""
    if p < len(buf) and buf[p] == 0x60:
        p += 5
    out = bytearray()
    while p < len(buf) and buf[p] not in (0x29, 0x2C, 0x3B):
        out.append(buf[p])
        p += 1
    return bytes(out)


def selr_slot_text(k: int, arg_text: bytes) -> bytes:
    """C string of line slot k after ``_SELR``: ``"$"`` + 6 decimal digits of
    :func:`prefix_line` (VA 0x99427..0x994DF, byte 7 = NUL) + ``strcat`` of argument 1 (VA
    0x994E5).  Bytes after the NUL keep their previous contents (not modelled)."""
    return b"$%06d" % prefix_line(k) + _cstr(arg_text)


# --------------------------------------------------------------------------------------
# history ring (only what the choice commands need; adv_state.md 2)
# --------------------------------------------------------------------------------------

@dataclass
class RingRecord:
    index: int
    serial: int
    kind: int
    cmd_offset: int
    resume_offset: int
    f799: int                  # u8: low byte of F799 at push time (record +0x3501 + s8 slot)
    text: bytes = b""          # C string at +0x38FE (a push writes byte 0 = 0)
    blocking: int = 0          # effect_blocking_bits (+0x3638)
    text_end_zero: bool = False   # +0x39F6 written 0 by a text copy


@dataclass
class HistoryRing:
    """History_Push's ring indices (adv_state.md 2.2): cur 0x120A6EC, next 0x120A6EE,
    wrapped 0x120A6F0, oldest 0x120A6F2, serial counter 0x120A6EA."""
    records: Dict[int, RingRecord] = field(default_factory=dict)
    cur: int = 0
    next: int = 0
    oldest: int = 0
    wrapped: bool = False
    counter: int = 0
    pushed: List[RingRecord] = field(default_factory=list)    # records pushed, in order

    def push(self, kind: int, cmd: int, resume: int, f799: int, blocking: int) -> RingRecord:
        k = self.next
        rec = RingRecord(k, self.counter, kind, cmd, resume, f799 & 0xFF, b"", blocking)
        c = _s16(self.counter + 1)
        self.counter = c if c > 0 else 0
        self.records[k] = rec
        self.pushed.append(rec)
        self.cur = k
        n = k + 1
        if n >= RING_RECORDS:
            n, self.wrapped = 0, True
        if n == self.oldest:
            self.oldest = n + 1 if n < RING_RECORDS - 1 else 0
        self.next = n
        if n in self.records:
            self.records[n].serial = -1
        return rec

    @classmethod
    def with_history(cls, n: int, kind: int = 0, text: bytes = b"", f799: int = 0,
                     blocking: int = 0) -> "HistoryRing":
        """A ring holding n valid records 0..n-1 (serials 0..n-1), the newest (cur = n-1)
        with ``kind``, ``text``, ``f799`` and ``blocking``; the others kind 0, empty."""
        r = cls(cur=max(n - 1, 0), next=n, counter=n)
        for i in range(n):
            last = i == n - 1
            r.records[i] = RingRecord(i, i, kind if last else 0, 0, 0, f799 if last else 0,
                                      text if last else b"", blocking if last else 0)
        r.records.setdefault(n, RingRecord(n, -1, 0, 0, 0, 0))
        return r


@dataclass
class StagingCall:
    """Staging_SetSnapshot(rec, n, recB, recC) (VA 0x73980) as ring / scene-stack indices."""
    record: int
    n: int
    stack_b: Optional[int]       # &scene_stack[top] when top >= 0
    stack_c: Optional[int]       # &scene_stack[top - 1] when top > 0 (ignored by the callee)
    source: str = "SELD"


@dataclass
class LineEvent:
    """One iteration of the per-line loop."""
    k: int
    f799: int
    pushed: bool                      # a kind-12 record was pushed (gate open)
    record: Optional[int] = None      # ring index whose text was overwritten (None: re-display)
    text: Optional[bytes] = None      # the slot text copied
    staging: Optional[StagingCall] = None
    cleared_rewind_block: bool = False


@dataclass
class ChoiceContext:
    """Engine state the choice block touches.  ``vars.mem`` also holds the scene-stack top
    (word SCENE_TOP_VA), because the engine reads it with a logged 16-bit access."""
    vars: vv.VarState = field(default_factory=vv.VarState)
    ring: HistoryRing = field(default_factory=HistoryRing)
    redisplay: int = 0                       # u8 0x174D9A8
    text_buffer: bytes = b""                 # 0x174D9B0 (C string)
    rewind_block: Optional[int] = None       # dword 0xDF29D4 (None: not written)
    slots: Dict[int, bytes] = field(default_factory=dict)   # line slots (C strings)
    scene_jump: int = 0                      # u16 0x120A6D2
    effect_blocking: int = 0                 # OR of 0x5C680(i), i < 14 (pushes, scene stack)
    staging_calls: List[StagingCall] = field(default_factory=list)
    scene_jump_ends: int = 0                 # calls of 0x7E620
    scene_pushes: int = 0                    # SceneStack_Push calls that pushed
    scan_target: int = -1                    # dword 0x873040: page Script_Scan looks for (image: -1)

    @property
    def scene_top(self) -> int:
        return _s16(self.vars.mem.peek(SCENE_TOP_VA))

    @scene_top.setter
    def scene_top(self, v: int) -> None:
        self.vars.mem.poke(SCENE_TOP_VA, v)


def history_push(ctx: ChoiceContext, cmd: int, resume: int, kind: int) -> Optional[RingRecord]:
    """History_Snapshot / History_Push (VA 0x7AF90): the gate with the engine's reads
    (``vm_vars.snapshot_gate``), then a new ring record; the push also clears 0x174D9A8."""
    if not vv.snapshot_gate(ctx.vars, kind):
        return None
    f799 = ctx.vars.mem.peek(vv.VAR_F + 2 * LAST_LINE_VAR)
    rec = ctx.ring.push(kind, cmd, resume, f799, ctx.effect_blocking & EFFECT_MASK)
    ctx.redisplay = 0
    return rec


def staging_allowed(ctx: ChoiceContext, rec: RingRecord, source: str) -> Optional[StagingCall]:
    """The 'snapshot if allowed' block shared by ``_SELD`` (0x9B3B4..0x9B42B) and ``_ATSV``
    (0x989AF..0x98A16): rec.blocking == 0, rec.kind not 11 / 12, then s16(F823) <= 1 (read),
    then the scene-stack top (read) gives the arguments."""
    if rec.blocking != 0 or rec.kind in (11, 12):
        return None
    if _s16(ctx.vars.mem.read(vv.VAR_F + 2 * F823)) > 1:
        return None
    top = _s16(ctx.vars.mem.read(SCENE_TOP_VA))
    call = StagingCall(rec.index, top + 1, top if top >= 0 else None, top - 1 if top > 0 else None, source)
    ctx.staging_calls.append(call)
    return call


def _copy_text(ctx: ChoiceContext, rec: RingRecord, text: bytes) -> None:
    rec.text = _cstr(text)[:REC_TEXT_COPY]
    rec.text_end_zero = True


def seld_line_loop(ctx: ChoiceContext, stmt_end: int) -> List[LineEvent]:
    """The per-line loop of ``_SELD`` (VA 0x9B215..0x9B430), run after the scan-mode exit
    and the scene-jump end and before the target decode.  ``stmt_end`` = offset of the byte
    after the statement's ';' (Script_Step's rbx), passed as both History_Snapshot pointers."""
    st = ctx.vars
    out: List[LineEvent] = []
    k = 0
    while k < _s16(st.choice_count):
        last = 1 if k == _s16(st.choice_count) - 1 else 0
        st.mem.write(vv.VAR_F + 2 * LAST_LINE_VAR, last)
        rec = history_push(ctx, stmt_end, stmt_end, KIND_SELD)
        ev = LineEvent(k, last, rec is not None)
        if ctx.redisplay == 0:
            cur = ctx.ring.records.setdefault(ctx.ring.cur, RingRecord(ctx.ring.cur, -1, 0, 0, 0, 0))
            slot = ctx.slots.get(k, b"")
            ctx.text_buffer = _cstr(slot)[:LINE_SLOT_SIZE]
            _copy_text(ctx, cur, slot)
            ev.record, ev.text = cur.index, cur.text
            ev.staging = staging_allowed(ctx, cur, "SELD")
            ctx.rewind_block = 0
            ev.cleared_rewind_block = True
        out.append(ev)
        k += 1
    return out


@dataclass
class Step:
    """One statement run by :func:`walk`."""
    offset: int                    # offset of the name (byte after '_')
    end: int                       # offset after the statement (Script_Step's rbx)
    name: str
    log: List[Tuple[str, int, int]]
    effect: Optional[vv.Effect] = None
    lines: Optional[List[LineEvent]] = None
    pushed: Optional[int] = None   # ring index pushed by this statement (ATSV / SELR)
    goto: Optional[bytes] = None


def seld(ctx: ChoiceContext, buf: bytes, name_pos: int, end: int) -> Tuple[vv.Effect, List[LineEvent]]:
    """Full ``_SELD``: scan mode yields at once (VA 0x9B1E3); else mode 1, a running scene
    jump is ended (0x7E620, word 0x120A6D2 = 0), the per-line loop, then
    ``vm_vars.execute`` for the target / id / UI part."""
    if ctx.vars.scan_mode:
        return vv.execute(buf, name_pos, ctx.vars), []
    if ctx.scene_jump:
        ctx.scene_jump_ends += 1
        ctx.scene_jump = 0
    lines = seld_line_loop(ctx, end)
    return vv.execute(buf, name_pos, ctx.vars), lines


def selr(ctx: ChoiceContext, buf: bytes, name_pos: int, end: int) -> Tuple[vv.Effect, Optional[RingRecord]]:
    """``_SELR`` (VA 0x9936D): ``vm_vars`` semantics, plus the kind-13 push of the first line
    (cmd = the ';' before the name, resume = statement end; VA 0x99395..0x993A0) and the line
    slot text."""
    k = ctx.vars.choice_count
    first = k == 0
    f799 = ctx.vars.mem.peek(vv.VAR_F + 2 * LAST_LINE_VAR)
    eff = vv.execute(buf, name_pos, ctx.vars)
    rec = None
    if first and eff.snapshot:
        rec = ctx.ring.push(KIND_SELR, name_pos - 2, end, f799, ctx.effect_blocking & EFFECT_MASK)
        ctx.redisplay = 0
    ctx.slots[_s16(k)] = selr_slot_text(_s16(k), selr_arg_text(buf, name_pos + 5, 1))
    return eff, rec


def atsv_choice_point(ctx: ChoiceContext, name_pos: int, end: int) -> Optional[RingRecord]:
    """``_ATSV(1)`` (adv_state.md 2.5; the context of 83 retail choices; only this form, with
    no scene jump running and not in scan mode): kind-7 push (0x97EA4), then unless
    re-displayed: record text = text buffer (0xF8 bytes), the staging block, dword 0xDF29D4 =
    0; then SceneStack_Push (top += 1 unless an effect slot blocks; 0x7E2E0)."""
    if ctx.vars.scan_mode or ctx.scene_jump:
        raise NotImplementedError("_ATSV(1) in scan mode / during a scene jump")
    rec = history_push(ctx, name_pos - 2, end, KIND_CHOICE_SAVE)
    if ctx.redisplay == 0:
        cur = ctx.ring.records.setdefault(ctx.ring.cur, RingRecord(ctx.ring.cur, -1, 0, 0, 0, 0))
        _copy_text(ctx, cur, ctx.text_buffer)
        staging_allowed(ctx, cur, "ATSV")
        ctx.rewind_block = 0
    if ctx.effect_blocking & EFFECT_MASK == 0:
        ctx.vars.mem.poke(SCENE_TOP_VA, ctx.scene_top + 1)
        ctx.scene_pushes += 1
    return rec


# --------------------------------------------------------------------------------------
# walker: runs a choice block statement by statement
# --------------------------------------------------------------------------------------
_MARKER = re.compile(rb"Z[YZ][0-9A-Fa-f]{5}\(")


def statement_at(buf: bytes, pos: int) -> Tuple[int, int]:
    """(name offset or -1, end) of the statement Script_Step reads at ``pos`` (VA
    0x95EC0..0x95F07): up to and including the next ';' within 0x300 bytes, else 0x300 bytes;
    the name follows the first '_' in it."""
    semi = buf.find(b";", pos, pos + 0x300)
    end = semi + 1 if semi >= 0 else pos + 0x300
    u = buf.find(b"_", pos, end)
    return (u + 1 if u >= 0 else -1), end


def walk(ctx: ChoiceContext, buf: bytes, start: int, labels: Dict[str, int],
         stop_names: Sequence[str] = ("SELD",), stops: int = 1, max_steps: int = 2000) -> List[Step]:
    """Run statements from offset ``start`` until ``stops`` statements named in ``stop_names``
    have run (``_SELD`` yields: Script_Step returns after it).  Supported:
    ``_ATSV(1)``, ``_SELR``, ``_SELD``, ``_IF__``, ``_GOTO``, the ``_WK*`` commands, ``_ZY`` /
    ``_ZZ`` markers (no variable effect; they would push kind 9 when F991 != 0, which is
    refused) and ``_PGST(n)`` (F1122 = n).  ``labels``: label name -> offset of its marker's '_'."""
    steps: List[Step] = []
    pos = start
    for _ in range(max_steps):
        name_pos, end = statement_at(buf, pos)
        if name_pos < 0:
            pos = end
            continue
        name = buf[name_pos:name_pos + 4].decode("latin-1")
        ctx.vars.mem.log.clear()
        step = Step(name_pos, end, name, [])
        nxt = end
        if name == "SELD":
            step.effect, step.lines = seld(ctx, buf, name_pos, end)
        elif name == "SELR":
            step.effect, rec = selr(ctx, buf, name_pos, end)
            step.pushed = rec.index if rec else None
        elif name == "ATSV":
            if buf[name_pos + 4:end] != b"(1);":
                raise NotImplementedError(f"walker supports only _ATSV(1): {buf[name_pos - 1:end]!r}")
            rec = atsv_choice_point(ctx, name_pos, end)
            step.pushed = rec.index if rec else None
        elif name == "GOTO":
            vv.copy_arg(buf, name_pos + 5, 0, ctx.vars)
            step.goto = bytes(ctx.vars.argbuf).split(b"\0")[0]
        elif name in ("IF__", "WKST", "WKAD", "WKOR", "WKOF", "WKBO"):
            step.effect = vv.execute(buf, name_pos, ctx.vars)
            step.goto = step.effect.goto
        elif name == "PGST":
            # page marker (handler 0x9637E): F1122 = n (VA 0x97852); in scan mode only the
            # compare with the scan target dword 0x873040 (0x9687D..0x96897)
            n = inline_int(buf, name_pos + 5, 0)
            if not ctx.vars.scan_mode:
                ctx.vars.mem.write(vv.VAR_F + 2 * PAGE_VAR, n)
            elif _s16(n) == ctx.scan_target:
                raise NotImplementedError("_PGST at the scan target page (0x9ABD4) not modelled")
        elif _MARKER.match(buf, name_pos, name_pos + 8):
            # Z marker (0x966E6..0x9848C): during a scene jump F998 and the read test may end
            # the jump (not modelled); outside scan mode a pending layer change (F991 != 0)
            # pushes a kind-9 commit record (refused); with F991 == 0 only F991 is read
            if ctx.scene_jump:
                raise NotImplementedError("marker during a scene jump (skip-end tests) not modelled")
            if not ctx.vars.scan_mode and ctx.vars.mem.read(vv.VAR_F + 2 * 991):
                raise NotImplementedError("marker with F991 != 0 (kind-9 commit push)")
        else:
            raise NotImplementedError(f"walker: unsupported statement {buf[name_pos - 1:end]!r}")
        if step.goto is not None:
            nxt = labels[step.goto.decode("latin-1")]
        step.log = list(ctx.vars.mem.log)
        steps.append(step)
        if name in stop_names:
            stops -= 1
            if stops <= 0:
                return steps
        pos = nxt
    raise RuntimeError("walk: step limit")


# --------------------------------------------------------------------------------------
# engine harness
# --------------------------------------------------------------------------------------
RING_VA = 0x48000000
RING_MAPPED = 32                       # records zeroed / compared per run (at least)
SCRIPT_VA = 0x40000000                 # AdvEmu SCRATCH: the decoded script
REWIND_SENTINEL = 0x5EED5EED
# regions saved after construction and restored before every run (everything the block's
# statements and the stubs write, apart from the ring and the scene stack)
_RESTORE = ((0x164B000, 0x2800), (0x1650000, 0x10000), (0x174D000, 0x4000), (0xDF2000, 0x1000),
            (0x120A6C0, 0x40), (0x86AA00, 0x100), (0x1653000, 0x1000))
_SELD_STUBS = (0xB8620, 0xC6A70, 0x56150, 0x53860, 0x76040, 0x41A20, 0xC6C20, 0xA5DF0)
_EXTRA_PLT = {0x1736E0: "strcat", 0x1735E0: "strtol", 0x1735C0: "strchr", 0x1735D0: "strpbrk",
              0x1734D0: "memmove"}


def _machine(eboot: Path) -> "AdvEmu":
    from tsuki.misc_adv_system_state_emu import AdvEmu

    class _Machine(AdvEmu):
        """AdvEmu with the string imports the script readers need."""

        def _plt(self, uc: object, addr: int, size: int, _: object) -> None:   # noqa: ANN001
            name = _EXTRA_PLT.get(addr)
            if name is None:
                super()._plt(uc, addr, size, _)
                return
            rdi, rsi, rdx = self.r("rdi"), self.r("rsi"), self.r("rdx")
            rax = rdi
            if name == "strcat":
                n = 0
                while self.u8(rdi + n):
                    n += 1
                m = 0
                while self.u8(rsi + m):
                    m += 1
                self.wr(rdi + n, self.rd(rsi, m + 1))
            elif name == "memmove":
                if rdx:
                    self.wr(rdi, self.rd(rsi, rdx))
            elif name == "strtol":
                txt = bytearray()
                while len(txt) < 4096 and self.u8(rdi + len(txt)):
                    txt.append(self.u8(rdi + len(txt)))
                mt = re.match(rb"[ \t\n\v\f\r]*([+-]?)([0-9]+)", bytes(txt))
                v, e = (0, rdi) if mt is None else (int(mt.group(2)) * (-1 if mt.group(1) == b"-" else 1),
                                                     rdi + mt.end())
                if rsi:
                    self.wr(rsi, struct.pack("<Q", e))
                rax = v & 0xFFFFFFFFFFFFFFFF
            elif name == "strchr":
                k = 0
                while True:
                    b = self.u8(rdi + k)
                    if b == rsi & 0xFF:
                        rax = rdi + k
                        break
                    if b == 0:
                        rax = 0
                        break
                    k += 1
            elif name == "strpbrk":
                acc = bytearray()
                while self.u8(rsi + len(acc)):
                    acc.append(self.u8(rsi + len(acc)))
                k = 0
                while True:
                    b = self.u8(rdi + k)
                    if b == 0:
                        rax = 0
                        break
                    if b in acc:
                        rax = rdi + k
                        break
                    k += 1
            self._return(rax)

    return _Machine(eboot)


@dataclass
class EngineRun:
    """What the engine did on one block (see :meth:`SeldEmu.run_block`)."""
    steps: List[Tuple[int, List[Tuple[str, int, int]]]]   # (name offset, filtered 16-bit log)
    pushes: List[Tuple[int, int, int, bool]]              # (cmd, resume, kind, body ran)
    records: Dict[int, RingRecord]
    ring: Tuple[int, int, int, int, int]                  # cur, next, oldest, wrapped, counter
    slots: Dict[int, bytes]
    text_buffer: bytes
    redisplay: int
    rewind_block: Optional[int]                           # None = sentinel untouched
    staging: List[StagingCall]
    count: int
    values: List[int]
    locked: List[int]
    mode: int
    f799: int
    scene_top: int
    scene_jump: int
    scene_jump_ends: int
    seld_ran: int
    error: Optional[str] = None


@dataclass
class Scenario:
    """Engine / model set-up of one run."""
    name: str = "retail"
    f: Dict[int, int] = field(default_factory=dict)      # F index -> value
    g: Dict[int, int] = field(default_factory=dict)      # G index -> value
    scan: bool = False
    block_byte: int = 0                                  # 0x16541F4
    redisplay: int = 0                                   # 0x174D9A8
    scene_top: int = 0
    scene_jump: int = 0
    effect_blocking: int = 0                             # OR mask returned by 0x5C680
    pre_records: int = 8                                 # valid ring records before the block
    pre_kind: int = 0                                    # kind of the newest of them
    pre_blocking: int = 0
    pre_f799: int = 0
    question: bytes = b"$000000"                         # text buffer / newest record text


class SeldEmu:
    """Script_Step (VA 0x95E60) on a decoded script under unicorn, state carried between the
    statements of a block.  Stubbed (return 0): the effect-slot tests 0x5C660 (0) / 0x5C680
    (``Scenario.effect_blocking``), 0x53620, the SELD layout / flowchart / UI calls
    (0xB8620, 0xC6A70, 0x56150, 0x53860, 0x76040, 0x41A20, 0xC6C20, 0xA5DF0), 0x67460, the
    auto save 0x73B70; logged: Staging_SetSnapshot 0x73980 (arguments), the scene-jump end
    0x7E620; in Python: SceneStack_Push 0x7E2E0 (adv_state.md 5.3: top += 1 and a copy of
    ring[cur] when no effect slot blocks; its own vector code is not run) and the label
    lookup 0x794D0 (label table offset).  History_Push 0x7AF90 runs for real."""

    def __init__(self, eboot: Union[str, Path, None] = None) -> None:
        from unicorn import UC_HOOK_CODE

        from tsuki import game_dir
        if eboot is None:
            g = game_dir()
            if g is None:
                raise FileNotFoundError("game folder not found")
            eboot = g / "eboot.bin"
        self.emu = emu = _machine(Path(eboot))
        emu.uc.mem_map(RING_VA, (REC_SIZE * 808 + 0xFFFF) & ~0xFFFF)
        self.sc = Scenario()
        self.labels: Dict[str, int] = {}
        self.staging: List[StagingCall] = []
        self.pushes: List[Tuple[int, int, int, bool]] = []
        self.stmt: Optional[int] = None
        self.cur_name = ""
        self.ring_n = RING_MAPPED
        self.step_logs: List[Tuple[int, List[Tuple[str, int, int]]]] = []
        self.seld_ran = 0
        self.jump_ends = 0
        for va in (0x5C660, 0x53620, 0x67460, 0x73B70) + _SELD_STUBS:
            emu.stub(va, 0)
        emu.stub(0x5C680, lambda e: (self.sc.effect_blocking >> (e.r("rdi") & 31)) & 1)
        emu.stub(STAGING_VA, self._staging)
        emu.stub(SCENE_JUMP_END_VA, self._jump_end)
        emu.stub(SCENE_STACK_PUSH_VA, self._scene_push)
        emu.stub(LABEL_LOOKUP_VA, self._label)
        emu.patch_functions([SCRIPT_STEP_VA, HISTORY_PUSH_VA, 0x76690, 0x76780, 0x76860, 0x76EA0])
        for va in (DISPATCH_VA, HISTORY_PUSH_VA, HISTORY_BODY[0], SELD_HANDLER):
            emu.uc.hook_add(UC_HOOK_CODE, self._hook, begin=va, end=va)
        self._clean = {a: emu.rd(a, n) for a, n in _RESTORE}

    # -- hooks ------------------------------------------------------------------------
    def _hook(self, uc: object, a: int, size: int, _: object) -> None:
        e = self.emu
        if a == DISPATCH_VA:
            self._flush()
            self.stmt = e.r("r14") - SCRIPT_VA
            self.cur_name = e.rd(e.r("r14"), 4).decode("latin-1")
        elif a == HISTORY_PUSH_VA:
            self.pushes.append((e.r("rdi") - SCRIPT_VA, e.r("rsi") - SCRIPT_VA, e.r("edx") & 0xFFFF, False))
        elif a == HISTORY_BODY[0]:
            c, r, k, _b = self.pushes[-1]
            self.pushes[-1] = (c, r, k, True)
        elif a == SELD_HANDLER:
            self.seld_ran += 1

    def _flush(self) -> None:
        log = self.emu.log
        if self.stmt is not None and log is not None:
            ev = [(("r", m.addr, m.value), m.seq) for m in log.reads if m.size == 2 and not
                  HISTORY_BODY[0] <= m.pc < HISTORY_BODY[1]]
            ev += [(("w", m.addr, m.value), m.seq) for m in log.writes if m.size == 2 and not
                   HISTORY_BODY[0] <= m.pc < HISTORY_BODY[1]]
            self.step_logs.append((self.stmt, [x for x, _s in sorted(ev, key=lambda t: t[1])]))
        if log is not None:
            log.reads.clear()
            log.writes.clear()
        self.stmt = None

    def _staging(self, e: "AdvEmu") -> int:
        rdi, rsi, rdx, rcx = e.r("rdi"), e.r("rsi") & 0xFFFFFFFF, e.r("rdx"), e.r("rcx")
        n = rsi - (1 << 32) if rsi >> 31 else rsi
        self.staging.append(StagingCall((rdi - RING_VA) // REC_SIZE, n,
                                        (rdx - SCENE_STACK_VA) // REC_SIZE if rdx else None,
                                        (rcx - SCENE_STACK_VA) // REC_SIZE if rcx else None,
                                        self.cur_name))
        return 0

    def _jump_end(self, e: "AdvEmu") -> int:
        self.jump_ends += 1
        return 0

    def _scene_push(self, e: "AdvEmu") -> int:
        if self.sc.effect_blocking & EFFECT_MASK:
            return 0
        top = e.i16(SCENE_TOP_VA) + 1
        e.wr(SCENE_TOP_VA, struct.pack("<h", top))
        cur = e.i16(0x120A6EC)
        e.wr(SCENE_STACK_VA + top * REC_SIZE, e.rd(RING_VA + cur * REC_SIZE, REC_SIZE))
        e.wr(0x174DE04, struct.pack("<i", 2))
        return 0

    def _label(self, e: "AdvEmu") -> int:
        name = _cstr(e.rd(e.r("rdi"), 32)).decode("latin-1")
        return SCRIPT_VA + self.labels[name]

    # -- set-up -----------------------------------------------------------------------
    def _setup(self, script: bytes, sc: Scenario, script_no: int) -> None:
        e = self.emu
        for a, b in self._clean.items():
            e.wr(a, b)
        e.wr(SCRIPT_VA, script + bytes(0x400))
        e.wr(0x174D640, struct.pack("<Q", SCRIPT_VA))
        e.wr(0x86AA18, struct.pack("<H", script_no))
        e.wr(0x120A6E0, struct.pack("<Q", RING_VA))
        self.ring_n = min(RING_RECORDS, max(RING_MAPPED, sc.pre_records + 24))
        ring = bytearray(REC_SIZE * self.ring_n)
        for i in range(self.ring_n):
            struct.pack_into("<ii", ring, i * REC_SIZE, i, -1)
        n = sc.pre_records
        for i in range(n):
            o = i * REC_SIZE
            struct.pack_into("<i", ring, o + 4, i)
            if i == n - 1:
                struct.pack_into("<H", ring, o + REC_KIND, sc.pre_kind)
                struct.pack_into("<H", ring, o + REC_BLOCKING, sc.pre_blocking)
                ring[o + 0x3501 + F799_S8_SLOT] = sc.pre_f799 & 0xFF
                ring[o + REC_TEXT:o + REC_TEXT + len(sc.question)] = sc.question
        e.wr(RING_VA, bytes(ring))
        e.wr(0x120A6EA, struct.pack("<hhhhh", n, max(n - 1, 0), n, 0, 0))   # counter, cur, next, wrapped, oldest
        e.wr(SCENE_TOP_VA, struct.pack("<h", sc.scene_top))
        for i, v in sc.f.items():
            e.wr(vv.VAR_F + 2 * i, struct.pack("<H", v & 0xFFFF))
        for i, v in sc.g.items():
            e.wr(vv.VAR_G + 2 * i, struct.pack("<H", v & 0xFFFF))
        e.wr(0x174DE48, bytes([1 if sc.scan else 0]))
        e.wr(vv.SNAPSHOT_BLOCK_VA, bytes([sc.block_byte & 0xFF]))
        e.wr(REDISPLAY_VA, bytes([sc.redisplay & 0xFF]))
        e.wr(SCENE_JUMP_VA, struct.pack("<H", sc.scene_jump))
        e.wr(TEXT_BUFFER_VA, sc.question + b"\0")
        e.wr(REWIND_BLOCK_VA, struct.pack("<I", REWIND_SENTINEL))
        self.sc = sc
        self.staging, self.pushes, self.step_logs = [], [], []
        self.stmt, self.seld_ran, self.jump_ends = None, 0, 0

    def run_block(self, script: bytes, start: int, labels: Dict[str, int], sc: Scenario,
                  script_no: int = 4, max_calls: int = 60, selds: int = 1) -> EngineRun:
        """Call Script_Step from ``start`` (and again from every pointer it returns: label
        jumps, yields) until it returns after ``selds`` ``_SELD`` statements ran."""
        e = self.emu
        self.labels = labels
        self._setup(script, sc, script_no)
        win = (SCENE_TOP_VA, vv.VAR_AFTER_G)
        e.start_log(reads=[win], writes=[win])
        err = None
        ptr = SCRIPT_VA + start
        try:
            for _ in range(max_calls):
                ptr = e.call(SCRIPT_STEP_VA, ptr, count=5_000_000)
                if self.seld_ran >= selds:
                    break
            else:
                err = "no _SELD reached"
        except RuntimeError as x:
            err = str(x)
        self._flush()
        e.stop_log()
        return self._result(err)

    def _result(self, err: Optional[str]) -> EngineRun:
        e = self.emu
        recs: Dict[int, RingRecord] = {}
        for i in range(self.ring_n):
            o = RING_VA + i * REC_SIZE
            idx, serial = struct.unpack("<ii", e.rd(o, 8))
            cmd, resume = struct.unpack("<QQ", e.rd(o + 8, 16))
            recs[i] = RingRecord(idx, serial, e.u16(o + REC_KIND), cmd, resume,
                                 e.u8(o + 0x3501 + F799_S8_SLOT), _cstr(e.rd(o + REC_TEXT, LINE_SLOT_SIZE)),
                                 e.u16(o + REC_BLOCKING), e.u16(o + REC_TEXT_END) == 0)
        count = e.u16(CHOICE_COUNT_VA)
        n = min(_s16(count), LINE_SLOT_COUNT) if _s16(count) > 0 else 0
        rb = e.u32(REWIND_BLOCK_VA)
        return EngineRun(
            steps=self.step_logs, pushes=self.pushes, records=recs,
            ring=(e.i16(0x120A6EC), e.i16(0x120A6EE), e.i16(0x120A6F2), e.i16(0x120A6F0), e.i16(0x120A6EA)),
            slots={k: _cstr(e.rd(LINE_SLOTS_VA + k * LINE_SLOT_SIZE, LINE_SLOT_SIZE)) for k in range(LINE_SLOT_COUNT)},
            text_buffer=_cstr(e.rd(TEXT_BUFFER_VA, LINE_SLOT_SIZE)), redisplay=e.u8(REDISPLAY_VA),
            rewind_block=None if rb == REWIND_SENTINEL else rb, staging=self.staging, count=count,
            values=list(struct.unpack(f"<{n}H", e.rd(vv.CHOICE_VALUES_VA, 2 * n))),
            locked=list(e.rd(0x175064C, n)), mode=e.u32(vv.CHOICE_MODE_VA),
            f799=e.u16(vv.VAR_F + 2 * LAST_LINE_VAR), scene_top=e.i16(SCENE_TOP_VA),
            scene_jump=e.u16(SCENE_JUMP_VA), scene_jump_ends=self.jump_ends, seld_ran=self.seld_ran, error=err)


# --------------------------------------------------------------------------------------
# engine vs model
# --------------------------------------------------------------------------------------

def model_context(sc: Scenario) -> ChoiceContext:
    """The model state matching :meth:`SeldEmu._setup` for ``sc``."""
    st = vv.VarState(scan_mode=sc.scan, snapshot_block=sc.block_byte)
    for i, v in sc.f.items():
        st.mem.poke(vv.VAR_F + 2 * i, v)
    for i, v in sc.g.items():
        st.mem.poke(vv.VAR_G + 2 * i, v)
    ring = HistoryRing.with_history(sc.pre_records, sc.pre_kind, sc.question, sc.pre_f799, sc.pre_blocking)
    ctx = ChoiceContext(vars=st, ring=ring, redisplay=sc.redisplay, text_buffer=sc.question,
                        scene_jump=sc.scene_jump, effect_blocking=sc.effect_blocking)
    ctx.scene_top = sc.scene_top
    return ctx


def compare_block(emu: SeldEmu, script: bytes, start: int, labels: Dict[str, int], sc: Scenario,
                  script_no: int = 4, selds: int = 1) -> Tuple[List[str], EngineRun, ChoiceContext, List[Step]]:
    """Run the block in the engine and in the model (up to the ``selds``-th ``_SELD``);
    returns (differences, engine run, model context, model steps)."""
    run = emu.run_block(script, start, labels, sc, script_no, selds=selds)
    ctx = model_context(sc)
    steps = walk(ctx, script, start, labels, stops=selds)
    diffs: List[str] = []
    if run.error:
        diffs.append("engine error: " + run.error)
    eng_steps = [(o, log) for o, log in run.steps]
    mod_steps = [(s.offset, s.log) for s in steps]
    if [o for o, _ in eng_steps] != [o for o, _ in mod_steps]:
        diffs.append(f"statement order engine={[o for o, _ in eng_steps]} model={[o for o, _ in mod_steps]}")
    else:
        for (o, el), (_o, ml) in zip(eng_steps, mod_steps):
            if el != ml:
                diffs.append(f"log @{o} {script[o - 1:o + 30]!r}: engine={_names(el)} model={_names(ml)}")
    model_pushes = [(r.cmd_offset, r.resume_offset, r.kind) for r in ctx.ring.pushed]
    eng_pushes = [(c, r, k) for c, r, k, body in run.pushes if body]
    if model_pushes != eng_pushes:
        diffs.append(f"pushes engine={eng_pushes} model={model_pushes}")
    for i, mr in sorted(ctx.ring.records.items()):
        er = run.records.get(i)
        if er is None:
            continue
        want: Tuple[object, ...] = (mr.serial, mr.kind, mr.cmd_offset, mr.resume_offset, mr.f799, mr.text,
                                    mr.blocking)
        got: Tuple[object, ...] = (er.serial, er.kind, er.cmd_offset, er.resume_offset, er.f799, er.text,
                                   er.blocking)
        if mr.serial < 0:                  # only "invalid" is meaningful for the next slot
            want, got = (mr.serial,), (er.serial,)
        if want != got:
            diffs.append(f"record {i}: engine={got} model={want}")
        if mr.text_end_zero and not er.text_end_zero:
            diffs.append(f"record {i}: +0x39F6 not zero")
    r = ctx.ring
    if run.ring != (r.cur, r.next, r.oldest, int(r.wrapped), r.counter):
        diffs.append(f"ring engine={run.ring} model={(r.cur, r.next, r.oldest, int(r.wrapped), r.counter)}")
    for k, t in ctx.slots.items():
        if run.slots.get(k) != t:
            diffs.append(f"slot {k}: engine={run.slots.get(k)!r} model={t!r}")
    st = ctx.vars
    checks = (("text buffer", run.text_buffer, ctx.text_buffer), ("0x174D9A8", run.redisplay, ctx.redisplay),
              ("0xDF29D4", run.rewind_block, ctx.rewind_block),
              ("staging", [asdict(c) for c in run.staging], [asdict(c) for c in ctx.staging_calls]),
              ("count", run.count, st.choice_count), ("values", run.values, st.choice_values),
              ("locked", run.locked, [int(x) for x in st.choice_locked]),
              ("mode", run.mode, st.choice_mode), ("F799", run.f799, st.mem.peek(vv.VAR_F + 2 * LAST_LINE_VAR)),
              ("scene top", run.scene_top, ctx.scene_top), ("scene jump", run.scene_jump, ctx.scene_jump),
              ("scene jump ends", run.scene_jump_ends, ctx.scene_jump_ends))
    for name, a, b in checks:
        if a != b:
            diffs.append(f"{name}: engine={a!r} model={b!r}")
    return diffs, run, ctx, steps


def _names(log: List[Tuple[str, int, int]]) -> List[Tuple[str, str, int]]:
    return [(k, vv.address_name(a) if a != SCENE_TOP_VA else "top", v) for k, a, v in log]


# --------------------------------------------------------------------------------------
# retail blocks
# --------------------------------------------------------------------------------------
_WALKABLE = re.compile(rb"_(SELR|IF__|GOTO|Z[YZ][0-9A-Fa-f]{5}|PGST|WK..)\(")


@dataclass
class Block:
    script: int
    start: int                    # offset where the walk starts (the _ATSV(1) or the first walkable)
    seld: int                     # offset of the _SELD name
    if_vars: List[Tuple[str, int]]   # variables read by _IF__ in the block
    f823: Optional[int]           # F823 from the linear scan (None: not set in the script so far)
    has_atsv: bool
    has_marker: bool = False      # a _ZY / _ZZ marker lies on the walk


def retail_blocks(scr: Optional["AllScr"] = None) -> Iterator[Tuple[Block, bytes, Dict[str, int]]]:
    """Every retail ``_SELD`` with the statements before it that a walk needs: back from the
    ``_SELD`` over ``_SELR`` / ``_IF__`` / ``_GOTO`` / markers / ``_PGST`` / ``_WK*``, plus a
    directly preceding ``_ATSV(1)``.  Yields (block, script bytes, labels)."""
    from tsuki.script import AllScr
    al = AllScr() if scr is None else scr
    try:
        for si in range(len(al)):
            s = al.raw(si)
            if b"_SELD(" not in s:
                continue
            labels = {lb.name: lb.offset for lb in al.labels(si)}
            segs: List[Tuple[int, bytes]] = []
            p = 0
            for seg in vv.segments(s):
                segs.append((p, seg))
                p += len(seg)
            f823: Optional[int] = None
            for i, (off, seg) in enumerate(segs):
                u = seg.find(b"_")
                body = seg[u:] if u >= 0 else b""
                m = re.match(rb"_WKST\(F823,eq,(-?\d+)", body)
                if m:
                    f823 = int(m.group(1))
                m = re.match(rb"_WKAD\(F823,(-?\d+)", body)
                if m:
                    f823 = (f823 or 0) + int(m.group(1))
                if not body.startswith(b"_SELD("):
                    continue
                j = i
                while j > 0 and _WALKABLE.match(segs[j - 1][1][segs[j - 1][1].find(b"_"):]):
                    j -= 1
                has_atsv = j > 0 and segs[j - 1][1].endswith(b"_ATSV(1);")
                if has_atsv:
                    j -= 1
                start = segs[j][0]
                if_vars: List[Tuple[str, int]] = []
                for _o, sg in segs[j:i]:
                    b = sg[sg.find(b"_"):]
                    if b.startswith(b"_IF__("):
                        op = vv.parse_operand(vv.arg_texts(b, 6)[0].decode("latin-1"))
                        if op.kind in ("F", "G") and (op.kind, op.index) not in if_vars:
                            if_vars.append((op.kind, op.index))
                marker = any(_MARKER.match(sg, sg.find(b"_") + 1) for _o, sg in segs[j:i])
                yield (Block(si, start, off + u + 1, if_vars, f823, has_atsv, marker), s, labels)
    finally:
        if scr is None:
            al.close()


def _assignment(b: Block, value: int) -> Tuple[Dict[int, int], Dict[int, int]]:
    f = {i: value for k, i in b.if_vars if k == "F"}
    g = {i: value for k, i in b.if_vars if k == "G"}
    return f, g


FLAG_SCENARIOS: Dict[str, Dict[str, object]] = {
    "retail": {},
    "F929=1": {"f": {929: 1}},
    "F1102=1": {"f": {1102: 1}},
    "block byte": {"block_byte": 1},
    "re-display, gate open": {"redisplay": 1},
    "re-display, F929=1": {"redisplay": 1, "f": {929: 1}},
    "F929=1, newest record blocking": {"f": {929: 1}, "pre_blocking": 4},
    "F929=1, newest record kind 11": {"f": {929: 1}, "pre_kind": 11},
    "F929=1, newest record kind 12": {"f": {929: 1}, "pre_kind": 12},
    "F929=1, F823=1, top=-1": {"f": {929: 1, 823: 1}, "scene_top": -1},
    "F929=1, F823=0, top=3": {"f": {929: 1, 823: 0}, "scene_top": 3},
    "F929=1, F823=2": {"f": {929: 1, 823: 2}},
    "F929=1, F823=-1": {"f": {929: 1, 823: 0xFFFF}},
    "effects blocking": {"effect_blocking": 2},
    "scene jump": {"scene_jump": 1},
    "scan mode": {"scan": True},
    "F823=1": {"f": {823: 1}},
}


def scenario_for(b: Block, assign: int, flags: str) -> Scenario:
    f, g = _assignment(b, assign)
    f.setdefault(823, b.f823 if b.f823 is not None else 0)
    sc = Scenario(name=flags, f=f, g=g)
    _apply(sc, FLAG_SCENARIOS[flags])
    return sc


def _apply(sc: Scenario, spec: Dict[str, object]) -> None:
    """Set Scenario fields from a spec; "f" entries are merged into ``sc.f``."""
    for k, v in spec.items():
        if k == "f":
            assert isinstance(v, dict)
            sc.f.update(v)
        else:
            setattr(sc, k, v)


def block_start(b: Block, s: bytes, sc: Scenario) -> int:
    """Walk start: in scan mode ``_ATSV`` itself yields, and during a scene jump ``_ATSV`` ends
    the jump (adv_state.md 2.5); in those two scenarios the walk starts after it."""
    if (sc.scan or sc.scene_jump) and b.has_atsv:
        return s.index(b";", b.start) + 1
    return b.start


@dataclass
class CheckResult:
    runs: int = 0
    mismatches: int = 0
    lines: int = 0
    pushes12: int = 0
    seld_staging: int = 0
    counts: Dict[int, int] = field(default_factory=dict)
    by_flags: Dict[str, int] = field(default_factory=dict)
    first: List[str] = field(default_factory=list)
    skipped: int = 0              # "scene jump" runs of blocks with markers (not modelled)


BlockList = Sequence[Tuple[Block, bytes, Dict[str, int]]]


def retail_check(emu: SeldEmu, flags: Sequence[str] = ("retail",), assigns: Sequence[int] = (0, 1, 5),
                 limit: Optional[int] = None, every: int = 1, blocks: Optional[BlockList] = None) -> CheckResult:
    """Engine vs model on the retail blocks (every ``every``-th, at most ``limit``), each with
    the _IF__ variables all set to each value of ``assigns`` and every flag scenario.
    ``blocks``: a list from :func:`retail_blocks` (read from allscr.mrg when omitted)."""
    res = CheckResult()
    for n, (b, s, labels) in enumerate(blocks if blocks is not None else retail_blocks()):
        if n % every:
            continue
        if limit is not None and res.runs >= limit * len(flags) * len(assigns):
            break
        for fl in flags:
            if fl == "scene jump" and b.has_marker:
                res.skipped += len(assigns)
                continue
            for a in assigns:
                sc = scenario_for(b, a, fl)
                diffs, run, ctx, steps = compare_block(emu, s, block_start(b, s, sc), labels, sc, b.script)
                res.runs += 1
                res.by_flags[fl] = res.by_flags.get(fl, 0) + 1
                lines = steps[-1].lines or []
                res.lines += len(lines)
                res.pushes12 += sum(1 for ev in lines if ev.pushed)
                res.seld_staging += sum(1 for ev in lines if ev.staging is not None)
                if fl == "retail":
                    res.counts[run.count] = res.counts.get(run.count, 0) + 1
                if diffs:
                    res.mismatches += 1
                    if len(res.first) < 20:
                        res.first.append(f"script {b.script} SELD@{b.seld} assign {a} [{fl}]: " + "; ".join(diffs)[:600])
    return res


def pending_census(assigns: Sequence[int] = (0, 1, 5), blocks: Optional[BlockList] = None) -> Dict[str, object]:
    """Model-only census over the retail blocks: pending lines at each ``_SELD`` per
    assignment of the block's ``_IF__`` variables (gate open)."""
    per: Dict[int, Dict[int, int]] = {a: {} for a in assigns}
    n_blocks = 0
    with_atsv = 0
    with_if = 0
    for b, s, labels in (blocks if blocks is not None else retail_blocks()):
        n_blocks += 1
        with_atsv += b.has_atsv
        with_if += bool(b.if_vars)
        for a in assigns:
            sc = scenario_for(b, a, "retail")
            ctx = model_context(sc)
            walk(ctx, s, b.start, labels)
            c = ctx.vars.choice_count
            per[a][c] = per[a].get(c, 0) + 1
    return {"blocks": n_blocks, "with_atsv1": with_atsv, "with_if": with_if,
            "pending_lines": {str(a): dict(sorted(v.items())) for a, v in per.items()},
            "pending_total": {str(a): sum(k * n for k, n in v.items()) for a, v in per.items()}}


# --------------------------------------------------------------------------------------
# synthetic cases (forms and states the retail blocks do not reach)
# --------------------------------------------------------------------------------------
SYNTHETIC_CASES: List[Tuple[str, str, Dict[str, object], int]] = [
    # (name, statements, Scenario fields, number of _SELD to run)
    ("one line", "_SELR(7,$000001);_SELD(_D000,);", {}, 1),
    ("no pending line", "_SELD(_D000,5);", {}, 1),
    ("eleven lines (k >= 9 uses line 43959)",
     "".join(f"_SELR({i},$0{i:05d});" for i in range(11)) + "_SELD(_D000,);", {}, 1),
    ("argument forms: tag, missing, empty, locked",
     "_SELR(0,`001:$000123);_SELR(1);_SELR(2,);_SELR(3,$000124,1);_SELD(_D000,);", {}, 1),
    ("two groups without the choice UI: the count keeps growing",
     "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,1);_SELR(5,$000003);_SELD(_D000,2);", {}, 2),
    ("gate shut by F929, F823 = 1, top = 2",
     "_SELR(0,$000001);_SELR(1,$000002);_SELR(2,$000003);_SELD(_D000,);",
     {"f": {929: 1, 823: 1}, "scene_top": 2}, 1),
    ("gate shut by F1102, newest record kind 7, F823 = 0, top = 0",
     "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"f": {1102: 1, 823: 0}, "pre_kind": 7}, 1),
    ("gate shut by the message-continues byte, F823 = 3",
     "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"block_byte": 1, "f": {823: 3}}, 1),
    ("re-display flag with the gate shut",
     "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"redisplay": 1, "f": {929: 1}}, 1),
    ("re-display flag with the gate open (the SELR push clears it)",
     "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"redisplay": 1}, 1),
    ("scene jump running", "_SELR(0,$000001);_SELD(_D000,);", {"scene_jump": 1}, 1),
    ("scan mode", "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"scan": True, "f": {823: 2}}, 1),
    ("effect slots blocking (pushed records carry the bits)",
     "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"effect_blocking": 0x41, "f": {823: 0}}, 1),
    ("_ATSV(1) context, F823 = 1, top = -1",
     "_ATSV(1);_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"f": {823: 1}, "scene_top": -1}, 1),
    ("_ATSV(1) gated by F929, F823 = 1",
     "_ATSV(1);_SELR(0,$000001);_SELD(_D000,);", {"f": {929: 1, 823: 1}}, 1),
    ("ring wrap: next = 799", "_SELR(0,$000001);_SELR(1,$000002);_SELD(_D000,);", {"pre_records": 799}, 1),
]


def synthetic_check(emu: SeldEmu) -> List[Tuple[str, List[str]]]:
    """Engine vs model on :data:`SYNTHETIC_CASES` (statements after a leading ';')."""
    out = []
    for name, text, spec, selds in SYNTHETIC_CASES:
        sc = Scenario(name=name)
        _apply(sc, spec)
        buf = (";" + text).encode("latin-1")
        diffs, _run, _ctx, _steps = compare_block(emu, buf, 1, {}, sc, 0, selds)
        out.append((name, diffs))
    return out


# --------------------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------------------

def _print_steps(steps: List[Step], ctx: ChoiceContext) -> None:
    for s in steps:
        print(f"@{s.offset} _{s.name}: log={_names(s.log)}" + (f" pushed={s.pushed}" if s.pushed is not None else "")
              + (f" goto={s.goto!r}" if s.goto else ""))
        for ev in s.lines or []:
            print(f"    line {ev.k}: F799={ev.f799} pushed={ev.pushed} record={ev.record} text={ev.text!r} "
                  f"staging={ev.staging} cleared_0xDF29D4={ev.cleared_rewind_block}")
    r = ctx.ring
    print(f"ring cur={r.cur} next={r.next} counter={r.counter}; text buffer={ctx.text_buffer!r}; "
          f"0x174D9A8={ctx.redisplay}; 0xDF29D4={ctx.rewind_block}; staging={ctx.staging_calls}")
    for i, rec in sorted(r.records.items()):
        if rec.serial >= 0:
            print(f"  record {i}: serial {rec.serial} kind {rec.kind} cmd {rec.cmd_offset} resume "
                  f"{rec.resume_offset} F799 {rec.f799} text {rec.text!r}")


def _main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.misc_seld", description=__doc__.split("\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    m = sub.add_parser("model", help="run the model on statements (from the first one to _SELD)")
    m.add_argument("text")
    m.add_argument("--set", action="append", default=[], metavar="Fn=v")
    m.add_argument("--redisplay", type=int, default=0)
    m.add_argument("--top", type=int, default=0)
    m.add_argument("--block-byte", type=int, default=0)
    m.add_argument("--scan", action="store_true")
    sub.add_parser("census", help="pending _SELR lines at the 170 retail _SELD (model)")
    r = sub.add_parser("retail", help="engine vs model on the retail blocks")
    r.add_argument("--limit", type=int)
    r.add_argument("--every", type=int, default=1)
    r.add_argument("--flags", default="retail", help="comma list of FLAG_SCENARIOS names or 'all'")
    r.add_argument("--assigns", default="0,1,5", help="values for the _IF__ variables")
    r.add_argument("--json")
    sub.add_parser("example", help="script 4's first choice in the engine and the model")
    sub.add_parser("synthetic", help="engine vs model on SYNTHETIC_CASES")
    a = ap.parse_args(argv)
    if a.cmd == "model":
        sc = Scenario(scan=a.scan, block_byte=a.block_byte, redisplay=a.redisplay, scene_top=a.top)
        for kv in a.set:
            k, v = kv.split("=")
            (sc.f if k[0] == "F" else sc.g)[int(k[1:])] = int(v, 0)
        ctx = model_context(sc)
        buf = (";" + a.text).encode("latin-1")
        steps = walk(ctx, buf, 1, {})
        _print_steps(steps, ctx)
        return 0
    if a.cmd == "census":
        print(json.dumps(pending_census(), indent=1))
        return 0
    if a.cmd == "retail":
        emu = SeldEmu()
        flags = list(FLAG_SCENARIOS) if a.flags == "all" else a.flags.split(",")
        res = retail_check(emu, flags, [int(x) for x in a.assigns.split(",")], limit=a.limit, every=a.every)
        out = asdict(res)
        print(json.dumps(out, indent=1))
        if a.json:
            Path(a.json).write_text(json.dumps(out, indent=1))
        return 1 if res.mismatches else 0
    if a.cmd == "synthetic":
        bad = 0
        for name, diffs in synthetic_check(SeldEmu()):
            bad += bool(diffs)
            print(f"{name}: {'; '.join(diffs) if diffs else 'identical'}")
        return 1 if bad else 0
    if a.cmd == "example":
        emu = SeldEmu()
        for b, s, labels in retail_blocks():
            if b.script == 4:
                sc = scenario_for(b, 0, "retail")
                diffs, run, ctx, steps = compare_block(emu, s, b.start, labels, sc, b.script)
                _print_steps(steps, ctx)
                print("engine pushes:", run.pushes)
                print("differences:", diffs or "none")
                break
        return 0
    return 2


if __name__ == "__main__":
    sys.exit(_main())
