# Phase 17 — PlayStation 2

**Goal (SPEC §7.11):** GameVault runs PlayStation 2 software: the Emotion Engine with its vector units, the Graphics
Synthesizer, the I/O processor, sound, controllers and memory cards, with the player's own BIOS for discs.

**Done when (SPEC §12):** "Scoped in its plan: a first playable milestone with test programs and homebrew". This plan
scopes it as:
- **Test programs pass:** ps2autotests (ISC licence; programs recorded on a real PS2 with their expected output) for
  the EE's integer, FPU and SIMD instructions, the VU0 macro mode, the VUs' micro mode, DMA (DMAC, scratchpad, VIF),
  the GS's SIGNAL/LABEL, memory and the EE kernel's threads and interrupts. Each program's printed output must equal
  its `.expected` file; programs that can't pass yet are listed with the reason.
- **Homebrew renders correctly:** GameVault's own PS2 homebrew test programs (source in the repository, built with
  clang) draw through every path to the GS (PATH3 by DMA, PATH2 through VIF1, PATH1 from a VU1 microprogram) with
  textures, CLUTs, blending and Z, and are held as golden images.
- **The player's BIOS boots:** a disc or ELF boots through the player's BIOS (EE and IOP both running it). The tests
  check this with a stand-in BIOS; real BIOSes and discs are on the owner's checklist.

SPEC §7.11 asks the later phases to scope a first playable milestone and list what remains; everything beyond it is
under "Left for later".

## Scope decisions
- **Interpreters first, no recompiler yet.** SPEC asks for dynamic recompilation. As in Phase 16, the EE and VUs are
  optimized interpreters first: they are deterministic, run on every OS in CI and serve as the reference a recompiler
  must match. The recompiler is left for later, when the interpreters' speed on real games is known.
- **The GS in software.** Like the N64's RDP, the GS is a software rasterizer in `GameVault.Core`, checked pixel for
  pixel in CI, shown through the existing Direct3D 11 presenter. A GPU renderer (upscaling, speed) is left for later.
