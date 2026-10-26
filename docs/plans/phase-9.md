# Phase 9 — Game-aware features

**Goal (SPEC §7.9, §12):**
- memory maps;
- "rewind to before I died";
- automatic maps;
- the "Previously on…" recap;
- family profiles;
- verified challenges;
- stat overlays.

**Done when:**
- every feature works end to end with synthetic test ROMs and memory maps;
- the screens render under `--sample-data`;
- launch memory maps for real games are listed for the owner to verify.

> **Needs the owner (later):** memory maps for real games must be checked against real games, which this
> repository can't contain. The format, engine and UI are built and tested with synthetic ROMs. Launch
> maps are an owner checklist at the end of this file, as with Phase 6's password titles.

## 1. Memory maps (Library)
- **Location:** `data/gamedb/memory/<sha1>.json`, documented in `docs/memory-maps.md`.
- **`values`:** named RAM values.
  - Each value has an address, a size of 1 or 2 bytes (little-endian), and an encoding (`binary` or `bcd`).
  - Optional `mask` and `shift` extract bits; an optional `offset` is added for display ("lives shown =
    RAM + 1"); an optional `max` is shown as "of N".
- **`playing`:** RAM conditions meaning real gameplay, not the title screen or demo. Deaths, rooms and
  challenges only count while these hold.
- **`lives`:** the name of the lives value.
- **`map`:** the room value, an optional area value (overworld / dungeon N), the grid width in rooms, and
  optional names per area.
- **`overlay`:** the values to show, with labels and an optional `when` condition.
- **Conditions:** `{ "value": "health", "op": "<=", "target": 2 }`. Operators are `==`, `!=`, `<`, `<=`, `>`
  and `>=`. Raw `{ "address", "mask", "value" }` RAM conditions are also accepted.
- **Contents:** addresses and names only, never game data.

## 2. Watching the game (Platform)
- **`GameWatcher`** runs on the emulation thread after each frame, when the game has a memory map.
  - It reads only the listed bytes, through side-effect-free peeks.
  - It doesn't run during rewind or run-ahead's hidden frames.
- It raises:
  - `LifeLost(frame)` when lives drop while playing;
  - `RoomEntered(area, room, frame, thumbnail)` the first time each room is seen;
  - overlay values about 6 times a second.

## 3. "Rewind to before I died" (SPEC §7.9)
- The watcher keeps a small ring of recent states, one per second for the last 10 seconds.
- On `LifeLost` it saves the state from about 5 seconds earlier to the timeline as a new kind,
  `TimelineKind.BeforeDeath` ("Before you lost a life"). Only the newest 5 such entries are kept per game.
- **F2** loads the newest one; the Player shows a notice with an Undo.
- The hotkey is added to `docs/design/input.md` §8.

## 4. Automatic maps
- `MapStore` keeps the visited rooms per game and area, each with a 64×60 thumbnail of the screen on its
  first visit.
- `MapRenderer` draws a PNG for each area: rooms on the memory map's grid, unvisited cells dark, and the
  current room outlined.
- The rendered map is a journal image ("Explored map: Overworld"), refreshed when a new room appears. It
  shows in Game Details → Journal and in the recap.

## 5. "Previously on…" recap
- `SessionLog` (Library) records each play session: start, length, game.
- When a game starts and the last session was a day or more ago, the Player shows a recap card:
  - when and how long the last session was;
  - the newest screenshot and journal note (title and first lines);
  - the newest password;
  - whether a resume point exists;
  - the explored map.
- **Continue** dismisses the card; **Resume where I left off** uses the existing resume offer.

## 6. Family profiles
- `ProfileStore` lives in `%LOCALAPPDATA%\GameVault\profiles.json`. Each profile has a name, an icon, a
  daily limit in minutes (or none), and whether it may change settings.
- The first profile uses the existing data folder, so nothing moves. Other profiles keep everything in
  `Profiles/<id>/`: library, saves, timeline, journal, passwords, codes, practice and settings.
- **Parent PIN:** stored as a PBKDF2 hash with a salt. It is needed to edit profiles and limits, and to
  keep playing past a limit.
- **Switching** is done from the navigation pane's footer (the custom title bar is a drag region, so a
  button there wouldn't take clicks). The app restarts into the chosen profile (`--profile=<id>`), so
  every service opens the right folders.
- **Daily limit:**
  - Today's play time is counted from the emulator's playtime reports.
  - A notice appears at 5 minutes left.
  - At the limit, GameVault saves the resume point and pauses. Unpausing asks for the PIN.
- **Sample data:** two sample profiles.

## 7. Verified challenges
- **A challenge file (`.gvchallenge`)** is JSON with:
  - the ROM's SHA-1 and title;
  - a start state (a save state);
  - goal conditions (RAM conditions, so they work without a memory map);
  - an optional time limit in frames;
  - the creator's note.
- **Creating** (Player → Challenges): start from the current moment, build the goal from the memory map's
  values (or raw RAM), set a time limit, and save the file.
- **Taking a challenge:**
  - The challenge's start state is loaded.
  - The inputs for players 1-2 are recorded every frame.
  - Rewind, state loads, codes, slow motion and netplay are off.
  - The attempt ends when the goal holds (success) or the time runs out.
  - The result is saved as a `.gvresult` file: the challenge's id, the inputs (run-length encoded), the
    finishing frame and the player's name.
- **Checking a result:** the inputs are replayed on a fresh console from the start state, without
  drawing, and the goal must hold exactly at the reported frame. The result is "Verified: done in 1:23.45"
  or "Doesn't check out". Nothing goes through a server.

## 8. Stat overlays
- The overlay is drawn over the game view as a small XAML panel, not in the swap chain: "Health 3 of 16",
  "Boss 12".
- It is toggled per game from the Player (and remembered). It is hidden in screenshots.

## Tests
- **Memory maps:** the parser, value encodings (bcd, 16-bit, mask and shift), and conditions.
- **Watcher**, with a synthetic ROM whose lives and room bytes change at known frames: life lost, rooms
  entered, overlay values, and no events outside `playing`.
- **Before-death states:** the timeline entry, pruning to 5, and F2.
- **Maps:** the store and the renderer (pixel checks on a known grid).
- **Recap:** the threshold and contents (ViewModel).
- **Profiles:** PIN hashing and verification, folder layout, limit warning and stop (with a fake clock),
  switching (ViewModel).
- **Challenges:**
  - A recorded attempt verifies.
  - A tampered input or frame fails.
  - A different ROM is refused.
  - The time limit is enforced.

## Order of work
1. Memory maps.
2. Watcher.
3. Before-death states.
4. Stat overlays.
5. Maps.
6. Recap.
7. Challenges.
8. Profiles.

## Results
All eight parts are done. The full Linux suite passes: Core 411, Library 48, ViewModels 134,
Platform 151 and Dumper 6.
- **Challenges:** file format in `docs/challenges.md`. The Player's *Challenges* flyout makes a
  challenge from the current moment (goal values from the memory map), takes one, saves the result and
  checks a friend's result by replaying it.
- **Profiles:**
  - `profiles.json` holds the profiles, a PBKDF2-SHA256 PIN hash (100,000 iterations, random salt) and
    each profile's play for today.
  - Settings → Family adds and removes profiles and sets the PIN. A profile that can't change
    settings sees them locked until a parent unlocks them.
  - The daily limit warns at 5 minutes left. At the limit it saves the resume point and pauses, and
    unpausing is blocked until the PIN is entered (then there are no more stops that day).
- **Sample data:** two profiles, Alex and Sam (PIN 1234), and a sample challenge and result.

## Owner checklist (later, with real games)
1. Choose about 5 launch games (for example, a platformer with lives, an overworld adventure, and a game
   with a visible health bar).
2. Write or check their memory maps against your own copies with a RAM viewer, and add them to
   `data/gamedb/memory/`.
