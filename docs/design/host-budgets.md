# Host budgets — rewind, run-ahead and netplay per system

Rewind, run-ahead and netplay's rollback all save (and load) the whole console while the game runs. Each must fit
inside a frame: 16.6 ms at 60 frames a second, together with the frame itself. This page records, per system, what
the emulator host (`src/GameVault.Platform/Emulation`) does about it. The rules live in `SystemBudgets`; the
PlayStation 2's numbers are measured by `PlayStation2HostTests.Ps2_state_rewind_and_run_ahead_costs_fit_the_budgets`.

## 1. The features

- **Rewind** (SPEC §7.2, `RewindBuffer`): a snapshot every *interval* frames, XORed with its group's keyframe and
  compressed with Brotli (quality 1); the oldest group is dropped past the memory cap (256 MB by default, Settings →
  Rewind memory). Holding rewind restores one capture per displayed frame, so each step goes back one interval.
- **Run-ahead** (SPEC §5.5, `RunAhead`): every frame, save, run 1–3 frames silently, show the last, load. It turns
  itself off after 30 frames in a row over budget.
- **Netplay** (SPEC §7.7, `RollbackSession`): saves every frame and, on a misprediction, loads and replays the frames
  since.

## 2. Per system

| System | State | Save / load | Rewind | Run-ahead | Netplay |
|---|---|---|---|---|---|
| NES, Master System, SG-1000, Genesis, SNES, Apple II | small | — | every 2nd frame | 0–3 frames (setting) | Yes (not Disk System games) |
| PlayStation (Phase 15) | 3.6 MB | — | every 2nd frame (about 3 ms a frame, phase-15 results) | 0–3 frames | Yes |
| Nintendo 64 (Phase 16) | 8 MB | 0.65 ms / 0.87 ms (phase-16 results) | every 2nd frame | 0–3 frames | Yes |
| **PlayStation 2 (Phase 17)** | **49 MB** | **6–7 ms / 7 ms** | **every 30th frame (½ s), compressed on a worker** | **Off** | **No** |

## 3. The PlayStation 2

A PS2 state is 51.4 million bytes (about 49 MB): 32 MB of RDRAM, the 8.6 MB memory card (inside the IOP's state, as
the IOP owns the card), 4 MB of GS memory, 2 MB of IOP RAM, 2 MB of sound RAM and the VU memories. Measured on the
homebrew ELFs (`tests/Ps2Homebrew/elf`) on the CI-class machine the tests run on (shared, so the numbers are rough):

| Operation | Time |
|---|---|
| A frame (vu1-path1, gs-basic) | 4–8 ms |
| Save into a reused `StateWriter` | 6–7 ms (the first save, which grows the writer to 51 MB, about 70 ms) |
| Load | 7 ms |
| A rewind capture: save, XOR, Brotli | 60–110 ms |
| Emulation thread's part of a capture with background compression (the save, and swapping writers) | 6–11 ms |
| A frame with one frame of run-ahead | 25–30 ms |

Decisions:

- **Rewind: a capture every 30 frames (about every half second), compressed in the background.** A capture every
  2nd frame would cost 30–55 ms a frame. Only the snapshot (6–7 ms) is taken between frames; the XOR with the keyframe
  and the Brotli compression run on a worker, one capture at a time (`RewindBuffer.CompressInBackground`: the buffer
  swaps between two writers, and every other member waits for the capture in flight). That is about 0.2–0.4 ms a frame
  on the emulation thread spread over the interval, and about 10–20% of one other core. Holding rewind steps back half
  a second per step (decompressing and loading take about 50–100 ms a step, so it shows about 10 steps a second).
  The homebrew's captures compress to about 32 KB each (most of RDRAM is zeros); a commercial game's RDRAM is full, so
  its keyframes may take 10–25 MB and a 256 MB cap holds a shorter history. Settings → Rewind memory says PS2 games
  rewind in half-second steps.
- **Run-ahead: off.** Even one frame costs a save, an extra frame and a load (25–30 ms on the homebrew, more with a
  real game's frame), well past the 16.6 ms budget. `RunAhead.Allowed` is false while a PS2 game runs, whatever the
  setting says; Settings → Reduce input lag says it's always off for PS2 games.
- **Netplay: not offered.** Rollback saves every frame (6–7 ms) and replays up to several frames with a load on each
  misprediction, which can't keep up. Hosting or joining a PS2 game fails with a message saying so
  (`SystemBudgets.PlayStation2NetplayMessage`), and the Netplay flyout says it before the player tries.
- **Save states, the timeline and challenges** work as on the other systems; each state is about 49 MB on disk (the
  host doesn't compress them). A challenge's starting point is a state in the challenge file, so a PS2 challenge file
  is about 68 MB (Base64).
- **Discs are not in states.** A state loaded into the running console keeps the disc in its drive; a new console (a
  challenge check, netplay on other systems) opens its own copy of the disc first, then loads the state.
- **The memory card is in states** (as on the PlayStation): loading a state or rewinding brings back the card as it
  was, and the host saves the card file whenever the card differs from what it last saved (checked on pause, stop,
  exit and about once a minute).
