# Audio formats: bgm / vse / vag / voice / voice2 / se_nam

Scope: every sound asset of TSUKIHIME -A piece of blue glass moon- (PS4, CUSA47999,
v01.01) once the container layer (docs/formats/containers.md) has produced an entry's
bytes. It covers the codecs and their parameters, the container fields, loop points, how
scripts and the engine name or index each sound, and what real-time playback needs. The
reference implementation is `tools/tsuki/audio.py` plus `tools/tsuki/audio_hevag.py`
(tests: `tools/tests/test_audio.py`). This document is meant to be enough to write a C#
or C++ player without reading that Python.

Confidence labels:

- **[V-all]**: verified on every entry of the retail data (counts given).
- **[V-n]**: verified on n samples.
- **[E]**: read from the game's own code (eboot.bin, text VA = file offset - 0x2280; data VA
  = file offset - 0x365FA0 + 0x600000).
- **[H]**: hypothesis.

All multi-byte integers are little-endian, except in the VAG header (big-endian).
"Sample" means one sample frame across all channels.

---

## 1. Overview

| bank | files | entries | codec | channels / rate | names | total length |
|---|---|---:|---|---|---|---:|
| bgm | bgm.hed (HED8, no terminator) + bgm.mrg | 132 | RIFF/WAVE ATRAC9, superframe 512 B (120 kbps / 192 kbps) | 2 ch / 48 kHz | none; script track number = entry index | 6.20 h |
| vse | vse.hed + vse.mrg | 1327 | RIFF/WAVE ATRAC9, 384 B superframes (144 kbps) | 1316 x 2 ch, 11 x 1 ch / 48 kHz | se_nam.mrg section 2, record i = entry i | 2.09 h |
| vag | vag.hed + vag.mrg | 19 | VAGp, **HE-VAG** ADPCM (not plain PS-ADPCM) | 1 ch / 48 kHz (stereo sounds stored as `_L`/`_R` pairs) | name inside each VAG header | 66 s |
| voice | voice.hed (HED4) + voice.nam (NAM8) + voice.mrg | 16464 | "LV" chunk + MZX0 -> RIFF/WAVE ATRAC9, 384 B superframes | 16462 x 1 ch, 2 x 2 ch / 48 kHz | full name only in the scripts (`_VPLY(name,hexindex)`); voice.nam = 4-char speaker group | 23.75 h |
| voice2 | voice2.hed/.nam/.mrg | 2 | same as voice entries 0-1 ("DU", 0.21 s silence) | 1 ch | "DU" | 0.4 s |
| se_nam.mrg | standalone mrgd00 | 4 sections | NAM32 name tables | - | - | - |

[V-all]: all 17,944 entries parse; every ATRAC9 stream (132 + 1327 + 16464 + 2) decodes with
ffmpeg to exactly `superframes * 1024` samples; all 19 VAGs decode with tsuki's HE-VAG
decoder. Silent entries: bgm #0, vse #0 and #1326 (`DUMMY`), voice #0 and #1 and voice2
#0 and #1 (`DU`), vag #0 (`dummy`). Every other entry is audible (RMS -45.9 to -6.9 dBFS).

No other audio exists outside the movies (`image/movie/*.mp4`, AAC stereo 48 kHz, played
by libSceAvPlayer). The eboot also names development files that are **not** on the disc
[V]: `/app0/data/at9/BGM_kaleido_016.at9`, `/app0/data/at9/bgm03_battle01.at9`,
`/app0/data/at9/SE_NAT_290.at9`, `/app0/data/jingle.vag` (all used only by a sound-test
routine at VA 0xEDB80.., which opens them as streams 0/1/2 and loads jingle.vag through the
"he-vag" allocator), plus `host0:bgm.mrg` (VA 0xC030, dev host path) next to the real
`/app0/image/bgm.mrg`.

---

## 2. ATRAC9 RIFF/WAVE (bgm, vse, voice, voice2)

All 17,925 ATRAC9 streams have the same layout [V-all]: `RIFF`, `WAVE`, then the chunks
`fmt ` (52 B), `fact` (12 B), `smpl` (60 B; bgm and vse only), `data`, in that order, with
nothing after `data`. RIFF size + 8 = HED size (bgm, vse) or MZX output size (voice).

### 2.1 Byte layout

| off | size | type | field | value in this game |
|---:|---:|---|---|---|
| 0x00 | 4 | char[4] | `RIFF` | |
| 0x04 | 4 | u32 | RIFF size | total stream bytes - 8 |
| 0x08 | 4 | char[4] | `WAVE` | |
| 0x0C | 4 | char[4] | `fmt ` | |
| 0x10 | 4 | u32 | chunk size | 0x34 |
| 0x14 | 2 | u16 | wFormatTag | 0xFFFE (WAVE_FORMAT_EXTENSIBLE) |
| 0x16 | 2 | u16 | nChannels | 1 or 2 |
| 0x18 | 4 | u32 | nSamplesPerSec | 48000 |
| 0x1C | 4 | u32 | nAvgBytesPerSec | block_align * 48000 / 1024 (7500 / 15000 / 18000 / 24000) |
| 0x20 | 2 | u16 | nBlockAlign | superframe bytes: 160, 320, 384 or 512 |
| 0x22 | 2 | u16 | wBitsPerSample | 0 |
| 0x24 | 2 | u16 | cbSize | 0x22 |
| 0x26 | 2 | u16 | wSamplesPerBlock | 1024 (samples per superframe) |
| 0x28 | 4 | u32 | dwChannelMask | 4 (FRONT_CENTER) for mono, 3 (FL+FR) for stereo [V-all] |
| 0x2C | 16 | GUID | SubFormat | `d2 42 e1 47 ba 36 8d 4d 88 fc 61 65 4f 8c 83 6c` = {47E142D2-36BA-4D8D-88FC-61654F8C836C} (ATRAC9) |
| 0x3C | 4 | u32 | dwVersionInfo | 1 |
| 0x40 | 4 | u8[4] | ATRAC9 config word | see 2.2 |
| 0x44 | 4 | u32 | reserved | 0 |
| 0x48 | 4 | char[4] | `fact` | |
| 0x4C | 4 | u32 | chunk size | 12 |
| 0x50 | 4 | u32 | fact[0] = sample count | playable samples (encoder delay excluded) |
| 0x54 | 4 | u32 | fact[1] | 256 (input overlap delay [H: Sony name]) |
| 0x58 | 4 | u32 | fact[2] | 256 = encoder delay; the engine subtracts this from the loop points [E] |
| 0x5C | 4 | char[4] | `smpl` | (absent in voice/voice2; then `data` follows at 0x5C) |
| 0x60 | 4 | u32 | chunk size | 0x3C |
| 0x64 | 8 | u32[2] | manufacturer, product | 0, 0 (two u32; correction (e)) |
| 0x6C | 4 | u32 | sample period (ns) | 20833 = 1e9/48000 |
| 0x70 | 4 | u32 | MIDI unity note | 60 |
| 0x74 | 12 | u32[3] | pitch fraction, SMPTE format, SMPTE offset | 0 |
| 0x80 | 4 | u32 | number of loops | 1 [V-all] (the engine reads at most 2) |
| 0x84 | 4 | u32 | sampler data | 24 (Sony writes the loop-record size here) |
| 0x88 | 4 | u32 | loop cue id | 0 |
| 0x8C | 4 | u32 | loop type | 0 (forward) |
| 0x90 | 4 | u32 | loop start | in decoder-output samples (delay included) |
| 0x94 | 4 | u32 | loop end | inclusive, decoder-output samples |
| 0x98 | 8 | u32[2] | fraction, play count | 0, 0 (play count 0 = infinite) |
| 0xA0 | 4 | char[4] | `data` | |
| 0xA4 | 4 | u32 | data size | superframes * nBlockAlign |
| 0xA8 | ... | | ATRAC9 superframes | |

