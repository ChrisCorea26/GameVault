# Phase 17 follow-up — PlayStation 2 speed, and choosing BIOS files

**Goal:** PS2 games ran well below full speed. Phase 17's results put most of the time in the EE interpreter
(64–71 M instructions a second against the real 294.9 MHz) and the rest in the software GS (22 Mpixels a second
textured). Make both faster without changing a single result. Also give players an obvious place to supply their own
BIOS files.

**Done when:**
- the EE runs recompiled, cycle for cycle the same as the interpreter: same output, picture, cycle count and save
  state on every test program;
- the GS draws on several cores with local memory byte-identical to one renderer's;
- every path box in Settings has a file picker, and BIOS files are copied into GameVault's folder;
- all tests pass, with the manual and `docs/bugs.md` updated.

It also draws the N64's RDP on several cores (§3), the same way, as the request that started this asked.

**Not in this change (next steps, §6):** the VU recompilers and the GPU renderers.

## 1. The EE recompiler (`R5900Cpu.Recompiler*.cs`)

**Blocks.** A block runs from an address up to a branch and its delay slot, or up to SYSCALL, BREAK or a COP0
instruction, or up to the page's end (at most 128 instructions). The run loop looks for one where a block can start:
at the slice's start, after a block, after a delay slot or after an interrupt. A block is compiled once it has been
reached 16 times, into a `DynamicMethod` on `R5900Cpu`. .NET's JIT turns that into machine code.

**Exactly the interpreter.** The interpreter checks three things before every instruction: the slice's end,
`StopRequested`, and a pending interrupt.
- A block only starts when all its instructions fit before the slice's end.
- Inline code can't change the other two.
- So the checks can only fail after a call out of the block: an instruction handed to the interpreter, the slow path
  of a load or store, or a stall (MFHI/MFLO, LWC1, FPU waits).
- After each such call, the block re-checks and leaves when one fails. It leaves the CPU's fields (PC, next PC,
  delay slot, current PC, Cycles) exactly as the interpreter would.
- Cycles are added in bulk, before anything that reads them.
- Idle-loop skipping, exceptions, branch-likelies and the BC1-in-delay-slot quirk all go through the interpreter's
  own code.

**What is inline:**
- The integer ALU and shifts. ADD, SUB and DADD go to the interpreter on overflow.
- MULT/MULTU, MFHI/MFLO, MTHI/MTLO, MFSA/MTSA.
- LB … LD and LQ. Stores SB … SD and SQ.
- LWC1/SWC1, with fast paths for KSEG0/1 and the TLB-mapped segments into RDRAM and the scratchpad.
- Branches and jumps.

**Called directly:** FPU, MMI and COP2 instructions call the interpreter's handlers (`ExecuteCop1`, `ExecuteMmi`,
`ExecuteCop2`) with the PC state set as constants. This skips the interpreter's decode and per-instruction loop work.

**Everything else** (the unaligned loads and stores, traps, DIV, LQC2/SQC2, COP0…) runs through `Execute` for that
one instruction.

**New code:**
- Every block keeps the words it was built from and compares them with memory each time it starts. So code loaded
  by DMA, by the IOP or by the CPU itself never runs stale.
- An EE store into a 256-byte granule a block was built from takes the slow path and ends the block. A block that
  rewrites its own later instructions behaves as the interpreter does.
- Blocks are a cache, not state: save states don't change.

**Switch:** `R5900Cpu.RecompilerEnabled`, on by default where the runtime can compile code
(`RuntimeFeature.IsDynamicCodeCompiled`).

**Tests:**
- `R5900RecompilerTests`: 48 random programs run on two CPUs, interpreted and recompiled. The programs cover
  every inline instruction, overflow, misaligned and I/O accesses, self-modifying stores, SYSCALL/BREAK/traps, FPU,
  MMI, interrupts and stops from a device. The whole CPU state and memory must match after each of 400 slices.
  There is also a self-rewriting loop, and a block whose code is replaced from outside.
