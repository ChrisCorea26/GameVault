# Phase 6 — Signature features

**Goal (SPEC §12):** Password capture, journal, assist/practice modes, Tier 2–3 mappers, dumper integration.

**Done when:** password capture works for the 5 launch titles, and the dumper flow is verified on hardware.

> **Needs the owner.** Both "done when" checks require things this repository must not contain and the
> cloud container doesn't have: commercial ROMs of the launch titles, and an INL Retro-Prog with a
> cartridge. Everything below is built and tested with synthetic ROMs and a simulated dumper. The final
> verification is a manual checklist the owner runs, at the end of this file.

## Core: Tier 2–3 mappers (SPEC §4.5), each with unit tests and a state round-trip

| Mapper | Board | Notes |
|---|---|---|
| 9 / 10 | MMC2 / MMC4 | CHR latches switched by PPU fetches of tiles $FD/$FE |
| 11 | Color Dreams | |
| 66 | GxROM | |
| 71 | Camerica / Codemasters | Including Fire Hawk's one-screen mirroring (submapper 1) |
| 34 | BNROM / NINA-001 | Told apart by CHR size and submapper |
| 206 | DxROM / Namco 108 | MMC3 subset without IRQ or mirroring control |
| 69 | Sunsoft FME-7 | CPU-cycle IRQ. Sunsoft 5B audio through `IExpansionAudio` (3 square channels) |
| 24 / 26 | Konami VRC6 | Scanline IRQ (prescaler) and VRC6 audio (2 pulse + sawtooth) |
| 21 / 22 / 23 / 25 | Konami VRC2 / VRC4 | Address-line variants by submapper, VRC IRQ |
| 118 / 119 | TxSROM / TQROM | MMC3 variants: CHR A17 mirroring, CHR-RAM/ROM mix |
| 79 | NINA-03 / NINA-06 | |
| 87 | Jaleco / Konami CHR | |
| 140 | Jaleco JF-11/14 | |
| 19 | Namco 163 | CPU-cycle IRQ, internal RAM, N163 wavetable audio (1–8 channels) |
| 85 | Konami VRC7 | Banking and IRQ; the OPLL FM audio is a **stretch** (Phase 7, "expansion audio completion") |
| 5 | MMC5 | PRG/CHR modes, ExRAM modes 0–3, fill mode, split screen, scanline IRQ, multiplier, MMC5 pulse/PCM audio |

Shared pieces:
- **`VrcIrq`** (the cycle/scanline prescaler used by VRC4/6/7).
- **Expansion audio mixing** in `Apu2A03`: each `IExpansionAudio` contributes amplitude changes at CPU-cycle
  timestamps, scaled per chip (NESdev Wiki levels).

## Library / Platform: signature features

- **Passwords (SPEC §7.3)**
  - `PasswordDefinition` JSON, documented in `docs/password-definitions.md`:
    - Detection: RAM conditions (address / mask / value, all must hold for N frames).
    - Decoding: RAM bytes or nametable tiles to characters through an alphabet table.
    - Length and grouping.
  - `PasswordCapture` runs on the emulation thread every frame (cheap: only the condition bytes are read)
    and raises a capture with a thumbnail.
  - Store: `Passwords/<game>/index.json` plus PNGs. The Passwords panel lists them per game with a copy
    button.
  - **Manual fallback**: the "Capture Password" hotkey takes a screenshot and opens a dialog to type the
    password.
  - **Launch definitions**: the format and engine are tested with synthetic ROMs. The 5 launch titles' RAM
    layouts need the owner's choice of titles and a check against real cartridges (see below).
- **Journal (SPEC §7.4)**
  - Per-game notes as rich text (RTF from `RichEditBox`, stored as a file per note).
  - F12 screenshots saved to `Screenshots/<game>/` as PNG.
  - Imported map images, and a one-click "attach screenshot to note".
  - Journal tab on Game Details.
- **Assist mode (SPEC §7.6)**, a per-session toggle on the Player, never forced:
  - Slow motion at 50% and 25% (pacing divider; audio pitch drops with it).
  - Unlimited rewind (a cap of 4 GB, or the system limit).
  - Turbo buttons (already on the pad mapping; exposed as toggles).
  - Flicker removal (sprite limit off).
  - Colorblind-friendly palettes:
    - Deuteranopia / protanopia / tritanopia variants of the NTSC palette, using Machado 2009 simulation
      matrices to remap hues.
    - Generated at build time, not shipped as binaries.
- **Practice mode (SPEC §7.6)**
  - Pin a practice point (a state), then retry it instantly with one button (R, or Select+Back on a pad).
  - Segment timer with splits, in frames for precision and shown as time. Personal bests per segment are
    stored per game.
- **Four Score in the input UI** (deferred from Phase 5): Settings → Input shows four ports while a Four
  Score game runs; port assignment extends to 4.
- **Dumper (SPEC §8)**, in a new project `GameVault.Dumper`:
  - `IDumper` interface.
  - `InlRetroDumper` shells out to the INL Retro-Prog CLI (`inlretro`) with the board script and sizes for
    the chosen mapper.
  - Retries up to 3 times, comparing hashes.
  - Plain-language errors ("Clean the cartridge contacts and try again").
  - The **Dump Cartridge wizard**:
    1. Pick the game, searching the catalog, which pre-fills the board.
    2. Insert the cartridge.
    3. Dump.
    4. Verify the hash against the catalog.
    5. Save to the library folder and mark "Owned: Cartridge + Digital dump".
  - A simulated dumper backs `--sample-data` and the tests. The app works fully without a dumper.

## Tests
- **Mappers:** per-mapper banking, mirroring and IRQ tests; state round-trips for every new mapper;
  expansion-audio channel tests (period, volume and mixing level).
- **Downloaded test ROMs where redistributable mirrors exist:** `mmc5test`, `exram` and similar
  MMC5/VRC behavior ROMs in the pinned mirror, plus `MMC1_A12`-style manual ROMs listed as informational.
- **Passwords:**
  - The definition parser.
  - Detection timing and decoding from RAM and from nametable tiles, using a synthetic ROM that draws a
    "password screen".
  - The store and the copy action (ViewModel).
- **Journal:** the note store, screenshots, and attaching.
- **Assist:**
  - Slow-motion pacing math.
  - The colorblind palette transform (a known color maps as the matrix says; the palette stays in range).
- **Practice:** the retry restores the exact state; split and personal-best logic.
- **Dumper:**
  - Command-line construction per mapper.
  - The retry and hash-compare policy against a fake process runner.
  - Error message mapping and the wizard ViewModel states.

## Owner checklist (manual, with real hardware)
1. Choose the 5 launch password titles. Using your own cartridges, confirm each definition captures the
   correct password.
2. Connect an INL Retro-Prog and dump an NROM, an MMC1 and an MMC3 cartridge. Confirm the hash matches the
   catalog and the game appears as Owned: Cartridge + Digital dump.
