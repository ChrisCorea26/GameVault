# Phase 8 — Game Genie code library

**Goal (SPEC §7.8, §12):** a Game Genie and RAM code engine, a per-game code library with import and export,
Player and Game Details UI, and a bundled code database.

**Done when:**
- codes decode and apply correctly in tests;
- codes can be added, toggled and imported for a game;
- the new screens render under `--sample-data`.

## Core (`GameVault.Core/Cheats`)
- **`GameGenieCode`**: decode and encode 6- and 8-letter codes (NESdev Wiki: "Game Genie").
  - The letters `APZLGITYEOXUKSVN` stand for 0–15.
  - A code yields an address in `$8000-$FFFF`, a value and, for 8 letters, a compare byte.
  - Invalid letters or lengths give a plain-language reason.
- **`RamCode`**: `AAAA:VV`, a RAM address (`$0000-$07FF` or `$6000-$7FFF`) and a value written before each
  frame.
- **`CheatEngine`** on the bus:
  - **ROM codes.** A CPU read in `$8000-$FFFF` that matches a code returns the code's value. For an 8-letter
    code, only when the real byte equals the compare byte, as the real device does.
  - **RAM codes.** Written at the start of every frame.
  - **Cost.** One branch per CPU read when no codes are on. When codes are on, a 32 KB-indexed lookup with no
    allocation and no LINQ.
  - **Determinism.** Codes change only between frames, so run-ahead and rewind stay deterministic.
- **State.** The active code set is part of the console snapshot (console chunk v3). Loading a state or
  rewinding restores the codes that were on.

## Library (`GameVault.Library/Cheats`)
- **`CheatStore`**: per game (headerless SHA-1), `%LOCALAPPDATA%\GameVault\Cheats\<sha1>.json`. It holds
  each code, a description, and whether it is on.
- **Import / export**
  - Plain text: one `CODE  description` per line; `#` comments.
  - FCEUX `.cht` (`S:` and `SC:` lines) on import.
  - Bad lines are reported with line numbers. Nothing is half-imported.
- **Bundled database**: `data/gamedb/cheats/<sha1>.json`, the same format. Shown read-only as "Suggested
  codes" and copied into the player's list when used. **Its content needs the owner** (see below).
  Every source is listed with its license in `data/gamedb/cheats/SOURCES.md`.

## Platform / ViewModels / App
- **`ICheatService`** (Platform): load and save per game, push the enabled set to the emulator between
  frames, and tell practice mode when codes are on (personal bests are not recorded then).
- **`CodesViewModel`**:
  - add, edit, delete, toggle and import/export, with live validation ("Decodes to $C3A2 = 05").
  - Sample data uses fictional games and made-up codes.
- **App**
  - Game Details → **Codes** tab (list with toggles, add box, import/export buttons).
  - Player → **Codes** flyout with toggles, plus a hotkey to turn all codes off at once (recorded in
    `docs/design/input.md` §8).
  - A small "Codes on" badge on the Player while any code is active.
- **CI smoke test:** the Codes list lives on Game Details and in the Player's flyout rather than on a page
  of its own, so the existing `--page=gamedetails` and `--page=player` launches cover it.

## Tests
- **Decoding:**
  - The NESdev Wiki's worked examples decode to the documented address, value and compare.
  - Encoding then decoding returns the same code, for every address/value/compare.
  - Malformed codes are rejected with a reason.
- **Engine:** with a synthetic ROM,
  - a 6-letter code replaces a byte;
  - an 8-letter code replaces it only when the compare matches (and not after a bank switch changes the
    byte);
  - RAM codes hold a value across frames.
- **State:** a snapshot with codes on round-trips and restores the code set. Rewind across a toggle
  restores the earlier set.
- **Library:** store round-trip, plain-text and `.cht` import (including error lines), export.
- **ViewModels:** validation messages, toggling, import results, the practice personal-best rule.

## Open: needs the owner
- **Where the bundled codes come from.** Published codebooks, and most code websites, are copyrighted
  compilations, so they can't be copied into the repository. Options:
  1. Ship an empty database plus the documented format. Accept community contributions under CC0, with
     each contributor confirming they found or verified the code themselves.
  2. Use a specific source whose license the owner has checked.
  3. No bundled database: codes come only from players and their imports.

  The engine, library and UI don't depend on this choice.

## Order of work
1. Core decoder, engine and state.
2. Library store and import/export.
3. Service and ViewModels.
4. App screens and smoke test.
5. Bundled database, once the owner picks a source.

## Results
- **Core:**
  - `CheatCode` parses and writes 6- and 8-letter Game Genie codes and RAM codes, with plain-language
    errors.
  - `CheatEngine` replaces ROM reads, honoring the compare byte, and writes RAM codes before each frame.
    When no codes are on, the cost is one check per cartridge read.
  - Console snapshots (chunk v3) record the active codes.
  - Tests check the documented SXIOPO patch ($91D9 = $AD) and a 20,000-code encode/decode round-trip.
- **Library:**
  - `CheatStore` keeps per-game lists in `%LOCALAPPDATA%\GameVault\Cheats`. Game Genie letters are kept as
    typed.
  - `CheatFiles` imports plain text and FCEUX `.cht` files (all or nothing, with line numbers) and exports
    plain text.
  - `CheatSuggestions` reads `data/gamedb/cheats/` (empty for now).
- **Platform:**
  - `CheatService` pushes the enabled codes to the running game.
  - When a save state, rewind or F3 changes what is on, the list's switches follow.
- **UI:**
  - Codes on Game Details and in a Player flyout: switches, live "Decodes to …" checking, import, export,
    "Turn all off" and suggestions.
  - A "Codes on" badge, and F3 to switch every code off.
  - Practice personal bests aren't recorded while codes are on.

## Follow-up: code sources (owner decision)
- **GameGenie.com**
  - Codes → "Get codes from GameGenie.com" downloads the game's page from the site's NES Game Genie list.
    The game is matched by title, or by the collection game the player chose.
  - The codes are shown under "Codes from GameGenie.com" with the credit "These codes come from
    GameGenie.com (© Danworld, Inc.)…" and a link to the page.
  - Pages are cached (the list for 7 days, each game for 30), and an old copy is used when offline.
- **Player code collections**
  - Settings → Codes imports .xlsx (the "NES" sheet, else the first) or .csv files with Game, Code ("Key
    in…") and Effect columns.
  - Rows that aren't codes become notes for the game.
  - Codes appear per game "in your collection", with the collection name and the row's source.
  - A search box picks the right game when titles differ ("Zelda II: The Adventures of Link").
- **Why nothing is bundled:**
  - The site's list and the official codebook compilations the owner supplied carry no license that
    allows redistribution, so the repository keeps none of their codes.
  - The owner's spreadsheet imports locally: 5,173 codes for 478 games.
- **Combined codes:** codes written "A + B + C" are stored and switched together.
