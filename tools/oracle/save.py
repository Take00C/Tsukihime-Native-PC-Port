"""Area ``save``: oracle data for the C# history ring and save code (package K4; ``Tsuki.Engine/Save``).

Parts (each a directory of ``samples/golden/save/``):

``history``  ``push_cases.json`` / ``restore_cases.json``: the game's own ``History_Push`` (VA 0x7AF90) and
             ``History_Restore`` (VA 0x7C6D0) run under unicorn (``tsuki.misc_adv_system_state_emu``) on random RAM.
             Per case: the live engine variables before (the ``LIVE`` windows, hex), the ring/stack indices, the
             record(s) before and after, the live variables after (only the windows that changed) and, for restores,
             the calls the restore makes into other engine functions (BGM, loop SEs, effect slots, staging ...).
             The C# ``AdvHistory.Push`` / ``AdvHistory.Restore`` must reproduce every byte and every call.
``nav``      ``nav_cases.json``: Adv_Rewind (VA 0xA2020) with History_Restore stubbed (which records it restores, in order),
             the previous-scene decision (VA 0xA23F0) and Backlog_Open (VA 0x3BFB0) on random rings.
``slots``    ``slots.json``: save images made by the game's own code (``engine_slot_image``): the history record
             pushed for the first ``_ZM`` statement of script 1, Staging_SetSnapshot, Slot_Store / QuickSave_Store and
             SaveImage_Commit, and the slot block each produced (the C# ``SlotImage`` / ``SaveStore`` must produce the
             same 0x7108 bytes); plus what Slot_Load -> 0x7E790 -> History_Restore leaves in RAM for those images.

Run: ``python -m oracle.save [--parts history,slots] [--quick]`` (registers the area with ``oracle.run``). Needs
unicorn and capstone in the virtual environment. Everything goes to the local ``samples/golden/save/``.
"""
from __future__ import annotations

import json
import math
import random
import struct
import sys
import time
from pathlib import Path
from typing import Any, Callable, Optional, Sequence

from .manifest import AreaWriter, PartWriter, Producer

AREA = "save"
MODULE = "oracle.save"
PARTS: tuple[str, ...] = ("history", "slots", "nav")
DESCRIPTION = ("save oracle for the C# history ring: History_Push / History_Restore of the game's own code under unicorn "
               "on random RAM, and slot images made by Slot_Store / QuickSave_Store")

# The live variables (RAM windows) a record captures or a restore writes: (name, VA, size). The C# test maps the
# names to AdvLiveState members (tests/Tsuki.Tests.Data/Save/LiveMap.cs).
LIVE: list[tuple[str, int, int]] = [
    ("script_number", 0x86AA18, 2), ("text_block", 0x1654344, 4), ("text_line", 0x1654348, 4),
    ("text_block2", 0x165434C, 4), ("text_line2", 0x1654350, 4), ("speaker", 0x164FCF6, 2),
    ("var_1654208", 0x1654208, 2), ("var_165420a", 0x165420A, 2), ("bgm_track", 0x16545D0, 2),
    ("bgm_volume", 0x1654238, 2), ("loop_se_ids", 0x1654240, 0x28), ("se_vol_a", 0x1654270, 0x28),
    ("se_vol_b", 0x16542A0, 0x28), ("se_arr_c", 0x16542D0, 0x28), ("se_arr_d", 0x1654300, 0x28),
    ("se_arr_e", 0x1654330, 0x14), ("channels", 0x16543F0, 0x1E0), ("window_164fd00", 0x164FD00, 0x32),
    ("window_164fd40", 0x164FD40, 0x1C), ("window_164fd60", 0x164FD60, 0x1C), ("message_text", 0x174D9B0, 0xFA),
    ("state_174dab0", 0x174DAB0, 0x40), ("var_174d940", 0x174D940, 2), ("scene_word", 0x16500BE, 2),
    ("scene", 0x16500C0, 0x1A08), ("committed", 0x1651AC8, 0x1A08), ("f", 0x164B1D0, 0x960),
    ("voice_id", 0x1654204, 4), ("var_16541fc", 0x16541FC, 4), ("var_1654200", 0x1654200, 4),
    ("var_16541ec", 0x16541EC, 4), ("var_16541f0", 0x16541F0, 4), ("var_16541e4", 0x16541E4, 4),
    ("var_16541d8", 0x16541D8, 4), ("var_16541f4", 0x16541F4, 1), ("restored_flag", 0x174D9A8, 1),
    ("var_174de00", 0x174DE00, 1), ("redisplay_length", 0x1653AE0, 2), ("var_86aa24", 0x86AA24, 4),
    ("text_fade", 0x86AA1C, 4), ("word_1750646", 0x1750646, 2), ("var_1653e24", 0x1653E24, 4),
    ("wait_flags", 0x1653E00, 8), ("wait_flags70", 0x1653E70, 8), ("word_1653e08", 0x1653E08, 2),
    ("redisplay_text", 0x16537E0, 0x300), ("redisplay_copy", 0x1653AF0, 0x300), ("config", 0x66C578, 0x140),
    ("scan_mode", 0x174DE48, 1),
]

