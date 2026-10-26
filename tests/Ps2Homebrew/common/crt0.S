# GameVault PS2 homebrew: start-up code. GameVault's own code.
#
# Entered at _start in kernel mode with interrupts off, no BIOS. Sets the stack and $gp, clears
# .bss through its uncached KSEG1 alias with 128-bit SQ stores, clears the status block, calls
# main() and then spins forever.

#include "r5900.inc"

    .set noreorder
    .set noat
    .section .text.start, "ax", @progbits
    .globl _start
    .type _start, @function
_start:
    la      $sp, __stack_top
    la      $gp, _gp

    # Clear .bss (16-byte aligned start and end, see link.ld) through KSEG1: 0x80xxxxxx | 0x20000000.
    la      $8, __bss_start
    la      $9, __bss_end
    lui     $10, 0x2000
    or      $8, $8, $10
    or      $9, $9, $10
1:  beq     $8, $9, 2f
    nop
    SQ      0, 0, 8                 # sq $zero, 0($t0)
    b       1b
    addiu   $8, $8, 16

    # Clear the 64-byte status block at physical 0x001FFF00.
2:  lui     $8, 0xA01F
    ori     $8, $8, 0xFF00
    SQ      0, 0, 8
    SQ      0, 16, 8
    SQ      0, 32, 8
    SQ      0, 48, 8
    sync

    jal     main
    nop
3:  b       3b
    nop
    .size _start, . - _start
