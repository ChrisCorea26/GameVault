# Compatibility dashboard

This page will be generated in CI from the test-ROM harness (SPEC §11.2). Until then it is updated by hand
at the end of each phase. Run `tests/TestRoms/fetch-test-roms.sh` (or `.ps1`) and then `dotnet test` to
reproduce it.

_Last updated: Phase 16._

## CPU

| Suite | Test | Status | Notes |
|---|---|---|---|
| nestest | Automation-mode trace (8,991 lines) | ✅ Pass | Byte-for-byte match with `nestest.log`, including PPU dot/line and CYC columns |
| instr_test-v5 | 01-basics | ✅ Pass | |
| instr_test-v5 | 02-implied | ✅ Pass | |
| instr_test-v5 | 03-immediate | ✅ Pass | |
| instr_test-v5 | 04-zero_page | ✅ Pass | |
| instr_test-v5 | 05-zp_xy | ✅ Pass | |
| instr_test-v5 | 06-absolute | ✅ Pass | |
| instr_test-v5 | 07-abs_xy | ✅ Pass | |
| instr_test-v5 | 08-ind_x | ✅ Pass | |
| instr_test-v5 | 09-ind_y | ✅ Pass | |
| instr_test-v5 | 10-branches | ✅ Pass | |
| instr_test-v5 | 11-stack | ✅ Pass | |
| instr_test-v5 | 12-jmp_jsr | ✅ Pass | |
| instr_test-v5 | 13-rts | ✅ Pass | |
| instr_test-v5 | 14-rti | ✅ Pass | |
| instr_test-v5 | 15-brk | ✅ Pass | |
| instr_test-v5 | 16-special | ✅ Pass | |
| instr_test-v5 | all_instrs / official_only | ✅ Pass | Combined ROMs on MMC1 |
| cpu_dummy_reads | | ✅ Golden image | CNROM; reports on screen only |
| cpu_dummy_writes | oam, ppumem | ✅ Pass | |
| instr_timing | 1-instr_timing, 2-branch_timing | ✅ Pass | |
| cpu_interrupts_v2 | 1-cli_latency … 5-branch_delays_irq (5) | ✅ Pass | |

## PPU

Each ROM is also checked against a golden image of its final screen (`tests/GameVault.Core.Tests/Golden/golden.json`).

| Suite | Tests | Status | Notes |
|---|---|---|---|
| ppu_vbl_nmi | 01-vbl_basics … 10-even_odd_timing (10) | ✅ Pass | All dot-exact NMI/`$2002` races |
| sprite_hit_tests_2005.10.05 | 01.basics … 11.edge_timing (11) | ✅ Pass | |
| sprite_overflow_tests | 1.Basics … 5.Emulator (5) | ✅ Pass | Includes the hardware evaluation bug |
| blargg_ppu_tests_2005.09.15b | palette_ram, sprite_ram, vbl_clear_time, vram_access | ✅ Pass | |
| blargg_ppu_tests_2005.09.15b | power_up_palette | ℹ️ Not run | Expects one particular console's power-up palette RAM |
| ppu_open_bus | | ✅ Pass | I/O latch with per-bit decay |
| oam_read | | ✅ Pass | |
| nestest | Menu screen | ✅ Golden image | Normal (non-automation) mode |
| ppu_vbl_nmi | Combined ROM | ✅ Pass | MMC1 |
| ppu_read_buffer | test_ppu_read_buffer | ✅ Pass | CNROM |

## APU

| Suite | Tests | Status | Notes |
|---|---|---|---|
| apu_test | 1-len_ctr … 8-dmc_rates (8) | ✅ Pass | |
| apu_reset | 4015_cleared … works_immediately (6) | ✅ Pass | |
| blargg_apu_2005.07.30 | 01.len_ctr … 11.len_reload_timing (11) | ✅ Pass | Also checked against golden images |
| dmc_tests | buffer_retained, latency, status, status_irq | ℹ️ Runs | Listening tests: they report by sound, so CI only checks that they run to completion |
| sprdma_and_dmc_dma | sprdma_and_dmc_dma, _512 | ❌ Known gap | Exact cycle cost when OAM and DMC DMA overlap |
| dmc_dma_during_read4 | 5 ROMs | ⏳ Not verified | Fetched but not yet asserted (DMC DMA side effects on `$2007` / `$4016` reads) |

