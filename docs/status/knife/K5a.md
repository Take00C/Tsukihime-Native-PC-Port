# K5a: menus (framework, title, system menu, save / load, config, backlog, mode panel)

Owner: package K5a. Code: `src/Tsuki.Ui/`. Behaviour from knife (`ui.rs`, `ui/pad.rs`, `fx.rs`, overseas branches), layout, art and flow
from the PS4 data and `docs/formats/system_flow.md` (PS4 wins over knife). Gallery and flowchart are K5b: they are reached through two service
calls and nothing is built for them here.

Status: **done for the listed screens**. Every screen steps at 60 Hz, appends `DrawCmd`s to a `DisplayList`, takes pad and mouse input, and renders from
the user's real UI sheets in ja / en / zc / zt. Not verified against a running PS4 (no shadPS4 was started): every layout number is read from the
eboot or the sheets, every behaviour that only knife documents is listed under "Open issues". Nothing is committed.

## What was built

| path | what |
|---|---|
| `Framework/UiMetrics.cs`, `UiPainter.cs` | 1920 x 1080 canvas, 640 x 448 unit conversions, `Sprite` / `Fill` / `Cover` / `Thumbnail` / rotated sprites, alpha and clip scopes, sheet tiles split along the stored tiles (apron-bordered UVs) |
| `Framework/UiAssets.cs`, `UiSheets.cs` | the six UI sheet sets (`UiSet` children by PS4 index, see UiSheets for every constant), cached sheet geometry, missing-sheet counters, `Sysmes` (knife SYSMES n = ui_text n + 1 from 217), `IUiPictureSource` for `parts.mrg` |
| `Framework/UiText.cs` | text through `TextLayout` + `BlockDraw` (glyph pages, outline = v + 512), `TextStyle.Slot / Menu / Rgb565`, layout cache |
| `Framework/UiInput.cs` | `InputFrame` -> `UiInput` (decide / cancel / directions with repeat / shoulders / triggers / triangle / square / options, pointer position, click, right click, wheel) |
| `Framework/UiScreen.cs`, `UiRoot.cs`, `UiContext.cs` | screen base (content fade, `Finished`, `Opaque`, `StepBackground`), the stack (only the top steps with input, covered screens keep animating), `OpenSystemMenu / OpenBacklog / OpenModePanel`, `ShowBanner`, `Mode` and `BlurScene` for the host |
| `Framework/UiWidgets.cs`, `UiBanner.cs`, `UiBackdrop.cs` | button guide line (12 sequences decoded from eboot function 0x6A3C0), scroll bar, dialog window, digits, mode banner pop-in, menu backdrop strips |
| `Screens/BootScreen.cs` | PS4 boot: autosave notice (parts.mrg 8 + language, spinner, "Saving", glow), no logo screen |
| `Screens/TitleScreen.cs` | PS4 title: background movie slot (or still), logo, CONTINUE / NEW GAME / LOAD GAME / CONFIG / GALLERY, stage rule G002 (`{1,2,3,3,2,4}`), PRESS ANY BUTTON -> caution -> caution1 -> white fade -> scene 110, first visit -> scene 260, returning-player intro (movie 38 + caution2), particles |
| `Screens/SystemMenuScreen.cs`, `SideFocus.cs` | Save / Load / Config / FlowChart / Title tabs with the scene title band; side focus handover to the sub menus |
| `Screens/SaveLoadScreen.cs` | slot lists (save: first free + used newest first; load: used newest first, autos tagged), thumbnails, phase titles, date / time digits, scroll, save / load / delete / copy flows, confirm dialogs |
| `Screens/ConfigScreen.cs` | System / Sound / Controls tabs, controls placed from the eboot `ConfigPageTable` (slider, minus / plus, check, test, radio groups, language buttons), voice rows placed at run time and shown only for characters heard, text preview typing, Recommended Settings, scroll, mouse |
| `Screens/BacklogScreen.cs` | entries by page, voice replay, jump with confirm, scroll bar |
| `Screens/ModePanelScreen.cs` | the slide-in mode panel: previous scene, rewind (held), auto, skip (held), next scene, Q.SAVE, Q.LOAD, replay voice; cells of MODEPNL_PARTS |
| `Screens/Dialogs.cs` | `ConfirmDialog` (15-frame hold on the guard, 1-frame cancel), `MessagePopup` (held / released, minimum frames) |
| `Services/IGameServices.cs` | the game side interface (below) |
| `Services/PreviewServices.cs`, `UiPreview.cs` | in-memory implementation with a call log, and the named preview scenes used by the screenshot tool and the tests |
| `src/Tsuki.Cli/Commands/UiShotCommand.cs`, `PngWriter.cs` | `ui-shot` |
| `src/Tsuki.Engine/Contracts/EbootTables.Ui.cs`, `src/Tsuki.Data/GameEbootTables.Ui.cs`, `src/Tsuki.Data/Golden/GoldenEbootTables.Ui.cs` | `IEbootTables.Ui` (`IUiTables`): sheet cell tables, config pages, dialog message ids, voice row words, title stage table, picture lookup |
| `tests/Tsuki.Tests.Synthetic/Ui/` | 100 state machine tests (fake `IGameData` / tables, no game data) |
| `tests/Tsuki.Tests.Data/Ui/UiRealDataTests.cs` | 157 real-data cases |