RING_VA = 0x50000000
RING_MAP = 0xB80000
INDEX_WORDS = (("serial_counter", 0x120A6EA), ("cur", 0x120A6EC), ("next", 0x120A6EE), ("wrapped", 0x120A6F0),
               ("oldest", 0x120A6F2))
STUB_CALLS_OF_INTEREST = {0x98C0, 0x9A90, 0xAA80, 0xAAB0, 0xAD60, 0xA300, 0xA9E0, 0xAA50, 0x7B790, 0x75CD0, 0x5C080,
                          0x73980, 0xE98D0, 0x423D0, 0x9EA0, 0x9CE0}


def _producer(function: str, args: dict[str, Any], uses: tuple[str, ...]) -> Producer:
    return Producer(MODULE, function, args, uses)


def _snap(emu: Any) -> dict[str, str]:
    return {name: emu.rd(va, size).hex() for name, va, size in LIVE}


def _indices(emu: Any) -> dict[str, int]:
    return {name: emu.i16(va) if name != "wrapped" else emu.u16(va) for name, va in INDEX_WORDS}


def _diff(before: dict[str, str], after: dict[str, str]) -> dict[str, str]:
    return {k: v for k, v in after.items() if before[k] != v}


def _new_emu(patch: Sequence[int]) -> Any:
    from tsuki import misc_adv_system_state_emu as e
    emu = e.AdvEmu(e._eboot_path(None))
    emu.patch_functions(list(patch))
    emu.uc.mem_map(RING_VA, RING_MAP)
    return emu


def _set_script(emu: Any, e: Any, raw: Optional[bytes]) -> None:
    emu.wr(e._SCRIPT_BUF, raw if raw is not None else bytes(64))


# ---------------------------------------------------------------------------
# push
# ---------------------------------------------------------------------------

def push_case(name: str, seed: int, kind: int, *, k: int = 5, oldest: int = 300, wrapped: int = 0,
              counter: Optional[int] = None, f929: int = 0, f1102: int = 0, scan: int = 0, b1f4: int = 0,
              ) -> dict[str, Any]:
    from tsuki import misc_adv_system_state_emu as e
    emu = _new_emu([e.PUSH_VA])
    rng = e._random_world(emu, seed)
    rec_size = e.REC
    emu.wr(0x120A6E0, struct.pack("<Q", RING_VA))
    nxt = k + 1 if k + 1 < 800 else 0
    emu.wr(0x120A6EE, struct.pack("<h", k))
    emu.wr(0x120A6F2, struct.pack("<h", oldest))
    emu.wr(0x120A6F0, struct.pack("<H", wrapped))
    if counter is not None:
        emu.wr(0x120A6EA, struct.pack("<h", counter))
    for idx in {k, nxt}:
        emu.wr(RING_VA + idx * rec_size, rng.randbytes(rec_size))
        emu.wr(RING_VA + idx * rec_size, struct.pack("<i", idx))
    emu.wr(0x164B912, struct.pack("<H", f929))
    emu.wr(0x164BA6C, struct.pack("<H", f1102))
    emu.wr(0x174DE48, bytes([scan]))
    emu.wr(0x16541F4, bytes([b1f4]))
    bits_a, bits_b, w = rng.getrandbits(14), rng.getrandbits(14), rng.getrandbits(1)
    emu.stub(0x5C660, lambda m: (bits_a >> m.r("rdi")) & 1)
    emu.stub(0x5C680, lambda m: (bits_b >> m.r("rdi")) & 1)
    emu.stub(0x53620, w)
    cmd, resume = rng.randrange(0x100000), rng.randrange(0x100000)
    rec_k_before = emu.rd(RING_VA + k * rec_size, rec_size)
    rec_n_before = emu.rd(RING_VA + nxt * rec_size, rec_size)
    live_before = _snap(emu)
    idx_before = _indices(emu)
    emu.call(e.PUSH_VA, e._SCRIPT_BUF + cmd, e._SCRIPT_BUF + resume, kind)
    live_after = _snap(emu)
    return {
        "name": name, "seed": seed, "kind": kind, "k": k, "cmd_offset": cmd, "resume_offset": resume,
        "effect_active": bits_a, "effect_blocking": bits_b, "last_message": w,
        "indices_before": idx_before, "indices_after": _indices(emu),
        "live": live_before, "live_after": _diff(live_before, live_after),
        "record_before": rec_k_before.hex(), "record_after": emu.rd(RING_VA + k * rec_size, rec_size).hex(),
        "next_before": rec_n_before.hex(), "next_after": emu.rd(RING_VA + nxt * rec_size, rec_size).hex(),
    }


