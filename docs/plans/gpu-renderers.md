# GPU renderers (outstanding-work item 14)

**Goal:** Direct3D 11 renderers for the PlayStation GPU, the N64's RDP and the PlayStation 2's GS, drawing at a
higher internal resolution, with the software renderers kept as the reference. Each system is a milestone, merged
before the next starts. The PlayStation's is first: its GPU is the simplest of the three and the one players most
want sharpened.

## How the renderers fit
- **The software renderer stays authoritative.** It keeps drawing every primitive into the emulated VRAM, so
  everything the game can see (VRAM reads, copies, textures it drew itself), save states, rewind, run-ahead, netplay
  and verified challenges stay exactly as they are and stay deterministic.
- **The GPU renderer draws the picture.** The core hands each primitive (after decoding, with its draw state) to an
  optional backend (`IPsGpuBackend`, no Direct3D types in the core). The backend draws it again, scaled, into its own
  VRAM render target, and the host shows that instead of the software frame. With no backend attached nothing
  changes.
- **Textures come from the authoritative VRAM.** Textured primitives sample a native-resolution copy of the emulated
  VRAM (a 16-bit integer texture, so 4- and 8-bit pages and their CLUTs decode exactly). Rows the game has changed
  (by drawing, uploading or copying) are re-sent before a textured primitive whose page or CLUT covers them.
- **After a state load** (and on attach) the backend resynchronises: the whole native VRAM is uploaded and drawn into
  the scaled target.

## Milestone 1: the PlayStation GPU (`D3D11PsGpuRenderer`)
- Flat and Gouraud triangles and quads, textured or not (4-bit, 8-bit and 15-bit pages, CLUTs, raw or modulated
  textures, the texture window), rectangles (all sizes, flips), lines, fills, CPU-to-VRAM uploads, VRAM-to-VRAM
  copies, the drawing area (scissor) and offset.
- Semi-transparency: the four modes as blend states; textured primitives draw in two passes so only texels with bit
  15 blend. Colours are taken to 15 bits before blending, as the GPU stores them.
- The mask bit: "set mask" and textures' bit 15 write it; "check mask" is honoured on opaque primitives.
- Not drawn by the GPU renderer (the software picture shows instead): 24-bit display mode (MDEC video).
- Not reproduced: dithering (dropped at higher resolutions, as other GPU renderers do).
- **Internal resolution** in **Settings → PlayStation**: 1× (software, the default), 2× or 3×. Without Direct3D 11 the
  software renderer is used.
- Video frames carry the larger picture (up to 1920 × 1536).

### Tests
- On Windows (CI's Windows job, on WARP, Direct3D's software device): command lists drawn through both renderers at
  1× and compared pixel by pixel within a tolerance for the interpolation and blending differences (flat, Gouraud,
  each texture depth, the semi-transparency modes, rectangles, lines, fills, uploads, copies, the mask bit), and at
  2× compared after scaling down.
- Everywhere: the core's backend calls (what's handed over, in order) and the host's choice of picture.

## Milestone 2: the N64 RDP (`N64RdpGpuRenderer`)

### Why a compute port, not hardware triangles
The software RDP is already what keeps heavy N64 scenes below full speed (phase-16.md: 8–40 Mpixels a second
against the 15–20 a heavy scene needs), so upscaling it on the CPU (4× the work at 2×, 9× at 3×) isn't an option.
The RDP's pipeline doesn't map onto fixed-function blending either: the blender reads the framebuffer with its own
formula and coverage rules, and the Z test compares compressed depth with the primitive's slope. So the RDP is
ported to an HLSL compute shader that does the same integer arithmetic as `N64Rdp`, per pixel, on the GPU.

### How it fits
- **The software RDP stays authoritative**, as on the PlayStation: RDRAM, save states, rewind, run-ahead and netplay
  are unchanged. The GPU renderer draws the picture only, and the host shows it instead of the VI's frame when it
  has the framebuffer the VI shows.
