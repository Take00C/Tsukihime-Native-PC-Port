"""Minimal AMD GCN (Sea Islands / GCN2, the PS4 "Liverpool" GPU) decoder and interpreter.

Scope: exactly what the 67 PSSL shader binaries embedded in eboot.bin need (see
docs/formats/compositing.md): SOP1/SOP2/SOPK/SOPC/SOPP, SMRD, VOP1/VOP2/VOPC/VOP3,
VINTRP, MIMG (image_sample*) and EXP, plus the Gnmx shader header (register block,
input-usage slots, input semantics) that tells which user SGPR holds which resource.

Sources used to learn the encodings (no code copied):
  * AMD "Sea Islands Series Instruction Set Architecture" reference guide (2013),
    chapters 12-13 (microcode formats, opcode tables, operand encoding).
  * shadPS4 (GPL-2.0) src/shader_recompiler/frontend/{decode.cpp,opcodes.h,format.cpp}:
    read only to cross-check opcode numbers; this is an independent implementation.
  * The Gnmx ShaderFileHeader / ShaderCommonData / PsStageRegisters / InputUsageSlot
    layouts as described by open-source PS4 projects (GPCS4, shadPS4 "gnmx" notes).

The interpreter is SIMT over N lanes with numpy (one "wave" of arbitrary width): VGPRs
are uint32 bit patterns of shape (256, N); SGPRs are Python ints.  Float ALU ops are
evaluated in float32 (IEEE, round to nearest) which matches the hardware to well below
1/255 for the ops used here (v_rcp/v_rsq/v_sqrt/v_exp/v_log are exact in numpy and
<= 1 ulp on the GPU).  Resources are abstract: a T# is recognised by the id written in
its first dword by `make_tsharp`, and `image_sample` asks a Python callback for texels.

CLI:  python -m tsuki.gcn <blob.sb> [...]          disassemble Shdr blobs (or raw code with --raw)
"""
from __future__ import annotations

import struct
import sys
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional, Sequence, Tuple

import numpy as np

__all__ = ["Insn", "decode", "disasm", "ShaderHeader", "InputSlot", "parse_shader",
           "Machine", "make_vsharp", "make_tsharp", "make_ssharp", "f2u", "u2f",
           "USAGE_NAMES", "GcnError"]


class GcnError(ValueError):
    pass


# ----------------------------------------------------------------------------------------
# Opcode tables (Sea Islands numbering)
# ----------------------------------------------------------------------------------------

SOP2 = {0: "s_add_u32", 1: "s_sub_u32", 2: "s_add_i32", 3: "s_sub_i32", 4: "s_addc_u32",
        5: "s_subb_u32", 6: "s_min_i32", 7: "s_min_u32", 8: "s_max_i32", 9: "s_max_u32",
        10: "s_cselect_b32", 11: "s_cselect_b64", 14: "s_and_b32", 15: "s_and_b64",
        16: "s_or_b32", 17: "s_or_b64", 18: "s_xor_b32", 19: "s_xor_b64",
        20: "s_andn2_b32", 21: "s_andn2_b64", 22: "s_orn2_b32", 23: "s_orn2_b64",
        24: "s_nand_b32", 25: "s_nand_b64", 26: "s_nor_b32", 27: "s_nor_b64",
        28: "s_xnor_b32", 29: "s_xnor_b64", 30: "s_lshl_b32", 31: "s_lshl_b64",
        32: "s_lshr_b32", 33: "s_lshr_b64", 34: "s_ashr_i32", 35: "s_ashr_i64",
        36: "s_bfm_b32", 37: "s_bfm_b64", 38: "s_mul_i32", 39: "s_bfe_u32", 40: "s_bfe_i32",
        41: "s_bfe_u64", 42: "s_bfe_i64", 44: "s_absdiff_i32"}
SOPK = {0: "s_movk_i32", 2: "s_cmovk_i32", 15: "s_addk_i32", 16: "s_mulk_i32"}
SOP1 = {3: "s_mov_b32", 4: "s_mov_b64", 5: "s_cmov_b32", 6: "s_cmov_b64", 7: "s_not_b32",
        8: "s_not_b64", 9: "s_wqm_b32", 10: "s_wqm_b64", 11: "s_brev_b32", 12: "s_brev_b64",
        31: "s_getpc_b64", 32: "s_setpc_b64", 33: "s_swappc_b64", 34: "s_rfe_b64",
        36: "s_and_saveexec_b64", 37: "s_or_saveexec_b64", 38: "s_xor_saveexec_b64",
        39: "s_andn2_saveexec_b64", 40: "s_orn2_saveexec_b64", 41: "s_nand_saveexec_b64",
        42: "s_nor_saveexec_b64", 43: "s_xnor_saveexec_b64"}
SOPC = {0: "s_cmp_eq_i32", 1: "s_cmp_lg_i32", 2: "s_cmp_gt_i32", 3: "s_cmp_ge_i32",
        4: "s_cmp_lt_i32", 5: "s_cmp_le_i32", 6: "s_cmp_eq_u32", 7: "s_cmp_lg_u32",
        8: "s_cmp_gt_u32", 9: "s_cmp_ge_u32", 10: "s_cmp_lt_u32", 11: "s_cmp_le_u32"}
SOPP = {0: "s_nop", 1: "s_endpgm", 2: "s_branch", 4: "s_cbranch_scc0", 5: "s_cbranch_scc1",
        6: "s_cbranch_vccz", 7: "s_cbranch_vccnz", 8: "s_cbranch_execz",
        9: "s_cbranch_execnz", 10: "s_barrier", 12: "s_waitcnt", 13: "s_sethalt",
        14: "s_sleep", 15: "s_setprio", 16: "s_sendmsg"}
SMRD = {0: "s_load_dword", 1: "s_load_dwordx2", 2: "s_load_dwordx4", 3: "s_load_dwordx8",
        4: "s_load_dwordx16", 8: "s_buffer_load_dword", 9: "s_buffer_load_dwordx2",
        10: "s_buffer_load_dwordx4", 11: "s_buffer_load_dwordx8",
        12: "s_buffer_load_dwordx16", 30: "s_memtime", 31: "s_dcache_inv"}
SMRD_COUNT = {0: 1, 1: 2, 2: 4, 3: 8, 4: 16, 8: 1, 9: 2, 10: 4, 11: 8, 12: 16}
VOP2 = {0: "v_cndmask_b32", 1: "v_readlane_b32", 2: "v_writelane_b32", 3: "v_add_f32",
        4: "v_sub_f32", 5: "v_subrev_f32", 6: "v_mac_legacy_f32", 7: "v_mul_legacy_f32",
        8: "v_mul_f32", 9: "v_mul_i32_i24", 10: "v_mul_hi_i32_i24", 11: "v_mul_u32_u24",
        12: "v_mul_hi_u32_u24", 13: "v_min_legacy_f32", 14: "v_max_legacy_f32",
        15: "v_min_f32", 16: "v_max_f32", 17: "v_min_i32", 18: "v_max_i32", 19: "v_min_u32",
        20: "v_max_u32", 21: "v_lshr_b32", 22: "v_lshrrev_b32", 23: "v_ashr_i32",
        24: "v_ashrrev_i32", 25: "v_lshl_b32", 26: "v_lshlrev_b32", 27: "v_and_b32",
        28: "v_or_b32", 29: "v_xor_b32", 30: "v_bfm_b32", 31: "v_mac_f32", 32: "v_madmk_f32",
        33: "v_madak_f32", 34: "v_bcnt_u32_b32", 35: "v_mbcnt_lo_u32_b32",
        36: "v_mbcnt_hi_u32_b32", 37: "v_add_i32", 38: "v_sub_i32", 39: "v_subrev_i32",
        40: "v_addc_u32", 41: "v_subb_u32", 42: "v_subbrev_u32", 43: "v_ldexp_f32",
        44: "v_cvt_pkaccum_u8_f32", 45: "v_cvt_pknorm_i16_f32", 46: "v_cvt_pknorm_u16_f32",
        47: "v_cvt_pkrtz_f16_f32", 48: "v_cvt_pk_u16_u32", 49: "v_cvt_pk_i16_i32"}
