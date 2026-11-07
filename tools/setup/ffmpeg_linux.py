"""Copy the FFmpeg 9 LGPL shared libraries for the Linux release into third_party/ffmpeg/linux.

The Linux counterpart of tools/setup/ffmpeg_dlls.ps1 (package K46). It never downloads
anything: it reads a BtbN "linux64-lgpl-shared-9.0" build that was extracted beforehand,

    third_party/ffmpeg/linux-src/<folder>/lib/libavcodec.so.63 ...
    third_party/ffmpeg/linux-src/<folder>/LICENSE.txt
    third_party/ffmpeg/linux-src/<folder>/bin/ffmpeg

checks it and copies the four libraries the player loads (FfmpegLibrary.RequiredDlls:
libavutil.so.61, libswresample.so.7, libavcodec.so.63, libavformat.so.63) into
third_party/ffmpeg/linux/, with LICENSE.txt, SOURCE.txt (what was copied, for the record)
and FFmpeg-SOURCE.txt (the source notice a Linux publish ships as licenses/FFmpeg-SOURCE.txt).

Checks: every library is a 64-bit x86-64 ELF shared object whose SONAME is its name; the
build is LGPL only (an "lgpl" folder, the package's LICENSE.txt is the LGPL, no libpostproc,
no --enable-gpl / --enable-nonfree in the recorded configuration, each library says
"license: LGPL version 3 or later"); bin/ffmpeg lists the atrac9, h264 and aac decoders. The
decoder list needs a Linux system: the script runs bin/ffmpeg directly on Linux and through WSL
on Windows (headless, nothing is installed); --no-run skips it with a warning.

    python -I tools/setup/ffmpeg_linux.py                 # find the one folder under linux-src
    python -I tools/setup/ffmpeg_linux.py --source DIR    # or name it
    python -I tools/setup/ffmpeg_linux.py --check-only    # verify and compare, copy nothing

Exit 0 when done (or, with --check-only, when the destination matches), 1 on a problem.
third_party/ is git-ignored and never committed.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import struct
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SOURCE_ROOT = REPO / "third_party" / "ffmpeg" / "linux-src"
DESTINATION = REPO / "third_party" / "ffmpeg" / "linux"
REQUIRED = ("libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63")
DECODERS = ("atrac9", "h264", "aac")
# what an FFmpeg library of the build may need from the system besides the bundled four (glibc's own libraries)
SYSTEM_LIBS = {
    "libc.so.6", "libm.so.6", "libpthread.so.0", "libdl.so.2", "librt.so.1", "ld-linux-x86-64.so.2",
    "libmvec.so.1", "libresolv.so.2",
}
ELF_MAGIC = b"\x7fELF"


class ElfInfo:
    """The few facts of an ELF file the checks need."""

    def __init__(self, path: Path):
        data = path.read_bytes()
        self.size = len(data)
        self.sha256 = hashlib.sha256(data).hexdigest()
        self.data = data
        self.ok = data[:4] == ELF_MAGIC and len(data) >= 64
        self.is64 = self.ok and data[4] == 2
        self.little = self.ok and data[5] == 1
        self.type = struct.unpack_from("<H", data, 16)[0] if self.ok else 0
        self.machine = struct.unpack_from("<H", data, 18)[0] if self.ok else 0
        self.soname: str | None = None
        self.needed: list[str] = []
        if self.ok and self.is64 and self.little:
            self._dynamic()

    def _dynamic(self) -> None:
        d = self.data
        shoff = struct.unpack_from("<Q", d, 0x28)[0]
        shentsize, shnum = struct.unpack_from("<HH", d, 0x3A)
        if shoff == 0 or shentsize < 64 or shoff + shentsize * shnum > len(d):
            return
        sections = []
        for i in range(shnum):
            o = shoff + i * shentsize
            _name, sh_type, _flags, _addr, offset, size, link = struct.unpack_from("<IIQQQQI", d, o)
            sections.append((sh_type, offset, size, link))
        for sh_type, offset, size, link in sections:
            if sh_type != 6 or link >= len(sections):  # SHT_DYNAMIC
                continue
            _t, str_off, str_size, _l = sections[link]
            for o in range(offset, min(offset + size, len(d)) - 15, 16):
                tag, value = struct.unpack_from("<qQ", d, o)
                if tag == 0:
                    break
                if tag in (1, 14) and value < str_size:  # DT_NEEDED, DT_SONAME
                    end = d.find(b"\0", str_off + value)
                    name = d[str_off + value:end].decode("ascii", "replace")
                    if tag == 1:
                        self.needed.append(name)
                    else:
                        self.soname = name


def find_source(given: str | None) -> Path | None:
    if given:
        p = Path(given)
        return p if (p / "lib").is_dir() else None
    if (SOURCE_ROOT / "lib").is_dir():
        return SOURCE_ROOT
    found = [p for p in sorted(SOURCE_ROOT.glob("*")) if (p / "lib").is_dir()] if SOURCE_ROOT.is_dir() else []
    return found[0] if len(found) == 1 else None


def resolve_library(lib: Path, name: str) -> Path | None:
    """The real file of a library: the file itself, the target of a link (also one an extractor left as a small text file), or the versioned file beside it."""
    p = lib / name
    for _ in range(4):
        if p.is_symlink():
            p = (p.parent / os.readlink(p)).resolve()
            continue
        if p.is_file() and p.stat().st_size < 256:
            target = p.read_bytes().strip().decode("utf-8", "replace")
            if target and "/" not in target and (lib / target).exists():
                p = lib / target
                continue
        break
    if p.is_file() and p.read_bytes()[:4] == ELF_MAGIC:
        return p
    versioned = sorted(q for q in lib.glob(name + ".*") if q.is_file() and not q.is_symlink())
    return versioned[-1] if versioned else None


def to_wsl(path: Path) -> str:
    full = str(path.resolve())
    m = re.match(r"^([A-Za-z]):[\\/](.*)$", full)
    return f"/mnt/{m.group(1).lower()}/{m.group(2).replace(chr(92), '/')}" if m else full.replace("\\", "/")


def run_ffmpeg(source: Path, args: list[str]) -> tuple[int, str]:
    exe = source / "bin" / "ffmpeg"
    if sys.platform.startswith("linux"):
        env = dict(os.environ, LD_LIBRARY_PATH=str(source / "lib"))
        r = subprocess.run([str(exe), *args], capture_output=True, env=env, timeout=60)
    elif os.name == "nt" and shutil.which("wsl.exe"):
        script = 'LD_LIBRARY_PATH="$1/lib" exec "$1/bin/ffmpeg" "${@:2}"'
        r = subprocess.run(["wsl.exe", "-e", "bash", "-c", script, "ffmpeg", to_wsl(source), *args],
                           capture_output=True, timeout=120, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    else:
        return -1, "no Linux system to run bin/ffmpeg on (not Linux, no WSL)"
    return r.returncode, r.stdout.decode("utf-8", "replace") + r.stderr.decode("utf-8", "replace")


def banner_of(libs: dict[str, ElfInfo], version_output: str | None) -> str:
    if version_output:
        m = re.search(r"ffmpeg version (\S+)", version_output)
        if m:
            return m.group(1)
    m = re.search(rb"\b(n\d+\.\d+[\w.\-]*-g[0-9a-f]{7,12}[\w.\-]*)\0", libs["libavutil.so.61"].data)
    return m.group(1).decode() if m else "unknown"


def provenance(folder: str, banner: str) -> list[str]:
    """Where the exact build and its source are: BtbN's dated archive (not the rolling "latest" copy it may have come from) and the FFmpeg commit."""
    m = re.fullmatch(r"(n(\d+)\.(\d+)[\d.]*)-(\d+)-g([0-9a-f]{7,40})-(\d{4})(\d{2})(\d{2})", banner)
    v = re.search(r"(linux64-lgpl-shared(?:-\d+(?:\.\d+)*)?)$", folder)
    if not m or not v:
        return [
            f"BtbN's archive folder: {folder}. Its dated archive and the FFmpeg source of the build banner are on",
            "https://github.com/BtbN/FFmpeg-Builds/releases and https://git.ffmpeg.org/ffmpeg.git.",
        ]
    tag, major, minor, ahead, commit, year, month, day = m.groups()
    lines = [
        f"Binary archive: ffmpeg-{banner[:-9]}-{v.group(1)}.tar.xz, BtbN's autobuild of {year}-{month}-{day} on",
        "https://github.com/BtbN/FFmpeg-Builds/releases"
        + (f' (copied from its rolling "latest" copy, folder {folder}).' if "latest" in folder else "."),
        "",
        f"FFmpeg source commit: {commit} on release/{major}.{minor}, {ahead} commits after {tag}. The {tag} release tag is not this build's",
        "source revision.",
        f"Source: https://github.com/FFmpeg/FFmpeg/commit/{commit}",
        f"Source archive: https://github.com/FFmpeg/FFmpeg/archive/{commit}.tar.gz",
        f"Build scripts: https://github.com/BtbN/FFmpeg-Builds as used for that autobuild (./build.sh linux64 lgpl-shared {major}.{minor},",
        f"--extra-version={year}{month}{day}).",
    ]
    return lines


