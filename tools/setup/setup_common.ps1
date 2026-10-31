# Shared helpers for tools/setup/*.ps1. Dot-source it; it only defines constants and functions.
#
# Nothing here installs anything, runs winget or writes outside the paths its callers name.
# Works in Windows PowerShell 5.1 and PowerShell 7 (no ?? / ?. / ternary operators).

Set-StrictMode -Version 2.0

# --- constants -----------------------------------------------------------------------------------

$TsukiProjectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path

# Identity of the analysed build (ROADMAP M0 acceptance 2, RR 3.3).
$TsukiEbootSize = 12767802
$TsukiEbootSha256 = 'c2843415fdfda5ebbc84f4d55c6cca044a7476436cf0cfdfccac83116beeb918'
$TsukiTitleId = 'CUSA47999'
# The game folder comes from the environment variable TSUKI_GAME_DIR only; there is no built-in default path.
$TsukiGameDirEnvName = 'TSUKI_GAME_DIR'
$TsukiGameDirMissing = 'set the environment variable TSUKI_GAME_DIR to your extracted CUSA47999 folder (the one holding eboot.bin, image\, data\ and sce_sys\)'

# The FFmpeg package the port loads (ARCHITECTURE 0.5).
$TsukiFfmpegPackageId = 'BtbN.FFmpeg.LGPL.Shared.9.0'
$TsukiFfmpegDest = Join-Path $TsukiProjectRoot 'third_party\ffmpeg\bin'

# DLL names of FFmpeg 9.0. The library majors were measured on 2026-10-25 from
# `ffmpeg -version` of the installed Gyan 9.0.2 build (same 9.0 branch, so the same sonames):
# libavutil 61, libavcodec 63, libavformat 63, libavdevice 63, libavfilter 12, libswscale 10,
# libswresample 7. Tsuki.Media needs the first five; the other two are copied when present.
$TsukiFfmpegDlls = @(
    [pscustomobject]@{ Name = 'avcodec-63.dll';    Lib = 'libavcodec';    Major = 63; Required = $true }
    [pscustomobject]@{ Name = 'avformat-63.dll';   Lib = 'libavformat';   Major = 63; Required = $true }
    [pscustomobject]@{ Name = 'avutil-61.dll';     Lib = 'libavutil';     Major = 61; Required = $true }
    [pscustomobject]@{ Name = 'swresample-7.dll';  Lib = 'libswresample'; Major = 7;  Required = $true }
    [pscustomobject]@{ Name = 'swscale-10.dll';    Lib = 'libswscale';    Major = 10; Required = $true }
    [pscustomobject]@{ Name = 'avfilter-12.dll';   Lib = 'libavfilter';   Major = 12; Required = $false }
    [pscustomobject]@{ Name = 'avdevice-63.dll';   Lib = 'libavdevice';   Major = 63; Required = $false }
)

# --- native process helper -----------------------------------------------------------------------