`data` is always a whole number of superframes, and
`superframes = ceil((fact[0] + fact[2]) / 1024)` [V-all, 17,925 streams].

### 2.2 ATRAC9 config word (fmt + 0x2C, i.e. file offset 0x40)

Read the 4 bytes as a big-endian u32 `c`:

| bits | field | values here |
|---|---|---|
| 31..24 | sync | 0xFE |
| 23..20 | sample-rate index | 7 = 48000 Hz (table: 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 44100, 48000, 64000, 88200, 96000, 128000, 176400, 192000) |
| 19..17 | channel-config index | 0 = mono, 2 = stereo (1 = dual mono, 3 = 5.1, 4 = 7.1, 5 = quad are unused) |
| 16 | validation bit | 0 |
| 15..5 | frame bytes - 1 | 39, 79, 95 or 127 |
| 4..3 | superframe index | 2, so 4 frames per superframe |
| 2..0 | unused | 0 |

Samples per frame = 2^frame_log2 with frame_log2 = {6,6,7,7,7,8,8,8,6,6,7,7,7,8,8,8}[rate
index], so 256 at 48 kHz. Superframe = 4 frames = 1024 samples. The engine derives the
same: rate 48000 -> 0x100 samples per frame, 24000 -> 0x80, 12000 -> 0x40, any other rate
is rejected (VA 0x1130CD..0x1130F4) [E].

Configs present [V-all]:

| config | ch | frame B | superframe B | kbps | used by |
|---|---:|---:|---:|---:|---|
| `fe 74 0f f0` | 2 | 128 | 512 | 192 | bgm #1..#131 |
| `fe 74 09 f0` | 2 | 80 | 320 | 120 | bgm #0 (dummy) |
| `fe 74 0b f0` | 2 | 96 | 384 | 144 | 1316 vse, voice #16462/#16463 |
| `fe 70 0b f0` | 1 | 96 | 384 | 144 | 9 vse, 16460 voice |
| `fe 70 04 f0` | 1 | 40 | 160 | 60 | vse #0/#1326, voice #0/#1, voice2 #0/#1 (dummies) |

### 2.3 Decoding and sample coordinates

A decoder that implements ATRAC9 like ffmpeg's `atrac9` (and Sony's libSceAudiodec)
outputs `superframes * 1024` samples per stream. The first `fact[2]` = 256 of them are
the encoder delay: they are near-silent (they contain at most a little pre-echo of the
first transient: 41 of 132 bgm and 16,428 of 16,464 voice streams have exactly zero
there, and the maximum anywhere is 29 % of the stream peak, vse) [V-all]. Sharp onsets sit
exactly at sample 256 in 58 files [V-58]. Therefore:

```
decoded[]            = all decoder output (superframes * 1024 samples)
playable[k]          = decoded[k + delay]          k = 0 .. fact[0]-1,   delay = fact[2] = 256
loop_start_playable  = smpl.loop_start - delay
loop_end_playable    = smpl.loop_end   - delay      (inclusive)
loop_length          = loop_end_playable - loop_start_playable + 1
```

The samples after `delay + fact[0]` are padding of the last superframe and are never played.

Engine evidence [E]:

- RIFF parser at VA 0x112D60. It requires `fmt ` with format tag 0xFFFE and the ATRAC9
  GUID (compared at VA 0x112F62, GUID constant at VA 0x2D9920), and fails otherwise. It
  copies `fact` (12 B), `smpl` (36 B + up to 2 loop records) and the `data` offset into a
  header struct, and stops at the first unknown chunk.
- Decoder setup at VA 0x1130B0 does `loop_start = smpl.start - fact[2]` (VA 0x11324F) and
  `loop_end = smpl.end - fact[2]` (VA 0x11325D). It uses fact[0] as the length and seeks
  to `data_offset + (start / 1024) * block_align`. It then skips `(start % 1024) / 256`
  frames and `start % 256` samples.
- Set-loop routine at VA 0x10FCB0. When looping, the number of frames to decode is
  `(loop_end + 256) / 256 + 1`, with `(loop_end + 1) % 256` samples taken from the last
  frame. So the loop end is **inclusive** and the stream length while looping is
  `loop_end + 1`. A loop counts as present iff `loop_start != loop_end` (VA 0x112530).
- Decode step at VA 0x1132B0. The first decoded frame after a start or a loop seek is
  dropped: this is the one-frame (256-sample) codec delay. On a loop wrap the decoder
  context is cleared and re-seeked to superframe `loop_start / 1024`.
- Without looping, the stream ends 128 samples before fact[0] (VA 0x113589: if
  `n >= 0x201` and `pos >= n - 0x180`, then the last output = `n - pos - 0x80`) [E; the
  purpose is unknown, inaudible].

Independent data evidence for the delay convention [V]: once 256 is subtracted, the loop
points are round numbers far more often than chance. Of 118 non-trivial bgm loops, 8
starts and 9 ends are multiples of 1000, and 19 and 21 are multiples of 100. The raw smpl
values have 0 multiples of 1000 (bgm and vse), and the same holds for every other offset
tried (S, S-255, E, E+1, E-256). Example: bgm #33 loop = [962000, 8257999], bgm #50
loop = [772000, 7299999].

### 2.4 Loops: "whole" vs "inner"

Every bgm and vse file has exactly one smpl loop [V-all]. Two kinds:

