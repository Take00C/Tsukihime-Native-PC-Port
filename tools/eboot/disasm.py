"""capstone-based static analysis of the eboot text segment.

* Function starts come from the .eh_frame_hdr binary-search table (PT_GNU_EH_FRAME),
  which lists one FDE per compiled function (6,9xx functions in CUSA47999).
* `Index` disassembles every function once (capstone "lite" mode, ~5 s) and records
  direct calls/jumps and RIP-relative references (lea/mov/cmp ... [rip+disp]).
* Pointers stored in data come from the R_X86_64_RELATIVE relocations
  (r_offset = slot VA, addend = target VA), which is how vtables, jump tables and
  command tables are found without running the loader.

CLI:
    python -m eboot.disasm dis <va> [<end>|+len]       disassemble a range
    python -m eboot.disasm func <va>                   disassemble the function containing va
    python -m eboot.disasm xref <va>                   code refs + data pointers to va
    python -m eboot.disasm strref <regex>              functions referencing matching strings
    python -m eboot.disasm funcmap <out.tsv>            one line per function: size, callers,
                                                        imports called, strings used, our name
"""
from __future__ import annotations

import bisect
import re
import struct
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from typing import Dict, Iterator, List, Optional, Tuple

import capstone

from eboot.selfelf import R_X86_64_RELATIVE, Eboot

_RIP = re.compile(r"\[rip ([+-]) 0x([0-9a-f]+)\]")


def _dec_ptr(buf: bytes, pos: int, enc: int, base_va: int) -> Tuple[int, int]:
    """Decode a DW_EH_PE-encoded pointer at buf[pos]; returns (value, new_pos)."""
    fmt = enc & 0x0F
    app = enc & 0x70
    if fmt == 0x0B:      # sdata4
        v = struct.unpack_from("<i", buf, pos)[0]
        n = 4
    elif fmt == 0x03:    # udata4
        v = struct.unpack_from("<I", buf, pos)[0]
        n = 4
    elif fmt == 0x0C:    # sdata8
        v = struct.unpack_from("<q", buf, pos)[0]
        n = 8
    elif fmt == 0x04:    # udata8
        v = struct.unpack_from("<Q", buf, pos)[0]
        n = 8
    else:
        raise ValueError(f"unsupported pointer encoding 0x{enc:x}")
    if app == 0x10:      # pcrel
        v += base_va + pos
    elif app == 0x30:    # datarel (relative to eh_frame_hdr start)
        v += base_va
    elif app != 0:
        raise ValueError(f"unsupported pointer application 0x{enc:x}")
    return v, pos + n


def eh_function_starts(eb: Eboot) -> List[int]:
    """Function start VAs from .eh_frame_hdr (sorted, unique)."""
    ph = next(p for p in eb.phdrs if p.type == 0x6474E550)
    hdr = eb.read_va(ph.vaddr, ph.memsz)
    ver, ptr_enc, cnt_enc, tab_enc = hdr[0], hdr[1], hdr[2], hdr[3]
    if ver != 1:
        raise ValueError(f"eh_frame_hdr version {ver}")
    pos = 4
    _ehf, pos = _dec_ptr(hdr, pos, ptr_enc, ph.vaddr)
    count, pos = _dec_ptr(hdr, pos, cnt_enc, ph.vaddr)
    starts = []
    for _ in range(count):
        loc, pos = _dec_ptr(hdr, pos, tab_enc, ph.vaddr)
        _fde, pos = _dec_ptr(hdr, pos, tab_enc, ph.vaddr)
        starts.append(loc)
    return sorted(set(starts))


@dataclass
class Insn:
    addr: int
    size: int
    mnem: str
    ops: str