def notice(folder: str, banner: str, license_text: str, files: list[tuple[str, ElfInfo]]) -> str:
    lines = [
        "FFmpeg: source and licence notice for the libraries shipped with the Linux port",
        "",
        "The player (Tsukihime) decodes its sound (ATRAC9) and its movies (H.264 video, AAC audio) with the shared libraries of FFmpeg,",
        "https://ffmpeg.org, loaded from the program's folder at run time:",
        "",
        "  " + "  ".join(sorted(REQUIRED)),
        "",
        "They are the unmodified libraries of BtbN's \"linux64-lgpl-shared-9.0\" build.",
        f"Build banner: {banner}.",
        *provenance(folder, banner),
        "BtbN's archive contains the binaries, not the FFmpeg source, and this port's download contains neither; get the source from the",
        "links above.",
        "",
        f"Licence: GNU Lesser General Public License, version 3 or later (libavcodec says \"{license_text}\"). The licence text is",
        "FFmpeg-LGPL.txt next to this file; because the LGPL v3 incorporates the terms of the GNU General Public License v3, that text is",
        "FFmpeg-GPL-3.0.txt beside it. No GPL or nonfree component is in the build.",
        "",
        "The port loads the libraries dynamically through the FFmpeg.AutoGen bindings, MIT, so they can be replaced by another build of",
        "the same major versions, as the LGPL asks.",
        "",
        "Files (name, size, SHA-256):",
    ]
    lines += [f"  {name} {info.size} {info.sha256}" for name, info in files]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--source", help="the extracted build (holding lib/ and LICENSE.txt); default: the one folder under third_party/ffmpeg/linux-src")
    ap.add_argument("--dest", default=str(DESTINATION), help="default: third_party/ffmpeg/linux")
    ap.add_argument("--check-only", action="store_true", help="verify and compare with the destination; copy nothing")
    ap.add_argument("--no-run", action="store_true", help="do not run bin/ffmpeg (the decoders stay unchecked)")
    a = ap.parse_args(argv)

    source = find_source(a.source)
    if source is None:
        print(f"No extracted FFmpeg build was found ({a.source or SOURCE_ROOT}/<folder>/lib/libavcodec.so.63).")
        print("Extract BtbN's ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0.tar.xz there first; this script never downloads. Nothing was copied.")
        return 1
    dest = Path(a.dest)
    problems: list[str] = []
    warnings: list[str] = []
    print(f"source   {source}")
    print(f"target   {dest}")

    if "lgpl" not in source.name.lower():
        problems.append(f"the folder '{source.name}' is not an LGPL build (BtbN names it ...-lgpl-shared-9.0)")
    lic = source / "LICENSE.txt"
    lic_text = lic.read_text("utf-8", "replace") if lic.is_file() else ""
    if not lic_text.lstrip().startswith("GNU LESSER GENERAL PUBLIC LICENSE"):
        problems.append("LICENSE.txt is missing or is not the LGPL text")
    if any((source / "lib").glob("libpostproc*")):
        problems.append("the build has libpostproc, which is GPL only: not the LGPL package")

    libs: dict[str, ElfInfo] = {}
    for name in REQUIRED:
        path = resolve_library(source / "lib", name)
        if path is None:
            problems.append(f"lib/{name} is not in the build")
            continue
        info = ElfInfo(path)
        libs[name] = info
        state = "ok"
        if not (info.ok and info.is64 and info.little and info.machine == 62 and info.type == 3):
            problems.append(f"{name} is not a 64-bit x86-64 ELF shared object"); state = "NOT ELF x86-64"
        elif info.soname != name:
            problems.append(f"{name} has the SONAME {info.soname}, not {name}"); state = "WRONG SONAME"
        if b"--enable-gpl" in info.data or b"--enable-nonfree" in info.data:
            problems.append(f"{name} was configured with --enable-gpl or --enable-nonfree"); state = "GPL"
        if b"license: LGPL version 3 or later" not in info.data:
            problems.append(f"{name} does not say \"license: LGPL version 3 or later\""); state = "LICENCE?"
        others = [n for n in info.needed if n not in REQUIRED and n not in SYSTEM_LIBS]
        if others:
            warnings.append(f"{name} also needs {', '.join(others)} from the system")
        print(f"  {name:<20} {info.size:>12,} B  {path.name:<26} {state}")

    version_out = None
    if a.no_run:
        warnings.append("bin/ffmpeg was not run (--no-run): the atrac9, h264 and aac decoders are not checked")
    elif not (source / "bin" / "ffmpeg").is_file():
        problems.append("bin/ffmpeg is not in the build, so its decoders cannot be checked")
    else:
        code, out = run_ffmpeg(source, ["-hide_banner", "-decoders"])
        if code != 0:
            problems.append(f"bin/ffmpeg -decoders failed ({code}): {out.strip()[:300]}")
        else:
            for d in DECODERS:
                if not re.search(rf"(?m)^\s*\S+\s+{d}\s", out):
                    problems.append(f"the build has no {d} decoder")
            code, version_out = run_ffmpeg(source, ["-hide_banner", "-version"])
            version_out = version_out if code == 0 else None
            if version_out and ("--enable-gpl" in version_out or "--enable-nonfree" in version_out):
                problems.append("bin/ffmpeg reports a GPL or nonfree configuration")

    banner = banner_of(libs, version_out) if "libavutil.so.61" in libs else "unknown"
    print(f"build    {banner}")
    for w in warnings:
        print(f"warning: {w}")
    if problems or len(libs) != len(REQUIRED):
        for p in problems:
            print(f"error: {p}")
        print("The build failed verification; nothing was copied.")
        return 1

    m = re.search(rb"license: (LGPL version [^\0]+)\0", libs["libavcodec.so.63"].data)
    files = [(n, libs[n]) for n in sorted(REQUIRED)]
    texts = {
        "SOURCE.txt": "\n".join([
            "# FFmpeg shared libraries copied by tools/setup/ffmpeg_linux.py. Local only; never committed.",
            f"folder: {source.name}",
            f"build: {banner}",
            "licence: LGPL v3 or later (LICENSE.txt is the package's own copy of the LGPL v3 text)",
            "files (name, size, sha256):",
            *[f"  {n} {i.size} {i.sha256}" for n, i in files],
        ]) + "\n",
        "FFmpeg-SOURCE.txt": notice(source.name, banner, m.group(1).decode() if m else "LGPL version 3 or later", files),
    }
    plan: list[tuple[str, bytes]] = [(n, i.data) for n, i in files]
    plan.append(("LICENSE.txt", lic.read_bytes()))
    plan += [(n, t.encode("utf-8")) for n, t in texts.items()]

    dirty = 0
    for name, data in plan:
        target = dest / name
        same = target.is_file() and target.read_bytes() == data
        if a.check_only:
            print(f"  {'in sync' if same else 'differs':<9} {target}")
        elif not same:
            dest.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            if hashlib.sha256(target.read_bytes()).digest() != hashlib.sha256(data).digest():
                print(f"error: the copy of {name} did not verify")
                return 1
            print(f"  copied    {target}")
        else:
            print(f"  unchanged {target}")
        dirty += 0 if same else 1
    if a.check_only:
        print("check only: the destination matches the build." if dirty == 0 else f"check only: {dirty} file(s) differ; run without --check-only to copy.")
        return 0 if dirty == 0 else 1
    print(f"done: {dirty} file(s) copied or updated, {len(plan) - dirty} unchanged.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
