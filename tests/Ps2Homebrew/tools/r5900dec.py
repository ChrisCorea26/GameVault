#!/usr/bin/env python3
"""GameVault PS2 homebrew: a small R5900 (EE) and VU micro-instruction decoder. GameVault's own code.

Written from ps2tek's "EE Instruction Decoding" tables (normal, SPECIAL, REGIMM, MMI, MMI0-3, COP0,
COP1, COP2 Special1/Special2) and, for VU micro mode, Play!'s decoder tables (Source/ee/MA_VU_Upper.cpp,
MA_VU_Lower.cpp, cross-checked with ps2tek's COP2 Special tables, which use the same encodings).

  r5900dec.py --check prog.elf   decodes every instruction of the ELF's code sections and fails on any
                                 word that isn't a valid R5900 instruction (e.g. a MIPS32 MUL or LL/SC);
                                 prints the R5900-only instructions (LQ/SQ, MMI, FPU ACC ops, 64-bit
                                 ops); decodes VU microcode between symbols NAME and NAME_end for every
                                 symbol whose name starts with "vu1_code" and fails on illegal VU words.
  r5900dec.py --word 0x7c880000  decodes one EE word.
  r5900dec.py --vu LOWER UPPER   decodes one VU instruction pair.
Only the standard library is used; run it with python3 -I.
"""
import argparse
import struct
import subprocess
import sys

GPR = ["zero", "at", "v0", "v1", "a0", "a1", "a2", "a3", "t0", "t1", "t2", "t3", "t4", "t5", "t6", "t7",
       "s0", "s1", "s2", "s3", "s4", "s5", "s6", "s7", "t8", "t9", "k0", "k1", "gp", "sp", "fp", "ra"]

