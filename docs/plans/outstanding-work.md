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

**As built.** `Sa1` (a second `W65816Cpu` on its own bus: I-RAM, BW-RAM and the $60–$6F bitmap view, CXB–FXB,
BMAPS/BMAP, the multiplier, divider and 40-bit sum of products, variable-length reads, the H/V and linear timer,
normal DMA, and character conversion types 1 and 2; DMA finishes at once and write protection isn't enforced). `Sdd1`
(the $C0–$FF memory controller, and Andreas Naive's decompressor answering the DMA's reads while the channel's count
lasts; the console shows the chip its DMA register writes). `Obc1`. `NecDsp` now also runs the µPD96050 of the ST010
and ST011 (`st010.rom`, `st011.rom`, 52 KB) with the SNES's window onto its data RAM, which is the ST010's save file.
DSP-2/3/4 came with item 4. **Not done:** the Cx4 (its HG51B needs a low-level core and the player's data ROM) and the
SPC7110 (its context-model decompressor), the ST018 (an ARM) and the S-RTC. Each is a large job that can't be
checked here without the games; they stay in `docs/bugs.md` as a later item. Tests: `Sa1Tests`, `Sdd1Tests`,
`Obc1Tests`, `SetaDspTests`.

## 6. `--sample-data` Player on every system
The sample Apple II, Genesis and SNES games show a test pattern. Run a freely licensed homebrew for each instead,
from `data/homebrew/` (its licence recorded as for the NES homebrew), so the Player shows real pictures under
`--sample-data`.

## 7. Genesis: EEPROM saves, interlace mode 2, Master System mode
- I²C EEPROM (24C01–24C65) saves with the game database's wiring for each board.
- Interlace mode 2: 448-line double-resolution frames through the variable-size video path.
- Master System mode (the Power Base Converter): the VDP's mode 4 and the SMS memory map, for `.sms` files the
  player chooses to run on the Genesis core.
- **As built:** the I²C EEPROM (X24C01 and the 24Cxx family) with the wiring Sega's boards declare in the header
  ("RA" $E8 $40 on $200001). EA, Acclaim and Codemasters boards wire it differently and need a per-game database
  entry first, so they wait. Interlace mode 2 is done. Master System mode was dropped: a Power Base Converter just
  runs the Master System's own hardware, which GameVault already has as the Master System core, so `.sms` files open
  there.

## 8. Master System FM and the SC-3000 keyboard
- YM2413 (OPLL) for Japanese Master System games, with its built-in instrument patches computed from the published
  register values; the FM unit's detection port.
- The SC-3000 keyboard matrix on the PPI, mapped to the PC keyboard (like the Apple II), so BASIC cartridges run.
- **As built:** the VRC7's FM engine became `Opll`, shared by both (the VRC7's output is pinned sample for sample by
  a fingerprint test); the YM2413 adds three channels, the rhythm sounds and the YM2413's instruments (emu2413's
  table, MIT). The unit is there for Japanese cartridges (header region 3, or a Japan profile). `.sc` files get the
  keyboard.

## 9. Apple II: monochrome, character ROM, mid-frame changes
- Settings → Apple II → "Monochrome" (green, amber or white).
- The player's own character ROM (Settings), with the built-in font as today when none is chosen.
- Draw the video per scanline from the soft switches then, so mid-frame mode changes show.

**As built.** `AppleConsole.RunFrame` runs the CPU 65 cycles at a time and draws each of the 192 visible lines
(`AppleVideo.RenderLine`) from the memory and switches when the beam finishes it. `AppleMonitor` (Color, Green,
Amber, White) picks the palette; on a monochrome monitor hi-res, lo-res and double hi-res show their dots, with no
artifact colour, in the phosphor's colour. `AppleCharacterRom` reads the player's 2, 4 or 8 KB character ROM and
finds its layout (index by ASCII, low six bits or screen code; active high or low; bit order; first line) by how
well A–Z and 0–9 match GameVault's own font, refusing files under 85 % agreement. Settings → Apple II gains
**Character ROM** and **Monitor**. Tests: `AppleDisplayTests`. The floating bus stays open.

## 10. N64 accessories and video
- The Rumble Pak (rumble through SDL) and the Transfer Pak (the player's own Game Boy cartridge dump and save).
- GameShark button codes (88/89).
- The VI's anti-alias and divot filters, and 32-bit frames shown at full depth.

**As built (first part).** `IN64Accessory` in each controller's slot; `N64RumblePak` (probe at $8000, motor at
$C000); `N64Console.RumblePaks` (Settings → Nintendo 64 → Rumble Pak) puts one in every controller. The host passes
`IRumbleSource.RumbleLevel` to `InputService.SetRumble`, and `ControllerManager` sends it to the pad on that port,
where `SdlControllerBackend` runs `SDL_GameControllerRumble` from its polling thread. The motor isn't part of the save
state. GameShark codes turned out not to exist for the N64 at all (cheats are NES-only from the store to the UI), so
they are a separate part with the cheat system made per-system. Tests: `N64AccessoryTests`, `ControllerManagerTests`.

32-bit frames: `IEmulatedConsole.TrueColorFrame` (empty unless the console drew more colours than the 15-bit
indexed frame holds) goes through `IVideoSink.PresentArgb` and `VideoFrame.IsArgb` to both renderers and to
screenshots; the N64 fills it when the VI is in 32-bit mode. The PlayStation's 24-bit mode (item 11) can use the
same path. The VI's anti-alias and divot filters need each pixel's coverage, which the RDP computes but doesn't keep in RDRAM's
hidden bits yet; they wait for that (with the GPU renderer, item 14). Tests: `N64ConsoleTests`, `FrameSinkTests`.

