# Game database (per-game profiles)

SPEC §7.5. Each `*.json` file here holds one profile, or an array of profiles, keyed by the game's
**headerless SHA-1**: the hash of the ROM without its 16-byte iNES header and 512-byte trainer, which is
what No-Intro DATs list. GameVault applies a profile automatically when that game starts. The player can
see and override every setting in **Game Details → Properties**, and their choices win.

Profiles hold metadata only. Never add ROM data, box art or copyrighted text.

```json
{
  "sha1": "0123456789abcdef0123456789abcdef01234567",
  "title": "Example Game (USA)",
  "header": { "mapper": 4, "submapper": 0, "mirroring": "vertical", "battery": true, "prgRamSize": 8192 },
  "input": "Zapper",
  "runAheadSafe": true,
  "removeSpriteLimit": false,
  "region": "ntsc",
  "passwordDefinition": "example-game.json"
}
```

| Field | Meaning |
|---|---|
| `header` | Corrections for a wrong or missing iNES header. Fields left out keep the header's value. `mirroring` is `horizontal`, `vertical` or `four-screen`. |
| `input` | `Controller`, `Zapper` (the mouse aims and clicks) or `FourScore`. |
| `runAheadSafe` | `false` turns run-ahead off for games it breaks. `true` marks the game as safe for it. |
| `removeSpriteLimit` | `true` draws more than 8 sprites per line, which removes flicker. |
| `region` | `ntsc`, `pal` or `dendy`. |
| `passwordDefinition` | A file in `passwords/` for password capture (Phase 6). |

Files that fail to parse are skipped and don't stop the app. The loader is
`src/GameVault.Library/Profiles/GameProfiles.cs` and its tests are in `tests/GameVault.Library.Tests`.
