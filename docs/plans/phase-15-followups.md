# Phase 15 and 16 follow-ups — PlayStation controllers, cards, timing, codes

**Goal:** finish items that Phase 15's and Phase 16's plans left for later, while Phase 17 is built elsewhere:
- **PlayStation:** the DualShock (analog sticks and vibration), the multitap, memory card slot 2, GPU command
  timing, and the XA resampling filter;
- **GameShark-style codes** for the PlayStation and the N64;
- **a memory card manager screen**, with `--sample-data` content and ViewModel tests.

None of this starts a new phase. Every item comes from a "Left for later" list in `phase-15.md` or `phase-16.md`.

**Done when:**
- every item below has unit tests, and all tests pass;
- the NES, SNES, Genesis and N64 paths behave as before;
- the new screen renders under `--sample-data`.

**Overlap with Phase 17:** the PS2 also uses the DualShock (the DualShock 2) and SIO-style pads. The 64-bit
`ControllerState` below is written so the PS2 can use it unchanged. Phase 17's branch will need to merge it.

## 1. A 64-bit `ControllerState`
The DualShock has two sticks and two more buttons, and the 32-bit state is full (16 button bits, plus the N64's
stick in the top 16). The state becomes a `ulong`:
- **Bits 0–15:** the buttons, unchanged.
- **Bits 16–31:** the left stick, X then Y, each a signed byte with up positive. This is the N64's stick,
  unchanged.
- **Bits 32–47:** the right stick, in the same form.
- **Bit 48 `L3` and bit 49 `R3`:** the stick buttons.
- **Bit 50 `Analog`:** the DualShock's Analog button. The pad toggles its mode on the press, so the toggle is
  part of the input stream and replays in recordings, netplay and challenges.

**Formats:**
- **Recordings (`InputRecording`):** version 4 stores 64 bits a player. Versions 1–3 still decode.
- **Netplay:** wire format version 4 and protocol version 4, with 64-bit inputs.

**Save states:** consoles that store pads in their states keep their own widths. Only the PlayStation's
SIO chunk changes (§2).

## 2. PlayStation controllers (`PsSio`)
Reference: psx-spx "Controllers - Communication Sequence", "Standard Digital/Analog Controllers",
"Configuration Commands", "Vibration/Rumble Control" and "Multitap".

### 2.1 The DualShock (SCPH-1200)
The pad is in its own class, `PsPad`, behind each port:
- **Digital mode (ID $5A41):** the button bytes, as today.
- **Analog mode (ID $5A73, red LED):**
  - adds the right stick, then the left (00 is left/up, 80 centre, FF right/down);
  - L3 and R3 come in the button bits.
- **Config mode (ID $5AF3):** commands 42h–4Dh with psx-spx's replies:
  - 44h sets the LED, with the lock;
  - 45h returns type 01 (PSX analog pad);
  - 4Dh maps the rumble bytes;
  - 46h/47h/4Ch return their constants;
  - other commands reply with zeros.
- **The Analog button:** toggles analog mode, unless 44h locked it. Like the hardware, the press also stops and
  locks the motors.
- **Rumble:**
  - **Old method:** before config mode is used, the small motor runs while byte 4 is in 40h–7Fh and byte 5 is
    odd.
  - **New method:** after 4Dh, the mapped bytes drive the large motor (0–255) and the small motor (bit 0).
  - The console reports each port's motors; the core keeps no time for them.
- **Settings → PlayStation:**
  - **"Controller":** DualShock (the default) or digital pad. A digital pad answers only 42h, as Phase 15's did.
  - **"Start in analog mode":** on by default. The hardware starts digital, but games like Gran Turismo never
    ask for analog mode, so players would have to find the Analog button first.
- **Save states:** each pad's mode, lock, config state, rumble map and motor levels. The SIO chunk goes to
  version 2, and version-1 states still load.

