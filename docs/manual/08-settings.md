# Settings reference

Open **Settings** with the gear at the bottom of the navigation pane. Changes save immediately; there's no Apply
button. Some settings say *takes effect next time a game starts*.

If the current family profile isn't allowed to change settings, the page shows *Settings are locked for this
profile* and a parent can unlock it with the PIN (see [Family profiles](#family-profiles)).

## Family

Profiles, the parent PIN and daily play limits. See [Family profiles](#family-profiles).

## RetroAchievements

| Setting | Values | Default |
|---|---|---|
| **Username**, **Password**, **Sign in** | Your RetroAchievements account | Signed out |
| **Hardcore mode** | On / Off | **On** |

See [RetroAchievements](#retroachievements).

## Library

**Game folders** lists the folders GameVault watches, each with **Remove**. **Add a folder…** adds one.
**Look for new games** rescans them all.

## Video

| Setting | Values | Default |
|---|---|---|
| **Hide screen edges** | On / Off. Hides the overscan border that TVs cut off. | On |
| **Picture style** | Sharp pixels, Smooth, Living room CRT, Arcade monitor | Sharp pixels |
| **TV pixel shape** | On / Off. Stretches the picture to the 8:7 pixel shape of a TV. | Off |
| **Play in full screen** | On / Off. Games start in full screen. | Off |

## Audio

| Setting | Values | Default |
|---|---|---|
| **Game volume** | 0 to 100 | 80 |

## Gameplay

| Setting | Values | Default |
|---|---|---|
| **Reduce input lag** | Off, Low, Medium, High (run-ahead of 0 to 3 frames) | Low |
| **Pause when GameVault is in the background** | On / Off | On |
| **Rewind memory** | 64 MB, 128 MB, 256 MB, 512 MB, 1 GB | 256 MB |

*Reduce input lag* uses run-ahead: GameVault quietly runs a frame or more ahead so the game responds sooner. Games
known to break with it have it turned off automatically, and you can override it per game in Game Details →
Properties. Higher settings need a faster PC.

## Controllers

| Setting | Values | Default |
|---|---|---|
| **Swap players** | Swaps Player 1 and Player 2. | |
| **Pin to this player** / **Unpin** | Per controller | Not pinned |
| **Use for Player 1 / 2** | For extra controllers | |
| **Two players, one keyboard** | On / Off | Off |
| **Turbo speed** | 30, 20, 15, 12, 10 or 7.5 presses per second | 15 |

## Cartridge dumper

The path to `inlretro.exe`, the INL Retro-Prog command-line tool, for example `C:\INL-retro\inlretro.exe`. Takes
effect the next time GameVault starts. See [Cartridge dumper](#cartridge-dumper-inl-retro-prog).

## Codes

Code collections you've imported, each with **Remove**. **Import a code collection…** adds a `.xlsx` or `.csv`
file. See [Code collections](#code-collections).

## Disk System

The path to your Famicom Disk System BIOS, for example `C:\NES\disksys.rom`.

## Apple II

| Setting | Example |
|---|---|
| **System ROM** | `C:\Apple\apple2e-enhanced.rom` |
| **Disk II boot ROM** | `C:\Apple\disk2.rom` |

## PlayStation

| Setting | Values | Default |
|---|---|---|
| **BIOS** | Path, for example `C:\PlayStation\scph1001.bin` | Empty |
| **Skip the startup screens** | On / Off | Off |

## Nintendo 64

| Setting | Values | Default |
|---|---|---|
| **Expansion Pak** | On / Off. Takes effect the next time a game starts. | On |

## Advanced

| Setting | Values | Default |
|---|---|---|
| **Allow opposite directions** | On / Off. Lets the D-pad press Left+Right or Up+Down together. | Off |

## Restore defaults

**Restore defaults** resets Video, Audio, Gameplay and *Allow opposite directions*. It doesn't touch your folders,
BIOS and ROM paths, the dumper path, controller settings, PlayStation and Nintendo 64 options, family profiles or
RetroAchievements.

All path boxes save when you leave the box. Quotes around a pasted path are removed for you.