def push_cases(quick: bool = False) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    seeds = range(1, 8) if quick else range(1, 15)
    for s in seeds:
        out.append(push_case(f"seed{s}_kind{s % 14}", s, s % 14))
    for kind in (0, 1, 2, 3, 6, 7, 9, 12, 13):
        out.append(push_case(f"kind{kind}", 40 + kind, kind))
    # ring management: wrap, oldest moving, serial counter reset
    out.append(push_case("wrap_at_799_oldest0", 51, 3, k=799, oldest=0, wrapped=1))
    out.append(push_case("wrap_at_799_oldest5", 52, 0, k=799, oldest=5, wrapped=0))
    out.append(push_case("next_hits_oldest", 53, 2, k=10, oldest=11))
    out.append(push_case("next_hits_oldest_798", 54, 2, k=798, oldest=799, wrapped=1))
    out.append(push_case("serial_max", 55, 6, counter=0x7FFF))
    out.append(push_case("serial_zero", 56, 6, counter=0))
    out.append(push_case("serial_neg", 57, 6, counter=-5))
    # gates
    out.append(push_case("gate_f929", 61, 0, f929=1))
    out.append(push_case("gate_f1102_kind3", 62, 3, f1102=1))
    out.append(push_case("gate_f1102_kind0", 63, 0, f1102=1))
    out.append(push_case("gate_f1102_kind11", 64, 11, f1102=1))
    out.append(push_case("gate_f1102_kind12", 65, 12, f1102=2))
    out.append(push_case("gate_scan", 66, 0, scan=1))
    out.append(push_case("gate_16541f4", 67, 1, b1f4=1))
    return out


# ---------------------------------------------------------------------------
# restore
# ---------------------------------------------------------------------------

def restore_case(name: str, seed: int, kind: int, from_history: int, *, index: int = 3, stack_top: int = 0,
                 stack_hit: bool = False, snapshot: bool = False, cfg4c: Optional[int] = None,
                 f967: Optional[int] = None, f967_match: bool = False, latch: int = 0, script_diff: bool = False,
                 cfg04_value: Optional[int] = None, cfg04_small: bool = True, f799: Optional[int] = None, f931: Optional[int] = None,
                 f823_big: bool = False, script: Optional[bytes] = None, cmd_offset: int = 0,
                 live_active: Optional[int] = None) -> dict[str, Any]:
    import capstone
    from tsuki import misc_adv_system_state_emu as e
    emu = _new_emu([e.RESTORE_VA])
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    code = emu.image[e.RESTORE_VA + e.TEXT_OFF:0x7DD10 + e.TEXT_OFF]
    for _a, _s, mn, ops in md.disasm_lite(code, e.RESTORE_VA):
        if mn in ("call", "jmp") and ops.startswith("0x"):
            t = int(ops, 16)
            if not e.RESTORE_VA <= t < 0x7DD10 and t not in e.PLT_NAMES and t not in emu.stubs:
                emu.stub(t, 0)
    rng = e._random_world(emu, seed)
    emu.wr(0x120A6E0, struct.pack("<Q", RING_VA))
    rec = bytearray(rng.randbytes(e.REC))
    rec_script = 6 if script_diff else 5
    struct.pack_into("<iQQH", rec, 4, 7, cmd_offset, e.RESUME_OFF, rec_script)
    struct.pack_into("<H", rec, 0x24, kind)
    struct.pack_into("<H", rec, 0x3638, 0 if snapshot else rng.choice((1, 1, 2, 0x300)))
    if cfg04_value is not None:
        struct.pack_into("<H", rec, 0x363E, cfg04_value)
    elif cfg04_small:
        struct.pack_into("<H", rec, 0x363E, rng.randrange(0x140))
    lists = e._lists(None)
    s8 = lists[2]
    k823 = s8.index(823)
    rec[0x3501 + k823] = rng.choice((2, 5, 100)) if f823_big else rng.choice((0, 1, 0xFF))
    if f799 is not None:
        rec[0x3501 + s8.index(799)] = f799 & 0xFF
    if f967 is not None:
        # F967 is in the u16 and the s8 list; the s8 copy wins. The record's scene word +0x19A6 decides the reset rule.
        rec[0x3501 + s8.index(967)] = f967 & 0xFF
        struct.pack_into("<h", rec, 0x58 + 0x19A6, (f967 - 1) if f967_match else rng.randrange(-5, 300))
    if f931 is not None:
        k = lists[0].index(931)
        struct.pack_into("<H", rec, 0x34AA + 2 * k, f931 & 0xFFFF)
    emu.wr(0x86AA18, struct.pack("<H", 5))
    raw = script if script is not None else e.RESTORE_STMT + bytes(64)
    emu.wr(e._SCRIPT_BUF, raw)
    emu.wr(RING_VA + index * e.REC, bytes(rec))
    emu.wr(RING_VA + index * e.REC, struct.pack("<i", index))
    emu.wr(RING_VA + index * e.REC + 4, struct.pack("<i", 7))
    if cfg4c is not None:
        emu.wr(0x66C578 + 0x4C, struct.pack("<H", cfg4c))
    active = rng.getrandbits(14) if live_active is None else live_active
    emu.stubs[0x5C660] = lambda m: (active >> m.r("rdi")) & 1      # re-use the hook the loop above installed
    emu.stubs[0x56140] = latch
    emu.wr(0x164B1C0, struct.pack("<h", stack_top))
    emu.wr(0x120A700 + max(stack_top, 0) * e.REC, struct.pack("<i", index if stack_hit else index + 1000))
    emu.log = e.EmuLog()
    live_before = _snap(emu)
    idx_before = _indices(emu)
    stack_before = {"top": stack_top, "ring_index": emu.i32(0x120A700 + max(stack_top, 0) * e.REC),
                    "counter": emu.u32(0x174DE04), "after_load": emu.u32(0x174DE08)}
    record_before = emu.rd(RING_VA + index * e.REC, e.REC)
    emu.call(e.RESTORE_VA, index, from_history)
    live_after = _snap(emu)
    after = dict(live_after)
    after["script_offset"] = f"{emu.u64(e.SCRIPT_PTR) - e._SCRIPT_BUF:016x}"
    calls = [[c.va, *c.args[:4], c.xmm0 if math.isfinite(c.xmm0) else None] for c in emu.log.calls if c.va in STUB_CALLS_OF_INTEREST]
    return {
        "name": name, "seed": seed, "kind": kind, "from_history": from_history, "index": index,
        "script": raw.hex(), "script_number": 5, "record_script": rec_script, "latch": latch,
        "effect_active": active,
        "indices_before": idx_before, "indices_after": _indices(emu), "stack_before": stack_before,
        "stack_after": {"top": emu.i16(0x164B1C0), "counter": emu.u32(0x174DE04), "after_load": emu.u32(0x174DE08)},
        "live": live_before, "live_after": _diff(live_before, live_after),
        "script_offset_after": after["script_offset"],
        "record": record_before.hex(), "calls": calls,
    }


