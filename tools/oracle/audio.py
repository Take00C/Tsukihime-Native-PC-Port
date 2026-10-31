"""Area ``audio``: oracle data for the C# media layer (package E1; ROADMAP M1 acceptance 6, ARCHITECTURE 7).

Parts (each a directory of ``samples/golden/audio/``):

``hevag``   the 19 vag.mrg entries decoded by ``tsuki.audio_hevag`` (``vag.tsv``): header facts and the
            SHA-1 of the int16 PCM and of the float PCM (int16 / 32768) the C# HE-VAG port must equal.
``atrac9``  every ATRAC9 stream of bgm, vse, voice and voice2 (17,925; ``streams.tsv``) decoded by
            ``tsuki.audio.decode_at9(float_output=True)`` (the ffmpeg CLI): the SHA-1 of the float32 playable PCM
            (``decoded[fact[2] : fact[2] + fact[0]]``, interleaved), its loop, its peak.  This is the nightly
            equality test of the C# decoder (minutes on all cores; left out of ``--quick``).
``mixer``   reference renders of the float mixer (ARCHITECTURE 7.1-7.2) defined by :func:`render_mix`: one
            synthetic scenario (its clips are written here too) and one on real clips (bgm #33 across its loop
            seam, vse #598, voice #15616, with ramps, pans and a SoundModel-style fade staircase).  The C# mixer
            must produce the same float32 bits.
``movie``   what the ffmpeg CLI decodes from three movies (package K1c; ``movies.json`` + ``frames_<name>.tsv``): the
            MD5 of every raw yuv420p frame of ``prologue_en`` (11,660) and ``TM_CI`` (180), of the first 300 frames of
            ``titlebg1``, and the SHA-1, sample count and peak of the AAC track as float32 LE stereo 48 kHz.  The C#
            ``Tsuki.Media.Movie`` pipeline must decode the same bits (about a minute with the ffmpeg CLI on PATH; left out of ``--quick``).
``sound``   traces of ``tsuki.misc_audio_runtime_semantics.SoundModel`` (package K1c / E2; ``sound_trace.json``): the audio
            statements of every script (22,037) replayed in script order with random frame updates between them, the same
            sequences in the silent states (ff, scan), skip mode, other vsync counts and two non-default configs, the
            voice gate and stop-on-advance rule over the voice groups and five configs, and the BGM ducking over 5 configs.
            Per step: the sound library calls, the return value and a digest of the 26 channel records plus the integer
            state.  The C# ``Tsuki.Engine.Sound.SoundModel`` must reproduce every step.

Run: ``python -m oracle.audio [--parts hevag,atrac9,mixer,movie,sound] [--quick]`` (registers the area with
``oracle.run``).  Everything goes to the local ``samples/golden/audio/``.

Mixer definition (the contract the C# ``Tsuki.Media.Mixer`` implements; all arithmetic IEEE float32 with
round-to-nearest, no fused multiply-add):

* output: float32 stereo 48 kHz; every frame starts at 0 and the voices are added in ascending voice id;
  nothing is clipped.
* a voice plays one clip (1 or 2 channels, interleaved) from ``start`` with an optional loop
  ``[loop_start, loop_end]`` (end inclusive).  With the loop active the voice never plays past
  ``loop_end``: after ``loop_end`` it jumps to ``loop_start`` while ``repeats`` is not 0 (-1 = forever, else
  one jump fewer each time) and stops otherwise.  Without the loop it stops after the clip's last frame.
* gain and pan are ramps.  A ramp set at frame F with length N from the current value ``g0`` to ``g1``
  gives frame F + k - 1 (k = 1..N) the value ``g0 + (g1 - g0) * (float(k) / float(N))`` and every later
  frame ``g1``; N = 0 sets the value at once.  ``g0`` is the value of frame F - 1.  A play command sets
  both at once.  The default ramp is 800 frames (one 60 Hz step).
* pan p in [-1, 1]: ``q = (p + 1) * 0.5``, ``pl = min(2 * (1 - q), 1)``, ``pr = min(2 * q, 1)``
  (engine law, audio.md 7); ``gl = g * pl``, ``gr = g * pr``; a mono source feeds both sides,
  ``out_l += gl * s_l`` and ``out_r += gr * s_r``.
* commands carry the absolute output frame they apply at and are applied in list order; a command at
  frame F acts before frame F is rendered.
"""
from __future__ import annotations

import hashlib
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Optional, Sequence

import numpy as np

from tsuki import audio as taudio
from tsuki import audio_hevag as thevag

from .manifest import AreaWriter, PartWriter, Producer, f32_json

__all__ = [
    "AREA", "MODULE", "PARTS", "DESCRIPTION", "EXPECTED_STREAMS", "DEFAULT_RAMP", "Clip", "render_mix",
    "synthetic_scenario", "real_scenario", "export_hevag", "export_atrac9", "export_mixer", "export_movie", "export_sound", "run", "main",
]

AREA = "audio"
MODULE = "oracle.audio"
PARTS: tuple[str, ...] = ("hevag", "atrac9", "mixer", "movie", "sound")
DESCRIPTION = ("audio oracle for the C# media layer: HE-VAG decodes (19), the float PCM SHA-1 of every ATRAC9 stream "
               "(17,925, nightly) and reference renders of the float mixer")

AUDIO_MD = "docs/formats/audio.md 1, 9"
EXPECTED_STREAMS = {"bgm": 132, "vse": 1327, "voice": 16464, "voice2": 2}
EXPECTED_VAG = 19
DEFAULT_RAMP = 800
F32 = np.float32

