# Supported systems

Each section below follows the same pattern: the files GameVault reads, what you must supply, setup, controls,
where saves go, and what isn't supported yet.

**Things that apply to every system:**

- GameVault detects the system from the file, so you never pick it yourself.
- Cartridge games can be inside a `.zip` that holds exactly one game. Files larger than 64 MB are ignored.
  PlayStation discs can't be zipped.
- BIOS and ROM settings are plain text boxes: paste the full path to the file (for example, in File Explorer
  <kbd>Shift</kbd>+right-click the file and choose **Copy as path**). GameVault reads the file from there each time;
  it never copies it or looks anywhere else. Files are checked by size only.
- Battery saves, memory cards and disk writes go to `%LOCALAPPDATA%\GameVault\Saves\`. They're matched to the game
  by its contents, so renaming or moving a game file keeps its saves.
- Save states, rewind, practice, challenges and netplay work on every system unless noted.

## Nintendo Entertainment System (NES)

| | |
|---|---|
| **Files** | `.nes` (iNES and NES 2.0 headers) |
| **You supply** | Nothing but your games |
| **Saves** | Battery saves as `.sav` |

**Controls:** the standard layout in [Controls](#controls), with turbo A and B. Zapper light-gun games use the mouse.
The Four Score adds players 3 and 4: choose it in Game Details → Properties → Controls if a game isn't recognised,
and the third and fourth controllers you connect join as Players 3 and 4 while the game runs.

**NES-only features:** Game Genie and RAM codes, password capture, the Zapper, and cartridge dumping.

**Compatibility:** GameVault supports the cartridge boards (mappers) that cover the large majority of the licensed
library, including NROM, MMC1, UxROM, CNROM, MMC3, AxROM, MMC2, MMC4, MMC5, VRC2/4/6/7, FME-7, Namco 163 and more,
with their expansion sound. A game on an unsupported board shows *This game uses a cartridge type GameVault doesn't
support yet (mapper N).*

**If a game won't start:** *That file isn't an NES game, or it's damaged* usually means a bad dump or a file with the
wrong extension.

## Famicom Disk System

| | |
|---|---|
| **Files** | `.fds`, with or without the 16-byte fwNES header |
| **You supply** | **The Disk System BIOS**, `disksys.rom`, exactly **8 KB**, dumped from your own Disk System |
| **Saves** | Disk writes as `.fdssave` patches; your disk image is never changed |

**Setup:**

1. Put `disksys.rom` anywhere on your PC, for example `C:\NES\disksys.rom`.
2. Open **Settings → Disk System** and paste the path into the box.
3. Open an `.fds` game.

**Changing disk sides:** when the game asks you to turn the disk over, press <kbd>F4</kbd> for the next side, or pick
one from the disk drop-down in the Player (*No disk*, *Disk 1, side A*, *Disk 1, side B*, …).

**Not supported yet:** netplay with Disk System games.

**Messages:**

| Message | What to do |
|---|---|
| *To play it, choose your own Disk System BIOS (disksys.rom) in Settings → Disk System.* | No BIOS is set. Follow the setup above. |
| *GameVault couldn't open your Disk System BIOS.* | The file moved or was renamed. Check the path. |
| *The Disk System BIOS (disksys.rom) must be exactly 8 KB.* | The file isn't the BIOS, or the dump is bad. |

## Sega SG-1000 and Master System

| | |
|---|---|
| **Files** | `.sms` (Master System); `.sg`, `.sc` (SG-1000 / SC-3000) |
| **You supply** | Nothing but your games. No BIOS is needed. |
| **Saves** | Battery saves as `.sav` |

**Controls:** the NES layout. **B** (<kbd>Z</kbd>) is **Button 1**, **A** (<kbd>X</kbd>) is **Button 2**, and
**Start** (<kbd>Enter</kbd>) is the console's **Pause** button. Select does nothing.

**Not supported yet:** the SC-3000 keyboard (so BASIC cartridges can't be used), FM sound for Japanese Master System
games, and choosing PAL timing (games run at NTSC speed).

## Apple II, II+ and IIe

| | |
|---|---|
| **Files** | 5.25-inch disk images: `.dsk`, `.do`, `.po` (143,360 bytes), `.nib` (232,960 bytes), `.woz` (WOZ 1 and 2) |
| **You supply** | **The system ROM** from your Apple (12 KB for a II or II+, 16 KB for a IIe) **and the Disk II controller's boot ROM** (256 bytes, the P5 PROM 341-0027) |
| **Saves** | Disk writes as `.a2save` patches; your disk images are never changed |

**Setup:**

1. Put both ROM files anywhere, for example `C:\Apple\apple2e-enhanced.rom` and `C:\Apple\disk2.rom`.
2. Open **Settings → Apple II** and paste the paths into **System ROM** and **Disk II boot ROM**.
3. Open a disk image.

**Which Apple runs:** a 16 KB ROM runs an enhanced Apple IIe (65C02 processor); a 12 KB ROM runs an Apple II+.

> **Important:** Without the Disk II boot ROM the computer starts with no disk controller, so disks won't boot and
> no error is shown. If you see only the Apple's startup screen, check that path.

**Keyboard:** while an Apple II game runs, the whole keyboard types into the computer.

| Apple key | PC key |
|---|---|
| Esc | <kbd>F1</kbd> |
| Left arrow | <kbd>Backspace</kbd> |
| Reset | <kbd>Ctrl</kbd>+<kbd>F12</kbd> |
| Rewind (GameVault) | <kbd>Shift</kbd>+<kbd>Backspace</kbd> |

**Disks:** the Player's **Disks** menu shows drives 1 and 2, with **Insert disk in drive 1…** / **Eject drive 1** and
the same for drive 2. Use it for games on more than one disk.

**Netplay:** both players need the same ROM files as well as the same disk.

**Not supported yet:** loading an older save state doesn't undo later disk writes; no monochrome display option;
your own character ROM isn't used; video mode changes in the middle of a frame aren't shown.

**Messages:**

| Message | What to do |
|---|---|
| *To play it, choose your own Apple II ROM files in Settings → Apple II.* | Set the System ROM path. |
| *The Apple II ROM must be 12 KB (II/II+) or 16 KB (IIe).* | Wrong file or bad dump. |
| *The Disk II boot ROM must be 256 bytes.* | Wrong file. |
| *A .dsk, .do or .po disk image is 143,360 bytes.* | The image is damaged or isn't a 5.25-inch disk. |
| *GameVault reads 5.25-inch WOZ images; this one is for 3.5-inch disks.* | 3.5-inch disks aren't supported. |

## Sega Genesis / Mega Drive

| | |
|---|---|
| **Files** | `.md`, `.gen`, `.smd`, `.bin` (a `.bin` is used only if it has a Sega header) |
| **You supply** | Nothing but your games. No BIOS is needed. |
| **Saves** | Battery saves (SRAM) as `.sav` |

**Region** is read from the game automatically.

**Controls:** GameVault uses the 6-button pad.

| Genesis | Keyboard | Controller (position) | Xbox label |
|---|---|---|---|
| A, B, C | <kbd>Z</kbd>, <kbd>X</kbd>, <kbd>C</kbd> | Left, bottom, right | X, A, B |
| X, Y, Z | <kbd>Q</kbd>, <kbd>W</kbd>, <kbd>E</kbd> | Left shoulder, top, right shoulder | LB, Y, RB |
| Mode | <kbd>Right Shift</kbd> | Back | View |
| Start | <kbd>Enter</kbd> | Start | Menu |

While a Genesis game runs the shoulder buttons are game buttons, so use <kbd>Backspace</kbd> and <kbd>Tab</kbd> to
rewind and fast-forward.

**Not supported yet:** stereo sound (audio is mono), interlaced high-resolution mode, Master System mode, EEPROM
saves, Sega CD and 32X.

## Super Nintendo

| | |
|---|---|
| **Files** | `.sfc`, `.smc` (a 512-byte copier header is skipped) |
| **You supply** | Nothing but your games. GameVault uses its own boot program, so no Nintendo ROM is needed. |
| **Saves** | Battery saves as `.sav` |

**Controls:**

| SNES | Keyboard | Controller (position) | Xbox label |
|---|---|---|---|
| B, A | <kbd>Z</kbd>, <kbd>X</kbd> | Bottom, right | A, B |
| Y, X | <kbd>A</kbd>, <kbd>S</kbd> | Left, top | X, Y |
| L, R | <kbd>1</kbd>, <kbd>2</kbd> | Left shoulder, right shoulder | LB, RB |
| Select, Start | <kbd>Right Shift</kbd>, <kbd>Enter</kbd> | Back, Start | View, Menu |

There's no turbo on the SNES. Use <kbd>Backspace</kbd> and <kbd>Tab</kbd> to rewind and fast-forward.

**Not supported yet:** games with **enhancement chips**. These show *This game needs the … chip, which GameVault
doesn't support yet*: DSP (for example *Super Mario Kart*, *Pilotwings*), Super FX (*Star Fox*, *Yoshi's Island*),
SA-1, S-DD1, S-RTC, OBC1, Cx4, SPC7110, ST01x, Super Game Boy and Satellaview. Also not supported: interlaced modes and
effects that change the picture partway along a line.

## Sony PlayStation

| | |
|---|---|
| **Files** | `.cue` with its `.bin` tracks; `.chd` (version 5); `.m3u` playlists for multi-disc games; `.exe` (PS-EXE homebrew) |
| **You supply** | **A PlayStation BIOS**, 512 KB, dumped from your own console. SCPH-1001 (North America), SCPH-5500 (Japan) and SCPH-7502 (Europe) are typical. |
| **Saves** | One memory card per game in slot 1, as `.mcd` |

**Setup:**

1. Put the BIOS file anywhere, for example `C:\PlayStation\scph1001.bin`.
2. Open **Settings → PlayStation** and paste the path into the box.
3. Optionally turn on **Skip the startup screens** to start games straight away. A few games need the intro to
   start correctly; turn it off if one doesn't.
4. Open the game's `.cue`, `.chd` or `.m3u` file.

> **Note:** GameVault reports each disc's real region to the BIOS, as a real console does, so use a BIOS from the
> same region as your discs.

**Disc images:**

- **`.cue` + `.bin`**: keep the `.cue` and all its `.bin` files together in one folder. Open the `.cue`. A `.bin`
  without a `.cue` is ignored. Audio tracks must be raw `.bin`, not `.wav` or `.mp3`.
- **`.chd`**: must be CHD version 5. Convert older files with MAME's tool:
  `chdman copy -i old.chd -o new.chd`. CHDs that depend on a parent CHD must be merged the same way.
- **Multi-disc games**: make an `.m3u` text file next to the discs with one disc file per line, for example:

  ```
  Final Fantasy VII (Disc 1).cue
  Final Fantasy VII (Disc 2).cue
  Final Fantasy VII (Disc 3).cue
  ```

  Open the `.m3u`. When the game asks for the next disc, press <kbd>F4</kbd> or pick it from the disc drop-down
  (*No disc (shell open)*, *Disc 1: …*, *Disc 2: …*). Lines starting with `#` are ignored.
