# Outstanding work — performance, ease of use and features

**Goal:** finish everything the phase plans, `docs/bugs.md` and SPEC still leave open, one change at a time.
Each item below is one branch and one pull request, merged before the next starts. Every item keeps all tests
green, updates `docs/bugs.md`, `docs/compatibility.md` and the README status where they apply, and updates the
user manual (`docs/manual/`, rebuilt to the PDF) when players can see the change.

**Where it's checked.** This work is done in a Linux container. Core, Library, Platform and ViewModels build and
test there (`GameVault.CrossPlatform.slnf`). The WinUI app builds only on Windows, so XAML and code-behind
changes are checked by CI's Windows job (build, tests and the `--sample-data` smoke test of every screen). Anything
that needs a real GPU, a real controller in the hand, or real games and cartridges is listed under **Owner checks**
in that item, as the phase plans do.

**Order.** The first four come first, as asked. The rest follow in roughly the order of value to players for the
effort, with the new systems last and in SPEC's order (§13.1).

| # | Item | Area | Size |
|---|---|---|---|
| 1 | Controller-only navigation of every page | UX | M |
| 2 | Stereo audio for every system | Audio | M |
| 3 | PS2 VU recompiler | Performance | L |
| 4 | SNES enhancement chips, part 1: DSP-1 and Super FX | Features | L |
| 5 | SNES enhancement chips, part 2: SA-1, Cx4, DSP-2/3/4, S-DD1, OBC1, ST010/011, SPC7110 | Features | L |
| 6 | `--sample-data` Player runs real homebrew on the Apple II, Genesis and SNES | UX | S |
| 7 | Genesis: EEPROM saves, interlace mode 2, Master System mode | Features | M |
| 8 | Master System FM (YM2413) and the SC-3000 keyboard | Features | M |
| 9 | Apple II: monochrome, the player's character ROM, mid-frame mode changes | Features | S |
| 10 | N64: Rumble Pak, Transfer Pak, GameShark button codes, VI filter and 32-bit colour | Features | M |
| 11 | PlayStation: mouse, GunCon and Justifier, 24-bit output, second multitap | Features | M |
| 12 | PS2: memory card slot 2, multitap, pressure-sensitive buttons, mipmapping | Features | M |
| 13 | Internet netplay (relay and STUN) and Famicom Disk System netplay | Features | L |
| 14 | GPU renderers (Direct3D 11) for the GS, the RDP and the PlayStation GPU, with upscaling | Performance | XL |
| 15 | N64 VR4300 recompiler | Performance | L |
| 16 | PS2 netplay and run-ahead | Features | M |
| 17 | Sega CD and 32X | Features | XL |
| 18 | Phase 18: GameCube / Wii | New system | XL |
| 19 | Phase 19: Atari 2600 / 5200 | New system | L |

Items 14, 17 and 18 are each larger than a whole earlier phase. Each gets its own `docs/plans/` file with a first
milestone before work starts, as SPEC §7.11 asks of the large phases.

---

## 1. Controller-only navigation (SPEC §9.3, GV-022)

**Today.** `ControllerManager` turns the D-pad, the left stick, South and East into `NavigationAction`s, and
`PadNavigation` (App) replays them as the arrow keys, Enter and Esc through `SendInput`. That only works where a
control already handles those keys:
- Arrow keys move focus between controls only inside the Library's two lists, the only places with
  `XYFocusKeyboardNavigation="Enabled"`. On Settings, Game Details, Catalog Import, Dump Cartridge and the Player's
  flyouts, the D-pad does nothing once focus is on a button.
- A text box keeps the arrows for its caret, so focus can't leave it with the pad.
- Enter doesn't toggle a ToggleSwitch or a CheckBox, or open an Expander.
- Nothing scrolls a long page, switches pages, or opens a context menu.

