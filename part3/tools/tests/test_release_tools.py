"""The release helpers the workflows run: the version check and the real-updater end-to-end driver. No game data is needed."""
from __future__ import annotations

import http.client
import importlib.util
import json
import re
import subprocess
import time
import types
from pathlib import Path

import pytest

TOOLS = Path(__file__).resolve().parents[1]
REPO = TOOLS.parent


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


version_tool = load("release_version", TOOLS / "setup" / "release_version.py")
e2e = load("update_e2e", TOOLS / "tests" / "update_e2e.py")


def projects(root: Path, *versions: str) -> Path:
    for name, version in zip(version_tool.PROJECTS, versions):
        (root / name).parent.mkdir(parents=True, exist_ok=True)
        (root / name).write_text(f'<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <Version>{version}</Version>\n  </PropertyGroup>\n</Project>\n')
    return root


def test_the_three_projects_of_this_tree_agree_on_one_version():
    assert re.fullmatch(r"\d+\.\d+\.\d+", version_tool.release_version())
    assert len(version_tool.PROJECTS) == 3 and any("Avalonia" in name for name in version_tool.PROJECTS)


def test_the_version_check_refuses_disagreeing_projects_and_a_wrong_tag(tmp_path):
    root = projects(tmp_path, "0.1.7", "0.1.7", "0.1.7")
    assert version_tool.release_version(root) == "0.1.7"
    assert version_tool.release_version(root, "branch", "main") == "0.1.7"
    assert version_tool.release_version(root, "tag", "v0.1.7") == "0.1.7"
    with pytest.raises(ValueError, match="the tag v0.1.6 is not v0.1.7"):
        version_tool.release_version(root, "tag", "v0.1.6")
    with pytest.raises(ValueError, match="the tag 0.1.7 is not v0.1.7"):
        version_tool.release_version(root, "tag", "0.1.7")
    # the miss the review found: Tsuki.App and the WPF launcher bumped, the Avalonia launcher not
    projects(tmp_path, "0.1.7", "0.1.7", "0.1.6")
    with pytest.raises(ValueError, match="disagree.*Tsuki.Launcher.Avalonia.csproj 0.1.6"):
        version_tool.release_version(root)


