# Genesis, Sega CD and 32X gaps

**Goal:** close the Genesis-family items left in `docs/bugs.md` ("Deferred on purpose"): EEPROM saves on the EA,
Acclaim and Codemasters boards; the Sega CD's mode 1, CD+G and 1M cell-image view; and the 32X's SH-2 caches and
mid-frame picture changes. Genesis stereo is already done (outstanding-work §2: the YM2612's panning reaches the
resampler's two channels), so its entry is just removed.

## 1. EEPROM saves on every board (outstanding-work §7)
- **`GenesisEepromBoards`** (Core): the boards that wire an I²C EEPROM somewhere other than Sega's header layout, by
  the header's product code at $183 (and the header checksum at $18E where the code is shared). Each entry gives the
  chip (X24C01, 24C02–24C16 or 24C65: size and page size from the data sheets) and its wiring: which address and
  bit the game drives SCL and SDA through, and where SDA reads back.
  - **EA:** SDA and SCL on bits 7 and 6 of $200000, SDA read back on bit 7 (X24C01): Rings of Power, NHLPA Hockey
    '93, John Madden Football '93 (and the Championship Edition), Bill Walsh College Football.
  - **Acclaim, first board:** SDA bit 0 and SCL bit 1 written at $200000, SDA read on bit 1 (24C02): NBA Jam.
  - **Acclaim, later boards:** SDA on bit 0 of $200001 (both ways), SCL on bit 0 of $200000: NFL Quarterback Club
    (24C02), NBA Jam Tournament Edition (24C04), NFL Quarterback Club '96 (24C16), College Slam and Frank Thomas
    Big Hurt Baseball (24C65).
  - **Codemasters:** SDA bit 0 and SCL bit 1 written at $300000, SDA read on bit 7 of $380001: Micro Machines 2,
    Micro Machines Military, Micro Machines '96 and Brian Lara Cricket (24C08 or 24C16), Brian Lara Cricket '96 and
    Shane Warne Cricket (24C65).
  - **Sega's own** boards also cover the games whose header doesn't declare the chip (Wonder Boy in Monster World,
    Mega Man: The Wily Wars, Evander Holyfield's Boxing, Greatest Heavyweights, Sports Talk Baseball and others).
- `GenesisCartridge` looks the game up first, then falls back to the header's "RA" $E8 $40. Other bits of a read
  are the ROM's, as before. The table is the board wiring Genesis Plus GX's eeprom_i2c.c documents, read for
  behaviour only.
- **Tests:** each wiring writes a byte and reads it back through the cartridge's addresses; a 24C65 uses its two
  address bytes; the EEPROM's contents are the battery save; a save-state round trip.

## 2. Sega CD: the 1M cell-image view
- In 1M mode the main CPU sees its Word RAM bank twice: in order at $200000–$21FFFF, and at $220000–$23FFFF as a
  "cell image". There the bank is read as a bitmap 64 cells (512 dots) wide, and its long words are re-ordered so
  that consecutive ones run down each column of cells. The column is 32 cells tall for the first 64 KB, 16 cells
  for the next 32 KB, 8 for the next 16 KB, and 4 for each of the last two 8 KB. A VDP DMA from the cell image
  therefore moves ready-made tiles. Reads and writes both go through the view. The mapping is Sega's Mega-CD
  hardware manual's figure, as PicoDrive's cell_map applies it.
- **Tests:** a dot pattern written in order at $200000 reads back cell by cell at $220000 in each of the five
  areas; a write through the view lands in the right place.

## 3. Sega CD: mode 1 (a cartridge with the CD)
- With a cartridge in the slot, the Genesis boots the cartridge, and the Sega CD moves up to $400000–$7FFFFF: the
  BIOS at $400000, PRG-RAM's bank at $420000, Word RAM at $600000. The gate array at $A12000 is unchanged. This is how
  MSU-MD games (cartridges that play CD audio) and other mode 1 software use the drive.
- `SegaCdUnit.Mode1` (set by the console when it gets both a real cartridge and the unit). The save is the
  cartridge's when it has one, otherwise backup RAM.
- **Host:** a Genesis cartridge with a `.cue` or `.chd` of the same name beside it starts in mode 1 when a Sega CD
  BIOS is chosen. Without the BIOS it plays as a plain cartridge, with a notice naming the setting.
- **Tests:** a cartridge program reads the BIOS through $400000, starts the sub CPU through the gate array, and the
  sub CPU sees a command word; the cartridge stays at $000000; a save-state round trip.

## 4. Sega CD: CD+G (subcode)
- **Discs:** `ICdSubcode` (Core, next to `ICdImage`): a disc image that also has each sector's 96 bytes of subcode,
  one byte per symbol (P in bit 7 … W in bit 0). `CueImage` reads a `.sub` file named like the cue sheet (CloneCD's
  layout: 12 bytes per channel, P to W). `ChdImage` reads the subcode stored after each frame when the track's
  SUBTYPE is RW_RAW (stored a byte per symbol). Images without subcode are unchanged.
- **Sega CD:** each sector the drive plays or reads (75 a second) moves the gate array's subcode pointer ($FF8068)
  on by 98 bytes. The sector's 96 symbols then go into the 64-word subcode buffer at $FF8100 (mirrored at $FF8180),
  two symbols a word, and the level 6 interrupt is raised. The BIOS's own CD player decodes the CD+G graphics from
  there. Behaviour follows Genesis Plus GX's cdd.c, read for behaviour only.
- **Tests:** the subcode conversion from both file layouts; the buffer, pointer and level 6 interrupt from a disc
  with known subcode; a `.sub` beside a cue sheet; a save-state round trip.

## 5. 32X: the SH-2 cache
- The SH7604's 4 KB, four-way cache of 16-byte lines (64 sets), write-through, with its six-bit LRU per set:
  - CCR ($FFFFFE92): CE, ID, OD, TW (two-way mode, ways 0–1 as 2 KB of RAM), CP (purge) and W1–W0.
  - Cached reads ($00000000) fill a line on a miss, unless ID (for instructions) or OD (for data) says not to.
  - Writes go to memory and update a line they hit.
  - The cache-through area ($20000000) always goes to memory.
  - The purge area ($40000000) invalidates the line holding an address.
  - The address array ($60000000) reads and writes a set's tags, valid bits and LRU.
  - The data array ($C0000000) is the lines' own data, as before.
- The other CPU, the DMA controllers and the 68000 don't snoop. A game that leaves stale lines therefore reads stale
  data until it purges them, as on the hardware. This is what the deferred item was about.
- **Tests:** a hit after a fill; a stale read after the bus changes behind the cache; the cache-through and purge
  areas; ID/OD; two-way mode; LRU replacement order; the address array; CP; save states (version 2 loads version 1).
  Every existing SH-2 and 32X test still passes.

## 6. 32X: mid-frame picture changes
- Each 32X line is laid over the Genesis line as the Genesis draws it (at the line's start), instead of all at
  vertical blank. The 32X's mode, palette, shift, priority and line table are therefore the ones in force when the
  line was scanned, so H-interrupt raster effects show. The buffer swap still waits for vertical blank.
- **Tests:** a palette change made on a Genesis H interrupt shows from that line down; a mode change mid-frame.

## Docs
`docs/bugs.md` (the deferred row), `docs/compatibility.md`, the README status, the manual's Genesis, Sega CD and
32X sections (mode 1 and the `.sub` file), with the PDF rebuilt.

## Owner checks
- NBA Jam, NHLPA '93, Micro Machines 2 and College Slam keep their saves across power cycles.
- An MSU-MD cartridge plays its CD audio with your Sega CD BIOS.
- A CD+G disc shows its graphics in the BIOS's CD player.
- Virtua Racing Deluxe, Doom and Knuckles' Chaotix still run with the cache on.
