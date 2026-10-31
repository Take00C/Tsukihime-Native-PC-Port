"""Tests for tsuki.wait_model (docs/formats/wait_model.md).

Unit tests run without the game.  Real-data tests (scripts, text layout, voices, movies) skip
when the game folder is missing; the emulation tests also need unicorn / capstone and run the
eboot's own 0x60710 (with 0xA0B70 and 0xA2630 inside), 0xA2630 / 0xA0B70 / 0xCA070 / 0x5E530
against the model.

Frame order (0x60710): Adv_Update 0xA0B70 first (and the statements it lets run), then
tick_reveal 0xA2630; ``_frame`` below is ``wm.frame``.
"""
from __future__ import annotations

import faulthandler
import importlib.util
import os
import sys
from pathlib import Path
from typing import Iterator

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import wait_model as wm  # noqa: E402
from tsuki.wait_model import (  # noqa: E402
    ACTION_DECIDE, PAD_CANCEL, PAD_OPTIONS, W_CLICK, W_MESSAGE, W_REVEAL, W_SHOWN, W_VOICE,
    AdvMode, Config, FrameInput, MovieRec, WaitState)


def _game() -> Path | None:
    from tsuki import game_dir
    g = game_dir()
    if os.environ.get("TSUKI_GAME_DIR") == "/nonexistent":
        return None
    return g if g is not None and (g / "eboot.bin").exists() else None


needs_game = pytest.mark.skipif(_game() is None, reason="game folder not found")
needs_ffprobe = pytest.mark.skipif(__import__("shutil").which("ffprobe") is None,
                                   reason="ffprobe not on PATH")
needs_emu = pytest.mark.skipif(_game() is None or importlib.util.find_spec("unicorn") is None,
                               reason="game or unicorn missing")


@pytest.fixture
def quiet_faulthandler() -> Iterator[None]:
    """unicorn's mem_map raises an SEH access violation on Windows that it handles itself;
    keep faulthandler from dumping it as 'Windows fatal exception' while the emulator runs."""
    was = faulthandler.is_enabled()
    faulthandler.disable()
    try:
        yield
    finally:
        if was and sys.__stderr__ is not None:
            faulthandler.enable(file=sys.__stderr__)


# ---------------------------------------------------------------------------
# helpers: drive one message through the two per-frame functions
# ---------------------------------------------------------------------------

def _records(n: int) -> list[tuple[int, int]]:
    return [(1, i + 1) for i in range(n)]


def _frame(st: WaitState, recs, inp=None, cfg=None, mode=None, voice=None, script=None):
    """One frame in the eboot order: Adv_Update (+ script), then tick_reveal.  Returns
    (tick_reveal effects, Adv_Update effects)."""
    cfg = cfg or Config()
    mode = mode or AdvMode()
    inp = inp or FrameInput()
    st.adv_key = inp.adv_key
    r = wm.frame(st, inp, cfg, mode,
                 lambda s, b: (1 if wm.reveal_done(recs, s.head, s.tail) else 0) if b == 4 else 1,
                 voice_state=voice, script=script)
    return r.reveal, r.update


def _message(mode: int, voice: bool = False, st: WaitState | None = None) -> WaitState:
    st = st or WaitState(window_target=1, window_alpha=1.0)
    wm.zm_apply(st, mode, voice)
    return st


# ---------------------------------------------------------------------------
# unit tests
# ---------------------------------------------------------------------------

def test_wait_frames() -> None:
    # _WTTM frames = (3 * ms) / 50 truncated, at least 1 (0x9ACF7, 0x9AD98)
    assert [wm.wait_frames(x) for x in (1000, 400, 2200, 1, 0, 33, 34, -5)] == [60, 24, 132, 1, 1, 1, 2, 1]
    st = WaitState()
    wm.wttm_apply(st, 1000, 1)
    assert (st.e08, st.e18) == (60, 1)
    wm.wttm_apply(st, 1000, 0)
    assert (st.e08, st.e18) == (60, 0)
    wm.wttm_apply(st, 500, None)
    assert (st.e08, st.e18) == (30, 0)
    wm.wtsv_apply(st)
    assert (st.e08, st.e18) == (6, 1)


