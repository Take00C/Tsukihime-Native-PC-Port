# Sound runtime: channels, commands, tags, fades, ducking, voice gating

Scope: how TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999, app 01.01) plays sound at
run time, once the codecs of `audio.md` have produced PCM. It covers the channel model (BGM
slots, voice streams, script SE channels, HE-VAG system sounds), every script audio command and
keyword tag, the deferred-SE queue, the per-frame mixer update (volume / pan ramps,
fade-to-stop, the per-character voice gate), the gain arithmetic, the voice pipeline (`_VPLY`
to playback, stop on advance, BGM ducking), the voice-group -> character -> config-slot table,
the system SEs and what triggers the 19 vag sounds, and how the sound state meets history /
skip / fast-forward. It answers the open questions of `audio.md` section 10 that are about
playback (3, 4, 5, 7) and applies its six review findings (section 14).

Python reference: `tools/tsuki/misc_audio_runtime_semantics.py` (model) with
`misc_audio_runtime_semantics_emu.py` (unicorn harness running the eboot's own code),
`misc_audio_runtime_semantics_vex.py` (Python execution of VEX instructions) and
`misc_audio_runtime_semantics_verify.py` (engine-vs-model comparison).
Tests: `tools/tests/test_misc_audio_runtime_semantics.py`. Samples:
`samples/misc_audio_runtime_semantics/`. Scratch: `scratch/misc_audio_runtime_semantics/`.
`docs/formats/misc_audio_runtime_semantics.md` is a pointer to this file.

Confidence labels:

- **[EMU-corpus]**: the engine's handlers and frame update were executed under unicorn on the
  audio statements of every script, in script order with random frame updates in between, and
  compared after every step with the Python model: all 26 channel records byte for byte, the
  BGM / SE globals, the script-side arrays, the 20 deferred-command entries, the music bits and
  every call to the sound library (section 13). 0 differences.
- **[EMU-n]**: the same engine-vs-model comparison on n synthetic cases.
- **[EMU]**: observed by running the engine code under unicorn (no model comparison).
- **[E]**: read from the disassembly (text VA = file offset - 0x2280; data VA = file offset -
  0x365FA0 + 0x600000).
- **[D]**: counted in the script / audio data.
- **[H]**: hypothesis.

All floating point is IEEE float32 in the engine's operation order; the model reproduces it
with `numpy.float32` and matches the engine's record bytes exactly.

---

## 1. Overview

```
script statement --ScriptStep 0x95E60--> handler (MPLY 0x87250, SEPL 0x893F0, ...)
      |  tag scan (3.1), argument parse, History_Push 0x7AF90
      |  `068 present -> deferred-SE queue 0x16543F0 (5)
      v
channel records (0x34 bytes, 2.2): BGM 0xDD86A0, voice 0xDD8B20, SE 0xDD8710
      |                                   ^ per-frame update 0xAD80(dt=1) (6):
      |                                   |   deferred timers, volume / pan ramps,
      |                                   |   fade-to-stop, character gate
      v                                   |
nexl_Sound object *0xDD9028 (vtable relro 0x432318): open / play / stop / set_volume /
set_pan on "streams" 0..25  ->  ATRAC9 decoder (audio.md 2) -> float mixer -> AudioOut

voice: _VPLY stores (entry, character) -> next _ZM 0x92380 gates by character and starts it
       (0xC3A80 -> 0x8F60 load -> 0x9220/0x8BD0 play on stream 2..5) -> stopped when the text
       advances (cfg +0x4E); BGM ducked while it plays (cfg +0x1C, 0x694B9)
UI:    SystemSE_Play(id) 0xCC670 -> HE-VAG vag.mrg pair (9 stereo sounds), system SE volume
```

Frame rate: the ADV frame calls the update with dt = 1 whenever at least one vsync elapsed
(VA 0xE9221: `dt = (0xD47C0() > 0)`), so every rate below is "per frame" and assumes 60 fps; a
`_X(..., `001:ms)` fade lasts ms * 60 / 1000 frames. The deferred-command timers count vsyncs
(0xD47C0 = min(word 0xDC7BE0, 3)) [E].

---

## 2. Channel model

### 2.1 Streams

| stream | record (data VA) | used by | volume byte (2.4) | evidence |
|---:|---|---|---|---|
| 0, 1 | 0xDD86A0 + 0x34 s | BGM, two slots that alternate on every new track (current slot u32 0xDD8F28) | +0xC BGM | 0x98C0, 0xAD80 [EMU-corpus] |
| 2..5 | 0xDD8B20 + 0x34 (s-2) | voice; the voice stream rotates `s = 2 + (n & 3)` with n = u32 0xDD8F08 incremented per voice; all four are stopped first | +0xD (kind 0/2), +0xE (kind 1), +0xF (kind 3) | 0x8BD0 (0x8DEC..0x8E8F), 0x8F60 [E] |
| 6..25 | 0xDD8710 + 0x34 (s-6) | script SE channel c = s - 6 (`_SEPL(c, ...)`, c = 0..19) | +0xE (kind 1), per-character gain when record +0x30 = 1..19 | 0xA300, 0xAD80 [EMU-corpus] |
| HE-VAG voices 0x2C..0x2F (and 0x28..0x2B) | - | system SEs (HE-VAG vag.mrg) | +0xF | 0xCC670, 0x8560 [E] |

A port needs 26 decoder "streams" plus a small HE-VAG voice pool. Only one voice plays at a
time (the request stops streams 2..5 first); the rotation just lets the previous voice's
stream finish closing.

### 2.2 Channel record (0x34 bytes)

| off | type | field | BGM | SE | voice |
|---:|---|---|---|---|---|
| 0x00 | u32 | state: 0 idle, 1 playing, 2 fading out (stop when the volume reaches 0) | yes | yes | yes |
| 0x04 | u32 | volume class ("kind"): 1 = SE (+0xE), 3 = system (+0xF), else voice (+0xD) | 0 | 1 | 0 (the `_ZM` start passes 0) |
| 0x08 | u16 | 1 = a stream was started (the frame update skips the record when 0) | - | yes | - |
| 0x0A | u16 | bgm.hed / vse.hed entry | yes | yes | - |
| 0x0C | f32 | current volume, 0..100 (the script's percentage) | yes | yes | yes (100) |
| 0x10 | f32 | target volume | yes | yes | yes |
| 0x14 | f32 | ramp step per frame (<= 0 is replaced by 0.2) | yes | yes | yes |
| 0x18 | f32 | current pan, -1000..1000 (stream pan = value * 0.001) | 0 | yes | 0 |
| 0x1C | f32 | pan target | | yes | |
| 0x20 | f32 | pan step per frame (0 = no sweep) | | yes | |
| 0x24 / 0x28 / 0x2C | f32 | third ramp (current, target, step), set only by the unused `_SEP3`; never sent to the stream | | yes | |
| 0x30 | u8 | character 1..19 (`` `070 ``) for a voice-like SE, else 0 | untouched | yes | |

[EMU-corpus for BGM and SE; E for voice]

### 2.3 Globals

| data VA | type | meaning | written by |
|---|---|---|---|
| 0xDD8F28 | u32 | current BGM slot | 0x98C0 / 0x9B40 (`xor 1`) |
| 0x62437C | i32 | BGM track started last, -1 = none (initial -1) | 0x98C0 / 0x9B40 / stop 0x9A90 / fade 0x9EE0 |
| 0x624380 | i32[2] | BGM loop flag per slot (initial 1, 1); 1 = loop forever | 0x9EA0 (both slots) |
| 0xDD8FC0 | i32[20] | SE loop flag per channel | 0xAA80 |
| 0xDD8F08 | u32 | voice stream counter (stream = 2 + n) | 0x8BD0 |
| 0xDD8E50 | u16 | voice state: 0 idle, 1 loading, 2 playing, 3..6 pause / prefetch states | 0x8F60, 0x9220 |
| 0x16545D0 | i16 | track of the last `_MPLY` (-1 after `_MPLY(-n)` / `_MFAD`) | MPLY, MFAD |
| 0x1654238 | i16 | volume of the last `_MPLY` / `_MVOL` (the "from" volume of fades) | MPLY, MVOL |
| 0x1654210 | i16[20] | vse entry of the last `_SEPL` per channel | SEPL |
| 0x1654240 | i16[20] | vse entry of the channel's looping SE, -1 = none | SEPL / SEFD / SESP |
| 0x1654270 | i16[20] | script volume per channel (the "from" volume of `_SEVL`) | SEPL / SEVL |
| 0x16542A0 | i16[20] | 1 = the channel's last SE was looped | SEPL |
| 0x16542D0 | i16[20] | pan per channel (-1000..1000) | SEPL |
| 0x1654330 | u8[20] | `` `070 `` character per channel (see 11.2 for a History_Push quirk) | SEPL |
| 0x16543F0 | 20 x 0x18 | deferred SE commands (5) | `` `068 `` |
| 0x164D5C0 | bits | bit 3200 + track = "track heard" (music room unlock) for tracks 0..519 | MPLY |
| 0x16541FC / 0x1654200 / 0x1654204 | i32 | voice entry requested by `_VPLY` (-1 = none) | VPLY, `_ZM`, VCLR |
| 0x1654208 | u16 | character of the pending voice | VPLY |
| 0x165420A | u16 | third `_VPLY` argument (0 if absent), kept in the history record +0x3641 | VPLY |
| 0x120A6D2 | u16 | "ff": history fast-forward / replay; sound commands only update script state | ADV |
| 0x174DE48 | u8 | "scan": history scan / restore; sound commands only update script state | ADV |
| 0x17DB232 | u16 | skip mode | ADV |
| 0x7A6184 / 0x7A608C | i32 | ducking level (256 = full) / level last applied (-1 = force) | 0x694B9, 0x674B0 |

### 2.4 Volume block and config fields

`Sound_ApplyConfig(full)` (VA 0x3E9B0, rdi = 1) fills the volume block 0x17DA268 from the
config (0x66C578, defaults from VA 0x3EAC0) [EMU]:

| block byte | value | default | meaning |
|---|---|---:|---|
| +0xB | (1 - cfg[+0xA6]) * cfg u16 +0x54 | 40 | movie volume (+0x54 is a copy of +0x14, set by 0x3E6E0) |
| +0xC | (1 - cfg[+0xA6]) * cfg +0x14 | 40 | BGM volume (lowered by ducking, 9) |
| +0xD | cfg +0x16 (full = 1), then per voice `char_vol[k] * cfg+0x16 / 100` (the gate, 7 step 2); `Sound_ApplyConfig(0)` (the config screen's per-item call, 0x40093) leaves it alone unless a row was tested ([0x66C720] >= 0): then `cfg.76[row] * cfg.16 / 100` through 0xB6F0, which also sets the playing voice stream's volume (adv_state.md 9.4) | 100 | voice volume of the current voice |
| +0xE | (1 - cfg[+0xA7]) * cfg +0x18 | 40 | SE volume |
| +0xF | (1 - cfg[+0xA8]) * cfg +0x1A | 10 | system SE volume |

| config field | default | meaning [E] |
|---|---:|---|
| u16 +0x14 | 40 | BGM volume (menu item 0x51) |
| u16 +0x16 | 100 | voice master: menu item 0x5C, **on = 100, off = 0** (VA 0x3E855) |
| u16 +0x18 | 40 | SE volume (item 0x55) |
| u16 +0x1A | 10 | system SE volume (item 0x5A) |
| u16 +0x1C | 0 | lower BGM while a voice plays (9) |
| u16 +0x1E | 0 | 1 = 5.1ch BGM set (entries n + 131, absent on PS4; audio.md 3) |
| u16 +0x2C | 0 | 1 = SE notification hook (0xE7EE0 + 0xCD610 after every `_SEPL`) [H: SE captions] |
| u16 +0x30 | 1 | in skip mode, still play voices (7, step 2) |
| u16 +0x36 | not written by 0x3EAC0 | second voice enable, `0x41AB0 = cfg+0x16 != 0 && cfg+0x36 != 0` |
| u32 +0x3C | 0x3FFFFFFF | bit k = voice of config slot k on (k = 0..18) |
| u16 +0x4E | 1 | stop the voice when the text advances (item 0x5E) |
| u16 +0x76 + 2k | 50 | voice volume of config slot k, **k = 0..18 (19 slots)** |
| u8 +0xA6 / +0xA7 / +0xA8 | 0 | mute BGM+movie / SE / system SE (items 0x52, 0x57, 0x5B) |

### 2.5 Sound library interface (nexl_Sound, vtable relro 0x432318)

| vtable / VA | name (ours) | arguments | notes |
|---|---|---|---|
| +0x28 (0x10EAA0) | open_file | stream, path | BGM: `/app0/image/bgm.mrg` |
| +0x30 (0x10EB10) | open_handle | stream, file | SE: the vse.mrg handle of slot 0x1C; voice: the decoded RIFF in memory |
| +0x38 (0x10EB80) | close | stream | |
| +0x50 (0x10EF70) | play | stream, byte offset, loop (0/1), volume 0..1, 0, 0 | offset = HED sector * 0x800 (0xC040 / 0xC080) |
| +0x58 (0x10F1C0) | status bit 3 | stream | voice state machine |
| +0x70 (0x10F530) | stop | stream, fade (frames; /60 s) | all callers pass 0 |
| +0x78 (0x10F580) | is_playing | stream | |
| +0x88 (0x10F5E0) | set flag 6 | stream, on | 0x98C0 passes 0 after play |
| +0xA8 (0x10FA00) | set_volume | stream, volume (clamped 0..1), time | all callers pass time 0 |
| +0xB8 (0x10FAA0) | set_pan | stream, pan (clamped -1..1) | |
| 0x10FC90 | set_loop | stream, on, count | `(1, -1)` = forever, `(0, 1)` = once |
| 0x10FFF0 | set_start_sample | stream, sample | `` `069 `` |
| 0x10F8A0 | set_volume0 | stream, volume | volume before play |

The stereo pan law and the float mixer are in audio.md 7.

---

## 3. Script commands and tags

### 3.1 Tag scan (all audio handlers) [E VA 0x87276..0x87308, EMU-corpus]

```
parse_tags(p = first byte after '('):
    ntags = 0
    for i in 0..63:
        tag[i] = 0xFF
        if s[p] == '`':
            tag[i] = ((d1*10 + d2)*10 + d3 + 0x30) mod 256   # d1..d3 = the next 3 bytes (ASCII)
                                                       # = the 3-digit number mod 256
            p += 5; ntags = i + 1
        scan from p (at most 500 bytes): ',' -> next argument; ')' or ';' -> stop
find(T) = first i < ntags with tag[i] == T, else -1
value   = inline_int(statement, i)        # script.md 3.3 (sum of decimal terms, 16 or 32 bit)
```

Tags are found by number at any position; the positions themselves do not matter. The
channel / track argument is argument 0 read with the same inline-integer rules (the handlers
inline a copy of the reader), and SE channels are `arg0 % 20` (C remainder, so -1 stays -1).

| tag | byte | `_MPLY` | `_MVOL` | `_MFAD` | `_SEPL` | `_SEFD` | `_SEVL` | scripts [D] |
|---|---:|---|---|---|---|---|---|---|
| `` `001 `` | 0x01 | fade-in ms (0) | ramp ms (0) | fade-out ms (0) | fade-in ms (0; >= 17 ms fades, +1000 if the channel is fading out) | fade-out ms (0 = stop) | ramp ms (0 = jump) | SEPL 2,788, SEFD 3,516, SEVL 1,285, MPLY 369, MVOL 780, MFAD 724 |
| `` `011 `` | 0x0B | fade-out ms of the old track (0 = use `001) | - | wait flag: absent or != 0 -> the handler waits ms/16 | - | - | - | MFAD 724 (all 0) |
| `` `017 `` | 0x11 | volume (100) | target (0) | - | volume (100), clamped 0..100 | - | target (0) | SEPL 14,540, SEVL 1,285, MPLY 1,170, MVOL 780 |
| `` `018 `` | 0x12 | - | - | - | pan 0..128, 64 = centre: pan = (v*1000 - 64000) / 64 | - | - | SEPL 15 |
| `` `049 `` | 0x31 | loop: 1 = forever (default 1), else once | - | - | - | - | - | MPLY 1 (`_MPLY(122,,`017:100,,`049:0)`) |
| `` `068 `` | 0x44 | - | - | - | delay ms (deferred, 5) | delay ms | delay ms | SEPL 3,471, SEFD 583, SEVL 40 |
| `` `069 `` | 0x45 | start sample (i32) | - | - | - | - | - | MPLY 131 |
| `` `070 `` | 0x46 | - | - | - | character 1..19 (voice-like SE, 6.3) | - | - | SEPL 19 |

(The value in parentheses is the default when the tag is absent.) `` `068 `` is honoured only
when it is not the first argument (index > 0, true for every script use) and the game is
neither in "ff" nor "scan" state; otherwise the command executes at once.

### 3.2 `_MPLY(track, , tags)` [E VA 0x96E2C -> 0x87250; EMU-corpus]

```
MPLY:
    History_Push(stmt, 2)                                   (11)
    vol = `017 or 100; loop = `049 or 1; fade = `001 or 0; t011 = `011 or 0; start = `069 or 0
    track = i16(arg0)
    if track < 0:
        if !ff && !scan: BGM_Stop()                         # 0x9A90: stop(slot), close(slot), track = -1
        scr_track = -1; return
    if !scan:
        if !ff && bgm_track != track:                       # same track: nothing reaches the library
            if fade == 0 && is_playing(slot): BGM_Stop()
            BGM_SetLoop(loop)                               # 0x9EA0: both slot flags; set_loop(slot, ...)
            set_start_sample(slot ^ 1, start)               # 0x9890 (the next slot)
            if fade != 0:
                new_rate = ((vol*1000)/60)/fade
                old_rate = ((scr_bgm_vol*1000)/60)/(t011 ? t011 : fade); if !(old_rate > 0): 100
                BGM_FadeOut(old_rate)                       # 0x9EE0: state 2, target 0; bgm_track = -1
                BGM_StartFade(track, vol, new_rate)         # 0x9B40
            else:
                BGM_Start(track, vol)                       # 0x98C0
        if (u16)track <= 0x207: music_bit[3200 + track] = 1
    scr_track = track; scr_bgm_vol = vol
```

`BGM_Start` (0x98C0) [EMU-corpus]:

```
if track < 0: BGM_Stop(); return
if bgm_track == track: return                               # finding (b)
if rec[slot].state == 1: rec[slot].state = 2; target 0; rate 4.0     # old track: 25 frames from 100
bgm_track = track; slot ^= 1
entry = (cfg+0x1E == 1 && 1 <= track <= 131) ? track + 131 : track
rec[slot] = {state 1, kind 0, entry, vol = target = rate = vol, pan..p3 = 0}   (+8, +0x30 untouched)
g = trunc(vol_byte[0xC] * vol * 0.01)                       # finding (c): an integer percentage
open_file(slot); play(slot, offset(entry), loopflag[slot] != 0, g * 0.01); set_flag6(slot, 0)
```

`BGM_StartFade` (0x9B40) differs only in: the new record starts at volume 0 with target `vol`
and step `new_rate`, pan fields are not cleared, `set_volume0(slot, 0)` precedes `play(..., 0.0)`,
and there is no set_flag6 call. Worked example (bgm volume 40): `_MPLY(7,,`017:60,`001:2000)`
after a track at 80 -> the old slot gets state 2, rate 80*1000/60/2000 = 0.6667 (120 frames),
the new slot ramps 0 -> 60 at 0.5 per frame; every frame the stream volume is
trunc(cur * 40 * 0.01) * 0.01 (section 6) [EMU].

BGM loops by default: `` `049 `` defaults to 1 = loop forever over the smpl loop (audio.md 2.4);
only `_MPLY(122,,`017:100,,`049:0)` plays once [D, EMU].

### 3.3 `_MVOL`, `_MFAD`, `_MSTP` [E VA 0x87940, 0x87660, 0x978BD; EMU-corpus for MVOL/MFAD]

```
MVOL(,, `001:ms ,, `017:v):   History_Push; if !ff && !scan:
        rate = (|v - scr_bgm_vol| * 1000 / 60) / (ms >= 2 ? ms : 1)
        if rec[slot].state != 0: rec[slot].target = v; rec[slot].rate = rate     # 0x9F20
    scr_bgm_vol = v                                          # v defaults to 0
MFAD(,, `001:ms ,, `011:w):   History_Push; if !ff && !scan:
        ms > 0 ? BGM_FadeOut((scr_bgm_vol*1000/60)/ms) : BGM_Stop()
    scr_track = -1
    if `011 present and == 0: return 0                        # every script use
    word 0x1653E08 = ms >> 4; return 1                        # the interpreter waits (ms/16 units)
MSTP (unused): BGM_FadeOut with the xmm0 the dispatcher leaves (rate 4.1667 observed) [EMU]
```

### 3.4 `_SEPL(ch, loop, name, , tags)` (also `_SEFI`) [E VA 0x98C8F -> 0x893F0; EMU-corpus]

```
SEPL:
    if `068 at index > 0 && !ff && !scan: Defer(stmt, kind 1); return          (5)
    name = argument 2 (skip a 5-byte tag; up to , ) ;)
    ch = i16(arg0) % 20
    defer[ch].timer = 0                       # a pending deferred command is suspended (its kind stays)
    vol = 100, raw = -1, pan = 0, fade = 0
    `001: fade = v + (rec[ch].state == 2 ? 1000 : 0)
    `017: raw = v; vol = clamp(v, 0, 100)
    `018: pan = (v*1000 - 64000) / 64       (C division)
    if name == "": History_Push; if !ff && !scan: SE_Stop(ch); scr_loopid[ch] = -1; return
    bx = (argument 1 non-empty && int != 0)
    id = se_nam lookup(name, flags = raw < 0 ? 2*bx + 1 : 3)    # audio.md 4.2; flag 1 skips section 0
    scr_id[ch] = id
    L = i16(arg1)
    if L == 1:
        History_Push
        if scr_loopid[ch] == id && scr_loop[ch] == 1 && scr_vol[ch] > 0: return  # same looped SE: ignored
        scr_loopid[ch] = id; scr_loop[ch] = 1
    else:
        scr_loop[ch] = 0
        if skip: (if scr_loopid[ch] >= 0: SE_Stop(ch)); scr_loopid[ch] = -1; return   # one-shots skipped
        scr_loopid[ch] = -1
    se_loop[ch] = bx ? L : 0
    char = `070 or 0; scr_char[ch] = char; rec[ch].char = char
    if !ff && !scan:
        if fade >= 17: SE_PlayFade(ch, id) ; SE_Ramp(ch, vol, ((vol*1000)/60)/fade)
        else:          SE_PlayNow(ch, id, vol)
        SE_SetPan(ch, pan); scr_pan[ch] = pan
        if cfg+0x2C: notify(0x1654210, ch, id) [0xE7EE0, 0xCD610]
    else: scr_pan[ch] = pan
    scr_vol[ch] = raw >= 0 ? raw : 100
```

SE primitives [EMU-corpus]:

```
SE_PlayNow(ch, id, vol)  0xA300:
    c = rec.char
    if 1 <= c <= 19: g = (cfg.3C bit c-1) ? (((vol/100) * char_vol[c-1]) * cfg.16) / 100 : 0
    else:            g = (vol_byte[0xE] * vol) / 100
    g = min(100, max(0, g))                                   # float, no truncation for SEs
    open_handle(s); set_volume0(s, g*0.01); play(s, offset(id), se_loop[ch] != 0, g*0.01)
    rec = {state 1, kind 1, w8 1, id, vol = target = vol, rate 100, pan..p3 = 0}
SE_PlayFade(ch, id)  0xA770: open_handle; set_volume(s, 0); play(s, ..., 0.0);
    rec = {state 1, kind 1, w8 1, id, vol 0, target 0, rate 2000, pan..p3 = 0}
SE_Ramp(ch, v, rate) 0xACC0: if state: target = v; rate; if rate >= 1666.67: vol = v (jump)
SE_SetPan(ch, pan)   0xA9E0: if kind == 1: pan..p3 = 0; set_pan(s, pan * 0.001)
SE_Stop(ch)          0xAAB0: state = 0; if w8: stop(s), close(s)
SE_FadeOut(ch, ms)   0xAC50: rate = ms > 0 ? (rec.vol*1000/60)/ms : 100; if state: state 2, target 0
```

The `_SEPL` loop flag is the only thing that decides looping: loop = 1 plays the vse file's
smpl loop forever (a "whole" loop repeats the file), loop = 0 plays it once. Over the corpus
loop = 0 is used 10,785 times and loop = 1 3,758 times (audio.md 2.4 has the split by loop
kind) [D].

### 3.5 `_SEFD`, `_SEVL`, `_SESP` [E VA 0x89E40, 0x89B50, 0x996E1; EMU-corpus]

```
SEFD(ch|-1, , , `001:ms [, , `068:d]):
    `068 -> Defer(kind 2) (all 20 channels for a negative ch, 5)
    for c in (ch < 0 ? 0..19 : ch):          # after History_Push
        if defer[c].kind == 1 && defer[c].timer > 0: defer[c].timer = 0; defer[c].kind = 0  # cancel
        if !ff && !scan: ms > 0 ? SE_FadeOut(c, ms) : SE_Stop(c)
        scr_loopid[c] = -1
SEVL(ch, , , , `001:ms, , `017:v [, , `068:d]):
    `068 -> Defer(kind 3); History_Push
    if !ff && !scan: rate = ms > 0 ? (|v - scr_vol[ch]| * 1000/60)/ms : 2000 (a jump)
                     SE_Ramp(ch, v, rate); scr_vol[ch] = v
SESP(ch|-1): History_Push; for c in range: if !ff: SE_Stop(c)
             defer[c] = cleared; scr_loopid[c] = -1; scr_id[c] = -1
```