def restore_cases(quick: bool = False) -> list[dict[str, Any]]:
    out: list[dict[str, Any]] = []
    kinds = (0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13)
    seeds = (11,) if quick else (11, 12, 13)
    for seed in seeds:
        for kind in kinds:
            for fh in (0, 1):
                out.append(restore_case(f"s{seed}_k{kind}_fh{fh}", seed, kind, fh))
    # kind 0 re-display variants: statement end codes and the latch
    stmts = {
        "end_e": b";_ZM00a01(TEXT@e);" + bytes(8),
        "end_n": b";_ZM00a01(TEXT@n);" + bytes(8),
        "end_paren": b";_ZM00a01(TEXT HERE);" + bytes(8),
        "at_other": b";_ZM00a01(a@b@c@e);" + bytes(8),
        "empty": b";_ZM00a01();" + bytes(8),
        "long": b";_ZM00a01(" + b"x" * 0x340 + b");",
    }
    for tag, stmt in stmts.items():
        for latch in (0, 1):
            for f799 in (0, 1):
                out.append(restore_case(f"redisplay_{tag}_latch{latch}_f799_{f799}", 21, 0, 1, script=stmt, latch=latch, f799=f799))
    out.append(restore_case("redisplay_offset", 22, 0, 1, script=b"\x00" * 7 + b";_ZM00b02(Hello@e);" + bytes(8), cmd_offset=7))
    # scene-stack pops
    for kind in (6, 7):
        for top in (0, 1, 3, -1):
            for hit in (False, True):
                out.append(restore_case(f"pop_k{kind}_top{top}_{'hit' if hit else 'miss'}", 31, kind, 0, stack_top=top, stack_hit=hit))
    # config gate, wrap, staging, F rules
    out.append(restore_case("cfg4c_zero", 41, 0, 0, cfg4c=0))
    out.append(restore_case("cfg4c_one", 42, 2, 0, cfg4c=1))
    out.append(restore_case("cfg04_big", 43, 3, 0, cfg04_small=False, cfg4c=1))
    for v in (0xFF, 0x100, 0x101, 0xFFFF):
        out.append(restore_case(f"cfg04_{v:x}", 43, 3, 0, cfg04_value=v, cfg4c=1))
    out.append(restore_case("wrap_index_799", 44, 12, 0, index=799))
    out.append(restore_case("index_0", 45, 3, 0, index=0))
    out.append(restore_case("snapshot_kind0", 46, 0, 0, snapshot=True))
    out.append(restore_case("snapshot_kind6_top1", 47, 6, 0, snapshot=True, stack_top=1))
    out.append(restore_case("snapshot_kind12", 48, 12, 0, snapshot=True))
    out.append(restore_case("no_snapshot_f823_big", 49, 0, 0, f823_big=True))
    out.append(restore_case("script_diff_k3", 81, 3, 0, script_diff=True))
    out.append(restore_case("script_diff_k0_fh1", 82, 0, 1, script_diff=True))
    for f967 in (0, 1, 5, 200, 0xF0):
        for match in (False, True):
            out.append(restore_case(f"f967_{f967}_{'match' if match else 'other'}", 50 + f967 % 7, 6, 0, f967=f967, f967_match=match))
    for f931 in (0, 7, 0x7FFF, 0x8000, 0xFFFF):
        out.append(restore_case(f"f931_{f931:x}", 60, 0, 0, f931=f931))
    for active in (0, 0x3FFF, 0x2A55):
        out.append(restore_case(f"active_{active:x}", 70, 6, 0, live_active=active))
    return out


