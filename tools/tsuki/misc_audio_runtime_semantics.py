"""Sound playback semantics of TSUKIHIME (PS4 CUSA47999 v01.01), as the engine does it.

Reference model for a port, written from the eboot's own code and checked against it under
emulation (``tsuki.misc_audio_runtime_semantics_emu``; see docs/formats/sound_runtime.md):

* channel model: 2 alternating BGM slots (sound streams 0/1), 4 voice-type streams (2..5;
  the script voice is stream 2), 20 script SE channels (streams 6..25), HE-VAG system SEs;
* the script commands ``_MPLY _MVOL _MFAD _MSTP _SEPL/_SEFI _SEFD _SEVL _SESP _VPLY/_VPL2
  _VCWT`` with their keyword tags (`001 `011 `017 `018 `049 `068 `069 `070);
* the per-frame mixer update (VA 0xAD80): volume / pan ramps, fade-to-stop, the deferred
  (`068) SE queue, the per-character voice gate for SE channels;
* BGM ducking during voice (VA 0x694B9), stop-voice-on-advance (VA 0xA1015..), the voice
  start at message display (VA 0x92380), the voice-group -> character -> config-slot tables,
  the system-SE (HE-VAG) id table.

All floating point is float32 in the engine's operation order (numpy.float32), so the model
reproduces the engine's record contents bit for bit.  Addresses are eboot VAs (text VA = file
offset - 0x2280; data VA = file offset - 0x365FA0 + 0x600000).

CLI::

    python -m tsuki.misc_audio_runtime_semantics groups            # voice group table
    python -m tsuki.misc_audio_runtime_semantics tags "<statement>" # tag table of a statement
    python -m tsuki.misc_audio_runtime_semantics run "<stmt>" ... frame [N] ...   # model trace
"""
from __future__ import annotations

import argparse
import math
import struct
import sys
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional, Sequence, Tuple

import numpy as np

from .script import inline_int, inline_present

F32 = np.float32

# ---------------------------------------------------------------------------
# addresses (data VAs) -- documented in sound_runtime.md section 2
# ---------------------------------------------------------------------------
VA_BGM_REC = 0xDD86A0          # 2 x 0x34 BGM slot records (streams 0, 1)
VA_VOICE_REC = 0xDD8B20        # 4 x 0x34 voice-type records (streams 2..5)
VA_SE_REC = 0xDD8710           # 20 x 0x34 SE records (streams 6..25)
VA_BGM_SLOT = 0xDD8F28         # current BGM slot (0/1)
VA_BGM_TRACK = 0x62437C        # track currently started (-1 = none)
VA_BGM_LOOP = 0x624380         # i32[2] loop flag per slot (1 = loop forever)
VA_SE_LOOP = 0xDD8FC0          # i32[20] loop flag per SE channel
VA_VOLUME = 0x17DA268          # volume block: +0xB movie +0xC BGM +0xD voice +0xE SE +0xF sys SE
VA_CONFIG = 0x66C578           # persistent config (0x140 bytes)
VA_DEFER = 0x16543F0           # 20 x 0x18 deferred SE commands (`068)
VA_SCR_TRACK = 0x16545D0       # i16 track of the last _MPLY (-1 none)
VA_SCR_BGM_VOL = 0x1654238     # i16 volume of the last _MPLY / _MVOL
VA_SCR_SE_ID = 0x1654210       # i16[20] vse entry of the last _SEPL per channel
VA_SCR_SE_LOOPID = 0x1654240   # i16[20] vse entry of a looping SE (-1 none)
VA_SCR_SE_VOL = 0x1654270      # i16[20] script volume per channel
VA_SCR_SE_LOOP = 0x16542A0     # i16[20] 1 = channel plays a looped SE
VA_SCR_SE_PAN = 0x16542D0      # i16[20] pan (-1000..1000)
VA_SCR_SE_CHAR = 0x1654330     # u8[20] `070 character of the channel
VA_MUSIC_BITS = 0x164D5C0      # bit array; bit 3200 + track = "track heard" (music room)
VA_VOICE_PENDING = 0x16541FC   # i32 voice entry requested by _VPLY, played at the next _ZM
VA_VOICE_CHAR = 0x1654208      # u16 character of the pending voice
VA_VOICE_ARG3 = 0x165420A      # u16 third _VPLY argument (kept in the history record +0x3641)
VA_FF = 0x120A6D2              # u16 fast-forward / rewind replay ("ff"): sound commands silent
VA_SCAN = 0x174DE48            # u8 history scan / restore ("scan"): sound commands silent
VA_SKIP = 0x17DB232            # u16 skip mode
VA_VSYNC = 0xDC7BE0            # u16 vsyncs since the last frame (0xD47C0 returns min(n, 3))

# per-frame constants (data VA -> value)
RAMP_DEFAULT = F32(0.2)        # 0x175028: rate used when a record's rate is <= 0
BGM_REPLACE_FADE = F32(4.0)    # 0x175084 / 0x1750A4: old slot fade when a new track starts
SE_STOP_RATE = F32(4.0)        # 0x175114 (0xABF0)
MS_PER_S, FPS = F32(1000.0), F32(60.0)
SE_JUMP_RATE = np.frombuffer(bytes.fromhex("5555d044"), dtype=np.float32)[0]   # 0x175024 = 1666.6666

# ---------------------------------------------------------------------------
# voice groups -> character -> config slot  [E: VA 0x96957..0x9A165 + table 0x215E90;
# mask table 0x86A850; V: emulated for all 54 voice.nam groups]
# ---------------------------------------------------------------------------
# character index stored in 0x1654208 for each voice.nam group (prefix of the voice name).
# Every group not listed (C80_, D*, M04_..M10_, DU) gets 0.
VOICE_GROUP_CHAR: Dict[str, int] = {
    "A00": 1, "A10": 2, "A11": 2, "A12": 2, "A20": 3, "A21": 3, "A22": 3, "A23": 3, "A24": 3,
    "B00": 4, "B10": 5, "B20": 6, "B30": 7, "B40": 8, "B50": 9, "B60": 10, "B70": 11,
    "B80": 12, "B90": 13, "C00": 14, "C10": 15, "C20": 16, "C30": 17, "C40": 18, "C50": 19,
    "C60": 20, "C70": 21, "C80": 0, "C90": 22,
    "M00": 23, "M01": 23, "M02": 23, "M03": 23, "M11": 23, "M12": 23,
}
# 0x86A850: config enable mask per character (u32[24]); slot k = log2(mask)
CHAR_MASK: Tuple[int, ...] = (
    0x40000, 0x1, 0x2, 0x4, 0x200, 0x10000, 0x10, 0x20, 0x8, 0x80, 0x1000, 0x8000,
    0x20000, 0x1, 0x400, 0x2000, 0x4000, 0x800, 0x40, 0x40000, 0x40000, 0x100, 0x40000, 0x40000)


def voice_char(name: str) -> int:
    """Character index the _VPLY handler derives from a voice name (first 3 characters)."""
    return VOICE_GROUP_CHAR.get(name[:3], 0)


def char_slot(char: int) -> int:
    """Config slot k (bit k of config +0x3C, volume u16 config +0x76 + 2k) of a character;
    VA 0x3D2D0 = (int)log2f(mask[char < 24 ? char : 0])."""
    return int(math.log2(CHAR_MASK[char if 0 <= char < 24 else 0]))


def group_slot(group: str) -> int:
    return char_slot(voice_char(group))