Audio output is checked by `AudioSoakTests`: 30 minutes of virtual time through the real APU, resampler, ring
buffer and rate controller. The device clock is run at its exact rate and at ±0.3 %. The result is zero
underruns, an average latency under 40 ms and a peak under 50 ms.

## Mappers

| Mapper | Board | Status |
|---|---|---|
| 0 | NROM | ✅ PRG-ROM, PRG-RAM, CHR-ROM/RAM, mirroring |
| 1 | MMC1 / SxROM | ✅ Serial writes (consecutive-write rule), all PRG/CHR modes, SUROM 512 KB, SOROM/SXROM PRG-RAM banks |
| 2 | UxROM | ✅ Bus conflicts on submapper 2 |
| 3 | CNROM | ✅ Bus conflicts on submapper 2 |
| 4 | MMC3 / MMC6 | ✅ IRQ counter with A12 filtering; new (Sharp) behavior by default, old (NEC) on submapper 4; MMC6 RAM on submapper 1 |
| 7 | AxROM | ✅ One-screen mirroring; bus conflicts on submapper 2 |
| 5 | MMC5 | ✅ PRG/CHR modes, PRG-RAM banks, ExRAM modes 0–3 incl. extended attributes, fill mode, scanline IRQ, multiplier, vertical split screen, 2 pulse + PCM audio |
| 9 / 10 | MMC2 / MMC4 | ✅ CHR latches on tiles $FD/$FE |
| 11 | Color Dreams | ✅ Bus conflicts |
| 19 | Namco 163 | ✅ Banking, CHR-ROM nametables, CPU-cycle IRQ, wavetable audio (channels averaged) |
| 21 / 22 / 23 / 25 | VRC2 / VRC4 | ✅ All address-line variants (ORed per mapper), VRC IRQ |
| 24 / 26 | VRC6 | ✅ Banking, IRQ, 2 pulse + sawtooth audio |
| 34 | BNROM / NINA-001 | ✅ |
| 66 | GxROM | ✅ |
| 69 | Sunsoft FME-7 / 5B | ✅ Banking, RAM/ROM at $6000, cycle IRQ, 5B tone, noise and envelope (logarithmic levels) |
| 71 | Camerica | ✅ Fire Hawk mirroring (submapper 1) |
| 79 | NINA-03/06 | ✅ |
| 85 | VRC7 | ✅ Banking, IRQ and OPLL FM audio (15 built-in patches + custom) |
| 87, 140 | Jaleco / Konami discrete | ✅ |
| 118 / 119 | TxSROM / TQROM | ✅ CIRAM routing from CHR bit 7; CHR-RAM via bit 6 |
| 206 | Namco 108 | ✅ |
| 13 | CPROM | ✅ 16 KB CHR-RAM, bus conflicts |
| 15 | 100-in-1 Contra Function 16 | ✅ All four PRG modes |
| 30 | UNROM 512 | ✅ 32 KB CHR-RAM; no bus conflicts on flashable boards |
| 32 | Irem G-101 | ✅ PRG swap mode |
| 33 / 48 | Taito TC0190 / TC0690 | ✅ TC0690 scanline IRQ |
| 65 | Irem H3001 | ✅ CPU-cycle IRQ |
| 67 | Sunsoft-3 | ✅ CPU-cycle IRQ |
| 73 | VRC3 | ✅ 16-bit and 8-bit IRQ modes |
| 75 | VRC1 | ✅ |
| 76 / 88 / 95 / 154 | Namco 108 variants | ✅ Including 95's CHR-selected nametables and 154's one-screen mirroring |
| 80 / 207 | Taito X1-005 | ✅ Internal RAM; 207 nametables from CHR registers |
| 152, 180, 184, 185, 232 | Discrete boards | ✅ 185 CHR-disable protection by submapper |
| 28 | Action 53 | ✅ All PRG modes and game sizes inside the outer bank |
| 64 | Tengen RAMBO-1 | ✅ 1 KB CHR mode, scanline and CPU-cycle IRQ |
| 68 | Sunsoft-4 | ✅ CHR-ROM nametables |
| 82 | Taito X1-017 | ✅ CHR inversion, three unlockable RAM chunks |
| 228 | Action 52 / Cheetahmen II | ✅ Address-decoded banking, 4-bit RAM |
| 20 | Famicom Disk System | ✅ With the player's own BIOS: RAM adapter, timer IRQ, drive with gaps and CRC-16/KERMIT, side switching (F4), disk writes saved as a separate patch, FDS wavetable audio with modulation |

