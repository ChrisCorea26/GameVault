# Compatibility dashboard

This page will be generated in CI from the test-ROM harness (SPEC §11.2). Until then it is updated by hand
at the end of each phase. Run `tests/TestRoms/fetch-test-roms.sh` (or `.ps1`) and then `dotnet test` to
reproduce it.

_Last updated: Phase 1._

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

## Mappers

| Mapper | Board | Status |
|---|---|---|
| 0 | NROM | ✅ CPU side (PRG-ROM, PRG-RAM, CHR-ROM/RAM). PPU side is exercised from Phase 2 |