### 2.2 The multitap (SCPH-1070)
- **Settings → PlayStation:** "Multitap in port 1" (off by default) gives four players on 1A–1D.
- **Reading pads:** both access methods.
  - **Method 1:** `01 42 01` asks for the long reply on the next access. That reply is 5A80h and then four
    8-byte slots: each pad's ID, buttons and sticks, padded with FFh; an empty slot is all FFh.
  - **Method 2:** `0n 42` reads slot n.
- **Memory cards:** `8n` reaches the card in slot n. Only slot 1A has a card; the others don't answer.
- **The Player:** sets four players, as the NES Four Score does (`UsesFourScore` becomes `PlayerCount`).

### 2.3 Memory card slot 2
- Both slots always hold a card, so netplay peers and challenges always see the same machine.
- **Files:** slot 1 stays `Title - hash.mcd`; slot 2 is saved as `Title - hash.card2.mcd`.
- **Saving:** like the N64's Controller Pak, slot 2 is loaded at start and saved when the game writes it.

### 2.4 Vibration on real pads
- `IControllerBackend.SetRumble(instance, low, high)` is SDL's `GameControllerRumble`. It's called on the SDL
  thread, from a queue.
- After each frame, the host passes the motors of each port to the pad on that port.
- **Settings → Input:** "Vibration" (on by default) turns it off everywhere.

### 2.5 Input mapping
- **Pads:** in the PlayStation layout, the left stick is the DualShock's left stick and still presses the D-pad
  for digital-pad games. The right stick is the right stick, and the stick clicks are L3/R3.