@dataclass
class Index:
    eb: Eboot
    funcs: List[int]
    insns: Dict[int, List[Insn]] = field(default_factory=dict)   # func start -> insns
    calls_to: Dict[int, List[int]] = field(default_factory=lambda: defaultdict(list))
    refs_to: Dict[int, List[int]] = field(default_factory=lambda: defaultdict(list))
    data_ptrs_to: Dict[int, List[int]] = field(default_factory=lambda: defaultdict(list))
    ptr_at: Dict[int, int] = field(default_factory=dict)

    @classmethod
    def build(cls, eb: Eboot) -> "Index":
        funcs = eh_function_starts(eb)
        ix = cls(eb, funcs)
        text = eb.text
        code = eb.segment_bytes(text)
        md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        ends = funcs[1:] + [text.vaddr + text.filesz]
        for st, en in zip(funcs, ends):
            if not (text.vaddr <= st < text.vaddr + text.filesz):
                continue
            body = code[st - text.vaddr:en - text.vaddr]
            lst: List[Insn] = []
            for a, s, m, o in md.disasm_lite(body, st):
                lst.append(Insn(a, s, m, o))
                if m in ("call", "jmp") or m.startswith("j"):
                    if o.startswith("0x"):
                        tgt = int(o, 16)
                        if m == "call":
                            ix.calls_to[tgt].append(a)
                        elif not (st <= tgt < en):
                            ix.calls_to[tgt].append(a)   # tail call
                mm = _RIP.search(o)
                if mm:
                    d = int(mm.group(2), 16)
                    tgt = a + s + (d if mm.group(1) == "+" else -d)
                    ix.refs_to[tgt].append(a)
            ix.insns[st] = lst
        for r in eb.rela():
            if r.type == R_X86_64_RELATIVE:
                ix.data_ptrs_to[r.addend].append(r.offset)
                ix.ptr_at[r.offset] = r.addend
        return ix

    # ------------------------------------------------------------------
    def func_of(self, va: int) -> Optional[int]:
        i = bisect.bisect_right(self.funcs, va) - 1
        return self.funcs[i] if i >= 0 else None

    def func_insns(self, va: int) -> List[Insn]:
        f = self.func_of(va)
        return self.insns.get(f, []) if f is not None else []

    def callers(self, va: int) -> List[int]:
        return sorted(self.calls_to.get(va, []))

    def caller_funcs(self, va: int) -> List[int]:
        return sorted({f for f in (self.func_of(c) for c in self.callers(va)) if f is not None})

    def code_refs(self, va: int) -> List[int]:
        return sorted(self.refs_to.get(va, []))

    def callees(self, func: int) -> List[int]:
        out = []
        for ins in self.insns.get(func, []):
            if ins.mnem == "call" and ins.ops.startswith("0x"):
                out.append(int(ins.ops, 16))
        return out

    def rip_targets(self, func: int) -> List[Tuple[int, int]]:
        """(insn addr, target VA) for every RIP-relative operand in a function."""
        out = []
        for ins in self.insns.get(func, []):
            mm = _RIP.search(ins.ops)
            if mm:
                d = int(mm.group(2), 16)
                out.append((ins.addr, ins.addr + ins.size + (d if mm.group(1) == "+" else -d)))
        return out

    def strings_used(self, func: int, min_len: int = 2) -> List[Tuple[int, bytes]]:
        out = []
        for a, t in self.rip_targets(func):
            s = self.eb.cstr(t, 256)
            if len(s) >= min_len:
                try:
                    s.decode("utf-8")
                except UnicodeDecodeError:
                    continue
                out.append((a, s))
        return out

    def ptr_table(self, va: int, max_n: int = 4096, stride: int = 8) -> List[int]:
        """Consecutive relocated pointers starting at va (e.g. a vtable or command table)."""
        out = []
        for i in range(max_n):
            p = self.ptr_at.get(va + i * stride)
            if p is None:
                break
            out.append(p)
        return out


def disasm_range(eb: Eboot, start: int, end: int) -> Iterator[Insn]:
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
    code = eb.read_va(start, end - start)
    for a, s, m, o in md.disasm_lite(code, start):
        yield Insn(a, s, m, o)