def export_history(part: PartWriter, quick: bool = False, log: Optional[Callable[[str], None]] = None) -> None:
    t0 = time.perf_counter()
    prod_push = _producer("push_cases", {"quick": quick}, ("tsuki.misc_adv_system_state_emu.AdvEmu", "tsuki.misc_adv_system_state_emu._random_world"))
    pushes = push_cases(quick)
    doc = {"model": "History_Push 0x7AF90 under unicorn; random RAM from the seed; record k = the ring slot written",
           "live": [[n, va, sz] for n, va, sz in LIVE], "cases": pushes}
    part.write_bytes("push_cases.json", (json.dumps(doc, separators=(",", ":")) + "\n").encode("utf-8"), "json", prod_push,
                     {"cases": len(pushes)})
    prod_rest = _producer("restore_cases", {"quick": quick}, ("tsuki.misc_adv_system_state_emu.AdvEmu", "tsuki.misc_adv_system_state_emu._random_world"))
    restores = restore_cases(quick)
    doc = {"model": "History_Restore 0x7C6D0 under unicorn; every callee outside the function is stubbed and logged "
                    "(calls: [va, rdi, rsi, rdx, rcx, xmm0]); live_after holds only the windows that changed",
           "live": [[n, va, sz] for n, va, sz in LIVE], "cases": restores}
    part.write_bytes("restore_cases.json", (json.dumps(doc, separators=(",", ":")) + "\n").encode("utf-8"), "json", prod_rest,
                     {"cases": len(restores)})
    part.count("push_cases", len(pushes))
    part.count("restore_cases", len(restores))
    part.count("seconds", round(time.perf_counter() - t0, 1))
    if log is not None:
        log(f"[save] history: {len(pushes)} push cases, {len(restores)} restore cases in {time.perf_counter() - t0:.1f} s")


# ---------------------------------------------------------------------------
# slots
# ---------------------------------------------------------------------------

def export_slots(part: PartWriter, quick: bool = False, log: Optional[Callable[[str], None]] = None) -> None:
    from eboot import savedata
    from tsuki import misc_adv_system_state_emu as e
    t0 = time.perf_counter()
    cases: list[dict[str, Any]] = []
    for name, kwargs in (("manual_slot7_state_b", dict(slot=7, with_state_b=True)),
                         ("manual_slot42", dict(slot=42, seed=6)),
                         ("quick", dict(quick=True, seed=8))):
        img = e.engine_slot_image(None, **kwargs)
        off = savedata.SLOT_BASE + img.slot * savedata.SLOT_STRIDE
        case = {
            "name": name, "slot": img.slot, "quick": bool(kwargs.get("quick")),
            "date": [2026, 10, 25, 12, 34, 56], "n": 1 if kwargs.get("with_state_b") else 0,
            "record": img.record.hex(), "record_b": img.record_b.hex() if img.record_b is not None else None,
            "slot_bytes": img.image[off:off + savedata.SLOT_SIZE].hex(),
            "script_no": img.script_no, "statement_offset": img.stmt_offset,
            "system_bytes": img.image[:savedata.SYSTEM_SIZE].hex(),
            "readmax": [[i, v] for i, v in enumerate(img.readmax) if v],
        }
        if not quick and not kwargs.get("quick"):
            # what Slot_Load leaves for this image (adv_state.md 7.3)
            case["load"] = _slot_load_case(img, e)
        cases.append(case)
    doc = {"model": "engine_slot_image: History_Push of the first _ZM statement, Staging_SetSnapshot, Slot_Store / "
                    "QuickSave_Store, SaveImage_Commit (date stubbed to 2026-10-25 12:34:56)",
           "live": [[n, va, sz] for n, va, sz in LIVE], "cases": cases}
    prod = _producer("export_slots", {"quick": quick}, ("tsuki.misc_adv_system_state_emu.engine_slot_image",
                                                        "tsuki.misc_adv_system_state_emu.run_slot_load"))
    part.write_bytes("slots.json", (json.dumps(doc, separators=(",", ":")) + "\n").encode("utf-8"), "json", prod, {"cases": len(cases)})
    part.count("slot_cases", len(cases))
    part.count("seconds", round(time.perf_counter() - t0, 1))
    if log is not None:
        log(f"[save] slots: {len(cases)} cases in {time.perf_counter() - t0:.1f} s")


