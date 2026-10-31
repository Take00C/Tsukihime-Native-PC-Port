"""Tests for the eboot.bin analysis tools (tools/eboot).

Synthetic tests always run; real-data tests skip when the game folder is missing
(set TSUKI_GAME_DIR to override).  Runtime: ~6 s (one disassembly index build, the
vectorised evaluation of both script interpreters, decompressing the 549 scripts once for
the command cross-check, four short unicorn emulations).
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_eboot.py -q
"""
from __future__ import annotations

import faulthandler
import struct
import sys
import zlib
from collections import Counter
from pathlib import Path
from typing import Dict, List, Optional, Tuple

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from eboot import eboot_path  # noqa: E402
from eboot import nids, savedata  # noqa: E402

EBOOT = eboot_path()
needs_game = pytest.mark.skipif(EBOOT is None, reason="game data not found")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def test_nid_formula_known_pairs() -> None:
    # pairs printed by shadPS4 for this game's imports
    assert nids.nid("malloc") == "gQX+4GDQjpM"
    assert nids.nid("free") == "tIhsqj0qsFE"
    assert nids.nid("sceAvPlayerInit") == "aS66RI0gGgo"
    assert nids.nid("sceRtcGetCurrentClockLocalTime") == "ZPD1YOKI+Kw"


def test_nid_db_is_self_consistent() -> None:
    db = nids.load_db()          # raises if any name does not hash to its NID
    assert len(db) == 337


def _covers(fields: List[Tuple[int, int]], size: int) -> bool:
    pos = 0
    for off, n in sorted(fields):
        if off != pos:
            return False
        pos = off + n
    return pos == size


def test_savedata_layout_constants() -> None:
    last_end = savedata.SLOT_BASE + (savedata.NUM_SLOTS - 1) * savedata.SLOT_STRIDE + savedata.SLOT_SIZE
    assert last_end <= savedata.SAVE_SIZE
    assert savedata.SYSTEM_SIZE <= savedata.SLOT_BASE
    assert savedata.SLOT_SIZE <= savedata.SLOT_STRIDE
    assert 8 + savedata.SNAPSHOT_SIZE + savedata.SNAPSHOT_B_SIZE == 0x70C0
    # the field tables describe every byte of both blocks, without overlaps
    assert _covers([(o, n) for o, n, _, _ in savedata.SYSTEM_FIELDS], savedata.SYSTEM_SIZE)
    assert _covers([(o, n) for o, n, _ in savedata.SLOT_FIELDS], savedata.SLOT_SIZE)


def test_savedata_blank_and_synthetic_slot() -> None:
    img = savedata.parse(savedata.build_blank())
    assert img.system_magic == savedata.MAGIC and not img.issues
    assert sum(s.present for s in img.slots) == 0
    b = bytearray(savedata.build_blank())
    off = savedata.SLOT_BASE + 5 * savedata.SLOT_STRIDE
    struct.pack_into("<HHi", b, off, savedata.MAGIC, 1, 0)
    struct.pack_into("<Q", b, off + 8 + 0x08, 0x1234)
    struct.pack_into("<H", b, off + 8 + 0x18, 42)
    struct.pack_into("<HBBBBB", b, off + 0x70C0, 2026, 10, 25, 7, 30, 59)
    struct.pack_into("<H", b, off + 0x70C8, savedata.MAGIC)
    img = savedata.parse(bytes(b))
    s = img.slots[5]
    assert s.present and s.script_index == 42 and s.script_offset == 0x1234
    assert str(s.time) == "2026-10-25 07:30:59" and not img.issues


def test_savedata_reports_real_size_of_short_file() -> None:
    img = savedata.parse(b"\xf8\x2a\x01\x00" + b"\0" * 996)
    assert img.size == 1000 and img.issues == ["size 0x3e8 != 0x400000"]


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@pytest.fixture(scope="module")
def eb():  # type: ignore[no-untyped-def]
    from eboot.selfelf import Eboot

    assert EBOOT is not None
    e = Eboot.open(EBOOT)
    yield e
    e.close()


@pytest.fixture(scope="module")
def ix(eb):  # type: ignore[no-untyped-def]
    from eboot.disasm import Index

    return Index.build(eb)


