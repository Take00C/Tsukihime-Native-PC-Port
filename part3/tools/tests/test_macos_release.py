"""Structural checks of the macOS packaging tools; no Mac, game data, or TYPE-MOON art is needed."""
from __future__ import annotations

import importlib.util
import os
import plistlib
import re
import struct
import sys
import zipfile
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SETUP = REPO / "tools" / "setup"
spec = importlib.util.spec_from_file_location("package_macos", SETUP / "package_macos.py")
package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(package)


def macho(arch: str, filetype: int = 2) -> bytes:
    data = bytearray(64)
    data[:4] = b"\xcf\xfa\xed\xfe"
    struct.pack_into("<IIII", data, 4, package.CPUTYPES[arch], 3, filetype, 0)
    return bytes(data)


def publish(root: Path, arch: str = "arm64") -> Path:
    folder = root / "publish"
    (folder / "licenses").mkdir(parents=True)
    (folder / "docs").mkdir()
    (folder / "assets" / "branding").mkdir(parents=True)
    (folder / "Tsukihime").write_bytes(macho(arch))
    (folder / "Tsukihime.dll").write_bytes(b"MZ managed assembly")
    (folder / "TsukihimeLauncher").write_bytes(macho(arch))
    (folder / "TsukihimeLauncher.dll").write_bytes(b"MZ managed assembly")
    (folder / "createdump").write_bytes(macho(arch))
    for lib in ("libSDL3.dylib", "libcoreclr.dylib", *package.LAUNCHER_NATIVE, *package.FFMPEG):
        (folder / lib).write_bytes(macho(arch, 6))
    (folder / "README.md").write_text("# Tsukihime\n")
    (folder / "MACOS.md").write_text("# macOS\n")
    (folder / "docs" / "MACOS.md").write_text("# macOS\n")
    (folder / "assets" / "branding" / "tsukihime-logo.png").write_bytes(b"README logo")
    (folder / "licenses" / "FFmpeg-SOURCE.txt").write_text("source commit\n")
    return folder


def test_thin_macho_checks_cpu_type_and_executable(tmp_path):
    path = tmp_path / "Tsukihime"
    path.write_bytes(macho("arm64"))
    assert package.thin_macho_architecture(path, executable=True) == "arm64"
    path.write_bytes(macho("x64"))
    assert package.thin_macho_architecture(path) == "x64"
    path.write_bytes(b"\xca\xfe\xba\xbe" + b"\0" * 60)
    with pytest.raises(ValueError, match="thin 64-bit"):
        package.thin_macho_architecture(path)


def test_icns_contains_only_the_crescent_png_entries():
    data = package.make_icns()
    assert data[:4] == b"icns" and struct.unpack_from(">I", data, 4)[0] == len(data)
    offset = 8
    entries = {}
    while offset < len(data):
        kind = data[offset:offset + 4]
        size = struct.unpack_from(">I", data, offset + 4)[0]
        entries[kind] = data[offset + 8:offset + size]
        offset += size
    assert set(entries) == {b"icp4", b"icp5", b"ic08"}
    assert entries[b"icp4"] == (package.ICON_DIR / "tsukihime_16.png").read_bytes()
    assert entries[b"icp5"] == (package.ICON_DIR / "tsukihime_32.png").read_bytes()
    assert entries[b"ic08"] == (package.ICON_DIR / "tsukihime_256.png").read_bytes()


def test_unsigned_app_layout_metadata_and_modes(tmp_path):
    folder = publish(tmp_path)
    app = package.build(folder, "0.1.6", "arm64", tmp_path / "out", sign=False)
    assert not list((tmp_path / "out").glob(".Tsukihime-package-*"))
    contents = app / "Contents"
    assert (contents / "MacOS" / "Tsukihime").read_bytes() == macho("arm64")
    assert (contents / "MacOS" / "Tsukihime.dll").is_file()
    assert (contents / "MacOS" / "libavcodec.63.dylib").is_file()
    assert (contents / "Resources" / "README.md").is_file()
    assert (contents / "Resources" / "docs" / "MACOS.md").is_file()
    assert (contents / "Resources" / "licenses" / "FFmpeg-SOURCE.txt").is_file()
    with (contents / "Info.plist").open("rb") as stream:
        info = plistlib.load(stream)
    assert info["CFBundleIdentifier"] == "io.github.take00c.tsukihime"
    assert info["CFBundleExecutable"] == "TsukihimeLauncher"
    assert (contents / "MacOS" / "TsukihimeLauncher").is_file()
    assert (contents / "MacOS" / "TsukihimeLauncher.dll").is_file()
    assert info["CFBundleShortVersionString"] == "0.1.6"
    assert info["LSMinimumSystemVersion"] == "15.0"
    assert info["NSSupportsAutomaticGraphicsSwitching"] is True
    assert (contents / "Resources" / "Tsukihime.icns").read_bytes() == package.make_icns()
    if os.name != "nt":
        assert (contents / "MacOS" / "Tsukihime").stat().st_mode & 0o777 == 0o755
        assert (contents / "MacOS" / "TsukihimeLauncher").stat().st_mode & 0o777 == 0o755
        assert (contents / "MacOS" / "libSDL3.dylib").stat().st_mode & 0o777 == 0o755
        assert (contents / "MacOS" / "Tsukihime.dll").stat().st_mode & 0o777 == 0o644
        assert (contents / "Resources" / "Tsukihime.icns").stat().st_mode & 0o777 == 0o644