## 11. PlayStation peripherals and output
- The mouse, the GunCon and the Justifier (with the PC mouse as the gun, as the NES Zapper does).
- 24-bit output shown at full depth (FMVs).

**As built (first part).** The pads are DualShocks (`PsDualShock`, byte by byte on SIO0): digital (41h), analog
(73h) and config (F3h) modes with 43h–4Dh, the left stick working the D-pad while digital, and the motors mapped by
4Dh driving `IRumbleSource` like the N64's Rumble Pak. **Settings → PlayStation → Analog mode** starts them analog,
for games that wait for the Analog button. PlayStation games get both sticks, the stick clicks and T/F/G/H as the
D-pad, as PS2 games do. 24-bit display goes through `TrueColorFrame`. SIO0's state is version 2 and still loads
version 1. The GunCon (`PsGunCon`: ID 63h, buttons and the latched X/Y, aimed with the PC mouse) and the mouse
(`PsMouse`: ID 12h, movement since the last read) go in port 1 from **Game Details → Properties → Port 1**. **Not
done:** the Justifier, which needs the lightgun interrupt (IRQ 10) timed to the beam, and the multitap, whose SIO
protocol can't be checked here. Tests: `PsDualShockTests`,
`PsGpuTrueColorTests`, `ControllerManagerTests`.
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

**As built.** Disk System games start from the same image and BIOS on both PCs, and a disk change travels in the
player's input (`ControllerState` bits 50–55 on the NES, one frame), so `NesConsole.SetControllerState` changes the
disk on the same frame everywhere and rollback replays it. Internet hosting: `StunMessage` (RFC 5389 Binding) on the
netplay socket gives the public address, carried in the same 10-character code with bit 48 set; the joiner learns its
own the same way and shows it as a reply code, and `UdpNetplayLink.ExpectPeer` makes the host send to it (and accept
its packets from another port of the same address) until the routers open. A relay (`src/GameVault.Relay`, a small
cross-platform console program) pairs two players by room (`GVRJ` + room, answered `GVRA`) and forwards their
datagrams; with **Settings → Netplay → Relay server** set, hosting goes through it with a 16-character code. Tests:
`InternetNetplayTests` (a stand-in STUN server and the relay on loopback), `LanNetplayTests`, `NetplayViewModelTests`.

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

**Measured first, and not needed now.** A loop of RDRAM loads, adds, stores and a branch runs at 369 M CPU cycles a
second on the interpreter (Release, the Linux build machine): about four times the VR4300's 93.75 MHz, so the CPU
isn't what holds N64 games below full speed; the RDP is (phase-17-performance.md §3). The recompiler waits until a
profile of real games shows the CPU on top; until then the GPU renderer (item 14) is where N64 speed comes from.

## 16. PS2 netplay and run-ahead
Make PS2 save states small and fast enough (dirty-page tracking for the 32 MB of RAM and the GS's 4 MB), then
enable run-ahead and rollback netplay.

**Measured: not possible yet.** A PS2 state is 51 MB. Saving it into a reused writer takes 7.2 ms and loading it
9.1 ms (Release, the Linux build machine, after the `gs-basic` homebrew's tenth frame), against a 16.7 ms frame.
Rollback saves a state every frame and replays several frames after a late input, and run-ahead saves, runs two
frames and loads every frame; on top of a PS2 frame, which already takes most of the frame's time in real games, either
would roughly halve the speed. Dirty-page tracking could make the save cheaper but not the replays. This waits until
the PS2 runs well above full speed (the GPU renderer, item 14); `SystemBudgets` keeps both off with a message until
then.

## 17. Sega CD and 32X
The Sega CD (a second 68000, the CD drive, PCM and the ASIC's scaling) with the player's own BIOS; the 32X (two
SH-2s and its VDP). Its own plan file first.

**As built (Sega CD).** `docs/plans/sega-cd-32x.md` milestone 1: `SegaCdUnit` (the sub CPU, PRG-RAM, Word RAM in
2M and 1M mode, backup RAM, the gate array), `SegaCdDrive` (the CDD's protocol at 75 Hz), `Lc8951` (the CDC),
`Rf5c164` (PCM) and `SegaCdGraphics` (the stamp ASIC), plugged into `GenesisConsole`. `SegaCdDiscs` tells Sega CD
discs from PlayStation ones; the player's BIOS in **Settings → Sega CD**. Tests: `SegaCdTests`, `SegaCdHostTests`.
The 32X is milestone 2.

## 18. Phase 18: GameCube / Wii
As SPEC §7.11 and §12. Its own `docs/plans/phase-18.md` first.

## 19. Phase 19: Atari 2600 / 5200
As SPEC §7.11 and §12. Its own `docs/plans/phase-19.md` first.

**As built (2600).** `docs/plans/phase-19.md` milestone 1: `Atari2600Console` (the NMOS `Cpu6502` on a 13-bit bus,
WSYNC holding it), `Tia` (drawn one colour clock at a time from the registers then; playfield, players, missiles,
ball, HMOVE, collisions, VSYNC framing with the picture's top where VBLANK ends, Ron Fries' sound counters), `Riot`,
and `Atari2600Cartridge` (2K/4K, F8/F6/F4 with SuperChip detection, E0, FE, 3F, FA; the profile's model can name
one). `.a26` files, the library's system filter, No-Intro DATs, RetroAchievements console 25, the sample library.
Tests: `Atari2600Tests`, `Atari2600CartridgeTests`, `Atari2600HostTests`. The 5200 is milestone 2.


## Not in this plan (needs the owner)
- Real-game and real-cartridge checks in every phase's owner checklist.
- RetroAchievements' approval for hardcore unlocks, and signing in with the owner's account.
- Password and memory-map checks against real copies (phases 6 and 9).