HEVAG_COLUMNS = ("index", "name", "data_size", "rate", "channels_byte", "frames", "sha1_i16", "sha1_f32", "peak")
ATRAC9_COLUMNS = ("bank", "index", "channels", "rate", "frames", "superframes", "loop_start", "loop_end", "whole",
                  "sha1", "peak")


def _producer(function: str, args: dict[str, Any], uses: tuple[str, ...]) -> Producer:
    return Producer(MODULE, function, args, uses)


def _tsv(columns: Sequence[str], rows: Sequence[Sequence[Any]]) -> bytes:
    lines = ["\t".join(columns)] + ["\t".join(str(v) for v in r) for r in rows]
    return ("\n".join(lines) + "\n").encode("utf-8")


# ---------------------------------------------------------------------------
# hevag
# ---------------------------------------------------------------------------

def export_hevag(part: PartWriter, game: Path, log: Optional[Callable[[str], None]] = None) -> None:
    image = game / "image"
    rows: list[tuple[Any, ...]] = []
    with taudio.AudioBank("vag", image) as b:
        for i in range(len(b)):
            data = b.arc.read(i)
            hdr = thevag.parse_vag(data)
            pcm = thevag.decode_vag(data)
            f = (pcm.astype(np.float32) / np.float32(32768.0)).astype("<f4")
            rows.append((i, hdr.name, hdr.data_size, hdr.sample_rate, hdr.channels_byte, int(pcm.shape[0]),
                         hashlib.sha1(pcm.astype("<i2").tobytes()).hexdigest(), hashlib.sha1(f.tobytes()).hexdigest(),
                         int(np.abs(pcm.astype(np.int32)).max()) if pcm.size else 0))
            part.add_entry("vag", i, hdr.name, b.arc.entries[i].offset, data)
    for ext in (".hed", ".mrg"):
        part.add_input(image / ("vag" + ext))
    part.write_bytes("vag.tsv", _tsv(HEVAG_COLUMNS, rows), "bytes",
                     _producer("export_hevag", {"columns": list(HEVAG_COLUMNS)}, ("tsuki.audio_hevag.decode_vag",)),
                     {"encoding": "tsv", "columns": list(HEVAG_COLUMNS), "rows": len(rows),
                      "digest": "sha1 of int16 LE mono (sha1_i16) and of float32 LE = int16 / 32768 (sha1_f32); "
                                "decode_vag(trim_end=True): frames before the first flag >= 7, leading zero frame kept"})
    part.count("entries", len(rows))
    part.check("entries", EXPECTED_VAG, len(rows), AUDIO_MD)
    if log is not None:
        log(f"[audio] hevag: {len(rows)} entries")


# ---------------------------------------------------------------------------
# atrac9
# ---------------------------------------------------------------------------

def _one_stream(b: taudio.AudioBank, i: int) -> tuple[Any, ...]:
    stream = b.stream(i)
    pcm = taudio.decode_at9(stream, trim=True, float_output=True)
    x = np.ascontiguousarray(pcm.samples, dtype="<f4")
    info = pcm.info
    assert info is not None
    lp = info.loop
    ls, le, whole = ("-", "-", "-") if lp is None else (lp.start, lp.end, int(lp.whole))
    return (b.bank, i, info.channels, info.sample_rate, info.num_samples, info.superframes, ls, le, whole,
            hashlib.sha1(x.tobytes()).hexdigest(), repr(float(np.abs(x).max())) if x.size else "0.0")


def export_atrac9(part: PartWriter, game: Path, workers: int, log: Optional[Callable[[str], None]] = None) -> None:
    image = game / "image"
    rows: list[tuple[Any, ...]] = []
    t0 = time.perf_counter()
    for bank, want in EXPECTED_STREAMS.items():
        with taudio.AudioBank(bank, image) as b:
            n = len(b)
            part.check(f"{bank}_entries", want, n, AUDIO_MD)
            t1 = time.perf_counter()
            with ThreadPoolExecutor(workers) as pool:
                got = list(pool.map(lambda i: _one_stream(b, i), range(n)))
            rows.extend(got)
            if log is not None:
                log(f"[audio] atrac9 {bank}: {n} streams in {time.perf_counter() - t1:.1f} s")
        for ext in (".hed", ".nam", ".mrg"):
            if (image / (bank + ext)).exists():
                part.add_input(image / (bank + ext))
    seconds = time.perf_counter() - t0
    part.write_bytes("streams.tsv", _tsv(ATRAC9_COLUMNS, rows), "bytes",
                     _producer("export_atrac9", {"columns": list(ATRAC9_COLUMNS)},
                               ("tsuki.audio.AudioBank.stream", "tsuki.audio.decode_at9")),
                     {"encoding": "tsv", "columns": list(ATRAC9_COLUMNS), "rows": len(rows),
                      "ffmpeg": taudio.ffmpeg_path(),
                      "digest": "sha1 of the float32 LE interleaved playable PCM decoded[fact[2] : fact[2] + fact[0]]; "
                                "loop in playable frames, end inclusive ('-' = no smpl loop)"})
    part.count("streams", len(rows))
    part.count("seconds", round(seconds, 1))
    part.count("workers", workers)
    part.check("streams", sum(EXPECTED_STREAMS.values()), len(rows), "ROADMAP M1 acceptance 6 (17,925 streams)")
    if log is not None:
        log(f"[audio] atrac9: {len(rows)} streams in {seconds:.1f} s on {workers} threads")


# ---------------------------------------------------------------------------
# mixer
# ---------------------------------------------------------------------------

@dataclass
class Clip:
    samples: np.ndarray          # float32, shape (frames, channels)
    loop: Optional[tuple[int, int]]

    @property
    def frames(self) -> int:
        return int(self.samples.shape[0])

    @property
    def channels(self) -> int:
        return int(self.samples.shape[1])