def test_end_modes() -> None:
    em = wm.zm_end_mode
    assert em(")", 0, False) == wm.MODE_CLICK
    assert em(")", 1, False) == wm.MODE_CLICK
    assert em(")", 0, True) == wm.MODE_NOWAIT        # Display End of Page Instantly, not page end
    assert em(")", 1, True) == wm.MODE_CLICK         # last message of the page still waits
    assert em("@n", 0, True) == wm.MODE_CONTINUE
    assert em("@e", 1, False) == wm.MODE_NOWAIT
    assert wm._message_terminator("$000119^@n") == ("$000119^", "@n")
    assert wm._message_terminator("$043350@k@e") == ("$043350@k", "@e")
    assert wm._message_terminator("$000107") == ("$000107", ")")


def test_reveal_speed_and_width() -> None:
    cfg = Config()
    sp = wm.reveal_speed(cfg, 0)
    assert abs(float(sp) - 1000 / (60 * 35)) < 1e-6
    assert abs(float(wm.reveal_width(cfg, 0)) - 100 / 35) < 1e-5
    assert float(wm.reveal_speed(cfg, 1)) == pytest.approx(1000 / (60 * 15), rel=1e-6)
    inst = Config(text_speed_unread=0)
    assert wm.reveal_speed(inst, 0) == wm.SPEED_INSTANT and wm.reveal_width(inst, 0) == 0
    # v > 175: the width is capped by min(0.5, v * 0.1 / 35)
    assert float(wm.reveal_width(Config(text_speed_unread=250), 0)) == pytest.approx(
        1000 / (60 * 250) * 0.5 * 60, rel=1e-6)


def test_reveal_done_rule() -> None:
    recs = _records(10)
    assert not wm.reveal_done(recs, 10.0, 7.2)          # last glyph alpha (10-10)/(10-7.2) = 0
    assert not wm.reveal_done(recs, 12.0, 9.9)          # tail not past the last counter
    assert wm.reveal_done(recs, 12.9, 10.01)            # tail > last counter
    assert wm.reveal_done(recs, 10.0, 10.0)             # head - tail <= 0
    assert not wm.reveal_done(recs, 9.5, 9.5)           # last glyph not reached: alpha 0
    assert wm.reveal_done([], 0.0, 0.0)                 # empty text: block not allocated (-1)
    assert wm.reveal_done(recs + [(0, 0)], 12.9, 10.01)  # control records are ignored


def test_click_wait_message() -> None:
    recs = _records(20)
    st = _message(wm.MODE_CLICK)
    assert st.e00 == W_CLICK | W_MESSAGE | W_REVEAL
    n = 0
    while not st.e00 & W_SHOWN:
        _e1, e2 = _frame(st, recs)
        assert not e2.script_step
        n += 1
    # head = 48 * 0.47619 = 22.857, tail = head - 2.857 = 20: last glyph alpha 1 at frame 48
    assert n == 48
    assert st.e0e == 132                                # auto countdown armed (2200 ms)
    for _ in range(300):                                # without input nothing happens
        _e1, e2 = _frame(st, recs)
        assert not e2.advanced and not e2.script_step
    _e1, e2 = _frame(st, recs, FrameInput.press())
    assert e2.advanced == "click" and e2.script_step and st.e00 == 0 and st.e7c == 1
    assert st.new_page == 1


def test_click_completes_reveal_then_needs_a_new_press() -> None:
    recs = _records(40)
    st = _message(wm.MODE_CLICK)
    _frame(st, recs)
    e1, e2 = _frame(st, recs, FrameInput.press())
    assert e1.reveal_clicked and st.head == float(wm.HEAD_ALL)
    assert not e2.advanced                              # same frame: tail not yet past the end
    e1, e2 = _frame(st, recs, FrameInput.hold())        # still held: no new edge
    assert st.e00 & W_SHOWN and not e2.advanced
    e1, e2 = _frame(st, recs, FrameInput.press())
    assert e2.advanced == "click"


def test_held_button_latch_blocks_reveal_click() -> None:
    recs = _records(30)
    st = _message(wm.MODE_CLICK)
    st.e7c = 1                                          # the previous message was clicked away
    e1, _ = _frame(st, recs, FrameInput.press())        # decide still held -> E7C stays 1
    assert not e1.reveal_clicked and st.e7c == 1
    e1, _ = _frame(st, recs, FrameInput())              # released -> E7C cleared by Adv_Update
    assert st.e7c == 0
    e1, _ = _frame(st, recs, FrameInput.press())
    assert e1.reveal_clicked


