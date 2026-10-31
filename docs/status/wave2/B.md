# B: Text_Layout port (PACE 7.2 #3), status

Written 2026-10-25 by the B agent. Package: correct the 1-ulp constants of `tools/tsuki/textbox.py`, then port `textbox.py` to C#
(`Tsuki.Engine.Text`): the 601 golden cases first, then all 175,876 script lines (4 x 43,969). Every number below comes from a run on
this machine (Windows 11; .NET 10.0.402; Python 3.11.9, numpy 2.4.6). Numbers, commands and identifiers only.

## Verdict

**Done: 601 / 601 golden engine layouts equal, and 175,876 / 175,876 script lines equal (by SHA-1 of the canonical block), twice** (the
ordinary message window and a speech bubble). Also equal: the 2,772 unicorn-verified synthetic edge cases (`verify_synth.py`), 6,000
seeded random messages with random call arguments, and the page-1 slice messages of 00_02 in ja and en (blocks, records, page origins
and drawn quads against `samples/golden/slice/layouts`).

| check | cases | result |
|---|---:|---|
| frozen engine layouts, `samples/textbox/engine_layout_golden.json` (unicorn run of VA 0xC6C20), every record field as float32 bits | 601 | **601 equal** |
| every script line, ordinary message window (flags 0, window 0), SHA-1 of the canonical block | 175,876 | **175,876 equal** |
| every script line, speech bubble (flags 1 << 20, window 5) | 175,876 | **175,876 equal** |
| unicorn-verified synthetic edge cases (77 texts x 9 flag/window configurations x 4 languages) | 2,772 | **2,772 equal** |
| seeded random messages (markup, ruby, tags, dashes, kinsoku, English words; random width, scale, spacing, leading, colour, flags, window, block, `@x` position) | 6,000 | **6,000 equal** |
| slice page-1 messages, ja and en: block, 15 record fields, page origins, outline/fill quads | 16 messages | **equal** |
| `@h`/`@w` scale table, `@f` widths, `@c` colours against the eboot bytes of `slice/eboot/textbox_tables.json` | 10 + 4 + 6 | **equal** |
| no-game-data unit cases (oracle hashes, pass 1, window tables, page stacking, reveal window) | 43 | **43 pass** |

