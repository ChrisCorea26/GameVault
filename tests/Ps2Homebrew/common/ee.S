# GameVault PS2 homebrew: helpers that need 64-bit or 128-bit stores. GameVault's own code.

#include "r5900.inc"

    .set noreorder
    .text

# void gs_write64(u32 phys, u32 lo, u32 hi): 64-bit store of (hi:lo) to KSEG1(phys).
# The privileged GS registers are 64 bits wide and are written with SD.
    .globl gs_write64
    .type gs_write64, @function
gs_write64:
    lui     $8, 0xA000
    or      $4, $4, $8
    .set push
    .set mips3
    dsll32  $6, $6, 0           # hi << 32
    dsll32  $5, $5, 0
    dsrl32  $5, $5, 0           # zero-extend lo
    or      $6, $6, $5
    sd      $6, 0($4)
    .set pop
    jr      $31
    nop
    .size gs_write64, . - gs_write64

# void qcopy(void *dst, const void *src, u32 nqw): copy nqw quadwords with LQ/SQ (both 16-byte aligned).
    .globl qcopy
    .type qcopy, @function
qcopy:
1:  beq     $6, $0, 2f
    nop
    LQ      8, 0, 5             # lq $t0, 0($a1)
    SQ      8, 0, 4             # sq $t0, 0($a0)
    addiu   $5, $5, 16
    addiu   $4, $4, 16
    b       1b
    addiu   $6, $6, -1
2:  jr      $31
    nop
    .size qcopy, . - qcopy

# void gif_fifo_write(const void *src, u32 nqw): PATH3 without DMA, storing each quadword to the
# GIF FIFO at 0x10006000 (KSEG1 0xB0006000) with SQ. Keep nqw <= 16 (the FIFO depth).
    .globl gif_fifo_write
    .type gif_fifo_write, @function
gif_fifo_write:
    lui     $9, 0xB000
    ori     $9, $9, 0x6000
1:  beq     $5, $0, 2f
    nop
    LQ      8, 0, 4             # lq $t0, 0($a0)
    SQ      8, 0, 9             # sq $t0, 0($t1)
    addiu   $4, $4, 16
    b       1b
    addiu   $5, $5, -1
2:  sync
    jr      $31
    nop
    .size gif_fifo_write, . - gif_fifo_write
