# Memory maps

SPEC §7.9. A memory map tells GameVault where a game keeps facts in RAM: lives, the current room, health. With
one, GameVault can:
- go back to **before you lost a life** (F2);
- draw an **explored map** in the game's journal;
- show a **stat overlay** with values the game doesn't print;
- check **challenge goals**.

Memory maps are JSON files in `data/gamedb/memory/`, one per game, keyed by the game's **headerless SHA-1**
(the hash No-Intro DATs list). They contain addresses and names only, never ROM data or graphics.

## Format

```jsonc
{
  "sha1": "0123456789abcdef0123456789abcdef01234567",
  "title": "Example Quest (USA)",

  // Named values in RAM.
  "values": {
    "lives":  { "address": "0x075A", "offset": 1 },                  // shown = RAM + 1
    "score":  { "address": "$07DD", "size": 2, "encoding": "bcd" },  // 2 bytes, low byte first, BCD
    "health": { "address": "0x0070", "mask": "0xF0", "shift": 4, "max": 16 },
    "room":   { "address": "0x00EB" },
    "dungeon":{ "address": "0x0010" },
    "mode":   { "address": "0x0012" },
    "bossHp": { "address": "0x0485" }
  },

  // Real gameplay (not the title screen, demo or a menu): all must hold.
  "playing": [ { "value": "mode", "op": "==", "target": 5 } ],

  // Which value is the lives counter ("rewind to before I died").
  "lives": "lives",

  // Automatic maps: room N goes to column N % columns, row N / columns of its area's map.
  "map": { "room": "room", "area": "dungeon", "columns": 16, "areaNames": { "0": "Overworld", "1": "Level 1" } },

  // The stat overlay.
  "overlay": [
    { "value": "health", "label": "Health" },
    { "value": "bossHp", "label": "Boss", "when": [ { "value": "bossHp", "op": ">", "target": 0 } ] }
  ]
}
```

### Values

| Field | Required | Meaning |
|---|---|---|
| `address` | yes | An address in the system's memory as GameVault reads it (`IEmulatedConsole.PeekMemory`). On the NES: RAM `$0000-$07FF` or cartridge RAM `$6000-$7FFF`. |
| `size` | no | 1 (default) or 2 bytes, low byte first. |
| `encoding` | no | `binary` (default) or `bcd` (each 4 bits is a decimal digit). |
| `mask`, `shift` | no | Keep only some bits, then shift right: `(raw & mask) >> shift`. |
| `offset` | no | Added to the result (games often store "lives − 1"). |
| `max` | no | Shown in the overlay as "3 of 16". |

### Conditions

Conditions are used by `playing` and overlay `when`, and by challenge goals.
- **Named values:** `{ "value": "<name>", "op": "<=", "target": 2 }`. The operators are `==`, `!=`, `<`,
  `<=`, `>` and `>=`.
- **Raw bytes:** `{ "address": "0x0010", "mask": "0xFF", "op": "==", "target": 7 }`. As in password
  definitions, `value` can stand in for `target`.

Numbers can be JSON numbers, `"0x1F"` or `"$1F"`.

## How GameVault uses it
- **Cost:** the map is read after every frame the player sees, never during rewind or run-ahead's hidden
  frames. Only the listed bytes are read, so it costs nothing measurable.
- **Lives:** a drop while `playing` holds saves the state from about 5 seconds earlier to the timeline as
  "Before you lost a life". The newest five are kept, and F2 loads the newest. Loading a state or rewinding
  never counts as losing a life.
- **Maps:** the first visit to each room (per area) keeps a small picture of the screen. The journal's
  "Explored map" image is redrawn whenever a new room appears.
- **Overlay:** the values are refreshed about 6 times a second. The overlay is drawn over the picture, so it
  never appears in screenshots.

## Writing a memory map
1. Use a debugger with a RAM viewer (for example Mesen's) on **your own** copy of the game.
2. Find each value by watching memory while it changes in the game (lose a life, change rooms, take a hit).
3. Find something that tells gameplay apart from the title screen, demo and menus, and use it for `playing`.
4. Get the headerless SHA-1 from GameVault (the library stores it). For the NES, any tool that skips the 16-byte
   iNES header gives the same hash; other systems follow `RomHasher`'s rules (see `data/gamedb/README.md`).
5. Put the file in `data/gamedb/memory/` and run the tests. `tests/GameVault.Library.Tests/MemoryMapTests.cs`
   shows the format in use.

## Launch games
Memory maps for real games need checking against real copies, which this repository can't contain.
Choosing and checking the first ones is on the owner checklist in [`plans/phase-9.md`](plans/phase-9.md).