- **The tap:** `IN64RdpBackend` (core, no Direct3D types). For every primitive (triangle, fill rectangle, texture
  rectangle; all reach the RDP's edge walker in one form) the RDP hands over the walker's inputs (edges, slopes,
  the attributes' start values and their DxDx, DxDe and DxDy) and the state that draws it (Set Other Modes, Set
  Combine, the colours, scissor, Set Convert and Set Key, the colour and Z images, the eight tiles). After each
  TMEM load it hands over TMEM. After a state load the backend resynchronises. With no backend attached nothing
  changes.
- **The kernel:** one dispatch per batch of primitives. Each thread owns one pixel of the scaled surface and walks
  the batch's primitives in order, so draw order (and every read of the framebuffer and Z buffer) is exactly the
  RDP's, with no races. Per primitive it rebuilds its scanline's quarter-scanline spans from the edges the same way
  the walker does (every per-line value is a linear function of the line, and HLSL's 32-bit multiply wraps like the
  walker's additions), works out its coverage and attributes, and runs the pixel pipeline: the combiner, alpha
  compare, the Z test with compressed depth, the blender, coverage, dither and the framebuffer formats.
- **Surfaces:** a scaled colour surface per colour image (address, width, pixel size) and a scaled Z surface per Z
  image, both GPU buffers of the RDP's own pixel values (16-bit, 32-bit or 8-bit colour; 16-bit compressed Z), each
  native pixel S×S. A surface is taken when a primitive that draws to it arrives, before the software RDP draws that
  primitive: it is seeded from RDRAM then when it is new, and again when RDRAM under it no longer matches the hash
  taken when the RDP last finished a run (the CPU, or a state load, drew there). A Z buffer cleared through a colour
  image (or a colour image read from a Z buffer) is the same memory under two keys: the surface drawn more recently
  is copied into the other on the GPU, as RDRAM may not hold it yet (the software RDP draws a batched run only at
  its end) and never holds it above 1×. Surfaces cover the scissor box, so they rarely grow mid-run.
- **Textures from TMEM:** primitives sample a copy of TMEM as the software RDP loaded it (from the authoritative
  RDRAM), so render-to-texture effects keep working at native resolution.
- **The picture:** the VI's origin, stride and pixel size pick the surface; the frame is that surface's rows at the
  internal resolution. Without one (the CPU drew the frame, or the VI shows memory the RDP never drew), the VI's own
  frame shows.

### Not reproduced (the software RDP still does all of these in RDRAM)
- **Sequential details:** the RDP's random noise and random dither (the GPU uses a hash of the pixel), one-cycle
  mode's COMBINED input (the previous pixel's result; the GPU uses 0), and combiner inputs a pixel doesn't set
  keeping the previous pixel's value.
- **The VI's filters**, as in the software path.

### Steps
One PR (2a and 2b together; the kernel's texture unit was ported with the rest): the tap, the batches and surfaces,
the edge walk, coverage, shade and Z, the combiner, alpha compare, the blender, fill and copy modes, two-cycle mode,
textures (tiles, TMEM's formats and interleave, the TLUT, filtering, the LOD and detail/sharpen, perspective, YUV,
the key), the host (**Settings → Nintendo 64 → Internal resolution**: 1× (original), 2×, 3×, 4×) and the picture.

### Speed
The software RDP still draws every frame into RDRAM (it is what the game and the CPU read back), so the GPU
renderer makes N64 games sharper, not faster. Letting the software RDP skip pixels nobody reads back is later work.

### Tests
- **The kernel on Linux:** the shader also compiles to SPIR-V (glslang's HLSL front end) and runs on a Vulkan
  software device (SwiftShader or Mesa's lavapipe) through the same buffers, so the test bench draws command lists
  through both RDPs at 1× and compares every pixel of RDRAM's colour and Z images with the software RDP's, and checks
  the 2× picture against the 1× one (each 2×2 block holds the 1× pixel where the primitive covers it wholly). The
  tests skip where no Vulkan device is installed.
- **The renderer on Windows:** the same tests on Direct3D 11 (`D3D11Compute`) on WARP in CI's Windows job, and on
  lavapipe in the Linux job.
- **PeterLemon's RDP programs** (fetched) through the whole console with the renderer attached at 1×: the GPU's
  picture of what the VI shows matches the software RDP's. Opt-in (`GAMEVAULT_N64_GPU=all`, or part of a program's
  path): software Vulkan devices execute every branch of the kernel for every pixel (a whole-screen batch takes
  SwiftShader most of a minute), so it is for runs on a real GPU or for a few programs at a time.
- **The host:** at 2× the GPU picture shows on Windows and the VI's frame elsewhere.

## Later milestones
- **3. The PlayStation 2 GS:** the GS drawn on the GPU with its local-memory formats and render-target tracking; this
  is also where the PS2 gains speed, so the GS's software renderer would stop drawing frames nobody reads back.

## Owner checks
With the owner's games at 2× and 3×: 3D games (Ridge Racer, Gran Turismo, Final Fantasy VII's battles) look sharper
and the same otherwise; 2D games and menus look the same as at 1×; switching resolution mid-game works.
