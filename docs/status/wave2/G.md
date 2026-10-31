# G: UI calls (PACE 7.2 #7)

Written by the G agent on 2026-10-25, updated the same day after review. Every number below comes
from a run in this session. Python only: no dotnet build, no GPU, no window, nothing committed (the
lead commits after review).

## Verdict

**Done.** `tools/eboot/uicalls.py` is a tested module. Its extracted draw calls of the title draw
(VA 0xB1F30) and the choice draw (VA 0xA6E10) equal the hand-built oracle draw lists of
`tsuki.misc_boot_title_choice_flow` on every field listed in "What is compared", and it ran on the
system-menu entry 0x6758E (result below). `tools/tests/test_uicalls.py`: **29 passed** (10 synthetic,
19 real-data); with the game folder missing the file collects and gives 10 passed, 19 skipped.

## Repair after review

| # | reviewer finding | outcome |
|---|---|---|
| 1 | major: the test file did not collect without the game folder (`sf` imported conditionally, used at module level) | **Fixed.** The oracle module imports without game data (numpy only), so `sf` is imported unconditionally. Proof: the reviewer's simulation (`eboot.eboot_path = lambda: None`, then pytest) gave `NameError: name 'sf' is not defined`, 0 tests, on a copy of the file with the old import; on the fixed file it gives `10 passed, 19 skipped`. `test_collects_and_skips_cleanly_without_game_data` pins this: it spawns that simulation as a subprocess (the nested run skips the test itself, so no recursion). |
| 2 | minor: hit-widget canvas not compared (extracted 640 x 344.7 vs oracle 640 x 448) | **Reviewer right, and the cause is a model error, now fixed.** `WidgetDraw` (0xCFC90) is a *setter*: it stores x, y, alpha at +0x1C, +0x20, +0x2C of a 0x78-byte widget record (array at 0x178F070) and draws nothing. The widgets of a group are drawn by `WidgetPass` 0xD0BE0(group), with the canvas in force at that call. The choice function calls the setter on its 640 x 344.7 text canvas; the pass runs on 640 x 448 (callers 0xA7F81 in 0xA7CD0 and 0xA8501 in 0xA8250, both group 13). New in the module: recipe rows `WidgetPass`, `WidgetTable`, `WidgetRegister`; `widget_passes()`, `widget_table()`; `Callee.clobbers` (the pass overwrites scale, colour and rotation, so those are unknown after it); `DrawSite` docstring and `site_row` say a widget site's draw state is the setter's. The tests now compare the oracle's widget canvas with the canvas of the `WidgetPass` calls of the extracted widget's group (title group 10, choice group 13), and `test_widget_setter_canvas_is_not_the_widget_canvas` pins the difference. |
| 3 | minor: the negative control covered 9 fields at one site; some fields not compared; title widget alpha membership weak | **Fixed where code allows.** (a) The negative control is now exhaustive: for every oracle draw of every fixed view it perturbs each compared field (2 to 3 wrong values) and requires that the field's own check reports it: title 981 perturbations, choice 1,171, **all noticed, all by the first wrong value**. (b) New compared fields: colour (r, g, b), effective rotation of every texture draw (the `SetRot` state only counts with draw flag 0x20), canvas of the choice text draws, alpha of the hit widgets, the pass canvas of every widget. (c) Alpha: the fixed views hit only 8 of 81 title alpha values strictly between 0 and 1, so membership in {0, 1} was weak. `test_random_views_equal_the_oracle` (seed 20261025, 40 views per screen) adds generic inputs: 143 title and 444 choice alpha comparisons strictly between 0 and 1. A wider run (seeds 1 to 5, 300 views each, this session, not in the suite): title 126,287 and choice 205,444 field comparisons, 0 mismatches. (d) The line index of a choice text or hit draw and the glow-copy index now come from the oracle's note/source, not from the slot, otherwise a wrong slot was self-consistent. Still not compared: see below. |
| 4 | minor: 0x6758E is not an instruction boundary and not a call | **Reviewer right.** 0x6758E is the 32-bit displacement of the 7-byte `lea rdi, [rip + 0x73f25e]` at 0x6758B (target 0x7A67F0), followed by `mov esi, 1` (0x67592) and `call 0xC2AF0` (0x67597). 0x6CA83 and 0x6E683 are the displacements of the `lea` at 0x6CA80 and 0x6E680, the first instruction of the 16-byte helpers (`lea; xor esi, esi; jmp 0xC2AF0`). Fixed in the docstrings and the CLI report; new `entry_instruction()`; `EntryRun.insn` and `EntryRun.load`; `test_sysmenu_entries_are_inside_the_lea_before_the_call`. A C# decoder (Iced) must start at the function (`function_of`) and use the entry only to say which table load is meant. |
| 5 | minor: small numeric and doc discrepancies | **Fixed / re-measured**: candidate counts and timings below. The engine.md conflict is real and now has a test, see "ProcessRegister fifth argument". |

