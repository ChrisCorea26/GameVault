# Bugs and missing features

Places where the application doesn't do what `SPEC.md` or `docs/design/input.md` says it should, for phases 0–16.
Each entry was checked against the code when it was filed. When you fix one, set its status to **Fixed** with the
commit, and update the user manual (`docs/manual/`) if players can see the change.

Severity: **High** = a required feature is missing for players. **Medium** = partly built, inconsistent, or required
content is missing. **Low** = wrong text or a small inconsistency.

| ID | Severity | Area | Title | Status |
|---|---|---|---|---|
| [GV-001](#gv-001) | High | Input | Buttons can't be remapped | Open |
| [GV-002](#gv-002) | Medium | Input | Pads can't be assigned to Players 3–4 by hand | Open |
| [GV-003](#gv-003) | Medium | Input | No controller port cards in the Player | Open |
| [GV-004](#gv-004) | Medium | Game Details | Properties expose only 3 of the profile settings | Open |
| [GV-005](#gv-005) | Medium | UX | Destructive actions have no undo or confirmation | Open |
| [GV-006](#gv-006) | Medium | Passwords | No password definitions ship | Open |
| [GV-007](#gv-007) | Medium | Game data | No game profiles or memory maps ship | Open |
| [GV-008](#gv-008) | Medium | Library | No bundled homebrew | Open |
| [GV-009](#gv-009) | Medium | Settings | BIOS, ROM and dumper paths have no file picker | Open |
| [GV-010](#gv-010) | Medium | Apple II | No message when the Disk II boot ROM is missing | Open |
| [GV-011](#gv-011) | Medium | Library | Only the title of an unknown game can be edited | Open |
| [GV-012](#gv-012) | Medium | Saves | Battery saves can't be kept next to the ROM | Open |
| [GV-013](#gv-013) | Medium | NES input | Power Pad and Arkanoid paddle aren't supported | Open |
| [GV-014](#gv-014) | Low | Settings | Library caption lists only .nes, .fds and .zip | Open |
| [GV-015](#gv-015) | Low | Catalog | Catalog Import page says NES only | Open |
| [GV-016](#gv-016) | Low | Game Details | NES-only properties and Codes shown for every system | Open |
| [GV-017](#gv-017) | Low | Player | Challenges button shown for every game | Open |
| [GV-018](#gv-018) | Low | Input | Settings → Input uses NES button names for every system | Open |
| [GV-019](#gv-019) | Low | Player | Rewind bar isn't a scrub bar and stops at 60 seconds | Open |
| [GV-020](#gv-020) | Low | Video | Overscan crop is only on or off | Open |

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

### GV-002

**Pads can't be assigned to Players 3–4 by hand** · Medium · Input

- **Expected:** input.md §1: the Four Score's Players 3 and 4 work like Players 1 and 2; §9 offers **Use for Player N**
  for unassigned pads.
- **Actual:** While a Four Score game runs, pads fill Players 3–4 in connection order
  (`PortAssignmentPolicy.MaxPorts = 4`, `PlayerViewModel` calls `SetPlayerCount(4)`), but Settings only has **Use for
  Player 1** and **Use for Player 2** (`SettingsPage.xaml:448-449`, `InputSettingsViewModel.UseForPlayer1/2`), and
  **Swap players** only swaps ports 0 and 1 (`InputSettingsViewModel.cs:90`).

### GV-003

**No controller port cards in the Player** · Medium · Input

- **Expected:** input.md §9: "The same port cards appear in the in-game overlay, so a player can swap without leaving
  the game."
- **Actual:** `PlayerPage.xaml` has no port cards or swap button; players must go back to the Library and open Settings.

### GV-004

**Properties expose only 3 of the profile settings** · Medium · Game Details

- **Expected:** SPEC §7.5: "Profiles apply automatically. The user can see and override any setting in *Game
  Properties*" (header corrections, input type, run-ahead, sprite limit, region/timing).
- **Actual:** Game Details → Properties has Controls, Reduce input lag and Reduce flicker only
  (`GameDetailsPage.xaml:137-139`; `GameDetailsViewModel.SaveOverrides`). Region, header corrections, the Apple II
  model, the Genesis 3/6-button pad and the N64 save type (`GameProfile` `region`, `header`, `model`, `pad`, `save`)
  can only be set in `data/gamedb` JSON. Players can't fix a PAL game running at NTSC speed, an N64 game with the wrong
  save type, or an unenhanced IIe ROM running as an enhanced IIe.

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

### GV-006

**No password definitions ship** · Medium · Passwords

- **Expected:** SPEC §7.3: "Ship definitions for 5–10 well-known password games first." Phase 6 "Done when": "Password
  capture works for the 5 launch titles."
- **Actual:** `data/gamedb/passwords/` is empty, so automatic capture never runs; only F9 manual entry works. Listed as
  an owner task in `docs/plans/phase-6.md`.

### GV-007

**No game profiles or memory maps ship** · Medium · Game data

- **Expected:** SPEC §7.5: per-game profiles give Zapper games mouse aiming, run-ahead safety and sprite-limit
  settings automatically. SPEC §7.9 / Phase 9 "Done when": "launch memory maps for real games are listed for the owner
  to verify."
- **Actual:** `data/gamedb/` has no `*.json` profiles and `data/gamedb/memory/` has only its README. For every real
  game: Zapper must be chosen by hand, run-ahead is on regardless of game safety, and the memory-map features (F2
  before-death rewind, automatic maps, stat overlays, making challenges) do nothing.

### GV-008

**No bundled homebrew** · Medium · Library

- **Expected:** SPEC §6.4: "Ships with a small set of freely licensed homebrew games … under
  `data/homebrew/<game>/LICENSE`. These give the app playable content on first launch."
- **Actual:** `data/homebrew/` doesn't exist. A new player has nothing to play until they supply a game.

### GV-009

**BIOS, ROM and dumper paths have no file picker** · Medium · Settings

- **Expected:** SPEC §9.1: "Use `System.IO` and Win32 file pickers initialized with the window handle." SPEC §9.3:
  "Every page must be fully usable with a controller alone."
- **Actual:** Settings → Disk System, Apple II (two ROMs), PlayStation and Cartridge dumper are plain text boxes ("Paste
  where the file is", `SettingsPage.xaml:506-612`). Players must find and paste a full path, which isn't possible with
  a controller alone.

### GV-010

**No message when the Disk II boot ROM is missing** · Medium · Apple II

- **Expected:** Every other missing BIOS or ROM shows a message naming the Settings page (e.g.
  `ConsoleFactory.cs:44-45` for the Apple II system ROM).
- **Actual:** With the Disk II path empty, the machine starts with no disk controller (`AppleConsole.cs:25`,
  `EmulatorHost.cs:891-893`), so the disk never boots and nothing tells the player why.

### GV-011

**Only the title of an unknown game can be edited** · Medium · Library

- **Expected:** SPEC §6.2: unmatched files appear as "Unknown / Homebrew", "where the user can edit metadata."
- **Actual:** Only the title is editable (`GameDetailsViewModel.CanEditTitle`). Publisher, developer, year, genre,
  region and player count can't be set, so homebrew can't be filtered or grouped.

### GV-012

**Battery saves can't be kept next to the ROM** · Medium · Saves

- **Expected:** SPEC §4.5: battery saves go next to the library data, "never inside the ROM folder unless the user opts
  in."
- **Actual:** There's no opt-in; saves always go to `%LOCALAPPDATA%\GameVault\Saves\` (`BatterySaveStore`).

### GV-013

**Power Pad and Arkanoid paddle aren't supported** · Medium · NES input

- **Expected:** SPEC §4.7: "Phase 5+: Four Score, Power Pad (keyboard mapping), Arkanoid paddle (mouse)."
- **Actual:** Only the Four Score was built; `src/GameVault.Core` has no Power Pad or Arkanoid (Vaus) device, and the
  Properties → Controls list has no entries for them.

### GV-014

**Library caption lists only .nes, .fds and .zip** · Low · Settings

- **Actual:** Settings → Library says "GameVault looks for .nes, .fds and .zip files in these folders…"
  (`SettingsPage.xaml:166`), but the scanner finds every extension in `RomFiles.Extensions`.
- **Fix:** Name the systems instead of extensions, or list them all.

### GV-015

**Catalog Import page says NES only** · Low · Catalog

- **Actual:** "A catalog lists every NES release … such as a No-Intro NES DAT" (`CatalogImportPage.xaml:15`) and the
  error "That file doesn't list any NES games" (`CatalogImportViewModel.cs:63`), but `CatalogImporter` detects and
  imports Master System, SG-1000, Apple II, Genesis, SNES, PlayStation (Redump) and N64 DATs too.

### GV-016

**NES-only properties and Codes shown for every system** · Low · Game Details

- **Actual:** Properties → Controls (Zapper, Four Score) and Reduce flicker apply only to the NES, and the Codes
  section only works for NES games (the Player hides its Codes button for other systems, `PlayerViewModel.cs:700`), but
  Game Details shows all of them for every system.

### GV-017

**Challenges button shown for every game** · Low · Player

- **Actual:** The XAML comment at `PlayerPage.xaml:202-203` says the button is conditional, but it has no `Visibility`
  binding. For games without a memory map the flyout only offers taking a challenge, which needs a challenge file made
  from a game that has one.

### GV-018

**Settings → Input uses NES button names for every system** · Low · Input

- **Expected:** SPEC §7.11: "per-system … controller mapping."
- **Actual:** Noted in `docs/plans/phase-11.md` ("Settings → Input doesn't rename buttons per system yet"); the Genesis,
  SNES, PlayStation and N64 layouts are only documented in input.md §6.2–6.5 and the user manual.

### GV-019

**Rewind bar isn't a scrub bar and stops at 60 seconds** · Low · Player

- **Expected:** SPEC §7.2: "Hold … to rewind, with a scrub bar shown while rewinding."
- **Actual:** A read-only progress bar with `Maximum="60"` (`PlayerPage.xaml:625`), even when Rewind memory or
  Unlimited rewind keeps more than 60 seconds. It can't be dragged to a point.

### GV-020

**Overscan crop is only on or off** · Low · Video

- **Expected:** SPEC §5.2: "The default crop hides overscan (top and bottom 8 lines). Configurable."
- **Actual:** Settings → Video → **Hide screen edges** is a single on/off switch (`AppSettings.CropOverscan`); the amount
  can't be changed.

---

## Deferred on purpose (not bugs)

These are left for later by the SPEC, README or a phase plan, so they aren't filed above.

| Item | Source |
|---|---|
| Internet netplay (relay/STUN); the first release is LAN only | SPEC §7.7, `docs/plans/phase-7.md` |
| Netplay for Famicom Disk System games | `EmulatorHost.cs:382` |
| SNES enhancement chips (DSP, Super FX, SA-1, …) | README status, `docs/plans/phase-14.md` |
| SC-3000 keyboard, Japanese SMS FM sound (YM2413) | `docs/plans/phase-11.md` |
| Genesis stereo, interlace mode 2, Master System mode, EEPROM saves, Sega CD, 32X | `docs/plans/phase-13.md` |
| PlayStation DualShock analog and vibration, multitap, light guns, GameShark, memory card manager, GPU renderer | `docs/plans/phase-15.md` |
| N64 GPU renderer and recompiler, Rumble Pak, Transfer Pak, 64DD, GameShark | README status, `docs/plans/phase-16.md` |
| Apple II monochrome, user character ROM, mid-frame mode changes | `docs/plans/phase-12.md` |
| RetroAchievements approval for hardcore unlocks; real-game and real-cartridge checks | Owner tasks in README and phase plans |
| PlayStation 2, GameCube / Wii, Atari 2600 / 5200 | SPEC §7.11, phases 17–19 |