@needs_game
def test_self_and_elf_layout(eb) -> None:  # type: ignore[no-untyped-def]
    assert len(eb.self_segments) == 10 and len(eb.phdrs) == 12
    assert eb.elf_off == 0x160 and eb.entry == 0xEF390
    t, d, r = eb.text, eb.data_seg, eb.relro
    assert (t.vaddr, t.filesz, eb.phdr_file_offset(t)) == (0, 0x329DC8, 0x2280)
    assert (d.vaddr, d.filesz, d.memsz, eb.phdr_file_offset(d)) == (0x600000, 0x7D66E0, 0x1A85790, 0x365FA0)
    assert r is not None and (r.vaddr, eb.phdr_file_offset(r)) == (0x400000, 0x32C210)
    assert eb.cstr(0x2D3F26) == b"voice.hed"
    name, _ = eb.module_info()
    assert name == "devXXX"
    assert eb.dynstr(eb.dyn1(0x61000009)).endswith("moonML/ORBIS/Release/devXXX.elf")


@needs_game
def test_imports_fully_resolved(eb) -> None:  # type: ignore[no-untyped-def]
    db = nids.load_db()
    imps = eb.imports()
    uniq = {i.nid: i for i in imps}
    assert len(uniq) == 337
    # 27 DT_SCE_IMPORT_LIB entries; 26 of them have imported symbols (libSceNpCommon none)
    assert len(eb.dyn(0x61000015)) == 27 and len(eb.dyn(0x6100000F)) == 27
    used = {i.library for i in imps}
    assert len(used) == 26
    assert {lib.name for lib in eb.libraries().values()} - used == {"libSceNpCommon"}
    assert all(n in db for n in uniq)
    assert eb.find_plt(imps) == 334                  # every JUMP_SLOT has a PLT stub
    per_lib = Counter(i.library for i in uniq.values())
    assert per_lib["libSceGnmDriver"] == 97 and per_lib["libc"] == 68 and per_lib["libkernel"] == 48
    names = {db[i.nid] for i in uniq.values()}
    for must in ("sceVideoOutSetFlipRate", "sceAvPlayerAddSource", "sceAudiodecDecode",
                 "sceSaveDataMount2", "scePadReadState", "sceMouseRead", "sceNpTrophyUnlockTrophy",
                 "sceGnmSubmitAndFlipCommandBuffers", "sincosf"):
        assert must in names
    # the game never imports libSceJpegDec although it loads the module
    assert not any("Jpeg" in i.library for i in imps)
    # the 377 R_X86_64_64 relocations: which symbols they bind
    syms = eb.symbols()
    r64 = Counter(db[syms[r.sym].name.split("#")[0]] for r in eb.rela() if r.type == 1)
    assert r64 == {"__cxa_pure_virtual": 371, "Need_sceLibc": 2, "_ZSt7nothrow": 1,
                   "vprintf": 1, "memcpy": 1, "__stack_chk_guard": 1}


@needs_game
def test_function_index_and_symbols(eb, ix) -> None:  # type: ignore[no-untyped-def]
    from eboot.symbols import SYMBOLS

    assert len(ix.funcs) == 7482 and ix.funcs[0] == 0x80
    fs = set(ix.funcs)
    for s in SYMBOLS:
        if s.kind == "func":
            assert s.va in fs, s
    assert len(ix.ptr_at) == 39459                   # R_X86_64_RELATIVE slots


@needs_game
def test_frame_timing_constants(eb) -> None:  # type: ignore[no-untyped-def]
    vals = struct.unpack("<5f", eb.read_va(0x2D7E30, 20))
    assert abs(vals[0] - 16.666668) < 1e-4 and vals[1] == 100.0 and vals[4] == 999.0
    assert struct.unpack("<4i", eb.read_va(0x2D7E90, 16)) == (-1, 0, 1, 2)
    # _WTTM: frames = ms * 3 / 50 (lea eax,[r8+r8*2]; cdqe; imul rcx,rax,0x51eb851f; ...; sar 0x24)
    from eboot.disasm import disasm_range

    seq = [(i.mnem, i.ops) for i in disasm_range(eb, 0x9ACF7, 0x9AD0F)]
    assert seq[0] == ("lea", "eax, [r8 + r8*2]")
    assert ("imul", "rcx, rax, 0x51eb851f") in seq and ("sar", "rcx, 0x24") in seq


