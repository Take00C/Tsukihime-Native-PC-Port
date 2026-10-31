"""Tests for tsuki.misc_audio_runtime_semantics (sound playback semantics).

The model tests run without game data; the engine tests execute the user's own eboot.bin
under unicorn (tsuki.misc_audio_runtime_semantics_emu) and compare it with the model, and
skip cleanly when the game folder (TSUKI_GAME_DIR) or unicorn is missing.
"""
from __future__ import annotations

import random
import struct
from typing import Iterator, Tuple

import pytest

from tsuki import game_dir, image_dir
from tsuki.misc_audio_runtime_semantics import (CHAR_MASK, VOICE_GROUP_CHAR, Config, Defer, SoundModel,
                                                char_slot, find_tag, group_slot, parse_tags,
                                                system_se_vags, voice_char)


def _tags(stmt: str) -> Tuple[list, int]:
    b = stmt.encode()
    return parse_tags(b, b.index(b"_") + 5)


# ---------------------------------------------------------------------------
# pure model
# ---------------------------------------------------------------------------
def test_tag_bytes_and_positions() -> None:
    tags, nt = _tags("_SEPL(19,0,FATE_SE142,,`001:200,,`017:30,,`068:100);")
    assert nt == 9
    assert [(i, t) for i, t in enumerate(tags[:nt]) if t != 0xFF] == [(4, 1), (6, 17), (8, 68)]
    assert find_tag(tags, nt, 68) == 8 and find_tag(tags, nt, 18) == -1
    tags, nt = _tags("_MPLY(8,,`001:3000,,`017:100,,`069:502272);")
    assert nt == 7 and tags[6] == 69
    tags, nt = _tags("_MPLY(5);")
    assert nt == 0                                   # no tag: handlers use their defaults


def test_tag_defaults_in_model() -> None:
    m = SoundModel()
    m.default_playing = False
    m.run("_MPLY(5);")                               # no tags: volume 100, loop 1, no fade
    assert m.scr_bgm_vol == 100 and m.bgm_loop == [1, 1]
    r = m.bgm[m.slot]
    assert (r.state, r.idx, float(r.vol), float(r.vol_to)) == (1, 5, 100.0, 100.0)
    m.run("_MVOL(,,`001:1000);")                     # `017 absent -> target 0
    assert float(m.bgm[m.slot].vol_to) == 0.0 and m.scr_bgm_vol == 0
    m.se_lookup = lambda name, flags: (2, 1)
    m.run("_SEPL(4,0,FATE_SE001,,`001:0);")           # `017 absent -> volume 100
    assert float(m.se[4].vol) == 100.0 and m.scr_se_vol[4] == 100
    m.run("_SEPL(5,0,FATE_SE001,,`017:50,`068:500);")  # `068: deferred 30 frames
    assert m.defer[5].timer == 30 and m.defer[5].kind == 1 and m.se[5].state == 0
    for _ in range(29):
        m.update(1)
    assert m.se[5].state == 0
    m.update(1)
    assert m.se[5].state == 1 and float(m.se[5].vol) == 50.0
    assert m.defer[5].pack() == Defer.cleared().pack()


def test_bgm_same_track_and_negative_model() -> None:
    m = SoundModel()
    m.default_playing = False
    m.run("_MPLY(5,,`017:80);")
    n = len(m.calls)
    m.run("_MPLY(5,,`017:40);")                      # same track: ignored (only the script copy)
    assert len(m.calls) == n and float(m.bgm[m.slot].vol) == 80.0 and m.scr_bgm_vol == 40
    m.run("_MPLY(-1);")                              # negative: stop now, track -1
    assert m.calls[-2:] == [("stop", m.slot, 0), ("close", m.slot)] and m.track == -1


def test_gain_truncation_model() -> None:
    m = SoundModel()                                 # BGM config 40
    m.default_playing = False
    m.run("_MPLY(7,,`017:33);")
    play = [c for c in m.calls if c[0] == "play"][0]
    # trunc(40 * 33 * 0.01) = 13 -> 0.13, not 40 * 33 * 0.0001 = 0.132
    assert play[5] == struct.unpack("<I", struct.pack("<f", 0.13))[0]


def test_vply_three_arguments_model() -> None:
    m = SoundModel()
    m.run("_VPLY(A10_07_00_0147_1,01b40,0);")
    assert (m.voice_pending, m.voice_char, m.voice_arg3) == (0x1B40, 2, 0)
    m.run("_VPLY(A00_06_00_0029_0,00726);")
    assert (m.voice_pending, m.voice_char, m.voice_arg3) == (0x726, 1, 0)


