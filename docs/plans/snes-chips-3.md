# SNES enhancement chips, part 3: S-RTC, SPC7110, Cx4 and ST018

**Goal (outstanding-work §5, `docs/bugs.md` "Deferred on purpose"):** the four SNES chips still missing. Each is
its own pull request, merged before the next, in this order (smallest first):

| # | Chip | Games | Player supplies |
|---|---|---|---|
| 1 | S-RTC (Sharp RTC) | Daikaijuu Monogatari II | — |
| 2 | SPC7110, with the Epson RTC-4513 | Far East of Eden Zero (with the RTC), Momotarou Dentetsu Happy, Super Power League 4 | — |
| 3 | Cx4 (Hitachi HG51B169) | Mega Man X2, Mega Man X3 | `cx4.rom`, the chip's 3 KB data ROM |
| 4 | ST018 (an ARMv3 CPU) | Hayazashi Nidan Morita Shogi 2 | `st018.rom`, its 128 KB program and 32 KB data ROM |

Behaviour follows ares (ISC licence, credited in `THIRD-PARTY-NOTICES.md` where code is ported) and fullsnes.

## Clocks (items 1 and 2)
- Both clocks count emulated time from the master clock, so a frame is a frame's worth of seconds, and save states
  hold the clock.
- When a game loads for normal play, the host sets the clock to the PC's local time. Netplay and challenge replays
  leave it at a fixed start, so both sides and every replay see the same time.
- A time the player sets inside the game lasts until the game is closed. It isn't kept in the save, because the core
  can't read the PC's clock to work out how much time passed. This is said in the manual.
- `ISnesRealTimeClock.SetTime(DateTime)` on the chip.

## 1. S-RTC
- `$2800` reads and `$2801` writes, in banks $00–$3F and $80–$BF. The protocol: $0D starts a read (15, then the 13
  digits, then 15); $0E then $00 starts a write of 12 digits, after which the weekday is worked out; $0E then $04
  resets. The digits are seconds, minutes, hours and day (two each), the month, the year (three, from 1000) and
  the weekday.
- Header chipset $55 (with the ExHiROM map the cartridge already reads).
- **Tests:** reading the time it was set to, a write then a read, the weekday it works out, time passing (a minute
  of frames), month and leap-year roll-overs, a save state.

## 2. SPC7110 and the RTC-4513
- **Memory:** the program ROM (the first 1 MB, or 2 MB with $4834 bit 2) at $00–$0F/$80–$8F:8000 and $C0–$CF, and
  the data ROM behind it. Each 1 MB window of $C0–$FF (and its LoROM mirrors) is set by $4830–$4833. SRAM is at
  $00–$3F/$80–$BF:6000–7FFF, with writes enabled by $4830 bit 7. The data ROM size is set by $4834. $50 and $58 are
  mirrors of $4800 and $4808.
- **Decompression unit ($4800–$480C):** the table entry ($4801–$4804) gives the mode (1, 2 or 4 bits a pixel) and
  the source. Neviksti's context-model decoder (as talarubi optimised it, ported from ares) unpacks tiles through
  $4800, with the skip ($4805/6) and stride ($4807) options.
- **Data port ($4810–$481A):** offset, adjust and stride, signed or unsigned, with the increment modes chosen by
  $4818.
- **ALU ($4820–$482F):** 16×16 multiply and 32÷16 divide, signed or unsigned, with the busy flag.
- **RTC-4513 ($4840–$4842):** chip select, the mode/seek/read/write protocol with its ready flag, the BCD registers
  (12- and 24-hour, hold, the calendar, rounding to the nearest minute, the periodic flag), counted the way the
  chip counts invalid BCD digits. Header chipset $F9 (with RTC) or $F5.
- **Tests:** vectors from ares' own decompressor (compiled here from its source as an oracle) for each mode; the
  data port's modes; ALU results including division by zero; the bank windows; SRAM write protect; the RTC's
  protocol, ready flag, 12/24-hour switch and roll-overs; save states.

## 3. Cx4
- The HG51B169: 24-bit accumulator and registers, the 48-bit multiplier, the 1,024-word data ROM, 3 KB of data
  RAM, the two 512-byte program cache pages loaded from the cartridge ROM, DMA, suspend, the IRQ on halt, and the
  CPU's view of $6000–$7FFF. Low-level, from ares' description.
- The player's `cx4.rom` (3,072 bytes) in the enhancement chip ROM folder. Without it the game shows the
  missing-ROM message.
- **Tests:** hand-assembled HG51B programs (moves, ALU, shifts, multiply, branches, the data ROM and RAM, cache
  loads, DMA), the IRQ, a save state.

## 4. ST018
- An ARM-mode ARMv3 interpreter (data processing, multiplies, single and block transfers, SWP, PSR transfers,
  branches, the exception modes and banked registers), and the ST018's bridge. That covers the two one-byte mailboxes
  and the status register at $3800–$3804, the reset and ready lines, and its 16 KB of RAM.
- The player's `st018.rom` (160 KB: the 128 KB program, then the 32 KB data) in the chip ROM folder.
- **Tests:** ARM programs (each instruction class, conditions, flags, banked registers, exceptions), the mailbox
  handshake with the SNES CPU, a save state.

## Each PR also
`docs/bugs.md`, `docs/compatibility.md`, the README status, the manual's Super Nintendo section and the required
files (with the PDF rebuilt), and `THIRD-PARTY-NOTICES.md` where code is ported.

## Owner checks
Each chip's games with your own cartridges and chip dumps: Daikaijuu Monogatari II's clock; Far East of Eden
Zero's clock and graphics; Mega Man X2 and X3 (their wireframes and the Cx4 effects); Morita Shogi 2's moves.