def test_frame_order_click_advance_starts_next_message() -> None:
    # frame order 0x60710: the click advance (Adv_Update), Script_Step running the next _ZM and
    # the first reveal step of that message (0xA2630) happen in the same frame; the e7c latch
    # set by the advance keeps the same press from completing the new message at once
    recs = _records(20)
    st = _message(wm.MODE_CLICK)
    while not st.e00 & W_SHOWN:
        _frame(st, recs)
    started: list[int] = []

    def script() -> None:
        wm.zm_apply(st, wm.MODE_CLICK, False)
        started.append(1)

    e1, e2 = _frame(st, recs, FrameInput.press(), script=script)
    assert e2.advanced == "click" and started == [1] and st.e7c == 1
    assert st.e00 & W_REVEAL and not e1.reveal_clicked
    assert st.head == pytest.approx(1000 / (60 * 35), rel=1e-6)    # one reveal step already
    # without the latch (e7c = 0) the same press, still "newly pressed", would complete it
    st2 = _message(wm.MODE_CLICK)
    st2.e7c = 0
    e1b = wm.tick_reveal(st2, FrameInput.press(), Config(), AdvMode())
    assert e1b.reveal_clicked


def test_nowait_advances_the_frame_after_the_reveal() -> None:
    recs = _records(20)
    st = _message(wm.MODE_NOWAIT)
    f = 0
    while True:
        _e1, e2 = _frame(st, recs)
        if st.e00 & W_SHOWN:
            break
        f += 1
    assert f == 47 and not e2.advanced                  # revealed in frame 47 (48th frame)
    _e1, e2 = _frame(st, recs)
    assert e2.advanced == "nowait" and e2.script_step   # frame 48


def test_one_press_at_30hz_only_completes_the_reveal() -> None:
    recs = _records(40)
    st = _message(wm.MODE_CLICK)
    mode = AdvMode(vsync=2)                             # n = 2 reveal iterations per update
    _frame(st, recs, mode=mode)
    e1, e2 = _frame(st, recs, FrameInput.press(), mode=mode)
    # iteration 1: the click sets head = 76800; iteration 2: tail follows -> revealed, but
    # Adv_Update has already run: the press does not dismiss the message
    assert e1.reveal_clicked and st.e00 & W_SHOWN and not e2.advanced
    _e1, e2 = _frame(st, recs, FrameInput.hold(), mode=mode)
    assert not e2.advanced                              # held, not newly pressed
    _e1, e2 = _frame(st, recs, FrameInput.press(), mode=mode)
    assert e2.advanced == "click"


def test_read_only_rule_after_update() -> None:
    # skipping onto an unread line: after Adv_Update skip is cleared and 0xA2630 runs with
    # n = vsync but the step global still 64 (fast path: 60 x speed per iteration)
    st = WaitState(window_target=1, window_alpha=1.0)
    read = {"v": True}

    def script() -> None:
        wm.zm_apply(st, wm.MODE_CLICK, False, read=False)
        read["v"] = False

    st.e00 = 0
    r = wm.frame(st, FrameInput(), Config(ff_read_only=1), AdvMode.skip(),
                 lambda s, b: 0, script=script, line_read=lambda: read["v"])
    assert r.skip_stopped and r.update.script_step
    assert st.head == pytest.approx(60 * 1000 / (60 * 35), rel=1e-6)


def test_nowait_and_continue_modes() -> None:
    recs = _records(10)
    for mode in (wm.MODE_NOWAIT, wm.MODE_CONTINUE):
        st = _message(mode)
        assert not st.e00 & W_CLICK
        adv = None
        for _ in range(100):
            _e1, e2 = _frame(st, recs)
            if e2.advanced:
                adv = e2.advanced
                break
        assert adv == "nowait" and st.e00 == 0 and st.e0e == 0


def test_nowait_waits_for_voice() -> None:
    recs = _records(5)
    st = _message(wm.MODE_NOWAIT, voice=True)
    assert st.e00 & W_VOICE
    st.voice_state = 2
    for _ in range(60):
        _e1, e2 = _frame(st, recs)
        assert not e2.advanced
    st.voice_state = 0
    _e1, e2 = _frame(st, recs)
    assert e2.advanced == "nowait" and e2.script_step
    # @n never sets the voice bit: the voice keeps playing into the appended _MSAD text
    st = _message(wm.MODE_CONTINUE, voice=True)
    assert not st.e00 & W_VOICE


