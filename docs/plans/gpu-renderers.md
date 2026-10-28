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

### Starting up
The HLSL compiler takes minutes over the kernel, so `D3D11Compute` keeps compiled kernels on disk (`%LOCALAPPDATA%\GameVault\Shaders`, named by a hash of
the source and settings), and the host makes the renderer on a background thread and attaches it between frames when
it's ready: the game starts at 1× and turns sharp. Both renderers seed what they need when attached mid-game.

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

## Milestone 3: the PlayStation 2 GS (`Ps2GsGpuRenderer`)

### Why scaled local memory
PS2 games draw into the GS's 4 MB of local memory through swizzled page layouts (32-, 24-, 16-, 8- and 4-bit, colour
and Z), and read it back as textures all the time: render targets become textures for bloom, shadows, depth of
field and the half-resolution passes most games use; frame and Z buffers are cleared through other formats; the
CLUT is loaded from what was drawn. Tracking "surfaces" for all that is where GPU PS2 renderers spend most of their
complexity. So the GPU keeps **local memory itself at S×S**: every native pixel slot holds S×S samples, and a pixel
(x, y) of any buffer at any layout addresses native slot `PixelAddress(psm, bp, bw, x, y)`, sample `(fy × S + fx)`.
Drawing, texturing, reading a frame buffer as a texture through another format, and the CLUT all go through the GS's
own address functions, so every format and every reinterpretation works at the internal resolution with no surface
tracking.

### How it fits
- **The software GS stays authoritative**, as for the PlayStation and the N64: local memory, downloads to the EE,
  save states, rewind, run-ahead and netplay are unchanged. The GPU draws the picture only.
- **The tap:** `IPs2GsBackend` (core, internal; the Platform assembly sees the GS's batch types). The GS already
  queues primitives with their draw state and draws them in batches (Ps2Gs.Batch.cs: a batch never reads pages it
  draws to, or draws them through two layouts; states whose own rows share memory are serial). Before drawing a
  batch the GS hands it over (commands, states with their CLUT snapshots, and the pages it draws to and reads), and
  after drawing it says which pages it drew.
- **Memory:** one GPU buffer of `LocalMemorySize / 4 × S²` words: native 32-bit word `w`, sample `k` at
  `w × S² + k`. 16-, 8- and 4-bit pixels share a word as they do natively, each sample word holding that sample of
  every pixel in the word (sub-word writes are atomic, as neighbouring pixels belong to other threads).
- **Keeping in step without hooks:** before a batch uses a page (and before the picture shows one), a hash of the
  page in local memory is compared with the one taken after the GPU last drew it; a page changed any other way (an
  upload, a local copy, the CPU, a reset or a state load) is seeded again from local memory, each word in every
  sample, so the CPU's pictures and videos show at native resolution, as they are. A state load needs nothing more:
  a page it left as the GPU drew it keeps its detail (run-ahead loads a state every frame).
- **HD pages:** pages the GPU drew are sampled on their sample grid when read as textures (S texels to the native
  texel), so render targets keep their detail; pages seeded from local memory are sampled as at 1×, so uploaded
  textures filter as they do natively.
- **The kernel:** a port of `Renderer` (points, lines, sprites, triangles; texturing with every PSM and the CLUT,
  bilinear, mipmaps, the region clamps; fog; the alpha, destination-alpha and Z tests; the blender; dithering;
  FBMSK; the 16/24/32-bit frame and Z formats). One thread per scaled pixel of the batch's bounding box walks the
  batch's primitives in order, as the N64 kernel does. Coverage is tested at the sample's position (pixel + (f + ½)/S),
  attributes are evaluated there, and textures are sampled at the scaled texel position: a texel's native slot, then
  its sample from the coordinate's fraction, so a 2× render target read as a texture gives back its 2× detail. Serial
  states are dispatched a primitive at a time.
- **Arithmetic:** the CPU sets each primitive up in doubles as the software GS does (edge functions, attribute planes);
  the kernel evaluates edges in 64-bit integers (emulated with 32-bit words) and Z in 48.16 fixed point, so coverage
  and depth match, and the other attributes in 32-bit floats (Direct3D 11 can't divide doubles), which round a few
  colours and texture coordinates the other way.
- **The picture:** the PCRTC's two circuits read from scaled memory at S× (their merge, the background colour and
  field mode as in Ps2Gs.Output.cs).

### Not reproduced
- Bit-exactness: float interpolation (above). Comparisons allow a few words in a thousand.
- Local-to-host downloads read the software GS's memory, so effects the EE computes from a downloaded picture show
  at native resolution; so do CLUTs loaded from what was drawn.
- A serial state's primitive whose texture is its own frame buffer (feedback) is drawn by many threads at once, so
  which texels it sees of its own pixels can differ from the software GS's row order.

### Speed
At first the software GS kept drawing (it is what downloads and save states read), so this made PS2 games sharper,
not faster. **Done since** (`docs/plans/ps2-full-speed.md` §3): for normal play the renderer draws in the software
GS's place, also at 1×, and the GS reads back the pages it needs.

### Tests
- **The kernel on Linux** (lavapipe in CI, SwiftShader locally) and **on Windows** (WARP), with
  `GAMEVAULT_REQUIRE_GPU=1` in CI so a missing device fails instead of skipping: register writes through both GSs at
  1×, comparing local memory where the GPU's copy is in step. Untextured and Gouraud triangles with Z, CLUT and
  bilinear textured sprites, a perspective triangle blended, a render target read back as a texture, a 16-bit
  dithered frame with alpha test and FBMSK, and random scenes (Ps2GsParallelTests's generator: every primitive and
  format, overlapping buffers, uploads, copies, CLUT loads).
- **The picture:** at 1× it is the software GS's; at 2× each block of a uniform area holds the 1× pixel; GameVault's
  PS2 homebrew through the whole console draws the same frames on the GPU.
- **The host:** at 2× the GPU picture (1280×896 for a 640×448 game) shows on Windows and the software one elsewhere.
- **Settings → PlayStation 2 → Internal resolution:** 1× (original), 2×, 3×, 4×.

## Owner checks
With the owner's games at 2× and 3×: 3D games (Ridge Racer, Gran Turismo, Final Fantasy VII's battles; Super Mario 64,
GoldenEye; Gran Turismo 3, Shadow of the Colossus's bloom) look sharper and the same otherwise; 2D games, menus and
videos look the same as at 1×; switching resolution between games works.
