"""Engine tables read from the PS4 eboot.bin at run time (package K1b, docs/research/knife_tables.md).

knife (the Rust reimplementation, ref/knife) hard-codes tables that it copied out of the Switch executable.
The PS4 eboot carries the same data (sometimes retuned, sometimes laid out differently). This module locates
each one by its eboot address and reads it, so a runtime needs no copy of anybody's tables.

Every reader takes ``eb``, any object with ``read_va(va, n) -> bytes`` (``eboot.selfelf.Eboot``) and returns
plain Python values. Addresses are VAs of eboot CUSA47999 v01.01 (docs/formats/engine.md 1.2: text VA = file
offset - 0x2280, RELRO 0x400000, data 0x600000); they are valid only after the build check.

Two tables live in code, not in data, and are read by running the (branch-only) compiled function in a tiny
x86-64 interpreter (``TinyX86``): the STMA loop points (``read_motion_loops``, function 0xC1C70, a compiled
switch) and the flowchart-jump clears (``read_jump_clears``, function 0xBA700).

``python -m tsuki.engine_tables`` prints a census of every table. ``python -m tsuki.engine_tables crosscheck
[KNIFE_ROOT]`` re-computes the knife comparison of the research document (needs the read-only ref/knife
checkout, default ``ref/knife``; nothing from knife is stored in this module). ``python -m tsuki.engine_tables
digests [--write PATH]`` prints the SHA-256 per table that the C# tests must reproduce.
"""
from __future__ import annotations

import re
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Tuple

__all__ = [
    "EngineTableError", "TinyX86", "open_eboot",
    "HAZE_VA", "HAZE_MAX_ID", "read_haze", "haze_entry",
    "SCENE_NODE_VA", "SCENE_NODE_COUNT", "read_scene_node", "scene_node_of_script_index",
    "MOTION_LOOP_FN", "read_motion_loops",
    "JUMP_CLEARS_FN", "read_jump_clears", "jump_clear_words",
    "FlowKeepLists", "FLOW_KEEP_VA", "SAVE_KEEP_VA", "read_flow_keep_lists",
    "FCLN_LINES_VA", "FclnKey", "read_fcln_lines", "FCLN_NOTES_VA", "FclnNote", "read_fcln_notes",
    "VOICE_SPEAKERS_VA", "VOICE_SPEAKER_RECORDS", "read_voice_speakers", "voice_speaker_of",
    "SPEAKER_ROW_BIT_VA", "read_speaker_row_bits", "VOICE_ROW_WORDS_VA", "read_voice_row_words",
    "TEST_SE_VA", "read_test_se_names",
    "DIALOG_MESSAGES_VA", "read_dialog_messages", "ps4_sysmes_id",
    "WINDOW_TABLE_VA", "WindowType", "read_window_types", "read_window_offsets",
    "SOFT_CLIP_RADII_VA", "read_soft_clip_radii",
    "Rect", "read_rects",
    "SAVE_TITLE_VA", "PHASE_TITLE_VA", "FLOW_TITLE_VA", "FLOW_LINE_VA", "FACE_RECT_VA", "NAME_RECT_VA",
    "GUIDE_ICON_VA", "GUIDE_WORD_VA", "BANNER_RECT_VA", "BANNER_KEY_VA",
    "read_save_title_cells", "read_phase_title_cells", "read_flow_title_cells", "read_flow_line_cells",
    "read_voice_face_rects", "read_voice_name_rects", "read_guide_icon_cells", "read_guide_word_cells",
    "read_banner_rects", "read_banner_keys",
    "TEXT_COLOURS_VA", "read_text_colours",
    "ConfigControl", "ConfigRow", "ConfigPage", "CONFIG_PAGES_VA", "read_config_pages_layout",
    "MOTION_MASKS_VA", "read_motion_field_masks",
    "canonical_bytes", "table_digests", "census",
]


class EngineTableError(ValueError):
    """The eboot does not contain the table the way this module expects (wrong build, or a corrupt file)."""


def open_eboot(path: Optional[Path] = None):
    """Open the eboot (``eboot.selfelf.Eboot``): ``path``, else eboot.bin in the game folder (TSUKI_GAME_DIR)."""
    from eboot import eboot_path
    from eboot.selfelf import Eboot

    p = Path(path) if path is not None else eboot_path()
    if p is None:
        raise FileNotFoundError("eboot.bin not found (set TSUKI_GAME_DIR)")
    return Eboot.open(p)


def _i32s(eb, va: int, n: int) -> Tuple[int, ...]:
    return struct.unpack(f"<{n}i", eb.read_va(va, 4 * n))


def _i16s(eb, va: int, n: int) -> Tuple[int, ...]:
    return struct.unpack(f"<{n}h", eb.read_va(va, 2 * n))


def _f32s(eb, va: int, n: int) -> Tuple[float, ...]:
    return struct.unpack(f"<{n}f", eb.read_va(va, 4 * n))


