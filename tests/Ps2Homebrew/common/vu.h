/*
 * GameVault PS2 homebrew: VU micro-mode instruction encodings as macros, usable from C and from
 * preprocessed assembly (.S, with .word). GameVault's own code.
 *
 * A VU instruction is a 64-bit pair, stored little-endian: the LOWER word first (lower address), then
 * the UPPER word (ps2tek "VU Instruction Format and Decoding"). VU_PAIR(upper, lower) emits them in
 * memory order. Field layout, from Play!'s decoders (Source/ee/MA_VU_Upper.cpp, MA_VU_Lower.cpp) and
 * ps2tek's COP2 Special1/Special2 tables (which share the encodings):
 *
 *   Upper: 31 I, 30 E, 29 M, 28 D, 27 T | 24-21 dest (x=bit 24, y 23, z 22, w 21) | 20-16 ft |
 *          15-11 fs | 10-6 fd | 5-0 op.  op 3Ch-3Fh selects "special2": index = (bits 10-6) * 4 + (bits 1-0).
 *   Lower: 31-25 op (40h = special) | 24-21 dest | 20-16 it/ft | 15-11 is/fs | 10-6 id/imm5 | 10-0 imm11.
 *          Special: bits 5-0 = op (30h IADD, 32h IADDI ...), 3Ch-3Fh = special2 like the upper.
 *          IADDIU imm15 = bits 24-21 (imm 14-11) : bits 10-0 (imm 10-0).
 *          Branch targets are (address of the next pair) + imm11 * 8, i.e. imm11 counts pairs.
 * tools/r5900dec.py decodes the result back as a check.
 */
#ifndef GV_VU_H
#define GV_VU_H

/* dest masks */
#define VU_X 8
#define VU_Y 4
#define VU_Z 2
#define VU_W 1
#define VU_XYZW 15
/* broadcast fields */
#define VU_BCX 0
#define VU_BCY 1
#define VU_BCZ 2
#define VU_BCW 3

#define VU_PAIR(upper, lower) (lower), (upper)

/* ---- upper ---- */
#define VUU_I_BIT 0x80000000
#define VUU_E_BIT 0x40000000
#define VUU_S1(op, dest, ft, fs, fd) ((((dest) & 15) << 21) | (((ft) & 31) << 16) | (((fs) & 31) << 11) | (((fd) & 31) << 6) | ((op) & 0x3F))
#define VUU_S2(idx, dest, ft, fs) ((((dest) & 15) << 21) | (((ft) & 31) << 16) | (((fs) & 31) << 11) | ((((idx) >> 2) & 31) << 6) | 0x3C | ((idx) & 3))

#define VUU_NOP 0x000002FF                                                  /* special2 2Fh */
#define VUU_NOP_E (VUU_NOP | VUU_E_BIT)
#define VUU_MADDbc(dest, fd, fs, ft, bc) VUU_S1(0x08 | (bc), dest, ft, fs, fd) /* fd = ACC + fs * ft.bc */
#define VUU_MULAbc(dest, fs, ft, bc) VUU_S2(0x18 | (bc), dest, ft, fs)        /* ACC = fs * ft.bc */
#define VUU_MADDAbc(dest, fs, ft, bc) VUU_S2(0x08 | (bc), dest, ft, fs)       /* ACC += fs * ft.bc */
#define VUU_ITOF0(dest, ft, fs) VUU_S2(0x10, dest, ft, fs)                    /* ft = float(fs) */
#define VUU_FTOI4(dest, ft, fs) VUU_S2(0x15, dest, ft, fs)                    /* ft = int(fs * 16) */

/* ---- lower ---- */
#define VUL_NOP 0x8000033C                                                  /* MOVE vf0, vf0 */
#define VUL_LQ(dest, ft, imm11, is) ((0x00 << 25) | (((dest) & 15) << 21) | (((ft) & 31) << 16) | (((is) & 31) << 11) | ((imm11) & 0x7FF))
#define VUL_SQ(dest, fs, imm11, it) ((0x01 << 25) | (((dest) & 15) << 21) | (((it) & 31) << 16) | (((fs) & 31) << 11) | ((imm11) & 0x7FF))
#define VUL_IADDIU(it, is, imm15) ((0x08 << 25) | ((((imm15) >> 11) & 15) << 21) | (((it) & 31) << 16) | (((is) & 31) << 11) | ((imm15) & 0x7FF))
#define VUL_IADDI(it, is, imm5) (0x80000000 | (((it) & 31) << 16) | (((is) & 31) << 11) | (((imm5) & 31) << 6) | 0x32)
#define VUL_IBNE(it, is, imm11) ((0x29 << 25) | (((it) & 31) << 16) | (((is) & 31) << 11) | ((imm11) & 0x7FF))
#define VUL_XTOP(it) (0x80000000 | (((it) & 31) << 16) | (0x1A << 6) | 0x3C)   /* special2 68h */
#define VUL_XGKICK(is) (0x80000000 | (((is) & 31) << 11) | (0x1B << 6) | 0x3C) /* special2 6Ch */

#endif
