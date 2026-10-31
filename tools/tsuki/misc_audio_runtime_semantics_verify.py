"""Engine-vs-model verification for tsuki.misc_audio_runtime_semantics.

``Pair`` drives the eboot's own code (``SoundEmu``, unicorn) and ``SoundModel`` with the
same statements and frame updates and compares, after every step, the raw sound state:
the 26 channel records (bytes), BGM slot / track / loop flags, the 20 SE loop flags, the
script-side arrays, the 20 deferred-command entries, the music-heard bits and the calls
made to the sound library (normalised, in order).

``corpus_sequences`` yields the audio statements of every script in script order, so the
whole retail corpus can be replayed (``python -m tsuki.misc_audio_runtime_semantics_verify
corpus``; results in scratch/misc_audio_runtime_semantics/).
"""
from __future__ import annotations

import argparse
import json
import random
import struct
import sys
import time
from pathlib import Path
from typing import Dict, Iterator, List, Optional, Sequence, Tuple

from .misc_audio_runtime_semantics import (VA_BGM_LOOP, VA_BGM_SLOT, VA_BGM_TRACK, VA_DEFER, VA_FF,
                                           VA_MUSIC_BITS, VA_SCAN, VA_SCR_BGM_VOL, VA_SCR_SE_CHAR,
                                           VA_SCR_SE_ID, VA_SCR_SE_LOOP, VA_SCR_SE_LOOPID, VA_SCR_SE_PAN,
                                           VA_SCR_SE_VOL, VA_SCR_TRACK, VA_SE_LOOP, VA_SKIP, VA_VOLUME,
                                           VA_VSYNC, Call, SoundModel, normalize_call)
from .misc_audio_runtime_semantics_emu import (BGM_REC, REC_SIZE, SE_REC, VOICE_REC, SoundCall, SoundEmu)

AUDIO_CMDS = ("MPLY", "MVOL", "MFAD", "SEPL", "SEFI", "SEFD", "SEVL", "SESP")


class _PlayEmu(SoundEmu):
    """SoundEmu whose is_playing answers follow the same rule as the model: a stream plays
    after play() and stops after stop(s, 0)."""

    def answer(self, c: SoundCall) -> int:
        if c.method == 0x70 and (c.rdx & 0xFFFFFFFF) == 0:
            self.playing[c.stream] = False
        return super().answer(c)


class _PlayModel(SoundModel):
    def _call(self, *c: object) -> None:
        super()._call(*c)
        if c[0] == "stop" and c[2] == 0:
            self.playing[int(c[1])] = False         # type: ignore[call-overload]


def engine_state(emu: SoundEmu) -> Dict[str, object]:
    st: Dict[str, object] = {}
    for s in range(26):
        base = BGM_REC + REC_SIZE * s if s < 2 else (VOICE_REC + REC_SIZE * (s - 2) if s < 6
                                                      else SE_REC + REC_SIZE * (s - 6))
        st[f"rec{s}"] = emu.rd(base, REC_SIZE).hex()
    st["slot"] = emu.u32(VA_BGM_SLOT)
    st["track"] = emu.i32(VA_BGM_TRACK)
    st["bgm_loop"] = list(struct.unpack("<2i", emu.rd(VA_BGM_LOOP, 8)))
    st["se_loop"] = list(struct.unpack("<20i", emu.rd(VA_SE_LOOP, 80)))
    st["scr_track"] = emu.i16(VA_SCR_TRACK)
    st["scr_bgm_vol"] = emu.i16(VA_SCR_BGM_VOL)
    for k, va in (("scr_se_id", VA_SCR_SE_ID), ("scr_se_loopid", VA_SCR_SE_LOOPID),
                  ("scr_se_vol", VA_SCR_SE_VOL), ("scr_se_loop", VA_SCR_SE_LOOP),
                  ("scr_se_pan", VA_SCR_SE_PAN)):
        st[k] = list(struct.unpack("<20h", emu.rd(va, 40)))
    st["scr_se_char"] = list(emu.rd(VA_SCR_SE_CHAR, 20))
    for ch in range(20):
        st[f"defer{ch}"] = emu.rd(VA_DEFER + 0x18 * ch, 0x16).hex()
    st["music"] = emu.rd(VA_MUSIC_BITS + 400, 0x41).hex()
    st["vol"] = emu.rd(VA_VOLUME + 0xB, 5).hex()
    return st