# ------------------------------------------------------------------------------------------------------
# a tiny x86-64 interpreter for the two branch-only table functions
# ------------------------------------------------------------------------------------------------------
class TinyX86:
    """Runs a straight-line / branching x86-64 function that only compares, branches, jumps through a jump
    table and loads constants. It supports exactly the instruction forms the two compiled tables use and
    raises ``EngineTableError`` on anything else, so a different build fails loudly instead of misreading.

    Supported: xor/add/cmp r,r|imm; lea (SIB, rip-relative); movsxd; mov r32, imm32; mov [mem], imm
    (16/32/64 bit); jcc rel8/rel32; jmp rel8/rel32/r64; ret; nop forms. Stores are logged, never performed.
    """

    _NAMES = ("rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15")
    _M64 = (1 << 64) - 1

    def __init__(self, read_va: Callable[[int, int], bytes]) -> None:
        self._read = read_va

    def run(self, rip: int, regs: Optional[Dict[str, int]] = None, max_steps: int = 4096
            ) -> Tuple[Dict[str, int], List[Tuple[int, int, int]]]:
        """Execute from ``rip`` until ``ret``. Returns (registers, stores) with stores as (address, size, value)."""
        r = [0] * 16
        for k, v in (regs or {}).items():
            r[self._NAMES.index(k)] = v & self._M64
        stores: List[Tuple[int, int, int]] = []
        flags = {"zf": False, "sf": False, "cf": False, "of": False}
        for _ in range(max_steps):
            ins = self._decode(rip)
            op = ins[0]
            nxt = rip + ins[1]
            if op == "ret":
                return {n: r[i] for i, n in enumerate(self._NAMES)}, stores
            if op == "jcc":
                if self._cond(ins[2], flags):
                    nxt = nxt + ins[3]
            elif op == "jmp":
                nxt = nxt + ins[2]
            elif op == "jmpreg":
                nxt = self._operand(ins[2], r, nxt)
            elif op == "xor":
                _, _, dst, src, w = ins
                v = self._val(r, dst, w) ^ self._val(r, src, w)
                self._set(r, dst, v, w)
                flags.update(zf=(v == 0), sf=bool(v >> (w - 1) & 1), cf=False, of=False)
            elif op in ("add", "cmp"):
                _, _, dst, src, w = ins
                a = self._val(r, dst, w) if dst[0] == "reg" else self._operand_mem(dst, r, nxt, w)
                b = self._val(r, src, w) if src[0] == "reg" else src[1] & ((1 << w) - 1)
                m = (1 << w) - 1
                if op == "add":
                    res = (a + b) & m
                    flags.update(zf=(res == 0), sf=bool(res >> (w - 1) & 1), cf=(a + b) > m,
                                 of=bool((~(a ^ b) & (a ^ res)) >> (w - 1) & 1))
                    self._set(r, dst, res, w)
                else:
                    res = (a - b) & m
                    flags.update(zf=(res == 0), sf=bool(res >> (w - 1) & 1), cf=a < b,
                                 of=bool(((a ^ b) & (a ^ res)) >> (w - 1) & 1))
            elif op == "lea":
                _, _, dst, mem, w = ins
                self._set(r, dst, self._ea(mem, r, nxt), w)
            elif op == "movsxd":
                _, _, dst, mem = ins
                v = struct.unpack("<i", self._read(self._ea(mem, r, nxt), 4))[0]
                r[dst[1]] = v & self._M64
            elif op == "movimm":
                _, _, dst, imm, w = ins
                self._set(r, dst, imm, w)
            elif op == "storeimm":
                _, _, mem, imm, size = ins
                stores.append((self._ea(mem, r, nxt), size, imm & ((1 << (8 * size)) - 1)))
            elif op == "nop":
                pass
            else:  # pragma: no cover - the decoder only returns the cases above
                raise EngineTableError(f"internal: {op}")
            rip = nxt
        raise EngineTableError("TinyX86: step limit reached")

    # -- operand helpers ------------------------------------------------------------------------------
    def _val(self, r, o, w: int) -> int:
        return r[o[1]] & ((1 << w) - 1)

    def _set(self, r, o, v: int, w: int) -> None:
        if w == 32:
            r[o[1]] = v & 0xFFFFFFFF
        elif w == 64:
            r[o[1]] = v & self._M64
        else:
            raise EngineTableError("TinyX86: unsupported register width")

    def _ea(self, mem, r, nxt: int) -> int:
        kind = mem[0]
        if kind == "rip":
            return (nxt + mem[1]) & self._M64
        _, base, index, scale, disp = mem
        a = disp
        if base is not None:
            a += r[base]
        if index is not None:
            a += r[index] * scale
        return a & self._M64

    def _operand(self, o, r, nxt: int) -> int:
        if o[0] == "reg":
            return r[o[1]]
        raise EngineTableError("TinyX86: indirect jump through memory is not used by these tables")

    def _operand_mem(self, o, r, nxt: int, w: int) -> int:
        raise EngineTableError("TinyX86: memory compare operands are not used by these tables")

    @staticmethod
    def _cond(cc: int, f) -> bool:
        base = (
            f["of"], f["cf"], f["zf"], f["cf"] or f["zf"], f["sf"], False,
            f["sf"] != f["of"], f["zf"] or (f["sf"] != f["of"]),
        )[cc >> 1]
        if cc >> 1 == 5:
            raise EngineTableError("TinyX86: parity conditions are not used by these tables")
        return (not base) if cc & 1 else bool(base)

    # -- decoder ------------------------------------------------------------------------------------
    def _decode(self, rip: int):
        buf = self._read(rip, 16)
        i = 0
        opsz16 = False
        while buf[i] == 0x66:
            opsz16 = True
            i += 1
        rex = 0
        if 0x40 <= buf[i] <= 0x4F:
            rex = buf[i]
            i += 1
        rexw, rexr, rexx, rexb = bool(rex & 8), (rex >> 2) & 1, (rex >> 1) & 1, rex & 1
        op = buf[i]
        i += 1
        w = 64 if rexw else 32

        def modrm():
            nonlocal i
            m = buf[i]
            i += 1
            mod, reg, rm = m >> 6, ((m >> 3) & 7) | (rexr << 3), m & 7
            if mod == 3:
                return mod, reg, ("reg", rm | (rexb << 3))
            if rm == 4:
                sib = buf[i]
                i += 1
                scale = 1 << (sib >> 6)
                idx = ((sib >> 3) & 7) | (rexx << 3)
                base = (sib & 7) | (rexb << 3)
                index = None if idx == 4 else idx
                if (sib & 7) == 5 and mod == 0:
                    disp = struct.unpack_from("<i", buf, i)[0]
                    i += 4
                    return mod, reg, ("mem", None, index, scale, disp)
                disp = 0
                if mod == 1:
                    disp = struct.unpack_from("<b", buf, i)[0]
                    i += 1
                elif mod == 2:
                    disp = struct.unpack_from("<i", buf, i)[0]
                    i += 4
                return mod, reg, ("mem", base, index, scale, disp)
            if rm == 5 and mod == 0:
                disp = struct.unpack_from("<i", buf, i)[0]
                i += 4
                return mod, reg, ("rip", disp)
            base = rm | (rexb << 3)
            disp = 0
            if mod == 1:
                disp = struct.unpack_from("<b", buf, i)[0]
                i += 1
            elif mod == 2:
                disp = struct.unpack_from("<i", buf, i)[0]
                i += 4
            return mod, reg, ("mem", base, None, 1, disp)

        if op == 0xC3:
            return ("ret", i)
        if op == 0x90:
            return ("nop", i)
        if 0x70 <= op <= 0x7F:
            rel = struct.unpack_from("<b", buf, i)[0]
            return ("jcc", i + 1, op & 15, rel)
        if op == 0x0F:
            op2 = buf[i]
            i += 1
            if 0x80 <= op2 <= 0x8F:
                rel = struct.unpack_from("<i", buf, i)[0]
                return ("jcc", i + 4, op2 & 15, rel)
            if op2 == 0x1F:
                modrm()
                return ("nop", i)
            raise EngineTableError(f"TinyX86: opcode 0F {op2:02X} at {rip:#x}")
        if op == 0xEB:
            return ("jmp", i + 1, struct.unpack_from("<b", buf, i)[0])
        if op == 0xE9:
            return ("jmp", i + 4, struct.unpack_from("<i", buf, i)[0])
        if 0xB8 <= op <= 0xBF:
            imm = struct.unpack_from("<I", buf, i)[0]
            return ("movimm", i + 4, ("reg", (op - 0xB8) | (rexb << 3)), imm, 32)
        if op in (0x31, 0x01, 0x29, 0x39):
            _, reg, rm = modrm()
            if rm[0] != "reg":
                raise EngineTableError(f"TinyX86: memory operand of {op:02X} at {rip:#x}")
            name = {0x31: "xor", 0x01: "add", 0x39: "cmp"}.get(op)
            if name is None:
                raise EngineTableError(f"TinyX86: sub at {rip:#x}")
            return (name, i, rm, ("reg", reg), w)
        if op in (0x81, 0x83):
            _, reg, rm = modrm()
            if op == 0x81:
                imm = struct.unpack_from("<i", buf, i)[0]
                i += 4
            else:
                imm = struct.unpack_from("<b", buf, i)[0]
                i += 1
            if rm[0] != "reg" or (reg & 7) not in (0, 7):
                raise EngineTableError(f"TinyX86: group-1 form at {rip:#x}")
            return ("add" if (reg & 7) == 0 else "cmp", i, rm, ("imm", imm), w)
        if op == 0x3D:
            imm = struct.unpack_from("<i", buf, i)[0]
            return ("cmp", i + 4, ("reg", 0), ("imm", imm), w)
        if op == 0x8D:
            _, reg, rm = modrm()
            if rm[0] == "reg":
                raise EngineTableError(f"TinyX86: lea r,r at {rip:#x}")
            return ("lea", i, ("reg", reg), rm, w)
        if op == 0x63 and rexw:
            _, reg, rm = modrm()
            if rm[0] == "reg":
                raise EngineTableError(f"TinyX86: movsxd r,r at {rip:#x}")
            return ("movsxd", i, ("reg", reg), rm)
        if op == 0xFF:
            _, reg, rm = modrm()
            if (reg & 7) == 4 and rm[0] == "reg":
                return ("jmpreg", i, rm)
            raise EngineTableError(f"TinyX86: opcode FF /{reg & 7} at {rip:#x}")
        if op == 0xC7:
            _, reg, rm = modrm()
            if (reg & 7) != 0 or rm[0] == "reg":
                raise EngineTableError(f"TinyX86: opcode C7 form at {rip:#x}")
            if opsz16:
                imm = struct.unpack_from("<H", buf, i)[0]
                return ("storeimm", i + 2, rm, imm, 2)
            imm = struct.unpack_from("<i", buf, i)[0]
            return ("storeimm", i + 4, rm, imm, 8 if rexw else 4)
        raise EngineTableError(f"TinyX86: opcode {op:02X} at {rip:#x} is not supported")


# ------------------------------------------------------------------------------------------------------
# STRA haze (knife: exe.rs ExeTables::haze, sub_91520)
# ------------------------------------------------------------------------------------------------------
HAZE_VA = 0x219910           # text segment; entry k at HAZE_VA + 40 * k, k = STRA id; accessor 0xB3630
HAZE_MAX_ID = 557            # accessor: ids 1..0x22D; entry 0 is the all-zero filler
HAZE_FLOATS = 10


def read_haze(eb) -> List[Tuple[float, ...]]:
    """The haze parameters of ``_STRA``: 558 entries (index = STRA id, entry 0 unused) of 10 floats.
    Field meaning (knife stage.rs ``Haze::params``): [0] x scale, [2] x amplitude factor, [3] y scale,
    [5] y amplitude factor, [6] amplitude, [7] period/size term, [9] fade-in time (ms)."""
    raw = eb.read_va(HAZE_VA, 40 * (HAZE_MAX_ID + 1))
    return [struct.unpack_from("<10f", raw, 40 * k) for k in range(HAZE_MAX_ID + 1)]


def haze_entry(table: Sequence[Tuple[float, ...]], stra_id: int) -> Optional[Tuple[float, ...]]:
    """Entry for a STRA id; ids outside 1..557 have none (accessor 0xB3630 returns NULL)."""
    return table[stra_id] if 1 <= stra_id <= HAZE_MAX_ID else None


# ------------------------------------------------------------------------------------------------------
# scene number -> flowchart node (knife: save_tables.rs SCENE_NODE, sub_92D00)
# ------------------------------------------------------------------------------------------------------
SCENE_NODE_VA = 0x228EF0     # text segment, i32[527]; -1 = no node
SCENE_NODE_COUNT = 527


def read_scene_node(eb) -> List[int]:
    """Flowchart node (RTST bit) of each scene, indexed by script index - 1 (the first script, scene 110, has none)."""
    return list(_i32s(eb, SCENE_NODE_VA, SCENE_NODE_COUNT))


def scene_node_of_script_index(table: Sequence[int], script_index: int) -> int:
    """Node of the script with allscr index ``script_index`` (0-based), -1 when none (index 0 or out of range)."""
    k = script_index - 1
    return table[k] if 0 <= k < len(table) else -1


# ------------------------------------------------------------------------------------------------------
# STMA loop points (knife: motion_loops.rs MOTION_LOOPS, a compiled switch)
# ------------------------------------------------------------------------------------------------------
MOTION_LOOP_FN = 0xC1C70     # int loop_point(int motion_id): 0 none, 1 whole motion, n > 1 loop from n ms
MOTION_ID_MAX = 24192


def read_motion_loops(eb, max_id: int = MOTION_ID_MAX) -> Dict[int, int]:
    """Loop point of every looping STMA motion id (ids 1..24192) by running 0xC1C70 for each id; ids with
    loop point 0 (no loop) are left out. 2,027 entries in the retail build (docs/formats/layers.md 8.1)."""
    cpu = TinyX86(eb.read_va)
    out: Dict[int, int] = {}
    for mid in range(1, max_id + 1):
        regs, _ = cpu.run(MOTION_LOOP_FN, {"rdi": mid})
        v = regs["rax"] & 0xFFFFFFFF
        if v:
            out[mid] = v
    return out


# ------------------------------------------------------------------------------------------------------
# flowchart-jump clears (knife: game.rs JUMP_CLEARS, sub_95800)
# ------------------------------------------------------------------------------------------------------
JUMP_CLEARS_FN = 0xBA700     # void jump_clears(int flow_row): zeroes chapter F words of the rows not reached yet
F_WORDS_BASE = 0x164B1D0     # the F word array F000..F1199 (u16 each)
JUMP_CLEARS_MAX_ROW = 276


