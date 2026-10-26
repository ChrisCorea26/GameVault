# GameVault

**The NES emulator built for players and collectors, not tinkerers.**
Accurate emulation, zero-config setup, and a library that tracks the whole NES catalog. You supply the games yourself.

> GameVault never ships, downloads or links to commercial games. It plays files you provide, such as dumps
> of cartridges you own, or freely licensed homebrew.

GameVault is a Windows desktop app written in C# / .NET 10 with a WinUI 3 interface. See [SPEC.md](SPEC.md) for the
full feature specification and [CLAUDE.md](CLAUDE.md) for the contributor rules.

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
| 7 | Netplay, FDS, Tier 4 mappers | ✅ LAN rollback netplay, FDS with the player's own BIOS, Tier 4 boards |
| 8 | Game Genie code library | ✅ Codes panel, imported code collections, GameGenie.com lookup on request |
| 9 | Game-aware features | ✅ Memory maps, before-death states, auto maps, recap, family profiles, verified challenges, stat overlays |
| 10 | RetroAchievements | 🟡 Built and tested offline with rcheevos; the owner's account and RetroAchievements' approval pending ([plan](docs/plans/phase-10.md#8-owner-tasks)) |
| 11 | Sega SG-1000 and Master System | ✅ Z80 passes zexdoc/zexall; VDP, PSG, mappers; library system filter. Real-game checks are the owner's ([plan](docs/plans/phase-11.md)) |
| 12 | Apple II, II+ and IIe | ✅ 6502/65C02 pass Dormann's tests; video, Disk II (DSK/PO/NIB/WOZ) with a two-drive disk menu, keyboard. Needs your own Apple ROMs ([plan](docs/plans/phase-12.md)) |
| 13 | Sega Genesis / Mega Drive | ✅ 68000 passes the SingleStepTests suite; VDP, YM2612, PSG, Z80, 3/6-button pads, SRAM. Real-game checks are the owner's ([plan](docs/plans/phase-13.md)) |
| 13–19 | More systems | Planned (SPEC §7.11) |

Test-ROM results are tracked in [`docs/compatibility.md`](docs/compatibility.md).

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
dotnet run --project src/GameVault.App                      # real (empty) library; "Open game file" plays .nes, .fds, .sms, .sg, .sc, .dsk, .po, .woz
dotnet run --project src/GameVault.App -- --sample-data     # fake library for UI previews
dotnet run --project src/GameVault.App -- --sample-data --page=player   # Player with a test pattern
```

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