class _Ramp:
    __slots__ = ("g0", "g1", "start", "n")

    def __init__(self, value: Any) -> None:
        self.g0 = F32(value)
        self.g1 = F32(value)
        self.start = 0
        self.n = 0

    def at(self, t: int) -> np.float32:
        if self.n == 0:
            return self.g1
        k = t - self.start + 1
        if k >= self.n:
            return self.g1
        if k <= 0:
            return self.g0
        return F32(self.g0 + F32(self.g1 - self.g0) * F32(F32(k) / F32(self.n)))

    def set(self, target: Any, frame: int, n: int) -> None:
        current = self.at(frame - 1)
        self.g1 = F32(target)
        if n <= 0:
            self.g0 = self.g1
            self.n = 0
        else:
            self.g0 = current
            self.start = frame
            self.n = n

    def values(self, t0: int, count: int) -> Any:
        """The value of absolute frames t0 .. t0 + count - 1 (a scalar when constant over them)."""
        if self.n == 0 or t0 - self.start + 1 >= self.n:
            return self.g1
        k = np.arange(t0 - self.start + 1, t0 - self.start + 1 + count, dtype=np.int64)
        v = F32(self.g0) + F32(self.g1 - self.g0) * (k.astype(F32) / F32(self.n))
        v = v.astype(F32)
        v[k >= self.n] = self.g1
        v[k <= 0] = self.g0
        return v


class _Voice:
    def __init__(self, clip: Clip, loop: bool, repeats: int, start: int, gain: Any, pan: Any) -> None:
        self.clip = clip
        self.loop = clip.loop if loop else None
        self.repeats = repeats
        self.pos = start
        self.gain = _Ramp(gain)
        self.pan = _Ramp(pan)
        self.done = False
        end = self.loop[1] + 1 if self.loop is not None else clip.frames
        if not 0 <= start < end:
            raise ValueError(f"start frame {start} outside [0, {end})")

    def mix(self, out: np.ndarray, t0: int, t1: int) -> None:
        t = t0
        x = self.clip.samples
        while t < t1 and not self.done:
            seg_end = self.loop[1] + 1 if self.loop is not None else self.clip.frames
            n = min(t1 - t, seg_end - self.pos)
            g = np.asarray(self.gain.values(t, n), dtype=F32)
            p = np.asarray(self.pan.values(t, n), dtype=F32)
            q = (p + F32(1.0)) * F32(0.5)
            pl = np.minimum(F32(2.0) * (F32(1.0) - q), F32(1.0))
            pr = np.minimum(F32(2.0) * q, F32(1.0))
            gl = g * pl
            gr = g * pr
            src = x[self.pos:self.pos + n]
            sl = src[:, 0]
            sr = src[:, 1] if self.clip.channels == 2 else src[:, 0]
            o = t - t0
            out[o:o + n, 0] += gl * sl
            out[o:o + n, 1] += gr * sr
            self.pos += n
            t += n
            if self.pos == seg_end:
                if self.loop is not None and self.repeats != 0:
                    self.pos = self.loop[0]
                    if self.repeats > 0:
                        self.repeats -= 1
                else:
                    self.done = True


def render_mix(clips: dict[str, Clip], commands: Sequence[dict[str, Any]], frames: int,
               default_ramp: int = DEFAULT_RAMP) -> np.ndarray:
    """The reference render (see the module docstring): float32 (frames, 2)."""
    out = np.zeros((frames, 2), dtype=F32)
    voices: dict[int, _Voice] = {}
    cmds = sorted(enumerate(commands), key=lambda ic: (int(ic[1]["frame"]), ic[0]))
    ci = 0
    t = 0
    while t < frames:
        while ci < len(cmds) and int(cmds[ci][1]["frame"]) <= t:
            c = cmds[ci][1]
            ci += 1
            vid = int(c["voice"])
            op = c["op"]
            ramp = default_ramp if c.get("ramp") is None else int(c["ramp"])
            if op == "play":
                voices[vid] = _Voice(clips[c["clip"]], bool(c["loop"]), int(c.get("repeats", -1)),
                                     int(c.get("start", 0)), _f32(c["gain"]), _f32(c["pan"]))
            elif op == "stop":
                voices.pop(vid, None)
            elif op == "gain":
                if vid in voices:
                    voices[vid].gain.set(_f32(c["gain"]), t, ramp)
            elif op == "pan":
                if vid in voices:
                    voices[vid].pan.set(_f32(c["pan"]), t, ramp)
            else:
                raise ValueError(f"unknown mixer op {op!r}")
        t1 = min(frames, int(cmds[ci][1]["frame"])) if ci < len(cmds) else frames
        seg = out[t:t1]
        for vid in sorted(voices):
            voices[vid].mix(seg, t, t1)
        for vid in [k for k, v in voices.items() if v.done]:
            del voices[vid]
        t = t1
    return out


def _f32(v: Any) -> np.float32:
    if isinstance(v, dict):
        return np.frombuffer(int(v["bits"]).to_bytes(4, "little"), dtype="<f4")[0].astype(F32)
    return F32(v)


def _g(x: float) -> dict[str, Any]:
    return f32_json(F32(x))


