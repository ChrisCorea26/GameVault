# Phase 4 — Tier 1 mappers + polish

**Goal (SPEC §12):** Mappers 1/2/3/4/7, save states, battery saves, D3D11 presets, run-ahead.

**Done when:** MMC3 IRQ tests pass; state round-trip tests pass for all mappers.

## Core

### Mappers (SPEC §4.5, Tier 1; one class per board under `Cartridge/Mappers`)
- **Shared base, `MapperBase`.**
  - Holds PRG/CHR bank tables at 8 KB and 1 KB granularity, so per-access reads are one shift, one index
    and one mask. No virtual calls inside the bank lookup.
  - Holds PRG-RAM sized from the header (NES 2.0 sizes honored) and CHR-RAM when there is no CHR-ROM.
  - Holds a `Battery` flag.
  - `IBatteryBacked` exposes the PRG-RAM (or other non-volatile) bytes for the platform to save and load.
- **Mapper 1, MMC1 / SxROM** (NESdev Wiki: "MMC1", "SxROM").
  - Serial 5-bit shift register. A write with bit 7 set resets it and sets PRG mode 3.
  - Consecutive-cycle writes are ignored after the first (the RMW double-write rule; test: `MMC1_A12`).
  - PRG modes 0/1 (32 KB) and 2/3 (fix first/last); CHR 4 KB / 8 KB; mirroring control.
  - SUROM 512 KB PRG uses CHR bit 4 as the PRG high bit. SOROM/SXROM PRG-RAM banking uses CHR bits 2–3.
  - The MMC1B PRG-RAM enable bit is honored.
- **Mapper 2, UxROM**: switchable 16 KB at `$8000`, last bank fixed. Bus conflicts on submapper 2.
- **Mapper 3, CNROM**: 8 KB CHR switching, with bus conflicts (NESdev Wiki: "CNROM").
- **Mapper 7, AxROM**: 32 KB PRG and single-screen mirroring select.
- **Mapper 4, MMC3 / MMC6** (NESdev Wiki: "MMC3").
  - Eight bank registers with PRG/CHR inversion, mirroring, PRG-RAM protect (MMC6 uses its own 1 KB
    internal RAM protect scheme on submapper 1).
  - **Scanline IRQ counter**, clocked by filtered A12 rising edges. A rise counts only when A12 was low
    for at least 3 falling M2 edges, so the eight sprite fetches clock it once per line.
  - Uses the Sharp "new" (rev B) behavior: reloading to 0 fires every clock. The NEC "old" behavior is
    selected by NES 2.0 submapper 4.
- The PPU gets **per-dot sprite pattern fetches**: on dots 257–320 each slot's two garbage nametable
  reads and two pattern reads happen at their real dots. Today all of them happen at dot 257, which
  would put MMC3 IRQs at the wrong dot.
- `IMapper` gains `OnA12Fall()` (default no-op). The PPU reports both edges.
- `MapperFactory` maps 0/1/2/3/4/7 (and 94/180-style UxROM variants only if trivial; otherwise Tier 2).

### Save states (SPEC §4.6)
- `NesConsole.SaveState()` / `LoadState(ReadOnlySpan<byte>)`: versioned header with magic and a ROM hash
  check, then every component's chunk. This builds on the existing `StateWriter` / `StateReader`.
- **Benchmark test:** save and load each take under 1 ms for NROM and MMC3 (Release; asserted with
  generous CI headroom and logged).
- **Round-trip test for every mapper:**
  1. Run N frames.
  2. Save.
  3. Run M more frames and record the frame hash and audio.
  4. Load and rerun M frames.
  5. Expect identical output.

  The test is driven by synthetic ROMs that exercise banking, IRQs and RAM, built in the test project. No
  ROMs are committed.

### Run-ahead (SPEC §5.5)
- `RunAhead` in Platform (`EmulatorHost`) with 0–3 frames:
  1. Save state.
  2. Run N frames with video and audio discarded, keeping the last frame's video.
  3. Load state.
  4. Run one real frame with audio.
- Auto-disable: if the measured save + load + N extra frames exceeds the frame budget (16.6 ms) for
  30 consecutive frames, run-ahead turns off and the player shows an InfoBar.
- `RunAheadFrames` setting (already in Settings → Gameplay) now takes effect.
- **Determinism test:** with run-ahead, the visible frame equals the frame a non-run-ahead console shows
  N frames later.