| kind | definition (playable coords) | bgm | vse |
|---|---|---:|---:|
| whole | start = 0 and end = fact[0] - 1 (at9tool's default whole-file loop) | 14 (#0, 80-82, 114, 122, 124-131) | 1131 |
| inner | anything else (authored loop; start may be 0) | 118 | 196 |

The smpl chunk does not decide *whether* a sound loops; the caller does:

- **SE**: `_SEPL(channel, loop, name, ...)`. Argument 2 is the loop flag. Over all 14,543
  calls [V-all], loop = 0 was used 10,784 times with a "whole" file and once with an inner
  one. loop = 1 was used 1,781 times with "whole" files (the whole file then repeats) and
  1,977 times with inner loops. 307 SEs are only ever played looped (median length
  13.45 s), and 874 are never looped (median 1.45 s). The engine stores the flag per channel
  (VA 0xAA80, table at data VA 0xDD8FC0, 20 channels) and passes it to the stream play call
  (VA 0xA274 `setne cl`) [E].
- **BGM**: the per-slot flag table at data VA 0x624380 is set by VA 0x9EA0. A value of 1
  means loop forever (play count -1), anything else means play once [E]. Scripts never pass
  a loop argument other than empty, except once, so tracks loop by default [H].
- **voice**: no smpl chunk, never loops [V-all].
- **vag**: no loop flags in any frame [V-all, 19].

Loop seam quality [V]: as measured with ffmpeg decoding, the seam `playable[end] ->
playable[start]` is much smoother than a random splice but not always click-free.
Measure: linear-extrapolation error at the seam divided by the local median step.

| metric | bgm (118 inner) | vse (196 inner) |
|---|---|---|
| natural continuation | median 0.50 | median 0.54 |
| engine seam | median 9.0 | median 2.5 |
| random splice | median 31 | median 30 |
| seams with error < 3 | 24 | 114 |
| seams with error > 10 | 56 | 53 |

Shifting both points by k = ±1, ±2, ±128 or ±256 does not lower the bgm median: for
k = -256 it is 13.2, and k = 0 is the minimum. The engine performs no crossfade, so the
data is like this. A port may add an optional 2-5 ms crossfade, but that is not engine
behaviour.

### 2.5 Worked example: bgm #33 (bgm.mrg offset 0x875D000, 4,129,448 bytes)

```
0000: 52 49 46 46 a0 02 3f 00 57 41 56 45 66 6d 74 20  RIFF size 0x3F02A0 (+8 = 4129448 = HED size), WAVE, "fmt "
0010: 34 00 00 00 fe ff 02 00 80 bb 00 00 c0 5d 00 00  0x34, tag FFFE, 2 ch, 48000 Hz, 24000 B/s
0020: 00 02 00 00 22 00 00 04 03 00 00 00 d2 42 e1 47  align 512, bits 0, cb 0x22, 1024 spb, mask 3, GUID...
0030: ba 36 8d 4d 88 fc 61 65 4f 8c 83 6c 01 00 00 00  ...GUID, version 1
0040: fe 74 0f f0 00 00 00 00 66 61 63 74 0c 00 00 00  config FE740FF0, reserved; "fact" 12
0050: 00 02 7e 00 00 01 00 00 00 01 00 00 73 6d 70 6c  n = 0x7E0200 = 8258048, 256, 256; "smpl"
0060: 3c 00 00 00 00 00 00 00 00 00 00 00 61 51 00 00  0x3C, manuf 0, product 0, period 20833
0070: 3c 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00  unity note 60, ...
0080: 01 00 00 00 18 00 00 00 00 00 00 00 00 00 00 00  1 loop, sampler data 24, cue 0, type 0
0090: d0 ae 0e 00 cf 02 7e 00 00 00 00 00 00 00 00 00  start 0x0EAED0 = 962256, end 0x7E02CF = 8258255
00a0: 64 61 74 61 00 02 3f 00 37 4d 0f 80 ...           "data" 0x3F0200 = 4129280 B = 8065 superframes
```

The config FE 74 0F F0 decodes as rate index 7 (48 kHz), channel config 2 (stereo),
frame 128 B, 4 frames per superframe, so 512 B per 1024 samples. Decoder output is
8065 * 1024 = 8,258,560 samples. The playable sound is decoded[256 .. 256+8258048). The
loop is [962256-256, 8258255-256] = **[962000, 8257999]**, length 7,296,000 = 152.0 s;
the intro is 20.04 s.

---

## 3. bgm

- 132 entries, HED8 without a terminator record (containers.md 2.1). No names anywhere.
- **Index rule** [E + V-all]: script `_MPLY(n, ...)` plays bgm.hed entry **n**. The
  play routines at VA 0x98C0 and VA 0x9B40 call the bgm offset accessor VA 0xC040, which
  uses slot 0x1A and the HED8 accessor VA 0xE7040, with index n. Scripts use 104 distinct
  n in 2..123 (1,171 calls). bgm #0 is a 2 s silent dummy. Entries never named by a script
  are 0, 1, 30, 39, 44, 53, 72, 80-82, 86, 88, 91, 93, 102, 103, 106, 112, 116, 117 and
  124-131; some of them are reached from code, e.g. the music-mode table below.
- **Alternate set remap** [E]: at VA 0x9918..0x9958 the routine computes
  `idx = (setting == 1 && 1 <= n <= 131) ? n + 131 : n`, where `setting` is the u16 at
  +0x1E of the global at data VA 0x66C578 (default 0, from the initialised data image).
  bgm.hed has no entries 132..262. The adjacent option strings are JP 「BGMの音源を2chにします」
  / 「BGMの音源を5.1chにします」 ("make the BGM source 2ch / 5.1ch"), translated in the EN
  data as "Set music to mono." / "Set music to stereo." [V: data VA 0xD64310, 0xD7C2B0].
  [H] Setting 1 = "5.1ch BGM", whose 131 surround tracks are not in the PS4 data. This is
  why the engine has an 8-channel mixer and port (section 8).
- **Music mode** (sound gallery) track table [E]: text VA 0x209510, 55 records of 16
  bytes `{i32 bgm_index, i32 page (0-2), i32 position, i32 group (1-4)}`. The routine at
  VA 0x664C0 steps through it cyclically, skipping locked tracks. Track order:
  `2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 45 21 22 23 24 25 26 27 28 29 30 31 32
  33 34 35 36 37 38 39 40 41 42 43 44 46 47 48 49 51 50 59 60 61 62 63` (pages 23/24/8).
- Script syntax: `_MPLY(track, [loop?], `017:vol [, `001:fade_ms] [, `069:start_sample] [, `049:x])`.
  - `017 is the volume in percent.
  - `001 is a fade-in time in ms [H].
  - `069 is a start position in samples: 131 uses, all inside the track, 99 of them
    multiples of 256 and 116 inside the loop body [V]. The decoder setup starts 1200
    samples (0x4B0) before the requested start, clamped to 0 (VA 0x113101) [E].
- Related commands: `_MVOL(,,`001:ms,,`017:vol)` ramps the music volume, and
  `_MFAD(,,`001:ms,,`011:0)` fades the music out (`011 is always 0). There are 780 and 724
  calls [V].
- Two BGM stream slots alternate: the current slot (data VA 0xDD8F28) is toggled by
  `xor 1` on every new track, so the old track can fade out while the new one starts.
  The bgm.mrg file is opened as a stream for the slot (vtable +0x28) and played from the
  entry offset (vtable +0x50) [E].
- Start rules [E, VA 0x98C0; emulated, sound_runtime.md 3.2] (correction (b)): a negative
  track stops the current BGM (`js 0x9A31`: stop, close, current track = -1), and a request for
  the track that is already playing (i32 0x62437C) is ignored - nothing is restarted, so a port
  must not restart the music on every `_MPLY`.
- Gain [E, VA 0x995B..0x99A0] (correction (c)): `g = trunc(config_byte * script_volume * 0.01)`,
  an integer percentage, and the stream volume is `g * 0.01` (config_byte = +0xC of data VA
  0x17DA268, the BGM volume, default 40). Example: volume 33 -> trunc(13.2) = 13 -> 0.13.

## 4. vse (sound effects) and se_nam.mrg

### 4.1 Names

se_nam.mrg is a standalone mrgd00 with 4 NAM32 sections (containers.md 7.1). The engine
loads them at boot (VA 0xCC730, slot 0x17) into four pointers [E]:

| section | records | content | pointer (data VA) | role |
|---:|---:|---|---|---|
| 0 | 8 | `S00` .. `S07` (tag `s0`) | 0x178DB98 | lookup kind 0 (purpose unknown [H]) |
| 1 | 2 | `RANKING`, `NEKO02` (tags `ra`, `ne`) | 0x178DBA0 | not used by the lookup routine [E] |
| 2 | 1327 | `DUMMY`, `FATE_SE001`, ... `WOH_SEEX36`, `DUMMY` | 0x178DBA8 | **record i names vse.hed entry i** |
| 3 | 67 | `SE_A0018` .. (tag `se`) | 0x178DBB0 | kind 2, index 10000 + i |

### 4.2 Name lookup (engine VA 0xCC790) [E]

```
lookup(name, flags) -> (kind, index) or not found
  base = part of name after the last '\', upper-cased (a..z only), cut at '.' or 28 chars
  if !(flags & 1): search section 0 (max 256 records) -> (0, i)
  if !(flags & 2): search section 3 (max 81)          -> (2, 10000 + i)
  search section 2 (max 2770)                          -> (2, i)
```

Kind 2 with index < 10000 plays vse.hed entry i (play routines VA 0xA1E0 / 0xA2F0 /
0xA770 call the vse offset accessor VA 0xC080, slot 0x1B, and the vse.mrg handle, slot
0x1C). Index >= 10000 plays section (index - 10000) of a separately registered mrgd00
container (data VA 0xDD8F30, set only by VA 0x9FA0, which nothing in the retail eboot calls)
[E]. So the 67 section-3 names have no data on PS4, and no script uses them [V].

Script calls [V-all]: `_SEPL(ch, loop, NAME, , `017:vol [, `001:fade_in_ms] [, `068:x]
[, `018:pan] [, `070:x])`. There are 14,543 calls naming 1,314 distinct names, and all of
them resolve in section 2.
- `ch` is 0..19 (20 channels). The engine plays SE channel c on sound stream c + 6
  (VA 0xA220 `lea r15d, [r14 + 6]`) [E].
- `018 is in 32..96 [H: pan, 64 = centre]; it is used 15 times.
- `068 is in 20..8000 [H: delay or duration in ms]; it is used 3,471 times.
- `070 is in 2..16 [H]; it is used 19 times.
- Unused vse names: DUMMY x2, FATE_SE442, HOLLOW_SE633, IRSE009, MIX_SE001/002/003,
  MIX_SE006_1/2/3, MIX_SE016, MOON_SE00013. The last one is played by UI code: VA 0x5E4A0
  looks up "moon_se00013" with flags 1, which resolves to vse #297.