def _vm_command_counts() -> Tuple[Dict[str, int], int]:
    """Command names as the VM sees them: each segment ends at ';' (or after 0x300 bytes),
    the name is the 4 bytes after the segment's first '_' (VA 0x95EC0..0x95F4E).
    Returns (counts of names starting with A..Z, number of segments)."""
    import mmap

    from tsuki import image_dir
    from tsuki.mzp import MzpArchive
    from tsuki.mzx import decompress, is_mzx

    img = image_dir()
    assert img is not None
    cnt: Counter[str] = Counter()
    nseg = 0
    with open(img / "allscr.mrg", "rb") as f:
        m = mmap.mmap(f.fileno(), 0, access=mmap.ACCESS_READ)
        a = MzpArchive(m)
        for i in range(len(a)):
            d = a.read(i)
            if not is_mzx(d):
                continue
            s = bytes(decompress(d, invert=True)).split(b"\0")[0]
            for seg in s.split(b";"):
                for k in range(0, max(len(seg), 1), 0x300):     # 0x300-byte scan window
                    part = seg[k:k + 0x300]
                    nseg += 1
                    u = part.find(b"_")
                    if u >= 0 and len(part) >= u + 5 and 0x41 <= part[u + 1] <= 0x5A:
                        cnt[part[u + 1:u + 5].decode("latin-1")] += 1
        del a
        m.close()
    return dict(cnt), nseg


@pytest.fixture(scope="module")
def vms(eb, ix):  # type: ignore[no-untyped-def]
    from eboot.scriptvm import analyse, find_interpreters

    found = find_interpreters(ix)
    assert found == [(0x95E60, 0x20CAC0), (0x9B760, 0x20D170)]
    return analyse(eb, ix, *found[0]), analyse(eb, ix, *found[1])


@needs_game
def test_script_vm_main_table(vms) -> None:  # type: ignore[no-untyped-def]
    a, _ = vms
    assert a.skip_va == 0x95EB0 and a.budget == 1000 and a.yield_va == 0x9B516
    assert a.counter_reg == "r11d"
    names = [c.name for c in a.commands]
    assert len(names) == 112 and [n for n in names if "*" in n] == ["J***", "Z***"]
    A = a.by_name()
    assert A["WKST"].handler == 0x96566 and A["WKST"].via == "ne-fallthrough"
    assert A["STBR"].calls[0] == 0x830D0
    assert A["WTTM"].exits == "yield" and A["WKST"].exits == "continue"
    assert A["RTM2"].handler == 0x97FBC                # sets 0x174DE0C, not a no-op
    assert A["SEFI"].handler == A["SEPL"].handler and A["VPL2"].handler == A["VPLY"].handler
    # jmp trampolines are followed to the real handler code
    assert A["WTTM"].handler == A["WT__"].handler == 0x9AC1A


@needs_game
def test_script_vm_prefetch_table(vms) -> None:  # type: ignore[no-untyped-def]
    _, b = vms
    assert b.skip_va == 0x9FF30 and b.yield_va == 0xA017F and b.counter_reg == "r15d"
    assert b.budget == 60                               # cmp r15d, 0x3A; jg exit; inc r15d
    names = [c.name for c in b.commands]
    # 41 names + J*** + ZY**: the V case (0x9BD0F, cmp/setne/cmp/setne/test/jne) accepts
    # only VPLY and VPL2, not every V name
    assert len(names) == 43 and [n for n in names if "*" in n] == ["J***", "ZY**"]
    B = b.by_name()
    assert B["VPLY"].handler == B["VPL2"].handler == 0x9BD2B
    assert b.lookup("VCWT") is None and b.lookup("VAAA") is None
    # the seven names behind the masked compare at 0x9F158 (and eax, ~2; cmp eax, 'TCU')
    assert B["STCU"].handler == B["STCW"].handler == 0x9F169 and B["STCU"].via == "masked"
    assert {"STFC", "STFI", "STFO", "STMV", "STT2"} <= set(B)
    for n in ("END_", "FRET", "RTM_", "RTMN", "J***"):
        assert B[n].handler == 0xA017F and B[n].exits == "exit"
    assert b.lookup("ZY12") is not None and b.lookup("ZM12") is None
    assert b.lookup("STBR") is None


