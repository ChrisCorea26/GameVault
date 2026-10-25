# Phase 0 — Scaffold

**Goal (SPEC §12):** solution, projects, CI, unpackaged WinUI 3 shell (NavigationView, Mica, DI,
`--sample-data` mode), test harness.

**Done when:** `dotnet build` and `dotnet test` pass in GitHub Actions; the app launches from `dotnet run`.

## Work items

1. **Repo plumbing**
   - `global.json` (SDK 10.0.1xx, roll forward to latest feature band).
   - `Directory.Build.props`: nullable, implicit usings, latest C#, deterministic builds.
   - `Directory.Packages.props`: central package management, one version per package.
   - `.gitignore`, `.editorconfig` (file-scoped namespaces).
2. **Solution `GameVault.sln`** with these projects (SPEC §3):
   | Project | TFM | Phase 0 content |
   |---|---|---|
   | `GameVault.Core` | `net10.0` | `State/` — `IStateful`, `StateWriter`, `StateReader` (versioned binary format). `TreatWarningsAsErrors`. No other references. |
   | `GameVault.Library` | `net10.0` | Game/collection models (`GameEntry`, `Ownership`, `PlayStatus`) and `CollectionSummary`. |
   | `GameVault.ViewModels` | `net10.0` | `ShellViewModel`, `LibraryViewModel`, `GameTileViewModel`, `SettingsViewModel`; service interfaces; sample-data services. |
   | `GameVault.Platform` | `net10.0-windows` | `AppPaths` (`%LOCALAPPDATA%\GameVault\`), `JsonSettingsStore`, `IVideoSink` contract. |
   | `GameVault.App` | `net10.0-windows10.0.19041.0` | Unpackaged, self-contained WinUI 3 shell. |
   | `GameVault.Core.Tests` | `net10.0` | State round-trip tests + test-ROM harness scaffolding. |
   | `GameVault.Library.Tests` | `net10.0` | Collection summary tests. |
   | `GameVault.ViewModels.Tests` | `net10.0` | ViewModel tests against fake services. |

   `GameVault.Dumper` is deferred to Phase 6, when it has real content.
3. **WinUI 3 shell (SPEC §9.1)**
   - `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, min OS 10.0.17763.
   - `MainWindow`: `MicaBackdrop`, custom title bar (`ExtendsContentIntoTitleBar` + `SetTitleBar`),
     `NavigationView` (Library + built-in Settings item) hosting a `Frame`.
   - Pages `LibraryPage` and `SettingsPage`, layout in XAML, compiled `x:Bind` to ViewModels resolved from DI.
   - `App` parses `--sample-data` and registers either sample services or real (empty for now) services.
   - `Properties/launchSettings.json` with *GameVault* and *GameVault (sample data)* profiles.
   - Nothing is opened or started in constructors. Data loads in each page's `Loaded`.
4. **Test harness**
   - xUnit + FluentAssertions in every test project.
   - `tests/TestRoms/SOURCES.md` (empty registry, licensing rules).
   - `TestRomLocator` (resolves `tests/TestRoms`) and `[LocalRomFact]`, which skips unless
     `GAMEVAULT_LOCAL_ROMS` points to an existing folder (SPEC §11.3).
   - `StateAssert.RoundTrips(...)` helper for the per-component and per-mapper round-trip tests that come later.
5. **CI** (`.github/workflows/ci.yml`)
   - `windows-latest`: `dotnet build GameVault.sln -c Debug` and `dotnet test GameVault.sln`.
   - `ubuntu-latest`: builds and tests the cross-platform projects through `GameVault.CrossPlatform.slnf`,
     which proves the core stays platform-independent.
6. **Docs**: `README.md`, `docs/compatibility.md` (empty dashboard), `docs/ui/README.md` (wireframe links).

## Decisions and deviations

- **Service interfaces live in `GameVault.ViewModels/Services`.** SPEC §3 says ViewModels uses
  "Platform interfaces only", but ViewModels is `net10.0` and can't reference the Windows-only Platform
  project. Instead, ViewModels defines the interfaces it needs (for example `ISettingsStore`), and Platform
  (`net10.0-windows`, which may reference `net10.0`) implements them. App wires them up through DI.
- **FluentAssertions is pinned to 7.x.** Version 8+ uses a commercial (Xceed) license; 7.x is Apache-2.0.
- **The App's default RuntimeIdentifier is `win-x64`.** This lets `dotnet build GameVault.sln`
  (AnyCPU) and `dotnet run` work without extra flags. Override with `-r win-arm64` and so on.
- WinUI 3 projects only build on Windows. On Linux and macOS, use `GameVault.CrossPlatform.slnf`.

## Out of scope

Emulation (Phase 1+), Win2D/`GameView` (Phase 2), audio and SDL input (Phase 3), and the SQLite
catalog (Phase 5).