def _run_slot_load(e: Any, image: bytes, slot: int, seed: int) -> Any:
    """``run_slot_load`` of tsuki.misc_adv_system_state_emu with 0x7E4E0 (the scene re-sync) run for real too."""
    import bisect
    import capstone
    from eboot import savedata
    from tsuki import misc_adv_system_state as m
    from tsuki.script import AllScr
    emu = e.AdvEmu(e._eboot_path(None))
    emu.uc.mem_map(e.RING_VA, 0xB80000)
    run = (0x73890, 0xA2D10, 0x7E790, e.RESTORE_VA, 0x73BD0, 0x73BE0, 0x73C20, 0x7E4E0)
    emu.patch_functions(list(run))
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    starts = emu._starts
    ends = {f: starts[bisect.bisect_right(starts, f)] for f in (0x73890, 0xA2D10, 0x7E790, e.RESTORE_VA, 0x7E4E0)}
    for f, end in ends.items():
        for _a, _s, mn, ops in md.disasm_lite(emu.image[f + e.TEXT_OFF:end + e.TEXT_OFF], f):
            if mn in ("call", "jmp") and ops.startswith("0x"):
                t = int(ops, 16)
                if t in run or f <= t < end or t in e.PLT_NAMES or t in emu.stubs:
                    continue
                emu.stub(t, 0)
    e._random_world(emu, seed)
    off = savedata.SLOT_BASE + slot * savedata.SLOT_STRIDE
    blk = image[off:off + savedata.SLOT_SIZE]
    a, _b = m.slot_states(blk)
    script_no = struct.unpack_from("<H", a, 0x18)[0]
    with AllScr() as scr:
        raw = scr.raw(script_no)
    emu.wr(e._SCRIPT_BUF, raw[:e._SCRIPT_MAX])
    emu.wr(0x86AA18, struct.pack("<H", script_no))
    emu.wr(0x120A6E0, struct.pack("<Q", e.RING_VA))
    emu.wr(0xE24600 + slot * 0x7108, blk)
    emu.wr(0xE20CC8, struct.pack("<I", 2))
    emu.pre = {0x17DB230: emu.rd(0x17DB230, 2)}
    emu.log = e.EmuLog()
    emu.call(0x73890, slot)
    return emu


def _slot_load_case(img: Any, e: Any) -> dict[str, Any]:
    """Run 0x73890(slot) -> Slot_Load -> 0x7E790 -> History_Restore -> 0x7E4E0 on ``img`` with random RAM; keep the windows."""
    from eboot import savedata
    seed = 9
    orig_call = e.AdvEmu.call
    before: dict[str, str] = {}

    def hooked(self: Any, va: int, *args: int) -> int:
        if va == 0x73890 and not before:
            before.update(_snap(self))
        return orig_call(self, va, *args)

    e.AdvEmu.call = hooked
    try:
        emu = _run_slot_load(e, img.image, img.slot, seed)
    finally:
        e.AdvEmu.call = orig_call
    after = _snap(emu)
    out = {
        "seed": seed, "live": dict(before), "live_after": _diff(before, after),
        "script_offset_after": f"{emu.u64(e.SCRIPT_PTR) - e._SCRIPT_BUF:016x}",
        "indices_after": _indices(emu),
        "stack_top_after": emu.i16(0x164B1C0),
        "counter_after": emu.u32(0x174DE04), "after_load_after": emu.u32(0x174DE08),
        "stack0": emu.rd(0x120A700, e.REC).hex(), "stack1": emu.rd(0x120A700 + e.REC, e.REC).hex(),
        "ring0": emu.rd(e.RING_VA, e.REC).hex(),
        "skip_latch_before": emu.pre[0x17DB230].hex(), "skip_latch_after": emu.rd(0x17DB230, 2).hex(),
        "slot_bytes": img.image[savedata.SLOT_BASE + img.slot * savedata.SLOT_STRIDE:][:savedata.SLOT_SIZE].hex(),
        "calls": [[c.va, *c.args[:4]] for c in emu.log.calls if c.va in (0x73980, 0x75CD0, 0x7B790)],
    }
    return out


# ---------------------------------------------------------------------------
# navigation: rewind (Adv_Rewind 0xA2020), previous scene (0xA23F0), backlog list (Backlog_Open 0x3BFB0)
# ---------------------------------------------------------------------------

def _stub_externals(emu: Any, e: Any, funcs: Sequence[int]) -> None:
    """Stub (return 0, logged) every call or jump that leaves the functions ``funcs``."""
    import bisect
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    spans = []
    for f in funcs:
        end = emu._starts[bisect.bisect_right(emu._starts, f)]
        spans.append((f, end))
    for f, end in spans:
        for _a, _s, mn, ops in md.disasm_lite(emu.image[f + e.TEXT_OFF:end + e.TEXT_OFF], f):
            if mn in ("call", "jmp") and ops.startswith("0x"):
                t = int(ops, 16)
                if any(lo <= t < hi for lo, hi in spans) or t in e.PLT_NAMES or t in emu.stubs:
                    continue
                emu.stub(t, 0)