# ---- ps2tek "Normal instructions" (opcode bits 26-31); None = "---" ----
NORMAL = [
    "*SPECIAL", "*REGIMM", "J", "JAL", "BEQ", "BNE", "BLEZ", "BGTZ",
    "ADDI", "ADDIU", "SLTI", "SLTIU", "ANDI", "ORI", "XORI", "LUI",
    "*COP0", "*COP1", "*COP2", None, "BEQL", "BNEL", "BLEZL", "BGTZL",
    "DADDI", "DADDIU", "LDL", "LDR", "*MMI", None, "LQ", "SQ",
    "LB", "LH", "LWL", "LW", "LBU", "LHU", "LWR", "LWU",
    "SB", "SH", "SWL", "SW", "SDL", "SDR", "SWR", "CACHE",
    None, "LWC1", None, "PREF", None, None, "LQC2", "LD",
    None, "SWC1", None, None, None, None, "SQC2", "SD",
]
SPECIAL = [
    "SLL", None, "SRL", "SRA", "SLLV", None, "SRLV", "SRAV",
    "JR", "JALR", "MOVZ", "MOVN", "SYSCALL", "BREAK", None, "SYNC",
    "MFHI", "MTHI", "MFLO", "MTLO", "DSLLV", None, "DSRLV", "DSRAV",
    "MULT", "MULTU", "DIV", "DIVU", None, None, None, None,
    "ADD", "ADDU", "SUB", "SUBU", "AND", "OR", "XOR", "NOR",
    "MFSA", "MTSA", "SLT", "SLTU", "DADD", "DADDU", "DSUB", "DSUBU",
    "TGE", "TGEU", "TLT", "TLTU", "TEQ", None, "TNE", None,
    "DSLL", None, "DSRL", "DSRA", "DSLL32", None, "DSRL32", "DSRA32",
]
REGIMM = [
    "BLTZ", "BGEZ", "BLTZL", "BGEZL", None, None, None, None,
    "TGEI", "TGEIU", "TLTI", "TLTIU", "TEQI", None, "TNEI", None,
    "BLTZAL", "BGEZAL", "BLTZALL", "BGEZALL", None, None, None, None,
    "MTSAB", "MTSAH", None, None, None, None, None, None,
]
MMI = [
    "MADD", "MADDU", None, None, "PLZCW", None, None, None,
    "*MMI0", "*MMI2", None, None, None, None, None, None,
    "MFHI1", "MTHI1", "MFLO1", "MTLO1", None, None, None, None,
    "MULT1", "MULTU1", "DIV1", "DIVU1", None, None, None, None,
    "MADD1", "MADDU1", None, None, None, None, None, None,
    "*MMI1", "*MMI3", None, None, None, None, None, None,
    "PMFHL", "PMTHL", None, None, "PSLLH", None, "PSRLH", "PSRAH",
    None, None, None, None, "PSLLW", None, "PSRLW", "PSRAW",
]
MMI0 = [
    "PADDW", "PSUBW", "PCGTW", "PMAXW", "PADDH", "PSUBH", "PCGTH", "PMAXH",
    "PADDB", "PSUBB", "PCGTB", None, None, None, None, None,
    "PADDSW", "PSUBSW", "PEXTLW", "PPACW", "PADDSH", "PSUBSH", "PEXTLH", "PPACH",
    "PADDSB", "PSUBSB", "PEXTLB", "PPACB", None, None, "PEXT5", "PPAC5",
]
MMI1 = [
    None, "PABSW", "PCEQW", "PMINW", "PADSBH", "PABSH", "PCEQH", "PMINH",
    None, None, "PCEQB", None, None, None, None, None,
    "PADDUW", "PSUBUW", "PEXTUW", None, "PADDUH", "PSUBUH", "PEXTUH", None,
    "PADDUB", "PSUBUB", "PEXTUB", "QFSRV", None, None, None, None,
]
MMI2 = [
    "PMADDW", None, "PSLLVW", "PSRLVW", "PMSUBW", None, None, None,
    "PMFHI", "PMFLO", "PINTH", None, "PMULTW", "PDIVW", "PCPYLD", None,
    "PMADDH", "PHMADH", "PAND", "PXOR", "PMSUBH", "PHMSBH", None, None,
    None, None, "PEXEH", "PREVH", "PMULTH", "PDIVBW", "PEXEW", "PROT3W",
]
MMI3 = [
    "PMADDUW", None, None, "PSRAVW", None, None, None, None,
    "PMTHI", "PMTLO", "PINTEH", None, "PMULTUW", "PDIVUW", "PCPYUD", None,
    None, None, "POR", "PNOR", None, None, None, None,
    None, None, "PEXCH", "PCPYH", None, None, "PEXCW", None,
]
COP0_RS = {0x00: "MFC0", 0x04: "MTC0", 0x08: "*BC0", 0x10: "*TLB"}
BC0 = {0: "BC0F", 1: "BC0T", 2: "BC0FL", 3: "BC0TL"}
TLB = {0x01: "TLBR", 0x02: "TLBWI", 0x06: "TLBWR", 0x08: "TLBP", 0x18: "ERET", 0x38: "EI", 0x39: "DI"}
COP1_RS = {0x00: "MFC1", 0x02: "CFC1", 0x04: "MTC1", 0x06: "CTC1", 0x08: "*BC1", 0x10: "*S", 0x14: "*W"}
BC1 = {0: "BC1F", 1: "BC1T", 2: "BC1FL", 3: "BC1TL"}
FPU_S = {
    0x00: "ADD.S", 0x01: "SUB.S", 0x02: "MUL.S", 0x03: "DIV.S", 0x04: "SQRT.S", 0x05: "ABS.S", 0x06: "MOV.S",
    0x07: "NEG.S", 0x16: "RSQRT.S", 0x18: "ADDA.S", 0x19: "SUBA.S", 0x1A: "MULA.S", 0x1C: "MADD.S",
    0x1D: "MSUB.S", 0x1E: "MADDA.S", 0x1F: "MSUBA.S", 0x24: "CVT.W.S", 0x28: "MAX.S", 0x29: "MIN.S",
    0x30: "C.F.S", 0x32: "C.EQ.S", 0x34: "C.LT.S", 0x36: "C.LE.S",
}
FPU_W = {0x20: "CVT.S.W"}
COP2_RS = {0x01: "QMFC2", 0x02: "CFC2", 0x05: "QMTC2", 0x06: "CTC2", 0x08: "*BC2"}

