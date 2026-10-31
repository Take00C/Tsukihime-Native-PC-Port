# K1b: knife's executable tables from the PS4 eboot

Owner: package K1b. Research document: [`docs/research/knife_tables.md`](../../research/knife_tables.md) (73 rows: 24 already decoded,
34 found, 15 not found or different). No game data, no knife code or knife tables in git.

## What was built

| path | what |
|---|---|
| `docs/research/knife_tables.md` | one row per knife table: layout, count, status (a) known / (b) found / (c) not found or different, PS4 evidence (VA, segment, stride, count, match rate), reader |
| `tools/tsuki/engine_tables.py` | Python readers for every (b) table, a tiny x86-64 interpreter (`TinyX86`) for the two tables that are compiled code, canonical bytes and digests, `crosscheck` against the knife checkout, CLI |
| `tools/tests/test_engine_tables.py` | 18 tests: synthetic interpreter programs and fake images always run; real-data tests skip without the game; the knife test skips without `ref/knife` |
| `src/Tsuki.Data/Eboot/Tables/` | C# readers (namespace `Tsuki.Data.Eboot.Tables`): `TinyX86`, `HazeTable`, `SceneNodeTable`, `MotionLoopTable`, `JumpClearTable`, `FlowKeepLists`, `FclnLineTable`, `FclnNoteTable`, `VoiceSpeakerTable`, `DialogMessageTable`, `TestSeNames`, `TextColourTable`, `MessageWindowTable`, `SoftClipRadii`, `UiCellTables`, `ConfigPageTable`, `MotionFieldMasks`, ports of the older Python decoders for the gallery and the flowchart (`GalleryTables`, `FlowchartTables`), and `EngineTables`, a lazy set of all of them over one `EbootReader` (what `GameEbootTables` in K1a's file says is to be hung on its `Reader`) |
| `tests/Tsuki.Tests.Data/EbootTables/` | `TinyX86Tests` (4), `EngineTableTests` (10, `[GameDataFact]`), `EngineTableDigestTests` (1, `[GameDataFact]`) and `engine_tables_digests.json` |

Shared files touched: none. The C# project needed no change (the new folder is picked up by the SDK globs). The tests use the existing
`SharedEboot` helper and `[GameDataFact]` of `tests/Tsuki.Tests.Data`.

MPL: no knife code was translated (the interpreter, the readers, the Rust-literal parser of the cross-check and the C# classes were
written from the PS4 disassembly and the table contents), so `docs/THIRD_PARTY.md` is unchanged. The cross-check reads knife's sources
at run time from the git-ignored checkout; nothing from them is stored. The STEC tint evaluation used once for section 2.2 of the
research document re-implemented knife's blend formulas and was therefore left out of the repo.

## Proof commands and results (2026-10-26)

```powershell
dotnet build src\Tsuki.slnx -warnaserror --artifacts-path artifacts\K1b
#   Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test tests\Tsuki.Tests.Data\Tsuki.Tests.Data.csproj --artifacts-path artifacts\K1b --filter "FullyQualifiedName~EbootTables"
#   Passed: 15 of mine (TinyX86Tests 4, EngineTableTests 10, EngineTableDigestTests 1), 0 failed, 0 skipped
#   (the filter also matches 2 tests of other packages that have "EbootTables" in their names: also green)
dotnet test tests\Tsuki.Tests.Synthetic\Tsuki.Tests.Synthetic.csproj --artifacts-path artifacts\K1b
#   Passed: 357, 0 failed (includes the project-rules tests)
$env:PYTHONPATH = "tools"
.venv\Scripts\python.exe -m pytest tools\tests\test_engine_tables.py -q
#   18 passed in about 10 s
.venv\Scripts\python.exe -m tsuki.engine_tables crosscheck       # 64 comparison rows, listed below
.venv\Scripts\python.exe -m tsuki.engine_tables digests --write tests\Tsuki.Tests.Data\EbootTables\engine_tables_digests.json   # 50 tables
.venv\Scripts\python.exe -m mypy --ignore-missing-imports tools\tsuki\engine_tables.py    # Success: no issues found
.venv\Scripts\python.exe -m pyflakes tools\tsuki\engine_tables.py tools\tests\test_engine_tables.py   # clean
```

C# equals Python: both build canonical bytes for each of the 50 tables, and the SHA-256 of each is the one in `engine_tables_digests.json`
(`EveryTableEqualsThePythonReader`; the Python side is `test_digests_equal_the_stored_proof`). Timing: the motion-loop table (24,192 interpreted
calls) takes 1.7 s in Python and 90 ms in C#; every other reader is a few milliseconds.

Cross-check against knife (`python -m tsuki.engine_tables crosscheck`; matched / total):