# ---------------------------------------------------------------------------
# system SE (HE-VAG) table [E: 0xCC670, 0x8560; tables 0x2D3770, 0x6241F0]
# ---------------------------------------------------------------------------
# id -> (bank, index) ; key = bank * 128 + index -> (left vag, right vag) ; right < 0 = mono
SYSSE_ID: Tuple[Tuple[int, int], ...] = (
    (0, 0), (0, 1), (0, 2), (0, 4), (0, 5), (0, 6), (0, 1), (0, 3), (0, 7), (0, 8)) + ((0, 20),) * 18
SYSSE_VAG: Tuple[Tuple[int, int], ...] = (
    (3, 4), (5, 6), (1, 2), (7, 8), (9, 10), (11, 12), (13, 14), (15, 16), (17, 18)) + ((0, -1),) * 27


def system_se_vags(sid: int) -> Tuple[int, int]:
    """(left, right) vag.mrg entries SystemSE_Play(id) (VA 0xCC670) plays; right = -1 for
    a mono sound, left <= 0 for nothing."""
    bank, idx = SYSSE_ID[sid]
    return SYSSE_VAG[bank * 128 + idx]


# ---------------------------------------------------------------------------
# keyword tags  [E VA 0x87276..0x87308 (MPLY); same loop in every audio handler]
# ---------------------------------------------------------------------------
TAG_FADE, TAG_WAIT, TAG_VOL, TAG_PAN, TAG_LOOP, TAG_DELAY, TAG_START, TAG_CHAR = 1, 11, 17, 18, 49, 68, 69, 70


def _arg_start(buf: bytes, sep: int) -> int:
    return sep + 1


def parse_tags(buf: bytes, sep: int) -> Tuple[List[int], int]:
    """Engine tag scan: returns (tag byte per argument position 0..63, ntags).
    ``sep`` is the index of the separator byte (name + 4, the '(').  An argument whose first
    byte is '`' gets tag byte ((d1*10 + d2)*10 + d3) mod 256 of its three digit characters,
    any other argument 0xFF.  ntags = 1 + position of the last tagged argument (0 if none);
    the handlers search positions 0..ntags-1 only."""
    tags: List[int] = []
    ntags = 0
    p = sep + 1
    for i in range(64):
        tags.append(0xFF)
        if p < len(buf) and buf[p] == 0x60:
            b = [buf[p + k] if p + k < len(buf) else 0 for k in (1, 2, 3)]
            v = ((((b[0] * 10) & 0xFF) + b[1]) & 0xFF)
            v = (((v * 10) & 0xFF) + b[2] + 0x30) & 0xFF
            tags[i] = v
            p += 5
            ntags = i + 1
        start = p
        q = p + 1
        end = False
        for _ in range(500):
            c = buf[q - 1] if q - 1 < len(buf) else 0
            if c in (0x29, 0x3B):
                end = True
                break
            if c == 0x2C:
                break
            q += 1
        else:
            q = start + 500
        if end:
            break
        p = q
    return tags, ntags


def find_tag(tags: Sequence[int], ntags: int, tag: int) -> int:
    for j in range(ntags):
        if tags[j] == tag:
            return j
    return -1


def _i16(v: int) -> int:
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def _i8(v: int) -> int:
    v &= 0xFF
    return v - 0x100 if v & 0x80 else v


def _tdiv(a: int, b: int) -> int:
    """C integer division (toward zero)."""
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b > 0) else -q


def _crem(a: int, b: int) -> int:
    return a - b * _tdiv(a, b)


def _pan_from_tag(v: int) -> int:
    """`018 value (0 left .. 64 centre .. 128 right) -> pan -1000..1000 (VA 0x89970)."""
    x = v * 1000 - 64000
    return _tdiv(x, 64) if x < 0 else x >> 6


def _rate(delta: float, ms: float) -> F32:
    """Per-frame ramp rate for |delta| volume points over ``ms`` milliseconds at 60 fps:
    ((delta * 1000) / 60) / ms in float32."""
    return F32(F32(F32(delta) * MS_PER_S) / FPS) / F32(ms)


# ---------------------------------------------------------------------------
# records and state
# ---------------------------------------------------------------------------
@dataclass
class Rec:
    """0x34-byte channel record (layout in sound_runtime.md 2.2)."""
    state: int = 0                 # +0x00 0 idle, 1 playing, 2 fading out to stop
    kind: int = 0                  # +0x04 volume class: 1 SE (vol +0xE), 3 system (+0xF), else voice (+0xD)
    w8: int = 0                    # +0x08 1 = a stream was started (SE only)
    idx: int = 0                   # +0x0A bgm / vse entry
    vol: F32 = F32(0)              # +0x0C current volume (0..100)
    vol_to: F32 = F32(0)           # +0x10 target
    vol_rate: F32 = F32(0)         # +0x14 step per frame
    pan: F32 = F32(0)              # +0x18 current pan (-1000..1000)
    pan_to: F32 = F32(0)           # +0x1C
    pan_rate: F32 = F32(0)         # +0x20
    p3: F32 = F32(0)               # +0x24 third ramp (set by _SEP3 only; never sent to the stream)
    p3_to: F32 = F32(0)            # +0x28
    p3_rate: F32 = F32(0)          # +0x2C
    char: int = 0                  # +0x30 character (1..19 -> per-character voice gate)

    def pack(self) -> bytes:
        b = bytearray(0x34)
        struct.pack_into("<IIHH", b, 0, self.state & 0xFFFFFFFF, self.kind & 0xFFFFFFFF,
                         self.w8 & 0xFFFF, self.idx & 0xFFFF)
        struct.pack_into("<9f", b, 0xC, self.vol, self.vol_to, self.vol_rate, self.pan,
                         self.pan_to, self.pan_rate, self.p3, self.p3_to, self.p3_rate)
        b[0x30] = self.char & 0xFF
        return bytes(b)

    @classmethod
    def unpack(cls, b: bytes) -> "Rec":
        st, kind, w8, idx = struct.unpack_from("<IIHH", b, 0)
        f = [F32(x) for x in struct.unpack_from("<9f", b, 0xC)]
        return cls(state=st, kind=kind, w8=w8, idx=idx, vol=f[0], vol_to=f[1], vol_rate=f[2],
                   pan=f[3], pan_to=f[4], pan_rate=f[5], p3=f[6], p3_to=f[7], p3_rate=f[8], char=b[0x30])


