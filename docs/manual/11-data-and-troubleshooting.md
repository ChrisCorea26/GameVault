# Your data

## Where GameVault keeps things

Everything GameVault creates is in `%LOCALAPPDATA%\GameVault\` (usually
`C:\Users\<you>\AppData\Local\GameVault\`). Your ROM folders are only ever read, never written to, and your BIOS
files are read from where you put them.

| Path | Contents |
|---|---|
| `settings.json` | Your settings |
| `library.db` | Library: games found, catalog, ownership, status, ratings, notes, playtime |
| `Saves\` | Battery saves (`.sav`), Disk System and Apple II disk writes (`.fdssave`, `.a2save`), PlayStation memory cards (`.mcd`), N64 Controller Paks (`.mpk`) |
| `States\` | Save states and their thumbnails |
| `Passwords\` | Captured passwords |
| `Journal\` | Notes, screenshots and maps |
| `Cheats\` | Your codes; `Collections\` holds imported code collections, `GameGenie.com\` the downloaded pages |
| `Sessions\`, `Maps\`, `Practice\` | Recent play sessions, automatic maps, practice personal bests |
| `retroachievements.json` | Encrypted RetroAchievements sign-in |
| `profiles.json` | Family profiles and the parent PIN (hashed) |
| `Profiles\<id>\` | The same files for each extra family profile |
| `logs\crash.log` | Details of any crash |

Cover pictures stay where you chose them; only their location is stored, so don't move or delete them.

## Backing up and moving to a new PC

1. Close GameVault.
2. Copy the whole `%LOCALAPPDATA%\GameVault\` folder to a backup drive.
3. On the new PC, install GameVault, copy the folder back to the same place, and copy your games and BIOS files.
4. If your games or BIOS files are in different places on the new PC, update **Settings → Library** and the BIOS
   paths. Saves are matched to games by contents, not by file name or location, so they carry over.

The RetroAchievements sign-in is tied to your Windows account, so sign in again on the new PC.

## Resetting GameVault

- **Settings only:** **Settings → Restore defaults**, or close GameVault and delete `settings.json`.
- **Everything:** close GameVault and delete (or rename) the `%LOCALAPPDATA%\GameVault\` folder. This removes your
  saves too, so back it up first.

# Troubleshooting

| Problem | Try this |
|---|---|
| A game isn't in the library | Check its extension is listed in [Supported systems](#supported-systems). A `.zip` must hold exactly one game; files over 64 MB and PlayStation `.bin` files without a `.cue` are skipped. Choose **Settings → Library → Look for new games**. |
| *That file isn't a game GameVault can open.* | The file is damaged, has the wrong extension, or is for a system GameVault doesn't support. |
| *GameVault couldn't open that file.* | It was moved, deleted, or is open in another program. |
| A game shows a BIOS or ROM message | Set the file in Settings for that system. See [What you must supply](#what-you-must-supply). |
| A game has the wrong name or shows as Unknown | Import a catalog for that system, or type the title in Game Details. Bad or modified dumps don't match the catalog. |
| No sound | Check **Settings → Audio → Game volume** and the Windows volume mixer. Sound is muted while fast-forwarding. |
| Sound or picture stutters | Close other heavy programs, set **Reduce input lag** to *Low* or *Off*, and choose **Sharp pixels**. Nintendo 64 3D scenes can run below full speed. |
| The game pauses when I click another window | That's **Pause when GameVault is in the background**; turn it off in Settings → Gameplay. |
| Controller doesn't work | Make sure GameVault is the active window; controllers are ignored in the background. Check **Settings → Controllers** for which player it's assigned to. If it's under *Other controllers*, choose **Use for Player 1**. Some apps (Steam, Game Bar) capture the Guide button; hold **Back + Start** instead. |
| A keyboard key does nothing | Click the game picture to give it focus. Two-player keyboard games: some keyboards can't register many keys at once. |
| Rewind or save states are greyed out | RetroAchievements **Hardcore mode** or a challenge attempt turns them off. |
| A game freezes or glitches with Reduce input lag on | Set **Game Details → Properties → Reduce input lag** to *Off* for that game. |
| NES sprites flicker | Turn on **Game Details → Properties → Reduce flicker**, or Assist → **Reduce flicker**. |
| An N64 game won't start | Turn off **Settings → Nintendo 64 → Expansion Pak**. |
| An N64 game doesn't keep its save | Its save type may be missing from GameVault's database. |
| A PlayStation game won't start with fast boot | Turn off **Skip the startup screens**. |
| Settings are greyed out | The family profile is locked. A parent can unlock it with the PIN. |
| GameVault crashed | Details are in `%LOCALAPPDATA%\GameVault\logs\crash.log`. Include that file when you report the problem. |

## Command-line options

| Option | What it does |
|---|---|
| `--sample-data` | Starts with a made-up library so you can explore without games. Nothing is saved. |
| `--sample-data=empty` | The same, with an empty library, to see the first-run screens. |
| `--page=<name>` | Opens on `Library`, `Settings`, `Player`, `GameDetails`, `CatalogImport` or `DumpCartridge`. Mainly for previews with `--sample-data`. |
| `--profile=<id>` | Opens as a family profile. GameVault uses this itself when you switch profiles. |

GameVault can't be given a game file on the command line yet.
