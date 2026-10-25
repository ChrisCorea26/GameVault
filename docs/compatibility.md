# Compatibility dashboard

This page will be generated in CI from the test-ROM harness (SPEC §11.2). Until then it is updated by hand
at the end of each phase. Run `tests/TestRoms/fetch-test-roms.sh` (or `.ps1`) and then `dotnet test` to
reproduce it.

_Last updated: Phase 2._

## CPU

| Suite | Test | Status | Notes |
|---|---|---|---|
| nestest | Automation-mode trace (8,991 lines) | ✅ Pass | Byte-for-byte match with `nestest.log`, including PPU dot/line and CYC columns |
| instr_test-v5 | 01-basics | ✅ Pass | |
| instr_test-v5 | 02-implied | ✅ Pass | |
| instr_test-v5 | 03-immediate | ✅ Pass | |
| instr_test-v5 | 04-zero_page | ✅ Pass | |
| instr_test-v5 | 05-zp_xy | ✅ Pass | |
| instr_test-v5 | 06-absolute | ✅ Pass | |
| instr_test-v5 | 07-abs_xy | ✅ Pass | |
| instr_test-v5 | 08-ind_x | ✅ Pass | |
| instr_test-v5 | 09-ind_y | ✅ Pass | |
| instr_test-v5 | 10-branches | ✅ Pass | |
| instr_test-v5 | 11-stack | ✅ Pass | |
| instr_test-v5 | 12-jmp_jsr | ✅ Pass | |
| instr_test-v5 | 13-rts | ✅ Pass | |
| instr_test-v5 | 14-rti | ✅ Pass | |
| instr_test-v5 | 15-brk | ✅ Pass | |
| instr_test-v5 | 16-special | ✅ Pass | |
| instr_test-v5 | all_instrs / official_only | ⏳ Phase 4 | MMC1 board |
| instr_timing | | ⏳ Phase 3 | Needs APU length-counter timing |

## PPU

Each ROM is also checked against a golden image of its final screen (`tests/GameVault.Core.Tests/Golden/golden.json`).

| Suite | Tests | Status | Notes |
|---|---|---|---|
| ppu_vbl_nmi | 01-vbl_basics … 10-even_odd_timing (10) | ✅ Pass | All dot-exact NMI/`$2002` races |
| sprite_hit_tests_2005.10.05 | 01.basics … 11.edge_timing (11) | ✅ Pass | |
| sprite_overflow_tests | 1.Basics … 5.Emulator (5) | ✅ Pass | Includes the hardware evaluation bug |
| blargg_ppu_tests_2005.09.15b | palette_ram, sprite_ram, vbl_clear_time, vram_access | ✅ Pass | |
| blargg_ppu_tests_2005.09.15b | power_up_palette | ℹ️ Not run | Expects one particular console's power-up palette RAM |
| ppu_open_bus | | ✅ Pass | I/O latch with per-bit decay |
| oam_read | | ✅ Pass | |
| nestest | Menu screen | ✅ Golden image | Normal (non-automation) mode |
| ppu_vbl_nmi | Combined ROM | ⏳ Phase 4 | MMC1 board |
| ppu_read_buffer | test_ppu_read_buffer | ⏳ Phase 4 | CNROM board |

## Mappers

| Mapper | Board | Status |
|---|---|---|
| 0 | NROM | ✅ PRG-ROM, PRG-RAM, CHR-ROM/RAM, mirroring |