def _random_ring(rng: random.Random, wrapped: bool) -> dict[str, Any]:
    """A ring description: per index [serial, kind, effect_blocking_bits, f823 byte], plus cur / next / oldest / wrapped."""
    if wrapped:
        cur = rng.randrange(800)
        nxt = (cur + 1) % 800
        oldest = (nxt + 1) % 800 if nxt < 799 else 0
        valid = [i for i in range(800) if i != nxt]
    else:
        cur = rng.choice((0, 1, 2, 3, 6, 15, 60, 300, 798))
        nxt = cur + 1
        oldest = 0
        valid = list(range(cur + 1))
    kinds = (0, 0, 0, 0, 0, 1, 1, 2, 3, 3, 6, 7, 9, 9, 12, 13, 4, 8, 11)
    recs: dict[int, list[int]] = {}
    for i in valid:
        recs[i] = [rng.randrange(0, 30000), rng.choice(kinds), 0 if rng.random() < 0.85 else rng.choice((1, 4, 0x3000)),
                   rng.choice((0, 1, 1, 2, 3, 0xFF, 0x80))]
    recs[nxt] = [-1, 0, 0, 0]
    return {"cur": cur, "next": nxt, "oldest": oldest, "wrapped": int(wrapped), "records": recs}


def _write_ring(emu: Any, e: Any, ring: dict[str, Any]) -> None:
    emu.wr(0x120A6E0, struct.pack("<Q", RING_VA))
    for i, (serial, kind, blocking, f823) in ring["records"].items():
        base = RING_VA + i * e.REC
        emu.wr(base + 4, struct.pack("<i", serial))
        emu.wr(base + 0x24, struct.pack("<H", kind))
        emu.wr(base + 0x3638, struct.pack("<H", blocking))
        emu.wr(base + 0x3521, bytes([f823]))
    emu.wr(0x120A6EC, struct.pack("<h", ring["cur"]))
    emu.wr(0x120A6EE, struct.pack("<h", ring["next"]))
    emu.wr(0x120A6F0, struct.pack("<H", ring["wrapped"]))
    emu.wr(0x120A6F2, struct.pack("<h", ring["oldest"]))


def _ring_json(ring: dict[str, Any]) -> dict[str, Any]:
    return {**ring, "records": {str(k): v for k, v in ring["records"].items()}}


def rewind_case(name: str, seed: int, wrapped: bool, cursor_shift: int = 0) -> dict[str, Any]:
    from tsuki import misc_adv_system_state_emu as e
    emu = _new_emu([0xA2020])
    _stub_externals(emu, e, [0xA2020])
    e._random_world(emu, seed)
    ring = _random_ring(random.Random(seed * 7919 + 1), wrapped)
    _write_ring(emu, e, ring)
    cursor = ring["cur"]
    if cursor_shift and (wrapped or cursor >= cursor_shift):
        cursor = (cursor - cursor_shift) % 800      # a record that holds a pushed record (unlisted slots are zero = valid)
    emu.wr(0x120A6E8, struct.pack("<h", cursor))
    for va, size in ((0x1653E00, 8), (0x1653E08, 2), (0x1653E0A, 2), (0x1653E0C, 2), (0x1653E0E, 2), (0x1653E70, 8)):
        emu.wr(va, bytes(size))
    emu.log = e.EmuLog()
    emu.call(0xA2020, 1, 0, e.SCRATCH)
    calls = [[c.va, c.args[0] & 0xFFFFFFFF, c.args[1] & 0xFFFFFFFF] for c in emu.log.calls
             if c.va in (0x7C6D0, 0x56730, 0x75CD0, 0x7E4E0)]
    return {"name": name, "ring": _ring_json(ring), "cursor": cursor, "calls": calls, "cursor_after": emu.i16(0x120A6E8)}


def prev_scene_case(name: str, top: int, counter: int, blocking: int, flag10: int) -> dict[str, Any]:
    from tsuki import misc_adv_system_state_emu as e
    emu = _new_emu([0xA23F0])
    _stub_externals(emu, e, [0xA23F0])
    e._random_world(emu, 5)
    emu.stubs[0x5C680] = lambda m: (blocking >> m.r("rdi")) & 1
    emu.wr(0x164B1C0, struct.pack("<h", top))
    emu.wr(0x174DE04, struct.pack("<i", counter))
    emu.wr(0x1653E00, struct.pack("<Q", 0x10 if flag10 else 0))
    emu.log = e.EmuLog()
    emu.call(0xA23F0)
    calls = [[c.va, c.args[0] & 0xFFFFFFFF] for c in emu.log.calls if c.va in (0x7E790, 0x75CD0)]
    return {"name": name, "top": top, "counter": counter, "blocking": blocking, "flag10": flag10, "calls": calls}