- **Two ways to boot:**
  - **With the player's BIOS** (Settings → PlayStation 2, like the PlayStation BIOS): the EE and IOP both run it
    (low-level), so discs, memory cards and the IOP's modules behave as on hardware. ELFs boot like a disc: the BIOS
    runs until it starts its EE loader, then the ELF is loaded instead (as PCSX2's fast boot does).
  - **Without a BIOS:** ELFs (homebrew, test programs) run on **GameVault's own EE kernel** (high-level emulation):
    its system calls, threads, semaphores, interrupt handlers and alarms are written in C#, and the IOP services the
    EE asks for over SIF (the RPC handshake, `fileio` for printing) are answered in C#. No Sony code is needed or
    shipped. Discs without a BIOS get a message naming the setting.
- **Frames as BGR555**, like the N64: the frame holds indices into the 32,768-colour palette; 24-bit colour shown at
  full depth is left for later. Frames are up to 640×512 (PAL), so the video sink grows to 512 lines.
- **The DualShock 2's second stick:** `ControllerState` grows from 32 to 64 bits (right stick in bits 32–47), and
  challenge recordings and netplay move to version 4, as Phase 16 did for the first stick.

References: ps2tek (psi-rockin.github.io/ps2tek) for the hardware; the EE Core and VU User's Manuals' published
behaviour as ps2tek and the emulators describe it; Play! (BSD-2-Clause) for the high-level kernel's design and the
VU/FPU's non-IEEE arithmetic; psx-spx for the IOP's R3000A and the parts of the SPU2 it shares with the PlayStation's
SPU. Code cites the page or source for subtle behaviour.

## 1. Emotion Engine CPU (`GameVault.Core.Cpu.R5900`)
- **Integer:** MIPS III (64-bit) without the MIPS III 64-bit multiply/divide quirks the R5900 lacks, plus its own:
  128-bit GPRs; MULT/MULTU with a third operand; MADD/MADDU; the pipeline-1 forms (MULT1, DIV1, MFHI1…, MADD1);
  MFSA/MTSA/MTSAB/MTSAH and QFSRV; LQ/SQ (16-byte aligned, low bits ignored); the branch-likely forms; the traps,
  SYSCALL, BREAK; MOVZ/MOVN, PREF (no-op), SYNC; the overflow exception on ADD/ADDI/SUB/DADD….
- **MMI:** every MMI, MMI0–3 instruction: parallel add/sub (saturating and wrapping, 8/16/32-bit), compares,
  min/max, pack/extend/interleave, parallel shifts, multiply/divide/MADD to HI/LO and HI1/LO1, PHMADH/PHMSBH,
  PMFHL/PMTHL formats, PLZCW, PCPYLD/PCPYUD/PCPYH, PEXEH/PEXEW/PROT3W, PABSH/PABSW, PAND/POR/PXOR/PNOR,
  QFSRV, PSLLVW…
- **COP0:** Index, Random, EntryLo0/1, Context, PageMask, Wired, BadVAddr, Count/Compare (timer interrupt on IP7),
  EntryHi, Status, Cause, EPC, PRId (0x2E20), Config, BadPAddr, Debug, Perf (counters approximate), TagLo/Hi,
  ErrorEPC; level 1 and level 2 exceptions with their vectors (ps2tek "EE COP0 Exception Handling"); EI/DI and
  ERET; the 48-entry TLB with TLBR/TLBWI/TLBWR/TLBP, odd/even pages, page masks, and the scratchpad bit (S) that
  maps the 16 KB scratchpad; BC0F/BC0T on the DMAC's CPCOND0.
- **COP1 (FPU):** the PS2's single-precision FPU, which isn't IEEE 754: no infinities, NaNs or denormals; overflow
  saturates to ±max with the O flag, underflow flushes to ±0 with U; results round toward zero; ADD/SUB/MUL/DIV/
  SQRT/RSQRT/ABS/NEG/MOV, the accumulator forms (ADDA, SUBA, MULA, MADD, MSUB, MADDA, MSUBA), MAX/MIN, CVT.S.W
  and CVT.W.S (saturating), C.F/EQ/LT/LE, BC1F/BC1T(L), FCR31's flags and the divide-by-zero/invalid flags.
- **COP2 in macro mode:** QMFC2/QMTC2/CFC2/CTC2 (with the interlock bit), LQC2/SQC2, BC2F/BC2T on VU0's running
  state, and the VU's upper and lower operations executed at once through VU0 (`IR5900Cop2`).
- **Timing:** one cycle per instruction at 294.912 MHz, plus multiply/divide latencies (and the HI/LO interlock);
  no cache model (the CACHE instruction is accepted and does nothing for data).
- **Speed:** decoded instructions are cached per page; RDRAM, scratchpad and the KSEG0/KSEG1 windows have fast paths.

## 2. Vector units (`Ps2Vu`)
- **VU0 (4 KB micro, 4 KB data) and VU1 (16 KB each):** 32 128-bit float registers, 16 integer registers, ACC, I,
  Q, P, R, the MAC, status (with sticky bits) and clipping flags.
- **Upper instructions:** every FMAC operation (ADD/SUB/MUL/MADD/MSUB with broadcast, I, Q, ACC forms, MAX/MINI,
  ABS, FTOI/ITOF at each precision, OPMULA/OPMSUB, CLIP, NOP).
- **Lower instructions:** loads and stores (LQ/SQ/LQI/LQD/SQI/SQD/ILW/ISW/ILWR/ISWR), integer ALU, branches with
  their delay slot and the flags' delay, MOVE/MR32/MFIR/MTIR, the flag instructions (FSAND… FCGET), DIV/SQRT/RSQRT
  with their latencies and WAITQ, the EFU on VU1 (EATAN… ESUM, WAITP), RINIT/RGET/RNEXT/RXOR, XGKICK, XTOP/XITOP.
- **The pipeline as programs see it:** FMAC results and flags four cycles later, Q after DIV's latency, P after the
  EFU's, the E bit's one-instruction delay, the I, E, M, D and T bits; branch-on-integer hazards as the VU manual
  documents them.
- **Arithmetic:** the VU's non-IEEE floats (no infinities, NaNs or denormals; saturation; truncation), matched to
  the hardware's results with the exact multiply and add (Play!'s `FpMulTruncate`/`FpAddTruncate`, BSD-2).
