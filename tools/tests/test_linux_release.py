"""Package K46: the Linux release tools, tools/setup/ffmpeg_linux.py and tools/setup/package_linux.py, on made-up files (no FFmpeg, no game data)."""
from __future__ import annotations

import importlib.util
import struct
import tarfile
from pathlib import Path

import pytest

SETUP = Path(__file__).resolve().parents[1] / "setup"
LIBS = ("libavutil.so.61", "libswresample.so.7", "libavcodec.so.63", "libavformat.so.63")
LGPL = "--prefix=/ffbuild/prefix --enable-version3 --enable-shared"


def load(name):
    spec = importlib.util.spec_from_file_location(name, SETUP / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def fake_so(soname: str, text: str = "", needed: tuple[str, ...] = ()) -> bytes:
    """An x86-64 ELF shared object with a .dynstr / .dynamic pair naming SONAME and DT_NEEDED, followed by TEXT."""
    dynstr = b"\0" + soname.encode() + b"\0"
    entries = [(14, 1)]
    for n in needed:
        entries.append((1, len(dynstr)))
        dynstr += n.encode() + b"\0"
    entries.append((0, 0))
    dynamic = b"".join(struct.pack("<qQ", t, v) for t, v in entries)
    body = text.encode()
    dynstr_off = 64
    dynamic_off = dynstr_off + len(dynstr) + (-len(dynstr) % 8)
    body_off = dynamic_off + len(dynamic)
    shoff = body_off + len(body) + (-(body_off + len(body)) % 8)

    def section(kind: int, offset: int, size: int, link: int = 0) -> bytes:
        return struct.pack("<IIQQQQIIQQ", 0, kind, 0, 0, offset, size, link, 0, 8, 16 if kind == 6 else 0)

    header = bytearray(64)
    header[0:4] = b"\x7fELF"
    header[4], header[5], header[6] = 2, 1, 1
    struct.pack_into("<HH", header, 16, 3, 62)
    struct.pack_into("<Q", header, 0x28, shoff)
    struct.pack_into("<HHH", header, 0x3A, 64, 3, 0)
    data = bytearray(shoff)
    data[0:64] = header
    data[dynstr_off:dynstr_off + len(dynstr)] = dynstr
    data[dynamic_off:dynamic_off + len(dynamic)] = dynamic
    data[body_off:body_off + len(body)] = body
    return bytes(data) + section(0, 0, 0) + section(3, dynstr_off, len(dynstr)) + section(6, dynamic_off, len(dynamic), 1)


def fake_build(root: Path, name: str = "ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0", configuration: str = LGPL) -> Path:
    build = root / name
    (build / "lib").mkdir(parents=True)
    (build / "bin").mkdir()
    for lib in LIBS:
        prefix = lib.split(".")[0]
        text = f"\0{prefix} license: LGPL version 3 or later\0{configuration}\0n9.0.2-39-g7adfbe40dc-20261031\0"
        (build / "lib" / (lib + ".1.100")).write_bytes(fake_so(lib, text, ("libc.so.6", "libm.so.6", "libavutil.so.61")))
        (build / "lib" / lib).write_text(lib + ".1.100")            # a link as an extractor without link support leaves it
    (build / "LICENSE.txt").write_text("                   GNU LESSER GENERAL PUBLIC LICENSE\n                       Version 3, 29 June 2007\n")
    return build


def test_a_verified_build_is_copied_with_its_notices(tmp_path, capsys):
    tool = load("ffmpeg_linux")
    build = fake_build(tmp_path / "src")
    dest = tmp_path / "linux"
    assert tool.main(["--source", str(build), "--dest", str(dest), "--no-run"]) == 0
    for lib in LIBS:
        assert (dest / lib).read_bytes() == (build / "lib" / (lib + ".1.100")).read_bytes()
        assert tool.ElfInfo(dest / lib).soname == lib
    assert (dest / "LICENSE.txt").read_text().lstrip().startswith("GNU LESSER GENERAL PUBLIC LICENSE")
    source = (dest / "SOURCE.txt").read_text()
    assert "build: n9.0.2-39-g7adfbe40dc-20261031" in source and "never committed" in source
    notice = (dest / "FFmpeg-SOURCE.txt").read_text()
    assert "linux64-lgpl-shared-9.0" in notice and "7adfbe40dc" in notice and "FFmpeg-GPL-3.0.txt" in notice
    assert all(lib in notice for lib in LIBS)
    # the dated archive, not the rolling "latest" one, and the exact source commit; no source "on request" (docs/PUBLISHING.md 3.3)
    assert "Binary archive: ffmpeg-n9.0.2-39-g7adfbe40dc-linux64-lgpl-shared-9.0.tar.xz, BtbN's autobuild of 2026-10-31" in notice
    assert "Source archive: https://github.com/FFmpeg/FFmpeg/archive/7adfbe40dc.tar.gz" in notice
    assert "7adfbe40dc on release/9.0, 39 commits after n9.0.2" in notice
    assert "on request" not in notice and "contains the binaries, not the FFmpeg source" in notice
    assert "decoders are not checked" in capsys.readouterr().out
    # a second run copies nothing, and the check agrees
    assert tool.main(["--source", str(build), "--dest", str(dest), "--no-run"]) == 0
    assert "0 file(s) copied" in capsys.readouterr().out
    assert tool.main(["--source", str(build), "--dest", str(dest), "--no-run", "--check-only"]) == 0


def test_the_notice_names_the_dated_archive_of_a_versioned_folder_and_falls_back_without_a_banner():
    tool = load("ffmpeg_linux")
    dated = "\n".join(tool.provenance("ffmpeg-n9.0.2-45-g1e65c6cd5d-linux64-lgpl-shared-9.0", "n9.0.2-45-g1e65c6cd5d-20261102"))
    assert "ffmpeg-n9.0.2-45-g1e65c6cd5d-linux64-lgpl-shared-9.0.tar.xz, BtbN's autobuild of 2026-11-02" in dated
    assert "latest" not in dated and "archive/1e65c6cd5d.tar.gz" in dated and "--extra-version=20261102" in dated
    unknown = "\n".join(tool.provenance("ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0", "unknown"))
    assert "ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0" in unknown and "git.ffmpeg.org" in unknown


@pytest.mark.parametrize("case, message", [
    ("gpl", "configured with --enable-gpl or --enable-nonfree"),
    ("postproc", "libpostproc"),
    ("folder", "is not an LGPL build"),
    ("licence", "LICENSE.txt is missing or is not the LGPL text"),
    ("soname", "has the SONAME"),
    ("missing", "lib/libavformat.so.63 is not in the build"),
])
def test_a_build_that_is_not_the_lgpl_ffmpeg_9_is_refused(tmp_path, capsys, case, message):
    tool = load("ffmpeg_linux")
    build = fake_build(tmp_path / "src", name="ffmpeg-n9.0-latest-linux64-gpl-shared-9.0" if case == "folder" else
                       "ffmpeg-n9.0-latest-linux64-lgpl-shared-9.0", configuration=LGPL + " --enable-gpl" if case == "gpl" else LGPL)
    if case == "postproc":
        (build / "lib" / "libpostproc.so.59").write_bytes(fake_so("libpostproc.so.59"))
    elif case == "licence":
        (build / "LICENSE.txt").write_text("GNU GENERAL PUBLIC LICENSE\n")
    elif case == "soname":
        (build / "lib" / "libavcodec.so.63.1.100").write_bytes(fake_so("libavcodec.so.62", "\0libavcodec license: LGPL version 3 or later\0"))
    elif case == "missing":
        for p in (build / "lib").glob("libavformat*"):
            p.unlink()
    dest = tmp_path / "linux"
    assert tool.main(["--source", str(build), "--dest", str(dest), "--no-run"]) == 1
    out = capsys.readouterr().out
    assert message in out and "nothing was copied" in out
    assert not dest.exists()


def test_no_extracted_build_means_nothing_is_done(tmp_path, capsys):
    tool = load("ffmpeg_linux")
    assert tool.main(["--source", str(tmp_path / "none"), "--dest", str(tmp_path / "linux"), "--no-run"]) == 1
    assert "never downloads" in capsys.readouterr().out


def publish_folder(root: Path) -> Path:
    folder = root / "publish"
    (folder / "licenses").mkdir(parents=True)
    (folder / "Tsukihime").write_bytes(b"\x7fELF" + b"\0" * 60)
    (folder / "Tsukihime.dll").write_bytes(b"MZ")
    (folder / "createdump").write_bytes(b"\x7fELF")
    for lib in ("libSDL3.so", "libcoreclr.so", *LIBS):
        (folder / lib).write_bytes(b"\x7fELF")
    (folder / "licenses" / "FFmpeg-LGPL.txt").write_text("lgpl")
    (folder / "LINUX.md").write_text("# Linux")
    return folder


def test_the_archive_has_one_folder_and_unix_modes_and_is_reproducible(tmp_path, monkeypatch):
    tool = load("package_linux")
    monkeypatch.setenv("SOURCE_DATE_EPOCH", "1793000000")
    folder = publish_folder(tmp_path)
    first = tool.build(folder, "0.1.6", tmp_path / "out1")
    second = tool.build(folder, "0.1.6", tmp_path / "out2")
    assert first.name == "Tsukihime-v0.1.6-linux-x64.tar.gz"
    assert first.read_bytes() == second.read_bytes()
    with tarfile.open(first) as tar:
        members = {m.name: m for m in tar.getmembers()}
    assert all(n == "Tsukihime-v0.1.6-linux-x64" or n.startswith("Tsukihime-v0.1.6-linux-x64/") for n in members)
    root = "Tsukihime-v0.1.6-linux-x64/"
    for name in ("Tsukihime", "createdump", "libSDL3.so", "libcoreclr.so", *LIBS):
        assert members[root + name].mode == 0o755, name
    for name in ("Tsukihime.dll", "LINUX.md", "licenses/FFmpeg-LGPL.txt"):
        assert members[root + name].mode == 0o644, name
    assert members[root + "licenses"].isdir() and members[root + "licenses"].mode == 0o755
    assert all(m.uid == 0 and m.gid == 0 and m.uname == "" and m.mtime == 1793000000 for m in members.values())
    assert all(m.isfile() or m.isdir() for m in members.values())


def test_a_folder_that_is_not_a_linux_publish_is_refused(tmp_path):
    tool = load("package_linux")
    folder = publish_folder(tmp_path)
    with pytest.raises(ValueError, match="X.Y.Z"):
        tool.build(folder, "v0.1.6", tmp_path / "out")
    (folder / "Tsukihime").unlink()
    with pytest.raises(ValueError, match="not a Linux publish folder"):
        tool.build(folder, "0.1.6", tmp_path / "out")
