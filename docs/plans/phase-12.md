# Phase 12 — Apple II, II+ and IIe

**Goal (SPEC §7.11):** GameVault runs Apple II software from disk images, on the player's own machine
ROMs, with the computer-style input the Apple II needs.

## 1. What the player supplies
The Apple II's firmware (Monitor and Applesoft/Integer BASIC, and the IIe's ROMs) is copyrighted, so
GameVault never ships or downloads it, as with the Disk System BIOS.
- **Settings → Apple II** takes the player's ROM files:
  - the II/II+ 12 KB system ROM;
  - the IIe 16 KB ROM (CD and EF halves, original or enhanced);
  - the 256-byte Disk II boot ROM (P5, 341-0027).
- GameVault checks their size and recognizes known dumps by SHA-1 (only to name them in Settings; any
  dump of the right size works).
- Without the ROMs, Apple II games show the same kind of message as Disk System games do.

## 2. CPU
- **NMOS 6502 with decimal mode:** GameVault's 6502 is a 2A03, which has no BCD arithmetic. A
  `DecimalMode` option adds it for the Apple II (ADC/SBC in decimal, with the NMOS N, V and Z flags).
  The NES keeps it off.
- **65C02 for the enhanced IIe:** the extra opcodes (BRA, PHX/PHY/PLX/PLY, STZ, TRB/TSB, (zp)
  addressing, INC/DEC A, BIT immediate), the bug fixes (JMP (abs) across a page, decimal-mode flags),
  and the CMOS NOPs.
- **Tests:** Klaus Dormann's `6502_functional_test` (including the decimal test) and
  `65C02_extended_opcodes_test` (GPL v3, fetched by the test-ROM scripts), which must reach their
  success traps. Plus the NES's existing CPU tests, unchanged.

## 3. Memory and soft switches
- **II/II+:**
  - 48 KB RAM;
  - the 16 KB Language Card (`$C080–$C08F`, with its double-read write-enable);
  - the I/O page `$C000–$C0FF`: keyboard, strobe, speaker, the graphics soft switches, paddles and
    buttons, annunciators;
  - slot ROMs `$C100–$C7FF`.
- **IIe:**
  - auxiliary 64 KB (80-column card) with RAMRD/RAMWRT/ALTZP/80STORE/PAGE2/HIRES;
  - `INTCXROM`/`SLOTC3ROM`;
  - the status reads at `$C011–$C01F`.
- **Slots:** 6 holds the Disk II; the rest are empty. The 80-column firmware is the IIe's own.

## 4. Video (`AppleVideo`)
- **Modes:** 40-column text (with flash and inverse), lo-res, hi-res, mixed mode, and on the IIe
  80-column text and double hi-res.
- **Colour:** an NTSC artifact-colour renderer, as the monitor showed it: 280×192 hi-res becomes
  560-dot colour with the half-pixel shift. A monochrome option (green, amber, white) is in Settings.
- **Frame buffer:** each line becomes 560 dots, which doesn't fit the 256×240 frame the NES and Sega
  consoles use. So `IEmulatedConsole` gains `ScreenWidth`/`ScreenHeight` (default 256×240), and the
  video sink takes any size up to 640×480:
  - the D3D11 path already scales a texture, which is now created at the console's size;
  - screenshots, thumbnails and maps take the size from the console.
- **Timing:** the video scanner is cycle-counted (65 cycles per line, 262 lines), so the "vapor lock"
  floating-bus reads some games use return the right byte.

## 5. Disk II
- **Images:**
  - `.dsk`/`.do` (DOS 3.3 order) and `.po` (ProDOS order) are nibblized to 6-and-2 GCR tracks on load;
  - `.woz` (1 and 2) is read bit for bit, so copy-protected disks work;
  - `.nib` too.
- **The controller:** a logic-state-sequencer model (as described for the Disk II's P6 PROM). It sets
  the timing of bits under the head, the 4-phase stepper and the write-protect sense.
- **Writes:** go to the in-memory image and are saved like the Disk System's (a patch file next to
  GameVault's saves; the player's image file is never modified).
- **Two drives;** the Player gets a disk menu with Insert disk, Swap and Eject (like the FDS side
  menu).

## 6. Sound and input
- **Speaker:** `$C030` toggles a 1-bit speaker. It goes through the band-limited resampler with a
  DC-blocking filter.
- **Keyboard:** while an Apple II game is running, the PC keyboard types into it: letters, digits,
  Return, Esc, arrows, Ctrl+key, and Reset as Ctrl+F12. GameVault's own hotkeys (F2–F12, Tab,
  Backspace for rewind) stay hotkeys, except that **Backspace is the Apple's left arrow** while
  typing. Rewind moves to Shift+Backspace for Apple II games.
- **Joystick:** a controller's left stick is the Apple's analog joystick (paddles 0/1, read with the
  558 timer) and A/B are buttons 0/1. The keyboard arrows and Z/X can stand in for players without a
  controller (Settings → Input).

## 7. Platform, library and UI
- **Extensions:** `.dsk`, `.do`, `.po`, `.nib` and `.woz`. The library system is `AppleII`, with the
  model chosen per game in Game properties (II+, IIe or enhanced IIe; default enhanced IIe).
