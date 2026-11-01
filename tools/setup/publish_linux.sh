#!/usr/bin/env bash
set -euo pipefail

tsuki_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
tsuki_output=${1:-"$tsuki_root/artifacts/linux-x64"}

dotnet publish "$tsuki_root/src/Tsuki.App" -c Release -r linux-x64 \
    --self-contained true -o "$tsuki_output" --nologo "${@:2}"

mkdir -p -- "$tsuki_output/licenses"
cp -- "$tsuki_root/LICENSE" "$tsuki_output/licenses/LICENSE.txt"
cp -- "$tsuki_root/src/Tsuki.Launcher/Licenses/MPL-2.0.txt" "$tsuki_output/licenses/knife-MPL-2.0.txt"
cp -- "$tsuki_root/src/Tsuki.Launcher/Licenses/SDL3-zlib.txt" "$tsuki_output/licenses/SDL3-zlib.txt"
cp -- "$tsuki_root/docs/THIRD_PARTY.md" "$tsuki_output/licenses/THIRD_PARTY.md"
cp -- "$tsuki_root/docs/LINUX.md" "$tsuki_output/LINUX.md"
printf 'Linux build: %s/Tsukihime\n' "$tsuki_output"
