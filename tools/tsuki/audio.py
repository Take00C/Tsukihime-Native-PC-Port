"""Audio banks of TSUKIHIME -A piece of blue glass moon- (PS4): reference reader.

Banks (all under ``image/``; containers are read with tsuki.mrg / tsuki.mzx):

======== ============ ======= ============================== ==========================
bank     container    entries payload                        names
======== ============ ======= ============================== ==========================
bgm      HED8 + MRG   132     RIFF/WAVE ATRAC9, stereo       none (script number = index)
vse      HED8 + MRG   1327    RIFF/WAVE ATRAC9 (1316 st/11 m) se_nam.mrg section 2
vag      HED8 + MRG   19      VAGp HE-VAG ADPCM, mono        name inside the VAG header
voice    HED4+NAM8    16464   "LV" + MZX0 -> RIFF ATRAC9 mono voice.nam = speaker prefix
voice2   HED4+NAM8    2       same as voice entries 0-1      "DU" dummies
======== ============ ======= ============================== ==========================

ATRAC9 RIFF (bgm/vse/voice): ``fmt `` (0x34 bytes, WAVE_FORMAT_EXTENSIBLE, ATRAC9
SubFormat GUID, 4-byte ATRAC9 config word), ``fact`` (12 bytes: sample count,
256, 256 = encoder delay), ``smpl`` (60 bytes, one loop; bgm/vse only), ``data``.
A decoder (ffmpeg's ``atrac9``) outputs the encoder delay as leading samples;
the playable sound is ``decoded[delay : delay + fact.samples]`` and the loop is
``[smpl.start - delay, smpl.end - delay]`` (end inclusive) in those
coordinates; the engine computes exactly these values (eboot text VA 0x1130B0,
0x10FCB0).

HE-VAG (vag): see tsuki.audio_hevag (own decoder, no system library needed).

The format documentation is docs/formats/audio.md.

CLI::

    python -m tsuki.audio list  <bank> [--limit N]
    python -m tsuki.audio info  <bank> <index|name>
    python -m tsuki.audio extract <bank> <index|name> <out.at9|out.vag>
    python -m tsuki.audio decode  <bank> <index|name> <out.wav> [--raw] [--loops N] [--seconds S]
    python -m tsuki.audio census  <bank> [--decode] [--limit N] [--step K]
"""
from __future__ import annotations

import io
import os
import shutil
import struct
import subprocess
import wave
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterator, Optional, Union

import numpy as np

from . import image_dir as _default_image_dir
from .audio_hevag import (FRAME_BYTES as VAG_FRAME_BYTES, FRAME_SAMPLES as VAG_FRAME_SAMPLES,
                          VAG_HEADER_SIZE, VagHeader, decode_vag, parse_vag)
from .mrg import MrgArchive
from .mzp import MzpArchive
from .mzx import LvHeader, decompress_ex, decompress_lv, parse_lv
from .nam import parse_nam32

__all__ = [
    "AudioError", "BANKS", "ATRAC9_GUID", "Atrac9Config", "parse_atrac9_config",
    "SmplLoop", "WaveInfo", "parse_wave", "LoopInfo", "AudioEntry", "AudioBank",
    "Pcm", "decode_at9", "decode_vag_pcm", "render_loop", "write_wav",
    "SeNames", "load_se_names", "se_lookup", "parse_voice_ref", "bgm_index",
    "ffmpeg_path", "census",
]

Buffer = Union[bytes, bytearray, memoryview]

BANKS = ("bgm", "vse", "vag", "voice", "voice2")
ATRAC9_GUID = bytes.fromhex("d242e147ba368d4d88fc61654f8c836c")   # {47E142D2-36BA-4D8D-88FC-61654F8C836C}
WAVE_FORMAT_EXTENSIBLE = 0xFFFE

# ATRAC9 config tables (sample-rate index -> Hz / log2 samples per frame;
# channel-config index -> channels).  Same tables as ffmpeg's atrac9dec /
# vgmstream; only rate index 7 (48 kHz), channel configs 0 (mono) and 2
# (stereo), 4 frames per superframe occur in this game.
AT9_SAMPLE_RATES = (11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000,
                    44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000)
AT9_FRAME_LOG2 = (6, 6, 7, 7, 7, 8, 8, 8, 6, 6, 7, 7, 7, 8, 8, 8)
AT9_CHANNELS = (1, 2, 2, 6, 8, 4, 0, 0)

# Engine BGM remap (eboot text VA 0x9918..0x9958): if the u16 setting at
# +0x1E of the global at data VA 0x66C578 is 1 (default 0) and 1 <= n <= 131,
# track n is played from entry n + 131.  bgm.hed has only 132 entries, so on the
# retail PS4 data this alternate set does not exist.
BGM_ALT_OFFSET = 0x83


