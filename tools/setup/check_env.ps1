<#
.SYNOPSIS
    Checks the development environment of the TSUKIHIME port and reports what is missing.
    It never installs, downloads or changes anything.

.DESCRIPTION
    ROADMAP M0 scope 1 / step 4. Checked, in this order:

      .NET      the .NET 10 SDK (dotnet --list-sdks)
      FFmpeg    the winget package BtbN.FFmpeg.LGPL.Shared.9.0 (found through the winget
                registry entry and the portable package roots), its DLL names and versions,
                the copies in third_party\ffmpeg\bin, and the ffmpeg the Python tools use
      Game      TSUKI_GAME_DIR, and that it is the analysed CUSA47999 build: param.sfo title id,
                eboot.bin size and SHA-256 (ROADMAP M0 acceptance 2), the archives and movies.
                The folder is only read.
      Python    .venv, the packages numpy / pillow / pytest / numba / capstone / unicorn, the
                project packages, and the pytest suite (collected; run with -RunTests)
      Disk      free space on the project drive and on the system drive
      GPU       adapters, and an OpenGL context probe in a child process (skip with -NoOpenGL)
      Repo      git, .gitignore entries of M0 scope 2, hooks, golden slice manifest (info only)

    Status per line: OK, MISSING (required, absent), FAIL (required, present but wrong),
    WARN (works, but needs attention), INFO.

    Exit code: 0 when nothing is MISSING or FAIL, 1 otherwise (the list is printed at the end),
    2 when the script itself failed.

.PARAMETER RunTests
    Run the whole pytest suite (about 2 minutes) instead of only collecting it.

.PARAMETER NoOpenGL
    Skip the OpenGL probe (it opens a hidden 16 x 16 window in a child process for a moment).

.PARAMETER Json
    Also write the results to this JSON file (paths, versions and hashes only; no game data).

.EXAMPLE
    pwsh -NoProfile -File tools\setup\check_env.ps1
    pwsh -NoProfile -File tools\setup\check_env.ps1 -RunTests -Json artifacts\M0\check_env.json
#>
[CmdletBinding()]
param(
    [switch]$RunTests,
    [switch]$NoOpenGL,
    [string]$Json
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'setup_common.ps1')

$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [Parameter(Mandatory = $true)][string]$Area,
        [Parameter(Mandatory = $true)][string]$Item,
        [Parameter(Mandatory = $true)][ValidateSet('OK', 'MISSING', 'FAIL', 'WARN', 'INFO')][string]$Status,
        [string]$Detail = '',
        [string]$Fix = ''
    )
    $results.Add([pscustomobject]@{ Area = $Area; Item = $Item; Status = $Status; Detail = $Detail; Fix = $Fix })
}

function Format-Bytes([double]$n) {
    if ($n -ge 1GB) { return ('{0:N1} GB' -f ($n / 1GB)) }
    if ($n -ge 1MB) { return ('{0:N1} MB' -f ($n / 1MB)) }
    return ('{0:N0} B' -f $n)
}

$started = Get-Date

# ==================================================================================================
# .NET
# ==================================================================================================
function Test-DotNet {
    $area = '.NET'
    $dotnet = $null
    $cmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $dotnet = $cmd.Source }
    elseif ($env:ProgramFiles -and (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))) {
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    }
    $fix = 'winget install --id Microsoft.DotNet.SDK.10 -e   (user, ROADMAP 4 step 1)'
    if (-not $dotnet) {
        Add-Result $area '.NET 10 SDK' 'MISSING' 'dotnet.exe not found on PATH or in %ProgramFiles%\dotnet' $fix
        return
    }
    $envs = @{ DOTNET_NOLOGO = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1' }
    $r = Invoke-TsukiNative -FilePath $dotnet -ArgumentList @('--list-sdks') -Env $envs -TimeoutSeconds 60
    $sdks = @()
    foreach ($line in ($r.StdOut -split "`r?`n")) {
        if ($line -match '^\s*(\d+)\.(\d+)\.(\d+)(\S*)\s+\[(.+)\]') {
            $sdks += [pscustomobject]@{ Version = "$($Matches[1]).$($Matches[2]).$($Matches[3])$($Matches[4])"; Major = [int]$Matches[1]; Path = $Matches[5] }
        }
    }
    $all = ($sdks | ForEach-Object { $_.Version }) -join ', '
    if (-not $all) { $all = 'none' }
    $ten = @($sdks | Where-Object { $_.Major -eq 10 })
    if ($ten.Count) {
        Add-Result $area '.NET 10 SDK' 'OK' "$($ten[-1].Version) ($dotnet; all SDKs: $all)"
    } else {
        Add-Result $area '.NET 10 SDK' 'MISSING' "installed SDKs: $all ($dotnet)" $fix
    }
    if (@($sdks | Where-Object { $_.Major -eq 9 }).Count -and -not $ten.Count) {
        Add-Result $area '.NET 9 SDK' 'INFO' '.NET 9 reaches end of support on 2026-11-10; the solution targets net10.0 (ARCHITECTURE 0.5)'
    }
    $rr = Invoke-TsukiNative -FilePath $dotnet -ArgumentList @('--list-runtimes') -Env $envs -TimeoutSeconds 60
    $rt = @(($rr.StdOut -split "`r?`n") | Where-Object { $_ -match '^Microsoft\.NETCore\.App\s+(\S+)' } | ForEach-Object { ($_ -split '\s+')[1] })
    Add-Result $area 'NETCore runtimes' 'INFO' ($(if ($rt.Count) { $rt -join ', ' } else { 'none' }))
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $userDotnet) {
        Add-Result $area 'user-local dotnet' 'WARN' "$userDotnet also exists; make sure PATH picks the intended one"
    }
    if ($env:DOTNET_ROOT) { Add-Result $area 'DOTNET_ROOT' 'INFO' $env:DOTNET_ROOT }
}

