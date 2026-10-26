# N64 save types

Phase 16. N64 cartridges don't say which save chip they carry, so `saves.json` maps a game's two-letter id (the
middle of the four-character game code in the ROM header: `SM` in `NSME`) to its save memory:

| Value | Chip |
|---|---|
| `eeprom4k` | 4 Kbit EEPROM (512 bytes) |
| `eeprom16k` | 16 Kbit EEPROM (2 KB) |
| `sram` | 32 KB battery-backed SRAM |
| `flash` | 128 KB FlashRAM |
| `none` | No save memory |

GameVault picks the save type in this order: the `"save"` field of the game's `data/gamedb` profile, then libdragon's
homebrew header, then this list, and otherwise a 4 Kbit EEPROM. Players can't choose it in Game Details yet.

The entries are metadata only and are **still to be checked by the owner** against their own cartridges (see the
owner checklist in `docs/plans/phase-16.md`). The loader is `src/GameVault.Library/Profiles/N64SaveTypes.cs`.
