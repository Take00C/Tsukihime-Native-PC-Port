"""MZX0 decompressor, the "LV" voice wrapper, and a literal-only compressor.

MZX is a tiny LZ-style codec that works on 16-bit *words* (pairs of bytes).
The command set was learned from mangetsu (MIT, src/data/mzx.cpp) and deepLuna
(BSD-2, luna/mzx.py); the "clear counter" reset rule is NOT implemented by
either of them but is required for correct output on PS4 data.  It was found
empirically (visual + statistical tests on image tiles) and then confirmed in
the game's own decoder (eboot.bin text VA 0xE7080), see
docs/formats/containers.md section 5.  This is an independent implementation.

Stream layout (all little-endian)::

    0x00  char[4]  magic  "MZX0"
    0x04  u32      decompressed size in bytes
    0x08  ...      command stream

Each command starts with one byte ``c``: ``cmd = c & 3``, ``n = (c >> 2) + 1``
(so 1..64 words).

* cmd 0 RLE      : emit the current ``last`` word n times.
* cmd 1 BACKREF  : read one more byte ``d``; copy n words starting
                   ``2*(d+1)`` bytes back in the output (may overlap);
                   ``last`` becomes the final copied word.
* cmd 2 RINGBUF  : emit ring[c >> 2] (exactly one word); ``last`` = that word.
* cmd 3 LITERAL  : copy n words (2n bytes) from the input (each byte XOR 0xFF
                   when the stream is *inverted*); every word is also stored in
                   a 64-slot ring buffer (write index wraps at 64);
                   ``last`` = final word.

Reset rule ("clear counter"): a counter starts at 0.  Before decoding each
command, if counter <= 0 then counter = 0x1000 and ``last`` = INIT.
After reading the command byte, counter -= 1 for RINGBUF, else counter -= n.
(The ring buffer is NOT reset.)

INIT is 0x0000 for plain streams and 0xFFFF for inverted streams, both at the
start AND at every reset.  With that choice ``decompress(d, invert=True)`` is
exactly ``bytes(b ^ 0xFF for b in decompress(d))`` (every state variable is the
XOR image of the plain decoder's), which is what the engine does: it only
decodes plain streams and XORs script output afterwards.  The engine leaves
the ring uninitialised; no retail stream reads a slot before writing it.  The
encoder never lets a command cross a 0x1000-word boundary, so the reset happens
exactly at every 0x2000-byte output boundary.

Inversion is NOT signalled in the stream; it depends on the consumer:
  * script bytecode sections of allscr.mrg : inverted (invert=True)
  * image tile sections inside MZP images  : plain    (invert=False)
  * voice.mrg "LV" entries                 : plain    (invert=False)

Output overrun: the decoder (like the engine) only tests "output full?"
*between* commands, and the encoder emits the final command in full.  The last
command therefore routinely writes past the declared size, by up to
MAX_OVERSHOOT = 127 bytes (63 words + 1 byte for an odd size; the retail
maximum is 127, in allscr.mrg); the surplus is dropped.  A port must allocate
``size + 128`` bytes (or clamp writes while still consuming the whole command).
On all PS4 data the stream is fully consumed when the output is full (the
remaining bytes of the section are container padding).

LV wrapper (voice.mrg / voice2.mrg entries, engine VA 0x8BF8)::

    0x00  char     'L'  (the engine tests only this byte; byte 1 is 'V')
    0x02  u16      n    (payload length; the engine copies min(n, 600) + 4
                         bytes but always skips 4 + n)
    0x04  n bytes  payload (every retail entry: n = 3, payload 00 09 00)
    4+n   ...      'M' -> MZX0 stream (plain); anything else -> raw data

A numba-JIT kernel is used when numba is importable (set TSUKI_NO_NUMBA=1 to
force the pure-Python path); both produce identical output.
"""
from __future__ import annotations

import os
import struct
from dataclasses import dataclass
from typing import Optional, Union

__all__ = [
    "MZX_MAGIC",
    "MzxError",
    "MzxResult",
    "is_mzx",
    "mzx_size",
    "decompress",
    "decompress_ex",
    "compress_literal",
    "LvHeader",
    "LvDecoded",
    "parse_lv",
    "decompress_lv",
    "MAX_OVERSHOOT",
    "HAVE_NUMBA",
]

MZX_MAGIC = b"MZX0"
HEADER_SIZE = 8
RESET_WORDS = 0x1000
MAX_COMMAND_BYTES = 128          # one command emits at most 64 words
MAX_OVERSHOOT = MAX_COMMAND_BYTES - 1   # bytes written past an odd declared size
_OUT_SLACK = MAX_COMMAND_BYTES + 2      # output buffer = target + this
LV_MAX_COPY = 600                # engine clamps the LV payload copy to 0x258 bytes

