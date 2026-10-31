"""Tests for tsuki.engine_tables (docs/research/knife_tables.md, package K1b).

Synthetic tests (a hand-assembled x86-64 interpreter program and fake eboot images) always run. Real-data tests
skip without the game folder; the knife cross-check skips without the read-only ref/knife checkout. The digest
file next to the C# tests (tests/Tsuki.Tests.Data/EbootTables/engine_tables_digests.json) is the equality proof
between this module and src/Tsuki.Data/Eboot/Tables: both sides must reproduce it.
"""
from __future__ import annotations

import json
import struct
from pathlib import Path

import pytest

from tsuki import engine_tables as E
from tsuki import game_dir

GAME = game_dir()
needs_game = pytest.mark.skipif(GAME is None or not (GAME / "eboot.bin").exists(), reason="game folder not found")
REPO = Path(__file__).resolve().parents[2]
KNIFE = REPO / "ref" / "knife"
needs_knife = pytest.mark.skipif(not (KNIFE / "knife" / "src").is_dir(), reason="ref/knife checkout not found")
DIGESTS = REPO / "tests" / "Tsuki.Tests.Data" / "EbootTables" / "engine_tables_digests.json"


class FakeImage:
    """An eboot stand-in: a sparse byte map with read_va."""

    def __init__(self, base: int, data: bytes) -> None:
        self.base, self.data = base, data

    def read_va(self, va: int, n: int) -> bytes:
        off = va - self.base
        chunk = self.data[max(off, 0):max(off + n, 0)]
        return chunk.ljust(n, b"\x00")                      # past the end: zero filled like .bss


# ---------------------------------------------------------------------------------------------------------
# TinyX86: hand-assembled programs
# ---------------------------------------------------------------------------------------------------------
def _asm_switch() -> bytes:
    """int f(int id): 0 for id <= 4; a jump table over ids 5..8 -> 11, 22, 33, 0; the rest 0.

        0:  xor eax, eax
        2:  lea ecx, [rdi - 5]
        5:  cmp ecx, 3
        8:  ja  ret                  (+0x1B)
        10: lea rdx, [rip + table]   table at 0x50
        17: movsxd rcx, dword [rdx + rcx*4]
        1b: add rcx, rdx
        1e: jmp rcx
        20: ...
    """
    code = bytearray(0x80)
    code[0:2] = bytes.fromhex("31C0")
    code[2:5] = bytes.fromhex("8D4FFB")
    code[5:8] = bytes.fromhex("83F903")
    ret_at = 0x40
    code[8:10] = bytes([0x77, ret_at - 0x0A])
    code[0x0A:0x11] = bytes.fromhex("488D15") + struct.pack("<i", 0x50 - 0x11)
    code[0x11:0x15] = bytes.fromhex("48630C8A")
    code[0x15:0x18] = bytes.fromhex("4801D1")
    code[0x18:0x1A] = bytes.fromhex("FFE1")
    code[ret_at] = 0xC3
    targets = []
    for k, v in enumerate((11, 22, 33)):
        t = 0x20 + 8 * k
        code[t:t + 5] = bytes([0xB8]) + struct.pack("<I", v)
        code[t + 5] = 0xC3
        targets.append(t)
    targets.append(ret_at)                       # id 8 -> plain return (eax = 0)
    for k, t in enumerate(targets):
        code[0x50 + 4 * k:0x50 + 4 * k + 4] = struct.pack("<i", t - 0x50)
    return bytes(code)


def test_tinyx86_runs_a_jump_table_function() -> None:
    cpu = E.TinyX86(FakeImage(0x1000, _asm_switch()).read_va)
    results = {i: cpu.run(0x1000, {"rdi": i})[0]["rax"] for i in range(0, 12)}
    assert results == {0: 0, 1: 0, 2: 0, 3: 0, 4: 0, 5: 11, 6: 22, 7: 33, 8: 0, 9: 0, 10: 0, 11: 0}