@dataclass
class Defer:
    """0x18-byte deferred SE command (one per channel)."""
    timer: int = 0                 # +0x00 i16 frames
    kind: int = 0                  # +0x02 1 SEPL, 2 SEFD, 3 SEVL, 4 SEAC (engine-only)
    se_id: int = -1                # +0x04 i16 vse entry
    fade: int = -1                 # +0x08 i64 `001 ms (-1 absent)
    vol: int = 0xFF                # +0x10 u8 `017 (0xFF absent)
    vol2: int = 0xFF               # +0x11 u8 (SEAC)
    pan: int = 0xFF                # +0x12 u8 `018 raw (0xFF absent)
    pan2: int = 0xFF               # +0x13 u8 (SEAC)
    loop: int = 0xFF               # +0x14 u8 loop argument
    char: int = 0                  # +0x15 u8 `070

    def pack(self) -> bytes:
        b = bytearray(0x18)
        struct.pack_into("<hBx", b, 0, _i16(self.timer), self.kind & 0xFF)
        struct.pack_into("<h", b, 4, _i16(self.se_id))
        struct.pack_into("<q", b, 8, self.fade)
        b[0x10:0x16] = bytes([self.vol & 0xFF, self.vol2 & 0xFF, self.pan & 0xFF, self.pan2 & 0xFF,
                              self.loop & 0xFF, self.char & 0xFF])
        return bytes(b)

    @classmethod
    def unpack(cls, b: bytes) -> "Defer":
        t, k = struct.unpack_from("<hB", b, 0)
        sid = struct.unpack_from("<h", b, 4)[0]
        fade = struct.unpack_from("<q", b, 8)[0]
        return cls(t, k, sid, fade, b[0x10], b[0x11], b[0x12], b[0x13], b[0x14], b[0x15])

    @classmethod
    def cleared(cls) -> "Defer":
        """State after execution / cancel (VA 0x88098): timer 0, kind 0, id -1, bytes
        +8..+0x14 = 0xFF, +0x15 = 0."""
        return cls(0, 0, -1, -1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0)


@dataclass
class Config:
    """Persistent config fields the sound code reads (0x66C578 + offset), defaults from
    VA 0x3EAC0."""
    bgm: int = 40                  # +0x14 BGM volume
    voice: int = 100               # +0x16 voice master (menu item 0x5C: on = 100, off = 0)
    se: int = 40                   # +0x18 SE volume
    sys_se: int = 10               # +0x1A system SE volume
    duck: int = 0                  # +0x1C 1 = lower BGM while a voice plays
    alt_bgm: int = 0               # +0x1E 1 = 5.1ch BGM set (entries n + 131, absent on PS4)
    se_caption: int = 0            # +0x2C 1 = SE notification hook (0xE7EE0 / 0xCD610)
    skip_voice: int = 1            # +0x30 voices also play in skip mode (read text)
    voice_on2: int = 1             # +0x36 second voice enable (0x41AB0); not set by 0x3EAC0
    char_enable: int = 0x3FFFFFFF  # +0x3C bit k = voice of config slot k on
    stop_on_advance: int = 1       # +0x4E stop the voice when the text advances
    movie: int = 40                # +0x54 movie volume (copied from +0x14 by 0x3E6E0)
    char_vol: List[int] = field(default_factory=lambda: [50] * 19)   # +0x76 + 2k
    mute_bgm: int = 0              # +0xA6
    mute_se: int = 0               # +0xA7
    mute_sys: int = 0              # +0xA8

    def volume_block(self) -> Dict[int, int]:
        """Sound_ApplyConfig(full) (VA 0x3E9B0, rdi = 1): bytes +0xB..+0xF of 0x17DA268."""
        return {0xC: (1 - self.mute_bgm) * self.bgm & 0xFF, 0xB: (1 - self.mute_bgm) * self.movie & 0xFF,
                0xE: (1 - self.mute_se) * self.se & 0xFF, 0xF: (1 - self.mute_sys) * self.sys_se & 0xFF,
                0xD: self.voice & 0xFF}

    def char_gain(self, slot: int) -> int:
        """Voice gain of config slot k: char_vol[k] * voice / 100 (C division), or 0 when the
        slot's enable bit is clear (VA 0xB26D / 0x3EA4F / 0x928A2)."""
        if not self.char_enable & (1 << slot):
            return 0
        return _tdiv(_i16(self.char_vol[slot]) * _i16(self.voice), 100)


Call = Tuple[object, ...]


