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

**Netplay:** Disk System games play over netplay when both players have the same BIOS; disk changes happen on both
PCs at once.

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

**FM sound:** Japanese Master System games play with the console's built-in FM Sound Unit, so their FM music plays.
GameVault knows a game is Japanese from its header, or when **Game Details → Properties → Region** is *Japan*.

**SC-3000 keyboard:** `.sc` files run as an SC-3000, the SG-1000 with a keyboard, so BASIC and other keyboard
cartridges work. While one runs, the PC keyboard types into it, key for key where the SC-3000 has the same key:

| SC-3000 | PC |
|---|---|
| Letters, digits, `,` `.` `/` `;` `-` Space, Return, the arrows | The same keys |
| `:` | <kbd>'</kbd> |
| `@`, `[`, `]` | <kbd>[</kbd>, <kbd>]</kbd>, <kbd>\</kbd> |
| `^`, `¥` | <kbd>=</kbd>, <kbd>End</kbd> |
| Ins/Del, Home/Clr | <kbd>Backspace</kbd> or <kbd>Delete</kbd>, <kbd>Home</kbd> |
| Shift, Ctrl, Graph | <kbd>Shift</kbd>, <kbd>Ctrl</kbd>, <kbd>Alt</kbd> |
| Eng Dier's, Func, π | <kbd>F2</kbd>, <kbd>F3</kbd>, <kbd>F4</kbd> |

Shift+Backspace rewinds, as on the Apple II. To use an SG-1000 game's file as an SC-3000 one, rename it to `.sc`.

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

**Display:** **Settings → Apple II → Monitor** shows the Apple on a colour TV (the default) or on a green, amber or
white monochrome monitor, where hi-res pictures show every dot sharply instead of in colour. To see text in your own
Apple's font, choose its character ROM under **Character ROM**. Games that change mode partway down the screen show
as they did on the Apple.

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

**Not supported yet:** loading an older save state doesn't undo later disk writes.

**Messages:**

| Message | What to do |
|---|---|
| *To play it, choose your own Apple II ROM files in Settings → Apple II.* | Set the System ROM path. |
| *The Apple II ROM must be 12 KB (II/II+) or 16 KB (IIe).* | Wrong file or bad dump. |
| *The Disk II boot ROM must be 256 bytes.* | Wrong file. |
| *An Apple II character ROM is 2, 4 or 8 KB.* or *That file doesn't look like an Apple II character ROM.* | Wrong file for **Character ROM**, or a bad dump. Clear the box to use GameVault's own font. |
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

Games that save to a serial EEPROM chip save as `.sav` like battery games. That covers Sega's own (such as *Wonder
Boy in Monster World* and *Mega Man: The Wily Wars*) and the EA, Acclaim and Codemasters games that use one (such as
*NHLPA Hockey '93*, *NBA Jam*, *College Slam* and *Micro Machines 2*). GameVault knows those by the code in the
game's header. Codemasters' J-Cart games (with two extra pad ports on the cartridge) save, but their extra pads
don't work yet. The double-resolution interlaced mode (*Sonic the Hedgehog 2*'s two-player game) shows all 448
lines. Master System games play on the Master System core instead (open
the `.sms` file).

The 32X has its own section below.

## Sega CD / Mega-CD

| | |
|---|---|
| **Files** | `.cue` + `.bin` (with the audio tracks), or `.chd` |
| **You supply** | A Sega CD BIOS (128 KB) from your own console: **Settings → Sega CD**. Use one of the same region as your discs (a US BIOS for US discs). |
| **Saves** | The Sega CD's own backup RAM (8 KB), as `.sav`, shared by every disc |

A disc is recognised as a Sega CD disc by its first sector, so `.cue` and `.chd` files for the PlayStation and the
Sega CD can sit in the same folder. Its region is read from the disc; **Game Details → Properties → Region** overrides it.

**Controls** are the Genesis pad's (see above). The Sega CD's CD music and PCM sound play with the Genesis' sound.

**CD+G discs:** a CD+G disc's graphics travel in the disc's subcode, which a `.bin` doesn't hold. Keep the subcode
as a `.sub` file with the same name as the `.cue` (for example `Karaoke.cue`, `Karaoke.bin` and `Karaoke.sub`; CloneCD
and most disc-ripping tools can write one). The Sega CD's own CD player then shows the graphics. Subcode inside a
`.chd` isn't read.

**Cartridges that use the CD (mode 1):** some Genesis cartridges, such as MSU-MD games, play music from a CD while
the game runs from the cartridge. Put the cartridge's `.cue` (or `.chd`) beside it with the same name (for example
`Game.md` and `Game.cue`), choose your Sega CD BIOS in **Settings → Sega CD**, and open the cartridge as usual. The
game saves to its own save if it has one, and to the Sega CD's backup RAM if it doesn't.