def test_tinyx86_compare_branches_use_signed_and_unsigned_conditions() -> None:
    # cmp edi, 10 ; jg +7 ; mov eax, 1 ; ret ; mov eax, 2 ; ret       (signed greater)
    code = bytes.fromhex("81FF0A000000") + bytes([0x7F, 0x06]) + bytes([0xB8, 1, 0, 0, 0, 0xC3]) + bytes([0xB8, 2, 0, 0, 0, 0xC3])
    # the jg skips B8 01 00 00 00 C3 (6 bytes)
    cpu = E.TinyX86(FakeImage(0, code).read_va)
    assert cpu.run(0, {"rdi": 10})[0]["rax"] == 1
    assert cpu.run(0, {"rdi": 11})[0]["rax"] == 2
    assert cpu.run(0, {"rdi": (-5) & 0xFFFFFFFF})[0]["rax"] == 1          # -5 > 10 is false as a signed compare
    # same bytes with ja (unsigned greater): 0xFFFFFFFB > 10 is true
    code_ja = code[:6] + bytes([0x77]) + code[7:]
    cpu = E.TinyX86(FakeImage(0, code_ja).read_va)
    assert cpu.run(0, {"rdi": (-5) & 0xFFFFFFFF})[0]["rax"] == 2


def test_tinyx86_logs_constant_stores() -> None:
    # lea rax,[rip+0x100] ; mov word [rax+0x8A],0 ; mov qword [rax+0x82],0 ; ret
    code = (bytes.fromhex("488D05") + struct.pack("<i", 0x100 - 7) + bytes.fromhex("66C7808A0000000000")
            + bytes.fromhex("48C7808200000000000000") + b"\xC3")
    _, stores = E.TinyX86(FakeImage(0, code).read_va).run(0)
    assert stores == [(0x100 + 0x8A, 2, 0), (0x100 + 0x82, 8, 0)]


def test_tinyx86_rejects_unknown_instructions() -> None:
    with pytest.raises(E.EngineTableError, match="not supported"):
        E.TinyX86(FakeImage(0, bytes([0xCC, 0xC3])).read_va).run(0)             # int3
    with pytest.raises(E.EngineTableError, match="step limit"):
        E.TinyX86(FakeImage(0, bytes([0xEB, 0xFE])).read_va).run(0, max_steps=50)  # jmp $


# ---------------------------------------------------------------------------------------------------------
# synthetic readers
# ---------------------------------------------------------------------------------------------------------
def test_ps4_sysmes_id_inserts_one_message_at_217() -> None:
    assert [E.ps4_sysmes_id(v) for v in (0, 216, 217, 218, 1497)] == [0, 216, 218, 219, 1498]


def test_read_rects_and_text_colours_on_a_fake_image() -> None:
    data = struct.pack("<8h", 1, 2, 3, 4, -5, 6, 7, 8) + struct.pack("<6H", 0x632C, 0xF800, 0x07E0, 0x001F, 0xFFFF, 0x0000)
    img = FakeImage(0x2000, data)
    assert [r.as_tuple() for r in E.read_rects(img, 0x2000, 2)] == [(1, 2, 3, 4), (-5, 6, 7, 8)]
    E_colours = E.TEXT_COLOURS_VA
    img = FakeImage(E_colours, struct.pack("<6H", 0x632C, 0xF800, 0x07E0, 0x001F, 0xFFFF, 0x0000))
    assert E.read_text_colours(img) == [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255), (0, 0, 0)]


def test_keep_list_reader_stops_at_minus_one_and_rejects_runaway() -> None:
    img = FakeImage(0x3000, struct.pack("<5h", 4, 9, 7, -1, 12))
    assert E._read_terminated_i16(img, 0x3000) == (4, 9, 7)
    with pytest.raises(E.EngineTableError, match="no terminator"):
        E._read_terminated_i16(FakeImage(0x3000, struct.pack("<2000h", *([1] * 2000))), 0x3000)


