#!/usr/bin/env bash
# The Linux release folder (docs/PUBLISHING.md): a self-contained publish of the launcher and player with SDL, the
# FFmpeg 9 libraries of third_party/ffmpeg/linux (tools/setup/ffmpeg_linux.py) and the licences beside it. Arguments after
# the output folder go to dotnet publish; -p:TsukiBundleFFmpeg=false leaves FFmpeg out and uses the system's FFmpeg 9.
set -euo pipefail

tsuki_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
tsuki_output=${1:-"$tsuki_root/artifacts/linux-x64"}

dotnet publish "$tsuki_root/src/Tsuki.Launcher.Avalonia" -c Release -r linux-x64 \
    --self-contained true -p:PublishReadyToRun=true -o "$tsuki_output" --nologo "${@:2}"

printf 'Linux launcher: %s/TsukihimeLauncher (game: Tsukihime)\n' "$tsuki_output"