# Instructions the R5900 has that a MIPS II/III assembler doesn't (printed by --check).
R5900_ONLY = {"LQ", "SQ", "MFSA", "MTSA", "MTSAB", "MTSAH", "EI", "DI", "LQC2", "SQC2"} | \
    {n for t in (MMI, MMI0, MMI1, MMI2, MMI3) for n in t if n and not n.startswith("*")} | \
    {"ADDA.S", "SUBA.S", "MULA.S", "MADD.S", "MSUB.S", "MADDA.S", "MSUBA.S", "MAX.S", "MIN.S", "RSQRT.S"}


def bits(w, lo, n):
    return (w >> lo) & ((1 << n) - 1)


def simm16(w):
    v = w & 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def decode_ee(w, pc=0):
    """Return (mnemonic, operands) or (None, reason) for an invalid word."""
    op, rs, rt, rd, sa, fn = bits(w, 26, 6), bits(w, 21, 5), bits(w, 16, 5), bits(w, 11, 5), bits(w, 6, 5), bits(w, 0, 6)
    name = NORMAL[op]
    if name is None:
        return None, "reserved opcode %02Xh" % op
    if name == "*SPECIAL":
        n = SPECIAL[fn]
        if n is None:
            return None, "reserved SPECIAL function %02Xh" % fn
        if n in ("SLL", "SRL", "SRA", "DSLL", "DSRL", "DSRA", "DSLL32", "DSRL32", "DSRA32"):
            if w == 0:
                return "NOP", ""
            return n, "$%s, $%s, %d" % (GPR[rd], GPR[rt], sa)
        if n in ("JR",):
            return n, "$%s" % GPR[rs]
        if n in ("MFHI", "MFLO", "MFSA"):
            return n, "$%s" % GPR[rd]
        if n in ("MULT", "MULTU", "DIV", "DIVU"):
            return n, "$%s, $%s, $%s" % (GPR[rd], GPR[rs], GPR[rt])
        if n in ("SYNC", "SYSCALL", "BREAK"):
            return n, ""
        return n, "$%s, $%s, $%s" % (GPR[rd], GPR[rs], GPR[rt])
    if name == "*REGIMM":
        n = REGIMM[rt]
        if n is None:
            return None, "reserved REGIMM %02Xh" % rt
        return n, "$%s, %d" % (GPR[rs], simm16(w))
    if name == "*MMI":
        n = MMI[fn]
        if n is None:
            return None, "reserved MMI function %02Xh" % fn
        if n.startswith("*MMI"):
            table = {"*MMI0": MMI0, "*MMI1": MMI1, "*MMI2": MMI2, "*MMI3": MMI3}[n]
            n = table[sa]
            if n is None:
                return None, "reserved %s sub-op %02Xh" % (MMI[fn][1:], sa)
        if n in ("MFLO1", "MFHI1"):
            return n, "$%s" % GPR[rd]
        if n in ("PABSW", "PABSH", "PCPYH", "PEXEH", "PEXEW", "PREVH", "PROT3W", "PEXCH", "PEXCW"):
            return n, "$%s, $%s" % (GPR[rd], GPR[rt])
        if n == "PLZCW":
            return n, "$%s, $%s" % (GPR[rd], GPR[rs])
        return n, "$%s, $%s, $%s" % (GPR[rd], GPR[rs], GPR[rt])
    if name == "*COP0":
        n = COP0_RS.get(rs)
        if n is None:
            return None, "reserved COP0 fmt %02Xh" % rs
        if n == "*BC0":
            n = BC0.get(rt)
            return (n, "%d" % simm16(w)) if n else (None, "reserved BC0")
        if n == "*TLB":
            n = TLB.get(fn)
            return (n, "") if n else (None, "reserved COP0 TLB function %02Xh" % fn)
        return n, "$%s, $%d" % (GPR[rt], rd)
    if name == "*COP1":
        n = COP1_RS.get(rs)
        if n is None:
            return None, "reserved COP1 fmt %02Xh" % rs
        if n == "*BC1":
            n = BC1.get(rt)
            return (n, "%d" % simm16(w)) if n else (None, "reserved BC1")
        if n in ("*S", "*W"):
            n = (FPU_S if n == "*S" else FPU_W).get(fn)
            if n is None:
                return None, "reserved FPU function %02Xh" % fn
            fd, fs, ft = sa, rd, rt
            if n in ("ADDA.S", "SUBA.S", "MULA.S", "MADDA.S", "MSUBA.S") or n.startswith("C."):
                return n, "$f%d, $f%d" % (fs, ft)
            return n, "$f%d, $f%d, $f%d" % (fd, fs, ft)
        return n, "$%s, $f%d" % (GPR[rt], rd)
    if name == "*COP2":
        n = COP2_RS.get(rs)
        if rs >= 0x10:
            return "VU0-macro", "%08X" % w
        if n is None:
            return None, "reserved COP2 fmt %02Xh" % rs
        return n, "$%s, $%d" % (GPR[rt], rd)
    if name in ("J", "JAL"):
        return name, "0x%08X" % (((pc + 4) & 0xF0000000) | (bits(w, 0, 26) << 2))
    if name in ("BEQ", "BNE", "BEQL", "BNEL"):
        return name, "$%s, $%s, 0x%08X" % (GPR[rs], GPR[rt], (pc + 4 + 4 * simm16(w)) & 0xFFFFFFFF)
    if name in ("BLEZ", "BGTZ", "BLEZL", "BGTZL"):
        return name, "$%s, 0x%08X" % (GPR[rs], (pc + 4 + 4 * simm16(w)) & 0xFFFFFFFF)
    if name == "LUI":
        return name, "$%s, 0x%04X" % (GPR[rt], w & 0xFFFF)
    if name in ("ADDI", "ADDIU", "SLTI", "SLTIU", "DADDI", "DADDIU"):
        return name, "$%s, $%s, %d" % (GPR[rt], GPR[rs], simm16(w))
    if name in ("ANDI", "ORI", "XORI"):
        return name, "$%s, $%s, 0x%04X" % (GPR[rt], GPR[rs], w & 0xFFFF)
    if name in ("LWC1", "SWC1"):
        return name, "$f%d, %d($%s)" % (rt, simm16(w), GPR[rs])
    if name in ("LQC2", "SQC2"):
        return name, "$vf%d, %d($%s)" % (rt, simm16(w), GPR[rs])
    if name in ("CACHE", "PREF"):
        return name, "%d, %d($%s)" % (rt, simm16(w), GPR[rs])
    return name, "$%s, %d($%s)" % (GPR[rt], simm16(w), GPR[rs])


