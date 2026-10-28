# PlayStation 2 above full speed

**Goal:** PS2 games run above full speed on an ordinary 4-core PC with a GPU. The settings that give up
bit-exactness for it are on by default for normal play. **Settings → PlayStation 2 → Exact emulation** turns them all
off. Netplay, challenge attempts and challenge checks always use the exact paths, whatever the setting.

## 1. Where the time goes (measured first)
Release, the Linux build machine (4 shared cores).

| | Now | Real PS2 |
|---|---|---|
| EE, integer loop (recompiled) | 540–710 M instr/s | ~295 M |
| EE, FPU-heavy loop (recompiled) | 195 M instr/s | ~295 M |
| VU1 transform loop (recompiled, exact floats) | 141 M cycles/s | 295 M |
| VU1 transform loop (interpreted) | 104 M cycles/s | |
| GS, textured, blended, Z-tested triangles (4 threads) | 33–37 Mpixels/s | games draw 100–300+ Mpixels/s |

The homebrew (`vu1-path1`, `gs-texture`) runs at 200–270 frames a second because it draws little. Real games are
held back by two things, the second by far the more:
- the bit-exact float maths in the EE FPU and the VUs (each `Add4`/`Mul4` is a 40–60 cycle chain of integer steps);
- the software GS. Games fill the screen several times a frame with blended, textured passes, and the GPU renderer
  (`gpu-renderers.md`, milestone 3) draws them again but doesn't spare the software GS the work.

## 2. Fast floats (`Ps2FastFloat`)
- The FMAC's and the FPU's add, subtract and multiply in the host's float arithmetic. PS2 numbers are clamped going in
  and coming out:
  - Going in: exponent 255 (an ordinary number on the PS2, infinity or NaN to the host) becomes the host's largest
    float with the same sign. Exponent 0 becomes a signed zero.
  - Coming out: an overflow becomes ±`Ps2VuFloat.Max` with O set, as the PS2 does. A denormal becomes a signed zero
    with U set.
  - This is how PCSX2's default clamping treats PS2 floats.
- **What differs:**
  - Results round to nearest where the PS2 truncates (the last bit).
  - Operands with exponent 255 read as the host's largest float, and a result that lands there (the PS2's top binade,
    past the host's range) saturates to ±`Ps2VuFloat.Max` with O.
  - The MAC and status flags follow from the clamped result, so Z, S, O and U are as on the PS2 except where the
    rounding itself crosses a boundary.
- The divider, square root and the EFU stay exact: they are rare, and games compare their results.
- **Switches:** `Ps2Vu.FastFloat` and `R5900Cpu.FastFloat`, both set by `Ps2Console.FastFloat`. All three are off by
  default, so tests, the challenge verifier and anything else that makes a console get the exact maths unless they
  ask. The recompilers call the same methods, so blocks follow the switch.
- **Host:**
  - `PlayStation2Setup.FastFloat` comes from `!AppSettings.PlayStation2ExactEmulation` (default false), so fast is on.
  - `ConsoleFactory` passes it to the console.
  - The challenge verifier and netplay use `setup with { FastFloat = false }`.
  - A challenge attempt turns the running console's switch off while it lasts, and back on after.
- **Tests:**
  - `Ps2FastFloatTests`: ordinary sums and products equal the exact maths, or differ by one in the last bit, over
    random operands.
  - Clamping going in and coming out, the O/U flags, and signed zeros.
  - Four lanes agree with one lane.
  - The FMAC, OPMULA/OPMSUB and the EE's ADD.S/MUL.S/MADD.S give the same results interpreted and recompiled with the
    switch on.
  - The console's switch reaches the EE and both VUs.
  - Host: exact by default in the factory; fast from settings; exact for the verifier; off during a challenge attempt.
  - `Ps2VuBenchmarkTests` reports exact and fast.
- **The EE's FPU in blocks.** Blocks called the interpreter's `ExecuteCop1` for every FPU instruction, through the
  general call that saves the PC state around it. Now:
  - `JitFpuSingle` takes ADD, SUB, MUL, ABS, MOV, NEG and the accumulator forms with the function as a constant, so
    it inlines into the block.
  - `JitCop1` takes the rest of the arithmetic, MFC1/MTC1 and CVT.S.W.
  - Both return false (the interpreter runs the instruction) when COP1 is unusable or the encoding is reserved. Its
    branches, CFC1 and CTC1 go through the general call as before.
  - `R5900RecompilerTests` now generates every S-format function and CVT.S.W, and runs 16 of its programs with fast
    floats on both sides.