- **Macro mode:** the COP2 instructions execute on VU0 immediately; VCALLMS/VCALLMSR start a micro program.
- **VU0 can read VU1's registers** at $4000 in its data space, as games use.

## 3. DMAC, interrupts, timers and VIF (`Ps2Dmac`, `Ps2Intc`, `Ps2EeTimers`, `Ps2Vif`)
- **DMAC:** the ten channels (VIF0, VIF1, GIF, IPU from/to, SIF0/1/2, SPR from/to) in normal, chain and interleave
  modes; source chain tags (REFE, CNT, NEXT, REF, REFS, CALL, RET, END) with TTE and the tag's IRQ bit; destination
  chain for SPR from and SIF0; stall control (D_CTRL STS/STD, D_STADR); the MFIFO ring (D_RBOR/D_RBSR);
  D_STAT's channel and stall interrupts; D_PCR's priority and CPCOND0; D_ENABLER/W; transfers take bus time.
- **INTC:** INTC_STAT/INTC_MASK (GS, SBUS, VBlank start/end, VIF0/1, VU0/1, IPU, the timers) onto the EE's INT0, and
  the DMAC onto INT1.
- **Timers:** the four EE timers with the bus clock, /16, /256 and H-blank sources, gating, compare and overflow
  interrupts.
- **VIF0/VIF1:** every VIFcode (NOP, STCYCL, OFFSET, BASE, ITOP, STMOD, MSKPATH3, MARK, FLUSHE/FLUSH/FLUSHA, MSCAL,
  MSCALF, MSCNT, STMASK, STROW, STCOL, MPG, DIRECT, DIRECTHL, UNPACK); UNPACK in every format with masking, the
  offset/difference modes, skipping and filling writes, TOPS/ITOPS and the double buffer; the I-bit stall and
  interrupts; the registers at $10003800/$10003C00 and the FIFOs.
- **Scratchpad DMA** (SPR from/to) and the 16 KB scratchpad.

## 4. GIF and GS (`Ps2Gif`, `Ps2Gs`)
- **GIF:** GIFtags in PACKED, REGLIST and IMAGE modes, the three paths with their arbitration, PATH3 masking
  (MSKPATH3, IMT), GIF_STAT/GIF_CTRL/GIF_MODE.
- **GS local memory:** 4 MB with the hardware's page, block and column swizzling for every format (PSMCT32/24/16/16S,
  PSMZ32/24/16/16S, PSMT8, PSMT4, PSMT8H, PSMT4HL/HH).
- **Transfers:** host to local (BITBLTBUF, TRXPOS, TRXREG, TRXDIR) in every format, local to host (read back
  through the GIF FIFO), local to local.
- **Drawing:** points, lines, line strips, triangles, strips, fans and sprites; flat and Gouraud shading; texturing
  with UV and STQ (perspective), CLUTs (CSM1/CSM2, CLD's load conditions), TEXA, wrap modes (REPEAT, CLAMP,
  REGION_CLAMP, REGION_REPEAT), nearest and bilinear filtering (mipmapping left for later); TEX0/TEX1/TEX2, the
  colour functions (MODULATE, DECAL, HIGHLIGHT, HIGHLIGHT2); fog; the alpha test, destination alpha test and depth
  test; alpha blending (A, B, C, D with FIX), PABE and FBA; dithering, COLCLAMP; scissoring and XYOFFSET;
  FRAME's mask and ZBUF's ZMSK; PRMODE/PRMODECONT; the fill conventions of the GS's rasterizer (top-left, 4-bit
  subpixel coordinates).
- **Privileged registers:** PMODE, SMODE1/2, SYNCH/SYNCV, DISPFB1/2, DISPLAY1/2, BGCOLOR, CSR (SIGNAL, FINISH,
  HSINT, VSINT, EDWINT, FIELD, the reset), IMR, BUSDIR, SIGLBLID; their interrupts to the INTC.
