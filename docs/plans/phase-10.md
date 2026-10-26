# Phase 10 — RetroAchievements

**Goal (SPEC §7.10):** achievements, leaderboards and rich presence from
[RetroAchievements](https://retroachievements.org), following its hardcore rules.

## 1. Approach: the official rcheevos library, not a port
RetroAchievements publishes **rcheevos** (MIT licence), the C library every approved emulator uses. Its
`rc_client` API covers:
- sign-in;
- game identification (the NES hash);
- condition processing after each frame;
- leaderboards and rich presence;
- the hardcore rules;
- the server requests, with retries.

GameVault binds to `rc_client` through P/Invoke instead of porting it to C#.
- **Approval:** RetroAchievements reviews emulators for hardcore unlocks, and expects rcheevos'
  `rc_client`.
- **Exactness:** the runtime reorders and optimizes conditions (pause and reset first, modified-memory
  chains, Remember/Recall). Matching it bit for bit in a port would take its whole 17,000-line test suite,
  and every rcheevos update would need porting again.
- **Maintenance:** new rcheevos releases are a source update.

The C# side never interprets achievement logic. It provides memory reads, HTTP, the clock and the UI.

## 2. Native library
- **Source:** `native/rcheevos/` holds an unmodified copy of rcheevos **v12.5.0** (`include/`, `src/`,
  `LICENSE`). `native/CMakeLists.txt` builds it as a shared library: `rcheevos.dll` on Windows,
  `librcheevos.so` on Linux. The defines are `RC_SHARED`, `RC_CLIENT_SUPPORTS_HASH` and
  `RC_CLIENT_SUPPORTS_EXTERNAL` off.
- **Build:** `GameVault.Platform.csproj` runs CMake before its own build when the library for the
  current platform is missing. The output goes to `native/out/<rid>/` and is copied next to the
  assemblies.
- **Prerequisites:** CMake plus a C compiler (Visual Studio's C++ workload on Windows, gcc or clang on
  Linux). CI installs nothing extra, since both runners have them.
- **Without the library:** if it can't be built or loaded, GameVault still runs. Settings →
  RetroAchievements then says the component is missing.
- **Credits:** README and `THIRD-PARTY-NOTICES.md` give the licence and the version.

## 3. Binding (`src/GameVault.Platform/Achievements/`)
- **`RcheevosNative`:** `[LibraryImport]` declarations for the `rc_client_*` functions GameVault uses,
  plus the struct layouts it reads (user, game, achievement, leaderboard, tracker, event, server request
  and response). Strings are UTF-8.
- **`AchievementsClient`** owns the `rc_client_t*` and its callbacks:
  - **Memory reads:** NES CPU addresses `$0000–$FFFF` through `NesBus.Peek` (no side effects), as
    rcheevos' NES memory map expects. Reads happen only on the emulation thread.
  - **Server calls:** `HttpClient` POSTs, with a GameVault user agent and rcheevos' own user-agent
    clause. Responses are handed back through the native callback.
  - **Clock:** rcheevos' millisecond clock, from `TimeProvider` (tests use a fake one).
  - **Events:** rcheevos events (unlocked, leaderboard started/failed/submitted, trackers, challenge and
    progress indicators, game completed, server error, disconnected/reconnected, reset request) become
    managed events, raised on the UI thread.
- **`EmulatorHost`:**
  - calls `rc_client_do_frame` after every emulated frame and `rc_client_idle` while paused;
  - calls `rc_client_reset` on a console reset;
  - stores rcheevos' progress in save states (a new optional chunk), so loading a state in softcore
    keeps achievement progress consistent;
  - loads the game in rcheevos on `Load` with the NES hash (`rc_client_begin_identify_and_load_game`
    with the ROM bytes; an FDS image uses its own data), and unloads it on `Stop`.

## 4. Hardcore mode
On by default once signed in, as RetroAchievements recommends. While it's on:

| Turned off | How |
|---|---|
| Loading save states, rewind, F2 before-death states, the practice loop | Refused with "Not available in hardcore mode". Saving is still allowed. |
| Game Genie and RAM codes | Cleared, and can't be turned on |
| Slow motion and frame advance | Turned off |
| Netplay and verified-challenge attempts | Refused |
| Run-ahead | Allowed; RetroAchievements allows it |

Turning hardcore on mid-game resets the game, as rcheevos requires (the `RESET` event). Turning it off
is allowed at any time.

## 5. Sign-in and the token
- Settings → RetroAchievements has the username and password fields, and a hardcore switch.
- The password is sent once, to rcheevos' login. GameVault then keeps only the **token**, encrypted
  for the Windows user with DPAPI (`ProtectedData`, `CurrentUser` scope), in `%LOCALAPPDATA%\GameVault`
  (per family profile).
- Signing out deletes the token.
- On start, the token signs in again in the background.

## 6. UI
- **Notifications** in the Player (a small card in a corner for about 5 seconds):
  - "Achievement unlocked", with the badge, title, points and description;
  - leaderboard started, failed and submitted;
  - "Game mastered".
- **On-screen indicators:** leaderboard trackers (the running time or score) and challenge indicators
  (small badges) while active.
- **Achievements flyout** in the Player: the list grouped as rcheevos groups it (locked, unlocked,
  almost there…), with progress bars.
- **Rich presence:** shown in Game details as "Last seen: …".
- **Sample data:** a fake achievements service with a few unlocked and locked achievements, a
  tracker, and a notification.

## 7. Tests (offline: no RetroAchievements server)
- **Hash:** the NES hash, MD5 without the 16-byte header, matches rcheevos' own `rc_hash_nes` for
  iNES and headerless images, computed in C# and through the library.
- **The binding**, with a fake server that answers with canned JSON in the documented formats (as
  rcheevos' own client tests do). It has to:
  - sign in with a password and with a token;
  - load a synthetic game whose achievement is `0xH0010=5`;
  - unlock it when a test ROM writes 5 to `$10`, check that the unlock request is sent and the event
    raised;
  - start, track and submit a leaderboard;
  - show rich presence text.
- **Hardcore:** each blocked action is refused, turning it on resets, and progress round-trips through
  save states.
- **ViewModels:** sign-in and out, notifications, the list, and sample data.

## 8. Owner tasks
These steps can't be done from here.
1. Create or use a RetroAchievements account to test real games.
2. Ask RetroAchievements to recognise GameVault as an emulator, and later apply for hardcore
   approval. Until then the server may record unlocks as softcore or refuse them for an unknown
   client. GameVault shows the server's message when that happens.
3. Try a few NES games with achievement sets against your own ROMs.

## Order of work
1. Vendor rcheevos, add the CMake build and the CI wiring, and load the library.
2. The binding: client, memory, server, events.
3. Host integration: frames, reset, load and unload, progress in save states.
4. Hardcore rules.
5. Token storage, settings, sign-in.
6. Player UI and sample data.
7. Docs: SPEC and compatibility.
