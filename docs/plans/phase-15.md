# Phase 15 — Sony PlayStation

**Goal (SPEC §7.11):** GameVault plays PlayStation discs with the player's own BIOS: picture, sound, controllers and
memory cards.

**Done when (SPEC §12):**
- the CPU and GPU test suites pass;
- homebrew and the player's games boot with their BIOS.

SPEC §7.11 asks the later phases to scope a first playable milestone and list what remains. This phase's
milestone is a disc booting through the player's BIOS to gameplay, with sound, controllers and memory cards.
Everything beyond that is under "Left for later".

## 1. R3000A CPU (`GameVault.Core.Cpu.R3000`)
- **Instructions:** the MIPS I set, with the PlayStation's behaviour:
  - the branch delay slot and the load delay slot, including a load's target being overwritten in its delay
    slot;
  - LWL/LWR merging with a pending load;
  - the overflow exception on ADD/ADDI/SUB;
  - the address-error exception on unaligned loads, stores and jumps (with BadVaddr);
  - SYSCALL and BREAK;
  - the reserved-instruction exception;
  - the coprocessor-unusable exception for COP1/COP3 and for COP2 when SR.CU2 is clear.
- **MULT/DIV:** run in the background and stall on MFHI/MFLO, with the documented timings (psx-spx "CPU
  Arithmetic"). DIV by zero and the 0x80000000 / −1 overflow give the documented results.
- **COP0:** SR, CAUSE, EPC, BadVaddr and PRID.
  - The interrupt-pending bits, and IEc/KUc pushed and popped by exceptions and RFE.
  - BEV.
  - Cache isolation (SR.IsC), which the BIOS uses to flush the cache: stores go nowhere while it is set.
  - The breakpoint registers are left for later.
- **Memory map:**
  - KUSEG/KSEG0/KSEG1 mirroring;
  - 2 MB RAM mirrored over the first 8 MB;
  - 512 KB BIOS at $1FC00000;
  - the 1 KB scratchpad (data-cache RAM) at $1F800000;
  - I/O at $1F801000;
  - the expansion regions;
  - the cache-control register at $FFFE0130.
- **Timing:**
  - one cycle per instruction plus memory wait states, from the memory-control delay registers for the BIOS
    and expansion regions;
  - the 4 KB instruction cache modelled for timing, so code in RAM runs at its real speed.
- **GTE (COP2):**
  - every command: RTPS/RTPT, NCLIP, OP, DPCS/DPCT, INTPL, MVMVA, NCDS/NCDT, NCCS/NCCT, CDP, CC, NCS/NCT,
    SQR, DCPL, AVSZ3/AVSZ4 and GPF/GPL;
  - with the saturation flags (FLAG), the UNR divider and its table (computed as psx-spx gives it), and the
    register quirks of MFC2/CFC2 (IRGB/ORGB, SXYP's FIFO, LZCS/LZCR).
- **Tests:** hand-assembled programs for the delay slots, exceptions, MULT/DIV timing and COP0, then the test
  programs below.

## 2. The machine (`GameVault.Core.PlayStation`)
- **Interrupt controller:** I_STAT/I_MASK, and the CPU's hardware interrupt line.
- **DMA:**
  - seven channels (MDECin, MDECout, GPU, CD-ROM, SPU, PIO, OTC);
  - the sync modes (block, request with chopping, linked list);
  - DPCR priorities and DICR's interrupt flags;
  - transfer time charged to the CPU.
- **Timers:** three timers, with the dot-clock, hblank and system/8 sources and the sync modes.
- **SIO0:** the controller and memory card port.
  - The digital pad (SCPH-1080) on both ports.
  - Memory cards (128 KB) with the read, write and ID commands, and the /ACK timing that games poll.
- **Clock:** 33.8688 MHz, with NTSC and PAL frame timing from the GPU's video clock.

## 3. GPU (`PsGpu`)
- **VRAM:** 1 MB of VRAM as 1024×512 16-bit pixels.
- **GP0:**
  - polygons (flat, Gouraud, textured, with raw and modulated textures);
  - lines and polylines;
  - rectangles (variable, 1×1, 8×8 and 16×16);
  - fill, and VRAM↔VRAM, CPU→VRAM and VRAM→CPU copies;
  - draw mode, texture window, draw area, draw offset and the mask bits.
- **Rasterizer:**
  - the hardware's fill rules and coverage;
  - 4/8/15-bit textures with CLUTs, texture windows and flips;
  - the four semi-transparency modes, dithering, and mask set/check;
  - drawing that never touches the 1024-wide wraparound wrongly.
- **GP1:** reset, the display area and range, the display mode (256/320/368/512/640 wide, 240/480 interlaced,
  NTSC/PAL, 24-bit colour), the DMA direction and GPUSTAT (with its ready and interlace bits).
- **Output:**
  - The displayed area of VRAM becomes the frame, at up to 640×480 (the sink takes that size since Phase 12).
  - The frame holds BGR555 indices into the 32,768-entry palette that Phase 14 added.
  - 24-bit mode (used by FMV) is shown rounded to 15 bits for now.
  - Pixel aspect comes from the dot clock, so every width shows at 4:3.
- **Timing:** commands take no GPU time yet; GPUSTAT reports ready.

## 4. CD-ROM (`PsCdRom`)
- **The controller's registers:** the index register, the parameter and response FIFOs, the interrupt flags
  INT1–INT5, and the data FIFO with its request bits.
- **Commands:**
  - Getstat, Setloc, ReadN/ReadS, Pause, Init, Mute/Demute, Setfilter, Setmode;
  - GetparamL/GetlocL/GetlocP, GetTN, GetTD, SeekL/SeekP;
  - Test (the version subcommand), GetID, ReadTOC, Play, Forward/Backward;
  - each with its first and second responses at documented cycle delays (psx-spx "CDROM Controller").
- **Sectors:**
  - Mode 2 Form 1/Form 2;
  - the 0x800/0x924 sector sizes;
  - XA-ADPCM decoding with Setfilter;
  - CD-DA playback, and both mixed into the SPU's CD input with the CD volume registers.
- **Disc images:**
  - The core reads sectors through an `ICdImage` interface (track list, plus 2352-byte reads by LBA), so a
    700 MB disc never has to sit in memory.
  - The platform implements it over the files. The core still does no file I/O.
  - Reads are pure, so emulation stays deterministic.
- **Seeking:** the seek time grows with distance, using a fixed formula so that runs are deterministic.

## 5. SPU (`PsSpu`)
- **Memory:** 512 KB of sound RAM, with manual and DMA transfers and the transfer modes.
- **Voices:**
  - 24 ADPCM voices with the loop flags and the start/repeat addresses;
  - pitch, with pitch modulation from the previous voice, and noise;
  - ADSR with its rate tables, and sweep volumes;
  - the Gaussian interpolation, from the table psx-spx publishes as a formula;
  - the key on/off and ENDX registers.
- **Reverb:** the reverb unit with its registers, at 22.05 kHz, with its buffer in sound RAM.
- **Other inputs and IRQ:** the CD audio input, and the SPU IRQ on an address match.
- **Output:** 44.1 kHz, mixed to mono like the Genesis and SNES, through the band-limited resampler.

## 6. MDEC (`PsMdec`)
- RLE decoding, the quantization and scale tables, and the IDCT;
- YUV→RGB at 4, 8, 15 and 24 bits, with the signed and set-bit-15 options;
- DMA in and out with the data request bits.

## 7. Booting
- **The BIOS:** 512 KB, player-supplied (Settings, like the Disk System BIOS and the Apple II ROMs). GameVault
  never ships or downloads one.
  - **Region:** read from the BIOS's version string (SCPH model), and the disc's licence sector picks NTSC
    or PAL timing.
  - **Without a BIOS:** a disc gets a clear message naming the setting.
- **PS-EXE files** (homebrew), loaded straight into RAM:
  - **With a BIOS:** it runs until the shell's entry point ($80030000), then the EXE is copied in and started
    with its PC, GP and SP (stack defaulting to $801FFFF0).
  - **Without a BIOS:** bare-metal programs (which never call the kernel) start directly. That is enough for
    the test programs below.
- **Fast boot:** an option that skips the BIOS's intro animation.

## 8. Input
The PlayStation digital pad has Cross, Circle, Square, Triangle, L1, R1, L2, R2, Select, Start and the D-pad.
They map onto `ControllerState` by position, as in Phase 14:
- **Face buttons:** Cross is B (south), Circle is A (east), Square is Y (west), Triangle is X (north).
- **Shoulders:** L1/R1 are L/R.
- **L2/R2:** two new bits (14 and 15), which still fit the 16-bit netplay and recording formats.

Keyboard defaults follow input.md: Z/X are Cross/Circle, A/S are Square/Triangle, Q/W are L1/R1, and the new
keys 1/2 are L2/R2.

## 9. Platform, library and UI
- **The library system:** `PlayStation`, named "PlayStation".
- **Files:**
  - `.cue` (with its `.bin` tracks), `.chd`, `.exe` (PS-EXE) and `.m3u` for multi-disc games.
  - A `.bin` without a `.cue` is ignored, since its tracks can't be known.
- **CHD:** a reader in GameVault.Platform for the CD codecs (`cdzl`, `cdlz` and `cdfl`).
  - zlib comes from .NET; LZMA and FLAC are decoded by GameVault's own code.
  - CHD may move to the next phase if it can't be finished in this one; `.cue/.bin` comes first.
- **Hashing and catalog:** discs are identified by the hash of the first data track, which Redump DATs list.
  The catalog import recognizes "Sony - PlayStation" DATs.
- **Memory cards:**
  - two 128 KB cards per game, saved next to the battery saves as raw `.mcd` files (the format other
    emulators read);
  - shared cards are left for later.
- **Multi-disc:** a "Change disc" menu in the Player for `.m3u` games (shell open, swap, close).
- **RetroAchievements:**
  - console id 12;
  - the hash as rcheevos computes it (the boot executable named in SYSTEM.CNF, with its name);
  - memory is the 2 MB RAM, then the scratchpad at $200000.
- **Everything through `IEmulatedConsole`:**
  - save states, rewind, run-ahead, timeline, challenges and netplay;
  - a state is about 3.6 MB (RAM, VRAM, sound RAM), so the plan checks the rewind and run-ahead budgets.
- **Sample data:** a PlayStation game in the sample library.

## 10. Tests
- **R3000A and GTE:** PeterLemon's PSX bare-metal `CPUTest` programs (31 programs: ALU, shifts,
  MULT/DIV, loads and stores, and two GTE programs).
  - **Source:** downloaded at test time from `PeterLemon/PSX`, pinned to a commit, because the repository
    has no licence (SPEC §11.3).
  - **Pass check:** each program draws PASS in a green font or FAIL in a red one. The harness reads the
    result column from VRAM.
- **GPU:** PeterLemon's `GPU` programs (polygons, textures with each CLUT depth, rectangles, lines, clipping,
  dither), checked against VRAM golden hashes.
  - Each hash is recorded once the picture has been checked against the reference PNG in the same
    repository.