def synthetic_scenario() -> tuple[dict[str, Clip], list[dict[str, Any]], int]:
    """Three generated clips (no game data) and a command list that exercises every rule."""
    rng = np.random.default_rng(20261025)
    a = (rng.standard_normal((30_000, 2)) * 0.3).astype(F32)            # stereo, inner loop
    b = (np.sin(np.arange(9_000) * 0.031) * 1.4).astype(F32).reshape(-1, 1)  # mono, over full scale, no loop
    c = (rng.uniform(-0.9, 0.9, (4_000, 2))).astype(F32)                 # stereo, whole-file loop
    clips = {"a": Clip(a, (5_000, 24_999)), "b": Clip(b, None), "c": Clip(c, (0, 3_999))}
    cmds: list[dict[str, Any]] = [
        {"frame": 0, "op": "play", "voice": 0, "clip": "a", "loop": True, "repeats": -1, "start": 0, "gain": _g(0.8), "pan": _g(0.0)},
        {"frame": 1_000, "op": "play", "voice": 6, "clip": "b", "loop": False, "repeats": 0, "start": 0, "gain": _g(1.25), "pan": _g(-0.5)},
        {"frame": 3_000, "op": "gain", "voice": 0, "gain": _g(0.25), "ramp": None},
        {"frame": 3_333, "op": "pan", "voice": 6, "pan": _g(1.0), "ramp": 1_500},
        {"frame": 3_500, "op": "gain", "voice": 0, "gain": _g(1.0), "ramp": 777},     # retarget mid-ramp
        {"frame": 4_000, "op": "pan", "voice": 0, "pan": _g(-0.3), "ramp": 0},       # at once
        {"frame": 9_999, "op": "play", "voice": 7, "clip": "c", "loop": True, "repeats": 3, "start": 100, "gain": _g(0.6), "pan": _g(0.75)},
        {"frame": 12_000, "op": "gain", "voice": 6, "gain": _g(0.0), "ramp": None},   # voice 6 already ended: ignored
        {"frame": 20_000, "op": "play", "voice": 6, "clip": "b", "loop": False, "repeats": 0, "start": 4_500, "gain": _g(0.5), "pan": _g(0.2)},
        {"frame": 20_000, "op": "gain", "voice": 6, "gain": _g(0.9), "ramp": 2_000},  # same frame, after the play
        {"frame": 33_000, "op": "pan", "voice": 0, "pan": _g(0.9), "ramp": 10_000},
        {"frame": 36_000, "op": "gain", "voice": 0, "gain": _g(0.0), "ramp": 3_000},
        {"frame": 45_000, "op": "stop", "voice": 0},
        {"frame": 46_000, "op": "play", "voice": 0, "clip": "a", "loop": True, "repeats": 1, "start": 24_000, "gain": _g(1.0), "pan": _g(-1.0)},
    ]
    return clips, cmds, 80_000


REAL_CLIPS = {"bgm33": ("bgm", 33), "se598": ("vse", 598), "voice15616": ("voice", 15616)}


def real_scenario(game: Path) -> tuple[dict[str, Clip], list[dict[str, Any]], int]:
    """bgm #33 from 1.2 s before its loop end (seam at output frame 58,000), vse #598 looping, voice #15616
    with a BGM duck, then a SoundModel-style fade: one gain step per 60 Hz frame (800 output frames)."""
    clips: dict[str, Clip] = {}
    for key, (bank, index) in REAL_CLIPS.items():
        with taudio.AudioBank(bank, game / "image") as b:
            pcm = b.decode(index, float_output=True)
        lp = pcm.loop
        clips[key] = Clip(np.ascontiguousarray(pcm.samples, dtype=F32),
                          None if lp is None or lp.start == lp.end else (lp.start, lp.end))
    cmds: list[dict[str, Any]] = [
        {"frame": 0, "op": "play", "voice": 0, "clip": "bgm33", "loop": True, "repeats": -1, "start": 8_200_000, "gain": _g(0.4), "pan": _g(0.0)},
        {"frame": 24_000, "op": "play", "voice": 6, "clip": "se598", "loop": True, "repeats": -1, "start": 0, "gain": _g(0.7), "pan": _g(0.0)},
        {"frame": 48_000, "op": "play", "voice": 2, "clip": "voice15616", "loop": False, "repeats": 0, "start": 0, "gain": _g(1.0), "pan": _g(0.0)},
        {"frame": 48_000, "op": "gain", "voice": 0, "gain": _g(0.2), "ramp": None},
        {"frame": 96_000, "op": "pan", "voice": 6, "pan": _g(-0.6), "ramp": 4_800},
        {"frame": 160_000, "op": "gain", "voice": 0, "gain": _g(0.4), "ramp": None},
    ]
    # fade the SE out the way SoundModel.Update does: a new target every 800 frames, 60 steps
    for k in range(1, 61):
        cmds.append({"frame": 240_000 + 800 * (k - 1), "op": "gain", "voice": 6, "gain": _g(0.7 * (60 - k) / 60), "ramp": None})
    cmds.append({"frame": 300_000, "op": "stop", "voice": 6})
    return clips, cmds, 480_000


def _scenario_json(name: str, clips: dict[str, Clip], cmds: list[dict[str, Any]], frames: int,
                   files: dict[str, str], real: dict[str, tuple[str, int]]) -> dict[str, Any]:
    cj: dict[str, Any] = {}
    for key, c in clips.items():
        d: dict[str, Any] = {"channels": c.channels, "frames": c.frames,
                             "loop": None if c.loop is None else list(c.loop),
                             "sha1": hashlib.sha1(np.ascontiguousarray(c.samples, dtype="<f4").tobytes()).hexdigest()}
        if key in files:
            d["file"] = files[key]
        if key in real:
            d["bank"], d["index"] = real[key]
        cj[key] = d
    return {"name": name, "rate": 48_000, "frames": frames, "default_ramp": DEFAULT_RAMP, "clips": cj,
            "commands": cmds, "output": f"{name}.f32"}


