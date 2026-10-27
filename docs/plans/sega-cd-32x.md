# Sega CD and 32X (outstanding-work item 17)

**Goal (SPEC §7.11):** GameVault plays Sega CD (Mega-CD) discs and 32X cartridges as add-ons to its Genesis.
Milestone 1 is the Sega CD; milestone 2, the 32X, is planned in full once milestone 1 is merged.

## 1. Milestone 1: the Sega CD

### Core (`GameVault.Core.SegaCd`)
- **`SegaCdUnit`** (the add-on): a second `M68000Cpu` (the sub CPU, 12.5 MHz) with its own bus, 512 KB of PRG-RAM,
  256 KB of Word RAM, 8 KB of backup RAM (battery-backed, the system's saves), and the gate array's registers.
  `GenesisConsole` takes it as an optional part: the main CPU's $000000–$3FFFFF and $A12000–$A1203F go to it, and
  the console runs the sub CPU, the drive and the sound chips in step with the main CPU's master clock.
- **Main CPU's map** (no cartridge, "mode 2"): the player's BIOS (128 KB) at $000000, the PRG-RAM bank (BK1–0) at
  $020000–$03FFFF, Word RAM at $200000–$23FFFF; the gate array at $A12000: sub CPU reset and bus request, the level 2
  interrupt to the sub CPU, PRG-RAM write protect and bank, Word RAM's RET/DMNA/MODE, the H-interrupt vector, the CDC
  host port, the stopwatch, the communication flags and the 16 communication words.
- **Sub CPU's map:** PRG-RAM at $000000, Word RAM at $080000 (2M mode) or the sub's bank at $0C0000 (1M mode),
  backup RAM at $FE0000 (odd bytes), the PCM chip at $FF0000 (odd bytes), the gate array at $FF8000: LEDs and reset,
  memory mode (RET/DMNA/MODE and the priority mode), the CDC registers and its DMA, the stopwatch, communication,
  the timer (level 3), the interrupt mask, the fader, the CDD's command and status ports, the font generator and the
  graphics ASIC's registers.
- **Word RAM:** 2M mode (all of it to one CPU at a time, swapped by DMNA and RET) and 1M mode (two banks, one each,
  swapped on RET), with the main CPU's 1M-mode cell-image view for the VDP's DMA.
- **CDD** (the drive's microcontroller): the 10-nibble command and status protocol at 75 Hz (level 4 interrupt), with
  stop, read TOC (position, track, length, track count, track start), seek, play, pause, resume, fast forward and
  rewind, the tray; seek timing from the distance moved. CD-DA tracks play through the fader into the mix.
- **CDC** (the LC8951 decoder): its register file, the 16 KB buffer, sector decoding (the header's MSF, mode 1
  data), the decoder and transfer-end interrupts (level 5), and DMA to the host port, PRG-RAM, Word RAM or PCM RAM.
- **PCM** (the RF5C164): eight channels over 64 KB of wave RAM with start, loop, step and pan, at 32.55 kHz, mixed
  in stereo with the Genesis' FM and PSG.
- **Graphics ASIC:** the stamp rotation and scaling engine (stamp sizes 16/32, map sizes 1×1/16×16 screens, repeat)
  drawing into Word RAM's image buffer from the trace table, with the level 1 interrupt when it finishes.
- **Discs:** the core reads `ICdImage` (the PlayStation's), so `.cue`/`.bin` and `.chd` work unchanged.
- **State:** every part implements `IStateful`; the sub CPU, the drive's position and the buffers are in save
  states, with a round-trip test.

### Platform, Library and UI
- `GameSystem.SegaCd`. A `.cue` or `.chd` whose first data sector starts "SEGADISCSYSTEM" is a Sega CD disc (others
  stay PlayStation or PlayStation 2); the region comes from the disc header.
- The player's own BIOS (128 KB: US, Europe and Japan models), **Settings → Sega CD**; none is shipped.
- Backup RAM saves next to the other saves as `.brm`.
- The Genesis pad layout; the manual's Sega CD section; the sample library.

### Tests
- Hand-assembled 68000 programs on both CPUs with a stand-in BIOS: the sub CPU's reset and bus request from the
  main CPU, the level 2 interrupt, the communication words and flags, PRG-RAM banking and write protect, Word RAM's
  swaps in 2M and 1M mode, the timer interrupt.
- The CDD's protocol (checksums, TOC answers, seek and play reaching the right sector), the CDC decoding a sector into
  its buffer and DMA to PRG-RAM, PCM pitch and looping, the ASIC on a small stamp map, and a save-state round trip.

### Not in milestone 1
- Cartridge-plus-CD mode (a cartridge booting the CD, "mode 1"); CD+G subcode graphics; the Wondermega/X'Eye's
  extras; the LaserActive.

## 2. Milestone 2: the 32X
Two SH-2s (a new CPU core, with its own test suite), the 32X VDP (packed, direct-colour and run-length frame buffers,
its palette and the priority over the Genesis picture), PWM sound, the communication and interrupt registers, and the
player's own BIOS files. Planned in full when milestone 1 is merged.

## Owner checks
With the owner's BIOS: the Sega CD's own menu appears, and Sonic CD, Lunar: The Silver Star, Snatcher and Final
Fight CD boot, play their CD music and save to backup RAM.
