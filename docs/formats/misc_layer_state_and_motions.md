# Layer state and motions (pointer)

The documentation of this gap is **[layers.md](layers.md)**: scene_state layout, the field map
of every layer command, the `_FADS` commit and the record arrays, tweens, motions (tables, time,
field table, interpolation, spline, easing), the projection of a layer from picture texels to the
1920 x 1080 frame (parents, size classes, zoom, mirror, rotation, pivot), masks / crop, the five
thumbnail framing outliers, verification, open questions and errata for script.md, images.md
and FORMATS.md.

Code: `tools/tsuki/misc_layer_state_and_motions.py` (model) and
`tools/tsuki/misc_layer_state_and_motions_emu.py` + `misc_layer_state_and_motions_vex.py`
(engine harness). Tests: `tools/tests/test_misc_layer_state_and_motions.py`. Samples:
`samples/misc_layer_state_and_motions/` (frames, check counts, motion loop table).

Module names: `tsuki.misc_layer_state_and_motions` is the `tsuki.layers` module of the gap
statement (named after the brief's `test_misc_<slug>.py` rule); see the header of layers.md.
Verification logs of the latest runs: `scratch/misc_layer_state_and_motions/xf/final/`
(layers.md section 11, including the fixes of the 2026-10-25 review in 11.1).