def test_auto_mode() -> None:
    recs = _records(10)
    st = _message(wm.MODE_CLICK, voice=True)
    st.voice_state = 2
    mode = AdvMode.auto()
    frames = 0
    while not st.e00 & W_SHOWN:
        _frame(st, recs, mode=mode)
        frames += 1
    assert st.e0e == 132
    for _ in range(131):
        _e1, e2 = _frame(st, recs, mode=mode)
        assert not e2.advanced
    assert st.e0e == 1
    _e1, e2 = _frame(st, recs, mode=mode)
    assert st.e0e == 0 and not e2.advanced              # Adv_Update ran while e0e was still 1
    _e1, e2 = _frame(st, recs, mode=mode)
    assert not e2.advanced                              # countdown over, but the voice plays
    st.voice_state = 0
    _e1, e2 = _frame(st, recs, mode=mode)
    assert e2.advanced == "auto"
    # without a voice: revealed in frame r (countdown 132 armed at the end of 0xA2630), the
    # countdown reaches 0 in the 0xA2630 of frame r + 132, Adv_Update advances in r + 133
    st = _message(wm.MODE_CLICK)
    f = 0
    while not st.e00 & W_SHOWN:
        _frame(st, recs, mode=mode)
        f += 1
    r = f - 1
    while True:
        _e1, e2 = _frame(st, recs, mode=mode)
        if e2.advanced:
            break
        f += 1
    assert e2.advanced == "auto" and f - r == 133
    # last message of the page: Auto-Mode Advance Page Speed (3000 ms)
    st = _message(wm.MODE_CLICK)
    st.f799 = 1
    while not st.e00 & W_SHOWN:
        _frame(st, recs, mode=mode)
    assert st.e0e == 180


def test_skip_mode() -> None:
    recs = _records(80)
    st = _message(wm.MODE_CLICK, voice=True)
    st.voice_state = 2
    mode = AdvMode.skip()
    e1, e2 = _frame(st, recs, mode=mode)                # 64 reveal steps x 60: done at once
    assert st.e00 & W_SHOWN and st.e0e == 0 and not e2.advanced
    # next frame: the plain-voice block (0xA0FE0) sees skip, stops the voice and clears bit 4,
    # then the message wait advances and the script runs
    e1, e2 = _frame(st, recs, mode=mode)
    assert e2.advanced == "skip" and e2.stop_voice == 1 and st.e00 == 0 and e2.script_step
    # timers run 64 times faster
    st = WaitState()
    wm.wttm_apply(st, 1000, 1)
    _frame(st, recs, mode=mode)
    assert st.e08 == 0


def test_wttm_second_argument() -> None:
    recs = _records(1)
    st = WaitState()
    wm.wttm_apply(st, 1000, 0)
    e1, e2 = _frame(st, recs, FrameInput.press())
    # 0xA2630 cancels the timer after Adv_Update has tested it: the script runs next frame
    assert e1.timer_clicked and st.e08 == 0 and not e2.script_step
    e1, e2 = _frame(st, recs)
    assert e2.script_step
    st = WaitState()
    wm.wttm_apply(st, 1000, 1)
    e1, e2 = _frame(st, recs, FrameInput.press())
    assert not e1.timer_clicked and st.e08 == 59 and not e2.script_step
    st = WaitState(e7c=1)                               # button still held from the last click
    wm.wttm_apply(st, 1000, 0)
    e1, _ = _frame(st, recs, FrameInput.press())
    assert not e1.timer_clicked
    st = WaitState()                                    # the mouse wheel does not end a timer
    wm.wttm_apply(st, 1000, 0)
    e1, _ = _frame(st, recs, FrameInput(wheel=-1))
    assert not e1.timer_clicked


def test_vcwt_and_voice_stop() -> None:
    recs = _records(1)
    st = WaitState(voice_state=0)
    assert not wm.vcwt_apply(st)
    st.voice_state = 2
    assert wm.vcwt_apply(st) and st.e00 == W_VOICE
    _e1, e2 = _frame(st, recs)
    assert st.e00 == W_VOICE and not e2.script_step
    _e1, e2 = _frame(st, recs, FrameInput.press())      # a click ends the wait, stops the voice
    assert e2.stop_voice == 1 and st.e00 == 0 and e2.script_step
    st = WaitState(voice_state=2, f1123=1)              # F1123 keeps the voice
    wm.vcwt_apply(st)
    _e1, e2 = _frame(st, recs, FrameInput.press())
    assert e2.stop_voice == 0 and st.e00 == 0