## What was built

| file | what |
|---|---|
| `tools/eboot/uicalls.py` (about 1,700 lines) | the call decoder: recipe tables (`CALLEES`, `SCREENS`, `RETURNS`, `MATH_FUNCS`), the symbolic value model (`Sym`, `Mem`, `Op`, `Choice`, `Carried`, `Frame`), the decoder (`Decoder.decode` / `decode_function`), draw sites (`draw_sites`, `DrawSite`), widgets (`widget_passes`, `widget_table`), the evaluator (`Env`, `ev_all`, `ev`, `try_all`, `read_rect`), the entry runner (`run_entry`, `entry_instruction`, `EntryRun`, `Process`), scans (`scan_functions`, `find_draws`) and a CLI |
| `tools/tests/test_uicalls.py` (about 920 lines) | 10 synthetic tests (a tiny assembler, no game data) and 19 real-data tests |

How it differs from the 113-line scratch `calls.py` (that file is untouched):

- It follows control flow. Forward branches carry state and join it into a `Choice`; loops are
  strongly connected components of the instruction graph (compilers rotate them, so a backward
  branch is not enough) and become `Carried(header, loc)` symbols that evaluate per iteration. The
  scratch decoder printed the first path's constant for a site reached by two paths.
- Values are expressions, not constants or `None`: table reads through a symbolic index
  (`f32[0x218B20 + 8 * lang]`), stack-slot spills (`[rbp-0x54]`), the xmm high lane
  (`vmovsd` / `vmovshdup` pairs), `vcvtsi2ss`, the `cvtss2sd` / `mulsd` / `cvtsd2ss` chain around
  `sinf`, `cmov` and `vblendvps` selects, `setcc` bools.
- It tracks the draw state (canvas, scale, colour, blend, rotation, clip) as part of the machine
  state, so every draw site carries the canvas and scale it is drawn with (for widgets: see the
  setter note above).
- The recipe is data (callee VA, name, argument registers, state clobbers); `Env` evaluates any
  argument for given runtime values.

## Commands that prove it (all run here)

Working directory: the repository root; Python `.venv/Scripts/python.exe`; `PYTHONPATH=tools`.

| command | result |
|---|---|
| `python -m pytest tools/tests/test_uicalls.py -q` | **29 passed** in about 2 s (game data present at the default path) |
| same, from `tools` and with no `PYTHONPATH` | 29 passed (the test file adds `tools` to `sys.path`) |
| the no-game simulation: `eboot.eboot_path = lambda: None` then `pytest.main([tools/tests/test_uicalls.py])` | 10 passed, 19 skipped (the old conditional import: collection error) |
| `python -m pytest tools/tests/test_misc_boot_title_choice_flow.py tools/tests/test_eboot.py tools/tests/test_uicalls.py -q` | 68 passed, 1 skipped in 25 s (the oracle and eboot suites are unchanged; the skip is the existing one) |
| `python -m pyflakes tools/eboot/uicalls.py tools/tests/test_uicalls.py` | clean |
| `python -m mypy tools/eboot/uicalls.py --ignore-missing-imports` | clean |
| `python -m eboot.uicalls screen sysmenu` | the system-menu report below (0.55 s wall) |
| `python -m eboot.uicalls entry 0x6CA83` / `entry 0x6E683` | one `UiTableLoad` each (0x7A6B20, 0x7A7680, force 0), no process |
| `python -m eboot.uicalls draws 0xb1f30` / `draws 0xa6e10` | 11 / 16 draw sites |
| `python -m eboot.uicalls find 0x1000 0xB5000 0x141 0x142 0x143 0x144 0x145 0x146 0x147` | 7 sites (0x6CC63, 0x6CC7C, 0x6CC99, 0x6CCB6, 0x6CD18, 0x6CFFE, 0x6D15D), all in 0x6CB80; 4.5 s wall for the whole game-code range |