- **ps1-tests (Jakub Czekański, MIT):**
  - The CPU, GTE (`test-all`), DMA, timer, GPU and MDEC tests, compared with the hardware logs and VRAM
    images in that repository.
  - The binaries are only published as a GitHub release, so a CI job downloads it.
  - Locally the tests are skipped unless the release is in the cache.
- **Unit tests:**
  - the delay slots, exceptions and COP0;
  - the memory map and mirrors;
  - DMA (block, chopping, linked list, OTC);
  - timers;
  - GPU rasterization with pixel probes (fill rules, textures, semi-transparency, mask);
  - CD-ROM commands and their interrupts, against a small ISO-9660 disc image built by the test (SYSTEM.CNF
    plus a PS-EXE);
  - SPU ADPCM decoding and a voice's pitch;
  - an MDEC block;
  - the memory card protocol;
  - save-state round-trips and determinism.
- **Host:** loading a disc image and an EXE; frames; the no-BIOS message.
- **Local:** `[LocalRomFact]` tests boot the player's BIOS and discs from `GAMEVAULT_LOCAL_ROMS`.

## Order of work
1. R3000A, COP0 and the memory map, with the `CPUTest` harness (needs only enough of the GPU to copy text
   into VRAM).
2. GTE.
3. Interrupts, DMA, timers, then the GPU and its golden tests.
4. CD-ROM with `ICdImage`, `.cue/.bin`, booting with a BIOS and PS-EXE loading.
5. SIO0: pads and memory cards.
6. SPU, then XA-ADPCM and CD-DA.
7. MDEC.
8. Platform: library, input, settings, RetroAchievements, multi-disc, sample data, docs; then CHD.