# ---- VU micro mode (Play! MA_VU_Upper.cpp / MA_VU_Lower.cpp; ps2tek COP2 Special1/Special2) ----
BC = "xyzw"
UPPER_OP = (["ADD" + c for c in BC] + ["SUB" + c for c in BC] + ["MADD" + c for c in BC] + ["MSUB" + c for c in BC] +
            ["MAX" + c for c in BC] + ["MINI" + c for c in BC] + ["MUL" + c for c in BC] +
            ["MULq", "MAXi", "MULi", "MINIi", "ADDq", "MADDq", "ADDi", "MADDi", "SUBq", "MSUBq", "SUBi", "MSUBi",
             "ADD", "MADD", "MUL", "MAX", "SUB", "MSUB", "OPMSUB", "MINI"] + [None] * 12 + ["*S2"] * 4)
UPPER_S2 = (["ADDA" + c for c in BC] + ["SUBA" + c for c in BC] + ["MADDA" + c for c in BC] + ["MSUBA" + c for c in BC] +
            ["ITOF0", "ITOF4", "ITOF12", "ITOF15", "FTOI0", "FTOI4", "FTOI12", "FTOI15"] +
            ["MULA" + c for c in BC] + ["MULAq", "ABS", "MULAi", "CLIPw"] +
            ["ADDAq", "MADDAq", "ADDAi", "MADDAi", "SUBAq", "MSUBAq", "SUBAi", "MSUBAi",
             "ADDA", "MADDA", "MULA", None, "SUBA", "MSUBA", "OPMULA", "NOP"])
LOWER_GEN = {0x00: "LQ", 0x01: "SQ", 0x04: "ILW", 0x05: "ISW", 0x08: "IADDIU", 0x09: "ISUBIU", 0x10: "FCEQ",
             0x11: "FCSET", 0x12: "FCAND", 0x13: "FCOR", 0x15: "FSSET", 0x16: "FSAND", 0x17: "FSOR", 0x18: "FMEQ",
             0x1A: "FMAND", 0x1B: "FMOR", 0x1C: "FCGET", 0x20: "B", 0x21: "BAL", 0x24: "JR", 0x25: "JALR",
             0x28: "IBEQ", 0x29: "IBNE", 0x2C: "IBLTZ", 0x2D: "IBGTZ", 0x2E: "IBLEZ", 0x2F: "IBGEZ", 0x40: "*OP"}