`_SEVL` ramps from the *script* volume of the channel, `_SEFD` from the record's current
volume. Note that `_SEVL` does not handle -1: `ch = -1` would address the record before
channel 0 (no script does this).

### 3.6 Voice commands [E VA 0x968B2, 0x96504; EMU]

```
VPLY / VPL2(name, hex [, n]):
    if scan: return (yield)
    name -> 0x16543C0; char = group table (8) of the first 3 characters -> 0x1654208
                                                         # a name starting with '_' keys "_xx": no group, char 0
    G[73] |= (u16)(1 << char)                            # "characters heard" bits (0x164C6B2, VA 0x99F08);
                                                         # chars 16..23 shift out of the 16-bit word
    arg3 = (number of arguments >= 3) ? i16(argument 2) : 0 -> 0x165420A, history +0x3641
    entry = name starts with '_' ? previous entry : hex(argument 1) -> 0x16541FC/0x1654200/0x1654204
    VoiceGallery_Heard(char, entry)                      # 0x3D260 (VA 0x9A058): returns for char 23 or entry < 0;
                                                         #   k = log2(mask[char < 24 ? char : 0]) (table 0x86A850)
                                                         #   G[31 + k] = (u16)entry; G[0x1A6850[k]] |= 4 (the row words,
                                                         #   i32[19]: the Sound tab shows row k by this bit; adv_state.md 9.4)
    VPL2 only: play immediately (same gate and gain as 7, step 2) and clear the pending voice
VCWT(): wait while voice state 0xDD8E50 != 0              # 0x9210
VCLR() (unused): pending voice = -1
```

