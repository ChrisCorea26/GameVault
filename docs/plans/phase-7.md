# Phase 7 — Stretch

**Goal (SPEC §12):** Rollback netplay, FDS, Tier 4 mappers, expansion audio completion.

**Done when:** no criterion is set (stretch phase). Each item below ships with tests and is listed on the
compatibility dashboard.

## Expansion audio completion
- **VRC7 FM.** A YM2413 (OPLL) core, NESdev Wiki: "VRC7 audio":
  - 6 channels with the VRC7's 15 built-in instruments plus one custom instrument.
  - Logarithmic sine and exponent tables, generated at startup from the documented formulas.
  - ADSR envelopes, the AM/FM LFOs and key scaling.
  - Clocked every 36 CPU cycles (the chip's 49.7 kHz sample rate) and mixed through the APU.
- **MMC5 audio**: two pulse channels (no sweep) on a 240 Hz frame sequencer, plus the PCM register (write
  mode).
- **Sunsoft 5B**: the noise generator and the envelope generator.
- **FDS audio**: the 64-step wavetable with modulation unit (see FDS below).
- **Tests.** Each channel's period and volume, envelope shapes, and that the mix stays below clipping.
  The OPLL is checked against the documented rate tables and instrument ROM values.

## Famicom Disk System (SPEC §4.5 Tier 4; user-supplied BIOS)
- **BIOS.** The player points GameVault at their own `disksys.rom` (8 KB) in Settings. It is never
  shipped, downloaded or linked to.
  - Without it, FDS images (`.fds`, with or without the fwNES header) show a plain message.
- **RAM adapter.**
  - 32 KB PRG-RAM, 8 KB CHR-RAM.
  - Timer IRQ.
  - Disk drive emulation at the byte level: gap, block start mark, CRC, and transfer IRQs, with
    side/disk switching.
  - Writes are saved to a separate patch file next to the save data, never into the image.
- **Disk switching UI:** Player → "Disk side" menu ("Eject", "Side A", "Side B", …), plus a hotkey.
- **Tests** use synthetic disk images (headers and block structure built in the test) and a stub BIOS
  routine written for the test. The real BIOS never enters the repository.

## Tier 4 mappers (SPEC §4.5)
Remaining NES 2.0 mappers, prioritized by how many licensed and popular unlicensed releases use them:
- 13 CPROM, 15 (100-in-1), 28 Action 53, 30 UNROM 512, 32 Irem G-101, 33/48 Taito TC0190/TC0690,
- 64 Tengen RAMBO-1, 65 Irem H3001, 67 Sunsoft-3, 68 Sunsoft-4, 73 VRC3, 75 VRC1, 76 / 88 / 95 / 154
  Namco 108 variants,
- 80 / 82 / 207 Taito X1-005/X1-017, 152, 180, 184, 185 (CHR disable), 232, 228.

Each gets unit tests and a state round-trip, like the earlier tiers.

## Rollback netplay (SPEC §7.7) — **needs the owner**
The core is ready for it: deterministic, sub-millisecond save/load (0.008 ms measured) and per-frame input.
The parts that need decisions:
- **Relay/STUN service.** "Connection by invite code through a lightweight relay/STUN service" needs a
  server someone hosts and pays for. Options:
  - A small relay the owner deploys.
  - A public STUN server plus a relay only as fallback.
  - LAN-only to start.
- **Protocol.** Rollback over UDP with input prediction, a configurable input delay of 0–2 frames,
  checksum-based desync detection, and ROM hash verification (never ROM data).

  The engine (rollback buffer, prediction, resimulation) can be built and tested locally with a simulated
  lossy link before any server exists.

## Order of work
1. Expansion audio completion.
2. Tier 4 mappers.
3. FDS.
4. The netplay engine with a simulated link.
5. The netplay transport, once the owner picks the service.
