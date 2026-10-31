"""Dump the eboot's platform API surface: every imported function with its resolved
name, PLT/GOT addresses, call sites, and the runtime feature it maps to.

    python -m eboot.imports [--json out.json] [--md out.md]

Names come from eboot/nid_db.tsv (verified by NID hash).  Feature tags are ours,
assigned per library (and refined per function where a library serves several
features).  `callers` lists the engine functions (eh_frame starts) that call the
PLT stub directly; functions reached only through the GOT slot (function
pointers, e.g. libc malloc stored into allocator tables) are listed under
`data_refs`.
"""
from __future__ import annotations

import json
import sys
from collections import defaultdict
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Dict, List, Optional

from eboot.disasm import Index
from eboot.nids import load_db
from eboot.selfelf import Eboot, Import

LIB_FEATURE: Dict[str, str] = {
    "libSceVideoOut": "video output: 1920x1080 scan-out buffers, flip rate, vblank wait",
    "libSceGnmDriver": "GPU: command buffer submit+flip, shader stage setup, draw calls",
    "libSceAvPlayer": "movie playback (.mp4: H.264 video + AAC audio)",
    "libSceAudiodec": "audio decode (ATRAC9 for bgm/vse/voice RIFF data)",
    "libSceAudiodecCpu": "audio decode on CPU (HEVAG for vag.mrg 'VAGp' entries)",
    "libSceAudioOut": "audio output ports (48 kHz float stereo + 8 ch)",
    "libScePad": "DualShock 4 input, vibration, light bar",
    "libSceMouse": "mouse input",
    "libSceSaveData": "save data mount/umount, backup, icon, param.sfo fields",
    "libSceSaveDataDialog": "system save-data dialogs (create/overwrite/corrupt/no space)",
    "libSceMsgDialog": "system message dialogs (yes/no, errors)",
    "libSceCommonDialog": "common dialog init (needed by Msg/SaveData dialogs)",
    "libSceNpTrophy": "trophies (unlock, query)",
    "libSceNpManager": "PSN title id registration",
    "libSceSystemService": "system params (LANG = 1, ENTER_BUTTON_ASSIGN = 1000, string 6), splash, BGM player",
    "libSceUserService": "user login / initial user / user color",
    "libScePlayGo": "PlayGo install-chunk locus (download-in-progress checks)",
    "libSceScreenShot": "screenshot enable/disable (spoiler protection)",
    "libSceVideoRecording": "video recording metadata / prohibition",
    "libSceGameLiveStreaming": "live streaming enable/disable",
    "libSceSharePlay": "Share Play prohibition",
    "libSceFios2": "file I/O (open, pread, async ops) for archives",
    "libSceRtc": "local wall-clock time (save timestamps)",
    "libSceSysmodule": "load system modules (audiodec, avplayer, dialogs, ...)",
    "libkernel": "threads, mutexes, semaphores, event flags/queues, memory, files, time",
    "libc": "C/C++ runtime (malloc, string, math, printf, new/delete)",
}

# finer tags for a few multi-purpose libraries
FUNC_FEATURE: Dict[str, str] = {
    "sceKernelGetProcessTime": "timing: microsecond clock",
    "sceKernelGetProcessTimeCounter": "timing: high-resolution counter",
    "sceKernelGetProcessTimeCounterFrequency": "timing: counter frequency",
    "sceKernelUsleep": "timing: sleep",
    "sceVideoOutSetFlipRate": "timing: flip rate (0 = 60 Hz)",
    "sceVideoOutWaitVblank": "timing: vblank wait",
    "sceVideoOutGetVblankStatus": "timing: vblank counter",
    "sceKernelOpen": "file I/O (direct)",
    "sceKernelRead": "file I/O (direct)",
    "sceKernelWrite": "file I/O (direct, save files)",
    "sceKernelStat": "file I/O (direct)",
    "sceKernelClose": "file I/O (direct)",
    "sceKernelIsNeoMode": "PS4 Pro detection",
    "sceSystemServiceParamGetInt": "system language / enter-button assignment",
}


@dataclass
class ImportRow:
    library: str
    module: str
    nid: str
    name: str
    kind: str                     # func / object
    plt_va: Optional[int]
    got_vas: List[int] = field(default_factory=list)
    call_sites: int = 0
    callers: List[int] = field(default_factory=list)
    data_refs: int = 0
    feature: str = ""


def collect(eb: Eboot, ix: Index) -> List[ImportRow]:
    db = load_db()
    imps: List[Import] = eb.imports()
    eb.find_plt(imps)
    rows: Dict[str, ImportRow] = {}
    for i in imps:
        key = i.library + ":" + i.nid
        r = rows.get(key)
        name = db.get(i.nid, "?" + i.nid)
        if r is None:
            r = ImportRow(i.library, i.module, i.nid, name,
                          "func" if i.sym_type == 2 else "object", i.plt_va)
            r.feature = FUNC_FEATURE.get(name, LIB_FEATURE.get(i.library, ""))
            rows[key] = r
        if i.plt_va is not None and r.plt_va is None:
            r.plt_va = i.plt_va
        if i.got_va is not None:
            r.got_vas.append(i.got_va)
    for r in rows.values():
        if r.plt_va is not None:
            cs = ix.callers(r.plt_va)
            r.call_sites = len(cs)
            r.callers = sorted({f for f in (ix.func_of(c) for c in cs) if f is not None})
        r.data_refs = sum(len(ix.code_refs(g)) for g in r.got_vas)
    return sorted(rows.values(), key=lambda r: (r.library, r.name))


def to_markdown(rows: List[ImportRow]) -> str:
    by = defaultdict(list)
    for r in rows:
        by[r.library].append(r)
    out = ["| library | functions | feature |", "|---|---:|---|"]
    for lib, rs in sorted(by.items()):
        out.append(f"| {lib} | {len(rs)} | {LIB_FEATURE.get(lib, '')} |")
    out.append("")
    for lib, rs in sorted(by.items()):
        out.append(f"### {lib} ({len(rs)})")
        out.append("")
        out.append("| function | NID | PLT VA | call sites | caller functions |")
        out.append("|---|---|---|---:|---|")
        for r in rs:
            plt = f"0x{r.plt_va:x}" if r.plt_va is not None else "-"
            cf = ", ".join(f"0x{c:x}" for c in r.callers[:6]) + (" ..." if len(r.callers) > 6 else "")
            out.append(f"| `{r.name}` | `{r.nid}` | {plt} | {r.call_sites} | {cf} |")
        out.append("")
    return "\n".join(out)


def _main(argv: List[str]) -> int:
    import argparse

    from eboot import eboot_path

    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--json")
    ap.add_argument("--md")
    a = ap.parse_args(argv)
    p = eboot_path()
    if p is None:
        print("game not found", file=sys.stderr)
        return 2
    eb = Eboot.open(p)
    ix = Index.build(eb)
    rows = collect(eb, ix)
    unresolved = [r for r in rows if r.name.startswith("?")]
    print(f"{len(rows)} imported symbols, {len(rows) - len(unresolved)} resolved, "
          f"{len({r.library for r in rows})} libraries")
    for r in unresolved:
        print(f"  unresolved: {r.library} {r.nid} plt={r.plt_va}")
    if a.json:
        Path(a.json).write_text(json.dumps([asdict(r) for r in rows], indent=1), encoding="utf-8")
    if a.md:
        Path(a.md).write_text(to_markdown(rows), encoding="utf-8")
    if not a.json and not a.md:
        print(to_markdown(rows))
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
