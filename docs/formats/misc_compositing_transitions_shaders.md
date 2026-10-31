# Compositing, transitions and shaders (pointer)

The full document for this gap is [compositing.md](compositing.md):

- the 35 embedded pixel shaders, decoded from their GCN code and verified by interpretation;
- the blend states and the draw-effect word (`_STBR`);
- straight alpha;
- `_FADS` (cross-fade, rule wipe, timing, the frame captures nexl_Capture[0] / [15] that the two
  draw back, the shared rule slot 0x4DD, and the RULE_nn = MASK_TOP + n mask source);
- `_FADE`;
- the `_STEC` / `_SNX2` / `_SNX3` CPU recolour.

Code: `tools/tsuki/compose.py`, `tools/tsuki/gcn.py`,
`tools/tsuki/misc_compositing_transitions_shaders.py` (+ `_render.py`). Tests:
`tools/tests/test_compose.py`, `test_gcn.py`, `test_misc_compositing_transitions_shaders.py`.
Samples: `samples/compose/`, `samples/misc_compositing_transitions_shaders/`.

Section 15 of compositing.md lists what changed after the review (type-0 capture surface,
`_STBR` flag scope and lifetime, rule-slot reuse, test fixes).