Contract changes (all additive): `TextureKind.Thumbnail = 6` and `TextureRef.ForThumbnail(slot, version)` in `DisplayList.cs`; `IEbootTables.Ui`
as a partial (`EbootTables.Ui.cs`); `new UiShotCommand()` in `CliApp.cs` (K3 overwrote the registration once; it is present now).

## The game side: `IGameServices`

One object per game implements `IGameServices` (= `IUiSystemServices` + `IUiSaveServices` + `IUiStoryServices` + `Title`). K3 (`Tsuki.Engine.Game`)
and K4 (`Tsuki.Engine.Save`) are the intended implementers; the UI never references them.

```csharp
// host loop
var ui = new UiRoot(gameData, services, partsPictureSource);    // parts source only needed for the boot notice
ui.Push(new BootScreen());  // or TitleScreen; ui.OpenSystemMenu() / OpenBacklog() / OpenModePanel() in the story
ui.Step(inputFrame);        // once per 60 Hz frame
ui.Draw(displayList);       // appends quads; ui.Mode (Title / Overlay / Story) and ui.BlurScene tell the host what to draw under it
```

| group | members |
|---|---|
| system | `Language`, `Config` (`ConfigBlock`), `ConfigChanged(ConfigChange)`, `CommitSystemData()`, `DecideIsCross`, `Global/SetGlobal(i)`, `SceneBit(bit)`, `PlaySystemSound(UiSound)`, `PlayTestSe()`, `PlayTestVoice(row)`, `StopTests()`, `PlayBgm(track)`, `FadeOutBgm(s)`, title movie (`StartTitleMovie`, `StopTitleMovie`, `TitleMovieSlot`, `SeekTitleMovie`, `TitleMovieTime`) and intro movie (`StartIntroMovie`, `IntroMoviePlaying`, `IntroMovieSlot`, `StopIntroMovie`) |
| save | `SavingDisabled`, `Slots` (140: manual 0..99, auto 100..138, quick 139), `Thumbnail(slot)` (drawn as `TextureKind.Thumbnail`, slot + version), `Save/Load/Delete/Copy(slot)`, `QuickSave()`, `QuickLoad()`, `HasQuickSave` |
| story | `Phase`, `SceneTitle`, `AutoMode`, `SkipMode`, `SceneJumpEnabled`, `PanelAllowed`, `ToggleAuto/Skip`, `FastForward(held)`, `ReplayVoice`, `NextScene/PreviousScene`, `Rewind(held)`, `StartScene(id)`, `ResumeLoaded()`, `AutoSaveForTitle()`, `ReturnToTitle()`, `Backlog` (pages of messages with voice ids), `PlayBacklogVoice(id)`, `BacklogJump(page)`, `OpenFlowchart()`, `OpenGallery()` |
| title | `Title` = `UiTitleInfo(PrologueSeen, StoryStarted, TitleStage, ContinueSlot)` |

The host owns the movie planes: the title screen draws `TextureRef.ForMovieFrame(TitleMovieSlot)` once the title movie has started (and `IntroMovieSlot` for the intro movie), and the stage-rule still sheet otherwise.
`PreviewServices` is a complete reference implementation.

## Proof

Build (real tree, warnings as errors): `dotnet build src/Tsuki.slnx -warnaserror --artifacts-path artifacts/K5a`
-> 0 warnings, 0 errors; same for `tests/Tsuki.Tests.Synthetic` and `tests/Tsuki.Tests.Data`.

| command | result |
|---|---|
| `dotnet test tests/Tsuki.Tests.Synthetic/Tsuki.Tests.Synthetic.csproj --artifacts-path artifacts/K5a --filter FullyQualifiedName~Tsuki.Tests.Synthetic.Ui` | 100 passed |
| `dotnet test tests/Tsuki.Tests.Data/Tsuki.Tests.Data.csproj --artifacts-path artifacts/K5a --filter FullyQualifiedName~Tsuki.Tests.Data.Ui` (needs `TSUKI_GAME_DIR`) | 157 passed in 12 s |
| `dotnet artifacts/K5a/bin/Tsuki.Cli/debug/Tsuki.Cli.dll ui-shot --all --lang en` (also ja, zc, zt) | 19 scenes x 4 languages, 0 missing textures, 0 missing sheets; PNGs in `scratch/K5a/ui_<screen>_<lang>.png` (not committed) |