`_VPLY(A10_07_00_0147_1,01b40,0)` (script 212) is the only 3-argument call: its index 0x1B40 =
6976 is a normal voice reference and the third argument (0) only goes to the history record
[EMU]. The `_VPLY` statements therefore reference 16,705 voices with 16,459 distinct names =
16,459 distinct indices (finding (a)).

### 3.7 Engine-only commands (no script uses them) [EMU]

| command | handler | observed effect |
|---|---|---|
| `_MSTP` | 0x978BD | BGM fade-out (state 2), track -1 |
| `_SEPN(ch, a, b, `001)` | 0x8A340 | pan from a to b (same 0..128 scale) or an immediate pan |
| `_SEP3(ch, a, b, `001)` | 0x8A5E0 | third ramp (+0x24..+0x2C) |
| `_SEAC(...)` | 0x8A880 | deferred command (kind 2/4 path of 5) |
| `_VCLR()` | 0x97277 | pending voice = -1 |

The emulator-backed per-case table (statement, initial state, every changed field, every
library call, model result) is `samples/misc_audio_runtime_semantics/effect_table.json`
(41 cases, 0 model mismatches on the 33 modelled ones).

---

## 4. Worked examples (bgm 40, SE 40, voice 100, char volumes 50) [EMU]

| statement | result |
|---|---|
| `_MPLY(5,,`017:80);` | slot 1 = {1, entry 5, 80, 80, 80}; play(1, bgm 5, loop 1, 0.32) (trunc(40*80*0.01) = 32) |
| `_MPLY(5,,`017:40);` again | no call; only scr_bgm_vol = 40 |
| `_MPLY(-1);` | stop(slot), close(slot), track = -1, scr_track = -1 |
| `_SEPL(3,0,FATE_SE001,,`017:80);` | stream 9: play(vse 1, loop 0, 0.32) (40*80/100 = 32.0 as float) |
| `_SEPL(4,1,FATE_SE001,,`017:50,`001:1000,`018:96);` | stream 10 starts at 0, ramps 0.8333/frame to 50; set_pan(0.5) |
| `_SEPL(5,0,FATE_SE001,,`017:80,`068:500);` | queued: timer 30 frames; plays on the 30th update |
| `_SEFD(-1,,,`001:300);` | every active channel: state 2, rate = vol*1000/60/300 |
| `_SEPL(5,0,SEV_A10_14_01A_0007_0,,`017:100,,`070:2);` | char 2 = config slot 1 (Arcueid): gain 100/100*50*100/100 = 50 -> 0.5 |

---

## 5. Deferred SE commands (`` `068 ``) [E VA 0x88160, 0x880F0, 0x87D50; EMU-corpus]

