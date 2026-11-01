# Contributing

Build instructions, development tools and contribution rules follow. Check the intended fork or upstream
repository before reporting a problem or sending a change.

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

The program contains no game data and does not provide game files or links to sources of them.
The pre-commit guard rejects specified data folders and file types. Web uploads bypass it; review
every attachment yourself.

## 2. How to build

Linux: see [the native Linux guide](docs/LINUX.md). Build `src/Tsuki.Linux.slnx` to exclude
the Windows WPF launcher and Windows development spikes. The same synthetic, rendering and
data test programs are included. The build commands below use Windows PowerShell from the repository root.

- Windows 10 or 11 (64-bit), the **.NET 10 SDK** (`winget install Microsoft.DotNet.SDK.10`), Git.
- The FFmpeg 9 LGPL libraries, once: `tools\setup\ffmpeg_dlls.ps1` copies the FFmpeg 9 LGPL DLLs from the
  installed winget package `BtbN.FFmpeg.LGPL.Shared.9.0` into the git-ignored `third_party\ffmpeg\bin` (if the
  package is missing it prints the `winget install` command and stops; it never installs or downloads anything
  itself; the build stops with `TSUKI001` without the DLLs).
- `dotnet build src\Tsuki.slnx -c Release` builds everything; warnings are errors
  (`Directory.Build.props`), so `-warnaserror` is the normal state, not an option.