- **`.exe` homebrew** runs without a BIOS unless it calls into the BIOS.

Discs are read from where they are; they're never copied or loaded into memory whole.

**Controls** (digital pad):

| PlayStation | Keyboard | Controller (position) | Xbox label |
|---|---|---|---|
| Cross, Circle | <kbd>Z</kbd>, <kbd>X</kbd> | Bottom, right | A, B |
| Square, Triangle | <kbd>A</kbd>, <kbd>S</kbd> | Left, top | X, Y |
| L1, R1 | <kbd>1</kbd>, <kbd>2</kbd> | Left shoulder, right shoulder | LB, RB |
| L2, R2 | <kbd>3</kbd>, <kbd>4</kbd> | Left trigger, right trigger | LT, RT |
| Select, Start | <kbd>Right Shift</kbd>, <kbd>Enter</kbd> | Back, Start | View, Menu |

**Memory cards:** each game gets its own card in slot 1, so you never run out of space. Slot 2 is empty.

**Not supported yet:** DualShock analog sticks and vibration, multitap, mouse, light guns, link cable, GameShark
codes, a memory card manager, and enhanced (upscaled) graphics.

**Messages:**

| Message | What to do |
|---|---|
| *To play it, choose your own PlayStation BIOS in Settings → PlayStation.* | Follow the setup above. |
| *…isn't a 512 KB BIOS file.* | Wrong file or bad dump. |
| *The cue sheet '…' names '…', which isn't in its folder.* | A `.bin` was renamed or moved. Keep the names in the `.cue` and the files the same. |
| *…is a version N CHD. GameVault reads version 5…* | Convert it with `chdman copy`. |
| *This CHD needs its parent CHD…* | Merge it with `chdman copy`. |