class SoundModel:
    """Engine sound state + handlers.  ``calls`` records what the engine asks the sound
    library (nexl_Sound) to do, normalised like ``normalize_call`` does for the emulator."""

    def __init__(self, cfg: Optional[Config] = None) -> None:
        self.cfg = cfg or Config()
        self.vol: Dict[int, int] = {k: 0 for k in range(0x10)}
        self.vol.update(self.cfg.volume_block())
        self.bgm = [Rec(), Rec()]
        self.voice = [Rec() for _ in range(4)]
        self.se = [Rec() for _ in range(20)]
        self.slot = 0
        self.track = -1                      # 0x62437C
        self.bgm_loop = [1, 1]               # 0x624380 (initialised data: 1, 1)
        self.se_loop = [0] * 20              # 0xDD8FC0
        self.scr_track = 0
        self.scr_bgm_vol = 0
        self.scr_se_id = [0] * 20
        self.scr_se_loopid = [0] * 20
        self.scr_se_vol = [0] * 20
        self.scr_se_loop = [0] * 20
        self.scr_se_pan = [0] * 20
        self.scr_se_char = [0] * 20
        self.defer = [Defer(0, 0, 0, 0, 0, 0, 0, 0, 0, 0) for _ in range(20)]
        self.music_bits: Dict[int, int] = {}
        self.voice_pending = -1
        self.voice_char = 0
        self.voice_arg3 = 0
        self.ff = 0
        self.scan = 0
        self.skip = 0
        self.vsyncs = 1
        self.duck_level = 256                # 0x7A6184
        self.duck_applied = -1               # 0x7A608C (Sound_ApplyConfig -> 0x674B0 sets -1)
        self.calls: List[Call] = []
        self.playing: Dict[int, bool] = {}
        self.default_playing = True
        self.se_lookup: Callable[[str, int], Optional[Tuple[int, int]]] = lambda n, f: None

    # -- backend ---------------------------------------------------------------
    def _call(self, *c: object) -> None:
        self.calls.append(c)

    def is_playing(self, s: int) -> bool:
        self._call("is_playing", s)
        return self.playing.get(s, self.default_playing)

    def _play(self, s: int, bank: str, entry: int, loop: int, v: F32) -> None:
        self._call("play", s, bank, entry, loop, _bits(v))
        self.playing[s] = True

    def _stop(self, s: int) -> None:
        self._call("stop", s, 0)
        self._call("close", s)

    def _set_volume(self, s: int, v: F32) -> None:
        self._call("set_volume", s, _bits(v), _bits(F32(0)))

    def _rec(self, s: int) -> Rec:
        if s < 2:
            return self.bgm[s]
        if s < 6:
            return self.voice[s - 2]
        return self.se[s - 6]

    def history_push(self) -> None:
        """History_Push (VA 0x7AF90), called by every sound handler with the statement
        pointer: besides recording the SE state it copies the ring record's bytes +0x36BC..
        +0x36CF BACK into the script character array 0x1654330 (VA 0x7B3BA..0x7B3C3); no
        code ever writes those record bytes, so with a zeroed ring the array is cleared."""
        self.scr_se_char = [0] * 20

    @property
    def silent(self) -> bool:
        return bool(self.ff or self.scan)

    # -- BGM primitives --------------------------------------------------------
    def bgm_stop(self) -> None:
        """VA 0x9A90: stop + close the current slot now, track = -1 (the record keeps its
        state; the frame update will not touch a stream that does not play)."""
        self._call("stop", self.slot, 0)
        self._call("close", self.slot)
        self.track = -1

    def bgm_fade_out(self, rate: F32) -> None:
        """VA 0x9EE0 (_MSTP, _MFAD, MPLY cross-fade): current slot -> state 2, target 0."""
        r = self.bgm[self.slot]
        if r.state != 0:
            r.state = 2
            r.vol_rate = F32(rate)
            r.vol_to = F32(0)
        self.track = -1

    def _bgm_entry(self, track: int) -> int:
        if self.cfg.alt_bgm == 1 and 0 <= track - 1 < 0x83:
            return track + 0x83
        return track

    def bgm_start(self, track: int, vol: F32) -> None:
        """VA 0x98C0: start ``track`` at volume ``vol`` on the other slot, no fade."""
        if track < 0:
            self.bgm_stop()
            return
        if self.track == track:
            return
        old = self.bgm[self.slot]
        if old.state == 1:
            old.state = 2
            old.vol_to = F32(0)
            old.vol_rate = BGM_REPLACE_FADE
        self.track = track
        self.slot ^= 1
        r = self.bgm[self.slot]
        entry = self._bgm_entry(track)
        r.state, r.kind, r.idx = 1, 0, entry
        r.vol = r.vol_to = r.vol_rate = F32(vol)
        r.pan = r.pan_to = r.pan_rate = r.p3 = r.p3_to = r.p3_rate = F32(0)
        g = _trunc(F32(F32(_i8(self.vol[0xC])) * F32(vol)) * F32(0.01))
        self._call("open_file", self.slot)
        self._play(self.slot, "bgm", entry, int(self.bgm_loop[self.slot] != 0),
                   F32(F32(_i16(g)) * F32(0.01)))
        self._call("set_flag6", self.slot, 0)

    def bgm_start_fade(self, track: int, vol: int, rate: F32) -> None:
        """VA 0x9B40: start ``track`` from volume 0 ramping to ``vol`` at ``rate``/frame."""
        if track < 0:
            self.bgm_stop()
            return
        if self.track == track:
            return
        old = self.bgm[self.slot]
        if old.state == 1:
            old.state = 2
            old.vol_to = F32(0)
            old.vol_rate = BGM_REPLACE_FADE
        self.track = track
        self.slot ^= 1
        r = self.bgm[self.slot]
        entry = self._bgm_entry(track)
        r.state, r.kind, r.idx = 1, 0, entry
        r.vol = F32(0)
        r.vol_rate = F32(rate)
        r.vol_to = F32(vol)
        self._call("open_file", self.slot)
        self._call("set_volume0", self.slot, _bits(F32(0)))
        self._play(self.slot, "bgm", entry, int(self.bgm_loop[self.slot] != 0), F32(0))

    def bgm_set_loop(self, flag: int) -> None:
        """VA 0x9EA0: both slots' loop flags; the current slot's stream gets loop
        (on, count -1) for flag 1, else (off, 1)."""
        self.bgm_loop = [flag, flag]
        if flag == 1:
            self._call("set_loop", self.slot, 1, -1)
        else:
            self._call("set_loop", self.slot, 0, 1)

    # -- commands ---------------------------------------------------------------
    def run(self, stmt: str) -> int:
        """Execute one statement; returns the handler's wait value (0 = continue)."""
        b = stmt.encode("latin-1")
        us = b.index(b"_")
        name = b[us + 1:us + 5].decode("latin-1")
        sep = us + 5
        h = getattr(self, "cmd_" + name, None)
        if h is None:
            raise KeyError(name)
        return int(h(b, sep))

    def _int(self, b: bytes, sep: int, index: int, bits: int = 16) -> int:
        return inline_int(b, sep + 1, index, bits)

    def _arg0(self, b: bytes, sep: int) -> int:
        """Channel / track argument: the inline integer of argument 0, i16 (handlers parse
        it with their own copy of the inline reader, VA 0x87445 / 0x895C5 / 0x89F35)."""
        return _i16(self._int(b, sep, 0))

    def cmd_MPLY(self, b: bytes, sep: int) -> int:
        """VA 0x87250."""
        self.history_push()
        tags, nt = parse_tags(b, sep)
        if nt <= 0:
            vol, loop, fade, t011, start = 100, 1, 0, 0, 0
        else:
            j = find_tag(tags, nt, TAG_VOL)
            vol = _i16(self._int(b, sep, j)) if j >= 0 else 100
            j = find_tag(tags, nt, TAG_LOOP)
            loop = _i16(self._int(b, sep, j)) if j >= 0 else 1
            j = find_tag(tags, nt, TAG_FADE)
            fade = _i16(self._int(b, sep, j)) if j >= 0 else 0
            j = find_tag(tags, nt, TAG_WAIT)
            t011 = _i16(self._int(b, sep, j)) if j >= 0 else 0
            j = find_tag(tags, nt, TAG_START)
            start = _i32(self._int(b, sep, j, 32)) if j >= 0 else 0
        track = self._arg0(b, sep)
        if track < 0:
            if not self.ff and not self.scan:
                self.bgm_stop()
            self.scr_track = -1
            return 0
        if not self.scan:
            if not self.ff and self.track != track:
                status = 3 if self.is_playing(self.slot) else 5
                if fade == 0 and status == 3:
                    self.bgm_stop()
                self.bgm_set_loop(loop)
                self._call("set_start_sample", self.slot ^ 1, start & 0xFFFFFFFF)
                fv = F32(vol)
                if fade != 0:
                    new_rate = F32(F32(F32(fv * MS_PER_S) / FPS) / F32(fade))
                    old = F32(F32(F32(self.scr_bgm_vol) * MS_PER_S) / FPS)
                    old_rate = F32(old / F32(t011 if t011 != 0 else fade))
                    if not (old_rate > 0 or np.isnan(old_rate)):
                        old_rate = F32(100.0)
                    self.bgm_fade_out(old_rate)
                    self.bgm_start_fade(track, vol, new_rate)
                else:
                    self.bgm_start(track, fv)
            if (track & 0xFFFF) <= 0x207:
                bit = track + 0xC80
                self.music_bits[bit >> 3] = self.music_bits.get(bit >> 3, 0) | (1 << (track & 7))
        self.scr_track = track
        self.scr_bgm_vol = _i16(vol)
        return 0

    def cmd_MVOL(self, b: bytes, sep: int) -> int:
        """VA 0x87940: ramp the BGM to `017 over `001 ms (both default 0)."""
        tags, nt = parse_tags(b, sep)
        j = find_tag(tags, nt, TAG_FADE)
        ms = _i16(self._int(b, sep, j)) if j >= 0 else 0
        j = find_tag(tags, nt, TAG_VOL)
        v = _i16(self._int(b, sep, j)) if j >= 0 else 0
        self.history_push()
        if not self.silent:
            rate = _rate(abs(v - self.scr_bgm_vol), ms if ms >= 2 else 1)
            r = self.bgm[self.slot]
            if r.state != 0:                  # VA 0x9F20
                r.vol_rate = rate
                r.vol_to = F32(v)
        self.scr_bgm_vol = v
        return 0

    def cmd_MFAD(self, b: bytes, sep: int) -> int:
        """VA 0x87660: fade the BGM out over `001 ms and stop it; returns the wait in
        16-ms units unless `011 is present and 0."""
        self.history_push()
        tags, nt = parse_tags(b, sep)
        jf = find_tag(tags, nt, TAG_FADE)
        jw = find_tag(tags, nt, TAG_WAIT)
        ms = _i16(self._int(b, sep, jf)) if jf >= 0 else 0
        if not self.silent:
            if ms > 0:
                self.bgm_fade_out(_rate(self.scr_bgm_vol, ms))
            else:
                self.bgm_stop()
        self.scr_track = -1
        if jw >= 0 and _i16(self._int(b, sep, jw)) == 0:
            return 0
        self.wait16 = ((ms + 15) if ms < 0 else ms) >> 4    # word 0x1653E08
        return 1

    def cmd_MSTP(self, b: bytes, sep: int) -> int:
        """VA 0x978BD (engine-only): 0x9EE0 with the rate the caller leaves in xmm0 [H]."""
        raise NotImplementedError("_MSTP is not used by the scripts; see the effect table")

    # SE ------------------------------------------------------------------------
    def se_stop(self, ch: int) -> None:
        """VA 0xAAB0."""
        if ch > 0x13:
            return
        r = self.se[ch]
        r.state = 0
        if r.w8 != 0:
            self._stop(ch + 6)

    def se_fade_out(self, ch: int, ms: int) -> None:
        """VA 0xAC50: from the record's current volume to 0 over ms."""
        r = self.se[ch]
        if ms > 0:
            rate = F32(F32(F32(r.vol * MS_PER_S) / FPS) / F32(ms))
        else:
            rate = F32(100.0)
        if r.state != 0:
            r.state = 2
            r.vol_rate = rate
            r.vol_to = F32(0)

    def se_fade_out_rate(self, ch: int, rate: F32) -> None:
        """VA 0xAC20."""
        r = self.se[ch]
        if r.state != 0:
            r.state = 2
            r.vol_rate = F32(rate)
            r.vol_to = F32(0)

    def se_ramp(self, ch: int, target: int, rate: F32) -> None:
        """VA 0xACC0: ramp to ``target``; a rate >= 1666.67 (100 points in one frame) jumps."""
        r = self.se[ch]
        if r.state != 0:
            r.vol_rate = F32(rate)
            r.vol_to = F32(target)
            if F32(rate) >= SE_JUMP_RATE:
                r.vol = F32(target)

    def _se_gain(self, ch: int, vol: F32) -> F32:
        r = self.se[ch]
        c = r.char
        if 0 <= c - 1 <= 0x12:
            if not self.cfg.char_enable & (1 << (c - 1)):
                return F32(0)
            x = F32(F32(F32(vol) / F32(100.0)) * F32(_i16(self.cfg.char_vol[c - 1])))
            x = F32(x * F32(_i16(self.cfg.voice)))
            x = F32(x / F32(100.0))
        else:
            x = F32(F32(F32(_i8(self.vol[0xE])) * F32(vol)) / F32(100.0))
        x = _maxss(F32(0), x)
        return _minss(F32(100.0), x)

    def se_play_now(self, ch: int, entry: int, vol: F32) -> None:
        """VA 0xA300: start vse ``entry`` on channel ch at ``vol`` (0..100)."""
        g = self._se_gain(ch, vol)
        s = ch + 6
        self._call("open_handle", s)
        gv = F32(g * F32(0.01))
        self._call("set_volume0", s, _bits(gv))
        self._play(s, "vse", entry, int(self.se_loop[ch] != 0), gv)
        r = self.se[ch]
        r.state, r.kind, r.idx = 1, 1, entry
        r.vol = r.vol_to = F32(vol)
        r.vol_rate = F32(100.0)
        r.pan = r.pan_to = r.pan_rate = r.p3 = r.p3_to = r.p3_rate = F32(0)
        r.w8 = 1

    def se_play_fade(self, ch: int, entry: int, rate: F32, target: int) -> None:
        """VA 0xA770: start at volume 0 (ramp set up by the caller)."""
        s = ch + 6
        self._call("open_handle", s)
        self._call("set_volume", s, _bits(F32(0)), _bits(F32(0)))
        self._play(s, "vse", entry, int(self.se_loop[ch] != 0), F32(0))
        r = self.se[ch]
        r.state, r.kind, r.idx = 1, 1, entry
        r.vol = F32(0)
        r.vol_rate = F32(rate)
        r.vol_to = F32(target)
        r.pan = r.pan_to = r.pan_rate = r.p3 = r.p3_to = r.p3_rate = F32(0)
        r.w8 = 1

    def se_set_pan(self, ch: int, pan: int) -> None:
        """VA 0xA9E0: immediate pan (only on an SE-kind record)."""
        if ch > 0x13:
            return
        r = self.se[ch]
        if r.kind != 1:
            return
        r.pan = r.pan_to = r.pan_rate = r.p3 = r.p3_to = r.p3_rate = F32(0)
        self._call("set_pan", ch + 6, _bits(F32(F32(pan) * F32(0.001))))

    def _channel(self, b: bytes, sep: int) -> int:
        return _i16(_crem(self._arg0(b, sep), 20))

    def cmd_SEPL(self, b: bytes, sep: int) -> int:
        """VA 0x893F0 (also _SEFI)."""
        tags, nt = parse_tags(b, sep)
        if nt > 0:
            jd = find_tag(tags, nt, TAG_DELAY)
            if jd > 0 and not self.ff and not self.scan:
                self.defer_command(b, sep, 1)
                return 0
        name = _arg_text(b, sep, 2)
        ch = self._channel(b, sep)
        self.defer[ch].timer = 0
        vol, raw_vol, pan, fade = 100, -1, 0, 0
        if nt > 0:
            j = find_tag(tags, nt, TAG_FADE)
            if j >= 0:
                v = _i16(self._int(b, sep, j))
                fade = v + 1000 if self.se[ch].state == 2 else v
            j = find_tag(tags, nt, TAG_VOL)
            if j >= 0:
                raw_vol = _i16(self._int(b, sep, j))
                vol = min(max(raw_vol, 0), 100)
            j = find_tag(tags, nt, TAG_PAN)
            if j >= 0:
                pan = _pan_from_tag(_i16(self._int(b, sep, j)))
        if not name:
            self.history_push()
            if not self.silent:
                self.se_stop(ch)
            self.scr_se_loopid[ch] = -1
            return 0
        loop_arg = self._int(b, sep, 1) if inline_present(b, sep + 1, 1) else 0
        bx = int(_i16(loop_arg) != 0)
        flags = (2 * bx + 1) if raw_vol < 0 else 3
        res = self.se_lookup(name, flags)
        se_id = res[1] if res is not None else -1
        self.scr_se_id[ch] = _i16(se_id)
        r14 = _i16(self._int(b, sep, 1))
        if r14 == 1:
            self.history_push()
            if (self.scr_se_loopid[ch] == _i16(se_id) and self.scr_se_loop[ch] == 1
                    and self.scr_se_vol[ch] > 0):
                return 0
            self.scr_se_loopid[ch] = _i16(se_id)
            self.scr_se_loop[ch] = 1
        else:
            self.scr_se_loop[ch] = 0
            if self.skip:
                if self.scr_se_loopid[ch] >= 0:
                    self.se_stop(ch)
                self.scr_se_loopid[ch] = -1
                return 0
            self.scr_se_loopid[ch] = -1
        loop = r14 if bx else 0
        self.se_loop[ch] = loop
        j = find_tag(tags, nt, TAG_CHAR)
        char = _i8(self._int(b, sep, j)) if j >= 0 else 0
        self.scr_se_char[ch] = char & 0xFF
        self.se[ch].char = char & 0xFF
        if not self.silent:
            if fade >= 0x11:
                self.se_play_fade(ch, se_id, F32(2000.0), 0)
                self.se_ramp(ch, vol, _rate(vol, fade))
            else:
                self.se_play_now(ch, se_id, F32(vol))
            self.se_set_pan(ch, pan)
            self.scr_se_pan[ch] = pan
            if self.cfg.se_caption:
                self._call("se_caption", ch, se_id)
        else:
            self.scr_se_pan[ch] = pan
        self.scr_se_vol[ch] = raw_vol if raw_vol >= 0 else 100
        return 0

    cmd_SEFI = cmd_SEPL

    def _range(self, ch: int) -> range:
        lo = ch if ch > 0 else 0
        hi = ch if ch >= 0 else 0x13
        return range(lo, hi + 1)

    def cmd_SEFD(self, b: bytes, sep: int) -> int:
        """VA 0x89E40: fade out channel ch (or all for a negative ch) over `001 ms."""
        tags, nt = parse_tags(b, sep)
        if nt > 0:
            jd = find_tag(tags, nt, TAG_DELAY)
            if jd > 0 and not self.ff and not self.scan:
                self.defer_command(b, sep, 2)
                return 0
        j = find_tag(tags, nt, TAG_FADE)
        ms = _i16(self._int(b, sep, j)) if j >= 0 else 0
        ch = self._channel(b, sep)
        self.history_push()
        for c in self._range(ch):
            d = self.defer[c]
            if d.kind == 1 and d.timer > 0:     # a pending deferred _SEPL is cancelled
                d.timer = 0
                d.kind = 0
            if not self.silent:
                if ms > 0:
                    self.se_fade_out(c, ms)
                else:
                    self.se_stop(c)
            self.scr_se_loopid[c] = -1
        return 0

    def cmd_SEVL(self, b: bytes, sep: int) -> int:
        """VA 0x89B50: ramp channel ch to `017 over `001 ms (from the script volume)."""
        tags, nt = parse_tags(b, sep)
        if nt > 0:
            jd = find_tag(tags, nt, TAG_DELAY)
            if jd > 0 and not self.ff and not self.scan:
                self.defer_command(b, sep, 3)
                return 0
        j = find_tag(tags, nt, TAG_FADE)
        ms = _i16(self._int(b, sep, j)) if j >= 0 else 0
        j = find_tag(tags, nt, TAG_VOL)
        v = _i16(self._int(b, sep, j)) if j >= 0 else 0
        self.history_push()
        if not self.silent:
            ch = self._channel(b, sep)
            if ms > 0:
                rate = _rate(abs(v - self.scr_se_vol[ch]), ms)
            else:
                rate = F32(2000.0)
            self.se_ramp(ch, v, rate)
            self.scr_se_vol[ch] = v
        return 0

    def cmd_SESP(self, b: bytes, sep: int) -> int:
        """VA 0x996E1: stop channel ch (or all), cancel its deferred command."""
        ch = _i16(_crem(_i16(self._int(b, sep, 0)), 20))
        self.history_push()
        for c in self._range(ch):
            if not self.ff:
                self.se_stop(c)
            self.defer[c] = Defer.cleared()
            self.defer[c].fade = -1
            self.scr_se_loopid[c] = -1
            self.scr_se_id[c] = -1
        return 0

    # deferred (`068) commands ------------------------------------------------------
    def defer_command(self, b: bytes, sep: int, kind: int) -> None:
        """VA 0x88160: queue SEPL (1) / SEFD (2) / SEVL (3) for channel ch (all channels
        for a negative SEFD channel).  A pending command of the channel is executed first.
        Timer = max(1, trunc(delay_ms * 3 / 50)) frames."""
        tags, nt = parse_tags(b, sep)
        jd = find_tag(tags, nt, TAG_DELAY)
        delay = _i16(self._int(b, sep, jd))
        timer = _tdiv(delay * 3, 50) if delay * 3 + 0x31 >= 0x63 else 1
        self.history_push()
        ch = _crem(self._arg0(b, sep), 20)
        lo = ch if ch > 0 else 0
        hi = ch if ch >= 0 else 0x13
        if hi < lo:
            return
        se_id = -1
        if kind == 1:
            name = _arg_text(b, sep, 2)
            loop_arg = self._int(b, sep, 1) if inline_present(b, sep + 1, 1) else 0
            j = find_tag(tags, nt, TAG_VOL)
            flags = 3 if j >= 0 else 2 * int(_i16(loop_arg) != 0) + 1
            res = self.se_lookup(name, flags) if name else None
            se_id = res[1] if res is not None else -1
        for c in range(lo, hi + 1):
            if self.defer[c].kind != 0:
                self.run_deferred(c)
            d = Defer(timer, kind, -1, -1, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0)
            j = find_tag(tags, nt, TAG_FADE)
            if j >= 0:
                d.fade = _i16(self._int(b, sep, j))
            if kind == 1:
                d.se_id = se_id
                j = find_tag(tags, nt, TAG_VOL)
                if j >= 0:
                    d.vol = self._int(b, sep, j) & 0xFF
                j = find_tag(tags, nt, TAG_PAN)
                if j >= 0:
                    d.pan = self._int(b, sep, j) & 0xFF
                d.loop = self._int(b, sep, 1) & 0xFF if inline_present(b, sep + 1, 1) else 0
                j = find_tag(tags, nt, TAG_CHAR)
                d.char = self._int(b, sep, j) & 0xFF if j >= 0 else 0
            elif kind == 3:
                j = find_tag(tags, nt, TAG_VOL)
                if j >= 0:
                    d.vol = self._int(b, sep, j) & 0xFF
            self.defer[c] = d

    def run_deferred(self, ch: int) -> None:
        """VA 0x87D50: execute the channel's deferred command and clear the entry."""
        d = self.defer[ch]
        k = d.kind
        if k == 1:
            self._deferred_play(ch)
        elif k == 2:
            if not self.silent:
                fade = _i32(d.fade)
                if fade > 0 and self.scr_se_vol[ch] > 0:
                    self.se_fade_out_rate(ch, _rate(self.scr_se_vol[ch], fade))
                else:
                    self.se_stop(ch)
            self.scr_se_loopid[ch] = -1
        elif k == 3:
            if not self.silent and _i8(d.vol) >= 0:
                tgt = _i8(d.vol)
                fade = _i32(d.fade)
                fade = fade if fade >= 2 else 1
                cur = self.scr_se_vol[ch]
                cur = 100 if cur < 0 else min(cur, 100)
                self.se_ramp(ch, tgt, _rate(abs(tgt - cur), fade))
                self.scr_se_vol[ch] = tgt & 0xFF
        elif k == 4:
            raise NotImplementedError("deferred kind 4 (_SEAC) is not used by the scripts")
        self.defer[ch] = Defer.cleared()

    def _deferred_play(self, ch: int) -> None:
        """VA 0x87AF0."""
        d = self.defer[ch]
        fade_raw = d.fade
        if fade_raw > 0:
            fade = _i32(fade_raw) + 1000 if self.se[ch].state == 2 else _i32(fade_raw)
        else:
            fade = 0
        self.scr_se_id[ch] = _i16(d.se_id)
        if d.loop == 1:
            self.scr_se_loopid[ch] = _i16(d.se_id)
            self.scr_se_loop[ch] = 1
            loop = 1
        else:
            self.scr_se_loop[ch] = 0
            if self.skip:
                if self.scr_se_loopid[ch] >= 0:
                    self.se_stop(ch)
                self.scr_se_loopid[ch] = -1
                return
            self.scr_se_loopid[ch] = -1
            loop = 0
        self.se_loop[ch] = loop
        self.se[ch].char = d.char
        self.scr_se_char[ch] = d.char
        if self.silent:
            return
        if d.kind == 4:
            fade = 0
        self.scr_se_vol[ch] = 100
        vol = 100
        if _i8(d.vol) >= 0:
            vol = d.vol
            self.scr_se_vol[ch] = vol
        if fade >= 0x11:
            self.se_play_now(ch, _i16(d.se_id), F32(0))
            self.se_ramp(ch, vol, _rate(vol, fade))
        else:
            self.se_play_now(ch, _i16(d.se_id), F32(vol))
        if d.pan != 0xFF:
            pan = _pan_from_tag(abs(_i8(d.pan)))
        else:
            pan = 0
        self.se_set_pan(ch, pan)
        self.scr_se_pan[ch] = pan
        if self.cfg.se_caption:
            self._call("se_caption", ch, _i16(d.se_id))

    # voice ----------------------------------------------------------------------
    def cmd_VPLY(self, b: bytes, sep: int) -> int:
        """VA 0x968B2 (also _VPL2): remember the voice; the next _ZM plays it.  Returns 0
        (the scan / skip-mode branches are not modelled)."""
        name = _arg_text(b, sep, 0)
        self.voice_char = voice_char(name)
        nargs = _count_args(b, sep)
        self.voice_arg3 = self._int(b, sep, 2) & 0xFFFF if nargs >= 3 else 0
        if name.startswith("_"):
            idx = self.voice_pending
        else:
            idx = int(_arg_text(b, sep, 1) or "0", 16)
            self.voice_pending = idx
        self.voice_index = idx
        return 0

    cmd_VPL2 = cmd_VPLY

    def voice_start_at_message(self) -> Optional[Tuple[int, int, int]]:
        """The voice part of the _ZM handler (VA 0x9281E..0x92902): returns (voice entry,
        config slot, voice gain) when the pending voice is started, else None; always
        clears the pending voice."""
        idx = self.voice_pending
        self.voice_pending = -1
        if idx < 0:
            return None
        if self.skip and not self.cfg.skip_voice:
            return None
        if self.ff or not (self.cfg.voice != 0 and self.cfg.voice_on2 != 0):
            return None
        k = char_slot(self.voice_char)
        if not self.cfg.char_enable & CHAR_MASK[self.voice_char if self.voice_char < 24 else 0]:
            return None
        gain = _tdiv(_i16(self.cfg.char_vol[k]) * _i16(self.cfg.voice), 100)
        self.vol[0xD] = gain & 0xFF
        return idx, k, gain

    def stop_voice_on_advance(self, f1123: int = 0) -> bool:
        """VA 0xA1015..0xA1040 (also 0xA1149, 0xA124D): when the message ends the voice is
        stopped (0x8EA0: streams 2..5) if (F1123 == 0 and config +0x4E) or skip mode."""
        return bool((f1123 == 0 and self.cfg.stop_on_advance) or self.skip)

    # per-frame -----------------------------------------------------------------
    def duck_update(self, voice_active: bool) -> Optional[int]:
        """BGM ducking in the ADV frame (VA 0x694B9..0x6955F): the level (256 = full) falls
        by 4 per frame to 128 while a voice plays and config +0x1C is set, else rises by 2
        to 256; on change the BGM volume byte becomes level * BGM / 256.  Returns the new
        volume byte or None."""
        lv = self.duck_level
        if voice_active and self.cfg.duck:
            if lv >= 0x81:
                lv = max(lv, 0x84) - 4
        else:
            if lv <= 0xFF:
                lv = min(lv, 0xFE) + 2
        self.duck_level = lv
        if lv == self.duck_applied:
            return None
        self.duck_applied = lv
        x = F32(F32(F32(lv) * F32(1 - _i8(self.cfg.mute_bgm))) * F32(_i16(self.cfg.bgm)))
        x = F32(F32(F32(x * F32(256.0)) / F32(100.0)) * F32(0.001953125))
        x = F32(F32(x * F32(100.0)) * F32(0.0078125))
        x = _minss(F32(100.0), _maxss(F32(0), x))
        v = _trunc(x)
        self.vol[0xC] = v & 0xFF
        r = self.bgm[self.slot]
        if r.state != 0:
            g = _trunc(F32(F32(F32(_i8(v)) * r.vol) * F32(0.01)))
            g = min(max(g, 0), 100)
            self._call("set_volume", self.slot, _bits(F32(F32(g) * F32(0.01))), _bits(F32(0)))
        return v

    def update(self, dt: int = 1) -> None:
        """Sound frame update, VA 0xAD80(dt) (the ADV frame passes dt = 1 when a vsync
        elapsed, else 0)."""
        dec = min(self.vsyncs, 3) * dt
        for ch in range(20):                     # VA 0x880F0
            t = self.defer[ch].timer
            if _i16(t) > 0:
                t = _i16(t - dec)
                self.defer[ch].timer = t
                if t <= 0:
                    self.run_deferred(ch)
        dtf = F32(dt)
        for s in (0, 1):
            r = self.bgm[s]
            if r.state == 0:
                if self.is_playing(s):
                    self._stop(s)
                continue
            rate = r.vol_rate
            if not (F32(0) < rate or np.isnan(rate)):
                rate = RAMP_DEFAULT
                r.vol_rate = RAMP_DEFAULT
            if not self.is_playing(s):
                continue
            if not (r.vol == r.vol_to):
                r.vol = _ramp(r.vol, r.vol_to, F32(rate * dtf))
                g = _trunc(F32(F32(r.vol * F32(_i8(self.vol[0xC]))) * F32(0.01)))
                g = min(max(g, 0), 100)
                self._set_volume(s, F32(F32(g) * F32(0.01)))
            if r.state == 2 and not (F32(0) < r.vol or np.isnan(r.vol)):
                r.state = 0
                self._stop(s)
        for s in range(2, 6):
            r = self.voice[s - 2]
            if r.state == 0:
                continue
            rate = r.vol_rate
            if not (F32(0) < rate or np.isnan(rate)):
                rate = RAMP_DEFAULT
                r.vol_rate = RAMP_DEFAULT
            if self.is_playing(s) and not (r.vol == r.vol_to):
                r.vol = _ramp(r.vol, r.vol_to, F32(rate * dtf))
                vb = 0xE if r.kind == 1 else (0xF if r.kind == 3 else 0xD)
                g = _trunc(F32(F32(r.vol * F32(_i8(self.vol[vb]))) * F32(0.01)))
                g = min(max(g, 0), 100)
                self._set_volume(s, F32(F32(g) * F32(0.01)))
            if r.state == 2 and not (F32(0) < r.vol or np.isnan(r.vol)):
                r.state = 0
                self._set_volume(s, F32(0))
                self._stop(s)
        for s in range(6, 26):
            ch = s - 6
            r = self.se[ch]
            if r.state == 0:
                if self.is_playing(s):
                    self._stop(s)
                continue
            if r.w8 == 0:
                continue
            playing = self.is_playing(s)
            rate = r.vol_rate
            if not (F32(0) < rate or np.isnan(rate)):
                rate = RAMP_DEFAULT
                r.vol_rate = RAMP_DEFAULT
            if playing:
                r.vol = _ramp(r.vol, r.vol_to, F32(rate * dtf), always=True)
                vb = 0xE if r.kind == 1 else (0xF if r.kind == 3 else 0xD)
                c = r.char
                if 0 <= c - 1 <= 0x12:
                    g = self.cfg.char_gain(c - 1)
                else:
                    g = _i8(self.vol[vb])
                x = F32(F32(F32(g) * r.vol) * F32(0.01))
                v = F32(0) if x < 0 else F32(_minss(F32(100.0), x) * F32(0.01))
                self._set_volume(s, v)
                if g == 0 and r.vol > r.vol_to:
                    self._stop(s)
                    r.state = 0
                if r.pan_rate != 0 or np.isnan(r.pan_rate):
                    r.pan, r.pan_rate = _sweep(r.pan, r.pan_to, r.pan_rate, dtf)
                    self._call("set_pan", s, _bits(F32(r.pan * F32(0.001))))
                if r.p3_rate != 0 or np.isnan(r.p3_rate):
                    r.p3, r.p3_rate = _sweep(r.p3, r.p3_to, r.p3_rate, dtf)
            if r.state == 2 and not (F32(0) < r.vol or np.isnan(r.vol)):
                r.state = 0
                self._stop(s)

    # -- comparison helpers ----------------------------------------------------
    def records(self) -> List[bytes]:
        return [r.pack() for r in self.bgm + self.voice + self.se]