VOP1 = {0: "v_nop", 1: "v_mov_b32", 2: "v_readfirstlane_b32", 5: "v_cvt_f32_i32",
        6: "v_cvt_f32_u32", 7: "v_cvt_u32_f32", 8: "v_cvt_i32_f32", 10: "v_cvt_f16_f32",
        11: "v_cvt_f32_f16", 12: "v_cvt_rpi_i32_f32", 13: "v_cvt_flr_i32_f32",
        17: "v_cvt_f32_ubyte0", 18: "v_cvt_f32_ubyte1", 19: "v_cvt_f32_ubyte2",
        20: "v_cvt_f32_ubyte3", 32: "v_fract_f32", 33: "v_trunc_f32", 34: "v_ceil_f32",
        35: "v_rndne_f32", 36: "v_floor_f32", 37: "v_exp_f32", 38: "v_log_clamp_f32",
        39: "v_log_f32", 40: "v_rcp_clamp_f32", 41: "v_rcp_legacy_f32", 42: "v_rcp_f32",
        43: "v_rcp_iflag_f32", 44: "v_rsq_clamp_f32", 45: "v_rsq_legacy_f32", 46: "v_rsq_f32",
        51: "v_sqrt_f32", 53: "v_sin_f32", 54: "v_cos_f32", 55: "v_not_b32",
        56: "v_bfrev_b32", 57: "v_ffbh_u32", 58: "v_ffbl_b32", 59: "v_ffbh_i32",
        64: "v_frexp_mant_f32", 65: "v_frexp_exp_i32_f32"}
_CMP_F = ["f", "lt", "eq", "le", "gt", "lg", "ge", "o", "u", "nge", "nlg", "ngt", "nle", "neq",
          "nlt", "tru"]
_CMP_I = ["f", "lt", "eq", "le", "gt", "ne", "ge", "t"]
VOPC: Dict[int, str] = {}
for _i, _c in enumerate(_CMP_F):
    VOPC[0x00 + _i] = f"v_cmp_{_c}_f32"
    VOPC[0x10 + _i] = f"v_cmpx_{_c}_f32"
for _i, _c in enumerate(_CMP_I):
    VOPC[0x80 + _i] = f"v_cmp_{_c}_i32"
    VOPC[0x90 + _i] = f"v_cmpx_{_c}_i32"
    VOPC[0xC0 + _i] = f"v_cmp_{_c}_u32"
    VOPC[0xD0 + _i] = f"v_cmpx_{_c}_u32"
VOPC[0x88] = "v_cmp_class_f32"
VOPC[0x98] = "v_cmpx_class_f32"
VOP3_ONLY = {320: "v_mad_legacy_f32", 321: "v_mad_f32", 322: "v_mad_i32_i24",
             323: "v_mad_u32_u24", 324: "v_cubeid_f32", 325: "v_cubesc_f32",
             326: "v_cubetc_f32", 327: "v_cubema_f32", 328: "v_bfe_u32", 329: "v_bfe_i32",
             330: "v_bfi_b32", 331: "v_fma_f32", 334: "v_alignbit_b32", 335: "v_alignbyte_b32",
             337: "v_min3_f32", 338: "v_min3_i32", 339: "v_min3_u32", 340: "v_max3_f32",
             341: "v_max3_i32", 342: "v_max3_u32", 343: "v_med3_f32", 344: "v_med3_i32",
             345: "v_med3_u32", 351: "v_cvt_pk_u8_f32", 352: "v_div_fixup_f32",
             366: "v_mul_lo_u32", 367: "v_mul_hi_u32", 368: "v_mul_lo_i32", 369: "v_mul_hi_i32",
             370: "v_div_scale_f32", 372: "v_div_fmas_f32"}
VINTRP = {0: "v_interp_p1_f32", 1: "v_interp_p2_f32", 2: "v_interp_mov_f32"}
MIMG = {0x00: "image_load", 0x08: "image_store", 0x20: "image_sample", 0x21: "image_sample_cl",
        0x22: "image_sample_d", 0x24: "image_sample_l", 0x25: "image_sample_b",
        0x27: "image_sample_lz", 0x28: "image_sample_c", 0x2F: "image_sample_c_lz",
        0x40: "image_gather4"}

# Gnm InputUsageSlot.m_usageType
USAGE_NAMES = {0x00: "ImmResource", 0x01: "ImmSampler", 0x02: "ImmConstBuffer",
               0x03: "ImmVertexBuffer", 0x04: "ImmRwResource", 0x05: "ImmAluFloatConst",
               0x06: "ImmAluBool32Const", 0x0A: "ImmShaderResourceTable",
               0x12: "SubPtrFetchShader", 0x13: "PtrResourceTable",
               0x15: "PtrSamplerTable", 0x16: "PtrConstBufferTable",
               0x17: "PtrVertexBufferTable", 0x1B: "PtrExtendedUserData"}

INLINE_F = {240: 0.5, 241: -0.5, 242: 1.0, 243: -1.0, 244: 2.0, 245: -2.0, 246: 4.0, 247: -4.0}


def f2u(x: float) -> int:
    return struct.unpack("<I", struct.pack("<f", x))[0]


def u2f(x: int) -> float:
    return struct.unpack("<f", struct.pack("<I", x & 0xFFFFFFFF))[0]


def _src_name(v: int, lit: Optional[int]) -> str:
    if v < 104:
        return f"s{v}"
    special = {106: "vcc_lo", 107: "vcc_hi", 124: "m0", 126: "exec_lo", 127: "exec_hi",
               251: "vccz", 252: "execz", 253: "scc"}
    if v in special:
        return special[v]
    if 128 <= v <= 192:
        return str(v - 128)
    if 193 <= v <= 208:
        return str(192 - v)
    if v in INLINE_F:
        return repr(INLINE_F[v])
    if v == 255:
        assert lit is not None
        f = u2f(lit)
        return f"0x{lit:08x}" + (f" ({f:.6g})" if abs(f) > 1e-30 and abs(f) < 1e30 else "")
    if 256 <= v < 512:
        return f"v{v - 256}"
    return f"?src{v}"