- The release folder: `dotnet publish src\Tsuki.Launcher -c Release -r win-x64 --self-contained -o <folder>`
  (one folder with `TsukihimeLauncher.exe`, `Tsukihime.exe`, the runtime, the libraries and `licenses\`).
  Never build with `-p:TsukiPersonalIcon=...` for anything that leaves your machine ([personal builds](https://github.com/Take00C/Tsukihime-Native-PC-Port/blob/main/docs/PUBLISHING.md#14-personal-builds-never-go-out-package-k28)).
- Tests: `dotnet test tests\Tsuki.Tests.Synthetic` needs no game. `tests\Tsuki.Tests.Data` and
  `tests\Tsuki.Tests.Render` run against your own copy and skip, with the reason, when the environment
  variable `TSUKI_GAME_DIR` does not name the `CUSA47999` folder; the Render tests open a hidden window.
- For Python readers, dependency checks and tests, see [Python tools](#python-tools) below.

Nothing in the build, the tools or the tests writes into the game folder. Keep it that way.

### Python tools

Requirements:

- CPython 3.14.10 or a newer 3.14 security release, only for development tools.
- FFmpeg and ffprobe on `PATH` (the audio tests decode ATRAC9 with FFmpeg; `TSUKI_FFMPEG` can point
  to a specific `ffmpeg` executable).
- An extracted copy of the game (the `CUSA47999` folder with `eboot.bin`, `image/`, `data/`,
  `sce_sys/`), for readers and tests that use real game data.

Run these commands from the repository root to create the virtual environment and install the packages:

```powershell
py -3.14 -m venv .venv
.venv\Scripts\python.exe -m pip install --only-binary=:all: numpy pillow pytest
# Optional tools used by readers and tests:
.venv\Scripts\python.exe -m pip install --only-binary=:all: numba capstone unicorn keystone-engine mypy pyflakes
.venv\Scripts\python.exe tools\setup\check_python.py
```

Use a supported [Python 3.14 release](https://www.python.org/downloads/) before creating the environment.
Recreate older environments without `--system-site-packages`. Use binary wheels for packages with native code.

- `numpy`, `pillow`, `pytest`: required.
- `numba`: JIT kernels for MZX0 and the HE-VAG decoder; set `TSUKI_NO_NUMBA=1` to use the pure Python paths.
- `capstone`, `unicorn`: eboot disassembly and the tests that run the game's own code under
  emulation to check our implementations; `keystone-engine`: one work-in-progress layer module.
- `mypy`, `pyflakes`: linting.

Tell the tools where the game is with `TSUKI_GAME_DIR` (the `CUSA47999` folder). There is no
built-in default: without the variable `tsuki.game_dir()` returns `None`, the readers stop with
"game folder not found (set TSUKI_GAME_DIR)" and the real-data tests skip. The game folder is only
ever opened read-only.

```powershell
$env:TSUKI_GAME_DIR = "D:\path\to\CUSA47999"
```

#### Running the readers

The packages live in `tools/`, so put it on `PYTHONPATH`. PowerShell:

```powershell
$env:PYTHONPATH = "tools"
New-Item -ItemType Directory -Force samples | Out-Null
.venv\Scripts\python.exe -m tsuki.script dump 3 --lang en            # readable dump of script 00_02
.venv\Scripts\python.exe -m tsuki.catalog image img2511 WHITE        # script picture id -> archive entries
.venv\Scripts\python.exe -m tsuki.image allpac1_2 ARU_L_81_00_N00.MZP --base ARU_L_81_00_N00_B.MZP -o samples/out.png
.venv\Scripts\python.exe -m tsuki.textbox render en samples/out.png 3 4 5
.venv\Scripts\python.exe -m tsuki.audio decode bgm 33 samples/out.wav --loops 1
.venv\Scripts\python.exe -m eboot.uitext --out samples/ui-text
.venv\Scripts\python.exe tools\inventory.py --deep                   # rebuild samples/inventory/
```

Bash on Linux, using a Python 3.14.10 or newer 3.14 interpreter:

```bash
python3.14 -m venv .venv
.venv/bin/python -m pip install --only-binary=:all: numpy pillow pytest
.venv/bin/python -m pip install --only-binary=:all: numba capstone unicorn keystone-engine mypy pyflakes
.venv/bin/python tools/setup/check_python.py
export TSUKI_GAME_DIR="/path/to/CUSA47999"
mkdir -p samples
export PYTHONPATH=tools
.venv/bin/python -m tsuki.script dump 3 --lang en
# For the remaining examples, use .venv/bin/python and / in file paths.
```

Each module has a CLI (`python -m tsuki.<module> --help`); the format documents list the
commands they used. Everything these commands write from game data is copyrighted material:
write it to `samples/` or `scratch/` (or another local folder) and keep it local.

#### Running the Python tests

```bash
PYTHONPATH=tools .venv/bin/python -m pytest tools/tests -q
```

PowerShell:

```powershell
$env:PYTHONPATH = "tools"
.venv\Scripts\python.exe -m pytest tools\tests -q
```

- Real-data tests skip cleanly when the game folder is missing; with
  `TSUKI_GAME_DIR=/nonexistent` only the synthetic tests run.
- The real-save test in `test_eboot.py` also needs a PS4 `savedata000` image.
- Lint: `.venv/Scripts/python.exe -m mypy --ignore-missing-imports tools/tsuki tools/eboot` and
  `.venv/Scripts/python.exe -m pyflakes tools`. On Linux use
  `.venv/bin/python`.
- Test the fallback too: Bash `TSUKI_NO_NUMBA=1 PYTHONPATH=tools .venv/bin/python -m pytest tools/tests -q`.
  In PowerShell set `$env:TSUKI_NO_NUMBA = "1"`, run the test command above, then
  `Remove-Item Env:TSUKI_NO_NUMBA`. Unset it for the normal Numba run.

Address conventions, confidence labels and reader-to-format mappings are in the
[format reference](https://github.com/Take00C/Tsukihime-Native-PC-Port/blob/main/docs/FORMATS.md). Large archives must be read with seeking or memory mapping.

## 3. How to report a problem

Include:

1. Reproduction steps, expected and actual behavior. Use script/line IDs for scenes, not story text.
2. Logs: Windows launcher **Log** opens `%APPDATA%\Tsuki\last_run.log`; `crash.log` records failures.
   A direct start needs `--log <file>` for a run log. Linux uses its profile directory from `docs/LINUX.md`.
   You may redact personal paths before posting.
3. Build details: `layout:`, `build:`, `languages:`, `tables:` and graphics adapter/OpenGL lines.
   Unknown-build errors already identify the eboot and missing tables; do not attach game files.
4. OS, graphics card/driver, monitor arrangement, input device and whether windowed/fullscreen differs.
5. For launcher failures, the red **Ready check** row's text. An error-only crop is fine; no saves or
   screenshots of the game.

## 4. Sending a change

- Small pull requests, one subject each, with a test (the Synthetic test program for anything that needs no
  game; a real-data test skips cleanly without `TSUKI_GAME_DIR`). Tests use **synthetic data only**:
  no fixture may embed game content, not even a line of text or a glyph.
- The build must stay warning-free (`dotnet build src\Tsuki.slnx -c Release`), and the three test
  programs and the Python tests must pass.
- Facts about the game's engine (addresses, table layouts, command behaviour) go into `docs/formats/`
  with the evidence that backs them (the documents use `[E]` read from the engine code, `[V]` verified on
  the retail data with counts, `[H]` hypothesis).
- Licence: the port's code is under the **Mozilla Public License 2.0** (`LICENSE`); by sending a change
  you agree that it is licensed the same way. The files translated from knife keep their MPL header and
  their row in `docs/THIRD_PARTY.md` (a test enforces the table). Do not copy code from other programs
  unless its licence is compatible and credited there.
- Install the pre-commit hook after setting up Python. It blocks specified working folders, game-data
  extensions and files over 5 MiB. If you already have a hook, merge the checker invocation instead of
  replacing it. The old `install_hooks.ps1` installer is not present in this repository.

PowerShell, from the repository root, copies the supplied Windows wrapper:

```powershell
Copy-Item tools\setup\hooks\pre-commit (git rev-parse --git-path hooks/pre-commit)
```

Bash needs the Linux virtual-environment path:

```bash
cat > "$(git rev-parse --git-path hooks/pre-commit)" <<'HOOK'
#!/bin/sh
exec ./.venv/bin/python tools/setup/hooks/precommit_check.py
HOOK
chmod +x "$(git rev-parse --git-path hooks/pre-commit)"
```

The guard also flags xUnit `Skip` attributes paired with `SkipUnless`. After reviewing every flagged
addition and running the applicable tests, allow conditional platform or native-library skips for one
commit only. In Bash, prefix that commit command with `TSUKI_ALLOW_SKIP=1`. In PowerShell, set
`$env:TSUKI_ALLOW_SKIP = "1"` for that commit, then immediately run `Remove-Item Env:TSUKI_ALLOW_SKIP`,
even if the commit fails. Never use this exception to disable tests unconditionally.
