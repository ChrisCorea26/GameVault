# GameVault PS2 homebrew "vu1-path1": the VU1 microprogram. GameVault's own code.
#
# VU1 data memory (quadword addresses) as the VIF1 stream leaves it:
#   0-3   matrix columns c0..c3 (floats): out = c0*x + c1*y + c2*z + c3*w
#   4     GIFtag for 4 PACKED (RGBAQ, XYZ2) vertices, PRE = Gouraud triangle strip, EOP
#   TOP   4 vertices x 2 quadwords: position (integers, w = 1 from the VIF row register), colour (RGBA integers)
# It writes the GIF packet at 200h (tag + 4 x (RGBA, XYZ2)) and XGKICKs it (PATH1).
#
# Registers: vi1 input pointer, vi2 output pointer, vi3 vertex count; vf1-vf4 matrix, vf5 tag,
# vf6 position, vf7 colour, vf8 transformed position.
# FMAC read-after-write hazards stall in hardware (ps2tek "VU Pipelining"), so no padding is needed for
# them; the ACC chain MULA/MADDA/MADDA/MADD is the standard back-to-back form.

#include "vu.h"

    .section .rodata.vu1, "a"
    .balign 16
    .globl vu1_code
    .globl vu1_code_end
vu1_code:
    .word VU_PAIR(VUU_NOP, VUL_XTOP(1))                                   # 00  xtop vi1
    .word VU_PAIR(VUU_NOP, VUL_IADDIU(2, 0, 0x200))                       # 01  iaddiu vi2, vi0, 0x200
    .word VU_PAIR(VUU_NOP, VUL_IADDIU(3, 0, 4))                           # 02  iaddiu vi3, vi0, 4
    .word VU_PAIR(VUU_NOP, VUL_LQ(VU_XYZW, 1, 0, 0))                      # 03  lq vf1, 0(vi0)
    .word VU_PAIR(VUU_NOP, VUL_LQ(VU_XYZW, 2, 1, 0))                      # 04  lq vf2, 1(vi0)
    .word VU_PAIR(VUU_NOP, VUL_LQ(VU_XYZW, 3, 2, 0))                      # 05  lq vf3, 2(vi0)
    .word VU_PAIR(VUU_NOP, VUL_LQ(VU_XYZW, 4, 3, 0))                      # 06  lq vf4, 3(vi0)
    .word VU_PAIR(VUU_NOP, VUL_LQ(VU_XYZW, 5, 4, 0))                      # 07  lq vf5, 4(vi0)
    .word VU_PAIR(VUU_NOP, VUL_SQ(VU_XYZW, 5, 0, 2))                      # 08  sq vf5, 0(vi2)
    .word VU_PAIR(VUU_NOP, VUL_IADDIU(2, 2, 1))                           # 09  iaddiu vi2, vi2, 1
    # loop:
    .word VU_PAIR(VUU_NOP, VUL_LQ(VU_XYZW, 6, 0, 1))                      # 0A  lq vf6, 0(vi1)
    .word VU_PAIR(VUU_NOP, VUL_LQ(VU_XYZW, 7, 1, 1))                      # 0B  lq vf7, 1(vi1)
    .word VU_PAIR(VUU_ITOF0(VU_XYZW, 6, 6), VUL_IADDI(3, 3, -1))          # 0C  itof0 vf6, vf6 | iaddi vi3, vi3, -1
    .word VU_PAIR(VUU_MULAbc(VU_XYZW, 1, 6, VU_BCX), VUL_NOP)             # 0D  mulax ACC, vf1, vf6x
    .word VU_PAIR(VUU_MADDAbc(VU_XYZW, 2, 6, VU_BCY), VUL_NOP)            # 0E  madday ACC, vf2, vf6y
    .word VU_PAIR(VUU_MADDAbc(VU_XYZW, 3, 6, VU_BCZ), VUL_NOP)            # 0F  maddaz ACC, vf3, vf6z
    .word VU_PAIR(VUU_MADDbc(VU_XYZW, 8, 4, 6, VU_BCW), VUL_NOP)          # 10  maddw vf8, vf4, vf6w
    .word VU_PAIR(VUU_FTOI4(VU_XYZW, 8, 8), VUL_NOP)                      # 11  ftoi4 vf8, vf8
    .word VU_PAIR(VUU_NOP, VUL_SQ(VU_XYZW, 7, 0, 2))                      # 12  sq vf7, 0(vi2)
    .word VU_PAIR(VUU_NOP, VUL_SQ(VU_XYZW, 8, 1, 2))                      # 13  sq vf8, 1(vi2)
    .word VU_PAIR(VUU_NOP, VUL_IADDIU(1, 1, 2))                           # 14  iaddiu vi1, vi1, 2
    .word VU_PAIR(VUU_NOP, VUL_IBNE(3, 0, 0x0A - 0x16))                   # 15  ibne vi3, vi0, loop (0Ah)
    .word VU_PAIR(VUU_NOP, VUL_IADDIU(2, 2, 2))                           # 16  (delay slot) iaddiu vi2, vi2, 2
    .word VU_PAIR(VUU_NOP, VUL_IADDIU(2, 0, 0x200))                       # 17  iaddiu vi2, vi0, 0x200
    .word VU_PAIR(VUU_NOP, VUL_XGKICK(2))                                 # 18  xgkick vi2
    .word VU_PAIR(VUU_NOP_E, VUL_NOP)                                     # 19  nop[e]
    .word VU_PAIR(VUU_NOP, VUL_NOP)                                       # 1A  (E-bit delay slot)
    .word VU_PAIR(VUU_NOP, VUL_NOP)                                       # 1B  padding to 14 quadwords
vu1_code_end:
