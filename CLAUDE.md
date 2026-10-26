# CLAUDE.md — GameVault

The full feature spec is in `SPEC.md`. Read it before starting any task, and follow §13, "Instructions for Claude Code".

## Project in one paragraph
GameVault is a Windows multi-system emulator (NES first, then Sega 8-bit, Apple II, Genesis, SNES, PlayStation and N64; SPEC §7.11) in C# / .NET 10 with a **WinUI 3** UI (Windows App SDK, unpackaged, MVVM). It has five main parts:
- a deterministic, cycle-stepped emulation core (`src/GameVault.Core`)
- a platform layer for audio, video and input (`src/GameVault.Platform`)
- a metadata-only game catalog and collection tracker (`src/GameVault.Library`)
- UI-free ViewModels (`src/GameVault.ViewModels`)
- the WinUI 3 app (`src/GameVault.App`)

Users supply their own ROMs. The app never ships, downloads or links to commercial games.

## Commands
- Build: `dotnet build GameVault.sln -c Debug`
- Test: `dotnet test GameVault.sln`
- Core tests only: `dotnet test tests/GameVault.Core.Tests`
- Fetch downloaded test ROMs (nestest, blargg and the other suites in `tests/TestRoms/manifest.tsv`) before testing: `tests/TestRoms/fetch-test-roms.sh` (or `.ps1` on Windows)
- Linux/macOS (no WinUI): `dotnet test GameVault.CrossPlatform.slnf`
- Run app: `dotnet run --project src/GameVault.App`
- Run with fake data (UI preview, no ROMs needed): `dotnet run --project src/GameVault.App -- --sample-data`

## Hard rules
- **No commercial ROMs, BIOS files, box art, or manuals in the repo — ever.** Test ROMs must be freely redistributable and listed in `tests/TestRoms/SOURCES.md`.
- `GameVault.Core` has no references to UI, file I/O, threading, timers, or platform libraries. It must stay deterministic.
- Every component in the core implements `IStateful`. Add a save/load round-trip test for every new mapper.
- WinUI 3 rules (SPEC §9.1):
  - Layout in XAML, logic in ViewModels (CommunityToolkit.Mvvm), compiled `x:Bind`.
  - ViewModels never reference WinUI types.
  - The app is **unpackaged**: no package-identity APIs (`ApplicationData`, etc.). Settings go to `%LOCALAPPDATA%\GameVault\`.
  - Never start the emulator or open devices in constructors; use `Loaded`.
  - The emulator thread never touches XAML objects. Marshal through `DispatcherQueue`; frames go straight to the swap chain.
  - Every new screen must render meaningful content under `--sample-data`.
- Work phase by phase (SPEC §12). Write a plan to `docs/plans/phase-N.md` first. Keep tests green before moving on.
- **Keep the user manual current.** Every phase that adds a system (and any change to user-visible features,
  settings, keys or required files) updates the chapters in `docs/manual/` and rebuilds
  `docs/manual/GameVault-User-Manual.pdf` in the same change. Steps: `docs/manual/README.md`.
- **Known gaps** between the app and the spec are tracked in `docs/bugs.md`. File new ones there, and mark entries
  Fixed (with the commit) when you fix them.

## Conventions
- C#: nullable enabled, file-scoped namespaces, `TreatWarningsAsErrors` in Core.
- Hot paths in the core (CPU step, PPU dot, APU tick):
  - No allocations, no LINQ, no virtual calls inside the per-cycle loop where avoidable.
  - Use `[MethodImpl(MethodImplOptions.AggressiveInlining)]` judiciously.
- Hardware behavior follows the NESdev Wiki. Cite the relevant page in a comment where behavior is subtle.
- Commit messages: `phase-N: <area>: <what changed>`.
