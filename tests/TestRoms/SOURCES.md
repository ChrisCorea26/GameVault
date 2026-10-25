# Test ROM sources

Every file **committed** in this folder must be freely redistributable and recorded below (SPEC §11.3).

Suites whose license doesn't clearly permit redistribution are **downloaded at test time** instead.
`fetch-test-roms.sh` / `fetch-test-roms.ps1` read [`manifest.tsv`](manifest.tsv), download each file into
`.cache/` (git-ignored) and verify its SHA-256. CI runs the script before the tests.

**Never commit commercial ROMs, BIOS files, or dumps of licensed games, even "for testing."**
To test against your own dumps, set `GAMEVAULT_LOCAL_ROMS` to a local folder. Tests marked
`[LocalRomFact]` run only when it is set.

## Committed files

| File / folder | Suite | Author | Source URL | License / permission | Added in |
|---|---|---|---|---|---|
| _(none yet)_ | | | | | |

## Downloaded at test time

All downloads are pinned to commit `95d8f621ae55cee0d09b91519a8989ae0e64753b` of the community mirror
[christopherpow/nes-test-roms](https://github.com/christopherpow/nes-test-roms). Hashes are in `manifest.tsv`.

| Cache path | Suite | Author | Why it isn't committed | Used by |
|---|---|---|---|---|
| `nestest/nestest.nes`, `nestest/nestest.log` | nestest | Kevin Horton (kevtris) | No explicit redistribution license | Phase 1: CPU trace comparison |
| `instr_test-v5/*.nes` (16 single-test ROMs) | instr_test-v5 | Shay Green (blargg) | No explicit redistribution license | Phase 1: CPU instruction behavior |
| `ppu_vbl_nmi/*.nes` (10 single-test ROMs) | ppu_vbl_nmi | Shay Green (blargg) | No explicit redistribution license | Phase 2: VBlank/NMI timing |
| `sprite_hit_tests_2005.10.05/*.nes` (11) | sprite_hit_tests | Shay Green (blargg) | No explicit redistribution license | Phase 2: sprite 0 hit |
| `sprite_overflow_tests/*.nes` (5) | sprite_overflow_tests | Shay Green (blargg) | No explicit redistribution license | Phase 2: sprite overflow flag |
| `blargg_ppu_tests_2005.09.15b/*.nes` (5) | blargg_ppu_tests | Shay Green (blargg) | No explicit redistribution license | Phase 2: palette, OAM, VRAM access |
| `ppu_open_bus/ppu_open_bus.nes` | ppu_open_bus | Shay Green (blargg) | No explicit redistribution license | Phase 2: PPU I/O latch decay |
| `oam_read/oam_read.nes` | oam_read | Shay Green (blargg) | No explicit redistribution license | Phase 2: OAM reads |
| `instr_timing/*.nes` (2) | instr_timing | Shay Green (blargg) | No explicit redistribution license | Phase 3: instruction cycle counts |
| `cpu_interrupts_v2/*.nes` (5) | cpu_interrupts_v2 | Shay Green (blargg) | No explicit redistribution license | Phase 3: IRQ/NMI latency |
| `apu_test/*.nes` (8) | apu_test | Shay Green (blargg) | No explicit redistribution license | Phase 3: APU registers and frame counter |
| `apu_reset/*.nes` (6) | apu_reset | Shay Green (blargg) | No explicit redistribution license | Phase 3: APU state after reset |
| `blargg_apu_2005.07.30/*.nes` (11) | blargg_apu_2005 | Shay Green (blargg) | No explicit redistribution license | Phase 3: length counters, IRQ timing |
| `dmc_tests/*.nes` (4) | dmc_tests | Shay Green (blargg) | No explicit redistribution license | Phase 3: DMC (run-to-completion only) |
| `sprdma_and_dmc_dma/*.nes` (2) | sprdma_and_dmc_dma | Shay Green (blargg) | No explicit redistribution license | Phase 3: OAM/DMC DMA overlap (known gap) |
| `dmc_dma_during_read4/*.nes` (5) | dmc_dma_during_read4 | Shay Green (blargg) | No explicit redistribution license | Phase 3: DMC DMA during reads (not yet asserted) |
| `mmc3_test_2/*.nes` (6 single ROMs) | mmc3_test_2 | Shay Green (blargg) | No explicit redistribution license | Phase 4: MMC3 IRQ counter |
| `instr_test-v5/all_instrs.nes`, `official_only.nes` | instr_test-v5 | Shay Green (blargg) | No explicit redistribution license | Phase 4: combined ROMs (MMC1) |
| `ppu_vbl_nmi/ppu_vbl_nmi.nes` | ppu_vbl_nmi | Shay Green (blargg) | No explicit redistribution license | Phase 4: combined ROM (MMC1) |
| `ppu_read_buffer/test_ppu_read_buffer.nes` | ppu_read_buffer | Joel Yliluoma (Bisqwit) | No explicit redistribution license | Phase 4: $2007 read buffer (CNROM) |
| `cpu_dummy_reads/cpu_dummy_reads.nes` | cpu_dummy_reads | Shay Green (blargg) | No explicit redistribution license | Phase 4: dummy reads (CNROM) |
| `cpu_dummy_writes/*.nes` (2) | cpu_dummy_writes | Joel Yliluoma (Bisqwit) | No explicit redistribution license | Phase 4: dummy writes |
