# Boot, title and choice flow (alias)

The documentation for the "boot_title_choice_flow" coverage gap is
[`system_flow.md`](system_flow.md). It covers:

- the boot sequence: system save, save-damaged notice, autosave notice and the dead logo /
  disclaimer states;
- the title screen: TM_CI movie, caution screens, the G[2] background state with the
  rewritten UI-table record 0, the menu and its draw list;
- the choice menu: `_SELR` / `_SELD` / `_SELA` / `_SELS`, the 640 x 344.7 text canvas, locked
  lines and the SELECTTXT images that are never drawn;
- errata for archives_ui_movies.md (UI lookup split 695 / 4 / 8), engine.md 8.1,
  FORMATS.md 5.4 and script.md;
- the review response (section 10). The repair of 2026-10-25 added:
  - the decided phase, which hides every line but the chosen one;
  - the previous-cursor glow cross-fade;
  - the "0." prefix of a 10th line;
  - the title logo-alpha schedule and the still-while-seeking rule;
  - the full title return table 0x209DC0;
  - a frame-level still-vs-movie scan.

Python reference: `tools/tsuki/misc_boot_title_choice_flow.py`.
Tests: `tools/tests/test_misc_boot_title_choice_flow.py`.
Samples: `samples/misc_boot_title_choice_flow/` (title, choice and boot renders including the
decided and cross-fade choice frames, draw lists, boot sequences, eboot tables, the
still-vs-movie frame scan).
