# K5b: gallery and flowchart

Owner: package K5b. Code: `src/Tsuki.Ui/Screens/Gallery*`, `Flowchart*`, `src/Tsuki.Ui/Services/IGalleryServices.cs`, `PreviewProgress.cs`. Behaviour and layout from knife
(`ui.rs` cGallery / cGalleryCG* / cGalleryMusic / cGalleryMovie / cGalleryEnding / cFlowchart, `flow.rs`, `gallery.rs`, `game/flow_jump.rs`, overseas branches) and
`docs/formats/gallery.md` (PS4 wins), all tables from the user's PS4 eboot through K1b's readers, art from the `GALLERY_PARTS_*` sets of allpac3.

Status: **done**. The gallery (CG viewer, music player, movies, endings), the flowchart (gallery and in-game) and their hooks in the title and the system menu build, step at
60 Hz, append `DrawCmd`s, take pad and mouse input, and render from the real sheets in ja / en / zc / zt. Not verified against a running PS4 (nothing was started): every
layout number comes from the eboot, the sheets, or knife (listed under "Open issues"). Nothing is committed.

## What was built

| path | what |
|---|---|
| `Screens/GalleryModel.cs` | the unlock rules (gallery.md 2.3, 4.3, 6.2, 7, 8) over `IUiGalleryTables` and `IUiProgress`: category gates, direct / alias / size-letter / node 557 CG unlock (name lookups resolved once), group tiles, track rules (heard, variants, the four special tracks, own heard bit), movie bits, ending rules (A or B scene, the last entry needs all others) |
| `Screens/GalleryScreen.cs` | the hub: six buttons (CG Gallery, Music Player, Flowchart, Movie, Ending List, Title), focus between buttons and content, mode crossfade (30 + 30 frames), cursor line and light, the guide that fades between its indices, the replay hand-off, the gallery memory (mode it closed on, entry a replay started from); `GalleryPanel` base for the modes |
| `Screens/GalleryCg.cs` | the CG grid (6 columns, 231 x 130.5 thumbnails at 392 + 240 c, 109 + 139 r, clip 109..1014, scroll tween, cursor frame and 15-frame glow, group grid and variations grid), `GalleryCgViewer` (full screen at the fit scale, zoom to 1:1, pan, L1 / R1 / left / right step through every unlocked picture with a crossfade, 4-strip language CGs, the two-second guide) |
| `Screens/GalleryMusic.cs` | the music player: 95 rows ("???" until unlocked), play / pause / stop / repeat / previous / next, time and volume sliders, title and credits panel, the player's clock with loop points (repeat wraps, otherwise a 1.5 s fade at the end), unlocks marked on entry |
| `Screens/GalleryMovies.cs` | movie cards (prologue, the openings once watched) and `GalleryMoviePlayer` (curtain, full-screen movie, one skip, BGM back, BGM 2 at frame 360 of the prologue) |
| `Screens/GalleryEndings.cs` | the ending list: three cards, 21 plates, the last scene's button; reached entries only take the cursor (knife's `ending_move`), "Play ...?" dialog, replay |
| `Screens/FlowchartChart.cs` | the chart (knife `flow.rs`): the PS4 column filter (reads the record past a column's end), sections with bands and dividers, chapter labels, grey / lit connector pieces, node pictures from the thumbnail mosaic, scroll range, cursor movement with line tracing, `KeepInView` |
| `Screens/FlowchartScreen.cs` | the chart screen (gallery and in game): view 1920 x 1022, pinned phase titles, cursor and glow, scroll widget, the scene card (preview, title, synopsis, "Current Scene" tag) with the 5-frame text fade, replay (gallery) or "rewind to this scene?" and the jump (in game), availability rules |
| `Screens/GallerySheets.cs`, `GalleryScroll.cs` | the sheet children of the six gallery sets, `GalleryMode`, `GalleryMemory`, the list scroll tween |
| `Services/IGalleryServices.cs` | `IUiProgress` and `IUiGalleryServices` (below) |
| `Services/PreviewProgress.cs`, `PreviewServices.cs`, `UiPreview.cs` | the in-memory implementation with believable unlock state (`Believe(data, "mid" / "early" / "full" / "none")`) and 12 preview scenes for `ui-shot` |
| `Tsuki.Engine/Contracts/EbootTables.Ui.cs`, `Tsuki.Data/GameEbootTables.Ui.cs`, `Golden/GoldenEbootTables.Ui.cs` | `IEbootTables.Gallery` (`IUiGalleryTables`): CG groups / aliases / strips / category scenes, tracks, endings, chart columns (with the overrun record), edges, label and connector cells, picture name lookup, picture seen index |
| `tests/Tsuki.Tests.Synthetic/Ui/` | `GalleryTestKit`, `GalleryModelTests` (12), `FlowchartChartTests` (11), `GalleryScreenTests` (26), `FlowchartScreenTests` (12) |
| `tests/Tsuki.Tests.Data/Ui/` | `GalleryRealDataTests` (15 methods, 35 cases), `GalleryOracleStates.cs`, generated `GalleryOracleExpected.cs`, `gen_gallery_oracle.py` |

Edits to K5a's files (all additive): `Framework/UiAssets.cs` (`Gallery` tables, null when the source has none), `UiContext.cs` (`Gallery` memory), `UiRoot.cs`
(`ReturnToGallery()`), `Screens/TitleScreen.cs` (GALLERY pushes the gallery after the host call; `ResumeInGallery`, `ReturnFromGallery`: the title restarts with the cursor on
GALLERY), `Screens/SystemMenuScreen.cs` (FlowChart opens the chart when available, ends a replay during a chart replay), `Screens/Dialogs.cs` (`ConfirmKind.EndReplay`, a
constructor taking a message id), `Services/IGameServices.cs` (below), `Tsuki.Ui.csproj` (**shared file**: `InternalsVisibleTo` Tsuki.Tests.Synthetic and Tsuki.Tests.Data, two lines). Contracts: `IEbootTables.Gallery` and
its record types, additive.

## IGameServices additions

`IGameServices` now also derives from `IUiGalleryServices : IUiProgress`. `IUiProgress.SceneSeen` has a default implementation (`SceneBit`), so an implementer adds 18 members:

| member | meaning |
|---|---|
| `SceneDone(n)`, `EdgeSeen(id)`, `MovieSeen(bit)`, `PictureSeen(seenIndex)`, `BgmHeard(track)` | the persistent bit sets of gallery.md 2.1 (G300, G400, G5, the picture / BGM block); false outside their ranges |
| `RouteScene(n)`, `RouteEdge(id)` | the per-save marks (F[201..272], F[600..687]) that light the in-game chart |
| `CurrentNode` | F110; the in-game chart opens on it |
| `PlayingBgm`, `MarkBgmHeard(track)` | the player marks the BGM that plays and the tracks a variant or a special rule opened |
| `PlayMusic(track, volume)`, `PauseMusic(bool)`, `SetMusicVolume(v)`, `StopMusic()` | the player (looping BGM at the player's volume); the track's end uses the existing `FadeOutBgm(1.5)` |
| `ReplayActive`, `ReplayScene(scriptScene, node)`, `EndReplay()` | a replay: G008 = scriptScene, F024 = 1, scene 3000 (ARCHIVE) (gallery.md 3.2); the UI has closed itself; when the story ends the host calls `UiRoot.ReturnToGallery()` |
| `FlowchartJump(column, node, scene)` | the in-game jump; the signature of K3's `GameHost.JumpToRecord(column, node, sceneId)`; false for a filler record |

The movie gallery needs no new member: it plays through `StartIntroMovie(4 / 5 / 6)`, `IntroMoviePlaying`, `IntroMovieSlot`, `StopIntroMovie` (the host applies the language remap).
`OpenGallery()` / `OpenFlowchart()` stay as notifications. Host wiring: the system menu and the title open the screens themselves; for a replay the host runs the scene and then
calls `ReturnToGallery()` (it pushes the title, hidden, and the gallery on the mode and entry the replay started from).

## Proof

Build (real tree, warnings as errors): `dotnet build src/Tsuki.slnx -warnaserror --artifacts-path artifacts/K5b` -> 0 warnings, 0 errors.

| command | result |
|---|---|
| `dotnet test tests/Tsuki.Tests.Synthetic/Tsuki.Tests.Synthetic.csproj --artifacts-path artifacts/K5b --filter FullyQualifiedName~Tsuki.Tests.Synthetic.Ui` | 161 passed (100 of K5a, 61 new) |
| `dotnet test tests/Tsuki.Tests.Data/Tsuki.Tests.Data.csproj --artifacts-path artifacts/K5b --filter FullyQualifiedName~Tsuki.Tests.Data.Ui` (needs `TSUKI_GAME_DIR`) | 288 passed in 18 s (K5a's cases now include the 12 new scenes x 4 languages) |
| `dotnet artifacts/K5b/bin/Tsuki.Cli/debug/Tsuki.Cli.dll ui-shot <scene> --lang ja\|en\|zc\|zt --out scratch/K5b` for `gallery-cg`, `-cg-group`, `-cg-view`, `-cg-strip`, `-music`, `-music-play`, `-movies`, `-endings`, `-confirm`, `-locked`, `flowchart-gallery`, `flowchart-game` | 48 PNGs, 0 missing textures, 0 missing sheets (`scratch/K5b/ui_<scene>_<lang>.png`) |

The real-data tests check: the tables load with the counts of gallery.md (458 groups, 1,972 CGs, 105 aliases, 11 strips, 19 category lists, 95 tracks, 25 endings, 277 columns,
1,286 nodes, 630 edges); every CG name finds its picture, thumbnails are 1,972 distinct children of 1,979; the unlock classes (952 direct, 105 aliased, 923 gallery-only) and the
11 language-strip pictures (each four strips tall); the 495 scene nodes (preview child = scene - 1, mosaic cell 0..519, title and synopsis texts in all four languages), the 95
tracks (loop points, credits) and 25 ending prompts in all four languages; the sheet sizes the layouts assume (en and ja) and the chart cells inside their sheets;
**the unlock rules equal the Python oracle** (`tools/tsuki/misc_gallery_extras_flowchart.py`) on seven fixed progress states: for each of CG entries, group tiles, tracks,
ending entries, movie items and the flowchart's column filter the C# lists are identical (`GalleryOracleExpected.cs`, regenerate with `gen_gallery_oracle.py`); the filter keeps
277 columns on the full state and 276 with all edges and no scenes (the numbers of gallery.md 5.4) and 71 with every scene started and scenes 1..100 finished (the oracle's answer); every cursor move on the real chart (gallery and in game, four
states) lands on a node; the full chart lays out (277 rows, 495 scene nodes) with every sprite inside its sheet; the title opens the gallery and gets its cursor back on real data.
The synthetic tests drive every state machine over fake tables: hub focus and wrap rules, mode switch, CG group / variations grid / viewer paging, music play / pause / stop /
repeat / step over locked tracks / end fade / volume / stop on leaving, movie player phases and BGM restore, ending dialog and replay call, endings cursor (knife's `ending_move`
cases), flowchart open / availability / jump / cancel / pointer, the replay round trip with `ReturnToGallery`, the title round trip, the system menu hooks.

Screens were read as PNGs: CG grid, variations grid, viewer (fit scale, language strip en / ja), music list and player (en, zc), movies, endings with dialog, locked flowchart
popup, both charts (en, ja) with labels, connectors, preview card and text.

## PS4 versus Switch (knife), what the data said

- The flowchart's chapter label and connector tables hold `(x, y, width, height)`; the other UI cell tables hold corners. `UiGalleryView` converts them to corners. The PS4 label table
  has "Prologue" at index 28 and a zero cell at 29 (knife uses the last cell); the code takes the last non-empty one.
- Node thumbnails: the PS4 has one mosaic (FC child 7, 1620 x 1170 = 20 x 26 cells of 81 x 45 with a one-pixel border), the cell is the node's +0x14 field (image comparison on 8 nodes: 7 exact, the eighth
  matched a near-identical neighbour cell); the Switch has a 104 x 64 atlas. The inner 79 x 43 is stretched into the frame's 102 x 62 [H]. The node frame is FC child 11.
- The column filter reads one record past a column's end (`j > count` tests, gallery.md 5.4); knife reads 0 there. The port follows the PS4 and equals the emulated engine on 7 states.
- CG pictures are addressed in the PS4 picture's own pixels (the table's x, y, w, h; no 1.5 scale); the 11 language-strip CGs are four strips tall in the data and the language picks
  the strip. Thumbnails are `GALLERY_PARTS_CG_TH` children (231 x 130), previews `GALLERY_PARTS_FC_TH` (430 x 243; four children are 430 x 972 and stack one band per language).
- The title banners of the CG gallery and the music player exist in English only (the ja / zc / zt sets have one child fewer: nothing is drawn there); the movie and ending banners
  exist everywhere. The six mode button labels are English in every language (GALLERY_PARTS_COMMON is identical in all five).
- The scene card is 488 x 496 in every language (the Switch's English card is 488 x 560): four synopsis lines fit below the title with knife's text numbers.
- Ending list: the table has 25 records; the last (QA_25) is drawn as knife's separate 112 x 112 button at (1702, 894) once the other 24 are reached, not as the 22nd plate "BAD END No.22" that the
  sheet also holds [H]; the cursor grid treats it as column 5 of the last row as knife and gallery.md do.
- Music, endings and movie unlock rules, the category gates and the picture-seen index follow gallery.md and equal knife's rules; ui ids are the PS4's (knife + 1 from 217).
- `ARCHIVE` replays (scene 3000, G008, F024) and the flowchart jump are game side; `FlowchartJump` is the signature of K3's `JumpToRecord`.

## Open issues

1. Not compared with a PS4 capture. Layout numbers that come from knife only: the hub, grid and music positions, text sizes (flowchart card 0.4 x 0.415 of the base glyph, music rows
   0.444 x 0.498), the card text boxes (150 / 136 units), the 5-frame text fade. English text tracking is wide as in K5a's menus.
2. The chart rows are 56 px apart as knife; gallery.md 5.4 mentions row advances of 8 / 32 / 56 and column widths in the PS4 code (0x217E0..0x21D0C, "partly read") that are not
   decoded. Connector cells for kinds the table lacks (12) fall back to a 4 x 4 cell as knife. The in-game chart's lit lines were checked on fake data and by eye only.
3. Simplified transitions: the hub does not crossfade into the chart (the chart fades in alone over black or the blurred scene); the CG viewer's picture change is a crossfade, not
   knife's rule wipe (`RULE_26` / `RULE_27`).
4. PC input: the viewer's sticks (pan / zoom) are Up / Down and the wheel (zoom) and dragging with the left button (pan); the music volume slider is set by a click (no drag), the music list
   does not pull its cursor into view after a wheel scroll (the CG grid and the chart do); no touch.
5. `GalleryMovies`: movie ids 4 / 5 / 6 go to the host unmapped (it applies the language remap); the curtain is white for the first opening for 30 frames, not until the movie is fully shown
   (the host reports no alpha).
6. K3 / K4 must implement the 18 new service members (`SystemData` has the bit sets; `GameHost.JumpToRecord` exists); `RouteScene` / `RouteEdge` read the per-save words F[201..272] / F[600..687].
7. `IEbootTables.Gallery` is null for the golden slice: the title's GALLERY then keeps K5a's behaviour (the host call only) and the system menu's FlowChart buzzes.