Timings depend on machine load (the first author run reported 107 ms / 27 ms / 241 ms / 8.4 s, the
reviewer 194 ms / 12.9 s; this session, quieter machine): decode of 0xB1F30 12 to 14 ms and of
0xA6E10 15 to 18 ms (7 runs, warm; the first `function_of` call parses the .eh_frame table, 144 ms),
`run_entry` 126 to 128 ms warm. Treat them as order of magnitude only.

## Title and choice: the extracted draw calls equal the oracle lists

Method: decode the function once; for each oracle `Draw` (it carries the call-site VA in `va`) find
the extracted site with that VA and compare, with the runtime globals bound to the oracle view
(`TitleView` / `ChoiceView` fields). A site matches when the oracle value is one of the values the
extracted expression can take under the bound state. Every site of the function must be explained by
some view.

| | title (0xB1F30) | choice (0xA6E10) |
|---|---:|---:|
| calls decoded / draw sites / loops | 51 / 11 / 1 | 53 / 16 / 1 |
| oracle views compared | 8 (both languages, G[10] 0/1, CONTINUE on/off, cursor 0/1/2/3/5, logo fading, movie frame) | 7 (both languages, 2 to 9 lines, cursor -1/0/1/2/8, previous cursor, phase 2 and 3, shadow on) |
| oracle draws compared | 81 | 146 |
| field comparisons | 735, **0 mismatches** | 976, **0 mismatches** |
| alternatives offered per compared field | 1: 612, 2: 67, 3: 54, 4: 2 | 1: 539, 2: 300, 4: 40, 5: 3, 6: 94 |
| negative control (perturbed fields, all noticed) | 981 | 1,171 |
| seeded random views in the suite (40 each), comparisons, mismatches | 3,314, 0 | 5,378, 0 |

Total fixed-view oracle draws 227 (81 + 146), field comparisons 1,711 (was 1,380 before the repair).

### What is compared

| draw kind | fields |
|---|---|
| title texture draws (10 sites, 55 oracle draws) | slot, flags, blend, canvas, scale, x, y, alpha, colour r g b, effective rotation, rectangle (where the oracle has one) |
| title widgets (26 oracle draws) | widget id, x, y, alpha, scale (the `WidgetScale` call), canvas of the group's `WidgetPass` |
| choice texture draws (25) | slot, flags, blend, canvas, scale, x, y, alpha, colour, effective rotation (the ring is the only rotated draw) |
| choice text draws (95) | block id, flags, x, y, alpha, canvas |
| choice hit widgets (26) | widget id, x, y (y x 448 / 344.7), alpha, canvas of the group's `WidgetPass` |

### What is NOT compared (and why)

- Title widget frame: texture slot, flags, blend, rectangle. They are in the widget's frame list
  (pointer at +0x20 of the definition record, read by `WidgetPass` at record +0x48); `uicalls` reads
  the table ids and groups (`widget_table`) but not the frames yet (open item 1).
- Choice text colour word (0xFFFF / 0x77FF / 0xC7FF): the first stack argument of `TextLayout`
  (`push rax` before the call at 0xA7270); the decoder does not track pushed arguments.
- Choice text blend: the function never calls `SetBlend` (pinned by `test_choice_text_blend_is_inherited`),
  so a text draw's blend is the caller's state, not decoded here; the oracle's 0 is the default.
- Choice text rotation: the 0x20 bit of the text flags 0x1020 is a text flag, not texture rotation.
- Texture sizes (`Draw.size`) are image data, not code.
- Title particles (0xB2F50, 0xB3310), the covers of 0xB02B0 and the boot notices decode (`draws`) but
  have no oracle draw list.

