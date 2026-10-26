# Phase 14 — Super Nintendo

**Goal (SPEC §7.11):** GameVault plays Super Nintendo / Super Famicom cartridges with correct picture and sound.
This is the last phase in this run of work: the owner asked to stop after Phase 14, with everything merged.

**Done when (SPEC §12):** the 65C816 and SPC700 test suites pass, and SNES test programs render correctly.

## 1. 65C816 (`GameVault.Core.Cpu.W65816`)
- **Modes:** native and emulation mode, with 8/16-bit accumulator and index registers (M and X flags).
- **Instructions:** every opcode and addressing mode, including:
  - the direct page and stack-relative modes;
  - long addressing, and the block moves MVN/MVP;
  - BCD arithmetic in both widths.
- **Emulation-mode quirks:** direct-page wrapping, the stack in page 1, and the wrapping of some
  instructions.
- **Interrupts:** NMI, IRQ, BRK, COP, ABORT and RESET with the native and emulation vectors; WAI and STP.
- **Timing:** cycles per instruction, including the extra cycles for 16-bit operands, a direct page not
  on a page boundary, and index page crossing.
  - The console turns them into master clocks through the memory map's access speeds: 6, 8 or 12 per
    access.
- **Tests:** the SingleStepTests 65816 suite (MIT): 20,000 tests per opcode (native and emulation), about
  10 million in all.
  - At 2.7 GB of JSON it is fetched only by its own CI job, which caches the download.
  - Registers, flags and memory must match. Cycle counts are reported.

## 2. SPC700 and the S-DSP (`GameVault.Core.Snes.Audio`)
- **SPC700:** every instruction, including the multiply and divide, the 16-bit word operations and the bit
  operations.
  - **Tests:** the SingleStepTests spc700 suite (MIT, 84 MB).
- **The sound module:**
  - 64 KB of audio RAM;
  - the three timers;
  - the four I/O ports to the 65C816;
  - the DSP register window.
- **IPL boot ROM:** Nintendo's 64-byte ROM is copyrighted, so GameVault doesn't ship it. Instead
  GameVault writes its own boot program for $FFC0–$FFFF. It speaks the same documented upload protocol:
  - the $AA/$BB handshake;
  - block transfers with the counter echo;
  - the jump to the uploaded program.

  It uses the same entry point and leaves the same register state, so games that upload through it, or
  jump back into it, work.
- **S-DSP:**
  - eight voices of BRR-compressed samples, with the Gaussian interpolation;
  - ADSR and GAIN envelopes;
  - pitch modulation, noise, and echo with its FIR filter and feedback;
  - main and echo volumes, and stereo, mixed to mono as for the Genesis;
  - 32 kHz output through the band-limited resampler.

  The Gaussian table is computed from its published formula.

## 3. The console (`GameVault.Core.Snes`)
- **Memory:**
  - 128 KB of WRAM and the WRAM port ($2180–$2183);
  - the 21.477 MHz master clock, with the FastROM bit ($420D) and the access speeds per region.
- **The CPU's I/O:**
  - the multiplier and divider ($4202–$4206 → $4214–$4217);
  - NMI ($4200) and H/V IRQ ($4207–$420A), with the RDNMI and TIMEUP flags;
  - HVBJOY;
  - joypad auto-read into $4218–$421F, and the manual serial port ($4016/$4017).
- **DMA:** eight channels, general-purpose DMA (all transfer modes and directions).
- **HDMA:** direct and indirect tables, repeat and the line counter, started per frame and run each line.
- **Cartridges:**
  - **Maps:** LoROM, HiROM and ExHiROM, found by scoring the internal header at $7FC0/$FFC0/$40FFC0
    (checksum, map mode, reset vector).
  - **Copier headers:** a 512-byte header is skipped.
  - **SRAM:** battery SRAM from the header's size, saved like other battery RAM.
  - **Region:** from the header's country code.
  - **Enhancement chips** (SuperFX, SA-1, DSP-1…) are left for later phases. A game that needs one gets a
    clear message instead of a black screen.

## 4. The S-PPU (`SnesPpu`)
- **Backgrounds:**
  - modes 0–7, with 2/4/8-bit tiles and 8×8 or 16×16 tiles;
  - scrolling, tilemap sizes, priorities, and mode 1's BG3 priority bit;
  - mosaic;
  - offset-per-tile in modes 2, 4 and 6;
  - the hi-res modes 5 and 6, and pseudo-hi-res.
- **Mode 7:** the matrix, wrapping and fill options, EXTBG, and direct colour.
- **Sprites:**
  - 128 sprites with the two size choices;
  - the 32-per-line and 34-tile limits, with the range-over and time-over flags;
  - priority rotation.
- **Windows:** two windows with the four logic operations, per layer and for colour math.
- **Colour math:** main and sub screens, add or subtract, half; the fixed colour; clip and prevent;
  brightness and forced blank.
- **Memory:** VRAM with its address remapping and increment modes, CGRAM and OAM with their write
  latches.
- **Counters:** the H/V counter latches ($2137, $213C/$213D).
- **Status:** $213E/$213F.
- **Output:**
  - 256×224 (or 239 lines with overscan);
  - 512 wide in the hi-res modes, so the frame is 512 wide whenever a hi-res line appears;
  - 15-bit colour with brightness;
  - shown at 4:3.
- **Rendering:** line by line, so HDMA and mid-frame changes show.

