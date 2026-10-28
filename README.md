# GameVault

**The retro game emulator built for players and collectors, not tinkerers.**
Accurate emulation, zero-config setup, and a library that tracks your whole collection. It started as an NES
emulator and now also plays the Famicom Disk System, SG-1000 and Master System, Apple II, Genesis / Mega Drive,
Super Nintendo, PlayStation, Nintendo 64 and PlayStation 2. You supply the games yourself.

> GameVault never ships, downloads or links to commercial games. It plays files you provide, such as dumps
> of cartridges you own, or freely licensed homebrew.

GameVault is a Windows desktop app written in C# / .NET 10 with a WinUI 3 interface. See [SPEC.md](SPEC.md) for the
full feature specification and [CLAUDE.md](CLAUDE.md) for the contributor rules.

**Players:** the [User Manual and Setup Guide](docs/manual/GameVault-User-Manual.pdf) covers installation, every
system, the BIOS and ROM files you must supply, controls, settings and troubleshooting.

## Status

| Phase | Deliverable | State |
|---|---|---|
| 0 | Scaffold: solution, CI, WinUI 3 shell, `--sample-data`, test harness | ✅ |
| 1 | 6502 CPU, bus, RAM | ✅ nestest and instr_test-v5 pass |
| 2 | PPU, NROM rendering, Win2D game view, keyboard input | ✅ PPU suites and golden images pass |
| 3 | APU, audio, SDL controllers | ✅ blargg APU suites and the 30-minute audio soak pass |
| 4 | Tier 1 mappers, save states, run-ahead, video presets | ✅ MMC3 IRQ tests and state round-trips for every mapper pass |
| 5 | Library, timeline, rewind, Zapper | ✅ Zero-config test (empty library → add folder → playing) passes |
| 6 | Passwords, journal, assist/practice, Tier 2–3 mappers, dumper | 🟡 Built and tested; owner checks with real cartridges pending ([plan](docs/plans/phase-6.md#open-needs-the-owner)) |
| 7 | Netplay, FDS, Tier 4 mappers | ✅ Rollback netplay on the LAN or over the internet (STUN, or the GameVault relay), Disk System games included; FDS with the player's own BIOS, Tier 4 boards |
| 8 | Game Genie code library | ✅ Codes panel, imported code collections, GameGenie.com lookup on request |
| 9 | Game-aware features | ✅ Memory maps, before-death states, auto maps, recap, family profiles, verified challenges, stat overlays |
| 10 | RetroAchievements | 🟡 Built and tested offline with rcheevos; the owner's account and RetroAchievements' approval pending ([plan](docs/plans/phase-10.md#8-owner-tasks)) |
| 11 | Sega SG-1000 and Master System | ✅ Z80 passes zexdoc/zexall; VDP, PSG, mappers; library system filter. Real-game checks are the owner's ([plan](docs/plans/phase-11.md)) |
| 12 | Apple II, II+ and IIe | ✅ 6502/65C02 pass Dormann's tests; video, Disk II (DSK/PO/NIB/WOZ) with a two-drive disk menu, keyboard. Needs your own Apple ROMs ([plan](docs/plans/phase-12.md)) |
| 13 | Sega Genesis / Mega Drive | ✅ 68000 passes the SingleStepTests suite; VDP, YM2612, PSG, Z80, 3/6-button pads, SRAM, EEPROM saves on Sega, EA, Acclaim and Codemasters boards. Real-game checks are the owner's ([plan](docs/plans/phase-13.md), [gaps](docs/plans/genesis-gaps.md)) |
| 14 | Super Nintendo | ✅ 65C816 and SPC700 pass their SingleStepTests suites; PPU modes 0–7, DMA/HDMA, S-DSP with GameVault's own boot program; the Super FX, SA-1, S-DD1 and OBC1, and the DSP-1 to DSP-4, ST010 and ST011 with your own chip ROM. The S-RTC and SPC7110 too, and the Cx4 and ST018 with your own chip ROMs; real-game checks are the owner's ([plan](docs/plans/phase-14.md)) |
| 15 | Sony PlayStation | ✅ R3000A passes PeterLemon's CPU tests; the GPU matches hardware VRAM dumps pixel for pixel; GTE, SPU, MDEC, CD-ROM (XA, CD-DA), pads and memory cards; .cue/.bin, .chd and .m3u. Needs your own BIOS; real-game checks are the owner's ([plan](docs/plans/phase-15.md)) |
| 16 | Nintendo 64 | ✅ VR4300, FPU and RSP pass PeterLemon's test programs; a software RDP draws PeterLemon's 115 RDP programs; audio, controllers with the analog stick, Controller Pak, EEPROM, SRAM and FlashRAM; .z64, .v64 and .n64. The RDP is drawn in software on several cores (a GPU renderer is left for later); real-game checks are the owner's ([plan](docs/plans/phase-16.md)) |
| 17 | Sony PlayStation 2 | ✅ 54 of ps2autotests' 55 EE programs match the hardware's output; EE, VU0/VU1, GS (software), IOP, SPU2, IPU, DualShock 2 and 8 MB memory cards; .iso, .chd, .cue/.bin and .elf. Discs need your own BIOS (ELF homebrew runs without one); the EE and both VUs run recompiled and the software GS draws on several cores, but many games still run below full speed (a GPU renderer is next, [plan](docs/plans/phase-17-performance.md)); real-game checks are the owner's ([plan](docs/plans/phase-17.md)) |
| — | Sega CD / Mega-CD | 🟡 Sub CPU, gate array, Word RAM (with 1M mode's cell image), CDD, CDC, PCM, the graphics ASIC and CD+G subcode under hand-written test programs; .cue/.bin (with a .sub for CD+G) and .chd; mode 1 for cartridges such as MSU-MD games. Needs your own BIOS; booting real discs is the owner's to check ([plan](docs/plans/sega-cd-32x.md)) |
| — | 32X | 🟡 Both SH-2s (the SH7604 with its cache, DMA, division unit and timers), the 32X VDP laid over the Genesis picture line by line, PWM sound and the FIFO, under hand-written test programs; .32x files. Needs your own BIOS files; booting real games is the owner's to check ([plan](docs/plans/sega-cd-32x.md)) |
| 18 | GameCube (then the Wii) | 🟡 Milestone 1: the Gekko (integer, FPU, paired singles, exceptions, BATs) interpreted, with VI's frame buffer, SI controllers, EXI's SRAM and the DSP's boot handshake; `.dol` and `.elf` homebrew that draws to its frame buffer. The GPU, discs, the DSP's sound and the recompiler are the next milestones ([plan](docs/plans/phase-18.md)) |
| 19 | Atari 2600 and 5200 | 🟡 2600: TIA drawn colour clock by colour clock, RIOT, sound, and the common bank-switching boards. 5200: ANTIC's display lists and DMA, GTIA's players and priorities, POKEY's sound, pots and keypad, with the player's own BIOS. Both under hand-written test programs; real-game checks are the owner's ([plan](docs/plans/phase-19.md)) |

Test-ROM results are tracked in [`docs/compatibility.md`](docs/compatibility.md). Known bugs and missing features are
tracked in [`docs/bugs.md`](docs/bugs.md).

Phase plans are in [`docs/plans/`](docs/plans).

## Requirements

- Windows 10 1809 or later. Development needs Windows 10/11 with the **.NET 10 SDK**.
- Visual Studio 2022/2026 with the *Windows application development* workload is optional. You need it for XAML Hot Reload and the Live Visual Tree.
- **CMake** and a C compiler build the RetroAchievements component (rcheevos) on the first build: on
  Windows, Visual Studio's *Desktop development with C++* workload; on Linux, gcc or clang. Without them
  GameVault still builds and runs, with RetroAchievements turned off. See [`native/README.md`](native/README.md).

## Build, test and run

```powershell
dotnet build GameVault.sln -c Debug
dotnet test GameVault.sln
dotnet run --project src/GameVault.App                      # real (empty) library; "Open game file" plays any supported file (see below)
dotnet run --project src/GameVault.App -- --sample-data     # fake library for UI previews
dotnet run --project src/GameVault.App -- --sample-data --page=player   # Player with a test pattern
```

Supported files (`src/GameVault.Library/Scanning/RomFiles.cs`): `.nes` `.fds` (NES), `.sms` `.sg` `.sc` (Sega 8-bit),
`.dsk` `.do` `.po` `.nib` `.woz` (Apple II), `.md` `.gen` `.smd` `.bin` (Genesis), `.sfc` `.smc` (SNES), `.cue` `.chd` `.m3u`
`.exe` (PlayStation), `.z64` `.v64` `.n64` (N64) and `.iso` `.elf` (PlayStation 2; its `.cue`/`.chd` discs are told apart from the PlayStation's by SYSTEM.CNF), plus a `.zip` holding one cartridge game.

Keyboard (Player 1): arrows, **Z** = B, **X** = A, **Enter** = Start, **Right Shift** = Select, **Esc** = pause.
See [`docs/design/input.md`](docs/design/input.md) for the full input design.

The app is **unpackaged** and builds for x64 by default. To build another architecture, add `-p:Platform=ARM64` or `-p:Platform=x86`.

### On Linux or macOS

The WinUI 3 app only builds on Windows. The emulation core, library, ViewModels and platform services
are cross-platform, and you can build and test them anywhere:

```bash
dotnet test GameVault.CrossPlatform.slnf
```

## Repository layout

```
src/
  GameVault.Core/        Emulation core: deterministic, no UI or I/O (net10.0)
  GameVault.Library/     Catalog, collection and playtime models (net10.0)
  GameVault.ViewModels/  MVVM ViewModels, service interfaces, sample data (net10.0)
  GameVault.Platform/    Windows services: storage, video, audio, input (net10.0-windows)
  GameVault.App/         WinUI 3 app (net10.0-windows10.0.19041.0)
tests/                   xUnit test projects and TestRoms/ (redistributable test ROMs only)
data/                    Game profiles and palettes
docs/                    Plans, compatibility dashboard, UI wireframe links
```

## Test ROMs

Accuracy suites such as nestest and blargg's tests are **downloaded at test time**, not committed, because
their licenses don't explicitly allow redistribution. Fetch them once before running the tests:

```bash
./tests/TestRoms/fetch-test-roms.sh       # Linux/macOS
./tests/TestRoms/fetch-test-roms.ps1      # Windows
```

Without them, ROM-based tests are skipped. CI sets `GAMEVAULT_REQUIRE_TEST_ROMS=1`, which turns a missing
ROM into a failure. Sources, authors and hashes are listed in
[`tests/TestRoms/SOURCES.md`](tests/TestRoms/SOURCES.md).

To run tests against your own dumps, set `GAMEVAULT_LOCAL_ROMS` to a local folder. Those tests are skipped
otherwise.

## Credits

- **[rcheevos](https://github.com/RetroAchievements/rcheevos)** (MIT), RetroAchievements' runtime,
  is included in `native/rcheevos/` and runs achievements, leaderboards and rich presence. See
  [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

- **Game Genie codes from GameGenie.com.** In the Codes panel, "Get codes from GameGenie.com" shows codes
  that come from [GameGenie.com](https://gamegenie.com/cheats/gamegenie/nes/index.html) (© Danworld, Inc.).
  GameVault downloads them from the site when you ask and always shows them with this credit and a link to
  the game's page. They aren't stored in this repository. See
  [`data/gamedb/cheats/SOURCES.md`](data/gamedb/cheats/SOURCES.md).
- **Code collections** that you import in Settings → Codes stay on your PC and are shown with their own
  source.