Entry (0x16543F0 + 0x18 ch):

| off | type | field |
|---:|---|---|
| 0x00 | i16 | timer, frames |
| 0x02 | u8 | kind: 1 `_SEPL`, 2 `_SEFD`, 3 `_SEVL` (4 = `_SEAC` path) |
| 0x04 | i16 | vse entry (kind 1), else -1 |
| 0x08 | i64 | `` `001 `` ms, -1 = absent |
| 0x10 | u8 | `` `017 `` (0xFF absent) |
| 0x11 | u8 | second volume (SEAC) |
| 0x12 | u8 | `` `018 `` raw value (0xFF absent) |
| 0x13 | u8 | second pan (SEAC) |
| 0x14 | u8 | loop argument (kind 1; 0xFF otherwise) |
| 0x15 | u8 | `` `070 `` (kind 1) |

```
Defer(stmt, kind):
    timer = (3*d + 49 >= 99) ? trunc(3*d / 50) : 1           # = max(1, d * 60 / 1000) frames
    History_Push; ch = i16(arg0) % 20; range = ch < 0 ? 0..19 : ch      # for every kind
    kind 1: id = lookup(name, `017 present ? 3 : 2*loop + 1)   # resolved now
    for c in range:
        if defer[c].kind != 0: Execute(c)                    # a pending command runs at once
        defer[c] = {timer, kind, id, `001, `017, `018, loop, `070}