## Nintendo 64

| | |
|---|---|
| **Files** | `.z64`, `.v64`, `.n64` (any byte order), up to 64 MB |
| **You supply** | Nothing but your games. No BIOS or PIF ROM is needed. |
| **Saves** | Cartridge saves (EEPROM, SRAM, FlashRAM) as `.sav`; one Controller Pak per game as `.mpk` |

**Settings → Nintendo 64 → Expansion Pak** (on by default) gives games the extra 4 MB of memory. Turn it off for the
rare game that won't start with it.

**Save type:** GameVault picks each game's save type from its database (`data\gamedb\n64\saves.json` in the
GameVault folder) and from homebrew headers. Games it doesn't know get a 4 Kbit EEPROM. If a game doesn't keep its
save, its save type is probably missing from the database.

**Controls:**

| N64 | Keyboard | Controller |
|---|---|---|
| Analog stick | Arrow keys (full push) | Left stick |
| A, B | <kbd>Z</kbd>, <kbd>A</kbd> | Bottom, left (Xbox A, X) |
| C buttons | <kbd>I</kbd> <kbd>J</kbd> <kbd>K</kbd> <kbd>L</kbd> (also <kbd>X</kbd> = C-down, <kbd>S</kbd> = C-left) | Right stick; right and top buttons are C-down and C-left |
| Z | <kbd>3</kbd> or <kbd>4</kbd> | Either trigger |
| L, R | <kbd>1</kbd>, <kbd>2</kbd> | Left shoulder, right shoulder |
| Start | <kbd>Enter</kbd> | Start |
| D-pad | <kbd>T</kbd> <kbd>F</kbd> <kbd>G</kbd> <kbd>H</kbd> | D-pad |

**Performance:** the N64's graphics are drawn in software. 2D games and lighter 3D scenes run at full speed on a
modern CPU; heavy 3D scenes may run slower until a GPU renderer is added.

**Not supported yet:** Rumble Pak, Transfer Pak, mouse, voice recognition unit, 64DD, GameShark codes, and the N64's
video filters.