LOWER_OP = {0x30: "IADD", 0x31: "ISUB", 0x32: "IADDI", 0x34: "IAND", 0x35: "IOR",
            0x3C: "*S2", 0x3D: "*S2", 0x3E: "*S2", 0x3F: "*S2"}
LOWER_S2 = {48: "MOVE", 49: "MR32", 52: "LQI", 53: "SQI", 54: "LQD", 55: "SQD", 56: "DIV", 57: "SQRT",
            58: "RSQRT", 59: "WAITQ", 60: "MTIR", 61: "MFIR", 62: "ILWR", 63: "ISWR", 64: "RNEXT", 65: "RGET",
            66: "RINIT", 67: "RXOR", 100: "MFP", 104: "XTOP", 105: "XITOP", 108: "XGKICK", 112: "ESADD",
            113: "ERSADD", 114: "ELENG", 115: "ERLENG", 116: "EATANxy", 117: "EATANxz", 118: "ESUM",
            120: "ESQRT", 121: "ERSQRT", 122: "ERCPR", 124: "ESIN", 126: "EEXP", 123: "WAITP"}


def dest_str(w):
    d = bits(w, 21, 4)
    return "".join(c for c, b in zip("xyzw", (8, 4, 2, 1)) if d & b)


def simm(v, n):
    return v - (1 << n) if v & (1 << (n - 1)) else v


def decode_vu_upper(w):
    flags = "".join(f for f, b in zip(("[I]", "[E]", "[M]", "[D]", "[T]"), (31, 30, 29, 28, 27)) if w & (1 << b))
    op = bits(w, 0, 6)
    ft, fs, fd, bc = bits(w, 16, 5), bits(w, 11, 5), bits(w, 6, 5), bits(w, 0, 2)
    n = UPPER_OP[op]
    if n is None:
        return None, "illegal upper op %02Xh" % op
    if n == "*S2":
        n = UPPER_S2[(bits(w, 6, 5) << 2) | bc] if ((bits(w, 6, 5) << 2) | bc) < len(UPPER_S2) else None
        if n is None:
            return None, "illegal upper special2 %03Xh" % ((bits(w, 6, 5) << 2) | bc)
        if n == "NOP":
            return "NOP" + flags, ""
        d = dest_str(w)
        if n.startswith(("ITOF", "FTOI", "ABS")):
            return "%s.%s%s" % (n, d, flags), "vf%d, vf%d" % (ft, fs)
        if n == "CLIPw":
            return "CLIPw.xyz" + flags, "vf%d, vf%dw" % (fs, ft)
        if n[-1] in BC and not n.endswith(("ADDA", "SUBA", "MULA", "MADDA", "MSUBA")):
            return "%s.%s%s" % (n, d, flags), "ACC, vf%d, vf%d%s" % (fs, ft, n[-1])
        if n[-1] in "qi":
            return "%s.%s%s" % (n, d, flags), "ACC, vf%d, %s" % (fs, n[-1].upper())
        return "%s.%s%s" % (n, d, flags), "ACC, vf%d, vf%d" % (fs, ft)
    d = dest_str(w)
    if n[-1] in BC:
        return "%s.%s%s" % (n, d, flags), "vf%d, vf%d, vf%d%s" % (fd, fs, ft, n[-1])
    if n[-1] in "qi":
        return "%s.%s%s" % (n, d, flags), "vf%d, vf%d, %s" % (fd, fs, n[-1].upper())
    return "%s.%s%s" % (n, d, flags), "vf%d, vf%d, vf%d" % (fd, fs, ft)


