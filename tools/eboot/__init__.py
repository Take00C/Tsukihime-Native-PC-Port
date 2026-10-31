"""eboot - static analysis helpers for the TSUKIHIME PS4 eboot.bin (CUSA47999 v01.01).

These tools treat the game executable as a black box to be described, not run:

    eboot.selfelf   SELF container + embedded ELF64: segments, VA<->file mapping,
                    SCE dynamic table, imported modules/libraries/NIDs, PLT/GOT
    eboot.nids      PS4 NID computation (SHA-1 based) and the local name database
    eboot.imports   CLI: dump the import surface (JSON / Markdown) with feature tags
    eboot.disasm    capstone helpers: linear sweep, call/xref index, string refs
    eboot.strings   C-string extraction with VAs and categories
    eboot.shaders   locate embedded GNM shader binaries (OrbShdr / "Shdr" blobs),
                    map them to shader classes, GCN instruction census, reflection names
    eboot.savedata  reader for the 4 MiB savedata000 blob (layout recovered from code)
    eboot.scriptvm  script-VM command tables of both interpreters (name -> handler VA),
                    by vectorised concrete evaluation of the dispatch code
    eboot.uitext    export of the ui_text[5][1499] string table (RELRO 0x41D5F0)
    eboot.symbols   curated function/data names used by docs/formats/engine.md

Everything opens the game folder read-only.  Default eboot location comes from
tsuki.game_dir() (env TSUKI_GAME_DIR), so `PYTHONPATH=D:/TsukihimePort/tools`.
"""
from __future__ import annotations

from pathlib import Path
from typing import Optional

__all__ = ["eboot_path"]


def eboot_path() -> Optional[Path]:
    """Path of eboot.bin in the game folder, or None if the game is missing."""
    from tsuki import game_dir

    g = game_dir()
    if g is None:
        return None
    p = g / "eboot.bin"
    return p if p.is_file() else None