function ConvertTo-TsukiArg([string]$a) {
    # Quote one argument by the CommandLineToArgvW rules.
    if ($a -ne '' -and $a -notmatch '[\s"]') { return $a }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $bs = 0
    foreach ($ch in $a.ToCharArray()) {
        if ($ch -eq [char]'\') { $bs++; continue }
        if ($ch -eq [char]'"') { [void]$sb.Append('\' * (2 * $bs + 1)); [void]$sb.Append('"'); $bs = 0; continue }
        if ($bs -gt 0) { [void]$sb.Append('\' * $bs); $bs = 0 }
        [void]$sb.Append($ch)
    }
    if ($bs -gt 0) { [void]$sb.Append('\' * (2 * $bs)) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Invoke-TsukiNative {
    <# Runs an executable with redirected output and returns ExitCode, StdOut, StdErr, TimedOut,
       Seconds. Env is a hashtable of extra environment variables for the child only. #>
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [string]$WorkingDirectory = $TsukiProjectRoot,
        [hashtable]$Env = @{},
        [int]$TimeoutSeconds = 120
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = (($ArgumentList | ForEach-Object { ConvertTo-TsukiArg $_ }) -join ' ')
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    foreach ($k in $Env.Keys) { $psi.EnvironmentVariables[$k] = [string]$Env[$k] }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $outTask = $p.StandardOutput.ReadToEndAsync()
    $errTask = $p.StandardError.ReadToEndAsync()
    $timedOut = -not $p.WaitForExit($TimeoutSeconds * 1000)
    if ($timedOut) {
        try { $p.Kill() } catch [System.InvalidOperationException] { }
        $p.WaitForExit()
    }
    [pscustomobject]@{
        ExitCode = $(if ($timedOut) { -1 } else { $p.ExitCode })
        StdOut   = $outTask.Result
        StdErr   = $errTask.Result
        TimedOut = $timedOut
        Seconds  = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    }
}

# --- winget package location ---------------------------------------------------------------------

function Get-TsukiWingetPortableRoots {
    <# Folders where winget extracts portable/zip packages, most likely first:
       the roots configured in winget's settings.json (installBehavior.portablePackageUserRoot /
       portablePackageMachineRoot), then the defaults %LOCALAPPDATA%\Microsoft\WinGet\Packages
       (user scope, the default) and %ProgramFiles%\WinGet\Packages (machine scope). #>
    $roots = New-Object System.Collections.Generic.List[string]
    $settings = Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json'
    if (Test-Path -LiteralPath $settings) {
        # settings.json allows // comments; strip whole-line comments before parsing.
        $text = (Get-Content -LiteralPath $settings -Raw) -replace '(?m)^\s*//.*$', ''
        try {
            $j = $text | ConvertFrom-Json
            if ($j.PSObject.Properties.Name -contains 'installBehavior') {
                $ib = $j.installBehavior
                foreach ($key in 'portablePackageUserRoot', 'portablePackageMachineRoot') {
                    if ($ib.PSObject.Properties.Name -contains $key -and $ib.$key) {
                        $roots.Add([Environment]::ExpandEnvironmentVariables([string]$ib.$key))
                    }
                }
            }
        } catch [System.ArgumentException] {
            Write-Warning "could not parse $settings ($($_.Exception.Message)); using the default roots"
        }
    }
    if ($env:LOCALAPPDATA) { $roots.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages')) }
    if ($env:ProgramFiles) { $roots.Add((Join-Path $env:ProgramFiles 'WinGet\Packages')) }
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ($pf86) { $roots.Add((Join-Path $pf86 'WinGet\Packages')) }
    return @($roots | Select-Object -Unique)
}

function Get-TsukiArpInstallLocations([string]$PackageId) {
    <# winget registers portable packages under ...\Uninstall\<PackageId>_<SourceId> with an
       InstallLocation (seen for Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe). #>
    $out = @()
    foreach ($hive in 'HKCU:', 'HKLM:') {
        $key = "$hive\Software\Microsoft\Windows\CurrentVersion\Uninstall"
        if (-not (Test-Path -LiteralPath $key)) { continue }
        foreach ($sub in Get-ChildItem -LiteralPath $key -ErrorAction SilentlyContinue) {
            if ($sub.PSChildName -notlike "$PackageId*") { continue }
            $p = Get-ItemProperty -LiteralPath $sub.PSPath
            if ($p.PSObject.Properties.Name -contains 'InstallLocation' -and $p.InstallLocation) {
                $out += [pscustomobject]@{ Key = $sub.PSChildName; Path = [string]$p.InstallLocation;
                                           Version = $(if ($p.PSObject.Properties.Name -contains 'DisplayVersion') { [string]$p.DisplayVersion } else { '' }) }
            }
        }
    }
    return $out
}

function Find-TsukiFfmpegPackage {
    <# Returns the package folder and its bin folder (the one that holds avcodec-*.dll), or $null.
       Looks in the ARP InstallLocation first, then in every portable root for
       <root>\BtbN.FFmpeg.LGPL.Shared.9.0_*\<zip folder>\bin. #>
    param([string]$PackageDir)
    $candidates = New-Object System.Collections.Generic.List[object]
    if ($PackageDir) {
        $candidates.Add([pscustomobject]@{ Dir = $PackageDir; Source = '-PackageDir' })
    } else {
        foreach ($arp in Get-TsukiArpInstallLocations $TsukiFfmpegPackageId) {
            $candidates.Add([pscustomobject]@{ Dir = $arp.Path; Source = "registry $($arp.Key)" })
        }
        foreach ($root in Get-TsukiWingetPortableRoots) {
            if (-not (Test-Path -LiteralPath $root)) { continue }
            foreach ($d in Get-ChildItem -LiteralPath $root -Directory -Filter "$TsukiFfmpegPackageId*" -ErrorAction SilentlyContinue) {
                $candidates.Add([pscustomobject]@{ Dir = $d.FullName; Source = "portable root $root" })
            }
        }
    }
    foreach ($c in $candidates) {
        if (-not (Test-Path -LiteralPath $c.Dir)) { continue }
        # bin\avcodec-*.dll at depth 0..2 below the package folder.
        $hits = @()
        foreach ($pattern in 'bin', '*\bin', '*\*\bin') {
            $hits += @(Get-ChildItem -Path (Join-Path $c.Dir "$pattern\avcodec-*.dll") -File -ErrorAction SilentlyContinue)
            if ($hits.Count) { break }
        }
        if ($hits.Count) {
            $bin = $hits[0].DirectoryName
            return [pscustomobject]@{ PackageDir = $c.Dir; BinDir = $bin; Source = $c.Source;
                                      ZipFolder = (Split-Path -Leaf (Split-Path -Parent $bin)) }
        }
    }
    return $null
}

function Get-TsukiDllVersion([string]$Path) {
    <# File version of a DLL's version resource as "major.minor.micro", or '' if it has none. #>
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($vi.FileMajorPart -or $vi.FileMinorPart -or $vi.FileBuildPart) {
        return "$($vi.FileMajorPart).$($vi.FileMinorPart).$($vi.FileBuildPart)"
    }
    if ($vi.FileVersion) { return ($vi.FileVersion -replace '\s', '') }
    return ''
}

function Get-TsukiFfmpegLibVersions([string]$Exe) {
    <# Parses `<exe> -hide_banner -version` of a shared FFmpeg build:
       returns @{ banner = 'ffmpeg version ...'; libavcodec = '63.1.102'; ... } or $null. #>
    if (-not (Test-Path -LiteralPath $Exe)) { return $null }
    $r = Invoke-TsukiNative -FilePath $Exe -ArgumentList @('-hide_banner', '-version') -TimeoutSeconds 30
    if ($r.ExitCode -ne 0) { return $null }
    $h = @{ banner = (($r.StdOut -split "`r?`n") | Select-Object -First 1) }
    foreach ($m in [regex]::Matches($r.StdOut, '(?m)^(lib\w+)\s+(\d+)\.\s*(\d+)\.\s*(\d+)')) {
        $h[$m.Groups[1].Value] = "$($m.Groups[2].Value).$($m.Groups[3].Value).$($m.Groups[4].Value)"
    }
    return $h
}
