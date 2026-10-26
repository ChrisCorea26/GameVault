# Phase 13 — Sega Genesis / Mega Drive

**Goal (SPEC §7.11):** GameVault plays Genesis / Mega Drive cartridges with correct picture and sound. It
needs no BIOS: GameVault emulates a model 1 without TMSS, so no Sega code is needed.

**Done when (SPEC §12):** the 68000 test suites pass, and Genesis test programs and homebrew run with correct
sound.

## 1. 68000 (`GameVault.Core.Cpu.M68000`)
- **Instructions:** every 68000 instruction and addressing mode, at byte, word and long sizes:
  - arithmetic and logic, including BCD (ABCD/SBCD/NBCD) with the 68000's undocumented V flag;
  - shifts and rotates;
  - bit operations;
  - MOVEM, MOVEP, CHK, DIVU/DIVS (with overflow) and MULU/MULS;
  - TAS (its write-back works on the Genesis, unlike some other machines);
  - TRAP/TRAPV, RTE/RTR/RTS, LINK/UNLK, STOP and RESET;
  - the privileged SR instructions.
- **Exceptions:**
  - address errors (odd word and long accesses) with the 68000's 14-byte frame;
  - illegal instructions and the line A/F traps;
  - privilege violations, trace, and divide by zero;
  - autovectored interrupts at levels 1–7, masked by the IPL bits.
- **Timing:** instruction cycle counts follow the 68000 User's Manual tables (effective-address
  calculation, the long-operand and register-direct cases, the data-dependent MULU/MULS/DIVU/DIVS times
  and the shift counts). The CPU reports cycles per instruction; the bus runs the other chips to catch up.
- **Tests:**
  - **SingleStepTests 680x0:** one test per instruction run against the stated state before and after.
    The suite is about 200 MB of gzipped JSON with no licence file. It is fetched by the test-ROM
    scripts, never committed, and listed in `SOURCES.md` with that note. It runs in a Release CI job
    (`GAMEVAULT_RUN_68K=1`), like the Z80 exerciser.
    - Required: registers, SR and RAM after every test match.
    - Cycle counts are reported but not required in this phase.
  - **Unit tests:** flags, exceptions, interrupts and timing for representative instructions.

## 2. Bus and the rest of the machine (`GameVault.Core.Genesis`)
- **Memory map:**

  | Range | Contents |
  |---|---|
  | $000000–$3FFFFF | cartridge (plus SRAM where the header says so) |
  | $A00000–$A0FFFF | the Z80's space, while the 68000 owns its bus |
  | $A10000–$A1001F | version register (region, PAL/NTSC) and the controller ports |
  | $A11100 / $A11200 | Z80 BUSREQ and RESET |
  | $A130F1–$A130FF | SRAM enable and the Super Street Fighter II bank registers |
  | $C00000–$C0001F | VDP and the SN76489 |
  | $E00000–$FFFFFF | 64 KB work RAM, mirrored |

  The unused areas don't lock up (no TMSS, so the bus doesn't hang).
- **Z80:** the existing core with 8 KB of RAM, the YM2612 at $4000, the PSG at $7F11, and the 32 KB
  bank window onto the 68000's bus at $8000 (the bank register at $6000). It gets the VDP's vertical
  interrupt.
- **Timing:** the 68000 runs at 7.67 MHz (NTSC) or 7.60 MHz (PAL). The Z80 runs at 3.58 MHz. The master
  clock (53.69 / 53.20 MHz) keeps them in step: the bus advances the VDP, the Z80 and the sound chips by
  master cycles after each 68000 instruction.
- **Cartridges:**
  - `.md`, `.gen` and `.bin` (a raw ROM; a `.bin` counts only with "SEGA" at $100), and `.smd`
    (interleaved 16 KB blocks with a 512-byte header, de-interleaved on load).
  - The header at $100 gives the region and the SRAM range (`RA`, $1B0).
  - Battery SRAM is byte-wide (odd or even addresses) or word-wide, and is saved like NES battery RAM.
  - The SSF2 mapper bank-switches ROMs over 4 MB.
  - EEPROM saves (I²C) are left for later.

## 3. VDP (`GenesisVdp`)
- **Mode 5:**
  - the two scroll planes A and B, with 32/64/128-cell sizes;
  - the window plane;
  - 80 sprites (64 in H32) with the per-line limits: 20 sprites or 320 pixels in H40, 16 or 256 in H32;
  - sprite masking, sprite collision and overflow flags;
  - per-tile priority and shadow/highlight.
- **Scrolling:** horizontal scroll per screen, per 8 lines or per line; vertical scroll per screen or per
  2-cell column.
- **Memory:** 64 KB VRAM, 64-word CRAM (9-bit colour), 40-word VSRAM.
- **The control port:** the two-word command latch and auto-increment.
- **DMA:** 68000 → VRAM/CRAM/VSRAM (the 68000 is stalled for the transfer), VRAM fill and VRAM copy.
- **Counters and interrupts:**
  - the HV counter;
  - H-interrupts from the line counter (level 4) and V-interrupts (level 6), with their enables and
    pending flags.
