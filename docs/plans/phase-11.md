# Phase 11 — Sega SG-1000 / SC-3000 and Master System, plus multi-system groundwork

**Goal (SPEC §7.11):** GameVault plays SG-1000, SC-3000 (cartridges) and Sega Master System games, and
becomes a multi-system emulator that later phases plug into.

## 1. Multi-system groundwork
GameVault was built around `NesConsole`. Every later system needs the same frame loop, audio pacing,
rewind, run-ahead, save states, timeline, challenges and netplay, so these move onto a small
system-neutral interface. NES-only features (Game Genie, the Disk System, the Zapper, password capture,
the dumper) stay NES-only.

### Core: `GameVault.Core.Systems`
```csharp
public enum GameSystem { Nes, Sg1000, MasterSystem }

public interface IEmulatedConsole : IStateful
{
    GameSystem System { get; }
    ulong RomHash { get; }
    int ScreenWidth { get; }            // 256 for all three; the visible height varies
    int ScreenHeight { get; }           // NES 240, SG-1000 192, SMS 192 or 224
    ReadOnlySpan<uint> FrameArgb { get; } // the finished frame (systems with palettes convert here)
    double CpuClockRate { get; }
    double FrameRate { get; }
    BandLimitedResampler? Audio { get; }
    BandLimitedResampler EnableAudio(int sampleRate);
    void Power(); void Reset();
    void RunFrame(); void RunFrameSilently();
    void SetControllerState(int port, ControllerState state);   // the NES pad bits, mapped per system
    byte PeekMemory(uint address);      // side-effect-free, for memory maps and achievements
    byte[] SaveSnapshot(); void SaveSnapshot(StateWriter writer); void LoadSnapshot(ReadOnlyMemory<byte> data);
    IBatteryBacked? Battery { get; }
}
```
- **NES:** `NesConsole` implements the interface. Its frame is still palette indices plus the palette
  presets; `FrameArgb` converts once per frame. The NES video path keeps its own fast route.
- **Video:** `IVideoSink` gains `PresentArgb(ReadOnlySpan<uint>, width, height)`. The D3D11 sink takes
  any width and height up to 256×240, and `GameView` keeps the aspect ratio per system (4:3 picture,
  with pixel-aspect correction per system).
