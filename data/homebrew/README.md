# Bundled homebrew

SPEC §6.4: GameVault ships a few freely licensed homebrew games, so a new player has something to play on first
launch. Each folder holds the game exactly as its author published it, its `LICENSE`, a `README.md` with where it came
from and the author's notice, and a `game.json` with the details the library shows. On first launch GameVault adds this
folder to the library once; the player can remove it in Settings → Library like any other folder.

| Folder | Game | Author | License | Board |
|---|---|---|---|---|
| `thwaite` | Thwaite | Damian Yerrick | GPL-3.0-or-later (source included) | NROM |
| `concentration-room` | Concentration Room | Damian Yerrick | GPL-3.0-or-later with an exception for exact copies | NROM |
| `rhde` | RHDE: Furniture Fight | Damian Yerrick | GNU All-Permissive | NROM (CHR RAM) |
| `robotfindskitten` | robotfindskitten | Damian Yerrick | zlib | NROM (CHR RAM) |

## Adding a game
Only games whose authors allow anyone to redistribute them, under a license that says so in writing, belong here. Add a
folder with the unmodified game file, the license text as `LICENSE`, a `README.md` like the others, and a `game.json`:

```json
{ "file": "game.nes", "title": "Game", "developer": "Author", "year": 2024, "genre": "Puzzle", "players": 1, "region": "World", "license": "MIT" }
```

`tests/GameVault.Library.Tests` checks every folder has a license and a game file the library can read.
