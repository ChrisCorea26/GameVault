# Phase 5 — Library

**Goal (SPEC §12):** Catalog import, scanner, collection tracking, playtime, game profiles, timeline,
rewind, Zapper.

**Done when:** the zero-config 60-second test passes on a fresh install. A new user launches, adds a ROM
folder from the empty Library, and is playing without opening Settings (SPEC §9.3).

## GameVault.Library (SQLite via Microsoft.Data.Sqlite; metadata only, never ROM bytes)

- **`LibraryDatabase`.** Creates and migrates `library.db`, with a schema version in `PRAGMA user_version`.
  - `catalog`: title, region, publisher, developer, year, genre, players, accessories, mapper, PRG/CHR
    size, headerless CRC32 and SHA-1, licensed flag, source DAT.
  - `files`: path, size, mtime, headerless CRC32 and SHA-1, matched catalog id.
  - `collection`: ownership flags, status, favorite, rating, notes, cover image path, custom title for
    unknown games.
  - `playtime`: per game, total and last played.
  - `folders`: library folders.
- **`RomHasher`.** Strips the 16-byte iNES header (and the 512-byte trainer) and computes CRC32 plus SHA-1
  (SPEC §2). Uses `INesRom` from Core only to find the header, as the dependency rule allows.
- **`CatalogImporter`.** Reads Logiqx XML DATs, the No-Intro format: `<game name>` / `<rom crc sha1 size>`.
  - Region, licensed and unlicensed status come from No-Intro name tags: `(USA)`, `(Europe)`, `(Japan)`,
    `(World)`, `(Unl)`, `(Proto)`, `(Beta)`, and so on.
  - Optional extra attributes (publisher, year, genre, players) are read when a richer DAT carries them.
  - Import is idempotent per DAT name and reports a count.
  - The repository ships the importer and an empty schema only (SPEC §6.1). The test DAT is a tiny
    hand-written file describing fictional games.
- **`LibraryScanner`.**
  - Walks folders recursively and finds `.nes` files, plus `.zip` files that contain exactly one `.nes`.
  - Hashes them on a background thread and matches each by SHA-1, falling back to CRC32.
  - Unmatched files become "Unknown / Homebrew" entries named after the file.
  - Unchanged files (same size and mtime) are skipped on rescan; removed files are dropped.
  - Target: 1,000 files in under 10 s (SPEC §10). There is a test with 1,000 small synthetic files.
- **`LibraryWatcher`**: a `FileSystemWatcher` per folder, debounced; it triggers incremental rescans.
- **`GameProfiles`**: loads `data/gamedb/*.json` (keyed by headerless SHA-1). Each profile can set:
  - header corrections (mapper, submapper, mirroring, battery, PRG-RAM),
  - input type (controller / Zapper / Four Score),
  - whether run-ahead is safe,
  - the sprite-limit recommendation,
  - region.

  The user's overrides live in the DB and win. Ships with the schema, a README, and entries for the
  freely redistributable test ROMs only.
- **`TimelineStore`** (SPEC §7.1). Stores unlimited states per game as `States/<sha1>/<id>.state` plus
  `.png`, with `index.json` holding the timestamp, playtime, label, pinned flag and kind (manual, quick
  or auto-exit).
  - Rename, pin and delete, with undo through a soft-delete that is purged on the next app start.
  - It replaces Phase 4's ten fixed slots; existing slot files are imported once.

## Core

- **Header override**: `NesConsole` accepts an `INesRom` with fields replaced
  (`rom with { Mapper = … }`).
- **`Input/Zapper`** (NESdev Wiki: "Zapper").
  - On `$4017`, D3 is light sense (0 = light seen) and D4 is the trigger.
  - Light is seen when the pixel under the aim point is bright, and the PPU drew it within the last ~20
    scanlines. Checked against the current frame as it renders.
  - Deterministic: the aim point and trigger are inputs set between frames.
- **`Input/FourScore`** (NESdev Wiki: "Four Score"): players 3–4 on `$4016`/`$4017` with the signature
  bits. Selected per game by profile or by the user.
