# Publishing

This guide covers source publication and Windows/Linux/macOS release archives. Build prerequisites and
Python tooling are in [CONTRIBUTING.md](../CONTRIBUTING.md#2-how-to-build).
Run commands from the repository root, using PowerShell on Windows and Bash on Linux or macOS.

## 0. The short version

1. Review the intended source revision and its licences. Keep game data and personal files out.
2. Build and test that revision, then publish into a fresh folder without `TsukiPersonalIcon`.
3. Inspect the complete folder, run `release-check`, archive it, and verify an extracted copy.
4. Draft release notes with the exact commit, dependencies and verification limits. Check the
   repository and tag before uploading the reviewed archives and matching dependency source.

The source repository and release attachments are separate. Do not commit build output.
A successful `release-check` detects personal-build markers; it does not certify package completeness,
licence compliance, decoder security or gameplay.

## 1. What to upload

### 1.1 The source repository

Publish the source, tests, build/setup files, documentation, approved branding and licence texts.
Do not include game content in fixtures or examples. Preserve format evidence and third-party credits.
Use the existing repository and history for normal updates; the first publication's cleanup is not
an instruction to recreate or rewrite the repository for every release.

### 1.2 The player download

Keep the full published folder together. The publish definitions are
`src/Tsuki.Distribution.targets`, `src/Tsuki.Launcher/Tsuki.Launcher.csproj`,
`tools/setup/publish_linux.sh` and `tools/setup/package_macos.py`.

| Package | Required contents |
|---|---|
| Shared | Player and managed libraries, self-contained .NET runtime, SDL3 and bindings, `README.md` with its logo, `CONTRIBUTING.md`, `LICENSE`, `docs/SETUP_GUIDE.md`, `docs/LINUX.md`, `docs/MACOS.md`, `docs/THIRD_PARTY.md`, and `licenses/` |
| Windows | `Tsukihime.exe`, `TsukihimeLauncher.exe`, their managed assemblies and WPF runtime, `SETUP_GUIDE.txt`, `avutil-61.dll`, `swresample-7.dll`, `avcodec-63.dll`, `avformat-63.dll` |
| Linux | `TsukihimeLauncher` and `Tsukihime` with their managed assemblies, the Avalonia runtime, `libSDL3.so`, `libavutil.so.61`, `libswresample.so.7`, `libavcodec.so.63`, `libavformat.so.63`, and root `LINUX.md`. No WPF launcher. |
| macOS | One `Tsukihime.app` whose `CFBundleExecutable` is `TsukihimeLauncher`, with `Tsukihime` beside it: programs and dylibs under `Contents/MacOS`, documents, logo, crescent ICNS and licences under `Contents/Resources`, plus `Contents/Info.plist`. Include the Avalonia runtime, `libSDL3.dylib` and the four FFmpeg 9 dylibs. No WPF launcher. |

Local player-document links must resolve inside every archive, so each carries the README's logo
(`assets/branding/tsukihime-logo.png`, below `Contents/Resources` on macOS); `release-check` and `release-sums` refuse any other picture or game data, apart from the Mac crescent ICNS. Developer links to
unbundled references use full repository URLs. Do not move essential offline instructions to such a link.
Check both copies of the setup guide when changing player instructions.

Current source versions are **0.1.7** for the player and both launchers. Recent self-contained evaluation resolved
**.NET 10.0.13** and **SDL 3.5.0**; check the actual resolved versions for each release.
Check [Releases](https://github.com/Take00C/Tsukihime-Native-PC-Port/releases) for published downloads.
Source builds and fork releases may differ from the published packages.

### 1.3 What never goes up

- Game/base/update folders, `eboot.bin`, archives, fonts, movies, audio, decoded pictures or story text.
- Saves, profiles, logs with private data, runtime game icons and screenshots of the game.
- `samples/`, `scratch/`, `ref/`, `third_party/`, `.venv/`, `artifacts/`, `bin/` or `obj/` in the source
  repository. Release attachments contain only the reviewed publish output, not these working folders.
- A personal-icon build, even if its marker file has been removed.

The pre-commit guard rejects specified folders, game-data extensions, files over 5 MiB and paths outside
its allow-list. It is a guard, not a complete content review; GitHub web uploads bypass it.
See [Sending a change](../CONTRIBUTING.md#4-sending-a-change) for hook setup.

### 1.4 Personal builds never go out (package K28)

Ordinary programs embed the port's crescent. At run time, the player's own files can supply the game
window and shortcut icon under their profile. A private Windows build can also embed that game art in
both executables:

```powershell
dotnet build src/Tsuki.slnx -c Release -p:TsukiPersonalIcon=D:\Games\CUSA47999
```

The same property works with `dotnet publish`. It builds `Tsuki.Cli`, runs `face-icon` into the program's
ignored intermediate folder and fails if no usable picture is found. Such binaries contain game art and
must never leave the owner's machine. Omit the property and use a fresh output folder for distribution.

Personal builds carry `PERSONAL_BUILD.txt`, assembly metadata `TsukiPersonalBuild=true`, a version
containing `-personal`, and an executable icon different from `assets/icon/tsukihime.ico`.
The checker inspects these marks and the actual icon group. On Linux and macOS it checks the icons in both
managed assemblies. A wrong path or missing player is a failure.

```powershell
dotnet run --project src/Tsuki.Cli -c Release -- release-check artifacts/release-win-x64
```

Require exit **0** and `RESULT: ... may be released` before archiving. `--icon <file>` can explicitly
select the repository's crescent icon; `--dump <folder>` writes the checked icon frames for local
inspection. Never substitute a game-derived icon as the checker reference or share dumped personal art.

## 2. Release preflight

Before building a distributable candidate:

1. Review the intended files and source history for game content, private paths, account details,
   credentials and accidental large files. Check approved images and their embedded metadata.
   Keep audit reports outside the repository. Do not replace format evidence or licence notices with
   shortened summaries. Do not include story lines in synthetic tests.
2. Confirm the destination repository, public commit identity and version. Set the same `<Version>` in
   `src/Tsuki.App`, `src/Tsuki.Launcher` and `src/Tsuki.Launcher.Avalonia`, and in the version lines of README,
   section 1.2 and both setup guides; `ProjectRulesTests` checks that they agree. Preserve existing history
   unless a specific correction requires otherwise.
3. Review `LICENSE`, `docs/THIRD_PARTY.md`, translated-file headers and dependency provenance.
   Verify packaged notices come from tracked files and the runtime packs actually resolved by the SDK.
4. Follow the build and Python prerequisites in CONTRIBUTING. Use an isolated supported Python
   environment, pinned binary wheels, the security gate and a fresh dependency audit. Do not install
   research tools into the system interpreter.
5. Build warning-free and run the Synthetic, Data, Render, Avalonia and Python test programs. Data/GPU checks
   need their documented inputs; record missing-input and platform skips separately from passes.
   Use `TSUKI_GAME_DIR=/nonexistent` for checks intended to exclude retail data.
6. Finish the intended source commit before the final publish. Record that commit outside the source
   tree with the candidate results. If source changes afterwards, rebuild and repeat affected checks.

Do not use historical test counts, old archive sizes or a prior security audit as proof for a new build.

## 3. Licensing

### 3.1 The port's source

The source is **MPL-2.0**, with the full text in root `LICENSE`. Keep source available for the version
being distributed. Include the port's licence in the package and retain the licence and no-game-data
statements in the README, launcher Help and release notes.

### 3.2 The knife-derived files stay MPL-2.0

Keep the MPL headers, AuroraWright's credit and every translated-file row in `docs/THIRD_PARTY.md`.
`ThirdPartyNoticesTests` checks that table against the source. Ship `licenses/knife-MPL-2.0.txt`.
The credited reference repositories remain links, not copied working trees in the source archive.

### 3.3 FFmpeg

For Windows, use the verified FFmpeg 9 LGPL shared build copied by `tools/setup/ffmpeg_dlls.ps1`.
The helper copies the installed `BtbN.FFmpeg.LGPL.Shared.9.0` package; it never downloads or installs it.
If absent, it prints the `winget install` command and stops. Its `-CheckOnly` option compares the
installed package with the local DLL copies without copying. `-PackageDir` selects a package explicitly;
`-Prune` removes stale destination DLLs, so use it only after reviewing those files.

Keep FFmpeg dynamically replaceable. For Windows and Linux, include:

- `licenses/FFmpeg-LGPL.txt` and `licenses/FFmpeg-GPL-3.0.txt`. LGPL v3 incorporates the GPL v3 terms;
  including both texts does not mean the selected libraries contain GPL-only components.
- `licenses/FFmpeg-SOURCE.txt` naming the **exact build shipped**, its source and build provenance.
  Compare this tracked notice with the helper's local `third_party/ffmpeg/SOURCE.txt` after updates.
- The FFmpeg notice in `licenses/THIRD_PARTY.md` and the matching source for the distributed build.
  Prefer attaching the matching source archive to the release, and retain a copy so source availability
  does not depend on a disappearing upstream link. Do not claim a source copy is available on request
  unless you can supply it. Do not reuse an old tag or source archive for newer DLLs.

For Linux, extract BtbN's `ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0.tar.xz` to
`third_party/ffmpeg/linux-src/` and run `python -I tools/setup/ffmpeg_linux.py`. It never downloads: it checks
the build (LGPL only, the library names, the three decoders through `bin/ffmpeg`, on Windows through WSL)
and copies the four libraries, `LICENSE.txt` and a generated `FFmpeg-SOURCE.txt` naming that build into
`third_party/ffmpeg/linux/`. A linux-x64 publish bundles them and stops with `TSUKI002` without them.
Linux alone supports `-p:TsukiBundleFFmpeg=false` for a system FFmpeg 9. Do not copy Windows provenance into a Linux package.

For macOS, run `bash tools/setup/ffmpeg_macos.sh arm64` or `x64` on the matching Mac architecture; Intel also needs `nasm`.
The script fetches `https://git.ffmpeg.org/ffmpeg.git` at depth 1 for `TSUKI_FFMPEG_REF` (default `n9.0.2`, commit
`946fcce07b6dcd0331c8cc609192aeff5e1924f8`) and builds that exact commit from a fresh `git archive` extraction. It makes
a minimal macOS 15 build: shared LGPL 2.1-or-later libraries, pthreads, `mov`/`file`, H.264/AAC parsers and ATRAC9/H.264/AAC
decoders, with no GPL, nonfree, network or external codec libraries. It checks the decoder list, thin Mach-O architecture,
`@loader_path` install names and absence of Homebrew dependencies. `third_party/ffmpeg/macos-ARCH/` receives the four dylibs,
FFmpeg licence, LGPL text and source notice with a neutral configure command, no builder home paths and pre-signing hashes;
an osx publish stops with `TSUKI003` if they are absent. macOS always uses these bundled dylibs.

### 3.4 SDL3, bindings and .NET

Keep the complete notices in `docs/THIRD_PARTY.md` for SDL3, SDL3-CS, Silk.NET, FFmpeg.AutoGen and .NET.
Ship `licenses/SDL3-zlib.txt` and `licenses/THIRD_PARTY.md`.
For self-contained publishes, `Tsuki.Distribution.targets` takes `licenses/dotnet-LICENSE.txt` and
`licenses/dotnet-THIRD-PARTY-NOTICES.txt` from the **resolved Microsoft.NETCore.App runtime pack**.
Windows WPF runtime notices must also remain in the published folder. Check the actual pack version and
contents; the binding notices alone do not replace runtime dependency notices.

Unix publishes also include Avalonia's MIT notice, the SkiaSharp, HarfBuzzSharp, MicroCom and Tmds.DBus MIT notices, and
`licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt` from the restored SkiaSharp 2.88.9 package. The Windows launcher does not use Avalonia.

With the ReadyToRun publish of section 4.2, the launcher adds about 13.5 MB to the Linux tar.gz (about 33 MB unpacked).
Windows has no Avalonia dependency.

### 3.5 Disclaimer and public documentation

State that this is an unofficial fan program, unaffiliated with TYPE-MOON, Aniplex, HuneX or Sony;
TSUKIHIME and its assets belong to their rights holders. The program contains no game data. Players
must supply their own files; the repository does not provide them or links to sources of them.
Keep download instructions, requirements, setup-guide links, controls and problem-reporting instructions
reachable. Do not attach game files, saves, story text or game screenshots to reports or release notes.

## 4. Build, check and archive

Use fresh candidate and verification folders. If the example paths already contain an earlier build,
choose new names rather than mixing outputs. Wait for publish to finish before inspecting it.
Never set `TsukiPersonalIcon` for these commands. Publish from a fresh clone or worktree: an older checkout can
keep CRLF copies of the licence texts, which the Python checks report with the command that refreshes them.

### 4.1 Windows

Complete [Windows prerequisites](../CONTRIBUTING.md#2-how-to-build), including real FFmpeg DLLs, first.
Run the Python checks from [Python tools](../CONTRIBUTING.md#python-tools) as well as these commands:

```powershell
$env:TSUKI_GAME_DIR = "/nonexistent"
dotnet build src\Tsuki.slnx -c Release
dotnet test tests\Tsuki.Tests.Synthetic -c Release
dotnet test tests\Tsuki.Tests.Render -c Release
dotnet test tests\Tsuki.Tests.Avalonia -c Release -p:TsukiUsesFFmpeg=false
$env:TSUKI_GAME_DIR = "D:\path\to\CUSA47999"
dotnet test tests\Tsuki.Tests.Data -c Release
New-Item -ItemType Directory -Force artifacts | Out-Null
dotnet publish src\Tsuki.Launcher -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts\release-win-x64
dotnet run --project src\Tsuki.Cli -c Release -- release-check artifacts/release-win-x64
```

The Data run only reads your game folder; it must show 0 failed and 0 skipped, because only its game tests see
Windows window-mode and log changes. Warnings are errors by default. Releases are ReadyToRun builds, as v0.1.6 was.
A Release publish already leaves out debug symbols, so `-p:DebugType=none` is not needed. A Windows cross-publish still needs the real DLLs;
`TSUKI001` means prerequisites are missing, not that substituting placeholders is acceptable.

Inspect the package and licences in sections 1 and 3, including no personal markers or stale `.pdb`/
program XML documentation. Then archive and extract:

```powershell
Compress-Archive -Path artifacts\release-win-x64\* -DestinationPath artifacts\Tsukihime-vX.Y.Z-win-x64.zip -CompressionLevel Optimal
Expand-Archive -Path artifacts\Tsukihime-vX.Y.Z-win-x64.zip -DestinationPath artifacts\verify-win-x64
dotnet run --project src\Tsuki.Cli -c Release -- release-check artifacts/verify-win-x64
```

Use the intended version in the filename. Compare the extracted file list and bytes with the publish
folder. On Windows, check the extracted launcher, first-run setup, Help guide and logs. Keep any test
profile separate from personal saves using launcher `--data` or player `--save-dir`; record exactly
which checks were performed. A cross-build alone does not verify Windows runtime behavior.

### 4.2 Linux

Prepare FFmpeg as in section 3.3. The archive can be cross-published from Windows (on Linux,
`bash tools/setup/publish_linux.sh <folder>` does the publish). The first linux-x64 restore downloads the .NET
runtime, apphost and ReadyToRun compiler packs for linux-x64, plus the launcher's Avalonia, SkiaSharp and HarfBuzzSharp packages.

```powershell
dotnet publish src\Tsuki.Launcher.Avalonia -c Release -r linux-x64 --self-contained -p:PublishReadyToRun=true -o artifacts\release-linux-x64
dotnet run --project src\Tsuki.Cli -c Release -- release-check artifacts/release-linux-x64
python -I tools\setup\package_linux.py artifacts\release-linux-x64 --version X.Y.Z --out artifacts
```

`package_linux.py` writes `Tsukihime-vX.Y.Z-linux-x64.tar.gz` with one `Tsukihime-vX.Y.Z-linux-x64/` folder
and Unix modes (0755 for `TsukihimeLauncher`, `Tsukihime` and the `.so` files). Extract it on Linux or in WSL and run
`SDL_VIDEODRIVER=offscreen SDL_AUDIODRIVER=dummy ./Tsukihime --smoke --report /tmp/tsuki-smoke.txt`;
its FFmpeg rows must pass. Then `python3 -I tools/tests/update_e2e.py <the tar.gz> --work /tmp/tsuki-update-e2e`
must end with `UPDATE E2E PASS`: the archive's own launcher updates itself from a loopback copy of GitHub and restarts.
Compare extracted files and executable permissions with the publish folder. Check all required library
names and decoders against the actual loaded dependencies, not just an installed `ffmpeg` executable.
Verify offline documentation and the native folder picker/settings on a supported desktop when available.
Keep skipped display or retail checks explicit.

### 4.3 macOS

Prepare the architecture's FFmpeg build as in section 3.3, then publish and package on that Mac architecture.
`package_macos.py` verifies the thin apphost, assembles `Tsukihime.app`, generates the crescent ICNS from the tracked
PNGs, ad-hoc signs with `codesign --force --deep --prefix io.github.take00c.tsukihime. -s -` (so the game host's identifier
is `io.github.take00c.tsukihime.Tsukihime`, the one the release rules demand), and verifies the signature. `--zip` uses
`ditto -c -k --sequesterRsrc --keepParent`, so signatures kept in extended attributes travel as `__MACOSX/` entries;
it is deliberately macOS-only.

```sh
dotnet publish src/Tsuki.Launcher.Avalonia -c Release -r osx-arm64 --self-contained true \
  -p:PublishReadyToRun=true -o artifacts/release-osx-arm64 --nologo
python3 -I tools/setup/package_macos.py artifacts/release-osx-arm64 \
  --version X.Y.Z --arch arm64 --out artifacts/package-macos-arm64 --zip
dotnet run --project src/Tsuki.Cli -c Release -- \
  release-check artifacts/package-macos-arm64/Tsukihime.app
```

For Intel, use `osx-x64` and `x64`; the two ZIPs are distinct thin builds, not a universal app. Check the extracted app,
native smoke report, documents and licences. Test rendering, sound-device changes, sleep/resume and Gatekeeper on real Macs;
ad-hoc signing is not notarization.

Without a Mac:

1. Put the reviewed, version-bumped source on `main` (section 5's push, or GitHub's upload page); **Run workflow**
   appears only for workflows on `main`. Then open **Actions > macOS experimental downloads > Run workflow** on `main`.
   No tag exists yet: the release in section 5 creates it on that commit.
2. Read each job's summary. A good run starts with the version and ZIP name, and shows the release commit as
   `Source commit`, `Tsukihime signature: Identifier=io.github.take00c.tsukihime.Tsukihime`, signature-attribute and
   `__MACOSX` counts in the hundreds (0 means the signatures were not stored), `SELFTEST PASS`, `SMOKE PASS`,
   `UPDATE E2E PASS` and the ZIP's SHA-256. The two macOS 26 jobs must pass too; both window smokes are informational.
   A failed job leaves `mac-diagnostics-ARCH-not-for-release` (macOS 26: `mac26-diagnostics-ARCH-not-for-release`), which is
   never attached. The updater check runs after the uploads, so a run that failed only there still has its ZIPs: read why
   (its summary ends with the launcher's logs) before deciding to release them.
3. Download both ZIPs within three days. In PowerShell, `(Get-FileHash -Algorithm SHA256 <zip>).Hash` must equal the
   summary's SHA-256 (ignoring case).
4. Run `release-sums` once with the Windows ZIP, Linux tar.gz and both Mac ZIPs.
5. Attach all four archives and `SHA256SUMS.txt` to the release. The workflow does not push or create a GitHub release.

For structural validation of a cross-publish on another OS, omit `--zip` and pass `--no-sign`. This intentionally
produces an unsigned app for inspection only. It neither compiles Mach-O FFmpeg libraries nor substitutes for the
Mac signing and runtime checks above.

### 4.4 Checksums

```powershell
dotnet run --project src\Tsuki.Cli -c Release -- release-sums artifacts\Tsukihime-vX.Y.Z-win-x64.zip artifacts\Tsukihime-vX.Y.Z-linux-x64.tar.gz artifacts\Tsukihime-vX.Y.Z-macos-arm64.zip artifacts\Tsukihime-vX.Y.Z-macos-x64.zip
```

It writes `SHA256SUMS.txt` (one line per file) beside them and refuses an archive the launcher or these rules
would refuse. Mac ZIPs must hold one `Tsukihime.app`, plus only the `__MACOSX/` entries that carry its signature
attributes, with safe paths, ordinary files, correct Unix modes and the matching thin architecture. Each launcher
selects only its own asset: Windows `win-x64.zip`, Linux `linux-x64.tar.gz`, or Mac `macos-arm64.zip`/`macos-x64.zip`
from the process architecture (x64 under Rosetta). Its own `SHA256SUMS.txt` line must match; none of the four assets
replaces another platform's. Zip a fresh Windows publish folder, never one the launcher ran in: it writes Arcueid into
its programs, and release programs must carry the crescent.

## 5. Publish the reviewed candidate

For an existing repository, confirm its owner and release destination before uploading. A fork release
is not an upstream release. Choose a new version tag on the exact source commit used for the binaries;
do not move an existing published tag to disguise a different build.

For a new destination only, create a private repository without generated README/licence files and use
GitHub's private commit email setting.

For every release, use `main` at the reviewed source commit used to build the binaries. Confirm that
`origin` points to the intended release repository with `git remote -v`. If the intended remote is missing,
add it with `git remote add origin <repository-url>`. Substitute the correct remote name throughout if it
is not `origin`.
Run `git push origin main`, then compare `git rev-parse HEAD` with the commit ID from
`git ls-remote origin refs/heads/main`. Stop on command failure or mismatched IDs.

For a new destination, inspect the uploaded README, links, licence and commit identity before changing
visibility. The pre-commit guard does not cover web uploads.

In the intended repository's **Releases > Draft a new release**:

1. Select the intended tag and commit, and a title matching the version.
2. Add release notes and mark a prerelease if appropriate to the build's status.
3. Attach the reviewed Windows ZIP, Linux archive, both macOS ZIPs and `SHA256SUMS.txt`, plus matching dependency source where needed.
   Wait for uploads to complete and check the filenames and package contents.
4. Review the draft, then publish when release publication is authorized. Verify the README download
   destination and the uploaded assets. A private repository's release remains private until visibility changes.

Release notes should include what changed, the source commit, OS/native requirements, own-game-data
requirement, installation/update steps, offline guide location, basic controls, profile/backup details,
known limits, how to report problems, credits/licences and exact FFmpeg provenance/source availability.
Record archive size and checksum outside the source revision used to build it. Report test commands,
passes, skips and unavailable checks accurately.

If private data was exposed, revoke any leaked credential and arrange repository/history cleanup.
Changing visibility alone cannot remove copies in caches or forks. Handle any rights-holder notice
through the host's process, retaining the licence and provenance records.

## 6. Verification limits

A source build, synthetic tests and a smoke run establish different things. They do not prove retail
playthrough, every controller or monitor, Windows runtime behavior from Linux, or compatibility with
untested distributions. Keep these distinctions in release notes.

The game folder supports linked assets and is not a sandbox. Movie input is restricted to local
MP4/MOV handling with H.264 and optional AAC, without external data references; that is not a decoder
security guarantee. The known FFmpeg 9.0.2 PSSH denial-of-service exposure remains unresolved in the
reviewed native dependency. Do not describe the dependency checks as full security clearance.
