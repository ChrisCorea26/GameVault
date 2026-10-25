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
| 2 | PPU, NROM rendering, keyboard input | Next |
| 3–7 | APU, mappers, library, signature features, netplay | Planned |

Test-ROM results are tracked in [`docs/compatibility.md`](docs/compatibility.md).

Phase plans are in [`docs/plans/`](docs/plans).

## Requirements

- Windows 10 1809 or later. Development needs Windows 10/11 with the **.NET 10 SDK**.
- Visual Studio 2022/2026 with the *Windows application development* workload is optional. You need it for XAML Hot Reload and the Live Visual Tree.

## Build, test and run

```powershell
dotnet build GameVault.sln -c Debug
dotnet test GameVault.sln
dotnet run --project src/GameVault.App                      # real (empty) library
dotnet run --project src/GameVault.App -- --sample-data     # fake library for UI previews
```

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
