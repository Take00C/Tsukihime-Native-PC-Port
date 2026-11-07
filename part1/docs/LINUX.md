# Linux

The SDL player and its Avalonia launcher run natively on Linux. The WPF launcher remains Windows only.
The launcher has the Windows version's pages, game settings, backup browser and update button; the player can still be started directly.

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

The release archive includes .NET, SDL, FFmpeg, Skia and HarfBuzz. OpenSSL, the C++ runtime and the graphics
driver remain external dependencies. `ldd` on the published launcher's bundled `libSkiaSharp.so` also requires
system `libfontconfig.so.1` and `libfreetype.so.6`, which bring in Expat, zlib, bzip2, PNG and Brotli.
Install the distribution's fontconfig and FreeType packages if they are absent. The launcher's 日本語, 简体中文 and 繁體中文
buttons need a CJK font such as Noto Sans CJK; most desktops have one. These ABI requirements do not
establish compatibility with SteamOS or other distributions we have not tested.

In the extracted program folder, `docs/SETUP_GUIDE.md` covers game/update layouts,
controls, saves and dictionary outputs. Its launcher pages are the same here; its Windows paths do not apply.
Native Windows text hooks are unavailable; clipboard and loopback WebSocket output use the player settings.

## Run a release archive

Check a release's actual assets before assuming a Linux archive is available.
Extract the Linux archive into a folder you can write to, then open a terminal in that folder:

```sh
tar -xzf /path/to/Tsukihime-vX.Y.Z-linux-x64.tar.gz
cd Tsukihime-vX.Y.Z-linux-x64
./TsukihimeLauncher
```

Keep the entire extracted folder together, including its libraries and `licenses/`.
Choose the game folder on the launcher's **Game files** page. Alternatively, start the player directly with
`./Tsukihime --game /path/to/CUSA47999` or set `TSUKI_GAME_DIR`.
Close the game before using the launcher's update button; it verifies, installs and restarts into the new release.
**Add Tsukihime to the application menu** on **Game files** (or **Create** in Play's quick settings) adds an entry that starts the game.
No .NET SDK is needed to run a self-contained release.

## Steam Deck

Same Linux download. Not yet tested on a real Deck: please report how it goes.

1. In Desktop Mode, extract the archive into a folder you can write to.
2. Start `TsukihimeLauncher`, pick your game folder on **Game files** (a microSD card is under `/run/media/deck/`) and press **Add Tsukihime to the application menu**.
3. In Steam, choose **Games > Add a Non-Steam Game to My Library…** and tick **Tsukihime** (it starts the game, not the launcher). If it is not listed, use **Browse…** and pick `Tsukihime` in the extracted folder (file type **All Files**).

- The game starts fullscreen; change it in **Config > System > Display Mode** or with the launcher's **Fullscreen** switch. If Game Mode still opens a window, add `--fullscreen` to its **Launch Options**.
- Use the launcher in Desktop Mode for backups and updates; the Steam entry keeps working after an update.
- From v0.1.6: extract into a new folder and add the new `Tsukihime` to Steam again.
- Steam Cloud does not cover non-Steam games: copy `~/.config/Tsuki` to a microSD card or USB stick now and then.
- Problems? Attach `last_run.log` and `crash.log` from `~/.config/Tsuki` (Ctrl+H shows hidden folders in Dolphin).

## Build and run

Install the .NET 10 SDK, then run from the repository root:

```sh
dotnet build src/Tsuki.Linux.slnx -c Release
bash tools/setup/publish_linux.sh
./artifacts/linux-x64/TsukihimeLauncher
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
is ignored in favour of `~/.config`. Use `--save-dir /path/to/saves` for a separate profile (the launcher takes `--data`).
The crash log is written there when possible. The launcher writes `launcher.log` and `launcher_crash.log` there and holds
`launcher.lock` while it runs, so a second launcher only says it is already open. `F12` takes a screenshot, `F11` toggles
fullscreen and `Escape` opens the pause menu.

Close the player before backing up or restoring the profile. The launcher's **Saves** page backs up,
restores and imports PS4 images; it backs up the current save before replacing it. Automatic recovery tries
`save.dat.bak` after setting a damaged save aside. An existing port `save.dat` can be copied into the Linux profile while the player is closed.
Every start writes the run log `last_run.log` in the profile; `./Tsukihime --log FILE` writes it elsewhere.

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
dotnet test tests/Tsuki.Tests.Avalonia -c Release
PYTHONPATH=tools .venv/bin/python -m pytest tools/tests -q
./artifacts/linux-x64/Tsukihime --smoke --report /tmp/tsuki-smoke.txt
./artifacts/linux-x64/TsukihimeLauncher --render /tmp/tsuki-launcher-pages --data /tmp/tsuki-profile --game /nonexistent
TSUKI_K48_LINUX_PUBLISH=$PWD/artifacts/linux-x64 dotnet test tests/Tsuki.Tests.Avalonia --filter LinuxUpdateEndToEndTests
python3 -I tools/tests/update_e2e.py artifacts/Tsukihime-vX.Y.Z-linux-x64.tar.gz --work /tmp/tsuki-update-e2e
```

Rendering tests need a display with hardware OpenGL. Xvfb's software renderer has different
blend rounding and fails two pixel comparisons. SDL's offscreen driver does not implement
window icons, so it cannot run the full window test suite. The native desktop backend runs
both sets of checks. `TSUKI_PYTHON` overrides the rendering tests' Python interpreter.
For a headless smoke check, prefix the smoke command with
`SDL_VIDEODRIVER=offscreen SDL_AUDIODRIVER=dummy`. This does not replace desktop rendering checks.

Tests that need retail data report a skip unless `TSUKI_GAME_DIR` names your extracted folder.
Windows COM shortcut tests and the Windows-apphost release fixtures also skip on Linux. These skips
do not establish gameplay compatibility. A complete play test still needs the retail files.
