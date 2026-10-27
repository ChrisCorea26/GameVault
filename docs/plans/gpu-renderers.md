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

## Later milestones
- **2. The N64 RDP:** the RDP's commands drawn by a compute-shader rasteriser at a higher resolution, the software
  RDP still authoritative for memory the game reads back.
- **3. The PlayStation 2 GS:** the GS drawn on the GPU with its local-memory formats and render-target tracking; this
  is also where the PS2 gains speed, so the GS's software renderer would stop drawing frames nobody reads back.

## Owner checks
With the owner's games at 2× and 3×: 3D games (Ridge Racer, Gran Turismo, Final Fantasy VII's battles) look sharper
and the same otherwise; 2D games and menus look the same as at 1×; switching resolution mid-game works.