**Change.**
- **`PadFocusPlanner` (Platform, unit tested):** decides what each action does from a description of the focused
  element (its kind, whether it's in an open popup, whether the page can go back, whether it has a context menu):
  move focus in a direction, send a key, invoke, toggle, expand, select, close the popup, go back, focus the
  first control, scroll a page, switch the main page, open a context menu, or focus the main menu.
- **`PadNavigation` (App):** classifies the focused element through its UI Automation patterns (Invoke, Toggle,
  ExpandCollapse, SelectionItem, RangeValue, Value), then carries out the plan:
  - Directions move focus with `FocusManager.TryMoveFocus` (XY, searched within the open popup or the window), so
    every page and flyout works without per-control setup. A horizontal slider takes left/right as its value;
    items in an open drop-down or menu take the arrow keys.
  - South invokes, toggles, expands or selects; list items get Enter so `ItemClick` fires.
  - East closes an open popup, else goes back, else sends Esc.
  - If nothing has focus, the first press focuses the page's first control.
- **New buttons** (`NavigationAction`): the shoulders switch between Library and Settings, the triggers scroll a page
  up or down and focus the first control in view, North opens the focused control's context menu (for example a
  game tile's), and Start focuses the main menu. Shoulders and triggers repeat while held, like the D-pad.
- **Tests:** `PadFocusPlannerTests` cover every rule; `ControllerManagerTests` cover the new buttons, trigger
  thresholds and repeat.
- **Docs:** `docs/design/input.md` §7, manual chapter 7 (Controls), GV-022 in `docs/bugs.md`.

**Owner checks:** walk every page with only a controller, at 100 % and 150 % scale.

## 2. Stereo audio

**Today.** The audio path is mono end to end (`BandLimitedResampler`, `AudioRingBuffer`, `WasapiAudioOutput`). The
Genesis computes the YM2612's panning and then averages it; the SNES DSP, PlayStation SPU, N64 AI and PS2 SPU2 do
the same with their left and right outputs.

**Change.**
- `BandLimitedResampler` keeps two channels. `AddDelta(clock, delta)` still feeds both (the NES, SG-1000/SMS PSG and
  Apple II stay mono), and a new `AddDelta(clock, left, right)` feeds each. `Read` returns interleaved stereo
  frames; the analog filter chain runs per channel.
- The host, ring buffer and WASAPI output carry interleaved stereo; latency and pacing count frames, not samples.
- Genesis, SNES, PlayStation, N64 and PS2 send their real left/right. The Game Gear isn't emulated, so the SN76489
  stays mono.
- **Settings → Audio → "Mono"** (off by default) mixes down for one speaker or one ear.
- Netplay, recordings and save states are unchanged: audio isn't part of any of them.
- **Tests:** stereo resampler tests (panned steps reach only their channel, mono deltas reach both); a hard-panned
  YM2612 channel, a panned SNES voice and a panned SPU voice come out of one side only; the soak and pacing tests run
  on frames.

## 3. PS2 VU recompiler

**Today.** VU1 runs about 77 M cycles a second on a transform loop; the time is in the interpreter's per-pair
pipeline bookkeeping (stalls, the MAC/status/clip flag queues, Q and P), not the maths (`phase-17-performance.md`
§6.1).

**Change.**
- Blocks of micro-instruction pairs, from a start address up to a branch and its delay pair, an E-bit end and its
  delay pair, or the end of micro memory.
- For a straight run, the stalls and every flag-queue and Q/P timing are worked out once at compile time from the
  same tables the interpreter uses, as long as the block starts with the same pipeline occupancy it was compiled
  for (the key includes that state); otherwise the interpreter runs that stretch.
- Compiled to a `DynamicMethod` like the EE's blocks, with the interpreter's float helpers called for the
  non-IEEE maths. XGKICK, the VIF's MSCAL and anything touching another unit go through the interpreter.
- Code in micro memory is checked against the block's words before each run (the VIF uploads new microcode at
  any time).
- `Ps2Vu.RecompilerEnabled`, on where the runtime can compile.
- **Tests:** random VU programs run interpreted and recompiled, comparing the whole VU state (registers, flags,
  pipeline, memory, cycles) after every slice; ps2autotests' VU programs and the homebrew compared frame by frame
  with the full save state; the benchmark reports cycles per second.

## 4. SNES enhancement chips, part 1: DSP-1 and Super FX

**Today.** A game that needs any enhancement chip gets a message (`phase-14.md`).

**Change.**
- **Cartridge detection:** the header's chipset byte and the game database pick the chip.
- **DSP-1/1A/1B (NEC µPD77C25):** a µPD77C25 core that runs the chip's program ROM. That ROM is Nintendo's, so the
  player supplies it (Settings → Super Nintendo → "DSP-1 program ROM", 8 KB `dsp1b.rom` as dumped), exactly like the
  PlayStation BIOS. Without it, the game gets a message naming the setting. The µPD77C25 core also runs DSP-2, 3
  and 4 in item 5.
