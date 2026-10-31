# Script-VM variables and the engine re-check errata (pointer)

The full document is **[vm_variables.md](vm_variables.md)**: variable arrays, operand forms and
the first-byte dispatch of every reader, per-message C slots (F823), the choice commands
(`_SELR` / `_SELD` / `_SELS` / `_SELA` / `_GOTS`, `_D000` = F[0], the `_SELA` save prompt and
its F1118 gate, scan mode, the History_Snapshot gate, both result stores), which F values a
history record and a save slot keep, the corpus census, and the applied engine re-check
findings (a)-(d) with the errata for other documents (section 11). Section 15 lists the review
findings on the first version and the changes made for them.

Code: `tools/tsuki/vm_vars.py` (model), `tools/tsuki/misc_vm_variables_errata.py` (unicorn
harness, sweeps, fuzzer, census, errata checks). Tests: `tools/tests/test_vm_vars.py`,
`tools/tests/test_misc_vm_variables_errata.py`. Samples: `samples/misc_vm_variables_errata/`.

Summary of the errata (all verified):

| | finding | where applied |
|---|---|---|
| (a) | `C<n>` as a `_WKST` / `_WKAD` target is F[F823 + n]; in value slots `C`, `Z`, `X`, ... are literals; Script_Prefetch has no C case | script.md 3.5, engine.md 5.5, RUNTIME_REQUIREMENTS.draft.md item 6 |
| (b) | Script_Prefetch's V case accepts only `VPLY` / `VPL2`: 43 entries (41 names + `J***` + `ZY**`) | engine.md 5.4, RUNTIME_REQUIREMENTS.draft.md line 59, `eboot/scriptvm.py`, `samples/engine/vm_commands.json`, `tests/test_eboot.py` |
| (c) | save image unused area 0x3E0900..0x400000 (0x1F700 bytes) | engine.md 7.2 |
| (d) | VM segment count 1,083,168 (not 1,083,717) | vm_variables.md 11 (engine.md 5.3 / 11 not edited) |

Review repair (vm_variables.md 15): `_SELA` opens its prompt only when F1118 = 0 and always
clears F1118. `_SELD` / `_SELS` / `_SELA` change nothing in scan mode. The `_SELR` snapshot gate
reads F929 and then F1102. `_SELS` stores the button index, and `_SELA` stores nothing. All of
this is now emulated through ScriptStep's own handlers and fuzzed with scan mode, the skip
counter, F1118 and the gate words (61,000 cases, 0 mismatches).
