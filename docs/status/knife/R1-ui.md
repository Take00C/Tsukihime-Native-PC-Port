# R1-ui: the review's UI findings, fixed

Package R1 (ui fixer, label `fix-ui`), run on 2026-10-30 on top of commit 38cc3af (K18) plus the uncommitted K19 display-mode work. Nothing is committed. Builds in
`artifacts/fix-ui` (Release, `-warnaserror`), scratch under `scratch/fix-ui/` (the probe, the renders, the logs). The game folder was read only; no app window was shown; the user's
`%APPDATA%\Tsuki`, `artifacts/play*` and `artifacts/dist` were not touched. No knife file was translated; `docs/THIRD_PARTY.md` is unchanged.

Files changed: `src/Tsuki.Ui/Screens/Dialogs.cs`, `Screens/FlowchartScreen.cs`, `Screens/SystemMenuScreen.cs`, `Services/UiPreview.cs`; tests
`tests/Tsuki.Tests.Synthetic/Ui/FlowchartScreenTests.cs`, `Ui/SaveLoadTests.cs`; new `tests/Tsuki.Tests.Data/Ui/TextFitRealDataTests.cs`; this file. Nothing outside the ui folders.

## 1. The findings

### Quick-load confirm dialog (English): the thumbnail over the second line of the message — fixed

`Dialogs.cs`: `ConfirmDialog.ThumbnailTop(Language)` = 212 units in English, 200 otherwise (knife `draw_dialog_thumb`, ui.rs 6611-6618: "212 for English (the longer message)");
`DrawThumbnail` hangs the frame from it instead of the fixed 200. The English message "Do you want to perform^a quick load?" (ui_text 231, dialog 9) takes two lines from 160 whose
last line box ends at 206.1 units (497 px); the frame now starts at 212 (511 px). Render `scratch/fix-ui/shots/ui_confirm-quickload_en.png` (sheet `sheet_after.png`, read): the
thumbnail sits under "a quick load?" with a gap; measured white text rows 393..494, frame top 511 (`measure_after.py`). The new `UiPreview` scene `confirm-quickload` (the mode
panel's Q.LOAD dialog with the quick save's thumbnail) is in `ui-shot --all` and in the real-data screen theories (4 languages x 2 checks).
Test: `TextFitRealDataTests.TheQuickLoadThumbnailHangsBelowItsMessageInEveryLanguage` (fails before: "En: the message ends at 206.1 units, the thumbnail frame starts at 200",
`scratch/fix-ui/test_before_data.log`).

### Dialog messages laid out with no fit overflow the 824 px window — fixed

