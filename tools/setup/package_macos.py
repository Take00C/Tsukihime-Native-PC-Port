"""Assemble a macOS publish folder as Tsukihime.app; sign and archive only when asked.

    python -I tools/setup/package_macos.py PUBLISH_DIR --version X.Y.Z --arch arm64|x64 [--out DIR]
    python -I tools/setup/package_macos.py PUBLISH_DIR --version X.Y.Z --arch arm64 --no-sign

The flat publish's programs and libraries go in Contents/MacOS. Documents, assets and licences go in
Contents/Resources, beside a generated crescent ICNS. The default signs ad hoc with codesign and verifies
the result. --prefix gives the nested game host the fixed identifier HOST_IDENTIFIER: without it, codesign names
an ad-hoc host 'Tsukihime-55554944<LC_UUID>' (signer.cpp), which changes with every build. --no-sign is the
explicit structural-check mode for non-Mac hosts. --zip is macOS-only and
uses ditto --sequesterRsrc --keepParent, producing Tsukihime-vX.Y.Z-macos-ARCH.zip. codesign can keep the signatures of
the non-Mach-O files in Contents/MacOS in extended attributes; --sequesterRsrc stores those as __MACOSX/ AppleDouble
entries, which ditto -x restores. No game data belongs in the publish.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ICON_DIR = REPO / "assets" / "icon"
FFMPEG = ("libavutil.61.dylib", "libswresample.7.dylib", "libavcodec.63.dylib", "libavformat.63.dylib")
LAUNCHER_NATIVE = ("libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib")
RESOURCE_FILES = {"README.md", "CONTRIBUTING.md", "LICENSE", "MACOS.md"}
RESOURCE_DIRS = {"docs", "assets", "licenses"}
CPUTYPES = {"x64": 0x01000007, "arm64": 0x0100000C}
BUNDLE_IDENTIFIER = "io.github.take00c.tsukihime"
# codesign puts --prefix before an implicit identifier that has no dot, and adds no build-specific suffix to one that then has a dot
# (Apple's codesign, signer.cpp). The launcher keeps BUNDLE_IDENTIFIER from Info.plist; ReleaseRules holds HOST_IDENTIFIER too.
SIGNING_PREFIX = BUNDLE_IDENTIFIER + "."
HOST_IDENTIFIER = SIGNING_PREFIX + "Tsukihime"


def thin_macho_architecture(path: Path, executable: bool = False) -> str:
    """Return x64/arm64 for a thin 64-bit Mach-O, refusing universal and malformed files."""
    data = path.read_bytes()
    if len(data) < 32 or data[:4] != b"\xcf\xfa\xed\xfe":
        raise ValueError(f"{path.name} is not a thin 64-bit little-endian Mach-O file")
    cpu = struct.unpack_from("<I", data, 4)[0]
    filetype = struct.unpack_from("<I", data, 12)[0]
    if executable and filetype != 2:  # MH_EXECUTE
        raise ValueError(f"{path.name} is not a Mach-O executable")
    for arch, expected in CPUTYPES.items():
        if cpu == expected:
            return arch
    raise ValueError(f"{path.name} has unsupported Mach-O CPU type 0x{cpu:x}")


def normalize_avalonia_install_name(app: Path) -> None:
    path = app / "Contents" / "MacOS" / "libAvaloniaNative.dylib"
    data = bytearray(path.read_bytes())
    old = b"/usr/local/lib/libAvalonia.Native.OSX.dylib"
    new = b"@rpath/libAvaloniaNative.dylib"

    def rewrite(start: int, size: int) -> None:
        if size < 32 or data[start:start + 4] != b"\xcf\xfa\xed\xfe":
            raise ValueError("libAvaloniaNative.dylib is not a thin 64-bit Mach-O slice")
        count = struct.unpack_from("<I", data, start + 16)[0]
        command_bytes = struct.unpack_from("<i", data, start + 20)[0]
        if command_bytes < 0 or command_bytes > size - 32:
            raise ValueError(f"libAvaloniaNative.dylib declares {command_bytes:#x} command bytes in a {size:#x}-byte slice")
        pos = start + 32
        end = pos + command_bytes
        for _ in range(count):
            if pos + 8 > end:
                raise ValueError("libAvaloniaNative.dylib has truncated load commands")
            command, command_size = struct.unpack_from("<II", data, pos)
            if command_size < 8 or pos + command_size > end:
                raise ValueError(f"libAvaloniaNative.dylib has invalid load commands at {pos:#x}, size {command_size:#x}, end {end:#x}")
            if command == 0xD:
                name_offset = struct.unpack_from("<I", data, pos + 8)[0]
                name_start = pos + name_offset
                name_end = data.index(0, name_start, pos + command_size)
                if data[name_start:name_end] == old:
                    if len(new) > len(old):
                        raise ValueError("the Avalonia install name has no room for its relocatable form")
                    data[name_start:name_end] = new + bytes(len(old) - len(new))
            pos += command_size
        if pos != end:
            raise ValueError("libAvaloniaNative.dylib has inconsistent load commands")

    if data[:4] == b"\xca\xfe\xba\xbe":
        count = struct.unpack_from(">I", data, 4)[0]
        for index in range(count):
            _, _, offset, size, _ = struct.unpack_from(">IIIII", data, 8 + index * 20)
            rewrite(offset, size)
    else:
        rewrite(0, len(data))
    path.write_bytes(data)


def make_icns(icon_dir: Path = ICON_DIR) -> bytes:
    """An ICNS whose 16, 32 and 256 pixel entries hold the existing crescent PNGs directly."""
    entries = []
    for kind, size in ((b"icp4", 16), (b"icp5", 32), (b"ic08", 256)):
        png = (icon_dir / f"tsukihime_{size}.png").read_bytes()
        if png[:8] != b"\x89PNG\r\n\x1a\n" or png[12:16] != b"IHDR":
            raise ValueError(f"the {size}px crescent is not a PNG")
        if struct.unpack_from(">II", png, 16) != (size, size):
            raise ValueError(f"the {size}px crescent has the wrong dimensions")
        entries.append(kind + struct.pack(">I", len(png) + 8) + png)
    body = b"".join(entries)
    return b"icns" + struct.pack(">I", len(body) + 8) + body


def _copy_publish(folder: Path, app: Path) -> None:
    macos = app / "Contents" / "MacOS"
    resources = app / "Contents" / "Resources"
    macos.mkdir(parents=True)
    resources.mkdir()
    files = []
    for path in sorted(folder.rglob("*"), key=lambda p: p.relative_to(folder).as_posix()):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError(f"{path} is a link or special file; a publish holds plain files and folders")
        if path.is_file():
            files.append(path)
    if not files:
        raise ValueError(f"{folder} is empty")
    for path in files:
        relative = path.relative_to(folder)
        resource = relative.parts[0] in RESOURCE_DIRS or relative.as_posix() in RESOURCE_FILES
        target = (resources if resource else macos) / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, target)


def build(folder: Path, version: str, arch: str, out_dir: Path, *, sign: bool = True,
          icon_dir: Path = ICON_DIR) -> Path:
    if not re.fullmatch(r"\d{1,5}\.\d{1,5}\.\d{1,5}", version):
        raise ValueError(f"the version must be X.Y.Z, not {version!r}")
    if arch not in CPUTYPES:
        raise ValueError("the architecture must be arm64 or x64")
    if sign and sys.platform != "darwin":
        raise ValueError("codesign needs macOS; use --no-sign only for an unsigned structural check")
    host = folder / "Tsukihime"
    if not host.is_file() or not (folder / "Tsukihime.dll").is_file():
        raise ValueError(f"{folder} holds no Tsukihime apphost and assembly: not a macOS publish folder")
    for program in ("Tsukihime", "TsukihimeLauncher"):
        if not (folder / f"{program}.dll").is_file():
            raise ValueError(f"the publish is missing {program}.dll")
        actual = thin_macho_architecture(folder / program, executable=True)
        if actual != arch:
            raise ValueError(f"{program} is {actual}, not the requested {arch}")
    missing = [name for name in ("libSDL3.dylib", *LAUNCHER_NATIVE, *FFMPEG, "licenses/FFmpeg-SOURCE.txt") if not (folder / name).is_file()]
    if missing:
        raise ValueError("the publish is missing " + ", ".join(missing))
    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / "Tsukihime.app"
    if target.exists():
        raise ValueError(f"{target} already exists; remove the old build first")
    temporary = Path(tempfile.mkdtemp(prefix=".Tsukihime-package-", dir=out_dir)) / "Tsukihime.app"
    try:
        _copy_publish(folder, temporary)
        normalize_avalonia_install_name(temporary)
        contents = temporary / "Contents"
        info = {
            "CFBundleName": "Tsukihime",
            "CFBundleDisplayName": "Tsukihime",
            "CFBundleExecutable": "TsukihimeLauncher",
            "CFBundleIdentifier": BUNDLE_IDENTIFIER,
            "CFBundleVersion": version,
            "CFBundleShortVersionString": version,
            "CFBundlePackageType": "APPL",
            "CFBundleIconFile": "Tsukihime",
            "LSMinimumSystemVersion": "15.0",
            "LSApplicationCategoryType": "public.app-category.games",
            "NSHighResolutionCapable": True,
            "NSSupportsAutomaticGraphicsSwitching": True,
        }
        with (contents / "Info.plist").open("wb") as stream:
            plistlib.dump(info, stream, fmt=plistlib.FMT_XML, sort_keys=True)
        (contents / "Resources" / "Tsukihime.icns").write_bytes(make_icns(icon_dir))
        for path in sorted(temporary.rglob("*"), reverse=True):
            if path.is_dir():
                path.chmod(0o755)
            else:
                executable = path.name in {"Tsukihime", "TsukihimeLauncher", "createdump"} or path.name.endswith(".dylib")
                path.chmod(0o755 if executable else 0o644)
        temporary.rename(target)
    except BaseException:
        shutil.rmtree(temporary.parent, ignore_errors=True)
        raise
    temporary.parent.rmdir()
    if sign:
        subprocess.run(["codesign", "--force", "--deep", "--prefix", SIGNING_PREFIX, "-s", "-", str(target)], check=True)
        subprocess.run(["codesign", "--verify", "--deep", "--strict", "--verbose=2", str(target)], check=True)
    return target


def archive(app: Path, version: str, arch: str, out_dir: Path) -> Path:
    if sys.platform != "darwin":
        raise ValueError("a release ZIP must be made on macOS with ditto; --no-sign checks the .app only")
    if not app.is_dir() or app.parent != out_dir:
        raise ValueError("the app must be the Tsukihime.app directly inside the output folder")
    target = out_dir / f"Tsukihime-v{version}-macos-{arch}.zip"
    subprocess.run(["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", app.name, target.name], cwd=out_dir, check=True)
    return target


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("folder", help="the flat osx-arm64 or osx-x64 self-contained publish folder")
    ap.add_argument("--version", required=True, help="X.Y.Z, matching Tsukihime.dll")
    ap.add_argument("--arch", required=True, choices=("arm64", "x64"))
    ap.add_argument("--out", default=".", help="the app's parent folder (default: current folder)")
    ap.add_argument("--no-sign", action="store_true", help="leave unsigned for a structural check (required off macOS)")
    ap.add_argument("--zip", action="store_true", help="on macOS, also make the release ZIP with ditto")
    a = ap.parse_args(argv)
    try:
        app = build(Path(a.folder), a.version, a.arch, Path(a.out), sign=not a.no_sign)
        print(f"{'unsigned structural app' if a.no_sign else 'signed app'}: {app}")
        if a.zip:
            target = archive(app, a.version, a.arch, Path(a.out))
            print(f"wrote {target} ({target.stat().st_size:,} bytes)")
            print(f"sha256 {hashlib.sha256(target.read_bytes()).hexdigest()}")
            with zipfile.ZipFile(target) as zipped:
                names = [n.filename for n in zipped.infolist() if not n.is_dir()]
            metadata = sum(name.startswith("__MACOSX/") for name in names)
            print(f"{len(names) - metadata} files, {metadata} __MACOSX entries")
    except (OSError, ValueError, subprocess.CalledProcessError) as e:
        print(f"package_macos: {e}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
