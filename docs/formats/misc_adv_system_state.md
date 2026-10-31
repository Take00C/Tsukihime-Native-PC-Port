# ADV system state (alias)

This gap's main document is [adv_state.md](adv_state.md). It covers:

- the history ring and the 0x3A10-byte record (every byte mapped), the 59 History_Push sites and
  the record kinds, the `_ATSV` command (scene start, choice save point, auto save), and the
  0x1A08-byte scene_state (51 per-layer arrays, 29 globals, 4 gaps, with writers);
- History_Restore, including the kind-0 tail for both values of `from_history`;
- rewind, previous / next scene, the backlog and the backlog jump; auto / skip / fast-forward;
- save slots, the load path (emulated end to end on engine-made slots) and the checks for a real
  save (`check_save_image`);
- the config block and its menu, and the 27 input actions.

Python: `tools/tsuki/misc_adv_system_state.py` (maps, readers, save checker, CLI) and
`tools/tsuki/misc_adv_system_state_emu.py` (emulation checks). Tests:
`tools/tests/test_misc_adv_system_state.py`. Samples: `samples/misc_adv_system_state/`. Section 13
of adv_state.md records the review repair of 2026-10-25.