- `Ps2RecompilerConsoleTests`: every ps2autotests program (55) and the 4 homebrew ELFs, recompiled against
  interpreted. Output, picture, cycles and the full save state are compared every 15 frames for 90 frames.

## 2. The GS on several cores (`Ps2Gs.Batch.cs`, `Ps2Gs.Renderer.cs`)

**Batches.**
- A completed primitive is queued with a snapshot of the decoded draw state (`DrawState`). Indexed textures snapshot
  their palette too.
- The queue is drawn when something needs local memory as the drawing left it:
  - a transfer that touches the batch's pages;
  - a local-to-local copy or a read-back;
  - a CLUT load from pages the batch draws to;
  - the displayed picture, or a save state;
  - `LocalMemory`, `ReadPixel` and `WritePixel`;
  - or when the queue holds 4096 primitives.
- Nothing the EE can see depends on drawing except local memory (events, FINISH, SIGNAL and LABEL never did). So
  queuing changes no result.

**Sharing out.**
- Drawing a batch can be split into bands: renderer *i* draws the rows `y` with `(y >> 4) % N == i`.
- Every per-row step (edge functions, attribute planes) runs for all rows. So a pixel's value doesn't depend on which
  renderer drew it.
- The pieces run through `IParallelWork` (Core), which the platform implements with `RenderThreads`. The emulation
  thread draws its share and waits for the rest, so the core never runs alongside its own drawing.

**When rows could share memory**, the batch is drawn first, or the state is drawn by one renderer:
- **Draw the batch first** when the new state:
  - reads a texture from pages the batch draws to (render to texture);
  - draws over pages the batch reads textures from;
  - or draws to the batch's pages through another layout (another base, width or format).
- **Draw with one renderer** a state whose texture or Z buffer overlaps its own frame buffer, or whose frame is
  narrower than its scissor (rows alias).
- Batches under 8192 pixels are drawn by one renderer, since splitting them costs more than it saves.

**Tests:** `Ps2GsParallelTests`.
- 24 random scenes run on a lone renderer, on three bands run in reverse order, and on five real threads. The scenes
  have overlapping frame and Z buffers, render to texture, uploads, CLUT loads, copies and read-backs. Local memory
  and read-backs must be identical.
- Turning the hazard rules off makes 24 of the 25 fail.
- A dedicated test covers drawing over a texture the batch still samples.
- The homebrew must match frame for frame.
- All of Phase 17's GS tests still pass.

## 3. The N64 RDP on several cores (`N64Rdp.Batch.cs`)

**Why it differs from the GS.** The RDP draws into RDRAM, which the CPU can read the moment the RDP's command run
returns. So nothing is deferred past the run: within one run (one write of DPC_END), drawing commands are recorded
and drawn before the run returns.

**Replicas.**
- The RDP itself runs every non-drawing command as it arrives, so its modes, tiles, TMEM and save state stay
  current. Loads still read RDRAM here, after the recording is drawn when they would read what it draws.
- Drawing is done by up to 8 replicas of the RDP, synced from it (through its save state) when a recording starts.
  Each replays the recorded commands and draws its band: every Nth strip of 8 lines.
- Every per-line step (edge walking, the DPC clock) runs for every line. Replica 0's clock and random generator are
  taken back by the RDP.

**What carries from pixel to pixel.**
- The combiner's inputs keep their last value, and one-cycle mode's COMBINED is the previous pixel's result.
  These are part of the save state.
- Every write to an input is stamped with its command and line. After each shared stretch, every replica (and the
  RDP) takes each input from the replica with the latest stamp. That is the value one RDP's last pixel left.
- A primitive is drawn by replica 0 alone, in order, when it:
  - reads an input before writing it (the COMBINED carry, or a texel the two-cycle swap writes after reading);
  - uses the random generator (noise, random colour dither, dithered alpha compare);
  - or its rows can alias, meaning a Z buffer over its colour image, or a right edge past the image's last column,
    which is the next row's first pixel.
