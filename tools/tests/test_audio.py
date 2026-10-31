"""Tests for tsuki.audio / tsuki.audio_hevag (bgm, vse, vag, voice, voice2, se_nam).

Synthetic tests always run; real-data tests skip when the game folder is
missing (TSUKI_GAME_DIR overrides the default), decode tests also need ffmpeg.
Run:  cd D:/TsukihimePort/tools && ../.venv/Scripts/python.exe -m pytest tests/test_audio.py -q
"""
from __future__ import annotations

import io
import random
import struct
import sys
import wave
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import image_dir  # noqa: E402
from tsuki import audio_hevag as H  # noqa: E402
from tsuki.audio import (ATRAC9_GUID, AudioBank, AudioError, LoopInfo, Pcm, SeNames,  # noqa: E402
                         bgm_index, decode_at9, ffmpeg_path, load_se_names, parse_atrac9_config,
                         parse_voice_ref, parse_wave, render_loop, se_lookup, write_wav)

IMG = image_dir()
needs_game = pytest.mark.skipif(IMG is None, reason="game data not found")
needs_ffmpeg = pytest.mark.skipif(ffmpeg_path() is None, reason="ffmpeg not on PATH")


# ---------------------------------------------------------------------------
# synthetic
# ---------------------------------------------------------------------------

def _riff(channels: int, cfg: str, n: int, loop: tuple[int, int] | None, superframes: int) -> bytes:
    c = parse_atrac9_config(bytes.fromhex(cfg))
    align = c.superframe_bytes
    fmt = struct.pack("<HHIIHHHHI16sI4s4s", 0xFFFE, channels, 48000, align * 48000 // 1024, align, 0, 34, 1024,
                      3 if channels == 2 else 4, ATRAC9_GUID, 1, bytes.fromhex(cfg), b"\0" * 4)
    body = b"fmt " + struct.pack("<I", len(fmt)) + fmt
    body += b"fact" + struct.pack("<I", 12) + struct.pack("<3I", n, 256, 256)
    if loop is not None:
        smpl = struct.pack("<9I", 0, 0, 20833, 60, 0, 0, 0, 1, 24) + struct.pack("<6I", 0, 0, loop[0], loop[1], 0, 0)
        body += b"smpl" + struct.pack("<I", len(smpl)) + smpl
    data = b"\0" * (align * superframes)
    body += b"data" + struct.pack("<I", len(data)) + data
    return b"RIFF" + struct.pack("<I", 4 + len(body)) + b"WAVE" + body


def test_atrac9_config() -> None:
    c = parse_atrac9_config(bytes.fromhex("fe740ff0"))          # bgm
    assert (c.sample_rate, c.channels, c.frame_bytes, c.frames_per_superframe) == (48000, 2, 128, 4)
    assert (c.superframe_bytes, c.samples_per_frame, c.samples_per_superframe) == (512, 256, 1024)
    c = parse_atrac9_config(bytes.fromhex("fe700bf0"))          # voice
    assert (c.channels, c.frame_bytes, c.superframe_bytes) == (1, 96, 384)
    c = parse_atrac9_config(bytes.fromhex("fe7004f0"))          # dummies
    assert (c.channels, c.frame_bytes, c.superframe_bytes) == (1, 40, 160)
    c = parse_atrac9_config(bytes.fromhex("fe7409f0"))          # bgm #0
    assert c.superframe_bytes == 320
    with pytest.raises(AudioError):
        parse_atrac9_config(bytes.fromhex("ff740ff0"))
    with pytest.raises(AudioError):
        parse_atrac9_config(bytes.fromhex("fe750ff0"))          # validation bit


def test_parse_wave_and_loop_coordinates() -> None:
    n = 96000
    w = parse_wave(_riff(2, "fe740ff0", n, (256, 256 + n - 1), 94))
    assert w.num_samples == n and w.delay == 256 and w.superframes == 94 and w.decoded_samples == 96256
    assert w.loop == LoopInfo(0, n - 1, True)
    w = parse_wave(_riff(2, "fe740ff0", 8258048, (962256, 8258255), 8065))
    assert w.loop == LoopInfo(962000, 8257999, False) and w.loop.end_exclusive == 8258000
    w = parse_wave(_riff(1, "fe700bf0", 1000, None, 2))
    assert w.loop is None and [c[0] for c in w.chunks] == ["fmt ", "fact", "data"]
    with pytest.raises(AudioError):
        parse_wave(_riff(1, "fe740ff0", 1000, None, 2))          # stereo config in a mono fmt
    with pytest.raises(AudioError):
        parse_wave(b"RIFF\0\0\0\0WAVEjunk")