# ---------------------------------------------------------------------------
# small helpers
# ---------------------------------------------------------------------------
def _bits(v: Any) -> int:
    return int(struct.unpack("<I", np.float32(v).tobytes())[0])


def _i32(v: int) -> int:
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v >> 31 else v


def _trunc(x: F32) -> int:
    """vcvttss2si r32: truncation, integer indefinite for NaN / overflow."""
    if np.isnan(x) or np.isinf(x) or not (-2147483648.0 <= float(x) < 2147483648.0):
        return -0x80000000
    return int(np.trunc(np.float64(x)))


def _minss(a: F32, b: F32) -> F32:
    return a if a < b else b


def _maxss(a: F32, b: F32) -> F32:
    return a if a > b else b


def _ramp(cur: F32, tgt: F32, step: F32, always: bool = False) -> F32:
    """Move ``cur`` toward ``tgt`` by ``step`` (VA 0xAE53..0xAEA6 / 0xB1A1..0xB202)."""
    if not always and cur == tgt:
        return cur
    d = F32(tgt - cur)
    ad = _maxss(F32(-d), d)
    if step > ad:
        return F32(tgt)
    if cur > tgt:
        return F32(cur - step)
    return F32(step + cur)


def _sweep(cur: F32, tgt: F32, rate: F32, dt: F32) -> Tuple[F32, F32]:
    """Pan / third-parameter sweep (VA 0xB34D..0xB3B4): returns (value, rate)."""
    p = F32(F32(rate * dt) + cur)
    if rate < 0:                             # falling: stop when tgt >= p
        if tgt >= p:
            return F32(tgt), F32(0)
        return p, rate
    if p >= tgt:                             # rising (or NaN rate): stop when p >= tgt
        return F32(tgt), F32(0)
    return p, rate


