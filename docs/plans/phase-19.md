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

### Core (`GameVault.Core.Atari`)
- **CPU:** the 6502C is `Cpu6502` (NMOS, decimal mode on) at 1.79 MHz. ANTIC's HALT holds it on any cycle its DMA
  takes and from a WSYNC write until cycle 105. ANTIC's NMIs go through `Cpu6502.TriggerNmi` (they can land while the
  CPU is halted, between the cycles that sample the NMI line).
- **Bus** (`Atari5200Console`): RAM $0000–$3FFF, cartridge $4000–$BFFF, GTIA $C000–$CFFF, ANTIC $D400–$D4FF, POKEY
  $E800–$EFFF, BIOS $F800–$FFFF. The BIOS is the player's own (2 KB, **Settings → Atari 5200**); none is shipped.
- **ANTIC** (`Antic`): 114 cycles a line, 262 lines; the display list from line 8 to 247 (blank lines, JMP, JVB, LMS,
  DLI, HSCROL and VSCROL), modes 2–F with CHBASE/CHACTL, narrow/normal/wide playfields; player/missile DMA (single
  and double line); DLI and VBI NMIs at cycle 7; VCOUNT; NMIST/NMIRES. Stolen cycles at the hardware's positions
  (missiles 0, instruction 1, players 2–5, LMS 6–7, refresh from 25, the playfield across the line). A line's
  playfield is built when the line starts.
- **GTIA** (`Gtia`): drawn a colour clock at a time (the console catches the line up before each GTIA access), so
  mid-line colour changes land where the CPU makes them. Players and missiles (sizes, VDELAY, the fifth player), the
  GTIA data sheet's priority equations (overlaps OR their colours), all collision registers, hi-res colours (PF2's
  hue, PF1's luminance), GTIA modes 9–11 on hi-res lines, TRIG0–3 with latching. The picture is 336 × 240 (colour
  clocks 44–211 of lines 8–247), its colours the 2600's NTSC table.
- **POKEY** (`Pokey`): four channels on 64 kHz/15 kHz/1.79 MHz clocks, 16-bit pairs, the 4/5/9/17-bit polys, high-pass
  filters, volume-only mode; timer IRQs (1, 2, 4), the keypad scan (KBCODE, keyboard IRQ, SKSTAT), the top fire
  button as BREAK/SHIFT, the eight pot counters (line-rate or fast scan), RANDOM, STIMER, SKCTL's init mode.
- **Controllers:** four ports. The stick's X and Y are pots 2n and 2n+1 (6–220, centre 114; the D-pad pushes them
  fully when the analog stick is centred); B the bottom fire button (TRIG), A the top one; Start and Select are the
  keypad's Start and Pause; the other keypad keys ride in `ControllerState` bits 50–53 (`Atari5200Keypad`). CONSOL
  bits 0–1 pick the controller the keypad scan reads.
- **Cartridges** (`Atari5200Cartridge`): 2K–32K mirrored through the window; 16K two-chip boards by the profile's
  model ("two-chip").
- **State:** every part implements `IStateful`; a mid-frame round trip is tested.

### Platform, Library and UI
- `GameSystem.Atari5200`; `.a52` files; No-Intro DATs ("5200"); no RetroAchievements console.
- `BiosKind.Atari5200` (2 KB, copied to `bios\a5200\`), `AppSettings.Atari5200BiosPath`, the Settings card.
- `InputService.KeypadMode`: while a 5200 game runs, the number row is controller 1's keypad (minus `*`, equals `#`,
  R Reset) and the pads' left sticks are read as analog sticks. The pad layout stays the NES's, so remaps and the
  controller pages need no new system.
- `--sample-data`: a 5200 game in the sample library.

### Tests
- `Atari5200Tests`: hand-assembled programs with a stand-in BIOS: mode 2 hi-res colours and the window, HSCROL,
  modes 4 and 6, players, collisions, PRIOR (including the OR of two winners), DLI and VBI counts, DMA's stolen cycles,
  WSYNC, VCOUNT, POKEY's timer IRQ, a pure tone's period, pots, the keypad scan, the controllers, a save state.
- `Atari5200HostTests`: an `.a52` through the host, the missing-BIOS message, the keypad on the keyboard.

### Not in milestone 2
- The 40K Bounty Bob board, the trak-ball, PAL machines, light pens; GTIA modes on non-hi-res lines; ANTIC register
  changes partway through a line (CHBASE, HSCROL); the keypad codes are from published notes, unchecked on hardware.

## Owner checks
Real 2600 cartridges or their dumps: the common games of each scheme (Combat, Pitfall!, Space Invaders, Frogger,
Decathlon, River Raid) look and sound right.

The 5200 with the owner's BIOS: the title screen shows, and Super Breakout, Pac-Man, Galaxian, Defender and Star
Raiders play; the keypad's keys (Star Raiders uses them all) do what the game's manual says.
