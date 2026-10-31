# docs/status

Status files of the port, one per milestone or topic (ROADMAP 1 and 3). They hold **numbers,
commands, test names and engine identifiers only**: no game text, no pictures, no extracted
tables. Frames, traces and diffs go to `artifacts/M<n>/`, PS4 captures to `samples/ps4/`, and
both stay local.

| file | what it is | who updates it |
|---|---|---|
| [`M0.md`](M0.md) | checklist of the M0 scope and acceptance items (ROADMAP M0) with the current state, the commands that prove each item and the measured environment | H writes it; L re-runs the commands and accepts M0 (ROADMAP 4 step 9) |
| [`spikes.md`](spikes.md) | template for spikes S1-S4: inputs, the exact measurements each spike must record, pass thresholds, fallbacks | E fills S1 / S2, D fills S3 / S4 |
| [`ps4_captures.md`](ps4_captures.md) | the screenshots and short videos the user is asked to take on the PS4: effect checkpoints before M4 and menu captures before M7, with script locations and what to look for | L / H; the user ticks items off |

Stream status files (`docs/status/<stream>.md`, ROADMAP 1) and `overview.md` (stream L) are
added by their owners.

## Commands used by these files

All commands run from the repository root in PowerShell.

```powershell
# environment check (reads only; installs nothing). Exit 0 = nothing required is missing.
pwsh -NoProfile -File tools\setup\check_env.ps1            # about 10 s; collects the pytest suite
pwsh -NoProfile -File tools\setup\check_env.ps1 -RunTests  # also runs the suite (about 2 min)

# FFmpeg DLLs: winget package folder -> third_party\ffmpeg\bin (idempotent; -CheckOnly copies nothing)
pwsh -NoProfile -File tools\setup\ffmpeg_dlls.ps1

# Python reference suite
$env:PYTHONPATH = "tools"; .venv\Scripts\python.exe -m pytest tools\tests -q

# where the PS4 capture checkpoints are in the scripts (numbers only; --show-text ja adds
# scene titles and nearby lines for the person at the console: local terminal only)
.venv\Scripts\python.exe tools\setup\ps4_capture_targets.py --limit 1
```

The scripts also run under Windows PowerShell 5.1
(`powershell -NoProfile -ExecutionPolicy Bypass -File ...`).

## Where winget puts the FFmpeg DLLs

`winget install --id BtbN.FFmpeg.LGPL.Shared.9.0 -e` is a zip package. winget extracts it to
`%LOCALAPPDATA%\Microsoft\WinGet\Packages\BtbN.FFmpeg.LGPL.Shared.9.0_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-n9.0.1-<build>-win64-lgpl-shared-9.0\`
(user scope, the default; `%ProgramFiles%\WinGet\Packages\` with `--scope machine`, or the
`portablePackageUserRoot` / `portablePackageMachineRoot` of winget's `settings.json`), with
the DLLs in `bin\`, and records the folder as `InstallLocation` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\BtbN.FFmpeg.LGPL.Shared.9.0_Microsoft.Winget.Source_8wekyb3d8bbwe`.
`tools/setup/setup_common.ps1` (`Find-TsukiFfmpegPackage`) looks in that order: registry
entry, then the portable roots. On 2026-10-25 the installed build was
`ffmpeg-n9.0.1-11-ge47273f4d9-win64-lgpl-shared-9.0` (registry version 9.0.1-20260831). It
holds `avcodec-63`, `avformat-63`, `avutil-61`, `swresample-7`, `swscale-10`, `avfilter-12`
and `avdevice-63` (all x.1.101) plus `LICENSE.txt` one level above `bin\`.

The package did not replace the `ffmpeg` / `ffprobe` command aliases. They still point at
Gyan 9.0.2 (`%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe`), which the Python tools use.
