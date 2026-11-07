"""Pack a Linux publish folder into the release archive Tsukihime-vX.Y.Z-linux-x64.tar.gz (package K46).

    python -I tools/setup/package_linux.py PUBLISH_DIR --version X.Y.Z [--out DIR]

The archive holds one folder, Tsukihime-vX.Y.Z-linux-x64/, with the publish folder's files. Unix modes are
set here, so the archive is right when it is made on Windows: 0755 for both programs (TsukihimeLauncher and Tsukihime), createdump and
every shared object (*.so, *.so.N), 0644 for the other files, 0755 for folders; owner root, no user names, one
time stamp (SOURCE_DATE_EPOCH when set, else the newest file's), gzip without a name or time, so the same folder
gives the same bytes. Links and other special files are refused. Run `Tsuki.Cli release-check PUBLISH_DIR` first
and `Tsuki.Cli release-sums` on the result (docs/PUBLISHING.md).
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import io
import os
import re
import sys
import tarfile
from pathlib import Path

EXECUTABLES = {"Tsukihime", "TsukihimeLauncher", "createdump"}
SHARED_OBJECT = re.compile(r"\.so(\.\d+)*$")
REQUIRED_NATIVE = ("libSDL3.so", "libSkiaSharp.so", "libHarfBuzzSharp.so", "libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63")


def mode_of(name: str) -> int:
    return 0o755 if name in EXECUTABLES or SHARED_OBJECT.search(name) else 0o644


def build(folder: Path, version: str, out_dir: Path) -> Path:
    if not re.fullmatch(r"\d{1,5}\.\d{1,5}\.\d{1,5}", version):
        raise ValueError(f"the version must be X.Y.Z, not {version!r}")
    missing = [name for name in ("Tsukihime", "Tsukihime.dll", "TsukihimeLauncher", "TsukihimeLauncher.dll", *REQUIRED_NATIVE) if not (folder / name).is_file()]
    if missing:
        raise ValueError(f"{folder} is missing {', '.join(missing)}: not a Linux launcher publish")
    files: list[Path] = []
    dirs: list[Path] = []
    for path in sorted(folder.rglob("*"), key=lambda p: p.relative_to(folder).as_posix()):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError(f"{path} is a link or a special file; the archive holds only folders and plain files")
        (dirs if path.is_dir() else files).append(path)
    epoch = os.environ.get("SOURCE_DATE_EPOCH")
    mtime = int(epoch) if epoch else max(int(p.stat().st_mtime) for p in files)

    root = f"Tsukihime-v{version}-linux-x64"
    out_dir.mkdir(parents=True, exist_ok=True)
    target = out_dir / f"{root}.tar.gz"

    def info(name: str, mode: int, kind: bytes, size: int = 0) -> tarfile.TarInfo:
        ti = tarfile.TarInfo(name)
        ti.mode, ti.type, ti.size, ti.mtime = mode, kind, size, mtime
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = ""
        return ti

    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.PAX_FORMAT) as tar:
        tar.addfile(info(root, 0o755, tarfile.DIRTYPE))
        entries = [(d, None) for d in dirs] + [(f, f) for f in files]
        for path, _ in sorted(entries, key=lambda e: e[0].relative_to(folder).as_posix()):
            rel = path.relative_to(folder).as_posix()
            if path.is_dir():
                tar.addfile(info(f"{root}/{rel}", 0o755, tarfile.DIRTYPE))
            else:
                data = path.read_bytes()
                tar.addfile(info(f"{root}/{rel}", mode_of(path.name), tarfile.REGTYPE, len(data)), io.BytesIO(data))
    with open(target, "wb") as f, gzip.GzipFile(filename="", mode="wb", fileobj=f, mtime=0, compresslevel=9) as gz:
        gz.write(raw.getvalue())
    return target


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("folder", help="the Linux publish folder (Tsukihime, Tsukihime.dll, the libraries, licenses/)")
    ap.add_argument("--version", required=True, help="X.Y.Z, the version of the release (Tsukihime.dll's; release-sums checks they agree)")
    ap.add_argument("--out", default=".", help="where the archive goes (default: the current folder)")
    a = ap.parse_args(argv)
    try:
        target = build(Path(a.folder), a.version, Path(a.out))
    except ValueError as e:
        print(f"package_linux: {e}")
        return 1
    digest = hashlib.sha256(target.read_bytes()).hexdigest()
    with tarfile.open(target) as tar:
        count = sum(1 for m in tar.getmembers() if m.isfile())
    print(f"wrote {target} ({target.stat().st_size:,} bytes, {count} files)")
    print(f"sha256 {digest}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
