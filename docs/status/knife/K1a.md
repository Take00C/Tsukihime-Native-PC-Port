# K1a: A3a check, GameFolderSource, census T0 (PACE row 10, ROADMAP M1 acceptance 5 and 7), status

Package K1a, 2026-10-26. Machine: AMD Ryzen 7 5800X3D, 64 GB, Windows 11, .NET 10.0.402, Python 3.11.9. Numbers, commands and identifiers only; no game text.
knife was not used by this package (nothing translated, no MPL file, nothing added to `docs/THIRD_PARTY.md`); "PS4 vs Switch": nothing to record.

## Verdict: done

| goal | result |
|---|---|
| 1. A3a checked against PACE row 10 | every item of the scope exists and passes on the real data; nothing was missing in code (table below). What A3a lacked was the proof document, `GameFolderSource` and the census; they are here |
| 2. `GameFolderSource` | `IGameData` over the real folder, every member, memory-mapped archives and positional reads. **Agrees with `GoldenSource` on everything the slice holds** (scripts 2 and 3, text ids 0..7 in ja and en, 130 font pages, 22 pictures tile for tile, 11 audio clips sample for sample, 134 movie paths, scene ids, 18 catalog records, 4 motions, 17,423 glyph lookups) |
| 3. Census T0 | 166,437 references of the 549 scripts, **each equal to the Python oracle's line**; everything resolves except the allow-list; runs in **2.0 s** (so it is a test, and a CLI command as well) |
| 4. Small fixes | `Tsuki.Tests.Data` references `Tsuki.Platform`; the giant-archive memory test counts per-thread allocation and runs alone |

Tests: `Tsuki.Tests.Synthetic` 357 / 357 and `Tsuki.Tests.Data` 599 / 602 on the live tree at the end (the 3 failures are the other package's in-progress `Stage/StageEngineTests`, not K1a). In the private snapshot (HEAD + K1a's files, below) `Tsuki.Tests.Data` is **570 / 570, three runs in a row**, 50 s each, and `Tsuki.Tests.Synthetic` 294 / 294. Build with `-warnaserror`: 0 warnings, 0 errors, Debug and Release.

## 1. A3a against PACE row 10

A3a was cut off by API errors and left no status file. Its files in `src/Tsuki.Data/Text`, `Scripts`, `Fonts` and their tests build and pass; the five failures that the H3a review saw in them (21:07-21:17 on 2026-10-25, while other packages were editing) are gone.

| PACE item | Python (the oracle) | C# | real-data tests (`tests/Tsuki.Tests.Data`) | state |
|---|---|---|---|---|
| `text` (973 lines) | `tsuki.text` | `Text/TextTable`, `ScriptText`, `TextExpansion`, `TextRules`, `Markup`, `MarkupTokens` | `ScriptTextTests` (5 languages x 43,969 lines: offset tables and blobs byte for byte, engine copy, tokens, runs, plain text, markup census), `MarkupAndExpansionTests` (6,000 random expansions, 9,000 random token streams) | done |
| `text_refs` (361) | `tsuki.text_refs` | `Text/TextReferences` | `TextReferencesTests` (all 549 scripts command for command, reference index, groups, census, 67 unreferenced ids, transcripts in 4 languages, choice slots) | done |
| `font` (995) | `tsuki.font` | pages: `Fonts/FontArchive`; glyph lookup and layout: `Tsuki.Engine/Text` (package B) | `FontPageTests` (325 pages of the 5 files equal) | done; see "not ported" |
| `font_eboot` (327) | `tsuki.font_eboot` | `Fonts/EbootFontData`, `FontTableProbe` | `FontTablesTests` (17,423 lookup entries, all rectangle records, self-check, probe, slice) | done |
| `script_tables` (377) | `tsuki.script_tables` | `Scripts/EngineCommandTable` (112 entries, aliases), `SceneTable` (549), `MotionTables` (24,192 motions, 71,905 keyframes); movie table 134: `Catalog/MovieTable` (A2) | `ScriptTableTests` | done |
| allscr label tables | `tsuki.script` (`AllScr`, `parse_labels`) | `Scripts/AllscrArchive` (names, label tables, script sections) | `AllscrTests` (549 scripts and 549 label tables equal) | done |
| scene ids 549, movie table 134, motions, font lookups 17,423 | as above | as above | counts asserted in the tests above | done |

