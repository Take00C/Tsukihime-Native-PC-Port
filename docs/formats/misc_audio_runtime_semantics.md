# Audio runtime semantics (pointer)

The sound playback semantics of TSUKIHIME (channel model, script audio commands and keyword
tags, deferred SE queue, per-frame fades, gains, voice start / stop, BGM ducking, voice-group ->
character -> config-slot table, system SEs and vag triggers) are documented in
[`sound_runtime.md`](sound_runtime.md).

- Python: `tools/tsuki/misc_audio_runtime_semantics.py` (model),
  `misc_audio_runtime_semantics_emu.py` (unicorn harness running the eboot's own sound code),
  `misc_audio_runtime_semantics_vex.py` (VEX instructions in Python),
  `misc_audio_runtime_semantics_verify.py` (engine vs model, corpus replay).
- Tests: `tools/tests/test_misc_audio_runtime_semantics.py`.
- Samples: `samples/misc_audio_runtime_semantics/` (`effect_table.json`, `voice_groups.tsv`).
- The six audio.md review findings are applied in audio.md ("Corrections") and listed with
  their evidence in sound_runtime.md section 14.1.