The real-data tests check: every preview scene in ja / en / zc / zt builds 3..5000 quads with no missing sheet or picture, every texture it names exists
in the game data (sheet tile, glyph page, picture tile); the sheet sizes the layouts assume (against `samples/ui_misc/ui_parts.csv`, en and ja); every cell
table lies inside its sheet in all languages; the table shapes (38 phase titles, 23 guide icons, 27 words, 55 dialog messages, 19 voice rows, the title
stage table); the guide sequences resolve; the config pages equal knife's control positions for the System and Sound tabs (knife's radio rows carry the
extra +6.637 units); every confirm dialog has a non-empty message in every language; the caution pictures are found by name; the boot notice pictures
exist; text draws through the glyph pages in every language; every config tab lays out on the real pages.

Screens were also read as PNGs against `docs/formats/system_flow.md` and the sheet layouts (title en / ja, menu, save, load, config tabs, backlog, mode
panel, banner, dialogs, boot, caution screens, zc / zt checks).

## PS4 versus Switch (knife), what the data said

- Flow: the PS4 has no logo screen; boot shows the autosave notice, a returning player gets movie 38 and caution2, the title background comes from the stage
  rule on G002, "first visit" is bit 485 (scene 260). knife's Switch intro was not used.
- Config: positions of every control come from the eboot page table (kinds 3/4/5 minus / slider / plus, 2 check, 1 test, 6..9 radio groups, 10 language);
  voice rows are placed at run time (370.01 + 50.607 n). No Switch table was copied; plain layout numbers that appear in tests carry a comment.
- Button guide: the PS4 guide sequences are word-then-icon and are decoded from the eboot, not taken from knife's `GUIDE_SEQ`.
- Dialog messages: ids from the eboot (55); dialogs from 21 on use the PS4 +2 shift; SYSMES ids from 217 shift by one.
- Banners: the sheet is DISP_PARTS child 10 (432 x 264, four text rows); the row rectangles are knife's plain layout numbers (commented in `UiBanner`), the pop-in scale keys come from the eboot. `IUiTables.BannerCells` (2 cells from the eboot) is read but not used: see open issue 12.
- The mode panel is a PS4 art set (MODEPNL_PARTS); only its slide and press behaviour is from knife's `touch.rs` panel.
- Save lists: manual slots 0..99, auto 100..138 (FIFO), quick 139 as in `adv_state.md`.

## Open issues

1. Pad semantics: SQUARE (west) opens the system menu, TRIANGLE (north) opens the backlog and is Copy Data in the save list. This follows the PS4 guide
   pictures and contradicts `adv_state.md` 10.2 [H]. One-line change in `UiInput` and the screens if the oracle says otherwise.
2. Save list: Triangle = copy, Square = delete is a decision from the guide icons, not from a disassembled handler.
3. Load list shows autosaves (tagged); the PS4's rule is [H].
4. `DecideIsCross` swaps the confirm / cancel icons in the guide: [H], no PS4 trace of the swap.
5. English text tracking in menus looks wide compared with the Switch shots; the layout is engine-exact but not compared with a PS4 capture.
6. Title particles use knife's model; the PS4 particle system is not documented. The notes text spacing in the save list is also unverified.
7. Not built: the choice screen (K3 / Game), help balloons (their message ids are in `IUiTables.DialogMessages`), flowchart and gallery (K5b; the system menu and title call
   `OpenFlowchart()` / `OpenGallery()`).
8. `parts.mrg` is not served by `IGameData`: the boot notice needs an `IUiPictureSource`. `PartsPictureSource` lives in `Tsuki.Cli` (internal) and
   `UiPictureArchive.Parts = 6` marks those textures. Request to the lead: serve the `parts` archive pictures through `IGameData` so the host needs no extra source.
9. Chinese config preview text in the System tab nearly touches the millisecond digits (zc / zt); measured from the layout, not from the PS4.
10. `UiSound` values (Decide 0, Done 1, Cursor 2, Open 3, Start 5, Buzzer 6) are knife's sound ids; the host maps them to the PS4 system effects.
11. Movies are not rendered by the screenshot tool (title uses the still); the movie texture path is covered only through the draw list.
12. The banner row rectangles are knife's numbers; the eboot's two banner cells (`IUiTables.BannerCells`) were not reconciled with the four rows.