def _arg_text(b: bytes, sep: int, index: int) -> str:
    """Argument ``index`` copied the way the SE / voice handlers copy a name: skip a
    5-byte '`' tag, stop at NUL ) , ; (bit set 0x0800120000000001)."""
    p = sep + 1
    for _ in range(index):
        for k in range(500):
            c = b[p + k] if p + k < len(b) else 0
            if c in (0x29, 0x3B):
                return ""
            if c == 0x2C:
                p = p + k + 1
                break
        else:
            p += 500
    if p < len(b) and b[p] == 0x60:
        p += 5
    out = bytearray()
    while p < len(b) and b[p] not in (0, 0x29, 0x2C, 0x3B):
        out.append(b[p])
        p += 1
    return out.decode("latin-1")


def _count_args(b: bytes, sep: int) -> int:
    """VA 0x99F12..0x99F5A: 1 + number of ',' before ')' / ';' (at most 0x1F3 bytes)."""
    n = 1
    k = sep + 1
    while k < len(b) and k - sep < 0x1F9:
        c = b[k]
        if c in (0x29, 0x3B):
            break
        if c == 0x2C:
            n += 1
        k += 1
    return n


# ---------------------------------------------------------------------------
# emulator <-> model comparison
# ---------------------------------------------------------------------------
def normalize_call(c: object) -> Call:
    """Map a ``misc_audio_runtime_semantics_emu.SoundCall`` to the model's call tuple."""
    from .misc_audio_runtime_semantics_emu import fake_entry
    m = c.method                                   # type: ignore[attr-defined]
    s = c.stream                                   # type: ignore[attr-defined]
    x0 = _bits(F32(c.xmm0))                        # type: ignore[attr-defined]
    x1 = _bits(F32(c.xmm1))                        # type: ignore[attr-defined]
    rdx, rcx = c.rdx, c.rcx                        # type: ignore[attr-defined]
    if m == 0x28:
        return ("open_file", s)
    if m == 0x30:
        return ("open_handle", s)
    if m == 0x38:
        return ("close", s)
    if m == 0x50:
        bank, entry = fake_entry(rdx)
        return ("play", s, bank, entry, rcx & 0xFF, x0)
    if m == 0x70:
        return ("stop", s, rdx & 0xFFFFFFFF)
    if m == 0x78:
        return ("is_playing", s)
    if m == 0x88:
        return ("set_flag6", s, rdx & 0xFFFFFFFF)
    if m == 0xA8:
        return ("set_volume", s, x0, x1)
    if m == 0xB8:
        return ("set_pan", s, x0)
    if m == 0x10FC90:
        return ("set_loop", s, rdx & 0xFFFFFFFF, _i32(rcx))
    if m == 0x10FFF0:
        return ("set_start_sample", s, rdx & 0xFFFFFFFF)
    if m == 0x10F8A0:
        return ("set_volume0", s, x0)
    return ("call", m, s)