@dataclass
class Insn:
    pc: int                 # byte offset in the code
    size: int
    enc: str                # SOP2, SOPK, SOP1, SOPC, SOPP, SMRD, VOP1, VOP2, VOPC, VOP3, VINTRP, MIMG, EXP
    op: int
    name: str
    f: Dict[str, int] = field(default_factory=dict)
    lit: Optional[int] = None

    def text(self) -> str:
        f, e, L = self.f, self.enc, self.lit
        if e in ("SOP2", "SOPC"):
            a = [_src_name(f["ssrc0"], L), _src_name(f["ssrc1"], L)]
            if e == "SOP2":
                a.insert(0, _src_name(f["sdst"], None) + ("" if "64" not in self.name else "+"))
            return f"{self.name} {', '.join(a)}"
        if e == "SOP1":
            return f"{self.name} {_src_name(f['sdst'], None)}, {_src_name(f['ssrc0'], L)}"
        if e == "SOPK":
            return f"{self.name} {_src_name(f['sdst'], None)}, 0x{f['simm16']:x}"
        if e == "SOPP":
            if self.name == "s_waitcnt":
                s = f["simm16"]
                return f"s_waitcnt vmcnt({s & 0xF}) expcnt({(s >> 4) & 7}) lgkmcnt({(s >> 8) & 0x1F})"
            if self.name.startswith("s_branch") or self.name.startswith("s_cbranch"):
                tgt = self.pc + 4 + 4 * ((f["simm16"] ^ 0x8000) - 0x8000)
                return f"{self.name} 0x{tgt:x}"
            return f"{self.name}" + (f" 0x{f['simm16']:x}" if f["simm16"] else "")
        if e == "SMRD":
            off = f"0x{f['offset'] * 4:x}" if f["imm"] else _src_name(f["offset"], L)
            n = SMRD_COUNT.get(self.op, 1)
            return (f"{self.name} s[{f['sdst']}:{f['sdst'] + n - 1}], "
                    f"s[{f['sbase']}:{f['sbase'] + (3 if self.op >= 8 else 1)}], {off}")
        if e in ("VOP1", "VOP2", "VOPC"):
            s0 = _src_name(f["src0"], L)
            if e == "VOP1":
                return f"{self.name} v{f['vdst']}, {s0}"
            s1 = f"v{f['vsrc1']}"
            if e == "VOPC":
                return f"{self.name} vcc, {s0}, {s1}"
            if self.name == "v_madmk_f32":
                return f"{self.name} v{f['vdst']}, {s0}, {_src_name(255, L)}, {s1}"
            if self.name == "v_madak_f32":
                return f"{self.name} v{f['vdst']}, {s0}, {s1}, {_src_name(255, L)}"
            extra = ", vcc" if self.name == "v_cndmask_b32" else ""
            return f"{self.name} v{f['vdst']}, {s0}, {s1}{extra}"
        if e == "VOP3":
            srcs = []
            for k in range(3):
                s = _src_name(f[f"src{k}"], L)
                if (f["abs"] >> k) & 1:
                    s = f"|{s}|"
                if (f["neg"] >> k) & 1:
                    s = "-" + s
                srcs.append(s)
            nsrc = _vop3_nsrc(self.op, self.name)
            dst = _src_name(f["vdst"], None) if self.op < 256 else f"v{f['vdst']}"
            if self.op < 256:
                dst = f"s[{f['vdst']}:{f['vdst'] + 1}]"
            mods = (" clamp" if f["clamp"] else "") + ({1: " mul:2", 2: " mul:4", 3: " div:2"}.get(f["omod"], ""))
            return f"{self.name} {dst}, {', '.join(srcs[:nsrc])}{mods}"
        if e == "VINTRP":
            return (f"{self.name} v{f['vdst']}, v{f['vsrc']}, attr{f['attr']}."
                    f"{'xyzw'[f['chan']]}")
        if e == "MIMG":
            return (f"{self.name} v[{f['vdata']}..], v[{f['vaddr']}..], s[{f['srsrc']}:{f['srsrc'] + 7}], "
                    f"s[{f['ssamp']}:{f['ssamp'] + 3}] dmask:0x{f['dmask']:x}")
        if e == "EXP":
            tgt = f["target"]
            tn = (f"mrt{tgt}" if tgt < 8 else "mrtz" if tgt == 8 else "null" if tgt == 9 else
                  f"pos{tgt - 12}" if 12 <= tgt < 16 else f"param{tgt - 32}" if tgt >= 32 else f"t{tgt}")
            vs = [f"v{f['vsrc' + str(k)]}" if (f['en'] >> k) & 1 else "off" for k in range(4)]
            return (f"exp {tn}, {', '.join(vs)}" + (" compr" if f["compr"] else "") +
                    (" done" if f["done"] else "") + (" vm" if f["vm"] else ""))
        return self.name


def _vop3_nsrc(op: int, name: str) -> int:
    if op < 256:
        return 2
    if op >= 384:
        return 1
    if op < 320:
        return 3 if name in ("v_cndmask_b32", "v_mac_f32", "v_mac_legacy_f32") else 2
    return 3


def decode(code: bytes, stop_at_endpgm: bool = True) -> List[Insn]:
    """Decode GCN machine code into instructions (up to and including s_endpgm)."""
    out: List[Insn] = []
    i, n = 0, len(code)
    while i + 4 <= n:
        w = struct.unpack_from("<I", code, i)[0]
        size = 4
        lit: Optional[int] = None
        needs_lit = False
        if (w >> 25) == 0x3F:                           # VOP1
            op = (w >> 9) & 0xFF
            fl = {"src0": w & 0x1FF, "vdst": (w >> 17) & 0xFF}
            ins = Insn(i, 4, "VOP1", op, VOP1.get(op, f"vop1_{op}"), fl)
            needs_lit = fl["src0"] == 255
        elif (w >> 25) == 0x3E:                           # VOPC
            op = (w >> 17) & 0xFF
            fl = {"src0": w & 0x1FF, "vsrc1": (w >> 9) & 0xFF}
            ins = Insn(i, 4, "VOPC", op, VOPC.get(op, f"vopc_{op}"), fl)
            needs_lit = fl["src0"] == 255
        elif w >> 31 == 0:                                  # VOP2
            op = (w >> 25) & 0x3F
            fl = {"src0": w & 0x1FF, "vsrc1": (w >> 9) & 0xFF, "vdst": (w >> 17) & 0xFF}
            ins = Insn(i, 4, "VOP2", op, VOP2.get(op, f"vop2_{op}"), fl)
            needs_lit = fl["src0"] == 255 or op in (32, 33)
        elif (w >> 23) == 0x17D:                          # SOP1
            op = (w >> 8) & 0xFF
            fl = {"ssrc0": w & 0xFF, "sdst": (w >> 16) & 0x7F}
            ins = Insn(i, 4, "SOP1", op, SOP1.get(op, f"sop1_{op}"), fl)
            needs_lit = fl["ssrc0"] == 255
        elif (w >> 23) == 0x17E:                          # SOPC
            op = (w >> 16) & 0x7F
            fl = {"ssrc0": w & 0xFF, "ssrc1": (w >> 8) & 0xFF}
            ins = Insn(i, 4, "SOPC", op, SOPC.get(op, f"sopc_{op}"), fl)
            needs_lit = 255 in (fl["ssrc0"], fl["ssrc1"])
        elif (w >> 23) == 0x17F:                          # SOPP
            op = (w >> 16) & 0x7F
            ins = Insn(i, 4, "SOPP", op, SOPP.get(op, f"sopp_{op}"), {"simm16": w & 0xFFFF})
        elif (w >> 28) == 0xB:                            # SOPK
            op = (w >> 23) & 0x1F
            ins = Insn(i, 4, "SOPK", op, SOPK.get(op, f"sopk_{op}"),
                       {"simm16": w & 0xFFFF, "sdst": (w >> 16) & 0x7F})
        elif (w >> 30) == 0x2:                            # SOP2
            op = (w >> 23) & 0x7F
            fl = {"ssrc0": w & 0xFF, "ssrc1": (w >> 8) & 0xFF, "sdst": (w >> 16) & 0x7F}
            ins = Insn(i, 4, "SOP2", op, SOP2.get(op, f"sop2_{op}"), fl)
            needs_lit = 255 in (fl["ssrc0"], fl["ssrc1"])
        elif (w >> 27) == 0x18:                           # SMRD
            op = (w >> 22) & 0x1F
            fl = {"offset": w & 0xFF, "imm": (w >> 8) & 1, "sbase": ((w >> 9) & 0x3F) * 2,
                  "sdst": (w >> 15) & 0x7F}
            ins = Insn(i, 4, "SMRD", op, SMRD.get(op, f"smrd_{op}"), fl)
            needs_lit = fl["offset"] == 255 and not fl["imm"]
        elif (w >> 26) == 0x32:                           # VINTRP
            op = (w >> 16) & 3
            ins = Insn(i, 4, "VINTRP", op, VINTRP[op] if op in VINTRP else "vintrp_3",
                       {"vsrc": w & 0xFF, "chan": (w >> 8) & 3, "attr": (w >> 10) & 0x3F,
                        "vdst": (w >> 18) & 0xFF})
        elif (w >> 26) == 0x34:                           # VOP3 (a/b)
            if i + 8 > n:
                raise GcnError(f"truncated VOP3 at 0x{i:x}")
            w1 = struct.unpack_from("<I", code, i + 4)[0]
            op = (w >> 17) & 0x1FF
            if op < 256:
                name = VOPC.get(op, f"vopc_{op}")
            elif op < 320:
                name = VOP2.get(op - 256, f"vop2_{op - 256}")
            elif op >= 384:
                name = VOP1.get(op - 384, f"vop1_{op - 384}")
            else:
                name = VOP3_ONLY.get(op, f"vop3_{op}")
            fl = {"vdst": w & 0xFF, "abs": (w >> 8) & 7, "clamp": (w >> 11) & 1,
                  "sdst": (w >> 8) & 0x7F, "src0": w1 & 0x1FF, "src1": (w1 >> 9) & 0x1FF,
                  "src2": (w1 >> 18) & 0x1FF, "omod": (w1 >> 27) & 3, "neg": (w1 >> 29) & 7}
            ins = Insn(i, 8, "VOP3", op, name, fl)
            size = 8
        elif (w >> 26) == 0x3C:                           # MIMG
            w1 = struct.unpack_from("<I", code, i + 4)[0]
            op = (w >> 18) & 0x7F
            fl = {"dmask": (w >> 8) & 0xF, "unorm": (w >> 12) & 1, "glc": (w >> 13) & 1,
                  "da": (w >> 14) & 1, "r128": (w >> 15) & 1, "tfe": (w >> 16) & 1,
                  "lwe": (w >> 17) & 1, "slc": (w >> 25) & 1, "vaddr": w1 & 0xFF,
                  "vdata": (w1 >> 8) & 0xFF, "srsrc": ((w1 >> 16) & 0x1F) * 4,
                  "ssamp": ((w1 >> 21) & 0x1F) * 4}
            ins = Insn(i, 8, "MIMG", op, MIMG.get(op, f"mimg_{op:#x}"), fl)
            size = 8
        elif (w >> 26) == 0x3E:                           # EXP
            w1 = struct.unpack_from("<I", code, i + 4)[0]
            fl = {"en": w & 0xF, "target": (w >> 4) & 0x3F, "compr": (w >> 10) & 1,
                  "done": (w >> 11) & 1, "vm": (w >> 12) & 1, "vsrc0": w1 & 0xFF,
                  "vsrc1": (w1 >> 8) & 0xFF, "vsrc2": (w1 >> 16) & 0xFF, "vsrc3": (w1 >> 24) & 0xFF}
            ins = Insn(i, 8, "EXP", 0, "exp", fl)
            size = 8
        else:
            raise GcnError(f"unknown encoding 0x{w:08x} at 0x{i:x}")
        if ins.enc == "VOP3":
            needs_lit = False   # VOP3 cannot take a literal on GCN2
        if needs_lit:
            lit = struct.unpack_from("<I", code, i + size)[0]
            size += 4
        ins.size, ins.lit = size, lit
        out.append(ins)
        i += size
        if stop_at_endpgm and ins.name == "s_endpgm":
            break
    return out


