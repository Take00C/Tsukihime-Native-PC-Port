"""Git pre-commit check for the Tsuki port.

Refuses a commit that would put game data or other non-source material into
the repository, or that switches tests off. Set TSUKI_ALLOW_SKIP=1 for a
deliberate, reviewed exception to the skip rule.
"""
from __future__ import annotations

import os
import re
import subprocess
import sys

ALLOWED_PREFIXES = (
    "docs/", "tools/", "src/", "tests/", ".claude/",
    "assets/",  # the port's own original art only (tools/setup/make_icon.py); never game art
)
ALLOWED_ROOT_FILES = {
    ".gitignore", ".gitattributes", ".editorconfig", "README.md", "LICENSE", "CONTRIBUTING.md",
    "Directory.Build.props", "Directory.Packages.props", "global.json",
    "NuGet.config", "nuget.config",
}
ALLOWED_ROOT_SUFFIXES = (".sln", ".slnx")
BLOCKED_PREFIXES = ("samples/", "scratch/", "ref/", "third_party/", ".venv/")
GAME_DATA_EXT = {
    ".mrg", ".hed", ".nam", ".mzp", ".mzx", ".at9", ".vag", ".mp4", ".sfo",
    ".prx", ".sprx", ".self", ".fself", ".elf", ".pkg", ".trp", ".dds",
}
MAX_BYTES = 5 * 1024 * 1024
SKIP_PATTERNS = [
    (re.compile(r"@pytest\.mark\.skip\b(?!if)"), "unconditional @pytest.mark.skip"),
    (re.compile(r"\[(Fact|Theory)\s*\([^)]*Skip\s*="), "xUnit Skip = on [Fact]/[Theory]"),
    (re.compile(r"\[Ignore\b"), "[Ignore] attribute"),
]


def git(*args: str) -> str:
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True,
                          encoding="utf-8").stdout


def main() -> int:
    staged = [p for p in git("diff", "--cached", "--name-only", "--diff-filter=ACMR").splitlines() if p]
    problems: list[str] = []
    for path in staged:
        low = path.lower()
        if low.startswith(BLOCKED_PREFIXES):
            problems.append(f"{path}: this folder never goes into git (game data, scratch or third-party)")
            continue
        if os.path.splitext(low)[1] in GAME_DATA_EXT:
            problems.append(f"{path}: game-data file type")
            continue
        top_level = "/" not in path
        if not (path.startswith(ALLOWED_PREFIXES) or path in ALLOWED_ROOT_FILES
                or (top_level and path.endswith(ALLOWED_ROOT_SUFFIXES))):
            problems.append(f"{path}: not in the allow-list (docs/, tools/, src/, tests/, .claude/ or a known root file)")
            continue
        size = int(git("cat-file", "-s", f":{path}").strip() or 0)
        if size > MAX_BYTES:
            problems.append(f"{path}: {size:,} bytes is over the 5 MiB limit")
    if os.environ.get("TSUKI_ALLOW_SKIP") != "1":
        # Only code can disable a test, so only .py/.cs files are scanned (docs may describe
        # the rule). This checker's own source spells out the patterns, so it is excluded.
        added = [ln[1:] for ln in git("diff", "--cached", "-U0", "--", "*.py", "*.cs",
                                      ":(exclude)tools/setup/hooks/precommit_check.py").splitlines()
                 if ln.startswith("+") and not ln.startswith("+++")]
        for line in added:
            for rx, what in SKIP_PATTERNS:
                if rx.search(line):
                    problems.append(f"added {what}: {line.strip()[:120]}")
    if problems:
        print("pre-commit: commit refused by the Tsuki project rules:", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