Facts the comparison confirms from the code itself (not from the oracle's formulas):

- Title: logo position and glow position by language (VA 0x218B20 / 0x218B50), the three logo-band
  rectangles (VA 0x873910 + 24 per language), the highlight bar rectangle in a stack slot
  (`movabs 0x20016800180018`), the two arms of the background position ((0,0) and the intro
  glow layer (-187.5, -55.4074)), the widget loop (id `0xE4 + k`, row from `u32[0x1762180 + 4k]`,
  position from the table at 0x218DF0), every alpha formula (staggered menu fade, logo la / la^2 /
  la^3, press pulse). `BG_REDRAW_Y` = 680 / 519 is read back from the redraw in 0xB29D0.
- Choice: the line loop (header 0xA70BC, index +1, y += `SEL_PITCH` = 26.02), the 8 glow-copy offsets
  (-0.9/0.9 and -0.7/0.7), the block ids 0x14 + k and 0x64 / 0x6C + j, the hit widgets 0xD5 + k with
  y x 448 / 344.7, the flag word 0x1020 | shadow << 4, and the phase-3 ring: its scale and its turn
  come out of the `sinf` chain in the code and equal `choice_ring_transform` at four flash values.
- Widgets: the groups come from the code. Definition tables (`widget_table`, 0x30-byte records, id,
  group, flags): title 0x873780 (0xE4..0xEA, group 10), menu 0x7A6C00 (0xC1..0xC5, group 1), mode
  panel 0x7A76D0 (0xCB..0xD2, group 14), prompt 0x8731F0 (0xD5, 0xD6, group 13). The nine choice hit
  widgets 0xD5..0xDD are not in a table: `WidgetRegister` 0xCF9E0 registers them in a loop at 0xA5F5A
  (group 13, position (-5000, -5000), alpha 0). The group-13 passes are at 0xA7F81 and 0xA8501, the
  title pass (group 10) at 0xB2929 and the menu pass (group 1) at 0x6CEDA; all draw on canvas 640 x 448.
  Scanning all game code (0x1000..0xB5000) finds 13 `WidgetPass` calls; only five of them (groups 3, 1,
  13, 13, 10) set a canvas in their own function (all 640 x 448); the others, among them the mode-panel
  pass (0x6EA0C, group 14), inherit it from the caller (canvas unknown to the per-function decoder).

Two notes for the oracle (nothing in `tools/tsuki` was edited):

1. `title_draw_list` gives logo band 1 the call-site `va` 0xB2316 in every state. While the logo fades
   in (`logo_fading`), the engine draws band 1 at 0xB22A5 (alpha la^2); 0xB2316 is the steady-state
   branch. The test maps the fading case to 0xB22A5. The content (slot, position, rect, alpha) is
   right either way.
2. The earlier scratch decoder, run on the same two ranges: 55 of 55 watched call sites also found
   by the new module with the same target; 158 of 165 constant arguments agree. The 7 that differ are
   all the old decoder's errors: the first path's value at a join (`TexDraw 0xB20BA` position (0, 0)
   instead of {(0, 0), (-187.5, -55.4)}; the `WidgetDraw 0xB27B9` x, y, alpha) or a loop's first
   iteration (`rdi = 0xE4` and `0xD5` instead of `0xE4 + k` and `0xD5 + k`).

## System menu: the run on entry 0x6758E

`python -m eboot.uicalls screen sysmenu` (0.55 s wall including Python start; `run_entry` itself
about 130 ms). `run_entry(image, 0x6758E)`:

1. 0x6758E is **not an instruction boundary**: it is inside the 7-byte `lea rdi, [0x7A67F0]` at
   0x6758B (the displacement). The `UiTableLoad` call (0xC2AF0, force 1) is at 0x67597, in
   `Game_StartProcesses` 0x67530. The table is menu parts 8..17 (texture slots 328..337).
2. The same function then registers **process 0x10** (priority 0x53): per-frame function 0x6BF10, exit
   callback 0x6C560 (which frees the table with `UiTableFree`).
3. The initialisers it calls register the other two menu processes: 0x6CA90 loads table 0x7A6B20
   (menu parts 0..7, slots 320..327) and registers **process 0x11** (priority 0x54): menu body 0x6CB80,
   exit 0x6D1F0; 0x6E690 loads 0x7A7680 (mode panel, slots 382 and 383) and registers **process 0x12**
   (priority 0x55): mode panel 0x6E810, exit 0x6EA20. The roadmap's other two entries (0x6CA83,
   0x6E683) are likewise inside the `lea` that starts the 16-byte `UiTableLoad` helpers 0x6CA80 and
   0x6E680 (`lea; xor esi, esi; jmp 0xC2AF0`, force 0).
