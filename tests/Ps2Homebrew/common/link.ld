/*
 * GameVault PS2 homebrew linker script. GameVault's own code.
 *
 * Code and data are linked in KSEG0 at 0x80100000 (virtual) and loaded at physical 0x00100000
 * (p_paddr), one PT_LOAD segment. A loader may use either p_paddr or (p_vaddr & 0x1FFFFFFF).
 * The 256 bytes at physical 0x001FFF00 are the status block (see ps2hw.h); the image must end below it.
 */
OUTPUT_FORMAT("elf32-tradlittlemips")
OUTPUT_ARCH(mips)
ENTRY(_start)

PHDRS
{
    image PT_LOAD FLAGS(7);
}

SECTIONS
{
    . = 0x80100000;

    .text : AT(0x00100000)
    {
        KEEP(*(.text.start))
        *(.text .text.*)
    } :image =0

    .rodata ALIGN(16) : AT(ADDR(.rodata) - 0x80000000)
    {
        *(.rodata .rodata.*)
    } :image =0

    .data ALIGN(16) : AT(ADDR(.data) - 0x80000000)
    {
        *(.data .data.*)
        *(.sdata .sdata.*)
    } :image =0

    _gp = ALIGN(16) + 0x7FF0;

    .bss ALIGN(16) (NOLOAD) : AT(ADDR(.bss) - 0x80000000)
    {
        __bss_start = .;
        *(.sbss .sbss.*)
        *(.bss .bss.*)
        *(COMMON)
        . = ALIGN(16);
        __stack_bottom = .;
        . += 0x4000;
        __stack_top = .;
        __bss_end = .;
    } :image =0

    _end = .;
    ASSERT(_end <= 0x801FFF00, "image overlaps the status block at 0x801FFF00")

    /DISCARD/ : { *(.MIPS.abiflags) *(.reginfo) *(.pdr) *(.comment) *(.gnu.attributes) *(.note*) }
}
