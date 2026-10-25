# Phase 1 — CPU

**Goal (SPEC §12):** full 6502 core (Ricoh 2A03, no decimal mode), bus, and RAM.

**Done when:** the `nestest.log` trace matches byte for byte, and `instr_test-v5` passes.

## Design

### Cycle model
- The CPU runs one instruction at a time, but **every bus access is exactly one CPU cycle**, including the
  dummy reads and writes real hardware makes (NESdev: "CPU unofficial opcodes", "6502 cycle times").
  Before each access, the bus advances the rest of the system by one CPU cycle (PPU: 3 dots, mappers:
  `OnCpuCycle`). Timing is cycle-exact without a per-cycle state machine.
- Interrupts are polled at the end of each instruction's second-to-last cycle, including the CLI/SEI/PLP
  delay and the branch quirks. NMI is edge-triggered, IRQ is level-triggered, and NMI can hijack
  BRK/IRQ (NESdev: "CPU interrupts").
- `Cpu6502` is sealed and uses no allocations or LINQ in `Step()`. It reaches the bus through `ICpuBus`.
  The call site is monomorphic, so dynamic PGO devirtualizes it. This gets benchmarked again in Phase 2.

### Components (all `IStateful`)
| Type | Responsibility |
|---|---|
| `Cpu/Cpu6502` | Registers, status flags, all 151 official opcodes plus the unofficial opcodes in SPEC §4.2, interrupts, `Step()` |
| `Cpu/CpuStatus` | Flag bits (`N V - B D I Z C`) |
| `Cpu/Opcodes` | Opcode table: mnemonic, addressing mode, official flag. Used by the tracer and the disassembler |
| `Cpu/CpuTracer` | Produces `nestest.log`-format lines (PC, bytes, disassembly, A/X/Y/P/SP, PPU dot/line, CYC) |
| `Bus/NesBus` | CPU memory map: 2 KB RAM mirrored to `$1FFF`, PPU registers `$2000–$3FFF`, APU/IO `$4000–$401F`, cartridge `$4020+`. Open-bus value kept |
| `Ppu/PpuTimingStub` | Phase 1 only. Counts dots/scanlines and the VBlank flag (`$2002` bit 7, NMI) so test ROMs that wait for VBlank run. Phase 2 replaces it with the real PPU |
| `Cartridge/INesRom` | Parses iNES/NES 2.0 headers from a byte array (no file I/O in the core) |
| `Cartridge/Mappers/Nrom` | Mapper 0 plus 8 KB PRG-RAM at `$6000`. The test ROMs report results there. Phase 2 extends it with CHR |
| `NesConsole` | Owns the components. `Power()`, `Reset()`, `StepInstruction()`, `RunCycles(n)` |

OAM DMA (`$4014`, 513/514 cycles) is included because it is a CPU stall. DMC DMA waits for the APU (Phase 3).

## Tests (written first)
1. **Per-opcode unit tests** on a flat 64 KB test bus: flags, results, cycle counts (page-cross and branch
   penalties), and the dummy-access sequence for representative opcodes of each addressing mode.
2. **Interrupt tests**: NMI edge detection, IRQ masking, CLI/SEI latency, BRK and NMI hijack.
3. **State round-trip**: CPU, bus RAM, NROM and the whole console.
4. **nestest**: run from `$C000` in automation mode and diff every trace line against `nestest.log`. The
   comparison covers PC, opcode bytes, registers and CYC. The PPU columns are checked once the stub
   models them.
5. **instr_test-v5**: the 16 `rom_singles` (all NROM). `TestRomRunner` runs until `$6000` reports done, then
   reads the text at `$6004`.
   - `all_instrs.nes` and `official_only.nes` use MMC1, so they are added in Phase 4.
   - `instr_timing` depends on APU timing, so it is added in Phase 3.

## Test ROM licensing
nestest (kevtris) and blargg's suites have no explicit redistribution license, so they are **not committed**
(SPEC §11.3).
- `tests/TestRoms/fetch-test-roms.ps1` and `fetch-test-roms.sh` download pinned files into
  `tests/TestRoms/.cache/` (git-ignored) and verify their SHA-256 hashes. CI runs the script.
- ROM-backed tests are skipped when the files are missing. CI sets `GAMEVAULT_REQUIRE_TEST_ROMS=1`, which
  turns a missing ROM into a failure.
- `SOURCES.md` records each file's origin URL, author and hash.

## Out of scope
The real PPU and rendering (Phase 2), APU (Phase 3), and MMC1 and other mappers (Phase 4).
