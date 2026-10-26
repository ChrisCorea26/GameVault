# GameVault's PlayStation 2 homebrew test programs

Small bare-metal programs that draw through every path to the GS (PATH3 by DMA and through the GIF FIFO,
PATH2 through VIF1 DIRECT, PATH1 from a VU1 microprogram) with textures, CLUTs, blending and Z. They are
GameVault's own work, built with the system's clang, and committed with their built ELFs (`elf/`) so the
emulator's tests can run them and pin the pictures as golden images (Phase 17, plan §9).

**Licence:** GameVault's own code, under the repository's licence. No Sony code, no ps2sdk, no BIOS.

## Building

```sh
tests/Ps2Homebrew/build.sh                 # rebuilds elf/*.elf; fails on any error
LISTINGS=1 tests/Ps2Homebrew/build.sh      # also writes build/<name>.lst (git-ignored)
tests/Ps2Homebrew/build.sh vu1-path1       # one program
```

Needs `clang` (target `mipsel-none-elf`, `-mcpu=mips2 -mabi=32`, freestanding), `ld.lld`, `llvm-objdump`
and `python3`. The build is deterministic (same bytes every time, no paths embedded). After linking each
ELF, `tools/r5900dec.py --check` decodes **every** word of the code sections with tables transcribed from
ps2tek ("EE Instruction Decoding") and fails on anything that isn't a valid R5900 instruction (so a MIPS32
`mul`, `ll`/`sc` or a stray padding trap can't slip in); it lists the R5900-only and 64-bit instructions
and disassembles the VU1 microcode (symbols `vu1_code*`), failing on illegal VU words.

| Path | What |
|---|---|
| `common/crt0.S` | Entry: `$sp`, `$gp`, clears `.bss` with 128-bit `SQ` through KSEG1, clears the status block, calls `main`, spins |
| `common/ee.S` | `gs_write64` (64-bit `SD` to the privileged GS registers), `qcopy` and `gif_fifo_write` (`LQ`/`SQ`) |
| `common/r5900.inc` | Assembler macros emitting R5900-only instructions as `.word` (LQ/SQ, MMI, FPU ACC ops) |
| `common/vu.h` | VU micro-instruction encoding macros (C and `.S`), field layout documented in the header |
| `common/ps2hw.h`, `common/common.c` | Registers, GS register/GIFtag builders, CRT set-up, DMA helpers |
| `common/link.ld` | Linker script |
| `tools/r5900dec.py` | EE + VU decoder / checker (`--check ELF`, `--word HEX`, `--vu LOWER UPPER`) |

## How the programs run

- **ELF:** one `PT_LOAD` segment, `p_vaddr` 0x80100000 (KSEG0), `p_paddr` 0x00100000; entry point 0x80100000.
  A loader may use `p_paddr` or `p_vaddr & 0x1FFFFFFF`. `.bss` (packet buffer, 16 KB stack) is cleared by crt0.
- **Start state:** kernel mode, interrupts off, no BIOS, no TLB needed: hardware is reached through KSEG1
  (0xA0000000 + physical; e.g. DMAC 0xB000A000, GS privileged registers 0xB2000000). No syscalls.
  DMA buffers are written through their uncached KSEG1 alias, so no cache flush is needed; DMA addresses are
  physical.
- **Status block** at physical **0x001FFF00**: word 0 = `0x42485647` ("GVHB") once the program has queued
  everything and entered its idle loop; word 1 = program id (1 gs-basic, 2 gs-texture, 3 vu1-path1,
  4 mmi-fpu-check). mmi-fpu-check also writes word 2 = number of passed checks (16 when all pass) and
  word 3 = bit mask of failed checks.