def model_state(m: SoundModel) -> Dict[str, object]:
    st: Dict[str, object] = {}
    for s, r in enumerate(m.bgm + m.voice + m.se):
        st[f"rec{s}"] = r.pack().hex()
    st["slot"] = m.slot
    st["track"] = m.track
    st["bgm_loop"] = list(m.bgm_loop)
    st["se_loop"] = list(m.se_loop)
    st["scr_track"] = m.scr_track
    st["scr_bgm_vol"] = m.scr_bgm_vol
    st["scr_se_id"] = list(m.scr_se_id)
    st["scr_se_loopid"] = list(m.scr_se_loopid)
    st["scr_se_vol"] = list(m.scr_se_vol)
    st["scr_se_loop"] = list(m.scr_se_loop)
    st["scr_se_pan"] = list(m.scr_se_pan)
    st["scr_se_char"] = [c & 0xFF for c in m.scr_se_char]
    for ch in range(20):
        st[f"defer{ch}"] = m.defer[ch].pack()[:0x16].hex()
    bits = bytearray(0x41)
    for k, v in m.music_bits.items():
        if 400 <= k < 400 + 0x41:
            bits[k - 400] |= v
    st["music"] = bytes(bits).hex()
    st["vol"] = bytes(m.vol[k] & 0xFF for k in range(0xB, 0x10)).hex()
    return st


class Pair:
    def __init__(self, eboot: Path, se_sections: Sequence[bytes], names: object) -> None:
        from .audio import se_lookup
        self.emu = _PlayEmu(eboot)
        self.emu.boot(list(se_sections))
        self.names = names
        self._lookup = se_lookup
        self.new_model()
        self.reset()

    def new_model(self) -> None:
        self.m = _PlayModel()
        self.m.se_lookup = lambda n, f: self._lookup(self.names, n, f)   # type: ignore[arg-type]

    def reset(self, default_playing: bool = False) -> None:
        self.emu.reset()
        self.new_model()
        self.emu.default_playing = default_playing
        self.m.default_playing = default_playing
        self.flags(0, 0, 0)

    def flags(self, ff: int, scan: int, skip: int, vsyncs: int = 1) -> None:
        self.emu.put_u16(VA_FF, ff)
        self.emu.put_u8(VA_SCAN, scan)
        self.emu.put_u16(VA_SKIP, skip)
        self.emu.put_u16(VA_VSYNC, vsyncs)
        self.m.ff, self.m.scan, self.m.skip, self.m.vsyncs = ff, scan, skip, vsyncs

    def step(self, item: str) -> Tuple[List[str], List[Call], List[Call]]:
        """Run one statement or ``frame[N]`` on both sides; returns (state mismatches,
        engine calls, model calls)."""
        self.m.calls = []
        if item.startswith("frame"):
            dt = int(item[5:] or 1)
            r = self.emu.call(0xAD80, rdi=dt)
            self.m.update(dt)
        else:
            r = self.emu.run(item)
            self.m.run(item)
        if r.err:
            return [f"engine error {r.err}"], [], []
        ec = [normalize_call(c) for c in r.calls]
        mc = list(self.m.calls)
        es, ms = engine_state(self.emu), model_state(self.m)
        bad = [f"{k}: engine {es[k]} model {ms[k]}" for k in es if es[k] != ms[k]]
        if ec != mc:
            bad.append(f"calls: engine {ec} model {mc}")
        return bad, ec, mc


