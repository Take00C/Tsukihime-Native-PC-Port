# Linux

The SDL player runs natively on Linux. The separate WPF launcher remains Windows only.
The player has its own game folder picker on first launch and settings in the pause menu.

## Requirements

- Linux x86-64 with glibc 2.34 or newer. Older glibc systems cannot run the bundled SDL.
  Distributions using musl need a different build.
- A system C++ runtime providing `GLIBCXX_3.4.22` and `CXXABI_1.3.7`, plus the system
  OpenSSL and crypto shared libraries. These system libraries are not bundled.
- OpenGL 3.3 and a working graphics driver.
- FFmpeg 9 shared libraries with the `atrac9`, `h264` and `aac` decoders:
  `libavutil.so.61`, `libswresample.so.7`, `libavcodec.so.63`, `libavformat.so.63`.
  The release archive includes them (BtbN's LGPL build). A source build uses a complete set beside
  `Tsukihime`, else the system library path. An older FFmpeg major version cannot be substituted
  by renaming its libraries.
- Your own extracted PS4 game folder. The tested source edition is `CUSA47999`, update 1.01.
  No game data is included with this program.

The release archive includes .NET, SDL and FFmpeg. OpenSSL, the C++ runtime and the graphics
driver remain external dependencies.
These ABI requirements do not establish compatibility with distributions we have not tested.

In the extracted program folder, `docs/SETUP_GUIDE.md` covers game/update layouts,
controls, saves and dictionary outputs. Its Windows launcher pages and Windows paths do not apply here.
Native Windows text hooks are unavailable; clipboard and loopback WebSocket output use the player settings.

## Run a release archive

Check a release's actual assets before assuming a Linux archive is available.
Extract the Linux archive into a folder you can write to, then open a terminal in that folder:

```sh
tar -xzf /path/to/Tsukihime-vX.Y.Z-linux-x64.tar.gz
cd Tsukihime-vX.Y.Z-linux-x64
./Tsukihime
```

Keep the entire extracted folder together, including its libraries and `licenses/`.
The game folder is separate and can be selected on first launch. Without a desktop file chooser
(xdg-desktop-portal or zenity), start it with `./Tsukihime --game /path/to/CUSA47999` or set `TSUKI_GAME_DIR`.
No .NET SDK is needed to run a self-contained release.

## Build and run

Install the .NET 10 SDK, then run from the repository root:

```sh
dotnet build src/Tsuki.Linux.slnx -c Release
bash tools/setup/publish_linux.sh
./artifacts/linux-x64/Tsukihime
```

The script accepts an optional output folder, followed by additional `dotnet publish` options.
It can also be run from another directory. It bundles FFmpeg from `third_party/ffmpeg/linux`
(see [PUBLISHING.md](https://github.com/Take00C/Tsukihime-Native-PC-Port/blob/main/docs/PUBLISHING.md) 3.3); to use your system's FFmpeg 9 instead, add
`-p:TsukiBundleFFmpeg=false`: `bash tools/setup/publish_linux.sh artifacts/linux-x64 -p:TsukiBundleFFmpeg=false`.
To choose the game folder explicitly:

```sh
./artifacts/linux-x64/Tsukihime --game /path/to/CUSA47999
```

Paths are case sensitive. Choose the extracted folder containing `eboot.bin`, rather than
an encrypted package or a console installation package.

Settings and saves keep the existing `Tsuki` profile name, normally `~/.config/Tsuki` or
`$XDG_CONFIG_HOME/Tsuki`. The folder is created if needed. A relative `XDG_CONFIG_HOME`
is ignored in favour of `~/.config`. Use `--save-dir /path/to/saves` for a separate profile.
The crash log is written there when possible. `F12` takes a screenshot, `F11` toggles
fullscreen and `Escape` opens the pause menu.

Close the player before backing up the profile. Copy `save.dat` and `settings.ini` to a separate backup
folder. To restore, first copy the current files aside, then replace `save.dat` with the backup; restore
`settings.ini` only if you also want the old settings. Automatic recovery tries `save.dat.bak` after
setting a damaged save aside. The Windows launcher’s backup browser and PS4 import UI are unavailable
on Linux. An existing port `save.dat` can be copied into the Linux profile while the player is closed.
For a run log, launch `./Tsukihime --log /tmp/tsuki-run.log` from the extracted folder.

## Checks without game files

For Python checks, first follow **Python tools** in root `CONTRIBUTING.md`, included in the archive
and repository. It gives the Linux isolated CPython 3.14.10-or-newer-3.14 setup, binary-only pinned
installation, security gate, strict dependency audit, pin updates and Numba fallback tests.
Run checks from the repository root; the source and tests are not included in a player archive.

```sh
export TSUKI_GAME_DIR=/nonexistent
dotnet test tests/Tsuki.Tests.Synthetic -c Release
dotnet test tests/Tsuki.Tests.Render -c Release
dotnet test tests/Tsuki.Tests.Data -c Release
PYTHONPATH=tools .venv/bin/python -m pytest tools/tests -q
./artifacts/linux-x64/Tsukihime --smoke --report /tmp/tsuki-smoke.txt
```

Rendering tests need a display with hardware OpenGL. Xvfb's software renderer has different
blend rounding and fails two pixel comparisons. SDL's offscreen driver does not implement
window icons, so it cannot run the full window test suite. The native desktop backend runs
both sets of checks. `TSUKI_PYTHON` overrides the rendering tests' Python interpreter.
For a headless smoke check, prefix the smoke command with
`SDL_VIDEODRIVER=offscreen SDL_AUDIODRIVER=dummy`. This does not replace desktop rendering checks.

Tests that need retail data report a skip unless `TSUKI_GAME_DIR` names your extracted folder.
Windows COM shortcut tests also skip on Linux. These skips do not establish gameplay
compatibility. A complete play test still needs the retail files.