- **Status:** FIFO empty/full approximated, VBlank, HBlank, odd frame, DMA busy, PAL.
- **Picture:**
  - H32 (256 wide) and H40 (320 wide);
  - V28 (224 lines) and V30 (240 lines, PAL);
  - interlace mode 1 shown as normal; interlace mode 2 (double resolution) is left for later.
- **Rendering:** the frame is drawn line by line, so mid-frame changes from H-interrupts (raster effects,
  palette changes) show. The frame size follows the mode up to 320×240 and goes through the
  variable-size video path from Phase 12.
- **The TMS/SMS modes:** Master System compatibility mode (the Power Base Converter) is left for later.

## 4. Sound
- **YM2612 (`Ym2612`):**
  - 6 FM channels of 4 operators each, with the 8 algorithms and operator 1's feedback;
  - envelopes: attack, decay, sustain and release with key scaling, and SSG-EG;
  - detune and multiple, and the LFO with AMS/PMS;
  - channel 3's special mode (per-operator frequencies);
  - channel 6's DAC;
  - timers A and B with their status flags;
  - left/right panning;
  - the busy flag (approximated).
- **Sine and envelope tables:** generated at start-up from the published formulas (no copied ROM tables).
  - The 9-bit DAC ladder effect is included.
  - The output runs at the chip's own rate (master ÷ 144 ÷ 7 ≈ 53.3 kHz), then through the
    band-limited resampler.
- **SN76489:** the Master System core, at the Z80 clock.
- **Mixing:** the YM2612 is mixed with the PSG at the usual levels (the PSG about a quarter as loud).
- **Reference:** the NESdev-like knowledge base for the Genesis is SpritesMind and the Sega Retro "YM2612"
  pages, with Nemesis' hardware notes.

## 5. Input
- **Pads:**
  - the 3-button pad: Up, Down, Left, Right, A, B, C, Start, read through the TH-select multiplexing;
  - the 6-button pad: X, Y, Z and Mode, with the TH-toggle counter and timeout.
- **`ControllerState`** gains C, X, Y, Z and Mode bits.
  - Keyboard defaults: Z/X/C for A/B/C, A/S/D for X/Y/Z, Enter for Start and a free key for Mode.
  - Controllers: face buttons and shoulders.
  - Settings → Input lists the Genesis buttons. NES and Sega 8-bit mappings are unchanged.
- **Per game:** the 3- or 6-button choice goes in Game properties (`pad`). The default is 6-button; it
  falls back to 3 for games known to break with it.

## 6. Platform, library and UI
- **The library system:** `Genesis`, named "Genesis" (or "Mega Drive" for European and Japanese
  regions).
- **Files:** the scanner and catalog import add the extensions above, with a header check for `.bin`.
- **Region:** auto from the header (NTSC USA/Japan, PAL Europe), overridable in Game properties
  (`region`).
- **RetroAchievements:**
  - console id 1;
  - the hash is the MD5 of the (de-interleaved) ROM, as rcheevos does;
  - memory is the 64 KB work RAM, then SRAM.
- **Everything that uses `IEmulatedConsole`:** save states, rewind, run-ahead, timeline, challenges and
  netplay. The core is deterministic.
- **Sample data:** a Genesis sample game in the library. The `--sample-data` Player shows it with the
  sample picture.

## 7. Tests
- **68000:** the SingleStepTests suite (Release job), plus unit tests.
- **Bus:** the memory map, the Z80 bus request and reset, the Z80 bank window, the version register, and
  the 3- and 6-button pad protocols.
- **VDP:** synthetic 68000 programs written in the tests (no third-party ROMs) that set up planes,
  sprites, scrolling, the window, shadow/highlight and DMA. They are checked against small golden images
  and pixel probes. Also the HV counter and the interrupt timing.
- **YM2612:**
  - a single sine operator plays at the frequency its F-number and block give;
  - attack, decay and release follow their rates;
  - the algorithm routing;
  - the timers;
  - DAC output;
  - panning.
- **Cartridges:** SMD de-interleaving, header parsing, the SRAM mapping and the SSF2 banks.
- **State:** save-state round trips and determinism across replays. Host loading and 320×224 frames.

## Order of work
1. 68000 core with unit tests, then the SingleStepTests harness and fixes until it passes.
2. Cartridge, bus, work RAM, the Z80 subsystem, the I/O ports and pads.
3. VDP: registers, ports and DMA, then the planes, sprites, window and scrolling, then the timing and
   interrupts.
4. YM2612 and the mixer.
5. Platform: loading, library, input mapping, settings, RetroAchievements, sample data.
6. Docs: compatibility page, README status, plan results.

## Owner checklist
- Try a few of your own cartridge dumps (3- and 6-button games, a game with SRAM saves, a PAL game) and
  list anything that misbehaves.