@needs_game
def test_script_vm_against_scripts(vms) -> None:  # type: ignore[no-untyped-def]
    a, _ = vms
    used, nseg = _vm_command_counts()
    zl = sum(n for k, n in used.items() if k[0] == "Z")
    cmds = {k: n for k, n in used.items() if k[0] != "Z"}
    assert len(cmds) == 94 and sum(cmds.values()) == 1_036_358 and zl == 46_810
    assert cmds["STTR"] == 111                          # incl. the typo `_STTR,0,26);`
    handled = {n for n in used if a.lookup(n) is not None}
    # names used by the scripts that the PS4 interpreter silently skips
    assert set(used) - handled == {"DMMY", "FANX", "IFP4", "NOP_", "SNX4", "STQK"}
    assert nseg > 1_083_000


@needs_game
def test_wkst_variable_writes() -> None:
    """Emulate ScriptStep (unicorn) on _WKST: F/G writes land at var_F/var_G + 2n without a
    bounds check; Z operands are not written at all."""
    unicorn = pytest.importorskip("unicorn")
    from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RDI, UC_X86_REG_RIP, UC_X86_REG_RSP

    assert EBOOT is not None
    data = EBOOT.read_bytes()

    def run(cmd: str) -> List[Tuple[int, int]]:
        mu = unicorn.Uc(unicorn.UC_ARCH_X86, unicorn.UC_MODE_64)
        mu.mem_map(0, 0x2086000)
        mu.mem_write(0, data[0x2280:0x2280 + 0x329DC8])
        mu.mem_write(0x400000, data[0x32C210:0x32C210 + 0x35ED0])
        mu.mem_write(0x600000, data[0x365FA0:0x365FA0 + 0x7D66E0])
        mu.mem_map(0x3000000, 0x100000)
        mu.mem_map(0x3200000, 0x10000)
        mu.mem_write(0x3200000, cmd.encode() + b"\0" * 64)
        mu.reg_write(UC_X86_REG_RSP, 0x30F0000)
        mu.reg_write(UC_X86_REG_RDI, 0x3200000)
        writes: List[Tuple[int, int]] = []

        def code(uc, addr, size, _):  # type: ignore[no-untyped-def]
            if addr in (0x95EB0, 0x9B516):               # next command / yield
                uc.emu_stop()
            elif 0x1734A0 <= addr < 0x174980:            # PLT import: return 0
                rsp = uc.reg_read(UC_X86_REG_RSP)
                uc.reg_write(UC_X86_REG_RIP, int.from_bytes(uc.mem_read(rsp, 8), "little"))
                uc.reg_write(UC_X86_REG_RSP, rsp + 8)
                uc.reg_write(UC_X86_REG_RAX, 0)

        def wr(uc, acc, addr, size, val, _):  # type: ignore[no-untyped-def]
            if 0x164B1D0 <= addr < 0x164E000:
                writes.append((addr, val))

        mu.hook_add(unicorn.UC_HOOK_CODE, code)
        mu.hook_add(unicorn.UC_HOOK_MEM_WRITE, wr)
        mu.emu_start(0x95E60, 0, count=200000)
        return writes

    # unicorn raises and handles Windows SEH access violations internally; keep
    # faulthandler from printing them as "fatal" while it runs
    was = faulthandler.is_enabled()
    faulthandler.disable()
    try:
        assert run("_WKST(F10,eq,7);") == [(0x164B1D0 + 20, 7)]
        assert run("_WKST(G010,eq,7);") == [(0x164C620 + 20, 7)]   # decimal, leading zeros ok
        assert run("_WKST(F1200,eq,7);") == [(0x164BB30, 7)]       # = Z[0]: no bounds check
        assert run("_WKST(Z10,eq,7);") == []
    finally:
        if was and sys.__stderr__ is not None:
            faulthandler.enable(file=sys.__stderr__)