- **Output (PCRTC):** the two read circuits with the merge circuit (PMODE's alpha), MAGH/MAGV, interlaced field and
  frame modes, NTSC 640×448 and PAL 640×512 → the frame, at 4:3.

## 5. The EE machine (`Ps2Console`)
- **Memory map (physical):** 32 MB RDRAM at $00000000; the EE registers at $10000000 (timers, IPU, GIF, VIF0/1,
  the FIFOs, DMAC, INTC, SIO, SIF, the RDRAM controller); VU0/VU1 memories at $11000000; the GS's privileged
  registers at $12000000; the IOP's RAM at $1C000000; the BIOS at $1FC00000. KSEG0/KSEG1 and the TLB's mappings
  (including the kernel's uncached and uncached-accelerated mirrors at $20000000 and $30000000).
- **The EE's debug UART** (SIO at $1000F180): characters go to `DebugOutput`, like the N64's IS-Viewer, for tests.
- **Clocks:** the EE at 294.912 MHz, the bus (DMAC, VIF, GIF, timers) at half of it, the IOP at 36.864 MHz
  (an eighth). A scheduler runs the EE to the next event (a line, a timer, an IOP slice, a DMA's progress); VU1 and
  DMA run alongside in slices.
- **Video timing:** NTSC (525 lines, 59.94 fields a second) and PAL (625, 50) from SMODE1, H-blank and V-blank to the
  INTC, timers and the GS's CSR.
- **IPU:** the MPEG decoder's commands (BCLR, IDEC, BDEC, VDEC, FDEC, SETIQ, SETVQ, CSC, PACK, SETTH) with its FIFOs
  and DMA, for games' video — if it doesn't fit this phase, it moves to "Left for later" with the reason.

## 6. The I/O processor (`Ps2Iop`)
- **CPU:** the R3000A core of Phase 15 (no GTE; PRId $1F), at 36.864 MHz, with 2 MB of RAM, the 1 KB scratchpad, the
  BIOS, and its I/O map (ps2tek "IOP Map").
- **IOP DMA:** the PS1-style channels 0–6 and the new 7–13 (SPU2 core 1, DEV9, SIF0, SIF1, SIO2 in/out), with chain
  modes where the IOP has them (SIF0's tags).
- **IOP INTC** (I_STAT, I_MASK, I_CTRL) and the six IOP timers (16- and 32-bit, with their sources and interrupts).
- **SIF:** the mailbox registers (MSCOM, SMCOM, MSFLG, SMFLG, CTRL, BD6) and the SIF0/SIF1 FIFOs between the EE's
  DMAC channels 5 and 6 and the IOP's 9 and 10, with SIF0's destination tags.
- **CDVD:** the N commands (read CD/DVD sectors, seek, stop, pause, read TOC, streaming reads) and the S commands the
  BIOS and libraries use (mechacon version, the real-time clock — deterministic, from a fixed start time — the
  region, the tray, the model and console id). Discs come through `IPs2Disc` (2,048-byte sectors, with raw sectors
  for CD discs), which the platform implements over the image files.
- **SIO2:** the DualShock 2 (digital, analog with both sticks, config mode, the pressure bytes as full presses) on
  both ports, and the 8 MB memory card with the PS2 card protocol (including the authentication commands as
  emulators answer them); the PS1 SIO for PS1 controllers is left for later.
- **SPU2 (`Ps2Spu2`):** two cores of 24 voices with the PS1 SPU's ADPCM, ADSR, pitch and noise; each core's reverb;
  the 2 MB sound RAM with manual, DMA and AutoDMA (ADMA) transfers; IRQ addresses; core 0 into core 1; 48 kHz output,
  mixed to mono like the other systems.

## 7. GameVault's EE kernel and IOP services (high-level, for running without a BIOS)
- **ELF loading** (`Ps2Elf`): the program headers into RDRAM, the entry point, the BSS; arguments.
- **The kernel:** the syscalls ps2sdk and the test programs use — SetupThread, SetupHeap, FlushCache, the thread
  calls (Create/Delete/Start/Exit/ExitDelete/Terminate/Suspend/Resume/Sleep/Wakeup/CancelWakeup/ChangePriority/
  RotateThreadReadyQueue/ReleaseWaitThread/ReferThreadStatus/GetThreadId, the i-forms), semaphores, event flags if
  needed, AddIntcHandler/AddDmacHandler/Enable/Disable(Intc|Dmac), alarms, SetVSyncFlag, GsPutIMR/GsGetIMR,
  SetGsCrt, the SIF calls (SifSetDma, SifDmaStat, SifSetDChain, SifSetReg, SifGetReg), Deci2Call (TTY), the TLB and
  the low memory the kernel owns. Interrupt handlers run on the EE as on the real kernel.
- **IOP services over SIF:** the SIF command layer (the init commands, SetSReg, RPC bind/call/end) answered in C#
  instead of by IOP code; `fileio` (and `tty`) writes go to `DebugOutput`.
- **Not covered:** IOP modules (IRX files) loaded by an ELF need a real IOP kernel; they run only with the BIOS.
  Running ps2autotests' IOP programs on a GameVault IOP kernel is left for later.

## 8. Input, memory cards, library and platform
- **The pad:** Cross/Circle/Square/Triangle on B/A/Y/X by position (as the PlayStation), L1/R1, L2/R2, L3 and R3
  (two new bits), Select, Start, the D-pad, and both sticks. Keyboard defaults: the PlayStation's, with the arrows on
  the left stick and I/J/K/L on the right (as the N64's C buttons).
- **Memory cards:** one 8 MB card per game in slot 1, saved as a raw `.ps2` image (the format other emulators use),
  created formatted.
- **Library:** `.iso` (DVD or CD), `.cue/.bin` and `.chd` for CD games, `.elf` for homebrew. A disc is a PS2 disc
  when its SYSTEM.CNF has `BOOT2`. Discs hash as Redump lists them (the whole image for DVDs, track 1 for CDs);
  "Sony - PlayStation 2" DATs.
- **Settings:** the BIOS path; "Skip the startup screens" (fast boot).
- **RetroAchievements:** console id 21; rcheevos' `rc_hash_ps2` (the boot executable named in SYSTEM.CNF's BOOT2,
  with its name); memory is the 32 MB RDRAM.
- **Everything through `IEmulatedConsole`:** save states (about 40 MB: RDRAM, GS memory, IOP RAM, sound RAM, VU
  memories), rewind, run-ahead (off by default for this system if the budget doesn't allow), timeline, challenges,
  netplay. The plan checks the rewind and run-ahead budgets.
- **Sample data:** a PlayStation 2 game in the sample library.

## 9. Tests
- **ps2autotests** (jpd002/ps2autotests, ISC): downloaded at test time pinned to a commit (they're 78 small files;
  downloaded like the other suites). Each EE program runs on GameVault's kernel; its printed output is compared with
  its `.expected` file. The IOP (`.irx`) programs are listed as not run (they need an IOP kernel).
- **GameVault's homebrew** (`tests/Ps2Homebrew/`, GameVault's own code): bare-metal C programs built with clang for
  `mipsel` plus hand-encoded VU microcode, committed with their built ELFs (they are GameVault's own work, so they may
  be committed). They draw with each GIF path and the GS's features and are pinned as golden images; their sources
  build with `tests/Ps2Homebrew/build.sh`.
- **Unit tests:** R5900 instructions (MMI, FPU rounding and saturation, COP0, the TLB); VU instructions, flags and
  latencies; VIF UNPACK formats; DMAC chain tags; GS swizzling (round trips per format), transfers and pixel probes
  (fill rule, textures, CLUTs, blending, Z, fog); SIF FIFOs; CDVD commands against a small ISO built by the test;
  SIO2 pad and memory card protocol; SPU2 voices; the kernel's threads and semaphores; save-state round trips and
  determinism.
- **BIOS boot:** a stand-in BIOS built by the test (EE and IOP code branching on PRId) checks the reset path, SIF
  handshakes and the ELF fast-boot hook. `[LocalRomFact]` tests boot the owner's BIOS (`ps2/bios.bin`) and discs
  from `GAMEVAULT_LOCAL_ROMS`.

## Order of work
1. The R5900 (integer, MMI, COP0, FPU); the VUs; the GS and GIF; the IOP machine; the SPU2 — in parallel, each
   with its unit tests.
2. The EE machine: memory map, DMAC, VIF, INTC, timers, the scheduler and frames; save states.
3. GameVault's kernel and IOP services; ps2autotests through the whole console.
4. GameVault's homebrew and golden images; BIOS boot with the stand-in BIOS; the IPU.
5. Platform: input, library, settings, memory cards, RetroAchievements, sample data, docs.