def test_the_version_check_prints_only_the_version_and_fails_loudly(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(version_tool, "REPO", projects(tmp_path, "1.2.3", "1.2.3", "1.2.3"))
    assert version_tool.main(["--ref-type", "tag", "--ref-name", "v1.2.3"]) == 0
    assert capsys.readouterr().out == "1.2.3\n"
    assert version_tool.main(["--ref-type", "tag", "--ref-name", "v1.2.4"]) == 1
    captured = capsys.readouterr()
    assert captured.out == "" and "v1.2.4 is not v1.2.3" in captured.err
    projects(tmp_path, "1.2.3", "1.2.3", "1.2")
    assert version_tool.main([]) == 1


def test_release_asset_names_are_read_for_both_unix_platforms():
    assert e2e.parse_archive_name("Tsukihime-v0.1.7-macos-arm64.zip") == ("0.1.7", True)
    assert e2e.parse_archive_name("Tsukihime-v0.1.7-macos-x64.zip") == ("0.1.7", True)
    assert e2e.parse_archive_name("Tsukihime-v0.1.7-linux-x64.tar.gz") == ("0.1.7", False)
    for name in ("Tsukihime-v0.1.7-win-x64.zip", "Tsukihime-0.1.7-macos-arm64.zip", "Tsukihime-v0.1.7-macos-universal.zip"):
        with pytest.raises(ValueError, match="not a Tsukihime"):
            e2e.parse_archive_name(name)


def test_the_checksum_file_has_the_archive_line_the_launcher_reads(tmp_path):
    archive = tmp_path / "Tsukihime-v0.1.7-macos-arm64.zip"
    archive.write_bytes(b"zip bytes")
    line = e2e.sums_text(archive)
    assert re.fullmatch(r"[0-9a-f]{64}  Tsukihime-v0\.1\.7-macos-arm64\.zip\n", line)


def get(base: str, path: str) -> http.client.HTTPResponse:
    host, port = base.split("//")[1].strip("/").split(":")
    connection = http.client.HTTPConnection(host, int(port), timeout=10)
    connection.request("GET", path)
    return connection.getresponse()


def test_the_loopback_github_serves_the_list_the_redirect_and_the_bytes(tmp_path):
    archive = tmp_path / "Tsukihime-v0.1.7-macos-x64.zip"
    archive.write_bytes(b"the release")
    sums = tmp_path / "SHA256SUMS.txt"
    sums.write_text(e2e.sums_text(archive))
    with e2e.FakeGitHub("v0.1.7", {archive.name: archive, sums.name: sums}) as server:
        assert server.base.startswith("http://127.0.0.1:") and server.base.endswith("/")
        listing = get(server.base, "/repos/Take00C/Tsukihime-Native-PC-Port/releases?per_page=20")
        assert listing.status == 200
        (release,) = json.loads(listing.read())
        assert release["tag_name"] == "v0.1.7" and release["draft"] is False and release["prerelease"] is False
        assets = {asset["name"]: asset for asset in release["assets"]}
        assert set(assets) == {archive.name, sums.name}
        assert assets[archive.name]["size"] == len(b"the release") and assets[archive.name]["state"] == "uploaded"
        url = assets[archive.name]["browser_download_url"]
        assert url == server.base + "Take00C/Tsukihime-Native-PC-Port/releases/download/v0.1.7/" + archive.name
        redirect = get(server.base, "/" + url[len(server.base):])
        assert redirect.status == 302 and redirect.getheader("Location") == server.base + "assets/v0.1.7/" + archive.name
        body = get(server.base, "/assets/v0.1.7/" + archive.name)
        assert body.status == 200 and body.read() == b"the release"
        assert get(server.base, "/assets/v0.1.7/other.zip").status == 404
        assert get(server.base, "/repos/Take00C/Tsukihime-Native-PC-Port/releases").status == 404
        assert server.requests[0] == "/repos/Take00C/Tsukihime-Native-PC-Port/releases?per_page=20"


def test_the_reports_must_show_the_update_and_the_restarted_launcher_of_this_version():
    before = 'UPDATE SELFTEST PASS: Update to v0.1.7; v0.1.7 is available (checked at 05:38). Press "Update to v0.1.7" at the bottom of the window.;'
    after = "SELFTEST PASS: 7 pages, Check for updates, 2 things need attention, launcher v0.1.7; Updated from v0.0.1 to v0.1.7."
    assert e2e.report_problems(before, after, "0.1.7") == []
    assert e2e.report_problems("UPDATE SELFTEST FAIL: ...", after, "0.1.7") == ["the old launcher did not report UPDATE SELFTEST PASS"]
    # a launcher that restarted without the update, or into another version, or not at all
    assert e2e.report_problems(before, after.replace("; Updated from v0.0.1 to v0.1.7.", ""), "0.1.7") == [
        "the restarted launcher's report lacks 'Updated from v0.0.1'"]
    assert e2e.report_problems(before, after, "0.1.8") == ["the restarted launcher's report lacks 'launcher v0.1.8'"]
    assert len(e2e.report_problems(before, "(no report from the restarted launcher)", "0.1.7")) == 3


def test_the_driver_refuses_a_used_work_folder_and_a_wrong_archive(tmp_path, capsys):
    archive = tmp_path / "Tsukihime-v0.1.7-linux-x64.tar.gz"
    archive.write_bytes(b"not used")
    work = tmp_path / "work"
    (work / "profile").mkdir(parents=True)
    (work / "keep.txt").write_text("someone's file")
    (work / "profile" / "launcher.log").write_text("an earlier run's line\n")
    summary = tmp_path / "summary.md"
    assert e2e.main([str(archive), "--work", str(work), "--summary", str(summary)]) == 1
    out = capsys.readouterr().out
    # nothing ran there, so the logs of whatever used the folder before are not this run's
    assert "is not empty" in out and "an earlier run's line" not in out and (work / "keep.txt").exists()
    other = tmp_path / "Tsukihime-v0.1.7-win-x64.zip"
    other.write_bytes(b"not used")
    assert e2e.main([str(other), "--work", str(tmp_path / "new")]) == 1
    text = summary.read_text()
    assert text.startswith("```text\nReal updater on Tsukihime-v0.1.7-linux-x64.tar.gz\n") and text.endswith("UPDATE E2E FAIL\n```\n")


def fake_linux_install(monkeypatch, launcher):
    """update_e2e with tar and the installed launcher faked (LAUNCHER takes the launcher's command line), and no waiting."""
    def run(command, **kwargs):
        if command[0] == "tar":
            (Path(command[command.index("-C") + 1]) / "Tsukihime-v0.1.7-linux-x64").mkdir()
            return subprocess.CompletedProcess(command, 0, "", "")
        assert Path(command[0]).name == "TsukihimeLauncher" and kwargs.get("cwd") == Path(command[0]).parent
        return launcher(command, Path(command[command.index("--data") + 1]), Path(command[command.index("--update-selftest") + 1]))

    monkeypatch.setattr(e2e, "run", run)
    monkeypatch.setattr(e2e, "time", types.SimpleNamespace(monotonic=time.monotonic, sleep=lambda seconds: None))


def write_logs(profile: Path) -> None:
    (profile / "launcher.log").write_text("".join(f"update: event {i:03}\n" for i in range(1, 101)), encoding="utf-8")
    (profile / "launcher_crash.log").write_text("System.IO.IOException: the swap could not rename TsukihimeLauncher\n", encoding="utf-8")


def refused(command, profile, report):
    write_logs(profile)
    report.write_text("UPDATE SELFTEST FAIL: the update was refused; launcher.log says why", encoding="utf-8")
    return subprocess.CompletedProcess(command, 1, "", "")


def timed_out(command, profile, report):
    write_logs(profile)
    raise subprocess.TimeoutExpired(command, 600)


@pytest.mark.parametrize("launcher", [refused, timed_out], ids=["refused", "timed-out"])
def test_a_failed_update_shows_the_end_of_the_launcher_logs(tmp_path, monkeypatch, capsys, launcher):
    # mac-1: the reason is only in the test profile's launcher.log and launcher_crash.log, which no job keeps (macOS 26 uploads nothing)
    fake_linux_install(monkeypatch, launcher)
    archive = tmp_path / "Tsukihime-v0.1.7-linux-x64.tar.gz"
    archive.write_bytes(b"the release")
    summary = tmp_path / "summary.md"
    assert e2e.main([str(archive), "--work", str(tmp_path / "work"), "--summary", str(summary)]) == 1
    out = capsys.readouterr().out.splitlines()
    log = out.index("profile/launcher.log, its last 40 lines:")
    assert out[log + 1:log + 41] == [f"  update: event {i:03}" for i in range(61, 101)]
    assert out[log + 41:log + 43] == ["profile/launcher_crash.log:", "  System.IO.IOException: the swap could not rename TsukihimeLauncher"]
    assert out[-1] == "UPDATE E2E FAIL" and log + 43 == len(out) - 1
    assert "  update: event 060" not in out and any(line.startswith("PROBLEM: ") for line in out[:log])
    assert summary.read_text(encoding="utf-8") == "```text\n" + "\n".join(out) + "\n```\n"


def test_a_passed_update_shows_no_logs(tmp_path, monkeypatch, capsys):
    def updated(command, profile, report):
        write_logs(profile)
        report.write_text('UPDATE SELFTEST PASS: Update to v0.1.7; v0.1.7 is available.', encoding="utf-8")
        Path(str(report) + ".after.txt").write_text("SELFTEST PASS: launcher v0.1.7; Updated from v0.0.1 to v0.1.7.", encoding="utf-8")
        return subprocess.CompletedProcess(command, 0, "", "")

    fake_linux_install(monkeypatch, updated)
    archive = tmp_path / "Tsukihime-v0.1.7-linux-x64.tar.gz"
    archive.write_bytes(b"the release")
    assert e2e.main([str(archive), "--work", str(tmp_path / "work")]) == 0
    out = capsys.readouterr().out
    assert out.endswith("UPDATE E2E PASS\n") and "profile/" not in out and "event" not in out


def test_the_mac_native_libraries_are_the_ones_the_packager_requires():
    package = load("package_macos", TOOLS / "setup" / "package_macos.py")
    assert set(e2e.MAC_NATIVE) == {"libSDL3.dylib", *package.LAUNCHER_NATIVE, *package.FFMPEG}
