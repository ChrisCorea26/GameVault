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
- Games that aren't in your catalog are listed as **Unknown / Homebrew**. You can type their title in
  Game Details.

**Open game file** (<kbd>Alt</kbd>+<kbd>O</kbd>) plays a single file straight away, without adding its folder.

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
   No-Intro DAT.
3. Check the preview, for example *"Nintendo - NES" lists 3,412 games (1,980 licensed releases).*, and
   choose **Import**.
4. Games in your folders that match are now named and grouped.

Import one catalog per system. GameVault recognises the system from the DAT's name (for example *Sega - Mega Drive -
Genesis* or *Sony - PlayStation*); DATs it doesn't recognise are treated as NES. Importing a newer version of the same
DAT replaces the old entries.

| System | Where to get a catalog |
|---|---|
| NES, Master System, SG-1000, Genesis, SNES, N64, Apple II | No-Intro DATs (Logiqx XML), from the No-Intro DAT-o-MATIC site |
| PlayStation | Redump DATs (Logiqx XML), from the Redump site. Discs are matched on their first track. |
| PlayStation 2 | Redump DATs (*Sony - PlayStation 2*). DVDs are matched on the whole image, CDs on their first track. |

The older text-based clrmamepro DAT format isn't supported; choose the XML download.

> **Note:** A catalog holds names, hashes and release details only. It never contains games.

## Game Details

Game Details is the page for a single game.

### Cover, title and Play

- **Choose a picture…** sets your own cover (`.png` or `.jpg`), for example a photo of your cartridge.
  GameVault never downloads box art. **Remove** clears it.
- Games that aren't in the catalog let you edit the title.
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
time the game starts.

| Property | Values |
|---|---|
| **Controls** | Automatic, Controller, Zapper, Four Score (NES only) |
| **Reduce input lag** | Automatic, On, Off |
| **Reduce flicker (more sprites per line)** | Automatic, On, Off (NES only) |

### Passwords, Journal and Codes

- **Passwords**: passwords you captured with <kbd>F9</kbd> or that GameVault saved automatically, with
  **Copy** and **Delete**. See [Passwords](#passwords).
- **Journal**: notes, screenshots (<kbd>F12</kbd>) and maps. See [Journal and maps](#journal-and-maps).
- **Codes**: Game Genie and RAM codes for NES games. See [Game Genie and RAM codes](#game-genie-and-ram-codes).

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