def disasm(insns: Sequence[Insn]) -> str:
    return "\n".join(f"{x.pc:04x}: {x.text()}" for x in insns)


# ----------------------------------------------------------------------------------------
# Gnmx shader header ("Shdr" block of a PSSL .sb binary)
# ----------------------------------------------------------------------------------------

@dataclass
class InputSlot:
    usage: int              # Gnm InputUsageSlot type (USAGE_NAMES)
    api_slot: int           # resource / sampler / constant-buffer slot number (PSSL register)
    start_reg: int          # first user SGPR
    reg_count_flag: int     # bit 0 of byte 3: 1 = 8-dword T#
    raw: int

    @property
    def name(self) -> str:
        return USAGE_NAMES.get(self.usage, f"usage_{self.usage:#x}")

    @property
    def dwords(self) -> int:
        if self.usage == 0x00:
            return 8 if self.reg_count_flag else 4
        if self.usage in (0x01, 0x02, 0x03):
            return 4
        return 2


@dataclass
class ShaderHeader:
    stage: str                      # "PS" or "VS"
    code_size: int                  # ShaderCommonData.m_shaderSize (bytes)
    regs: Dict[str, int]
    slots: List[InputSlot]
    semantics: List[int]            # raw semantic words (PS: u16 each, VS: u32 each)
    code: bytes

    @property
    def user_sgprs(self) -> int:
        k = "spi_shader_pgm_rsrc2_ps" if self.stage == "PS" else "spi_shader_pgm_rsrc2_vs"
        return (self.regs[k] >> 1) & 0x1F


_PS_REGS = ["spi_shader_pgm_lo_ps", "spi_shader_pgm_hi_ps", "spi_shader_pgm_rsrc1_ps",
            "spi_shader_pgm_rsrc2_ps", "spi_shader_z_format", "spi_shader_col_format",
            "spi_ps_input_ena", "spi_ps_input_addr", "spi_ps_in_control", "spi_baryc_cntl",
            "db_shader_control", "cb_shader_mask"]
_VS_REGS = ["spi_shader_pgm_lo_vs", "spi_shader_pgm_hi_vs", "spi_shader_pgm_rsrc1_vs",
            "spi_shader_pgm_rsrc2_vs", "spi_vs_out_config", "spi_shader_pos_format",
            "pa_cl_vs_out_cntl"]


def parse_shader(blob: bytes) -> ShaderHeader:
    """Parse an embedded .sb blob (0x24-byte binary header + 'Shdr' Gnmx header + code)."""
    if blob[0x24:0x28] != b"Shdr":
        raise GcnError("no Shdr header at +0x24")
    typ, hdw = blob[0x2C], blob[0x2D]
    base = 0x34
    w0 = struct.unpack_from("<I", blob, base)[0]
    code_size = w0 & 0x7FFFFF
    nslots = w0 >> 24
    stage = "PS" if typ == 2 else "VS"
    names = _PS_REGS if stage == "PS" else _VS_REGS
    regs = dict(zip(names, struct.unpack_from(f"<{len(names)}I", blob, base + 8)))
    p = base + 8 + 4 * len(names)
    nsem = blob[p]
    if stage == "VS":
        # VsShader: regs, then u8 numInputSemantics, u8 numExportSemantics, u8 gsModeOrNumInputSemanticsCs, u8 fetchControl
        p += 4
    else:
        p += 4
    slots = []
    for k in range(nslots):
        b = blob[p + 4 * k:p + 4 * k + 4]
        slots.append(InputSlot(b[0], b[1], b[2], b[3] & 1, struct.unpack("<I", b)[0]))
    p += 4 * nslots
    sems: List[int] = []
    if stage == "PS":
        sems = list(struct.unpack_from(f"<{nsem}H", blob, p))
    else:
        sems = list(struct.unpack_from(f"<{nsem}I", blob, p))
    code_off = 0x34 + hdw * 4
    return ShaderHeader(stage, code_size, regs, slots, sems, bytes(blob[code_off:code_off + code_size]))


# ----------------------------------------------------------------------------------------
# Interpreter
# ----------------------------------------------------------------------------------------

def make_vsharp(addr: int, num_records: int, stride: int = 0) -> List[int]:
    """Buffer resource (V#): dword0 base lo, dword1 base hi | stride << 16, dword2 records."""
    return [addr & 0xFFFFFFFF, ((addr >> 32) & 0xFFFF) | (stride << 16), num_records, 0]