**Not supported yet:** the backup RAM cartridge, and the 32X Sega CD games. Each part is tested with programs written for GameVault, not yet with a real BIOS, so whether a disc
boots is the first thing to check.

**Messages:**

| Message | What to do |
|---|---|
| *This is a Sega CD disc. To play it, choose your own Sega CD BIOS…* | Choose the BIOS in **Settings → Sega CD**. |
| *The Sega CD BIOS file should be 128 KB.* | The file chosen isn't a Sega CD BIOS. |
| *GameVault couldn't open that Sega CD disc image…* | A `.bin` the `.cue` names is missing or renamed. |

## 32X {#sega-32x}

| | |
|---|---|
| **Files** | `.32x` |
| **You supply** | The 32X's three BIOS files from your own 32X: the 68000's (256 bytes), the master SH-2's (2 KB) and the slave SH-2's (1 KB), in **Settings → 32X** |
| **Saves** | Battery saves (SRAM) as `.sav`, for the few games that have it |

**Controls** are the Genesis pad's (see above).

The 32X's picture is laid over the Genesis' line by line, so games that change it partway down the screen show the
change where it happens. The SH-2s' caches are emulated, including the stale data games must clear themselves.

**Not supported yet:** 32X games on Sega CD discs; PAL 32X games' 240-line mode. Each part is tested with programs
written for GameVault, not yet with the real BIOS files, so whether a game boots is the first thing to check.

**Messages:**

| Message | What to do |
|---|---|
| *This is a 32X game. To play it, choose your own 32X BIOS files…* | Choose all three in **Settings → 32X**. |
| *The 32X BIOS files should be 256 bytes…, 2 KB… and 1 KB…* | One of the files is the wrong one. |

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
- **SA-1** (*Super Mario RPG*, *Kirby Super Star*, *Kirby's Dream Land 3*, …), **S-DD1** (*Star Ocean*, *Street
  Fighter Alpha 2*) and **OBC1** (*Metal Combat*): work with nothing extra. SA-1 games save in the cartridge's
  BW-RAM as `.sav`.
- **ST010** (*F1 ROC II*, *Exhaust Heat II*) and **ST011** (*Hayazashi Nidan Morita Shogi*): like the DSP chips, they
  need the chip's program from your own cartridge, named `st010.rom` or `st011.rom` (52 KB), in the same folder.
- **S-RTC** (*Daikaijuu Monogatari II*): the cartridge's clock works with nothing extra. It starts at your PC's
  time whenever you open the game. A time you set inside the game lasts until you close it.
- **SPC7110** (*Far East of Eden Zero*, *Momotarou Dentetsu Happy*, *Super Power League 4*): works with nothing
  extra. *Far East of Eden Zero*'s clock starts at your PC's time, like the S-RTC's.

| Message | What to do |
|---|---|
| *This game needs the DSP-1 chip's program ROM. Put your own dump (dsp1b.rom or dsp1.rom) in the folder chosen in Settings → Super Nintendo → Enhancement chip ROMs. GameVault doesn't include it.* | Add the file and choose its folder, then open the game again. |
| *This game needs the … chip, which GameVault doesn't support yet.* | The chip isn't emulated yet. |

**Not supported yet:** the Cx4 (*Mega Man X2*, *X3*) and ST018 chips, the Super Game Boy and the
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

**Controls** (DualShock):

| PlayStation | Keyboard | Controller (position) | Xbox label |
|---|---|---|---|
| Cross, Circle | <kbd>Z</kbd>, <kbd>X</kbd> | Bottom, right | A, B |
| Square, Triangle | <kbd>A</kbd>, <kbd>S</kbd> | Left, top | X, Y |
| L1, R1 | <kbd>1</kbd>, <kbd>2</kbd> | Left shoulder, right shoulder | LB, RB |
| L2, R2 | <kbd>3</kbd>, <kbd>4</kbd> | Left trigger, right trigger | LT, RT |
| L3, R3 (analog mode) | <kbd>5</kbd>, <kbd>6</kbd> | Click the left stick, click the right stick | LS, RS |
| Select, Start | <kbd>Right Shift</kbd>, <kbd>Enter</kbd> | Back, Start | View, Menu |
| D-pad | <kbd>T</kbd> <kbd>F</kbd> <kbd>G</kbd> <kbd>H</kbd> | D-pad | D-pad |
| Left stick | Arrow keys (full push) | Left stick | Left stick |
| Right stick | <kbd>I</kbd> <kbd>J</kbd> <kbd>K</kbd> <kbd>L</kbd> (full push) | Right stick | Right stick |

**Analog mode:** the controllers are DualShocks. They start digital, and the left stick (and the arrow keys) work the
D-pad. Games that use the sticks switch them to analog themselves. For a game that supports the sticks but waits for
you to press the DualShock's Analog button, turn on **Settings → PlayStation → Analog mode**. Games that rumble shake
your controller, if it has motors.

