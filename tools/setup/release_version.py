"""Print the release version: the <Version> that the player and both launchers' projects agree on.

    python3 -I tools/setup/release_version.py [--ref-type branch|tag --ref-name NAME]

Exit 1 with a reason when the three projects disagree, or when a tag ref (GITHUB_REF_TYPE/GITHUB_REF_NAME) is not vX.Y.Z of
that version. The Mac download workflow runs this first, so a press on a commit without the version bump stops at once
instead of after the FFmpeg build. Standard library only.
"""
from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
PROJECTS = ("src/Tsuki.App/Tsuki.App.csproj", "src/Tsuki.Launcher/Tsuki.Launcher.csproj",
            "src/Tsuki.Launcher.Avalonia/Tsuki.Launcher.Avalonia.csproj")


def project_versions(repo: Path = REPO) -> dict[str, str]:
    versions = {}
    for name in PROJECTS:
        found = [element.text or "" for element in ElementTree.parse(repo / name).getroot().iter("Version")]
        if len(found) != 1:
            raise ValueError(f"{name} has {len(found)} <Version> elements, not one")
        versions[name] = found[0].strip()
    return versions


def release_version(repo: Path = REPO, ref_type: str = "", ref_name: str = "") -> str:
    versions = project_versions(repo)
    distinct = sorted(set(versions.values()))
    if len(distinct) != 1:
        raise ValueError("the projects disagree on <Version>: " + ", ".join(f"{name} {value}" for name, value in versions.items())
                         + "; set the same version in all three")
    version = distinct[0]
    if not re.fullmatch(r"\d{1,5}\.\d{1,5}\.\d{1,5}", version):
        raise ValueError(f"the version must be X.Y.Z, not {version!r}")
    if ref_type == "tag" and ref_name != "v" + version:
        raise ValueError(f"the tag {ref_name} is not v{version}, the version of the projects")
    return version


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--ref-type", default="", help="branch or tag (GITHUB_REF_TYPE)")
    ap.add_argument("--ref-name", default="", help="the branch or tag name (GITHUB_REF_NAME)")
    a = ap.parse_args(argv)
    try:
        print(release_version(REPO, a.ref_type, a.ref_name))
    except (OSError, ValueError, ElementTree.ParseError) as e:
        print(f"release_version: {e}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