4. `run_entry` decodes the whole entry function, so `tables` and `processes` also list the loads and
   registrations before the entry; only the calls after the entry are followed into initialisers. The
   run lists the processes of the other screens that `Game_StartProcesses` and its initialisers register
   (config 0x15 -> 0x3F140, save/load 0x16 -> 0x70340, 0xC -> 0x67820, ...): 11 processes, 6 table
   loads, 32 functions decoded.

Draw sites extracted from the four menu functions (37 in all; 13 with constant x and y):

| function | sites | constant x, y | what it draws |
|---|---:|---:|---|
| 0x6BF10 (process 0x10) | 14 | 2 | 5 x `Sprite` id 0x10 flags 0x14 at the canvas centre (320, 224) and four copies at (320 +- a, 224 +- a), a = f32[0xE2377C] (menu fade), alpha a and a x 0.25 [H: blurred scene capture]; slot 0x149 (329) at (0, 0); slots 0x14A..0x151 (330..337), flags 0x14, x = 320 (283.25 for 0x14F, 276.5 for 0x151), y = (q n + 720) x 448 / 720 with n = -132, -107, -85, -55 for 330..333 and y = q n x 448 / 720 with n = 103.5, 78.5, 56.5, 26.5 for 334..337, q = {s (2 - s) \| 1}, s = the slide variable f32[0xE23784]; alpha s (2 - s) x {64, 96, 160, 224, 64, 96, 128, 160} / 255 |
| 0x6C640 (petals, called by 0x6BF10) | 1 | 0 | `TexDrawRect2` slot from the particle table (starts at 0x148 = 328), flags 0x34, blend 0x17, rect table 0x7A68F8, canvas 960 x 540; per-particle state at 0xE23790 + 0x24 k |
| 0x6CB80 (process 0x11, menu body) | 13 | 11 | canvas 640 x 448, scale (1/3, 448/1080); see the list below |
| 0x6E810 (process 0x12, mode panel) | 9 | 0 | slot 0x17E (382) at (0, (f32[0xE245F8] - 1) x 43.1407); widgets 0xCB..0xD2 at x = 25.333 + 73.667 i, y = (f32[0xE245F8] - 1) x 43.1407 + 6.637 |

Menu body 0x6CB80, constant positions (canvas units on 640 x 448, and the 1920 x 1080 pixels):

| call site | draw | canvas | pixels |
|---|---|---|---|
| 0x6CC63 | slot 321 (menu part 1) | (18.667, 1.244) | (56, 3) |
| 0x6CC7C | slot 324 | (0, 0) | (0, 0) |
| 0x6CC99 | slot 323 | (250.667, 0) | (752, 0) |
| 0x6CCB6 | slot 327 | (0, 29.867) | (0, 72) |
| 0x6CD18 | slot 322, rectangle `{0x7A6E70, 0x7A6FA0, 0x7A70D0} + 8 x item` | (2.667, 0) | (8, 0) |
| 0x6CD95 | text block 0x3C, flags 0x200, text `ui_text[0x18C + item]` (the help line) | (110, 19) | (330, 45.8) |
| 0x6CDC6 .. 0x6CEB6 | item widgets 0xC1..0xC5 (group 1, drawn by the `WidgetPass` at 0x6CEDA on 640 x 448) | x = 26.667, y = 43.556 + 31.111 i | x = 80, y = 105, 180, 255, 330, 405 |

and, not constant: the cursor plate (slot 0x146, flags 0x11, blend 0xB, rectangle in a stack slot) and
the cursor glow (slot 0x145, blend 0xB) at x, y read from the float tables at 0x20AD60 / 0x20AD80 and
0x20ADA0 / 0x20ADC0 by the selected item (`u32[0xE245AC]`), scale and alpha from the animation
variables 0xE245D4 / 0xE245D8 / 0xE245DC. The mode panel's eight widgets (group 14) sit at x = 76, 297,
518, 739, 960, 1181, 1402, 1623 px (pitch 221 px); a ninth widget (0xD3) is scaled in the initialiser
but not drawn.