def make_tsharp(tex_id: int, width: int = 1920, height: int = 1080) -> List[int]:
    """Image resource (T#) carrying an abstract texture id in dword0 (the base address)."""
    return [tex_id & 0xFFFFFFFF, 0, ((width - 1) & 0x3FFF) | (((height - 1) & 0x3FFF) << 14),
            0, 0, 0, 0, 0]


def make_ssharp(sampler_id: int = 0) -> List[int]:
    return [sampler_id, 0, 0, 0]


SampleFn = Callable[[int, int, List[np.ndarray], "Machine", Insn], np.ndarray]
FetchFn = Callable[["Machine"], None]


class Machine:
    """SIMT interpreter: one wave of `n` lanes.

    sgpr: list of 128 Python ints (104 SGPRs + specials at their operand numbers:
          106/107 VCC, 124 M0, 126/127 EXEC are kept separately as lane masks).
    vgpr: np.uint32 array (256, n).
    Memory for SMRD loads: `mem` maps a base address to a bytes object.
    """

    def __init__(self, n: int):
        self.n = n
        self.s = [0] * 128
        self.v = np.zeros((256, n), dtype=np.uint32)
        self.exec = np.ones(n, dtype=bool)
        self.vcc = np.zeros(n, dtype=bool)
        self.scc = 0
        self.m0 = 0
        self.mem: Dict[int, bytes] = {}
        self.sample: Optional[SampleFn] = None
        self.fetch: Optional[FetchFn] = None
        # interpolation parameters per (attr, chan): (P0, P10, P20), scalars or (n,) arrays
        self.interp: Dict[Tuple[int, int], Tuple[np.ndarray, np.ndarray, np.ndarray]] = {}
        self.exports: Dict[int, Tuple[np.ndarray, int, int]] = {}   # target -> (values (4,n) u32, en, compr)
        self.trace: List[str] = []
        self.lane_masks: Dict[int, np.ndarray] = {}  # sgpr index -> lane mask (for s[x:x+1] masks)
        self.samples: List[Tuple[int, List[np.ndarray]]] = []

    # ---- scalar operand access ------------------------------------------------------
    def _mask_to_int(self, m: np.ndarray) -> int:
        # lane mask -> 64-bit int (only meaningful for n <= 64; we keep masks symbolic)
        return 0

    def sget(self, src: int, lit: Optional[int]) -> int:
        if src < 104:
            return self.s[src]
        if src == 124:
            return self.m0
        if src in (106, 107, 126, 127):
            raise GcnError("scalar read of a lane mask register (use mask ops)")
        if 128 <= src <= 192:
            return src - 128
        if 193 <= src <= 208:
            return (192 - src) & 0xFFFFFFFF
        if src in INLINE_F:
            return f2u(INLINE_F[src])
        if src == 253:
            return self.scc
        if src == 255:
            assert lit is not None
            return lit
        raise GcnError(f"bad scalar source {src}")

    def sset(self, dst: int, val: int) -> None:
        if dst < 104:
            self.s[dst] = val & 0xFFFFFFFF
        elif dst == 124:
            self.m0 = val & 0xFFFFFFFF
        elif dst == 107 or dst == 106:
            # vcc_hi used as a scratch marker by psslc (s_mov_b32 vcc_hi, imm): ignore
            pass
        else:
            raise GcnError(f"bad scalar destination {dst}")

    def mask_get(self, src: int) -> np.ndarray:
        if src == 106:
            return self.vcc.copy()
        if src == 126:
            return self.exec.copy()
        if src in self.lane_masks:
            return self.lane_masks[src].copy()
        if src == 128:
            return np.zeros(self.n, dtype=bool)
        if src == 193:
            return np.ones(self.n, dtype=bool)
        raise GcnError(f"lane mask read from s{src} with no mask stored")

    def mask_set(self, dst: int, m: np.ndarray) -> None:
        m = np.asarray(m, dtype=bool)
        if dst == 106:
            self.vcc = m.copy()
        elif dst == 126:
            self.exec = m.copy()
        else:
            self.lane_masks[dst] = m.copy()

    # ---- vector operand access ------------------------------------------------------
    def vget(self, src: int, lit: Optional[int]) -> np.ndarray:
        if src >= 256:
            return self.v[src - 256].copy()
        if src in (106, 126):
            m = self.mask_get(src)
            return m.astype(np.uint32)  # lane bit as 0/1 (only used by v_cndmask with masks)
        return np.full(self.n, self.sget(src, lit), dtype=np.uint32)

    def vset(self, dst: int, val: np.ndarray, mask: Optional[np.ndarray] = None) -> None:
        m = self.exec if mask is None else mask
        self.v[dst][m] = np.asarray(val, dtype=np.uint32)[m] if np.ndim(val) else np.uint32(val)

    @staticmethod
    def F(u: np.ndarray) -> np.ndarray:
        return np.asarray(u, dtype=np.uint32).view(np.float32)

    @staticmethod
    def U(f: np.ndarray) -> np.ndarray:
        return np.asarray(f, dtype=np.float32).view(np.uint32)

    # ---- run ------------------------------------------------------------------------
    def run(self, insns: Sequence[Insn], max_steps: int = 100000) -> None:
        by_pc = {x.pc: k for k, x in enumerate(insns)}
        k = 0
        steps = 0
        with np.errstate(all="ignore"):
            while k < len(insns):
                steps += 1
                if steps > max_steps:
                    raise GcnError("step limit")
                ins = insns[k]
                nxt = self.step(ins)
                if nxt == -1:
                    return
                if nxt is not None:
                    if nxt not in by_pc:
                        raise GcnError(f"branch to 0x{nxt:x} not on an instruction")
                    k = by_pc[nxt]
                else:
                    k += 1

    def step(self, ins: Insn) -> Optional[int]:
        e = ins.enc
        if e == "SOPP":
            return self._sopp(ins)
        if e == "SOP1":
            self._sop1(ins)
        elif e == "SOP2":
            self._sop2(ins)
        elif e == "SOPK":
            self._sopk(ins)
        elif e == "SOPC":
            self._sopc(ins)
        elif e == "SMRD":
            self._smrd(ins)
        elif e == "VINTRP":
            self._vintrp(ins)
        elif e == "MIMG":
            self._mimg(ins)
        elif e == "EXP":
            self._exp(ins)
        elif e in ("VOP1", "VOP2", "VOPC", "VOP3"):
            self._valu(ins)
        else:
            raise GcnError(f"cannot execute {ins.name}")
        return None

    def _sopp(self, ins: Insn) -> Optional[int]:
        nm = ins.name
        if nm == "s_endpgm":
            return -1
        if nm in ("s_nop", "s_waitcnt", "s_barrier", "s_setprio", "s_sleep", "s_sethalt"):
            return None
        off = (ins.f["simm16"] ^ 0x8000) - 0x8000
        tgt = ins.pc + 4 + 4 * off
        take = {"s_branch": True, "s_cbranch_scc0": self.scc == 0, "s_cbranch_scc1": self.scc == 1,
                "s_cbranch_vccz": not (self.vcc & self.exec).any(),
                "s_cbranch_vccnz": bool((self.vcc & self.exec).any()),
                "s_cbranch_execz": not self.exec.any(),
                "s_cbranch_execnz": bool(self.exec.any())}.get(nm)
        if take is None:
            raise GcnError(f"unsupported {nm}")
        return tgt if take else None

    def _sop1(self, ins: Insn) -> None:
        nm, d, s = ins.name, ins.f["sdst"], ins.f["ssrc0"]
        if nm == "s_mov_b32":
            self.sset(d, self.sget(s, ins.lit))
        elif nm == "s_mov_b64":
            if s in (106, 126) or s in self.lane_masks or d in (106, 126):
                self.mask_set(d, self.mask_get(s))
            else:
                self.sset(d, self.sget(s, ins.lit))
                self.sset(d + 1, self.sget(s + 1, ins.lit) if s < 104 else 0)
        elif nm == "s_wqm_b64":
            # whole-quad mode: with independent lanes, the quad expansion is the identity
            # (all lanes of the test wave are live)
            self.mask_set(d, self.mask_get(s))
            self.scc = int(self.mask_get(d).any())
        elif nm.endswith("_saveexec_b64"):
            src = self.mask_get(s)
            old = self.exec.copy()
            self.mask_set(d, old)
            op = nm[2:].split("_saveexec")[0]
            new = {"and": src & old, "or": src | old, "xor": src ^ old,
                   "andn2": src & ~old, "orn2": src | ~old, "nand": ~(src & old),
                   "nor": ~(src | old), "xnor": ~(src ^ old)}[op]
            self.exec = new
            self.scc = int(new.any())
        elif nm == "s_not_b64":
            m = ~self.mask_get(s)
            self.mask_set(d, m)
            self.scc = int(m.any())
        elif nm == "s_swappc_b64":
            if self.fetch is None:
                raise GcnError("s_swappc_b64 without a fetch-shader stub")
            self.fetch(self)
        else:
            raise GcnError(f"unsupported {nm}")

    def _sop2(self, ins: Insn) -> None:
        nm, f = ins.name, ins.f
        if nm.endswith("_b64") and nm[2:].split("_")[0] in ("and", "or", "xor", "andn2", "orn2", "nand", "nor", "xnor"):
            a, b = self.mask_get(f["ssrc0"]), self.mask_get(f["ssrc1"])
            op = nm[2:].rsplit("_", 1)[0]
            r = {"and": a & b, "or": a | b, "xor": a ^ b, "andn2": a & ~b, "orn2": a | ~b,
                 "nand": ~(a & b), "nor": ~(a | b), "xnor": ~(a ^ b)}[op]
            self.mask_set(f["sdst"], r)
            self.scc = int(r.any())
            return
        a, b = self.sget(f["ssrc0"], ins.lit), self.sget(f["ssrc1"], ins.lit)
        sa, sb = (a ^ 0x80000000) - 0x80000000, (b ^ 0x80000000) - 0x80000000
        if nm == "s_add_u32":
            r = a + b
            self.scc = int(r > 0xFFFFFFFF)
        elif nm == "s_sub_u32":
            r = a - b
            self.scc = int(b > a)
        elif nm == "s_add_i32":
            r = sa + sb
        elif nm == "s_sub_i32":
            r = sa - sb
        elif nm == "s_addc_u32":
            r = a + b + self.scc
            self.scc = int(r > 0xFFFFFFFF)
        elif nm == "s_and_b32":
            r = a & b
            self.scc = int(r != 0)
        elif nm == "s_or_b32":
            r = a | b
            self.scc = int(r != 0)
        elif nm == "s_lshl_b32":
            r = (a << (b & 31)) & 0xFFFFFFFF
            self.scc = int(r != 0)
        elif nm == "s_lshr_b32":
            r = a >> (b & 31)
            self.scc = int(r != 0)
        elif nm == "s_mul_i32":
            r = sa * sb
        elif nm == "s_min_u32":
            r = min(a, b)
        elif nm == "s_max_u32":
            r = max(a, b)
        elif nm == "s_cselect_b32":
            r = a if self.scc else b
        elif nm == "s_bfe_u32":
            off, w = b & 31, (b >> 16) & 0x7F
            r = (a >> off) & ((1 << w) - 1) if w else 0
        else:
            raise GcnError(f"unsupported {nm}")
        self.sset(f["sdst"], r)

    def _sopk(self, ins: Insn) -> None:
        nm, f = ins.name, ins.f
        imm = (f["simm16"] ^ 0x8000) - 0x8000
        if nm == "s_movk_i32":
            self.sset(f["sdst"], imm)
        elif nm == "s_addk_i32":
            self.sset(f["sdst"], self.s[f["sdst"]] + imm)
        elif nm == "s_mulk_i32":
            self.sset(f["sdst"], self.s[f["sdst"]] * imm)
        else:
            raise GcnError(f"unsupported {nm}")

    def _sopc(self, ins: Insn) -> None:
        a, b = self.sget(ins.f["ssrc0"], ins.lit), self.sget(ins.f["ssrc1"], ins.lit)
        cmp = ins.name.split("_")[2]
        if ins.name.endswith("i32"):
            a, b = (a ^ 0x80000000) - 0x80000000, (b ^ 0x80000000) - 0x80000000
        self.scc = int({"eq": a == b, "lg": a != b, "gt": a > b, "ge": a >= b, "lt": a < b,
                        "le": a <= b}[cmp])

    def _smrd(self, ins: Insn) -> None:
        f = ins.f
        if ins.name in ("s_dcache_inv",):
            return
        cnt = SMRD_COUNT[ins.op]
        off = f["offset"] * 4 if f["imm"] else self.sget(f["offset"], ins.lit)
        sb = f["sbase"]
        if ins.op >= 8:      # buffer load through a V# in s[sbase:sbase+3]
            addr = self.s[sb] | ((self.s[sb + 1] & 0xFFFF) << 32)
        else:
            addr = self.s[sb] | (self.s[sb + 1] << 32)
        data = self.mem.get(addr)
        if data is None:
            raise GcnError(f"{ins.name}: no memory at 0x{addr:x}")
        for k in range(cnt):
            o = off + 4 * k
            val = struct.unpack_from("<I", data, o)[0] if o + 4 <= len(data) else 0
            self.sset(f["sdst"] + k, val)

    def _vintrp(self, ins: Insn) -> None:
        f = ins.f
        key = (f["attr"], f["chan"])
        if key not in self.interp:
            raise GcnError(f"no interpolant data for attr{key[0]}.{'xyzw'[key[1]]}")
        p0, p10, p20 = (np.broadcast_to(np.asarray(x, dtype=np.float32), (self.n,)) for x in self.interp[key])
        if ins.name == "v_interp_p1_f32":
            i = self.F(self.v[f["vsrc"]])
            r = (p10 * i + p0).astype(np.float32)
        elif ins.name == "v_interp_p2_f32":
            j = self.F(self.v[f["vsrc"]])
            r = (p20 * j + self.F(self.v[f["vdst"]])).astype(np.float32)
        else:
            sel = f["vsrc"] & 3   # v_interp_mov: 0 = P10, 1 = P20, 2 = P0
            r = {0: p10, 1: p20, 2: p0}[sel].astype(np.float32)
        self.vset(f["vdst"], self.U(r))

    def _mimg(self, ins: Insn) -> None:
        f = ins.f
        if self.sample is None:
            raise GcnError("image op without a sampler callback")
        tid = self.s[f["srsrc"]]
        sid = self.s[f["ssamp"]]
        naddr = {"image_sample": 2, "image_sample_lz": 2, "image_sample_l": 3, "image_sample_b": 3}.get(ins.name, 2)
        if ins.name == "image_sample_b":
            coords = [self.F(self.v[f["vaddr"] + k]) for k in range(naddr)]
        else:
            coords = [self.F(self.v[f["vaddr"] + k]) for k in range(naddr)]
        self.samples.append((tid, coords))
        texel = np.asarray(self.sample(tid, sid, coords, self, ins), dtype=np.float32)   # (4, n)
        d = f["vdata"]
        for c in range(4):
            if (f["dmask"] >> c) & 1:
                self.vset(d, self.U(texel[c]))
                d += 1

    def _exp(self, ins: Insn) -> None:
        f = ins.f
        vals = np.zeros((4, self.n), dtype=np.uint32)
        for k in range(4):
            if (f["en"] >> k) & 1:
                vals[k] = self.v[f[f"vsrc{k}"]]
        self.exports[f["target"]] = (vals, f["en"], f["compr"])

    # ---- VALU -----------------------------------------------------------------------
    def _valu(self, ins: Insn) -> None:
        f, e, nm = ins.f, ins.enc, ins.name
        F, U = self.F, self.U
        if e == "VOP3":
            srcs = [self.vget(f[f"src{k}"], ins.lit) for k in range(3)]
            absm, negm = f["abs"], f["neg"]
        elif e == "VOP2":
            srcs = [self.vget(f["src0"], ins.lit), self.v[f["vsrc1"]].copy(), np.zeros(self.n, np.uint32)]
            absm = negm = 0
        elif e == "VOPC":
            srcs = [self.vget(f["src0"], ins.lit), self.v[f["vsrc1"]].copy(), np.zeros(self.n, np.uint32)]
            absm = negm = 0
        else:
            srcs = [self.vget(f["src0"], ins.lit), np.zeros(self.n, np.uint32), np.zeros(self.n, np.uint32)]
            absm = negm = 0

        def fl(k: int) -> np.ndarray:
            x = F(srcs[k]).astype(np.float32)
            if (absm >> k) & 1:
                x = np.abs(x)
            if (negm >> k) & 1:
                x = -x
            return x

        def out_f(r: np.ndarray) -> np.ndarray:
            r = np.asarray(r, dtype=np.float32)
            if e == "VOP3":
                om = f["omod"]
                if om == 1:
                    r = r * np.float32(2)
                elif om == 2:
                    r = r * np.float32(4)
                elif om == 3:
                    r = r * np.float32(0.5)
                if f["clamp"]:
                    r = np.where(np.isnan(r), np.float32(0), np.clip(r, 0, 1)).astype(np.float32)
            return U(r)

        # ---- compares -> lane mask
        if nm.startswith("v_cmp"):
            cmpx = nm.startswith("v_cmpx")
            parts = nm.split("_")
            c, ty = parts[2], parts[3]
            if c == "class":
                raise GcnError("v_cmp_class not supported")
            if ty == "f32":
                a, b = fl(0), fl(1)
                nan = np.isnan(a) | np.isnan(b)
                r = {"f": np.zeros(self.n, bool), "lt": a < b, "eq": a == b, "le": a <= b,
                     "gt": a > b, "lg": (a < b) | (a > b), "ge": a >= b, "o": ~nan, "u": nan,
                     "nge": ~(a >= b), "nlg": ~((a < b) | (a > b)), "ngt": ~(a > b),
                     "nle": ~(a <= b), "neq": ~(a == b), "nlt": ~(a < b),
                     "tru": np.ones(self.n, bool)}[c]
            else:
                a, b = srcs[0], srcs[1]
                if ty == "i32":
                    a, b = a.view(np.int32), b.view(np.int32)
                r = {"f": np.zeros(self.n, bool), "lt": a < b, "eq": a == b, "le": a <= b,
                     "gt": a > b, "ne": a != b, "ge": a >= b, "t": np.ones(self.n, bool)}[c]
            r = r & self.exec
            dst = 106 if e == "VOPC" else f["vdst"]
            self.mask_set(dst, r)
            if cmpx:
                self.exec = r.copy()
            return

        d = f["vdst"]
        if nm == "v_mov_b32":
            self.vset(d, srcs[0] if e != "VOP3" else out_f(fl(0)) if (absm or negm or f["clamp"] or f["omod"]) else srcs[0])
        elif nm == "v_cndmask_b32":
            m = self.vcc if e == "VOP2" else self.mask_get(f["src2"])
            a = srcs[0] if e == "VOP2" else (out_f(fl(0)) if (absm or negm) & 1 else srcs[0])
            b = srcs[1] if e == "VOP2" else (out_f(fl(1)) if (absm | negm) & 2 else srcs[1])
            self.vset(d, np.where(m, b, a))
        elif nm == "v_add_f32":
            self.vset(d, out_f(fl(0) + fl(1)))
        elif nm == "v_sub_f32":
            self.vset(d, out_f(fl(0) - fl(1)))
        elif nm == "v_subrev_f32":
            self.vset(d, out_f(fl(1) - fl(0)))
        elif nm == "v_mul_f32":
            self.vset(d, out_f(fl(0) * fl(1)))
        elif nm == "v_mul_legacy_f32":
            a, b = fl(0), fl(1)
            r = np.where((a == 0) | (b == 0), np.float32(0), a * b)
            self.vset(d, out_f(r))
        elif nm in ("v_mac_f32", "v_mac_legacy_f32"):
            a, b = fl(0), fl(1)
            acc = F(self.v[d]).astype(np.float32)
            prod = a * b
            if nm == "v_mac_legacy_f32":
                prod = np.where((a == 0) | (b == 0), np.float32(0), prod)
            self.vset(d, out_f(prod + acc))
        elif nm in ("v_mad_f32", "v_mad_legacy_f32", "v_fma_f32"):
            a, b, c = fl(0), fl(1), fl(2)
            prod = a * b
            if nm == "v_mad_legacy_f32":
                prod = np.where((a == 0) | (b == 0), np.float32(0), prod)
            if nm == "v_fma_f32":
                r = (a.astype(np.float64) * b.astype(np.float64) + c.astype(np.float64)).astype(np.float32)
            else:
                r = prod + c
            self.vset(d, out_f(r))
        elif nm == "v_madmk_f32":
            k = np.float32(u2f(ins.lit or 0))
            self.vset(d, out_f(fl(0) * k + F(srcs[1])))
        elif nm == "v_madak_f32":
            k = np.float32(u2f(ins.lit or 0))
            self.vset(d, out_f(fl(0) * F(srcs[1]) + k))
        elif nm in ("v_min_f32", "v_min_legacy_f32"):
            self.vset(d, out_f(np.fmin(fl(0), fl(1)) if nm == "v_min_f32" else np.where(fl(0) < fl(1), fl(0), fl(1))))
        elif nm in ("v_max_f32", "v_max_legacy_f32"):
            self.vset(d, out_f(np.fmax(fl(0), fl(1)) if nm == "v_max_f32" else np.where(fl(0) >= fl(1), fl(0), fl(1))))
        elif nm == "v_min3_f32":
            self.vset(d, out_f(np.fmin(np.fmin(fl(0), fl(1)), fl(2))))
        elif nm == "v_max3_f32":
            self.vset(d, out_f(np.fmax(np.fmax(fl(0), fl(1)), fl(2))))
        elif nm == "v_med3_f32":
            a, b, c = fl(0), fl(1), fl(2)
            self.vset(d, out_f(np.fmax(np.fmin(a, b), np.fmin(np.fmax(a, b), c))))
        elif nm == "v_rcp_f32" or nm == "v_rcp_legacy_f32" or nm == "v_rcp_iflag_f32":
            self.vset(d, out_f(np.float32(1) / fl(0)))
        elif nm == "v_rcp_clamp_f32":
            r = np.float32(1) / fl(0)
            r = np.where(np.isinf(r), np.sign(r) * np.float32(3.4028235e38), r)
            self.vset(d, out_f(r))
        elif nm in ("v_rsq_f32", "v_rsq_legacy_f32", "v_rsq_clamp_f32"):
            self.vset(d, out_f(np.float32(1) / np.sqrt(fl(0))))
        elif nm == "v_sqrt_f32":
            self.vset(d, out_f(np.sqrt(fl(0))))
        elif nm == "v_exp_f32":
            self.vset(d, out_f(np.exp2(fl(0))))
        elif nm in ("v_log_f32", "v_log_clamp_f32"):
            self.vset(d, out_f(np.log2(fl(0))))
        elif nm == "v_fract_f32":
            x = fl(0)
            self.vset(d, out_f(x - np.floor(x)))
        elif nm == "v_floor_f32":
            self.vset(d, out_f(np.floor(fl(0))))
        elif nm == "v_ceil_f32":
            self.vset(d, out_f(np.ceil(fl(0))))
        elif nm == "v_trunc_f32":
            self.vset(d, out_f(np.trunc(fl(0))))
        elif nm == "v_rndne_f32":
            self.vset(d, out_f(np.rint(fl(0))))
        elif nm == "v_sin_f32":
            self.vset(d, out_f(np.sin(fl(0) * np.float32(2 * np.pi))))
        elif nm == "v_cos_f32":
            self.vset(d, out_f(np.cos(fl(0) * np.float32(2 * np.pi))))
        elif nm == "v_ldexp_f32":
            self.vset(d, out_f(np.ldexp(fl(0), srcs[1].view(np.int32))))
        elif nm == "v_cvt_f32_i32":
            self.vset(d, out_f(srcs[0].view(np.int32).astype(np.float32)))
        elif nm == "v_cvt_f32_u32":
            self.vset(d, out_f(srcs[0].astype(np.float32)))
        elif nm in ("v_cvt_u32_f32", "v_cvt_i32_f32"):
            x = fl(0)
            if nm == "v_cvt_u32_f32":
                r = np.clip(np.nan_to_num(np.trunc(x)), 0, 4294967295).astype(np.uint64).astype(np.uint32)
            else:
                r = np.clip(np.nan_to_num(np.trunc(x)), -2147483648, 2147483647).astype(np.int64).astype(np.int32).view(np.uint32)
            self.vset(d, r)
        elif nm.startswith("v_cvt_f32_ubyte"):
            k = int(nm[-1])
            self.vset(d, out_f(((srcs[0] >> (8 * k)) & 0xFF).astype(np.float32)))
        elif nm == "v_cvt_pkrtz_f16_f32":
            a, b = fl(0), fl(1)
            self.vset(d, _f16_rtz(a).astype(np.uint32) | (_f16_rtz(b).astype(np.uint32) << 16))
        elif nm == "v_cvt_f32_f16":
            self.vset(d, out_f((srcs[0] & 0xFFFF).astype(np.uint16).view(np.float16).astype(np.float32)))
        elif nm == "v_cvt_f16_f32":
            self.vset(d, fl(0).astype(np.float16).view(np.uint16).astype(np.uint32))
        elif nm == "v_cvt_pk_u8_f32":
            x = np.clip(np.rint(fl(0)), 0, 255).astype(np.uint32)
            sh = (srcs[1] & 3) * 8
            r = (srcs[2] & ~(np.uint32(0xFF) << sh)) | (x << sh)
            self.vset(d, r)
        elif nm in ("v_and_b32", "v_or_b32", "v_xor_b32"):
            a, b = srcs[0], srcs[1]
            self.vset(d, {"v_and_b32": a & b, "v_or_b32": a | b, "v_xor_b32": a ^ b}[nm])
        elif nm == "v_not_b32":
            self.vset(d, ~srcs[0])
        elif nm in ("v_lshlrev_b32", "v_lshrrev_b32", "v_ashrrev_i32"):
            sh, x = srcs[0] & 31, srcs[1]
            r = {"v_lshlrev_b32": x << sh, "v_lshrrev_b32": x >> sh,
                 "v_ashrrev_i32": (x.view(np.int32) >> sh.astype(np.int32)).view(np.uint32)}[nm]
            self.vset(d, r)
        elif nm in ("v_lshl_b32", "v_lshr_b32", "v_ashr_i32"):
            x, sh = srcs[0], srcs[1] & 31
            r = {"v_lshl_b32": x << sh, "v_lshr_b32": x >> sh,
                 "v_ashr_i32": (x.view(np.int32) >> sh.astype(np.int32)).view(np.uint32)}[nm]
            self.vset(d, r)
        elif nm in ("v_add_i32", "v_sub_i32", "v_subrev_i32"):
            a, b = srcs[0].astype(np.uint64), srcs[1].astype(np.uint64)
            r = {"v_add_i32": a + b, "v_sub_i32": a - b, "v_subrev_i32": b - a}[nm]
            self.vset(d, (r & 0xFFFFFFFF).astype(np.uint32))
        elif nm in ("v_mul_u32_u24", "v_mul_i32_i24", "v_mul_lo_u32", "v_mul_lo_i32"):
            a, b = srcs[0].astype(np.uint64), srcs[1].astype(np.uint64)
            if nm.endswith("24"):
                a, b = a & 0xFFFFFF, b & 0xFFFFFF
            self.vset(d, ((a * b) & 0xFFFFFFFF).astype(np.uint32))
        elif nm in ("v_bfe_u32",):
            off, w = srcs[1] & 31, srcs[2] & 31
            self.vset(d, (srcs[0] >> off) & ((np.uint32(1) << w) - np.uint32(1)))
        elif nm in ("v_min_u32", "v_max_u32", "v_min_i32", "v_max_i32"):
            a, b = srcs[0], srcs[1]
            if nm.endswith("i32"):
                a, b = a.view(np.int32), b.view(np.int32)
            r = np.minimum(a, b) if "min" in nm else np.maximum(a, b)
            self.vset(d, r.view(np.uint32) if r.dtype == np.int32 else r)
        else:
            raise GcnError(f"unsupported VALU op {nm}")


