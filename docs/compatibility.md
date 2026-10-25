# Compatibility dashboard

This page will be generated in CI from the test-ROM harness (SPEC §11.2). Until then it is updated by hand
at the end of each phase. Run `tests/TestRoms/fetch-test-roms.sh` (or `.ps1`) and then `dotnet test` to
reproduce it.

_Last updated: Phase 3._

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
| instr_timing | 1-instr_timing, 2-branch_timing | ✅ Pass | |
| cpu_interrupts_v2 | 1-cli_latency … 5-branch_delays_irq (5) | ✅ Pass | |

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

## APU

| Suite | Tests | Status | Notes |
|---|---|---|---|
| apu_test | 1-len_ctr … 8-dmc_rates (8) | ✅ Pass | |
| apu_reset | 4015_cleared … works_immediately (6) | ✅ Pass | |
| blargg_apu_2005.07.30 | 01.len_ctr … 11.len_reload_timing (11) | ✅ Pass | Also checked against golden images |
| dmc_tests | buffer_retained, latency, status, status_irq | ℹ️ Runs | Listening tests: they report by sound, so CI only checks that they run to completion |
| sprdma_and_dmc_dma | sprdma_and_dmc_dma, _512 | ❌ Known gap | Exact cycle cost when OAM and DMC DMA overlap |
| dmc_dma_during_read4 | 5 ROMs | ⏳ Not verified | Fetched but not yet asserted (DMC DMA side effects on `$2007` / `$4016` reads) |

Audio output is checked by `AudioSoakTests`: 30 minutes of virtual time through the real APU, resampler, ring
buffer and rate controller. The device clock is run at its exact rate and at ±0.3 %. The result is zero
underruns, an average latency under 40 ms and a peak under 50 ms.

## Mappers

| Mapper | Board | Status |
|---|---|---|
| 0 | NROM | ✅ PRG-ROM, PRG-RAM, CHR-ROM/RAM, mirroring |
