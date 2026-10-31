# M0 spikes S1-S4: measurements and verdicts

Template written 2026-10-25 (setup job); filled at the M0 review (L, 2026-10-25 17:36-18:20
local time, UTC-4) from the owners' reports and from L's own re-runs. The thresholds and
fallbacks are those of the ROADMAP M0 spike table. This file is committed: numbers only, no game
text, no pictures.

Full write-ups, with every run, command and caveat:

| spike | owner | report |
|---|---|---|
| S1 loop | E | [`spikes/S1.md`](spikes/S1.md) |
| S2 movie | E | [`spikes/S2.md`](spikes/S2.md) |
| S3 compositor | D | [`spikes/S3.md`](spikes/S3.md) |
| S4 giant picture | D | [`spikes/S4.md`](spikes/S4.md) |

Spike code lives in `src/Spikes/S1.AudioLoop`, `S2.MovieSync`, `S3.Blend1080` and
`S4.BigPicture` (ROADMAP step 8 named a top-level `spikes/` folder; the skeleton put them under
`src/Spikes/`). They are part of the solution build (0 warnings) but not of the shipping build,
and are deleted after M2.

Where the local outputs are (git-ignored): the owners wrote theirs to `samples/spikes/s1..s4/`
(the template asked for `artifacts/M0/spikes/`). L's re-runs are in `artifacts/M0/rerun/s1..s4/`.

**Rules for every spike**

- Record the command line and the commit (`git rev-parse --short HEAD`, plus "dirty" if the
  tree has changes) of each run.
- Release build (`-c Release`), no debugger attached, other heavy jobs (pytest, explorer,
  exports) not running at the same time.
- Report distributions as p50 / p95 / p99 / max over the stated number of frames, not means.
- A failed threshold triggers the listed fallback, measured again within 2 days (ROADMAP 7).
  Keep the failed run's table and add the re-measurement below it; never overwrite numbers.
- Verdict: **PASS**, **FAIL** (fallback triggered) or **INCONCLUSIVE** (say what is missing).

**Rule exceptions in this run.** S2 and S4 were measured while other agents' builds, Python jobs
and spikes ran (other load 2.4-11.8 cores for S2; machine-wide busy 11-100 % for S4). S1's full
playback run had its output folder rewritten by an outside Release build. L's re-runs were on a
quieter machine: one unrelated Python job (about 1 core) and nothing else heavy, "other load"
1.6-1.8 cores in the S2 runs and 9.5-16 % machine-wide in the S4 runs.

## Machine (fill once; update if anything changes)

| item | value | source |
|---|---|---|
| CPU, cores / threads | AMD Ryzen 7 5800X3D, 8 cores / 16 threads | `Get-CimInstance Win32_Processor` (S1, S2, S3, S4, L) |
| RAM | 63.9 GB (68,639,719,424 bytes) | S1, S3 |
| GPU, driver | AMD Radeon RX 9070 XT, driver 32.0.32015.7029 | check_env.ps1, 2026-10-25 |
| OpenGL (legacy WGL probe) | 4.6.0 Compatibility Profile Context 26.10.1.261008, GLSL 4.60 | check_env.ps1, 2026-10-25 |
| OpenGL (spikes, SDL3) | `3.3.0 Core Profile Context 26.10.1.261008`, profile mask 1 (core), GLSL 4.60 | S2, S3, S4 |
| monitor mode | 2560 x 1440 @ 144 Hz (so vsync tests ran at 6.94 ms, not 16.7 ms) | check_env.ps1; SDL in S2 |
| audio device | WASAPI "Speakers (BW01)", F32LE 2 ch 48,000 Hz, 480-frame (10 ms) buffer | S1, S2 |
| OS | Windows 11 Home 10.0.26300 | |
| .NET SDK / runtime | 10.0.402 / 10.0.13 | check_env.ps1, 2026-10-25 |
| FFmpeg DLLs (engine) | BtbN n9.0.1-11-ge47273f4d9-20260831: avcodec 63.1.101, avformat 63.1.101, avutil 61.1.101, swresample 7.1.101, swscale 10.1.101; `avcodec_license()` = "LGPL version 3 or later" | ffmpeg_dlls.ps1, `third_party/ffmpeg/SOURCE.txt`, S1 |
| FFmpeg (Python oracle) | Gyan 9.0.2 full_build: avcodec 63.1.102 (static exe) | `ffmpeg -version` |
| FFmpeg.AutoGen, ppy.SDL3-CS, Silk.NET.OpenGL | 9.0.1.1, 2026.1002.1 (SDL 3.5.0, SDL-3.5.0-ea7a2da), 2.23.0 | `Directory.Packages.props`; S1 |

