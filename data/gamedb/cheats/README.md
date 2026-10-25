# Bundled code suggestions

SPEC §7.8. Each file here offers Game Genie and RAM codes for one game. GameVault shows them as
**Suggested codes** under **Codes** on Game Details and on the Player. A suggestion is only used once the player adds it.

```jsonc
{
  "sha1": "0123456789abcdef0123456789abcdef01234567", // headerless SHA-1 (what No-Intro lists)
  "title": "Example Quest (USA)",                     // for people reading the file
  "codes": [
    { "code": "SXIOPO", "description": "Infinite lives" },  // 6 or 8 Game Genie letters
    { "code": "075A:09", "description": "Start with 9 lives" } // RAM code: AAAA:VV or AAAA?CC:VV
  ]
}
```

## What may go here

Published codebooks, and most code websites, are copyrighted collections, so their text is **never copied
here**. A file can only be added when its source allows redistribution, and every source must be listed
with its license in [`SOURCES.md`](SOURCES.md).

This folder holds no codes yet. Players get codes in three other ways, none of which puts third-party codes
into this repository (see [`SOURCES.md`](SOURCES.md)):
- **GameGenie.com:** downloaded on request, and always shown with credit to the site.
- **Code collections:** spreadsheets or .csv files the player imports in Settings → Codes.
- **Their own:** typed in, or imported as plain text or FCEUX `.cht` files.