## Platform / App

### Battery saves
- `<game>.sav` under `%LOCALAPPDATA%\GameVault\saves\` (named by ROM hash plus a readable title), never
  next to the ROM.
- Loaded at start; saved on stop, on pause, and every 60 s when it changed (dirty flag from the mapper).
- Atomic write: temp file, then replace.

### Save-state slots
- Hotkeys: F5 quick save and F8 quick load to the selected slot (`docs/design/input.md` §8). The slot
  (1–10) is chosen in the flyout; no extra hotkeys are added.
- Player-page flyout "Save states" lists the slots with a thumbnail and time; save and load go through
  the command queue.
- Files: `%LOCALAPPDATA%\GameVault\states\<hash>\slot-N.state` plus `slot-N.png`.
- Sample data shows filled slots with generated thumbnails.

### Display presets: D3D11 renderer (SPEC §5.2)
- New `D3D11GameRenderer` on a `SwapChainPanel` (Vortice.Direct3D11 / DXGI / D3DCompiler), behind the
  existing `IVideoSink` / `FrameSink`. The Win2D path stays as a fallback when D3D11 init fails.
- Presets, with plain names in Settings → Video and the player menu:
  - **Sharp Pixels**: integer scale, optional 8:7 aspect.
  - **Smooth**: bilinear.
  - **Living Room CRT**: scanlines, aperture mask, light bloom.
  - **Arcade Monitor**: stronger mask and curvature.
- HLSL is embedded as resources and compiled at startup. The presets' math lives in shared constants and
  is unit-tested where it is pure (integer scale and aspect rectangles).

### ViewModels
- `SaveStatesViewModel` (slots, thumbnails, current slot).
- `PlayerViewModel` gains run-ahead state and a battery-save notice.
- Settings → Video gains the preset choice and the 8:7 toggle.
- Everything runs under `--sample-data`.

## Tests
- **Downloaded test ROMs:**
  - `mmc3_test_2` rom singles 1–5. `6-MMC3_alt` expects the NEC behavior and is listed as expected-fail
    under the default submapper.
  - Deferred combined ROMs: `instr_test-v5` `all_instrs` / `official_only`, the `ppu_vbl_nmi` combined
    ROM, `ppu_read_buffer`, and `cpu_dummy_reads` / `cpu_dummy_writes`.
- Unit tests per mapper:
  - Banking registers, mirroring, MMC1 serial-write rules and the MMC3 IRQ counter, using a scripted A12
    sequence.
  - State round-trips.
- Platform: battery save file naming and atomic write (temp folder), slot store, run-ahead determinism,
  and the auto-disable logic.
- ViewModels: the save-state slots.

## Out of scope
Tier 2+ mappers (Phase 6), the visual timeline and rewind (Phase 5), and the game database's run-ahead
"safe" flag (Phase 5, with the catalog). Until then, run-ahead defaults to 1 frame for every game, and the
user can change it.

## Results
- **MMC3:** `mmc3_test_2` 1–5 pass. `6-MMC3_alt` passes when declared as NES 2.0 submapper 4.
  - The A12 filter needs about 4 CPU cycles of low time. The 9-dot low from dot 337 to dot 5 must not
    count when the background uses $1000; that is `4-scanline_timing` #12.
- **Deferred combined ROMs now pass:** `all_instrs`, `official_only`, `ppu_vbl_nmi`, `test_ppu_read_buffer`
  and `cpu_dummy_writes`. `cpu_dummy_reads` is checked by golden image.
- **`MMC1_A12` dropped:** it is an interactive visual test (adjust a delay with the D-pad), not
  machine-checkable. The combined MMC1 ROMs cover MMC1.
- **State:** every mapper round-trips at console level. Snapshots take about 0.005–0.008 ms to save or
  load and are about 14 KB.
- **Run-ahead:**
  - With N frames, the shown frame equals a plain console's frame N ahead.
  - The real timeline is unchanged.
  - Run-ahead switches itself off after 30 frames in a row over budget.
- **D3D11:** the presets are checked on Windows CI with WARP by reading pixels back. Win2D remains the
  fallback, and the fallback reason goes to the log.
- **Speed:** unchanged by the per-dot sprite fetches (about 10× real time on nestest in Release on the cloud
  container).