| table | match | note |
|---|---|---|
| motion loops (MOTION_LOOPS) | 2,027 / 2,027 | also equal to the unicorn extraction of layers.md |
| JUMP_CLEARS | 19 / 19 | (161, 62) with exception = (160, 62) |
| haze | 542 / 558 | 16 entries retuned on the PS4 |
| SCENE_NODE / SCENE_ID_NODE | 527 / 527, 500 / 500 | |
| JUMP_KEEP_* and F_* lists | 38/39, 274/274, 42/42 and 38/38, 274/274, 42/42 | PS4-only: F967, F501, F500 |
| flow ROWS nodes | 1,286 / 1,286 (fields 0..6 and, with the +1 rule, all 9) | |
| flow EDGES | 628 / 629 | PS4 has 630 |
| flow TITLES, TITLES_EN, LINES | 29/30, 29/30, 11/12 | |
| SAVE_TITLES, _EN | 36/38 (knife's Japanese table; 38/38 with the widened phases 16, 18), 38/38 | |
| PHASE_TITLES, _EN, _ZH | 38/38 each | |
| GUIDE_ICONS, GUIDE_WORDS (ja) | 16/23, 26/27 | |
| FACE_RECTS, NAME_RECTS, NAME_RECTS_EN | 20/20 each | |
| BANNER_KEYS, BANNER_SRC | 10/10, 2/4 | |
| DIALOG_SYSMES | 24 / 24 | PS4 index k + 2 from dialog 21, ids +1 from 217 |
| VOICE_SPEAKERS, SPEAKER_ROW_BIT, VOICE_ROW_WORDS, TEST_SES | 55/55, 24/24, 19/19, 14/14 | |
| WINDOWS, EN_WINDOWS (x, y), WINDOW_DX, WINDOW_DY | 33/33, 56/56, 27/27, 37/37 | |
| FCLN LINES, FCLN NOTES, stage RADII | 118/120, 3/3, 6/6 | |
| MUSIC (bgm/title/variants, length), MUSIC_LOOP (start, end+1), MUSIC_CREDITS | 95/95, 84/95, 95/95, 87/95, 95/95 | |
| ENDINGS, EN_ENDING_DONE | 25/25, 24/24 | |
| CG_GROUPS (+ overseas groups), CG_ALIASES, CG_ORIGINS, CG_LANGUAGE_STRIPS, SEEN_ALIAS | 1,972/1,972, 105/105, 19/19, 11/11, 2,144/2,144 | |
| fill colours @c1..@c5 | 5 / 5 | RGB565 against RGBA8 |
| STEC modes without tint layers | 23 / 23 | exact for x >= 1 |
| exe.rs bundle: scene_ids, image_names, image_info (flags / size), motion_keys, ends, lens, masks | 549/549, 14,744/14,744, 14,744 and 14,713 of 14,744, 71,859/71,905, 24,192/24,192 twice, 24,193/24,193 | |
| line_break_pairs | 0 / 1 | not in the eboot |

## PS4 versus Switch

1. SYSMES numbering: the PS4 ui_text id is knife's SYSMES number + 1 from 217 (the overseas build inserts one message there; knife moves it to 1498). The confirm-dialog table has two extra PS4 entries after dialog 20.
2. Retuned or different PS4 data: 16 haze entries, 46 motion keyframes in 33 motions, one flow edge pair, 11 music lengths, 8 music loop ends, 2 FCLN keys, 3 button-guide rows (rows 13..22 of the icon cells are the PS4 pad icons), the flow chapter label row 29.
3. The F-word keep lists carry three PS4-only variables (F967, F501, F500); knife's `JUMP_KEEP_WORDS` has F026 which the PS4 list does not.
4. Storage differs: the loop switch and the jump clears are code on the PS4 as on the Switch (read here by interpreting the function); STEC is evaluated LUTs on the PS4, shader parameters on the Switch; SE and voice names are in archives; knife's `SCENE_NODE` is `i16`, the PS4's `i32`.
5. Possible erratum for adv_state.md 9.1: the config type codes 3 / 4 / 5 sit at knife's minus / slider / plus coordinates (called label / value / slider there) [H].
6. The phase-title cells for Korean use the third ("zh") table on the PS4.

## Open issues

1. Flow-jump special pairs (knife `jump_special`, PS4 function 0xB9460 with a 500-entry jump table at 0x220C24) and the button-guide sequences (0x6A3C0) are code on the PS4. Nothing to read; a port transcribes knife's logic and checks it against those functions.
2. Not found (15 rows, section 3 of the research document): nn::font line-break tables, SE names (use `se_nam.mrg`), bubble widths, system sound ids, voice ids, radio cells, save info route, edge colours, ruby scale, config defaults, menu strips and logo particles, the button-guide sequences, the flow-jump pairs.
3. The `Order` field of flow nodes and the meaning of haze field 7 stay [H].
4. Build note: while this package was written `src/Tsuki.Engine/Stage/` (another package, untracked) did not compile for about 30 minutes, so intermediate C# builds ran on a scratch copy of the HEAD tree plus the K1b files (`artifacts/K1b-shadow`, outside the repo sources); the final build and test commands above ran on the real tree.