**Sharper 3D:** **Settings → PlayStation → Internal resolution** draws games at two or three times the console's
resolution on your graphics card. Polygons get smooth edges and distant detail stays readable; 2D games and menus
look the same as at 1×. Videos (and anything else in 24-bit colour) show at the console's own resolution. The game
itself runs exactly as it does at 1×, so save states, rewind, netplay and challenges are unaffected. The dithering
the PlayStation adds to its colours isn't drawn above 1×.

**Light gun and mouse:** for a GunCon game (*Time Crisis*, *Point Blank*), set **Game Details → Properties → Port
1** to **GunCon (the mouse aims)**: point with the mouse, the left button is the trigger, the right button A and the
middle button B. Most GunCon games start with a calibration screen; shoot its centre. For a mouse game (*Lemmings*,
*Discworld*), choose **Mouse**: moving the mouse over the picture moves it, with its left and right buttons.

**Memory cards:** each game gets its own card in slot 1, so you never run out of space. Slot 2 is empty.

Videos and other pictures in 24-bit colour are shown with all their colours.

**Not supported yet:** multitap, the Justifier light gun, link cable, GameShark codes, a memory card manager, and enhanced
(upscaled) graphics.

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

**Sharper 3D:** **Settings → Nintendo 64 → Internal resolution** draws games at two, three or four times the
console's resolution on your graphics card (Direct3D 11). Polygons get smooth edges and textures stay sharp up close;
2D games and menus look the same as at 1×. Pictures a game draws with the processor rather than the graphics chip
show at the console's own resolution. The game itself runs exactly as it does at 1× (and no faster), so save states,
rewind, netplay and challenges are unaffected. The N64's random dither and noise are drawn slightly differently above
1×. The first time you start a game after installing or updating GameVault, your PC prepares the graphics card's
program for a minute or two; the game plays at 1× meanwhile and turns sharp when it's ready.

**Rumble Pak:** turn on **Settings → Nintendo 64 → Rumble Pak** and every controller gets one, so games that use it
shake your controller (if it has rumble motors). Games can't save to the Controller Pak while it's on.

Games that draw in 32-bit colour are shown with all their colours, without the banding of 16-bit colour.

**Not supported yet:** Transfer Pak, mouse, voice recognition unit, 64DD, GameShark codes, and the N64's
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
lean on the PS2's vector units. More speed-ups are planned.

**Sharper 3D:** **Settings → PlayStation 2 → Internal resolution** draws games at two, three or four times the
console's resolution on your graphics card (Direct3D 11). Polygons get smooth edges, and effects games draw into
their own pictures first (glow, shadows, reflections) keep their detail too. Videos and pictures a game sends
straight to the screen show at the console's own resolution. The game itself runs exactly as it does at 1× (and no
faster), so save states and rewind are unaffected. As for the N64, the first start after installing or updating
GameVault plays at 1× for a minute or two while the graphics card's program is prepared.

**Not supported yet:** changing discs in multi-disc games (an `.m3u` starts its first disc), vibration, the multitap,
PlayStation games on the PlayStation 2, USB devices, the network adapter and hard drive, and GameShark codes.

**Messages:**

| Message | What to do |
|---|---|
| *This is a PlayStation 2 disc. To play it, choose your own PlayStation 2 BIOS in Settings → PlayStation 2. GameVault doesn't include it.* | Follow the setup above. |
| *The PlayStation 2 BIOS chosen in Settings → PlayStation 2 isn't a 4 MB BIOS file. Choose the BIOS dumped from your own console.* | Wrong file or bad dump. |
| *GameVault couldn't open your PlayStation 2 BIOS. Check the file chosen in Settings → PlayStation 2.* | The file was moved, deleted or is locked. |
| *That file isn't a PlayStation 2 program (ELF) or disc, or it's damaged.* | Check the file; re-dump the disc if needed. |
| *Netplay isn't available for PlayStation 2 games: its rollback saves and loads the whole console every frame, and a PlayStation 2 is too large for that.* | Play it locally. |

## Atari 2600

| | |
|---|---|
| **Files** | `.a26` (or a `.zip` with one inside) |
| **You supply** | Nothing: the 2600 has no BIOS |
| **Saves** | None: 2600 games don't save. Use save states. |

**Controls** (the joystick):

| Atari 2600 | Keyboard | Controller |
|---|---|---|
| Joystick | Arrow keys | D-pad or left stick |
| Fire | <kbd>Z</kbd> or <kbd>X</kbd> | Bottom or right button |
| Console **Reset** switch | <kbd>Enter</kbd> | Start |
| Console **Select** switch | <kbd>Right Shift</kbd> | Back |

The second player's joystick is the second controller (or the second keyboard player). The difficulty switches stay
on **B** (the easier setting) and the TV switch on colour.