---

## S1 loop: bgm #33 through FFmpeg.AutoGen into the mixer

**Test.** Decode bgm #33 with the engine's FFmpeg DLLs through FFmpeg.AutoGen (ATRAC9 RIFF
from memory, custom AVIO), trim to the playable range, and render the intro plus 3 loop
bodies offline through the real mixer (gain 1, centre pan).
**Pass.** The seams are sample-exact, and the C# float PCM equals the Python decode within 1e-6.
**Fallback.** Check decoder version parity (BtbN 9.0.1 / avcodec 63.1.101 in the engine vs
Gyan 9.0.2 / 63.1.102 in the oracle); else LibAtrac9 behind the same decoder interface.

How it was built: no demuxer and no AVIO were needed. The `atrac9` decoder is opened with
`block_align` 512 and the 12 bytes of extradata at fmt + 0x28, and each 512-byte superframe is
sent as one packet (S1.md 1).

Known input facts (audio 2.3-2.5, ARCHITECTURE 7.2); **all confirmed** by the spike (S1.md 2):

| fact | expected | measured |
|---|---|---|
| bgm.hed entry 33 | bgm.mrg offset 0x875D000, 4,129,448 bytes (RIFF, WAVE_FORMAT_EXTENSIBLE ATRAC9) | same; entry SHA-1 `6b6ee469e5b28d28c7c1c4e36f000f68370e710f` |
| format | 2 channels, 48,000 Hz, 1,024 samples per superframe, config `FE 74 0F F0` | same; block_align 512 |
| decoder output | 8,065 superframes x 1,024 = 8,258,560 samples per channel | 8,258,560 (end-of-stream flush returns 0) |
| `fact` | n = 8,258,048, encoder delay 256 | (8,258,048, 256, 256) |
| playable range | `decoded[256 : 256 + 8,258,048]` | same |
| `smpl` loop (raw) | start 962,256, end 8,258,255 | same |
| loop (playable coordinates, end inclusive) | [962,000, 8,257,999], length 7,296,000 samples = 152.0 s; intro 20.04 s | same; intro 20.0417 s |
| 3-loop render length | 962,000 + 3 x 7,296,000 = 22,850,000 samples per channel | 22,850,000 (476.042 s) |

Python reference: `tsuki.audio.AudioBank("bgm").decode(33, float_output=True)` (ffmpeg from
`TSUKI_FFMPEG` or PATH), and the golden `samples/golden/slice/audio/bgm_00033.f32`.

