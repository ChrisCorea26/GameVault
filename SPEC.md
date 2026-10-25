# GameVault — NES Emulator for Windows
### Feature Specification v1.0

> **One-line pitch:** The NES emulator built for players and collectors, not tinkerers.
> Accurate emulation, zero-config setup, and a library that tracks the whole NES catalog — with games supplied only by the user.

---

## 1. Goals and Non-Goals

### 1.1 Goals
1. Accurately emulate the NTSC NES (and later PAL/Dendy) so that the large majority of the licensed library runs correctly.
2. Deliver a Windows desktop app with **zero-config** defaults: plug in a controller, open a game, play.
3. Provide a **complete catalog** of known NES releases (metadata only) with collection tracking.
4. Offer features no single existing emulator combines: cartridge-dumper import, collection tracking, password capture, a per-game journal, a visual save-state timeline, and assist/practice modes.
5. Keep the emulation core **deterministic, UI-independent, and fully unit-tested**.

### 1.2 Non-Goals (hard rules)
- **Never bundle, download, link to, or help acquire commercial ROMs.** The app plays only files the user supplies (e.g., dumped from their own cartridges) or freely licensed homebrew.
- No built-in ROM search, scraping of ROM sites, or "download missing games" feature.
- No commercial game files, BIOS files, or copyrighted box art/manuals committed to this repository. Test fixtures must be freely redistributable (see §11.3).
- Not a multi-system emulator. NES/Famicom only (Famicom Disk System is a stretch goal).

---

## 2. Technology Stack