`Dialogs.cs`: `ConfirmDialog.DialogStyle(UiText, message, lineSpacing)` measures the widest `^` line's ink at the base style (`Slot(0.6, 0.7)`, width 640, centred) and, when it is
wider than `DialogTextWidth` = 760 px (the window's dark inner area is 782 px, x 566..1347 on the rendered window, `measure_card.py`; 11 px margin each side), scales both glyph
scales by 760 / ink (the K12 `ConfigScreen.SampleScale` rule), keeping the top and the centring. `DialogText` (the confirm dialogs and `MessagePopup`) draws with it. The
layout is cached per string and style, so the measure is one pass over the line's quads per frame.
What the fit touches (`scratch/fix-ui/probe_dialogs.txt`, the probe's own copy of the rule): English 180 / 198 / 200 / 211 / 194 / 232 / 238 (0.86..0.99), the two-line 1498 (0.885)
and Sysmes 160 (0.90); Japanese 236 (0.94), 1444 (0.90), 160 (0.90) and the 25 ending prompts 1472..1496 (0.83..0.84); Simplified Chinese 1481..1496 (0.91..0.92); Traditional
Chinese 1472..1496 (0.93..0.97). Every other message is unchanged (`k 1.000`). Renders: `ui_gallery-confirm_ja.png` (the ending prompt inside the window: white text columns
625..1250 in the 566..1347 inner area), `_zc`, `_en` (sheet read).
Test: `TextFitRealDataTests.EveryDialogMessageFitsTheDialogWindowInEveryLanguage` over the 55 dialog-table messages, 236, 1497, 1498, 217, Sysmes 160 / 183 / 184 / 188 / 189 / 190 /
193 and every ending's `MessageUi`, at both line spacings: the widest line of the fitted style is at most 782 px, the line count is unchanged, a message that fits keeps the plain
style, a fitted one is scaled between 0.8 and 1 equally on both axes, and English and Japanese each have at least one fitted message (fails before: "Ja dialog 14 (ui_text 236):
widest line 808 px in the 782 px window").

### English flowchart scene card: synopses below the card, titles over the synopsis — fixed (with a correction to the finding's numbers)

Measured on the rendered card (`scratch/fix-ui/measure_card2.py`, `measure_card3.py` on the reviewer's `ui_flowchart-gallery_en_keyboard.png`): flow_infobase's **white area is
x 1379..1810 and ends at y 567**, not at the sprite's bottom 592 (the lower 25 px fade into the chart), and the text starts 11 px inside it. So the finding's proposal (0.85, box 150,
top 440) is not enough: at 0.85 / 150 / 440 the longest synopsis ends at 586 and the box lets the ink run to 1866, 56 px into the dark edge (`probe_flowchart1.txt`). The Traditional
Chinese synopses, at knife's own numbers, end at 566: the white edge is the design's text bottom.
`FlowchartScreen.cs`: `CardTextOf(Language)` gives the card text (x, title style, synopsis style, tops); English takes **0.75** of the glyph scale, keeps its 136-unit synopsis box
and starts the synopsis at **436** (the title's line ends by 425); the other languages keep knife's numbers. `TitleStyle` fits a title whose ink would run past `CardTextRight` =
1799 (English only: the 600 px title box on a 488 px card). `probe_flowchart2.txt` at the chosen numbers: 495 cards, 0 synopses below 567 (the longest, six lines, at 565), 0 titles
wrap, synopsis ink at most 1810, one title fitted ("...The Back Alleys of the Shopping District", 1853 -> 1799). Before (x1, 136, 452): 226 synopses below 567 (longest 652), 3 titles
wrap, 37 titles past 1832 (longest 1979). Renders: `ui_flowchart-gallery_en.png`, `ui_flowchart-game_en.png` (the card text inside the white area; sheet read), `_zt` unchanged.
Test: `TextFitRealDataTests.EveryFlowchartCardTextFitsTheCardInEveryLanguage` over every distinct (title, synopsis) of the full chart in four languages: the title is one line, its
ink ends by 1799 in English (by the sprite's edge elsewhere), its line ends above the synopsis top, the synopsis ends by 567; exactly the English titles are fitted (fails before:
"En node 485: the synopsis ends at 595, the card's white area at 567").

### In-game FlowChart tab only buzzes when the chart is unavailable — fixed

`SystemMenuScreen.OpenChart`: `Root.Push(new MessagePopup(Ctx.Message(1497), 480f))` in place of the buzzer (knife ui.rs 6058-6066, cFlowchartTask state 3 / sub_17530: popup
1496 = PS4 ui_text 1497, the string `GalleryScreen.OpenChart` already shows). The tab is not dimmed: knife's `draw_menu_base` dims Save and Load only (ui.rs 6282-6292).
Tests: `FlowchartScreenTests.WithoutThePlaythroughTheChartDoesNotOpenAndTheMenuSaysSo` (the popup with "text 1497" over the menu, no buzzer, the menu back on top after 520
frames) and `TheCurrentNodeOutsideTheEndingListIsRequired` (the popup for an ending node); `MessagePopup.Message` is new for them. Both fail before (`test_before_synth.log`).
Not run in the real program: `src/Tsuki.App` does not build at the moment (section 2).

### A confirm dialog stops fading out under the popup it opened — fixed

`ConfirmDialog.StepBackground()`: an answered dialog (`_acted`) keeps taking 0.07 off its `_t` and closes at 0, so it fades out under the Saving... / Loading... / Deleting...
popup and leaves the stack once invisible (knife's `DlgOut` timer, ui.rs 5907-5920, runs whatever screen is on top). `Step` shares the same `FadeOut`.
Test: `SaveLoadTests.AnAnsweredDialogKeepsFadingUnderThePopupItsAnswerOpened`: Yes on Delete, 16 frames later the popup is on top and the dialog still in the stack (its fade has
just begun), 16 frames later the popup is still up and the dialog is gone (fails before: the dialog stayed at t 0.93 under the popup).

### Menu opens, loads and the story-end transition stall 2-12 frames (perf, minor) — skipped

The UI-side part is `FlowchartScreen.OnOpened` building `FlowchartChart` on every open. Measured on the real data (`scratch/fix-ui/probe/`, `probe_flowchart0.txt`):
`FlowchartChart.Build` takes **1.1-1.3 ms warm** in every language and 25 ms only on its first call in a process (JIT and the first decode of the gallery tables), so a chart cache in
the UI would save about one millisecond per open and would need a progress version to invalidate on (the chart follows the playthrough's scene, edge and route bits, which
`IUiProgress` does not version). The 40 ms the verifier saw on the first open, the first title draw and the sheet-geometry decodes are first-use costs that belong to the App's
prewarm thread (`src/Tsuki.App/Play/GameApp.cs StartPrewarm`, outside this package's folders), and the verifier's correction puts the 194 ms story-end stall in the save flush
(finding 4 of the app area). Nothing changed here.

## 2. Build and tests

| check | result |
|---|---|
| `dotnet build src/Tsuki.slnx -c Release -warnaserror --artifacts-path artifacts/fix-ui` (`scratch/fix-ui/build3.log`) | `Tsuki.Ui` and `Tsuki.Cli` build with 0 warnings; **`Tsuki.App` fails on another fixer's in-flight edit** (`GameApp.cs(459,89) CS0407 'bool AppServices.Commit()' has the wrong return type`, `Program.cs(49,23) CS8602`), so the two test projects, which reference `Tsuki.App`, could not be built from the tree |
| scratch copies of the test projects without the App and Launcher folders (`scratch/fix-ui/tests/SynthUi`, `DataUi`: the same sources, packages and references, `AssemblyName` kept for `InternalsVisibleTo`; `build_synth3.log`, `build_data3.log`) | 0 warnings, 0 errors with `-warnaserror` |
| `dotnet test` SynthUi, `--filter FullyQualifiedName~Tsuki.Tests.Synthetic.Ui` (`test_synth_ui.log`) | **254 / 254** (253 of K19 + 1 new) |
| `dotnet test` SynthUi, everything but App and Launcher (`test_synth_final.log`) | 828 / 831: the 3 failures are `Game.SkipRestoreStateTests` (engine area; `src/Tsuki.Engine/Game/GameHost.*.cs` are modified in the tree by the engine fixer, not by this package) |
| `dotnet test` DataUi, `--filter FullyQualifiedName~Tsuki.Tests.Data.Ui`, `TSUKI_GAME_DIR` set (`test_data_final.log`) | **513 / 513** (the 3 new tests, the new scene x 4 languages x 2 checks, the rest as K19) |
| the same six tests with the pre-fix behaviour put back temporarily (`before_patch.py`; `test_before_synth.log`, `test_before_data.log`) | all 6 fail, with the messages quoted above; the fixed files were restored and rebuilt (`build_synth3.log`) |
| `ui-shot` confirm-quickload en / ja, flowchart-gallery en / zt, flowchart-game en, gallery-confirm ja / zc / en (`scratch/fix-ui/shots/`, `sheet_after.png` read) | the thumbnail under the English message; the Japanese and Chinese ending prompts inside the window; the English card text inside the white area; Traditional Chinese unchanged |

## 3. Open issues

1. The English card text at 0.75 is smaller than knife's 0.4 x 0.41481 (which the Switch shows on a 64 px taller card); what the PS4 itself does with its 496 px card in English is
   unproven (no capture). The choice follows the card's white area and the Traditional Chinese layout, whose synopses end on its edge.
2. Kept as knife lays them out: 17 Japanese titles end up to 13 px past the white area (max x 1823; the fit is English-only), and 3 Traditional Chinese synopses reach x 1850
   (the 150-unit box is 450 px on a 420 px text area). Both are the PS4's own numbers; a `TitleLimit` per language in `CardTextOf` would fit them if wanted.
3. The quick-load thumbnail frame ends at 263.5 units, 2 units into the buttons' sprite row (261.46), in English as in knife; the button art is transparent there.
4. The in-game FlowChart popup and the dialog fade-out were verified in the synthetic tests only (the real program could not be built while `Tsuki.App` is mid-edit).
