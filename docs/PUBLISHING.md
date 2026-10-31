# Putting the port on GitHub: what to upload, what to fix first, and how (package K26)

Written 2026-10-30 against commit `f74a533` (branch `main`, 19 commits). This is an **audit and a plan**:
nothing outside this file and `scratch/k26/` was changed, no history was rewritten, no remote was created,
nothing was pushed. Every statement below was checked with a command on this repository; the commands and
their results are in section 6. Plain language first, technical detail for the lead agent in the tables.

Two of the three things you asked for in the same message, the Windows screenshot message and the launcher
working with other people's game folders, are **not** part of this package; they need their own packages.
This file answers "what do I need to upload on GitHub?".

---

## 0. The short version

**What you upload is two things, and never a third:**

1. **The source code repository** (the `D:\TsukihimePort` project: the C# engine, the launcher, the tests, the
   Python research tools and the format documents). It is small (12.6 MB of text, 924 files, the git
   history itself is 5 MB) and contains **no game files**: not one archive, picture, sound, movie, font,
   `eboot.bin` or save file was ever committed, in any of the 19 commits (section 2.4).
2. **The player download**, as a *GitHub Release* attachment: one zip of the published folder
   (`Tsukihime.exe`, `TsukihimeLauncher.exe`, the .NET runtime, SDL3, the four FFmpeg DLLs, the README and the
   licence texts). Measured on the last publish: 279 files, 252 MB unpacked, **105 MB zipped**. It contains no
   game data either; players point it at their own copy of the game.
3. **Never**: the game folder (`CUSA47999`, 44 GB), anything extracted or decoded from it (`samples/`), the
   working folders (`scratch/`), build output (`artifacts/`), the third-party sources (`ref/`: link to them
   instead), the FFmpeg download (`third_party/`), your saves (`%APPDATA%\Tsuki`). All of these are already
   outside git (`.gitignore`) and the pre-commit guard refuses them, so "upload the repository" cannot take
   them along by accident.
4. **Never a personal build** (package K28, section 1.4): a build made with `-p:TsukiPersonalIcon=<game folder>`
   carries Arcueid's face, cut from your game files, inside `Tsukihime.exe` and `TsukihimeLauncher.exe`. The
   release zip is built **without** that property, and `Tsuki.Cli release-check <folder>` must say "may be
   released" before the folder is zipped.

**The repository is close to publishable, but not yet. Five things must be fixed before it goes public:**

| # | finding | where | why it matters | fix (one line; details in section 2) |
|---|---|---|---|---|
| 1 | **Your Windows user name and the game's location on your disk**: `<user profile folder>\<account>\Downloads\<extractor tool>\...\CUSA47999` | 10 files of the current version, and in **every one of the 19 commits** (3 files in the first commit, 10 from the 15th on); also your Windows account name (`<account>`) in one status file and a stray a stray `D:\...html` file name in one research note | personal data; also tells everyone which extraction tool you used | replace the path with a neutral placeholder (`D:\Games\CUSA47999`) in the 10 files; publish with a fresh history (section 4) so the old copies disappear too |
| 2 | **Your e-mail address** (a private webmail address) and your private name on all 19 commits (author and committer) | git metadata, not file content | GitHub shows the author on every commit page and anyone can read the address with `git log` | publish with a fresh history under the name and e-mail you choose (GitHub offers a private `...@users.noreply.github.com` address), or rewrite the 19 commits with a mailmap |
| 3 | **About forty full lines of the game's story text** quoted as examples | mostly `docs/formats/text.md` (a table of 12 prologue caption lines in Japanese and English, the opening line in four languages, two hex dumps with the first words), plus single lines in `fonts.md`, `system_flow.md`, `script.md`, `FORMATS.md`, `RUNTIME_REQUIREMENTS.md` (and `.draft.md`), two status files, and two test files (`tools/tests/test_text.py`, `tools/oracle/textbox.py`) | the README promises "no script text" in the repository; forty lines out of 44,000 is small, but the promise should be true, and quoting translated prose is the one thing a rights holder can point at | shorten every quote to its line id plus the first two or three characters (the documents stay usable: they are about ids, offsets and layout); in the two tests, replace the real line with a made-up one and keep the assertions on ids and lengths |
| 4 | **No licence file**: our own code (about 90 % of the repository) has no licence, so legally nobody may copy, build or redistribute it | repository root | without a LICENSE the project is "all rights reserved" by default; GitHub shows "no license" | add `LICENSE` with **MPL-2.0** (recommended, section 3.1) |
| 5 | **Third-party notices incomplete** for the release zip: the MIT notices for the .NET runtime / WPF, Silk.NET, SDL3-CS and FFmpeg.AutoGen (all ship as DLLs) are missing from `docs/THIRD_PARTY.md`; and the two licence texts in the zip (`FFmpeg-LGPL.txt`, `knife-MPL-2.0.txt`) are copied from **git-ignored folders** (`third_party/`, `ref/`), so a build from a fresh clone would silently leave them out | `docs/THIRD_PARTY.md`, `src/Tsuki.Launcher/Tsuki.Launcher.csproj` lines 52-53 | MIT asks for its notice to travel with the binaries; the LGPL text must be in the zip | add the four MIT notices to `THIRD_PARTY.md`; put the LGPL-3.0 and MPL-2.0 texts into `src/Tsuki.Launcher/Licenses/` and copy from there |

**Found and judged acceptable** (section 2 has every item): short interface strings (the quit question, the
save prompt, menu labels); the names of the 23 characters and the chapter titles; tables of script command
names, addresses and counts read from the game program (facts, not content); the Sony system-function name
table `tools/eboot/nid_db.tsv`; the three generated test tables (synthetic data, their headers say so); the
port's own icon (original art, drawn by `tools/setup/make_icon.py`); the machine specifications (CPU, GPU, 64
GB) in the status reports; the development notes that say the port was built with AI coding agents.

**Decisions only you can make** (section 5): the licence (MPL-2.0 / MIT / GPL-3.0), fresh history or
rewritten history, the name and e-mail on the commits, whether to keep the "built with AI agents" notes, the
repository name, whether the research documents go public at all, the version number of the first release.

---

## 1. What to upload

### 1.1 The source repository: what is in it and why it is safe

