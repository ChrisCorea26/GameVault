# Bugs and missing features

Places where the application doesn't do what `SPEC.md` or `docs/design/input.md` says it should, for phases 0–16.
Each entry was checked against the code when it was filed. When you fix one, set its status to **Fixed** with the
commit, and update the user manual (`docs/manual/`) if players can see the change. GV-001 to GV-020 were fixed on the
`fix/bugs-gv-001-020` branch; the plan and what remains for the owner are in `docs/plans/bugs-gv-001-020.md`.

Severity: **High** = a required feature is missing for players. **Medium** = partly built, inconsistent, or required
content is missing. **Low** = wrong text or a small inconsistency.

| ID | Severity | Area | Title | Status |
|---|---|---|---|---|
| [GV-001](#gv-001) | High | Input | Buttons can't be remapped | Fixed (35483aa) |
| [GV-002](#gv-002) | Medium | Input | Pads can't be assigned to Players 3–4 by hand | Fixed (35483aa) |
| [GV-003](#gv-003) | Medium | Input | No controller port cards in the Player | Fixed (35483aa) |
| [GV-004](#gv-004) | Medium | Game Details | Properties expose only 3 of the profile settings | Fixed (12a99fa) |
| [GV-005](#gv-005) | Medium | UX | Destructive actions have no undo or confirmation | Fixed (2e8ad95) |
| [GV-006](#gv-006) | Medium | Passwords | No password definitions ship | Fixed (68e1574) |
| [GV-007](#gv-007) | Medium | Game data | No game profiles or memory maps ship | Fixed (68e1574) |
| [GV-008](#gv-008) | Medium | Library | No bundled homebrew | Fixed (a4207a3) |
| [GV-009](#gv-009) | Medium | Settings | BIOS, ROM and dumper paths have no file picker | Fixed (2e8ad95) |
| [GV-010](#gv-010) | Medium | Apple II | No message when the Disk II boot ROM is missing | Fixed (12a99fa) |
| [GV-011](#gv-011) | Medium | Library | Only the title of an unknown game can be edited | Fixed (a4207a3) |
| [GV-012](#gv-012) | Medium | Saves | Battery saves can't be kept next to the ROM | Fixed (cc2a9ac) |
| [GV-013](#gv-013) | Medium | NES input | Power Pad and Arkanoid paddle aren't supported | Fixed (dff109b) |
| [GV-014](#gv-014) | Low | Settings | Library caption lists only .nes, .fds and .zip | Fixed (12a99fa) |
| [GV-015](#gv-015) | Low | Catalog | Catalog Import page says NES only | Fixed (12a99fa) |
| [GV-016](#gv-016) | Low | Game Details | NES-only properties and Codes shown for every system | Fixed (12a99fa) |
| [GV-017](#gv-017) | Low | Player | Challenges button shown for every game | Fixed (12a99fa) |
| [GV-018](#gv-018) | Low | Input | Settings → Input uses NES button names for every system | Fixed (35483aa) |
| [GV-019](#gv-019) | Low | Player | Rewind bar isn't a scrub bar and stops at 60 seconds | Fixed (12a99fa) |
| [GV-020](#gv-020) | Low | Video | Overscan crop is only on or off | Fixed (12a99fa) |
| [GV-021](#gv-021) | High | Input | App crashes on launching a game after visiting Settings | Fixed |
| [GV-022](#gv-022) | High | UX | Most pages can't be used with a controller alone | Fixed (e0b0e8a) |
| [GV-023](#gv-023) | High | PlayStation | Discs stop on the BIOS's white Sony logo | Fixed (b3c1816; pending a check with a retail BIOS) |
| [GV-024](#gv-024) | High | PlayStation 2 | Discs stay on a black screen with the player's BIOS | Open |
| [GV-025](#gv-025) | High | PlayStation | Games run slowly and ignore the keyboard at their menu | Open |
| [GV-026](#gv-026) | High | Atari 5200 | Games show the BIOS's title and then a black screen | Fixed (pending a check with the game) |

---

### GV-001

**Buttons can't be remapped** · High · Input

- **Expected:** SPEC §5.4: "Remapping is per-player and can be per-game. Includes turbo buttons and one-handed layout
  presets." input.md §6: "Remaps are stored per controller model (SDL GUID) and per player"; §9 lists a **Remap…**
  button on each port card.
- **Actual:** Settings → Controllers has no Remap button. Keyboard layouts are fixed in
  `src/GameVault.Platform/Input/KeyboardMapping.cs` and pad layouts in `ControllerManager.ApplyToGame`
  (`src/GameVault.Platform/Input/Controllers/ControllerManager.cs`). There are no per-player, per-game or one-handed
  layouts.
- **Impact:** Players with non-standard keyboards, left-handed players and players with accessibility needs can't
  change the controls.
- **Fixed** in `35483aa`: Settings → Controllers and the Player's Controllers flyout have **Remap…** on every source: per system (with each pad's own button names), per player, per controller model (SDL GUID) and, in the Player, per game. Keyboard layouts: Standard and one-handed left/right; turbo buttons are remappable. Stored as `AppSettings.InputRemaps` (`InputRemapSet`), applied by `ControllerManager` through `KeyboardMapping.ForLayout` and per-model pad tables (input.md §6.7). Sticks and analog triggers stay fixed.

### GV-002

**Pads can't be assigned to Players 3–4 by hand** · Medium · Input

- **Expected:** input.md §1: the Four Score's Players 3 and 4 work like Players 1 and 2; §9 offers **Use for Player N**
  for unassigned pads.
- **Actual:** While a Four Score game runs, pads fill Players 3–4 in connection order
  (`PortAssignmentPolicy.MaxPorts = 4`, `PlayerViewModel` calls `SetPlayerCount(4)`), but Settings only has **Use for
  Player 1** and **Use for Player 2** (`SettingsPage.xaml:448-449`, `InputSettingsViewModel.UseForPlayer1/2`), and
  **Swap players** only swaps ports 0 and 1 (`InputSettingsViewModel.cs:90`).
- **Fixed** in `35483aa`: Unassigned controllers get **Use for Player N** for every port in use (1–4 during a Four Score game), and each port card has **Swap** with every other player.

### GV-003

**No controller port cards in the Player** · Medium · Input

- **Expected:** input.md §9: "The same port cards appear in the in-game overlay, so a player can swap without leaving
  the game."
- **Actual:** `PlayerPage.xaml` has no port cards or swap button; players must go back to the Library and open Settings.
- **Fixed** in `35483aa`: The Player has a **Controllers** flyout with the same port cards and Remap… (`Controls/ControllerPortsPanel.xaml`, `PlayerViewModel.Controllers`).

### GV-004

**Properties expose only 3 of the profile settings** · Medium · Game Details

- **Expected:** SPEC §7.5: "Profiles apply automatically. The user can see and override any setting in *Game
  Properties*" (header corrections, input type, run-ahead, sprite limit, region/timing).
- **Actual:** Game Details → Properties has Controls, Reduce input lag and Reduce flicker only
  (`GameDetailsPage.xaml:137-139`; `GameDetailsViewModel.SaveOverrides`). Region, header corrections, the Apple II
  model, the Genesis 3/6-button pad and the N64 save type (`GameProfile` `region`, `header`, `model`, `pad`, `save`)
  can only be set in `data/gamedb` JSON. Players can't fix a PAL game running at NTSC speed, an N64 game with the wrong
  save type, or an unenhanced IIe ROM running as an enhanced IIe.
- **Fixed** in `12a99fa`: Properties shows Region (NES, Genesis, SNES, Master System/SG-1000), NES header corrections (mapper, mirroring, battery), the Apple II model, the Genesis 3/6-button pad and the N64 save type, saved as overrides like the others.

### GV-005

**Destructive actions have no undo or confirmation** · Medium · UX

- **Expected:** SPEC §9.3: "Every destructive action (delete state, remove from library) supports undo or asks for
  confirmation."
- **Actual:** Only save-state deletion has undo (`TimelineViewModel`). The app has no `ContentDialog` and these act
  immediately:
  - Settings → Library → **Remove** a folder (`LibrarySettingsViewModel.RemoveAsync`)
  - Settings → Family → **Remove** a profile, **Remove PIN** (`FamilyViewModel.Remove`, `RemovePin`)
  - Settings → Codes → **Remove** a code collection (`CodeCollectionsViewModel.RemoveAsync`)
  - Codes → **Delete** a code (`CodesViewModel.DeleteAsync`)
  - Passwords → **Delete** (`PasswordsViewModel.DeleteAsync`)
  - Journal → **Delete note**, **Delete** a screenshot or map (`JournalViewModel.DeleteNoteAsync`, `DeleteAsync`)
- **Fixed** in `2e8ad95`: Every listed action asks first through `IConfirmationService` (a `ContentDialog` in the app, Cancel by default); save-state deletion keeps its undo.

### GV-006

**No password definitions ship** · Medium · Passwords

- **Expected:** SPEC §7.3: "Ship definitions for 5–10 well-known password games first." Phase 6 "Done when": "Password
  capture works for the 5 launch titles."
- **Actual:** `data/gamedb/passwords/` is empty, so automatic capture never runs; only F9 manual entry works. Listed as
  an owner task in `docs/plans/phase-6.md`.
- **Fixed** in `68e1574`: `data/gamedb/passwords/metroid-usa.json` ships, with every fact from the Metroid disassembly (`docs/gamedb-sources.md`). Choosing and checking the rest of the five launch titles on cartridges stays on the owner checklist (`docs/plans/phase-6.md`).

### GV-007

**No game profiles or memory maps ship** · Medium · Game data

- **Expected:** SPEC §7.5: per-game profiles give Zapper games mouse aiming, run-ahead safety and sprite-limit
  settings automatically. SPEC §7.9 / Phase 9 "Done when": "launch memory maps for real games are listed for the owner
  to verify."
- **Actual:** `data/gamedb/` has no `*.json` profiles and `data/gamedb/memory/` has only its README. For every real
  game: Zapper must be chosen by hand, run-ahead is on regardless of game safety, and the memory-map features (F2
  before-death rewind, automatic maps, stat overlays, making challenges) do nothing.
- **Fixed** in `68e1574`: Profiles for 66 accessory dumps (`input-zapper.json`, `input-four-score.json`, `input-power-pad.json`, `input-arkanoid.json`, hashes from No-Intro) and memory maps for Super Mario Bros. (World), The Legend of Zelda (USA) and Metroid (USA) from public disassemblies (`docs/gamedb-sources.md`). Run-ahead and sprite-limit flags need per-game testing and aren't set; checking the maps on real copies stays on the owner checklist.

### GV-008

**No bundled homebrew** · Medium · Library

- **Expected:** SPEC §6.4: "Ships with a small set of freely licensed homebrew games … under
  `data/homebrew/<game>/LICENSE`. These give the app playable content on first launch."
- **Actual:** `data/homebrew/` doesn't exist. A new player has nothing to play until they supply a game.
- **Fixed** in `a4207a3`: `data/homebrew/` holds Thwaite, Concentration Room, RHDE: Furniture Fight and robotfindskitten by Damian Yerrick (GPL with source / GPL with a binary exception / all-permissive / zlib), added to the library once on first launch with their details.

### GV-009

**BIOS, ROM and dumper paths have no file picker** · Medium · Settings

- **Expected:** SPEC §9.1: "Use `System.IO` and Win32 file pickers initialized with the window handle." SPEC §9.3:
  "Every page must be fully usable with a controller alone."
- **Actual:** Settings → Disk System, Apple II (two ROMs), PlayStation and Cartridge dumper are plain text boxes ("Paste
  where the file is", `SettingsPage.xaml:506-612`). Players must find and paste a full path, which isn't possible with
  a controller alone.
- **Fixed** in `2e8ad95`: Each path box has a **Choose…** button (`IGameFilePicker.PickSystemFileAsync`); typing a path still works.
  A BIOS picked that way (Disk System, PlayStation, PlayStation 2) is now also checked and copied into
  `%LOCALAPPDATA%\GameVault\bios\<system>\` (`BiosImporter`, `docs/plans/phase-17-performance.md` §4).

### GV-010

**No message when the Disk II boot ROM is missing** · Medium · Apple II

- **Expected:** Every other missing BIOS or ROM shows a message naming the Settings page (e.g.
  `ConsoleFactory.cs:44-45` for the Apple II system ROM).
- **Actual:** With the Disk II path empty, the machine starts with no disk controller (`AppleConsole.cs:25`,
  `EmulatorHost.cs:891-893`), so the disk never boots and nothing tells the player why.
- **Fixed** in `12a99fa`: Starting an Apple II disk without the Disk II boot ROM shows "This disk can't start without the Disk II boot ROM. Choose your own Disk II boot ROM (256 bytes) in Settings → Apple II. GameVault doesn't include it." (`EmulatorHost.MissingDiskRomNotice`).

### GV-011

**Only the title of an unknown game can be edited** · Medium · Library

- **Expected:** SPEC §6.2: unmatched files appear as "Unknown / Homebrew", "where the user can edit metadata."
- **Actual:** Only the title is editable (`GameDetailsViewModel.CanEditTitle`). Publisher, developer, year, genre,
  region and player count can't be set, so homebrew can't be filtered or grouped.
- **Fixed** in `a4207a3`: Game Details → **Edit details** (unknown games only): publisher, developer, year, genre, region and players, saved in the library database (schema 3) and used by the library's filters.

### GV-012

**Battery saves can't be kept next to the ROM** · Medium · Saves

- **Expected:** SPEC §4.5: battery saves go next to the library data, "never inside the ROM folder unless the user opts
  in."
- **Actual:** There's no opt-in; saves always go to `%LOCALAPPDATA%\GameVault\Saves\` (`BatterySaveStore`).
- **Fixed** in `cc2a9ac`: Settings → Gameplay → **Keep game saves next to the game files** (off by default) writes `Game.sav` beside the game file and reads it first; saves already in the Saves folder are still found, and a read-only folder falls back to it.

### GV-013

**Power Pad and Arkanoid paddle aren't supported** · Medium · NES input

- **Expected:** SPEC §4.7: "Phase 5+: Four Score, Power Pad (keyboard mapping), Arkanoid paddle (mouse)."
- **Actual:** Only the Four Score was built; `src/GameVault.Core` has no Power Pad or Arkanoid (Vaus) device, and the
  Properties → Controls list has no entries for them.
- **Fixed** in `dff109b`: `PowerPad` and `ArkanoidPaddle` devices on port 2 (NES state version 4); the keyboard is the mat (U I O P / J K L ; / M , . /) and the mouse turns the paddle. Chosen in Properties → Controls or by the game database.

### GV-014

**Library caption lists only .nes, .fds and .zip** · Low · Settings

- **Actual:** Settings → Library says "GameVault looks for .nes, .fds and .zip files in these folders…"
  (`SettingsPage.xaml:166`), but the scanner finds every extension in `RomFiles.Extensions`.
- **Fix:** Name the systems instead of extensions, or list them all.
- **Fixed** in `12a99fa`: The caption names every system GameVault plays, and .zip files.

### GV-015

**Catalog Import page says NES only** · Low · Catalog

- **Actual:** "A catalog lists every NES release … such as a No-Intro NES DAT" (`CatalogImportPage.xaml:15`) and the
  error "That file doesn't list any NES games" (`CatalogImportViewModel.cs:63`), but `CatalogImporter` detects and
  imports Master System, SG-1000, Apple II, Genesis, SNES, PlayStation (Redump) and N64 DATs too.
- **Fixed** in `12a99fa`: The page and the error name every system and No-Intro/Redump DATs.

### GV-016

**NES-only properties and Codes shown for every system** · Low · Game Details

- **Actual:** Properties → Controls (Zapper, Four Score) and Reduce flicker apply only to the NES, and the Codes
  section only works for NES games (the Player hides its Codes button for other systems, `PlayerViewModel.cs:700`), but
  Game Details shows all of them for every system.
- **Fixed** in `12a99fa`: Controls, Reduce flicker, header corrections and the Codes section show only for NES games.

### GV-017

**Challenges button shown for every game** · Low · Player

- **Actual:** The XAML comment at `PlayerPage.xaml:202-203` says the button is conditional, but it has no `Visibility`
  binding. For games without a memory map the flyout only offers taking a challenge, which needs a challenge file made
  from a game that has one.
- **Fixed** in `12a99fa`: The button shows only when the game has a memory map or a challenge is open (`PlayerViewModel.ShowChallenges`).

### GV-018

**Settings → Input uses NES button names for every system** · Low · Input

- **Expected:** SPEC §7.11: "per-system … controller mapping."
- **Actual:** Noted in `docs/plans/phase-11.md` ("Settings → Input doesn't rename buttons per system yet"); the Genesis,
  SNES, PlayStation and N64 layouts are only documented in input.md §6.2–6.5 and the user manual.
- **Fixed** in `35483aa`: Remap… names each system's buttons as its pad does (input.md §6.7).

### GV-019

**Rewind bar isn't a scrub bar and stops at 60 seconds** · Low · Player

- **Expected:** SPEC §7.2: "Hold … to rewind, with a scrub bar shown while rewinding."
- **Actual:** A read-only progress bar with `Maximum="60"` (`PlayerPage.xaml:625`), even when Rewind memory or
  Unlimited rewind keeps more than 60 seconds. It can't be dragged to a point.
- **Fixed** in `12a99fa`: The bar is a slider spanning the whole history when rewinding started. Dragging it holds the game still and moves both ways through the history (`RewindBuffer.Restore`); letting go plays from that point.

### GV-020

**Overscan crop is only on or off** · Low · Video

- **Expected:** SPEC §5.2: "The default crop hides overscan (top and bottom 8 lines). Configurable."
- **Actual:** Settings → Video → **Hide screen edges** is a single on/off switch (`AppSettings.CropOverscan`); the amount
  can't be changed.
- **Fixed** in `12a99fa`: Settings → Video → **Hide screen edges** offers the whole picture, 4, 8 (default), 12 or 16 lines (`AppSettings.OverscanCropLines`).

### GV-021

**App crashes on launching a game after visiting Settings** · High · Input · **Fixed**

- **Expected:** Starting a game works whatever pages the player visited first.
- **Actual:** `InputSettingsViewModel` subscribed to `IInputDevicesService.Changed` and `Notice` in its constructor and
  never unsubscribed. It is registered `AddTransient`, and the Settings page is not cached, so every visit to Settings
  left another live view model owned by the `ControllerManager` singleton's event. Opening a game calls
  `PlayerViewModel.Attach` → `SetGameRunning(true)`, which raises `Changed`; each leaked view model then ran `Refresh`
  and pushed `Clear`/`Add` into the `ObservableCollection`s still bound to the torn-down Settings
  `ItemsRepeater`s (`SettingsPage.xaml:386,438`), and the native `NotifyCollectionChangedEventHandler` threw
  `COMException` 0x80004005 ("Unspecified error"). This hit every system, not just the Master System — the crash
  happens in `Attach()`, before the ROM is read — but it showed up on new systems because players open Settings to add
  the folder holding their `.sms` files first.
- **Fixed:** `InputSettingsViewModel` now has the `Attach`/`Detach` pair every other view model here uses, and
  `SettingsPage` calls them on `Loaded`/`Unloaded` (commit `14499bb`).

### GV-022

**Most pages can't be used with a controller alone** · High · UX · **Fixed**

- **Expected:** Every page is fully usable with a controller alone: the D-pad and stick move focus, South
  activates, East goes back (SPEC §9.3, `docs/design/input.md` §7).
- **Actual:** `PadNavigation` replays pad presses as the arrow keys, Enter and Esc. Arrow keys move focus between
  controls only where `XYFocusKeyboardNavigation` is enabled, which is just the Library's two lists
  (`LibraryPage.xaml:266,275`). On Settings, Game Details, Catalog Import, Dump Cartridge and the Player's flyouts the
  D-pad can't leave the focused button; a text box keeps the arrows for its caret; Enter doesn't toggle a
  ToggleSwitch or CheckBox or open an Expander; nothing scrolls a long page or opens a game tile's context menu.
- **Fixed** in `e0b0e8a`: `PadFocusPlanner` decides each press from the focused element and `PadNavigation` carries it out
  with XY focus and UI Automation on every page and flyout; the shoulders, triggers, North and Start were added
  (`docs/design/input.md` §7.1, `docs/plans/outstanding-work.md` §1).

### GV-023

**Discs stop on the BIOS's white Sony logo** · High · PlayStation · **Fixed in b3c1816 (pending a check with a retail BIOS)**

- **Expected:** With the player's BIOS and fast boot off, a disc goes from the white Sony Computer Entertainment logo to
  the black PlayStation logo and the game.
- **Actual (reported):** a `.cue`/`.bin` disc stays on the white logo, which is where the BIOS starts the CD-ROM drive
  and reads the disc.
- **Cause found:** when a response waited behind an unacknowledged one (GetID's or Init's second response, for example),
  `PsCdRom` delivered it inside the write that acknowledged the first. The BIOS's handler acknowledges the controller
  first and I_STAT after (OpenBIOS `cdromIOVerifier`/`cdromIOHandler`), so the second write cleared the new
  interrupt's I_STAT bit and the BIOS waited for it forever. A waiting response now arrives 1,000 cycles after the
  acknowledge, and a sector's INT1 no longer queues behind one still unacknowledged (as DuckStation does). Test:
  `PlayStationMachineTests.A_response_waiting_behind_another_still_interrupts_after_the_bios_acknowledges_both`.
- **Second cause, from the reporter's `boot-ps1.log` (SCPH-1001, BIOS 2.2, *Crash Bandicoot*):** with the drive fixed,
  the BIOS read the licence and logo sectors and then spent every cycle in its interrupt handler, reading I_MASK,
  I_STAT and DICR 80,000 times a second. A DICR write cleared bit 31 before the edge check, so every write while
  another enabled channel's flag stayed set looked like a new rising edge and raised IRQ 3 again: each time the
  handler acknowledged the CD-ROM's DMA it interrupted itself. Bit 31 is now kept across the write, so only a real
  rise raises IRQ 3 (psx-spx "DMA Interrupt Register"). Test:
  `PlayStationMachineTests.Writing_dicr_while_its_irq_flag_stays_set_raises_no_new_interrupt`. The log line now
  shows I_STAT, I_MASK and DICR.
- **Still to check:** no retail BIOS is available to the tests. If a disc still stops, `logs\boot-ps1.log` (below)
  shows the drive's last command and where the CPU is waiting.

### GV-024

**Discs stay on a black screen with the player's BIOS** · High · PlayStation 2 · **Open**

- **Expected:** With the player's BIOS, a disc (`.iso`) shows the BIOS's start-up screens and then the game.
- **Actual (reported):** a black screen from the start, with fast boot off and with **Exact emulation** on (so the GPU
  renderer isn't the cause). The BIOS never reaches a picture, so the EE or the IOP is stuck early in its boot.
- **Why it's still open:** the BIOS boot has only been tested with the stand-in BIOS the tests build
  (`docs/plans/phase-17.md`); a retail BIOS can't be in the repository or the tests.
- **To diagnose:** every PlayStation and PlayStation 2 game now writes its first minute to `logs\boot-ps1.log` or
  `logs\boot-ps2.log` (`BootLog`): once a second where the EE and the IOP are, the hardware registers each reads most
  (a stuck BIOS spins on one), PMODE/SMODE2, and the BIOS's own text output; on the PlayStation every CD-ROM command and
  response.
- **First cause, from the reporter's `boot-ps2.log` (*GTA San Andreas*):** the BIOS printed "Initialize memory" and
  then the EE read $1000F410 42 million times a second at $9FC41928, while the IOP waited on the SIF for it. That
  register kept whatever was written to it; the BIOS waits for it to read 0, as PCSX2 returns. It now reads 0, and so
  does the SIO status at $1000F130 (KPUTCHAR's wait), the only other register on that page that differed. Test:
  `Ps2ConsoleTests.The_bioss_wait_registers_read_zero_whatever_was_written`.
- **Second stop (the next log):** the BIOS now prints "Total accessable memory size: 32 MB", the TLB setup,
  "Initialize Start." and "Initialize GS ...", and then the log ends: not even the once-a-second line comes, so no
  frame finished. The EE's own frame limit (a tenth of a second of its cycles) would end a frame if the EE were
  running, so the emulator itself is stuck in C# (a device loop or a wait), not the BIOS in a MIPS loop. The boot log
  now has a watchdog: when no frame finishes for three seconds it writes, from its own thread, the EE's and IOP's PCs
  and cycle counts, the last 16 registers each wrote, and the emulation thread's managed stack (read with ClrMD from
  a snapshot of the process). The next log will name the method it is stuck in.
- **Third stop (the watchdog's log):** frames were finishing after all (the earlier log had simply been closed
  early); the EE now spun at $80007CC4 reading $1A000004 42 million times a second, right after "Initialize GS ...".
  $1A000000 is the DVE, the video encoder, which wasn't mapped (reads gave 0): the BIOS sends it commands and waits
  for its status at $1A000006 to say ready (bit 1). `Ps2Dve` now models it as PCSX2's `ba0R16`/`ba0W16` do
  (commands, power, the status that turns ready a few reads after a command, the error bit), and it is in the save
  state (console chunk version 3). Test: `Ps2ConsoleTests.The_video_encoder_reports_ready_a_few_status_reads_after_a_command`.

### GV-025

**Games run slowly and ignore the keyboard at their menu** · High · PlayStation · **Open**

- **Reported** after GV-023's fixes: *Crash Bandicoot* reaches its menu, but runs very slowly with dragging sound,
  and no keyboard key works there.
- **Speed:** the core runs a GPU test program at 826 frames a second in a Release build and 128 in a Debug build
  (`dotnet run` builds Debug unless given `-c Release`), so a Debug build may fall below 60 in a real game. The boot
  log now gives the build and the real time each 60 frames took.
- **From the next log:** it was a Debug build, at about 2.7 s of real time per 60 frames. `GameVault.Core` is now
  compiled optimized in Debug builds too: the GPU test program runs at 725 frames a second in Debug (146 before),
  close to Release's 826.
- **Keyboard, from the same log:** the game polls the pad 120 times a second and the pad answers correctly (a digital
  pad, nothing pressed: `01>FF 42>41 00>5A 00>FF 00>FF`), but the host passed no button to pad 1 in any second while
  the reporter pressed keys. `InputService` maps the PlayStation's keys correctly (Enter → Start, X → ○, the arrows →
  the stick), so the keys don't reach it: most likely the game picture didn't have keyboard focus. The boot log now
  counts the key presses the game picture passes in each second, to confirm.
- **Keyboard, first look:** the pad's replies and button bits read correctly in the code and tests. The boot log now gives,
  each second, the buttons the host passed to pad 1, how many conversations the game started on the port, and the
  last one byte by byte (sent>received, `!` where the device didn't answer with /ACK), so the next log shows whether
  keys reach the pad and whether the game is reading it.

### GV-026

**Games show the BIOS's title and then a black screen** · High · Atari 5200 · **Fixed (pending a check with the game)**

- **Reported:** a 5200 game shows the Atari logo with its name, then a black screen.
- **Cause found:** a 16 KB cartridge is either one 16 KB chip (code at $8000–$BFFF, mirrored at $4000) or two 8 KB
  chips (the first at $4000–$7FFF, the second at $8000–$BFFF), and many of Atari's own 16 KB games are two-chip.
  GameVault used the two-chip layout only when a game's profile named it, and none ship. Mapped as one chip, a
  two-chip game's name and start address still read correctly (they sit at the end of the second chip, file
  offset $3FE8–$3FFF, either way), so the BIOS shows the title; the game then jumps into code that isn't where it
  expects and crashes.
- **Fixed:** with no model in the profile, `Atari5200Cartridge.LooksTwoChip` decides from the ROM's code: a one-chip
  game's JSRs and JMPs point into $8000–$BFFF, a two-chip game's also into $4000–$7FFF. The profile's model
  (`two-chip`, or the new `one-chip`) still wins. Test:
  `Atari5200Tests.A_16k_games_board_is_told_from_where_its_code_calls_unless_the_profile_names_it`. Not yet checked
  with the reporter's game.

---

## Deferred on purpose (not bugs)

These are left for later by the SPEC, README or a phase plan, so they aren't filed above.

| Item | Source |
|---|---|
| ~~SNES enhancement chips still missing~~ Done: every chip a game uses (the S-RTC, SPC7110, Cx4 and ST018 came last, `docs/plans/snes-chips-3.md`); the Super Game Boy and Satellaview aren't chips of that kind and stay unsupported | `docs/plans/snes-chips-3.md` |
| ~~Genesis stereo, EEPROM saves on EA/Acclaim/Codemasters boards; the Sega CD's mode 1, CD+G and 1M cell-image view; the 32X's SH-2 caches and mid-frame picture changes~~ Done (`docs/plans/genesis-gaps.md`; stereo came with outstanding-work §2). Still open: the J-Cart's extra pads, CHD subcode | `docs/plans/genesis-gaps.md` |
| PlayStation multitap, Justifier, GameShark, memory card manager (the DualShock, GunCon, mouse and GPU renderer are done) | `docs/plans/phase-15.md` |
| N64 recompiler, Transfer Pak, 64DD, GameShark (the Rumble Pak and GPU renderer are done) | README status, `docs/plans/phase-16.md` |
| ~~Apple II monochrome, user character ROM, mid-frame mode changes~~ Done (outstanding-work §9) | `docs/plans/phase-12.md` |
| RetroAchievements approval for hardcore unlocks; real-game and real-cartridge checks | Owner tasks in README and phase plans |
| ~~GPU renderers~~ Done for the PlayStation, N64 and PlayStation 2 (they sharpen, they don't speed up: the software renderers still draw) | `docs/plans/gpu-renderers.md`, `docs/plans/outstanding-work.md` §14 |
| PlayStation 2 speed: heavy 3D games may still run below full speed. Fast float maths and the GPU drawing in the software GS's place are done (on by default; **Exact emulation** turns them off); without Direct3D 11 the software GS still draws | `docs/plans/ps2-full-speed.md` |
| PlayStation 2 multitap and DualShock 2 vibration (slot 2, pressure and mipmapping are done) | `docs/plans/outstanding-work.md` §12 |
| GameCube: the GPU (Flipper), discs, the DSP's sound, memory cards, the recompiler (it runs at about an eighth of full speed); the Wii; the 2600's paddles, rarer boards and PAL colours; the 5200's trak-ball, 40 KB boards and PAL machines | SPEC §7.11, `docs/plans/phase-18.md`, `docs/plans/phase-19.md` |