The block compared is the whole `Text_Layout` result: element count, line count, box width, line glyph counts, line widths and heights,
the end counter, `@y` field, every glyph record (index, flags, colour, ext, line, texture, rectangle, counter, dx, dy, sx, sy as
float32 bit patterns), the 1,200-entry break-class table and the word-length table. The serialisation is `oracle.textbox.canonical`
(Python) and `LayoutCanonical.Serialize` (C#), byte for byte.

## Step 1: the 1-ulp constants (spec first)

`tools/tsuki/textbox.py` now holds the eboot float32 values (followups.md):

- `SCALE_TABLE[8]`, `[9]` (VA 0x2D3730): 0.2864583134651184 (0x3E92AAAA) and 0.3151041865348816 (0x3EA15556); they were 0x3E92AAAB and
  0x3EA15555.
- `_WSIZE` widths `[2]`, `[4]`, `[21]` (VA 0x200BB0): 684.800048828125 (0x442B3334), 684.800048828125 and 678.3999633789062
  (0x44299999); they were 0x442B3333, 0x442B3333 and 0x4429999A.

Checked against the eboot bytes in `samples/golden/slice/eboot/textbox_tables.json`: all 10 scales and all 23 window sizes equal after the
change. Corpus effect: none. No `script_text` line contains `@h` or `@w` at all (0 of 4 x 43,969; messages written inline in script
files were not scanned), and the window sizes only place the speech-bubble windows 35, 37 and 54 in the HUD, which the layout does not
read. So the 601 goldens and the 175,876 lines were equal before the change as well (textbox.md 10.3) and stay equal; it matters for the
`@h`/`@w` codes and those three windows. Python tests: `test_textbox.py` + `test_misc_adv_textbox_layout.py` 13 passed before the change;
with `test_text.py` and `test_font.py` 59 passed (17.6 s) after it.

## What was built

| file | content |
|---|---|
| `src/Tsuki.Engine/Text/TextClassifier.cs` | pass 1 (VA 0xC6D80): break classes, word lengths, 1,200-element budget, `IsWide` |
| `src/Tsuki.Engine/Text/TextLayout.cs` | pass 2 (VA 0xC76C0): all markup codes, ruby, dashes, kinsoku, English word wrap, `Wrap`, `MainBoxWidth`; float32 in the engine's operation order |
| `src/Tsuki.Engine/Text/LanguageGlyphs.cs`, `FontTables.cs` | glyph lookup (VA 0x10810, 0x11150, 0x11750, 0x11B80, 0x11ED0): lookup tables, 10-byte record tables read unchecked like the engine, wide grid, tags, italic advance, dash pieces |
| `src/Tsuki.Engine/Text/TextBlock.cs` | `TextBlock`, `GlyphRecord`, `LayoutParams` |
| `src/Tsuki.Engine/Text/MessageWindow.cs` | `MainBoxParams`, `PagePositions`, `LastLineOffset`, window table, `WindowFor`, `BubbleTextOrigin` |
| `src/Tsuki.Engine/Text/BlockDraw.cs` | `LineOffsets`, `BlockQuads` (reveal window, redaction bars, `@b` mirror, shadow/outline/fill), `Rgb565Bytes` |
| `tools/oracle/textbox.py` | area `textbox` (below), `canonical`, `dump`, `synth-source` |
| `tests/Tsuki.Tests.Data/Text/Layout/` | `LayoutDifferentialTests` (6 tests), `SliceLayoutTests` (1), loaders for the golden font tables and the script lines |
| `tests/Tsuki.Tests.Synthetic/Text/` | `TextLayoutTests` (43 tests), `SyntheticLayoutCases.cs` (generated), `SyntheticFont`, `CanonicalBlock` |

The engine takes its font data through `FontTables` (lookup arrays, record images, wide bases), not from files: `Tsuki.Engine` still
references nothing. Until package A2/A3 reads eboot.bin, the tests build `FontTables` from `samples/golden/slice/eboot/font_tables.json`
plus `samples/golden/textbox/fonts/font_overrun.json`.

Not ported (Python only, not part of the layout): `render_quads` / `render_page` (PIL), the `Font` page rasteriser, the CLI.

## The oracle area `textbox` (`tools/oracle/textbox.py`)

`python -m oracle.textbox` exports `samples/golden/textbox/` (local only) and verifies it. Parts:

- `lines`: 8 files `layout_{main,bubble}_{ja,en,zc,zt}.sha1`, 43,969 x 20 bytes each (SHA-1 of the canonical block of each line, line order).
- `fonts`: `font_overrun.json`, the glyph records the layouts read past the end of their table.
- `synthetic`: `cases.json`, 2,772 cases (text, call arguments, SHA-1).
- `fuzz`: `cases.json`, 6,000 cases from seed 20261025.

Manifest checks (all pass): 43,969 lines per language; 351,752 layouts; the Python layouts reproduce the 601 engine goldens
(0 mismatches); synthetic case count; fuzz case count. Export run 3: 351,752 layouts with 14 processes in 28.7 s, whole area 11 files,
16,961,887 bytes in 56.3 s (the two earlier exports took 33.1 s and 49.1 s for the layouts); `verify`: 0 problems.

Corpus measured by the export (ordinary window): elements ja 1,115,624, en 2,629,444, zc 852,753, zt 883,862; glyph records 1,105,057 /
2,626,556 / 850,321 / 880,444 (ruby readings 7,432 / 0 / 831 / 1,511); 9 empty lines (3 each in en, zc, zt; the "9 empty lines" of
textbox.md 10.3); 0 out-of-range record reads in the real lines.

Useful commands: `python -m oracle.textbox dump ja 3 --config bubble` prints the readable block and its SHA-1 for a line (compare with
`LayoutCanonical.Describe`); `python -m oracle.textbox synth-source --out tests/Tsuki.Tests.Synthetic/Text/SyntheticLayoutCases.cs`
regenerates the no-game-data cases and the glyph entries the oracle read for them.

## Findings

- **The real lines do not exercise every path.** Changing the empty-line height constant 20 to 21 left all 351,752 real-line comparisons
  green (no script line contains an empty line) and was caught by 84 of the 2,772 synthetic cases and 355 of the 6,000 random ones.
  Changing the Japanese kinsoku margin -96 to -95 failed 8 main-window and 1 bubble lines. So the synthetic and random sets are part of
  the proof, not decoration. Both mutations were reverted; the final sources are the ones tested.
- **Glyph-table overrun is real but unreachable in the script text.** `ja_s0` has 104 records and the `narrow` lookup 109 entries; U+00B0,
  U+00E2, U+00E4, U+00EA, U+00EB give indices 104..108, and the engine reads the next 5 records of the executable (the first of `ja_s2`).
  Only the synthetic case `äâ°êë` reaches it. `GlyphRecordTable` takes the image bytes from its first record to the end of the segment
  so a real eboot reader reproduces this; a read past the supplied bytes throws.
- `U+3000` (ideographic space) is class 1 in pass 1, not 0x0D; U+3001 is 0x0D (`k == 0` of the 0xE38081 range). The comment in
  `tsuki.textbox._wide_class` names U+3000; the code is right.
- A quad's `Record` in C# is the index into `TextBlock.Records`; the oracle JSON names the element index (`Records[q.Record].Index`).

## Commands (all run in this session)

```
$env:PYTHONPATH="tools"
.venv\Scripts\python.exe -m pytest tools\tests\test_textbox.py tools\tests\test_misc_adv_textbox_layout.py tools\tests\test_text.py tools\tests\test_font.py -q -p no:cacheprovider   # 59 passed in 17.61 s
.venv\Scripts\python.exe -m pytest tools\tests\test_oracle.py -q -p no:cacheprovider                                                  # 15 passed in 21.41 s
.venv\Scripts\python.exe -m oracle.run slice --verify                                                                                # 0 problems (2.0 s)
.venv\Scripts\python.exe -m oracle.textbox export                                                                                    # 11 files, verify 0 problems
.venv\Scripts\python.exe -m oracle.textbox synth-source --out tests\Tsuki.Tests.Synthetic\Text\SyntheticLayoutCases.cs

dotnet build src\Tsuki.Engine\Tsuki.Engine.csproj -warnaserror --artifacts-path artifacts\B                              # Debug and -c Release: 0 warnings, 0 errors
dotnet test tests\Tsuki.Tests.Synthetic --artifacts-path artifacts\B                                                  # 232 passed (43 of them Text), includes ProjectRulesTests
dotnet test tests\Tsuki.Tests.Data --artifacts-path artifacts\B --filter "FullyQualifiedName~Text.Layout"             # 7 passed, 0 failed, 0 skipped
```

Durations of the 7 data tests (xUnit report, Debug build, parallel over 16 threads, warm cache; not timing measurements under
decision 4): constants 32 ms; 601 goldens 145 ms; each of the two all-lines runs about 1 s (175,876 layouts and SHA-1s); synthetic
109 ms; random 457 ms; slice 196 ms; class total 2 s. The script text (4 x 43,969 lines) is read once per test run from `script_text.mrg`.

## Open items

- `tools/oracle/run.py` is not in this package's folders: register `"textbox": "oracle.textbox"` in `AREAS` so `python -m oracle.run textbox`
  works. The module already has `AREA`, `PARTS`, `DESCRIPTION` and `run(...)`, and `python -m oracle.textbox` works without it.
- `samples/golden/slice/eboot/textbox_tables.json` was written before the constants fix: it still records `matches_tsuki_textbox: false` and
  findings for `scales` and `window_sizes`. Re-running `oracle.run slice` clears them (it also re-hashes about 1.9 GB of movies, so it was
  not run here). Nothing depends on the flag.
- Remove the followups.md line "`tsuki.textbox` constants are 1 ulp off the eboot float32 tables" (done by this package).
- Package A2/A3 should read the font tables from eboot.bin into `FontTables` (keeping the image tail for the overrun) and expose them
  through `IEbootTables`; the tests then drop `font_tables.json` / `font_overrun.json`.
- `MessageWindow.WindowFor` and `BubbleTextOrigin` mirror the oracle's double arithmetic on float32 table values; M2 may want the engine's
  float32 evaluation order once a window is compared pixel for pixel.
- A `@x` wrap that pushes the line index past 31 raises `IndexOutOfRangeException` in C# (IndexError in Python); the engine would write
  outside the block. No corpus line or random case does this (0 random cases were skipped for Python errors).
- `$NNNNNN` expansion and the 0x2FC-byte cap belong to the text package (A3); `TextLayout.Layout` takes the expanded bytes.