- **Video sizes:** host and video sink support for the variable frame size (above).
- **RetroAchievements:** console id 38. Disk images hash as rcheevos does (`rc_hash_apple2`), and
  memory follows rcheevos' layout (main RAM, then auxiliary).
- **Save states, rewind, run-ahead and challenges** work through `IEmulatedConsole`. Netplay works too
  (both players need the same ROMs; the handshake compares a hash of the ROM set).
- **Sample data:** an Apple II sample game in the library. The Player runs a built-in test program on
  a tiny monitor GameVault writes itself (not Apple's), showing hi-res artifact colours.

## 8. Tests
- **CPU:** Dormann's functional tests (6502 with decimal mode, 65C02 extended).
- **Soft switches and memory banking:** IIe aux memory, the Language Card, `INTCXROM`.
- **Video:** golden images for each mode, with the test monitor ROM writing patterns.
- **Disk II:**
  - nibblizing round-trips: DOS 3.3 and ProDOS order back to sectors;
  - reading a sector with a small RWTS-style routine in the test ROM;
  - write-then-read;
  - WOZ parsing.
- **Speaker:** samples follow the toggles.
- **Determinism and save-state round-trips.**

## Results
- **CPU:** a `CpuVariant` on GameVault's cycle-exact 6502 (one bus access per cycle). It adds NMOS
  decimal mode for the II/II+/IIe and the 65C02 for the enhanced IIe, including the WDC/Rockwell bit
  instructions, STP and WAI. Klaus Dormann's 6502 functional test (with decimal) and 65C02
  extended-opcodes test both reach their success traps; the NES CPU tests are unchanged.
- **Frames of any size:** `IEmulatedConsole` has `ScreenWidth`, `ScreenHeight` and `PixelAspect`. The
  frame sink carries each frame's size, and these follow the frame: the D3D11 texture and shader, the
  Win2D fallback, display layout (overscan and TV shape apply to 256×240 only), thumbnails, screenshots
  and map tiles. The watcher's thumbnails now use each system's own palette, which also fixes them for
  Sega games.
- **The machine:** `AppleConsole` covers:
  - the II+, IIe and enhanced IIe;
  - main and auxiliary memory, the Language Card, and the IIe memory and video switches with their
    status reads;
  - the keyboard latch and any-key-down, the speaker (1-bit, through the band-limited resampler, settling
    to rest), and paddles and buttons from the pad;
  - slot 6 with the player's Disk II ROM, and the IIe's internal $C100–$CFFF ROM.
- **Video:** 560×384, 4:3.
  - Modes: 40- and 80-column text, lo-res, hi-res with NTSC artifact colour (including the half-pixel
    shift and gap fill), double hi-res, and mixed mode.
  - Text uses GameVault's own 5×7 font, because Apple's character ROM is copyrighted.
- **Disk II:** a bit-level controller.
  - Reads: the latch holds a complete byte for two bit times, and zero bits after a complete byte are
    dropped (self-sync).
  - Writes: the data register shifts out a bit at a time.
  - Also the stepper and two drives.
  - **Images:** `.dsk`/`.do`/`.po` nibblized with 6-and-2 GCR (DOS and ProDOS order; a ProDOS volume
    header in a `.dsk` picks ProDOS order), `.nib`, and WOZ 1 and 2.
- **Platform:**
  - Settings → Apple II takes the system ROM and the Disk II boot ROM.
  - The library finds `.dsk`, `.do`, `.po`, `.nib` and `.woz` files.
  - Game properties' `model` picks II+, IIe or enhanced IIe.
  - Typing mode sends every key to the Apple, with Shift and Ctrl:
    - F1 is Esc and Ctrl+F12 is Reset;
    - Backspace types the left arrow, and Shift+Backspace rewinds.
  - RetroAchievements hashes the disk as rcheevos does (MD5 of the file) and reads main and auxiliary
    RAM in its layout.
- **Tests:**
  - Dormann's two suites;
  - disk round-trips (DOS and ProDOS order), WOZ 2 parsing, and a write-then-read;
  - a 6502 loop reading nibbles in order through the controller, and the stepper;
  - the video modes and colours, the keyboard, the Language Card, IIe auxiliary memory, paddles and
    buttons, and speaker audio;
  - save-state replay;
  - host loading with and without ROMs, typing, and 560×384 frames.
- **Changed from the plan, left for later:**
  - The Player has no disk menu yet, so the second drive and disk swapping wait. Games that need disk 2
    can't run yet.
  - The video is drawn once per frame (no mid-frame mode changes or floating-bus "vapor lock").
  - No monochrome option; a user-supplied character ROM isn't used.
  - Sample data has an Apple II game in the library, but the `--sample-data` Player doesn't run an Apple
    program.

## Order of work
1. 6502 decimal mode and the 65C02, with Dormann's tests.
2. Variable frame size through the video path.
3. Memory, soft switches and video.
4. Disk II with DSK/PO/NIB, then WOZ.
5. Keyboard, joystick and speaker.
6. Library, Settings (ROMs), Player disk menu, RetroAchievements and sample data.
7. Docs.

## Owner checklist
- Supply your own Apple II ROM dumps in Settings → Apple II.
- Try a few of your disk images (DOS 3.3, ProDOS, a copy-protected WOZ), and list anything that
  misbehaves.