class AudioError(ValueError):
    pass


# ---------------------------------------------------------------------------
# ATRAC9 RIFF
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class Atrac9Config:
    """The 4-byte ATRAC9 configuration word (big-endian bit fields)."""
    raw: bytes
    sync: int                    # bits 31..24, always 0xFE
    sample_rate_index: int       # bits 23..20
    channel_config_index: int    # bits 19..17
    validation: int              # bit 16, must be 0
    frame_bytes: int             # bits 15..5, stored minus 1
    superframe_index: int        # bits 4..3, frames per superframe = 1 << index
    unused: int                  # bits 2..0

    @property
    def sample_rate(self) -> int:
        return AT9_SAMPLE_RATES[self.sample_rate_index]

    @property
    def channels(self) -> int:
        return AT9_CHANNELS[self.channel_config_index]

    @property
    def frames_per_superframe(self) -> int:
        return 1 << self.superframe_index

    @property
    def samples_per_frame(self) -> int:
        return 1 << AT9_FRAME_LOG2[self.sample_rate_index]

    @property
    def superframe_bytes(self) -> int:
        return self.frame_bytes * self.frames_per_superframe

    @property
    def samples_per_superframe(self) -> int:
        return self.samples_per_frame * self.frames_per_superframe


def parse_atrac9_config(raw: Buffer) -> Atrac9Config:
    b = bytes(raw[:4])
    if len(b) != 4:
        raise AudioError("ATRAC9 config word needs 4 bytes")
    c = int.from_bytes(b, "big")
    cfg = Atrac9Config(b, c >> 24, (c >> 20) & 0xF, (c >> 17) & 7, (c >> 16) & 1,
                       ((c >> 5) & 0x7FF) + 1, (c >> 3) & 3, c & 7)
    if cfg.sync != 0xFE:
        raise AudioError(f"ATRAC9 config sync byte 0x{cfg.sync:02x} != 0xFE")
    if cfg.validation:
        raise AudioError("ATRAC9 config validation bit set")
    if cfg.channels == 0:
        raise AudioError(f"ATRAC9 channel config index {cfg.channel_config_index} is reserved")
    return cfg


@dataclass(frozen=True)
class SmplLoop:
    """One 24-byte loop record of a RIFF ``smpl`` chunk, as stored."""
    cue_id: int
    type: int
    start: int                   # sample index incl. the encoder delay
    end: int                     # inclusive, incl. the encoder delay
    fraction: int
    play_count: int


@dataclass(frozen=True)
class LoopInfo:
    """Loop region in *playable* sample coordinates (encoder delay removed)."""
    start: int                   # first sample of the loop body
    end: int                     # last sample of the loop body (inclusive)
    whole: bool                  # True if the loop covers the whole sound (the
                                 # at9tool default written into most files)

    @property
    def end_exclusive(self) -> int:
        return self.end + 1

    @property
    def length(self) -> int:
        return self.end - self.start + 1


@dataclass(frozen=True)
class WaveInfo:
    """Parsed RIFF/WAVE ATRAC9 header."""
    riff_size: int               # RIFF size field + 8 (= total stream bytes)
    format_tag: int
    channels: int
    sample_rate: int
    avg_bytes_per_sec: int
    block_align: int             # = superframe bytes
    bits_per_sample: int
    cb_size: int
    samples_per_block: int       # wValidBitsPerSample/wSamplesPerBlock: 1024
    channel_mask: int            # 4 (front centre) for mono, 3 (FL|FR) for stereo
    subformat: bytes
    version: int                 # dwVersionInfo, 1
    config: Atrac9Config
    fact: tuple[int, ...]        # (samples, input delay?, encoder delay) = (n, 256, 256)
    smpl_header: Optional[tuple[int, ...]]   # 9 u32 (manufacturer .. sampler data)
    loops: tuple[SmplLoop, ...]
    data_offset: int
    data_size: int
    chunks: tuple[tuple[str, int, int], ...]  # (id, offset, size) in file order

    @property
    def num_samples(self) -> int:
        return self.fact[0]

    @property
    def delay(self) -> int:
        """Encoder delay in samples (fact[2]; the engine subtracts this value
        from the smpl loop points, VA 0x113256)."""
        return self.fact[2] if len(self.fact) >= 3 else 0

    @property
    def superframes(self) -> int:
        return self.data_size // self.block_align

    @property
    def decoded_samples(self) -> int:
        """Samples a decoder produces from all superframes (delay included)."""
        return self.superframes * self.samples_per_block

    @property
    def duration(self) -> float:
        return self.num_samples / self.sample_rate

    @property
    def loop(self) -> Optional[LoopInfo]:
        if not self.loops:
            return None
        lp = self.loops[0]
        s, e = lp.start - self.delay, lp.end - self.delay
        return LoopInfo(s, e, s == 0 and e == self.num_samples - 1)


