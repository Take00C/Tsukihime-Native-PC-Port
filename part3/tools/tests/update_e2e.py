"""Install a release archive, update it with its own launcher from a loopback stand-in of GitHub, and check the restarted launcher.

    python3 -I tools/tests/update_e2e.py ARCHIVE --work DIR [--summary FILE]

ARCHIVE is Tsukihime-vX.Y.Z-macos-ARCH.zip or Tsukihime-vX.Y.Z-linux-x64.tar.gz. It is installed into DIR (a Mac ZIP with ditto -x,
then codesign --verify --deep --strict and a load of its native libraries; a Linux archive with tar). The installed launcher is told
it is v0.0.1 (--update-current), so it offers ARCHIVE itself as the update: it downloads it and SHA256SUMS.txt from the loopback
server, checks and installs it exactly as a player's update does, and restarts. The new launcher's --selftest report must say
SELFTEST PASS, launcher vX.Y.Z and "Updated from v0.0.1". DIR must be new or empty. After a failure the report ends with the last
lines of the launcher's own logs in DIR/profile, which say why. The Mac workflows run this on the ZIP they made; on Linux (WSL
included) it checks a release tar.gz before it is uploaded. Standard library only; no game data is used.
"""
from __future__ import annotations

import argparse
import hashlib
import http.server
import json
import os
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

REPOSITORY = "Take00C/Tsukihime-Native-PC-Port"
SUMS = "SHA256SUMS.txt"
CURRENT = "0.0.1"
ARCHIVE = re.compile(r"Tsukihime-v(\d{1,5}\.\d{1,5}\.\d{1,5})-(macos-(arm64|x64)\.zip|linux-x64\.tar\.gz)")
MAC_NATIVE = ("libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib", "libSDL3.dylib",
              "libavutil.61.dylib", "libswresample.7.dylib", "libavcodec.63.dylib", "libavformat.63.dylib")
LOAD = "import ctypes, sys\nfor path in sys.argv[1:]:\n    ctypes.CDLL(path)\n"
LAUNCHER_LOGS = ("launcher.log", "launcher_crash.log")
LOG_TAIL = 40


def parse_archive_name(name: str) -> tuple[str, bool]:
    """The version and whether it is a Mac ZIP, from a release asset name."""
    match = ARCHIVE.fullmatch(name)
    if not match:
        raise ValueError(f"{name} is not a Tsukihime-vX.Y.Z-macos-ARCH.zip or -linux-x64.tar.gz release asset")
    return match.group(1), match.group(2).startswith("macos-")


def sums_text(archive: Path) -> str:
    return hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name + "\n"


class FakeGitHub:
    """The releases list, the github.com download redirect and the asset bytes, on 127.0.0.1 (a free port)."""

    def __init__(self, tag: str, files: dict[str, Path]):
        self.tag, self.files, self.requests = tag, dict(files), []
        server = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                server.requests.append(self.path)
                download = f"/{REPOSITORY}/releases/download/{server.tag}/"
                stored = f"/assets/{server.tag}/"
                if self.path == f"/repos/{REPOSITORY}/releases?per_page=20":
                    return self.reply(200, server.listing(), "application/json")
                if self.path.startswith(download) and self.path[len(download):] in server.files:
                    self.send_response(302)
                    self.send_header("Location", server.base + stored[1:] + self.path[len(download):])
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return None
                if self.path.startswith(stored) and self.path[len(stored):] in server.files:
                    return self.reply(200, server.files[self.path[len(stored):]].read_bytes(), "application/octet-stream")
                return self.reply(404, b"not found", "text/plain")

            def reply(self, code: int, body: bytes, kind: str):
                self.send_response(code)
                self.send_header("Content-Type", kind)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self._http = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.base = f"http://127.0.0.1:{self._http.server_address[1]}/"
        self._thread = threading.Thread(target=self._http.serve_forever, daemon=True)

    def listing(self) -> bytes:
        assets = [{"name": name, "browser_download_url": f"{self.base}{REPOSITORY}/releases/download/{self.tag}/{name}",
                   "size": path.stat().st_size, "state": "uploaded"} for name, path in self.files.items()]
        return json.dumps([{"tag_name": self.tag, "name": f"Tsukihime PC port {self.tag}", "body": "## Changed\n\n- update test",
                            "draft": False, "prerelease": False, "html_url": f"https://github.com/{REPOSITORY}/releases/tag/{self.tag}",
                            "assets": assets}]).encode("utf-8")

    def __enter__(self) -> FakeGitHub:
        self._thread.start()
        return self

    def __exit__(self, *exc) -> None:
        self._http.shutdown()
        self._http.server_close()


def report_problems(before: str, after: str, version: str) -> list[str]:
    """What the two --update-selftest reports lack: the old launcher's PASS, and the new one's selftest of this version after the update."""
    problems = []
    if "UPDATE SELFTEST PASS" not in before:
        problems.append("the old launcher did not report UPDATE SELFTEST PASS")
    for needle in ("SELFTEST PASS:", f"launcher v{version}", f"Updated from v{CURRENT}"):
        if needle not in after:
            problems.append(f"the restarted launcher's report lacks {needle!r}")
    return problems


