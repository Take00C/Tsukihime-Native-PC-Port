<#
.SYNOPSIS
    Copies the FFmpeg 9.0 LGPL shared DLLs from the winget package folder into
    third_party\ffmpeg\bin, the only place the engine loads FFmpeg from (ARCHITECTURE 0.5).

.DESCRIPTION
    ROADMAP M0 scope 1 / step 4. It never installs anything and never runs winget.

    1. Finds the package BtbN.FFmpeg.LGPL.Shared.9.0: through the winget registry entry
       (HKCU/HKLM ...\Uninstall\BtbN.FFmpeg.LGPL.Shared.9.0_*, InstallLocation), then under the
       portable package roots (winget settings.json, %LOCALAPPDATA%\Microsoft\WinGet\Packages,
       %ProgramFiles%\WinGet\Packages). winget extracts the zip there as
       <root>\BtbN.FFmpeg.LGPL.Shared.9.0_Microsoft.Winget.Source_8wekyb3d8bbwe\
            ffmpeg-n9.0.1-<build>-win64-lgpl-shared-9.0\bin\*.dll
       If the package is absent it prints the install command and exits 1 without touching
       any file.
    2. Verifies the source: the five required DLLs (avcodec-63, avformat-63, avutil-61,
       swresample-7, swscale-10) exist, every DLL's version resource has the major its name
       promises, the build is LGPL (zip folder name, no postproc DLL) and its ffmpeg.exe has the
       atrac9, h264 and aac decoders. Prints the avcodec version.
    3. Copies every *.dll of the package's bin folder (idempotent: a file whose SHA-256 already
       matches is left alone), plus LICENSE.txt, and writes third_party\ffmpeg\SOURCE.txt (package,
       version, SHA-256 of each DLL; no time stamp, so a re-run changes nothing).

    third_party\ is git-ignored and never committed.

.PARAMETER PackageDir
    Use this package folder instead of searching (it must contain ...\bin\avcodec-*.dll).

.PARAMETER Destination
    Target folder; default <project>\third_party\ffmpeg\bin.

.PARAMETER CheckOnly
    Verify the package and compare it with the destination; copy nothing. Exit 0 when the
    destination is complete and identical, 1 otherwise.

.PARAMETER Prune
    Delete DLLs in the destination that the package does not have (left from another FFmpeg
    version). Without it they are only reported.

.EXAMPLE
    pwsh -NoProfile -File tools\setup\ffmpeg_dlls.ps1
    pwsh -NoProfile -File tools\setup\ffmpeg_dlls.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [string]$PackageDir,
    [string]$Destination,
    [switch]$CheckOnly,
    [switch]$Prune
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup_common.ps1')

if (-not $Destination) { $Destination = $TsukiFfmpegDest }
$Destination = [System.IO.Path]::GetFullPath($Destination)
$ffmpegRoot = Split-Path -Parent $Destination        # third_party\ffmpeg

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

