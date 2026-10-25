# Password definitions

SPEC §7.3. A password definition tells GameVault how to recognize a game's password screen and how to read
the password from it. When a definition matches, the password is saved automatically, with a screenshot,
in **Game Details → Passwords**. For games without a definition, players press **F9** while the password is
on screen and type it in.

Definitions are JSON files in `data/gamedb/passwords/`, one per game, keyed by the game's **headerless
SHA-1** (the hash No-Intro DATs list). They contain memory addresses and character tables only, never ROM
data, graphics or text from the game.

## Format

```jsonc
{
  "sha1": "0123456789abcdef0123456789abcdef01234567",
  "title": "Example Quest (USA)",

  // The password screen: every condition must hold for `stableFrames` frames in a row.
  "detect": [
    { "address": "0x0010", "value": "0x07" },              // RAM[$0010] == $07
    { "address": "$0011", "mask": "0x80", "value": 128 }   // bit 7 of RAM[$0011] set
  ],
  "stableFrames": 30,

  // Where the characters are.
  "source": { "from": "ram", "address": "0x0500", "length": 24 },

  // Byte value → character. A string maps value 0 to its first character, and so on.
  "alphabet": "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!?",

  // Optional display grouping: "ABCDEF GHIJKL MNOPQR STUVWX".
  "groups": [6, 6, 6, 6]
}
```

### Fields

| Field | Required | Meaning |
|---|---|---|
| `sha1` | yes | Headerless SHA-1 of the game's ROM, lowercase or uppercase. |
| `title` | no | For people reading the file. |
| `detect` | yes | A list of `{ address, mask?, value }`. Addresses are CPU addresses: RAM `$0000-$07FF` or cartridge RAM `$6000-$7FFF`. `mask` defaults to `0xFF`. |
| `stableFrames` | no | How many frames the conditions must hold before reading (default 30, half a second). This avoids reading while the screen is still being drawn. |
| `source.from` | yes | `ram` (one byte per character) or `nametable` (one background tile per character). |
| `source.address` | yes | For `ram`, the CPU address of the first character. For `nametable`, the PPU address ($2000-$2FFF) of the first tile. |
| `source.length` | yes | Number of characters. |
| `source.stride` | no | `ram` only: bytes between characters (default 1). |
| `source.columns` | no | `nametable` only: characters per row before moving down (default: all on one row). |
| `source.rowStride` | no | `nametable` only: bytes between rows (32 for consecutive tile rows, 64 to skip one). |
| `alphabet` | yes | Either a string (value N is the Nth character) or an object of `"0x41": "A"` pairs for tile-based screens. Values that aren't listed read as `?`. |
| `groups` | no | Group sizes for display. |

Numbers can be written as JSON numbers, as `"0x1F"` or as `"$1F"`.

## How capture behaves
- Detection runs after every frame and reads only the listed bytes, so it costs nothing measurable.
- Each time the screen appears the password is read once. It is saved unless it equals the newest saved
  password for that game, so revisiting the same screen doesn't create duplicates.
- Reading never changes the game: RAM and nametables are read without side effects, so run-ahead,
  rewind and save states behave the same with or without a definition.

## Writing a definition
1. Use a debugger with a RAM viewer (for example Mesen's) on **your own** copy of the game.
2. Find a byte, or a few, that has a distinctive value only while the password screen is up. Use them
   as `detect`.
3. Find where the password lives: either a RAM buffer (`ram`) or the background tiles that draw it
   (`nametable`). Build the alphabet from the values you see for known characters.
4. Get the headerless SHA-1 from GameVault (Game Details shows the file; the library stores the hash).
   Alternatively, use any tool that skips the 16-byte iNES header.
5. Put the file in `data/gamedb/passwords/` and run the definition tests.
   `tests/GameVault.Library.Tests/PasswordTests.cs` shows how to test a definition against recorded memory.

## Launch titles
SPEC §7.3 asks for definitions for 5–10 well-known password games, and Phase 6 is done when capture works
for 5 of them. Choosing them and checking each against a real cartridge needs the project owner, because
the repository can't contain the games. The checklist is in [`plans/phase-6.md`](plans/phase-6.md).