def _f16_rtz(x: np.ndarray) -> np.ndarray:
    """float32 -> float16 bits with round-toward-zero (v_cvt_pkrtz_f16_f32)."""
    x = np.asarray(x, dtype=np.float32)
    h = x.astype(np.float16)
    # astype rounds to nearest; step one ulp toward zero where that rounded away
    hf = h.astype(np.float32)
    away = np.abs(hf) > np.abs(x)
    hb = h.view(np.uint16).astype(np.int32)
    hb = np.where(away & np.isfinite(x), hb - 1, hb)
    # overflow (inf from rounding a finite value): RTZ gives max finite
    big = np.isinf(hf) & np.isfinite(x)
    hb = np.where(big, np.where(x > 0, 0x7BFF, 0xFBFF), hb)
    return hb.astype(np.uint16)


def f16_to_f32(h: np.ndarray) -> np.ndarray:
    return np.asarray(h, dtype=np.uint16).view(np.float16).astype(np.float32)


def _main(argv: Sequence[str]) -> int:
    import argparse
    from pathlib import Path

    ap = argparse.ArgumentParser(description="Disassemble embedded PSSL .sb shader blobs")
    ap.add_argument("files", nargs="+")
    ap.add_argument("--raw", action="store_true", help="input is raw GCN code")
    a = ap.parse_args(list(argv))
    for fn in a.files:
        data = Path(fn).read_bytes()
        print(f"== {fn}")
        if a.raw:
            print(disasm(decode(data)))
            continue
        h = parse_shader(data)
        print(f"stage {h.stage}, code {h.code_size} bytes, user SGPRs {h.user_sgprs}")
        for k, v in h.regs.items():
            print(f"  {k} = 0x{v:x}")
        for sl in h.slots:
            print(f"  slot {sl.name} api {sl.api_slot} -> s[{sl.start_reg}:{sl.start_reg + sl.dwords - 1}]")
        print(f"  semantics {[hex(x) for x in h.semantics]}")
        print(disasm(decode(h.code)))
    return 0


if __name__ == "__main__":
    sys.exit(_main(sys.argv[1:]))