def test_restored_page_and_bare_message() -> None:
    recs = _records(1)
    st = WaitState(e00=W_CLICK | W_MESSAGE)
    _e1, e2 = _frame(st, recs)
    assert st.e00 == W_CLICK | W_MESSAGE
    _e1, e2 = _frame(st, recs, FrameInput.press())
    assert e2.advanced == "restored" and st.e00 == 0
    st = WaitState(e00=W_MESSAGE)
    _e1, e2 = _frame(st, recs)
    assert st.e00 == 0 and st.new_page == 1 and e2.script_step


def test_movie_start_se_and_stcf_masks() -> None:
    # 0x5E6EE `bt; jae 0x5E968`: the start SE 0x5E490 plays when the id's bit is CLEAR
    assert not any(wm.movie_start_se(i) for i in (*range(8), 13, *range(35, 46)))
    assert all(wm.movie_start_se(i) for i in (8, 12, 14, 34, 46, 60))
    assert wm.MOVIE_START_HOOK == frozenset((*range(8, 13), *range(14, 35)))
    assert wm.movie_se_calls(MovieRec(movie_id=20, phase=1)) == 1
    assert wm.movie_se_calls(MovieRec(movie_id=4, phase=1)) == 0
    assert wm.movie_se_calls(MovieRec(movie_id=20, phase=1), ready=False) == 0
    assert wm.movie_se_calls(MovieRec(movie_id=13, phase=3, frame=209)) == 1
    r, _ = wm.movie_update(MovieRec(movie_id=20, phase=1, frame=50, alpha=0.7), FrameInput(), True, 0)
    assert (r.phase, r.frame) == (2, 0) and r.alpha == pytest.approx(1 / 48, rel=1e-6)
    # _STCF mask 0x20 (0x4ABDC..0x4ABF6): pending while 0xBA910 != 0 AND 0xBA890 == 0
    assert wm.stcf_pending(0x20, {"ba910": 1, "ba890": 0})
    assert not wm.stcf_pending(0x20, {"ba910": 1, "ba890": 1})
    assert not wm.stcf_pending(0x20, {"ba910": 0, "ba890": 0})
    assert wm.stcf_pending(8, {"w98": 100}) and not wm.stcf_pending(8, {"w98": 0})


def test_movie_skip_gate() -> None:
    assert not wm.movie_skippable(4, 0)                 # prologue: only after it was seen once
    assert wm.movie_skippable(4, 1 << 2)
    assert wm.movie_skippable(8, 0) and wm.movie_skippable(39, 0)
    rec = MovieRec(movie_id=39, phase=5, frame=100)
    r, act = wm.movie_update(rec, FrameInput.press(ACTION_DECIDE), True, 0)
    assert r.phase == 5 and act == ""                   # decide does not skip
    r, act = wm.movie_update(rec, FrameInput.press(PAD_CANCEL), True, 0)
    assert r.phase == 7 and r.frame == 618
    n = 0
    while act != "stop":
        r, act = wm.movie_update(r, FrameInput(), True, 0)
        n += 1
    assert n == 61 and r.phase == 6                     # 60 x float32(1/60) leaves alpha > 0
    r, act = wm.movie_update(r, FrameInput(), False, 0)
    assert act == "end"
    rec = MovieRec(movie_id=4, phase=3, frame=1000)
    r, _ = wm.movie_update(rec, FrameInput.press(PAD_OPTIONS), True, 0)
    assert r.phase == 3 and r.skip == 0                 # not seen: ignored
    r, _ = wm.movie_update(rec, FrameInput.press(PAD_OPTIONS), True, 1 << 2)
    assert r.phase == 7                                 # seen: phase 3 -> 4 -> 5 -> skip


def test_dead_state_and_census_keys() -> None:
    assert wm.dead_state_ok(WaitState(e00=wm.LIVE_BITS))
    assert not wm.dead_state_ok(WaitState(e00=0x1000))
    assert not wm.dead_state_ok(WaitState(scene_jump=1))


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@needs_game
def test_plan_00_00() -> None:
    from tsuki.script import AllScr
    with AllScr() as scr:
        plan = wm.wait_plan(scr.commands(scr.index("00_00")))
    msgs = [op for op in plan if op.kind == "message"]
    assert len(msgs) == 394 and sum(op.msad for op in msgs) == 7
    assert sorted({(t, sum(op.terminator == t for op in msgs)) for t in (")", "@n", "@e")}) == [
        (")", 386), ("@e", 1), ("@n", 7)]
    first = msgs[0]
    assert (first.index, first.raw_arg, first.f799, first.mode) == (89, "$000107", 1, wm.MODE_CLICK)
    cont = next(op for op in msgs if op.terminator == "@n")
    assert (cont.index, cont.raw_arg) == (319, "$000119^")
    nxt = msgs[msgs.index(cont) + 1]
    assert nxt.msad and nxt.raw_arg == "$000120"
    wttm = [op for op in plan if op.kind == "wttm"]
    assert (wttm[0].ms, wttm[0].second, wttm[1].ms, wttm[1].second) == (1, 1, 1000, 0)


