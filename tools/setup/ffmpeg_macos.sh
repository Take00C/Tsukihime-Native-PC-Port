#!/usr/bin/env bash
# Build the minimal, replaceable FFmpeg 9 shared libraries for Tsukihime.app.
# Run on the matching macOS architecture: bash tools/setup/ffmpeg_macos.sh arm64|x64
# TSUKI_FFMPEG_REF selects the source ref (default n9.0.2); the exact commit is recorded.
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
    printf 'ffmpeg_macos: build on macOS; the output is a set of Mach-O dylibs\n' >&2
    exit 1
fi

arch=${1:-}
case "$arch" in
    arm64) ff_arch=arm64; host_arch=arm64 ;;
    x64) ff_arch=x86_64; host_arch=x86_64 ;;
    *) printf 'usage: bash tools/setup/ffmpeg_macos.sh arm64|x64\n' >&2; exit 2 ;;
esac
if [[ "$(uname -m)" != "$host_arch" ]]; then
    printf 'ffmpeg_macos: %s output must be built on a %s Mac\n' "$arch" "$host_arch" >&2
    exit 1
fi

root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
ref=${TSUKI_FFMPEG_REF:-n9.0.2}
expected_commit=946fcce07b6dcd0331c8cc609192aeff5e1924f8
source_dir=${TSUKI_FFMPEG_SOURCE_DIR:-"$root/third_party/ffmpeg/macos-src"}
work="$root/third_party/ffmpeg/macos-$arch-build"
dest="$root/third_party/ffmpeg/macos-$arch"
required=(libavutil.61.dylib libswresample.7.dylib libavcodec.63.dylib libavformat.63.dylib)

for tool in git clang make otool install_name_tool lipo shasum tar; do
    command -v "$tool" >/dev/null || { printf 'ffmpeg_macos: %s is missing (install the Xcode command line tools)\n' "$tool" >&2; exit 1; }
done
if [[ "$arch" == x64 ]]; then
    command -v nasm >/dev/null || { printf 'ffmpeg_macos: nasm is missing (install it with: brew install nasm)\n' >&2; exit 1; }
fi

if [[ ! -d "$source_dir/.git" ]]; then
    if [[ -e "$source_dir" ]]; then
        printf 'ffmpeg_macos: %s exists but is not an FFmpeg git checkout\n' "$source_dir" >&2
        exit 1
    fi
    mkdir -p "$(dirname -- "$source_dir")"
    git init "$source_dir"
    git -C "$source_dir" remote add origin https://git.ffmpeg.org/ffmpeg.git
fi
origin=$(git -C "$source_dir" remote get-url origin)
if [[ "$origin" != "https://git.ffmpeg.org/ffmpeg.git" ]]; then
    printf 'ffmpeg_macos: the checkout origin is not the official FFmpeg repository: %s\n' "$origin" >&2
    exit 1
fi
# git.ffmpeg.org sometimes drops a connection ("Connection reset by peer" on GitHub's macOS runner): try it three times, then FFmpeg's own
# GitHub mirror. The commit check below still decides: n9.0.2 must resolve to the expected commit wherever it came from.
fetched=""
for attempt in 1 2 3; do
    if git -C "$source_dir" fetch --depth 1 origin "$ref"; then
        fetched=1
        break
    fi
    printf 'ffmpeg_macos: fetching %s from git.ffmpeg.org failed (try %s of 3)\n' "$ref" "$attempt" >&2
    sleep $((attempt * 10))
done
if [[ -z "$fetched" ]]; then
    printf 'ffmpeg_macos: fetching %s from FFmpeg'"'"'s GitHub mirror instead\n' "$ref" >&2
    git -C "$source_dir" fetch --depth 1 https://github.com/FFmpeg/FFmpeg.git "$ref"
fi
git -C "$source_dir" checkout --detach FETCH_HEAD
if [[ -n "$(git -C "$source_dir" status --porcelain)" ]]; then
    printf 'ffmpeg_macos: the FFmpeg checkout has local changes: %s\n' "$source_dir" >&2
    exit 1
fi
commit=$(git -C "$source_dir" rev-parse 'FETCH_HEAD^{commit}')
if [[ "$ref" == n9.0.2 && "$commit" != "$expected_commit" ]]; then
    printf 'ffmpeg_macos: n9.0.2 resolved to %s, expected %s\n' "$commit" "$expected_commit" >&2
    exit 1
fi

rm -rf "$work"
mkdir -p "$work/build" "$work/source"
git -C "$source_dir" archive "$commit" | tar -x -C "$work/source"
build_source="$work/source"
prefix=/usr/local
install_root="$work/install"
installed="$install_root$prefix"
configure=(
    "--prefix=$prefix"
    "--arch=$ff_arch"
    --target-os=darwin
    --enable-cross-compile
    --enable-shared
    --disable-static
    --disable-gpl
    --disable-nonfree
    --disable-version3
    --disable-autodetect
    --enable-pthreads
    --disable-network
    --disable-debug
    --disable-doc
    --disable-programs
    --enable-ffmpeg
    --disable-everything
    --enable-avutil
    --enable-swresample
    --enable-avcodec
    --enable-avformat
    --enable-demuxer=mov
    --enable-protocol=file
    --enable-parser=h264,aac
    --enable-decoder=atrac9,h264,aac
    --extra-cflags=-mmacosx-version-min=15.0
    --extra-ldflags=-mmacosx-version-min=15.0
)
printf -v configure_line '%q ' ./configure "${configure[@]}"
configure_line=${configure_line% }

