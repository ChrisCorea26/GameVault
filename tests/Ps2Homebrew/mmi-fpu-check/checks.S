# GameVault PS2 homebrew "mmi-fpu-check": the instructions under test. GameVault's own code.
#
# void run_checks(u32 out[16][4]): runs 16 checks and stores each result to out[i] (16-byte aligned,
# zeroed by the caller). MMI results are stored whole with SQ; scalar results with SW. The expected
# values are in main.c (derived from ps2tek's descriptions and checked against ps2autotests' hardware
# results for the same operations: tests/cpu/ee_simd/{arithmetic,shuffle,logic}.expected, tests/cpu/ee_fpu/arithmetic.expected).

#include "r5900.inc"

    .set noreorder
    .set noat
    .set hardfloat

    .section .rodata.checks, "a"
    .balign 16
in_a: .word 0x00000001, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF
in_b: .word 0x00000002, 0x00000001, 0xFFFFFFFF, 0x00000001
in_c: .word 0x01FF807F, 0x01FF807F, 0x01FF807F, 0x01FF807F
in_d: .word 0x44441111, 0x55552222, 0x66663333, 0x77778888
in_e: .word 0x80000000, 0xFFFFFFFF, 0x00000005, 0xFFFFFFFB

    .text
    .globl run_checks
    .type run_checks, @function
run_checks:
    # COP1 usable: Status.CU1 (bit 29).
    mfc0    $8, $12
    lui     $9, 0x2000
    or      $8, $8, $9
    mtc0    $8, $12
    .word   0x0000040F              # sync.p
    nop
    nop

    la      $2, in_a
    LQ      8, 0, 2                 # t0 = A
    LQ      9, 16, 2                # t1 = B
    LQ      10, 32, 2               # t2 = C
    LQ      11, 48, 2               # t3 = D
    LQ      12, 64, 2               # t4 = E

    PADDW   13, 8, 9                # 0: paddw  t5, A, B
    SQ      13, 0x000, 4
    PSUBW   13, 8, 9                # 1: psubw  t5, A, B
    SQ      13, 0x010, 4
    PADDSB  13, 10, 10              # 2: paddsb t5, C, C
    SQ      13, 0x020, 4
    PADDUB  13, 10, 10              # 3: paddub t5, C, C
    SQ      13, 0x030, 4
    PEXTLW  13, 8, 9                # 4: pextlw t5, A, B
    SQ      13, 0x040, 4
    PCPYLD  13, 9, 8                # 5: pcpyld t5, B, A
    SQ      13, 0x050, 4
    PCPYUD  13, 8, 9                # 6: pcpyud t5, A, B
    SQ      13, 0x060, 4
    PCPYH   13, 11                  # 7: pcpyh  t5, D
    SQ      13, 0x070, 4
    PABSW   13, 12                  # 8: pabsw  t5, E
    SQ      13, 0x080, 4
    PLZCW   13, 8                   # 9: plzcw  t5, A (low 64 bits)
    sw      $13, 0x090($4)
    .set push
    .set mips3
    dsrl32  $13, $13, 0
    .set pop
    sw      $13, 0x094($4)

    # 10: mult1 / mflo1 (pipeline 1): 1234 * -5678
    li      $14, 1234
    li      $15, -5678
    MULT1   0, 14, 15
    MFLO1   13
    sw      $13, 0x0A0($4)

    # 11: madd twice from HI:LO = 0: 2 * (100000 * 100000) -> LO in rd, then HI
    mthi    $0
    mtlo    $0
    li      $14, 100000
    MADD    0, 14, 14
    MADD    13, 14, 14
    sw      $13, 0x0B0($4)
    mfhi    $13
    sw      $13, 0x0B4($4)

    # 12: adda.s 1.5 + 2.0, then madd.s fd = ACC + 2.0 * 3.0 = 9.5
    li      $13, 0x3FC00000
    mtc1    $13, $f1
    li      $13, 0x40000000
    mtc1    $13, $f2
    li      $13, 0x40400000
    mtc1    $13, $f3
    mtc1    $0, $f0
    ADDA_S  1, 2
    MADD_S  4, 2, 3
    swc1    $f4, 0x0C0($4)

    # 13: mula.s 2.0 * 3.0, madda.s += 4.0 * 0.5, read ACC with madd.s fd = ACC + 0 * 0 -> 8.0
    li      $13, 0x40800000
    mtc1    $13, $f5
    li      $13, 0x3F000000
    mtc1    $13, $f6
    MULA_S  2, 3
    MADDA_S 5, 6
    MADD_S  7, 0, 0
    swc1    $f7, 0x0D0($4)

    # 14: max.s / min.s of -1.0 and 2.0
    li      $13, 0xBF800000
    mtc1    $13, $f8
    MAX_S   9, 8, 2
    MIN_S   10, 8, 2
    swc1    $f9, 0x0E0($4)
    swc1    $f10, 0x0E4($4)

    # 15: add.s 0x7F800000 + 0x7F800000 saturates to 0x7FFFFFFF (no infinities on the EE FPU)
    li      $13, 0x7F800000
    mtc1    $13, $f11
    add.s   $f12, $f11, $f11
    swc1    $f12, 0x0F0($4)

    jr      $31
    nop
    .size run_checks, . - run_checks
