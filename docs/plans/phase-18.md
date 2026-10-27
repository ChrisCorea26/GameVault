# Phase 18 — GameCube (then the Wii)

**Goal (SPEC §7.11, §12):** GameVault plays GameCube software. SPEC scopes the phase as "a first playable milestone
with test programs and homebrew". The GameCube is far larger than any earlier system, so it comes in milestones,
each merged before the next starts.

## 1. Milestone 1: the Gekko and the machine around it, for homebrew that draws to the screen itself

### Core (`GameVault.Core.GameCube`)
- **Gekko** (`GameVault.Core.Cpu.Gekko`): the PowerPC 750CL as the GameCube has it, interpreted: every integer,
  branch, condition-register, rotate, load/store (with update, indexed, byte-reversed, multiple and string forms),
  cache (as no-ops, except `dcbz`) and system instruction; the FPU (single and double, `fmadd` and friends, `frsp`,
  `fctiw[z]`, `fcmpu/o`, `fsel`, `fres`, `frsqrte`, FPSCR); the paired-single unit (`ps_*` arithmetic, merges, sums,
  `psq_l/st[u][x]` with the GQRs' quantisation); the SPRs games use (LR, CTR, XER, MSR, SRR0/1, SPRGs, DEC, TB,
  HID0–2, GQR0–7, WPAR, DMAU/L, L2CR); exceptions (system reset, program, FP unavailable, decrementer, external,
  system call) with `rfi`; the BATs, decoded the way the GameCube's OS sets them (the cached and uncached mirrors of
  physical memory at $80000000 and $C0000000). A block recompiler like the EE's is milestone 3 (SPEC: "recompiled").
- **Memory and hardware** (`GameCubeConsole`): 24 MB of main RAM; the processor interface's interrupt cause and mask
  (PI); the video interface (VI): its timing (NTSC 525 lines, PAL 625), the display interrupts, and the picture read
  from the external frame buffer (XFB, YUV 4:2:2 in main RAM) into 32-bit colour; the serial interface (SI) with
  standard controllers on its four ports (buttons, both sticks, analog triggers); EXI with the RTC and SRAM (for the
  OS's settings) and nothing else attached yet; the DSP interface's mailboxes answered the way the OS's boot
  handshake expects (a high-level DSP until milestone 4); the audio interface's sample counter; the DVD interface
  idle with no disc (discs are milestone 2); the command processor's FIFO taking GX writes (and drawing nothing:
  the GPU is milestone 2).
- **Programs:** `.dol` executables (the seven text and eleven data sections, BSS and entry point) and ELF files,
  started the way Nintendo's boot ROM leaves the machine (the OS globals at $80000000, the stack, the BATs and MSR),
  so no IPL is needed.
- **State:** every part implements `IStateful`; a save/load round trip mid-program is tested.

### Platform, Library and UI
- `GameSystem.GameCube`; `.dol` (and `.elf` with a PowerPC header) files; the GameCube pad on the PlayStation 2's
  layout (both sticks, the analog triggers, A/B/X/Y, Z, Start); the sample library; the manual's section.

### Tests
- `GekkoTests`: hand-assembled programs for each instruction group (carries and overflow, rotates and masks, CR logic,
  branches and the link register, loads and stores of every width and form, the FPU's rounding and conversions,
  paired singles and quantised loads/stores, exceptions and `rfi`, the decrementer, BAT translation).
- `GameCubeConsoleTests`: a DOL boots and writes the XFB that the VI shows; VI and PI interrupts; SI reading a pad;
  EXI's SRAM; the DSP mailbox handshake; a save state.

### Not in milestone 1
- The GPU (Flipper's transform and TEV pipeline, textures, the EFB and its copies): milestone 2, with discs (`.iso`,
  `.gcm`, `.rvz`) through the DVD interface and the IPL's high-level boot.
- The recompiler (milestone 3), the DSP and its audio (milestone 4), memory cards, the Wii (Broadway, Hollywood,
  IOS, the Wii Remote): their own milestones, planned when the GameCube is playable.

## Owner checks
Homebrew built with devkitPPC and libogc that writes to the XFB (its console demos) shows its text, and reads the
controller.