Other SE commands [V]:
- `_SEFD(ch|-1, , , `001:ms [, `068:x])` fades out a channel or all channels (3,516 calls).
- `_SEVL(ch, , , , `001:ms, , `017:vol)` ramps a channel's volume (1,285).
- `_SESP(ch|-1)` stops a channel (18).
- `_SELR/_SELD/_SELA/_SELS` are menu ("select") commands, not audio.

Default SE volume config byte (+0xD of data VA 0x17DA268) is 100 [E, VA 0xD22D9].

### 4.3 Content

- 1316 stereo and 11 mono entries. The mono ones are the two DUMMYs (60 kbps config) and 9
  `SEV_*` voice-like effects, #775..#783 [V-all].
- Names come from several source libraries: FATE_SE*, HOLLOW_SE*, MOON_SE*, WOH_SE*, MIX_SE*,
  SEV_* and others. Lengths are 0.095 s .. 66.8 s, median 2.17 s.
- 574 entries reach the int16 limit when decoded to 16-bit. ffmpeg's float output exceeds
  1.0 in 645 of the 1,327 entries, up to 1.878 full scale (vse #288) (correction (d); the
  earlier "up to 1.30" came from a sample of 40), so **decode to float and mix in float**; the
  engine's output ports are float.

## 5. voice

### 5.1 Entry layout

`voice.mrg` entry (HED4, containers.md 3) = **LV chunk + MZX0 stream** whose output is an
ATRAC9 RIFF without `smpl` (section 2) [V-all, 16,464].

| off | size | field | value |
|---:|---:|---|---|
| 0 | 2 | `LV` | the engine tests only byte 0 == 'L' |
| 2 | 2 | u16 n | 3 in every entry |
| 4 | n | lip-sync data | `00 09 00` in every entry |
| 4+n | ... | MZX0 stream (plain, not inverted) | decompresses to the RIFF |

```
voice.mrg entry 2 (offset 0x1000):  4c 56 03 00 00 09 00 | 4d 5a 58 30 e4 a2 01 00 2b ...
                                     "LV" n=3 payload      "MZX0" size 0x1A2E4 = RIFF size 0x1A2DC + 8
decoded RIFF:  52 49 46 46 dc a2 01 00 57 41 56 45 66 6d 74 20 34 00 00 00 fe ff 01 00 80 bb 00 00
               50 46 00 00 80 01 00 00 22 00 00 04 04 00 00 00 <GUID> 01 00 00 00 fe 70 0b f0 00 00 00 00
               66 61 63 74 0c 00 00 00 c0 57 04 00 00 01 00 00 00 01 00 00 64 61 74 61 80 a2 01 00 ...
               -> mono, 144 kbps, 0x457C0 = 284,608 samples (5.929 s), 0x1A280 data bytes = 279 superframes
```

**LV payload = lip-sync amplitude track** [E]. At play time (VA 0x8BF8) the engine copies
`4 + min(n, 600)` bytes to data VA 0xDD8BF0 and stores `min(n, 600)` at 0xDD8E48. The
mouth-animation routine (VA 0x9690) reads timer 2 (VA 0xD4720, a per-tick counter that is
reset when the voice starts [H: one tick per video frame]) and divides it by 6. It returns
`abs((int8) payload[t/6])` while the voice plays. VA 0x9480 and VA 0x95E0 map that level to
mouth states with thresholds 2/11 and 13/31 and a 4-tick hysteresis. All retail entries
carry the 3-byte placeholder `00 09 00`, so the PS4 data has no real lip-sync. A port
can ignore it, but should keep the parse (skip `4 + n` bytes).

Then (VA 0x8D10): if the next byte is 'M', MZX-decompress the stream into the voice buffer
(pointer at data VA 0xDD8E68), otherwise use the bytes raw. The voice is played from memory
on sound stream 2 (vtable +0x70 with 2 at VA 0x8D5B) [E].

### 5.2 Index and naming scheme [V-all]

Scripts call `_VPLY(<full name>,<hex index>)` (16,689 calls; one of them,
`_VPLY(A10_07_00_0147_1,01b40,0)` in script 212, has a third argument that the engine only
stores in the history record, correction (a)), e.g.
`_VPLY(A00_06_00_0029_0,00726)`, and `_VPL2(...)` (16 calls, always followed by `_VCWT()`
= wait for voice end [H]). **The hex number is the voice.hed entry index.** Checks:

- For all 16,705 references, voice.nam[index] (the 4-character group such as `A00_`) is
  the prefix of the name.
- 16,459 distinct names map to 16,459 distinct indices, with no name used with two indices.
- Sorted by index, the names increase strictly: voice.mrg is sorted by full name.
- Never referenced: #0, #1 (DU), #1273, #11196, #15478 (#6976 is the 3-argument call).

Name pattern: `<speaker group>_<chapter>_<scene>_<line4>_<variant>`. Some chapter fields
are letters (`QA_XX`, `15_01B`). voice.nam stores only the group: 54 groups, from `DU`
and `A00_` .. `A24_`, `B00_` .. `B90_`, `C00_` .. `C80_`, `D00_` .. `D93_` to `M00_` .. `M12_`
(contiguous runs; see containers.md 7.2). [H] The group is the speaker id used by the
per-character voice settings (the options have "voice volume for misc. women", "misc. men",
"???" and so on).

Content: 23.75 h, 0.21 .. 28.5 s each (median 4.41 s), RMS median -16.3 dBFS, peaks
normalised near -1.6 dBFS. Stereo: #16462 `M11_` and #16463 `M12_` only. The largest
decoded RIFF is 512,740 B (#16401). That is the voice buffer a port needs, plus 128 B of
MZX overrun slack.

voice2.hed/.nam/.mrg: 2 entries, identical in form to voice #0/#1 ("DU", 0.21 s
silence), at engine slot 0x15 [V].

## 6. vag (HE-VAG)

### 6.1 Header (48 bytes, big-endian)

| off | size | field | value |
|---:|---:|---|---|
| 0x00 | 4 | magic | `VAGp` |
| 0x04 | 4 | version | 0x00020001 [V-all] |
| 0x08 | 4 | reserved | 0 |
| 0x0C | 4 | data size | bytes of frames after the header; `+ 48 = HED size` [V-all] |
| 0x10 | 4 | sample rate | 48000 |
| 0x14 | 10 | reserved | 0 |
| 0x1E | 1 | channels | 1 (entry 0 `dummy`: 2) [H: channel count] |
| 0x1F | 1 | reserved | 0 |
| 0x20 | 16 | name | ASCII, NUL padded |
| 0x30 | data size | frames | 16 bytes each, mono |

Entries [V-all]: #0 `dummy` (124 zero frames). Then 9 stereo sounds stored as mono pairs
`moon_seNNNNN_L` / `_R` (#1/#2 = 00001, 3/4 = 00002, 5/6 = 00003, 7/8 = 00004, 9/10 =
00007, 11/12 = 00010, 13/14 = 00011, 15/16 = 00013, 17/18 = 00014), 0.68 .. 12.0 s.

Frame sequence of every non-dummy entry: one all-zero frame, the audio frames, then a frame
with flag 1 (last audio frame) and a frame with flag 7 (end marker). The dummy has flags
1, 1, 7, 7 at the end. No loop flags anywhere [V-all].

```
vag.mrg entry 1 (offset 0x800, 18,800 B):
0000: 56 41 47 70 00 02 00 01 00 00 00 00 00 00 49 40   "VAGp", v 0x00020001, data 0x4940 = 18752 (+48 = HED size)
0010: 00 00 bb 80 00 00 00 00 00 00 00 00 00 00 01 00   48000 Hz, ..., byte 0x1E = 1
0020: 6d 6f 6f 6e 5f 73 65 30 30 30 30 31 5f 4c 00 00   "moon_se00001_L"
0030: 00 00 ... 00                                      frame 0: zeros
0040: b9 60 00 ff 1e f3 f1 e0 10 22 f1 ef 40 13 2f d1   frame 1: shift 9, index (0x60 | 0xB) = 107, flag 0
```

### 6.2 Codec: HE-VAG, not plain PS-ADPCM [V]

Frame byte 1's high nibble is non-zero in many frames: the predictor index reaches 127.
Plain PS-ADPCM allows only indices 0..4 with a zero high nibble. ffmpeg's `vag` demuxer
decodes these files as `adpcm_psx`, and the result is wrong: correlation 0.04 with the
correct decode, and a wrong length (229,236 vs 336,028 samples for #15). **Use the
decoder below.**

```
frame (16 bytes):
  b0 = frame[0], b1 = frame[1]
  shift = b0 & 0x0F
  index = (b1 & 0xF0) | (b0 >> 4)        ; 0..255, if > 127 use 0
  flag  = b1 & 0x0F                      ; 0 normal, 1 = last frame, 7 = end marker (2/3/4/6 = loop bits, unused here)
  28 4-bit codes in frame[2..15], low nibble first

state: h1, h2, h3, h4 (int32, newest first), start at 0, carried across frames
for each of the 28 codes:
  if flag < 7:
      nib  = signed 4-bit code (-8..7)
      code = (nib << 12) >> shift                             ; arithmetic shift
      p    = float32(h1*C[index][0]) + float32(h2*C[index][1])  ; all in float32, left to right
      p    = float32(p + float32(h3*C[index][2]))
      p    = float32(p + float32(h4*C[index][3]))
      s    = code + trunc_toward_zero(p)                      ; int32
  else:
      s = 0
  output clamp(s, -32768, 32767)
  h4 = h3; h3 = h2; h2 = h1; h1 = s                           ; history keeps the UNclamped value
```

`C[i][j] = K[i][j] / 8192` with the integer table K below. The table and arithmetic follow
vgmstream's `src/coding/psv_decoder.c` (credited there to reverse engineering by daemon1).
Rows 0..4 are the classic PS-ADPCM filters (60/64, 115/64 & -52/64, ...), and rows 0..28
use only two taps. Every float in vgmstream's table is exactly K/8192 [V: all 512 values,
max deviation 0.0004/8192 from the printed digits].

```
/*   0 */ {     0,     0,     0,     0}, {  7680,     0,     0,     0}, { 14720, -6656,     0,     0}, { 12544, -7040,     0,     0},
/*   4 */ { 15616, -7680,     0,     0}, { 14731, -7059,     0,     0}, { 14507, -7366,     0,     0}, { 13920, -7522,     0,     0},
/*   8 */ { 13133, -7680,     0,     0}, { 12028, -7680,     0,     0}, { 10764, -7680,     0,     0}, {  9359, -7680,     0,     0},
/*  12 */ {  7832, -7680,     0,     0}, {  6201, -7680,     0,     0}, {  4488, -7680,     0,     0}, {  2717, -7680,     0,     0},
/*  16 */ {   910, -7680,     0,     0}, {  -910, -7680,     0,     0}, { -2717, -7680,     0,     0}, { -4488, -7680,     0,     0},
/*  20 */ { -6201, -7680,     0,     0}, { -7832, -7680,     0,     0}, { -9359, -7680,     0,     0}, {-10764, -7680,     0,     0},
/*  24 */ {-12028, -7680,     0,     0}, {-13133, -7680,     0,     0}, {-13920, -7522,     0,     0}, {-14507, -7366,     0,     0},
/*  28 */ {-14731, -7059,     0,     0}, {  5376, -9216,  3328, -3072}, { -6400, -7168, -3328, -2304}, {-10496, -7424, -3584, -1024},
/*  32 */ {  -167, -2722,  -494,  -541}, { -7430, -2221, -2298,   424}, { -8001, -3166, -2814,   289}, {  6018, -4750,  2649, -1298},
/*  36 */ {  3798, -6946,  3875, -1216}, { -8237, -2596, -2071,   227}, {  9199,  1982, -1382, -2316}, { 13021, -3044, -3792,  1267},
/*  40 */ { 13112, -4487, -2250,  1665}, { -1668, -3744, -6456,   840}, {  7819, -4328,  2111,  -506}, {  9571, -1336,  -757,   487},
/*  44 */ { 10032, -2562,   300,   199}, { -4745, -4122, -5486, -1493}, { -5896,  2378, -4787, -6947}, { -1193, -9117, -1237, -3114},
/*  48 */ {  2783, -7108, -1575, -1447}, { -7334, -2062, -2212,   446}, {  6127, -2577,  -315,   -18}, {  9457, -1858,   102,   258},
/*  52 */ {  7876, -4483,  2126,  -538}, { -7172, -1795, -2069,   482}, { -7358, -2102, -2233,   440}, { -9170, -3509, -2674,  -391},
/*  56 */ { -2638, -2647, -1929, -1637}, {  1873,  9183,  1860, -5746}, {  9214,  1859, -1124, -2427}, { 13204, -3012, -4139,  1370},
/*  60 */ { 12437, -4792,  -256,   622}, { -2653, -1144, -3182, -6878}, {  9331, -1048,  -828,   507}, {  1642,  -620,  -946, -4229},
/*  64 */ {  4246, -7585,  -533, -2259}, { -8988, -3891, -2807,    44}, { -2562, -2735, -1730, -1899}, {  3182,  -483,  -714, -1421},
/*  68 */ {  7937, -3844,  2821, -1019}, { 10069, -2609,   314,   195}, {  8400, -3297,  1551,  -155}, { -8529, -2775, -2432,  -336},
/*  72 */ {  9477, -1882,   108,   256}, {    75, -2241,  -298, -6937}, { -9143, -4160, -2963,     5}, { -7270, -1958, -2156,   460},
/*  76 */ { -2740,  3745,  5936, -1089}, {  8993,  1948,  -683, -2704}, { 13101, -2835, -3854,  1055}, {  9543, -1961,   130,   250},
/*  80 */ {  5272, -4270,  3124, -3157}, { -7696, -3383, -2907,  -456}, {  7309,  2523,   434, -2461}, { 10275, -2867,   391,   172},
/*  84 */ { 10940, -3721,   665,    97}, {    24,  -310, -1262,   320}, { -8122, -2411, -2311,  -271}, { -8511, -3067, -2337,   163},
/*  88 */ {   326, -3846,   419,  -933}, {  8895,  2194,  -541, -2880}, { 12073, -1876, -2017,  -601}, {  8729, -3423,  1674,  -169},
/*  92 */ { 12950, -3847, -3007,  1946}, { 10038, -2570,   302,   198}, {  9385, -2757,  1008,    41}, { -4720, -5006, -2852, -1161},
/*  96 */ {  7869, -4326,  2135,  -501}, {  2450, -8597,  1299, -2780}, { 10192, -2763,   360,   181}, { 11313, -4213,   833,    53},
/* 100 */ { 10154, -2716,   345,   185}, {  9638, -1417,  -737,   482}, {  3854, -4554,  2843, -3397}, {  6699, -5659,  2249, -1074},
/* 104 */ { 11082, -3908,   728,    80}, { -1026, -9810,  -805, -3462}, { 10396, -3746,  1367,   -96}, { 10287,   988, -1915, -1437},
/* 108 */ {  7953,  3878,  -764, -3263}, { 12689, -3375, -3354,  2079}, {  6641,  3166,   231, -2089}, { -2348, -7354, -1944, -4122},
/* 112 */ {  9290, -4039,  1885,  -246}, {  4633, -6403,  1748, -1619}, { 11247, -4125,   802,    61}, {  9807, -2284,   219,   222},
/* 116 */ {  9736, -1536,  -706,   473}, {  8440, -3436,  1562,  -176}, {  9307, -1021,  -835,   509}, {  1698, -9025,   688, -3037},
/* 120 */ { 10214, -2791,   368,   179}, {  8390,  3248,  -758, -2989}, {  7201,  3316,    46, -2614}, {   -88, -7809,  -538, -4571},
/* 124 */ {  6193, -5189,  2760, -1245}, { 12325, -1290, -3284,   253}, { 13064, -4075, -2824,  1877}, {  5333,  2999,   775, -1132},
```

Playable length = 28 * (index of the first frame with flag >= 7), which includes the
leading zero frame [V-all 19]. The PS4 runs HE-VAG on the CPU (libSceAudiodecCpuHevag;
eboot strings "hevag decode thread", "hevag waveout thread", "hevag output"), so exact
Sony rounding cannot be checked. vgmstream's float arithmetic is the reference used here.

Validation of the decoder [V]:

- vag #3/#4 (`moon_se00002_L/_R`) and vse #296 `MOON_SE00002` (ATRAC9) are the same
  sound; likewise vag #15/#16 and vse #297.
  - The 10 ms RMS envelopes correlate at 0.9996 and 0.9988 at lag 0.
  - RMS differs by less than 0.3 dB (1123 vs 1153, 2088 vs 2094) and peaks by less than
    1.5 % (9164 vs 9199).
  - Band energies 0-12 kHz agree within 0.2 dB.
  - Sample level (correction (f)): the HE-VAG copies are **polarity-inverted** and lag the
    ATRAC9 copies by **28 samples**. Inverted and shifted, the gain is -1.000..-1.002 and the
    SNR 24.5 / 26.1 dB (vag #3 / #4 vs vse #296) and 24.7 / 22.4 dB (vag #15 / #16 vs vse
    #297): a sample-level validation of the HE-VAG decoder (the earlier "correlation about 0.5"
    compared unaligned signals).
- Log-spectral distance to the ATRAC9 copy, 0-16 kHz, mean over 4 channels: correct
  decoder 10.3 dB. Alternatives: rows shifted by one 18.2 dB, index high bits ignored
  (= plain PS-ADPCM) 20.6 dB, taps reversed 63.6 dB, 2 taps only 53.9 dB, c2/c3 negated
  58.0 dB (`scratch/audio/hevag_lsd.py`).
- No decoded sample of the 19 entries reaches the int16 limit, and the peaks are 5,291 ..
  17,638.

### 6.3 Engine use [E]

At boot (VA 0xD21C0..0xD221E) the engine registers vag entries 0..49 (slot 0x1E vag.hed
sizes, slot 0x1D vag.mrg) with the sound object (VA 0x110020). The file is
memory-resident (1.8 MB). Only 19 entries exist. Indices 19 and 20 hit the 16-byte 0xFF
terminator, for which accessor VA 0xE7040 returns sector 0. Indices 21..49 lie past the
168-byte table, and what the engine reads there depends on memory layout [E]. A port
should register only the real entries. Which game events trigger the vag sounds is not
established. The UI plays `moon_se00013` from **vse** (#297), not from vag [E, VA 0x5E4A0].

## 7. Real-time playback requirements (for the port)

What the engine does [E unless marked]:

| item | engine behaviour |
|---|---|
| output | sceAudioOut, port type MAIN, user 0xFF, 48 kHz, **float32**. Three opens: HEVAG player `(len 256, FLOAT_STEREO)` VA 0x10E716; waveout mixer A `(len 1024, FLOAT_STEREO)` VA 0x118110; waveout mixer B `(len 1024, FLOAT_8CH)` VA 0x1181F5. Only `sceAudioOutOutput` is imported (no Outputs/Ngs2/Audio3d) [V: NID search]. |
| decoders | ATRAC9 via libSceAudiodec (`sceAudiodecCreateDecoder/Decode/ClearContext`, NIDs found) in an "at9 thread"; HE-VAG via libSceAudiodecCpu(+Hevag) in a "hevag decode thread". |
| BGM | streamed from bgm.mrg by superframes (seek = data offset + superframe * block_align). Two slots alternate for crossfades. Loop handling as in 2.3. |
| SE | 20 channels (script 0..19 -> stream 6..25), each with its own loop flag, volume and fade. vse.mrg is opened as a file (slot 0x1C), and entries are read as streams. |
| voice | whole entry MZX-decompressed to memory, played on stream 2. Script options "stop voice when skipping text" and "decrease music volume during voice" (ducking) exist [V: option strings]. |
| mixer | per-voice float gain and pan. Stereo mixer: `gL = min(2(1-p), 1)`, `gR = min(2p, 1)` (VA 0x118658..0x11867F, constants 1.0/0.5 at VA 0x2D9E38). The 8-ch mixer (VA 0x119A20) takes 8-channel interleaved sources: FL*gL, FR*gR, C*(gL+gR)*0.5, LFE unscaled, SL*gL, SR*gR, BL*gL, BR*gR. Volumes are percentages: SE `min(100, config * vol * 0.01) * 0.01` in float; BGM and voice `trunc(config * vol * 0.01) * 0.01` (correction (c); sound_runtime.md 6.1). |
| grain | 1024 samples (21.3 ms) per output block, double-buffered (32 KiB per buffer = 1024 x 8 ch x 4 B). HEVAG port: 256 samples (5.3 ms). |

Why a float 8-channel port: the mixer B path exists for multichannel sources. The only
multichannel content the engine anticipates is the alternate "5.1ch" BGM set (section 3,
setting +0x1E, JP option text) [H, strong]. The PS4 data has no 5.1 tracks, and every
retail asset is mono or stereo (movies are stereo AAC). **A PC port needs only one
stereo float mixer**, with optional surround upmix.

Recommended port design:

1. **Decode.** For ATRAC9, use any ATRAC9 decoder: an FFmpeg build (`libavcodec` atrac9),
   or a port of LibAtrac9 or vgmstream's atrac9 code. For stereo/mono streams, feed whole
   superframes of `block_align` bytes. Drop the first 256 output samples after every start
   or seek, and stop after `fact[0]` (or after `loop_end + 1` while looping). For HE-VAG,
   use the 30-line decoder above. Decode to float, because loud SEs exceed full scale.
2. **Stream BGM.** Peak bit rate is 24 KB/s, so read about 0.5-1 s ahead from bgm.mrg.
   Voice and SE can be decoded fully into memory: the largest voice is 28.5 s, and the
   largest vse is 66.8 s, 1.2 MB of ATRAC9.
3. **Loop.** Seek to superframe `loop_start / 1024`, reset the decoder, discard 256 +
   `loop_start % 1024` samples, and continue. Alternatively, keep a decoded copy and jump
   the read index from `loop_end` to `loop_start`. The result is identical apart from
   decoder warm-up.
4. **Mix.** Mix in float at 48 kHz. Channels: 2 BGM slots, 1 voice stream (plus VPL2), 20
   SE channels and the HE-VAG voices. Apply the linear pan law above, then per-category
   volume (BGM, SE and voice config bytes, and per-speaker voice volume [H]), then fades.
   Ramp volumes (`001 times) linearly [H].
5. **Output.** A 1024-sample period matches the engine's latency. Use a smaller period if
   the platform allows it.

## 8. Python reference (`tools/tsuki/audio.py`, `audio_hevag.py`)

```python
from tsuki.audio import AudioBank, load_se_names, se_lookup, write_wav, render_loop
with AudioBank("bgm") as b:             # "bgm" | "vse" | "vag" | "voice" | "voice2"
    e = b.entry(33)                     # AudioEntry: codec, channels, rate, num_samples, delay, loop, wave/vag/lv
    pcm = b.decode(33)                  # Pcm(samples int16 (n, ch), rate, loop) - delay removed (ffmpeg)
    pcm = b.decode(33, float_output=True)
    stream = b.stream(33)               # decodable RIFF bytes (voice: LV + MZX unwrapped) / VAGp bytes
    write_wav("x.wav", render_loop(pcm, loops=1), pcm.rate)
with AudioBank("vse") as v: v.find("FATE_SE001")          # -> 1
with AudioBank("voice") as v: v.find("A00_06_00_0029_0,00726")  # -> 0x726 (prefix-checked)
se_lookup(load_se_names(), "moon_se00013", flags=1)       # -> (2, 297)
```

CLI: `python -m tsuki.audio list|info|extract|decode|census <bank> ...`. For example:
- `decode bgm 33 out.wav --loops 1` writes intro + 2 x loop body.
- `decode vse FATE_SE001 out.wav` writes a WAV with a `smpl` loop chunk.
- `extract voice A00_06_00_0029_0,00726 x.at9` writes a RIFF that ffprobe reads as atrac9.
- `census voice --decode` decodes every entry and reports the statistics.

## 9. Measured counts and evidence (commands actually run)

- `python -m tsuki.audio census <bank> --decode` for all five banks
  (`scratch/audio/census_*.log`/`.json`):
  - bgm: 132/132 decode, 16 s.
  - vse: 1327/1327, 19 s.
  - voice: 16464/16464, 215 s with 12 workers.
  - voice2: 2/2.
  - vag: 19/19.
  - 0 errors. Every ATRAC9 stream gives `decoded == superframes * 1024`, with
    fact[1:] = (256, 256) everywhere.
- `scratch/audio/census.py` + `census_report.py` (raw RIFF census over 17,925 headers):
  - chunk order, sizes and every fmt field as tabulated in 2.1/2.2;
  - `align == frame_bytes * frames_per_superframe` and the byte-rate identity;
  - `data % align == 0`, `data end == RIFF end` and `superframes == ceil((n + delay)/1024)`
    hold for all.
- Loop statistics:
  - `census_report.py` gives the loop-kind counts.
  - The round-number test (inline script, section 2.3) gives 8/9 multiples of 1000 for
    S-256 / E-255 against 0 for the raw values.
  - `seam_jump.py` and `seam_test.py` measure seam quality.
  - `looptest*.py` and `loopxcorr.py` are exploratory: the tail after the loop end is a
    quiet fade (RMS 2..391), not a copy of the loop start, so no sample-exact seam proof is
    possible from the data alone.
- Script cross-reference (`scratch/audio/scriptcmds.py`, all 549 allscr scripts decoded
  read-only with tsuki.mzx):
  - 16,705 voice references (with the 3-argument call), all prefix-consistent;
  - 14,543 `_SEPL`, all resolvable in se_nam section 2;
  - 1,171 `_MPLY` with tracks 2..123;
  - loop flag versus smpl kind as in 2.4;
  - `069 start positions inside the tracks.
- eboot disassembly (capstone; `scratch/audio/ebdis.py`, `ebxref.py`, `ebfield.py`,
  `ebstr.py`, `ebhex.py`) of the addresses quoted throughout.
  - NIDs: sceAudioOutOpen `ekNvsT22rsY`, sceAudioOutOutput `QOQtbeDqsT4`, sceAudiodec* and
    sceAudiodecCpu* found; Ngs2/Audio3d/AudioOutOutputs absent.
  - The three sceAudioOutOpen call sites are VA 0x10E716, 0x118110 and 0x1181F5, via PLT
    VA 0x173C00, with arguments `(0xFF, 0, 0, len, 48000, param)`.
- HE-VAG:
  - `scratch/audio/hevag_try.py`: 19/19 decoded, numba equals pure Python.
  - `vag_vs_vse.py` and `vag_vs_vse2.py`: envelope and spectrum match against the ATRAC9
    copies.
  - `hevag_lsd.py`: decoder-variant discrimination.
  - The coefficient table was cross-checked against vgmstream's float table (512 values).
- Samples: `samples/audio/*.wav` (27 files, 23 MB) and `samples/audio/index.json`.
  - ffprobe on every WAV gives pcm_s16le, 48000 Hz and the expected channels and duration.
  - ffmpeg `astats` gives RMS -36.4 .. -8.8 dBFS for audible files and -inf for the 5
    dummies.
  - Spectrograms (`samples/audio/png/`, viewed):
    - `voice_00002_spec.png` shows speech: harmonic stacks, pauses and an 18 kHz codec
      cutoff.
    - `moon_se00002_vag_vs_at9_spec.png` shows the same harmonic lines and decay in the
      HE-VAG (top) and ATRAC9 (bottom, band-limited at 16 kHz) versions.
    - `bgm_033_loopseam_spec.png` shows the seam at 4.00 s with a strong onset (loop start
      on a downbeat) and a thin full-band line there, i.e. a waveform step at the seam
      (cf. 2.4).
- `pytest tools/tests/test_audio.py`: 19 passed, about 1.5 s. With TSUKI_NO_NUMBA=1: 18
  passed and 1 skipped (numba comparison), about 11 s. With a bogus TSUKI_GAME_DIR: 11
  passed and 8 skipped. mypy and pyflakes are clean.

## Corrections

The review of this document found six errors; they are corrected in place above. Evidence:
sound_runtime.md 14.1, `scratch/misc_audio_runtime_semantics/work/audio_fixes.py` /
`audio_fixes.json`, and the engine emulation of `tools/tsuki/misc_audio_runtime_semantics_emu.py`.

| # | where | was | is |
|---|---|---|---|
| (a) | 5.2, 9 | 16,704 references, 16,458 names, #6976 unreferenced | `_VPLY(A10_07_00_0147_1,01b40,0)` has a third argument: 16,705 references, 16,459 names / indices, #6976 referenced |
| (b) | 3 | (missing) | BGM start 0x98C0: a negative track stops the BGM (`js 0x9A31`); the track already playing (0x62437C) is not restarted |
| (c) | 3, 7 | gain `config * vol * 0.01 * 0.01` | `trunc(config * vol * 0.01) * 0.01` for BGM (and voice) |
| (d) | 4.3 | float decode up to 1.30 | 645 of 1,327 vse exceed 1.0, maximum 1.878 (vse #288) |
| (e) | 2.1 | row 0x64 is 4 bytes | 8 bytes (two u32), then 0x6C |
| (f) | 6.2 | sample correlation about 0.5, spectral check only | polarity-inverted, 28-sample lag; aligned SNR 22.4..26.1 dB |

Playback semantics (channels, tags, fades, ducking, voice gating, system SEs) are in
[sound_runtime.md](sound_runtime.md); its section 14.2 lists further corrections to sections
4.2 and 5.1 of this document.

## 10. Open questions

1. Meaning of fact[1] (always 256) as opposed to fact[2] (the delay the engine subtracts).
2. The purpose of the "end 128 samples early" rule for non-looped AT9 streams (VA 0x113589)
   and of the -1200-sample start offset (VA 0x113101).
3. se_nam section 0 (`S00`..`S07`, lookup kind 0) and section 1 (`RANKING`, `NEKO02`):
   which player handles kind 0 and where the sounds live. They may be HE-VAG voices or
   system voices [H].
4. What triggers the vag (HE-VAG) sounds at runtime, and how the `_L`/`_R` pairs are
   panned.
5. The SE parameters `068, `070 and `018 (pan?), and the BGM `049; the semantics of
   `001 for `_SEPL` (fade-in assumed).
6. The 5.1ch BGM set (entries n + 131): whether any other platform or DLC ships it.
7. The LV lip-sync tick unit (frames?) and the mapping of voice groups (`A00_` ...) to
   characters and to the per-character voice settings.
8. Whether Sony's ATRAC9 and HE-VAG decoders differ from ffmpeg / vgmstream by more than
   rounding. This is unverifiable without the PS4 libraries; the decoded levels and spectra
   are consistent.