@needs_game
def test_shaders(eb, ix) -> None:  # type: ignore[no-untyped-def]
    from eboot.shaders import blob_code, find_blobs, find_classes, gcn_classes

    blobs = find_blobs(eb)
    assert len(blobs) == 67
    assert Counter(b.stage for b in blobs) == {"VS": 32, "PS": 35}
    assert sum(b.size for b in blobs) == 49104 and blobs[-1].va + blobs[-1].size == 0xDD66B0
    for b in blobs:                                   # contiguous; header +0x10 = footer - 8
        hdr = eb.read_va(b.va, 0x24)
        assert b.orbshdr_off is not None
        assert struct.unpack_from("<I", hdr, 0x10)[0] == b.orbshdr_off - 8
        assert hdr[0x0C] == (1 if b.stage == "PS" else 0) and hdr[0x0D] == 1   # stage flag is +0x0C
        assert eb.read_va(b.va + 0x24, 4) == b"Shdr"                          # 0x24-byte header
        assert b.names[-1] == ("S_POSITION" if b.stage == "VS" else "S_TARGET_OUTPUT")
    assert all(a.va + a.size == b.va for a, b in zip(blobs, blobs[1:]))
    classes = find_classes(eb, ix, blobs)
    assert len(classes) == 38
    owned = {c.vs for c in classes} | {c.ps for c in classes}
    assert owned == set(range(67))
    by_key = {c.key: c for c in classes}
    for b in blobs:
        cl = gcn_classes(blob_code(eb, b))
        assert cl[-1] == "s_endpgm" and "?" not in cl, b.index
    blend = ["add", "addalpha", "darken", "dodge", "lighten", "multiplicative", "opaque",
             "screen", "sub"] + ["ps_" + k for k in ("add", "burn", "darken", "diff", "diff5",
                                                    "dodge", "dodge5", "exclusion", "hardlight",
                                                    "lighten", "multi", "overlay", "screen",
                                                    "softlight", "sub")]
    for k in blend:                                   # every blend PS reads source + backdrop
        ps = blobs[by_key[k].ps]  # type: ignore[index]
        cl = gcn_classes(blob_code(eb, ps))
        assert sum(x.startswith("image_sample") for x in cl) == 2, k
        assert "dst_texture" in ps.names and "CommonFragmentParamsConstants" in ps.names, k
    assert {"y_texture", "cbcr_texture"} <= set(blobs[by_key["basic YCbCr"].ps].names)  # type: ignore[index]
    # transition PS: only 'transition 2D' samples the rule mask; '2DTex' samples `texture`
    t2d = blobs[by_key["transition 2D"].ps]           # type: ignore[index]
    t2dt = blobs[by_key["transition 2DTex"].ps]       # type: ignore[index]
    assert "transition_tex" in t2d.names and "transition_tex" not in t2dt.names
    assert "texture" in t2dt.names and "TextureSizeConstants" not in t2d.names + t2dt.names
    # draw code looks shaders up by crc32(key); e.g. 'clear' = 0xE5B1F106 at VA 0xF42F9
    assert zlib.crc32(b"clear") == 0xE5B1F106
    assert struct.unpack("<I", eb.read_va(0xF42FA, 4))[0] == 0xE5B1F106


@needs_game
def test_screen_effect_tables(eb) -> None:  # type: ignore[no-untyped-def]
    # mode byte / strength float are indexed [id-1] (VA 0xD2D92 dec eax; cmp eax, 0x32);
    # the dword table is indexed [id] (VA 0xD2D74..0xD2D81)
    modes = list(eb.read_va(0x2D5978, 51))
    assert modes[:25] == [1, 2, 0] + list(range(4, 26))
    assert modes[25:] == [0, 1, 2, 0] + list(range(4, 26))
    strength = struct.unpack("<51f", eb.read_va(0x2D59AC, 51 * 4))
    assert strength[:26] == (0.0,) * 26 and strength[28] == 0.0
    assert all(v == 1.0 for i, v in enumerate(strength[26:], 26) if i != 28)
    dw = struct.unpack("<52i", eb.read_va(0x2D55E0, 52 * 4))
    assert [i for i, v in enumerate(dw) if v != 1] == [26, 29] and dw[26] == dw[29] == 6