def export_mixer(part: PartWriter, game: Path, log: Optional[Callable[[str], None]] = None) -> None:
    prod = _producer("export_mixer", {"default_ramp": DEFAULT_RAMP}, ("oracle.audio.render_mix", "tsuki.audio.AudioBank.decode"))
    clips, cmds, frames = synthetic_scenario()
    files: dict[str, str] = {}
    for key, c in clips.items():
        rel = f"synthetic_clip_{key}.f32"
        part.write_bytes(rel, np.ascontiguousarray(c.samples, dtype="<f4").tobytes(), "f32le", prod,
                         {"channels": c.channels, "frames": c.frames})
        files[key] = rel
    t0 = time.perf_counter()
    out = render_mix(clips, cmds, frames)
    part.write_bytes("synthetic.f32", out.astype("<f4").tobytes(), "f32le", prod, {"channels": 2, "frames": frames})
    part.write_json("synthetic.json", _scenario_json("synthetic", clips, cmds, frames, files, {}), prod)
    part.count("synthetic_peak", float(np.abs(out).max()))
    part.check("synthetic_exceeds_full_scale", True, bool(np.abs(out).max() > 1.0), "ARCHITECTURE 7.1 (nothing is clipped)")

    rclips, rcmds, rframes = real_scenario(game)
    rout = render_mix(rclips, rcmds, rframes)
    part.write_bytes("real.f32", rout.astype("<f4").tobytes(), "f32le", prod, {"channels": 2, "frames": rframes})
    part.write_json("real.json", _scenario_json("real", rclips, rcmds, rframes, {}, REAL_CLIPS), prod)
    for bank in sorted({b for b, _ in REAL_CLIPS.values()}):
        for ext in (".hed", ".nam", ".mrg"):
            if (game / "image" / (bank + ext)).exists():
                part.add_input(game / "image" / (bank + ext))
    part.count("real_peak", float(np.abs(rout).max()))
    part.count("seconds", round(time.perf_counter() - t0, 1))
    if log is not None:
        log(f"[audio] mixer: synthetic {frames} + real {rframes} frames")


# ---------------------------------------------------------------------------
# movie (package K1c)
# ---------------------------------------------------------------------------

#: (file name without .mp4, frame limit or None for all).  prologue_en is the S2 reference movie.
MOVIES: tuple[tuple[str, Optional[int]], ...] = (("prologue_en", None), ("TM_CI", None), ("titlebg1", 300))
EXPECTED_PROLOGUE_FRAMES = 11_660
EXPECTED_PROLOGUE_SAMPLES = 18_655_997       # the AAC stream's duration_ts (ARCHITECTURE 7.5)
MOVIE_FRAME_COLUMNS = ("index", "pts_ticks", "md5")


def _tool(name: str) -> str:
    import shutil
    path = shutil.which(name)
    if path is None:
        raise RuntimeError(f"{name} is not on PATH (the movie oracle needs the ffmpeg 9 CLI)")
    return path


def _probe(path: Path) -> dict[str, Any]:
    import json
    import subprocess
    out = subprocess.run([_tool("ffprobe"), "-v", "error", "-show_entries",
                          "stream=index,codec_type,codec_name,time_base,avg_frame_rate,nb_frames,duration_ts,sample_rate",
                          "-of", "json", str(path)], capture_output=True, text=True, check=True).stdout
    streams = json.loads(out)["streams"]
    video = next(s for s in streams if s["codec_type"] == "video")
    audio = next((s for s in streams if s["codec_type"] == "audio"), None)
    return {"video": video, "audio": audio}


def _fraction(text: str) -> float:
    a, _, b = text.partition("/")
    return float(a) / float(b) if b else float(a)


def _frame_md5s(path: Path, limit: Optional[int]) -> list[tuple[int, int, str]]:
    """(timeline index, pts in stream ticks, md5 of the raw yuv420p frame) for every decoded frame."""
    import subprocess
    probe = _probe(path)["video"]
    tb, fps = _fraction(probe["time_base"]), _fraction(probe["avg_frame_rate"])
    cmd = [_tool("ffmpeg"), "-v", "error", "-nostdin", "-i", str(path), "-map", "0:v:0", "-fps_mode", "passthrough"]
    if limit:
        cmd += ["-frames:v", str(limit)]
    cmd += ["-f", "framemd5", "-"]
    out = subprocess.run(cmd, capture_output=True, text=True, check=True).stdout
    rows = []
    for line in out.splitlines():
        if not line or line.startswith("#"):
            continue
        _stream, _dts, pts, _dur, _size, md5 = (c.strip() for c in line.split(","))
        rows.append((int(round(int(pts) * tb * fps)), int(pts), md5))
    return rows