| # | measurement | value |
|---|---|---|
| 1 | libavcodec version loaded by the spike (`avcodec_version()`), and the DLL path it came from | 63.1.101, `src/Spikes/S1.AudioLoop/bin/Release/net10.0/avcodec-63.dll` (copied from `third_party/ffmpeg/bin` by `TsukiUsesFFmpeg`) |
| 2 | decoded samples per channel before trimming / after trimming | 8,258,560 / 8,258,048 |
| 3 | delay removed (samples) and how it was determined (`fact` value 256 or codec delay) | 256 = `fact[2]`; the codec adds no delay of its own (the flush returns 0 frames) |
| 4 | C# vs Python (Gyan) over the playable range, per channel: max abs diff; count of samples with abs diff > 1e-6; index of the first such sample | max abs diff **0**; **0** samples > 1e-6; 0 samples not bit-identical; no first mismatch. The untrimmed decode is identical too |
| 5 | C# vs Python run with the BtbN `ffmpeg.exe` of the same package (isolates the decoder version): max abs diff; count > 1e-6 | 0; 0. Gyan CLI vs BtbN CLI: 0 of 16,516,096 samples differ |
| 6 | SHA-256 of the C# float32 playable buffer / of the Python one (bit-exact or not) | `0a3090ebe424dc6554b9e8e06c131714a5ba042fdadf8f389ec6bf083f719163` / same (Gyan, BtbN and the golden slice file): **bit-exact** |
| 7 | render: length per channel (expect 22,850,000) | 22,850,000; 2 seams |
| 8 | render vs ideal concatenation `playable[0:962000] + 3 x playable[962000:8258000]`: mismatching samples (expect 0) | **0** of 45,700,000 (C# check, and `oracle_s1.py verify-wav` with the oracle's own `render_loop`) |
| 9 | seam samples at render index 8,258,000 and 15,554,000: `render[s-1] == playable[8,257,999]` and `render[s] == playable[962,000]` (both channels) | yes at both seams, both channels; seam position error 0 / 0 frames; 0 errors in the +-4,800-frame window of each seam |
| 10 | seam metric of audio 2.4 (linear-extrapolation error / local median step) at the seam, C# render vs Python render | 15.635 / 15.635 at both seams (natural continuations 0.805 / 0.479). The step is in the game data; the PS4 plays it too (S1.md 5) |
| 11 | peak abs sample of the playable range (float headroom: values above 1.0 must not be clipped) | 0.93128 |
| 12 | mixer path: max abs diff between the mixer output at gain 1 / pan 0.5 and the decoded input | 0 |
| 13 | decode wall time for the whole track on one worker thread (ms) and real-time factor | median **109.7 ms** for 172.05 s = 1,568x real time (10 runs: 1,243x-1,636x) |
| 14 | managed allocations during the decode (MB) | 63.4 MB, of which the output buffer is 63.0 MB |

Beyond the template (S1.md 4, 6, 7):

- **Real device.** 4 runs through SDL3 on WASAPI (158 s x 3 from 3 s before seam 1, 327 s from
  frame 0) crossed 2 seams each with **0 underruns**. Callback interval p50 10.00 / max 10.91 ms;
  worst time in the callback 1.67 ms. The postmix capture was bit-identical to the oracle over
  38.45 M frames.
- **Engine-style seek** (superframe 939, flush, drop 720 samples) gives the decoded-copy result
  exactly, with a warm or a fresh decoder (0 diffs over 163,120 frames).
- **SDL quirks.** The `disk` driver ignores the requested spec (always S16LE 44.1 kHz), so it is
  unusable for sample-exact tests; `SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES` does not change the
  480-frame WASAPI period. Stream pull mode works for headless tests (0 diffs).

| run | date, commit | verdict | notes |
|---|---|---|---|
| E: headless + oracle export + 4 device runs | 2026-10-25 16:38-17:19 local (output file times); `cd94425` dirty (headless, first seam run), `3851949` dirty (hint runs) | **PASS** | numbers above; device runs not on an idle machine |
| L re-run: `oracle_s1.py export --raw --tag gyan` and `--tag btbn`, then `S1.AudioLoop.dll headless --speed-runs 5 --out artifacts/M0/rerun/s1`, then `verify-wav --loops 2` | 2026-10-25 17:41 local; `3851949` dirty | **PASS** (confirms) | oracles re-exported from the game data: 0 samples not bit-identical vs Gyan trim, BtbN trim, Gyan raw and the golden slice; render 0 of 45,700,000 differ (verify-wav pass); seam errors 0 / 0; J 15.635; decode median 113 ms = 1,518x (best 1,743x, worst 1,303x); seek check 0 diffs; SDL pull 0 diffs. Device playback not re-run |

---

## S2 movie: `prologue_en.mp4` decoded on a thread, YCbCr shader, synced to the audio clock

**Test.** Play movie id 46 (`image/movie/prologue_en.mp4`) to its end in the app skeleton:
FFmpeg demux + H.264 + AAC on the movie thread, Y / U / V planes uploaded per frame, BT.709
limited-range shader (compositing 2.3, PS #4), presented at 60 Hz, the audio clock as master.
**Pass.** A/V offset at the end <= 1 frame (33 ms); 0 dropped frames at 60 Hz; decode < 1 core.
**Fallback.** Media Foundation behind `IMovieDecoder` (ARCHITECTURE 7.5).

How it was run: a 1920 x 1080 SDL3 / GL 3.3 core window with vsync at the monitor's 144 Hz and
60 Hz logic ticks. Windows must close within 60 s, so the movie was played in segments: 0-45 s,
55-65 s, 295-305 s and 345 s to the end. The whole file was decoded headless. Recommended
design, now the spike default: the tick reads the audio clock at its **nominal** tick time, with a
**4 ms cadence lock** (S2.md 7). Values are for that design unless marked "first design".

Known facts (archives_ui_movies 9.1, RR 7.2): H.264 High 4.0, 30 fps, AAC with sound, 388.7 s
(the ja `prologue.mp4` is 384.3 s). Reference frames: the 25 s and 60 s frames show page 1 of
`00_02` (textbox 10.1).

| # | measurement | value |
|---|---|---|
| 1 | probe: width x height, r_frame_rate, nb_frames, video duration, audio codec / rate / channels / duration, colour tags (primaries, transfer, matrix, range) | 1920 x 1080, 30/1, 11,660 frames, 388.666602 s, H.264 High 4.0 yuv420p; AAC LC 48,000 Hz 2 ch fltp, 388.666604 s (duration_ts 18,655,997); bt709 / bt709 / bt709 / tv |
| 2 | frames decoded / expected nb_frames | **11,660 / 11,660** headless (software, d3d11va, dxva2, d3d12va with auto threads) |
| 3 | frames presented at 60 Hz; frames shown twice (expect about 2 per source frame); dropped source frames (expect 0); late frames | from 0 s: 2,699 ticks, ticks per frame {2: 1,348; 3: 1 (the first frame, before audio)}; 345 s to end: {2: 1,308; 3: 1}. **Dropped 0, late 0**, never presented 0 in both |
| 4 | A/V offset (video PTS of the presented frame minus the audio clock) at the first frame, 25 s, 60 s, 300 s and the last frame (ms) | -16.18; -0.66; -7.14 (first design); -2.43 (first design); **-2.08** (frame 11,659). First design at the last frame: -8.02 / -10.80 / -7.58 |
| 5 | max abs A/V offset over the whole run (ms), and where | offset range -18.25..+1.10 ms (from 0 s) and -18.77..+0.53 ms (end); in sync means 0..-33.3 ms. Sync error (clock outside the shown frame's interval) max 1.10 / 0.53 ms; first design 0.000 ms |
| 6 | end detection: time from presenting the last frame to the "movie ended" event that releases `_TOMW` (ms) | 19.1 (first design 27.4-28.0) |
| 7 | movie thread CPU time / wall time (= cores used; must be < 1.0); whole-process CPU | **0.225 cores** (from 0 s), 0.148 (end), software with 16 frame threads; process 0.318 / 0.252 cores. d3d11va: 0.043 |
| 8 | per-frame decode time p50 / p99 / max (ms); frame queue depth min / max | decode thread's own time p50 0.040 / p99 0.138 / max 15.1 ms (the work runs on FFmpeg's threads); one-thread headless interval p50 4.87 / p99 18.8 / max 50.8 ms; ready queue 6 / 7 |
| 9 | per-frame plane upload time p50 / p99 / max (ms) | CPU 0.381 / 0.743 / 1.134; GPU 0.019 / 0.021 / 0.026 (PBO ring CPU p50 0.346: not needed) |
| 10 | main-loop frame time p50 / p99 / max (ms); frames over 16.7 ms | present interval 6.939 / 7.732 / 10.24 at 144 Hz; **0** over 16.7 ms in the final runs. 2 of 23 windowed runs had 20-38 ms swap stalls under heavy machine load and dropped 1 frame each (S2.md 8) |
| 11 | YCbCr correctness: our 25.0 s frame (read back) vs `ffmpeg -ss 25 -i prologue_en.mp4 -frames:v 1 -pix_fmt rgb24` with BT.709 limited range: PSNR, max abs diff | frame 750 (25.000 s): decoded planes byte-identical to the Gyan CLI (software and d3d11va); GL read-back vs swscale BT.709 limited rgb24: max 2, PSNR 49.0 dB; vs `tsuki.compose.ps_ycbcr`: max 1, PSNR 66.5 dB (the oracle itself is max 2 from swscale) |
| 12 | AAC: decoded samples vs duration; resampling done (device rate vs stream rate) | 18,655,997 = duration_ts; no resampling (stream and device F32 48 kHz stereo); 0 underruns |
| 13 | peak memory (MB) | working set 288-292 (software), 213 (d3d11va, 1 thread); managed heap 35; 0 GCs while playing |

Headless decode speed (S2.md 5): software 1,274 fps (42.5x real time, 0.210 cores at 30 fps)
with auto threads, 182 fps with 1 thread; d3d11va 762, d3d12va 724, dxva2 597 fps. d3d12va
with `thread_count` 1 fails after frame 2,143 in BtbN 9.0.1. Software stays the default.

| run | date, commit | verdict | notes |
|---|---|---|---|
| E: 23 windowed runs + 8 headless benches (`final_sw_from0`, `final_sw_end` are the main ones) | 2026-10-25 16:47-17:24 local (20:47-21:24 UTC); `cd94425` dirty before 17:08, `3851949` dirty after | **PASS** | other load 2.4-11.8 cores; the owner asked L to re-run the two final runs on an idle machine |
| L re-run: `S2.MovieSync.exe bench --hw none --threads 0` | 2026-10-25 17:42 local; `3851949` dirty | confirms | 11,660 frames in 7.89 s = 1,477.9 fps (49.3x), 0.200 cores at 30 fps |
| L re-run: `play --hw none --start 345 --seconds 50 --tick-clock nominal --cadence-lock 4` (`rr_final_sw_end`) | 17:42 local | **PASS** (confirms) | movie ended; dropped 0, late 0, never presented 0; ticks per frame {2: 1,308; 3: 1}; last frame (11,659) offset **-0.87 ms**; sync error p99 1.20 / max 1.76 ms; end event 21.5 ms; presents over 16.7 ms 0 (max 7.90); movie threads 0.135 cores; audio underruns 0; other load 1.6 cores |
| L re-run: `play --hw none --seconds 45 --tick-clock nominal --cadence-lock 4` (`rr_final_sw_from0`) | 17:43 local | **PASS** (confirms) | dropped 0, late 0, never presented 0; ticks per frame {2: 1,348; 3: 1}; offset at 25 s -0.98 ms; sync error max 0.80 ms; presents over 16.7 ms 0; movie threads 0.211 cores; other load 1.8 cores |

---

## S3 compositor: 10 full-screen backdrop-reading blends at 1920 x 1080

**Test.** One 1920 x 1080 RGBA8 render target, 10 full-screen layers (RGBA8 straight alpha;
procedural textures or the golden slice pictures), each drawn with a blend shader that reads
the destination: modes 11 (ps_add), 17 (ps_dodge5), 19 (ps_hardlight), 23 (ps_screen),
24 (ps_softlight), two layers each, in that order. Before each quad the affected rectangle of
the target is copied into a backdrop texture (`glBlitFramebuffer`, ARCHITECTURE 6.3).
**Pass.** <= 4 ms GPU per frame (hard limit 8 ms). Read as: p99 <= 4.0 ms and max <= 8.0 ms
over 600 frames. **L confirms this reading** (M0 review).
**Fallback.** Blit only the quad bounds, plus the static-layer cache (ARCHITECTURE 6.3, 4.3).

What was timed (S3.md 2): the 10 layers above with affine transforms (scale, rotation, drift,
some mirrored) over a 2,070 x 1,230 backdrop that zooms every frame, plus 2 partial-screen sprite
layers (modes 13 and 19) and a continuous `_FADS(2, 1500, 29)` rule wipe: 15-16 quads and 12
backdrop blits per frame, real decoded pictures. Row "E" is the template test alone (10 layers).
All 24 blend shaders, the basic, solid and transition shaders were written from `compose.py`.

| # | measurement | baseline (full-target blit) | quad-bounds blit | + static-layer cache |
|---|---|---|---|---|
| 1 | GPU time per frame from `GL_TIME_ELAPSED` queries, 600 frames: p50 / p95 / p99 / max (ms) | 0.496 / 0.547 / **0.680** / **0.737** (A, full scene); 0.400 / 0.470 / 0.522 / 0.621 (E, 10 layers only); stress 30 layers 1.232 / 1.299 / 1.329 / 1.363 | 0.493 / 0.574 / 0.702 / 0.749 (B) | not built: fallback not needed, and the zooming backdrop leaves nothing static |
| 2 | GPU time of the backdrop blits alone per frame (ms) and blit count | 0.161 mean, 0.267 p99; 12 blits, 24.88 Mpx (about 0.013 ms per 1080p blit; ARCHITECTURE 6.3 estimated 0.1 ms) | 0.151 mean, 0.342 p99; 12 blits, 23.18 Mpx | n/a |
| 3 | CPU time to build and submit the frame: p50 / p99 (ms) | 0.056 / 0.143 | 0.046 / 0.145 | n/a |
| 4 | the same with layer opacity 0.5 instead of 1.0: GPU p99 (ms) | 0.734 | not run | n/a |
| 5 | vsync setting, swap interval, window size | swap interval 0 (runs A-G, S); interval 1 in run H (144 Hz, mean frame 6.945 ms, GPU p99 0.547); 1280 x 720 window, 1920 x 1080 FBO | same | n/a |
| 6 | correctness: one frame read back vs `tsuki.compose` on the same synthetic list: max abs diff; share of pixels with abs diff <= 2/255 (T5 (b) needs 99.9 %) | aligned 13-layer frame + rule wipe at p = 0.5: max 6, **99.970 %** with the PS4 fp16 round-toward-zero colour export modelled; max 7, 99.556 % against `compose` as it is | byte-identical to the full-blit frame | n/a |

Test-pattern correctness (419 cases, S3.md 4.1): every shader mode 0..25, effects 52..67 and
all 14 blend states are within **1 UNORM8 level** of `compose`; the two-pass rule wipe within 2
(1 with the fp16 export model); the rule mask is bit-exact with it. 27 of 27 programs link as
GLSL 3.30 core and 27 of 27 as GLSL ES 3.00 on this driver.

| run | date, commit | verdict | notes |
|---|---|---|---|
| D: `export_s3.py`, `verify`, `verify_s3.py`, bench runs A-H, S, S2 | 2026-10-25 16:41-17:05 local (output file times); `cd94425` dirty | **PASS** | about 6x headroom under the 4 ms p99 limit |
| L re-run: `export_s3.py --out artifacts/M0/rerun/s3`; `S3.Blend1080.exe verify --data ...`; `verify_s3.py --data ...` | 2026-10-25 17:44 local; `3851949` dirty | **PASS** (confirms) | 27/27 core + 27/27 ES; 419 cases rendered in 249 ms; every mode max 1 level; rule wipe max 2 (f32) / 1 (fp16 RTZ); check frame 99.556 % / **99.970 %** within 2 levels; full = bounds (max diff 0). Identical to the owner's numbers |
| L re-run: `bench` A (full), B (bounds), E (10 layers only), 600 frames each | 17:45 local | **PASS** (confirms) | GPU p50 / p95 / p99 / max: A 0.501 / 0.547 / **0.573** / 0.814; B 0.495 / 0.546 / 0.563 / 0.601; E 0.405 / 0.453 / 0.466 / 0.505 |

Open point for the oracle (S3.md 6): `tsuki.compose` does not model the PS4's fp16
round-toward-zero colour export (compositing 2.1). Without it the aligned frame reaches 99.556 %,
below the 99.9 % of T5 (b). The compose owner (D) should add the export model, spec first
(ROADMAP 6 rule 8).

---

## S4 giant picture: the largest MZP through a C# MZX0 + MZP prototype

**Test.** Decode the largest picture with parallel tiles on worker threads and upload it in
slices while the main loop keeps presenting.
**Pass.** Decode <= 500 ms on the workers; no presented frame over 16.7 ms during the upload.
**Fallback.** A longer prefetch distance; the opt-in decoded-texture cache (ARCHITECTURE 4.3).

Input (images 10, "largest area"): allpac1 `IMG1481.MZP`, HEP8, 512 x 512 tiles, stored
11,264 x 7,680, shown 11,220 x 7,650 = 85.8 MP, 343 MB as RGBA8 (confirmed as rank 1 by decoded
size from `samples/inventory`; it ties with `IMG1936` and has the larger compressed entry).
allpac1_2 `IMG3885.MZP` checks the 256-row tile layout. `IMG1481_20_00` (RGB565+LO+A) is the
largest MZX0 output (361,758,720 bytes) and was measured too. M1 targets for comparison:
single-thread MZX0 >= 500 MB/s; the numba kernel reaches 868 MB/s (containers 5.6).

Upload settings that passed (S4.md 5.3; "m16"): at most 8 decode workers (the physical cores)
at below-normal priority; at most 16 MiB uploaded per frame, at least one tile; direct upload
from client memory (no PBO); textures created lazily or recycled; a bounded 64 MiB staging pool.
Variants that break these rules failed the frame threshold (S4.md 5.2).

| # | measurement | IMG1481 | IMG3885 |
|---|---|---|---|
| 1 | archive entry index, compressed entry size (bytes), tile count, tile size, pixel format | allpac1 #1259; 47,298,096; 330 (22 x 15); 512 x 512; HEP8 | allpac1_2 #4009; 33,943,128; 660 (22 x 30); 512 x 256; HEP8 |
| 2 | positional read of the entry (ms) | first 10.75; OS-cached median 6.14; uncached 12.13 / 11.84 / 12.12 (3,722 MiB/s) | 9.55; 4.83; 8.63 / 8.68 / 9.26 |
| 3 | decode wall time with 1 / 4 / 8 / 16 workers (ms) | 166.2 / 47.6 / 29.8 / **25.8** (median of 7; cold first decode with 16 workers 43.0; read + decode with 16 workers 32.0, 37.9 with an uncached read) | 107.4 / 29.8 / 22.7 / 20.4 |
| 4 | single-thread MZX0 throughput (MB/s of output) | 620 MiB/s | 1,175 MiB/s |
| 5 | SHA-1 of the RGBA vs `tsuki.image` (equal?) | equal (`674954b7ba01...`) | equal (`a100cfb031f0...`) |
| 6 | upload: tiles per frame, bytes per frame, frames until complete | 16; 16 MiB; 22 frames (146.4 ms at 144 Hz) | 32; 16 MiB; 22 frames (146.3 ms) |
| 7 | presented frame time during the upload: p50 / p99 / max (ms); frames over 16.7 ms (must be 0) | 6.93 / 7.66 / 7.71; **0 of 110** | 6.96 / 7.87 / 7.94; **0 of 110** |
| 8 | peak managed + native memory (MB); GC count and longest pause (ms) during decode | decode: peak working set 408.1, managed 47.9, native buffers 334 MiB; 0 GCs, 0 ms. Upload with the staging pool: working set 241.1, native peak 67.0 MiB | 397.0, 34.3, 332 MiB; 0 GCs. Upload: 233.3, 65.5 MiB |
| 9 | GPU memory of the uploaded tiles (MB) | 330 MiB (346,030,080 bytes, borders included) | 330 MiB |

Across all rule-abiding settings: **0 of 5,103 load frames over 16.7 ms in 170 runs**, worst
8.95 ms. An earlier build without the staging pool had 4 of 805 load frames over 16.7 ms
(kept in S4.md 5.2). C# RGBA equals the oracle for 154 of 154 pictures (all four pixel formats),
and the GPU read-back equals it in 205 of 205 upload runs. Scene loads (backdrop + 3 sprites,
usage-weighted from 59,164 picture statements): median 8 frames / 48.8 ms, p90 11 frames /
69.8 ms, heavy scene 40 frames / 271.6 ms at 144 Hz, so the prefetcher needs 8-40 frames of
lead time.

| run | date, commit | verdict | notes |
|---|---|---|---|
| D: `s4_oracle.py`, `verify`, `decode` x 4 pictures, `scenes`, `run_s4.ps1` parts cpu / matrix / budget / hitch (216 upload runs) | 2026-10-25 16:37-17:28 local; final runs 17:15-17:28 at `3851949` dirty, earlier matrix at `cd94425` dirty | **PASS** (with the upload rules) | machine-wide busy 11-100 % during load phases |
| L re-run: `S4.BigPicture.exe verify --oracle samples/spikes/s4/oracle.json --out artifacts/M0/rerun/s4` | 2026-10-25 17:45 local; `3851949` dirty | **PASS** (confirms) | 154 of 154 SHA-1 equal |
| L re-run: `decode allpac1 1259 --reps 7` | 17:45 local | **PASS** (confirms) | 1 / 2 / 4 / 8 / 16 workers 161.7 / 82.6 / 44.1 / 27.1 / **21.9 ms**; cold 37.2 ms; single-thread MZX0 126.2 ms = 656 MiB/s; uncached read 11.8 ms; SHA-1 `674954b7ba01...` |
| L re-run: `upload rr_m16_img1481_r0..r2 --layers allpac1#1259 --tiles-per-frame 256 --budget-mb 16 --workers 8 --staging-mb 64 --texpool` | 17:45-17:46 local | **PASS** (confirms) | 3 runs: 22 load frames each, decode start to last tile 146.6-146.8 ms; load-frame max 7.45-7.47 ms; **0 of 66** load frames over 16.7 ms; read-back SHA-1 equal 3 of 3; peak working set 241 MB; machine busy 30-44 % (this process 21-28 %) |

---

## Summary (for M0 acceptance 6)

| spike | verdict | key number | fallback triggered | re-measured |
|---|---|---|---|---|
| S1 loop | **PASS** | C# float PCM bit-identical to the oracle (0 of 16,516,096 samples differ); 3-loop render 0 of 45,700,000 differ, seam error 0 frames; 0 underruns on the device | no (BtbN 9.0.1 and Gyan 9.0.2 give the same bits) | yes, by L at 17:41: same result, decode 1,518x real time |
| S2 movie | **PASS** | last-frame A/V offset -2.08 ms (limit 33); 0 dropped / 0 late; decode 0.15-0.25 cores | no (Media Foundation not needed) | yes, by L at 17:42-17:43 on a quieter machine: offset -0.87 ms, 0 dropped / late, 0.21 cores |
| S3 compositor | **PASS** | 0.680 ms GPU p99, 0.737 max (limits 4 / 8 ms); every blend mode within 1 level of `compose` | no (quad-bounds blits measured anyway, same cost, identical image; cache not built) | yes, by L at 17:44-17:45: p99 0.573 ms, verify numbers identical |
| S4 giant picture | **PASS** (with the 5 upload rules) | read + decode 32.0 ms with 16 workers (limit 500); 0 of 5,103 load frames over 16.7 ms | no | yes, by L at 17:45: decode 21.9 ms, 0 of 66 load frames over 16.7 ms, 154 / 154 SHA-1 |

Findings that change the plan (carried into `PACE.md`, wave 2):

1. **S2:** the 60 Hz logic tick must read the audio clock at its nominal tick time, plus a 4 ms
   cadence lock, or frames get 1 or 3 ticks (judder; up to 89 of 299 frames in one run).
   ARCHITECTURE 3.4 / 7.5 should say so.
2. **S4:** the five upload rules belong in the `Tsuki.Render` upload scheduler (ARCHITECTURE
   4.3), and the prefetcher must be bounded in decoded bytes or frames of lead time, not by the
   PS4's 12,000,000-byte budget, which IMG1481 alone exceeds.
3. **S3:** the oracle needs the fp16 round-toward-zero export model for T5 (b).
4. **S1:** bgm #33's seam has a waveform step that the PS4 plays too (J 15.6). Default stays
   engine-exact; an optional crossfade is a product decision. The BtbN DLLs are an LGPL **v3**
   build, which the licence notes must say.
5. **S1 / S2:** headless sample-exact audio tests must use SDL stream pull mode or the postmix
   capture, never the SDL `disk` driver.