def jump_clear_words(eb, row: int) -> List[int]:
    """The F word numbers the flowchart jump to column ``row`` (0..276) sets to 0, by running 0xBA700."""
    cpu = TinyX86(eb.read_va)
    regs, stores = cpu.run(JUMP_CLEARS_FN, {"rdi": row})
    words: List[int] = []
    for addr, size, value in stores:
        if value != 0:
            raise EngineTableError(f"0xBA700 stores a non-zero value at {addr:#x}")
        off = addr - F_WORDS_BASE
        if off < 0 or off % 2:
            raise EngineTableError(f"0xBA700 stores outside the F array: {addr:#x}")
        words.extend(range(off // 2, off // 2 + size // 2))
    return words


def read_jump_clears(eb) -> List[Tuple[int, int]]:
    """(last row, F word) pairs: jumping to a column <= last row zeroes the word. Derived by running 0xBA700 for
    every column 0..276. F062 is cleared up to row 160 here: knife lists (161, 62) with the exception "not at
    row 161 itself", which is the same set of rows."""
    rows_of: Dict[int, List[int]] = {}
    for row in range(JUMP_CLEARS_MAX_ROW + 1):
        for w in jump_clear_words(eb, row):
            rows_of.setdefault(w, []).append(row)
    out: List[Tuple[int, int]] = []
    for w, rows in rows_of.items():
        last = max(rows)
        missing = [r for r in range(last + 1) if r not in rows]
        if missing:
            raise EngineTableError(f"F{w:03d} is not cleared for every row up to {last} (missing {missing[:5]})")
        out.append((last, w))
    out.sort(key=lambda p: (-p[0], p[1]))
    return out


# ------------------------------------------------------------------------------------------------------
# F-word lists kept through a flowchart jump / saved in a history record
# (knife: game.rs JUMP_KEEP_*, save_tables.rs F_WORDS / F_BITS / F_BYTES)
# ------------------------------------------------------------------------------------------------------
FLOW_KEEP_VA = (0x1A4250, 0x1A42A0, 0x1A4500)   # flow-jump copy 0x2A080 / 0x2A1D0: words, bytes, bits
SAVE_KEEP_VA = (0x2160E0, 0x2165F0, 0x216580)   # history copy 0x7AF90 / 0x7C6D0 / 0x76610: words, bytes, bits
KEEP_LIST_LIMIT = 1024


@dataclass(frozen=True)
class FlowKeepLists:
    """Three -1-terminated i16 lists of F word numbers: kept whole (``words``), kept as a signed byte
    (``bytes``) and kept as a flag (``bits``). The PS4 lists are knife's plus a few PS4-only variables."""
    words: Tuple[int, ...]
    bytes: Tuple[int, ...]
    bits: Tuple[int, ...]


def _read_terminated_i16(eb, va: int) -> Tuple[int, ...]:
    out: List[int] = []
    while True:
        v = struct.unpack("<h", eb.read_va(va + 2 * len(out), 2))[0]
        if v == -1:
            return tuple(out)
        out.append(v)
        if len(out) > KEEP_LIST_LIMIT:
            raise EngineTableError(f"list at {va:#x} has no terminator")


def read_flow_keep_lists(eb, saved: bool = False) -> FlowKeepLists:
    """The flow-jump lists (``saved`` False, used by 0x2A080 / 0x2A1D0) or the history-record copies
    (``saved`` True, used by History_Push / _Restore). Both pairs hold the same numbers in retail."""
    w, b, bi = SAVE_KEEP_VA if saved else FLOW_KEEP_VA
    return FlowKeepLists(_read_terminated_i16(eb, w), _read_terminated_i16(eb, b), _read_terminated_i16(eb, bi))


# ------------------------------------------------------------------------------------------------------
# FCLN focus lines (knife: game/focus.rs LINES, dword_35153C)
# ------------------------------------------------------------------------------------------------------
FCLN_LINES_VA = 0x216BC0     # text segment: 30 effects x 4 keys x 9 i32 (36 bytes)
FCLN_EFFECTS = 30


@dataclass(frozen=True)
class FclnKey:
    """One timed key of a focus-line effect; ``time_ms`` -1 means the key is unused."""
    time_ms: int
    centre_x: int          # 1280x720 units
    centre_y: int
    radius_min: int
    radius_max: int
    count_min: int
    count_max: int
    alpha: int             # 0..255
    reroll_ms: int


def read_fcln_lines(eb) -> List[List[FclnKey]]:
    """Per FCLN effect (index 0 = effect 4), its four keys. Effects 0..29 of the table."""
    raw = _i32s(eb, FCLN_LINES_VA, FCLN_EFFECTS * 4 * 9)
    return [[FclnKey(*raw[(e * 4 + k) * 9:(e * 4 + k) * 9 + 9]) for k in range(4)] for e in range(FCLN_EFFECTS)]


FCLN_NOTES_VA = 0x8734A0     # data segment: 3 falling-note records of 0x58 bytes (FCLN effects 1..3)
FCLN_NOTE_STRIDE = 0x58
FCLN_NOTE_COUNT = 3


@dataclass(frozen=True)
class FclnNote:
    """One falling-note kind of FCLN effects 1..3 (knife focus.rs ``NOTES``, ``NOTE_V``, ``NOTE_JITTER``, ``NOTE_FLOOR``)."""
    image: int                     # +0 u16 (57, 58, 59)
    frames: int                    # +2 u16 frame count (24, 20, 16)
    sequence: Tuple[int, ...]      # +4 u16[frames] animation frame order (the record holds 24 slots, then -1)
    width: int                     # +0x36 i16 (563, 225, 225)
    height: int                    # +0x38 i16 (187, 112, 150)
    step: int                      # +0x3A i16
    vx: float                      # +0x3C f32 (-0.4)
    vy: float                      # +0x40 f32 (4.0)
    jitter_x: float                # +0x44 f32 (0.005)
    jitter_y: float                # +0x48 f32 (0.005)
    drift: float                   # +0x4C f32 (~1e-5, no knife counterpart)
    accel: float                   # +0x50 f32 (0.001, 0.0002, 0.0015)
    floor: int                     # +0x54 i32 (450)


def read_fcln_notes(eb) -> List[FclnNote]:
    """The three falling-note records. The frame sequence slots are 0x18 long; ``frames`` of them are used."""
    out = []
    for k in range(FCLN_NOTE_COUNT):
        b = eb.read_va(FCLN_NOTES_VA + FCLN_NOTE_STRIDE * k, FCLN_NOTE_STRIDE)
        image, frames = struct.unpack_from("<HH", b, 0)
        if not 0 < frames <= 24:
            raise EngineTableError(f"note {k}: {frames} frames")
        seq = struct.unpack_from(f"<{frames}h", b, 4)
        width, height, step = struct.unpack_from("<3h", b, 0x36)
        vx, vy, jx, jy, drift, accel = struct.unpack_from("<6f", b, 0x3C)
        out.append(FclnNote(image, frames, seq, width, height, step, vx, vy, jx, jy, drift, accel,
                            struct.unpack_from("<i", b, 0x54)[0]))
    return out


# ------------------------------------------------------------------------------------------------------
# voice speakers (knife: game.rs VOICE_SPEAKERS, SPEAKER_ROW_BIT, VOICE_ROW_WORDS)
# ------------------------------------------------------------------------------------------------------
VOICE_SPEAKERS_VA = 0x215E90   # text segment: 36 records of 16 bytes {char key[8]; i32 speaker; i32 0}
VOICE_SPEAKER_RECORDS = 36


def read_voice_speakers(eb) -> Dict[str, int]:
    """Voice file name prefix (3 characters, e.g. ``A00``) -> speaker number 0..23. The eboot stores each
    prefix reversed (``00A``); this returns the file-name order. Prefixes that are absent map to speaker 0."""
    out: Dict[str, int] = {}
    for k in range(VOICE_SPEAKER_RECORDS):
        rec = eb.read_va(VOICE_SPEAKERS_VA + 16 * k, 16)
        key = rec[:8].split(b"\0")[0].decode("ascii")[::-1]
        out[key] = struct.unpack_from("<i", rec, 8)[0]
    return out


def voice_speaker_of(table: Dict[str, int], voice_name: str) -> int:
    """Speaker of a voice file name (first three letters, case-insensitive); 0 when unlisted."""
    return table.get(voice_name[:3].upper(), 0)


SPEAKER_ROW_BIT_VA = 0x86A850  # data segment: i32[24], speaker -> config voice-row bit (row = log2)


def read_speaker_row_bits(eb) -> Tuple[int, ...]:
    return _i32s(eb, SPEAKER_ROW_BIT_VA, 24)


VOICE_ROW_WORDS_VA = 0x1A6850  # text segment: i32[19], config voice row -> progress F word


def read_voice_row_words(eb) -> Tuple[int, ...]:
    return _i32s(eb, VOICE_ROW_WORDS_VA, 19)


# ------------------------------------------------------------------------------------------------------
# config SE test list (knife: game.rs TEST_SES, off_46B138)
# ------------------------------------------------------------------------------------------------------
TEST_SE_VA = 0x1A6774          # text segment: 14 inline 13-byte names
TEST_SE_COUNT = 14


def read_test_se_names(eb) -> Tuple[str, ...]:
    return tuple(eb.read_va(TEST_SE_VA + 13 * k, 13).split(b"\0")[0].decode("ascii") for k in range(TEST_SE_COUNT))


# ------------------------------------------------------------------------------------------------------
# dialog / help message ids (knife: ui.rs DIALOG_SYSMES, dword_3C9340)
# ------------------------------------------------------------------------------------------------------
DIALOG_MESSAGES_VA = 0x2D2D40  # text segment: i32[55]; -1 = no message (docs/formats/gallery.md 1, "help messages")
DIALOG_MESSAGE_COUNT = 55
SYSMES_OVERSEAS_INSERT = 217   # the overseas SYSMES table has one message more from here on


def read_dialog_messages(eb) -> Tuple[int, ...]:
    """The ui_text id of each confirm dialog / help message (the first 26 are the dialogs of ui.rs; the rest
    are the ending-list prompts). knife's numbers are the Japanese SYSMES numbers; see ``ps4_sysmes_id``."""
    return _i32s(eb, DIALOG_MESSAGES_VA, DIALOG_MESSAGE_COUNT)


def ps4_sysmes_id(knife_id: int) -> int:
    """knife SYSMES message number -> PS4 ui_text id: the overseas table inserts one message at 217."""
    return knife_id + 1 if knife_id >= SYSMES_OVERSEAS_INSERT else knife_id


# ------------------------------------------------------------------------------------------------------
# message window types (knife: game.rs WINDOWS / EN_WINDOWS, WINDOW_DX / WINDOW_DY)
# ------------------------------------------------------------------------------------------------------
WINDOW_TABLE_VA = 0x2006C0     # text segment: 56 x {u32 slot; f32 x; f32 y}, 1280x720 units (textbox.md 7)
WINDOW_TYPES = 56
WINDOW_DX_VA, WINDOW_DX_COUNT = 0x2005B0, 27
WINDOW_DY_VA, WINDOW_DY_COUNT = 0x200620, 37


@dataclass(frozen=True)
class WindowType:
    slot: int      # PS4 image slot (knife's slot numbers differ: another texture list)
    x: float
    y: float


def read_window_types(eb) -> List[WindowType]:
    return [WindowType(*struct.unpack("<Iff", eb.read_va(WINDOW_TABLE_VA + 12 * k, 12))) for k in range(WINDOW_TYPES)]


def read_window_offsets(eb) -> Tuple[Tuple[float, ...], Tuple[float, ...]]:
    """(x offsets by F1106, y offsets by F1107), 1280x720 units."""
    return _f32s(eb, WINDOW_DX_VA, WINDOW_DX_COUNT), _f32s(eb, WINDOW_DY_VA, WINDOW_DY_COUNT)


# ------------------------------------------------------------------------------------------------------
# soft clip radii (knife: stage.rs RADII, dword_34D81C)
# ------------------------------------------------------------------------------------------------------
SOFT_CLIP_RADII_VA = 0x200590  # text segment: f32[6] = 10, 32, 40, 64, 128, 256


def read_soft_clip_radii(eb) -> Tuple[float, ...]:
    return _f32s(eb, SOFT_CLIP_RADII_VA, 6)


# ------------------------------------------------------------------------------------------------------
# UI texture cell tables: 4 x i16 per cell (knife ui.rs / flow_table.rs)
# ------------------------------------------------------------------------------------------------------
@dataclass(frozen=True)
class Rect:
    """A cell of a UI sheet in texels: (x0, y0, x1, y1) unless the table says otherwise."""
    a: int
    b: int
    c: int
    d: int

    def as_tuple(self) -> Tuple[int, int, int, int]:
        return (self.a, self.b, self.c, self.d)


def read_rects(eb, va: int, count: int) -> List[Rect]:
    raw = _i16s(eb, va, 4 * count)
    return [Rect(*raw[4 * k:4 * k + 4]) for k in range(count)]


SAVE_TITLE_VA = {"other": 0x7A85C0, "en": 0x7A86F0}              # 38 cells each, x0 y0 x1 y1 (savetitle sheet)
PHASE_TITLE_VA = {"ja": 0x7A6E70, "en": 0x7A6FA0, "zh": 0x7A70D0}  # 38 cells each (system_phasetitle sheet)
FLOW_TITLE_VA = {"other": 0x1A3EE0, "en": 0x1A3FD0}              # 30 cells each, x y w h (flow_phasetitle); last = Prologue
FLOW_LINE_VA = 0x1A40C0                                            # 12 cells x y w h (flow_line_merge, grey kinds 0..11)
FACE_RECT_VA, NAME_RECT_VA = 0x66E6B0, 0x66E750                   # 20 cells x0 y0 x1 y1 (conf_faceicon / conf_name)
NAME_RECT_EN_VA = 0x66E7F0                                         # 20 English conf_name cells
GUIDE_ICON_VA, GUIDE_ICON_COUNT = 0x7A6190, 23                      # help_key icon cells x0 y0 x1 y1 (PS4 pad icons)
GUIDE_WORD_VA, GUIDE_WORD_COUNT = 0x7A6250, 27                      # help_txt word cells x0 y0 x1 y1, one block per language
GUIDE_WORD_BLOCK = 27 * 8                                          # bytes per language block (0x6A765: imul 0xD8)
BANNER_RECT_VA = (0x781B10, 0x781B20)                              # mode banner cells {x0 y0 x1 y1; i32; i32}
BANNER_KEY_VA, BANNER_KEY_COUNT = 0x781AC0, 10                      # banner scale keys {f32 x; f32 y}


def read_save_title_cells(eb, language: str = "other") -> List[Rect]:
    """Savetitle source rects by phase F023 (38 phases); ``language`` ``en`` or ``other`` (ja/zc/zt)."""
    return read_rects(eb, SAVE_TITLE_VA[language], 38)


def read_phase_title_cells(eb, language: str = "ja") -> List[Rect]:
    """system_phasetitle source rects by phase index (38); ``language`` ``ja``, ``en`` or ``zh``."""
    return read_rects(eb, PHASE_TITLE_VA[language], 38)


def read_flow_title_cells(eb, language: str = "other") -> List[Rect]:
    """Flowchart chapter-label cells (x, y, w, h) by chapter_label index; the entry after the last (index 29)
    is zero in the PS4 table (knife has a separate Prologue cell)."""
    return read_rects(eb, FLOW_TITLE_VA[language], 30)


def read_flow_line_cells(eb) -> List[Rect]:
    """Flowchart connector cells (x, y, w, h) by connector kind 0..11 (grey)."""
    return read_rects(eb, FLOW_LINE_VA, 12)


def read_voice_face_rects(eb) -> List[Rect]:
    """conf_faceicon cells (x0 y0 x1 y1) per config voice row (20)."""
    return read_rects(eb, FACE_RECT_VA, 20)


def read_voice_name_rects(eb, english: bool = False) -> List[Rect]:
    """conf_name cells (x0 y0 x1 y1) per config voice row (20)."""
    return read_rects(eb, NAME_RECT_EN_VA if english else NAME_RECT_VA, 20)


def read_guide_icon_cells(eb) -> List[Rect]:
    return read_rects(eb, GUIDE_ICON_VA, GUIDE_ICON_COUNT)


def read_guide_word_cells(eb, language: int = 0) -> List[Rect]:
    """help_txt word cells (x0 y0 x1 y1) of one language (0 ja, 1 en, 2 zc, 3 zt, 4 ko): 27 cells, in the order of
    the knife word list (back, select, move, ..., scroll)."""
    if not 0 <= language <= 4:
        raise ValueError("language 0..4")
    return read_rects(eb, GUIDE_WORD_VA + GUIDE_WORD_BLOCK * language, GUIDE_WORD_COUNT)


def read_banner_rects(eb) -> List[Rect]:
    """The two mode-banner cells of the PS4 build (x0 y0 x1 y1), at 0x781B10 and 0x781B20."""
    return [Rect(*_i16s(eb, va, 4)) for va in BANNER_RECT_VA]


def read_banner_keys(eb) -> List[Tuple[float, float]]:
    """Banner pop-in scale keys (x scale, y scale) sampled over t * 9."""
    f = _f32s(eb, BANNER_KEY_VA, 2 * BANNER_KEY_COUNT)
    return [(f[2 * k], f[2 * k + 1]) for k in range(BANNER_KEY_COUNT)]


# ------------------------------------------------------------------------------------------------------
# message colours @c1..@c5 (knife: text.rs fill_colour; docs/formats/text.md 6)
# ------------------------------------------------------------------------------------------------------
TEXT_COLOURS_VA = 0x2D34C0     # text segment: u16[6] RGB565; word 0 is the default colour, words 1..5 are @c1..@c5


def read_text_colours(eb) -> List[Tuple[int, int, int]]:
    """The five fill colours of ``@c1``..``@c5`` as 8-bit (R, G, B) from the RGB565 words (bits replicated)."""
    out = []
    for w in struct.unpack("<6H", eb.read_va(TEXT_COLOURS_VA, 12))[1:]:
        r5, g6, b5 = w >> 11, (w >> 5) & 63, w & 31
        out.append(((r5 << 3) | (r5 >> 2), (g6 << 2) | (g6 >> 4), (b5 << 3) | (b5 >> 2)))
    return out


# ------------------------------------------------------------------------------------------------------
# config screen pages (knife: ui.rs conf_layout_at, dword_46CDB0 + 628 * tab)
# ------------------------------------------------------------------------------------------------------
CONFIG_PAGES_VA = 0x66E890     # data segment: 2 pages of 0x9D0 bytes (628 i32), the System and Sound tabs
CONFIG_PAGE_STRIDE = 0x9D0
CONFIG_PAGE_COUNT = 2
CONFIG_ROW_LIMIT = 26
CONFIG_UNUSED_POSITION = -9999.0


@dataclass(frozen=True)
class ConfigControl:
    item: int          # item id (config menu item), grid +0x280
    kind: int          # type code, grid +0x78: 1 test button, 2 check box, 3 / 4 / 5 left / middle / right control of a
                       # slider row (adv_state.md 9.1 says label / value / slider; knife puts its minus button, slider
                       # and plus button at these coordinates [H]), 6..10 radio groups
    x: float           # 640x448 units; -9999 for the rows the Sound tab places at run time (+0x488)
    y: float


@dataclass(frozen=True)
class ConfigRow:
    controls: Tuple[ConfigControl, ...]
    current: int       # +0x900
    maximum: int       # +0x968 (slider maximum)


@dataclass(frozen=True)
class ConfigPage:
    first_item: int
    last_item: int
    rows: Tuple[ConfigRow, ...]


def read_config_pages_layout(eb) -> List[ConfigPage]:
    """The two data-driven config pages with the control positions (the older ``misc_adv_system_state
    .read_config_pages`` leaves the positions out). Page 0 is the System tab (16 rows), page 1 the Sound tab
    (26 rows; rows 7..25, one per character, are placed at run time and hold -9999)."""
    pages: List[ConfigPage] = []
    for p in range(CONFIG_PAGE_COUNT):
        b = eb.read_va(CONFIG_PAGES_VA + CONFIG_PAGE_STRIDE * p, CONFIG_PAGE_STRIDE)
        first, last, _, nrows = struct.unpack_from("<4i", b, 0)
        if not 0 <= nrows <= CONFIG_ROW_LIMIT:
            raise EngineTableError(f"config page {p}: {nrows} rows")
        rows = []
        for r in range(nrows):
            k = struct.unpack_from("<i", b, 0x10 + 4 * r)[0]
            if not 0 <= k <= 5:
                raise EngineTableError(f"config page {p} row {r}: {k} controls")
            ctl = []
            for c in range(k):
                item = struct.unpack_from("<i", b, 0x280 + 20 * r + 4 * c)[0]
                kind = struct.unpack_from("<i", b, 0x78 + 20 * r + 4 * c)[0]
                x, y = struct.unpack_from("<2f", b, 0x488 + 40 * r + 8 * c)
                ctl.append(ConfigControl(item, kind, x, y))
            rows.append(ConfigRow(tuple(ctl), struct.unpack_from("<i", b, 0x900 + 4 * r)[0],
                                  struct.unpack_from("<i", b, 0x968 + 4 * r)[0]))
        pages.append(ConfigPage(first, last, tuple(rows)))
    return pages


# ------------------------------------------------------------------------------------------------------
# STMA field masks (knife: exe.rs motion_masks); decoded before by misc_layer_state_and_motions.MotionTable
# ------------------------------------------------------------------------------------------------------
MOTION_MASKS_VA = 0x245130     # text segment: u64[24193] indexed by motion id (0xC1C40: 0 outside 1..24192)
MOTION_MASK_COUNT = 24193


def read_motion_field_masks(eb) -> Tuple[int, ...]:
    """Field-enable bit masks of each STMA motion id (index = id; entry 0 unused)."""
    return struct.unpack(f"<{MOTION_MASK_COUNT}Q", eb.read_va(MOTION_MASKS_VA, 8 * MOTION_MASK_COUNT))


# ------------------------------------------------------------------------------------------------------
# digests: the equality proof between this module and the C# readers (tests/Tsuki.Tests.Data/EbootTables)
# ------------------------------------------------------------------------------------------------------
def _pack_rects(rects: Iterable[Rect]) -> bytes:
    return b"".join(struct.pack("<4h", *r.as_tuple()) for r in rects)


def _pack_i32(values: Iterable[int]) -> bytes:
    vals = list(values)
    return struct.pack(f"<{len(vals)}i", *vals)


def canonical_bytes(eb) -> Dict[str, bytes]:
    """Every table of this module in a canonical byte form (little-endian i32 / f32 / i16 / u64 runs, strings as
    UTF-8 + NUL). The C# tests rebuild the same bytes from their own readers."""
    out: Dict[str, bytes] = {}
    out["haze"] = b"".join(struct.pack("<10f", *e) for e in read_haze(eb))
    out["scene_node"] = _pack_i32(read_scene_node(eb))
    loops = read_motion_loops(eb)
    out["motion_loops"] = _pack_i32(v for k in sorted(loops) for v in (k, loops[k]))
    out["jump_clears"] = _pack_i32(v for pair in read_jump_clears(eb) for v in pair)
    for tag, saved in (("flow_keep", False), ("save_keep", True)):
        keep = read_flow_keep_lists(eb, saved)
        out[tag] = b"".join(_pack_i32([len(lst), *lst]) for lst in (keep.words, keep.bytes, keep.bits))
    out["fcln_lines"] = _pack_i32(v for e in read_fcln_lines(eb) for k in e for v in
                                  (k.time_ms, k.centre_x, k.centre_y, k.radius_min, k.radius_max, k.count_min, k.count_max, k.alpha, k.reroll_ms))
    out["fcln_notes"] = b"".join(
        struct.pack("<2H", n.image, n.frames) + struct.pack("<24h", *(list(n.sequence) + [-1] * (24 - n.frames)))
        + struct.pack("<3h6fi", n.width, n.height, n.step, n.vx, n.vy, n.jitter_x, n.jitter_y, n.drift, n.accel, n.floor)
        for n in read_fcln_notes(eb))
    sp = read_voice_speakers(eb)
    out["voice_speakers"] = b"".join(k.encode("ascii") + b"\0" + struct.pack("<i", sp[k]) for k in sorted(sp))
    out["speaker_row_bits"] = _pack_i32(read_speaker_row_bits(eb))
    out["voice_row_words"] = _pack_i32(read_voice_row_words(eb))
    out["test_se"] = b"".join(n.encode("ascii") + b"\0" for n in read_test_se_names(eb))
    out["dialog_messages"] = _pack_i32(read_dialog_messages(eb))
    out["window_types"] = b"".join(struct.pack("<Iff", w.slot, w.x, w.y) for w in read_window_types(eb))
    dx, dy = read_window_offsets(eb)
    out["window_offsets"] = struct.pack(f"<{len(dx)}f", *dx) + struct.pack(f"<{len(dy)}f", *dy)
    out["soft_clip_radii"] = struct.pack("<6f", *read_soft_clip_radii(eb))
    out["save_title_other"] = _pack_rects(read_save_title_cells(eb, "other"))
    out["save_title_en"] = _pack_rects(read_save_title_cells(eb, "en"))
    for lang in ("ja", "en", "zh"):
        out[f"phase_title_{lang}"] = _pack_rects(read_phase_title_cells(eb, lang))
    out["flow_title_other"] = _pack_rects(read_flow_title_cells(eb, "other"))
    out["flow_title_en"] = _pack_rects(read_flow_title_cells(eb, "en"))
    out["flow_line"] = _pack_rects(read_flow_line_cells(eb))
    out["voice_face"] = _pack_rects(read_voice_face_rects(eb))
    out["voice_name"] = _pack_rects(read_voice_name_rects(eb))
    out["voice_name_en"] = _pack_rects(read_voice_name_rects(eb, True))
    out["guide_icon"] = _pack_rects(read_guide_icon_cells(eb))
    for lang_no in range(5):
        out[f"guide_word_{lang_no}"] = _pack_rects(read_guide_word_cells(eb, lang_no))
    out["banner_rects"] = _pack_rects(read_banner_rects(eb))
    out["banner_keys"] = b"".join(struct.pack("<2f", *k) for k in read_banner_keys(eb))
    cfg = bytearray()
    for page in read_config_pages_layout(eb):
        cfg += struct.pack("<3i", page.first_item, page.last_item, len(page.rows))
        for row in page.rows:
            cfg += struct.pack("<i", len(row.controls))
            for c in row.controls:
                cfg += struct.pack("<iiff", c.item, c.kind, c.x, c.y)
            cfg += struct.pack("<2i", row.current, row.maximum)
    out["config_pages"] = bytes(cfg)
    out["text_colours"] = bytes(v for c in read_text_colours(eb) for v in c)
    out["motion_masks"] = struct.pack(f"<{MOTION_MASK_COUNT}Q", *read_motion_field_masks(eb))
    out.update(_gallery_flow_bytes(eb))
    return out


def _gallery_flow_bytes(eb) -> Dict[str, bytes]:
    """Canonical bytes of the gallery and flowchart tables (decoded by ``misc_gallery_extras_flowchart``)."""
    from . import misc_gallery_extras_flowchart as gal

    def z(text: str) -> bytes:
        return text.encode("ascii") + bytes(1)

    d = gal.load(eb.path)
    out: Dict[str, bytes] = {}
    cg = bytearray()
    for g in d.cg_groups:
        cg += _pack_i32([g.index, g.category, len(g.entries)])
        for e in g.entries:
            cg += z(e.name) + z(e.prefix) + _pack_i32([e.cg_th, e.x, e.y, e.w, e.h, e.flag])
    out["cg_entries"] = bytes(cg)
    out["cg_aliases"] = b"".join(z(a.key) + _pack_i32([len(a.aliases)]) + b"".join(z(n) for n in a.aliases) for a in d.cg_aliases)
    out["cg_special"] = b"".join(z(n) for n in d.cg_special)
    out["seen_map"] = b"".join(z(m.name) + _pack_i32([m.seen_index]) for m in d.seen_map)
    out["music"] = b"".join(
        _pack_i32([t.bgm, t.title_ui, t.vocal_ui, t.lyrics_ui, t.composer_ui, t.arranger_ui, t.length_ms, t.loop_start, t.loop_end,
                   len(t.variants), *t.variants]) for t in d.music)
    out["extras"] = b"".join(_pack_i32([x.script_scene, x.fc_scene, x.unlock_a, x.unlock_b, x.message, x.message_ui]) for x in d.extras)
    out["help_messages"] = _pack_i32(d.help_messages)
    out["trophy_scenes"] = _pack_i32(d.trophy_scenes)
    out["category_flags"] = b"".join(_pack_i32([c, len(f), *f]) for c, f in sorted(d.category_flags.items()))
    flow = bytearray()
    for c in d.columns:
        flow += _pack_i32([c.index, c.group, c.count])
        for n in c.nodes:
            flow += _pack_i32([n.scene, n.conn, n.link, n.script_scene, n.chapter_label, n.order, n.fc_th, n.title_ui, n.synopsis_ui])
    out["flow_columns"] = bytes(flow)
    out["flow_edges"] = b"".join(_pack_i32([a, b, len(g), *g, len(f), *f]) for a, b, g, f in d.edges)
    out["choice_flags"] = _pack_i32(v for t in d.choice_flags for v in t)
    return out


def table_digests(eb) -> Dict[str, str]:
    """SHA-256 (hex) of ``canonical_bytes`` per table."""
    import hashlib

    return {k: hashlib.sha256(v).hexdigest() for k, v in canonical_bytes(eb).items()}


# ------------------------------------------------------------------------------------------------------
# census and CLI
# ------------------------------------------------------------------------------------------------------
def census(eb) -> Dict[str, object]:
    """Counts of what every reader returns (a quick sanity check of an eboot)."""
    keep = read_flow_keep_lists(eb)
    loops = read_motion_loops(eb)
    clears = read_jump_clears(eb)
    return {
        "haze entries": len(read_haze(eb)),
        "scene_node": len(read_scene_node(eb)),
        "motion loops": len(loops),
        "jump clears": len(clears),
        "keep words/bytes/bits": (len(keep.words), len(keep.bytes), len(keep.bits)),
        "fcln effects / notes": (len(read_fcln_lines(eb)), len(read_fcln_notes(eb))),
        "voice speakers": len(read_voice_speakers(eb)),
        "test SEs": len(read_test_se_names(eb)),
        "dialog messages": len(read_dialog_messages(eb)),
        "window types": len(read_window_types(eb)),
    }


# ------------------------------------------------------------------------------------------------------
# research helper: compare every table with the literals of the knife checkout
# ------------------------------------------------------------------------------------------------------
# knife (ref/knife) is read-only and git-ignored. Its tables are parsed at run time from the checkout and
# compared with what the readers above return; the numbers of docs/research/knife_tables.md come from here.
# No knife value is stored in this module.
_RS_TOKEN = re.compile(
    r"""(?P<ws>\s+|//[^\n]*)|(?P<str>"(?:\\.|[^"\\])*")|(?P<chr>'(?:\\.|[^'\\])')"""
    r"""|(?P<num>-?(?:0x[0-9A-Fa-f_]+|\d[\d_]*(?:\.\d*)?(?:[eE][+-]?\d+)?)(?:u8|u16|u32|u64|usize|i8|i16|i32|i64|f32|f64)?)"""
    r"""|(?P<id>[A-Za-z_][A-Za-z_0-9:]*)|(?P<p>[\[\]\(\),&;{}=*/+\-.])"""
)
_RS_SUFFIX = re.compile(r"(u8|u16|u32|u64|usize|i8|i16|i32|i64|f32|f64)$")


class _RustLiteral:
    """Parser for the subset of Rust literal syntax the knife tables use: nested arrays / tuples of numbers,
    strings, bool, None / Some, repeat ``[x; n]``."""

    def __init__(self, text: str) -> None:
        self.t = [(m.lastgroup, m.group(0)) for m in _RS_TOKEN.finditer(text) if m.lastgroup != "ws"]
        self.i = 0

    def _peek(self):
        return self.t[self.i] if self.i < len(self.t) else (None, None)

    def _next(self):
        v = self.t[self.i]
        self.i += 1
        return v

    @staticmethod
    def _num(v: str):
        v = _RS_SUFFIX.sub("", v.replace("_", ""))
        if v.startswith("-0x"):
            return -int(v[1:], 16)
        if v.startswith("0x"):
            return int(v, 16)
        return int(v) if re.fullmatch(r"-?\d+", v) else float(v)

    def value(self):
        k, v = self._peek()
        if v == "&":
            self._next()
            return self.value()
        if v in ("[", "("):
            close = "]" if v == "[" else ")"
            self._next()
            out: list = []
            while self._peek()[1] != close:
                out.append(self.value())
                if self._peek()[1] == ";":
                    self._next()
                    out = out * self.value()
                    continue
                if self._peek()[1] == ",":
                    self._next()
            self._next()
            return out if v == "[" else tuple(out)
        if k == "num":
            self._next()
            return self._num(v)
        if k == "str":
            self._next()
            return v[1:-1]
        if k == "id":
            self._next()
            if v in ("true", "false"):
                return v == "true"
            if v == "None":
                return None
            if v == "Some":
                self._next()
                x = self.value()
                self._next()
                return x
            return ("ID", v)
        if v == "-":
            self._next()
            return -self._num(self._next()[1])
        raise EngineTableError(f"cannot parse Rust literal near {self.t[self.i - 3:self.i + 3]}")


def _rust(src: Path, fname: str, name: str, after: str = ""):
    """Value of ``const|static NAME: ... = <literal>`` in a knife source file (optionally after a marker)."""
    text = (src / fname).read_text(encoding="utf-8")
    start = text.index(after) if after else 0
    m = re.search(r"(?:const|static)\s+" + re.escape(name) + r"\s*:[^=]*=\s*", text[start:])
    if m is None:
        raise KeyError(f"{fname}: {name}")
    lit = _RustLiteral(text[start + m.end():start + m.end() + 400_000])
    if lit._peek()[1] == "{":              # `= { const N: ... = ...; [ ... ] }`: skip the inner const
        lit._next()
        while lit._next()[1] != "=":
            pass
        lit.value()
        lit._next()
    return lit.value()


@dataclass
class CrossRow:
    table: str
    matched: int
    total: int
    note: str = ""

    def __str__(self) -> str:
        return f"{self.table:36s} {self.matched:>6d} / {self.total:<6d} {self.note}"


def _rects_equal(a: Sequence[Sequence[int]], b: Sequence[Rect], n: Optional[int] = None) -> Tuple[int, int]:
    n = len(a) if n is None else n
    return sum(1 for i in range(n) if i < len(b) and tuple(a[i]) == b[i].as_tuple()), n


def _bundle(knife_root: Path) -> Dict[str, bytes]:
    raw = zlib.decompress((knife_root / "knife" / "data" / "exe_tables_overseas.bin").read_bytes())
    if raw[:4] != b"KXT0":
        raise EngineTableError("not an exe table bundle")
    n = struct.unpack_from("<I", raw, 36)[0]
    names = ("image_names", "image_info", "scene_ids", "haze", "motion_keys", "motion_ends", "motion_lens",
             "motion_masks", "line_break_classes", "line_break_pairs")
    out: Dict[str, bytes] = {}
    o = 40
    for k in range(n):
        _, ln = struct.unpack_from("<QI", raw, o)
        out[names[k]] = raw[o + 12:o + 12 + ln]
        o += 12 + ln
    return out


def _fcln_flat(keys: Iterable[FclnKey]) -> List[List[int]]:
    return [[k.time_ms, k.centre_x, k.centre_y, k.radius_min, k.radius_max, k.count_min, k.count_max, k.alpha, k.reroll_ms] for k in keys]


def crosscheck(eb, knife_root: Path) -> List[CrossRow]:
    """Compare the readers of this module (and the older gallery / motion / image decoders) with knife's tables.
    ``knife_root`` is ``ref/knife``. Returns one row per comparison."""
    from . import misc_gallery_extras_flowchart as gal

    src = knife_root / "knife" / "src"
    rows: List[CrossRow] = []

    def add(table: str, matched: int, total: int, note: str = "") -> None:
        rows.append(CrossRow(table, matched, total, note))

    # -- the Switch table bundle (binary tables of exe.rs) -------------------------------------------
    b = _bundle(knife_root)
    ps = read_haze(eb)
    same = sum(1 for k in range(HAZE_MAX_ID + 1) if struct.pack("<10f", *ps[k]) == b["haze"][40 * k:40 * k + 40])
    add("haze (exe.rs)", same, HAZE_MAX_ID + 1, "the other entries were retuned for the PS4")
    ids = struct.unpack("<549H", b["scene_ids"][:1098])
    add("scene_ids (exe.rs)", sum(1 for a, c in zip(ids, _i16s(eb, 0x216130, 549)) if a == (c & 0xFFFF)), 549)
    names = b["image_names"]
    n = len(names) // 32
    pn = eb.read_va(0x7F6800, 32 * n)

    def nm(r: bytes) -> bytes:
        return r.split(b"\0")[0].split(b"\r")[0]

    add("image_names (exe.rs)", sum(1 for k in range(n) if nm(names[32 * k:32 * k + 32]) == nm(pn[32 * k:32 * k + 32])), n)
    info, pinfo = b["image_info"], eb.read_va(0x671900, 72 * n)

    def flags_eq(k: int) -> bool:
        a, c = info[72 * k:72 * k + 72], pinfo[72 * k:72 * k + 72]
        return a[67] & 3 == c[0x3F] & 3 and a[68] == c[0x40] and a[70:72] == c[0x42:0x44]

    add("image_info flags/char/scale", sum(1 for k in range(n) if flags_eq(k)), n, "knife +67/+68/+70 = PS4 +0x3F/+0x40/+0x42")

    def wh_eq(k: int) -> bool:
        kw, kh = struct.unpack_from("<2f", info, 72 * k + 44)
        pw, ph_ = struct.unpack_from("<2H", pinfo, 72 * k + 0x30)
        return abs(kw - pw / 3.0) < 1e-2 and abs(kh - ph_ * 362 / 1080.0) < 1e-2

    add("image_info size (f32 vs u16)", sum(1 for k in range(n) if wh_eq(k)), n, "knife f32 = PS4 logical size / 3 and * 362/1080")
    nk = len(b["motion_keys"]) // 64
    keys, pkeys = b["motion_keys"], eb.read_va(0x873FF0, 72 * nk)

    def key_eq(i: int) -> bool:
        k, p = keys[64 * i:64 * i + 64], pkeys[72 * i:72 * i + 72]
        return (struct.unpack_from("<I", k, 0)[0] == struct.unpack_from("<q", p, 0)[0] and k[4:18] == p[8:22]
                and struct.unpack_from("<i", k, 20)[0] == struct.unpack_from("<q", p, 0x18)[0] and k[24:62] == p[0x20:0x46])

    add("motion_keys (exe.rs)", sum(1 for i in range(nk) if key_eq(i)), nk)
    for nm_, va_ in (("motion_ends", 0x2A3938), ("motion_lens", 0x274538)):
        kv = struct.unpack("<24192I", b[nm_][:4 * 24192])
        pv = struct.unpack("<24192q", eb.read_va(va_, 8 * 24192))
        add(f"{nm_} (exe.rs)", sum(1 for a, c in zip(kv, pv) if a == c), 24192)
    km = struct.unpack("<24193Q", b["motion_masks"][:8 * 24193])
    pm = struct.unpack("<24193Q", eb.read_va(0x245130, 8 * 24193))
    add("motion_masks (exe.rs)", sum(1 for a, c in zip(km, pm) if a == c), 24193)
    pat = b["line_break_pairs"][:240]
    found = 0
    for ph in eb.load_segments():
        fo = eb.phdr_file_offset(ph)
        if fo is not None and eb.data[fo:fo + ph.filesz].find(pat) >= 0:
            found = 1
    add("line_break_pairs (exe.rs)", found, 1, "0 = not in the PS4 eboot (own kinsoku code, textbox.md 5.2)")

    # -- Rust source tables ---------------------------------------------------------------------------
    kl = {int(a): int(v) for a, v in _rust(src, "motion_loops.rs", "MOTION_LOOPS")}
    pl = read_motion_loops(eb)
    add("MOTION_LOOPS", sum(1 for k, v in kl.items() if pl.get(k) == v), len(kl), f"PS4 has {len(pl)} loops")
    kc = sorted((a, w) for a, w in _rust(src, "game.rs", "JUMP_CLEARS"))
    pc = read_jump_clears(eb)
    knorm = [((160 if (a, w) == (161, 62) else a), w) for a, w in kc]
    add("JUMP_CLEARS", sum(1 for x in knorm if x in set(pc)), len(kc), "knife (161, 62) with exception = PS4 (160, 62)")
    keep = (read_flow_keep_lists(eb), read_flow_keep_lists(eb, saved=True))
    for name, field in (("JUMP_KEEP_WORDS", "words"), ("JUMP_KEEP_BYTES", "bytes"), ("JUMP_KEEP_BITS", "bits")):
        kl_ = list(_rust(src, "game.rs", name))
        pl_ = list(getattr(keep[0], field))
        extra = sorted(set(pl_) - set(kl_))
        add(name, sum(1 for x in kl_ if x in set(pl_)), len(kl_),
            f"order kept: {[x for x in pl_ if x in set(kl_)] == [x for x in kl_ if x in set(pl_)]}; PS4-only {extra}")
    for name, field in (("F_WORDS", "words"), ("F_BITS", "bits"), ("F_BYTES", "bytes")):
        kl_ = list(_rust(src, "save_tables.rs", name))
        pl_ = list(getattr(keep[1], field))
        add(name, sum(1 for x in kl_ if x in set(pl_)), len(kl_), f"PS4-only {sorted(set(pl_) - set(kl_))}")
    # flowchart
    d = gal.load(eb.path)
    tot = ok7 = ok9 = 0
    for ci, (_, recs) in enumerate(_rust(src, "flow_table.rs", "ROWS")):
        for j, rec in enumerate(recs):
            tot += 1
            nd = d.columns[ci].nodes[j]
            p9 = [nd.scene, nd.conn, nd.link, nd.script_scene, nd.chapter_label, nd.order, nd.fc_th, nd.title_ui, nd.synopsis_ui]
            ok7 += p9[:7] == list(rec[:7])
            ok9 += p9[:7] == list(rec[:7]) and all(p9[k] == (ps4_sysmes_id(rec[k]) if rec[k] >= 0 else -1) for k in (7, 8))
    add("flow ROWS nodes: fields 0..6", ok7, tot)
    add("flow ROWS nodes: all 9 (+1 rule)", ok9, tot, "fields 7/8 are SYSMES ids: PS4 = knife + 1 from 217")
    ek = _rust(src, "flow_table.rs", "EDGES")
    pe = {(a, c, g) for a, c, g, _ in d.edges}
    add("flow EDGES", sum(1 for e in ek if (e[0], e[1], tuple(v for v in e[2:] if v > 0)) in pe), len(ek), f"PS4 has {len(d.edges)} edges")
    sin = _rust(src, "flow_table.rs", "SCENE_ID_NODE")
    sn = read_scene_node(eb)
    sids = struct.unpack("<549h", eb.read_va(0x216130, 1098))
    derived = {sids[k + 1]: sn[k] for k in range(SCENE_NODE_COUNT) if sn[k] >= 0}
    add("flow SCENE_ID_NODE", sum(1 for a, nd in sin if derived.get(a) == nd), len(sin), "= scene_ids[n + 1] with SCENE_NODE[n]")
    add("SCENE_NODE", sum(1 for a, c in zip(_rust(src, "save_tables.rs", "SCENE_NODE"), sn) if a == c), SCENE_NODE_COUNT)
    ft = _rust(src, "flow_table.rs", "TITLES")
    add("flow TITLES", *_rects_equal(ft, read_flow_title_cells(eb, "other")), "row 29 (Prologue) is zero in the PS4 table")
    add("flow TITLES_EN", *_rects_equal(_rust(src, "flow_table.rs", "TITLES_EN"), read_flow_title_cells(eb, "en")))
    add("flow LINES (grey kinds)", *_rects_equal(_rust(src, "flow_table.rs", "LINES"), read_flow_line_cells(eb), 12), "lit half not in the PS4 table")
    # ui cells
    add("SAVE_TITLES", *_rects_equal(_rust(src, "ui.rs", "SAVE_TITLES"), read_save_title_cells(eb, "other")), "phases 16, 18 widened to x1 571 (knife comment)")
    add("SAVE_TITLES_EN", *_rects_equal(_rust(src, "ui.rs", "SAVE_TITLES_EN"), read_save_title_cells(eb, "en")))
    for nm_, lang in (("PHASE_TITLES", "ja"), ("PHASE_TITLES_EN", "en"), ("PHASE_TITLES_ZH", "zh")):
        add(nm_, *_rects_equal(_rust(src, "ui.rs", nm_), read_phase_title_cells(eb, lang)))
    add("GUIDE_ICONS", *_rects_equal(_rust(src, "ui.rs", "GUIDE_ICONS"), read_guide_icon_cells(eb)), "PS4 pad icons differ from row 13")
    add("GUIDE_WORDS (ja)", *_rects_equal(_rust(src, "ui.rs", "GUIDE_WORDS"), read_guide_word_cells(eb, 0)))
    add("FACE_RECTS", *_rects_equal(_rust(src, "ui.rs", "T", "const FACE_RECTS"), read_voice_face_rects(eb)))
    add("NAME_RECTS", *_rects_equal(_rust(src, "ui.rs", "T", "const NAME_RECTS:"), read_voice_name_rects(eb)))
    add("NAME_RECTS_EN", *_rects_equal(_rust(src, "ui.rs", "T", "const NAME_RECTS_EN"), read_voice_name_rects(eb, True)))
    bk = _rust(src, "ui.rs", "BANNER_KEYS")
    pk = read_banner_keys(eb)
    add("BANNER_KEYS", sum(1 for a, c in zip(bk, pk) if abs(a[0] - c[0]) < 1e-6 and abs(a[1] - c[1]) < 1e-6), len(bk))
    br = [tuple(int(v) for v in r) for r in _rust(src, "ui.rs", "BANNER_SRC")]
    prs = {r.as_tuple() for r in read_banner_rects(eb)}
    add("BANNER_SRC", sum(1 for r in br if r in prs), len(br), "the PS4 build keeps 2 banner cells")
    dm = _rust(src, "ui.rs", "DIALOG_SYSMES")
    pd = read_dialog_messages(eb)
    mapped = [pd[k if k <= 20 else k + 2] for k in range(len(dm))]
    add("DIALOG_SYSMES", sum(1 for a, c in zip(dm, mapped) if (a == 0 and c == -1) or (a != 0 and c == ps4_sysmes_id(a))), len(dm),
        "PS4 dialog k >= 21 is k + 2 (two inserted); ids +1 from 217; 0 = -1")
    # game
    vs = {k: v for k, v in _rust(src, "game.rs", "VOICE_SPEAKERS")}
    pv_ = read_voice_speakers(eb)
    add("VOICE_SPEAKERS", sum(1 for k, v in vs.items() if pv_.get(k, 0) == v), len(vs), f"the eboot stores {len(pv_)} prefixes, the rest map to 0")
    add("SPEAKER_ROW_BIT", sum(1 for a, c in zip(_rust(src, "game.rs", "SPEAKER_ROW_BIT"), read_speaker_row_bits(eb)) if a == c), 24)
    add("VOICE_ROW_WORDS", sum(1 for a, c in zip(_rust(src, "game.rs", "VOICE_ROW_WORDS"), read_voice_row_words(eb)) if a == c), 19)
    add("TEST_SES", sum(1 for a, c in zip(_rust(src, "game.rs", "TEST_SES"), read_test_se_names(eb)) if a == c), 14)
    wt = read_window_types(eb)
    ew = _rust(src, "game.rs", "EN_WINDOWS")
    add("EN_WINDOWS (x, y)", sum(1 for a, c in zip(ew, wt) if a[1] == c.x and a[2] == c.y), len(ew), "slot numbers differ (another texture list)")
    jw = _rust(src, "game.rs", "WINDOWS")
    add("WINDOWS (x, y)", sum(1 for a, c in zip(jw, wt) if a[1] == c.x and a[2] == c.y), len(jw), "slot numbers differ")
    dx, dy = read_window_offsets(eb)
    add("WINDOW_DX", sum(1 for a, c in zip(_rust(src, "game.rs", "WINDOW_DX"), dx) if abs(a - c) < 1e-6), len(dx))
    add("WINDOW_DY", sum(1 for a, c in zip(_rust(src, "game.rs", "WINDOW_DY"), dy) if abs(a - c) < 1e-6), len(dy))
    kflat = [[-1] * 9 if r == ("ID", "N") else list(r) for e in _rust(src, "game/focus.rs", "LINES") for r in e]
    pflat = _fcln_flat(k for e in read_fcln_lines(eb) for k in e)
    add("FCLN LINES", sum(1 for a, c in zip(kflat, pflat) if a == c), len(kflat))
    notes_src = (src / "game" / "focus.rs").read_text(encoding="utf-8")
    kn = re.findall(r"Note \{ frames: (\d+), size: \(([\d.]+), ([\d.]+)\), step: (\d+), accel: ([\d.]+) \}", notes_src)
    pn = read_fcln_notes(eb)
    add("FCLN NOTES (frames, size, step, accel)", sum(1 for a, c in zip(kn, pn) if (int(a[0]), float(a[1]), float(a[2]), int(a[3])) == (c.frames, c.width, c.height, c.step)
                                                    and abs(float(a[4]) - c.accel) < 1e-7), len(kn), "records of 0x58 bytes at 0x8734A0")
    rr = _rust(src, "stage.rs", "RADII")
    add("stage RADII", sum(1 for a, c in zip(rr, read_soft_clip_radii(eb)) if a == c), len(rr))
    # gallery family (decoded by tsuki.misc_gallery_extras_flowchart)
    mus = _rust(src, "gallery_table.rs", "MUSIC")
    ml = _rust(src, "gallery_table.rs", "MUSIC_LOOP")
    mc = _rust(src, "gallery_table.rs", "MUSIC_CREDITS")
    sh = ps4_sysmes_id
    add("MUSIC bgm/title/variants", sum(1 for t, m in zip(d.music, mus) if t.bgm == m[0] and t.title_ui == sh(m[1])
                                       and list(t.variants) == [v for v in m[3] if v >= 0]), len(mus))
    add("MUSIC length_ms", sum(1 for t, m in zip(d.music, mus) if t.length_ms == m[2]), len(mus))
    add("MUSIC_LOOP start", sum(1 for t, m in zip(d.music, ml) if t.loop_start == m[0]), len(ml))
    add("MUSIC_LOOP end", sum(1 for t, m in zip(d.music, ml) if t.loop_end + 1 == m[1]), len(ml), "knife end = PS4 end + 1")
    add("MUSIC_CREDITS", sum(1 for t, m in zip(d.music, mc) if [t.vocal_ui, t.lyrics_ui, t.composer_ui, t.arranger_ui]
                             == [sh(v) if v >= 0 else -1 for v in m]), len(mc))
    ends = _rust(src, "gallery.rs", "ENDINGS")
    add("ENDINGS (scene id, node, prompt)", sum(1 for e, x in zip(ends, d.extras)
                                               if x.script_scene == e[1] and x.fc_scene == e[0] and x.message_ui == sh(e[2])), len(ends))
    done = _rust(src, "gallery.rs", "EN_ENDING_DONE")
    add("EN_ENDING_DONE", sum(1 for a, x in zip(done, d.extras) if (x.unlock_a, x.unlock_b) == tuple(a)), len(done))
    cgk = [(c, list(e)) for c, e in _rust(src, "gallery_table.rs", "CG_GROUPS")]
    for gi, ents in _rust(src, "gallery_table.rs", "CG_GROUPS_OVERSEAS"):
        cgk[gi] = (cgk[gi][0], list(ents))
    eq = tot = 0
    for gi, (cond, ents) in enumerate(cgk):
        pg = d.cg_groups[gi]
        for ei, e in enumerate(ents):
            tot += 1
            if ei < len(pg.entries):
                pe_ = pg.entries[ei]
                eq += (pe_.name.upper().removesuffix(".MZP"), pe_.cg_th, pe_.w, pe_.h, pg.category) == (e[0], e[2], e[3], e[4], cond)
    add("CG_GROUPS (+ overseas groups)", eq, tot, "name, thumbnail, size, category")
    al = _rust(src, "gallery_table.rs", "CG_ALIASES")
    add("CG_ALIASES", sum(1 for a, c in zip(al, d.cg_aliases) if c.key.upper().removesuffix(".MZP") == a[0]
                          and [x.upper().removesuffix(".MZP") for x in c.aliases] == list(a[1])), len(al))
    byname = {e.name.upper().removesuffix(".MZP"): e for e in d.cg_entries}
    og = _rust(src, "gallery_table.rs", "CG_ORIGINS")
    add("CG_ORIGINS", sum(1 for n_, x, y in og if n_ in byname and (byname[n_].x, byname[n_].y) == (x, y)), len(og))
    ls = _rust(src, "gallery_table.rs", "CG_LANGUAGE_STRIPS")
    add("CG_LANGUAGE_STRIPS", sum(1 for n_ in ls if n_ + ".MZP" in d.cg_special), len(ls))
    sa = _rust(src, "save_tables.rs", "SEEN_ALIAS")
    add("SEEN_ALIAS", sum(1 for a, c in zip(sa, d.seen_map) if c.name.upper() == a[0].upper() and c.seen_index == a[1]), len(sa))
    # message colours
    cols = read_text_colours(eb)
    kcol = _rust(src, "text.rs", "TSUKIHIME", "pub fn fill_colour")
    add("fill colours @c1..@c5", sum(1 for a, c in zip(kcol, cols) if all(abs(((a >> (8 * k)) & 255) - c[k]) <= 8 for k in range(3))), len(kcol),
        "RGB565 words vs RGBA8 (tolerance 8 per channel)")
    rows.extend(_stec_rows(eb, src))
    return rows


def _stec_rows(eb, src: Path) -> List[CrossRow]:
    """STEC grades: the PS4 stores evaluated LUTs, knife the 20 shader parameters per mode. For the per-channel modes
    that have no tint layer (levels and gain only) the LUT is the plain levels / gamma curve
    ``low + (high - low) * (x / 255) ** (1 / gain)``; compare that with the PS4 LUTs (x >= 1; the PS4 forces x = 0 to 0).
    Modes with tint layers need knife's blend formulas and are not evaluated here (section 2.2 of the research document)."""
    import numpy as np

    from .misc_compositing_transitions_shaders import read_stec_tables

    table = _rust(src, "grade.rs", "TABLE")
    tabs = read_stec_tables(eb.path)
    x = np.arange(256, dtype=np.float64)
    plain = exact = 0
    tinted = 0
    for k in range(1, 82):
        if int(tabs.flags[k - 1]) != 0x100:
            continue
        e = table[k]
        if any(int(t[4]) >= 1 for t in (e[10:15], e[15:20])):
            tinted += 1
            continue
        plain += 1
        worst = 0.0
        for c in range(3):                                   # knife channel c = r, g, b; the PS4 LUT bytes are b, g, r
            gain = e[3 * c] if e[3 * c] != -9.0 else 1.0
            low = e[3 * c + 1] / 255.0 if e[3 * c + 1] != -9.0 else 0.0
            high = e[3 * c + 2] / 255.0 if e[3 * c + 2] != -9.0 else 1.0
            curve = 255.0 * (low + (high - low) * np.power(x / 255.0, np.inf if gain == 0 else 1.0 / gain))
            lut = np.round(np.clip(curve, 0, 255))
            worst = max(worst, float(np.abs(lut[1:] - tabs.luts[k - 1][2 - c].astype(np.float64)[1:]).max()))
        exact += worst == 0.0
    flags = [int(f) for f in tabs.flags]
    return [CrossRow("STEC modes without tint layers", exact, plain,
                     f"LUT = levels / gamma curve exactly for x >= 1; {tinted} tinted per-channel modes, "
                     f"{flags.count(0x200)} luma modes, {flags.count(0x400)} fill mode not evaluated")]


def _crosscheck_main(args: Sequence[str]) -> int:
    root = Path(args[0]) if args else Path(__file__).resolve().parents[2] / "ref" / "knife"
    if not (root / "knife" / "src").is_dir():
        print(f"knife checkout not found at {root}", file=sys.stderr)
        return 2
    eb = open_eboot()
    try:
        for row in crosscheck(eb, root):
            print(row)
    finally:
        eb.close()
    return 0


def _main(argv: Sequence[str]) -> int:
    if argv and argv[0] == "crosscheck":
        return _crosscheck_main(argv[1:])
    if argv and argv[0] == "digests":
        import json
        eb = open_eboot()
        try:
            text = json.dumps(table_digests(eb), indent=2, sort_keys=True) + "\n"
        finally:
            eb.close()
        if len(argv) > 2 and argv[1] == "--write":
            with open(argv[2], "w", encoding="utf-8", newline="\n") as fh:
                fh.write(text)
        else:
            print(text, end="")
        return 0
    eb = open_eboot()
    try:
        for k, v in census(eb).items():
            print(f"{k:24s} {v}")
    finally:
        eb.close()
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
