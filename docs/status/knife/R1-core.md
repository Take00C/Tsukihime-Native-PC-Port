# R1 (core): review fixes for the media, data, render, CLI and tools area

Review round R1, fixer `fix-core`, run on 2026-10-30 on top of commit 38cc3af (K18) plus the uncommitted K19 display-mode work. Two verified findings were assigned; both are fixed.
Nothing is committed. Builds: `artifacts/fix-core` (the shared tree) and `artifacts/fix-core/tree` (a scratch copy, section 3). Logs, the scratch copy and the smoke report are in
`scratch/fix-core/`. The game folder was read only (the data tests), no visible window was opened, `%APPDATA%\Tsuki`, `artifacts/play*` and `artifacts/dist` were not touched.

## 1. docs/THIRD_PARTY.md had no FFmpeg or SDL3 entry; SOURCE.txt said the wrong FFmpeg licence version (minor) — fixed

**Before** (`git show HEAD:...`): `docs/THIRD_PARTY.md` had one section, `## knife (MPL-2.0)` (line 3), while `src/Tsuki.Launcher/Views/HelpPage.xaml:95` sends the user there for the
FFmpeg (LGPL v3) and SDL3 (zlib) notices too; `tools/setup/ffmpeg_dlls.ps1:172` wrote `licence: LGPL v2.1 or later` into `third_party/ffmpeg/SOURCE.txt`. The build is LGPL v3: the
hidden smoke run of section 3 prints `license "LGPL version 3 or later"` (`scratch/fix-core/smoke.txt`), `third_party/ffmpeg/LICENSE.txt` begins with the LGPL version 3 heading, and
ARCHITECTURE 0.5 / RUNTIME_REQUIREMENTS 5 say the notices must say v3 (L2.md open item 2 had already flagged the script line).

**Changed**