def test_voice_group_table() -> None:
    assert char_slot(voice_char("A00_01")) == 0 and group_slot("B90") == 0     # Shiki / young Shiki
    assert [group_slot(g) for g in ("A10", "A11", "A12")] == [1, 1, 1]
    assert [group_slot(g) for g in ("A20", "A21", "A22", "A23", "A24")] == [2] * 5
    assert {g: group_slot(g) for g in ("B00", "B10", "B20", "B30", "B40", "B50", "B60", "B70", "B80")} == \
        {"B00": 9, "B10": 16, "B20": 4, "B30": 5, "B40": 3, "B50": 7, "B60": 12, "B70": 15, "B80": 17}
    assert {g: group_slot(g) for g in ("C00", "C10", "C20", "C30", "C40", "C70")} == \
        {"C00": 10, "C10": 13, "C20": 14, "C30": 11, "C40": 6, "C70": 8}
    for g in ("C50", "C60", "C80", "D00", "D93", "M00", "M05", "M12", "DU"):
        assert group_slot(g) == 18                                           # catch-all slot
    assert sorted({char_slot(c) for c in range(24)}) == list(range(19))
    assert all(bin(m).count("1") == 1 for m in CHAR_MASK)


def test_voice_groups_cover_voice_nam() -> None:
    img = image_dir()
    if img is None:
        pytest.skip("game data not found")
    from tsuki.nam import parse_nam8
    groups = sorted(set(parse_nam8((img / "voice.nam").read_bytes()).names))
    assert len(groups) == 54
    explicit = {g for g in groups if g[:3] in VOICE_GROUP_CHAR}
    default = [g for g in groups if g[:3] not in VOICE_GROUP_CHAR]
    assert len(explicit) + len(default) == 54
    assert all(voice_char(g) == 0 for g in default)                         # D*, M04..M10, DU


def test_system_se_table() -> None:
    assert [system_se_vags(i) for i in range(10)] == [
        (3, 4), (5, 6), (1, 2), (9, 10), (11, 12), (13, 14), (5, 6), (7, 8), (15, 16), (17, 18)]
    assert all(system_se_vags(i) == (0, -1) for i in range(10, 28))


def test_char_gate_in_frame_update() -> None:
    m = SoundModel(Config(char_vol=[50] * 19, char_enable=0x3FFFFFFF & ~(1 << 1)))
    m.default_playing = False
    m.se_lookup = lambda name, flags: (2, 775)
    m.run("_SEPL(6,0,SEV_A10_14_01A_0009_0,,`017:100,,`070:2);")   # `070:2 = config slot 1 (off)
    assert m.se[6].char == 2
    m.calls = []
    m.update(1)
    assert ("set_volume", 12, 0, 0) in m.calls


# ---------------------------------------------------------------------------
# engine (unicorn) vs model
# ---------------------------------------------------------------------------
@pytest.fixture(scope="module")
def pair() -> Iterator[object]:
    gd = game_dir()
    if gd is None or not (gd / "eboot.bin").is_file():
        pytest.skip("game data not found")
    pytest.importorskip("unicorn")
    pytest.importorskip("capstone")
    from tsuki.misc_audio_runtime_semantics_verify import _setup
    p, _ = _setup()
    yield p


def _run(pair: object, items: list) -> list:
    pair.reset()                                     # type: ignore[attr-defined]
    bad = []
    for it in items:
        mm, _, _ = pair.step(it)                     # type: ignore[attr-defined]
        if mm:
            bad.append((it, mm[:3]))
    return bad


def test_engine_mply_same_and_negative(pair: object) -> None:
    items = ["_MPLY(5,,`017:80);", "frame1", "_MPLY(5,,`017:40);", "frame3", "_MPLY(-1);", "frame1",
             "_MPLY(7,,`017:60,`001:2000);", "frame10", "_MPLY(9,,`001:500,`011:3000,`017:70);", "frame40",
             "_MVOL(,,`001:2000,,`017:20);", "frame30", "_MFAD(,,`001:3000,,`011:0);", "frame200"]
    assert _run(pair, items) == []
    e = pair.emu                                     # type: ignore[attr-defined]
    pair.reset()                                     # type: ignore[attr-defined]
    _, calls, _ = pair.step("_MPLY(5,,`017:80);")     # type: ignore[attr-defined]
    _, calls2, _ = pair.step("_MPLY(5,,`017:80);")    # type: ignore[attr-defined]
    assert calls2 == []                              # same track: nothing reaches the library
    _, calls3, _ = pair.step("_MPLY(-3);")            # type: ignore[attr-defined]
    assert ("stop", e.u32(0xDD8F28), 0) in calls3


def test_engine_gain_truncation(pair: object) -> None:
    pair.reset()                                     # type: ignore[attr-defined]
    _, calls, _ = pair.step("_MPLY(7,,`017:33);")     # type: ignore[attr-defined]
    play = [c for c in calls if c[0] == "play"][0]
    assert play[5] == struct.unpack("<I", struct.pack("<f", 0.13))[0]


def test_engine_vply_three_arguments(pair: object) -> None:
    e = pair.emu                                     # type: ignore[attr-defined]
    pair.reset()                                     # type: ignore[attr-defined]
    r = e.run("_VPLY(A10_07_00_0147_1,01b40,0);")
    assert r.err is None
    assert (e.i32(0x16541FC), e.u16(0x1654208), e.u16(0x165420A)) == (0x1B40, 2, 0)
    r = e.run("_VPLY(A10_07_00_0147_1,01b40,7);")
    assert e.u16(0x165420A) == 7