- A primitive that reads an input it doesn't write, after earlier recorded primitives wrote it, starts after a merge.

**Memory.** The recording is drawn first when:
- a load reads RDRAM it draws to;
- a primitive would draw over a recorded load's source, or over the commands still to read;
- or a primitive draws the recording's memory through another layout.

**Tests:** `N64RdpParallelTests`.
- 24 random command lists run on a lone RDP, on three bands in reverse order, and on four threads. The lists have
  random modes and combiners, overlapping colour and Z images, loads from what was just drawn, noise and dither, and
  command lists placed where they get drawn over. RDRAM, interrupts and the full RDP save state must be identical.
- Turning off the carry rules fails all 24 lists, the memory rules 22, and the right-edge rule 21.
- All 117 of PeterLemon's RDP programs run through the whole console on four threads. RDRAM and the console's save
  state match every 10 frames.

## 4. Choosing BIOS files (`SettingsViewModel`, `BiosImporter`, `IFilePathPicker`)

- Every path box in Settings has a **Browse…** button.
- The Disk System, PlayStation and PlayStation 2 BIOS boxes have **Choose BIOS file…**:
  - it checks the file (size, and for the PS2 its version);
  - it copies the file to `%LOCALAPPDATA%\GameVault\bios\<fds|ps1|ps2>\` and uses the copy.
- The "needs a BIOS" messages point at the button.
- This fixes GV-009.

## 5. Results (Release, the shared 4-core build machine)

| | Before | After |
|---|---|---|
| EE, integer loop (loads, stores, ALU, branches) | 121–147 M instr/s | 540–710 M instr/s (4.5–5×) |
| EE, load-heavy loop | 135 M instr/s | 430 M instr/s (3.2×) |
| EE, FPU-heavy loop | 88 M instr/s | 195 M instr/s (2.2×) |
| EE, MMI-heavy loop | 92 M instr/s | 192 M instr/s (2.1×) |
| GS, 2000 textured bilinear blended Z-tested triangles a frame | 12.8 Mpixels/s | 27 (2 threads), 33–37 (4 threads) |
| VU1, transform loop | 77 M cycles/s | unchanged (§6) |
| N64 RDP, 300 shaded textured Z-buffered triangles | 18.8 scenes/s | 40.2 (2 threads), 56.0 (4 threads) |

The EE now runs integer code faster than the real CPU. FPU-heavy code is limited by the PS2's non-IEEE float rules,
which are done bit-exactly in software. Benchmarks: `R5900BenchmarkTests` (`GAMEVAULT_EE_BENCHMARK=1`),
`Ps2GsParallelTests.Benchmark_reports_pixels_per_second` (`GAMEVAULT_GS_BENCHMARK=1`),
`N64RdpParallelTests.Benchmark_reports_pixels_per_second` (`GAMEVAULT_RDP_BENCHMARK=1`) and `Ps2VuBenchmarkTests`
(`GAMEVAULT_VU_BENCHMARK=1`).

## 6. Next steps

1. **The VU recompilers.**
   - VU1 runs about 77 M cycles a second on a transform loop. Its float maths is already SIMD; the time is in the
     per-pair pipeline bookkeeping (stalls, the flag queue, Q/P).
   - A recompiler would work out the stalls of a straight run of pairs once, as the EE's blocks do, and keep the
     interpreter as the reference, checked by the same kind of differential tests.
2. **A GPU renderer for the GS** (Direct3D 11, through the existing presenter), for upscaling and very heavy scenes.
   - This needs a Windows machine with a GPU to build and check.
   - It can't be checked in the Linux build environment this change was made in, so it wasn't attempted here.
   - The software GS stays the reference. Its batches are already the unit a GPU renderer would take.
3. **A GPU renderer for the N64 RDP**, as SPEC §7.11 asks, with the same caveat as 2.
