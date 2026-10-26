# GameVault PS2 homebrew: R5900-only instructions as .word encodings (llvm-mc doesn't know them).
# GameVault's own code. Encodings: ps2tek "EE Instruction Decoding" (normal opcode table: LQ = 1Eh,
# SQ = 1Fh; MMI = 1Ch with function in bits 0-5 and the MMI0-3 sub-op in bits 6-10).
# Register operands are plain numbers (e.g. 8 for $t0). tools/r5900dec.py decodes them back.

# LQ rt, offset(base): load 128-bit quadword (address low 4 bits ignored)
.macro LQ rt, off, base
    .word (0x1E << 26) | ((\base) << 21) | ((\rt) << 16) | ((\off) & 0xFFFF)
.endm

# SQ rt, offset(base): store 128-bit quadword
.macro SQ rt, off, base
    .word (0x1F << 26) | ((\base) << 21) | ((\rt) << 16) | ((\off) & 0xFFFF)
.endm

# MMI R-type: rs, rt, rd, sa(sub-op), function
.macro MMI_R rd, rs, rt, sub, fn
    .word (0x1C << 26) | ((\rs) << 21) | ((\rt) << 16) | ((\rd) << 11) | ((\sub) << 6) | (\fn)
.endm

# MMI0 (function 08h)
.macro PADDW rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x00, 0x08
.endm
.macro PSUBW rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x01, 0x08
.endm
.macro PADDH rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x04, 0x08
.endm
.macro PADDB rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x08, 0x08
.endm
.macro PADDSB rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x18, 0x08
.endm
.macro PEXTLW rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x12, 0x08
.endm
# MMI1 (function 28h)
.macro PADDUB rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x18, 0x28
.endm
.macro PABSW rd, rt
    MMI_R \rd, 0, \rt, 0x01, 0x28
.endm
# MMI2 (function 09h)
.macro PCPYLD rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x0E, 0x09
.endm
.macro PAND rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x12, 0x09
.endm
.macro PXOR rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x13, 0x09
.endm
# MMI3 (function 29h)
.macro PCPYUD rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x0E, 0x29
.endm
.macro POR rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x12, 0x29
.endm
.macro PCPYH rd, rt
    MMI_R \rd, 0, \rt, 0x1B, 0x29
.endm
# MMI function 04h: PLZCW rd, rs
.macro PLZCW rd, rs
    MMI_R \rd, \rs, 0, 0x00, 0x04
.endm
# MULT1 / MFLO1 (MMI functions 18h / 12h)
.macro MULT1 rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x00, 0x18
.endm
.macro MFLO1 rd
    MMI_R \rd, 0, 0, 0x00, 0x12
.endm
# MADD (MMI function 00h): (HI,LO) += rs * rt, rd = LO
.macro MADD rd, rs, rt
    MMI_R \rd, \rs, \rt, 0x00, 0x00
.endm

# COP1 (FPU) S-format arithmetic, opcode 11h, fmt 10h (ps2tek "COP1"):
#   function: ADDA.S 18h, MULA.S 1Ah, MADD.S 1Ch, MADDA.S 1Eh, MAX.S 28h, MIN.S 29h
.macro FPU_S fn, fd, fs, ft
    .word (0x11 << 26) | (0x10 << 21) | ((\ft) << 16) | ((\fs) << 11) | ((\fd) << 6) | (\fn)
.endm
.macro ADDA_S fs, ft
    FPU_S 0x18, 0, \fs, \ft
.endm
.macro MULA_S fs, ft
    FPU_S 0x1A, 0, \fs, \ft
.endm
.macro MADD_S fd, fs, ft
    FPU_S 0x1C, \fd, \fs, \ft
.endm
.macro MADDA_S fs, ft
    FPU_S 0x1E, 0, \fs, \ft
.endm
.macro MAX_S fd, fs, ft
    FPU_S 0x28, \fd, \fs, \ft
.endm
.macro MIN_S fd, fs, ft
    FPU_S 0x29, \fd, \fs, \ft
.endm