def annotate(eb: Eboot, ins: Insn, names: Dict[int, str]) -> str:
    """Render an instruction with string / import-name comments."""
    note = ""
    mm = _RIP.search(ins.ops)
    if mm:
        d = int(mm.group(2), 16)
        t = ins.addr + ins.size + (d if mm.group(1) == "+" else -d)
        if t in names:
            note = f"  ; {names[t]}"
        else:
            s = eb.cstr(t, 120)
            if len(s) >= 2:
                try:
                    note = f"  ; 0x{t:x} {s.decode('utf-8')!r}"
                except UnicodeDecodeError:
                    note = f"  ; 0x{t:x}"
            else:
                note = f"  ; 0x{t:x}"
    elif ins.mnem in ("call", "jmp") and ins.ops.startswith("0x"):
        t = int(ins.ops, 16)
        if t in names:
            note = f"  ; {names[t]}"
    return f"{ins.addr:08x}: {ins.mnem:<7} {ins.ops}{note}"


def import_names(eb: Eboot) -> Dict[int, str]:
    """VA -> name for PLT stubs and GOT slots of resolved imports."""
    from eboot.nids import load_db

    db = load_db()
    imps = eb.imports()
    eb.find_plt(imps)
    out: Dict[int, str] = {}
    for i in imps:
        nm = db.get(i.nid, "?" + i.nid)
        if i.plt_va is not None:
            out[i.plt_va] = nm
        if i.got_va is not None and i.reloc_type == 7:
            out[i.got_va] = nm + "@got"
    return out


def _main(argv: List[str]) -> int:
    from eboot import eboot_path

    if not argv:
        print(__doc__)
        return 1
    p = eboot_path()
    if p is None:
        print("game not found", file=sys.stderr)
        return 2
    eb = Eboot.open(p)
    names = import_names(eb)
    cmd = argv[0]
    if cmd == "dis":
        a = int(argv[1], 0)
        b = argv[2] if len(argv) > 2 else "+0x100"
        e = a + int(b[1:], 0) if b.startswith("+") else int(b, 0)
        for ins in disasm_range(eb, a, e):
            print(annotate(eb, ins, names))
        return 0
    ix = Index.build(eb)
    if cmd == "func":
        f = ix.func_of(int(argv[1], 0))
        print(f"function 0x{f:x}; callers: {[hex(c) for c in ix.callers(f or 0)][:20]}")
        for ins in ix.func_insns(int(argv[1], 0)):
            print(annotate(eb, ins, names))
    elif cmd == "xref":
        t = int(argv[1], 0)
        for c in ix.callers(t):
            print(f"call/jmp  {c:08x} in func {ix.func_of(c) or 0:08x}")
        for c in ix.code_refs(t):
            print(f"rip-ref   {c:08x} in func {ix.func_of(c) or 0:08x}")
        for d in sorted(ix.data_ptrs_to.get(t, [])):
            print(f"data-ptr  {d:08x}")
    elif cmd == "strref":
        rx = re.compile(argv[1])
        from eboot.strings import all_strings

        for va, seg, s in all_strings(eb, 2):
            if rx.search(s):
                fs = sorted({ix.func_of(c) or 0 for c in ix.code_refs(va)})
                print(f"{va:08x} {s[:60]!r}: funcs {[hex(f) for f in fs]} "
                      f"dptr {[hex(d) for d in ix.data_ptrs_to.get(va, [])][:4]}")
    elif cmd == "funcmap":
        from eboot.symbols import SYMBOLS

        known = {x.va: x.name for x in SYMBOLS}
        nl, tab = chr(10), chr(9)
        with open(argv[1], "w", encoding="utf-8", newline=nl) as fh:
            fh.write(tab.join(["va", "insns", "callers", "data_ptrs", "name", "imports",
                              "strings"]) + nl)
            for fn in ix.funcs:
                imps = sorted({names[c] for c in ix.callees(fn) if c in names})
                strs = []
                for _a, sv in ix.strings_used(fn, 3):
                    try:
                        txt = sv.decode("utf-8")
                    except UnicodeDecodeError:
                        continue
                    if txt.isprintable():
                        strs.append(txt[:40])
                row = [f"{fn:06x}", str(len(ix.insns.get(fn, []))), str(len(ix.caller_funcs(fn))),
                       str(len(ix.data_ptrs_to.get(fn, []))), known.get(fn, ""), ",".join(imps),
                       " | ".join(strs[:12])]
                fh.write(tab.join(row) + nl)
        print(f"{len(ix.funcs)} functions -> {argv[1]}")
    else:
        print(f"unknown command {cmd}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