printf 'FFmpeg source: %s (%s)\n' "$commit" "$ref"
printf 'Building macOS %s shared libraries, macOS 15 minimum, LGPL 2.1 or later\n' "$arch"
(
    cd "$work/build"
    "$build_source/configure" "${configure[@]}"
    make -j"$(sysctl -n hw.logicalcpu)"
    make install DESTDIR="$install_root"
)

export DYLD_LIBRARY_PATH="$installed/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
version_output=$("$installed/bin/ffmpeg" -hide_banner -version 2>&1)
if [[ "$version_output" == *--enable-gpl* || "$version_output" == *--enable-nonfree* ]]; then
    printf 'ffmpeg_macos: the build reports a GPL or nonfree configuration\n' >&2
    exit 1
fi
if [[ "$version_output" == */Users/* || "$version_output" == */home/* ]]; then
    printf 'ffmpeg_macos: the dylib configure string contains an absolute home path\n' >&2
    exit 1
fi
decoders=$("$installed/bin/ffmpeg" -hide_banner -decoders 2>&1)
for decoder in atrac9 h264 aac; do
    if ! grep -Eq "[[:space:]]${decoder}[[:space:]]" <<<"$decoders"; then
        printf 'ffmpeg_macos: the build has no %s decoder\n' "$decoder" >&2
        exit 1
    fi
done

stage="$work/stage"
mkdir -p "$stage"
for lib in "${required[@]}"; do
    [[ -f "$installed/lib/$lib" ]] || { printf 'ffmpeg_macos: %s was not installed\n' "$lib" >&2; exit 1; }
    cp -L "$installed/lib/$lib" "$stage/$lib"
    install_name_tool -id "@loader_path/$lib" "$stage/$lib"
done
# Change both the four libraries' IDs and every reference between them to loader-relative names.
for lib in "${required[@]}"; do
    for dependency in "${required[@]}"; do
        install_name_tool -change "$prefix/lib/$dependency" "@loader_path/$dependency" "$stage/$lib"
    done
done

for lib in "${required[@]}"; do
    if [[ "$(lipo -archs "$stage/$lib")" != "$host_arch" ]]; then
        printf 'ffmpeg_macos: %s is not a thin %s Mach-O library\n' "$lib" "$host_arch" >&2
        exit 1
    fi
    linkages=$(otool -L "$stage/$lib")
    if ! grep -Fq "@loader_path/$lib (compatibility version" <<<"$linkages"; then
        printf 'ffmpeg_macos: %s has the wrong install name\n' "$lib" >&2
        exit 1
    fi
    while IFS= read -r dependency; do
        if [[ "$dependency" == /usr/local/* || "$dependency" == /opt/homebrew/* ]]; then
            printf 'ffmpeg_macos: %s depends on a Homebrew/local library: %s\n' "$lib" "$dependency" >&2
            exit 1
        fi
        case "$dependency" in
            # macOS's own libraries and frameworks are on every Mac (libavutil links CoreFoundation); release-check allows the same
            @loader_path/*|/usr/lib/*|/System/Library/Frameworks/*) ;;
            *) printf 'ffmpeg_macos: %s has a non-relocatable dependency: %s\n' "$lib" "$dependency" >&2; exit 1 ;;
        esac
    done < <(tail -n +2 <<<"$linkages" | awk '{print $1}')
    library_strings=$(strings "$stage/$lib")
    if ! grep -Fq 'license: LGPL version 2.1 or later' <<<"$library_strings"; then
        printf 'ffmpeg_macos: %s does not report the expected LGPL 2.1-or-later licence\n' "$lib" >&2
        exit 1
    fi
done

cp "$build_source/LICENSE.md" "$stage/FFmpeg-LICENSE.md"
cp "$build_source/COPYING.LGPLv2.1" "$stage/FFmpeg-LGPL-2.1.txt"
{
    printf 'FFmpeg source and licence notice for Tsukihime.app (macOS %s)\n\n' "$arch"
    printf 'Official source: https://git.ffmpeg.org/ffmpeg.git\n'
    printf 'Requested ref: %s\nExact source commit: %s\n' "$ref" "$commit"
    printf 'Source archive mirror: https://github.com/FFmpeg/FFmpeg/archive/%s.tar.gz\n' "$commit"
    printf 'Configure command (run from the FFmpeg source folder): %s\n\n' "$configure_line"
    printf 'Unmodified FFmpeg source, built with tools/setup/ffmpeg_macos.sh. Shared libraries only; no GPL, nonfree, version-3,\n'
    printf 'external-library, or network component is enabled. The player uses mov/file, H.264/AAC parsers, and ATRAC9/H.264/AAC decoders.\n'
    printf 'Licence: GNU Lesser General Public License 2.1 or later. FFmpeg-LICENSE.md and FFmpeg-LGPL-2.1.txt carry the notices.\n'
    printf 'Install names are @loader_path-relative so the libraries inside Contents/MacOS can be replaced with ABI-compatible builds.\n'
    printf 'Minimum macOS version: 15.0. The four dylibs are loaded dynamically through FFmpeg.AutoGen.\n\n'
    printf 'Files (SHA-256, before ad-hoc signing):\n'
    (cd "$stage" && shasum -a 256 "${required[@]}")
} > "$stage/FFmpeg-SOURCE.txt"
if grep -Eq '/Users/|/home/' "$stage/FFmpeg-SOURCE.txt"; then
    printf 'ffmpeg_macos: FFmpeg-SOURCE.txt contains an absolute home path\n' >&2
    exit 1
fi

rm -rf "$dest"
mv "$stage" "$dest"
printf 'done: %s\n' "$dest"