- **Results** (Release, the shared 4-core build machine; `R5900BenchmarkTests` and `Ps2VuBenchmarkTests`, plus a
  stand-alone driver for the EE's FPU loop):

  | | Before | Exact floats | Fast floats |
  |---|---|---|---|
  | EE, FPU loop (MUL.S/ADD.S/SUB.S chains, recompiled) | 107 M cycles/s | 149 M | 213 M |
  | VU1, transform loop, recompiled | 134–141 M cycles/s | unchanged | 161–197 M |
  | VU1, transform loop, interpreted | 80–104 M cycles/s | unchanged | 107–117 M |

  What's left in the EE's FPU loop is mostly entering blocks (`TryRunBlock` compares a block's words with memory each
  time). That is the same for all code, and it is the next place to look on the EE.

## 3. The GPU draws the GS; the software GS skips it
- **Ownership:** the backend (`IPs2GsBackend`) says whether it is **authoritative**. When it is, `Ps2Gs.Flush` hands
  the batch to the GPU and doesn't draw it in software. The pages the batch drew are then marked **GPU-owned**: local
  memory's copy of them is stale.
- **Reading back:** every reader of local memory already flushes first (transfers, CLUT loads, the picture, save
  states, `LocalMemory`). These now also call `EnsureNative(pages)`. For each GPU-owned page among them, the
  backend copies the GPU's copy back to local memory and the page stops being GPU-owned. At 1× that copy is the
  picture itself; above 1× it is each pixel's first sample. Pages read back keep their high-resolution detail on the
  GPU, because the backend records the hash of what it wrote back. The readers are:
  - local-to-host downloads and the source of local-to-local copies;
  - host-to-local uploads and the destination of local copies, when they write part of a GPU-owned page;
  - CLUT loads;
  - the PCRTC's software picture (its display pages);
  - save states, rewind and `LocalMemory` (every page).
- **Never stale:** the backend's `Sync` skips GPU-owned pages, which it holds the only good copy of. A reset or a
  state load clears GPU ownership, since local memory is good again.
- **When:** authoritative only when fast mode is on and Direct3D 11 is there. The renderer is attached at 1× too.
  Until it is attached, and without Direct3D, the software GS draws as now. Netplay and challenges never attach it as
  authoritative.
- **Tests (SwiftShader/lavapipe on Linux, WARP on Windows):**
  - Through an authoritative backend, the scenes of `Ps2GsGpuRendererTests` and the random generator give local
    memory (after a read back) that matches the software GS within the existing tolerance.
  - A download, a CLUT load from a drawn page, a partial upload over a drawn page, and a local copy from a drawn page
    each see what the GPU drew.
  - A save state taken after GPU drawing loads into a software-only console with the same picture.
  - Ownership is cleared by a reset and by a state load.
  - The homebrew draws the same frames as the software GS.

## 4. Recompiling in the background
- **What it fixes:** the EE recompiler compiles a block on the emulation thread the first time it gets hot.
  Games load code all the time (overlays, IOP modules), which shows as hitches.
- **The change:** a hot block is queued to a worker. The interpreter keeps running it until the compiled delegate is
  ready, and the next entry after that uses it.
- **Determinism:** blocks are a cache, not state, and compiled code gives the same results as the interpreter
  (`Ps2RecompilerConsoleTests`). Timing doesn't depend on which path ran, so when a block becomes ready changes
  nothing a game can see.
- **Measure first:** time spent compiling during the autotests' boot and the homebrew's first seconds. Build this
  only if it is a real part of a frame. Otherwise it stays recorded here as not needed.

## 5. Docs
- `docs/bugs.md` and `docs/compatibility.md`: PS2 speed and what fast mode changes.
- `gpu-renderers.md`: "Speed".
- The README and the PS2 manual chapter: the Exact emulation switch, and when to use it (a game that misbehaves).
- The manual's edition history and PDF.

## Order
Three PRs: fast floats (§2), the GPU GS (§3), then background recompilation (§4) if the measurement asks for it.

## Owner checks
- With Exact emulation off, 3D games (Gran Turismo 3, Shadow of the Colossus, Final Fantasy X) run at full speed and
  look the same.
- Turning it on brings back the old speed and the exact maths.
