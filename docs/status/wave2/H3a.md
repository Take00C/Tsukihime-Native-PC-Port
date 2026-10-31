# H3a: oracle gaps for M2, part 1 (PACE 7.2 #8, followups.md)

Written 2026-10-25 by the H3a agent (first pass about 19:58-20:45, repair after review about
20:58-21:30 local time). Every number below comes from a run in this session. Python only (plus
C# test runs to measure the effect on the consumers). Nothing is committed; the lead commits
after review. Files written: `tools/oracle/` (`export_slice.py`, `manifest.py`, `run.py`),
`tools/tests/test_oracle.py`, `tools/tests/test_oracle_m2.py`, this file. The golden slice was
regenerated through `python -m oracle.run slice`.

## Verdict

**Done.** `python -m oracle.run slice --verify` reports **0 problems** on the regenerated slice,
with the new parts and the provenance (drift) checks (command and output below). 219 of 219
recorded cross-checks pass (178 before H3a). `tools/tests/test_oracle.py` + `test_oracle_m2.py`:
**29 passed** (15 + 14), and the real-data tests delete their exports.

## Repair after review

| review finding | fix | proof (runs of 21:08-21:20) |
|---|---|---|
| **major**: `provenance_problems` re-read entries from `image/<archive>.mrg`, so the image area's 23 entries named `allscr.mrg` / `ft_*.mrg` became `ft_ko.mrg.mrg` "missing" | `manifest.entry_archive`: a name that already ends in `.mrg` is the file name, any other name is a stem of `image/<stem>.mrg`. Both forms are now defined in the schema docstring (`input_entries.archive`) | `python -m oracle.image --verify`: **1725 archive entries re-read, 0 problems** (before: 29 problems = 23 false `.mrg.mrg` + 6 drift). New test `test_entry_archive_names_with_and_without_suffix` (both forms re-read; a flipped byte is reported once per form; no `.mrg.mrg`) |
| (same finding) the fallback "every recorded source" for manifests without `tool_used` made other packages' verifies fail on modules they never load (the image area was flagged for `oracle/export_slice.py`) | such a manifest cannot say which sources it used: its changed sources are now printed as `unattributed` notes, not problems; `--strict-provenance` (`strict_legacy=True`) restores the old behaviour. Input and entry drift stay problems for every manifest | runner verify, default / `--strict-provenance`: image 0 / 6, catalog 0 / 6, audio 0 / 7 problems; `oracle.data run --verify` 0. Tests: `test_provenance_detects_tool_source_drift`, `test_runner_verify_reports_drift` |
| minor: `tool_used` over-approximated (every module the process had loaded), so `s4` was flagged for `wait_model.py` | `oracle.run` now exports every part in its own interpreter (`export_slice.export(isolate=True)`; the parent writes the area manifest). A part's `tool_used` is the exporter's own imports plus what that part loaded; `oracle/run.py` is no longer in it. The area manifest's `tool_used` is the union of its parts' lists | regenerated slice: `tsuki/wait_model.py` and `tsuki/misc_audio_runtime_semantics.py` are listed by **models only**, `tsuki/misc_layer_state_and_motions.py` by eboot, models, retail; `oracle/run.py`, `tsuki/compose.py`, `tsuki/quake.py`, `eboot/uicalls.py` by none. Part lists: 22 modules (scripts, text, layouts, pictures, audio, fonts, s4), 25 models, 26 retail, 29 eboot (was 26-30 each). Real-data test `test_slice_models_part` exports `models` then `text` isolated and asserts text does not list `wait_model.py` |
| minor: the `models` manifest listed 2 inputs although the part reads eboot.bin, allscr.mrg, script_text.mrg and the fonts | `PartWriter.track_reads()`: while a part runs, every file of the game folder or the repository that the process opens for reading (the interpreter's `open` audit event: `open`, `io.open`, `os.open`, `numpy.memmap`) becomes an input, unless the exporter recorded it itself. Python code, `.venv`, `.git` and the area being written are left out. `generator.reads_tracked` says the list was observed. The allscr sections behind cached script analyses are added as entries (`_record_analyses`) | `models`: inputs **2 -> 7** (`eboot.bin`, `image/allscr.mrg`, `image/ft_en.mrg`, `image/ft_ja.mrg`, `image/script_text.mrg` added), entries 0 -> 4 (allscr 3..6). Other parts gained inputs the same way: eboot +allscr, script_text; text +allscr; layouts +eboot, allscr, script_text; pictures +eboot, allpac1/1_2/4.nam, allscr; audio +allscr; retail +eboot, allpac1/1_2/3/4.nam, script_text; scripts, fonts, s4 unchanged. Area totals unchanged (44 inputs, 199 entries): the parts that read them had recorded them already. Test `test_track_reads_records_the_files_a_part_opens` |
| minor: "the done state does not survive concurrent edits" | the slice was re-exported at 21:12 after `wait_model.py` had been quiet since 20:54; with per-part `tool_used`, a later edit of `wait_model.py` flags only `models` (`python -m oracle.run slice --parts models` clears it) | final verify below |
| minor: status file said s4 had "170 archive entries recorded" | corrected: `s4/manifest.json` has **154** entries (154 distinct pictures); 170 was s4 + retail before the repair | `len(input_entries)` of s4/manifest.json = 154 |
| minor: "6,042 sampled motion values" | corrected below: 6,042 per-frame sample rows over 9 motions, holding **17,248** field values | counted in `models/motion_samples.json` |
| minor: flaky C# allocation test | not H3a's code (see open item 1); recorded for its owner | - |

The repair changed no data file: after the regeneration all **130 files have the same SHA-1** as
before the repair (only the part and area manifests differ: inputs, entries, `tool_used`,
`reads_tracked`). The isolated export takes **57.1 s** (54.4 s in a scratch trial) instead of
41.6 s, because each part starts an interpreter and redoes the script analyses it needs.

## What was done (first pass)

| followups.md / PACE item | where | result |
|---|---|---|
| catalog records for the 23 `_STCG` names of `00_01` | `eboot/catalog.json` key `seen_records` (39 records: every `_STCG` name the traced statements run) | 39 records, each with picture-name index, the 72-byte catalog record, file / overlay ids, `picture_id` (0x82CE0), `stcg_id` (the `>= 0xA8D` clamp, FORMATS 2.2), `seen_index` (gallery.md 2.2) and its uses. Checks: 39 names; the 23 undisplayed keys equal the followups list; all resolve; resolver index == `Unlocks.lookup_picture`. `records` (the 18 displayed pictures) is unchanged, so `GoldenSource` reads the file as before |
| retail path, RR 7.6, jump handling in the tracer | `export_slice.trace_path` (follows `_JUMP(scene [, label])` across scripts, variables carry over); part `retail` | START_SCRIPT (69 statements) -> `_JUMP(240,START)` -> `00_00` up to `_VPLY(D10_00_00_0000_0,03fb5)` at 0x2341: **577 executed steps**, `00_00` skips 18..46 (`_IF__(F026,ne,0,LV00000)`), 26 messages (first text id 107), 6 pictures (WHITE, IMG2345, IMG2183, IMG1696_11_11, IMG1696, IMG2343) as tiles + headers, motions 1..5, voice #16309 and 9 SEs as float PCM. 26 checks, all from RR 7.6: 37 statement names, the 11 new names (ATSV CLO3 MSAD SCH2 SEFD SEVL SHAF SHS2 SQK2 SQS2 SSS2), `_STBR(4,23)`, `G[10] = 1`, voice 16309 ... `trace_script` is unchanged (it still refuses JUMP) |
| `_SEPL(7,1,MOON_SE03028,,`001:8000,,`017:30)` SoundModel trace | part `models` -> `models/sound_sepl.json` | `SoundModel(Config())`, 600 frame updates: plays vse #598 looped, step 0.0625 (= `30*1000/60/8000` in float32), **target 30.0 reached at frame 480** (RR 7.5 check 7); channel record per frame, library calls, all 26 records at the end |
| `_WTTM(4000,1)` | `models/waits.json` (all 13 `_WTTM` of 00_01, 00_02 and the retail path) | `wait_model.wttm_apply` + `tick_reveal` + `adv_update`: `_WTTM(4000,1)` resumes the script at **frame 240** with no input and with decide pressed every frame; `_WTTM(1000,0)` resumes at frame 1 when clicked |
| reveal speed | `models/reveal.json` | speed 35 -> **0.476190507 counters per frame** (bits 0x3EF3CF3E, engine float32 order; check rounds to 0.476), speed 15 -> 1.1111112; per slice message (ja, en, unread and read) the head / tail bits per frame until wait bit 0x40 |
| sampled motion values | `models/motion_samples.json` | 9 motions (22523..22526, 1..5): 6,042 per-frame sample rows (`motion_time_ms`, `motion_keytime`, `motion_value`) holding 17,248 field values. Checks: 22526 field 9 = 1200 / **1074 at frame 1050 (17.5 s)** / 912; 22525 field 5 = 255 -> 0 |
| manifest provenance | `manifest.tool_used`, `manifest.provenance_problems`, `manifest.resolve_input`, `manifest.entry_archive`, `PartWriter.track_reads`; runner `--verify` | every part manifest records `generator.tool_used` and `reads_tracked`. `provenance_problems` reports a used tool source that changed or vanished, and inputs / archive entries whose size, SHA-256 or SHA-1 changed (entries are re-read at their offset in their archive file); game inputs need the game folder and are otherwise reported as not checked. `oracle.run <area> --verify` runs it (`--quick`: re-hash only inputs <= 64 MB; `--no-provenance`; `--strict-provenance`). `verify_area` itself is unchanged |
| document the `project:` prefix | `manifest.py` module docstring (the schema) | the three input path forms (game-relative, `project:<repo-relative>`, absolute), the two `input_entries.archive` forms and the meaning of entry offsets, observed inputs, `tool_sources` / `tool_digest` / `tool_used` |
| tmp_path cleanup | fixture `export_tmp` in both test files | real-data exports go to `tmp_path/export`, removed with `shutil.rmtree` at teardown (pass or fail). After the final test run (21:19) `%TEMP%/pytest-of-<user>` held 526 KB, no `export` folder, no file over 50 KB: all from the synthetic tests |
| S4's 154 picture SHA-1s as golden data | part `s4` -> `s4/pictures.json` | the spike's picture list (`samples/spikes/s4/oracle.json`, recorded as a `project:` input) decoded again with `tsuki.image`: **154 / 154 SHA-1s equal** the spike's; format counts hep8 135, rgb565lo+a 10, pal8 8, pal4 1; 154 archive entries recorded |

The refactor of `analyse_script` (asset collection moved to `collect_assets`) changed none of the
existing slice files: after the first regeneration every file of the seven old parts had the same
SHA-1 as before except `eboot/catalog.json` (new `seen_records`) and
`eboot/textbox_tables.json` (changed by package B's `tools/tsuki/textbox.py` fix; see open item 1).

## Commands that prove it (PowerShell or Git Bash, from the repository root)

```
$env:PYTHONPATH = "tools"
.venv\Scripts\python.exe -m oracle.run slice                 # 130 files, 623,357,514 bytes in 57.1 s
.venv\Scripts\python.exe -m oracle.run slice --verify        # final run: see below
.venv\Scripts\python.exe -m oracle.image --verify            # and oracle.catalog / oracle.audio, oracle.data run
.venv\Scripts\python.exe -m pytest -q -p no:cacheprovider tools/tests/test_oracle.py tools/tests/test_oracle_m2.py
.venv\Scripts\python.exe -m pytest -q -p no:cacheprovider tests/Tsuki.Tests.Data/Catalog/test_oracle_catalog.py tests/Tsuki.Tests.Data/Oracle/test_oracle_image_data.py
"C:/Program Files/dotnet/dotnet.exe" build tests/Tsuki.Tests.Data/Tsuki.Tests.Data.csproj -warnaserror --artifacts-path artifacts/H3a
"C:/Program Files/dotnet/dotnet.exe" test tests/Tsuki.Tests.Data/Tsuki.Tests.Data.csproj --no-build --artifacts-path artifacts/H3a
```

Results of the repair runs:

| command | result |
|---|---|
| `oracle.run slice` (all 10 parts, each in its own process, movies hashed) | 130 files, 623,357,514 bytes, 57.1 s; parts: eboot 9, scripts 5, text 1, layouts 2, pictures 44, audio 22, fonts 4, retail 38 (109.3 MB), models 4 (2.4 MB), s4 1; 0 failed checks in every part |
| `oracle.run slice --verify` | see "Final verify" below |
| `oracle.image --verify` / `oracle.catalog --verify` / `oracle.audio --verify` / `oracle.data run --verify` | 0 / 0 / 0 / 0 problems (image: 1725 entries re-read). With `--strict-provenance`: 6 / 6 / 7 (unattributed sources of manifests that predate `tool_used`) |
| pytest `test_oracle.py` + `test_oracle_m2.py` | 29 passed (15 + 14) in 35.0 s, and again after the regeneration (38.6 s) |
| pytest of the other oracle consumers (catalog, image data) | 19 passed |
| `pyflakes` and `mypy --ignore-missing-imports` on the three modules and both test files | clean |
| `dotnet build` / `dotnet test` of `Tsuki.Tests.Data` | see "C# consumers" below |

### Final verify

The last source change of this package was at 21:06:51 (`export_slice.py`); the slice was
regenerated with `oracle.run slice` at 21:12 (57.1 s, 0 failed checks in every part; at that time
the most recent tool-source edit by another workstream was `tools/tsuki/wait_model.py` at
20:54). The verify at 21:19:27 gave

```
> python -m oracle.run slice --verify
[slice] provenance: tool sources: 31 checked, 0 drifted
[slice] provenance: inputs: 44 checked by size, 36 by SHA-256, 199 archive entries re-read
[slice] verify: 0 problems (3.2 s)
```

### C# consumers

`dotnet build tests/Tsuki.Tests.Data -warnaserror --artifacts-path artifacts/H3a`: 0 warnings,
0 errors (21:16; a first attempt at 21:11 failed on a half-edited
`tests/Tsuki.Tests.Data/Text/ScriptTextTests.cs` of another package, which was fixed at 21:15).
`dotnet test` (21:16:50, against the regenerated slice): **505 passed, 6 failed** of 511.

* The golden-data tests alone (`--filter FullyQualifiedName~Tsuki.Tests.Data.Golden`, which
  includes the manifest, input and archive-entry checks that read the new manifests): **64 passed,
  1 failed**; the failure is H2's `TextboxTablesDecodeToTheirExportedValues` (expected
  `[scales, window_sizes]`, got `[]`; open item 1).
* The other 5 failures are in files other packages were editing during the run (mtimes 21:07-21:17:
  `Text/MarkupAndExpansionTests.cs`, `Text/ScriptTextTests.cs` together with
  `src/Tsuki.Data/Text/Markup.cs`, `Scripts/AllscrTests.cs`, `Fonts/FontPageTests.cs`):
  `NineThousandRandomTokenStreams...` (1 random-stream mismatch), `AllscrTests.OpeningReads...`
  ("two small scripts read 66310 bytes"), `ThePaletteAlphaConversion...` (a synthetic test),
  `ScriptTextTests.AnEngineLineIsCut...` (998 vs 767) and
  `ScriptTextTests.TheSliceExportAndTheDataExportAgreeWithTheReader` (the reader's `Raw` no longer
  returns the trailing CR LF that `text/lines.json` holds; that file is byte-identical to the pre-repair export). None of them
  can come from this repair: every one of the 130 slice data files has the SHA-1 it had before
  the repair, and these tests read data files, not manifests.

## Open items

1. **Not H3a's code, seen in the C# runs.** (a) `GoldenSourceTests.TextboxTablesDecodeToTheirExportedValues`
   (tests/Tsuki.Tests.Data/Golden, owner H2) asserts that the tables `scales` and `window_sizes`
   do **not** match `tsuki.textbox`; package B corrected those 1-ulp constants in
   `tools/tsuki/textbox.py` (18:26), so the export says every table matches and the two findings
   are gone. The test should expect an empty list (or read `matches_tsuki_textbox` from the
   JSON). (b) The reviewer saw `ArchiveTableTests.OpeningTheGiantArchiveAndReadingEntriesNeverLoadsItIntoMemory`
   fail once (309,169,456 bytes): it measures the process-wide `GC.GetTotalAllocatedBytes`, which
   tests running in parallel also raise; its owner should measure per thread
   (`GC.GetAllocatedBytesForCurrentThread`) or put the test in a non-parallel collection.
2. **Concurrent edits of tool sources.** Any later edit of a module a part loaded shows up as
   drift of exactly the parts that list it (`wait_model.py`: models) and is cleared by
   `python -m oracle.run slice --parts <those parts>`; the C# consumers do not depend on that check.
3. **Areas exported before `tool_used` existed** (`catalog`, `audio`, `image`, `script`,
   `textbox`, `data`): their changed tool sources are reported as `unattributed` notes until their
   owners re-export (any re-export through `oracle.manifest` records `tool_used`;
   `track_reads` is opt-in, used by `export_slice` only). `oracle.script` and `oracle.textbox` call
   `verify_area` directly and do not run the provenance check at all.
4. `tool_used` is still an over-approximation within one part: it lists every tool module the
   exporter imports at load time (22 for the lightest parts), not only the ones a part executes.
   In-process exports (`export(isolate=False)`, used by the tests) also list what earlier parts
   loaded, and reads served from the shared `SliceContext` caches are not observed again (the
   allscr sections behind cached analyses are still recorded).
5. Not modelled in `trace_path`: the JUMP handler's scene-transition edge code (VA 0xBA3C0 /
   0xBA67F: `F[110] = to`, edge_seen and F[600..] bits). There is no edge record for 110 -> 240;
   `path.json` lists the lookup result (`edge_record: null`). GOTO, GOTS, choices and the other
   `UNMODELLED_FLOW` commands still raise `SliceFlowError`.
6. The SoundModel trace starts from a fresh model with every stream reported playing; it does not
   replay the statements before the `_SEPL` (voices, other SEs) or the frame timing between them.
   The motion samples use `cur = 0` for non-animated fields and divisor 0 (no slice `_STMA` has a
   divisor). The retail path's pictures are exported as tiles (about 77 MB); GoldenSource does not
   read the `retail/`, `models/` or `s4/` parts yet (H2 / M2 owners decide whether it should).
7. Still open from PACE 7.2 #8 for H3b: the page-1 oracle render (`tools/oracle/render.py`).