def decode_vu_lower(w):
    op7 = bits(w, 25, 7)
    it, is_, id_ = bits(w, 16, 5), bits(w, 11, 5), bits(w, 6, 5)
    imm11 = simm(bits(w, 0, 11), 11)
    n = LOWER_GEN.get(op7)
    if n is None:
        return None, "illegal lower op %02Xh" % op7
    if n == "*OP":
        n = LOWER_OP.get(bits(w, 0, 6))
        if n is None:
            return None, "illegal lower special1 %02Xh" % bits(w, 0, 6)
        if n == "*S2":
            idx = (bits(w, 6, 5) << 2) | bits(w, 0, 2)
            n = LOWER_S2.get(idx)
            if n is None:
                return None, "illegal lower special2 %03Xh" % idx
            if n == "MOVE" and w == 0x8000033C:
                return "NOP", ""
            if n in ("XTOP", "XITOP"):
                return n, "vi%d" % it
            if n == "XGKICK":
                return n, "vi%d" % is_
            if n in ("MOVE", "MR32"):
                return "%s.%s" % (n, dest_str(w)), "vf%d, vf%d" % (it, is_)
            if n in ("LQI", "LQD"):
                return "%s.%s" % (n, dest_str(w)), "vf%d, (vi%d%s)" % (it, is_, "++" if n == "LQI" else "--")
            if n in ("SQI", "SQD"):
                return "%s.%s" % (n, dest_str(w)), "vf%d, (vi%d%s)" % (is_, it, "++" if n == "SQI" else "--")
            return n, "it=%d is=%d fsf/ftf=%X" % (it, is_, bits(w, 21, 4))
        if n == "IADDI":
            return n, "vi%d, vi%d, %d" % (it, is_, simm(id_, 5))
        return n, "vi%d, vi%d, vi%d" % (id_, is_, it)
    if n == "LQ":
        return "LQ.%s" % dest_str(w), "vf%d, %d(vi%d)" % (it, imm11, is_)
    if n == "SQ":
        return "SQ.%s" % dest_str(w), "vf%d, %d(vi%d)" % (is_, imm11, it)
    if n in ("ILW", "ISW"):
        return "%s.%s" % (n, dest_str(w)), "vi%d, %d(vi%d)" % (it, imm11, is_)
    if n in ("IADDIU", "ISUBIU"):
        imm15 = bits(w, 0, 11) | (bits(w, 21, 4) << 11)
        return n, "vi%d, vi%d, 0x%X" % (it, is_, imm15)
    if n in ("B", "BAL"):
        return n, "%+d" % imm11 if n == "B" else "vi%d, %+d" % (it, imm11)
    if n in ("IBEQ", "IBNE"):
        return n, "vi%d, vi%d, %+d" % (it, is_, imm11)
    if n.startswith("IB"):
        return n, "vi%d, %+d" % (is_, imm11)
    if n in ("JR", "JALR"):
        return n, "vi%d" % is_
    return n, "%06X" % bits(w, 0, 24)


def decode_vu_pair(lower, upper):
    un, uo = decode_vu_upper(upper)
    if upper & 0x80000000:  # I bit: the lower word is a float loaded into I
        ln, lo = "LOI", "0x%08X (%g)" % (lower, struct.unpack("<f", struct.pack("<I", lower))[0])
    else:
        ln, lo = decode_vu_lower(lower)
    return (un, uo), (ln, lo)