# ==================================================================================================
# FFmpeg
# ==================================================================================================
function Test-Ffmpeg {
    $area = 'FFmpeg'
    $pkg = Find-TsukiFfmpegPackage
    $roots = (Get-TsukiWingetPortableRoots) -join '; '
    if (-not $pkg) {
        Add-Result $area "winget $TsukiFfmpegPackageId" 'MISSING' "no package folder with bin\avcodec-*.dll (searched the winget registry entries and $roots)" "winget install --id $TsukiFfmpegPackageId -e   (user, ROADMAP 4 step 1), then pwsh -File tools\setup\ffmpeg_dlls.ps1"
    } else {
        $lgpl = $pkg.ZipFolder -match 'lgpl'
        Add-Result $area "winget $TsukiFfmpegPackageId" 'OK' "$($pkg.BinDir) (found via $($pkg.Source))"
        if (-not $lgpl) { Add-Result $area 'LGPL build' 'WARN' "zip folder '$($pkg.ZipFolder)' does not say lgpl" }
        $libs = Get-TsukiFfmpegLibVersions (Join-Path $pkg.BinDir 'ffmpeg.exe')
        foreach ($d in $TsukiFfmpegDlls) {
            $p = Join-Path $pkg.BinDir $d.Name
            if (-not (Test-Path -LiteralPath $p)) {
                Add-Result $area "package $($d.Name)" $(if ($d.Required) { 'FAIL' } else { 'WARN' }) 'not in the package bin folder (other FFmpeg major version?)'
                continue
            }
            $v = Get-TsukiDllVersion $p
            if (-not $v -and $libs -and $libs.ContainsKey($d.Lib)) { $v = $libs[$d.Lib] }
            $ok = $v -and ([int](($v -split '\.')[0]) -eq $d.Major)
            Add-Result $area "package $($d.Name)" $(if ($ok) { 'OK' } elseif ($v) { 'FAIL' } else { 'WARN' }) $(if ($v) { "version $v" } else { 'no version resource and no ffmpeg.exe -version' })
        }
        $exe = Join-Path $pkg.BinDir 'ffmpeg.exe'
        if (Test-Path -LiteralPath $exe) {
            $dec = Invoke-TsukiNative -FilePath $exe -ArgumentList @('-hide_banner', '-decoders') -TimeoutSeconds 30
            $missing = @('atrac9', 'h264', 'aac' | Where-Object { $dec.StdOut -notmatch "(?m)^\s*\S+\s+$_\s" })
            $banner = $(if ($libs) { $libs['banner'] } else { 'ffmpeg.exe -version failed' })
            Add-Result $area 'package decoders' $(if ($missing.Count) { 'FAIL' } else { 'OK' }) $(if ($missing.Count) { "missing: $($missing -join ', ')" } else { "atrac9, h264, aac present ($banner)" })
        }
    }

    # third_party copies (what the engine loads)
    if (-not (Test-Path -LiteralPath $TsukiFfmpegDest)) {
        Add-Result $area 'third_party\ffmpeg\bin' 'MISSING' "$TsukiFfmpegDest does not exist" 'pwsh -File tools\setup\ffmpeg_dlls.ps1   (after the winget install)'
    } else {
        foreach ($d in $TsukiFfmpegDlls) {
            $p = Join-Path $TsukiFfmpegDest $d.Name
            if (-not (Test-Path -LiteralPath $p)) {
                Add-Result $area "third_party $($d.Name)" $(if ($d.Required) { 'MISSING' } else { 'INFO' }) 'not copied' 'pwsh -File tools\setup\ffmpeg_dlls.ps1'
                continue
            }
            $v = Get-TsukiDllVersion $p
            $detail = $(if ($v) { "version $v" } else { 'no version resource' })
            $status = 'OK'
            if ($v -and ([int](($v -split '\.')[0]) -ne $d.Major)) { $status = 'FAIL' }
            if ($pkg) {
                $src = Join-Path $pkg.BinDir $d.Name
                if ((Test-Path -LiteralPath $src) -and ((Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash)) {
                    $status = 'WARN'; $detail += '; differs from the package copy (re-run ffmpeg_dlls.ps1)'
                }
            }
            Add-Result $area "third_party $($d.Name)" $status $detail
        }
    }

    # the ffmpeg.exe the Python tools call (tools/tsuki/audio.py: TSUKI_FFMPEG, else PATH)
    $py = $null
    if ($env:TSUKI_FFMPEG) { $py = $env:TSUKI_FFMPEG }
    else {
        $c = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($c) { $py = $c.Source }
    }
    if (-not $py -or -not (Test-Path -LiteralPath $py)) {
        Add-Result $area 'ffmpeg for the Python tools' 'MISSING' 'no TSUKI_FFMPEG and no ffmpeg on PATH (tsuki.audio decodes ATRAC9 through it)' 'set TSUKI_FFMPEG to an ffmpeg.exe with the atrac9 decoder'
        return
    }
    $target = $py
    $item = Get-Item -LiteralPath $py
    if ($item.PSObject.Properties.Name -contains 'Target' -and $item.Target) { $target = (@($item.Target))[0] }
    $libs = Get-TsukiFfmpegLibVersions $py
    $dec = Invoke-TsukiNative -FilePath $py -ArgumentList @('-hide_banner', '-decoders') -TimeoutSeconds 30
    $hasAtrac9 = $dec.StdOut -match '(?m)^\s*\S+\s+atrac9\s'
    $banner = $(if ($libs) { $libs['banner'] } else { 'version unknown' })
    Add-Result $area 'ffmpeg for the Python tools' $(if ($hasAtrac9) { 'OK' } else { 'FAIL' }) "$banner; atrac9 decoder: $hasAtrac9; $py -> $target"
    if ($target -match [regex]::Escape($TsukiFfmpegPackageId)) {
        Add-Result $area 'ffmpeg alias' 'WARN' 'PATH ffmpeg now resolves to the BtbN shared build; it has atrac9 too, but set TSUKI_FFMPEG to the Gyan path to keep the Python goldens on the same decoder (ARCHITECTURE 0.5)'
    }
}

# ==================================================================================================
# Game folder
# ==================================================================================================
function Read-ParamSfo([string]$Path) {
    <# Minimal PARAM.SFO reader: returns @{ key = value } for string (0x0204) and int (0x0404) entries. #>
    $b = [System.IO.File]::ReadAllBytes($Path)
    if ($b.Length -lt 20 -or [BitConverter]::ToUInt32($b, 0) -ne 0x46535000) { throw "not a PARAM.SFO: $Path" }
    $keyStart = [BitConverter]::ToUInt32($b, 8)
    $dataStart = [BitConverter]::ToUInt32($b, 12)
    $n = [BitConverter]::ToUInt32($b, 16)
    $h = @{}
    for ($i = 0; $i -lt $n; $i++) {
        $e = 20 + 16 * $i
        $ko = $keyStart + [BitConverter]::ToUInt16($b, $e)
        $fmt = [BitConverter]::ToUInt16($b, $e + 2)
        $len = [BitConverter]::ToUInt32($b, $e + 4)
        $off = $dataStart + [BitConverter]::ToUInt32($b, $e + 12)
        $kend = $ko; while ($kend -lt $b.Length -and $b[$kend] -ne 0) { $kend++ }
        $key = [System.Text.Encoding]::ASCII.GetString($b, $ko, $kend - $ko)
        if ($fmt -eq 0x0404) { $h[$key] = [BitConverter]::ToUInt32($b, $off) }
        else { $h[$key] = ([System.Text.Encoding]::UTF8.GetString($b, $off, $len)).TrimEnd([char]0) }
    }
    return $h
}

function Test-GameDir {
    $area = 'Game'
    $proc = $env:TSUKI_GAME_DIR
    $user = [Environment]::GetEnvironmentVariable('TSUKI_GAME_DIR', 'User')
    $machine = [Environment]::GetEnvironmentVariable('TSUKI_GAME_DIR', 'Machine')
    $setx = "setx TSUKI_GAME_DIR `"<your CUSA47999 folder>`"   (user, ROADMAP 4 step 1; then open a new terminal)"
    $dir = $null
    if ($proc) {
        Add-Result $area 'TSUKI_GAME_DIR' 'OK' "$proc (process environment)"
        $dir = $proc
    } elseif ($user -or $machine) {
        $v = $(if ($user) { $user } else { $machine })
        Add-Result $area 'TSUKI_GAME_DIR' 'WARN' "set in the $(if ($user) { 'user' } else { 'machine' }) environment ($v) but not in this process: open a new terminal"
        $dir = $v
    } else {
        # no built-in default: the tools, the tests and the programs read this variable only
        Add-Result $area 'TSUKI_GAME_DIR' 'MISSING' "not set in the process, user or machine environment; $TsukiGameDirMissing" $setx
    }
    if (-not $dir) { return }
    if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
        Add-Result $area 'game folder' 'FAIL' "$dir is not a folder" $setx
        return
    }
    Add-Result $area 'game folder' 'INFO' "checking $dir (read-only)"
    $full = (Resolve-Path -LiteralPath $dir).Path
    if ($full.StartsWith($TsukiProjectRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-Result $area 'game folder location' 'WARN' 'the game folder is inside the project root; keep it outside so it can never be committed'
    }
    $leaf = Split-Path -Leaf $full
    Add-Result $area 'folder name' $(if ($leaf -eq $TsukiTitleId) { 'OK' } else { 'WARN' }) "'$leaf' (expected $TsukiTitleId; the check below uses param.sfo)"

    $sfo = Join-Path $full 'sce_sys\param.sfo'
    if (Test-Path -LiteralPath $sfo) {
        $p = Read-ParamSfo $sfo
        $tid = $(if ($p.ContainsKey('TITLE_ID')) { $p['TITLE_ID'] } else { '' })
        Add-Result $area 'param.sfo TITLE_ID' $(if ($tid -eq $TsukiTitleId) { 'OK' } else { 'FAIL' }) "'$tid'"
        $ver = @('APP_VER', 'VERSION', 'CONTENT_ID' | Where-Object { $p.ContainsKey($_) } | ForEach-Object { "$_=$($p[$_])" }) -join ', '
        Add-Result $area 'param.sfo version' 'INFO' "$ver (RR 3.3: identify the build by the eboot hash, not by APP_VER)"
    } else {
        Add-Result $area 'param.sfo' 'FAIL' 'sce_sys\param.sfo not found'
    }

    $eboot = Join-Path $full 'eboot.bin'
    if (-not (Test-Path -LiteralPath $eboot)) {
        Add-Result $area 'eboot.bin' 'FAIL' 'not found'
    } else {
        $len = (Get-Item -LiteralPath $eboot).Length
        Add-Result $area 'eboot.bin size' $(if ($len -eq $TsukiEbootSize) { 'OK' } else { 'FAIL' }) ('{0:N0} bytes (expected {1:N0})' -f $len, $TsukiEbootSize)
        $sha = (Get-FileHash -LiteralPath $eboot -Algorithm SHA256).Hash.ToLowerInvariant()
        Add-Result $area 'eboot.bin SHA-256' $(if ($sha -eq $TsukiEbootSha256) { 'OK' } else { 'FAIL' }) $(if ($sha -eq $TsukiEbootSha256) { "$sha (the analysed build)" } else { "$sha, expected ${TsukiEbootSha256}: another build; every eboot address in the docs would be wrong" })
    }

    # Files of RR 3.1 that the runtime opens; three sizes are documented exactly.
    $img = Join-Path $full 'image'
    $sizes = @{ 'allscr.mrg' = 9585000; 'script_text.mrg' = 12601040; 'allpac1.mrg' = 24257312768 }
    $need = @()
    foreach ($a in 'allpac0', 'allpac1', 'allpac1_2', 'allpac2', 'allpac3', 'allpac4', 'voice', 'voice2') { $need += "$a.hed", "$a.nam", "$a.mrg" }
    $need += 'parts.hed', 'parts.mrg', 'allscr.mrg', 'script_text.mrg', 'vse.hed', 'vse.mrg', 'se_nam.mrg', 'bgm.hed', 'bgm.mrg', 'vag.hed', 'vag.mrg', 'ft_ja.mrg', 'ft_en.mrg', 'ft_zc.mrg', 'ft_zt.mrg'
    $absent = @($need | Where-Object { -not (Test-Path -LiteralPath (Join-Path $img $_)) })
    Add-Result $area 'image\ archives' $(if ($absent.Count) { 'FAIL' } else { 'OK' }) $(if ($absent.Count) { "missing: $($absent -join ', ')" } else { "$($need.Count) files present" })
    foreach ($k in 'allscr.mrg', 'script_text.mrg', 'allpac1.mrg') {
        $f = Join-Path $img $k
        if (Test-Path -LiteralPath $f) {
            $len = (Get-Item -LiteralPath $f).Length
            Add-Result $area "size $k" $(if ($len -eq $sizes[$k]) { 'OK' } else { 'FAIL' }) ('{0:N0} bytes (expected {1:N0})' -f $len, $sizes[$k])
        }
    }
    $movies = @(Get-ChildItem -LiteralPath (Join-Path $img 'movie') -Filter '*.mp4' -File -ErrorAction SilentlyContinue).Count
    Add-Result $area 'image\movie\*.mp4' $(if ($movies -eq 134) { 'OK' } else { 'FAIL' }) "$movies files (expected 134)"
    Add-Result $area 'entry counts' 'INFO' 'archive entry and text-line counts (FORMATS 2) are checked by Tsuki.Cli verify (M0 acceptance 2), not here'
}

# ==================================================================================================
# Python
# ==================================================================================================
function Test-Python {
    $area = 'Python'
    $py = Join-Path $TsukiProjectRoot '.venv\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $py)) {
        Add-Result $area '.venv' 'MISSING' "$py not found" 'py -3.14 -m venv .venv; .venv\Scripts\python.exe -m pip install --only-binary=:all: -r requirements-dev.txt'
        return
    }
    $envs = @{ PYTHONPATH = (Join-Path $TsukiProjectRoot 'tools'); PYTHONDONTWRITEBYTECODE = '1' }
    $r = Invoke-TsukiNative -FilePath $py -ArgumentList @((Join-Path $PSScriptRoot 'check_python.py')) -Env $envs -TimeoutSeconds 120
    if ($r.ExitCode -ne 0) {
        Add-Result $area 'check_python.py' 'FAIL' "exit $($r.ExitCode): $($r.StdOut.Trim()) $($r.StdErr.Trim())"
        return
    }
    $j = $r.StdOut | ConvertFrom-Json
    Add-Result $area '.venv' $(if ($j.prefix_is_venv) { 'OK' } else { 'WARN' }) "Python $($j.python) ($($j.executable))"
    foreach ($prop in $j.packages.PSObject.Properties) {
        $v = $prop.Value
        if ($v.imported) { Add-Result $area "package $($prop.Name)" 'OK' "$($v.version)" }
        elseif ($v.version) { Add-Result $area "package $($prop.Name)" 'FAIL' "installed $($v.version) but the import fails: $($v.error)" }
        else { Add-Result $area "package $($prop.Name)" 'MISSING' "not installed ($($v.error))" ".venv\Scripts\python.exe -m pip install --only-binary=:all: -r requirements-dev.txt" }
    }
    foreach ($prop in $j.project.PSObject.Properties) {
        $v = $prop.Value
        Add-Result $area "import $($prop.Name)" $(if ($v.imported) { 'OK' } else { 'FAIL' }) $(if ($v.imported) { 'from tools\' } else { $v.error })
    }
    Add-Result $area 'tsuki.game_dir()' 'INFO' $(if ($j.tsuki_game_dir) { $j.tsuki_game_dir } else { 'None (game folder not found)' })

    $base = @('-m', 'pytest', 'tools/tests', '-q', '-p', 'no:cacheprovider')
    if ($RunTests) {
        Write-Host 'running the pytest suite (about 2 minutes) ...'
        $t = Invoke-TsukiNative -FilePath $py -ArgumentList ($base + @('-rs')) -Env $envs -TimeoutSeconds 1200
        $lines = @($t.StdOut -split "`r?`n" | Where-Object { $_.Trim() })
        $summary = @($lines | Where-Object { $_ -match '\b(passed|failed|error|errors|skipped)\b.*\bin [\d.]+s' } | Select-Object -Last 1)
        $sum = $(if ($summary.Count) { $summary[0].Trim('= ').Trim() } else { "no summary line (exit $($t.ExitCode))" })
        $bad = ($t.ExitCode -ne 0) -or ($sum -match '\b(failed|error|errors)\b')
        Add-Result $area 'pytest tools/tests' $(if ($bad) { 'FAIL' } else { 'OK' }) "$sum (exit $($t.ExitCode), $($t.Seconds) s)"
        foreach ($s in @($lines | Where-Object { $_ -like 'SKIPPED*' })) {
            Add-Result $area 'pytest skip' 'WARN' $s.Trim()
        }
    } else {
        $t = Invoke-TsukiNative -FilePath $py -ArgumentList ($base + @('--collect-only')) -Env $envs -TimeoutSeconds 300
        $last = @($t.StdOut -split "`r?`n" | Where-Object { $_ -match 'collected|error' } | Select-Object -Last 1)
        $sum = $(if ($last.Count) { $last[0].Trim() } else { "exit $($t.ExitCode)" })
        Add-Result $area 'pytest tools/tests' $(if ($t.ExitCode -eq 0) { 'INFO' } else { 'FAIL' }) "$sum (collected only; -RunTests runs the suite)"
    }
}

# ==================================================================================================
# Disk
# ==================================================================================================
function Test-Disk {
    $area = 'Disk'
    $checks = @(
        @{ Path = $TsukiProjectRoot; What = 'project drive (samples\golden, artifacts, third_party)'; Fail = 5GB; Warn = 25GB },
        @{ Path = $env:SystemDrive + '\'; What = 'system drive (.NET SDK, %USERPROFILE%\.nuget, %LOCALAPPDATA% caches)'; Fail = 3GB; Warn = 10GB }
    )
    $seen = @{}
    foreach ($c in $checks) {
        $root = [System.IO.Path]::GetPathRoot($c.Path)
        $d = New-Object System.IO.DriveInfo($root)
        $free = $d.AvailableFreeSpace
        $status = $(if ($free -lt $c.Fail) { 'FAIL' } elseif ($free -lt $c.Warn) { 'WARN' } else { 'OK' })
        $label = $root.TrimEnd('\')
        if ($seen.ContainsKey($label)) { continue }
        $seen[$label] = $true
        Add-Result $area "$label free" $status ("{0} free of {1}: {2}" -f (Format-Bytes $free), (Format-Bytes $d.TotalSize), $c.What)
    }
    $golden = Join-Path $TsukiProjectRoot 'samples\golden'
    if (Test-Path -LiteralPath $golden) {
        $sz = (Get-ChildItem -LiteralPath $golden -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        Add-Result $area 'samples\golden size' 'INFO' (Format-Bytes ([double]$sz))
    }
}

# ==================================================================================================
# GPU / OpenGL
# ==================================================================================================
$GlProbeScript = @'
$ErrorActionPreference = 'Stop'
$src = @"
using System;
using System.Runtime.InteropServices;
public static class TsukiGlProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize; public ushort nVersion; public uint dwFlags; public byte iPixelType; public byte cColorBits;
        public byte cRedBits; public byte cRedShift; public byte cGreenBits; public byte cGreenShift; public byte cBlueBits;
        public byte cBlueShift; public byte cAlphaBits; public byte cAlphaShift; public byte cAccumBits; public byte cAccumRedBits;
        public byte cAccumGreenBits; public byte cAccumBlueBits; public byte cAccumAlphaBits; public byte cDepthBits;
        public byte cStencilBits; public byte cAuxBuffers; public byte iLayerType; public byte bReserved;
        public uint dwLayerMask; public uint dwVisibleMask; public uint dwDamageMask;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool UnregisterClassW(string cls, IntPtr inst);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr wnd);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr wnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr wnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int ChoosePixelFormat(IntPtr dc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool SetPixelFormat(IntPtr dc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("opengl32.dll", SetLastError = true)] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr rc);
    [DllImport("opengl32.dll")] static extern IntPtr glGetString(uint name);

    static string Str(uint name) { IntPtr p = glGetString(name); return p == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(p); }

    public static string[] Probe()
    {
        const string cls = "TsukiGlProbeWindow";
        IntPtr inst = GetModuleHandleW(null);
        WNDCLASSEXW wc = new WNDCLASSEXW();
        wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
        wc.style = 0x0020; // CS_OWNDC
        wc.lpfnWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
        wc.hInstance = inst;
        wc.lpszClassName = cls;
        if (RegisterClassExW(ref wc) == 0) { int e = Marshal.GetLastWin32Error(); if (e != 1410) throw new InvalidOperationException("RegisterClassEx failed: " + e); }
        IntPtr wnd = CreateWindowExW(0, cls, "tsuki-gl-probe", 0x80000000u, 0, 0, 16, 16, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (wnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        IntPtr dc = IntPtr.Zero, rc = IntPtr.Zero;
        try
        {
            dc = GetDC(wnd);
            PIXELFORMATDESCRIPTOR pfd = new PIXELFORMATDESCRIPTOR();
            pfd.nSize = (ushort)Marshal.SizeOf(typeof(PIXELFORMATDESCRIPTOR));
            pfd.nVersion = 1;
            pfd.dwFlags = 0x4 | 0x20 | 0x1; // PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER
            pfd.cColorBits = 32; pfd.cDepthBits = 24; pfd.cStencilBits = 8;
            int fmt = ChoosePixelFormat(dc, ref pfd);
            if (fmt == 0) throw new InvalidOperationException("ChoosePixelFormat failed: " + Marshal.GetLastWin32Error());
            if (!SetPixelFormat(dc, fmt, ref pfd)) throw new InvalidOperationException("SetPixelFormat failed: " + Marshal.GetLastWin32Error());
            rc = wglCreateContext(dc);
            if (rc == IntPtr.Zero) throw new InvalidOperationException("wglCreateContext failed: " + Marshal.GetLastWin32Error());
            if (!wglMakeCurrent(dc, rc)) throw new InvalidOperationException("wglMakeCurrent failed");
            return new string[] { Str(0x1F00), Str(0x1F01), Str(0x1F02), Str(0x8B8C) };
        }
        finally
        {
            if (rc != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(rc); }
            if (dc != IntPtr.Zero) ReleaseDC(wnd, dc);
            DestroyWindow(wnd);
            UnregisterClassW(cls, inst);
        }
    }
}
"@
try {
    Add-Type -TypeDefinition $src -Language CSharp
    $r = [TsukiGlProbe]::Probe()
    @{ ok = $true; vendor = $r[0]; renderer = $r[1]; version = $r[2]; glsl = $r[3] } | ConvertTo-Json -Compress
} catch {
    @{ ok = $false; error = $_.Exception.Message } | ConvertTo-Json -Compress
}
'@

function Test-Gpu {
    $area = 'GPU'
    try {
        $vcs = @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)
        foreach ($v in $vcs) {
            $mode = $(if ($v.CurrentHorizontalResolution) { "$($v.CurrentHorizontalResolution) x $($v.CurrentVerticalResolution) @ $($v.CurrentRefreshRate) Hz" } else { 'no active mode' })
            Add-Result $area 'adapter' 'INFO' "$($v.Name), driver $($v.DriverVersion), $mode"
        }
    } catch {
        Add-Result $area 'adapter' 'WARN' "Win32_VideoController query failed: $($_.Exception.Message)"
    }
    if ($NoOpenGL) { Add-Result $area 'OpenGL' 'INFO' 'probe skipped (-NoOpenGL)'; return }
    $hostExe = (Get-Process -Id $PID).Path
    $enc = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($GlProbeScript))
    $r = Invoke-TsukiNative -FilePath $hostExe -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $enc) -TimeoutSeconds 60
    $line = @($r.StdOut -split "`r?`n" | Where-Object { $_ -match '^\{' } | Select-Object -Last 1)
    if ($r.TimedOut -or -not $line.Count) {
        Add-Result $area 'OpenGL' 'WARN' "probe gave no result (exit $($r.ExitCode); $($r.StdErr.Trim()))"
        return
    }
    $g = $line[0] | ConvertFrom-Json
    if (-not $g.ok) { Add-Result $area 'OpenGL' 'WARN' "probe failed: $($g.error)"; return }
    $ok = $false
    if ($g.version -match '^(\d+)\.(\d+)') { $ok = ([int]$Matches[1] * 10 + [int]$Matches[2]) -ge 33 }
    Add-Result $area 'OpenGL' $(if ($ok) { 'OK' } else { 'WARN' }) "$($g.version); $($g.renderer) ($($g.vendor)); GLSL $($g.glsl). Legacy WGL context; the 3.3 core context is checked by Tsuki.App --smoke (M0 acceptance 3)"
}

# ==================================================================================================
# Repository (information for M0 scope 2; nothing here fails the check)
# ==================================================================================================
function Test-Repo {
    $area = 'Repo'
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git) {
        $r = Invoke-TsukiNative -FilePath $git.Source -ArgumentList @('--version') -TimeoutSeconds 30
        Add-Result $area 'git' 'INFO' $r.StdOut.Trim()
    } else {
        Add-Result $area 'git' 'WARN' 'git not found (needed for M0 scope 2)'
    }
    if (Test-Path -LiteralPath (Join-Path $TsukiProjectRoot '.git')) {
        Add-Result $area 'repository' 'INFO' 'local git repository present'
    } else {
        Add-Result $area 'repository' 'WARN' 'no git repository yet: M0 scope 2 waits for the user''s approval (ROADMAP 4 step 1)'
    }
    $gi = Join-Path $TsukiProjectRoot '.gitignore'
    $want = 'bin/', 'obj/', '.vs/', '*.user', 'artifacts/', 'third_party/', 'TestResults/', 'samples/', 'scratch/', 'ref/', '.venv/'
    $have = @()
    if (Test-Path -LiteralPath $gi) { $have = @(Get-Content -LiteralPath $gi | ForEach-Object { $_.Trim() }) }
    $lack = @($want | Where-Object { $have -notcontains $_ })
    Add-Result $area '.gitignore' $(if ($lack.Count) { 'WARN' } else { 'OK' }) $(if ($lack.Count) { "lacks $($lack -join ' ') (M0 scope 2)" } else { 'has every M0 entry' })
    $hooks = Join-Path $TsukiProjectRoot '.claude\settings.json'
    Add-Result $area 'project hooks' 'INFO' $(if (Test-Path -LiteralPath $hooks) { '.claude\settings.json present' } else { 'not installed yet (A 10.8; needs the user''s approval)' })
    $man = Join-Path $TsukiProjectRoot 'samples\golden\slice\manifest.json'
    Add-Result $area 'golden slice manifest' 'INFO' $(if (Test-Path -LiteralPath $man) { 'samples\golden\slice\manifest.json present' } else { 'samples\golden\slice\manifest.json not generated yet (M0 acceptance 5)' })
}

# ==================================================================================================
# run
# ==================================================================================================
try {
    Test-DotNet
    Test-Ffmpeg
    Test-GameDir
    Test-Python
    Test-Disk
    Test-Gpu
    Test-Repo
} catch {
    Write-Output "check_env.ps1 failed: $($_.Exception.Message)"
    Write-Output $_.ScriptStackTrace
    exit 2
}

$os = [System.Environment]::OSVersion.VersionString
Write-Output ('TSUKIHIME port: environment check, {0:yyyy-MM-dd HH:mm} (read-only; installs nothing)' -f $started)
Write-Output "project $TsukiProjectRoot; PowerShell $($PSVersionTable.PSVersion); $os"
$area = ''
foreach ($r in $results) {
    if ($r.Area -ne $area) { $area = $r.Area; Write-Output ''; Write-Output "[$area]" }
    Write-Output ('  {0,-8} {1,-30} {2}' -f $r.Status, $r.Item, $r.Detail)
    if ($r.Fix -and ($r.Status -eq 'MISSING' -or $r.Status -eq 'FAIL')) { Write-Output ('  {0,-8} {1,-30} fix: {2}' -f '', '', $r.Fix) }
}
$bad = @($results | Where-Object { $_.Status -eq 'MISSING' -or $_.Status -eq 'FAIL' })
$warn = @($results | Where-Object { $_.Status -eq 'WARN' })
Write-Output ''
Write-Output ('Summary: {0} missing, {1} failed, {2} warnings, {3} ok ({4:N1} s)' -f @($bad | Where-Object { $_.Status -eq 'MISSING' }).Count, @($bad | Where-Object { $_.Status -eq 'FAIL' }).Count, $warn.Count, @($results | Where-Object { $_.Status -eq 'OK' }).Count, ((Get-Date) - $started).TotalSeconds)
if ($bad.Count) {
    Write-Output 'Required items that are missing or wrong:'
    $i = 0
    foreach ($b in $bad) {
        $i++
        Write-Output ('  {0}. [{1}] {2}: {3}' -f $i, $b.Status, $b.Item, $b.Detail)
        if ($b.Fix) { Write-Output "     fix: $($b.Fix)" }
    }
}
if ($Json) {
    $doc = [pscustomobject]@{
        tool = 'tools/setup/check_env.ps1'; time = $started.ToString('s'); project = $TsukiProjectRoot
        missing = @($bad | Where-Object { $_.Status -eq 'MISSING' }).Count; failed = @($bad | Where-Object { $_.Status -eq 'FAIL' }).Count
        warnings = $warn.Count; results = $results
    }
    $dir = Split-Path -Parent ([System.IO.Path]::GetFullPath($Json))
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $doc | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Json -Encoding UTF8
    Write-Output "results written to $Json"
}
exit $(if ($bad.Count) { 1 } else { 0 })