@needs_game
def test_timeline_00_00_click_and_auto() -> None:
    from tsuki.script import AllScr
    with AllScr() as scr:
        plan = wm.wait_plan(scr.commands(scr.index("00_00")))
    env = wm.real_env("en")
    sim = wm.Simulator(plan, env=env, policy="click")
    sim.run(stop_index=140)
    m = sim.messages
    assert [x["index"] for x in m[:3]] == [89, 125, 130]
    assert m[0]["start"] == 294                         # 2 + 2 + 1 + 2 + 1 + 60 + 92 + 2 + 132 frames
    # the _ZM frame makes the first reveal step; 16 frames of window opening; 100 more steps
    assert m[0]["revealed"] - m[0]["start"] == 116      # 45 counters + window opening
    assert m[0]["advanced"] == m[0]["revealed"] + 1 and m[0]["how"] == "click"
    sim = wm.Simulator(plan, env=env, policy="auto")
    sim.run(stop_index=140)
    a = sim.messages
    # countdown armed at the end of 0xA2630 in the reveal frame, 0 after `countdown` more
    # 0xA2630 runs, acted on by the next frame's Adv_Update
    assert a[0]["advanced"] - a[0]["revealed"] == 181   # F799 = 1: 3000 ms = 180 frames
    assert a[1]["advanced"] - a[1]["revealed"] == 133   # F799 = 0: 2200 ms = 132 frames


@needs_game
@needs_ffprobe
def test_timeline_00_02_voice_and_movie() -> None:
    from tsuki.script import AllScr
    with AllScr() as scr:
        plan = wm.wait_plan(scr.commands(scr.index("00_02")))
    assert sum(op.kind == "voice_now" for op in plan) == 12
    env = wm.real_env("en")
    sim = wm.Simulator(plan, env=env, policy="auto")
    sim.run(stop_index=560)
    first = sim.messages[0]
    assert first["index"] == 170 and first["voice"] > 0
    # auto waits for the voice when it is longer than reveal + countdown
    assert first["advanced"] >= first["start"] + first["voice"]
    ev = [e for e in sim.events if e.what.startswith("TOMP(39)")]
    assert ev and "skips" in ev[0].what


@needs_emu
def test_emulation_frame_order(quiet_faulthandler: None) -> None:
    # the real 0x60710 calls the real Adv_Update first, then tick_reveal, in every mode
    order = wm.emu_frame_order()
    want = [(wm.VA_ADV_UPDATE, wm.CALL_ADV_UPDATE + 5), (wm.VA_TICK_REVEAL, wm.CALL_TICK_REVEAL + 5)]
    assert order == {"normal": want, "auto": want, "skip": want}


@needs_emu
def test_emulation_frames(quiet_faulthandler: None) -> None:
    # multi-frame scenarios through the eboot's 0x60710 vs wm.frame: every global, every frame
    r = wm.emu_check_frames(seeds=range(4))
    assert r["frames"] == 4 * 160 and r["mismatch_frames"] == 0, r["examples"]
    assert r["advanced"].get("click", 0) > 5 and r["script_step"] > 100


@needs_emu
def test_emulation_tick_and_update(quiet_faulthandler: None) -> None:
    r = wm.emu_check(seeds=range(150))
    assert r["cases"] == 150
    assert r["tick_mismatch"] == 0 and r["update_mismatch"] == 0, r["examples"]
    assert r["script_step"] > 10 and r["advanced"].get("click", 0) > 10


@needs_emu
def test_emulation_reveal_done(quiet_faulthandler: None) -> None:
    r = wm.emu_check_reveal_done(cases=120)
    assert r["mismatch"] == 0, r["examples"]


@needs_emu
def test_emulation_movie_update(quiet_faulthandler: None) -> None:
    r = wm.emu_check_movie(cases=250)
    assert r["mismatch"] == 0, r["examples"]
    assert r["transitions"].get("5->7", 0) > 0 and r["transitions"].get("7->6/stop", 0) > 0
    assert r["transitions"].get("1->2", 0) > 0 and r["transitions"].get("se", 0) > 0