Every mapper has a console-level save-state round-trip test (`MapperTests.Console_state_round_trips_for_every_mapper`).
Save and load take about 0.005–0.008 ms each (budget: 1 ms).

### MMC3

| Suite | Tests | Status | Notes |
|---|---|---|---|
| mmc3_test_2 | 1-clocking … 5-MMC3 (5) | ✅ Pass | |
| mmc3_test_2 | 6-MMC3_alt | ✅ Pass as submapper 4 | Expects the old (NEC) IRQ behavior; the test marks the header as NES 2.0 submapper 4 |

## Input devices

| Device | Status | Notes |
|---|---|---|
| Standard controller | ✅ | Two ports; turbo and pads in the platform layer |
| Zapper | ✅ | Port 2. Light sense needs a bright pixel the beam drew within the last 20 scanlines (NESdev Wiki: "Zapper"). The mouse aims and the left button fires. |
| Four Score | ✅ | Players 3–4 and the signature bits. While a Four Score game runs, the third and fourth pads join Players 3 and 4 (choose Four Score in Game Details → Properties or through the game database, which lists the Four Score games). Pads can be moved to any player by hand in Settings or the Player's Controllers flyout |
| Power Pad | ✅ | Port 2 (NESdev Wiki: "Power Pad"); keyboard U I O P / J K L ; / M , . / are buttons 1–12. Unit tests check the serial order on D3/D4; not yet checked against a real mat game |
| Arkanoid (Vaus) paddle | ✅ | Port 2 (NESdev Wiki: "Arkanoid controller"); the mouse turns the knob (98–242) and the left button fires. Unit tests check the inverted MSB-first serial position; not yet checked against Arkanoid |

## Sega SG-1000 / SC-3000 and Master System (Phase 11)

### Z80
| Test | Result |
|---|---|
| zexdoc (Frank Cringle): every instruction, documented flags | ✅ No errors (5.76 billion instructions) |
| zexall: every instruction, all flags including bits 3 and 5 | ✅ No errors |
| Unit tests: DAA, block instructions, IX/IY with displacements and halves, IM 1, EI delay, HALT, NMI, R, save states | ✅ |

zexdoc and zexall run in their own CI job ("Z80 exerciser", Release build). Locally, run them with
`GAMEVAULT_RUN_ZEX=1 dotnet test tests/GameVault.Core.Tests -c Release --filter "FullyQualifiedName~Zex_exercises"`.