* `docs/THIRD_PARTY.md`: two new sections, `## FFmpeg (LGPL v3 or later)` (line 38) and `## SDL3 (zlib)` (line 54). FFmpeg: the four DLLs `FfmpegLibrary.RequiredDlls` loads
  (`swscale-10`, `avfilter-12`, `avdevice-63` travel with them, unused: no `sws_` call in `src/Tsuki.Media`), BtbN `win64-lgpl-shared-9.0` / winget `BtbN.FFmpeg.LGPL.Shared.9.0`
  9.0.1-20260831 / `n9.0.1-11-ge47273f4d9-20260831` (from `SOURCE.txt` and the script's own output), the release page and the FFmpeg git tag, what `ffmpeg_dlls.ps1` copies and
  writes, `--enable-version3` = LGPL v3 or later, no GPL component (no `postproc`), dynamic loading by `FfmpegLibrary.cs` through `FFmpeg.AutoGen` 9.0.1.1 (MIT: the package's
  `LICENSE.txt` in the NuGet cache is the MIT text). SDL3: what Tsuki.Platform takes from it, zlib licence, `SDL3.dll` = SDL 3.5.0 (file version 3.5.0.0 of
  `runtimes/win-x64/native/SDL3.dll` in the `ppy.SDL3-CS` 2026.1002.1 package; nuspec licence MIT for the bindings), `Silk.NET.OpenGL` 2.23.0 (nuspec MIT) as the GL binding,
  `Directory.Packages.props` as the pin.
* `tools/setup/ffmpeg_dlls.ps1:172`: `licence: LGPL v3 or later (an --enable-version3 build: avcodec_license() answers "LGPL version 3 or later"; LICENSE.txt is the package's own copy
  of the LGPL v3 text)`. The script was then run (`scratch/fix-core/ffmpeg_dlls_run.log`): the seven DLLs and `LICENSE.txt` `unchanged` (SHA-256 identical), `SOURCE.txt` `updated`;
  the local `third_party/ffmpeg/SOURCE.txt:5` now carries the v3 line (git-ignored, as before).

**Test** `tests/Tsuki.Tests.Synthetic/ThirdPartyNoticesTests.cs` (new): every `src/` file with the MPL header is a row of the table and every row has the header (25 files, the rule
of knife_port.md 1); every licence name of the Help page's `LicenseName` blocks (knife, FFmpeg, SDL3) has a `## <name> (` section, with the three headings spelled out; the setup
script writes `'licence: LGPL v3 or later` and neither it nor the notice file says `v2.1`. Before / after (section 3): with HEAD's `THIRD_PARTY.md` and `ffmpeg_dlls.ps1` placed in
the scratch copy, `EveryLicenceOfTheHelpPageHasASection` and `TheFfmpegSetupScriptWritesTheLgplV3Notice` fail and the MPL table test passes (`test_before_thirdparty.log`); with
the edited files all three pass (`test_after_thirdparty.log`).

## 2. Start-up failure dialogs showed developer text (polish) — fixed

**Before**: `FfmpegLibrary.Load` threw `FileNotFoundException("FFmpeg DLLs missing from <root>: <names> (run tools/setup/ffmpeg_dlls.ps1; ARCHITECTURE 0.5)")` (HEAD
`FfmpegLibrary.cs:68`), `GameApp.Run` showed `e.Message` verbatim in the SDL error box (HEAD `GameApp.cs:196`), so a player with a missing DLL was told to run a repository script;
a refused GL 3.3 context showed `SDL_GL_CreateContext (3.3 core): <SDL error>` and a missing archive `image/allpac4.nam not found in <folder>`.

**Changed**

* `src/Tsuki.Media/Ffmpeg/FfmpegLibrary.cs:24-36`: `FfmpegMissingException : FileNotFoundException` (`Folder`, `MissingFiles`, `FileName` = the first missing DLL) with the message
  `FFmpeg DLLs missing from <root>: <names>`, no developer suffix (the script is named in the type's doc comment); `Load` throws it (line 81). The check still runs before anything
  is loaded, so a failed `Load` leaves the library unloaded. `--smoke` still prints `FAIL ffmpeg.load FileNotFoundException`-style lines through its own catch (H1.md).
* `src/Tsuki.App/Play/GameApp.cs` (Tsuki.App: an edit outside the core folders, the smallest that puts the sentence where the box is made):
  * `StartupFailure(message, cause) : Exception` (line 120): a start-up failure with the player's sentence; the cause keeps the technical text.
  * `Run`'s catch (line 202) shows `PlayerMessage(e)` instead of `e.Message`; `log.WriteLine("Tsuki failed: " + e)` and `WriteCrashLog(e)` are unchanged, and `Exception.ToString()`
    of a `StartupFailure` includes its cause, so the log and crash.log keep the SDL / .NET text.
  * `PlayerMessage` (line 229): `StartupFailure` -> its message; `FfmpegMissingException` -> `The FFmpeg libraries are missing next to Tsuki.App.exe (<names>). Reinstall the port or
    copy them back.`; anything else -> its own message (unchanged behaviour).
  * `WindowMessage(PlatformException)` (line 237), used by the new try / catch around `GameWindow.Create` (lines 258-265): an SDL message starting with `SDL_GL_` (`SDL_GL_CreateContext
    (3.3 core)`, `SDL_GL_MakeCurrent`, `SDL_GL_SetAttribute`, all thrown by `GameWindow.cs`) -> `Your graphics driver does not provide OpenGL 3.3. Update the driver or run the game
    on the dedicated GPU.`; another SDL call (`SDL_CreateWindow`, `SDL_InitSubSystem`) -> `The game window could not be created. Update your graphics driver or run the game on the
    dedicated GPU.` `GameWindow.cs` itself is not edited: its messages already name the SDL call.
  * `GameFolderMessage(folder, e)` (line 242), used by the new catch of `FileNotFoundException` / `DirectoryNotFoundException` around `GameFolderSource.Open` (lines 292-299):
    `DirectoryNotFoundException` -> `The game folder <folder> no longer exists. Choose the CUSA47999 folder again in the launcher.`; `FileNotFoundException` with a file name
    (`DirectoryGameFiles.Require` passes the relative path, `GameFolderSource` the archive name) -> `The game folder is incomplete: <file> is missing from <folder>. Choose the
    CUSA47999 folder again in the launcher.`; else `The game folder <folder> could not be opened: <message>`. `EbootFormatException` (a foreign eboot) is not mapped: `Open` also
    succeeds on unanalysed builds (`Build.IsAnalysedBuild` is only logged), so no sentence about the version would be safe.
  * The box's second line stays `Details: <crash.log path>`.

**Test** `tests/Tsuki.Tests.Synthetic/App/StartupMessageTests.cs` (new, 4 tests): `FfmpegLibrary.Load(<empty temp folder>)` throws `FfmpegMissingException` naming the four DLLs
in `RequiredDlls` order with `FileName` `avutil-61.dll`, a message without `tools/setup` or `ARCHITECTURE`, `IsLoaded` still false, and the player line is the sentence above with the
four names; the `SDL_GL_CreateContext` and `SDL_GL_MakeCurrent` texts give the OpenGL sentence and `SDL_CreateWindow` the window one, a `StartupFailure` built from them shows the
sentence while its `ToString()` keeps `SDL_GL_CreateContext (3.3 core)`; `GameFolderSource.Open` on an empty folder (the real Tsuki.Data path) gives `eboot.bin is missing from
<folder>`, a synthetic `image/allpac4.nam` one the same shape, a missing folder `no longer exists`, another exception the `could not be opened` form; other exceptions keep their
message. The FFmpeg test cannot compile against HEAD (the type is new); HEAD's message carried the suffix the test rejects (`git show HEAD:src/Tsuki.Media/Ffmpeg/FfmpegLibrary.cs`, line
68).

**Not run end to end**: a real start that fails goes through `WriteCrashLog`, which appends to the user's real `%APPDATA%\Tsuki\crash.log` whatever `--save-dir` says (the robust
area's finding 7; `src/Tsuki.App/Play/CrashLog.cs` appeared in the tree while this ran, so that fixer is on it). The success path is unchanged apart from the two try / catch wrappers;
the hidden `--smoke` run of section 3 exercised `FfmpegLibrary.Load` and `GameWindow.Create` for real.

## 3. Proof

The shared tree did not build during this round because of other fixers' in-flight edits in `src/Tsuki.App` (first `AppServices.cs(153)` `HasAudioCommands`, then `GameApp.cs(459)`:
`AppServices.Commit()` now returns `bool`, so K19's `new DisplaySettings(..., _services.Commit, ...)` no longer converts to `Action`; later also `Program.cs(49)` CS8602;
`scratch/fix-core/build_synthetic*.log`). As the brief says, a scratch copy was built instead: `scratch/fix-core/tree` = the working tree's `src/`, `tests/`, `tools/setup/`, `docs/`
and the two `Directory.*.props` (robocopy without `bin`/`obj`, a junction to `third_party/` for the DLLs, removed afterwards), with one shim in the copy only:
`GameApp.cs:459` `() => _services.Commit()`. Nothing of that shim is in the tree; whoever owns the `Commit()` change must adapt the call site.