- State round-trip tests for both.

## Platform

- **`RewindBuffer`** (SPEC §7.2).
  - Every 2 frames it stores a snapshot, XOR-delta-encoded against the previous keyframe and compressed
    with Deflate (fastest).
  - There is a full keyframe every 60 entries.
  - The default cap is 256 MB, configurable, and it keeps at least 60 s.
  - Holding Backspace or the left shoulder steps back one entry every 2 frames and shows that frame;
    release resumes from there.
  - Exposes position and length for the scrub bar. Rewinding is allowed during run-ahead; run-ahead pauses
    while rewinding.
- **`PlaytimeTracker`**: counts emulated frames while the game is running and not paused, and writes to the
  DB every minute and on stop.
- **Auto-save on exit**: on Stop, a timeline entry of kind auto-exit (only the latest one is kept). The next
  launch of that game offers "Resume where you left off".
- **Zapper input**: the mouse over GameView maps to NES coordinates through `DisplayLayout`; left button is
  the trigger. Aiming off-screen and pulling the trigger reloads, as on hardware.
- **Game profile application**: header corrections, the input type (Zapper sets up mouse aiming
  automatically), the run-ahead "safe" flag (1 frame when safe and the user hasn't chosen otherwise), and
  region.

## ViewModels + App (every screen renders under `--sample-data`)

- **Library page.**
  - Real `LibraryService` (catalog + files + collection).
  - Filters: region, genre, year, publisher, owned/unowned, status.
  - Toggle between "My games" and "Complete checklist".
  - Empty state with **Add a ROM folder**, which starts the scan, shows progress, and lands in a playable
    library (the zero-config path).
- **Game Details page.**
  - Metadata; ownership toggles (cartridge, box, manual, digital); status, favorite, rating and notes.
  - Playtime and last played; cover image ("Choose a picture…").
  - Save-state timeline (`TimelineStrip` templated control using `ItemsRepeater`).
  - Game Properties overrides (input type, run-ahead, sprite limit); Play button.
- **Catalog Import wizard**: pick a DAT, preview its counts, import, and show the result.
- **Settings → Library**: folders (add/remove), rescan, rewind memory cap.
- **Player.**
  - Timeline flyout (replaces the slot flyout).
  - Rewind scrub bar while rewinding.
  - Zapper crosshair cursor in Zapper games.
  - "Resume where you left off" InfoBar.
- Sample services provide a realistic fictional library, catalog, timeline and playtime.

## Tests
- **Library:**
  - DB migrations; hashing with and without a header or trainer.
  - DAT import (region/licensed tags, idempotent).
  - Scanner: matching, zip, unknown files, rescan skip/remove, and the 1,000-file timing.
  - Watcher debounce; profile loading and override precedence.
  - Timeline store: CRUD, pin, undo, auto-exit kept to one.
- **Core:**
  - Zapper light and trigger bits at a bright or dark pixel, and timing relative to the beam.
  - Four Score signature and bit order.
  - Header override.
- **Platform:**
  - Rewind keeps ≥ 60 s under the cap, rewinds to the exact earlier frame (equal to a snapshot taken
    then), and has bounded memory.
  - Playtime counting excludes pauses.
- **ViewModels:** library filters and checklist mode, Add-folder flow, Game Details edits saved, timeline
  operations with undo, catalog import wizard states, and the rewind scrub state.
- **Zero-config test** (automated, ViewModels level):
  1. Fresh in-memory services.
  2. Library is empty and shows Add a ROM folder.
  3. Pick a folder with a test ROM, then the scan finishes.
  4. One tile appears; Play navigates to the Player.
  5. The emulator is running.
  6. Asserted to finish within 60 s of simulated user time, with no Settings visit.

## Out of scope
Password capture, journal, assist/practice modes, Tier 2–3 mappers, dumper (Phase 6).

**Bundled homebrew (SPEC §6.4)** needs titles whose authors clearly permit redistribution. Choosing them
needs a license review by the owner, so the Library falls back to the empty-state flow until titles are
picked.