### Video, sound and cartridges
| Part | State |
|---|---|
| TMS9918A (SG-1000): Graphics I and II, Text, Multicolor, sprites (4 per line, 5th-sprite flag, collision) | ✅ Unit tests |
| SMS VDP Mode 4: background, scrolling with the locks, priority, left-column blanking, sprites (8 per line, 8×16, zoom), line and frame interrupts, V counter | ✅ Unit tests |
| SMS2 224-line mode | Implemented; no test ROM yet |
| SN76489 PSG (Sega noise variant) | ✅ Unit tests |
| Sega mapper (with cartridge RAM, saved like a battery save), Codemasters (detected from the ROM), Korean, plain ≤48 KB | ✅ Unit tests (Sega, detection) |
| SG-1000 8 KB RAM-expansion boards | Supported by the cartridge; not yet selected by a game database |
| YM2413 FM sound (Japanese SMS): nine channels, rhythm mode, the $F0–$F2 ports and detection | 🟡 Unit tests (detection, a note's pitch, the bass drum, save states); a behavioral FM model shared with the VRC7, not checked against hardware recordings |
| SC-3000 keyboard (8255 PPI matrix) | ✅ Unit tests (a key's row and bit, row 7 as the joypads); the PC key layout follows SMS Power!'s matrix |
| PAL timing | Supported (313 lines), chosen by the game database's `region` |

Real-game results need the owner's cartridges or ROMs (see `docs/plans/phase-11.md`, owner checklist).

## Apple II, II+ and IIe (Phase 12)

| Part | State |
|---|---|
| 6502 with decimal mode: Klaus Dormann's `6502_functional_test` | ✅ Success trap reached |
| 65C02: Dormann's `65C02_extended_opcodes_test` (with WDC/Rockwell instructions) | ✅ Success trap reached |
| Memory: Language Card, IIe auxiliary memory and switches | ✅ Unit tests |
| Video: text (GameVault's own font, or the player's character ROM), lo-res, hi-res artifact colour, 80 columns, double hi-res, mixed | ✅ Unit tests |
| Disk II: DSK/DO/PO (6-and-2), NIB, WOZ 1/2; reads, writes, stepper | ✅ Unit tests |
| Speaker, keyboard, paddles and buttons | ✅ Unit tests |
| Disk menu (drives 1 and 2, insert and eject); disk writes saved as patches | ✅ Host and ViewModel tests |
| Video drawn line by line (mid-frame mode changes); green, amber and white monitors; the player's character ROM | ✅ Unit tests |
| Floating bus | Not yet |

Real disks need the owner's Apple ROMs (Settings → Apple II) and disk images; see
`docs/plans/phase-12.md`.

## Sega Genesis / Mega Drive (Phase 13)

| Part | State |
|---|---|
| 68000: SingleStepTests 680x0 (1,000,057 tests: registers, SR, memory, address-error frames) | ✅ All pass; 3 tests excluded as inconsistent with the rest of the suite |
| 68000 cycle counts | 🟡 98.8% match; the rest are address-error and odd-jump paths |
| 68000 reset, interrupts, STOP, trace, privilege, save states | ✅ Unit tests |
| VDP: planes, priority, scrolling (line, cell, 2-cell columns), window, sprites, shadow/highlight | ✅ Unit tests with tiles written in the tests |
| VDP: DMA (68000, fill, copy), status, H/V interrupts, HV counter | ✅ Unit tests |
| VDP: interlace mode 2 (double resolution: 8×16 tiles, woven fields, 448/480 lines) | ✅ Unit test with an 8×16 tile over two fields |
| VDP: Master System mode (Power Base Converter) | Not planned: `.sms` files play on the Master System core |
| YM2612: pitch, envelopes, timers, DAC, panning | ✅ Unit tests (440 Hz check) |
| YM2612: SSG-EG, LFO, channel 3 special mode | 🟡 Implemented, not checked against hardware recordings |
| SN76489, Z80 sound CPU, bus request/reset, bank window | ✅ Unit tests |
| Cartridges: raw and .smd, header region, SRAM (odd/even/word), SSF2 mapper | ✅ Unit tests |
| 3- and 6-button pads | ✅ Unit tests |
| EEPROM saves (I²C: X24C01 and 24C01–24C512), with Sega's header-declared wiring on $200001 | ✅ Unit tests (page writes, wrap, random reads, the cartridge's lines, save states) |
| EEPROM boards by product code: EA, Acclaim (16M and 32M), Codemasters, and Sega games whose header doesn't declare it | ✅ Unit tests for each wiring; real cartridges not checked yet (the owner's) |
| J-Cart (Codemasters' extra pad ports) | Not yet: its pad lines read as idle |
| 32X | See below |

### 32X (outstanding-work item 17, milestone 2)

| Part | State |
|---|---|
| SH-2: the instruction set, delayed branches, MAC, DIV0/DIV1 (the manual's division programs), exceptions, interrupts, SLEEP | ✅ Unit tests (37) |
| SH7604 on-chip: DIVU, DMAC (auto-request, DREQ), FRT, WDT interval mode, interrupt priorities | ✅ Unit tests |
| SH7604 cache: four ways, LRU, write-through, CCR (ID, OD, two-way, purge), the purge and address-array areas, DMA around it | ✅ Unit tests (10); a miss costs no extra time |
| The adapter: 68000 mapping, the SH-2s' reset, both boot ROMs, communication ports, CMD/V/H/PWM interrupts | ✅ Unit tests |
| VDP: packed, direct-colour and run-length lines, FS swap, auto fill, overwrite image, palette priority | ✅ Unit tests |
| The picture over the Genesis' (PRI, the colour's priority bit, the Genesis backdrop), line by line | ✅ Unit tests, including a palette change on an H interrupt |
| The 68000-to-SH-2 FIFO into DMA | ✅ Unit test |
| PWM sound | 🟡 Plays; not checked against hardware recordings |
| Booting the real BIOS and games | Not checked yet (the owner's) |

### Sega CD (outstanding-work item 17, milestone 1)

| Part | State |
|---|---|
| Sub CPU (the 68000 core), reset and bus request from the main CPU, level 2 interrupt, communication words and flags | ✅ Unit tests with programs on both CPUs |
| PRG-RAM banking and write protect; Word RAM in 2M and 1M mode, the dot image and its priority modes | ✅ Unit tests |
| 1M mode's cell-image view for the main CPU ($220000–$23FFFF, all five areas) | ✅ Unit tests |
| Mode 1: a cartridge booting with the unit at $400000 (MSU-MD), with a disc of the same name beside it | ✅ Unit and host tests; real MSU-MD games not checked yet (the owner's) |
| Subcode: the 64-word buffer, its pointer and the level 6 interrupt, from a .sub file beside the cue sheet (CD+G) | ✅ Unit tests; CHD subcode isn't read |
| Timer, stopwatch, font generator, backup RAM | ✅ Unit tests |
| CDD: TOC reports, seek, play, pause, stop, scan, the status checksum | ✅ Unit tests; seek times are estimates |
| CDC: decoding into its buffer, the host ports, DMA to PRG-RAM, Word RAM and PCM RAM | ✅ Unit tests |
| RF5C164 PCM: steps, loops, envelope, pan | ✅ Unit tests |
| Graphics ASIC: stamp maps, sizes, flips, rotations, repeat | 🟡 One drawing checked; the rotations' directions aren't checked against hardware |
| CD-DA through the fader | 🟡 Plays; the fader curve is linear |
| Booting a real BIOS and discs | Not checked yet (the owner's) |

Real games are the owner's to try; see `docs/plans/phase-13.md`.

## Super Nintendo / Super Famicom (Phase 14)

| Part | State |
|---|---|
| 65C816: SingleStepTests 65816 (5,080,400 tests, native and emulation mode) | ✅ All pass, including every cycle count |
| SPC700: SingleStepTests spc700 (256,000 tests) | ✅ All pass, including cycle counts |
| Boot program (GameVault's own, not Nintendo's IPL): the upload protocol | ✅ Unit test uploads and runs a program |
| S-DSP: BRR, envelopes, pitch, noise, echo | 🟡 A voice plays at its register's pitch; not checked against hardware recordings (interpolation uses a computed Gaussian) |
| Memory map: LoROM, HiROM, ExHiROM; SRAM; copier headers | ✅ Unit tests |
| DMA and HDMA; multiplier/divider; NMI/IRQ; joypad auto-read and serial | ✅ Unit tests |
| PPU: modes 0–7, sprites, windows, colour math, brightness, hi-res | 🟡 Mode 1, mode 7, sprites, windows and colour math have pixel tests; other modes are implemented without their own tests |
| Interlace, mid-line register changes, exact PPU timing | Not yet |
| Super FX (GSU-1/GSU-2): instructions, pipeline delay slot, cache, ROM buffer, PLOT/RPIX, bus ownership, IRQ | 🟡 Unit tests on hand-assembled GSU programs and a whole console starting the GSU; no freely redistributable Super FX test ROM to check against; real games are the owner's |
| DSP-1/2/3/4 (µPD7725, with the player's own program ROM): OP/RT/JP/LD, multiplier, DR/SR handshake | 🟡 Unit tests on hand-assembled µPD7725 programs and a whole console talking to one; real games with the player's DSP dump are the owner's |
| SA-1: second 65C816, I-RAM, BW-RAM and its bitmap view, Super MMC, arithmetic, variable-length reads, timer, DMA, character conversion 1 and 2 | 🟡 Unit tests, and a whole console with both CPUs talking; no freely redistributable SA-1 test ROM; real games are the owner's |
| S-DD1: memory controller and the decompressor on the DMA | 🟡 Unit tests for the mapping, the DMA hookup and save states; the decoder follows Andreas Naive's description but hasn't been checked against real compressed data |
| OBC1 | ✅ Unit test of its sprite table |
| ST010/ST011 (µPD96050, with the player's own program ROM): two program halves, shared data RAM (the ST010's save) | 🟡 Unit tests on hand-assembled programs; real games are the owner's |
| Cx4, SPC7110, ST018, S-RTC | Not yet: such games get a message naming the chip |

Real games are the owner's to try; see `docs/plans/phase-14.md`.

## Sony PlayStation (Phase 15)

| Part | State |
|---|---|
| R3000A: PeterLemon `CPUTest` (29 programs: ALU, shifts, MULT/DIV, loads and stores) | ✅ All pass |
| R3000A: delay slots, exceptions, COP0, cache isolation, instruction cache | ✅ Unit tests; coprocessor rules follow the ps1-tests `cpu/cop` hardware log |
| GTE: every command and register quirk | 🟡 PeterLemon's AVSZ and NCLIP programs pass, plus unit tests; ps1-tests `gte/test-all` (hardware log) not run yet |
| GPU rasterizer: Gouraud triangles (with dithering), quads and seams, texture and colour interpolation, lines | ✅ Pixel for pixel with ps1-tests VRAM dumps from a real console (`triangle`, `quad`, `uv-interpolation`, `lines`) |
| GPU: PeterLemon's 25 drawing programs (textures at each depth, windows, clipping, mask, rectangles, transfers) | ✅ Golden images. PeterLemon's own reference PNGs are captures with a gamma curve, and their polygon edges sit one pixel left of the hardware dumps above, so they aren't used as references |
| GPU timing (GPUSTAT busy bits, command time) | Not yet: commands finish at once |
| DMA (block, request, linked list, OTC, DICR); timers; interrupts | ✅ Unit tests |
| CD-ROM: commands, responses and interrupts, reading by DMA, GetID region, TOC | ✅ Unit tests with discs built in the tests |
| XA-ADPCM and CD-DA | 🟡 Decoding and playback have unit tests; XA is resampled by linear interpolation, not the hardware's filter |
| SPU: ADPCM voices, ADSR, pitch, noise, reverb, CD input, IRQ, transfers | 🟡 Unit tests; transfers match the ps1-tests `memory-transfer` data; reverb runs without the hardware's resampling filters |
| MDEC: 4/8/15/24-bit decoding | 🟡 Within ±1 of the ps1-tests hardware logs (the IDCT's rounding isn't documented) |
| Controllers: digital pad; memory cards (read, write, ID) | ✅ Unit tests; a new card comes formatted |
| Disc images: .cue/.bin, .chd (cdzl, cdlz, cdfl), .m3u | ✅ Unit tests; CHD verified against chdman's output |
| Fast boot and PS-EXE loading | ✅ Unit tests with a stand-in BIOS |
| Whole machine: PeterLemon's `printgpu` demo through the frame loop | ✅ Golden image |
| DualShock: digital, analog and config modes, the motors (passed to the player's pad); 24-bit display at full depth | ✅ Unit tests |
| GunCon and mouse (port 1, from Game Details) | ✅ Unit tests; aiming against real games is the owner's to check |
| Multitap, Justifier; GameShark codes | Not yet |

Real discs need the owner's BIOS (Settings → PlayStation); real games are the owner's to try. See
`docs/plans/phase-15.md`.

## Nintendo 64 (Phase 16)

| Part | State |
|---|---|
| VR4300: PeterLemon `CPUTest/CPU` (43 programs: arithmetic, 64-bit shifts and multiply/divide, loads and stores, LL/SC) | ✅ All pass |
| VR4300: COP0 registers and Cause after a cold boot (`CPUTest/CP0`, recorded on hardware) | ✅ Pass |
| VR4300: TLB, exceptions, branch-likely, Count/Compare | ✅ Unit tests |
| FPU: PeterLemon `CPUTest/CP1` (28 programs: arithmetic, conversions, rounding, all 16 compares) | ✅ All pass, plus 573 unit tests of the rounding modes, exceptions and FR modes |
| CPU timing (`CPUTIMINGNTSC`, `CP1TIMINGNTSC`: exact loop counts against the VI) | 🟡 Counts within 0.3% of the hardware's on average for integer instructions and 1.3% for FPU ones (worst 3.1%); not exact, because the hardware's counts include bus delays from the VI's framebuffer reads |
| RSP: PeterLemon `RSPTest` (scalar unit, every vector instruction including the reserved opcodes, transposes, IMEM, SP DMA alignment) | ✅ All pass |
| RDP: PeterLemon's 115 drawing programs through the whole console (CPU, RSP over XBUS, RDP, VI) | ✅ Golden images; 105 are also held to their reference pictures (89–100% of pixels identical; the references are an emulator's captures, some a row off) |
| RDP: coverage anti-aliasing, the VI's filters (anti-aliasing, divot, gamma) | 🟡 Coverage is computed per pixel but the hidden RDRAM bits aren't kept; the VI shows the framebuffer without its filters |
| PI DMA, including unaligned addresses and odd lengths (`DMAAlignment-PI`, recorded on hardware) | ✅ All pass |
| VI: 16-bit and 32-bit framebuffers (32-bit shown at full depth) | ✅ Unit tests |
| SI and the joybus: controllers, Controller Pak, EEPROM | ✅ Unit tests |
| SRAM and FlashRAM | ✅ Unit tests (FlashRAM follows Mupen64Plus's command set) |
| AI: audio buffers and the DAC rate | ✅ Unit tests and PeterLemon's PCM demos (not committed: their samples' origin isn't stated) |
| Boot: simulated PIF, the cartridge's own IPL3; CIC seeds 6101–6106 | 🟡 PeterLemon's programs boot through krom's IPL3; Nintendo's IPL3s are the owner's to try with their cartridges |
| Rumble Pak (probe and motor; passed to the player's pad through SDL) | ✅ Unit tests; how it feels on a real pad is the owner's to check |
| 64DD, Transfer Pak, mouse, VRU | Not yet |

Real cartridges are the owner's to try; see `docs/plans/phase-16.md`.


## PlayStation 2 (Phase 17)

| Part | State |
|---|---|
| R5900: ps2autotests `cpu/ee` (alu, branch, branchdelay, lsu, lsudelay, muldiv), recorded on hardware | ✅ All pass through the whole console, and replayed instruction by instruction |
| R5900 MMI: ps2autotests `cpu/ee_simd` (6 programs) | ✅ All pass |
| FPU (non-IEEE: truncation, saturation, the multiplier's dropped carry): ps2autotests `cpu/ee_fpu` (9 programs) | ✅ All pass |
| COP0: TLB, exceptions, Count/Compare; ps2autotests `cpu/ee_cop0/performance` | ✅ Pass (performance counters approximate) |
| VU0 macro mode: ps2autotests `cpu/vu0_macro` (2) | ✅ All pass |
| VU micro mode: ps2autotests `vu/lower` (branch, fdivdelay, integer, random), `vu/upper/clip`, `vu/games/triace` | ✅ All pass |
| VU1's EFU: ps2autotests `vu/lower/efu` | 🟡 184 of 208 lines exact; 24 differ in the last bit or two (the hardware's series aren't known exactly) |
| DMAC, scratchpad DMA and VIF: ps2autotests `dma/**` (11 programs) | ✅ All pass |
| GS: ps2autotests `gs/label`, `gs/signal` | ✅ Pass |
| GameVault's EE kernel: ps2autotests `kernel/ee/**` (threads and interrupt order, 10 programs), `memory/ee/default` | ✅ All pass |
| IOP programs (ps2autotests `.irx`, 23) | Not run: they need an IOP kernel (left for later) |
| GS drawing: GameVault's homebrew (`tests/Ps2Homebrew`: PATH3 primitives, textures with CLUTs, blending and Z, a VU1 microprogram over PATH1 with PATH2) | ✅ Golden images and pixel probes; `mmi-fpu-check` 16/16 |
| IOP, SIF, CDVD, SIO2 (DualShock 2, 8 MB memory card), SPU2, IPU | ✅ Unit tests; the EE and IOP running a stand-in BIOS together (SIF handshake and DMA both ways, CDVD reads, a GS frame) |
| Booting the player's BIOS, ELF and disc fast boot | 🟡 Checked with a stand-in BIOS; real BIOSes and discs are the owner's to try |
| GS mipmapping: TEX1's LOD, MIPTBP1/2 and MTBA, nearest and blended levels | ✅ Unit tests |
| Memory card slot 2 (one shared card); L2/R2 pressure from analog triggers | ✅ Unit tests |
| GS anti-aliasing, the IPU's command timing, PS1 mode, multitap | Not yet |

Real BIOSes and discs are the owner's to try; see `docs/plans/phase-17.md`.

## Atari 2600 (Phase 19)

| Part | State |
|---|---|
| 6507: GameVault's NMOS 6502 (Dormann's functional test, as for the Apple II) on a 13-bit bus | ✅ |
| TIA: playfield (repeat, reflect, score, priority), players (copies, sizes, reflection, delay), missiles, ball, RESPx positions, HMOVE and its black bar, collisions, mid-line register changes, VSYNC/VBLANK framing, fire buttons | ✅ Unit tests on hand-written programs |
| TIA sound (Ron Fries' counters) | 🟡 A pure tone plays at its divider's pitch; the noise modes aren't checked against hardware recordings |
| RIOT: RAM, joysticks, console switches, timer | ✅ Unit tests |
| Bank switching: 2K/4K, F8/F6/F4 (and SuperChip), E0, FE, 3F, FA | ✅ Unit tests and a save/load round trip each |
| Paddles, keypads, DPC, E7, Supercharger, PAL/SECAM palettes, the TIA's HMOVE quirks beyond the black bar | Not yet |

No freely redistributable 2600 test ROM is used yet; real games are the owner's to try (`docs/plans/phase-19.md`).

## Atari 5200 (Phase 19)

| Part | State |
|---|---|
| 6502C: GameVault's NMOS 6502, halted for ANTIC's DMA and WSYNC | ✅ Unit tests (cycles a frame with and without DMA) |
| ANTIC: display lists (blank, JMP, JVB, LMS), modes 2–F, character sets and CHACTL, narrow/normal/wide playfields, HSCROL and VSCROL, player/missile DMA, DLI and VBI NMIs, VCOUNT, WSYNC | ✅ Unit tests on hand-written programs; VSCROL by the hardware's rule but untested |
| GTIA: players and missiles (sizes, VDELAY, fifth player), PRIOR's priority logic, collisions, hi-res colours, GTIA modes 9–11, triggers | ✅ Unit tests; mid-line colour changes land at the colour clock the CPU reaches |
| POKEY: four channels, polys, 16-bit pairs, high-pass filters, timers and their IRQs, pots, keypad scan, RANDOM | 🟡 Timers and pure tones checked; the noise modes aren't checked against hardware recordings |
| Keypad codes | 🟡 From published notes, not a real console |
| 40K boards, trak-ball, PAL 5200s | Not yet |

No freely redistributable 5200 test ROM is used yet; the BIOS and real games are the owner's to try
(`docs/plans/phase-19.md`).

## GameCube (Phase 18, milestone 1)

| Part | State |
|---|---|
| Gekko: integer, branches, CR, rotates, loads/stores (update, indexed, byte-reversed, multiple, string), SPRs | ✅ Unit tests on hand-assembled programs |
| Gekko FPU: double and single arithmetic, fused multiply-add, conversions, compares, FPSCR | ✅ Unit tests; `fres` and `frsqrte` are exact, not the hardware's estimates |
| Paired singles and quantised loads/stores (GQRs) | ✅ Unit tests |
| Exceptions, `rfi`, decrementer, external interrupts, BATs | ✅ Unit tests |
| VI: the XFB in YUV, display interrupts | ✅ Unit tests |
| PI, SI controllers, EXI RTC/SRAM, the DSP's boot handshake and ARAM DMA | ✅ Unit tests |
| DOL and ELF loading with the boot ROM's start-up done by GameVault | ✅ Unit tests |
| GPU (GX), discs, DSP audio, memory cards, recompiler | Not yet (milestones 2–4) |

No freely redistributable GameCube homebrew is bundled yet; homebrew is the owner's to try (`docs/plans/phase-18.md`).
