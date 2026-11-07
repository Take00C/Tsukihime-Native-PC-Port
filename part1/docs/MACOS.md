# macOS (experimental)

Not yet tested on a real Mac: please [report problems in Issues](https://github.com/Take00C/Tsukihime-Native-PC-Port/issues). Needs macOS 15 or newer.

## Install

1. Download `Tsukihime-vX.Y.Z-macos-arm64.zip` (Apple silicon: **About This Mac** says **Chip**) or `Tsukihime-vX.Y.Z-macos-x64.zip` (Intel: it says **Processor**).
2. Let Safari unzip it, or double-click it in Finder (other unzip tools break the app's signature). Move `Tsukihime.app` to **Applications**.
3. Open it. When macOS says it was not opened, click **Done** (not **Move to Trash**), then within an hour open **System Settings > Privacy & Security**, scroll down and click **Open Anyway**.
4. On **Game files**, pick your game folder (layout: [setup guide](https://github.com/Take00C/Tsukihime-Native-PC-Port/blob/main/docs/SETUP_GUIDE.md)) and press **PLAY**. If macOS asks for access to the folder, choose **Allow**.

## Update

Close the game and press **Update to vX.Y.Z**. macOS may ask for **Open Anyway** and folder access again. The button needs the app on your Mac's own drive (APFS); in `/Applications` it also needs an administrator account, otherwise keep the app in `~/Applications`. If an update fails, download the new ZIP and replace the app; your saves stay.

## Good to know

- **Option+Return** toggles fullscreen. **Fn+F5** and **Fn+F9** quick save and load; **Fn+F12** saves a screenshot to `~/Pictures/Tsukihime`.
- Saves, settings and logs are in `~/Library/Application Support/Tsuki` (**Help & about > Open the saves folder**). Attach `last_run.log`, `crash.log`, `launcher.log` and `launcher_crash.log` to a problem report.
- If the game folder can no longer be read, pick it again with **Browse…**, or turn on Tsukihime in **System Settings > Privacy & Security > Files & Folders**.
- The Dock shows Arcueid's face while the launcher runs; the Finder icon stays the crescent (changing it would break the signature).
- Terminal: `Tsukihime.app/Contents/MacOS/Tsukihime --game /path/to/CUSA47999` starts the game without the launcher; `--save-dir FOLDER` (game) or `--data FOLDER` (launcher) uses a separate profile; `xattr -dr com.apple.quarantine /path/to/Tsukihime.app` replaces **Open Anyway** for a copy you trust.

## Build from source

Install the .NET 10 SDK, Apple's command line tools and Python 3. An Intel build also needs `nasm` (`brew install nasm`). Run on the architecture being packaged:

```sh
dotnet build src/Tsuki.Linux.slnx -c Release
bash tools/setup/ffmpeg_macos.sh arm64
```

For Intel, use `x64` instead of `arm64`. Follow [PUBLISHING.md](https://github.com/Take00C/Tsukihime-Native-PC-Port/blob/main/docs/PUBLISHING.md) section 4.3 to publish, package, sign and check the app. macOS does not support the Linux system-FFmpeg publish option.

## Checks

Run `TSUKI_GAME_DIR=/nonexistent dotnet test tests/Tsuki.Tests.Synthetic -c Release` and `dotnet test tests/Tsuki.Tests.Avalonia -c Release` from the source tree. The latter exercises the pages headlessly. `Tsukihime.app/Contents/MacOS/TsukihimeLauncher --render /tmp/tsuki-pages --data /tmp/tsuki-profile --game /nonexistent` writes all seven page PNGs outside the repository. `python3 -I tools/tests/update_e2e.py Tsukihime-vX.Y.Z-macos-ARCH.zip --work /tmp/tsuki-update-e2e` updates an extracted copy of a ZIP with its own launcher.

Test native smoke, rendering, audio-device changes, sleep/resume, the one-click update and Gatekeeper on both Mac architectures. Do not use `SDL_VIDEODRIVER=offscreen`: it requires EGL, which macOS does not provide.
