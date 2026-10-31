"""Claude Code PreToolUse guard: refuse hand-edits to protected paths.

Reads the hook payload (JSON) from stdin. For file-writing tools it denies
edits to generated or third-party data that must only change through its
generator: golden oracle exports, the archive inventory, the reference repos
and the user's game folder (TSUKI_GAME_DIR). Everything else is allowed.

The project root is the folder three levels above this file (tools/setup/hooks),
so the guard works wherever the repository is checked out.
"""
from __future__ import annotations

import json
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..")).replace("\\", "/").lower()
PROTECTED: list[tuple[str, str]] = [
    (f"{ROOT}/samples/golden/", "golden oracle data: regenerate it with tools/oracle, never hand-edit"),
    (f"{ROOT}/samples/inventory/", "generated archive inventory: regenerate it with tools/inventory.py"),
    (f"{ROOT}/ref/", "third-party reference source: read-only"),
]


def _norm(path: str) -> str:
    return os.path.abspath(path).replace("\\", "/").lower()


def _game_dir() -> str | None:
    """The game folder named by TSUKI_GAME_DIR, normalised with a trailing slash; None when the variable is unset."""
    g = os.environ.get("TSUKI_GAME_DIR")
    if not g:
        return None
    return _norm(g).rstrip("/") + "/"


def main() -> int:
    try:
        payload = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0  # not a payload we understand; never block on our own parse error
    tool_input = payload.get("tool_input") or {}
    path = tool_input.get("file_path") or tool_input.get("notebook_path")
    if not path:
        return 0
    target = _norm(path)
    rules = list(PROTECTED)
    game = _game_dir()
    if game:
        rules.append((game, "the user's game folder: read-only, never modified"))
    for prefix, why in rules:
        if target == prefix.rstrip("/") or target.startswith(prefix):
            json.dump({
                "hookSpecificOutput": {
                    "hookEventName": "PreToolUse",
                    "permissionDecision": "deny",
                    "permissionDecisionReason": f"Blocked by project guard: {path} is {why}.",
                }
            }, sys.stdout)
            return 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