- **Input:** the NES `ControllerState` bits are reused: Up/Down/Left/Right, A → button 2, B → button 1,
  Start → Pause on SMS (it's an NMI) or nothing on SG-1000. Keyboard defaults stay the same.

### Platform
- **`EmulatorHost`** holds an `IEmulatedConsole`. Rewind, run-ahead, rollback netplay, challenges,
  the watcher (memory maps) and RetroAchievements all use the interface. NES-only paths check for
  `NesConsole`.
- **Loading** picks the system from the file: `.nes`/`.fds` (NES), `.sg`/`.sc` (SG-1000 and SC-3000
  cartridges), `.sms` (Master System), and `.zip` with one of them inside.
- **RetroAchievements:** the console id (NES 7, SG-1000 33, Master System 11) and each system's hash.
  For SG and SMS that's a plain MD5 of the file. Each system has its own memory map for rcheevos.

### Library
- A `system` column on `files` and `catalog` (a migration; existing rows are NES). The scanner finds
  the new extensions.
- Catalog import (No-Intro DATs) is per system.
- The Library gets a system filter (All, NES, Master System, SG-1000), and tiles show a small
  system badge.
- Game keys stay `sha1:<hash>`; the hash is over the whole file for SG/SMS, which have no header.

## 2. Z80 (`GameVault.Core.Cpu.Z80`)
- Every documented and undocumented instruction: the CB, ED, DD, FD, DDCB and FDCB prefixes; the
  undocumented flags (bits 3 and 5, from MEMPTR/WZ where the hardware does that); IM 0/1/2; NMI; HALT;
  the R register; the EI delay.
- **Cycle-accurate per instruction:** each instruction reports its T-states and the bus is ticked per
  instruction. That's accurate enough for SMS and SG games, as other accurate emulators do, but not
  per memory access.
- **Tests:**
  - zexdoc and zexall (Frank Cringle; GPL, redistributable, fetched by the test-ROM scripts) run on a
    tiny CP/M stub. Both must report no errors.
  - Unit tests for flags, block instructions, interrupts and the R register.

## 3. Video
- **TMS9918A** (SG-1000): Graphics I and II, Text and Multicolor modes; sprites with the
  4-per-line limit, the 5th-sprite flag and collision; the status register and its read timing; the
  VRAM address latch.
- **SMS VDP** (315-5124): Mode 4, with the 32-colour palette (CRAM), horizontal and vertical scroll
  (with the scroll locks), the line interrupt counter, left-column blanking, 8 sprites per line, and
  sprite zoom. The TMS modes run on the SMS VDP too.
- 192-line output. The 224/240-line modes come with the 315-5246 (SMS2), which is included and
  selected by the game database for games that need it.
- **Rendering:** per scanline, with the VDP stepped by Z80 cycles, so mid-frame register changes
  (raster effects) land on the right line.
- **Tests:** the VDP test ROMs that are freely licensed (FluBBa's SMS VDPTEST, and the TMS9918 tests
  from the MSX community if their licence allows), plus golden images from homebrew test programs
  GameVault builds itself.

## 4. Audio
- **SN76489 PSG:** three square channels and noise, with SMS-specific LFSR taps (16-bit, tapped
  bits 0 and 3), the volume table, and the 3.579545 MHz ÷ 16 clock. Output goes through the existing
  band-limited resampler.
- The YM2413 (the Japanese SMS FM unit) is left for later.

## 5. Cartridges and memory
- **SG-1000 / SC-3000:**
  - plain ROMs (up to 48 KB);
  - the Taiwanese 8 KB RAM-expansion boards;
  - the SC-3000 keyboard isn't emulated in this phase (BASIC cartridges need it).
- **Master System:**
  - the Sega mapper (`$FFFC–$FFFF` with cartridge RAM and battery saves);
  - Codemasters;
  - Korean (`$A000`);
  - the 8 KB "4 PAK" style boards that need them.
- **Game database:** `data/gamedb/sms.json`, keyed by SHA-1. It covers the mapper, the region
  (NTSC/PAL, Japan/export), whether the game needs the SMS2 VDP, and the input device.
- **No BIOS:** the SMS boots cartridges directly. GameVault sets up the state the BIOS leaves (stack
  pointer, the `$3E` memory control port, VDP registers), as documented on SMS Power!.

## 6. I/O
- **SMS:**
  - The memory control port `$3E` and the I/O control port `$3F` (TH lines, and region detection:
    export games read back what they wrote).
  - The V and H counters (`$7E/$7F`), the VDP (`$BE/$BF`), the PSG (`$7F` writes) and the joypad
    ports (`$DC/$DD`).
  - The Pause button is an NMI.
- **SG-1000:** the PPI-style ports and two joypads.

## 7. Save states, rewind, run-ahead and netplay
Every new component implements `IStateful`, and each system has save/load round-trip tests and
determinism tests (two runs with the same inputs give the same RAM and frame). Netplay, rewind and
run-ahead work unchanged through `IEmulatedConsole`.

## 8. UI
- **Library:** the system filter and badges, and "Open game file" accepts the new extensions.
- **Player:**
  - the frame size and aspect per system;
  - the Codes panel, Disk menu, Zapper and password capture hide for non-NES games;
  - Pause on SMS is mapped to Start (keyboard Enter, pad Start).
- **Settings → Input:** button names per system in the controller help ("Button 1", "Button 2").
- **`--sample-data`:** a Master System sample game in the library, and the Player runs a built-in SMS
  test pattern (rendered by the real VDP) so the new video path is previewed.

## 9. Docs
- SPEC §7.11 progress.
- `docs/compatibility.md` gets a Sega section (test ROMs and mapper list).
- `tests/TestRoms/SOURCES.md` lists the new test ROMs and their licences.

## Order of work
1. `IEmulatedConsole`, with NesConsole implementing it, and the host and video path refactor. All
   NES tests stay green.
2. Z80, with zexdoc/zexall.
3. TMS9918 and the SG-1000 console.
4. SMS VDP, mappers and I/O, and the SMS console.
5. SN76489.
6. Library: system column, scanner, filter, catalog.
7. RetroAchievements for the new systems.
8. UI and sample data.
9. Docs.

## Results
- **Groundwork:** `IEmulatedConsole` (Core) is what the host, rewind, run-ahead, netplay, challenges,
  memory maps and RetroAchievements use.
  - Every system draws into the same 256×240 indexed frame with its own palette, so the D3D11 path,
    screenshots, thumbnails, maps and the recap needed no changes. The 192-line picture sits in a
    border, as on a TV.
  - `ConsoleFactory` builds the right console for loading, netplay and challenge checks.
- **Z80:** zexdoc and zexall pass with no errors. They run in a separate Release CI job, about
  80 seconds each.
- **Consoles:** `SegaConsole` (SG-1000 and Master System) has the VDP, PSG, the Sega/Codemasters/Korean
  boards and the I/O ports, and the Pause button is the NMI. Save states round-trip, and a replay from
  a state is identical.
- **Library:** schema version 2 adds a `system` column. The scanner finds `.sms`, `.sg` and `.sc` files,
  zipped too. DAT imports take the system from each ROM's extension or the DAT's name. The Library has a
  System filter, which shows once there's more than one system, and each tile has a system badge.
- **RetroAchievements:**
  - Master System and SG-1000 games are hashed as rcheevos does (plain MD5).
  - Memory reads follow rcheevos' address layouts for these consoles (system RAM at 0, then cartridge
    RAM).
- **Player:** the Codes panel hides for non-NES games. Netplay, the timeline, rewind, challenges and
  achievements all work for Sega games.
- **Sample data:** a Master System and an SG-1000 game in the library.
- **Changed from the plan:**
  - The `--sample-data` Player still shows the NES test pattern. The SMS video path is covered by the
    host test (a Mode 4 ROM drawing through the real frame sink).
  - Settings → Input doesn't rename buttons per system yet; `docs/design/input.md` §6.1 gives the
    mapping.
  - The SMS VDP is always the 315-5246 (SMS2), which runs every SMS1 game's modes as well.

## Owner checklist (later)
Try a handful of your own SG-1000 and SMS cartridges or ROMs (a few mapper types and PAL/NTSC
titles), and list anything that misbehaves.