try {
    # ---- 1. find the package ----------------------------------------------------------------------
    $pkg = Find-TsukiFfmpegPackage -PackageDir $PackageDir
    if (-not $pkg) {
        Write-Output "The winget package $TsukiFfmpegPackageId was not found."
        Write-Output ''
        Write-Output 'Searched:'
        if ($PackageDir) { Write-Output "  -PackageDir: $PackageDir\[*\[*\]]bin\avcodec-*.dll" }
        else {
            foreach ($a in Get-TsukiArpInstallLocations $TsukiFfmpegPackageId) { Write-Output "  registry entry $($a.Key): $($a.Path) (no bin\avcodec-*.dll there)" }
            foreach ($r in Get-TsukiWingetPortableRoots) { Write-Output "  $r\$TsukiFfmpegPackageId*\...\bin\avcodec-*.dll" }
        }
        Write-Output ''
        Write-Output 'To install it (the user runs this; it is step 1 of ROADMAP section 4):'
        Write-Output "  winget install --id $TsukiFfmpegPackageId -e"
        Write-Output ''
        Write-Output 'winget may warn that the ffmpeg / ffprobe command aliases already exist (Gyan 9.0.2 is'
        Write-Output 'installed). Either outcome is fine: both builds have the atrac9 decoder, and TSUKI_FFMPEG'
        Write-Output 'can pin the Python tools to the Gyan path. Then run this script again.'
        Write-Output ''
        Write-Output 'Nothing was copied.'
        exit 1
    }
    $arp = @(Get-TsukiArpInstallLocations $TsukiFfmpegPackageId | Select-Object -First 1)
    $pkgVersion = $(if ($arp.Count -and $arp[0].Version) { $arp[0].Version } else { 'unknown' })
    Write-Output "package  $TsukiFfmpegPackageId $pkgVersion"
    Write-Output "source   $($pkg.BinDir)"
    Write-Output "target   $Destination"
    Write-Output ''

    # ---- 2. verify the source ---------------------------------------------------------------------
    $problems = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]
    if ($pkg.ZipFolder -notmatch 'lgpl') { $problems.Add("the zip folder '$($pkg.ZipFolder)' is not an LGPL build") }
    $libs = Get-TsukiFfmpegLibVersions (Join-Path $pkg.BinDir 'ffmpeg.exe')
    $srcDlls = @(Get-ChildItem -LiteralPath $pkg.BinDir -Filter '*.dll' -File | Sort-Object Name)
    $known = @{}
    foreach ($d in $TsukiFfmpegDlls) { $known[$d.Name.ToLowerInvariant()] = $d }
    foreach ($d in $TsukiFfmpegDlls) {
        $p = Join-Path $pkg.BinDir $d.Name
        if (-not (Test-Path -LiteralPath $p)) {
            if ($d.Required) { $problems.Add("required $($d.Name) is not in the package") } else { $warnings.Add("optional $($d.Name) is not in the package") }
        }
    }
    $rows = @()
    foreach ($f in $srcDlls) {
        $v = Get-TsukiDllVersion $f.FullName
        $key = $f.Name.ToLowerInvariant()
        $state = 'ok'
        if ($f.Name -like 'postproc-*') { $problems.Add("$($f.Name) is GPL-only; this is not the LGPL package"); $state = 'GPL' }
        elseif ($known.ContainsKey($key)) {
            $d = $known[$key]
            if (-not $v -and $libs -and $libs.ContainsKey($d.Lib)) { $v = $libs[$d.Lib] }
            if (-not $v) { $warnings.Add("$($f.Name) has no version resource"); $state = 'no version' }
            elseif ([int](($v -split '\.')[0]) -ne $d.Major) { $problems.Add("$($f.Name) reports version $v, expected major $($d.Major)"); $state = 'WRONG MAJOR' }
        } else {
            $warnings.Add("$($f.Name) is not one of the expected FFmpeg 9.0 DLLs (copied anyway)"); $state = 'unexpected'
        }
        $rows += [pscustomobject]@{ Name = $f.Name; Version = $v; Size = $f.Length; State = $state; Path = $f.FullName }
    }
    foreach ($r in $rows) { Write-Output ('  {0,-18} {1,-10} {2,12:N0} B  {3}' -f $r.Name, $r.Version, $r.Size, $r.State) }
    $exe = Join-Path $pkg.BinDir 'ffmpeg.exe'
    if (Test-Path -LiteralPath $exe) {
        $dec = Invoke-TsukiNative -FilePath $exe -ArgumentList @('-hide_banner', '-decoders') -TimeoutSeconds 30
        foreach ($name in 'atrac9', 'h264', 'aac') {
            if ($dec.StdOut -notmatch "(?m)^\s*\S+\s+$name\s") { $problems.Add("the package's ffmpeg.exe has no $name decoder") }
        }
    } else {
        $warnings.Add('the package has no ffmpeg.exe; decoders not checked')
    }
    $avcodec = @($rows | Where-Object { $_.Name -like 'avcodec-*' } | Select-Object -First 1)
    Write-Output ''
    if ($avcodec.Count) { Write-Output "avcodec  $($avcodec[0].Version) ($($avcodec[0].Name))" }
    if ($libs) { Write-Output "build    $($libs['banner'])" }
    foreach ($w in $warnings) { Write-Output "warning: $w" }
    if ($problems.Count) {
        foreach ($p in $problems) { Write-Output "error: $p" }
        Write-Output 'The package failed verification; nothing was copied.'
        exit 1
    }

    # ---- 3. compare with / copy into the destination ----------------------------------------------
    $license = Join-Path (Split-Path -Parent $pkg.BinDir) 'LICENSE.txt'
    $plan = @()
    foreach ($r in $rows) {
        $dst = Join-Path $Destination $r.Name
        $action = 'copy'
        if (Test-Path -LiteralPath $dst) { $action = $(if ((Get-Sha256 $dst) -eq (Get-Sha256 $r.Path)) { 'unchanged' } else { 'update' }) }
        $plan += [pscustomobject]@{ Src = $r.Path; Dst = $dst; Action = $action }
    }
    if (Test-Path -LiteralPath $license) {
        $dst = Join-Path $ffmpegRoot 'LICENSE.txt'
        $action = 'copy'
        if (Test-Path -LiteralPath $dst) { $action = $(if ((Get-Sha256 $dst) -eq (Get-Sha256 $license)) { 'unchanged' } else { 'update' }) }
        $plan += [pscustomobject]@{ Src = $license; Dst = $dst; Action = $action }
    } else {
        $warnings.Add('the package has no LICENSE.txt next to bin\')
    }
    $stale = @()
    if (Test-Path -LiteralPath $Destination) {
        $names = @($rows | ForEach-Object { $_.Name.ToLowerInvariant() })
        $stale = @(Get-ChildItem -LiteralPath $Destination -Filter '*.dll' -File | Where-Object { $names -notcontains $_.Name.ToLowerInvariant() })
    }

    # SOURCE.txt: provenance for the licence notices (M8); deterministic content.
    $srcLines = @(
        '# FFmpeg DLLs copied by tools/setup/ffmpeg_dlls.ps1. Local only; never committed.',
        "package: $TsukiFfmpegPackageId $pkgVersion",
        "build: $(if ($libs) { $libs['banner'] } else { 'unknown' })",
        "zip folder: $($pkg.ZipFolder)",
        'licence: LGPL v3 or later (an --enable-version3 build: avcodec_license() answers "LGPL version 3 or later"; LICENSE.txt is the package''s own copy of the LGPL v3 text)',
        'files (name, version, size, sha256):'
    )
    foreach ($r in $rows) { $srcLines += ('  {0} {1} {2} {3}' -f $r.Name, $r.Version, $r.Size, (Get-Sha256 $r.Path)) }
    $sourceTxt = Join-Path $ffmpegRoot 'SOURCE.txt'
    $sourceText = ($srcLines -join "`r`n") + "`r`n"
    $sourceAction = 'copy'
    if (Test-Path -LiteralPath $sourceTxt) { $sourceAction = $(if ((Get-Content -LiteralPath $sourceTxt -Raw) -eq $sourceText) { 'unchanged' } else { 'update' }) }

    Write-Output ''
    if ($CheckOnly) {
        foreach ($p in $plan) { Write-Output ('  {0,-12} {1}' -f $(if ($p.Action -eq 'unchanged') { 'in sync' } else { "needs $($p.Action)" }), $p.Dst) }
        foreach ($s in $stale) { Write-Output "  stale        $($s.FullName) (not in the package)" }
        $dirty = @($plan | Where-Object { $_.Action -ne 'unchanged' }).Count + $stale.Count
        Write-Output $(if ($dirty) { "check only: $dirty file(s) differ; run without -CheckOnly to copy." } else { 'check only: the destination matches the package.' })
        exit $(if ($dirty) { 1 } else { 0 })
    }

    if (-not (Test-Path -LiteralPath $Destination)) { New-Item -ItemType Directory -Path $Destination | Out-Null }
    foreach ($p in $plan) {
        if ($p.Action -ne 'unchanged') {
            Copy-Item -LiteralPath $p.Src -Destination $p.Dst -Force
            if ((Get-Sha256 $p.Dst) -ne (Get-Sha256 $p.Src)) { throw "copy of $($p.Src) did not verify" }
        }
        Write-Output ('  {0,-9} {1}' -f $(if ($p.Action -eq 'copy') { 'copied' } elseif ($p.Action -eq 'update') { 'updated' } else { 'unchanged' }), $p.Dst)
    }
    if ($sourceAction -ne 'unchanged') { [System.IO.File]::WriteAllText($sourceTxt, $sourceText, (New-Object System.Text.UTF8Encoding($false))) }
    Write-Output ('  {0,-9} {1}' -f $(if ($sourceAction -eq 'copy') { 'written' } elseif ($sourceAction -eq 'update') { 'updated' } else { 'unchanged' }), $sourceTxt)
    foreach ($s in $stale) {
        if ($Prune) { Remove-Item -LiteralPath $s.FullName; Write-Output "  removed   $($s.FullName) (not in the package)" }
        else { Write-Output "  stale     $($s.FullName) (not in the package; -Prune removes it)" }
    }
    foreach ($w in $warnings) { Write-Output "warning: $w" }
    $n = @($plan | Where-Object { $_.Action -ne 'unchanged' }).Count
    Write-Output ''
    Write-Output "done: $n file(s) copied or updated, $(@($plan).Count - $n) unchanged."
    exit 0
} catch {
    Write-Output "ffmpeg_dlls.ps1 failed: $($_.Exception.Message)"
    Write-Output $_.ScriptStackTrace
    exit 2
}