def test_engine_voice_group_chars(pair: object) -> None:
    e = pair.emu                                     # type: ignore[attr-defined]
    for g in ("A00", "A12", "A24", "B00", "B90", "C40", "C70", "C80", "C90", "D61", "M02", "M07"):
        pair.reset()                                 # type: ignore[attr-defined]
        e.run(f"_VPLY({g}_01_00_0000_0,00002);")
        assert e.u16(0x1654208) == voice_char(g), g


def test_engine_se_commands(pair: object) -> None:
    items = ["_SEPL(3,0,FATE_SE001,,`017:80);", "_SEPL(4,1,FATE_SE001,,`017:50,`001:1000,`018:96);",
             "frame1", "_SEPL(4,1,FATE_SE001,,`017:50);", "_SEPL(5,0,FATE_SE001,,`017:80,`068:500);",
             "frame31", "_SEVL(4,,,,`001:500,,`017:20);", "frame5", "_SEFD(-1,,,`001:300);", "frame30",
             "_SEPL(6,1,FATE_SE003,,`017:80,`001:300,`018:32,`068:100);", "frame6", "frame5",
             "_SEVL(6,,,,`001:500,,`017:20,,`068:200);", "frame12", "_SEFD(6,,,`001:300,,`068:100);",
             "frame6", "_SEPL(7,0,FATE_SE003,,`017:80,`068:100);", "_SEPL(7,0,FATE_SE004,,`017:70,`068:200);",
             "frame12", "_SEPL(5,0,SEV_A10_14_01A_0007_0,,`001:800,,`017:100,,`068:500,,`070:2);",
             "frame40", "_SESP(-1,);", "frame1", "_SEPL(8,1,MOON_SE02067,,`001:800,,`017:30,,`068:100);",
             "_SEFD(-1,,,`001:300,,`068:100);", "frame10", "_SEPL(9,0,,);", "_SESP(8);", "frame2"]
    assert _run(pair, items) == []


def test_engine_flags_silence_commands(pair: object) -> None:
    pair.reset()                                     # type: ignore[attr-defined]
    for ff, scan, skip in ((1, 0, 0), (0, 1, 0), (0, 0, 1)):
        pair.flags(ff, scan, skip)                   # type: ignore[attr-defined]
        for it in ("_MPLY(12,,`017:80);", "_SEPL(2,0,FATE_SE001,,`017:80);", "_SEPL(3,1,FATE_SE001,,`017:80);",
                   "_SEFD(2,,,`001:300);", "_SEVL(3,,,,`001:500,,`017:20);", "frame3"):
            mm, _, _ = pair.step(it)                 # type: ignore[attr-defined]
            assert mm == [], (ff, scan, skip, it, mm[:2])


def test_engine_real_script_statements(pair: object) -> None:
    """The first 40 audio statements of three scripts, replayed with random frames."""
    from tsuki.misc_audio_runtime_semantics_verify import replay, script_sequence
    rng = random.Random(7)
    done = 0
    for si, seq in script_sequence((17, 120, 300)).items():
        assert seq, si
        pair.reset()                                 # type: ignore[attr-defined]
        steps, bad, msgs = replay(pair, seq[:40], rng)   # type: ignore[arg-type]
        assert bad == 0, msgs
        done += 1
    assert done == 3


def test_engine_voice_start_and_ducking(pair: object) -> None:
    from tsuki.misc_audio_runtime_semantics import normalize_call
    from tsuki.misc_audio_runtime_semantics_emu import run_duck_step, run_voice_start
    e = pair.emu                                     # type: ignore[attr-defined]
    vols = [10 + 3 * k for k in range(19)]
    for char in (0, 1, 2, 4, 9, 13, 21, 23):
        pair.reset()                                 # type: ignore[attr-defined]
        e.put_u16(0x66C578 + 0x36, 1)
        e.put_u32(0x66C578 + 0x3C, 0x2AAAAAAA)
        e.wr(0x66C578 + 0x76, struct.pack("<19h", *vols))
        got, vol, r = run_voice_start(e, 0x726, char)
        m = SoundModel(Config(char_vol=vols, char_enable=0x2AAAAAAA))
        m.vol[0xD] = 100
        m.voice_pending, m.voice_char = 0x726, char
        res = m.voice_start_at_message()
        assert (got, vol) == ((res[0] if res else None), m.vol[0xD]), char
    pair.reset()                                     # type: ignore[attr-defined]
    e.put_u16(0x66C578 + 0x1C, 1)
    m = SoundModel(Config(duck=1))
    m.vol[0xC] = e.u8(0x17DA274)
    for vs in [1] * 40 + [0] * 70:
        r = run_duck_step(e, vs)
        m.calls = []
        m.duck_update(vs != 0)
        assert (e.i32(0x7A6184), e.u8(0x17DA274)) == (m.duck_level, m.vol[0xC])
        assert [normalize_call(c) for c in r.calls] == m.calls
