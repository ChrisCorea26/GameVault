# Game database (per-game profiles)

SPEC §7.5. Each `*.json` file here holds one profile, or an array of profiles, keyed by the game's
**headerless SHA-1**, which is what No-Intro and Redump DATs list: for the NES, the ROM without its 16-byte iNES
header and 512-byte trainer; SMS, SG-1000 and SNES drop a 512-byte copier header, Genesis `.smd` files are
de-interleaved, N64 ROMs are byte-swapped to big-endian, and a PlayStation disc hashes its first track
(`src/GameVault.Library/Hashing/RomHasher.cs`). GameVault applies a profile automatically when that game starts. In **Game Details →
Properties** the player can override the controls (`input`), run-ahead (`runAheadSafe`) and sprite-limit
(`removeSpriteLimit`) settings, and their choices win. The other fields can only be set here.

Profiles hold metadata only. Never add ROM data, box art or copyrighted text.

```json
{
  "sha1": "0123456789abcdef0123456789abcdef01234567",
  "title": "Example Game (USA)",
  "header": { "mapper": 4, "submapper": 0, "mirroring": "vertical", "battery": true, "prgRamSize": 8192 },
  "input": "Zapper",
  "runAheadSafe": true,
  "removeSpriteLimit": false,
  "region": "ntsc"
}
```

| Field | Meaning |
|---|---|
| `header` | Corrections for a wrong or missing iNES header. Fields left out keep the header's value. `mirroring` is `horizontal`, `vertical` or `four-screen`. |
| `input` | `Controller`, `Zapper` (the mouse aims and clicks) or `FourScore`. |
| `runAheadSafe` | `false` turns run-ahead off for games it breaks. `true` marks the game as safe for it. |
| `removeSpriteLimit` | `true` draws more than 8 sprites per line, which removes flicker. |
| `region` | NES: `ntsc`, `pal` or `dendy`. Genesis: `usa` (or `ntsc`), `japan`, or `europe` (or `pal`). SNES: `pal`/`europe` or `ntsc`/`usa`/`japan`. SG-1000 and Master System: `pal` (anything else is NTSC). |
| `model` | Apple II: `ii+` (or `iiplus`), `iie` or `iie-enhanced`. Without it a 16 KB ROM runs an enhanced IIe and a 12 KB ROM a II+. |
| `pad` | Genesis: `3` for the 3-button pad; otherwise the 6-button pad. |
| `save` | N64: `eeprom4k`, `eeprom16k`, `sram`, `flash` or `none` (see `n64/README.md`). |
| `passwordDefinition` | Not used: password definitions are found by the `sha1` inside the files in `passwords/` (`PasswordDefinitions.For`). |

Files that fail to parse are skipped and don't stop the app. The loader is
`src/GameVault.Library/Profiles/GameProfiles.cs` and its tests are in `tests/GameVault.Library.Tests`.