Buffer = Union[bytes, bytearray, memoryview]

_XOR_FF = bytes(i ^ 0xFF for i in range(256))


class MzxError(ValueError):
    """Raised for malformed or truncated MZX streams."""


@dataclass(frozen=True)
class MzxResult:
    data: bytes
    consumed: int          # bytes of the input stream consumed, header included
    commands: int          # number of commands executed
    overshoot: int         # bytes the last command wrote past the requested size (0..127)


def is_mzx(data: Buffer) -> bool:
    return len(data) >= HEADER_SIZE and bytes(data[:4]) == MZX_MAGIC


def mzx_size(data: Buffer) -> int:
    """Declared decompressed size of an MZX stream."""
    if not is_mzx(data):
        raise MzxError("not an MZX0 stream")
    return struct.unpack_from("<I", data, 4)[0]


# ---------------------------------------------------------------------------
# pure Python implementation
# ---------------------------------------------------------------------------

def _decode_py(data: bytes, target: int, invert: bool, reset: bool) -> tuple[bytearray, int, int, int]:
    """Returns (out, op, pos, ncmd); ``out`` may be longer than ``target``."""
    out = bytearray(target + _OUT_SLACK)    # the last command may overrun by <= 127 bytes
    init = b"\xff\xff" if invert else b"\x00\x00"
    ring = bytearray(init * 64)
    rpos = 0
    pos = HEADER_SIZE
    op = 0
    n_in = len(data)
    ncmd = 0
    clear = 0
    # ``last`` is always the last word written to ``out`` unless a reset (or the
    # start of the stream) happened since, in which case it is ``init``.
    last_init = True
    xor = _XOR_FF
    while op < target:
        if pos >= n_in:
            raise MzxError(f"input exhausted at output {op}/{target}")
        c = data[pos]
        pos += 1
        cmd = c & 3
        n = (c >> 2) + 1
        ncmd += 1
        if reset:
            if clear <= 0:
                clear = RESET_WORDS
                last_init = True
            clear -= 1 if cmd == 2 else n
        if cmd == 3:  # literal
            end = pos + 2 * n
            if end > n_in:
                raise MzxError(f"literal runs past end of input at {pos}")
            chunk = data[pos:end]
            if invert:
                chunk = chunk.translate(xor)
            pos = end
            out[op:op + 2 * n] = chunk
            op += 2 * n
            first = 64 - rpos
            if n <= first:
                ring[2 * rpos:2 * (rpos + n)] = chunk
            else:
                ring[2 * rpos:128] = chunk[:2 * first]
                ring[0:2 * (n - first)] = chunk[2 * first:]
            rpos = (rpos + n) & 63
            last_init = False
        elif cmd == 1:  # back reference
            if pos >= n_in:
                raise MzxError("backref distance byte missing")
            dist = 2 * (data[pos] + 1)
            pos += 1
            src = op - dist
            if src < 0:
                raise MzxError(f"backref before start of output (op={op}, dist={dist})")
            length = 2 * n
            if dist >= length:
                out[op:op + length] = out[src:src + length]
            else:
                pat = bytes(out[src:op])
                out[op:op + length] = (pat * (length // dist + 1))[:length]
            op += length
            last_init = False
        elif cmd == 0:  # RLE of last word
            if last_init or op == 0:
                out[op:op + 2 * n] = init * n
            else:
                out[op:op + 2 * n] = out[op - 2:op] * n
            op += 2 * n
        else:  # ring buffer
            i2 = 2 * (c >> 2)
            out[op:op + 2] = ring[i2:i2 + 2]
            op += 2
            last_init = False
    return out, op, pos, ncmd


# ---------------------------------------------------------------------------
# optional numba kernel
# ---------------------------------------------------------------------------

HAVE_NUMBA = False
_decode_nb = None
if os.environ.get("TSUKI_NO_NUMBA", "") not in ("1", "true", "yes"):
    try:  # pragma: no cover - depends on environment
        import numpy as _np
        import numba as _numba

        @_numba.njit(cache=True, nogil=True)
        def _decode_nb_kernel(data, out, target, invert, reset):  # type: ignore[no-untyped-def]
            # returns (op, pos, ncmd, err) ; err: 0 ok, 1 input exhausted,
            # 2 literal past end, 3 backref distance missing, 4 backref before start
            init = 0xFF if invert else 0x00
            ring = _np.empty(128, _np.uint8)
            for k in range(128):
                ring[k] = init
            rpos = 0
            pos = 8
            op = 0
            n_in = data.shape[0]
            ncmd = 0
            clear = 0
            l0 = init
            l1 = init
            x = 0xFF if invert else 0x00
            while op < target:
                if pos >= n_in:
                    return op, pos, ncmd, 1
                c = data[pos]
                pos += 1
                cmd = c & 3
                n = (c >> 2) + 1
                ncmd += 1
                if reset:
                    if clear <= 0:
                        clear = 0x1000
                        l0 = init
                        l1 = init
                    if cmd == 2:
                        clear -= 1
                    else:
                        clear -= n
                if cmd == 3:
                    if pos + 2 * n > n_in:
                        return op, pos, ncmd, 2
                    for k in range(n):
                        l0 = data[pos] ^ x
                        l1 = data[pos + 1] ^ x
                        pos += 2
                        out[op] = l0
                        out[op + 1] = l1
                        op += 2
                        ring[2 * rpos] = l0
                        ring[2 * rpos + 1] = l1
                        rpos = (rpos + 1) & 63
                elif cmd == 1:
                    if pos >= n_in:
                        return op, pos, ncmd, 3
                    dist = 2 * (data[pos] + 1)
                    pos += 1
                    if op - dist < 0:
                        return op, pos, ncmd, 4
                    for k in range(n):
                        l0 = out[op - dist]
                        l1 = out[op - dist + 1]
                        out[op] = l0
                        out[op + 1] = l1
                        op += 2
                elif cmd == 0:
                    for k in range(n):
                        out[op] = l0
                        out[op + 1] = l1
                        op += 2
                else:
                    i2 = 2 * (c >> 2)
                    l0 = ring[i2]
                    l1 = ring[i2 + 1]
                    out[op] = l0
                    out[op + 1] = l1
                    op += 2
            return op, pos, ncmd, 0

        def _decode_nb(data: bytes, target: int, invert: bool, reset: bool) -> tuple[bytearray, int, int, int]:
            src = _np.frombuffer(data, dtype=_np.uint8)
            buf = bytearray(target + _OUT_SLACK)
            out = _np.frombuffer(buf, dtype=_np.uint8)
            op, pos, ncmd, err = _decode_nb_kernel(src, out, target, invert, reset)
            if err:
                msg = {1: "input exhausted", 2: "literal runs past end of input",
                       3: "backref distance byte missing",
                       4: "backref before start of output"}[int(err)]
                raise MzxError(f"{msg} (op={op}, pos={pos})")
            return buf, int(op), int(pos), int(ncmd)

        HAVE_NUMBA = True
    except ImportError:
        HAVE_NUMBA = False


# ---------------------------------------------------------------------------
# public API
# ---------------------------------------------------------------------------

def decompress(data: Buffer, invert: bool = False, *, max_output: int | None = None,
               reset: bool = True, use_numba: bool | None = None) -> bytes:
    """Decompress an MZX0 stream and return exactly the declared number of bytes.

    ``max_output`` stops early (returning only that many bytes), handy to peek at
    the start of large streams.  ``reset`` selects the clear-counter rule (must
    be True for correct PS4 output; False reproduces mangetsu/deepLuna).
    """
    return decompress_ex(data, invert, max_output=max_output, reset=reset,
                         use_numba=use_numba).data


def decompress_ex(data: Buffer, invert: bool = False, *, max_output: int | None = None,
                  reset: bool = True, use_numba: bool | None = None) -> MzxResult:
    if not isinstance(data, bytes):
        data = bytes(data)
    if len(data) < HEADER_SIZE or data[:4] != MZX_MAGIC:
        raise MzxError("not an MZX0 stream")
    size = struct.unpack_from("<I", data, 4)[0]
    target = size if max_output is None else min(size, max_output)
    if use_numba is None:
        use_numba = HAVE_NUMBA and target >= 4096
    if use_numba and _decode_nb is not None:
        out, op, pos, ncmd = _decode_nb(data, target, invert, reset)
    else:
        out, op, pos, ncmd = _decode_py(data, target, invert, reset)
    overshoot = max(0, op - target)
    del out[target:]
    return MzxResult(bytes(out), pos, ncmd, overshoot)


# ---------------------------------------------------------------------------
# "LV" voice wrapper (voice.mrg / voice2.mrg entries)
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class LvHeader:
    """Optional 4+n byte chunk in front of a voice entry's audio stream."""
    present: bool          # byte 0 == 'L' (the only byte the engine tests)
    magic: bytes           # bytes 0..1 as stored (b"LV" on all retail entries)
    length: int            # n = u16 at +2 (0 when absent)
    payload: bytes         # the n payload bytes (b"\x00\x09\x00" on all retail entries)
    body_offset: int       # where the audio stream starts: 4 + n, or 0 when absent

    @property
    def engine_copy_len(self) -> int:
        """Bytes the engine copies to its global LV buffer (header included)."""
        return 4 + min(self.length, LV_MAX_COPY) if self.present else 0


@dataclass(frozen=True)
class LvDecoded:
    header: LvHeader
    data: bytes            # decoded audio (RIFF/WAVE ATRAC9 on retail data)
    compressed: bool       # body started with 'M' and was MZX-decoded
    consumed: int          # entry bytes used: body_offset + MZX bytes consumed
                           # (= len(entry) for a raw body)
    mzx: Optional[MzxResult]


def parse_lv(data: Buffer) -> LvHeader:
    """Parse the LV chunk the way the engine does (eboot VA 0x8BF8)."""
    if len(data) < 1 or data[0] != 0x4C:  # 'L'
        return LvHeader(False, b"", 0, b"", 0)
    if len(data) < 4:
        raise MzxError("LV chunk shorter than its 4-byte header")
    n = struct.unpack_from("<H", data, 2)[0]
    if 4 + n > len(data):
        raise MzxError(f"LV payload length {n} runs past the entry ({len(data)} bytes)")
    return LvHeader(True, bytes(data[:2]), n, bytes(data[4:4 + n]), 4 + n)


def decompress_lv(data: Buffer, *, use_numba: bool | None = None) -> LvDecoded:
    """Decode one voice entry: optional LV chunk, then an MZX0 stream if the
    next byte is 'M', otherwise the remaining bytes are returned raw (engine
    behaviour).  Pass the entry's whole sector span; no exact size is stored
    in HED4, ``consumed`` gives it."""
    hdr = parse_lv(data)
    body = memoryview(data)[hdr.body_offset:]
    if len(body) >= 1 and body[0] == 0x4D:  # 'M'
        res = decompress_ex(body, False, use_numba=use_numba)
        return LvDecoded(hdr, res.data, True, hdr.body_offset + res.consumed, res)
    return LvDecoded(hdr, bytes(body), False, len(data), None)


def compress_literal(raw: bytes, invert: bool = False) -> bytes:
    """Encode ``raw`` as a valid (non-compressing) MZX0 stream of literals.

    Useful for tests and repacking.  Never emits RLE, so the reset rule is
    irrelevant to it.  Output is ~0.8% larger than the input.
    """
    out = bytearray(MZX_MAGIC + struct.pack("<I", len(raw)))
    padded = raw + (b"\x00" if len(raw) & 1 else b"")
    for i in range(0, len(padded), 128):
        chunk = padded[i:i + 128]
        out.append(3 | ((len(chunk) // 2 - 1) << 2))
        out += chunk.translate(_XOR_FF) if invert else chunk
    return bytes(out)


def _main(argv: list[str] | None = None) -> int:
    import argparse
    import time

    ap = argparse.ArgumentParser(prog="python -m tsuki.mzx",
                                 description="Decompress a raw MZX0 stream from a file")
    ap.add_argument("input")
    ap.add_argument("output", nargs="?")
    ap.add_argument("--invert", action="store_true",
                    help="stream is XOR-0xFF inverted (allscr script sections)")
    ap.add_argument("--offset", type=lambda s: int(s, 0), default=0,
                    help="byte offset of the MZX0 header inside the input file")
    ap.add_argument("--no-reset", action="store_true",
                    help="disable the 0x1000-word clear counter (mangetsu behaviour)")
    ap.add_argument("--python", action="store_true", help="force the pure-Python decoder")
    ap.add_argument("--lv", action="store_true",
                    help="the offset points at a voice entry: skip its 'LV' chunk first")
    args = ap.parse_intermixed_args(argv)
    with open(args.input, "rb") as f:
        f.seek(args.offset)
        if args.lv:
            lvh = f.read(4)
            hdr = parse_lv(lvh + f.read(struct.unpack_from("<H", lvh, 2)[0] if lvh[:1] == b"L" else 0))
            print(f"LV: present={hdr.present} n={hdr.length} payload={hdr.payload.hex()} "
                  f"body at +{hdr.body_offset}")
            f.seek(args.offset + hdr.body_offset)
        head = f.read(HEADER_SIZE)
        declared = mzx_size(head)
        # worst case (all literals) is 129/128 of the output plus the header
        data = head + f.read(declared + declared // 64 + 1024)
    t = time.perf_counter()
    res = decompress_ex(data, args.invert, reset=not args.no_reset,
                        use_numba=False if args.python else None)
    dt = time.perf_counter() - t
    print(f"size={len(res.data)} consumed={res.consumed} commands={res.commands} "
          f"overshoot={res.overshoot} "
          f"time={dt * 1000:.1f}ms ({len(res.data) / max(dt, 1e-9) / 1e6:.1f} MB/s)")
    if args.output:
        with open(args.output, "wb") as f:
            f.write(res.data)
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