- **The keyboard, while a PlayStation game runs:**
  - T/F/G/H push the left stick and I/J/K/L the right stick (as the N64's keys);
  - 5/6 are L3/R3;
  - V is the Analog button.
- **The Player:** an "Analog" button toggles analog mode for pads that can't reach V.
- **Docs:** `docs/design/input.md` lists these keys.

## 3. GPU command timing and GPUSTAT's busy bits
psx-spx has no table of drawing costs, so the GPU uses an approximate model in CPU cycles, after Mednafen's:
- **Fill:** 46 + (width/8 + 9) × height.
- **Polygons and rectangles:** 64 for setup, then 1 per pixel considered, 2 when textured, and half again for
  semi-transparency.
- **Lines:** 16 plus 1 per pixel.
- **VRAM→VRAM copies:** 2 per pixel.

The busy time feeds two things:
- **GPUSTAT:** bit 26 ("ready for a command") and bit 28 ("ready for a DMA block") are clear while drawing, and
  bit 25 follows them in DMA mode 2.
- **DMA:** the GPU's FIFO holds 16 words. GPU DMA (block and linked list) can't run more than that ahead of the
  drawing, so the CPU waits for the GPU to catch up, as it does on the hardware.

The draw itself still happens at once, so VRAM looks the same whenever it's read.
- **Save states:** `busyUntil` is saved, and the GPU chunk goes to version 2.
- **Tests:**
  - GPUSTAT is busy after a large fill and ready after enough cycles;
  - linked-list DMA of a heavy list takes longer than a light one;
  - the golden-image tests still match.

## 4. The XA resampling filter
- **Resampling:** XA-ADPCM goes through the hardware's zigzag interpolation (psx-spx "CDROM XA Audio ADPCM
  Compression": "25-point Zigzag Interpolation"). It replaces the linear interpolation.
  - Each channel keeps a 32-sample ring.
  - After every six 37.8 kHz samples, seven 44.1 kHz outputs come from the seven 29-tap tables.
  - 18.9 kHz audio writes each sample twice.
- **Save states:** the rings, their positions and the six-step counter. The CD-ROM chunk goes to version 2, and a
  version-1 state starts with empty rings.
- **Tests:**
  - a constant signal settles at the tables' DC gain;
  - 6 samples in give 7 out;
  - stereo channels stay apart.

## 5. GameShark codes (PlayStation and N64)
- **Core (`GameVault.Core.Cheats`):**
  - `GameSharkCode`: a code type, a 24-bit address and a 16-bit value.
  - `GameSharkEngine`: applies a list once per frame, before the frame runs. Codes change only between
    frames, so play stays deterministic.
  - Like the NES's RAM codes, they aren't part of save states: the host turns them off for netplay, challenges
    and hardcore.
- **PlayStation types** (psx-spx "Cheat Devices"):
  - 30 (8-bit write), 80 (16-bit write);
  - 10/11 and 20/21 (16- and 8-bit increment/decrement);
  - D0–D3 and E0–E3 (16- and 8-bit equal / not equal / less / greater: run the next code);
  - 50 (a serial repeater over the next code);
  - C0 (all codes wait until the value matches);
  - C1 (delay, read as nothing).
  - Addresses are RAM ($000000–$1FFFFF) or the scratchpad ($1F800000–$1F8003FF).
- **N64 types:**
  - 80/A0 (8-bit write), 81/A1 (16-bit write);
  - D0–D3 (8/16-bit equal and not equal);
  - 50 (repeater).
  - Enabler codes (DE, EE, F0, F1, FF, 2A, 3C) are accepted and skipped, since GameVault needs no enabler.
  - 88/89 (codes run by the GameShark's button) aren't supported, with a clear message.
  - Writes go to RDRAM, which is big-endian.
- **Format:** `XXXXXXXX YYYY` per line. Several lines that work together are joined with " + ", as NES codes
  are.
- **The Codes panel:** now also on PlayStation and N64 games, with the system's parser.
  - The bundled suggestions and GameGenie.com stay NES-only, since they hold NES codes.
  - The player's own code collections still work, matched by title.
- **Import:** plain-text files with one code per line, as for the NES.

## 6. The memory card manager screen
- **Library (`GameVault.Library.Saves.PsMemoryCardImage`):** reads and edits a raw 128 KB card
  (psx-spx "Memory Card Data Format"):
  - the directory frames;
  - each save's title (Shift-JIS, shown through .NET's encoding with the full-width forms folded to ASCII);
  - each save's product code and block count, and its icon (16×16, 4-bit, 16 colours, first frame);
  - deleting a save (all of its linked blocks), copying a save to another card (needs enough free blocks);
  - exporting and importing a save as a `.mcs` file (the 128-byte directory frame, then the blocks);
  - formatting.
- **The service** (`IMemoryCardService`, Platform: `MemoryCardService`):
  - lists every PlayStation card in the saves folder by game and slot;
  - reads a card;
  - applies changes atomically to the file.
  - The running game's cards are read-only, so a change can't race the game's own writes.
- **ViewModel (`MemoryCardsViewModel`):**
  - the list of cards and the saves on the chosen one (title, product code, blocks, icon PNG);
  - free blocks;
  - copy to another card, delete with confirmation, export/import a save, export the whole card, format.
- **Page:** `MemoryCardsPage`, reached from Settings → PlayStation ("Manage memory cards") and from a
  PlayStation game's details. From the details, the page opens on that game's slot 1 card.
- **Sample data:** two games' cards with a few saves and generated icons.
- **Tests:**
  - the image parser and editor: directory, chaining, delete, copy, `.mcs` round trip, format, damage;
  - the ViewModel against a fake service: listing, read-only while running, copy target choice, messages.

## Order of work
1. 64-bit `ControllerState`, recordings and netplay.
2. DualShock, multitap and slot 2 in the core; then the platform (settings, input, rumble, saving).
3. GPU timing; the XA filter.
4. GameShark codes: core, then library, service and ViewModel.
5. Memory card manager: library, service, ViewModel, page, sample data.
6. Docs: `docs/compatibility.md`, `docs/design/input.md`, and the "Left for later" lists in phase-15.md and
   phase-16.md.

## Left for later
- The mouse, GunCon/Justifier, the link cable.
- A second multitap (8 players: the Player and the input service stop at 4).
- The DualShock 2's pressure-sensitive buttons (Phase 17).
- N64 GameShark button codes (88/89) and the Rumble Pak.
