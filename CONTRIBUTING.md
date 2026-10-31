# Contributing

Thank you for looking at the port. Three rules come before everything else, then the practical part:
how to build, how to report a problem, how to send a change.

## 1. No game data, anywhere

**Never attach, paste or link game data** in an issue, a pull request, a discussion, a release comment or
a wiki page. Game data means anything that comes out of the game's files or the running game:

- the game folder or any file from it (`eboot.bin`, the `image\`, `data\` and `sce_sys\` folders, `.mrg`,
  `.hed`, `.nam`, `.mzp`, `.mp4`, `.at9`, `param.sfo`, fonts, saves);
- anything decoded or extracted from them: pictures, sprites, backgrounds, sound, music, movies, text
  dumps, script dumps, tables or strings read out of `eboot.bin`;
- **screenshots of the game**, including the title screen, the menus and the launcher's Play page banner
  (they show the game's art and text). A screenshot of the launcher's Help or Game files page, or a
  crop that shows only the port's own message, is fine;
- story text: do not quote the game's lines, even one, even to show a bug. Give the **line id** (the
  log prints it) or the script name and offset instead.

A report that needs a picture of a rendering bug works like this: say the scene (script name, page or
line id, the choice taken) and what is wrong in words; the maintainers reproduce it on their own copy.

The port contains no game data and the project does not provide game files or link to any source of
them. A pull request, issue or comment that does is closed without discussion. The
repository's pre-commit guard (`tools/setup/hooks/precommit_check.py`) refuses the data folders and the
game-data file types, but the GitHub web upload has no guard: please look before you post.

## 2. How to build

- Windows 10 or 11 (64-bit), the **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`), Git.
- The FFmpeg 9 LGPL libraries, once: `tools\setup\ffmpeg_dlls.ps1` copies the FFmpeg 9 LGPL DLLs from the
  installed winget package `BtbN.FFmpeg.LGPL.Shared.9.0` into the git-ignored `third_party\ffmpeg\bin` (if the
  package is missing it prints the `winget install` command and stops; it never installs or downloads anything
  itself; the build stops with `TSUKI001` without the DLLs).
- `dotnet build src\Tsuki.slnx -c Release` builds everything; warnings are errors
  (`Directory.Build.props`), so `-warnaserror` is the normal state, not an option.
- The release folder: `dotnet publish src\Tsuki.Launcher -c Release -r win-x64 --self-contained -o <folder>`
  (one folder with `TsukihimeLauncher.exe`, `Tsukihime.exe`, the runtime, the libraries and `licenses\`).
  Never build with `-p:TsukiPersonalIcon=...` for anything that leaves your machine (README, "Personal build").
- Tests: `dotnet test tests\Tsuki.Tests.Synthetic` needs no game. `tests\Tsuki.Tests.Data` and
  `tests\Tsuki.Tests.Render` run against your own copy and skip, with the reason, when the environment
  variable `TSUKI_GAME_DIR` does not name the `CUSA47999` folder; the Render tests open a hidden window.
- The Python research tools and their tests: README, "Setup" and "Running the tests" (a `.venv` with
  `numpy`, `pillow`, `pytest`; `PYTHONPATH=tools`; `python -m pytest tools\tests -q`).

Nothing in the build, the tools or the tests writes into the game folder. Keep it that way.

## 3. How to report a problem

Open an issue with:

1. **What happened and what you expected**, and how to get there (New Game, which choice, which menu).
2. **The log.** The launcher's **Log** button opens `%APPDATA%\Tsuki\last_run.log` (the game writes it
   on every start from the launcher; `crash.log` beside it when the game fails). Attach the whole file or
   its first forty lines. Before you post it, you may replace your Windows user name in the paths it
   prints (the game folder, the save folder) with anything you like; nothing else in it is personal.
3. **The build info**, which the log's first lines carry: the `layout:` and `build:` lines (your dump's
   folder shape, the eboot's size and SHA-256, the version from `param.sfo`), the `languages:` line, the
   `tables:` line, and the graphics adapter line (`renderer:` / `OpenGL`). If the game refused to start
   because a table was not found, the log names the build and the missing tables: that is the report.
4. **Your machine**: Windows version, graphics card or chip and driver version, monitor size, whether
   the problem happens windowed and fullscreen.
5. **Not** a screenshot of the game and **not** a save file. A screenshot of the launcher's Help page or
   of a Windows error dialog is fine.

For the launcher, `TsukihimeLauncher.exe` reports its own problems on the Play page's Ready check; copy
the text of the row that is red.

## 4. Sending a change

- Small pull requests, one subject each, with a test (the Synthetic project for anything that needs no
  game; a real-data test skips cleanly without `TSUKI_GAME_DIR`). Tests use **synthetic data only**:
  no fixture may embed game content, not even a line of text or a glyph.
- The build must stay warning-free (`dotnet build src\Tsuki.slnx -c Release`), and the three test
  projects and the Python tests must pass.
- Facts about the game's engine (addresses, table layouts, command behaviour) go into `docs/formats/`
  with the evidence that backs them (the documents use `[E]` read from the engine code, `[V]` verified on
  the retail data with counts, `[H]` hypothesis).
- Licence: the port's code is under the **Mozilla Public License 2.0** (`LICENSE`); by sending a change
  you agree that it is licensed the same way. The files translated from knife keep their MPL header and
  their row in `docs/THIRD_PARTY.md` (a test enforces the table). Do not copy code from other projects
  unless its licence is compatible and credited there.
- Install the hooks (`tools\setup\install_hooks.ps1`, or copy `tools\setup\hooks\precommit_check.py` to
  `.git\hooks\pre-commit`): they refuse `samples\`, `scratch\`, `ref\`, `third_party\`, game-data file
  types and files over 5 MiB.
