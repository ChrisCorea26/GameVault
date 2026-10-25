# Phase 3 — APU + Input

**Goal (SPEC §12):** APU, NAudio output, SDL controllers, timing loop.

**Done when:** blargg APU tests pass; a 30-minute audio soak test has no underruns.

## Core

### `Apu/Apu2A03` (NESdev Wiki: "APU", "APU Frame Counter", "APU DMC", "APU Mixer")
- Two pulse channels: envelope, sweep (pulse 1 negates with ones' complement) and length counter.
  Triangle with linear counter. Noise with a 15-bit LFSR and NTSC/PAL period tables. DMC with its memory
  reader, output unit, loop and IRQ.
- Frame counter in 4- and 5-step modes, using the NESdev cycle table. A `$4017` write takes effect after
  3 or 4 CPU cycles. The frame IRQ is raised over three cycles, and the inhibit flag clears it.
- Length-counter halt and reload apply after the frame-counter clock in the same cycle
  (blargg `10.len_halt_timing` / `11.len_reload_timing`).
- `$4015` status read: the frame IRQ flag clears on read; bit 5 is open bus.
- **DMC DMA** stalls the CPU. The CPU performs DMC and OAM DMA on its next read cycle, with halt, dummy
  and alignment cycles, and they interleave when both are pending (NESdev Wiki: "DMA").
- Ticks once per CPU cycle from the bus. IRQs go through `IrqSource.FrameCounter` / `IrqSource.Dmc`.
- **Mixer**: the non-linear lookup tables for pulse and TND. `IExpansionAudio` lets mappers add
  VRC6/FDS/etc. later.
- Output: the APU reports amplitude changes at CPU-cycle timestamps to an `IAudioSink`. The core
  itself never resamples.

### `Audio/BandLimitedResampler` (pure DSP, in Core so it is deterministic and testable)
- Band-limited step synthesis ("BLEP"): each amplitude change at a CPU-cycle time is added as a windowed
  sinc step, phase-interpolated, at the 48 kHz output rate. This resamples from 1.789773 MHz without
  aliasing, at the cost of only the changes, not every cycle.
- The output clock rate can be changed per frame (`SetInputRate`), which is how dynamic rate control
  nudges the ratio by ±0.5 %.
- After resampling, the NES's own filter chain (NESdev Wiki: "APU Mixer"): first-order high-pass at
  90 Hz and 440 Hz, and low-pass at 14 kHz.

## Platform
- `Audio/AudioRingBuffer`: lock-free single-producer/single-consumer float ring buffer.
- `Audio/WasapiAudioOutput`: NAudio WASAPI shared mode, 48 kHz stereo float (mono duplicated), with
  ≤ 40 ms latency (SPEC §5.3). It pulls from the ring buffer and plays silence on underrun, counting each
  one.
- `Audio/RateController`: a pure class. It keeps the ring buffer near its target fill by adjusting the
  resample ratio within ±0.5 %.
- `EmulatorHost` timing (SPEC §5.6):
  - **Audio is the master clock**: emulate a frame whenever the buffer is below target, otherwise wait.
  - With audio muted or no device, it falls back to the Phase 2 timer pacing.
  - **Fast-forward** while Tab is held: uncapped up to 8×, audio muted.
- **Soak test** (`AudioSoakTests`): a simulated WASAPI consumer runs for 30 minutes of *virtual* time
  against the real APU, resampler, ring buffer and rate controller.
  - The device clock is 0.3 % fast in one run and 0.3 % slow in another.
  - It asserts zero underruns and bounded latency. It runs in seconds in CI.
  - A real-device soak is documented as a manual check.

## Input (per [`docs/design/input.md`](../design/input.md) §11, Phase 3 rows)
- `GameVault.ViewModels/Services/Input/PortAssignmentPolicy`: a pure class covering §3 and §5.
  - Connection-order assignment to the lowest free port, with the keyboard always on Player 1.
  - Extra pads stay unassigned. Pins displace unpinned pads.
  - On disconnect, the port is reserved for that pad and the game pauses. Pressing A or Start on an
    unassigned pad takes the port over. Swap is supported.
- `Platform/Input`:
  - SDL2 game controllers via Silk.NET.SDL on a polling thread, with
    `SDL_HINT_GAMECONTROLLER_USE_BUTTON_LABELS=0`.
  - Positional default mapping (§6): South = B, East = A, West/North = turbo B/A.
  - Left stick as 8-way input with a 50 % radial dead zone.
  - Turbo is 2 frames on and 2 off, derived from the frame number.
  - Pad input is ignored while the window isn't in the foreground.
  - Pads still drive XY-focus UI navigation outside the game (§7): South activates, East goes back.
  - Guide, or holding Back + Start for 1 s, toggles pause.
- Two-player keyboard preset (§4.3).
- `IInputDevicesService` (ViewModels) lists ports and sources and supports swap, pin, assign and the
  preset. It has a Platform implementation, plus a sample implementation with the §9 sample devices.
- `InputSettingsViewModel` and the **Settings → Input** section. `PlayerViewModel` shows the disconnect
  message (§5).

## Tests
- **APU unit tests**: length table, envelope, sweep muting, triangle linear counter, noise modes, the
  `$4015` status, frame-counter step timing and IRQ, the mixer tables, state round-trips, and DMC DMA
  cycle cost.
- **Test ROMs** (downloaded, all NROM):
  - `apu_test` single ROMs 1–8, `apu_reset` (6), `cpu_interrupts_v2` single ROMs 1–5, and the
    `instr_timing` single ROMs (deferred from Phase 1).
  - The 2005 `blargg_apu` suite (11) and `dmc_tests` (4).
- **Resampler**: DC passes, a 1 kHz square keeps its fundamental, and no energy appears above
  20 kHz for a 15 kHz input. Changing the rate keeps the sample count consistent.
- **Input**: policy cases from input.md §12, turbo timing from the frame count, the stick dead zone, and
  the two-player preset.
- **ViewModels**: Settings → Input with the sample devices, and swap applied through a fake service.

## Out of scope
Mappers beyond NROM, save states, run-ahead and D3D11 presets (Phase 4). Rewind and the Zapper (Phase 5).
Slow motion and other assist features (Phase 6).

## Results
- **APU suites:** apu_test 1–8, apu_reset (6), blargg_apu_2005 (11), cpu_interrupts_v2 1–5 and
  instr_timing 1–2 all pass.
- **dmc_tests:** these report by sound, so CI only checks that they run to completion.
- **Known gaps** (tracked in `docs/compatibility.md`):
  - `sprdma_and_dmc_dma` fails.
  - `dmc_dma_during_read4` is fetched but not asserted yet.
- **Audio soak:** 30 virtual minutes at the exact device rate and at ±0.3 % drift. Zero underruns, average
  latency under 40 ms, peak under 50 ms, and frame count within ±0.5 % of nominal.
- **Speed:** about 13× real time with the APU, PPU and audio resampling running (Release build, 2.1 GHz
  cloud Xeon).
- **Controller hardware:** SDL controller behavior on real hardware (hot-plug, disconnect pause, take-over)
  is a manual check on Windows. The policy and manager logic is covered by unit tests with a fake backend.