| check | result |
|---|---|
| `dotnet build tests/Tsuki.Tests.Synthetic -c Release -warnaserror --artifacts-path artifacts/fix-core/tree` (scratch copy) | 0 warnings, 0 errors (`build_tree_synthetic.log`) |
| `dotnet build tests/Tsuki.Tests.Data -c Release -warnaserror` (scratch copy) | 0 warnings, 0 errors (`build_tree_data.log`) |
| `dotnet test tests/Tsuki.Tests.Synthetic` (scratch copy, whole suite) | **1079 / 1079**, the 7 new tests listed as Passed in `synthetic.trx` (`test_synthetic.log`) |
| the notice tests against HEAD's `THIRD_PARTY.md` + `ffmpeg_dlls.ps1` / against the edited ones | 2 failed, 1 passed / 3 passed (`test_before_thirdparty.log`, `test_after_thirdparty.log`) |
| `dotnet test tests/Tsuki.Tests.Data --filter MovieOpenTests|MovieTests|ShutdownRaceTests` (`TSUKI_GAME_DIR` set, `SDL_AUDIO_DRIVER=dummy`): the tests that call `FfmpegLibrary.Load` with the DLLs present | **14 / 14** in 47 s (`test_data_media.log`, `data_media.trx`) |
| `Tsuki.App.exe --smoke --report scratch/fix-core/smoke.txt` from the scratch build, hidden, dummy audio | `SMOKE PASS (15 checks)`: `ffmpeg.load ... license "LGPL version 3 or later"`, 3 FFmpeg modules from the app folder, atrac9 / h264 / aac found, GL 3.3 core (`smoke.txt`) |
| `tools/setup/ffmpeg_dlls.ps1 -CheckOnly`, then the real run | `the destination matches the package`; 8 files unchanged, `SOURCE.txt` updated (`ffmpeg_dlls_run.log`) |
| `git check-ignore third_party/ffmpeg/SOURCE.txt` | ignored, as before |

## 4. Files

Changed: `docs/THIRD_PARTY.md`, `tools/setup/ffmpeg_dlls.ps1`, `src/Tsuki.Media/Ffmpeg/FfmpegLibrary.cs`, `src/Tsuki.App/Play/GameApp.cs` (outside the core folders: the
`StartupFailure` class, the `Run` catch, three message helpers, two try / catch wrappers in `Play`). New: `tests/Tsuki.Tests.Synthetic/ThirdPartyNoticesTests.cs`,
`tests/Tsuki.Tests.Synthetic/App/StartupMessageTests.cs`, this file. Local only: `third_party/ffmpeg/SOURCE.txt` rewritten by the script. Not edited: `src/Tsuki.Platform/GameWindow.cs`,
`src/Tsuki.Launcher/Views/HelpPage.xaml` (both named by the findings; neither needed a change).

## 5. Open items

1. The shared tree's `Tsuki.App` does not compile until the `Commit()` -> `bool` change and `Program.cs:49` are finished by their owners (section 3). `GameApp.Run`'s catch is being
   touched by the crash-log work too (`CrashLog.cs`): keep `PlayerMessage(e)` as the first line of the box when that lands.
2. `HelpPage.xaml:95` also says the notices are "in the licence files beside the game": `Directory.Build.props` copies the DLLs only, not `LICENSE.txt` / `SOURCE.txt`; whether the
   published build (`artifacts/dist`, not touched here) ships them was not checked. Launcher / dist area.
3. "Choose the CUSA47999 folder again in the launcher": a direct `Tsuki.App.exe` start whose remembered folder lost an archive has no in-game way to re-choose (`ResolveGameFolder`
   asks only when `eboot.bin` is missing); the launcher's Ready check is the way back, and the sentence says so.