Not ported, on purpose: `ScriptText.find/lines/tokens` and the `_main` command lines of the Python modules (conveniences); `Font.render`, `charset`, `render_text`, `iter_pages` (developer rendering); `Font.layout` and the glyph choice (`char_glyph`, dashes) are in `Tsuki.Engine/Text` (`LanguageGlyphs`, `TextLayout`), where package B tests them on 601 + 175,876 lines. No oracle exporter under `tools/oracle` exists for these areas; the tests call thin Python callers (`Text/text_oracle.py`, `Scripts/script_tables_oracle.py`, `Fonts/font_oracle.py`) through `OracleRun`, as A3a built them.

Mutation check of A3a's code with K1a's tests (a source change, rebuild, the Text / Scripts / Fonts / Source tests; restored after each): M01 engine-line cap 0x301 -> 0x302, M02 label hex base, M03 keyframe i64 offset, M04 CSM1 unswizzle shift, M05 wide-grid base, M19 text line loses a byte, M20 motion first keyframe, M21 scene ids big-endian, M22 one handler address, M23 font alpha 0x80, M24 allscr section base: **all 11 killed** (by 1 to 17 tests each; the real-data oracle tests kill 10 of them).

## 2. `GameFolderSource`

`src/Tsuki.Data/GameFolderSource.cs` (namespace `Tsuki.Data`), with helpers `GameEbootTables.cs`, `UiSheetNames.cs`, `Audio/SeNames.cs`.