| Area | Choice | Notes |
|---|---|---|
| Language / runtime | C# 13, **.NET 10** (LTS) | `net10.0-windows` for UI projects, `net10.0` for the core |
| UI | **WinUI 3** (latest stable Windows App SDK) | Fluent design, Mica backdrop, follows system light/dark theme. Rules in §9.1 |
| UI pattern | **MVVM** via CommunityToolkit.Mvvm | ViewModels contain no WinUI types and are unit-testable |
| Packaging | **Unpackaged, self-contained** (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`) | Free file-system access to ROM folders; xcopy/zip or installer distribution. MSIX optional later |
| Min OS | Windows 10 1809 (10.0.17763) and later | TFM `net10.0-windows10.0.19041.0` |
| Rendering | Phase 1: **Win2D** `CanvasSwapChainPanel` drawing the 256×240 framebuffer with nearest-neighbor scaling. Phase 4: Direct3D 11 via **Vortice.Windows** on a `SwapChainPanel` for scaling and CRT shaders | Rendering sits behind an `IVideoSink` interface so the backend can be swapped |
| Settings storage | JSON in `%LOCALAPPDATA%\GameVault\` | `ApplicationData` isn't available to unpackaged apps |
| Audio | **NAudio** (WASAPI shared mode) | Audio is the timing master (see §5.6) |
| Gamepad input | **SDL2 via Silk.NET.SDL** (GameController API + SDL controller DB) | Covers Xbox, PlayStation, Switch Pro, and 8BitDo pads. Keyboard via WinUI `KeyDown`/`KeyUp` on the game view. SDL input is also translated into UI navigation (§9.3) |
| Library storage | **SQLite** via Microsoft.Data.Sqlite | Catalog, collection, playtime, journal |
| Hashing | System.IO.Hashing (CRC32) and SHA-1 | Hash the ROM **without** the 16-byte iNES header |
| Compression | System.IO.Compression (Brotli / Deflate) | Save states and rewind buffer |
| Testing | xUnit and FluentAssertions | Core tests run headless in CI |
| CI | GitHub Actions, `windows-latest` | Build, test, and publish artifacts |

---

## 3. Solution Structure

```
GameVault.sln
├─ src/
│  ├─ GameVault.Core/            # Emulation core: no UI, no I/O beyond byte arrays. net10.0
│  │  ├─ Cpu/                    # Cpu6502, opcode tables, addressing modes
│  │  ├─ Ppu/                    # Ppu2C02, palette, sprite evaluation
│  │  ├─ Apu/                    # Pulse, Triangle, Noise, DMC, frame counter, mixer
│  │  ├─ Cartridge/              # RomLoader (iNES/NES 2.0), IMapper, Mappers/*
│  │  ├─ Input/                  # StandardController, Zapper, (later) FourScore
│  │  ├─ State/                  # IStateful, StateWriter/Reader
│  │  └─ NesConsole.cs           # Owns components; RunFrame(), Reset(), Power()
│  ├─ GameVault.Platform/        # Audio (NAudio), video sinks, SDL input, timing. net10.0-windows
│  ├─ GameVault.Library/         # Catalog DB, scanner, hashing, collection, journal, passwords
│  ├─ GameVault.Dumper/          # Cartridge dumper integration (Phase 6)
│  ├─ GameVault.ViewModels/      # MVVM ViewModels + services interfaces. No WinUI references. net10.0
│  └─ GameVault.App/             # WinUI 3 app: Views (XAML), controls, DI setup. net10.0-windows10.0.19041.0
├─ tests/
│  ├─ GameVault.Core.Tests/      # Unit tests + test-ROM harness
│  ├─ GameVault.Library.Tests/
│  └─ TestRoms/                  # Freely redistributable test ROMs ONLY (see §11.3)
├─ data/
│  ├─ gamedb/                    # Per-game profiles (JSON): fixes, input type, password defs
│  └─ palettes/                  # Palette files (original/permissively licensed only)
├─ docs/
└─ SPEC.md / CLAUDE.md / README.md
```

**Dependency rule:**
- `Core` references nothing else in the solution.
- `Library` may reference `Core` for hashing helpers only.
- `ViewModels` references `Core` and `Library`, plus `Platform` interfaces only.
- `App` references everything.
- Tests add `tests/GameVault.ViewModels.Tests/`.

---

## 4. Emulation Core

### 4.1 Timing model (NTSC)
- Master clock: 21.477272 MHz. CPU = master ÷ 12 (≈1.789773 MHz). PPU = master ÷ 4.
- **3 PPU dots per CPU cycle.** PAL is 3.2, so build the scheduler to support fractional ratios from the start.
- Frame: 262 scanlines × 341 dots. On odd frames with rendering enabled, the pre-render line skips dot 340.
- NTSC frame rate is 60.0988 Hz.
- The core is **cycle-stepped**: the CPU advances one cycle at a time, and the PPU/APU catch up per CPU cycle. Accuracy comes before raw speed, but the core must still hit the performance targets in §10.

### 4.2 CPU (Ricoh 2A03 / 6502 without decimal mode)
- All 151 official opcodes, plus the commonly used **unofficial opcodes** (LAX, SAX, DCP, ISC, SLO, RLA, SRE, RRA, the NOP variants, ANC, ALR, ARR, AXS). Unstable opcodes can be approximated.
- Correct cycle counts, including page-cross and branch penalties and dummy reads/writes.
- NMI, IRQ, and BRK with correct interrupt hijacking and polling timing.
- OAM DMA ($4014): 513 or 514 cycles depending on alignment. DMC DMA stalls the CPU.
- **Acceptance:** byte-for-byte match with the `nestest.log` reference trace. All blargg `instr_test-v5` and `instr_timing` tests pass.

### 4.3 PPU (2C02)
- Registers $2000–$2007 with correct open-bus behavior, the write toggle, and buffered $2007 reads.
- Scrolling via the loopy `v/t/x/w` model.
- Background fetch pipeline, 8-sprites-per-line evaluation, sprite overflow flag (including the hardware bug), and sprite 0 hit.
- Palette RAM mirroring and grayscale/emphasis bits.
- The `A12` rising-edge signal is exposed to mappers, which MMC3 IRQ counting needs.
- An optional **"remove sprite limit"** mode for flicker reduction. Off by default; on via per-game profile or user toggle.
- **Acceptance:** blargg `ppu_vbl_nmi`, `sprite_hit_tests`, `sprite_overflow_tests`, and `ppu_read_buffer` pass. Golden-image tests (§11.2) match.

### 4.4 APU (2A03)
- Two pulse channels, triangle, noise, and DMC. Frame counter in 4- and 5-step modes with IRQ.
- Non-linear mixer using the standard lookup-table approximation.
- Output: generate samples at the CPU rate, low-pass/high-pass filter, then resample to 48 kHz with a band-limited resampler.
- Expansion audio interface for mappers (VRC6, VRC7, Sunsoft 5B, Namco 163, MMC5, FDS).
- **Acceptance:** blargg `apu_test` and `dmc_tests` pass.

### 4.5 Cartridge and Mappers
- Formats: **iNES** and **NES 2.0** headers. NES 2.0 fields (submapper, PRG-RAM/CHR-RAM sizes, timing) are honored.
- When a header is wrong or missing, a **header override** looks up corrected values in the game database by PRG+CHR hash.
- `IMapper` interface: `CpuRead/CpuWrite`, `PpuRead/PpuWrite`, `NametableMirroring`, `OnA12Rise`, `OnCpuCycle`, `IrqPending`, `ExpansionAudio`, `IStateful`.
- Battery-backed PRG-RAM is saved as `<game>.sav` next to the library data, never inside the ROM folder unless the user opts in.

**Mapper priority (implement in this order):**

| Tier | Mappers | Notes |
|---|---|---|
| 1 | 0 NROM, 1 MMC1 (incl. SxROM variants), 2 UxROM, 3 CNROM, 4 MMC3/MMC6, 7 AxROM | Covers the large majority of the licensed library |
| 2 | 9 MMC2, 10 MMC4, 11 Color Dreams, 66 GxROM, 69 FME-7/Sunsoft 5B, 71 Camerica, 34, 206 | Common second tier |
| 3 | 5 MMC5, 24/26 VRC6, 85 VRC7, 19 Namco 163, 21–25 VRC2/4, 118, 119, 79, 87, 140 | Includes expansion audio |
| 4 | Remaining NES 2.0 mappers as reported by the compatibility dashboard; Famicom Disk System (user-supplied BIOS) | Stretch |

### 4.6 Determinism and State
- The core must be **fully deterministic**: identical inputs from power-on produce identical output. No `DateTime`, no random numbers without a seed, and no dependence on threading.
- Every component implements `IStateful` (`SaveState(StateWriter)` / `LoadState(StateReader)`) with a versioned, binary state format.
- Save and load must finish in **under 1 ms** for typical mappers, because run-ahead and rewind depend on it.

### 4.7 Input devices
- Standard controller (×2), with turbo A/B handled in the platform layer, not the core.
- Zapper light gun via mouse, with the hit test against the current framebuffer.
- Phase 5+: Four Score, Power Pad (keyboard mapping), Arkanoid paddle (mouse).

---

## 5. Platform Layer

### 5.1 Threading
- **Emulation thread:** runs `NesConsole.RunFrame()` in a loop.
- **Render path:** the emulation thread hands finished frames through a lock-free triple buffer to the swap-chain renderer, which presents them independently of the XAML UI thread.
- **UI thread:** WinUI 3. ViewModel updates are marshaled through `DispatcherQueue`, and the UI thread never blocks on the emulator.
- **Audio thread:** NAudio callback reading from a ring buffer.
- The UI never calls into the core directly. All commands (pause, load state, reset, input) go through a thread-safe command queue that is processed between frames.

### 5.2 Video
- `IVideoSink` receives a 256×240 indexed framebuffer plus the palette.
- Phase 1 uses a Win2D `CanvasSwapChainPanel` sink with nearest-neighbor integer scaling.
- Fullscreen uses `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)`.
- Phase 4 uses a D3D11 `SwapChainPanel` sink (Vortice) with these display presets, shown with plain names:
  - **Sharp Pixels:** integer scale, 8:7 pixel aspect optional.
  - **Smooth:** bilinear scaling.
  - **Living Room CRT:** scanlines, mask, and slight bloom.
  - **Arcade Monitor:** stronger mask and curvature.
- The default crop hides overscan (top and bottom 8 lines). Configurable.

### 5.3 Audio
- Output is 48 kHz stereo (the NES signal is mono, duplicated to both channels) through WASAPI shared mode, with a target latency of 40 ms or less.
- **Dynamic rate control:** adjust the resample ratio by up to ±0.5% based on ring-buffer fill, so audio never crackles or drifts.

### 5.4 Input
- On startup and hot-plug, SDL detects controllers and applies a default mapping with **no setup screen required**.
- Keyboard defaults: arrows, Z = B, X = A, Enter = Start, Right Shift = Select.
- Remapping is per-player and can be per-game. Includes turbo buttons and one-handed layout presets.

### 5.5 Run-ahead (latency reduction)
- **On by default with 1 frame** for games marked safe in the game database. The user can set 0–3 frames.
- Implementation: save state, run N frames with audio/video discarded, show frame N, then load state and advance one frame for real.
- Run-ahead is automatically disabled if the save/load benchmark fails the frame budget.

### 5.6 Timing
- Audio is the master clock, with vsync pacing as a fallback when audio is muted.
- Fast-forward (hold Tab): uncapped up to 8×, with audio muted or pitch-preserved.
- Slow motion at 50% and 25% (also part of Assist Mode).

---

## 6. Library and Catalog

### 6.1 Catalog (metadata only, no ROMs)
- The catalog DB contains: title, region, publisher, developer, release year, mapper/board, PRG/CHR sizes, CRC32/SHA-1 (headerless), player count, accessories (Zapper and so on), and genre.
- **Source:** the user imports DAT/XML metadata files (for example, No-Intro DATs) through *Library → Import Catalog*.
  - The repository ships only an **importer and an empty schema**. Third-party DATs are committed only if their license permits redistribution; otherwise the user downloads them.
- The catalog is browsable as the **complete NES checklist**, with games the user owns highlighted.

### 6.2 Library scanner
- The user adds one or more folders. The scanner recursively finds `.nes` files (and `.zip` files with a single `.nes` inside).
- For each file: strip the header, hash it, match it against the catalog, and record the path.
  - Unmatched files appear as "Unknown / Homebrew", where the user can edit metadata.
- A file watcher picks up additions and removals while the app is running.

### 6.3 Collection tracking
Per-game fields:
- Owned: cartridge, box, manual, and digital dump, each a separate toggle.
- Status: unplayed, playing, beaten, or 100%.
- Favorite, rating (1–5), and personal notes.
- **Playtime:** tracked automatically per game, with last-played date.

The library view also includes:
- Filters by region, genre, year, publisher, owned/unowned, and status.
- A **collection completion** summary, such as "214 / 677 licensed NA releases owned".

### 6.4 Bundled homebrew
- Ships with a small set of freely licensed homebrew games whose authors permit redistribution. Each includes its license file under `data/homebrew/<game>/LICENSE`.
- These give the app playable content on first launch.

### 6.5 Artwork
- The user can attach their own cover images per game, for example photos of their cartridges.
- No copyrighted box art is shipped or scraped.

---

## 7. Signature Features

### 7.1 Visual save-state timeline
- Unlimited save states per game. Each stores a thumbnail, timestamp, playtime, and optional label.
- The **timeline strip** shows states as thumbnails in chronological order. Click to load, right-click to rename, pin, or delete.
- Quick save/load (F5 and F8) uses the most recent slot, and automatic **auto-save on exit** offers "resume where you left off" on next launch.

### 7.2 Rewind
- Hold the Backspace key or a controller shoulder button to rewind, with a scrub bar shown while rewinding.
- The ring buffer captures a compressed state every N frames (default 2) and keeps at least 60 seconds within a 256 MB memory cap (configurable).

### 7.3 Password capture
For games that save progress as passwords, two methods are offered:
- **Automatic.** The game DB's per-game *password definition* (JSON) identifies the password screen (RAM-flag condition) and how to decode the password from RAM or nametable tiles. Captured passwords are stored with a thumbnail and timestamp.
- **Manual fallback.** A "Capture Password" hotkey saves a screenshot and lets the user type the password.

The **Passwords panel** lists saved passwords per game with a copy button. Ship definitions for 5–10 well-known password games first; the definition format is documented in `docs/password-definitions.md` so the community can add more.

### 7.4 Game journal
- Per-game notes (rich text), screenshots (F12), and hand-made maps (images the user imports).
- A screenshot can be attached to a note with one click.

### 7.5 Per-game smart profiles (`data/gamedb/*.json`, keyed by hash)
- Header corrections.
- Input type (controller, Zapper, Four Score), so Zapper games automatically set up mouse aiming.
- Run-ahead safety, recommended sprite-limit setting, and region/timing.
- Password definition reference.
- Profiles apply automatically. The user can see and override any setting in *Game Properties*.

### 7.6 Assist and Practice modes
- **Assist Mode** (toggle per session; never forced): slow motion, unlimited rewind, turbo buttons, flicker removal, and colorblind-friendly palettes.
- **Practice Mode:** pin a "practice point" state, then retry with one button (instant reload). Includes a built-in segment timer with splits and personal bests per segment.

### 7.7 Netplay (Phase 7, stretch)
- Two-player online play with **rollback netcode**, built on the deterministic core and fast save states.
- Connection by **invite code** through a lightweight relay/STUN service, with no manual port forwarding.
- Both players must have a ROM with the same hash. The app verifies hashes and never transfers ROM data.

---

## 8. Cartridge Dumper Integration (Phase 6)
- Support **INL Retro-Prog** first (open-source USB dumper). Initial integration shells out to its CLI tool; a native USB implementation is a later option.
- Flow: *Library → Dump Cartridge* → pick the board/mapper (pre-filled from a catalog search) → dump → verify the hash against the catalog → add to library and mark as "Owned: Cartridge + Digital dump."
- Clear error messages for bad reads ("Clean the cartridge contacts and try again"), with up to 3 automatic retries comparing hashes.
- The dumper is optional. The app works fully without one.

---

## 9. User Interface

### 9.1 WinUI 3 architecture rules (mandatory)
- **Layout lives in XAML.** Code-behind is limited to view-only concerns (focus, animations, swap-chain hookup). Do not build layouts in code. Repeating items (library tiles, timeline thumbnails) use `ItemsRepeater` or `GridView` with `DataTemplate`s.
- **MVVM:** each page binds to a ViewModel from `GameVault.ViewModels`. Use `x:Bind` (compiled bindings) with `[ObservableProperty]` and `[RelayCommand]` from CommunityToolkit.Mvvm. Wire services through `Microsoft.Extensions.DependencyInjection`.
- **No visual designer exists for WinUI 3.** Preview UI with **XAML Hot Reload** and the **Live Visual Tree**.
  - The app supports a `--sample-data` launch argument that swaps in fake services with a realistic library, states and passwords, so every screen can be previewed without ROMs.
  - Keep a launch profile for it in `launchSettings.json`.
- **Custom controls** (`GameView`, `TimelineStrip`, `GameTile`) are templated controls with styles in `Themes/Generic.xaml` and `DependencyProperty` APIs. Never start the emulator, open files, or touch SDL/NAudio in a constructor; initialize in `Loaded`.
- **Shell:** `NavigationView` with Library, Settings and wizards as pages. Mica backdrop, custom title bar, and theme following the system light/dark setting.
- **DPI:** WinUI scales automatically. Verify at 100%, 150% and 200%; the game view always uses integer scaling of the framebuffer.
- **Packaging gotchas:** the app is unpackaged, so call the Windows App SDK bootstrapper and don't use package-identity-only APIs (`ApplicationData`, `StorageFile` broker features). Use `System.IO` and Win32 file pickers initialized with the window handle.

### 9.2 Screens
1. **Library (home):** grid/list toggle with search, filters, and collection summary. Tile = cover, title, and status badges.
2. **Game Details:** metadata, ownership toggles, playtime, journal, passwords, save-state timeline, and a Play button.
3. **Player:** the game screen, with a top overlay that appears when the mouse moves (pause, states, rewind, assist, settings, fullscreen).
4. **Settings:** Video, Audio, Input (per player), Library folders, Assist, and Advanced (run-ahead, region, accuracy options).
5. **Catalog Import** and **Dump Cartridge** wizards.

Screen notes and any reference images live in `docs/ui/`. Tile layouts in the library and timeline should use Fluent controls (`GridView`, `ItemsRepeater`, `TeachingTip`, `InfoBar`) rather than custom-drawn equivalents.

### 9.3 UX principles
- **Zero-config:** a new user can launch, add a ROM folder, and be playing within 60 seconds without opening Settings.
- Plain language everywhere (no "PPU dot skip", "WASAPI exclusive", or shader file names in default views).
- Every destructive action (delete state, remove from library) supports undo or asks for confirmation.
- Full controller navigation of the Library and overlay (Big Picture–style), plus keyboard shortcuts for everything.
  - SDL gamepad input maps to XY focus navigation (`XYFocusKeyboardNavigation`) and to `AccessKey`s.
  - Every page must be fully usable with a controller alone.

---

## 10. Performance Targets
| Metric | Target |
|---|---|
| Emulation speed | ≥ 10× real time on a 4-core 2018-era CPU (single thread, rendering off) |
| Input latency | ≤ 1 frame added beyond the display, with run-ahead 1 enabled |
| Audio | No underruns in a 30-minute soak test |
| Save/load state | < 1 ms (typical mappers) |
| Library scan | 1,000 files in < 10 s (SSD) |
| Memory | < 400 MB including the default rewind buffer |

---

## 11. Testing Strategy

### 11.1 Unit tests
- CPU: per-opcode tests for flags, cycles, and addressing modes.
- Mappers: banking, mirroring, and IRQ behavior tested with synthetic PRG/CHR data, so no real game data is needed.
- State: round-trip tests (`save → load → run N frames`) must equal an uninterrupted run, for every mapper.

### 11.2 Test-ROM harness
- `TestRomRunner` loads a ROM, runs until the blargg status byte at `$6000` reports done (or a frame limit is reached), and reads the result text at `$6004`.
- `nestest` runs in automation mode from `$C000`, and its trace log is diffed against the reference.
- **Golden-image tests:** render frame N of a test ROM, hash it, and compare against a committed hash.
- A compatibility dashboard generated in CI (`docs/compatibility.md`) lists every test ROM's pass/fail status.

### 11.3 Test fixture licensing
- Only commit test ROMs whose license permits redistribution, for example community accuracy suites distributed for that purpose. Record the source and license of each in `tests/TestRoms/SOURCES.md`.
- If a suite's license is unclear, the CI script downloads it from its official source at test time instead of committing it.
- **Never commit commercial ROMs, even "for testing."** Local tests against a developer's own dumps run only when the env var `GAMEVAULT_LOCAL_ROMS` points to a local folder, and are skipped otherwise.

---

## 12. Milestones

Each phase ends with all tests green and a tagged release.

| Phase | Deliverable | Done when |
|---|---|---|
| **0. Scaffold** | Solution, projects, CI, unpackaged WinUI 3 shell (NavigationView, Mica, DI, `--sample-data` mode), test harness | `dotnet build` and `dotnet test` pass in GitHub Actions; app launches from `dotnet run` |
| **1. CPU** | Full 6502 core, bus, and RAM | `nestest.log` matches; instr_test-v5 passes |
| **2. PPU + NROM** | PPU, mapper 0, Win2D video in `GameView`, keyboard input | NROM test ROMs render correctly; golden-image tests pass |
| **3. APU + Input** | APU, NAudio output, SDL controllers, timing loop | blargg APU tests pass; 30-minute audio soak test has no underruns |
| **4. Tier 1 mappers + polish** | Mappers 1/2/3/4/7, save states, battery saves, D3D11 presets, run-ahead | MMC3 IRQ tests pass; state round-trip tests pass for all mappers |
| **5. Library** | Catalog import, scanner, collection tracking, playtime, game profiles, timeline, rewind, Zapper | Zero-config 60-second test passes with a fresh install |
| **6. Signature features** | Password capture, journal, assist/practice modes, Tier 2–3 mappers, dumper integration | Password capture works for the 5 launch titles; dumper flow verified on hardware |
| **7. Stretch** | Rollback netplay, FDS, Tier 4 mappers, expansion audio completion | — |

---

## 13. Instructions for Claude Code
1. **Work one phase at a time, in order.** Do not start a phase until the previous phase's "Done when" criteria pass.
2. Before each phase, write a short plan in `docs/plans/phase-N.md`, then implement. Keep each commit small and focused, with a descriptive message.
3. **Tests first** for the core: write or enable the failing test, then implement.
4. Never add commercial ROMs, BIOS files, or copyrighted assets to the repo. If a task seems to need one, stop and ask.
5. Keep `GameVault.Core` free of UI, file system, threading, and platform dependencies.
6. Follow the WinUI 3 rules (§9.1):
   - Layout in XAML, logic in ViewModels.
   - Compiled `x:Bind`.
   - Every new screen gets sample data so it renders under `--sample-data`.
   - Add ViewModel unit tests for new UI logic.
7. When behavior is ambiguous, prefer documented hardware behavior (for example, NESdev Wiki descriptions) and record the reference in a code comment.
8. Update `docs/compatibility.md` and this spec's checkboxes when features are completed.
