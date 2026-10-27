# Supported systems

Each section below follows the same pattern: the files GameVault reads, what you must supply, setup, controls,
where saves go, and what isn't supported yet.

**Things that apply to every system:**

- GameVault detects the system from the file, so you never pick it yourself.
- Cartridge games can be inside a `.zip` that holds exactly one game. Files larger than 64 MB are ignored.
  PlayStation discs can't be zipped.
- For BIOS and ROM settings, choose **Choose…** next to the box and pick the file, or paste its full path (for
  example, in File Explorer <kbd>Shift</kbd>+right-click the file and choose **Copy as path**).
  - A BIOS you pick with **Choose…** (Disk System, PlayStation, PlayStation 2) is checked and copied into GameVault's
    own `bios` folder (see [Your data](#your-data)), and the box then shows the copy. So the BIOS keeps working if
    you move the original or unplug the drive it's on. A file that fails the check isn't copied.
  - A pasted path, and the Apple II ROMs, are read from where they are each time.
  - Files are checked by size only (the PlayStation 2 BIOS by its version too).
- Battery saves, memory cards and disk writes go to `%LOCALAPPDATA%\GameVault\Saves\`. They're matched to the game
  by its contents, so renaming or moving a game file keeps its saves.
  With **Settings → Gameplay → Keep game saves next to the game files**, cartridge saves go beside each game as a
  `.sav` instead.
- Save states, rewind, practice, challenges and netplay work on every system unless noted.

## Nintendo Entertainment System (NES)

| | |
|---|---|
| **Files** | `.nes` (iNES and NES 2.0 headers) |
| **You supply** | Nothing but your games |
| **Saves** | Battery saves as `.sav` |

**Controls:** the standard layout in [Controls](#controls), with turbo A and B. Zapper light-gun games and the
Arkanoid paddle use the mouse, and the Power Pad mat uses the keyboard (see [Power Pad](#power-pad)). The Four Score
adds players 3 and 4: the third and fourth controllers you connect join as Players 3 and 4 while the game runs, and
**Use for Player 3 / 4** moves one there. GameVault recognises about 65 Zapper, Four Score, Power Pad and Arkanoid
releases and sets their controls itself; for others choose them in **Game Details → Properties → Controls**.

**Game data GameVault ships:** memory maps for *Super Mario Bros.*, *The Legend of Zelda* (USA, first release) and
*Metroid* (USA), which give F2's "before you lost a life" (Super Mario Bros.), the explored map (Zelda), the stat
overlay and challenges; and automatic password capture for *Metroid* (USA). They were written from the games'
published disassemblies and are matched by the game's exact dump, so other releases (another region or revision)
aren't covered.

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
2. Open **Settings → Disk System**, choose **Choose…** and pick the file (or paste its path into the box).
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
**Start** (<kbd>Enter</kbd>) is the console's **Pause** button. Select does nothing. **Remap…** shows these
buttons by their Sega names.

**Region:** games run at NTSC speed (60 Hz). For a European game, set **Game Details → Properties → Region** to
*PAL (50 Hz)*.

**Not supported yet:** the SC-3000 keyboard (so BASIC cartridges can't be used) and FM sound for Japanese Master
System games.

## Apple II, II+ and IIe

| | |
|---|---|
| **Files** | 5.25-inch disk images: `.dsk`, `.do`, `.po` (143,360 bytes), `.nib` (232,960 bytes), `.woz` (WOZ 1 and 2) |
| **You supply** | **The system ROM** from your Apple (12 KB for a II or II+, 16 KB for a IIe) **and the Disk II controller's boot ROM** (256 bytes, the P5 PROM 341-0027) |
| **Saves** | Disk writes as `.a2save` patches; your disk images are never changed |

**Setup:**

1. Put both ROM files anywhere, for example `C:\Apple\apple2e-enhanced.rom` and `C:\Apple\disk2.rom`.
2. Open **Settings → Apple II** and choose each file with **Choose…** next to **System ROM** and **Disk II boot ROM**
   (or paste their paths).
3. Open a disk image.

**Which Apple runs:** a 16 KB ROM runs an enhanced Apple IIe (65C02 processor); a 12 KB ROM runs an Apple II+. For a
game that needs another model (for example an unenhanced IIe), set **Game Details → Properties → Apple II model**.

> **Important:** Without the Disk II boot ROM the computer starts with no disk controller, so disks can't boot.
> GameVault then shows *This disk can't start without the Disk II boot ROM. Choose your own Disk II boot ROM (256
> bytes) in Settings → Apple II. GameVault doesn't include it.*

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
| *This disk can't start without the Disk II boot ROM. Choose your own Disk II boot ROM (256 bytes) in Settings → Apple II. GameVault doesn't include it.* | Set the Disk II boot ROM path. |
| *A .dsk, .do or .po disk image is 143,360 bytes.* | The image is damaged or isn't a 5.25-inch disk. |
| *GameVault reads 5.25-inch WOZ images; this one is for 3.5-inch disks.* | 3.5-inch disks aren't supported. |

## Sega Genesis / Mega Drive

| | |
|---|---|
| **Files** | `.md`, `.gen`, `.smd`, `.bin` (a `.bin` is used only if it has a Sega header) |
| **You supply** | Nothing but your games. No BIOS is needed. |
| **Saves** | Battery saves (SRAM) as `.sav` |

**Region** is read from the game automatically. To run a game as another region (for example a European game at
50 Hz), set **Game Details → Properties → Region**.

**Controls:** GameVault uses the 6-button pad. For the few older games that misread it, set **Game Details →
Properties → Controller** to *3-button*.

| Genesis | Keyboard | Controller (position) | Xbox label |
|---|---|---|---|
| A, B, C | <kbd>Z</kbd>, <kbd>X</kbd>, <kbd>C</kbd> | Left, bottom, right | X, A, B |
| X, Y, Z | <kbd>Q</kbd>, <kbd>W</kbd>, <kbd>E</kbd> | Left shoulder, top, right shoulder | LB, Y, RB |
| Mode | <kbd>Right Shift</kbd> | Back | View |
| Start | <kbd>Enter</kbd> | Start | Menu |

While a Genesis game runs the shoulder buttons are game buttons, so use <kbd>Backspace</kbd> and <kbd>Tab</kbd> to
rewind and fast-forward.

**Not supported yet:** interlaced high-resolution mode, Master System mode, EEPROM
saves, Sega CD and 32X.

## Super Nintendo

| | |
|---|---|
| **Files** | `.sfc`, `.smc` (a 512-byte copier header is skipped) |
| **You supply** | Your games. Games with a **DSP chip** also need that chip's program ROM, dumped from your own cartridge (see below). GameVault uses its own boot program, so no other Nintendo ROM is needed. |
| **Saves** | Battery saves as `.sav` (Super FX games' saves too) |

**Controls:**

| SNES | Keyboard | Controller (position) | Xbox label |
|---|---|---|---|
| B, A | <kbd>Z</kbd>, <kbd>X</kbd> | Bottom, right | A, B |
| Y, X | <kbd>A</kbd>, <kbd>S</kbd> | Left, top | X, Y |
| L, R | <kbd>1</kbd>, <kbd>2</kbd> | Left shoulder, right shoulder | LB, RB |
| Select, Start | <kbd>Right Shift</kbd>, <kbd>Enter</kbd> | Back, Start | View, Menu |

There's no turbo on the SNES. Use <kbd>Backspace</kbd> and <kbd>Tab</kbd> to rewind and fast-forward.

**Enhancement chips.** Some cartridges carry an extra chip:

- **Super FX** (*Star Fox*, *Yoshi's Island*, *Stunt Race FX*, …): works with nothing extra. The game's own code runs
  on it.
- **DSP-1** (*Super Mario Kart*, *Pilotwings*, …), DSP-2, DSP-3 and DSP-4: the chip runs a program built into it, which
  GameVault doesn't include. Dump it from your own cartridge and name the file `dsp1b.rom` (or `dsp1.rom`),
  `dsp2.rom`, `dsp3.rom` or `dsp4.rom`. Put it in a folder, then choose that folder in **Settings → Super
  Nintendo → Enhancement chip ROMs**. Either byte order works. DSP-2, DSP-3 and DSP-4 haven't been tried with real
  games yet.

| Message | What to do |
|---|---|
| *This game needs the DSP-1 chip's program ROM. Put your own dump (dsp1b.rom or dsp1.rom) in the folder chosen in Settings → Super Nintendo → Enhancement chip ROMs. GameVault doesn't include it.* | Add the file and choose its folder, then open the game again. |
| *This game needs the … chip, which GameVault doesn't support yet.* | The chip isn't emulated yet. |

**Not supported yet:** the SA-1, S-DD1, S-RTC, OBC1, Cx4, SPC7110 and ST01x chips, the Super Game Boy and the
Satellaview; interlaced modes; effects that change the picture partway along a line.

## Sony PlayStation

| | |
|---|---|
| **Files** | `.cue` with its `.bin` tracks; `.chd` (version 5); `.m3u` playlists for multi-disc games; `.exe` (PS-EXE homebrew) |
| **You supply** | **A PlayStation BIOS**, 512 KB, dumped from your own console. SCPH-1001 (North America), SCPH-5500 (Japan) and SCPH-7502 (Europe) are typical. |
| **Saves** | One memory card per game in slot 1, as `.mcd` |

**Setup:**

1. Put the BIOS file anywhere, for example `C:\PlayStation\scph1001.bin`.
2. Open **Settings → PlayStation**, choose **Choose…** and pick the file (or paste its path into the box).
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
save, its save type is probably missing from the database: choose the cartridge's save type in **Game Details →
Properties → Save type**.

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

**Performance:** the N64's graphics are drawn in software, on several processor cores at once, so a CPU with more
cores helps. 2D games and most 3D scenes run at full speed on a modern CPU; the heaviest 3D scenes may still slow
down on a CPU with few cores.

**Not supported yet:** Rumble Pak, Transfer Pak, mouse, voice recognition unit, 64DD, GameShark codes, and the N64's
video filters.

## Sony PlayStation 2

| | |
|---|---|
| **Files** | `.iso` (DVD or CD images); `.chd` (version 5, CD or DVD); `.cue` with its `.bin` tracks (CD games); `.elf` (homebrew programs) |
| **You supply** | **A PlayStation 2 BIOS**, 4 MB, dumped from your own console (for example SCPH-39001), for discs. Homebrew `.elf` programs run without one. |
| **Saves** | One 8 MB memory card per game in slot 1, as `.ps2`, and one card in slot 2 shared by every game |

**Setup:**

1. Put the BIOS file anywhere, for example `C:\PlayStation 2\scph39001.bin`.
2. Open **Settings → PlayStation 2**, choose **Choose…** and pick the file (or paste its path into the box). GameVault checks the file and shows its version,
   for example *PlayStation 2 BIOS version 1.60, Europe, 2002-02-07.*
3. Optionally turn on **Skip the startup screens** to start discs straight away instead of showing the console's
   startup screens and browser. A few games need them; turn it off if one doesn't start.
4. Open the game's `.iso`, `.chd` or `.cue` file, or an `.elf` program.

GameVault tells PlayStation and PlayStation 2 discs apart by what's on them, so a `.cue`, `.chd` or `.iso` can be
either; it shows up under the right system in the library.

**Homebrew `.elf` programs** run on GameVault's own built-in system software when no BIOS is set. With a BIOS set
they start through it instead, which programs that load their own IOP modules need.

**Disc images:** as for the PlayStation, keep a `.cue` with its `.bin` files, and use version 5 CHDs. Discs are read
from where they are; they're never copied or loaded into memory whole.

**Controls** (DualShock 2):

| PlayStation 2 | Keyboard | Controller (position) | Xbox label |
|---|---|---|---|
| Cross, Circle | <kbd>Z</kbd>, <kbd>X</kbd> | Bottom, right | A, B |
| Square, Triangle | <kbd>A</kbd>, <kbd>S</kbd> | Left, top | X, Y |
| L1, R1 | <kbd>1</kbd>, <kbd>2</kbd> | Left shoulder, right shoulder | LB, RB |
| L2, R2 | <kbd>3</kbd>, <kbd>4</kbd> | Left trigger, right trigger | LT, RT |
| L3, R3 | <kbd>5</kbd>, <kbd>6</kbd> | Click the left stick, click the right stick | LS, RS |
| Select, Start | <kbd>Right Shift</kbd>, <kbd>Enter</kbd> | Back, Start | View, Menu |
| D-pad | <kbd>T</kbd> <kbd>F</kbd> <kbd>G</kbd> <kbd>H</kbd> | D-pad | D-pad |
| Left stick | Arrow keys (full push) | Left stick | Left stick |
| Right stick | <kbd>I</kbd> <kbd>J</kbd> <kbd>K</kbd> <kbd>L</kbd> (full push) | Right stick | Right stick |

The pressure-sensitive buttons report full presses, except L2 and R2 on a controller with analog triggers: they
report how far you pull them. While a PlayStation 2 game runs the left shoulder button is L1,
so use <kbd>Backspace</kbd> to rewind.

**Memory cards:** each game gets its own 8 MB card in slot 1, created already formatted and saved with your other game
saves. Slot 2 holds one more card, the same for every game (`Memory card (slot 2).ps2` in the saves folder), so a
sequel can find a save that the earlier game wrote there.

**Rewind, run-ahead and netplay:** a PlayStation 2 is much larger than the other consoles, so:

- **Rewind** goes back in half-second steps.
- **Reduce input lag** (run-ahead) is always off for PlayStation 2 games.
- **Netplay** isn't available for PlayStation 2 games.

**Performance:** the PlayStation 2 is emulated in software. GameVault translates the main processor's code into
your PC's own as it runs, and draws the graphics on several processor cores at once, so a CPU with more cores
helps. Lighter games can reach full speed on a fast PC, but many games still run below it, especially 3D games that
lean on the PS2's vector units. Enhanced graphics and more speed-ups are planned.

**Not supported yet:** changing discs in multi-disc games (an `.m3u` starts its first disc), vibration, the multitap,
PlayStation games on the PlayStation 2, USB devices, the network adapter and hard drive, GameShark codes, and enhanced
(upscaled) graphics.

**Messages:**

| Message | What to do |
|---|---|
| *This is a PlayStation 2 disc. To play it, choose your own PlayStation 2 BIOS in Settings → PlayStation 2. GameVault doesn't include it.* | Follow the setup above. |
| *The PlayStation 2 BIOS chosen in Settings → PlayStation 2 isn't a 4 MB BIOS file. Choose the BIOS dumped from your own console.* | Wrong file or bad dump. |
| *GameVault couldn't open your PlayStation 2 BIOS. Check the file chosen in Settings → PlayStation 2.* | The file was moved, deleted or is locked. |
| *That file isn't a PlayStation 2 program (ELF) or disc, or it's damaged.* | Check the file; re-dump the disc if needed. |
| *Netplay isn't available for PlayStation 2 games: its rollback saves and loads the whole console every frame, and a PlayStation 2 is too large for that.* | Play it locally. |
