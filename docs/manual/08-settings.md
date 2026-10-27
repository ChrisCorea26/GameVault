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
**Look for new games** rescans them all. GameVault looks for games for every system it plays, including games inside
`.zip` files. **Remove** asks first (*Remove this folder?*): the folder's games leave your library, but the files
aren't deleted, and adding the folder again brings them back with their saves and notes.

## Video

| Setting | Values | Default |
|---|---|---|
| **Hide screen edges** | Show the whole picture, 4 lines, 8 lines (like most TVs), 12 lines, 16 lines, at the top and at the bottom. Hide more if a game draws flickering lines or garbage at the edges. | 8 lines |
| **Picture style** | Sharp pixels, Smooth, Living room CRT, Arcade monitor | Sharp pixels |
| **TV pixel shape** | On / Off. Stretches the picture to the 8:7 pixel shape of a TV. | Off |
| **Play in full screen** | On / Off. Games start in full screen. | Off |

## Audio

| Setting | Values | Default |
|---|---|---|
| **Game volume** | 0 to 100 | 80 |
| **Mono** | On / Off. Plays both stereo channels on both speakers, for one speaker or one ear. | Off |

Sound is in stereo where the console had it: the Genesis / Mega Drive, Super Nintendo, PlayStation, Nintendo 64
and PlayStation 2. The NES, SG-1000, Master System and Apple II were mono, so their sound is the same on both
speakers.

## Gameplay

| Setting | Values | Default |
|---|---|---|
| **Reduce input lag** | Off, Low, Medium, High (run-ahead of 0 to 3 frames) | Low |
| **Pause when GameVault is in the background** | On / Off | On |
| **Rewind memory** | 64 MB, 128 MB, 256 MB, 512 MB, 1 GB | 256 MB |
| **Keep game saves next to the game files** | On / Off. Cartridge saves go beside each game as a `.sav` with the same name (for example `Game.sav` next to `Game.nes`), so other emulators can use them. Saves already in GameVault's Saves folder are still found. If a game's folder can't be written to, its save stays in GameVault's folder. Memory cards and Controller Paks always stay there. | Off |

*Reduce input lag* uses run-ahead: GameVault quietly runs a frame or more ahead so the game responds sooner. Games
known to break with it have it turned off automatically, and you can override it per game in Game Details →
Properties. Higher settings need a faster PC.

## Controllers

| Setting | Values | Default |
|---|---|---|
| **Swap players** | Swaps Player 1 and Player 2. Each card's **Swap** menu swaps it with any other player. | |
| **Pin to this player** / **Unpin** | Per controller | Not pinned |
| **Remap…** | Per keyboard or controller model, per player and per system. See [Remapping buttons](#remapping-buttons). | Standard layouts |
| **Use for Player N** | For extra controllers, one button per player (four during Four Score games) | |
| **Two players, one keyboard** | On / Off | Off |
| **Turbo speed** | 30, 20, 15, 12, 10 or 7.5 presses per second | 15 |

## Netplay

| Setting | Values | Default |
|---|---|---|
| **Relay server (optional)** | `server:port` of a GameVault relay, for hosting over the internet when the two PCs can't reach each other directly. See [Netplay](#netplay). | Empty |

## Cartridge dumper

The path to `inlretro.exe`, the INL Retro-Prog command-line tool, for example `C:\INL-retro\inlretro.exe`. Type or
paste it, or choose **Choose…** to pick the file. Takes effect the next time GameVault starts. See [Cartridge dumper](#cartridge-dumper-inl-retro-prog).

## Codes

Code collections you've imported, each with **Remove** (which asks first). **Import a code collection…** adds a `.xlsx` or `.csv`
file. See [Code collections](#code-collections).

## Disk System

The path to your Famicom Disk System BIOS, for example `C:\NES\disksys.rom`, or **Choose…** to pick it.

## Sega CD

The path to your Sega CD BIOS (128 KB), for example `C:\Sega\bios_CD_U.bin`, or **Choose…** to pick it. A chosen
file is checked and copied into GameVault's own folder.

## 32X

The paths to your three 32X BIOS files: the 68000's (256 bytes, for example `C:\Sega\32X_G_BIOS.BIN`), the master
SH-2's (2 KB) and the slave SH-2's (1 KB), or **Choose…** for each. Each chosen file is checked and copied into
GameVault's own folder.

## Apple II

| Setting | Example |
|---|---|
| **System ROM** | `C:\Apple\apple2e-enhanced.rom` |
| **Disk II boot ROM** | `C:\Apple\disk2.rom` |
| **Character ROM** (optional) | `C:\Apple\apple2e-video.rom`: text is drawn in your own Apple's font instead of GameVault's |
| **Monitor** | **Colour TV** (default), **Green monitor**, **Amber monitor** or **White monitor** |

Each box has **Choose…** to pick the file. The character ROM can be the 2 KB one from a II or II+ or the 4 or 8 KB
video ROM from a IIe; GameVault works out how it's laid out. A new monitor or character ROM applies from the next
game started.

## Super Nintendo

| Setting | Values | Default |
|---|---|---|
| **Enhancement chip ROMs** | A folder holding your own `dsp1b.rom` (or `dsp1.rom`), `dsp2.rom`, `dsp3.rom`, `dsp4.rom`, `st010.rom` or `st011.rom`, or **Choose…** | Empty |

## PlayStation

| Setting | Values | Default |
|---|---|---|
| **BIOS** | Path, for example `C:\PlayStation\scph1001.bin`, or **Choose…** | Empty |
| **Skip the startup screens** | On / Off | Off |
| **Analog mode** | On starts the controllers in analog mode, as if you'd pressed the DualShock's Analog button. Takes effect the next time a game starts. | Off |

## PlayStation 2

| Setting | Values | Default |
|---|---|---|
| **BIOS** | Path, for example `C:\PlayStation 2\scph39001.bin`, or **Choose…**. GameVault checks it's a 4 MB BIOS and shows its version. | Empty |
| **Skip the startup screens** | On / Off | Off |

## Nintendo 64

| Setting | Values | Default |
|---|---|---|
| **Expansion Pak** | On / Off. Takes effect the next time a game starts. | On |
| **Rumble Pak** | On puts a Rumble Pak in every controller instead of the Controller Pak, so games can shake your controller. Takes effect the next time a game starts. | Off |

## Advanced

| Setting | Values | Default |
|---|---|---|
| **Allow opposite directions** | On / Off. Lets the D-pad press Left+Right or Up+Down together. | Off |

## Restore defaults

**Restore defaults** resets Video, Audio, Gameplay and *Allow opposite directions*. It doesn't touch your folders,
BIOS and ROM paths, the dumper path, controller settings, PlayStation and Nintendo 64 options, family profiles or
RetroAchievements.

All path boxes save when you leave the box, or as soon as you pick a file with **Choose…**, which also works with a
controller. Quotes around a pasted path are removed for you.