| what | count | what it is |
|---|---|---|
| C# source (`src/`, `tests/`) | 649 files, 147,650 lines | the engine, the game program, the launcher and their tests; 25 files are translations of knife (MPL-2.0, section 3.2) |
| Python (`tools/`) | 127 files, 61,568 lines | the research readers and tests that run against a player's own copy |
| documents (`docs/`, `README.md`) | 96 files, 33,468 lines | format descriptions, design, status reports |
| project files | 17 `.csproj`, 13 `.xaml`, `Tsuki.slnx`, 2 `.props`, 1 `app.manifest`, 5 `.ps1` setup scripts | build and setup |
| the port's icon (`assets/icon/`) | 1 `.ico`, 4 `.png`, 1 `.rgba` | **original art** (a crescent on an indigo plate), drawn by `tools/setup/make_icon.py` from nothing but Pillow; package K22 checked the committed and the embedded copies pixel by pixel |
| data files | `tools/eboot/nid_db.tsv` (338 lines: Sony system-function names and their hashes), `tests/Tsuki.Tests.Data/EbootTables/engine_tables_digests.json` (SHA-256 checksums of the tables the game reads from the player's `eboot.bin`), `.claude/settings.json` (an editor hook) | no game content: names of system functions, checksums, a tool setting |

Why it is safe to publish (after the five fixes): no file of a game-data type was ever committed (no `.mrg`,
`.hed`, `.nam`, `.mzp`, `.mp4`, `.at9`, `.sfo`, `.bin`, no fonts, no audio, no pictures other than the six icon
files); the largest file ever committed is 327 KB (a generated table of synthetic test cases); the project's
own pre-commit guard (`tools/setup/hooks/precommit_check.py`, installed as `.git/hooks/pre-commit`) refuses
`samples/`, `scratch/`, `ref/`, `third_party/`, `.venv/`, every game-data file type, any file over 5 MiB and any
folder outside its allow-list; the whole history has only one path that is not in the current version (a
file that was moved). The published programs were also searched for your user name: not present.

### 1.2 The player download: a GitHub Release attachment

GitHub keeps big binary downloads out of the repository and attaches them to a *Release* (a tagged version
with release notes and files). The release zip is the published folder, built with the command the README
gives (`dotnet publish src/Tsuki.Launcher -c Release -r win-x64 --self-contained -o <folder>`), then zipped.
What the last such folder contained (`scratch/k22/pub`, built by package K22 from the same code as commit
`f74a533`):

| in the zip | what | licence |
|---|---|---|
| `Tsukihime.exe`, `Tsukihime.dll`, `Tsuki.Data/Engine/Media/Render/Platform/Ui.dll`, `TsukihimeLauncher.exe`, `TsukihimeLauncher.dll`, `*.deps.json`, `*.runtimeconfig.json` | the port | ours (section 3.1) + MPL-2.0 for the knife-derived parts |
| about 230 `System.*`, `Microsoft.*`, `Presentation*`, `WindowsBase.dll`, `coreclr.dll`, `msquic.dll`, ... | the .NET 10 runtime and WPF, self-contained so players install nothing | MIT (Microsoft) |
| `SDL3.dll` | window, input, audio device | zlib |
| `SDL3-CS.dll` | the C# binding of SDL3 (ppy) | MIT |
| `Silk.NET.OpenGL.dll`, `Silk.NET.Core.dll`, `Silk.NET.Maths.dll` | the OpenGL binding | MIT |
| `FFmpeg.AutoGen.dll` | the C# binding of FFmpeg | MIT (checked in the package's own `LICENSE.txt`: "MIT License, Copyright (c) 2025 Ruslan Balanukhin") |
| `avcodec-63.dll`, `avformat-63.dll`, `avutil-61.dll`, `swresample-7.dll` | FFmpeg 9.0.1, unmodified BtbN LGPL build | LGPL v3 or later (section 3.3) |
| `licenses\FFmpeg-LGPL.txt`, `FFmpeg-SOURCE.txt`, `knife-MPL-2.0.txt`, `SDL3-zlib.txt`, `THIRD_PARTY.md` | the notices | |
| `README.md` | the README | |
| 8 `*.pdb` files (686 KB) | debug symbols; not needed by players | recommend leaving them out (`-p:DebugType=none` on the publish) |

Measured: 279 files, 252,237,892 bytes; `Compress-Archive` made a 110,376,894-byte zip (105.3 MB) in 6 s.
GitHub allows release files up to 2 GB, so one zip is fine. Suggested name:
`Tsukihime-PC-port-v0.1.0-win-x64.zip`. **No game data is in it**: the game's art, text, sound and the
Arcueid icon are read from, or built from, the player's own folder at run time (README, "Window and icon").

### 1.3 What never goes up

| never upload | why | where it is kept out |
|---|---|---|
| the game folder `CUSA47999` (44 GB: `eboot.bin`, `image/`, `data/`, `sce_sys/`) and the `CUSA47999-patch` folder | copyrighted; also far too big | outside the project; the guard hooks treat it as read-only |
| `samples/` | pictures, text, audio, tables **decoded from the game**; copyrighted | `.gitignore`, pre-commit guard |
| `scratch/` | working folders with screenshots of the game, decoded data, logs, test profiles | `.gitignore`, pre-commit guard |
| `artifacts/` | build output (including `artifacts/dist`, earlier published builds) | `.gitignore` |
| `ref/` (knife, mangetsu, deepLuna) | other people's projects; link to them (`docs/THIRD_PARTY.md` has the URLs) | `.gitignore`, pre-commit guard |
| `third_party/` | the downloaded FFmpeg DLLs (they go into the release zip, not into the repository) | `.gitignore`, pre-commit guard |
| `.venv/` | the Python environment | `.gitignore` |
| `%APPDATA%\Tsuki` (`save.dat`, `settings.ini`, `game_dir.txt`, `icons\tsukihime.ico`, `backups\`) | your saves, your settings, the icon built from your files | outside the project |
| `%APPDATA%\shadPS4` | the emulator's folder and its save | outside the project |
| any screenshot of the game, any decoded picture or sound, any text dump | copyrighted | do not attach them to issues, release notes or the README either |
| a **personal build** (`PERSONAL_BUILD.txt` beside the programs, the Arcueid icon on the `.exe` files) | her art is TYPE-MOON's, cut from your own files | section 1.4: build releases without `-p:TsukiPersonalIcon`, run `Tsuki.Cli release-check` on the folder before zipping |

### 1.4 Personal builds never go out (package K28)

The port shows Arcueid's face on the game window, the taskbar and the shortcuts by building `%APPDATA%\Tsuki\icons\tsukihime.ico`
from the player's own files at run time; the programs themselves carry only the crescent. For the one machine that owns the game,
`-p:TsukiPersonalIcon=<game folder>` on a build or publish puts her face into the icon resources of `Tsukihime.exe` and
`TsukihimeLauncher.exe` as well (README, "Personal build"). Such a build is game art inside a binary and must never be uploaded,
attached or passed on. Three marks make that checkable, and one command checks them:

| mark | where | written by |
|---|---|---|
| `PERSONAL_BUILD.txt` (says what the folder is and that it must not be shared) | beside the programs, in the build output and in a publish folder | `src/Tsuki.PersonalIcon.targets` (copied as a `None` item; a later build without the property into the same folder removes it through IncrementalClean) |
| `[assembly: AssemblyMetadata("TsukiPersonalBuild", "true")]` and the informational version `1.0.0-personal+<commit>` | `Tsukihime.dll`, `TsukihimeLauncher.dll` (the game's log prints `personal build: ... must not be shared`) | the same targets file, through the SDK's assembly-info generation |
| the icon group 32512 of the `.exe` is not `assets/icon/tsukihime.ico` | the apphost's resources | the C# compiler's `/win32icon` from the `.ico` the build wrote into `obj/` (git-ignored) |

**The release step:** build the publish folder **without** the property (the command of section 1.2 as it stands), then

```
dotnet run --project src/Tsuki.Cli -c Release -- release-check <publish folder>
```

prints `ok` lines for both programs (the crescent icon group, no personal mark) and `RESULT: ... may be released`, exit 0; any mark
gives `REFUSED ...` lines and exit 1, as does a folder without `Tsukihime.exe` (so a wrong path is a failure, not a pass).
`--dump <dir>` writes PNGs of the icon groups the executables really carry, for a look. The check reads the files with the port's own
code (`src/Tsuki.Data/Icons/PeIcons.cs`, no Win32 call); `tests/Tsuki.Tests.Data/Cli/ReleaseCheckTests.cs` covers every rule on
copies of the built programs. The step belongs in 4.1 step 6 before the zip is made.

---

## 2. The audit: every hit, with its fix

Scope: the current version (924 tracked files) **and the whole history** (`git log --all`: 19 commits on one
branch, no tags, no stashes, no other refs; `git rev-list --objects --all`: 1,858 objects, 5.1 MB). Searched
for: game content (text lines, pictures, fonts, audio, movies, tables copied from `eboot.bin`, fixtures that
embed game data), personal data (Windows user name and paths, e-mail, machine names, other files or games of
yours), secrets, and large files. Risk: **high** = must not go public; **medium** = fix before public; **low**
= acceptable, your call; **ok** = acceptable as is, with the reason.

### 2.1 Personal data

| # | where (current version) | in history | what it is | risk | fix |
|---|---|---|---|---|---|
| P1 | `tools/tsuki/__init__.py` 24-25 (`DEFAULT_GAME_DIR`) | all 19 commits | your full game path `<user profile folder>/<account>/Downloads/<extractor tool>/.../CUSA47999` as the default when `TSUKI_GAME_DIR` is not set | high | replace with `D:/Games/CUSA47999` (a placeholder nobody has) or `None`; the README already tells people to set `TSUKI_GAME_DIR` |
| P2 | `tools/setup/hooks/guard_paths.py` 28-29 (and `ROOT = "d:/tsukihimeport"` line 14) | all 19 | same path as the fallback of the editor guard; the project root hard-coded | high | same placeholder; derive `ROOT` from the script's own location |
| P3 | `tools/setup/setup_common.ps1` 16 (`$TsukiDefaultGameDir`) | 18 commits | same path | high | same placeholder |
| P4 | `docs/ROADMAP.md` 512 (`setx TSUKI_GAME_DIR "..."`) | all 19 | same path in a setup example | high | `setx TSUKI_GAME_DIR "D:\Games\CUSA47999"` |
| P5 | `tests/Tsuki.Tests.Data/GameData.cs` 13, `tests/Tsuki.Tests.Render/Compositor/Support.cs` 246 | 17 and 15 commits | same path as the test fallback | high | same placeholder (tests already prefer `TSUKI_GAME_DIR`) |
| P6 | `src/Tsuki.Cli/GameFolderLocator.cs` 8; `src/Spikes/S1.AudioLoop/GameFiles.cs` 11, `S2.MovieSync/Program.cs` 18, `S4.BigPicture/Game.cs` 7 | 17 commits each | same path in the dev command-line tool and three feasibility spikes (all in `Tsuki.slnx`, none in the release) | high | same placeholder; optionally remove the four spike projects from the solution (they were M0 experiments) |
| P7 | `docs/status/wave2/H3a.md` 47 (`%TEMP%/pytest-of-<account>`) | since 9bc8706 | your Windows account name | medium | `pytest-of-<user>` |
| P8 | `docs/research/engine_lineage.md` 437 (a stray `D:\...html` file name) | since bf6d249 | a stray file on your disk, mentioned in a research note | low | delete the sentence |
| P9 | git metadata: author and committer `<private name> <private e-mail address>` on all 19 commits | all 19 | your e-mail address (it is in **no** file; `git grep` over every commit finds neither the address nor the name) | high | choose the public identity first (section 5); then either publish with a fresh history (section 4.2, recommended) or `git filter-repo --mailmap` |
| P10 | commit messages: `Co-Authored-By: Claude Opus 5.5 ...`, "Workflow on Fable: ...", "stopped at the weekly usage limit" | 19 commits | they say the port was written with AI coding agents and name the subscription limit | low (your call) | fresh history drops them; or `git filter-repo --replace-message`; or keep them (honest, and common) |
| P11 | machine specifications: `AMD Ryzen 7 5800X3D`, `AMD Radeon RX 9070 XT`, driver version, `64 GB`, `2560 x 1440 @ 144 Hz`, "D: has about 870 GB" | `docs/status/M0.md`, `spikes.md`, `spikes/S1..S4.md`, `wave2/A1.md`, `A2.md`, `D1.md`, `knife/K1a.md`, `R1-app.md`, `design/strategy_renpy.md` 471 | the test machine's hardware, as context for timings | ok | thousands of projects state their benchmark machine; nothing identifies you. Leave, or trim the disk-size sentence |
| P12 | mentions of other games or files of yours | none found: the only other titles named are other HuneX games in the public engine-lineage research (Witch on the Holy Night, Mahoyo, DRAMAtical Murder, Hana Awase), with public sources | ok | nothing to do |
| P13 | the machine name (`hostname`) | not in any tracked file or commit | ok | nothing to do |
| P14 | `.claude/settings.json` (tracked): a hook with the absolute paths `D:/TsukihimePort/.venv/Scripts/python.exe` and `D:/TsukihimePort/tools/setup/hooks/guard_paths.py` | 17 commits | the drive and folder of your project; the hook breaks on anyone else's machine | low | use `$CLAUDE_PROJECT_DIR/...` in the hook, or stop tracking `.claude/` |

### 2.2 Game content

| # | where | what it is | risk | fix |
|---|---|---|---|---|
| G1 | `docs/formats/text.md` 403-414 | a table of **12 full story lines** (the prologue caption pictures), Japanese and English columns, with their ids and voice names | **high** (the largest quote in the repository) | keep the table but drop the two text columns, or cut each cell to the id plus its first two or three characters; the column "line id" carries the information |
| G2 | `docs/formats/text.md` 119-122, 421, 611-612; `docs/FORMATS.md` 642; `docs/formats/script.md` 736; `docs/RUNTIME_REQUIREMENTS.md` 1071-1072; `docs/RUNTIME_REQUIREMENTS.draft.md` 29 | the opening line (script_text id 3) quoted in full, in up to four languages, as the worked example | medium | one place may keep "id 3 is the fourth message of script 00_02"; cut every quotation to its first two or three characters plus the byte length |
| G3 | `docs/formats/text.md` 109-111 | two hex-dump lines that spell out the first words of strings 0 and 1 (ja and en) | medium | keep the offsets, replace the decoded words with `...` |
| G4 | `docs/formats/system_flow.md` 681 | the three texts of the first choice, English and Japanese | medium | cut to "1. ..." / the ids |
| G5 | `docs/formats/fonts.md` 673 and `tools/oracle/textbox.py` 228 | one full choice line (Japanese), as a layout example and as a test input | medium | docs: id + first characters; test: replace with a made-up line of the same length and shape (two numbered items, a caret, an `@t`) and regenerate the expected values |
| G6 | `tools/tests/test_text.py` 272-275 | the opening line asserted in full (ja) and its first words (zc, zt) | medium | assert the id, the byte length, the ruby positions and the first two characters instead |
| G7 | `docs/status/knife/K6.md` 93, `K16.md` 37 | one story sentence each, copied from a save-slot "notes" field in a test log | low | cut to the first three words |
| G8 | `docs/formats/text.md` 235 | a fragment of an editor's note left in the data (not story text) | low | cut to a few characters |
| G9 | `docs/formats/engine.md` 1157-1158 (23 character names), 1283-1284 (two chapter titles, one episode title), `sound_runtime.md` 596-603, `audio.md` 256-257, `archives_ui_movies.md` 526, 573, `gallery.md` 142, 558, `images.md` 514; `README.md` 90, 102, 106; `tools/tests/test_eboot.py` 409, 412; `src/Tsuki.Ui/Screens/TitleScreen.cs` 571 | **interface strings and names**: the quit question, the save prompt, the autosave notice, menu option help lines, the voice-setting names, the end card, the language names | ok | names and functional interface strings are facts about the program, a few words each; every port and wiki quotes them. Leave |
| G10 | `src/Tsuki.Ui/Framework/PortText.cs` (51 lines), `UiPrompts.cs` (30), `UiDialogButtons.cs`, `src/Tsuki.Data/GameTitles.cs`, `src/Tsuki.Launcher/Core/LauncherProfile.cs`, the tests of these | **our own** Japanese and Chinese strings (the port's folder prompt, its config rows, Yes / No, the fallback window titles, the language names) | ok | written by the port, not taken from the game |
| G11 | `docs/formats/engine.md` 640-764 (114-row command table: names, addresses, callees, use counts), `script.md` command table, `adv_state.md`, `vm_variables.md`, `layers.md`, `compositing.md`, the eboot addresses throughout | **facts and addresses** read from the game program: command names, where each handler is, how often each is used, byte layouts | ok | this is the documentation of a format and a program's behaviour, the thing the project exists for. No table of game *content* (no string table, no image list beyond identifiers, no glyph bitmaps) is copied. `python -m eboot.uitext` exports the UI string table, but into `samples/` on the player's machine, never into the repository |
| G12 | `tools/eboot/nid_db.tsv` (338 lines) | Sony system-function names and their hashed ids, as the game imports them | ok | names of a public SDK's functions, published in every open-source PS4 project; not game content |
| G13 | `tests/Tsuki.Tests.Data/EbootTables/engine_tables_digests.json` | SHA-256 checksums of the 30-odd tables the port reads from the player's `eboot.bin` | ok | a checksum reveals nothing of the table |
| G14 | `tests/Tsuki.Tests.Synthetic/Stage/StageOracleCases.g.cs` (327 KB, the largest file ever committed), `Vm/OracleReaderCases.g.cs` (157 KB), `tests/Tsuki.Tests.Data/Ui/GalleryOracleExpected.cs` (53 KB) | generated test tables; the first two say in their header "synthetic inputs only, no game data" (random data with a fixed seed), the third is lists of index numbers | ok | checked the headers and the contents: numbers and made-up picture names (`IMG0001`, `BOY_A_00_00_N01`) |
| G15 | `assets/icon/*` | the port's icon | ok | original art (section 1.1) |
| G16 | `tools/oracle/textbox.py` 247, 251 | twenty single kanji from the game's vocabulary and two names with their readings, used to build random test strings | ok | single characters and names |
| G17 | code ported from the reference projects | knife: 25 C# files with the MPL header (section 3.2). mangetsu and deepLuna: cited in comments of `tools/tsuki/mrg.py`, `mzp.py`, `mzx.py`, `nam.py`, `text.py`, `src/Tsuki.Data/Text/TextTable.cs` and two tests as the source of a format fact, no code copied (the README says so; the grep for Rust or C++ snippets in `docs/` found none) | ok | keep the citations |

### 2.3 Secrets

Searched every tracked file for API keys, tokens, passwords, private keys and cloud credentials (section 6,
command 9): **none**. The only hits are the words "token" (the script tokenizer, `CancellationToken`) and
"credits" (the game's staff-credit movies). There is no `.env`, no `NuGet.config` with a feed password, no
certificate.

### 2.4 Large files and the history

- Biggest objects ever committed: `StageOracleCases.g.cs` 326,807 bytes, `assets/icon/tsukihime.ico`
  180,462, `OracleReaderCases.g.cs` 157,354, then documents and source of 50-125 KB. Nothing over the
  guard's 5 MiB limit; the whole `.git` is 5.1 MB (3.84 MiB in packs, 1.22 MiB loose). GitHub's soft limit is
  1 GB per repository and 100 MB per file.
- Paths ever tracked that are not in the current version: exactly one, `src/Tsuki.Launcher/Core/ParamSfo.cs`
  (moved to `src/Tsuki.Data` in K20). So "what is in the history" = "what is in the current version" plus
  older versions of the same files. No deleted game dump hides in an old commit.
- The Japanese/Chinese text in the history grows from 91 lines (first commit) to 336 (now) and is made of the
  items above (G1-G10); the personal path is in every commit (P1-P6). **Both reach every commit, which is why
  the history must be rewritten or replaced** (section 4), not just the current files.

### 2.5 Housekeeping found on the way (not blocking)

- `Directory.Packages.props` pins `Iced` 1.21.0, which no project references. Remove the line or leave it.
- `src/Tsuki.Launcher/Tsuki.Launcher.csproj` 52-53 copy the LGPL and MPL texts from `third_party/` and
  `ref/` with `Condition="Exists(...)"`: present on your machine (checked), silently absent on a clean clone.
  Fix in finding 5.
- The `.pdb` files in the publish (finding 5, table 1.2).
- `docs/THIRD_PARTY.md` says FFmpeg.AutoGen is MIT: **correct** (verified against the package's licence file;
  the project's older releases were LGPL, the 9.x packages are MIT).

---

## 3. Licensing

### 3.1 A licence for our own code: recommend MPL-2.0

The port is about 150,000 lines of our own C# and 60,000 lines of our own Python, plus 25 C# files that are
translations of knife and therefore **must stay under the Mozilla Public License 2.0** (MPL-2.0 is
"file-level copyleft": the licence travels with those files, whatever the rest of the project uses).

| option | what it means for others | for you | fit with the knife files |
|---|---|---|---|
| **MPL-2.0 (recommended)** | anyone may use, change and redistribute; whoever distributes a *changed version of one of our files* must publish that file's source; they may combine it with closed code | **one licence for the whole repository**: `LICENSE` at the root, no per-file bookkeeping beyond what exists; the knife files keep their header and the table in `THIRD_PARTY.md` names them as a courtesy | perfect: the same licence as the files you cannot relicense anyway |
| MIT | anyone may do anything, including closing the source, as long as the copyright line stays | the simplest text; but the repository then has **two** licences (MIT + MPL for 25 files) and the table must be kept exact forever (a test already enforces it) | fine, two licences side by side |
| GPL-3.0 | anyone who distributes the program or a derivative must publish all of its source | the strongest protection against someone selling a closed copy of the port | allowed: knife's files carry no "Incompatible With Secondary Licenses" notice (checked), so MPL-2.0 lets them be combined into a GPL work; the FFmpeg LGPL and the MIT/zlib libraries are compatible |

Why MPL-2.0 first: it is recognised by GitHub (shows in the sidebar), approved by OSI and the FSF, used by
Firefox and many game-engine reimplementations, compatible with the GPL community (the shadPS4 world) through
its secondary-licence clause, and it keeps improvements to the port public without telling anyone what to do
with their own code. It is also what knife chose, and a large part of the menu code is knife's work in
another language.

What the lead agent adds for MPL-2.0: the root `LICENSE` file (the MPL-2.0 text, 373 lines), a "Licence"
paragraph in the README, and a `SPDX-License-Identifier: MPL-2.0` line at the top of `Directory.Build.props`
(so every assembly carries it; optional). A header in each of our files is *not* required by the MPL when a
`LICENSE` file is where a reader would look.

### 3.2 The knife-derived files stay MPL-2.0

The 25 files in the table of `docs/THIRD_PARTY.md` (checked: every file with the MPL header is in the table
and every table row has the header; the test `ThirdPartyNoticesTests` enforces this). Obligations, all met:
keep the header (done), make the source available (the repository), ship the licence text with binaries
(`licenses\knife-MPL-2.0.txt` in the zip: fix its source folder, finding 5). Also credit AuroraWright in the
README (section 3.6).

### 3.3 FFmpeg (LGPL v3 or later): what the release zip must do

The four DLLs are the unmodified BtbN "win64-lgpl-shared" build of FFmpeg 9.0.1 (`--enable-version3`, so
LGPL **v3**, no GPL component). The port loads them at run time from its own folder and never links against
them; a player can swap in another build of the same major versions. LGPL v3 section 4 for a "Combined Work":

| obligation | status |
|---|---|
| give prominent notice that the library is used and covered by the LGPL | done: `licenses\FFmpeg-SOURCE.txt`, `THIRD_PARTY.md`, the launcher's Help page |
| include the LGPL text | done, but see finding 5: `FFmpeg-LGPL.txt` is copied from the git-ignored `third_party/` folder; put a copy in `src/Tsuki.Launcher/Licenses/`. K31 (round 2) added the GPL v3 text beside it as `licenses\FFmpeg-GPL-3.0.txt`, because the LGPL v3 is a set of additional permissions on top of the GPL v3 and incorporates its terms (7.1) |
| let the user replace the library: "a suitable shared library mechanism" (4 d 1) | done: dynamic loading of standard DLLs, no static linking |
| the library's source: make it available for the exact version shipped | **partly**: `FFmpeg-SOURCE.txt` points at BtbN's release page and the FFmpeg tag `n9.0.1`. Links die; the safe habit is to **attach the matching source archive to the GitHub Release as a second file** (BtbN publishes one per build; ~15-20 MB) or keep a private copy you can hand out on request |
| no GPL component, no modification claims, no "endorsed by FFmpeg" | done (`Tsukihime.exe --smoke` prints `LGPL version 3 or later`; the build has no `postproc`) |

### 3.4 SDL3, the NuGet packages and the .NET runtime

| component | ships in the zip | licence | notice needed in `THIRD_PARTY.md` |
|---|---|---|---|
| SDL3 3.5.0 (`SDL3.dll`) | yes | zlib | none required for binaries; `SDL3-zlib.txt` is shipped anyway (fine) |
| ppy.SDL3-CS 2026.1002.1 (`SDL3-CS.dll`) | yes | MIT | **add** the copyright + permission notice |
| Silk.NET.OpenGL / Core / Maths 2.23.0 | yes | MIT | **add** |
| FFmpeg.AutoGen 9.0.1.1 (`FFmpeg.AutoGen.dll`) | yes | MIT | **add** |
| .NET 10 runtime and WPF (`System.*`, `Presentation*`, `coreclr.dll`, `msquic.dll`, ...) | yes (self-contained) | MIT (Microsoft; `https://github.com/dotnet/runtime/blob/main/LICENSE.TXT`, `dotnet/wpf`) | **add** one paragraph |
| Iced 1.21.0 | no (pinned, unused) | MIT | none |
| xunit.v3 4.0.2, xunit.runner.visualstudio 4.0.1 (Apache-2.0), Microsoft.NET.Test.Sdk 18.11.0 (MIT) | no (tests only) | | optional one line |

MIT's only condition is that its copyright line and permission notice travel with the copies. One
`THIRD_PARTY.md` section per package (name, version, URL, the notice text) satisfies it, and the zip already
carries `licenses\THIRD_PARTY.md`.

### 3.5 The disclaimer

Already present in the launcher's Help page (`HelpPage.xaml` line 32) and the README's "Legal note". Proposed
wording for the README's first screen, the release notes and the Help page (keep all three identical):

> **Tsukihime PC port is an unofficial fan project.** It is not affiliated with, endorsed by or supported by
> TYPE-MOON, Aniplex, HuneX or Sony. TSUKIHIME and all of its art, music, text and characters belong to
> their rights holders. This program contains **no game data**: it only plays the files of a copy of
> *TSUKIHIME -A piece of blue glass moon-* (PS4) that you own. You must supply those files yourself; this
> project does not provide the files or link to any source of them, and it will not accept
> contributions that do.

The last sentence matters: the README should describe the folder the program needs (`eboot.bin`, `image/`,
`data/`, `sce_sys/`) and stop there. (This is a careful default, not legal advice; nobody on this project is a
lawyer.)

### 3.6 What the README's first screen should say

The current README is a developer document (300 lines, starts with format research). The public one should
open with, in this order, all visible without scrolling:

1. **Title and one sentence**: "Tsukihime PC port: a native Windows version of *TSUKIHIME -A piece of blue
   glass moon-* (PS4) that plays the files of your own copy. Fan-made, open source."
2. **The disclaimer box** (3.5).
3. **What you need**: Windows 10/11 64-bit; a graphics card with OpenGL 3.3; your own extracted PS4 game
   folder (`CUSA47999`, 44 GB, with `eboot.bin`, `image\`, `data\`, `sce_sys\`); about 300 MB for the port.
4. **Download and play in three steps**: download the zip from *Releases*, unzip anywhere, start
   `TsukihimeLauncher.exe`, point it at the game folder, PLAY (or `Tsukihime.exe` directly, which asks for the
   folder on the first start).
5. **What it does**: the whole game in Japanese, English and both Chinese variants; saves, backlog, rewind,
   flowchart, gallery; keyboard, mouse, PlayStation and Xbox pads; windowed or fullscreen; screenshots;
   text-hooker output for dictionary tools; import of a PS4 / shadPS4 save.
6. **Status**: "playable end to end; first public build; report problems in Issues (never attach game files
   or screenshots of the game)".
7. **Credits and licences**: knife by AuroraWright (MPL-2.0; the menu code is a translation of it), FFmpeg
   (LGPL v3), SDL3 (zlib), the .NET runtime, Silk.NET, SDL3-CS, FFmpeg.AutoGen (MIT); "built with the help of
   AI coding agents" if you choose to say so (section 5).
8. **Licence line**: "Source code: MPL-2.0 (see LICENSE). Files translated from knife remain under MPL-2.0 and
   are listed in docs/THIRD_PARTY.md."
9. Then the developer material: building from source, the command-line switches, the repository layout, and a
   link to `docs/FORMATS.md` for the research.

---

## 4. Step by step

### 4.1 What the lead agent prepares (you review, nothing goes online yet)

1. **Scrub the current files** (findings 1 and 3, items P1-P8, G1-G8, P14): the path placeholder in the ten
   files, the account name, the stray file name, the forty text quotes, the hook paths. Re-run the three test
   suites and the Python tests (the two changed tests get new made-up inputs).
2. **Licensing** (findings 4 and 5): `LICENSE` (MPL-2.0 unless you choose otherwise), the four MIT notices and
   the .NET paragraph in `docs/THIRD_PARTY.md`, the LGPL-3.0 and MPL-2.0 texts copied into
   `src/Tsuki.Launcher/Licenses/` and the two csproj lines pointed there, the `ThirdPartyNoticesTests`
   extended to the new sections, `.pdb` files dropped from the publish.
3. **The public README** (3.6) and a short `CONTRIBUTING` note ("no game data in issues or pull requests").
4. **A clean public copy of the repository** with a fresh history: a new folder (for example
   `D:\TsukihimePort-public`) that holds the scrubbed files as **one commit**, "First public release", under
   the name and e-mail you chose. Your private repository with the 19 commits stays where it is. Why a fresh
   history rather than rewriting the 19 commits: every commit carries the path and the e-mail, the quotes are
   in every version of `text.md`, and the history is five days old; one commit is checked in one pass
   (`git grep` over a single tree) and leaves nothing to miss. If you prefer to keep the 19 commits public,
   the agent runs `git filter-repo` instead on a fresh clone with `--replace-text` (the path in both slash
   forms, the account name, each quoted line), `--mailmap` (the e-mail) and `--replace-message` (the Co-Authored-By
   lines, if you want them gone), then verifies with `git grep -i <user folder name> $(git rev-list --all)` and
   `git log --format='%an %ae %cn %ce' | sort -u`.
5. **Verification before anything is uploaded**, written into this file's section 7: no user folder name, no account name,
   no e-mail local part and no e-mail provider name in any file or commit of the public copy; the Japanese/Chinese line count equals the
   count of the port's own strings (G10) plus the interface strings (G9); build `-warnaserror` clean from
   the public copy on a clean clone; all tests pass; the publish from the public copy contains the five
   licence files; `grep` of the zip's contents for the same four words finds nothing.
6. **The release zip** from the public copy (`dotnet publish ... -p:DebugType=none`, **never with
   `-p:TsukiPersonalIcon`**), then `Tsuki.Cli release-check <publish folder>` must print `may be released`
   (section 1.4), then zipped, plus the release notes (what it is, the disclaimer, what you need, the key list,
   known issues, the licences, the FFmpeg source link and file).

The agent does **not** push, create the repository or the release: those are your clicks (4.2), so that no
automated step can publish before you have looked.

### 4.2 What you click

**A. Create the GitHub repository (private first).**

1. Sign in at github.com (create an account if you have none; turn on two-factor authentication under
   *Settings > Password and authentication*). Decide the user name that will be public; the repository is
   shown as `github.com/<your-name>/<repo-name>`.
2. *Settings > Emails*: tick **"Keep my email addresses private"** and **"Block command line pushes that
   expose my email"**. GitHub shows your private address of the form `12345678+name@users.noreply.github.com`;
   that is the address to use on the commits (tell the agent).
3. Top-right **+** > **New repository**: name (suggestion: `tsukihime-pc-port`), one-line description,
   **Private**, and leave "Add a README", "Add .gitignore" and "Choose a license" **unticked** (the agent's
   copy brings its own). Click **Create repository**.

**B. Upload the source (the clean copy from 4.1 step 4).**

The easiest tool for a non-developer is **GitHub Desktop** (free, from desktop.github.com): *File > Add local
repository* > choose `D:\TsukihimePort-public` > the big **Publish repository** button > untick nothing, keep
**"Keep this code private"** ticked > **Publish**. Done: the repository page shows the files and the one
commit. (Alternative: the agent gives you three command lines for a terminal: `git remote add origin <url>`,
`git push -u origin main`; the first push asks you to sign in in a browser window.)

**C. Look at it while it is private.** Open the repository in the browser: the README renders on the front
page; click through `docs/`; check *Insights > Community standards* (licence detected, README present); check
the commit's author shows the name you chose. Anything wrong: tell the agent, it fixes the public copy, you
push again (GitHub Desktop: **Push origin**).

**D. Create the release with the zip.**

1. Repository page > right column **Releases** > **Create a new release** (or **Draft a new release**).
2. **Choose a tag** > type `v0.1.0` > "Create new tag on publish". Title: `Tsukihime PC port v0.1.0`.
3. Paste the release notes the agent wrote. Tick **"Set as a pre-release"** if you want to call the first
   build a beta.
4. Drag the zip into the **Attach binaries** box and wait for the upload (105 MB; a minute or two). The
   agent downloads nothing (decision D8): the release notes and `licenses\FFmpeg-SOURCE.txt` point at the
   exact upstream source (FFmpeg tag `n9.0.1`, BtbN build `ffmpeg-n9.0.1-11-ge47273f4d9-win64-lgpl-shared-9.0`
   on https://github.com/BtbN/FFmpeg-Builds/releases) and say that a copy is available on request. If you
   want the LGPL point to depend on no external link, download that build's source archive from the BtbN
   release page yourself and drag it in as a second file.
5. **Publish release.** While the repository is private, only you can see it.

**E. Make it public, when you are satisfied.** *Settings > General* > scroll to the **Danger Zone** >
**Change visibility** > **Make public** > type the repository name to confirm. The README, the code and the
release become visible to everyone at once. From then on, never commit anything from `samples/` or
`scratch/`, and never attach game screenshots to issues (the pre-commit guard still runs locally, but the web
upload has no guard).

**F. Afterwards (good habits).** *Settings > General > Features*: keep **Issues** on, consider turning
**Discussions** on; *Settings > Moderation*: you can limit who may comment. Add the repository link to the
launcher's Help page in the next build. When you publish a new build: a new tag (`v0.1.1`), a new release, a
new zip; the source goes up with ordinary pushes.

### 4.3 If the worst happens

- **Something private got pushed**: making the repository private again hides it from new visitors, but
  GitHub caches and forks keep it; the only real fix is to rewrite the history (the agent) and, for a secret,
  to change the secret. Hence "private first, look, then public".
- **A takedown notice (DMCA) arrives**: GitHub forwards it; the project's position is that it contains no
  game data, requires the player's own copy and is a clean-room reimplementation (like the many open-source
  engine reimplementations on GitHub). Keep this file and `docs/THIRD_PARTY.md` as the record of what was
  checked. Not legal advice.

---

## 5. What still needs your decision

| # | decision | recommendation | why it is yours |
|---|---|---|---|
| D1 | the licence of our code: MPL-2.0, MIT or GPL-3.0 | MPL-2.0 (3.1) | it decides what others may do with five days of work; it cannot be taken back for versions already published |
| D2 | fresh single-commit history, or the 19 commits rewritten with `git filter-repo` | fresh (4.1 step 4) | the development history is yours; it shows the pace and the method, and it also shows the agent workflow (P10) |
| D3 | the name and e-mail on the public commits | your GitHub user name with the `noreply` address | it is the one line of personal data that *must* exist |
| D4 | whether to keep the notes that the port was built with AI coding agents (32 documents mention agents, leads or Claude; the commit messages carry `Co-Authored-By: Claude`) | keep them, and say it in one README sentence: honest, and the status files are the proof of what was verified | some communities react strongly either way |
| D5 | whether the research documents (`docs/formats/`, `docs/research/`, `docs/status/`) go public at all, or only the code | publish them after the quote trimming (G1-G8); they are the most useful part for other developers | they are also the part with the most quoted material and the most work to trim |
| D6 | the repository name and the project's public name ("Tsukihime PC port" uses the game's title; many fan ports do, with the disclaimer) | `tsukihime-pc-port`, programs stay `Tsukihime.exe` / `TsukihimeLauncher.exe` | a trademark question nobody here can settle |
| D7 | the version number and label of the first release | `v0.1.0`, marked pre-release | "1.0" invites different expectations |
| D8 | whether to attach the FFmpeg source archive to the release (3.3) | yes (one extra file) | a small download cost against the only LGPL point that depends on an external link |
| D9 | whether the four spike projects and `Tsuki.Cli` stay in the repository (P6) | keep, scrubbed; they document the feasibility work | housekeeping |
| D10 | whether to publish under your own name at all, or a handle | a handle, matching D3 | the project is a fan project of a commercial game |
| D11 | whether the name of the PKG extraction tool whose update-folder suffix (`CUSA47999-patch`) the port recognises may appear in the README and in a code comment (`src/Tsuki.Data/IO/GameLayout.cs`) | K31 (round 2) replaced it with "some PKG extractors", the wording the setup guide already used (3.3 of the guide); the suffix is recognised exactly as before, nothing else changes. Put the name back if you want the tool credited | finding 1 wanted the tool unnamed; it is a public tool and a compatibility fact, not a path, so either choice is defensible and it is yours |
| D12 | whether the local clock times in the status notes (screenshot file names such as `Tsukihime_2026-10-30_21-15-02.png`, times such as "21:19" or "17:21:23" in about a dozen notes) stay | left as they are (K31, round 2): against the UTC date of the public commit they give a rough time zone and nothing more; no name, account, machine or e-mail follows from them | only you can weigh that hint against the notes' usefulness as a record; trimming them is a few minutes of work if you want it |

---

## 6. Evidence: the commands run and what they returned

All commands were run in Git Bash or PowerShell from `D:/TsukihimePort` on 2026-10-30 at commit `f74a533`.
Only `scratch/k26/release_size_probe.zip` was written (a measurement), nothing else.

1. **History shape.** `git log --all --oneline | wc -l` = 19. `git branch -a` = `main` only. `git tag` = none.
   `git stash list` = none. `git for-each-ref` = one ref. `git remote -v` = none. `git count-objects -vH`:
   442 loose objects (1.22 MiB), 1,426 in 3 packs (3.84 MiB).
2. **Authors.** `git log --all --format='%an <%ae>' | sort | uniq -c` = 19 `<private name> <private e-mail address>`;
   the committer field is the same. `git grep -i -E '<e-mail local part>' $(git rev-list --all)` = no hit;
   `git grep -n -w -i <name>` = no hit. `hostname` = the machine name; `git grep -n -i <machine name>` = no hit.
3. **Tracked files.** `git ls-files | wc -l` = 924; by extension: 649 cs, 127 py, 96 md, 17 csproj, 13 xaml,
   5 ps1, 4 png, 2 txt, 2 props, 2 json, 1 tsv, 1 slnx, 1 rgba, 1 manifest, 1 ico, 1 gitignore. Files of a
   binary or game-data type: only the six `assets/icon/*` files. Total content 12,632,621 bytes; 147,650 lines
   of C#, 61,568 of Python, 33,468 of Markdown.
4. **Largest blobs in history.** `git rev-list --objects --all | git cat-file --batch-check` sorted by size:
   `StageOracleCases.g.cs` 326,807; `tsukihime.ico` 180,462; `OracleReaderCases.g.cs` 157,354;
   `tools/oracle/export_slice.py` 123,406; then documents and source under 110 KB.
5. **Paths in history not in HEAD.** `comm -23 <(git log --all --name-only --format='' | sort -u) <(git ls-files | sort)`
   = `src/Tsuki.Launcher/Core/ParamSfo.cs` only. `git log --all --diff-filter=D --name-only` = nothing deleted.
6. **The personal path.** `git grep -l -i -E 'Users[/\\]+<folder>'` in HEAD = 10 files (P1-P6). Per commit
   (`for c in $(git rev-list --all); do git grep -l ... $c | wc -l; done`): 3 at `cd94425`, 4 at `3851949`, 9 at
   `37f2b71` and `bf6d249`, 10 in the 15 commits from `9bc8706` to `f74a533`. Paths over all commits:
   `tools/tsuki/__init__.py`, `guard_paths.py`, `docs/ROADMAP.md` 19 each; `setup_common.ps1` 18;
   `GameData.cs`, `GameFolderLocator.cs`, the three spikes 17; `Support.cs` 15.
7. **Other personal strings.** `git grep -n -i <account>` = `docs/status/wave2/H3a.md:47`. `git grep -n -o -i -E '\b[A-Z]:[/\\]...'`
   outside the project and user paths: `docs/research/engine_lineage.md:437` (a stray `D:\...html` file name),
   `docs/formats/engine.md:51` and `engine_lineage.md:77` (`F:/Projects/DEVmoonLang/...`: a build path found
   **inside the game program**, HuneX's, not yours; a fact), test placeholders (made-up user folders, since package K31 without
   the Windows user-profile prefix, and `D:\My Games`), `C:/Program Files` (tool locations). Hardware: `git grep -n -i -E 'GeForce|Radeon|RTX|Ryzen'`
   = the files in P11.
8. **The published binaries.** `grep -l -a -i -E 'Users[/\\]<folder>|<account>' scratch/k22/pub/*.{dll,exe,pdb,json}`
   = no hit. The publish is deterministic with `ContinuousIntegrationBuild` (`Directory.Build.props`), so the
   `.pdb` files carry `/_/` paths, not yours.
9. **Secrets.** `git grep -n -i -E '(api[_-]?key|secret|password|passwd|token|ghp_...|sk-...|AKIA...|BEGIN (RSA|OPENSSH|PRIVATE)|Authorization: ?Bearer)'`
   = only `token` as tokenizer / `CancellationToken` and documentation words. No `.env`, no `nuget.config`.
10. **Japanese / Chinese text.** `git grep -c -P '[\x{3040}-\x{30FF}\x{4E00}-\x{9FFF}]'` = 62 files, 336 lines
    in HEAD (the two icon binaries match by chance); per commit 91 (first four commits), 155-192 (the middle),
    336 at HEAD (K20's `PortText.cs` added 51 of our own lines). Every line was read; the classification is
    G1-G10 and G16. English story lines: `git grep -n -o -E '"[A-Z][a-z][^"]{30,}[.!?…]"' -- docs README.md`
    = 22 hits, of which 5 are story sentences (`script.md:736` and `RUNTIME_REQUIREMENTS.draft.md:29`, the
    opening line; `text.md:111`, the first words of string 0; `K6.md:93`; `K16.md:37`) and the other 17
    interface strings (G9).
11. **Tables.** Longest run of table rows in `docs/`: 114 rows, `docs/formats/engine.md` ending line 764
    (command, handler address, dispatch kind, callees, use count). Lines of pure numbers per file:
    `tools/tsuki/script_tables.py` 46 (command numbers); no C# file over 40.
12. **Generated test files.** Headers of `StageOracleCases.g.cs` ("Synthetic inputs only, no game data"),
    `OracleReaderCases.g.cs` ("Random synthetic inputs only, no game data"), `GalleryOracleExpected.cs` (index
    lists) read.
13. **Licences.** `~/.nuget/packages/*/…/*.nuspec`: `ppy.sdl3-cs` MIT, `silk.net.opengl` / `core` / `maths` MIT,
    `ffmpeg.autogen` "file LICENSE.txt" whose first line is "MIT License" (Copyright (c) 2025 Ruslan
    Balanukhin), `xunit.v3.mtp-off` and `xunit.runner.visualstudio` Apache-2.0, `microsoft.net.test.sdk` MIT;
    `iced` is not in the cache (no project references it; `git grep '<PackageReference'` lists only
    FFmpeg.AutoGen, Microsoft.NET.Test.Sdk, Silk.NET.OpenGL, ppy.SDL3-CS, xunit.v3.mtp-off,
    xunit.runner.visualstudio). `git grep -l 'Mozilla Public' -- src` = 25 `.cs` files (+ `HelpPage.xaml`,
    which only names the licence); the same 25 are the rows of `docs/THIRD_PARTY.md`. `third_party/ffmpeg/LICENSE.txt`
    first line "GNU LESSER GENERAL PUBLIC LICENSE Version 3"; `ref/knife/LICENSE` "Mozilla Public License
    Version 2.0" (293 lines); no `.rs` / `.md` / `.toml` file of knife contains "Incompatible With Secondary
    Licenses". `git ls-files | grep -i -E '^(LICENSE|COPYING|NOTICE)'` = none.
14. **The publish folder.** `scratch/k22/pub`: 279 files, 252,237,892 bytes; non-Microsoft files listed in
    table 1.2; `licenses/` holds `FFmpeg-LGPL.txt`, `FFmpeg-SOURCE.txt`, `knife-MPL-2.0.txt`, `SDL3-zlib.txt`,
    `THIRD_PARTY.md`; 8 `.pdb` files, 686,484 bytes. `Compress-Archive -CompressionLevel Optimal` into
    `scratch/k26/release_size_probe.zip`: 110,376,894 bytes in 5.9 s.
15. **Guards.** `.gitignore` excludes `.venv/`, `samples/`, `scratch/`, `ref/`, `bin/`, `obj/`, `artifacts/`,
    `third_party/`, `.claude/settings.local.json`. `.git/hooks/pre-commit` exists;
    `tools/setup/hooks/precommit_check.py` blocks `samples/ scratch/ ref/ third_party/ .venv/`, the game-data
    extensions, files over 5 MiB and paths outside `docs/ tools/ src/ tests/ .claude/ assets/` and the known
    root files.

## 7. Verification of the public copy (package K31, 2026-10-30, after two audits)

Package K31 did steps 4-6 of section 4.1 on top of K30's scrub and K33 (both uncommitted in the private working tree). An
independent audit of its first result found sixteen problems (7.1, first table); a second audit of the rebuilt result found five
more (7.1, second table). Every one was fixed at its root in the private working tree, the public copy was rebuilt as one commit and
the release was built again, last, from that final commit. Nothing was pushed, no remote and no GitHub repository were created,
nothing was downloaded, nothing was written into the game folder or under `%APPDATA%`, the private repository's history was not
touched (still 20 commits on `main`, `9a9a227` at the top, the K30 / K31 / K33 changes uncommitted). Every number below is the output
of a command run on 2026-10-30 or in the first hour of 2026-10-31 UTC; the logs are in the agent's scratch folder, not in the
repository.

### 7.1 Scrubs and fixes made on the way (private working tree, uncommitted)

Found by K31 itself before the first audit:

| where | what | fix |
|---|---|---|
| `tests/Tsuki.Tests.Data/Golden/GoldenManifestTests.cs` 255 | a test placeholder path that started with the Windows user-profile prefix | `C:/Data/someone/file.bin` (the test classifies absolute paths; any absolute path serves) |
| `docs/formats/fonts.md` 644, `docs/formats/text.md` 548 | one full English story sentence (Vlov's foreign-language line, en 6570) quoted as the example of `<` `>` in English text | cut to its first words and its length |
| this file | its own spelling of the user-profile prefix, the e-mail provider's name and the old test placeholders | neutral words |

Found by the audit of the first public copy, fixed in the first round:

| # | where | what | fix |
|---|---|---|---|
| 1 | `docs/status/knife/K22.md` 61 | the game's first story line in Japanese (the first WebSocket frame of a test log), inside a table cell | `the first frame = id 107, starts 気が, 16 characters` |
| 2 | `docs/RUNTIME_REQUIREMENTS.md` 1168, `docs/formats/script.md` 890, `tests/Tsuki.Tests.Data/Game/GamePlayTests.cs` 327 | the same line in English (id 107), quoted in full three times; K26's pattern missed quotes that start with `"I ` | `id 107, 43 characters, starts \`I wake\``; the test comment keeps only the glyph count. A wider pattern was then run over the whole tree (7.2) |
| 3 | `docs/status/knife/K15.md` 59, `tests/Tsuki.Tests.Synthetic/Game/AtXContinuationTests.cs` 9, `tests/Tsuki.Tests.Data/Game/PageLayoutOnRealScenesTests.cs` 8-9 and 31 | two story lines of `08_01_CIEL08_1` quoted as the `@x` example (one complete) | "the message starting "All right" then the one starting "With my""; the test's `StartsWith` check shortened to `@xWith my` (the test passes, 7.3) |
| 4 | `docs/status/knife/K16.md` 54 | a complete story line from a test log | `story text starts "I return to"` (the row already names scene 323 `01_00_ARC01_9B`) |
| 5 | `tools/oracle/textbox.py` 230, `tools/tests/test_font.py` 247 | Vlov's English sentence kept as a layout-oracle input (and a shortened form in a test) | a made-up sentence of the same shape and length (49 characters: quote, `<`, nine words of the same lengths, period, `>`, quote; it contains none of the game's words), the shortened made-up form in the test. The textbox oracle was re-exported (`python -m oracle.textbox export`: 351,752 layouts, 11 files, `verify: 0 problems`; `samples/golden/textbox/synthetic/cases.json` now carries the made-up sentence and K30's made-up choice line, 2,772 cases, 77 distinct texts, neither story text) and **both made-up inputs were run against the game's own `Text_Layout` under unicorn** (`scratch/textbox/verify_layout.compare`, the 9 flag/window configurations x 4 languages): **72 compares, 0 mismatches**, so the list's "unicorn-verified" label stays true. Python tests `test_font.py`, `test_textbox.py`, `test_text.py`: 58 passed |
| 6 | `docs/formats/sound_runtime.md` 582 | a sung lyric quoted | `(a sung line)` |
| 7 | this file, 7.2 and 7.4 of the first version | two parenthetical explanations of how the account name was searched, from which the name could be derived | deleted; this section rewritten |
| 8 | the release binaries | reported a commit that no longer existed (the public commit had been amended after the build) | the release is the last step: built from the final commit, after this file was committed (7.4) |
| 9 | `README.md` 24-25 | the download named differently from the release file; an `OWNER` placeholder in the Releases link | `Tsukihime-v0.1.0-win-x64.zip` (the release file's name); the link is relative (`../../releases`), which GitHub resolves to the repository's own Releases page, so no account name is needed; the placeholder comment is gone |
| 10 | README 11, `CONTRIBUTING.md` 24, `src/Tsuki.Launcher/Views/HelpPage.xaml` 32, `ThirdPartyNoticesTests` 143, this file 3.5, the setup guide 1 (both copies), the release notes | the disclaimer said the project does not "link to or explain how to obtain or extract" the files, while the guide's 3.2 explains the two routes from a copy you own | "does not provide the files or link to any source of them" everywhere; the guide's "will not help obtain them" became "does not provide game files or links to any source of them" |
| 11 | `src/Tsuki.App/Tsuki.App.csproj` 12, `src/Tsuki.Launcher/Tsuki.Launcher.csproj` 19, the guide | programs versioned 1.0.0 under a v0.1.0 pre-release | `<Version>0.1.0</Version>` in both; "version 0.1.0" in the guide (both copies) |
| 12 | README 19-21 against the release notes | 44 / 45 GB and 300 / 260 MB | 44 GB and about 260 MB in the README, the guide (both copies) and the release notes |
| 13 | about 60 cross-references in `docs/` | absolute `D:/TsukihimePort/...` paths of this machine | 139 occurrences in 51 documents made relative (`--artifacts-path artifacts/K10`, `PYTHONPATH=tools`, `samples/engine/`, `ref/knife`, `docs/formats/engine.md`), 12 bare "from `D:\TsukihimePort`" became "from the repository root"; `git grep 'D:.TsukihimePort' -- docs ':!docs/PUBLISHING.md'` = 0. This file keeps the folder names (it is the plan for this machine); the README's command examples were left as they are |
| 14 | the commit dates | the `-0400` offset showed the time zone | `GIT_AUTHOR_DATE` / `GIT_COMMITTER_DATE` given in UTC: both dates `+0000` |
| 15 | `docs/ARCHITECTURE.md` 438-442 | the original plan's locations (`%APPDATA%\TsukihimePort\settings.json`, `Saved Games`, a cache) | the locations as built (`%APPDATA%\Tsuki\save.dat`, `settings.ini`, `game_dir.txt`, `icons\`, the logs), the plan kept as a note, "no cache is written" |
| 16 | no `.gitattributes` | the index stored what each machine's `core.autocrlf` gave it | `.gitattributes` with `* text=auto` and the icon files `binary`: the index stores LF, every clone checks out as its own setting says. The three binaries' blob ids are unchanged (checked, 7.2) |

Found by the second audit (of the rebuilt public copy, commit `8a5c087`), fixed in the second round:

| # | where | what | fix |
|---|---|---|---|
| 17 | `README.md` 114, `CONTRIBUTING.md` 32-34 | `tools/setup/ffmpeg_dlls.ps1` described as a script that "downloads" the FFmpeg DLLs (README) or "installs the winget package" (CONTRIBUTING); the script's own header says it never installs anything and never runs winget | both now say what the script does: it copies the FFmpeg 9 LGPL DLLs from the installed winget package `BtbN.FFmpeg.LGPL.Shared.9.0` into `third_party/ffmpeg/bin`; if the package is missing it prints the `winget install` command and stops; the build stops with `TSUKI001` without the DLLs. `docs/THIRD_PARTY.md` 51 already said "copies" |
| 18 | `README.md` 190, `src/Tsuki.Data/IO/GameLayout.cs` 111 | the name of the PKG extraction tool whose `-patch` folder suffix the port recognises (finding 1 wanted the tool unnamed) | "some PKG extractors", the words `docs/SETUP_GUIDE.md` 141 already used; `git grep -i` for the tool's two words = 0 in the public copy. Decision D11 records that the name may go back if you want it |
| 19 | `tests/Tsuki.Tests.Data/App/FaceIconRealDataTests.cs` 15, `tests/Tsuki.Tests.Synthetic/App/AppIdentityTests.cs` 91-95, `tools/inventory.py` 3 and 8 | absolute paths of this machine that were inputs or outputs, not command examples: the icon test wrote to a fixed `D:\TsukihimePort\scratch\k23\icon`, the mutex test used a `D:\TsukihimePort\scratch\k20\save` input and two placeholders with the Windows user-profile prefix, the inventory docstring named this machine's output folder | the icon test derives the folder from the repository root (`Path.Combine(GoldenData.RepoRoot, "scratch", "k23", "icon")`, git-ignored, so a contributor's pictures land in their own clone); the mutex test uses `D:\x\save` and `D:\Profiles\x\...` (so the literal grep for the user-profile prefix is clean too, 7.2); the docstring says `samples/inventory under the repository root` (the code already used `Path(__file__).resolve().parents[1] / "samples" / "inventory"`). The remaining `D:/TsukihimePort` strings are `Run:` command examples in docstrings (25 lines in tests and tools, 6 in the README; 7.2) |
| 20 | `src/Tsuki.Launcher/Licenses/` (the zip's `licenses\FFmpeg-LGPL.txt`) | the shipped FFmpeg licence was the LGPL-3.0 alone; the LGPL v3 is a set of additional permissions on top of the GPL v3 and says it incorporates the GPL v3's terms, so the GPL text should accompany it (BtbN's package ships the LGPL alone, so this matched upstream practice; low) | `src/Tsuki.Launcher/Licenses/GPL-3.0.txt` (674 lines, 35,149 bytes, SHA-256 `3972dc97…`, LF), published as `licenses\FFmpeg-GPL-3.0.txt` by one more `<None Include>` line in `Tsuki.Launcher.csproj`; named in `docs/THIRD_PARTY.md` (the list of shipped texts and a sentence in the FFmpeg section that says why it is there and that no shipped library is GPL-licensed), in `Licenses/FFmpeg-SOURCE.txt` and in the Help page's two lists; `ThirdPartyNoticesTests` now checks the LGPL's "incorporates the terms and conditions of version 3 of the GNU General Public License" sentence, the GPL file's title, its section 17 and its appendix heading, the csproj pair and that the notices and the SOURCE file name the new file. **No download**: the text is the copy Git for Windows ships (`mingw64/share/licenses/xz/COPYING.GPLv3`, byte-identical to its `mintty/LICENSE.GPL`, both 674 lines / 35,149 bytes) |
| 21 | the status notes (`docs/status/knife/K24.md` 13, `docs/status/wave2/H3a.md` 47, about ten others) | local clock times in file names and timings, against a UTC commit date: a rough time zone | left as they are; decision D12 |

### 7.2 The public copy: `D:\TsukihimePort-public`

| check | command | result |
|---|---|---|
| what went in | `git ls-files --cached --others --exclude-standard` in the private tree (K30's and K33's intent-to-add files, the `.gitattributes` and the new `Licenses/GPL-3.0.txt` included), copied file by file with `cp --parents -p` run from the private root | **959 files** (958 in the first round + `GPL-3.0.txt`); the list was grepped for `samples/ scratch/ ref/ third_party/ artifacts/ .venv/ bin/ obj/ .claude/settings.local.json`: none; `git ls-files` of the commit, sorted, is identical to that list (`diff`); `cmp` of every one of the 959 files against the private tree: **0 differences** |
| how it was rebuilt | the agent's `rm -rf` of the earlier attempt was refused by the tool's safety check (it stops removals of whole top-level folders unless a person approves, and nobody was there to), so the repository was rebuilt **in place with git alone**: the working tree synchronised from the list, `git checkout --orphan`, `git rm -r --cached .`, `git add -A`, one commit, the old `main` deleted, the orphan renamed `main`, `git reflog expire --expire=now --all`, `git gc --prune=now` | `git rev-list --all \| wc -l` = **1**; `git for-each-ref` = `refs/heads/main` only; `git remote -v` = none; `.git/hooks` holds only git's `*.sample` files (no hook installed; the global config has no `core.hooksPath` / `init.templatedir`); the first round's commit object `8a5c087` **no longer exists** (`git cat-file -t` fails); `git fsck --unreachable` = 0 objects. The folder itself was never deleted, which is why its path is unchanged |
| a mistake on the way | the first copy loop gave `cp --parents` an absolute source path, which put a second copy of the tree under `d/TsukihimePort/` inside the public copy, and that was committed (1,917 files) | caught by the list comparison (every `d/...` path was extra); `git rm -r d` removed the stray tree, the files were copied again with relative paths, the commit was redone on a new orphan branch and the intermediate commits pruned as above; the checks of this table were run on the result |
| the identity and the dates | `git config --local user.name` / `user.email` in that repository only; `GIT_AUTHOR_DATE` = `GIT_COMMITTER_DATE` in UTC | `git log --format='%an %ae %cn %ce %ai %ci'` = `Tsukihime PC Port tsukihime-pc-port@example.invalid` twice, both dates `+0000`; the global git identity is unchanged (SHA-256 of `user.name` and `user.email` compared before and after) |
| the commit | | the commit built, cloned, tested and released below is the amended one that carries this section; its hash is what `git rev-parse HEAD` prints there and what the release binaries carry in their version string (7.4). The build and the tests of 7.3 ran on the commit before the amend (`ecd1278`), whose tree differs from the final one in `docs/PUBLISHING.md` and the two placeholder lines of `AppIdentityTests.cs` only (`git diff --stat` in the clone); the Synthetic suite was run again on the final commit (7.3) |
| the binaries | `git hash-object` in the private tree against `git rev-parse HEAD:<file>` in the public copy | `tsukihime.ico` `1a90213…`, `tsukihime_16.png` `ab6a833…`, `tsukihime_64.rgba` `7c4f0e1…`: the same blobs (`text=auto` touched no binary) |
| personal strings in files | `git grep -n -i -E '<the six strings>'`: the account name, the machine name, the e-mail local part, the e-mail provider's name, the user-profile prefix in both slash forms | **0 lines of personal data**; the e-mail provider's name, the account and machine names and the user-profile prefix have **0** hits (the two user-profile placeholders of the first round, `AppIdentityTests` 91-92, are gone); the account name's five letters occur in 46 lines, every one of them the English name of the game's ♥ inline font tag (`engine.md`, `fonts.md`, `text.md`, `K25.md`, `EbootLocator.cs`, `TextRules.cs`, `FontTables.cs`, the text tests and tools: a format fact) or inside the test name `...WhereTheArtsDoes`; `git grep -w` for the name alone = 0 |
| the same in the commit metadata | `git log --all --format='%an %ae %cn %ce %ai %ci %s %b' \| grep -i -c -E '<the six strings>'` | **0** |
| the extraction tool | `git grep -n -i` for the tool's two words | **0** (finding 18) |
| Japanese / Chinese text | `git grep -c -P '[\x{3040}-\x{30FF}\x{4E00}-\x{9FFF}]' -- . ':!assets/'` | **378 lines in 64 files**, the same count as after the first round (this section keeps its two stubs and adds no CJK), every line classified in the first round and unchanged since: the port's own strings (G10: `PortText.cs` 69, `UiPrompts.cs` 33, `UiDialogButtons.cs`, `GameTitles.cs`, `LauncherProfile.cs`, `LauncherModel.cs`, `TitleScreen.cs`, and their tests); interface strings and names (G9: the game's title in three scripts, the 23 character names, chapter and episode titles, menu labels and config descriptions, the quit and save questions, language names, the caution and "The End" captions, in `engine.md`, `sound_runtime.md`, `audio.md`, `images.md`, `system_flow.md`, `archives_ui_movies.md`, `README.md`, the guide's section headings, the K8..K28 status files, `test_eboot.py`); made-up test inputs (`MarkupAndExpansionTests`, `ScriptTextTests`, `TextReferencesTests`, `text_oracle.py`, `SyntheticLayoutCases.cs`, `TextLayoutTests`, `NamSearchTests`, `tools/oracle/textbox.py`, `test_font.py`, `test_textbox.py`, `test_text.py`: kana runs, `<漢字|かんじ>`, the made-up choice line, the made-up ruby sentence of `text.md`); glyph and character-class examples (`fonts.md`, `textbox.md`, `tools/tsuki/textbox.py`, `CensusAllowList.cs`, G16's single kanji); the stubs (`starts もし、`, `starts 気が`, a three-character fragment in K8); Japanese Wikipedia URLs and staff-role words in `engine_lineage.md` (public sources). **No full story line remains** |
| English story text | `git grep -o -P '(?<!")"[A-Z][^"]{20,}[.!?]"(?!")' -- docs tests tools src README.md CONTRIBUTING.md` (the audit's wider pattern, minus docstring openers) | **134 distinct quoted sentences**, the same set as after the first round (every one read then: the port's own messages and labels, the game's interface strings (G9), FFmpeg's "dubious hint" message, two made-up test lines, the shipped prose of the Help page and the guide); the second round's edits added none |
| absolute paths of this machine | `git grep -n -E 'D:.TsukihimePort' -- . ':!docs/PUBLISHING.md' ':!README.md'` | **25 lines, all `Run:` / `Usage:` command examples in docstrings** (`tools/tests/test_*.py`, `tools/oracle`, `tools/setup`, `tools/eboot`, the test generators under `tests/`, one spike script, one smoke `.ps1`); the three that were inputs or outputs are gone (finding 19). The README keeps 6 command examples; this file keeps its folder names by design |
| what never goes up | `ls samples scratch ref third_party artifacts .venv` in the public copy | none of them exists there |

### 7.3 The clean clone: `D:\TsukihimePort-public-check2`

The first round's clone, `D:\TsukihimePort-public-check`, could not be deleted by the agent (the same safety check as above), so the
second round cloned into a **new, empty** folder; the old one holds the first round's checkout and build output and nothing else of
value, and you may delete it.

| step | command (from the clone) | result |
|---|---|---|
| clone | `git clone D:\TsukihimePort-public D:\TsukihimePort-public-check2` | one commit (`ecd1278` at that moment), **959 files**, `origin` = the public copy |
| FFmpeg | `cp -rp D:\TsukihimePort\third_party\ffmpeg third_party\ffmpeg` (the folder `tools/setup/ffmpeg_dlls.ps1` fills from the installed winget package; the script's only download-related line prints a `winget install` hint) | the 7 DLLs, `LICENSE.txt`, `SOURCE.txt`; **no download** |
| build | `dotnet build src/Tsuki.slnx -c Release -warnaserror -m:4` (BelowNormal priority) | **Build succeeded, 0 Warning(s), 0 Error(s)**, 17 project outputs (`Tsukihime.exe`, `TsukihimeLauncher.dll`, `Tsuki.Cli.dll`, the libraries, the three test assemblies and the spikes), 15 s with a warm compiler server (.NET SDK 10.0.402) |
| the research prerequisites | junctions (`New-Item -ItemType Junction`) `samples\` and `.venv` pointing at the private folders, instead of the first round's 1.8 GB copy: no test writes under `samples\` (`grep` of the test sources for write calls near "samples" = none; every use is a read through `GoldenData.RepoRoot`), and a copy could not have been deleted afterwards | the junctions were removed after the runs (the private folders are untouched) |
| tests | `dotnet test tests/Tsuki.Tests.Synthetic -c Release --no-build` | **1245 / 1245 passed** (12 s) |
| | `dotnet test tests/Tsuki.Tests.Render -c Release --no-build --filter "FullyQualifiedName!~UploadTimingTests"` | **87 / 87 passed** (51 s) |
| | `dotnet test tests/Tsuki.Tests.Data -c Release --no-build` with `TSUKI_GAME_DIR` set | **1212 / 1212 passed (2 min 29 s), 0 failed, 0 skipped**; `PageLayoutOnRealScenesTests`, `GamePlayTests` and `FaceIconRealDataTests` among them; the icon test's pictures went to the clone's own `scratch\k23\icon` (finding 19), the private tree's folder was not touched |
| the final commit | `git fetch && git reset --hard origin/main` after the amend, `git rev-parse HEAD` equal in both; `dotnet build ... -warnaserror` again; the Synthetic suite again | done after the amend, so the result cannot stand in this file (it would change the commit): the build's and the Synthetic suite's lines for the final commit are in `RELEASE_NOTES.md` ("What was verified") and in the agent's report |

As before, the tests that need the research environment (the venv and the golden exports made from the game) fail on a bare clone
with a clear message instead of skipping (the first round measured 86 / 87 Render tests on the bare clone); a `Skip` with that reason
is housekeeping for the lead, not a blocker: the Synthetic suite, the one that needs nothing, is green.

### 7.4 The release: `D:\TsukihimePort-release\`

The release is the **last** step. Order: this section was written, the file copied into the public copy and the single commit
amended; the clone was moved to that final commit (`git fetch && git reset --hard origin/main`, `git rev-parse HEAD` equal in both);
then `dotnet publish` ran from the clone. The binaries therefore report `0.1.0+<hash>` where `<hash>` is the public `HEAD`. Because
the zip is made from the commit that contains this file, the final zip's size and SHA-256 cannot be written here (they would change
the commit and the binaries); they are in `D:\TsukihimePort-release\RELEASE_NOTES.md` beside the zip, together with the hash they
belong to. What this section records is the **candidate build** made the same way from the commit before the amend (`ecd1278`): the
final build differs from it in the commit hash inside the version string only, and the same checks were run on it (the results in the
release notes). The release folder of the first round was not deleted (the safety check again): the final publish wrote over it, and
its file list was then compared with the candidate's, so that no stale file of the first round could remain.

| check | command | result (candidate build from `ecd1278`) |
|---|---|---|
| publish | `dotnet publish src/Tsuki.Launcher -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:DebugType=none -o <folder>` from the clone, **without** `-p:TsukiPersonalIcon` | exit 0, 0 warning lines; **274 files, 252,085,071 bytes** (the first round's 273 plus `licenses\FFmpeg-GPL-3.0.txt`; `diff` of the two file lists shows that one line); ReadyToRun applied (`Tsuki.Data.dll` 1,605,632 bytes against 614,400 in the plain build) |
| contents | `find -type f` | `Tsukihime.exe`, `Tsukihime.dll`, `TsukihimeLauncher.exe`, `TsukihimeLauncher.dll`, the six `Tsuki.*.dll`, `README.md`, `SETUP_GUIDE.txt`, `licenses\LICENSE.txt` (the port's MPL-2.0), `licenses\knife-MPL-2.0.txt`, `licenses\FFmpeg-LGPL.txt` (LGPL-3.0), **`licenses\FFmpeg-GPL-3.0.txt`** (GPL-3.0, finding 20), `licenses\FFmpeg-SOURCE.txt`, `licenses\SDL3-zlib.txt`, `licenses\THIRD_PARTY.md` (with the four MIT notices: .NET runtime and WPF, Silk.NET, SDL3-CS, FFmpeg.AutoGen), `SDL3.dll`, `SDL3-CS.dll`, `Silk.NET.*.dll`, `FFmpeg.AutoGen.dll`, the four FFmpeg DLLs (`avcodec-63`, `avformat-63`, `avutil-61`, `swresample-7`), the .NET 10 runtime and WPF; **0 `.pdb`**, **0 `.xml`**, **no `PERSONAL_BUILD.txt`**; `README.md`, `SETUP_GUIDE.txt`, `THIRD_PARTY.md`, `LICENSE.txt`, `FFmpeg-LGPL.txt`, `FFmpeg-GPL-3.0.txt` and `FFmpeg-SOURCE.txt` are **identical to the repository's files up to line endings** (`diff --strip-trailing-cr`; the clone checks out CRLF on this machine, the public working copy holds the private tree's bytes) |
| release check | `dotnet src/Tsuki.Cli/bin/Release/net10.0/Tsuki.Cli.dll release-check <folder>` | `ok Tsukihime.exe: the icon group is the crescent (9 frames)`, `ok Tsukihime.dll: version 0.1.0+ecd1278399e84c18e7a2a09077cffa13f34bd5d8, no personal mark`, the same two lines for the launcher, **`RESULT: ... may be released`**, exit 0; `grep -c TsukiPersonalBuild` on both program DLLs = 0. The final build prints the same with the final hash (release notes). A first reading of the folder, taken while the publish target that copies the game program was still running, showed 266 files and three `ok` lines; the complete folder was read again once the process had ended, which is the row above |
| personal strings in the folder | `grep -r -l -a -i -E '<the six strings and the tool's two words>'` over every file, binaries included | the port's own files: **0**. Two upstream binaries (`avcodec-63.dll`, `System.IO.Compression.Native.dll`) contain the e-mail provider's five letters once each as adjacent bytes; `avcodec-63.dll` has the SHA-256 of the winget package's own copy and `System.IO.Compression.Native.dll` that of the .NET 10 runtime pack in the NuGet cache, so both are unmodified upstream content, not this machine's data |
| the program runs | `Tsukihime.exe --game <TSUKI_GAME_DIR> --save-dir <scratch> --shot-dir <scratch> --hidden --mute --autoplay click --exit-after 3000 --log <scratch>` with `SDL_AUDIO_DRIVER=dummy`, BelowNormal priority | **exit 0: `done: 3000 steps in 51.5 s`**, `0 timeouts`, `1437 movie frames`, `textures 184 resident (39 MiB)`, `1 frames waited 7 ms`; `timing: step 0.04 ms mean, 21.4 ms max; draw+present 1.11 ms mean, 80.7 ms max, 3 of 3000 draws over 16.7 ms`; `frames: 3000, longest 102.1 ms, 2 over 33 ms and 1 over 50 ms after the first second`; the log's first line names `Tsukihime 0.1.0+ecd1278…; FFmpeg n9.0.1-11-ge47273f4d9-20260831 from <the candidate folder>`, then `no save file: a new profile`, `build: known build: CUSA47999 01.01 (official 1.01 update eboot)`, `tables: at the documented addresses (verified build)`; nothing on stdout or stderr; 52 s wall clock. The run wrote `save.dat` and the run-time icon into the scratch save folder only; the user's `%APPDATA%\Tsuki` was not touched (newest file `save.dat` 17:21:23 before and after) |
| the zip | `Compress-Archive -CompressionLevel Optimal -Force` of the folder's contents into `D:\TsukihimePort-release\Tsukihime-v0.1.0-win-x64.zip`, then `Expand-Archive` into a scratch folder and `sha256sum` of every file against the publish folder | the final zip's byte count and SHA-256 are in `RELEASE_NOTES.md`; the unpacked files equal the publish folder's (0 differ); the zip's bytes were grepped for the six strings |
| release notes | `D:\TsukihimePort-release\RELEASE_NOTES.md` | what it is, the disclaimer (3.5, the corrected wording), what you need (44 GB, about 260 MB, integrated graphics), where the setup guide is, the key list, what was verified and the known / untested items from the status files and the guide's 1.1, how to report, the licences (now with the GPL v3 text beside the LGPL), the FFmpeg source tag `n9.0.1` and the BtbN build name (D8: no archive downloaded; the user may attach it, 4.2 D 4), the commit hash the build carries, the zip's size and SHA-256 |

### 7.5 For the lead before the upload

1. The README's Releases link is relative (`../../releases`): look at it on the repository page while the repository is still
   private (4.2 C); it should open the repository's own Releases page.
2. The tests that need the research environment (7.3) fail on a bare clone with a clear message; a `Skip` with that reason is
   housekeeping.
3. Two folders the agent could not delete (the tool's safety check refuses whole-folder removals without a person's approval) are
   yours to remove: `D:\TsukihimePort-public-check` (the first round's clone, with its build output) and nothing else; the public copy
   and the release folder were rebuilt in place and verified as such (7.2, 7.4). `D:\TsukihimePort-public-check2` is the clone that was
   actually built, tested and published from, and may stay or go.
4. The FFmpeg source archive (D8): the release notes point at the exact tag and build; attach the archive yourself if you want the
   LGPL point to depend on no external link (4.2 D 4).
5. D11 (the extraction tool's name) and D12 (the clock times in the status notes) were decided by default in the careful direction;
   both are a one-line change to revisit.