# ---- ELF32 little-endian reading ----
def read_elf(path):
    data = open(path, "rb").read()
    if data[:4] != b"\x7fELF" or data[4] != 1 or data[5] != 1:
        raise SystemExit("%s: not an ELF32 little-endian file" % path)
    (e_type, e_machine, _, e_entry, e_phoff, e_shoff, _, _, e_phentsize, e_phnum, e_shentsize, e_shnum,
     e_shstrndx) = struct.unpack_from("<HHIIIIIHHHHHH", data, 16)
    secs = []
    for i in range(e_shnum):
        secs.append(struct.unpack_from("<IIIIIIIIII", data, e_shoff + i * e_shentsize))
    shstr = secs[e_shstrndx]

    def name_at(strsec, off):
        start = strsec[4] + off
        return data[start:data.index(b"\0", start)].decode()

    sections = []
    for s in secs:
        sections.append({"name": name_at(shstr, s[0]), "type": s[1], "flags": s[2], "addr": s[3],
                         "offset": s[4], "size": s[5], "link": s[6], "entsize": s[9]})
    symbols = {}
    for s in sections:
        if s["type"] == 2:  # SHT_SYMTAB
            strsec = secs[s["link"]]
            for j in range(s["size"] // 16):
                st_name, st_value, st_size, st_info, st_other, st_shndx = struct.unpack_from(
                    "<IIIBBH", data, s["offset"] + j * 16)
                if st_name:
                    symbols[name_at(strsec, st_name)] = st_value
    return data, e_machine, e_entry, sections, symbols


def read_vaddr(data, sections, addr, size):
    for s in sections:
        if s["type"] == 1 and s["addr"] <= addr and addr + size <= s["addr"] + s["size"]:
            off = s["offset"] + addr - s["addr"]
            return data[off:off + size]
    raise SystemExit("address 0x%08X not in a PROGBITS section" % addr)


def check_elf(path, objdump):
    data, machine, entry, sections, symbols = read_elf(path)
    if machine != 8:
        raise SystemExit("%s: e_machine %d is not MIPS" % (path, machine))
    errors = 0
    count = 0
    special = []
    for s in sections:
        if s["type"] != 1 or not (s["flags"] & 0x4):  # PROGBITS + SHF_EXECINSTR
            continue
        body = data[s["offset"]:s["offset"] + s["size"]]
        for k in range(0, len(body) - 3, 4):
            w = struct.unpack_from("<I", body, k)[0]
            pc = s["addr"] + k
            n, ops = decode_ee(w, pc)
            count += 1
            if n is None:
                print("%s: 0x%08X: %08X: INVALID R5900 instruction: %s" % (path, pc, w, ops))
                errors += 1
            elif n in R5900_ONLY or n.startswith(("D", "SD", "LD")) and n not in ("DIV", "DIVU"):
                special.append("  0x%08X: %08X  %-8s %s" % (pc, w, n, ops))
    print("%s: %d EE instructions decoded, entry 0x%08X" % (path, count, entry))
    if special:
        print("  R5900-only / 64-bit instructions:")
        print("\n".join(special))
    for name in sorted(symbols):
        if name.startswith("vu1_code") and not name.endswith("_end"):
            start, end = symbols[name], symbols.get(name + "_end")
            if end is None:
                print("%s: %s has no %s_end symbol" % (path, name, name))
                errors += 1
                continue
            code = read_vaddr(data, sections, start, end - start)
            print("  VU microcode %s (%d instructions):" % (name, len(code) // 8))
            for i in range(0, len(code), 8):
                lower, upper = struct.unpack_from("<II", code, i)
                (un, uo), (ln, lo) = decode_vu_pair(lower, upper)
                if un is None or ln is None:
                    print("    %04X: %08X %08X  INVALID: %s %s" % (i // 8, upper, lower, uo if un is None else "",
                                                                  lo if ln is None else ""))
                    errors += 1
                    continue
                print("    %04X: %08X %08X  %-26s | %s" % (i // 8, upper, lower, (un + " " + uo).strip(),
                                                          (ln + " " + lo).strip()))
    if objdump:
        # Cross-check: every word llvm-objdump (MIPS III) can't decode must be one we listed as R5900-only.
        out = subprocess.run([objdump, "-d", "--mcpu=mips3", path], capture_output=True, text=True, check=True).stdout
        for line in out.splitlines():
            if "<unknown>" in line:
                parts = line.split()
                addr = int(parts[0].rstrip(":"), 16)
                w = int("".join(reversed(parts[1:5])), 16)
                n, _ = decode_ee(w, addr)
                if n is None or not (n in R5900_ONLY or n == "VU0-macro"):
                    print("%s: 0x%08X: %08X unknown to llvm-objdump and not an R5900-only instruction" % (path, addr, w))
                    errors += 1
    if errors:
        raise SystemExit("%s: %d decoding error(s)" % (path, errors))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", metavar="ELF")
    ap.add_argument("--objdump", default=None, help="llvm-objdump to cross-check unknown words")
    ap.add_argument("--word", metavar="HEX")
    ap.add_argument("--vu", nargs=2, metavar=("LOWER", "UPPER"))
    a = ap.parse_args()
    if a.word:
        n, ops = decode_ee(int(a.word, 16))
        print(n, ops)
    if a.vu:
        print(decode_vu_pair(int(a.vu[0], 16), int(a.vu[1], 16)))
    if a.check:
        check_elf(a.check, a.objdump)


if __name__ == "__main__":
    main()
