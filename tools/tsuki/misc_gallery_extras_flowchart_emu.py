"""Run the engine's own flowchart column filter (cFlowchartScene build, VA 0x21430) under
unicorn and compare it with :func:`tsuki.misc_gallery_extras_flowchart.flow_filter`.

Only the column loop (0x21430 .. 0x21774) is executed.  The two std::vector pushes of a
kept column (``call 0x253D0`` per node at 0x216F3, ``call 0x254C0`` at 0x21737) are skipped
and the column index (``[rsp+0x48]``) is recorded at the second one.  The flag tests
0xB7B50 (scene_done) and 0xB7B80 (choice_seen) run natively and read var_G (0x164C620),
which the harness fills from an :class:`UnlockState`.  AVX instructions only copy node
records to the stack before the skipped pushes, so they are skipped too (unicorn 2.1.4 has
no reliable VEX support).

    python -m tsuki.misc_gallery_extras_flowchart_emu [--trials N]
"""
from __future__ import annotations

import random
import struct
import sys
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple, Union

from .misc_gallery_extras_flowchart import (CHOICE_ID_HI, CHOICE_ID_LO, FlowRaw, UnlockState,
                                            flow_filter)

TEXT_OFF, TEXT_LEN = 0x2280, 0x329DC8
RELRO_OFF, RELRO_LEN, RELRO_VA = 0x32C210, 0x35ED0, 0x400000
DATA_OFF, DATA_LEN, DATA_VA, DATA_MEMSZ = 0x365FA0, 0x7D66E0, 0x600000, 0x1A85790
G_VA = 0x164C620
FUNC = 0x21430
STOP = 0x21774
CALL_PUSH_NODE, AFTER_PUSH_NODE = 0x216F3, 0x216F8
CALL_PUSH_COL, AFTER_PUSH_COL = 0x21737, 0x2173C
STK_LO, STK_HI = 0x7F000000, 0x7F100000
THIS = 0x7E000000
RET = 0x7EF00000


class FlowEmu:
    def __init__(self, eboot: Union[str, Path]) -> None:
        import capstone
        from unicorn import UC_ARCH_X86, UC_HOOK_CODE, UC_MODE_64, Uc
        d = Path(eboot).read_bytes()
        self.uc = uc = Uc(UC_ARCH_X86, UC_MODE_64)
        uc.mem_map(0, 0x440000)
        uc.mem_write(0, d[TEXT_OFF:TEXT_OFF + TEXT_LEN])
        uc.mem_write(RELRO_VA, d[RELRO_OFF:RELRO_OFF + RELRO_LEN])
        uc.mem_map(DATA_VA, (DATA_MEMSZ + 0xFFFF) & ~0xFFFF)
        uc.mem_write(DATA_VA, d[DATA_OFF:DATA_OFF + DATA_LEN])
        uc.mem_map(STK_LO, STK_HI - STK_LO)
        uc.mem_map(THIS, 0x10000)
        uc.mem_map(RET, 0x1000)
        self._md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self._len: Dict[int, Tuple[int, bool]] = {}
        self.kept: List[int] = []
        self.calls_b7b50 = 0
        uc.hook_add(UC_HOOK_CODE, self._code)

    def _reg(self, name: str) -> int:
        from unicorn import x86_const
        return int(self.uc.reg_read(getattr(x86_const, "UC_X86_REG_" + name)))

    def _set(self, name: str, v: int) -> None:
        from unicorn import x86_const
        self.uc.reg_write(getattr(x86_const, "UC_X86_REG_" + name), v & (1 << 64) - 1)

    def _code(self, uc: object, a: int, size: int, _: object) -> None:
        if a == STOP:
            self.uc.emu_stop()
            return
        if a == CALL_PUSH_COL:
            rsp = self._reg("RSP")
            self.kept.append(struct.unpack("<q", bytes(self.uc.mem_read(rsp + 0x48, 8)))[0])
            self._set("RIP", AFTER_PUSH_COL)
            return
        if a == CALL_PUSH_NODE:
            self._set("RIP", AFTER_PUSH_NODE)
            return
        if a == 0xB7B50:
            self.calls_b7b50 += 1
        info = self._len.get(a)
        if info is None:
            ins = next(self._md.disasm_lite(bytes(self.uc.mem_read(a, 16)), a))
            info = self._len[a] = (ins[1], ins[2].startswith("v"))
        if info[1]:
            self._set("RIP", a + info[0])

    def run(self, state: UnlockState) -> List[int]:
        """Columns kept by the engine for ``state``."""
        self.uc.mem_write(G_VA, struct.pack("<500H", *state.g))
        self.uc.mem_write(THIS, bytes(0x400))
        self.kept = []
        rsp = STK_HI - 0x1000
        self.uc.mem_write(rsp, struct.pack("<Q", RET))
        self._set("RSP", rsp)
        self._set("RBP", 0)
        self._set("RDI", THIS)
        self.uc.emu_start(FUNC, RET, count=5_000_000)
        if self._reg("RIP") != STOP:
            raise RuntimeError(f"emulation stopped at 0x{self._reg('RIP'):x}")
        return list(self.kept)


def random_state(rng: random.Random, p_scene: float, p_choice: float, upto: Optional[int] = None) -> UnlockState:
    s = UnlockState()
    for n in range(1, 576):
        if (upto is None or n <= upto) and rng.random() < p_scene:
            s.set_scene_done(n)
    for cid in range(CHOICE_ID_LO, CHOICE_ID_HI + 1):
        if rng.random() < p_choice:
            s.set_choice_seen(cid)
    return s


def compare(eboot: Union[str, Path], trials: int = 40, seed: int = 20261025) -> Tuple[int, List[str]]:
    """Run ``trials`` random unlock states plus the empty and the full state; returns
    (number compared, list of mismatch descriptions)."""
    emu = FlowEmu(eboot)
    raw = FlowRaw.from_eboot(eboot)
    rng = random.Random(seed)
    states = [UnlockState(), random_state(rng, 1.0, 1.0)]
    for i in range(trials):
        p = rng.choice([0.02, 0.1, 0.3, 0.6, 0.9])
        q = rng.choice([0.0, 0.05, 0.3, 0.8])
        states.append(random_state(rng, p, q, upto=rng.choice([None, 50, 150, 300, 495])))
    bad: List[str] = []
    for i, s in enumerate(states):
        e = emu.run(s)
        m = flow_filter(raw, s)
        if e != m:
            bad.append(f"state {i}: engine {len(e)} columns, model {len(m)}; first diff "
                       f"{sorted(set(e) ^ set(m))[:5]}")
    return len(states), bad


def _main(argv: Optional[Sequence[str]] = None) -> int:
    import argparse
    from eboot import eboot_path
    ap = argparse.ArgumentParser()
    ap.add_argument("--trials", type=int, default=40)
    a = ap.parse_args(argv)
    p = eboot_path()
    if p is None:
        print("eboot.bin not found", file=sys.stderr)
        return 1
    n, bad = compare(p, a.trials)
    print(f"{n} states compared, {len(bad)} mismatches")
    for b in bad:
        print(" ", b)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(_main())