def test_voice_speaker_prefix_is_stored_reversed() -> None:
    recs = b"".join(k.encode().ljust(8, b"\0") + struct.pack("<ii", v, 0) for k, v in [("ZZZ", 0), ("00A", 1), ("02A", 3)])
    recs += bytes(16 * (E.VOICE_SPEAKER_RECORDS - 3))
    img = FakeImage(E.VOICE_SPEAKERS_VA, recs)
    table = E.read_voice_speakers(img)
    assert table["A00"] == 1 and table["A20"] == 3 and table["ZZZ"] == 0
    assert E.voice_speaker_of(table, "a20_0001") == 3 and E.voice_speaker_of(table, "Q99") == 0


def test_rust_literal_parser_reads_the_knife_forms() -> None:
    p = E._RustLiteral("&[(73, 1), (0x10, -2), [1.5f32, 2.0], \"x\", true, None, Some(3), [7; 3]]")
    assert p.value() == [(73, 1), (16, -2), [1.5, 2.0], "x", True, None, 3, [7, 7, 7]]


# ---------------------------------------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------------------------------------
@pytest.fixture(scope="module")
def eb():
    e = E.open_eboot()
    yield e
    e.close()


@needs_game
def test_census_counts(eb) -> None:
    c = E.census(eb)
    assert c["haze entries"] == 558 and c["scene_node"] == 527 and c["motion loops"] == 2027
    assert c["jump clears"] == 19 and c["keep words/bytes/bits"] == (39, 276, 43)
    assert c["fcln effects / notes"] == (30, 3) and c["voice speakers"] == 36 and c["test SEs"] == 14
    assert c["dialog messages"] == 55 and c["window types"] == 56


@needs_game
def test_haze_table_entries(eb) -> None:
    haze = E.read_haze(eb)
    assert haze[0] == (0.0,) * 10                              # id 0 is the zero filler
    assert haze[1] == (1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 2.0, 1.0, 1.0, 0.0)
    assert E.haze_entry(haze, 557) == haze[557] and E.haze_entry(haze, 558) is None and E.haze_entry(haze, 0) is None
    # the accessor at 0xB3630 is `lea ecx, [rdi - 0x22E]`: ids 1..0x22D index the table
    assert eb.read_va(0xB3630, 6) == bytes.fromhex("8d8fd2fdffff")


@needs_game
def test_motion_loops_equal_the_independent_unicorn_extraction(eb) -> None:
    loops = E.read_motion_loops(eb)
    runs = json.loads((REPO / "samples" / "misc_layer_state_and_motions" / "motion_loops.json").read_text())
    expected = {i: v for a, b, v in runs for i in range(a, b + 1)}
    assert loops == expected and len(loops) == 2027
    assert loops[73] == 1 and loops[1693] == 600 and loops[1761] == 4700 and 74 in loops


@needs_game
def test_jump_clears_and_scene_node(eb) -> None:
    clears = E.read_jump_clears(eb)
    assert (203, 69) in clears and (160, 62) in clears and (10, 54) in clears and len(clears) == 19
    assert E.jump_clear_words(eb, 204) == [] and E.jump_clear_words(eb, 203) == [69]
    assert 62 not in E.jump_clear_words(eb, 161) and 62 in E.jump_clear_words(eb, 160)
    node = E.read_scene_node(eb)
    assert node[:4] == [486, 485, -1, 1] and E.scene_node_of_script_index(node, 0) == -1 and E.scene_node_of_script_index(node, 1) == 486


@needs_game
def test_keep_lists_agree_between_the_two_copies(eb) -> None:
    flow, save = E.read_flow_keep_lists(eb), E.read_flow_keep_lists(eb, saved=True)
    assert flow == save
    assert flow.words[:9] == tuple(range(9)) and 967 in flow.words and 500 in flow.bits and {501, 967} <= set(flow.bytes)
    assert flow.words[-2:] == (110, 1122)