def corpus_sequences(scr: object = None) -> Iterator[Tuple[int, List[str]]]:
    """(script index, audio statements in order) for every script that has any."""
    from .script import AllScr
    own = scr is None
    a = AllScr() if own else scr
    try:
        for si in range(len(a)):                          # type: ignore[arg-type]
            seq = [c.raw for c in a.commands(si) if c.key in AUDIO_CMDS]   # type: ignore[attr-defined]
            if seq:
                yield si, seq
    finally:
        if own:
            a.close()                                     # type: ignore[attr-defined]


def script_sequence(indices: Sequence[int]) -> Dict[int, List[str]]:
    """Audio statements of the given scripts (only those scripts are decoded)."""
    from .script import AllScr
    out: Dict[int, List[str]] = {}
    with AllScr() as a:
        for si in indices:
            out[si] = [c.raw for c in a.commands(si) if c.key in AUDIO_CMDS]
    return out


def replay(pair: Pair, seq: Sequence[str], rng: random.Random, frames: Tuple[int, int] = (0, 3),
           stop_at: int = 5) -> Tuple[int, int, List[str]]:
    """Replay a statement sequence with random frame updates in between; returns
    (steps, mismatching steps, first messages)."""
    steps = bad = 0
    msgs: List[str] = []
    for st in seq:
        items = [st] + [f"frame{rng.choice((1, 1, 1, 2, 5, 30))}" for _ in range(rng.randint(*frames))]
        for it in items:
            mm, _, _ = pair.step(it)
            steps += 1
            if mm:
                bad += 1
                if len(msgs) < stop_at:
                    msgs.append(f"{it}: " + " | ".join(mm)[:600])
    return steps, bad, msgs


def _setup() -> Tuple[Pair, object]:
    from eboot import eboot_path
    from . import image_dir
    from .audio import load_se_names
    from .mzp import MzpArchive
    eb, img = eboot_path(), image_dir()
    if eb is None or img is None:
        raise SystemExit("game data not found")
    arc = MzpArchive((img / "se_nam.mrg").read_bytes())
    names = load_se_names(img)
    return Pair(eb, [arc.read(i) for i in range(4)], names), names


def _main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.misc_audio_runtime_semantics_verify")
    ap.add_argument("mode", choices=["corpus", "steps"])
    ap.add_argument("items", nargs="*")
    ap.add_argument("--scripts", default="", help="comma list of script indices (corpus)")
    ap.add_argument("--seed", type=int, default=20261025)
    ap.add_argument("--out", default="")
    ap.add_argument("--flags", default="0,0,0", help="ff,scan,skip for the whole replay")
    ap.add_argument("--every", type=int, default=1, help="replay every n-th script only")
    a = ap.parse_args(argv)
    pair, _ = _setup()
    if a.mode == "steps":
        for it in a.items:
            mm, ec, mc = pair.step(it)
            print(it, "OK" if not mm else "MISMATCH")
            for x in mm:
                print("   ", x[:400])
        return 0
    rng = random.Random(a.seed)
    want = {int(x) for x in a.scripts.split(",") if x}
    t0 = time.time()
    tot_steps = tot_bad = nseq = 0
    report: Dict[str, object] = {"scripts": {}}
    for si, seq in corpus_sequences():
        if want and si not in want:
            continue
        if si % a.every:
            continue
        pair.reset()
        pair.flags(*[int(x) for x in a.flags.split(",")])
        steps, bad, msgs = replay(pair, seq, rng)
        tot_steps += steps
        tot_bad += bad
        nseq += 1
        report["scripts"][str(si)] = {"statements": len(seq), "steps": steps, "bad": bad, "msgs": msgs}  # type: ignore[index]
        if bad:
            print(si, len(seq), steps, bad, msgs[:2])
    report.update({"sequences": nseq, "steps": tot_steps, "bad_steps": tot_bad,
                   "seconds": round(time.time() - t0, 1)})
    print(json.dumps({k: v for k, v in report.items() if k != "scripts"}))
    if a.out:
        Path(a.out).write_text(json.dumps(report, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(_main())