@needs_game
def test_pointer_clamp(eb) -> None:  # type: ignore[no-untyped-def]
    from eboot.disasm import disasm_range

    ops = [(i.mnem, i.ops) for i in disasm_range(eb, 0xCF5D6, 0xCF611)]
    assert ("mov", "edx, 0x27f") in ops and ("cmp", "eax, 0x280") in ops      # x: 0..639
    assert ("mov", "esi, 0x1bf") in ops and ("cmp", "edx, 0x1c0") in ops      # y: 0..447


@needs_game
def test_ui_text_table(eb) -> None:  # type: ignore[no-untyped-def]
    from eboot.disasm import disasm_range
    from eboot.uitext import LANGS, UI_COUNT, read_table

    getter = [(i.mnem, i.ops) for i in disasm_range(eb, 0xC29B0, 0xC29E2)]
    assert ("cmp", "eax, 0x5d9") in getter and ("imul", "eax, eax, 0x5db") in getter
    assert ("cmp", "eax, 0x1d47") in getter
    tab = read_table(eb)
    assert list(tab) == list(LANGS) and all(len(v) == UI_COUNT == 1499 for v in tab.values())
    assert [tab[k][0].text for k in LANGS] == ["NULL", "NULL", "NULL ", "NULL ", "NULL "]
    en = [u.text for u in tab["en"]]
    assert en[1] == "Change audio settings." and "Open the log." in en
    assert tab["ja"][1].text.startswith("サウンドの設定") and tab["zt"][1].text.startswith("設定遊戲聲音")
    # strings with U+3000 (dropped by an earlier heuristic export) are present
    ja = [u.text for u in tab["ja"]]
    assert ja[888] == "教えて！シエル先生\u3000その１" and sum("\u3000" in s for s in ja) == 63
    assert tab["ko"][1].text == "한글 system message 1"
    assert all(len({u.va for u in v}) == UI_COUNT for v in tab.values())


@needs_game
def test_embedded_name_tables_match_allpac2(eb) -> None:  # type: ignore[no-untyped-def]
    from tsuki import image_dir

    img = image_dir()
    assert img is not None
    raw = (img / "allpac2.nam").read_bytes()
    nam = [raw[i:i + 32].split(b"\0")[0].split(b".")[0].decode() for i in range(0, 14744 * 32, 32)]
    emb = [eb.read_va(0x7F6800 + 32 * i, 32).split(b"\0")[0].decode() for i in range(14744)]
    assert emb == nam                       # base names; allpac2.nam carries the extension


@needs_game
def test_image_table_fields(eb) -> None:  # type: ignore[no-untyped-def]
    from eboot.disasm import disasm_range

    assert ("cmp", "edi, 0x39b6") in [(i.mnem, i.ops) for i in disasm_range(eb, 0x41BA0, 0x41BBC)]
    base = 0x671900
    raw = eb.read_va(base, 14744 * 0x48)
    for k in range(14744):                  # +0x2C = size in the 640x362 canvas
        a, b, w, h = struct.unpack_from("<4H", raw, k * 0x48 + 0x2C)
        assert (a, b) == (w // 3, h * 362 // 1080), k
    rec = raw[9958 * 0x48:9959 * 0x48]
    file_ptr, overlay_ptr = struct.unpack_from("<QQ", rec, 0x08)
    assert eb.cstr(file_ptr) == b"AKI_A_00_00_N00_B.MZP" and eb.cstr(overlay_ptr) == b"AKI_A_00_00_N01.MZP"
    assert struct.unpack_from("<4H", raw, 0x2C) == (640, 362, 1920, 1080)     # BLACK


@needs_game
def test_real_save_if_present() -> None:
    p: Optional[Path] = savedata.default_save_path()
    if p is None:
        pytest.skip("no savedata000 on this machine")
    img = savedata.parse(p.read_bytes())
    assert not img.issues, img.issues
