"""tsuki - reference readers for the PS4 TSUKIHIME -A piece of blue glass moon- data.

Container layer (owned by the container agent):
    tsuki.nam   name tables (.nam, NAM32 / NAM8)
    tsuki.mrg   HED/NAM/MRG triplet archives (HED8 / HED4)
    tsuki.mzp   "mrgd00" (MZP) containers, nested or standalone
    tsuki.mzx   MZX0 decompressor (numba-accelerated when available) and the
                "LV" voice-entry wrapper (parse_lv / decompress_lv)
    tsuki.sniff inner-format labelling used by tools/inventory.py

Game location: the environment variable TSUKI_GAME_DIR (the CUSA47999 folder
that holds eboot.bin, image/, data/ and sce_sys/).  There is no built-in
default: without the variable game_dir() returns None and the readers that
need the game stop with GAME_DIR_MISSING.  The game folder is only ever
opened read-only.
"""
from __future__ import annotations

import os
from pathlib import Path
from typing import Optional

__all__ = ["GAME_DIR_ENV", "GAME_DIR_MISSING", "game_dir", "image_dir", "PROJECT_ROOT"]

#: The environment variable that names the game folder.
GAME_DIR_ENV = "TSUKI_GAME_DIR"
#: The message every tool prints when the game folder cannot be found.
GAME_DIR_MISSING = (
    "game folder not found: set the environment variable TSUKI_GAME_DIR to your "
    "extracted CUSA47999 folder (the one holding eboot.bin, image\\, data\\ and sce_sys\\)"
)
PROJECT_ROOT = Path(__file__).resolve().parents[2]


def game_dir() -> Optional[Path]:
    """The CUSA47999 folder named by TSUKI_GAME_DIR, or None if it is unset or not a game folder."""
    env = os.environ.get(GAME_DIR_ENV)
    if not env:
        return None
    p = Path(env)
    return p if (p / "image").is_dir() else None


def image_dir() -> Optional[Path]:
    g = game_dir()
    return g / "image" if g else None
