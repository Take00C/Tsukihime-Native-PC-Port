# Porting guide: using knife as the behaviour reference

Decision (user, 2026-10-26): we keep our own C# engine, Tsuki, and use knife as the reference for the
game logic we have not written yet, so we stop rediscovering behaviour that knife already ships.

**knife** (github.com/AuroraWright/knife, read-only clone at `ref/knife`, commit
b12433c) is a Rust reimplementation of the same HuneX engine ("nexl"), written from a decompilation of
the **Switch** executables and checked against the Switch game. It plays Tsukihime end to end: the
script, stage, text, sound, movies, saves, backlog, menus, flowchart and gallery. Start with
`ref/knife/engine-writeup.md` (600 lines). The Switch-exe addresses in knife's comments (`sub_...`,
`0x...`) are Switch addresses. They are not ours.

## 1. Rules for using knife

1. **License: MPL-2.0 (file-level copyleft).** Reading knife to learn behaviour is unrestricted.
   A C# file that translates a knife file (same structure, logic or constants carried over) is a
   modification of that file and must carry this header at the top:

   ```csharp
   // This Source Code Form is subject to the terms of the Mozilla Public
   // License, v. 2.0. If a copy of the MPL was not distributed with this
   // file, You can obtain one at https://mozilla.org/MPL/2.0/.
   // Ported from knife (https://github.com/AuroraWright/knife) by AuroraWright: knife/src/<file>.rs
   ```

   Keep translated code in its own files, and keep our own code (written from our docs) out of them.
   Every MPL file is listed in `docs/THIRD_PARTY.md` (add yours).
2. **PS4 first.** We run the **PS4** data and the PS4 eboot. Where our verified PS4 docs
   (`docs/formats/*.md`, `[V]` claims) and knife disagree, the PS4 docs win. Where our docs are
   silent, `[H]` or open, knife's behaviour is the default. Record each disagreement you hit in your
   status file under "PS4 vs Switch".
3. **Our PS4 build is knife's `Build::Overseas`** (ja/en/zc/zt in one build). Follow the overseas
   branches. Skip `Build::Witch`, `witch*.rs`, `ui/witch/`, `teatime.rs`, the touch UI
   (`ui/touch.rs`), `text_stream.rs` (WebSocket), and the platform movie decoders.
4. **Do not copy Switch tables.** knife hard-codes tables taken from the Switch exe
   (`motion_loops.rs`, `flow_table.rs`, `gallery_table.rs`, `save_tables.rs`, constant arrays in
   `ui.rs` / `game.rs`). We read the same tables from the user's PS4 eboot at run time. Package K1b
   locates them (`docs/research/knife_tables.md`); use its readers, not knife's literals. A
   constant that is a plain layout number (a pixel position, a frame count, a speed) may be carried
   over with a comment naming the knife source.
5. Never add knife's Rust code, its data bundles or anything from the game to git. `ref/` is git-ignored
   and write-protected by the guard hook.

## 2. Module map

| knife | holds | Tsuki home | notes |
|---|---|---|---|
| `knife-core/src/vm/parse.rs` | script tokenizer | keep ours: `Tsuki.Engine/Vm`, `Flow` | ours is verified on all 549 PS4 scripts |
| `knife-core/src/format/*`, `kdu` | Switch formats (BNTX, NXGX, bffnt, bfttf, CFF) | not needed | PS4 formats are in `Tsuki.Data` |
| `knife-core/src/exe.rs`, `*_table.rs`, `motion_loops.rs` | exe tables | `Tsuki.Data/Eboot` (K1b) | read from the PS4 eboot |
| `knife-core/src/linebreak.rs` | UAX #14 for English | compare with `Tsuki.Engine/Text` (package B) | ours follows the PS4 Text_Layout |
| `assets.rs` | archive lookup, texture cache | `IGameData` (`GoldenSource`, `GameFolderSource`) | image lookup order: archives_ui_movies.md |
| `game.rs` + `game/choice.rs`, `flow_jump.rs`, `story_movie.rs`, `focus.rs`, `ghost.rs` | interpreter: one arm per command, frame loop, waits, skip/auto/rewind, choices, scene jumps, TOMP, FCLN, FADE(40) | `Tsuki.Engine/Game` (an `IHost`) | PS4 commands knife ignores: see 3 |
| `stage.rs`, `motion.rs`, `grade.rs`, `game/shake.rs`, `fx.rs` | 40 layers, three stage copies, commits, tweens, STMA motions, quakes, STEC grades, screen shake, menu particles | `Tsuki.Engine/Stage` -> `DisplayList` | also docs/formats/layers.md, quake.md, compositing.md |
| `text.rs` (layout half) | font atlas + NVL layout | keep ours (`TextLayout`, glyph pages) | PS4 uses pre-rendered glyph pages |
| `game.rs` `Page`, typing, waits | pages, typing head, window fades, history ring | `Tsuki.Engine/Game` + `MessageWindow` | wait_model.md, textbox.md |
| `sound.rs`, `knife-audio/src/lib.rs` | BGM / SE channels / voice, fades, ducking | `Tsuki.Media` mixer via `AudioCommand` | `bfsar.rs`, `codec.rs` are Switch-only |
| `movie/mod.rs`, `movie/decoder.rs` | movie playback task | `Tsuki.Media/Movie` (FFmpeg) | platform decoders not needed |
| `savedata.rs`, `save.rs`, `game/records.rs` | save file, slots, system data, rewind snapshots | `Tsuki.Engine/Save` | no PS4 retail save exists: use knife's layout for our own file |
| `flow.rs`, `game/flow_jump.rs` | flowchart layout, node marks | `Tsuki.Ui` + `Tsuki.Engine/Game` | tables via K1b |
| `gallery.rs` | CG unlocks | `Tsuki.Ui` | gallery.md |
| `ui.rs`, `ui/pad.rs` | every menu as a state machine (title, system menu, save/load, config, backlog, mode panel, dialogs, gallery, flowchart) | `Tsuki.Ui` | PS4 atlases: `UiSet` (allpac3); PS4 boot differs (system_flow.md) |
| `main.rs`, `gamepad.rs`, `ini.rs` | window, frame pacing, input, options | `Tsuki.App`, `Tsuki.Platform` | SDL3: keyboard, mouse, gamepad |
| `knife-render` | wgpu quads, blend modes, rule transition, blur | `Tsuki.Render` (`DisplayListExecutor`) | map knife's `Blend` / `PsOp` / `Shade` / `RuleMask` / blur to our draw effects |

