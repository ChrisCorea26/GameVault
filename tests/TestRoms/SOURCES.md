# Test ROM sources

Every file **committed** in this folder must be freely redistributable and recorded below (SPEC §11.3).

Suites whose license doesn't clearly permit redistribution are **downloaded at test time** instead.
`fetch-test-roms.sh` / `fetch-test-roms.ps1` read [`manifest.tsv`](manifest.tsv), download each file into
`.cache/` (git-ignored) and verify its SHA-256. CI runs the script before the tests.

**Never commit commercial ROMs, BIOS files, or dumps of licensed games, even "for testing."**
To test against your own dumps, set `GAMEVAULT_LOCAL_ROMS` to a local folder. Tests marked
`[LocalRomFact]` run only when it is set.

## Committed files

| File / folder | Suite | Author | Source URL | License / permission | Added in |
|---|---|---|---|---|---|
| `tests/Ps2Homebrew/**` (sources and built `elf/*.elf`) | GameVault's PS2 homebrew test programs: GS drawing over PATH1/2/3, textures and CLUTs, a VU1 microprogram, MMI/FPU self-checks | GameVault | This repository | GameVault's own code | Phase 17 |
| `tests/GameVault.Core.Tests/Nintendo64/Fixtures/rsp-vector-tests.json` | The RSP code, inputs and hardware results of 33 `RSPTest/CP2` programs (vector instructions, including the reserved opcodes), taken from the ROMs and their `.asm` tables | Peter Lemon (krom) | [PeterLemon/N64](https://github.com/PeterLemon/N64) at commit `7085543e4a19d8c539fc9e0a4d2869e788b4ed4b` | Public domain (Unlicense) | Phase 16 |

## Downloaded at test time

The NES suites below are pinned to commit `95d8f621ae55cee0d09b91519a8989ae0e64753b` of the community mirror
[christopherpow/nes-test-roms](https://github.com/christopherpow/nes-test-roms); the other suites list their own pins
in their sections. Hashes are in `manifest.tsv` and the other `manifest-*.tsv` files.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `nestest/nestest.nes`, `nestest/nestest.log` | nestest | Kevin Horton (kevtris) | No explicit redistribution license | Phase 1: CPU trace comparison |
| `instr_test-v5/*.nes` (16 single-test ROMs) | instr_test-v5 | Shay Green (blargg) | No explicit redistribution license | Phase 1: CPU instruction behavior |
| `ppu_vbl_nmi/*.nes` (10 single-test ROMs) | ppu_vbl_nmi | Shay Green (blargg) | No explicit redistribution license | Phase 2: VBlank/NMI timing |
| `sprite_hit_tests_2005.10.05/*.nes` (11) | sprite_hit_tests | Shay Green (blargg) | No explicit redistribution license | Phase 2: sprite 0 hit |
| `sprite_overflow_tests/*.nes` (5) | sprite_overflow_tests | Shay Green (blargg) | No explicit redistribution license | Phase 2: sprite overflow flag |
| `blargg_ppu_tests_2005.09.15b/*.nes` (5) | blargg_ppu_tests | Shay Green (blargg) | No explicit redistribution license | Phase 2: palette, OAM, VRAM access |
| `ppu_open_bus/ppu_open_bus.nes` | ppu_open_bus | Shay Green (blargg) | No explicit redistribution license | Phase 2: PPU I/O latch decay |
| `oam_read/oam_read.nes` | oam_read | Shay Green (blargg) | No explicit redistribution license | Phase 2: OAM reads |
| `instr_timing/*.nes` (2) | instr_timing | Shay Green (blargg) | No explicit redistribution license | Phase 3: instruction cycle counts |
| `cpu_interrupts_v2/*.nes` (5) | cpu_interrupts_v2 | Shay Green (blargg) | No explicit redistribution license | Phase 3: IRQ/NMI latency |
| `apu_test/*.nes` (8) | apu_test | Shay Green (blargg) | No explicit redistribution license | Phase 3: APU registers and frame counter |
| `apu_reset/*.nes` (6) | apu_reset | Shay Green (blargg) | No explicit redistribution license | Phase 3: APU state after reset |
| `blargg_apu_2005.07.30/*.nes` (11) | blargg_apu_2005 | Shay Green (blargg) | No explicit redistribution license | Phase 3: length counters, IRQ timing |
| `dmc_tests/*.nes` (4) | dmc_tests | Shay Green (blargg) | No explicit redistribution license | Phase 3: DMC (run-to-completion only) |
| `sprdma_and_dmc_dma/*.nes` (2) | sprdma_and_dmc_dma | Shay Green (blargg) | No explicit redistribution license | Phase 3: OAM/DMC DMA overlap (known gap) |
| `dmc_dma_during_read4/*.nes` (5) | dmc_dma_during_read4 | Shay Green (blargg) | No explicit redistribution license | Phase 3: DMC DMA during reads (not yet asserted) |
| `mmc3_test_2/*.nes` (6 single ROMs) | mmc3_test_2 | Shay Green (blargg) | No explicit redistribution license | Phase 4: MMC3 IRQ counter |
| `instr_test-v5/all_instrs.nes`, `official_only.nes` | instr_test-v5 | Shay Green (blargg) | No explicit redistribution license | Phase 4: combined ROMs (MMC1) |
| `ppu_vbl_nmi/ppu_vbl_nmi.nes` | ppu_vbl_nmi | Shay Green (blargg) | No explicit redistribution license | Phase 4: combined ROM (MMC1) |
| `ppu_read_buffer/test_ppu_read_buffer.nes` | ppu_read_buffer | Joel Yliluoma (Bisqwit) | No explicit redistribution license | Phase 4: $2007 read buffer (CNROM) |
| `cpu_dummy_reads/cpu_dummy_reads.nes` | cpu_dummy_reads | Shay Green (blargg) | No explicit redistribution license | Phase 4: dummy reads (CNROM) |
| `cpu_dummy_writes/*.nes` (2) | cpu_dummy_writes | Joel Yliluoma (Bisqwit) | No explicit redistribution license | Phase 4: dummy writes |

### Z80 (Phase 11)

Pinned to commit `1c418fa0d719abab9273131113defbe276101d95` of
[anotherlin/z80emu](https://github.com/anotherlin/z80emu), which carries the original files.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `z80/zexdoc.com`, `z80/zexall.com` | Z80 instruction exerciser (zexdoc: documented flags; zexall: all flags) | Frank D. Cringle (1994) | GPL v2: redistributable, but kept out of the repository with the other suites | Phase 11: Z80 instructions and flags, run on a minimal CP/M stub |

### 6502 and 65C02 (Phase 12)

Pinned to commit `7954e2dbb49c469ea286070bf46cdd71aeb29e4b` of
[Klaus2m5/6502_65C02_functional_tests](https://github.com/Klaus2m5/6502_65C02_functional_tests).

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `6502/6502_functional_test.bin` | 6502 functional test, including decimal mode (success trap at `$3469`) | Klaus Dormann | GPL v3: redistributable, but kept out of the repository with the other suites | Phase 12: NMOS 6502 with decimal mode (Apple II) |
| `6502/65C02_extended_opcodes_test.bin` | 65C02 extended opcodes, including the WDC/Rockwell bit instructions (success trap at `$24F1`) | Klaus Dormann | GPL v3, as above | Phase 12: 65C02 (enhanced Apple IIe) |

### 68000 (Phase 13)

Pinned to commit `e0d5ece9670205cc84a0101081837deb446f86a3` of
[SingleStepTests/680x0](https://github.com/SingleStepTests/680x0), listed in `manifest-680x0.tsv` and fetched
with `fetch-test-roms.sh manifest-680x0.tsv` (`-Manifest manifest-680x0.tsv` on Windows). It is about 200 MB, so only
the 68000 CI job downloads it.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `680x0/68000/v1/*.json.gz` (124 files, 8,065 tests each) | One instruction per test, with the processor and memory state before and after | Thomas Harte (SingleStepTests) | No licence file in the repository; too large to commit anyway | Phase 13: 68000 instructions, flags, exceptions and address-error frames (`GAMEVAULT_RUN_68K=1`) |

### 65816 and SPC700 (Phase 14)

Pinned to commit `307c63f5673c8c6b861c52f71ddb89212a227480` of
[SingleStepTests/65816](https://github.com/SingleStepTests/65816) (`manifest-65816.tsv`, about 2.6 GB) and commit
`67d15f492b2740964abd4efd1229e0ec9c342228` of [SingleStepTests/spc700](https://github.com/SingleStepTests/spc700)
(`manifest-spc700.tsv`, about 84 MB). Only the SNES CPU CI job fetches them, and it caches the 65816 files.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `65816/v1/*.json` (512 files: each opcode in native and emulation mode, 10,000 tests each; 100 for MVN/MVP) | One instruction per test, with the state before and after and every bus cycle | Thomas Harte and contributors (SingleStepTests) | MIT: redistributable, but far too large to commit | Phase 14: 65C816 instructions, flags, emulation-mode wrapping and cycles (`GAMEVAULT_RUN_65816=1`) |
| `spc700/v1/*.json` (256 files, 1,000 tests each) | One instruction per test, with every bus cycle | SingleStepTests | MIT, as above | Phase 14: SPC700 instructions, flags and cycles (`GAMEVAULT_RUN_SPC700=1`) |

### PlayStation (Phase 15)

Pinned to commit `6d20c132aba02cf387ed2224993ce9ee9a48e620` of [PeterLemon/PSX](https://github.com/PeterLemon/PSX) and
commit `f727802fead11f1daa7549285548392ef87749cb` of [JaCzekanski/ps1-tests](https://github.com/JaCzekanski/ps1-tests),
listed in `manifest.tsv` (about 3 MB), so every test run fetches them.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `psx-peterlemon/CPUTest/**/*.exe` (31 PS-EXE programs: 29 CPU, 2 GTE) | Bare-metal tests that draw PASS (green font) or FAIL (red font) for each check; no BIOS needed | Peter Lemon (krom) | No licence file in the repository | Phase 15: R3000A ALU, shifts, MULT/DIV, loads and stores; GTE AVSZ3/AVSZ4 and NCLIP |
| `psx-peterlemon/Demo/printgpu/PRINTGPU.exe` | A bare-metal demo: Gouraud background and text sent by DMA and by the CPU | Peter Lemon (krom) | No licence file in the repository | Phase 15: the whole machine through the frame loop (golden image) |
| `psx-peterlemon/GPU/**/*.exe` (25 PS-EXE programs) | Bare-metal drawing demos: polygons, lines, rectangles, textures at each depth, texture windows, clipping, dithering and VRAM transfers | Peter Lemon (krom) | No licence file in the repository | Phase 15: GPU golden images (regression hashes, reviewed against the repository's PNGs) |
| `ps1-tests/gpu/{triangle,quad,lines,uv-interpolation}/vram.png` | VRAM dumps from a real console after each test program | Jakub Czekański (ps1-tests) | MIT: redistributable, but kept with the other downloads | Phase 15: the rasterizer, pixel for pixel (coverage, seams, interpolation, dithering, lines) |

### Nintendo 64 (Phase 16)

Pinned to commit `7085543e4a19d8c539fc9e0a4d2869e788b4ed4b` of [PeterLemon/N64](https://github.com/PeterLemon/N64), listed in
`manifest-n64.tsv` (about 280 MB: each program is a 1 MB ROM) and fetched with `fetch-test-roms.sh manifest-n64.tsv`
(`-Manifest manifest-n64.tsv` on Windows). Only the Nintendo 64 CI job fetches them, and it caches them; elsewhere the
tests run when the files are cached, or with `GAMEVAULT_RUN_N64=1`. The programs boot with krom's own boot code, not
Nintendo's, and the repository is public domain; they are downloaded like the other suites to keep the repository
small.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `n64-peterlemon/CPUTest/**/*.N64` (CPU, CP0, CP1, LL/SC and PI DMA programs) | Bare-metal tests that print PASS (green font) or FAIL (red font) for each check | Peter Lemon (krom) | Public domain (Unlicense), but 1 MB each | Phase 16: VR4300 integer, COP0, FPU and PI DMA alignment (`PeterLemonN64Tests`) |
| `n64-peterlemon/CPUTest/CPU/ADD/Font{Green,Red}8x8.bin` | The fonts those programs print with | Peter Lemon (krom) | As above | Phase 16: reading PASS and FAIL back from the framebuffer |
| `n64-peterlemon/RSPTest/**/*.N64` | The RSP's scalar and vector instructions, its memories and SP DMA alignment, as PASS/FAIL programs | Peter Lemon (krom) | As above | Phase 16: the RSP (`PeterLemonN64Tests`) |
| `n64-peterlemon/RDPTest/**/*.N64`, `n64-peterlemon/RDP/**/*.{N64,png}` (115 programs, 105 reference pictures) | The RDP's registers (PASS/FAIL), and drawing demos: fill, copy, one-cycle and two-cycle modes, triangles, rectangles, lines, every texture format, TLUTs, mask/clamp/mirror, the combiner, blender, alpha compare and Z buffer | Peter Lemon (krom) | As above | Phase 16: the RDP through the whole console, held to each reference picture and pinned as golden images (`PeterLemonN64RdpTests`) |
| `n64-peterlemon/RSP/**/*.{N64,png}` (5 demos), `n64-peterlemon/CP1/Fractal/**/*.N64` (2) | Demos that use the whole machine: RSP programs (a gradient, DCT decoding, strided DMA, RDP commands over the XBUS) and FPU fractals | Peter Lemon (krom) | As above | Phase 16: the RSP's demos held to their reference pictures, all pinned as golden images |

### PlayStation 2 (Phase 17)

Pinned to commit `97469ffbed8631277b94e28d01dabd702aa97ef3` of [jpd002/ps2autotests](https://github.com/jpd002/ps2autotests),
listed in `manifest.tsv` (about 6 MB), so every test run fetches them.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `ps2autotests/**/*.elf`, `*.irx` and `*.expected` (78 programs) | Test programs for the EE (integer, FPU, MMI, COP0), VU0 macro and VU micro mode, DMA (DMAC, scratchpad, VIF), the GS, memory and the EE and IOP kernels, each with its output recorded on a real PS2 | ps2autotests project contributors | ISC: redistributable, but kept with the other downloads | Phase 17: the EE, the VUs, DMA and GameVault's kernel, through the whole console |

### Bundled homebrew (GV-008)

Not downloads: these are committed in `data/homebrew/`, which ships with the app (SPEC §6.4), and the tests read them
from there. Sources, licenses and hashes are in each folder's `README.md`.

| Path | Game | Author | License | Used by |
|---|---|---|---|---|
| `data/homebrew/thwaite/thwaite.nes` | Thwaite v0.04 | Damian Yerrick | GPL-3.0-or-later (source included) | `BundledHomebrewTests`: each game starts and draws; the library lists it by name |
| `data/homebrew/concentration-room/croom.nes` | Concentration Room v0.02a | Damian Yerrick | GPL-3.0-or-later with an exception for exact copies | As above |
| `data/homebrew/rhde/rhde.nes` | RHDE: Furniture Fight v0.07 | Damian Yerrick | GNU All-Permissive | As above |
| `data/homebrew/robotfindskitten/robotfindskitten.nes` | robotfindskitten v0.10 | Damian Yerrick | zlib | As above |