| member | how |
|---|---|
| `GameFolderSource.Open(folder, IAudioDecoder? = null, decodeParallelism = 1)` | reads `eboot.bin` (12.7 MB), the five `.nam` tables and the SHA-256 of the eboot: **70 ms, 25 MB allocated** (Debug, cold JIT). Nothing else is opened (a test opens it on a folder holding only those six files). The folder comes from the caller: `Tsuki.Cli.GameFolderLocator` (`--game`, `TSUKI_GAME_DIR`, the dev default) lives in Tsuki.Cli, which Tsuki.Data cannot reference; the CLI census passes its result |
| `Build` | `BuildInfo(folder, eboot size, SHA-256)`; equals the golden export's identity and `IsAnalysedBuild` |
| `Eboot` | `GameEbootTables : IEbootTables`: `Reader` (the strict `EbootReader`), `Catalog` (A2), `SceneIds`, `Movies`, `Motions`, `FontData`, `FontTables`, `UiText`; the heavy ones are built on first use |
| `ScriptCount`, `Script(k)` | `AllscrArchive`: one section is read with a positional read and MZX0 + XOR decoded, segment starts from `ScriptSegmenter` |
| `TextLineCount`, `TextLine(lang, id)` | `ScriptText`: offset tables on first use, one blob per language on first use; the bytes of the engine's copy routine (no CR LF, at most 0x2FF bytes). Ids outside 0..43,968 are `ArgumentOutOfRangeException`. All 5 x 43,969 lines are compared with the blob bytes through the interface |
| `FontPage(lang, page)` | `FontArchive`: one page is read and decoded per call |
| `Picture(archive, entry)` | archives 0..5 memory-mapped on first use (the 24 GB `allpac1` costs address space only); `ImageMzp.ToPicture`. Extras: `OpenImage` (header and tile table only), `Picture(ImageId)` |
| `Audio(bank, index)` | the injected `IAudioDecoder` (`Tsuki.Media.Audio.MediaAudioDecoder`), fed by `AudioPayload`: RIFF/WAVE of `bgm`/`vse`, VAGp of `vag`, and for the voice banks the LV chunk and MZX0 stream unwrapped. Without a decoder `Audio` throws `InvalidOperationException` |
| `Movie(index)` | the table's `/app0/...` path under the folder, resolved to the file on disk; `FileNotFoundException` if the file is missing (`GoldenSource` returns the path unchecked) |
| `UiSheet(set, lang, child)` | the allpac3 archive `UiSheetNames.FileName` (the loader's rule of VA 0xC2AF0; six sets have no language), found by the engine's NAM search; the child is an image MZP. No clamp: the engine's clamp for registration records is `UiTextureTables.ChildIndex`; extras `UiSheetChildCount`, `OpenUiSheetImage` |
| extras | `Archive(name)`, `Scripts`, `Text`, `Catalog`, `Tables`, `SoundEffectNames` (`se_nam.mrg`: four tables and the lookup of VA 0xCC790), `FontFile`, `AudioCount`, `RootDirectory` |

Thread safety: all members; every call decodes afresh and returns new objects, nothing large is cached. `Dispose` unmaps the archives.

Tests (`tests/Tsuki.Tests.Data/Source/`, 29):
- `GameFolderSourceAgreesWithGoldenTests` (9, `TSUKI_DEV`): build identity, scripts (bytes and segment starts), text lines, 130 font pages, 22 pictures tile for tile, 11 audio clips sample for sample, 134 movie paths and records, eboot tables (549 scene ids, 18 catalog records, 4 motions with keyframes, 16 + 17 glyph tables with 17,423 lookup entries, wide-grid bases, tags), and the things the slice lacks are answered by the folder.
- `GameFolderSourceTests` (11): opens on six files only; missing folder or eboot; 21 refused arguments; Korean text and fonts; audio payloads and counts (132, 1,327, 16,464, 2, 19); **all 71 UI sheet archives, 3,699 children, every child parses as an image**; a sheet child equals a direct decode; **all 220 registration records** of the 21 UI tables name a sheet in allpac3 and a child that exists (documented exceptions: 15 `extra_parts.mrg`, 5 `ed1_parts.mrg`, 1 rewritten at run time, 1 clamped); 12 threads give the answers of one; dispose; **all 219,845 text lines** equal the engine's copy of their blob bytes (0 are cut at the cap, 9 are under 3 bytes).
- `GameFolderSourceMemoryTests` (alone in the process, `SerialMemoryCollection`): entries of the 24 GB archive are touched with less than 1 GB managed allocation and 1.5 GB private growth.
- `SourcePartsTests` (8, no game folder except one): `SeNames`, `UiSheetNames`, the allow-list on synthetic input.

## 3. Census T0

`src/Tsuki.Data/Census/` (`ReferenceCensus`, `CensusAllowList`, `GlyphCensus`, `LayerNameTracker`, `StatementView`), command `Tsuki.Cli census [--game DIR] [--threads N] [--no-targets] [--no-glyphs] [--sample N] [--refs FILE] [-v]` (registered in `CliApp.cs`; exit 1 on a failure), tests `tests/Tsuki.Tests.Data/Census/`, oracle `Census/census_oracle.py`.

Part 1 walks every statement of the 549 scripts through `GameFolderSource` (segments, `StatementView` = the `tsuki.script.Command` readers) and resolves each reference as `tsuki.script.command_assets` does: pictures with the `_hh_vv` suffix and the layer tracker (`picture_key`, `LayerTracker`), voices, sound effects, music, movies, scene jumps and labels, text ids, motion ids, wipe masks. **All 166,437 lines equal the Python oracle's lines** (`EveryReferenceOfEveryScriptEqualsTheOraclesLine`). Part 2 reads every distinct target through the source. Part 3 (fonts) checks every character of every referenced text line in ja, en, zc and zt.

| kind | references | distinct | resolved | allow-listed | failed |
|---|---:|---:|---:|---:|---:|
| image (`_STCH _SCH2 _SPFC _SPRD _STCG`) | 59,730 | 10,967 | 59,724 | 6 | 0 |
| voice | 16,705 | 16,459 | 16,705 | 0 | 0 |
| sound effect | 14,543 | 1,314 | 14,543 | 0 | 0 |
| music | 1,171 | 104 | 1,171 | 0 | 0 |
| movie | 48 | 40 | 48 | 0 | 0 |
| scene jump | 1,741 | 545 | 1,741 | 0 | 0 |
| label (629 `_JUMP` + 2,551 local) | 3,180 | 1,217 | 3,180 | 0 | 0 |
| text id | 43,930 | 43,902 | 43,930 | 0 | 0 |
| motion | 24,442 | 24,024 | 24,349 | 93 | 0 |
| wipe mask (`_FADS(2, ...)`) | 947 | 49 | 947 | 0 | 0 |

Targets read through the source: 22,845 picture files opened (header and tile table; the full-size layers and their allpac2 copies), 16,459 voices, 1,314 sound effects and 104 tracks read and unwrapped (RIFF/WAVE), 160 movie files (40 ids x 4 languages), 175,608 text lines (43,902 ids x 4 languages), 24,022 motion entries, 49 wipe masks, and a sample of 48 pictures decoded to pixels and 48 audio entries decoded to PCM. 0 failed. Glyphs: ja 43,902 lines, 2,719 distinct characters, 30 pages; en 85 characters, 3 pages; zc 3,434, 36 pages; zt 3,555, 38 pages; every glyph rectangle lies inside its page; the counts equal the oracle's.

**Allow-list (103 entries, all documented, `CensusAllowList`).**
- the lead's two groups: **5 picture script bugs** (`IMG5007_02_02`, `IMG5007_00_27`, `IMG1637_28_28`, `Z20_M_61_00_A00_10_00`, and the `_SCH2` name `SIK_A_32_00_N00_02_30` that inherits its suffix; 10,962 of 10,967 effective names resolve) and **3 sprite bodies on no disk** (`ARU_M_03_35_N00_B`, `ARU_S_03_35_N00_B`, `NOE_L_60_03_N00_B`; a test audits the whole catalog and finds exactly these three; scripts reach only `NOE_L_60_03_N01`, once, and its face layer is found);
- documented elsewhere and found by the census: **92 `_STMA`/`_SGMA` with motion id 0** (no motion) and **1 motion id 101960** past the table (script.md doc_checks item 3); **4 characters without a glyph** in the Chinese texts (zc `ｂ` x3, `ａ` x2, `鲽` x1; zt `鰈` x1; fonts.md 5.4 and 9, which says the engine draws the blank fallback);
- one reading rule, not an exception: `_FADS(2, ms, 0)` (2 uses) selects `MASK_TOP.MZP` itself, the one-tile placeholder of archives_ui_movies.md 5.4; rules 1..52 are `RULE_nn.MZP`.

Mutation check of the new code (rebuild + Source / Census / Text / Scripts / Fonts tests; restored after each): M06 movie path, M08 variant-digit length rule, M09 SE key length, M10 marker display name, M11 allow-list entry, M12 UI sheet suffix, M13 voice unwrap, M14 archive names 1 / 1_2 swapped, M15 SE section order, M16 glyph table threshold, M17 UI child index: **all 11 killed**. Two survived and are equivalent on the retail data: M07 (`TextLine` returns the line without CR LF instead of the engine copy; the two differ only for a line over 0x2FF bytes, and no retail line is) and M18 (the scene id read with the 32-bit inline reader; no id exceeds 16 bits). In total 24 mutations, 22 killed, 2 equivalent.

## 4. Small fixes

- `tests/Tsuki.Tests.Data/Tsuki.Tests.Data.csproj`: explicit `ProjectReference` to `Tsuki.Platform`.
- `OpeningTheGiantArchiveAndReadingEntriesNeverLoadsItIntoMemory` moved to `GiantArchiveTests.cs`: managed allocation counted with `GC.GetAllocatedBytesForCurrentThread`, and the class is in the new `SerialMemoryCollection` (`DisableParallelization`), because the private-memory check is process-wide. It failed 1 of 1 in the baseline run of this session (406 MB counted from other threads) and passed in all runs since.

## Proof commands and results

```powershell
# build and test with K1a's folder (live tree)
dotnet build src\Tsuki.slnx -warnaserror --artifacts-path artifacts\K1a   # 0 warnings, 0 errors
dotnet test tests\Tsuki.Tests.Data      --artifacts-path artifacts\K1a --no-build           # 599 / 602; the 3 failures are Stage/StageEngineTests (other package)
dotnet test tests\Tsuki.Tests.Synthetic --artifacts-path artifacts\K1a --no-build           # 357 / 357
dotnet test tests\Tsuki.Tests.Data --artifacts-path artifacts\K1a --no-build --filter "FullyQualifiedName~Tsuki.Tests.Data.Source|FullyQualifiedName~Tsuki.Tests.Data.Census"
# the census alone (Debug 2.0 s, Release 2.2 s) and its reference lines
dotnet artifacts\K1a\bin\Tsuki.Cli\debug\Tsuki.Cli.dll census --refs scratch\K1a\cli_refs.tsv                 # exit 0: "every reference resolves except the allow-list"
.venv\Scripts\python.exe -I tests\Tsuki.Tests.Data\Census\census_oracle.py refs     # 166,437 lines, 13 s
.venv\Scripts\python.exe -I tests\Tsuki.Tests.Data\Census\census_oracle.py glyphs   # 4 JSON lines
```

Private snapshot (`scratch/K1a/make_snapshot.sh`, `sync_wt.sh`, `mutate.py`, `run_mutations*.sh`, logs `mutations*.log`): for long stretches the live tree did not build because other packages' files were half edited (`Tsuki.Engine/Stage/*`, then `Tsuki.Data/Eboot/Tables/*`). K1a therefore also built and tested `git archive HEAD` plus its own files in `scratch/K1a/wt` (artifacts `artifacts/K1a-wt`). The 24 mutations reported above were applied and run there, so no other package saw a mutated file. Before the snapshot existed, a first round of 13 mutations had run in the live tree (about 30 s each; the harness restored each file, and `git status` shows `Text`, `Scripts` and `Fonts` unmodified against HEAD); its results were replaced by the snapshot run.

## Open issues

1. **`IEbootTables` is still empty and `GameEbootTables` has no readers for K1b's tables.** The golden slice holds `config`, `textbox_tables`, `compositing` and `build` tables that nothing in `GameEbootTables` reads yet. K1b's classes in `Eboot/Tables` should hang on `GameEbootTables` as lazy properties built from `Reader` when K1b lands; contract members are the lead's to add.
2. **The live tree is not always buildable by one package alone** (see above). `Tsuki.Tests.Data/Stage/StageEngineTests` (3 cases, 27-43 s each) fails in the live tree at the time of writing; it is the Stage package's.
3. The census follows `tsuki.script` and so tracks layer names in statement order without control flow; that names every picture a script can show but is not the engine's run-time layer state. Only samples (48 + 48) are decoded to pixels and PCM; the complete decodes stay with `decode-all --pictures` (A1) and the ATRAC9 nightly (E1, 17,925 / 17,925). Movie files are checked to exist and be non-empty, not decoded (K1c / S2).
4. `_SEFI` is treated like `_SEPL` (same handler); no retail `_SEFI` carries a name, so the counts equal the oracle's `_SEPL` count.
5. `GameFolderSource.Movie` throws for a missing file; `GoldenSource.Movie` returns a path without looking. `UiSheet` does not clamp the child (`UiTextureTables.ChildIndex` does, for registration records).
6. Shared files touched, all additive: `Tsuki.Data.csproj` (`InternalsVisibleTo Tsuki.Tests.Data`, for the census helpers), `Tsuki.Cli/CliApp.cs` (one line to register `census`), `Tsuki.Tests.Data.csproj` (the Platform reference).
7. The census test needs the Python oracle (13 s for `refs`, 11 s for `glyphs`, each once per test process), so the Census tests take about 25 s; the census itself takes 2 s.

## Files

New, `src/Tsuki.Data`: `GameFolderSource.cs`, `GameEbootTables.cs`, `UiSheetNames.cs`, `Audio/SeNames.cs`, `Census/{CensusTypes,CensusAllowList,GlyphCensus,LayerNameTracker,ReferenceCensus,StatementView}.cs`; `src/Tsuki.Cli/Commands/CensusCommand.cs`.
New, `tests/Tsuki.Tests.Data`: `Source/{SharedSource,GameFolderSourceTests,GameFolderSourceAgreesWithGoldenTests,SourcePartsTests}.cs`, `Census/{CensusTests,CensusRulesTests,CensusCommandTests}.cs`, `Census/census_oracle.py`, `GiantArchiveTests.cs`.
Changed: `src/Tsuki.Data/Tsuki.Data.csproj`, `src/Tsuki.Cli/CliApp.cs`, `tests/Tsuki.Tests.Data/Tsuki.Tests.Data.csproj`, `tests/Tsuki.Tests.Data/ArchiveTableTests.cs`.