## 3. PS4 versus Switch

| area | Switch (knife) | PS4 (ours) |
|---|---|---|
| images | BNTX (BC7), NXGX / NXCX in `allpac*`, `allui` | MZP PAL8 / HEP8 / RGB565 in `image/allpac0,1,1_2,4`; UI in `allpac3` (`UiSet`), `parts.mrg` |
| audio | `knife1.bfsar` + Opus streams | ATRAC9 RIFF (`bgm`, `vse`, `voice`) + HE-VAG; `IGameData.Audio` |
| fonts | TTF / OTF + Switch system font | pre-rendered glyph pages `ft_<lang>.mrg` + eboot tables (fonts.md) |
| movies | 46 MP4 | 134 MP4 (per-language copies); `IGameData.Movie` |
| exe tables | Switch NSO (knife bundles) | PS4 eboot (`Tsuki.Data/Eboot`; address mapping in `scratch/rerun/agent_brief.md`) |
| script | same scenes; knife skips PS4-only blocks (`IFP4`, 204 uses) and ignores `SNX4`, `FANX`, `IFNX`, `WTSV`, `STQK`, `TRPY` | we run the PS4 blocks; semantics of those commands: docs/formats/script.md |
| boot | logo movie, caution, title | no logo screen; notices then title (system_flow.md 3-4) |
| input | Switch buttons, touch | PS4 pad semantics; on PC: keyboard, mouse, SDL3 gamepad |
| draw space | 1920 x 1080 canvas | same; PS4 compositor and 25 blend modes (compositing.md) |

## 4. Bridges into our architecture

- **Rendering.** The engine never calls the GPU. `Stage` and `Ui` append `DrawCmd`s to a
  `DisplayList` (`Tsuki.Engine/Contracts/DisplayList.cs`); `Tsuki.Render` executes it. When knife
  draws something our executor cannot express (a blur pass, a rule mask, a soft clip), add the
  feature to the executor and the contract additively, with a GPU test.
- **Data.** Everything comes through `IGameData` and the eboot table readers. No file I/O in Engine.
- **Sound and movies.** The engine emits `AudioCommand` / `MovieCommand`; `Tsuki.Media` performs them.
- **Time.** The engine steps in 60 Hz frames, as knife's update does; the app paces presents.

## 5. Working rules for every package

- Paths, data locations and hard rules: `scratch/rerun/agent_brief.md` (game folder read-only, no
  shadPS4, no pushes, no installs outside `.venv`).
- Build and test only with your own output folder:
  `dotnet build src/Tsuki.slnx -warnaserror --artifacts-path artifacts/<package>` and
  `dotnet test <project> --artifacts-path artifacts/<package>`. Never leave an app or
  test host running. GPU timing measurements hold `artifacts/gpu.lock` (decisions.md 4).
- Contracts (`Tsuki.Engine/Contracts`) change only additively. Do not rename or remove a member
  another package uses.
- Edit only the files your package owns. If you must touch a shared file (the solution, a `.csproj`,
  `Directory.Packages.props`), make the smallest additive change and say so in your status file.
- Tests: synthetic tests in `tests/Tsuki.Tests.Synthetic`, real-data tests in `tests/Tsuki.Tests.Data`
  with `[GameDataFact]`. No skipped tests. A real-data test stays under 60 s.
- Status file: `docs/status/knife/<package>.md`. It holds what was built, the proof commands with
  results, PS4 vs Switch findings, and open issues.
- Do not commit; the lead commits after checking.
- Spend carefully: read the knife files your package needs, not the whole repository.
