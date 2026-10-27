# The Library

The Library is GameVault's home screen. It shows the games in your folders, and, once you've imported a
catalog, a complete checklist of every known release with the ones you own highlighted.

## Adding games

**Add a ROM folder** (<kbd>Alt</kbd>+<kbd>A</kbd>) opens a folder picker. GameVault searches the folder and
all its subfolders for every supported file type (see [Supported systems](#supported-systems)), hashes each
game, and matches it against your catalog. The status line shows *Looking for games…*, then
*Checking N of M files…*, then *Found N games*.

- Add as many folders as you like. Manage the list in **Settings → Library**, where **Look for new games**
  rescans every folder.
- While GameVault is open it watches your folders, so files you add or delete appear and disappear
  automatically.
- A `.zip` file is read if it contains exactly one game (not a PlayStation disc).
- Games that aren't in your catalog are listed as **Unknown / Homebrew**. You can give them a title, publisher,
  developer, year, genre, region and number of players in Game Details, so they can be filtered and grouped like
  any other game.

**Open game file** (<kbd>Alt</kbd>+<kbd>O</kbd>) plays a single file straight away, without adding its folder.

### Bundled homebrew

GameVault comes with four freely licensed NES homebrew games by Damian Yerrick. On first launch it adds their folder
(`data\homebrew` in the GameVault folder) to your library once, with their names and details filled in.

| Game | What it is | Players | License |
|---|---|---|---|
| **Thwaite** | Defend the villages from incoming missiles | 1–2 | GPL 3 or later (its source is included) |
| **Concentration Room** | The memory card game | 1–2, or against the computer | GPL 3 or later |
| **RHDE: Furniture Fight** | Build and furnish your house, then defend it | 1–2 | GNU All-Permissive |
| **robotfindskitten** | Look through the room's odd objects until you find kitten | 1 | zlib |

Each game's folder has its `LICENSE` and a `README.md` saying where it came from. If you don't want them, remove the
folder in **Settings → Library**; GameVault doesn't add it again.

## Browsing

| Control | What it does |
|---|---|
| **Search** (<kbd>Alt</kbd>+<kbd>S</kbd>) | Matches title, publisher or genre. |
| **All games / Owned only** | Show only games you've marked as owned. |
| **My games / Complete checklist** | *My games* shows games you have a file for or own. *Complete checklist* shows every game in your catalog. |
| **System** | Appears when your library has more than one system. |
| **Region, Genre, Publisher, Year, Status** | Filters. Each starts at *Any*. |
| **List view / grid** (<kbd>Alt</kbd>+<kbd>V</kbd>) | Switches between cover tiles and a compact list that also shows playtime. |

The bar at the top shows your collection progress, for example *214 / 677 licensed releases owned*. Only
licensed releases in your catalog are counted, so this appears after you import a catalog.

Click a game to open its details. Right-click a tile and choose **Play** to start it directly.

## Importing a catalog

A catalog is a list of every known release of a system. It lets GameVault name your games correctly and show
a complete checklist. GameVault does **not** ship one: you download it yourself (see
[What you must supply](#what-you-must-supply)).

1. Choose **Import catalog** (<kbd>Alt</kbd>+<kbd>I</kbd>).
2. Choose **Choose a DAT file…** and pick a `.dat` or `.xml` file in the Logiqx XML format, such as a
   No-Intro DAT for the NES, SG-1000, Master System, Apple II, Genesis, Super Nintendo or Nintendo 64, or a Redump DAT
   for the PlayStation or PlayStation 2. A file that lists no games GameVault plays shows *That file doesn't list any
   games GameVault plays.*
3. Check the preview, for example *"Nintendo - NES" lists 3,412 games (1,980 licensed releases).*, and
   choose **Import**.
4. Games in your folders that match are now named and grouped.

Import one catalog per system. GameVault recognises the system from the DAT's name (for example *Sega - Mega Drive -
Genesis* or *Sony - PlayStation*); DATs it doesn't recognise are treated as NES. Importing a newer version of the same
DAT replaces the old entries.

| System | Where to get a catalog |
|---|---|
| NES, Master System, SG-1000, Genesis, SNES, N64, Apple II, Atari 2600, Atari 5200 | No-Intro DATs (Logiqx XML), from the No-Intro DAT-o-MATIC site |
| PlayStation | Redump DATs (Logiqx XML), from the Redump site. Discs are matched on their first track. |
| PlayStation 2 | Redump DATs (*Sony - PlayStation 2*). DVDs are matched on the whole image, CDs on their first track. |

The older text-based clrmamepro DAT format isn't supported; choose the XML download.

> **Note:** A catalog holds names, hashes and release details only. It never contains games.

## Game Details

Game Details is the page for a single game.

### Cover, title and Play

- **Choose a picture…** sets your own cover (`.png` or `.jpg`), for example a photo of your cartridge.
  GameVault never downloads box art. **Remove** clears it.
- Games that aren't in the catalog let you edit the title, and **Edit details** opens boxes for **Publisher**,
  **Developer**, **Year**, **Genre**, **Region** and **Players**. They save when you leave each box.
- The details line shows region, number of players, whether the game is a licensed release, and the developer.
- Playtime and the last-played date are tracked automatically.
- **Play** (<kbd>Alt</kbd>+<kbd>P</kbd>) starts the game. It's disabled when GameVault has no file for the
  game, for example a checklist entry you own only on cartridge.

### My collection

Every change saves immediately.

- **Cartridge**, **Box**, **Manual**, **Digital copy**: what you own, as separate check boxes.
- **Status**: Unplayed, Playing, Beaten or 100%.
- **Favorite** and **My rating** (1 to 5 stars).
- **Notes**: free text, for tips or where you bought the cartridge.

### Save states

Your save states for this game, newest last. Click one to start the game from that point. Right-click a state,
or use its **…** button, to **Load**, **Pin**, rename (**Name**, then **Save name**) or **Delete** it. Deleting
shows an **Undo** button. See [Save states and the timeline](#save-states-and-the-timeline).

### Properties

Per-game overrides. *Automatic* uses what GameVault's game database knows about the game. Changes apply the next
time the game starts. Only the properties that apply to the game's system are shown.

| Property | Systems | Values |
|---|---|---|
| **Controls** | NES | Automatic, Controller, Zapper, Four Score, Power Pad (keyboard), Arkanoid paddle (mouse) |
| **Port 1** | PlayStation | Automatic, DualShock, GunCon (the mouse aims), Mouse |
| **Reduce input lag** | All | Automatic, On, Off |
| **Reduce flicker (more sprites per line)** | NES | Automatic, On, Off |
| **Region (speed)** | NES | Automatic, NTSC (60 Hz), PAL (50 Hz), Dendy (50 Hz) |
| | Genesis | Automatic, USA (60 Hz), Japan (60 Hz), Europe (50 Hz) |
| | Super Nintendo | Automatic, NTSC (60 Hz), PAL (50 Hz) |
| | SG-1000, Master System | Automatic (NTSC, 60 Hz), NTSC (60 Hz), PAL (50 Hz) |
| **Apple II model** | Apple II | Automatic, Apple II+, Apple IIe, Enhanced Apple IIe |
| **Controller** | Genesis | Automatic (6-button), 6-button, 3-button (a few older games misread the 6-button pad) |
| **Save type** | Nintendo 64 | Automatic, EEPROM (4 Kbit), EEPROM (16 Kbit), SRAM, Flash, None |
| **Header corrections** | NES | **Mapper** (a number, or empty to use the file's), **Mirroring** (Automatic, Horizontal, Vertical, Four-screen), **Battery-backed save** (Automatic, On, Off) |

Use **Region** when a European game runs too fast or a US game too slow, **Save type** when an N64 game can't save,
and **Header corrections** only when an NES file shows garbage or won't start because its header is wrong. A mapper
that isn't a number from 0 to 4095 shows *The mapper is a number from 0 to 4095, or leave it empty to use the
file's.*

GameVault's database sets **Controls** automatically for the NES's Zapper, Four Score, Power Pad and Arkanoid games
(about 65 releases), so for those you usually don't need to change anything.

### Passwords, Journal and Codes

- **Passwords**: passwords you captured with <kbd>F9</kbd> or that GameVault saved automatically, with
  **Copy** and **Delete**. See [Passwords](#passwords).
- **Journal**: notes, screenshots (<kbd>F12</kbd>) and maps. See [Journal and maps](#journal-and-maps).
- **Codes**: Game Genie and RAM codes, shown for NES games only. See [Game Genie and RAM codes](#game-genie-and-ram-codes).

Deleting a password, a code, a note or a picture asks first (*Delete this password?* and so on); choose **Delete**
to go ahead or **Cancel** to keep it.

## Dumping a cartridge

If you own an **INL Retro-Prog** USB cartridge dumper, GameVault can dump NES cartridges straight into your
library. The dumper is optional. See [Cartridge dumper](#cartridge-dumper-inl-retro-prog) for setup.

1. Choose **Dump cartridge** (<kbd>Alt</kbd>+<kbd>D</kbd>).
2. Search the catalog for the game (**Which game is it?**), or type its name if it isn't listed.
3. Pick the **Board**. **Program size (KB)** and **Graphics size (KB)** are filled in for you; change them only
   if you know your cartridge differs.
4. Choose **Next**, insert the cartridge label-facing-you, and choose **Dump**.
5. GameVault reads the cartridge up to 3 times to check the reads match, then checks the dump against the
   catalog. A good dump is saved as `<Title>.nes` in your first library folder (or `Documents\GameVault Dumps`),
   added to your library, and marked as owned (cartridge and digital copy).

If a read fails, clean the cartridge contacts and try again.
