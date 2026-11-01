"""Malformed counts must fail before allocation or unbounded iteration."""
from __future__ import annotations

import struct
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from tsuki import audio, mzx  # noqa: E402


@pytest.mark.parametrize("use_numba", [False, True])
@pytest.mark.parametrize("size", [4096, 0xFFFFFFFF])
def test_mzx_impossible_output_is_rejected_before_allocation(monkeypatch, use_numba, size):
    def unexpected_decode(*args):
        pytest.fail("an impossible output size reached the allocator")

    monkeypatch.setattr(mzx, "_decode_py", unexpected_decode)
    monkeypatch.setattr(mzx, "_decode_nb", unexpected_decode)
    with pytest.raises(mzx.MzxError, match="cannot come from"):
        mzx.decompress(b"MZX0" + struct.pack("<I", size) + b"\xFC", use_numba=use_numba)


@pytest.mark.parametrize("use_numba", [False, True])
def test_mzx_size_bound_preserves_maximum_expansion_and_peeking(use_numba):
    stream = b"MZX0" + struct.pack("<I", 128) + b"\xFC"
    assert mzx.decompress(stream, use_numba=use_numba) == bytes(128)
    stream = b"MZX0" + struct.pack("<I", 0xFFFFFFFF) + b"\xFC"
    assert mzx.decompress(stream, max_output=16, use_numba=use_numba) == bytes(16)
    with pytest.raises(mzx.MzxError, match="negative"):
        mzx.decompress(stream, max_output=-1, use_numba=use_numba)


def _riff(cid, body, size=None):
    chunk = cid + struct.pack("<I", len(body) if size is None else size) + body
    return b"RIFF" + struct.pack("<I", len(chunk) + 4) + b"WAVE" + chunk


@pytest.mark.parametrize("size", [1024, 0x7FFFFFFC, 0xFFFFFFFC])
def test_riff_truncated_fact_is_rejected_before_unpacking(monkeypatch, size):
    unpack = struct.unpack_from

    def guarded_unpack(fmt, *args):
        if fmt not in ("<I", "<HHIIHHHHI16sI4s"):
            pytest.fail("an unbounded fact count reached struct.unpack_from")
        return unpack(fmt, *args)

    monkeypatch.setattr(audio.struct, "unpack_from", guarded_unpack)
    with pytest.raises(audio.AudioError, match="truncated"):
        audio.parse_wave(_riff(b"fact", b"", size))


def test_riff_huge_loop_count_is_rejected_before_iteration(monkeypatch):
    def guarded_range(count):
        if count > 1:
            pytest.fail("an unbounded smpl count reached range")
        return range(count)

    monkeypatch.setattr(audio, "range", guarded_range, raising=False)
    smpl = struct.pack("<9I", 0, 0, 0, 0, 0, 0, 0, 0xFFFFFFFF, 0)
    with pytest.raises(audio.AudioError, match="loop count"):
        audio.parse_wave(_riff(b"smpl", smpl))


@pytest.mark.parametrize("cid,body", [(b"fact", b"123"), (b"smpl", b"1234")])
def test_riff_partial_records_fail_as_audio_errors(cid, body):
    with pytest.raises(audio.AudioError):
        audio.parse_wave(_riff(cid, body))