def log_tails(profile: Path) -> list[str]:
    """The last LOG_TAIL lines of each launcher log in the test profile: after a failure they say why, and no job keeps the profile."""
    lines = []
    for name in LAUNCHER_LOGS:
        path = profile / name
        if path.is_file():
            text = path.read_text(encoding="utf-8", errors="replace").splitlines()
            lines.append(f"profile/{name}" + (f", its last {LOG_TAIL} lines:" if len(text) > LOG_TAIL else ":"))
            lines.extend("  " + line for line in text[-LOG_TAIL:])
    return lines


def run(command: list[str], **kwargs) -> subprocess.CompletedProcess:
    return subprocess.run(command, capture_output=True, text=True, errors="replace", **kwargs)


def check(archive: Path, work: Path, lines: list[str]) -> bool:
    version, mac = parse_archive_name(archive.name)
    if work.exists() and any(work.iterdir()):
        raise ValueError(f"{work} is not empty")
    try:
        passed = install_and_update(archive, work, version, mac, lines)
    except (OSError, ValueError, subprocess.SubprocessError) as e:
        lines.append(f"PROBLEM: {e}")
        passed = False
    if not passed:
        lines.extend(log_tails(work / "profile"))
    return passed


def install_and_update(archive: Path, work: Path, version: str, mac: bool, lines: list[str]) -> bool:
    install, assets, profile = work / "install", work / "assets", work / "profile"
    for folder in (install, assets, profile):
        folder.mkdir(parents=True, exist_ok=True)
    if mac:
        run(["ditto", "-x", "-k", str(archive), str(install)], check=True)
        app = install / "Tsukihime.app"
        programs = app / "Contents" / "MacOS"
        staging = install / ".Tsukihime.app.update"
        signed = run(["codesign", "--verify", "--deep", "--strict", str(app)])
        lines.append(f"codesign --verify --deep --strict of the extracted app: exit {signed.returncode} {signed.stderr.strip()}".rstrip())
        loaded = run([sys.executable, "-I", "-c", LOAD, *(str(programs / name) for name in MAC_NATIVE)])
        lines.append(f"ctypes load of {len(MAC_NATIVE)} native libraries: exit {loaded.returncode} {loaded.stderr.strip()[-400:]}".rstrip())
        if signed.returncode or loaded.returncode:
            return False
    else:
        run(["tar", "-xzf", str(archive), "-C", str(install)], check=True)
        programs = install / f"Tsukihime-v{version}-linux-x64"
        staging = programs / "update-staging"
    asset = assets / archive.name
    asset.write_bytes(archive.read_bytes())
    (assets / SUMS).write_text(sums_text(asset), encoding="utf-8")
    report = work / "report.txt"
    environment = {key: value for key, value in os.environ.items() if key != "TSUKI_GAME_DIR"}
    with FakeGitHub(f"v{version}", {asset.name: asset, SUMS: assets / SUMS}) as server:
        started = run([str(programs / "TsukihimeLauncher"), "--update-selftest", str(report), "--data", str(profile),
                       "--game", str(work / "no-game"), "--update-api", server.base, "--update-current", CURRENT],
                      cwd=programs, env=environment, timeout=600)
        after_report = Path(str(report) + ".after.txt")
        deadline = time.monotonic() + 90
        while started.returncode == 0 and not after_report.exists() and time.monotonic() < deadline:
            time.sleep(0.2)
        time.sleep(1)
        requests = list(server.requests)
    before = report.read_text(encoding="utf-8", errors="replace").strip() if report.exists() else (started.stdout + started.stderr).strip()[-2000:]
    after = after_report.read_text(encoding="utf-8", errors="replace").strip() if after_report.exists() else "(no report from the restarted launcher)"
    lines.append(f"update from v{CURRENT}: exit {started.returncode}; {before}")
    lines.append(f"restarted launcher: {after}")
    lines.append("downloads: " + ", ".join(path.rsplit("/", 1)[-1] for path in requests if path.startswith("/assets/")))
    problems = report_problems(before, after, version)
    if started.returncode:
        problems.append(f"the old launcher exited with {started.returncode}")
    if staging.exists():
        problems.append(f"the staging folder {staging.name} is still there")
    leftovers = sorted(path.name for path in programs.rglob("*.old"))
    if leftovers:
        problems.append("files of the old version are still there: " + ", ".join(leftovers[:5]))
    if mac:
        resigned = run(["codesign", "--verify", "--deep", "--strict", str(app)])
        if resigned.returncode:
            problems.append("the installed app fails codesign --verify --deep --strict: " + resigned.stderr.strip())
    lines.extend("PROBLEM: " + problem for problem in problems)
    return not problems


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("archive", help="Tsukihime-vX.Y.Z-macos-ARCH.zip or Tsukihime-vX.Y.Z-linux-x64.tar.gz")
    ap.add_argument("--work", required=True, help="a new or empty folder for the install, the profile and the reports")
    ap.add_argument("--summary", help="a Markdown file to append the result to (GITHUB_STEP_SUMMARY)")
    a = ap.parse_args(argv)
    archive = Path(a.archive).resolve()
    lines = [f"Real updater on {archive.name}"]
    try:
        passed = check(archive, Path(a.work).resolve(), lines)
    except (OSError, ValueError, subprocess.SubprocessError) as e:
        lines.append(f"PROBLEM: {e}")
        passed = False
    lines.append("UPDATE E2E PASS" if passed else "UPDATE E2E FAIL")
    print("\n".join(lines))
    if a.summary:
        with open(a.summary, "a", encoding="utf-8") as summary:
            summary.write("```text\n" + "\n".join(lines) + "\n```\n")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