## Progress
- **Step 1 (done):**
  - **R3000A:** the MIPS I set with both delay slots, the LWL/LWR merge, the exceptions, COP0, the
    multiplier/divider timing, cache isolation and the instruction cache.
    - Coprocessor accesses follow the hardware log of ps1-tests `cpu/cop`.
    - Byte and halfword stores carry the CPU register's whole value, so 32-bit device registers receive all
      of it, as ps1-tests `cpu/io-access-bitwidth` shows.
  - **Machine:** the memory map, the interrupt controller and PS-EXE loading.
  - **GPU so far:** fill, the three VRAM copies, the draw environment, GP1 and GPUSTAT, and command
    lengths for every GP0 packet.
  - **Results:** all 31 `CPUTest` programs pass. A deliberately broken NOR shows the harness catches FAIL.
- **Step 2 (GTE):** every command and register quirk is written, with unit tests for the registers, RTPS,
  AVSZ3, NCLIP and the division overflow; the two GTE programs pass.
  - Checking every command against hardware waits on ps1-tests `gte/test-all`, which ships only as a
    release binary (CI job to come).
- **Memory timing is approximate for now:**
  - RAM reads cost 4 cycles, BIOS reads 20 and I/O 2; writes are buffered.
  - The memory-control delay registers aren't read yet.

## Left for later
- **Controllers and peripherals:** the DualShock (analog sticks and vibration), the multitap, the mouse,
  GunCon/Justifier, and the link cable.
- **GPU:** command timing and GPUSTAT's busy bits; true 24-bit output; upscaling and a GPU-accelerated
  renderer (with Phase 16's Direct3D work); PGXP-style precision fixes.
- **Codes:** GameShark-style cheat codes.
- **Memory cards:** a memory card manager screen, and shared cards.
- **BIOS:** a GameVault-written replacement kernel, so homebrew that calls the BIOS can run without one.

## Owner checklist
- Set your own BIOS file in Settings (for example SCPH-1001 for NTSC-U, SCPH-7502 for PAL, SCPH-5500 for
  NTSC-J).
- Try your own discs as `.cue/.bin`, including:
  - a game with CD-DA music;
  - a game with FMV;
  - a multi-disc game;
  - a game that saves to a memory card.

  List anything that misbehaves.