## 5. Input
The SNES pad has B, Y, Select, Start, the D-pad, A, X, L and R. On `ControllerState`:
- **SNES B and A** are its B and A (the NES's places).
- **SNES Y and X** are its new Y and X.
- **L and R** are L and R.

Keyboard defaults: Z/X stay B/A, and A/S, whose NES role is turbo B/A, become Y/X for SNES games, which have
no turbo keys (input.md §6.3).

Controllers are positional, as on the real pad:
- B south, A east, Y west, X north;
- L and R on the shoulders.

## 6. Platform, library and UI
- **The library system:** `Snes`, named "Super Nintendo" ("Super Famicom" for Japanese games).
- **Files:** `.sfc` and `.smc`, also inside `.zip`.
- **Hashing:** copier headers are skipped, so No-Intro DATs match.
- **Catalog import:** recognizes "Super Nintendo" DATs.
- **Game properties:** `region` ("ntsc" or "pal").
- **RetroAchievements:**
  - console id 3;
  - the hash is the MD5 of the ROM without a copier header, as rcheevos does;
  - memory is WRAM ($000000–$01FFFF), then SRAM ($020000–).
- **Everything that uses `IEmulatedConsole`:** save states, rewind, run-ahead, timeline, challenges and
  netplay.
- **Sample data:** an SNES game in the sample library.

## 7. Tests
- The two single-step suites (CPU and sound CPU) in Release CI jobs.
- **Unit tests:**
  - **Memory map:** LoROM/HiROM mapping, header scoring, SRAM.
  - **DMA and HDMA.**
  - **Interrupts and registers:** NMI/IRQ timing, the multiplier/divider, joypad auto-read.
  - **Picture:** a background in each mode, mode 7, sprites, windows and colour math, from tiles written
    in the tests and checked with pixel probes.
  - **Audio:** the IPL upload protocol from the 65C816 side, BRR decoding, and a DSP voice playing a sample
    at the pitch its register gives.
  - **Save states:** round-trips and determinism.
- **Host:** loading and frames.

## Results
- **65C816:** passes all 5,080,400 tests of the SingleStepTests 65816 suite (native and emulation mode), with
  every cycle count matching.
  - Every bus and internal cycle goes through the bus, so the console charges each access its speed.
  - MVN/MVP move one byte per step, so interrupts can come between bytes; the harness steps a block move to
    its end.
  - WAI and STP take 4 cycles, as the suite counts.
- **SPC700:** passes all 256,000 tests of the SingleStepTests spc700 suite, including cycle counts.
- **Both suites** are fetched only by the "65816 and SPC700" CI job, which caches the 2.6 GB 65816 download.
- **Boot program:** GameVault's own boot program replaces Nintendo's IPL ROM. A unit test uploads a program
  through the documented protocol and runs it.
- **S-DSP:**
  - eight voices of BRR with the four filters, 4-point interpolation, ADSR and GAIN;
  - pitch modulation, noise, and echo (8-tap FIR, feedback, buffer in audio RAM);
  - mixed to mono at 32,040 Hz.
- **Machine:**
  - **Timing:** the 65C816 runs on the master clock with per-address speeds, FastROM, and the DRAM refresh.
  - **Memory and DMA:** WRAM and its port, eight-channel DMA, and HDMA (direct and indirect, repeat).
  - **CPU registers:** the multiplier and divider, NMI, H/V IRQ, and joypad auto-read plus the serial port.
- **Cartridges:**
  - LoROM, HiROM and ExHiROM by header score, and copier headers skipped;
  - SRAM from the header, saved when the header says there's a battery;
  - PAL from the country code.
  - Enhancement-chip games get a message naming the chip.
- **PPU:**
  - modes 0–7 with 8×8/16×16 tiles, mosaic and offset-per-tile;
  - hi-res and pseudo-hi-res: the frame becomes 512 wide, with other lines doubled;
  - mode 7 with its matrix, wrap and fill options, EXTBG and direct colour;
  - 128 sprites with the range and time limits;
  - windows with their logic, main/sub screens, colour math with clip/prevent, brightness and forced blank;
  - VRAM remapping, the scroll and mode 7 latches, and the counter latches.
  - The frame holds BGR555 colours, so the video path's palette mask grew to 32,768 entries.
- **Platform:**
  - The library finds `.sfc`/`.smc` files, and headered copies hash like plain ones.
  - The catalog import recognizes "Super Nintendo" and "Super Famicom" DATs.
  - Game properties take `region`.
  - Controllers use the SNES layout, and the keyboard's turbo keys become Y and X.
  - RetroAchievements: rcheevos' SNES hash (header skipped) and memory (WRAM, then SRAM at $20000).
  - The sample library has an SNES game.
- **Tests:** the two suites; cartridge mapping; a boot program; DMA and HDMA; the multiplier, divider and
  joypads; mode 1 with a sprite; colour math and windows; mode 7; the boot-program upload; a DSP voice's pitch;
  save-state replay; host loading; and the chip message.
- **Changed from the plan, or left for later:**
  - **Rendering and timing:** each line is drawn as it starts, from the registers then. Mid-line changes, exact
    PPU fetch timing and interlace aren't modeled.
  - **The DSP:** its interpolation uses a computed Gaussian rather than the chip's table, and the
    envelope-rate counter offsets aren't modeled.
  - **Enhancement chips:** left for later phases, as planned.
  - **`--sample-data`:** the Player shows the standard test pattern for the SNES sample game.

## Order of work
1. 65C816 with the single-step harness.
2. SPC700 with its harness, then the S-DSP and GameVault's IPL.
3. Memory map, cartridges, I/O registers, DMA/HDMA.
4. S-PPU.
5. Platform, input, library, RetroAchievements, sample data, docs.

## Owner checklist
- Try a few of your own cartridge dumps (LoROM and HiROM games, a game with battery saves, a mode 7 game),
  and list anything that misbehaves.
- Games with enhancement chips won't run yet.