Update (0x880F0, start of every frame update):
    for c in 0..19: if defer[c].timer > 0:
        timer -= vsyncs * dt; if timer <= 0: Execute(c)
Execute(c) 0x87D50:
    kind 1: DeferredPlay(c)                                  # 0x87AF0, as _SEPL with these differences:
            fade = `001 (+1000 if fading out); no "same looped SE" check; vol = `017 or 100;
            fade >= 17: SE_PlayNow(c, id, 0) then SE_Ramp(vol, ...); pan = conv(|`018|)
    kind 2: if !ff && !scan: (`001 > 0 && scr_vol[c] > 0) ? fade out at (scr_vol*1000/60)/ms
                                                          : SE_Stop(c)
            scr_loopid[c] = -1
    kind 3: if !ff && !scan && `017 >= 0: SE_Ramp(c, v, |v - min(scr_vol, 100)| * 1000/60/max(ms,1))
            scr_vol[c] = v
    then defer[c] = {0, 0, -1, -1, FF x5, 0}
```

Consequences a port must keep: one pending command per channel (a second `` `068 `` command on
the same channel fires the first immediately); `_SEPL` without `` `068 `` on a channel only
zeroes the pending timer (the command stays and fires when the next deferred command for that
channel arrives); `_SEFD` cancels only a pending kind-1 entry. 3,471 + 583 + 40 script
statements use `` `068 `` (values 20..8,000 ms) [D].

---

## 6. Per-frame update (VA 0xAD80) [E; EMU-corpus]

```
Update(dt):                                     # dt = 1 per game frame (0 if no vsync elapsed)
    deferred timers (5)
    for s in 0, 1:                              # BGM
        r = rec[s]
        if r.state == 0: if is_playing(s): stop(s), close(s); continue
        if !(r.rate > 0): r.rate = 0.2
        if !is_playing(s): continue
        if r.vol != r.target:
            step = r.rate * dt
            r.vol = step > |target - vol| ? target : vol > target ? vol - step : step + vol
            g = clamp(trunc(r.vol * vol_byte[0xC] * 0.01), 0, 100); set_volume(s, g * 0.01)
        if r.state == 2 && r.vol <= 0: r.state = 0; stop(s), close(s)
    for s in 2..5:                              # voice
        same ramp when state != 0 and the stream plays, volume byte by kind (2.2), integer g
        if state == 2 && vol <= 0: state 0; set_volume(s, 0); stop; close
    for s in 6..25:                             # SE, c = s - 6
        if state == 0: if is_playing(s): stop, close; continue
        if w8 == 0: continue
        playing = is_playing(s); if !(rate > 0): rate = 0.2
        if playing:
            ramp (always computed, no equality shortcut)
            g = (1 <= char <= 19) ? char_gain(char - 1) : vol_byte[kind]      # 6.3
            x = g * vol * 0.01;  set_volume(s, x < 0 ? 0 : min(100, x) * 0.01)  # float
            if g == 0 && vol > target: stop, close, state = 0
            if pan_rate != 0: pan += pan_rate * dt, clamped at pan_target -> rate 0; set_pan(s, pan*0.001)
            if p3_rate != 0: same sweep, no call
        if state == 2 && vol <= 0: state = 0; stop, close
```

### 6.1 Gains (finding (c))

BGM and voice: `trunc(cur * vol_byte * 0.01)` is an **integer percentage** (clamped 0..100)
and the stream gets that integer * 0.01. `_MPLY(7,,`017:33)` with BGM 40 plays at 0.13, not
0.132 [EMU]. SE: `min(100, g * cur * 0.01) * 0.01` in float (no truncation); the initial play
uses `g * vol / 100` the same way.

### 6.2 Fades and ramps

All fades are linear per frame. The step is `points * 1000 / 60 / ms` (one script volume point
= 1 % of the channel's class volume). A record whose step is <= 0 gets 0.2 (500 frames for
100 points). A BGM replaced without fade fades out at 4.0 per frame (25 frames from 100); a
stop with `` `001:0 `` stops at once. A port at another frame rate should scale the step by the
real frame time.

### 6.3 Per-character voice gate for SE channels

An SE whose record +0x30 holds a character c (1..19, set by `` `070 ``) is a voice clip
(the 9 `SEV_*` vse entries, 19 statements): its gain is `char_gain(c - 1)` instead of the SE
volume, and it is silenced (and stopped while fading down) when the character's voice is
switched off:

```
char_gain(k) = (cfg.3C & (1 << k)) ? (i16 cfg.76[k] * i16 cfg.16) / 100 : 0      # C division
```

`` `070 `` values in the scripts: 2 (Arcueid's slot 1), 3, 10, 16 [D] - note that `` `070 `` is
"config slot + 1" (k = c - 1), a different number space from the `_VPLY` character index (8).

---

## 7. Voice playback [E VA 0x92380, 0xC3A80, 0x8F60, 0x9220, 0x8BD0, 0xA0B70]

1. `_VPLY` stores the entry and character (3.6); nothing plays yet.
2. The next `_ZM` (0x92380, the message display) runs the start block 0x9281E..0x92902
   [EMU-192 vs model]:

```
idx = pending (0x16541FC); pending = -1
if idx >= 0 && !(skip && (0x16541E8 != 0 || cfg+0x30 == 0)) && !ff
       && cfg+0x16 != 0 && cfg+0x36 != 0 && (cfg+0x3C & mask[char]):
    k = log2(mask[char]); vol_byte[0xD] = (cfg.76[k] * cfg.16) / 100
    VoiceRequest(idx, kind 0)              # 0xC3A80
    last_voice (0x86AA28) = idx            # "replay last voice"
```

3. `VoiceRequest(idx, kind)` (0xC3A80): idx <= 19,999 -> voice.hed entry idx (archive slot
   0x13); 20,000..59,999 -> voice2.hed entry idx - 20,000 (slot 0x15); larger -> rejected.
   0x8F60 stops streams 2..5, sets the voice state to 1 and starts loading (a voice prefetched
   by Script_Prefetch is reused).
4. 0x9220 (every frame): state 1 and the load done -> state 2, 0x8BD0 copies the LV lip-sync
   data (audio.md 5.1), MZX-decompresses, stops streams 2..5, picks stream `2 + (++n & 3)`,
   sets its record {1, kind, vol = target = 100, rate 100} and plays it from memory with
   `volume = vol_byte[0xD] * 0.01`; state 2 -> counts vsyncs into 0xDD8F04 and returns to 0
   when the stream stops.
5. Stop on advance (Adv_Update 0xA1015, 0xA1149, 0xA124D): when the message is dismissed the
   voice is stopped (0x8EA0: streams 2..5) if `(F1123 == 0 && cfg+0x4E != 0) || skip`. F1123
   (word 0x164BA96) is the script's "keep the voice" switch. With the default cfg+0x4E = 1 a
   voice never outlives its message.
6. `_VCWT` waits for state 0; auto mode uses 0x96F0 = (state 2 ? elapsed * 17 ms : state 1 ?
   0 : 10000).

Default voice level: char volume 50 * master 100 / 100 = 50 -> the voice stream plays at 0.5.
(The boot default of every character row is 50 %, not the 40 % of the music and SFX sliders.)

7. The config screen's voice test (VA 0x401E0, item 99 + 5r of page 1): `VoiceRequest(G[31 + r], kind -1)`
   with the row's last voice (3.6), then the `Sound_ApplyConfig(0)` every config item ends with (0x40093)
   sets volume byte 0xD to the row's gain `cfg.76[r] * cfg.16 / 100` and the playing stream's volume
   (0xB6F0), or stops the voice when the row is muted (0x8EA0). While the test voice plays, the row's
   slider and mute act on it live through the same path; a scene voice is never re-volumed after its
   start (the port: `SoundModel.StartTestVoice`, `ApplyTestRow`, `SetVoiceVolume`; adv_state.md 9.4).

---

## 8. Voice groups, characters and config slots [E table 0x215E90, mask 0x86A850; EMU all 54 groups]

The `_VPLY` handler compares the first three characters of the voice name with a table of
{u32 key, u32, u32 character} records at text VA 0x215EA0 (`A00`, `A10`, ... `M12`; 0x215E90 is
the `ZZZ` sentinel) and stores the character (0 when none matches). The config slot is
`k = log2(mask[char])` with the u32[24] mask table 0x86A850 (`0x3D2D0`). Every voice.nam group
was run through the real handler under unicorn; `tsuki.misc_audio_runtime_semantics.voice_char`
/ `char_slot` reproduce all 54:

| groups (voice.nam) | char | slot k | lines [D] | speaker (sample lines) [H] |
|---|---:|---:|---:|---|
| A00 | 1 | 0 | 5,896 | Shiki |
| B90 | 13 | 0 | 225 | young Shiki (hospital) - shares Shiki's slot |
| A10, A11, A12 | 2 | 1 | 2,808 + 272 + 90 | Arcueid (A11 Neco-Arc lines of the QA corner) |
| A20, A21, A22, A23, A24 | 3 | 2 | 2,564 + 3 + 6 + 3 + 296 | Ciel (A24 "Teach Me, Miss Ciel") |
| B40 | 8 | 3 | 707 | Akiha |
| B20 | 6 | 4 | 384 | Hisui |
| B30 | 7 | 5 | 374 | Kohaku |
| C40 | 18 | 6 | 58 | addresses Inui (Satsuki?) |
| B50 | 9 | 7 | 363 | Shiki's delinquent friend (Arihiko?) |
| C70 | 21 | 8 | 45 | ? |
| B00 | 4 | 9 | 1,004 | Noel (homeroom teacher) |
| C00 | 14 | 10 | 200 | ? (a sung line) |
| C30 | 17 | 11 | 94 | ? |
| B60 | 10 | 12 | 336 | ? |
| C10 | 15 | 13 | 64 | ? ("You called, boss?") |
| C20 | 16 | 14 | 30 | ? (Church / blockade report) |
| B70 | 11 | 15 | 137 | ? |
| B10 | 5 | 16 | 446 | Vlov? (Russian lines) |
| B80 | 12 | 17 | 89 | Aoko? (meets young Shiki in the field) |
| C50, C60 | 19, 20 | 18 | 21 + 16 | misc |
| C80, D00..D93 (13 groups), M04..M10 (7), DU | 0 | 18 | 169 | misc (doctor, crowd, classmates) |
| M00, M01, M02, M11, M12 | 23 | 18 | 5 | misc (M11/M12 are the two stereo voices) |

(`C90` -> 22 and `M03` -> 23 are in the table but not in voice.nam.) So the 54 groups fall into
**19 config slots**: 18 named characters plus the catch-all slot 18. This matches the option
strings: the 19 named voice options minus "遠野志貴（少年）" (young Shiki shares slot 0) = 18
named slots, plus その他 ("misc."); the strings "???", "misc. women" and "misc. men" have no
slot of their own [H]. The slot -> menu label assignment needs the config menu's row table
(dependency: adv_system_state); the speaker column is evidence from the first voiced line of
each group, not proof. `samples/misc_audio_runtime_semantics/voice_groups.tsv` lists every group
with entry counts, first entry, character, slot, mask and a sample line.

"System voice" (UI text 26, 「システムボイスを変更します」): no system-voice player exists in the
sound code; se_nam section 0 (`S00`..`S07`, kind 0) is never searched (10.2) [E, H: the option
belongs to a feature cut from the PS4 build].

---

## 9. BGM ducking during voice [E VA 0x694B9..0x6955F; EMU-1,100 frames vs model]

In the ADV frame:

```
if voice_state != 0 && cfg+0x1C:  level = level >= 0x81 ? max(level, 0x84) - 4 : level   # down to 128
else:                             level = level <= 0xFF ? min(level, 0xFE) + 2 : level    # up to 256
if level != applied:
    applied = level
    v = level * (1 - mute) * cfg.14 * 256 / 100 / 512        # = level * BGM / 200
    vol_byte[0xC] = trunc(clamp(v * 100 / 128, 0, 100))      # 0xCD380 -> 0xB590
    if rec[slot].state: set_volume(slot, clamp(trunc(vol_byte * rec.vol * 0.01), 0, 100) * 0.01)
```

So with the option on the BGM drops to half volume (-6 dB) in 32 frames when a voice starts and
recovers in 64 frames after it ends. The option is off by default (cfg+0x1C = 0). Applying the
config (0x3E9B0 -> 0x674B0) sets `applied = -1`, which forces a refresh on the next frame.

---

## 10. System SEs, vag triggers and se_nam sections

### 10.1 SystemSE_Play(id) (VA 0xCC670) [E; tables EMU-read]

```
vol = vol_byte[0xF] * 0.01
(bank, i) = table 0x2D3770[id]; (L, R) = table 0x6241F0[bank*128 + i]       # i16 pairs
voice = 0x2C + n; n = (n + 1) & 3
R >= 0: HEVAG_Play(L, pan -1.0) and HEVAG_Play(R, pan +1.0) on the voice; set volume
R < 0 and L > 0: HEVAG_Play(L, pan 0) (none in this game)
```

| id | vag pair | sound | call sites with this constant [E, heuristic: last `mov edi, imm` before each of the 154 calls] |
|---:|---|---|---:|
| 0 | 3/4 | moon_se00002 | 28 |
| 1 | 5/6 | moon_se00003 | 22 |
| 2 | 1/2 | moon_se00001 | 54 |
| 3 | 9/10 | moon_se00007 | 1 |
| 4 | 11/12 | moon_se00010 | 0 |
| 5 | 13/14 | moon_se00011 | 2 |
| 6 | 5/6 | moon_se00003 | 3 |
| 7 | 7/8 | moon_se00004 | 1 |
| 8 | 15/16 | moon_se00013 | 0 |
| 9 | 17/18 | moon_se00014 | 0 |
| 10..27 | 0/-1 | nothing (dummy) | 13: 9, 10: 2, 25: 1 |

(31 call sites pass a computed id.) These are the only triggers of the 19 vag entries: the UI
(menus, title, save / load, config, backlog, flowchart, gallery) plays ids 0, 1 and 2 almost
everywhere [H: decide / cancel / cursor]. Volume = system SE config (default 10). The second
player 0xCC6D0 (SE volume, voices 0x28..0x2B) has no callers.

### 10.2 se_nam lookups (VA 0xCC790 callers) [E]

Every caller sets flag bit 0, so **section 0 (`S00`..`S07`) is never searched**, and section 1
(`RANKING`, `NEKO02`) is not searched by the routine at all. Callers: `_SEPL` / deferred
`_SEPL` (flags 3, or 1 / 3 without `` `017 ``), the config SE preview 0x3CFF0 (flags 3: a random
one of 14 `moon_se020xx` / `moon_se10047` / `moon_se09005` names on a free channel at volume
100), the UI SE player 0x5E490 (`moon_se00013` = vse #297, flags 1, channel 1), and 0xCC9B0 /
0xCCA50 / 0xCCC10 (flags 1, UI channels). Section 3 (`SE_A0018`..) is searched only with
flags 1 and no script name matches it.

---

## 11. History, skip and fast-forward

### 11.1 Silent states

"ff" (word 0x120A6D2) and "scan" (byte 0x174DE48) make every sound command update only the
script-side state (scr_* arrays, music bits for ff) and skip all library calls; `` `068 `` is
executed immediately in these states. Skip mode (0x17DB232) does not silence BGM, but one-shot
SEs are dropped (3.4) and voices follow 7 (2) [EMU-corpus for the handlers].

### 11.2 History_Push (VA 0x7AF90)

Every handler calls it with the statement pointer (types 2 / 3). It copies the BGM track and
volume, the 20 SE ids, volumes, loop flags, pans and the deferred queue into the 0x3A10-byte
history record. One copy runs backwards: VA 0x7B3BA..0x7B3C3 copies the record's bytes
+0x36BC..+0x36CF **into** the script character array 0x1654330, and no code writes those record
bytes, so the array is reset to the ring's initial content (0) on every push [EMU-corpus].
History_Restore (0x7C6D0) reads the same bytes back. Effect: after a rewind or load, a looping
voice-like SE (`` `070 ``) is restored without its character. Harmless for playback (the record
+0x30 keeps the character); a port may store the array properly. (Dependency:
adv_system_state, history / save.)

---

## 12. Port recipe

1. Keep the records of 2.2 and the globals of 2.3; run the handlers of 3 and the queue of 5 on
   the script thread, and `Update(1)` (6) plus the ducking step (9) once per 60 Hz frame.
2. Implement the sound library calls of 2.5 on any mixer: per stream a decoder (audio.md 7),
   a loop flag, a linear gain and a pan. Volumes reaching the library are already final
   (class volume * script volume, integer-truncated for BGM / voice).
3. Voice: VPLY -> pending; at message display apply the gate of 7 (2); stop on advance (7.5).
4. System SEs: `SystemSE_Play(id)` with the table of 10.1; pan L/R hard left/right.
5. `tsuki.misc_audio_runtime_semantics.SoundModel` implements all of the above (except the VPL2
   immediate path and engine-only commands) and is checked against the engine.

---

## 13. Verification (commands actually run)

- `python -m tsuki.misc_audio_runtime_semantics_verify corpus` (log and JSON in
  `scratch/misc_audio_runtime_semantics/corpus_replay.*`): every script's `_MPLY _MVOL _MFAD
  _SEPL _SEFD _SEVL _SESP` statements in order (14,543 + 3,516 + 1,285 + 1,171 + 780 + 724 + 18 =
  22,037 statements in 514 scripts) with 0..3 random frame updates (dt 1/2/5/30) after each,
  engine vs model after every step: see 13.1.
- `scratch/misc_audio_runtime_semantics/work/effect_table.py` ->
  `samples/misc_audio_runtime_semantics/effect_table.json`: 41 cases, the 33 modelled ones match.
- Voice start block 0x9281E: 24 characters x 8 config / flag combinations = 192 cases, 96
  started, 0 mismatches. Ducking block 0x694B9: 1,100 frames over 5 configurations, 0 mismatches.
- `_VPLY` on one name of each of the 54 voice.nam groups: the character written to 0x1654208
  equals `voice_char` for all 54.
- `scratch/misc_audio_runtime_semantics/work/audio_fixes.py` (`audio_fixes.json`): the audio.md
  corrections (a), (d), (e), (f) re-measured (section 14).
- `pytest tools/tests/test_misc_audio_runtime_semantics.py`: 17 passed in about 4-14 s (the first unicorn `mem_map` of a process prints a benign "Windows fatal exception: access violation" faulthandler notice, also seen with `misc_vm_variables_errata.VarEmu`); with `TSUKI_GAME_DIR=/nonexistent`: 8 passed, 9 skipped in 0.4 s. `mypy` and `pyflakes` are clean on the four modules.
- VEX emulation: `python -m tsuki.misc_audio_runtime_semantics_vex` self test (7/7); every VEX
  instruction of the text segment is patched to `ud2` and executed in Python, so an
  unimplemented form raises instead of being skipped.

### 13.1 Corpus replay result

| run | scripts | statements | frame updates | compared steps | differences | time |
|---|---:|---:|---:|---:|---:|---:|
| normal play (`corpus_replay.json`) | 514 | 22,037 | 32,551 | 54,588 | **0** | 812 s |
| skip mode on (every 5th script) (`corpus_replay_skip.json`) | 100 | 4,483 | 6,708 | 11,191 | **0** | 74 s |
| fast-forward on (every 7th script) (`corpus_replay_ff.json`) | 74 | 3,988 | 5,905 | 9,893 | **0** | 31 s |
| final code re-check (every 10th script, `corpus_replay_final10.json`) | 49 | - | - | 4,800 | **0** | 79 s |

Each compared step checks the 26 records (bytes), slot, track, the BGM and SE loop flags, the
six script-side arrays, the 20 deferred entries, the music bits, the volume block and the full
ordered list of library calls (open / play with entry, loop and volume bits / stop / close /
set_volume / set_pan / set_loop / set_start_sample / is_playing).

---

## 14. Errata

### 14.1 Errata for audio.md (the six review findings, applied there as "Corrections")

- (a) `_VPLY(A10_07_00_0147_1,01b40,0)` has a third argument; voice #6976 is referenced:
  16,705 references, 16,459 distinct names and indices; unreferenced entries are only #0, #1,
  #1273, #11196, #15478 [measured, audio_fixes.json].
- (b) The BGM start (0x98C0) stops the BGM on a negative track (`js 0x9A31`) and ignores a
  request for the track already playing (i32 0x62437C) [EMU-corpus].
- (c) The BGM gain is `trunc(config * vol * 0.01)` (an integer percentage) * 0.01, not
  `config * vol * 0.01 * 0.01` [EMU].
- (d) ffmpeg's float ATRAC9 decode exceeds full scale in 645 of 1,327 vse streams, up to
  1.878 (vse #288), not "up to 1.30" (that was a 40-file sample; its maximum is 1.026 in our
  run) [measured].
- (e) The RIFF row at 0x64 is 8 bytes (two u32: manufacturer, product); 0x6C = sample period
  20833 [measured on bgm #33].
- (f) The HE-VAG `_L`/`_R` copies are polarity-inverted and lag their ATRAC9 copies by 28
  samples: after inverting and shifting, the gain is -1.000..-1.002 and the SNR 22.4, 26.1,
  24.7, 22.4 dB (vag #3, #4, #15, #16; the reviewer quoted 24..28 dB with another window) -
  a sample-level validation of the HE-VAG decoder [measured].

### 14.2 Further errata for audio.md (not in the review; recorded here, not applied there)

- 5.1 "played from memory on sound stream 2 (vtable +0x70 with 2 at VA 0x8D5B)": +0x70 is
  *stop*; the voice plays on stream `2 + (n & 3)`, rotating over 2..5 (VA 0x8DEC..0x8E8F).
- 4.2: the `_SEPL` lookup uses flags 3 (or 2*loop + 1 when `` `017 `` is absent), never 0, so
  section 0 is never searched; "kind 0 purpose unknown" -> unused (10.2).
- 4.2 tag meanings: `` `018 `` is pan (0..128, 64 centre), `` `068 `` a delay in ms (deferred
  command), `` `070 `` the character for the per-character voice gate; 3.4 / 5 / 6.3 here.
- 2.4 "Scripts never pass a loop argument other than empty, except once, so tracks loop by
  default [H]": confirmed [E, EMU]: `` `049 `` defaults to 1 (loop forever); the one exception
  is `_MPLY(122,,`017:100,,`049:0)`, which plays track 122 once.
- 6.3 "Which game events trigger the vag sounds is not established": the system-SE player
  SystemSE_Play (0xCC670) is the only trigger (10.1 here).

### 14.3 Errata for engine.md

- 8.4 "+0x76..+0x9D = 20 u16 per-character volumes (16 from 0x1A66A0 + 4 x 50)": there are
  **19** u16 volumes at +0x76..+0x9B (16 from 0x1A66A0 + 3 x 50, all 50); +0x9C / +0x9D are byte
  fields (menu items 0x36 / 0x3A) and are 0 by default (the qword stored at +0x96 is
  0x0000003200320032) [E VA 0x3EADF / 0x3EB21; EMU].
- 8.4 config map additions: +0x16 is set to 100 / 0 by item 0x5C (an on/off switch);
  +0x54 (movie volume) is a copy of +0x14; +0x1C = lower BGM during voice (read only by the
  ADV frame at 0x694D0; 0x3E6E0 does not write it); +0x4E = stop voice on advance (item 0x5E);
  +0xA6 / +0xA7 / +0xA8 = mutes (items 0x52 / 0x57 / 0x5B) (2.4).
- 8.6: the HE-VAG player is used only for system SEs (10.1).

### 14.4 Errata for script.md

- 3.4 tag table: `` `011 `` in `_MFAD` is a wait switch (0 = do not wait), and in `_MPLY` the
  old track's fade-out time; `` `018 `` = pan; `` `049 `` = loop (1 forever, else once); `` `068 ``
  = delay (deferred command); `` `070 `` = character (not a repeat count).
- 4 table: `_VCWT` waits on the voice state 0xDD8E50 (0x9210 returns it), confirmed.

### 14.5 Errata for FORMATS.md

- 6.6 is applied (audio.md "Corrections"); row 22 of the coverage matrix can become "decoded".
- 7 P2 item 15: se_nam sections 0 / 1 are unused, the vag sounds are the system SEs of 10.1,
  the group -> slot table is section 8; the "end 128 samples early" and "-1200 samples start"
  rules remain codec-level (audio.md 2.3).

---

## 15. Open questions

1. Menu label of each config slot k (section 8): needs the config menu row table
   (adv_system_state). The speaker column is [H].
2. The skip-mode voice condition reads u32 0x16541E8 (probably "message already read"); its
   writer belongs to the skip / read-text logic (adv_system_state).
3. cfg +0x1C (ducking), +0x2C (SE notification hook 0xE7EE0 / 0xCD610) and +0x36: no writer
   besides the whole-block config load was found, so their menu items are unknown
   (adv_system_state).
4. Exact effects of the unused `_MSTP` / `_SEPN` / `_SEP3` / `_SEAC` arguments (3.7 lists the
   observed effects only).
5. The voice prefetch path (0xDD8EA0 slot, Script_Prefetch `_VPLY` at 0x9BD2B) and the voice
   states 3..6 (pause) are described from code only.
6. Whether the history ring is zero-initialised (11.2), i.e. whether the character array is
   really reset to 0 or to stale bytes after 800 records.

---

## 16. Python API

```python
from tsuki.misc_audio_runtime_semantics import SoundModel, Config, voice_char, char_slot, system_se_vags
m = SoundModel(Config())                       # defaults of VA 0x3EAC0
m.se_lookup = lambda name, flags: se_lookup(load_se_names(), name, flags)   # tsuki.audio
m.run("_MPLY(5,,`017:80);"); m.update(1)       # handlers + frame update; m.calls = library calls
m.run("_VPLY(A00_06_00_0029_0,00726);"); m.voice_start_at_message()          # -> (entry, slot, gain)
m.duck_update(voice_active=True)
voice_char("B90_00_01_0001_0"), char_slot(13)  # -> 13, 0
```

CLI: `python -m tsuki.misc_audio_runtime_semantics groups | tags "<stmt>" | run <stmt|frameN>...`;
`python -m tsuki.misc_audio_runtime_semantics_verify corpus|steps ...`.