- **Super FX (GSU-1/GSU-2):** the GSU's instruction set, its own RAM and ROM buses with the SNES's access rules,
  the pixel cache and PLOT, the colour/bitplane modes, the instruction cache, and IRQ on STOP. No program ROM is
  needed (the game's code runs on it).
- Both are deterministic and part of the save state; a save-state round-trip test for each chip, as for every
  mapper.
- **Tests:** µPD77C25 instruction tests, Super FX instruction and PLOT tests on synthetic programs, and freely
  licensed Super FX homebrew if a redistributable one exists (listed in `tests/TestRoms/SOURCES.md`).
- **As built:** one folder setting (Settings → Super Nintendo → Enhancement chip ROMs) holds every chip dump, and the
  same µPD7725 core also runs DSP-2, DSP-3 and DSP-4 (chosen by the game's title, each at its board's addresses), so
  those moved here from item 5. No redistributable Super FX test ROM was found, so the GSU is checked by
  hand-assembled programs and a whole-console test.

**Owner checks:** Pilotwings and Super Mario Kart (DSP-1) with your own DSP-1 dump; Star Fox and Yoshi's Island
(Super FX).

## 5. SNES enhancement chips, part 2
SA-1 (a second 65C816 at 10.74 MHz with its DMA, character conversion and arithmetic unit), Cx4, DSP-2/3/4 and
ST010/011 (on the µPD77C25 / µPD96050 core, each with the player's program ROM), S-DD1 decompression, OBC1, and
SPC7110 with its decompression and RTC. Same rules and tests as item 4.

## 6. `--sample-data` Player on every system
The sample Apple II, Genesis and SNES games show a test pattern. Run a freely licensed homebrew for each instead,
from `data/homebrew/` (its licence recorded as for the NES homebrew), so the Player shows real pictures under
`--sample-data`.

## 7. Genesis: EEPROM saves, interlace mode 2, Master System mode
- I²C EEPROM (24C01–24C65) saves with the game database's wiring for each board.
- Interlace mode 2: 448-line double-resolution frames through the variable-size video path.
- Master System mode (the Power Base Converter): the VDP's mode 4 and the SMS memory map, for `.sms` files the
  player chooses to run on the Genesis core.

## 8. Master System FM and the SC-3000 keyboard
- YM2413 (OPLL) for Japanese Master System games, with its built-in instrument patches computed from the published
  register values; the FM unit's detection port.
- The SC-3000 keyboard matrix on the PPI, mapped to the PC keyboard (like the Apple II), so BASIC cartridges run.

## 9. Apple II: monochrome, character ROM, mid-frame changes
- Settings → Apple II → "Monochrome" (green, amber or white).
- The player's own character ROM (Settings), with the built-in font as today when none is chosen.
- Draw the video per scanline from the soft switches then, so mid-frame mode changes show.

## 10. N64 accessories and video
- The Rumble Pak (rumble through SDL) and the Transfer Pak (the player's own Game Boy cartridge dump and save).
- GameShark button codes (88/89).
- The VI's anti-alias and divot filters, and 32-bit frames shown at full depth.

## 11. PlayStation peripherals and output
- The mouse, the GunCon and the Justifier (with the PC mouse as the gun, as the NES Zapper does).
- 24-bit output shown at full depth (FMVs).
- A second multitap (8 players): the input service and the Player grow to 8 ports.

## 12. PS2 peripherals and GS details
- Memory card slot 2 and the multitap, as the PlayStation's.
- The DualShock 2's pressure-sensitive buttons, from the analog triggers and full presses.
- Mipmapping and LOD in the software GS.

**As built.** Mipmapping in the GS: MXL, MMIN's four mipmap modes, the levels' addresses from MIPTBP1/2 or MTBA's
packed layout, each level's size and region clamp halved, and a blend by LOD's fraction between levels. Slot 2
holds one card shared by every game (`Memory card (slot 2).ps2`); SIO2's state is now version 2 and still loads
version 1. L2 and R2 report how far a pad's analog triggers are pulled (`ControllerState` bits 50–63), other
buttons report full presses. **Not done:** the multitap — SIO2's multitap commands (21h) can't be checked here
against hardware or a test program, so rather than guess they stay open in `docs/bugs.md`; and the DualShock 2's
vibration, which needs the rumble path from item 10. Tests: `Ps2GsRasterTests`, `Sio2Tests`,
`PlayStation2PlatformTests`, `ControllerManagerTests`.

## 13. Internet netplay
- A relay server option and STUN-based NAT traversal on top of the LAN rollback netplay (SPEC §7.7).
- Netplay for Famicom Disk System games (disk contents synced at start).

## 14. GPU renderers
- Direct3D 11 renderers for the PS2 GS, the N64 RDP and the PlayStation GPU, with internal resolution scaling.
- The software renderers stay the reference: at 1× each GPU renderer is compared with them on the test programs
  and homebrew, in CI's Windows job on WARP (Direct3D's software device), within a per-system tolerance for the
  differences fixed-function blending makes.
- Software stays the default where results must be exact (netplay, verified challenges).
- Its own plan file first.

## 15. N64 recompiler
A VR4300 block recompiler to IL like the EE's, with the interpreter as the reference and the same kind of
differential tests.

## 16. PS2 netplay and run-ahead
Make PS2 save states small and fast enough (dirty-page tracking for the 32 MB of RAM and the GS's 4 MB), then
enable run-ahead and rollback netplay.

## 17. Sega CD and 32X
The Sega CD (a second 68000, the CD drive, PCM and the ASIC's scaling) with the player's own BIOS; the 32X (two
SH-2s and its VDP). Its own plan file first.

## 18. Phase 18: GameCube / Wii
As SPEC §7.11 and §12. Its own `docs/plans/phase-18.md` first.

## 19. Phase 19: Atari 2600 / 5200
As SPEC §7.11 and §12. Its own `docs/plans/phase-19.md` first.

## Not in this plan (needs the owner)
- Real-game and real-cartridge checks in every phase's owner checklist.
- RetroAchievements' approval for hardcore unlocks, and signing in with the owner's account.
- Password and memory-map checks against real copies (phases 6 and 9).