@needs_game
def test_small_game_tables(eb) -> None:
    assert E.read_test_se_names(eb)[0] == "moon_se02009" and E.read_test_se_names(eb)[-1] == "moon_se09005"
    sp = E.read_voice_speakers(eb)
    assert sp["A00"] == 1 and sp["B90"] == 13 and sp["M12"] == 23 and E.voice_speaker_of(sp, "D40_0001") == 0
    assert E.read_speaker_row_bits(eb)[:6] == (262144, 1, 2, 4, 512, 65536)
    assert E.read_voice_row_words(eb)[:7] == (11, 12, 13, 14, 15, 16, 27)
    msgs = E.read_dialog_messages(eb)
    assert msgs[:4] == (179, 178, 180, 199) and msgs[13] == -1 and msgs[23] == 182
    wt = E.read_window_types(eb)
    assert (wt[0].x, wt[0].y) == (0.0, 0.0) and (wt[3].x, wt[3].y) == (525.0, 414.0)
    dx, dy = E.read_window_offsets(eb)
    assert (len(dx), len(dy), dx[1], dy[2]) == (27, 37, 100.0, 100.0)
    assert E.read_soft_clip_radii(eb) == (10.0, 32.0, 40.0, 64.0, 128.0, 256.0)
    assert E.read_text_colours(eb)[0] == (255, 0, 0)


@needs_game
def test_cells_and_config_pages(eb) -> None:
    assert E.read_phase_title_cells(eb, "ja")[1].as_tuple() == (1192, 696, 1760, 776)
    assert E.read_save_title_cells(eb, "en")[1].as_tuple() == (64, 112, 504, 152)
    assert len(E.read_guide_icon_cells(eb)) == 23 and E.read_guide_word_cells(eb, 1)[0].as_tuple() == (10, 10, 57, 38)
    assert E.read_banner_rects(eb)[0].as_tuple() == (8, 72, 424, 128)
    assert E.read_banner_keys(eb)[0] == pytest.approx((0.0, 1.4)) and len(E.read_banner_keys(eb)) == 10
    pages = E.read_config_pages_layout(eb)
    assert [(p.first_item, p.last_item, len(p.rows)) for p in pages] == [(52, 78, 16), (79, 190, 26)]
    first = pages[0].rows[0].controls[0]
    assert (first.item, first.x) == (52, 245.0) and first.y == pytest.approx(100.8)
    assert pages[1].rows[7].controls[0].x == E.CONFIG_UNUSED_POSITION
    keys = E.read_fcln_lines(eb)
    assert (keys[0][0].time_ms, keys[0][0].centre_x, keys[0][1].time_ms) == (0, 640, -1)


@needs_game
def test_digests_equal_the_stored_proof(eb) -> None:
    stored = json.loads(DIGESTS.read_text(encoding="utf-8"))
    assert E.table_digests(eb) == stored


@needs_game
@needs_knife
def test_knife_crosscheck_counts(eb) -> None:
    rows = {r.table: (r.matched, r.total) for r in E.crosscheck(eb, KNIFE)}
    assert rows["MOTION_LOOPS"] == (2027, 2027) and rows["JUMP_CLEARS"] == (19, 19)
    assert rows["haze (exe.rs)"] == (542, 558) and rows["SCENE_NODE"] == (527, 527)
    assert rows["flow ROWS nodes: all 9 (+1 rule)"] == (1286, 1286) and rows["flow EDGES"] == (628, 629)
    assert rows["CG_GROUPS (+ overseas groups)"] == (1972, 1972) and rows["SEEN_ALIAS"] == (2144, 2144)
    assert rows["VOICE_SPEAKERS"] == (55, 55) and rows["DIALOG_SYSMES"] == (24, 24) and rows["FCLN LINES"] == (118, 120)
    assert rows["STEC modes without tint layers"] == (23, 23)
    assert rows["FCLN NOTES (frames, size, step, accel)"] == (3, 3) and rows["stage RADII"] == (6, 6)
    assert rows["line_break_pairs (exe.rs)"] == (0, 1)
