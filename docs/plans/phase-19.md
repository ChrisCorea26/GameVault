# Phase 19 — Atari 2600 (and later the 5200)

**Goal (SPEC §7.11, phase 19):** GameVault plays Atari 2600 games. The 5200 (6502C, ANTIC, GTIA, POKEY and the
player's own BIOS) is the phase's second milestone, after the 2600 ships.

## 1. Milestone 1: the Atari 2600

### Core (`GameVault.Core.Atari`)
- **CPU:** the 6507 is GameVault's `Cpu6502` (NMOS) with a 13-bit bus; no interrupts are wired.
- **Bus** (`Atari2600Console`): A12 set is the cartridge; otherwise A7 clear is the TIA, A7 set with A9 clear the
  RIOT's 128 bytes of RAM, A9 set the RIOT's ports and timer. Every bus cycle runs the TIA three colour clocks and
  the RIOT one cycle. WSYNC holds the CPU (RDY) until the line's end.
- **TIA** (`Tia`): drawn one colour clock at a time from the registers at that moment, so games that change them
  mid-line ("racing the beam") show as they should: the playfield (normal, reflected, score mode, priority), two
  players (copies, sizes, reflection, vertical delay), two missiles (and their reset to the players), the ball,
  HMOVE (and the black bar it leaves), all fifteen collision latches, VBLANK, VSYNC, the fire buttons (with latching)
  and the two sound channels (the polynomial counters and dividers as Ron Fries' TIASound describes them).
- **RIOT** (`Riot`): RAM, the joystick port (SWCHA) and the console switches (SWCHB), the interval timer
  (1, 8, 64 or 1024 cycles, then counting every cycle after it passes zero, with its flag).
- **Frames:** a frame ends when the game ends VSYNC (or after 400 lines, for code that never does). The picture is
  160 pixels wide; its top line is where the game turns VBLANK off, kept steady across frames; NTSC frames show 210
  lines, PAL frames (more than 290 lines) 250. NTSC palette (128 colours, Stella's published table).
- **Cartridges** (`Atari2600Cartridge`): 2K and 4K, F8, F6 and F4 (each with or without the 128-byte SuperChip
  RAM), E0 (Parker Brothers), FE (Activision), 3F (Tigervision) and FA (CBS RAM Plus), detected from size and the
  code's signatures; the profile's model can name one ("F8SC", "E0", …).
- **Input:** the joystick (`ControllerState` directions and B as fire) on ports 1 and 2; Start is the console's
  Reset switch and Select its Select switch; the difficulty switches stay on B (amateur) and colour on.
- **State:** every part implements `IStateful`; save/load round trips for each bank-switching scheme.

### Platform, Library and UI
- `GameSystem.Atari2600`; `.a26` files (and `.zip` with one inside); No-Intro DATs; RetroAchievements console 25
  (an MD5 of the file).
- `ConsoleFactory` and `EmulatorHost`: the system's pad layout (a joystick and one button), its frame size and pixel
  shape; netplay, rewind and run-ahead like the other 8-bit systems.
- `--sample-data`: a 2600 game in the sample library.
- The manual: Supported systems at a glance, the 2600's section (files, controls, the switches, what isn't supported
  yet), edition history.

### Tests
- Hand-assembled 6507 programs through the whole console: the playfield (normal, reflected, score), players with
  copies and reflection, missiles and the ball, HMOVE, collisions, WSYNC timing, VSYNC framing, the RIOT timer and
  ports, the sound channels' periods, and every bank-switching scheme's hotspots and save states.

### Not in milestone 1
- Paddles, the keypad and driving controllers; DPC (Pitfall II) and the other rarer boards (E7, F0, UA, AR
  Supercharger); the PAL and SECAM palettes; the TIA's undocumented quirks (the "starfield" effect of HMOVE during
  a line, the late-HMOVE "Cosmic Ark" behaviour) beyond the black bar.

## 2. Milestone 2: the Atari 5200
6502C with ANTIC (display lists, character and map modes, DMA stealing cycles), GTIA (players, missiles,
priorities, colours), POKEY (sound, keyboard scanning, random numbers, pot readings), the analog joystick and keypad,
and the player's own 2 KB BIOS. Planned in full when milestone 1 is merged.

## Owner checks
Real 2600 cartridges or their dumps: the common games of each scheme (Combat, Pitfall!, Space Invaders, Frogger,
Decathlon, River Raid) look and sound right.