def test_wrong_architecture_incomplete_publish_and_existing_output_are_refused(tmp_path):
    folder = publish(tmp_path, "x64")
    with pytest.raises(ValueError, match="not the requested arm64"):
        package.build(folder, "0.1.6", "arm64", tmp_path / "wrong", sign=False)
    (folder / "libavformat.63.dylib").unlink()
    with pytest.raises(ValueError, match="libavformat.63.dylib"):
        package.build(folder, "0.1.6", "x64", tmp_path / "missing", sign=False)
    with pytest.raises(ValueError, match="X.Y.Z"):
        package.build(folder, "v0.1.6", "x64", tmp_path / "version", sign=False)


def test_signing_and_release_zip_are_not_silently_skipped_off_macos(tmp_path, monkeypatch):
    folder = publish(tmp_path)
    monkeypatch.setattr(sys, "platform", "linux")
    with pytest.raises(ValueError, match="codesign needs macOS"):
        package.build(folder, "0.1.6", "arm64", tmp_path / "signed")
    with pytest.raises(ValueError, match="must be made on macOS"):
        package.archive(tmp_path / "Tsukihime.app", "0.1.6", "arm64", tmp_path)


def test_ad_hoc_sign_and_ditto_commands_when_on_macos(tmp_path, monkeypatch):
    folder = publish(tmp_path)
    calls = []
    monkeypatch.setattr(sys, "platform", "darwin")
    monkeypatch.setattr(package.subprocess, "run", lambda command, **kwargs: calls.append((command, kwargs)))
    app = package.build(folder, "0.1.6", "arm64", tmp_path / "out")
    # --prefix: the nested game host gets a fixed, dotted identifier instead of codesign's ad-hoc 'Tsukihime-55554944<LC_UUID>'
    assert calls[0][0] == ["codesign", "--force", "--deep", "--prefix", "io.github.take00c.tsukihime.", "-s", "-", str(app)]
    assert calls[1][0][:4] == ["codesign", "--verify", "--deep", "--strict"]
    target = package.archive(app, "0.1.6", "arm64", tmp_path / "out")
    assert target.name == "Tsukihime-v0.1.6-macos-arm64.zip"
    # --sequesterRsrc: the signatures codesign keeps in extended attributes travel as __MACOSX/ AppleDouble entries
    assert calls[2][0] == ["ditto", "-c", "-k", "--sequesterRsrc", "--keepParent", "Tsukihime.app", target.name]


def test_the_zip_summary_counts_app_files_and_macosx_entries_apart(tmp_path, monkeypatch, capsys):
    app = tmp_path / "Tsukihime.app"
    zip_path = tmp_path / "Tsukihime-v0.1.6-macos-arm64.zip"
    with zipfile.ZipFile(zip_path, "w") as zipped:
        zipped.writestr("Tsukihime.app/", "")
        zipped.writestr("Tsukihime.app/Contents/MacOS/Tsukihime", b"host")
        zipped.writestr("Tsukihime.app/Contents/MacOS/Tsukihime.dll", b"assembly")
        zipped.writestr("__MACOSX/Tsukihime.app/Contents/MacOS/._Tsukihime.dll", b"AppleDouble")
    monkeypatch.setattr(package, "build", lambda *args, **kwargs: app)
    monkeypatch.setattr(package, "archive", lambda *args: zip_path)
    assert package.main(["publish", "--version", "0.1.6", "--arch", "arm64", "--zip"]) == 0
    assert "2 files, 1 __MACOSX entries" in capsys.readouterr().out