def _audio_digest(path: Path) -> Optional[dict[str, Any]]:
    """SHA-1, sample count and peak of the AAC track as float32 LE stereo 48 kHz (what libavcodec + swresample produce)."""
    import subprocess
    if _probe(path)["audio"] is None:
        return None
    cmd = [_tool("ffmpeg"), "-v", "error", "-nostdin", "-i", str(path), "-map", "0:a:0", "-f", "f32le", "-ar", "48000",
           "-ac", "2", "-"]
    h = hashlib.sha1()
    total = 0
    peak = 0.0
    carry = b""
    with subprocess.Popen(cmd, stdout=subprocess.PIPE) as proc:
        assert proc.stdout is not None
        while True:
            chunk = proc.stdout.read(1 << 22)
            if not chunk:
                break
            h.update(chunk)
            total += len(chunk)
            data = carry + chunk
            usable = len(data) - (len(data) % 4)
            carry = data[usable:]
            x = np.frombuffer(data[:usable], dtype="<f4")
            if x.size:
                peak = max(peak, float(np.abs(x).max()))
        if proc.wait() != 0:
            raise RuntimeError(f"ffmpeg failed on {path.name}")
    return {"sha1": h.hexdigest(), "samples_per_channel": total // 8, "peak": repr(peak)}


def export_movie(part: PartWriter, game: Path, log: Optional[Callable[[str], None]] = None) -> None:
    movies: dict[str, Any] = {}
    prod = _producer("export_movie", {"movies": [list(m) for m in MOVIES]}, ())
    for name, limit in MOVIES:
        path = game / "image" / "movie" / (name + ".mp4")
        t0 = time.perf_counter()
        probe = _probe(path)
        rows = _frame_md5s(path, limit)
        digest = _audio_digest(path)
        rel = f"frames_{name}.tsv"
        part.write_bytes(rel, _tsv(MOVIE_FRAME_COLUMNS, rows), "bytes", prod,
                         {"encoding": "tsv", "columns": list(MOVIE_FRAME_COLUMNS), "rows": len(rows),
                          "digest": "md5 of the raw yuv420p frame (tight planes Y, U, V) from ffmpeg -f framemd5 "
                                    "-fps_mode passthrough; index = round(pts * time_base * fps)"})
        movies[name] = {"file": f"image/movie/{name}.mp4", "frames_file": rel, "frames": len(rows), "frame_limit": limit,
                        "fps": _fraction(probe["video"]["avg_frame_rate"]), "time_base": probe["video"]["time_base"],
                        "nb_frames": int(probe["video"].get("nb_frames", 0)),
                        "audio": None if digest is None else {**digest, "duration_ts": int(probe["audio"]["duration_ts"])}}
        part.add_input(path, full_hash=False)
        part.count(f"{name}_frames", len(rows))
        if log is not None:
            log(f"[audio] movie {name}: {len(rows)} frames, audio {'-' if digest is None else digest['samples_per_channel']} "
                f"in {time.perf_counter() - t0:.1f} s")
    part.write_json("movies.json", {"movies": movies, "ffmpeg": _tool("ffmpeg")}, prod)
    pro = movies["prologue_en"]
    part.check("prologue_en_frames", EXPECTED_PROLOGUE_FRAMES, pro["frames"], "ARCHITECTURE 7.5 (S2 measurement)")
    part.check("prologue_en_audio_samples", EXPECTED_PROLOGUE_SAMPLES, pro["audio"]["samples_per_channel"],
               "ARCHITECTURE 7.5 (equal to the stream's duration_ts)")
    part.check("prologue_en_audio_duration_ts", EXPECTED_PROLOGUE_SAMPLES, pro["audio"]["duration_ts"], "ffprobe")


# ---------------------------------------------------------------------------
# sound (package K1c / E2)
# ---------------------------------------------------------------------------

SOUND_SEED = 20261026
SOUND_AUDIO_CMDS = ("MPLY", "MVOL", "MFAD", "SEPL", "SEFI", "SEFD", "SEVL", "SESP")
SOUND_DT_CHOICES = (1, 1, 1, 2, 5, 30)
SOUND_EXPECTED_CORPUS_STATEMENTS = 22_037           # sound_runtime.md 13: 14,543 + 3,516 + 1,285 + 1,171 + 780 + 724 + 18


def _sound_words(m: Any) -> list[int]:
    """The integer state the C# ``SoundModel.StateWords`` returns, in the same order."""
    w: list[int] = [m.slot, m.track, m.bgm_loop[0], m.bgm_loop[1]]
    w += m.se_loop
    w += [m.scr_track, m.scr_bgm_vol]
    for arr in (m.scr_se_id, m.scr_se_loopid, m.scr_se_vol, m.scr_se_loop, m.scr_se_pan, m.scr_se_char):
        w += arr
    w += [m.voice_pending, m.voice_char, m.voice_arg3]
    w += [m.vol[i] for i in range(0xB, 0x10)]
    w += [m.duck_level, m.duck_applied]
    for d in m.defer:
        lo = d.fade & 0xFFFFFFFF
        lo = lo - (1 << 32) if lo >> 31 else lo
        w += [d.timer, d.kind, d.se_id, lo, d.fade >> 32, d.vol, d.vol2, d.pan, d.pan2, d.loop, d.char]
    w.append(len(m.music_bits))
    for k in sorted(m.music_bits):
        w += [k, m.music_bits[k]]
    return w


def _sound_digest(m: Any) -> str:
    import struct
    h = hashlib.sha1()
    h.update(b"".join(m.records()))
    w = _sound_words(m)
    h.update(struct.pack("<%di" % len(w), *w))
    return h.hexdigest()[:12]


def _sound_calls(m: Any) -> list[str]:
    cs = [" ".join(str(x) for x in c) for c in m.calls if c[0] != "is_playing"]
    m.calls.clear()
    return cs


class _SoundHarness:
    """One SoundModel plus the recording of what the C# test has to reproduce."""

    def __init__(self, names: Any, captured: dict[str, Any], cfg: dict[str, Any], default_playing: bool,
                 flags: Sequence[int]) -> None:
        from tsuki import misc_audio_runtime_semantics as sm
        c = sm.Config()
        for k, v in cfg.items():
            if k == "char_vol":
                c.char_vol = list(v)
            else:
                setattr(c, k, v)
        self.m = sm.SoundModel(c)
        self.m.default_playing = default_playing
        self.m.se_lookup = self._lookup
        self._names, self._captured = names, captured
        self.set_flags(*flags)

    def _lookup(self, name: str, flags: int) -> Optional[tuple[int, int]]:
        res = taudio.se_lookup(self._names, name, flags)
        self._captured[f"{name}|{flags}"] = None if res is None else res[1]
        return res

    def set_flags(self, ff: int, scan: int, skip: int, vsyncs: int) -> None:
        self.m.ff, self.m.scan, self.m.skip, self.m.vsyncs = ff, scan, skip, vsyncs

    def step(self, st: list[Any]) -> list[Any]:
        """Run one step; returns [calls, ret, digest]."""
        m = self.m
        ret: Any = None
        op = st[0]
        if op == "r":
            ret = m.run(st[1])
        elif op == "u":
            m.update(st[1])
        elif op == "apply":
            m.vol.update(m.cfg.volume_block())
            m.duck_applied = -1
        elif op == "cfg":
            for k, v in st[1].items():
                if k == "char_vol":
                    m.cfg.char_vol = list(v)
                else:
                    setattr(m.cfg, k, v)
        elif op == "flags":
            self.set_flags(*st[1:5])
        elif op == "gate":
            r = m.voice_start_at_message()
            ret = None if r is None else list(r)
        elif op == "duck":
            ret = m.duck_update(bool(st[1]))
        elif op == "adv":
            ret = bool(m.stop_voice_on_advance(st[1]))
        else:
            raise ValueError(op)
        return [_sound_calls(m), ret, _sound_digest(m)]


def _sound_scenarios(game: Path, log: Optional[Callable[[str], None]]) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    import random
    from tsuki.script import AllScr
    names = taudio.load_se_names(game / "image")
    captured: dict[str, Any] = {}
    rng = random.Random(SOUND_SEED)
    out: list[dict[str, Any]] = []

    def replay(name: str, seq: Sequence[str], cfg: dict[str, Any], default_playing: bool, flags: Sequence[int]) -> None:
        h = _SoundHarness(names, captured, cfg, default_playing, flags)
        steps: list[list[Any]] = []
        expect: list[list[Any]] = []
        for stmt in seq:
            items: list[list[Any]] = [["r", stmt]] + [["u", rng.choice(SOUND_DT_CHOICES)] for _ in range(rng.randint(0, 3))]
            for it in items:
                steps.append(it)
                expect.append(h.step(it))
        out.append({"name": name, "cfg": cfg, "default_playing": default_playing, "flags": list(flags),
                    "steps": steps, "expect": expect})

    sequences: list[tuple[int, list[str]]] = []
    with AllScr() as a:
        for si in range(len(a)):
            seq = [c.raw for c in a.commands(si) if c.key in SOUND_AUDIO_CMDS]
            if seq:
                sequences.append((si, seq))
    total = sum(len(s) for _, s in sequences)
    for si, seq in sequences:
        replay(f"corpus:{si}", seq, {}, False, (0, 0, 0, 1))
    if log is not None:
        log(f"[audio] sound corpus: {len(sequences)} scripts, {total} statements")
    # the silent states, skip mode and other vsync counts on every 8th script; a loud config with the SE hook and the 5.1 set
    variants: list[tuple[str, dict[str, Any], bool, tuple[int, int, int, int]]] = [
        ("ff", {}, False, (1, 0, 0, 1)),
        ("scan", {}, False, (0, 1, 0, 1)),
        ("skip", {}, True, (0, 0, 1, 2)),
        ("vsync3", {}, True, (0, 0, 0, 3)),
        ("cfg_a", {"bgm": 70, "se": 85, "sys_se": 30, "voice": 60, "se_caption": 1, "alt_bgm": 1, "mute_se": 0,
                   "char_enable": 0x2AAAA, "char_vol": [10 + 5 * i for i in range(19)]}, True, (0, 0, 0, 1)),
        ("cfg_b", {"bgm": 0, "se": 100, "voice": 0, "mute_bgm": 1, "mute_se": 1, "duck": 1,
                   "char_enable": 0x15555, "char_vol": [100 - 4 * i for i in range(19)]}, False, (0, 0, 0, 2)),
    ]
    for vname, cfg, dp, flags in variants:
        for si, seq in sequences[::8]:
            replay(f"{vname}:{si}", seq, cfg, dp, flags)

    # voice gate, stop on advance, ducking (hand scenarios)
    voice_names = sorted({"A00", "A10", "A20", "A24", "B00", "B50", "B90", "C00", "C40", "C70", "C80", "C90", "D01", "DU0",
                          "M00", "M03", "M11", "M12", "M04", "X99"})
    configs: list[tuple[str, dict[str, Any]]] = [
        ("default", {}),
        ("voice_off", {"voice": 0}),
        ("second_off", {"voice_on2": 0}),
        ("some_chars_off", {"char_enable": 0x2AAAA, "char_vol": [7 * i + 1 for i in range(19)]}),
        ("no_skip_voice", {"skip_voice": 0}),
    ]
    gate_steps: list[list[Any]] = []
    for cn, cfg in configs:
        gate_steps.append(["cfg", dict(cfg)])
        gate_steps.append(["apply"])
        for flags in ((0, 0, 0, 1), (1, 0, 0, 1), (0, 0, 1, 1), (0, 1, 0, 2)):
            gate_steps.append(["flags", *flags])
            for i, g in enumerate(voice_names):
                gate_steps.append(["r", f"_VPLY({g}_06_00_0029_0,{0x726 + i:05x});"])
                gate_steps.append(["gate"])
                gate_steps.append(["adv", i % 2])
        # a repeated entry ('_' name keeps the previous one) and the third argument
        gate_steps.append(["flags", 0, 0, 0, 1])
        gate_steps.append(["r", "_VPLY(A10_07_00_0147_1,01b40,0);"])
        gate_steps.append(["r", "_VPLY(_,00000);"])
        gate_steps.append(["gate"])
        gate_steps.append(["gate"])
        gate_steps.append(["cfg", {"voice": 100, "voice_on2": 1, "char_enable": 0x3FFFFFFF, "skip_voice": 1,
                                   "char_vol": [50] * 19}])
        gate_steps.append(["apply"])
    h = _SoundHarness(names, captured, {}, True, (0, 0, 0, 1))
    out.append({"name": "voice_gate", "cfg": {}, "default_playing": True, "flags": [0, 0, 0, 1], "steps": gate_steps,
                "expect": [h.step(s) for s in gate_steps]})

    for dn, cfg in (("duck_on", {"duck": 1}), ("duck_on_bgm70", {"duck": 1, "bgm": 70}), ("duck_on_muted", {"duck": 1, "mute_bgm": 1}),
                    ("duck_off", {"duck": 0}), ("duck_loud", {"duck": 1, "bgm": 100})):
        steps = [["r", "_MPLY(33,,`017:90);"], ["u", 1]]
        for block in range(8):
            active = block % 2 == 0
            steps += [["duck", active], ["u", 1]] * (30 + 17 * block)
            if block == 3:
                steps += [["cfg", {"bgm": 55}], ["apply"]]
        steps += [["r", "_MVOL(,,`001:500,,`017:40);"]] + [["duck", True], ["u", 1]] * 20
        h = _SoundHarness(names, captured, cfg, True, (0, 0, 0, 1))
        out.append({"name": dn, "cfg": cfg, "default_playing": True, "flags": [0, 0, 0, 1], "steps": steps,
                    "expect": [h.step(s) for s in steps]})
    return out, {"se_lookup": captured, "statements": total, "scripts": len(sequences)}


def export_sound(part: PartWriter, game: Path, log: Optional[Callable[[str], None]] = None) -> None:
    t0 = time.perf_counter()
    scenarios, meta = _sound_scenarios(game, log)
    steps = sum(len(s["steps"]) for s in scenarios)
    prod = _producer("export_sound", {"seed": SOUND_SEED}, (
        "tsuki.misc_audio_runtime_semantics.SoundModel.run", "tsuki.misc_audio_runtime_semantics.SoundModel.update",
        "tsuki.misc_audio_runtime_semantics.SoundModel.duck_update", "tsuki.misc_audio_runtime_semantics.SoundModel.voice_start_at_message",
        "tsuki.audio.se_lookup", "tsuki.script.AllScr"))
    doc = {"model": "tsuki.misc_audio_runtime_semantics.SoundModel (boot config VA 0x3EAC0 unless a scenario sets cfg)",
           "format": "each scenario starts a fresh model; expect[i] = [calls, ret, digest] after steps[i]; calls = 'op args' strings "
                     "(is_playing left out); digest = first 12 hex of SHA-1(26 channel records + int32 state words)",
           "seed": SOUND_SEED, "dt_choices": list(SOUND_DT_CHOICES), "se_lookup": meta["se_lookup"], "scenarios": scenarios}
    import json
    text = json.dumps(doc, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n"   # compact: the file is large
    part.write_bytes("sound_trace.json", text.encode("utf-8"), "json", prod, {"scenarios": len(scenarios), "steps": steps})
    part.add_input(game / "image" / "se_nam.mrg", full_hash=True)
    part.count("scenarios", len(scenarios))
    part.count("steps", steps)
    part.count("corpus_statements", meta["statements"])
    part.count("corpus_scripts", meta["scripts"])
    part.count("se_lookups", len(meta["se_lookup"]))
    part.check("corpus_statements", SOUND_EXPECTED_CORPUS_STATEMENTS, meta["statements"], "docs/formats/sound_runtime.md 13")
    part.count("seconds", round(time.perf_counter() - t0, 1))
    if log is not None:
        log(f"[audio] sound: {len(scenarios)} scenarios, {steps} steps in {time.perf_counter() - t0:.1f} s")


# ---------------------------------------------------------------------------
# runner
# ---------------------------------------------------------------------------

def run(root: Path, game: Path, *, parts: Optional[Sequence[str]] = None, quick: bool = False,
        log: Optional[Callable[[str], None]] = None) -> dict[str, Any]:
    """Entry point used by ``oracle.run``.  Default: every part; ``quick`` leaves out ``atrac9``.
    ``TSUKI_ORACLE_PROCS`` sets the decode thread count."""
    sel = list(parts) if parts else ([p for p in PARTS if p not in ("atrac9", "movie")] if quick else list(PARTS))
    bad = [p for p in sel if p not in PARTS]
    if bad:
        raise ValueError(f"unknown parts {bad}; choose from {list(PARTS)}")
    workers = int(os.environ.get("TSUKI_ORACLE_PROCS", "0")) or max(1, (os.cpu_count() or 4) - 2)
    gen = _producer("run", {"parts": sel, "game": Path(game).as_posix(), "workers": workers}, ())
    area = AreaWriter(Path(root), AREA, gen, description=DESCRIPTION, game_dir=Path(game))
    for name in sel:
        pw = area.part(name, {"hevag": "the 19 HE-VAG entries", "atrac9": "every ATRAC9 stream",
                              "mixer": "reference mixer renders",
                              "movie": "ffmpeg CLI decodes of three movies",
                              "sound": "SoundModel traces"}[name])
        if name == "hevag":
            export_hevag(pw, Path(game), log)
        elif name == "atrac9":
            export_atrac9(pw, Path(game), workers, log)
        elif name == "movie":
            export_movie(pw, Path(game), log)
        elif name == "sound":
            export_sound(pw, Path(game), log)
        else:
            export_mixer(pw, Path(game), log)
        pw.finish()
        if log is not None:
            log(f"[audio] {name}: {len(pw.files)} files, {sum(1 for c in pw.checks if not c.ok)} failed checks of {len(pw.checks)}")
    return area.finish()


def main(argv: Optional[Sequence[str]] = None) -> int:
    """``python -m oracle.audio [runner options]``: the runner with this area registered."""
    from . import run as runner
    runner.AREAS.setdefault(AREA, MODULE)
    return runner.main([AREA, *(sys.argv[1:] if argv is None else argv)])


if __name__ == "__main__":
    raise SystemExit(main())