def _main(argv: Optional[Sequence[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="python -m tsuki.misc_audio_runtime_semantics")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("groups")
    t = sub.add_parser("tags")
    t.add_argument("stmt")
    r = sub.add_parser("run")
    r.add_argument("items", nargs="+")
    a = ap.parse_args(argv)
    if a.cmd == "groups":
        from . import image_dir
        from .nam import parse_nam8
        img = image_dir()
        groups: List[str] = []
        if img is not None:
            for n in parse_nam8((img / "voice.nam").read_bytes()).names:
                if n not in groups:
                    groups.append(n)
        else:
            groups = sorted(VOICE_GROUP_CHAR)
        for g in groups:
            print(f"{g:5s} char {voice_char(g):2d} slot {group_slot(g):2d}")
        return 0
    if a.cmd == "tags":
        b = a.stmt.encode("latin-1")
        sep = b.index(b"_") + 5
        tags, nt = parse_tags(b, sep)
        print("ntags", nt, [f"{i}:{t:03d}" for i, t in enumerate(tags) if t != 0xFF])
        return 0
    m = SoundModel()
    m.default_playing = False
    from .audio import AudioError, load_se_names, se_lookup
    try:
        names = load_se_names()
        m.se_lookup = lambda n, f: se_lookup(names, n, f)
    except (AudioError, OSError) as e:
        print("se_nam.mrg not available:", e, file=sys.stderr)
    for it in a.items:
        if it.startswith("frame"):
            m.update(int(it[5:] or 1))
        else:
            m.run(it)
        for c in m.calls:
            if c[0] != "is_playing":
                print("  ", c)
        m.calls.clear()
    for i, rr in enumerate(m.bgm + m.voice + m.se):
        if rr.state or rr.vol:
            print(f"stream {i}: {rr}")
    return 0


if __name__ == "__main__":
    sys.exit(_main())
