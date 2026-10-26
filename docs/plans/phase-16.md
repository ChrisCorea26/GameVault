# Phase 16 — Nintendo 64

**Goal (SPEC §7.11):** GameVault plays Nintendo 64 cartridges: picture, sound, controllers (with the analog stick)
and cartridge saves.

**Done when (SPEC §12):**
- the CPU and RSP test ROMs pass;
- N64 test ROMs and homebrew render correctly.

SPEC §7.11 asks the later phases to scope a first playable milestone and list what remains. This phase's
milestone is a cartridge booting to gameplay with picture, sound, the controller and saves, rendered by a
software RDP in the core. Everything beyond that is under "Left for later".

## Scope decisions
- **Rendering in software first.** SPEC §7.11 calls for GPU-accelerated graphics through Direct3D. This phase's
  RDP is a software rasterizer in `GameVault.Core` instead:
  - It is deterministic, so save states, rewind, run-ahead, netplay and challenges work exactly as on the other
    systems.
  - The RDP test programs check it pixel for pixel on every OS in CI, which a Direct3D renderer can't do on Linux.
  - Its output goes through the existing Direct3D 11 presenter like every other system.

  A GPU renderer (upscaling, and speed on heavy scenes) is left for later. It will use this rasterizer as its
  reference.
- **CPU: an optimized interpreter.** SPEC allows a recompiler or an optimized interpreter. This phase writes the
  interpreter: decoded fields cached per instruction, and fast paths for the unmapped KSEG0/KSEG1 segments that
  most code and data use. A recompiler is left for later, if the interpreter can't reach full speed.
- **No Nintendo boot code.** The PIF ROM's boot (IPL1/IPL2) is simulated (high-level emulation), so no PIF ROM
  is needed. The cartridge's own boot code (IPL3) runs as written. GameVault never ships either.