def backlog_case(name: str, seed: int, wrapped: bool) -> dict[str, Any]:
    from tsuki import misc_adv_system_state_emu as e
    emu = _new_emu([0x3BFB0, 0x765C0])
    _stub_externals(emu, e, [0x3BFB0, 0x765C0])
    e._random_world(emu, seed)
    ring = _random_ring(random.Random(seed * 104729 + 3), wrapped)
    _write_ring(emu, e, ring)
    rng = random.Random(seed)
    for i, rec in ring["records"].items():
        v = 1 if rng.random() < 0.35 else rng.choice((0, 2))
        emu.wr(RING_VA + i * e.REC + 0x3501 + 9, bytes([v]))       # F799 is slot 9 of the s8 list
        rec.append(v)
    emu.wr(0xDDA390, struct.pack("<i", 0x12345))
    ret = emu.call(0x3BFB0)
    count = emu.i32(0xDDA390)
    entries = [struct.unpack("<i", emu.rd(0xDD9710 + 4 * j, 4))[0] for j in range(800)]
    return {"name": name, "ring": _ring_json(ring), "return": ret & 0xFFFFFFFF, "count": count,
            "entries": [x for x in entries if x >= 0]}


def export_nav(part: PartWriter, quick: bool = False, log: Optional[Callable[[str], None]] = None) -> None:
    t0 = time.perf_counter()
    top_seed = 9 if quick else 25
    rewinds = [rewind_case(f"rewind_{'wrap' if w else 'lin'}_{s}_{shift}", s, w, shift)
               for w in (False, True) for s in range(1, top_seed) for shift in ((0, 3) if s % 4 == 0 else (0,))]
    prevs = [prev_scene_case(f"prev_t{top}_c{cnt}_b{blk}_f{fl}", top, cnt, blk, fl)
             for top in (-1, 0, 1, 2, 5) for cnt in (0, 1, 2) for blk in (0, 8) for fl in (0, 1)]
    backs = [backlog_case(f"backlog_{'wrap' if w else 'lin'}_{s}", s, w) for w in (False, True) for s in range(1, top_seed - 4)]
    doc = {"model": "Adv_Rewind 0xA2020 (History_Restore and the other callees stubbed and logged: calls = [va, rdi, rsi]); "
                    "0xA23F0 (0x7E790 stubbed); Backlog_Open 0x3BFB0 (0x765C0 real). ring.records: index -> "
                    "[serial, kind, effect_blocking_bits, f823 byte (, F799 byte for the backlog)]",
           "rewind": rewinds, "previous_scene": prevs, "backlog": backs}
    prod = _producer("export_nav", {"quick": quick}, ("tsuki.misc_adv_system_state_emu.AdvEmu",))
    part.write_bytes("nav_cases.json", (json.dumps(doc, separators=(",", ":")) + "\n").encode("utf-8"), "json", prod,
                     {"rewind": len(rewinds), "previous_scene": len(prevs), "backlog": len(backs)})
    part.count("rewind_cases", len(rewinds))
    part.count("previous_scene_cases", len(prevs))
    part.count("backlog_cases", len(backs))
    part.count("seconds", round(time.perf_counter() - t0, 1))
    if log is not None:
        log(f"[save] nav: {len(rewinds)} rewind, {len(prevs)} previous-scene, {len(backs)} backlog cases in {time.perf_counter() - t0:.1f} s")


# ---------------------------------------------------------------------------
# runner
# ---------------------------------------------------------------------------

def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Entry point used by ``oracle.run``. Default: every part; ``quick`` exports fewer cases."""
    sel = list(parts) if parts else list(PARTS)
    bad = [p for p in sel if p not in PARTS]
    if bad:
        raise ValueError(f"unknown parts {bad}; choose from {list(PARTS)}")
    gen = _producer("run", {"parts": sel, "game": Path(game).as_posix(), "quick": quick}, ())
    area = AreaWriter(Path(root), AREA, gen, description=DESCRIPTION, game_dir=Path(game))
    for name in sel:
        pw = area.part(name, {"history": "History_Push / History_Restore cases", "slots": "engine-made save slots",
                              "nav": "rewind, previous scene and backlog cases"}[name])
        pw.add_input(Path(game) / "eboot.bin", full_hash=True)
        if name == "history":
            export_history(pw, quick, log)
        elif name == "slots":
            export_slots(pw, quick, log)
        else:
            export_nav(pw, quick, log)
        pw.finish()
    return area.finish()


def main(argv: Optional[Sequence[str]] = None) -> int:
    """``python -m oracle.save [runner options]``: the runner with this area registered."""
    from . import run as runner
    runner.AREAS.setdefault(AREA, MODULE)
    return runner.main([AREA, *(sys.argv[1:] if argv is None else argv)])


if __name__ == "__main__":
    raise SystemExit(main())
