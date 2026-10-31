# Spike S4: the full measurement run (docs/status/spikes/S4.md). Windows the spike opens close
# themselves (each upload run lasts about 3 s, the idle run about 30 s; hard limit 40 s per window).
#
#   pwsh -NoProfile -File src\Spikes\S4.BigPicture\run_s4.ps1 [-Reps 5] [-HitchReps 20] [-Part all|cpu|matrix|budget|hitch]
#        [-SkipBuild] [-SkipOracle]
#
# Outputs (local, git-ignored): samples\spikes\s4\*.json and summary.json / summary.md.
param(
    [int]$Reps = 5,
    [int]$HitchReps = 20,
    [ValidateSet('all', 'cpu', 'matrix', 'budget', 'hitch')][string]$Part = 'all',
    [switch]$SkipBuild,
    [switch]$SkipOracle
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$out = Join-Path $root 'samples\spikes\s4'
$py = Join-Path $root '.venv\Scripts\python.exe'
$env:PYTHONPATH = Join-Path $root 'tools'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $SkipBuild) {
    & dotnet build $PSScriptRoot -c Release -warnaserror | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\S4.BigPicture.exe'

if (-not $SkipOracle -or -not (Test-Path (Join-Path $out 'oracle.json'))) {
    & $py (Join-Path $PSScriptRoot 'oracle\s4_oracle.py')
    if ($LASTEXITCODE -ne 0) { throw 'oracle failed' }
}

function Run([string[]]$a) {
    & $exe @a
    if ($LASTEXITCODE -ne 0) { throw "S4.BigPicture $($a -join ' ') failed ($LASTEXITCODE)" }
}

function RunConfigs($configs, [int]$n) {
    foreach ($name in $configs.Keys) {
        for ($r = 0; $r -lt $n; $r++) {
            Run (@('upload', "${name}_r$r") + ($configs[$name] -split ' '))
        }
    }
}

if ($Part -in 'all', 'cpu') {
    Run @('verify')
    foreach ($p in @(@('allpac1', '1259'), @('allpac1', '1653'), @('allpac1_2', '4009'), @('allpac1', '4946'))) {
        Run (@('decode') + $p + @('--reps', '7'))
    }
    Run @('scenes', '--reps', '5')
}

# IMG1481 = allpac1#1259, IMG3885 = allpac1_2#4009, IMG1481_20_00 = allpac1#4946; scenes are picked
# from scenes.json by upload bytes. tN = N tiles per frame, b4ms = time-capped slices (4 ms CPU),
# wN = N decode workers (below-normal priority), dec = decode-only control (no upload),
# pool = tile textures created during the warm-up, s64 = bounded 64 MiB CPU staging pool.
if ($Part -in 'all', 'matrix') {
    $matrix = [ordered]@{
        'img1481_dec_w14'          = '--layers allpac1#1259 --tiles-per-frame 0 --workers 14'
        'img1481_dec_w8'           = '--layers allpac1#1259 --tiles-per-frame 0 --workers 8'
        'img1481_t4_w14'           = '--layers allpac1#1259 --tiles-per-frame 4 --workers 14'
        'img1481_t4_w8'            = '--layers allpac1#1259 --tiles-per-frame 4 --workers 8'
        'img1481_t8_w8'            = '--layers allpac1#1259 --tiles-per-frame 8 --workers 8'
        'img1481_t16_w8'           = '--layers allpac1#1259 --tiles-per-frame 16 --workers 8'
        'img1481_t32_w8'           = '--layers allpac1#1259 --tiles-per-frame 32 --workers 8'
        'img1481_b4ms_w8'          = '--layers allpac1#1259 --tiles-per-frame 64 --budget-ms 4 --workers 8'
        'img1481_t16_w8_pbo'       = '--layers allpac1#1259 --tiles-per-frame 16 --workers 8 --pbo'
        'img1481_t16_w8_pre'       = '--layers allpac1#1259 --tiles-per-frame 16 --workers 8 --prealloc'
        'img1481_t16_w8_pool_s64'  = '--layers allpac1#1259 --tiles-per-frame 16 --workers 8 --texpool --staging-mb 64'
        'img1481_t16_w14_pool_s64' = '--layers allpac1#1259 --tiles-per-frame 16 --workers 14 --texpool --staging-mb 64'
        'img3885_t16_w8'           = '--layers allpac1_2#4009 --tiles-per-frame 16 --workers 8'
        'img1481_20_00_t16_w8'     = '--layers allpac1#4946 --tiles-per-frame 16 --workers 8'
        'scene_median_t16_w8'      = '--scene median --tiles-per-frame 16 --workers 8'
        'scene_p90_t16_w8'         = '--scene p90 --tiles-per-frame 16 --workers 8'
        'scene_heavy_t16_w8'       = '--scene heavy --tiles-per-frame 16 --workers 8'
        'scene_heavy_b4ms_w8'      = '--scene heavy --tiles-per-frame 64 --budget-ms 4 --workers 8'
        'scene_heavy_t16_w8_pool_s64' = '--scene heavy --tiles-per-frame 16 --workers 8 --texpool --staging-mb 64'
    }
    RunConfigs $matrix $Reps
}

# the recommended runtime settings: byte-capped slices (16 MiB per frame), 8 below-normal workers,
# 64 MiB staging pool, with and without a texture pool
if ($Part -in 'all', 'budget') {
    $rec = '--tiles-per-frame 256 --budget-mb 16 --workers 8 --staging-mb 64'
    $budget = [ordered]@{
        'm16_img1481'             = "--layers allpac1#1259 $rec --texpool"
        'm16_img3885'             = "--layers allpac1_2#4009 $rec --texpool"
        'm16_img1481_20_00'       = "--layers allpac1#4946 $rec --texpool"
        'm16_scene_median'        = "--scene median $rec --texpool"
        'm16_scene_p90'           = "--scene p90 $rec --texpool"
        'm16_scene_heavy'         = "--scene heavy $rec --texpool"
        'm16_scene_median_nopool' = "--scene median $rec"
        'm16_scene_heavy_nopool'  = "--scene heavy $rec"
    }
    RunConfigs $budget $Reps
}

# hitch rate: more repetitions of the candidate settings, plus an idle control (no load for 30 s)
if ($Part -in 'all', 'hitch') {
    $hitch = [ordered]@{
        'hr_img1481_t16_w8'          = '--layers allpac1#1259 --tiles-per-frame 16 --workers 8'
        'hr_img1481_t16_w8_pool_s64' = '--layers allpac1#1259 --tiles-per-frame 16 --workers 8 --texpool --staging-mb 64'
        'hr_heavy_t16_w8'            = '--scene heavy --tiles-per-frame 16 --workers 8'
        'hr_heavy_t16_w8_pool_s64'   = '--scene heavy --tiles-per-frame 16 --workers 8 --texpool --staging-mb 64'
    }
    RunConfigs $hitch $HitchReps
    RunConfigs ([ordered]@{ 'idle' = '--layers allpac0#2448 --tiles-per-frame 16 --warm-frames 4300 --no-verify' }) 1
}
& $py (Join-Path $PSScriptRoot 'oracle\summarize.py') $out