def parse_wave(data: Buffer, *, strict: bool = True) -> WaveInfo:
    """Parse the RIFF header of an ATRAC9 WAVE stream (needs the bytes up to
    the ``data`` chunk header; 0xA0 bytes suffice for retail files)."""
    b = bytes(data[:0x400])
    if len(b) < 12 or b[:4] != b"RIFF" or b[8:12] != b"WAVE":
        raise AudioError("not a RIFF/WAVE stream")
    riff_size = struct.unpack_from("<I", b, 4)[0] + 8
    fmt: Optional[tuple[Any, ...]] = None
    fact: tuple[int, ...] = ()
    smpl_head: Optional[tuple[int, ...]] = None
    loops: list[SmplLoop] = []
    data_off = data_size = -1
    chunks: list[tuple[str, int, int]] = []
    o = 12
    while o + 8 <= len(b):
        cid = b[o:o + 4]
        size = struct.unpack_from("<I", b, o + 4)[0]
        chunks.append((cid.decode("latin1"), o, size))
        body = b[o + 8:o + 8 + size]
        if cid != b"data" and len(body) != size:
            raise AudioError(f"truncated {cid!r} chunk")
        if cid == b"fmt ":
            if size < 0x34 or len(body) < 0x34:
                raise AudioError(f"fmt chunk of {size} bytes is too short for ATRAC9")
            fmt = struct.unpack_from("<HHIIHHHHI16sI4s", body, 0)
        elif cid == b"fact":
            if size % 4:
                raise AudioError("fact chunk is not whole sample counts")
            fact = struct.unpack_from("<%dI" % (size // 4), body, 0)
        elif cid == b"smpl":
            if len(body) < 36:
                raise AudioError("smpl chunk is shorter than its header")
            smpl_head = struct.unpack_from("<9I", body, 0)
            if smpl_head[7] > (len(body) - 36) // 24:
                raise AudioError("smpl loop count exceeds the chunk")
            for k in range(smpl_head[7]):
                loops.append(SmplLoop(*struct.unpack_from("<6I", body, 36 + 24 * k)))
        elif cid == b"data":
            data_off, data_size = o + 8, size
            break
        o += 8 + size + (size & 1)
    if fmt is None:
        raise AudioError("no fmt chunk")
    tag, ch, rate, abps, align, bits, cb, spb, mask, guid, ver, cfgraw = fmt
    if tag != WAVE_FORMAT_EXTENSIBLE or guid != ATRAC9_GUID:
        raise AudioError(f"not ATRAC9 (format tag 0x{tag:04x}, subformat {guid.hex()})")
    if data_off < 0:
        raise AudioError("no data chunk in the header bytes")
    cfg = parse_atrac9_config(cfgraw)
    info = WaveInfo(riff_size, tag, ch, rate, abps, align, bits, cb, spb, mask, guid, ver, cfg,
                    tuple(fact), smpl_head, tuple(loops), data_off, data_size, tuple(chunks))
    if strict:
        problems = []
        if not fact:
            problems.append("no fact chunk")
        if cfg.channels != ch:
            problems.append(f"config says {cfg.channels} channels, fmt {ch}")
        if cfg.sample_rate != rate:
            problems.append(f"config rate {cfg.sample_rate} != fmt rate {rate}")
        if cfg.superframe_bytes != align:
            problems.append(f"superframe {cfg.superframe_bytes} B != block align {align}")
        if cfg.samples_per_superframe != spb:
            problems.append(f"superframe samples {cfg.samples_per_superframe} != {spb}")
        if align <= 0 or data_size % align:
            problems.append(f"data size {data_size} not a multiple of {align}")
        if problems:
            raise AudioError("; ".join(problems))
    return info


# ---------------------------------------------------------------------------
# entries / banks
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class AudioEntry:
    bank: str
    index: int
    name: Optional[str]
    codec: str                   # "atrac9" or "hevag"
    channels: int
    sample_rate: int
    num_samples: int             # playable samples (delay excluded; VAG: frames before the end marker * 28)
    delay: int                   # leading decoder samples to drop (ATRAC9: 256, VAG: 0)
    loop: Optional[LoopInfo]
    stream_size: int             # bytes of the decodable stream (RIFF or VAGp)
    archive_offset: int          # byte offset of the entry in <bank>.mrg
    wave: Optional[WaveInfo] = None
    vag: Optional[VagHeader] = None
    lv: Optional[LvHeader] = None
    extra: dict[str, Any] = field(default_factory=dict)

    @property
    def duration(self) -> float:
        return self.num_samples / self.sample_rate if self.sample_rate else 0.0


@dataclass(frozen=True)
class SeNames:
    """se_nam.mrg: four NAM32 sections.  The engine loads them at VA 0xCC730
    (slot 0x17): s0 -> 0x178DB98, s1 -> 0x178DBA0, s2 -> 0x178DBA8,
    s3 -> 0x178DBB0."""
    s0: tuple[str, ...]          # "S00".."S07": kind-0 names (purpose unknown)
    s1: tuple[str, ...]          # "RANKING", "NEKO02": not used by the lookup routine
    s2: tuple[str, ...]          # 1327 names, record i = vse.hed entry i
    s3: tuple[str, ...]          # 67 "SE_Axxxx": kind 2, index 10000 + i

    @property
    def vse(self) -> tuple[str, ...]:
        return self.s2


def load_se_names(img: Optional[Path] = None) -> SeNames:
    img = img or _default_image_dir()
    if img is None:
        raise AudioError("game image directory not found")
    arc = MzpArchive((img / "se_nam.mrg").read_bytes())
    if len(arc) != 4:
        raise AudioError(f"se_nam.mrg has {len(arc)} sections, expected 4")
    secs = [tuple(parse_nam32(arc.read(i)).names) for i in range(4)]
    return SeNames(*secs)


def _se_key(name: str) -> str:
    # engine VA 0xCC7AD..0xCC80F: strip everything up to the last '\\',
    # upper-case a..z, stop at '.' or after 28 characters
    base = name.rsplit("\\", 1)[-1]
    base = base.split(".", 1)[0][:28]
    return "".join(chr(ord(c) - 32) if "a" <= c <= "z" else c for c in base)


def se_lookup(names: SeNames, name: str, flags: int = 0) -> Optional[tuple[int, int]]:
    """Resolve a sound-effect name like the engine (eboot text VA 0xCC790).

    Returns (kind, index) or None.  Search order: unless ``flags & 1``,
    section 0 (max 256 records) -> kind 0, index i; unless ``flags & 2``,
    section 3 (max 81) -> kind 2, index 10000 + i; then section 2 (max 2770)
    -> kind 2, index i = vse.hed entry.  Kind 2 with index >= 10000 is played
    from section (index - 10000) of a separately registered mrgd00 container
    (pointer at data VA 0xDD8F30, set by VA 0x9FA0, which nothing in the retail
    eboot calls)."""
    key = _se_key(name)
    if not flags & 1:
        for i, n in enumerate(names.s0[:0x100]):
            if n.upper() == key:
                return 0, i
    if not flags & 2:
        for i, n in enumerate(names.s3[:0x51]):
            if n.upper() == key:
                return 2, 10000 + i
    for i, n in enumerate(names.s2[:0xAD2]):
        if n.upper() == key:
            return 2, i
    return None


def parse_voice_ref(args: str) -> tuple[str, int]:
    """Parse the argument text of a script ``_VPLY(name,hexindex)`` /
    ``_VPL2(...)`` call, e.g. ``"A00_06_00_0029_0,00726"`` -> ("A00_06_00_0029_0", 0x726).
    The hex number is the voice.hed entry index; voice.nam[index] is the
    first 4 characters of the name (verified on all 16,704 script calls)."""
    parts = [p.strip() for p in args.split(",")]
    if len(parts) < 2:
        raise AudioError(f"voice reference {args!r} has no index")
    return parts[0], int(parts[1], 16)


def bgm_index(track: int, alt_set: bool = False) -> int:
    """bgm.hed entry for script ``_MPLY(track, ...)`` (index = track; see
    BGM_ALT_OFFSET for the engine's alternate-set remap)."""
    if alt_set and 1 <= track <= BGM_ALT_OFFSET:
        return track + BGM_ALT_OFFSET
    return track


class AudioBank:
    """One audio bank (bgm, vse, vag, voice, voice2).

    >>> with AudioBank("bgm") as b:
    ...     e = b.entry(33); e.loop
    LoopInfo(start=962000, end=8257999, whole=False)
    """

    def __init__(self, bank: str, img: Optional[Path] = None) -> None:
        if bank not in BANKS:
            raise AudioError(f"unknown bank {bank!r}; expected one of {BANKS}")
        img = img or _default_image_dir()
        if img is None:
            raise AudioError("game image directory not found (set TSUKI_GAME_DIR)")
        self.bank = bank
        self.img = Path(img)
        self.arc = MrgArchive(self.img / bank, use_nam=bank in ("voice", "voice2"))
        self._names: list[Optional[str]]
        if bank == "vse":
            self.se_names: Optional[SeNames] = load_se_names(self.img)
            self._names = list(self.se_names.s2)
            if len(self._names) != len(self.arc):
                raise AudioError(f"se_nam.mrg section 2 has {len(self._names)} names for {len(self.arc)} vse entries")
        elif bank in ("voice", "voice2"):
            self.se_names = None
            self._names = [e.name for e in self.arc.entries]
        elif bank == "vag":
            self.se_names = None
            self._names = [parse_vag(self.arc.head(e.index, VAG_HEADER_SIZE), header_only=True).name if e.size >= VAG_HEADER_SIZE else None
                           for e in self.arc.entries]
        else:
            self.se_names = None
            self._names = [None] * len(self.arc)

    # -- lifecycle -----------------------------------------------------------
    def close(self) -> None:
        self.arc.close()

    def __enter__(self) -> "AudioBank":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def __len__(self) -> int:
        return len(self.arc)

    @property
    def codec(self) -> str:
        return "hevag" if self.bank == "vag" else "atrac9"

    @property
    def names(self) -> list[Optional[str]]:
        return list(self._names)

    def find(self, name: str) -> int:
        """Entry index by name (vse: se_nam section 2, vag: VAG header name;
        case-insensitive, first match).  Also accepts a decimal index or
        a voice reference ``NAME,HEX``."""
        if name.isdigit():
            return int(name)
        if self.bank in ("voice", "voice2") and "," in name:
            vname, idx = parse_voice_ref(name)
            pre = self._names[idx] or ""
            if not vname.startswith(pre):
                raise AudioError(f"voice {vname!r}: entry {idx} belongs to group {pre!r}")
            return idx
        key = name.upper()
        for i, n in enumerate(self._names):
            if n is not None and n.upper() == key:
                return i
        raise KeyError(f"{name!r} not found in bank {self.bank}")

    # -- data ----------------------------------------------------------------
    def stream(self, index: int) -> bytes:
        """The decodable stream of an entry: RIFF/WAVE ATRAC9 (bgm/vse/voice)
        or VAGp (vag).  Voice entries are LV-unwrapped and MZX-decompressed."""
        if self.bank in ("voice", "voice2"):
            return decompress_lv(self.arc.read_sectors(index)).data
        return self.arc.read(index)

    def _header_bytes(self, index: int) -> tuple[bytes, Optional[LvHeader], int]:
        if self.bank in ("voice", "voice2"):
            raw = self.arc.read_sectors(index)
            lv = parse_lv(raw)
            body = raw[lv.body_offset:]
            if body[:4] == b"MZX0":
                res = decompress_ex(body, max_output=0x100)
                size = struct.unpack_from("<I", body, 4)[0]
                return res.data, lv, size
            return body[:0x100], lv, len(body)
        e = self.arc.entries[index]
        return self.arc.head(index, 0x100), None, e.size

    def entry(self, index: int) -> AudioEntry:
        e = self.arc.entries[index]
        name = self._names[index]
        if self.bank == "vag":
            data = self.arc.read(index)
            hdr = parse_vag(data)
            body = data[VAG_HEADER_SIZE:VAG_HEADER_SIZE + hdr.data_size]
            nfr = hdr.frames
            flags = [body[f * VAG_FRAME_BYTES + 1] & 0x0F for f in range(nfr)]
            end = next((f for f, fl in enumerate(flags) if fl >= 7), nfr)
            loop = _vag_loop(flags)
            return AudioEntry(self.bank, index, name, "hevag", 1, hdr.sample_rate, end * VAG_FRAME_SAMPLES, 0,
                              loop, len(data), e.offset, vag=hdr,
                              extra={"flags_nonzero": [(f, fl) for f, fl in enumerate(flags) if fl]})
        head, lv, size = self._header_bytes(index)
        w = parse_wave(head)
        if w.riff_size != size:
            raise AudioError(f"{self.bank} #{index}: RIFF size {w.riff_size} != stream size {size}")
        return AudioEntry(self.bank, index, name, "atrac9", w.channels, w.sample_rate, w.num_samples,
                          w.delay, w.loop, size, e.offset, wave=w, lv=lv)

    def __iter__(self) -> Iterator[AudioEntry]:
        for i in range(len(self)):
            yield self.entry(i)

    def decode(self, index: int, *, trim: bool = True, ffmpeg: Optional[str] = None,
               float_output: bool = False) -> "Pcm":
        if self.bank == "vag":
            pcm = decode_vag_pcm(self.arc.read(index))
            if float_output:
                pcm.samples = pcm.samples.astype(np.float32) / 32768.0
            return pcm
        return decode_at9(self.stream(index), trim=trim, ffmpeg=ffmpeg, float_output=float_output)


def _vag_loop(flags: list[int]) -> Optional[LoopInfo]:
    """PS-ADPCM loop flags: bit 2 = loop start frame, bit 0 = loop end frame,
    bit 1 = repeat.  (No vag.mrg entry has a loop: flags are 0, then 1, 7.)"""
    start = next((f for f, fl in enumerate(flags) if fl & 4 and fl < 7), None)
    end = next((f for f, fl in enumerate(flags) if fl & 3 == 3 and fl < 7), None)
    if start is None or end is None or end < start:
        return None
    return LoopInfo(start * VAG_FRAME_SAMPLES, (end + 1) * VAG_FRAME_SAMPLES - 1, False)


# ---------------------------------------------------------------------------
# decoding
# ---------------------------------------------------------------------------

@dataclass
class Pcm:
    samples: np.ndarray          # int16 (or float32 with float_output), shape (n, channels)
    rate: int
    loop: Optional[LoopInfo]     # in the coordinates of ``samples``
    trimmed: bool                # encoder delay removed / cut to the playable length
    info: Optional[WaveInfo] = None

    @property
    def channels(self) -> int:
        return int(self.samples.shape[1])

    @property
    def frames(self) -> int:
        return int(self.samples.shape[0])


def ffmpeg_path() -> Optional[str]:
    return os.environ.get("TSUKI_FFMPEG") or shutil.which("ffmpeg")


def decode_at9(riff: Buffer, *, trim: bool = True, ffmpeg: Optional[str] = None,
               float_output: bool = False) -> Pcm:
    """Decode an ATRAC9 RIFF stream with ffmpeg (``-f wav``, atrac9 decoder).

    ffmpeg emits every superframe (``superframes * 1024`` samples, the first
    ``delay`` = 256 of them silent).  With ``trim`` the result is the playable
    sound ``[delay, delay + fact.samples)`` and the loop is in that frame;
    without it the raw decoder output is returned and the loop is the smpl
    values as stored.  ``float_output`` returns float32 samples (full scale
    1.0) without clipping; the int16 path clamps, which matters for loud
    effects (574 vse entries reach the int16 limit)."""
    info = parse_wave(riff)
    exe = ffmpeg or ffmpeg_path()
    if exe is None:
        raise AudioError("ffmpeg not found (set TSUKI_FFMPEG)")
    fmt, codec, dt = ("f32le", "pcm_f32le", "<f4") if float_output else ("s16le", "pcm_s16le", "<i2")
    p = subprocess.run([exe, "-v", "error", "-f", "wav", "-i", "pipe:0", "-f", fmt,
                        "-acodec", codec, "pipe:1"], input=bytes(riff), capture_output=True)
    if p.returncode != 0:
        raise AudioError(f"ffmpeg failed: {p.stderr.decode(errors='replace').strip()}")
    pcm = np.frombuffer(p.stdout, dtype=dt)
    if pcm.size % info.channels:
        raise AudioError("ffmpeg output is not a whole number of frames")
    pcm = pcm.reshape(-1, info.channels)
    if pcm.shape[0] != info.decoded_samples:
        raise AudioError(f"ffmpeg produced {pcm.shape[0]} samples, expected {info.decoded_samples}")
    loop = info.loop
    if trim:
        pcm = pcm[info.delay:info.delay + info.num_samples]
        return Pcm(np.ascontiguousarray(pcm), info.sample_rate, loop, True, info)
    raw_loop = None
    if info.loops:
        lp = info.loops[0]
        raw_loop = LoopInfo(lp.start, lp.end, loop.whole if loop else False)
    return Pcm(pcm.copy(), info.sample_rate, raw_loop, False, info)


def decode_vag_pcm(vag: Buffer, *, use_numba: Optional[bool] = None) -> Pcm:
    hdr = parse_vag(vag)
    x = decode_vag(vag, use_numba=use_numba)
    body = bytes(vag[VAG_HEADER_SIZE:VAG_HEADER_SIZE + hdr.data_size])
    flags = [body[f * VAG_FRAME_BYTES + 1] & 0x0F for f in range(hdr.frames)]
    return Pcm(x.reshape(-1, 1), hdr.sample_rate, _vag_loop(flags), True)


def render_loop(pcm: Pcm, loops: int = 1, *, seconds: Optional[float] = None) -> np.ndarray:
    """intro + loop body * (loops + 1) (+ nothing after; the engine never plays
    past the loop end while looping).  ``seconds`` caps the output length."""
    x = pcm.samples
    lp = pcm.loop
    if lp is None:
        out = x
    else:
        intro = x[:lp.start]
        body = x[lp.start:lp.end_exclusive]
        out = np.concatenate([intro] + [body] * (loops + 1), axis=0)
    if seconds is not None:
        out = out[:int(seconds * pcm.rate)]
    return out


def write_wav(path: Union[str, Path], samples: np.ndarray, rate: int,
              loop: Optional[LoopInfo] = None) -> None:
    """Write 16-bit PCM WAV; with ``loop`` a ``smpl`` chunk (one forward loop,
    start/end inclusive, in output samples) is appended."""
    s = np.asarray(samples, dtype="<i2")
    if s.ndim == 1:
        s = s.reshape(-1, 1)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(s.shape[1])
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(s.tobytes())
    data = bytearray(buf.getvalue())
    if loop is not None:
        smpl = struct.pack("<9I", 0, 0, int(round(1e9 / rate)), 60, 0, 0, 0, 1, 0)
        smpl += struct.pack("<6I", 0, 0, loop.start, loop.end, 0, 0)
        data += b"smpl" + struct.pack("<I", len(smpl)) + smpl
        struct.pack_into("<I", data, 4, len(data) - 8)
    Path(path).write_bytes(bytes(data))


# ---------------------------------------------------------------------------
# census
# ---------------------------------------------------------------------------

def _pcm_stats(x: np.ndarray) -> dict[str, float]:
    f = x.astype(np.float64)
    rms = float(np.sqrt(np.mean(f * f))) if f.size else 0.0
    return {"rms": rms, "rms_dbfs": float(20 * np.log10(rms / 32768.0)) if rms > 0 else -999.0,
            "peak": int(np.abs(x).max()) if x.size else 0}


def census(bank: str, *, decode: bool = False, limit: Optional[int] = None, step: int = 1,
           workers: int = 8, img: Optional[Path] = None) -> dict[str, Any]:
    """Parse (and optionally decode) every entry of a bank and count the
    invariants documented in audio.md.  Returns a summary dict."""
    from collections import Counter

    out: dict[str, Any] = {"bank": bank, "entries": 0, "errors": []}
    hist: dict[str, Counter[Any]] = {k: Counter() for k in (
        "channels", "rate", "block_align", "config", "fact_tail", "chunks", "loop", "decode", "silent")}
    rows: list[dict[str, Any]] = []
    with AudioBank(bank, img) as b:
        idxs = list(range(0, len(b), step))
        if limit is not None:
            idxs = idxs[:limit]

        def one(i: int) -> dict[str, Any]:
            r: dict[str, Any] = {"index": i}
            try:
                e = b.entry(i)
                r.update(name=e.name, channels=e.channels, rate=e.sample_rate, samples=e.num_samples,
                         loop=None if e.loop is None else ("whole" if e.loop.whole else [e.loop.start, e.loop.end]))
                if e.wave is not None:
                    w = e.wave
                    r.update(block_align=w.block_align, config=w.config.raw.hex(), fact_tail=list(w.fact[1:]),
                             chunks=[c[0] for c in w.chunks], superframes=w.superframes)
                if decode:
                    if e.codec == "atrac9":
                        raw = decode_at9(b.stream(i), trim=False)
                        d = e.delay
                        r["delay_zero"] = bool(not np.any(raw.samples[:d]))
                        r["delay_peak"] = int(np.abs(raw.samples[:d]).max()) if d else 0
                        if d >= 32:
                            pre = raw.samples[d - 32:d].astype(np.float64)
                            post = raw.samples[d:d + 32].astype(np.float64)
                            r["onset_rms"] = [float(np.sqrt(np.mean(pre * pre))), float(np.sqrt(np.mean(post * post)))]
                        r.update(_pcm_stats(raw.samples[d:d + e.num_samples]))
                        r["decoded"] = raw.frames
                    else:
                        pcm = b.decode(i)
                        r.update(_pcm_stats(pcm.samples))
                        r["decoded"] = pcm.frames
            except Exception as ex:  # noqa: BLE001 - collected and reported, never hidden
                r["error"] = f"{type(ex).__name__}: {ex}"
            return r

        if decode and workers > 1:
            with ThreadPoolExecutor(workers) as pool:
                rows = list(pool.map(one, idxs))
        else:
            rows = [one(i) for i in idxs]
    for r in rows:
        out["entries"] += 1
        if "error" in r:
            out["errors"].append((r["index"], r["error"]))
            continue
        hist["channels"][r["channels"]] += 1
        hist["rate"][r["rate"]] += 1
        if "config" in r:
            hist["block_align"][r["block_align"]] += 1
            hist["config"][r["config"]] += 1
            hist["fact_tail"][",".join(str(v) for v in r["fact_tail"])] += 1
            hist["chunks"][" ".join(r["chunks"])] += 1
        lp = r["loop"]
        hist["loop"]["none" if lp is None else ("whole" if lp == "whole" else "inner")] += 1
        if decode:
            hist["decode"]["ok"] += 1
            if "delay_zero" in r:
                hist["decode"]["delay_zero" if r["delay_zero"] else "delay_nonzero"] += 1
            hist["silent"]["silent(<-60dBFS)" if r["rms_dbfs"] < -60 else "audible"] += 1
    out["hist"] = {k: dict(v) for k, v in hist.items() if v}
    out["total_seconds"] = sum(r.get("samples", 0) / r["rate"] for r in rows if "rate" in r)
    out["rows"] = rows
    return out


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def _main(argv: Optional[list[str]] = None) -> int:
    import argparse
    import json

    ap = argparse.ArgumentParser(prog="python -m tsuki.audio", description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("list"); p.add_argument("bank"); p.add_argument("--limit", type=int, default=20)
    p = sub.add_parser("info"); p.add_argument("bank"); p.add_argument("entry")
    p = sub.add_parser("extract"); p.add_argument("bank"); p.add_argument("entry"); p.add_argument("out")
    p = sub.add_parser("decode"); p.add_argument("bank"); p.add_argument("entry"); p.add_argument("out")
    p.add_argument("--raw", action="store_true", help="keep the encoder delay and padding (ATRAC9)")
    p.add_argument("--loops", type=int, default=0, help="render the loop this many extra times")
    p.add_argument("--seconds", type=float, default=None, help="cap the output length")
    p = sub.add_parser("census"); p.add_argument("bank"); p.add_argument("--decode", action="store_true")
    p.add_argument("--limit", type=int, default=None); p.add_argument("--step", type=int, default=1)
    p.add_argument("--workers", type=int, default=8); p.add_argument("--json", default=None)
    a = ap.parse_args(argv)

    if a.cmd == "census":
        res = census(a.bank, decode=a.decode, limit=a.limit, step=a.step, workers=a.workers)
        rows = res.pop("rows")
        print(json.dumps(res, indent=1, default=str))
        if a.json:
            Path(a.json).write_text(json.dumps(rows, default=str))
        return 1 if res["errors"] else 0

    with AudioBank(a.bank) as b:
        if a.cmd == "list":
            for i in range(min(len(b), a.limit) if a.limit else len(b)):
                e = b.entry(i)
                lp = "-" if e.loop is None else ("whole" if e.loop.whole else f"{e.loop.start}..{e.loop.end}")
                print(f"{i:5d} {str(e.name):24s} {e.codec:6s} {e.channels}ch {e.sample_rate} "
                      f"{e.duration:8.3f}s loop={lp}")
            return 0
        i = b.find(a.entry)
        e = b.entry(i)
        if a.cmd == "info":
            d = {k: v for k, v in e.__dict__.items() if k not in ("wave", "vag", "lv", "extra")}
            if e.wave is not None:
                d["wave"] = {k: (v.__dict__ if hasattr(v, "__dict__") else v) for k, v in e.wave.__dict__.items()}
                d["wave"]["config"] = {**e.wave.config.__dict__, "raw": e.wave.config.raw.hex(),
                                       "sample_rate": e.wave.config.sample_rate, "channels": e.wave.config.channels,
                                       "superframe_bytes": e.wave.config.superframe_bytes,
                                       "samples_per_superframe": e.wave.config.samples_per_superframe}
                d["wave"]["subformat"] = e.wave.subformat.hex()
            if e.vag is not None:
                d["vag"] = {**e.vag.__dict__, "magic": e.vag.magic.decode(), "reserved_14": e.vag.reserved_14.hex()}
            if e.lv is not None:
                d["lv"] = {"length": e.lv.length, "payload": e.lv.payload.hex()}
            d["extra"] = e.extra
            print(json.dumps(d, indent=1, default=str))
            return 0
        if a.cmd == "extract":
            Path(a.out).write_bytes(b.stream(i))
            print(f"wrote {a.out} ({e.stream_size} bytes, {e.codec})")
            return 0
        if a.cmd == "decode":
            pcm = b.decode(i, trim=not a.raw)
            x = render_loop(pcm, a.loops, seconds=a.seconds) if a.loops else pcm.samples
            if a.seconds is not None and not a.loops:
                x = x[:int(a.seconds * pcm.rate)]
            wav_loop = pcm.loop if (pcm.loop is not None and pcm.loop.end < x.shape[0] and not a.loops) else None
            write_wav(a.out, x, pcm.rate, wav_loop)
            print(f"wrote {a.out}: {x.shape[0]} samples x {x.shape[1]} ch @ {pcm.rate} Hz "
                  f"({x.shape[0] / pcm.rate:.3f} s), loop={pcm.loop}")
            return 0
    return 2


if __name__ == "__main__":
    raise SystemExit(_main())