@pytest.mark.parametrize("library", package.LAUNCHER_NATIVE)
def test_a_publish_without_a_launcher_native_library_is_refused(tmp_path, library):
    folder = publish(tmp_path)
    (folder / library).unlink()
    with pytest.raises(ValueError, match=library):
        package.build(folder, "0.1.6", "arm64", tmp_path / "out", sign=False)


def test_avalonia_install_name_is_relocatable_in_both_universal_slices(tmp_path):
    old = b"/usr/local/lib/libAvalonia.Native.OSX.dylib"
    new = b"@rpath/libAvaloniaNative.dylib"

    def dylib(arch):
        command_size = (24 + len(old) + 7) & ~7
        command = struct.pack("<IIIIII", 0xD, command_size, 24, 0, 0x10000, 0x10000) + old + bytes(command_size - 24 - len(old))
        return struct.pack("<IIIIIIII", 0xFEEDFACF, package.CPUTYPES[arch], 3, 6, 1, len(command), 0, 0) + command

    arm, x64 = dylib("arm64"), dylib("x64")
    fat = bytearray(struct.pack(">II", 0xCAFEBABE, 2))
    fat += struct.pack(">IIIII", package.CPUTYPES["x64"], 3, 48, len(x64), 12)
    fat += struct.pack(">IIIII", package.CPUTYPES["arm64"], 12, 48 + len(x64), len(arm), 12)
    fat += x64 + arm
    path = tmp_path / "Tsukihime.app" / "Contents" / "MacOS" / "libAvaloniaNative.dylib"
    path.parent.mkdir(parents=True)
    path.write_bytes(fat)
    package.normalize_avalonia_install_name(tmp_path / "Tsukihime.app")
    rewritten = path.read_bytes()
    assert len(rewritten) == len(fat)
    assert new in rewritten and old not in rewritten
    assert rewritten[48 + len(x64):].count(new) == 1
    assert rewritten[48:48 + len(x64)].count(new) == 1


def csharp_string_constants(text: str) -> dict[str, str]:
    """The const string fields of C# source whose values are literals or + joins of literals and other such constants."""
    expressions = dict(re.findall(r"\bconst\s+string\s+(\w+)\s*=\s*([^;]+);", text))
    values: dict[str, str] = {}
    for _ in range(len(expressions)):
        for name, expression in expressions.items():
            resolved = []
            for part in (part.strip() for part in expression.split("+")):
                literal = re.fullmatch(r'"([^"\\]*)"', part)
                if literal:
                    resolved.append(literal.group(1))
                elif part.split(".")[-1] in values:
                    resolved.append(values[part.split(".")[-1]])
                else:
                    break
            else:
                values[name] = "".join(resolved)
    return values


def test_the_csharp_constant_reader_resolves_joined_constants():
    text = 'const string A = "io.x"; public const string B = A + ".y"; const string C = ReleaseRules.B + "." + D; const string D = "z";'
    assert csharp_string_constants(text) == {"A": "io.x", "B": "io.x.y", "D": "z", "C": "io.x.y.z"}


def test_the_release_rules_expect_the_identifier_codesign_gives_the_game_host():
    # D2: with --prefix the nested host is io.github.take00c.tsukihime.Tsukihime. release-check and release-sums demand the value of one
    # ReleaseRules constant, so that constant must be this one: any other value refuses every Mac ZIP the workflow makes
    assert package.SIGNING_PREFIX == package.BUNDLE_IDENTIFIER + "."
    assert package.HOST_IDENTIFIER == package.SIGNING_PREFIX + "Tsukihime"
    rules = sorted((REPO / "src" / "Tsuki.Cli" / "Commands").glob("ReleaseRules*.cs"))
    assert rules, "the release rules were not found"
    constants = csharp_string_constants("\n".join(path.read_text(encoding="utf-8") for path in rules))
    # a constant for the prefix itself is fine; every identifier under it must be the game host's
    hosts = sorted({value for value in constants.values() if value.startswith(package.SIGNING_PREFIX) and value != package.SIGNING_PREFIX})
    assert hosts == [package.HOST_IDENTIFIER], (
        "ReleaseRules must keep the game host's signing identifier in one constant equal to package_macos.HOST_IDENTIFIER "
        f"({package.HOST_IDENTIFIER}); found {hosts}")