def _frame(idx: int, shift: int, flag: int, nibbles: list[int]) -> bytes:
    b0 = ((idx & 0x0F) << 4) | shift
    b1 = (idx & 0xF0) | flag
    data = bytearray(14)
    for i, v in enumerate(nibbles):
        v &= 0xF
        data[i >> 1] |= (v << 4) if i & 1 else v
    return bytes([b0, b1]) + bytes(data)


def test_hevag_table() -> None:
    t = np.array(H.HEVAG_COEFS_8192)
    assert t.shape == (128, 4)
    # rows 0..4 are the classic PS-ADPCM filters (x/64), rows 0..28 use two taps only
    assert [tuple(r) for r in (t[:5, :2] // 128)] == [(0, 0), (60, 0), (115, -52), (98, -55), (122, -60)]
    assert not t[:29, 2:].any() and t[29:, 2:].any()
    assert abs(H.HEVAG_COEFS[5, 0] - 1.7982178) < 1e-7 and abs(H.HEVAG_COEFS[46, 3] + 0.84802246) < 1e-7


def test_hevag_hand_computed() -> None:
    # index 1 = (0.9375, 0, 0, 0); shift 12 -> code = signed nibble
    fr = _frame(1, 12, 0, [7, -8, 3, 0] + [0] * 24)
    out = H.decode_frames(fr, use_numba=False)
    exp = []
    h = 0
    for nib in [7, -8, 3, 0] + [0] * 24:
        s = nib + int(np.float32(np.float32(h) * np.float32(0.9375)))
        exp.append(s)
        h = s
    assert out.tolist() == exp
    # shift 0 -> code = nibble << 12; clamping keeps the unclamped value in the history
    fr = _frame(0, 0, 0, [7, 7] + [0] * 26)
    assert H.decode_frames(fr, use_numba=False)[:2].tolist() == [28672, 28672]
    fr = _frame(4, 0, 0, [7, 7, 7] + [0] * 25)           # 1.90625*h1 - 0.9375*h2 overflows
    o = H.decode_frames(fr, use_numba=False)
    assert o[1] == 32767 and o.min() >= -32768


def test_hevag_flag7_and_index_clamp() -> None:
    fr = _frame(1, 12, 7, [7] * 28)
    assert not H.decode_frames(fr, use_numba=False).any()
    assert H.frame_params(bytes([0x0C, 0x80]) + bytes(14)) == (0, 12, 0)   # index 128 -> 0


def test_hevag_numba_matches_python() -> None:
    if not H.HAVE_NUMBA:
        pytest.skip("numba not installed")
    rnd = random.Random(7)
    body = b"".join(_frame(rnd.randrange(128), rnd.randrange(13), rnd.choice([0, 0, 0, 2, 6]),
                           [rnd.randrange(-8, 8) for _ in range(28)]) for _ in range(300))
    a = H.decode_frames(body, use_numba=False)
    b = H.decode_frames(body, use_numba=True)
    assert np.array_equal(a, b)


def test_hevag_streaming_history() -> None:
    rnd = random.Random(3)
    body = b"".join(_frame(rnd.randrange(128), rnd.randrange(4, 13), 0,
                           [rnd.randrange(-8, 8) for _ in range(28)]) for _ in range(20))
    whole = H.decode_frames(body, use_numba=False)
    hist = np.zeros(4, np.int64)
    parts = [H.decode_frames(body[k * 16 * 5:(k + 1) * 16 * 5], hist=hist, use_numba=False) for k in range(4)]
    assert np.array_equal(np.concatenate(parts), whole)


def test_write_wav_with_smpl(tmp_path: Path) -> None:
    x = (np.arange(2000, dtype=np.int16) % 100).reshape(-1, 2)
    p = tmp_path / "t.wav"
    write_wav(p, x, 48000, LoopInfo(10, 899, False))
    raw = p.read_bytes()
    with wave.open(io.BytesIO(raw)) as w:
        assert (w.getnchannels(), w.getframerate(), w.getnframes()) == (2, 48000, 1000)
    i = raw.index(b"smpl")
    assert struct.unpack_from("<I", raw, i + 4)[0] == 60
    assert struct.unpack_from("<2I", raw, i + 8 + 36 + 8) == (10, 899)
    assert struct.unpack_from("<I", raw, 4)[0] == len(raw) - 8


def test_render_loop() -> None:
    x = np.arange(10, dtype=np.int16).reshape(-1, 1)
    pcm = Pcm(x, 48000, LoopInfo(3, 6, False), True)
    assert render_loop(pcm, 1)[:, 0].tolist() == [0, 1, 2, 3, 4, 5, 6, 3, 4, 5, 6]
    assert render_loop(Pcm(x, 48000, None, True), 3).shape[0] == 10


def test_se_lookup_rules() -> None:
    names = SeNames(("S00", "S01", "DUP"), ("RANKING",), ("DUMMY", "FATE_SE001", "DUP", "MOON_SE00013"),
                    ("SE_A0018", "DUP"))
    assert se_lookup(names, "S01") == (0, 1)
    assert se_lookup(names, "dup") == (0, 2)                       # section 0 first
    assert se_lookup(names, "dup", flags=1) == (2, 10001)          # then section 3 (+10000)
    assert se_lookup(names, "dup", flags=3) == (2, 2)              # then section 2
    assert se_lookup(names, "x\\moon_se00013.wav", flags=1) == (2, 3)
    assert se_lookup(names, "RANKING") is None                     # section 1 is never searched
    assert se_lookup(names, "nothing") is None


def test_voice_ref_and_bgm_index() -> None:
    assert parse_voice_ref("A00_06_00_0029_0,00726") == ("A00_06_00_0029_0", 0x726)
    assert parse_voice_ref("A12_00_01_0000_0,022be") == ("A12_00_01_0000_0", 0x22BE)
    with pytest.raises(AudioError):
        parse_voice_ref("A00_06_00_0029_0")
    assert bgm_index(14) == 14 and bgm_index(14, alt_set=True) == 145 and bgm_index(0, alt_set=True) == 0


# ---------------------------------------------------------------------------
# real data
# ---------------------------------------------------------------------------

@needs_game
def test_bank_sizes_and_names() -> None:
    expect = {"bgm": 132, "vse": 1327, "vag": 19, "voice": 16464, "voice2": 2}
    for bank, n in expect.items():
        with AudioBank(bank) as b:
            assert len(b) == n
            names = b.names
            if bank == "vse":
                assert names[0] == names[-1] == "DUMMY" and names[1] == "FATE_SE001" and names[296] == "MOON_SE00002"
                assert b.find("moon_se00013") == 297
            elif bank == "vag":
                assert names[0] == "dummy" and names[1] == "moon_se00001_L" and names[18] == "moon_se00014_R"
            elif bank == "voice":
                assert names[:3] == ["DU", "DU", "A00_"] and names[16463] == "M12_"
                assert b.find("A00_06_00_0029_0,00726") == 0x726
                with pytest.raises(AudioError):
                    b.find("B40_06_00_0012_0,00726")
            elif bank == "bgm":
                assert set(names) == {None}


@needs_game
def test_bgm_headers_all() -> None:
    whole = inner = 0
    with AudioBank("bgm") as b:
        for e in b:
            assert (e.codec, e.channels, e.sample_rate, e.delay) == ("atrac9", 2, 48000, 256)
            assert e.wave is not None and e.wave.decoded_samples >= e.num_samples + e.delay
            assert e.wave.decoded_samples - (e.num_samples + e.delay) < 1024
            assert e.loop is not None and 0 <= e.loop.start <= e.loop.end < e.num_samples
            whole += e.loop.whole
            inner += not e.loop.whole
        assert b.entry(0).num_samples == 96000 and b.entry(0).wave.config.raw.hex() == "fe7409f0"  # type: ignore[union-attr]
        assert b.entry(33).loop == LoopInfo(962000, 8257999, False)
        assert b.entry(50).loop == LoopInfo(772000, 7299999, False)
        assert b.entry(2).loop == LoopInfo(0, 7328753, False)
    assert (whole, inner) == (14, 118)


@needs_game
def test_vse_headers_all() -> None:
    mono = whole = 0
    with AudioBank("vse") as b:
        for e in b:
            assert e.codec == "atrac9" and e.sample_rate == 48000 and e.delay == 256 and e.loop is not None
            assert e.wave is not None and e.wave.channel_mask == (4 if e.channels == 1 else 3)
            mono += e.channels == 1
            whole += e.loop.whole
    assert (mono, whole) == (11, 1131)


@needs_game
def test_voice_headers_subset() -> None:
    with AudioBank("voice") as b:
        idx = list(range(0, len(b), 97)) + [7850, 3028, 16462, 16463]
        for i in idx:
            e = b.entry(i)
            assert e.lv is not None and e.lv.payload == b"\x00\x09\x00" and e.loop is None
            assert e.wave is not None and e.wave.chunks[0][0] == "fmt " and len(e.wave.chunks) == 3
            assert e.channels == (2 if i >= 16462 else 1)
        assert b.entry(2).num_samples == 284608


@needs_game
def test_se_names_real() -> None:
    names = load_se_names()
    assert (len(names.s0), len(names.s1), len(names.s2), len(names.s3)) == (8, 2, 1327, 67)
    assert se_lookup(names, "FATE_SE001") == (2, 1)
    assert se_lookup(names, "moon_se00013", flags=1) == (2, 297)
    assert se_lookup(names, "S03") == (0, 3)
    assert se_lookup(names, "SE_A0018") == (2, 10000)
    assert se_lookup(names, "S03", flags=1) is None


@needs_game
def test_vag_decode_all() -> None:
    with AudioBank("vag") as b:
        for i in range(len(b)):
            e = b.entry(i)
            assert e.vag is not None and e.vag.version == 0x00020001 and e.sample_rate == 48000
            fl = e.extra["flags_nonzero"]
            if i == 0:   # "dummy": 124 zero frames, flags 1, 1, 7, 7 at the end
                assert fl == [(120, 1), (121, 1), (122, 7), (123, 7)] and e.num_samples == 122 * 28
            else:        # frames: zero frame, data ..., flag 1 (last data), flag 7 (end marker)
                assert fl == [(e.vag.frames - 2, 1), (e.vag.frames - 1, 7)]
                assert e.num_samples == (e.vag.frames - 1) * 28
            pcm = b.decode(i)
            assert pcm.frames == e.num_samples
            peak = int(np.abs(pcm.samples).max())
            if i == 0:
                assert peak == 0
            else:
                assert 4000 < peak < 32767
        raw = b.stream(1)
        assert np.array_equal(H.decode_vag(raw, use_numba=False), H.decode_vag(raw, use_numba=True)) or not H.HAVE_NUMBA


@needs_game
@needs_ffmpeg
def test_decode_at9_real() -> None:
    with AudioBank("bgm") as b:
        raw = decode_at9(b.stream(0), trim=False)
        assert raw.samples.shape == (96256, 2) and not raw.samples.any()
        assert raw.loop == LoopInfo(256, 96255, True)
    with AudioBank("voice") as b:
        p = b.decode(2)
        assert p.samples.shape == (284608, 1)
        rms = float(np.sqrt(np.mean(p.samples.astype(np.float64) ** 2)))
        assert 3000 < rms < 4000                     # -19.5 dBFS measured
    with AudioBank("vse") as b:
        p = b.decode(296)
        assert p.samples.shape[1] == 2 and p.frames == b.entry(296).num_samples
        f = b.decode(296, float_output=True)
        assert f.samples.dtype == np.float32 and f.samples.shape == p.samples.shape
        assert np.abs(f.samples * 32768.0 - p.samples).max() <= 1.0


@needs_game
@needs_ffmpeg
def test_hevag_matches_atrac9_copy() -> None:
    """vag moon_se00002_L/_R and vse MOON_SE00002 are the same sound in two
    codecs: same level, same 10 ms envelope."""
    with AudioBank("vag") as v, AudioBank("vse") as s:
        L = v.decode(3).samples[:, 0].astype(np.float64)
        A = s.decode(296).samples[:, 0].astype(np.float64)
    n = min(len(L), len(A)) // 480 * 480
    eL = np.sqrt((L[:n].reshape(-1, 480) ** 2).mean(1))
    eA = np.sqrt((A[:n].reshape(-1, 480) ** 2).mean(1))
    assert np.corrcoef(eL, eA)[0, 1] > 0.99
    assert abs(20 * np.log10(eL.mean() / eA.mean())) < 0.5