- **Display (all programs):** NTSC interlaced, field mode, 640×448 PSMCT32 frame at FBP 0 (FBW 10), as the
  BIOS's `SetGsCrt(1, 2, 0)` leaves it: `SMODE1` 0x740824504 → 0x740834504 (PRST) → 0x740804504,
  `SMODE2` 1 (INT=1 FFMD=0), `SRFSH` 8, `SYNCH1` 0x0007F5B61F06F040, `SYNCH2` 0x33A4D8,
  `SYNCV` 0x00C7800601A01801, `DISPLAY1` 0x001BF9FF01835281 (DX 641, DY 53, MAGH 3, DW 2559, DH 447),
  `DISPFB1` 0x1400, `PMODE` 0xFF25 (circuit 1 only, ALP 0xFF so frame alpha doesn't matter), `BGCOLOR` 0.
  Field values follow the Linux PS2 port's `ps2fb.c`/`gs-registers.h`. `GS_CSR` is reset first.
- **Drawing environment (all programs):** context 1, `XYOFFSET_1` = (1024, 1024) so screen pixel (x, y) is
  vertex (x + 1024, y + 1024); scissor 0–639 × 0–447; PSMZ24 Z buffer at page 140 (`ZBP` 140); Z test on
  with ZTST ALWAYS unless noted; `PRMODECONT` 1; `COLCLAMP` 1; no dither, FBA, PABE.
  Pixel coordinates below are frame-buffer coordinates (x right, y down, (0,0) top left of the 640×448 frame).
  Probe points are chosen away from edges so ±1-pixel rasterization differences don't matter; "≈" marks
  interpolated colours whose exact value the golden image pins.

## 1. `gs-basic` — CRT set-up and untextured primitives (PATH3)

One PATH3 DMA packet (normal mode, `D2_CHCR` 0x101, polled on STR) in PACKED mode (PRIM in the GIFtag, one
sprite with PRIM as a PACKED register, A+D for the environment) plus REGLIST for the points; then the corner
markers are written straight into the GIF FIFO (0x10006000) with `SQ`.

| Feature | Geometry | Probe → expected RGB |
|---|---|---|
| Background (A+D sprite) | whole frame | (300, 220) → (16, 16, 64) |
| Corner markers (GIF FIFO, `SQ`) | (0,0)–(8,8) and (632,440)–(640,448), white | (3, 3) and (636, 444) → (255, 255, 255) |
| 1 Flat triangle | (40,40) (200,40) (40,200), red | (80, 80) → (255, 0, 0); (190, 190) → background |
| 2 Gouraud triangle | (240,40) red, (400,40) green, (240,200) blue | (244, 44) ≈ red; (316, 120) ≈ (6, 121, 128); (392, 42) ≈ green |
| 3 Flat triangle strip | (440,40) (600,40) (440,200) (600,200); colours white, white, yellow, cyan | (470, 70) → (255, 255, 0); (570, 170) → (0, 255, 255) — flat shading uses the colour of the vertex completing each triangle, white never shows |
| 4 Flat triangle fan | centre (120,320), corners (40,240) (200,240) (200,400) (40,400) (40,240) | top (120, 260) → magenta (255, 0, 255); right (180, 320) → orange (255, 128, 0); bottom (120, 380) → white; left (60, 320) → (0, 128, 255) |
| 5 Lines | white (240,260)–(400,260); Gouraud red→blue (240,280)–(400,280); green (420,240)–(420,400) | (320, 260) → white; (420, 320) → (0, 255, 0); (250, 280) ≈ red, (390, 280) ≈ blue |
| Line strip | yellow zigzag (240,300) (280,380) (320,300) (360,380) (400,300) | (260, 340) → (255, 255, 0) (±1 px) |
| 6 Sprite, PRIM as PACKED register | (460,300)–(600,400), grey | (530, 350) → (128, 128, 128) |
| 7 Points (REGLIST) | (460 + 20i, 260), i = 0..7 | exactly those pixels: red, green, blue, yellow (255,255,0), cyan (0,255,255), magenta (255,0,255), white, orange (255,128,0) |

## 2. `gs-texture` — textures, CLUTs, filtering, blending, Z (PATH3)

Uploads by IMAGE-mode GIF transfers (`BITBLTBUF`/`TRXPOS`/`TRXREG`/`TRXDIR` by A+D, then an IMAGE GIFtag),
then `TEXFLUSH`. GS memory: checkerboard TBP 8960 (page 280, TBW 1), PSMT8 TBP 9024 (TBW 2), PSMT4 TBP 9056
(TBW 2), 256-colour CLUT CBP 9088, 16-colour CLUT CBP 9120; CLUTs are PSMCT32, CSM1, loaded with CLD 1.

Textures:
- **Checkerboard**, 64×64 PSMCT32, 8×8-texel squares: texel (u, v) is white (255,255,255,A 0x80) when
  ⌊u/8⌋ + ⌊v/8⌋ is even, else blue (0,0,255,A 0x80).
- **PSMT8**, 16×16, texel (u, v) = index 16v + u; CLUT entry i = (R (i mod 16)·17, G ⌊i/16⌋·17, B 64). The CLUT
  is uploaded as a 16×16 image with index bits 3 and 4 swapped (the CSM1 layout), so a correct CSM1 lookup
  gives a smooth two-way gradient; a missing swizzle shows as swapped 8-texel column bands.
- **PSMT4**, 16×16, 4×4 cells of 4×4 texels, cell (cx, cy) = index cx + 4cy (even texels in the low nibble);
  16-colour CLUT (8×2 image): 0 black, 1 red, 2 green, 3 blue, 4 yellow, 5 cyan, 6 magenta, 7 white,
  8 (128,128,128), 9 (128,0,0), 10 (0,128,0), 11 (0,0,128), 12 (255,128,0), 13 (0,128,128), 14 (128,0,128),
  15 (128,128,0).

| Draw | Geometry | Probe → expected RGB |
|---|---|---|
| Background | clear (48,48,48), Z cleared to 0 | (620, 20) → (48, 48, 48) |
| D1 checkerboard, DECAL, nearest, UV sprite 2× | (32,32)–(160,160), UV (0,0)–(64,64) | (40, 40) → white; (56, 40) → (0, 0, 255); (56, 56) → white |
| D2 checkerboard, MODULATE by (64,128,128) | (192,32)–(320,160) | (200, 40) → (127, 255, 255); (216, 40) → (0, 0, 255) |
| D3 checkerboard, DECAL, bilinear, 8× | (352,32)–(608,160), UV (0,0)–(32,16) | (384, 64) → white; (448, 64) → (0, 0, 255); square edges blend (e.g. (416, 64) ≈ (128, 128, 255)) |
| D4 PSMT8 + 256-colour CLUT, 8× | (32,192)–(160,320), UV (0,0)–(16,16) | (36, 196) → (0, 0, 64); (156, 196) → (255, 0, 64); (100, 196) → (136, 0, 64); (36, 316) → (0, 255, 64); (100, 260) → (136, 136, 64); (156, 316) → (255, 255, 64) |
| D5 PSMT4 + 16-colour CLUT, 8× | (192,192)–(320,320) | cell (cx, cy) at (208 + 32cx, 208 + 32cy) → palette[cx + 4cy]: (208, 208) black, (240, 208) red, (272, 208) green, (304, 208) blue, (208, 240) yellow, (304, 304) (128, 128, 0) |
| D6 checkerboard, STQ, perspective | triangle strip (352,192) (608,192) (352,320) (608,320); Q 1 on the left edge, 0.5 on the right | (356, 196) → white; (356, 316) → (0, 0, 255); (604, 196) → (0, 0, 255); squares shrink towards the right |
| D7 alpha blend | sprite (128,96)–(256,176), (255,0,0) A 0x40, ALPHA (Cs−Cd)·As>>7 + Cd | over the background (176, 120) and (140, 170) → (151, 24, 24); over D1/D2 ≈ 50 % red tint |
| D8 Z test (PSMZ24, ZTST GEQUAL) | A green (360,336) (600,336) (360,440), Z 0x8000; then B magenta (360,350) Z 0xC000, (600,350) and (600,440) Z 0x4000 | both, B nearer: (420, 360) → (255, 0, 255); both, B behind: (520, 355) → (0, 255, 0); A only: (380, 420) → green; B only: (590, 430) → magenta; the boundary inside the overlap is the line x = 480 |

## 3. `vu1-path1` — VIF1, VU1 microprogram and PATH1, PATH2

A PATH3 DMA clears the frame to (24,24,24). Then one VIF1 source-chain DMA (`D1_CHCR` 0x145: chain, TTE):

1. `ref` → the microprogram in `.rodata` (28 instructions, 14 qwords), with `MPG 28, 0` in the tag's upper
   word (TTE).
2. `cnt` → VIF codes and data: `STCYCL 1,1`; `STMOD 0`; `UNPACK V4-32` ×5 to VU1 addresses 0–4 (matrix
   columns c0 = (0.75, 0.75, 0, 0), c1 = (−0.75, 0.75, 0, 0), c2 = 0, c3 = (1344, 1248, 0, 1), and a GIFtag
   for 4 PACKED RGBAQ/XYZ2 vertices, PRE Gouraud triangle strip, EOP); `BASE 100h`, `OFFSET 80h` (which
   sets TOPS = BASE, DBF 0 — as Play!'s Vif1.cpp and PCSX2 do; ps2tek's wording has it backwards);
   `STCYCL 2,1` (write 1, skip 1); `STMASK 40404040h` (w of every cycle from the row register); `STROW 0,0,0,1`;
   `UNPACK V4-16` masked, FLG, ×4 to TOPS+0, +2, +4, +6 (positions (−80,−80) (80,−80) (−80,80) (80,80),
   z 0, w sent as 7777h and replaced by R3 = 1); `UNPACK V4-32`, FLG, ×4 to TOPS+1, +3, +5, +7 (colours
   red, green, blue, white, A 128); `MSCAL 0`; `FLUSH`.
3. `ref` → a 4-qword GIF packet, with `DIRECT 4` in the tag's upper word (PATH2).
4. `end`.

The microprogram (`vu1-path1/vu1_prog.S`) reads TOP with `XTOP` (100h), converts each position with
`ITOF0`, transforms it with `MULAx/MADDAy/MADDAz/MADDw` by the matrix (exact in single precision:
X = 0.75x − 0.75y + 1344, Y = 0.75x + 0.75y + 1248), converts with `FTOI4` (12.4 fixed point), builds the
packet at VU1 address 200h and `XGKICK`s it. Afterwards a PATH3 DMA draws a marker.

| Feature | Geometry | Probe → expected RGB |
|---|---|---|
| Background (PATH3) | whole frame | (40, 400) → (24, 24, 24) |
| Diamond (PATH1, Gouraud triangle strip) | top (320,104) red, right (440,224) green, left (200,224) blue, bottom (320,344) white — the 160×160 square rotated 45° and scaled by 1.06 | (320, 112) ≈ (238, 8, 8); (430, 224) ≈ green; (210, 224) ≈ blue; (320, 330) ≈ (225, 240, 240); (320, 224) ≈ (0, 128, 128) |
| Yellow sprite (PATH2, VIF1 DIRECT after FLUSH) | (360,280)–(520,400) | (500, 390) → (255, 255, 0); (370, 290) → (255, 255, 0) — inside the diamond too, so PATH2 drew after PATH1 |
| Marker (PATH3, last) | (8,8)–(24,24), green | (16, 16) → (0, 255, 0) — shows the VIF1 chain completed |

## 4. `mmi-fpu-check` — R5900-only instructions, self-checking

Runs 16 checks (`mmi-fpu-check/checks.S`), compares with the expected values in `main.c` (from ps2tek's
descriptions, matching ps2autotests' hardware results for the same operations), writes the status block and
draws, on a (32,32,32) background, a 56×80 rectangle per check at (40 + 72·(i mod 8), 120 + 104·⌊i/8⌋):
green (0,255,0) = pass, red (255,0,0) = fail; probe each at (68 + 72·(i mod 8), 160 + 104·⌊i/8⌋).
A summary bar (40,360)–(600,400) is green only when all pass; probe (320, 380).

| # | Check | # | Check |
|---|---|---|---|
| 0 | PADDW (wrapping) | 8 | PABSW (0x80000000 → 0x7FFFFFFF) |
| 1 | PSUBW | 9 | PLZCW (low 64 bits) |
| 2 | PADDSB (signed saturation) | 10 | MULT1 / MFLO1 |
| 3 | PADDUB (unsigned saturation) | 11 | MADD twice (HI:LO accumulate) |
| 4 | PEXTLW | 12 | ADDA.S then MADD.S = 9.5 |
| 5 | PCPYLD | 13 | MULA.S, MADDA.S, read ACC = 8.0 |
| 6 | PCPYUD | 14 | MAX.S / MIN.S |
| 7 | PCPYH | 15 | ADD.S overflow saturates to 0x7FFFFFFF |

It sets `Status.CU1` itself before using the FPU. All checks pass on hardware-accurate behaviour: the status
block then reads `GVHB`, 4, 16, 0.

## Notes for the emulator's tests

- The pictures are complete once the status block's word 0 is "GVHB"; run a few more frames before
  comparing.
- Nothing here depends on timing, interrupts or the BIOS. Each program finishes its drawing once; the frame
  then stays the same.
- These programs haven't been run on hardware or an emulator yet; their golden images should be reviewed
  against the probe tables above when first recorded.