`test_sysmenu_*` pins the process table (ids, priorities, functions, exit callbacks, registering
functions), the entry geometry (insn 0x6758B, call 0x67597, helpers), that every menu slot (321..327,
329..337, 382) is drawn, the constants above, and the 37 / 13 counts.

### ProcessRegister fifth argument (conflict with engine.md 3.1)

`docs/formats/engine.md` 3.1 writes `Process_Register(id, ?, priority, update, draw)`. The code says the
fifth argument (r8) is an **exit callback**, not a draw function: `ProcessRegister` 0xECE30 stores rcx
at +0x10 and r8 at +0x18 of the 0x48-byte entry (0xECEC4, 0xECEC9); re-registering an active entry
first calls the old +0x18 value (0xECE60..0xECE72); the unregister function 0xECEE0 calls +0x18
(0xECF03..0xECF0D), so does the shutdown loop 0xED160 over all 0x10 entries (0xED193..0xED19D); and the
sysmenu's +0x18 function 0x6C560 calls `UiTableFree(0x7A67F0)`, which cannot run every frame.
`test_process_register_fifth_argument_is_the_exit_callback` pins the instructions. engine.md is outside
this package's folders; its parameter name should change from `draw` to `exit`.

## Not done / open items

1. **Widget frames.** `WidgetDraw` gives the widget id and position only; the frame (texture slot,
   flags, rectangle) is the frame list at +0x20 of the definition record (registered through 0xCF900
   / 0xCF9E0; for the title 0x30-byte frame records at 0x873C00). `widget_table` reads id, group and
   flags only. A frame reader is the next recipe step (and gives the widget text labels); until then the
   menu item buttons, the mode-panel buttons and the title widget slot / flags / blend / rectangle are
   position-only and are not compared with the oracle.
2. **Inherited draw state.** A function that does not set the canvas itself (the mode panel 0x6E810,
   its `WidgetPass` 0x6EA0C) shows `canvas = None`; the decoder is per function. A caller-to-callee
   state hand-off (or listing the canvas the process driver sets) would close it.
3. **Not yet compared with an oracle:** the system menu has no oracle draw list, so its test pins
   extracted constants and counts, not an equality. Gathering it needs the PS4 captures (M01 in
   `ps4_captures.md`). Also not compared: the title particles, the boot notices, the choice text colour
   word (stack argument, see above).
4. **Not extracted by design:** runtime state (fades, the slide and scroll variables, particle tables,
   which item is selected) stays symbolic; stores to globals are not tracked, so a test binds them
   (`Env.mems`). Jump tables and indirect calls end a path; nested loops are one component. Pushed
   (stack) call arguments are not tracked.
5. **The C# port** (`Tsuki.Data.UiLayoutExtractor`, M7) needs a golden dump to compare against. The
   CLI prints the sites; a machine-readable dump under `samples/` has to be produced through
   `tools/oracle` (outside this package's folders). The algorithm is deliberately small: one sweep
   with a state map, joins, an SCC pass and an evaluator, no capstone-specific logic beyond Intel
   operand strings (Iced gives structured operands). The port starts decoding at the function, not at
   the roadmap's entry VA.
6. **Docs to align (not mine to edit):** ARCHITECTURE 9.3, ROADMAP M7 and M8 task G, PACE 7.2 #7
   still say "becomes a tested module"; FORMATS.md can cite `tools/eboot/uicalls.py` next to
   `scratch/.../calls.py`; the roadmap's entry VAs should say "inside the `lea` before the
   `UiTableLoad` call at 0x67597 / 0x6CA89 / 0x6E689"; system_flow.md line 73 calls 0xCFC90 "widget
   draw" (it is the position/alpha setter; 0xD0BE0(group) draws); engine.md 3.1 fifth argument (above);
   the note on the logo band's call site belongs in system_flow 4.5.
7. **Approximations to know about:** `setcc` on an unknown register gives Choice(0, 1), valid only for
   code that reads the byte alone (the compiler's bool idiom); an unknown callee is assumed not to
   change the draw state (only `WidgetPass` is modelled as clobbering scale, colour and rotation); the
   sweep repeats at most 4 passes to a fixpoint. The alternatives of a joined value carry no path
   condition, so a check is "the oracle's value is one of them" (at most 6 alternatives in the fixed
   views); the random views and the perturbation test are what make that membership meaningful.