**Cartridges:** 2K and 4K games, and the bank-switching boards most games use: Atari's 8K, 16K and 32K (with or
without the SuperChip's extra RAM), Parker Brothers (E0), Activision (FE), Tigervision (3F) and CBS RAM Plus (FA).
GameVault tells them apart by the file's size and code. If a game shows garbage, set **Game Details → Properties**'
model to the board's name (`F8`, `F8SC`, `F6`, `F6SC`, `F4`, `F4SC`, `E0`, `FE`, `3F` or `FA`).

**Not supported yet:** paddles, keypads and the driving controller; the rarer boards (Pitfall II's DPC, M-Network
E7, the Supercharger); PAL games' own colours (they show NTSC colours).

**Messages:**

| Message | What to do |
|---|---|
| *GameVault doesn't know how a … KB Atari 2600 cartridge switches its banks.* | The file isn't a 2600 image GameVault knows, or it's a rarer board. |
| *That isn't an Atari 2600 cartridge image.* | Wrong file. |

## Atari 5200

| | |
|---|---|
| **Files** | `.a52` (or a `.zip` with one inside) |
| **You supply** | The 5200's BIOS, `5200.rom` (2 KB), from your own console: **Settings → Atari 5200** |
| **Saves** | None: 5200 games don't save. Use save states. |

**Controls** (the 5200 controller: an analog stick, two fire buttons and a keypad):

| Atari 5200 | Keyboard | Controller |
|---|---|---|
| Stick | Arrow keys (pushed fully) | Left stick (analog), or the D-pad |
| Bottom fire button | <kbd>Z</kbd> | Bottom button |
| Top fire button | <kbd>X</kbd> | Right button |
| **Start** | <kbd>Enter</kbd> | Start |
| **Pause** | <kbd>Right Shift</kbd> | Back |
| Keypad **0**–**9** | <kbd>0</kbd>–<kbd>9</kbd> on the number row | — |
| Keypad **\*** and **#** | <kbd>-</kbd> and <kbd>=</kbd> | — |
| Keypad **Reset** | <kbd>R</kbd> | — |

While a 5200 game runs, the number row is controller 1's keypad, so <kbd>1</kbd>–<kbd>4</kbd> don't do what they do
for other systems. Controllers 2–4 are the second to fourth controllers; their keypads aren't on the keyboard.

**Cartridges:** 4K to 32K games. A few 16K games are built from two 8K chips; if one of them shows garbage, set
**Game Details → Properties**' model to `two-chip`.

**Not supported yet:** *Bounty Bob Strikes Back!* (its 40K board), the trak-ball, PAL 5200s, and light pens. The
keypad's codes follow published hardware notes and haven't been checked against a real console.

**Messages:**

| Message | What to do |
|---|---|
| *This is an Atari 5200 game. To play it, choose your own Atari 5200 BIOS…* | Choose `5200.rom` in **Settings → Atari 5200**. |
| *The Atari 5200 BIOS file should be 2 KB.* | The file chosen isn't the 5200 BIOS. |
| *That isn't an Atari 5200 cartridge image.* | Wrong file, or one of a size the 5200 doesn't use. |

## Nintendo GameCube

| | |
|---|---|
| **Files** | `.dol` and `.elf` homebrew programs (built with devkitPPC and libogc, for example) |
| **You supply** | Nothing for homebrew |
| **Saves** | None yet (memory cards come later). Use save states. |

This is the GameCube's first milestone: the CPU, the video output from the program's own frame buffer, the
controllers, and the system parts a homebrew program needs to start. Programs that draw with the GameCube's GPU show
a black screen for now, and discs (`.iso`, `.gcm`, `.rvz`) can't be opened yet. The CPU is interpreted, so programs run
at about an eighth of full speed until its recompiler arrives.

**Controls** (the GameCube controller, on the PlayStation 2's layout):

| GameCube | Controller | Keyboard |
|---|---|---|
| Control stick | Left stick | Arrow keys |
| C-stick | Right stick | <kbd>I</kbd>, <kbd>J</kbd>, <kbd>K</kbd>, <kbd>L</kbd> |
| A, B, X, Y | Bottom, left, right, top | <kbd>X</kbd>, <kbd>Z</kbd> and the others as for the PlayStation 2 |
| Z | Right shoulder | <kbd>2</kbd> |
| L and R | Left and right triggers | <kbd>3</kbd> and <kbd>4</kbd> |
| Start | Start | <kbd>Enter</kbd> |
| D-pad | D-pad | <kbd>T</kbd>, <kbd>F</kbd>, <kbd>G</kbd>, <kbd>H</kbd> |

**Messages:**

| Message | What to do |
|---|---|
| *That isn't a GameCube program (a .dol or a PowerPC .elf).* | Wrong file, or a disc image (not supported yet). |