## 1. VR4300 CPU (`GameVault.Core.Cpu.R4300`)
- **Instructions:** the MIPS III set in 64-bit mode (n64brew: "MIPS III instructions"):
  - 64-bit registers, with 32-bit results sign-extended;
  - the branch delay slot, and the branch-likely forms that skip it when not taken;
  - the doubleword loads, stores, shifts, adds and multiply/divide (DMULT/DDIV…), with HI/LO;
  - LWL/LWR/LDL/LDR/SWL/SWR/SDL/SDR, LL/SC/LLD/SCD with LLbit;
  - the traps (TGE…, TEQI…), SYSCALL, BREAK, the overflow exception, and the reserved-instruction exception;
  - CACHE as a no-op for data (the caches aren't modelled for contents), SYNC.
- **COP0:**
  - Index, Random, EntryLo0/1, Context, PageMask, Wired, BadVAddr, Count/Compare (the timer interrupt),
    EntryHi, Status, Cause, EPC, PRId, Config, LLAddr, WatchLo/Hi, XContext, TagLo/Hi and ErrorEPC;
  - exceptions with their vectors (general, TLB refill, XTLB refill, BEV), the interrupt lines IP2–IP7, ERET;
  - the 32-entry TLB with TLBR/TLBWI/TLBWR/TLBP, odd/even pages and page masks, and the TLB miss, invalid and
    modified exceptions.
  - 32-bit and 64-bit addressing (Status.KX/SX/UX) for kernel mode; user and supervisor modes as documented.
- **COP1 (FPU):**
  - single, double, word and long formats in 32 or 64 registers (Status.FR);
  - ADD/SUB/MUL/DIV/SQRT/ABS/MOV/NEG, the conversions, ROUND/TRUNC/CEIL/FLOOR, and the 16 compares;
  - the four rounding modes of FCSR, done exactly: .NET always rounds to nearest, so the other modes adjust the
    result with its exact error (TwoSum and fused multiply-add);
  - FCSR's flags, enables and causes, with the unimplemented-operation exception for denormal and NaN inputs
    as the VR4300 raises it;
  - the coprocessor-unusable exception when Status.CU1 is clear.
- **Timing:** one cycle per instruction at 93.75 MHz, plus the multiply/divide latencies, and Count ticking at
  half the clock. Uncached RDRAM and I/O accesses cost extra cycles (approximate).

## 2. The machine (`GameVault.Core.Nintendo64`)
- **Memory:** 4 MB of RDRAM, or 8 MB with the Expansion Pak (on by default, as most homebrew and many games
  expect). RDRAM's registers and RI are answered so that the boot code's memory sizing finds the right size.
- **The RCP's interfaces** (n64brew: "Memory map"):
  - **MI:** the interrupt flags and masks (SP, SI, AI, VI, PI, DP), the version register.
  - **VI:** the registers, the line counter and the V interrupt, NTSC/PAL/MPAL timing.
  - **AI:** the two-entry DMA queue from RDRAM, the sample rate from the DAC rate, and the AI interrupt.
  - **PI:** DMA between RDRAM and the cartridge (both directions), with the domain timing registers and the
    PI interrupt; reads of the cartridge's ROM through the bus.
  - **SI:** DMA to and from PIF RAM, and the SI interrupt.
  - **SP and DP:** the RSP's registers, DMA, and the RDP's command registers (below).
- **Scheduling:** like the PlayStation, the CPU runs to the next device event (a VI line, an AI buffer end, a
  DMA end, a Count/Compare match), and devices catch up before their registers are touched. The RSP and the
  RDP run in slices between CPU events.
- **Clock:** 93.75 MHz CPU, 62.5 MHz RCP; VI timing from the VI's registers (NTSC 60 Hz, PAL 50 Hz).

## 3. PIF and controllers (`N64Pif`)
- **Boot:** the PIF's boot is simulated. It copies the cartridge's boot code to the RSP's DMEM, sets the
  registers the boot code expects (the CIC seed, the TV type, the reset type), and starts at $A4000040.
  - **The CIC:** found from a CRC of the boot code (6101, 6102, 6103, 6105, 6106 and their PAL versions, and
    libdragon's open-source IPL3). Unknown boot code is treated as a 6102.
- **Joybus** commands through PIF RAM: info/reset, read the controller, read/write the Controller Pak, read/write
  EEPROM, and the EEPROM's info.
- **The controller:** A, B, Z, Start, the D-pad, L, R, the four C buttons and the analog stick.
- **The Controller Pak:** 32 KB, formatted, one per game, saved next to the other saves as a raw `.mpk` file.
- **CIC challenge** (6105 games): the documented challenge algorithm.

## 4. The RSP (`N64Rsp`)
- **Scalar unit:** the MIPS I subset the RSP runs (no multiply/divide, no 64-bit), 4 KB IMEM and 4 KB DMEM,
  12-bit addresses that wrap, BREAK and the halt/broke flags, the semaphore, and the COP0 registers (its own DMA
  and the RDP's command registers).
- **Vector unit (COP2):**
  - 32 vector registers of 8 × 16 bits, the 48-bit accumulators, and the VCO, VCC and VCE flags;
  - every computational instruction: the multiplies (VMULF…VMADH), adds and subtracts with carry, VABS, the
    selects and compares (VLT, VEQ, VNE, VGE, VCL, VCH, VCR, VMRG), the logical ops, VSAR, VMOV and the
    reciprocals (VRCP, VRCPL, VRCPH, VRSQ…) with their tables computed as documented;
  - the element selectors;
  - every load and store: byte to quad, rest, packed (LPV/LUV/LHV/LFV and stores), the transposes (LTV/STV),
    LWV/SWV, with their wrapping in DMEM;
  - the reserved opcodes behaving as the hardware does (the PeterLemon RESERVED tests).
- **DMA:** RDRAM ↔ IMEM/DMEM with lengths, counts and skips; the DMA full/busy flags.
- **Timing:** one instruction per RCP cycle; the vector unit's pipeline stalls are left for later.

## 5. The RDP (`N64Rdp`)
- **Commands** from RDRAM or DMEM (XBUS): the eight triangle types (fill, shade, texture, Z, and their
  combinations), texture rectangles (and flipped), fill rectangles, the Set commands (scissor, other modes,
  colours, combine, texture image, Z image, colour image, tile, tile size, prim depth, convert, key), Load Block,
  Load Tile and Load TLUT, and the syncs (full sync raises the DP interrupt).
- **The pipeline:**
  - fill, copy, one-cycle and two-cycle modes;
  - edge walking with the hardware's fixed-point subpixel rules, and scissoring;
  - shade, texture and Z interpolation, with perspective correction;
  - TMEM (4 KB) with its interleaving, and the texture formats RGBA16/32, CI4/8 (with the TLUT), IA4/8/16, I4/8
    and YUV; clamp, mirror, mask and shift; point and bilinear filtering;
  - the colour combiner and alpha combiner in both cycles;
  - the blender, alpha compare, Z compare and update (with the Z modes), and colour dithering;
  - 16-bit (5551) and 32-bit colour images, and 8-bit for copies.
- **Coverage:** anti-aliasing coverage is approximated as full coverage this phase.

## 6. Video, audio and output
- **VI:** the frame is read from RDRAM at the VI's origin, width and scale, in 16-bit or 32-bit, at up to
  640×480 (interlaced modes alternate fields). Colours go into the 32,768-entry palette (32-bit pixels are
  rounded to 15 bits, as for the PlayStation's 24-bit mode). The VI's filters (anti-aliasing, divot) are left
  for later; the picture shows at 4:3.
- **AI:** 16-bit stereo samples from RDRAM, at the DAC rate the game sets, mixed to mono like the other systems,
  through the band-limited resampler.

## 7. Cartridges and saves
- **ROM formats:** `.z64` (big-endian), `.v64` (byte-swapped) and `.n64` (little-endian), all turned into
  big-endian when loaded. The header gives the name, the game code, the region and the boot code.
- **Save types:** 4 Kbit and 16 Kbit EEPROM, 32 KB SRAM and 128 KB FlashRAM.
  - The type comes from the homebrew header (libdragon's save-type byte) when the ROM has one.
  - Otherwise, from a small table of game codes in `data/gamedb/n64/saves.json`, which the owner verifies.
  - Otherwise, a 4 Kbit EEPROM, which answers on the joybus without getting in the way of a game that doesn't
    use it.
- Saves are battery saves, in the format other emulators read (raw, big-endian).

## 8. Input
The N64 controller maps onto `ControllerState` by position, like the SNES and PlayStation pads:
- **Buttons:** A is B (south), B is Y (west), Z is L2 (the left trigger), L and R are L and R, Start is Start.
- **C buttons:** on a game controller, the right stick. They also have bits: C-down is A (east), C-left is X
  (north), C-up is C and C-right is Z.
- **D-pad:** the D-pad bits.
- **The analog stick:** two signed bytes, the stick's X and Y, in a range of ±80 like a real controller.
  - `ControllerState` widens from 16 to 32 bits to carry them, so recordings, challenges and netplay carry the
    stick too (netplay's wire format goes to version 3).
  - The keyboard's arrow keys push the stick all the way; the keyboard's D-pad moves to other keys (docs).
  - A game controller's left stick drives it, with the dead zone, and the D-pad stays the D-pad.

## 9. Platform, library and UI
- **The library system:** `Nintendo64`, named "Nintendo 64", with `.z64`, `.v64` and `.n64` files.
- **Hashing and catalog:** No-Intro N64 DATs list big-endian ROMs, so the hash is of the big-endian ROM. The
  catalog import recognizes "Nintendo - Nintendo 64" DATs.
- **RetroAchievements:**
  - console id 2;
  - the hash as rcheevos computes it (MD5 of the big-endian ROM);
  - memory is RDRAM (8 MB), with each 32-bit word's bytes in little-endian order as RetroAchievements' sets
    expect (to be checked by the owner).
- **Everything through `IEmulatedConsole`:** save states, rewind, run-ahead, timeline, challenges and netplay. A
  state is about 8.5 MB (RDRAM, the RSP's memories, TMEM), so the plan checks the rewind and run-ahead budgets.
- **Settings:** the Expansion Pak on or off.
- **Sample data:** an N64 game in the sample library.

## 10. Tests
- **PeterLemon's N64 test programs** (public domain, Unlicense; downloaded at test time, pinned to a commit, like
  the other suites; they boot with krom's own boot code, not Nintendo's):
  - `CPUTest/CPU` (arithmetic, shifts, multiply/divide, loads and stores, LL/SC), `CPUTest/CP0` and
    `CPUTest/CP1` (every FPU operation, the compares and the rounding modes): each draws PASS in green or FAIL in
    red, and the harness counts them in the framebuffer.
  - `RSPTest/CPU` and `RSPTest/CP2`: the same for the RSP's scalar and vector instructions.
  - `CPUTest/DMAAlignment-PI-cart` and `RSPTest/DMAAlignment-SP`: DMA edge cases.
  - `RDP` and `RDPTest` programs: each has a reference picture in the repository. They are compared with it,
    then pinned as golden images.
- **Unit tests:**
  - the CPU's delay slots, branch-likely, exceptions, the TLB, and the FPU's rounding modes;
  - the memory map, MI interrupts, PI/SI/SP DMA;
  - the PIF's joybus commands, EEPROM, the Controller Pak;
  - the RSP's vector unit on chosen values;
  - RDP rasterization with pixel probes (fill and copy rectangles, a shaded triangle, a textured rectangle);
  - ROM byte orders, the CIC detection, the save type;
  - save-state round-trips and determinism.
- **Host:** loading a ROM in each byte order; frames; saves.
- **Local:** `[LocalRomFact]` tests boot the player's own cartridges from `GAMEVAULT_LOCAL_ROMS`.

## Order of work
1. VR4300 integer core, COP0 and the memory map; PI boot and the simulated PIF; the VI showing a framebuffer;
   the `CPUTest/CPU` and `CP0` harness.
2. COP1 and the `CP1` tests.
3. The RSP: scalar unit, then the vector unit and the `RSPTest` programs.
4. The RDP and the `RDP` programs.
5. AI, SI and the joybus: controllers, EEPROM, the Controller Pak; FlashRAM and SRAM.
6. Platform: `ControllerState` widening, library, input, saves, settings, RetroAchievements, sample data, docs.
