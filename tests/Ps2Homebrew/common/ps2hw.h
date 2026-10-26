/*
 * GameVault PS2 homebrew: hardware definitions shared by the test programs.
 * GameVault's own code (see ../README.md for the licence).
 *
 * Everything here is bare metal: no BIOS, no syscalls. Hardware is reached through KSEG1
 * (0xA0000000 + physical, uncached), code and data live in KSEG0 (see link.ld).
 * References: ps2tek ("EE Map", "GIF", "DMAC", "GS Register List", "VIF"), the Linux PS2 port's
 * gs-registers.h for the privileged GS register fields.
 */
#ifndef GV_PS2HW_H
#define GV_PS2HW_H

typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
typedef unsigned long long u64;
typedef signed short s16;
typedef signed int s32;

/* One 128-bit quadword, the unit of DMA, GIF and VIF transfers. */
typedef struct { u32 w[4]; } __attribute__((aligned(16))) qword_t;

#define KSEG1(phys) (0xA0000000u | (u32)(phys))
#define REG32(phys) (*(volatile u32 *)KSEG1(phys))

/* Physical address of a KSEG0 object (what the DMAC wants; bit 31 would select the scratchpad). */
#define PHYS(p) ((u32)(p) & 0x1FFFFFFFu)
/* Uncached (KSEG1) alias of a KSEG0 object: the CPU writes DMA buffers through it, so no cache flush is needed. */
#define UNCACHED(p) ((void *)(PHYS(p) | 0xA0000000u))

/* ---- DMAC (ps2tek "DMAC I/O") ---- */
#define D1_CHCR REG32(0x10009000) /* VIF1 */
#define D1_MADR REG32(0x10009010)
#define D1_QWC  REG32(0x10009020)
#define D1_TADR REG32(0x10009030)
#define D2_CHCR REG32(0x1000A000) /* GIF (PATH3) */
#define D2_MADR REG32(0x1000A010)
#define D2_QWC  REG32(0x1000A020)
#define D2_TADR REG32(0x1000A030)
#define D_CTRL  REG32(0x1000E000)
#define D_STAT  REG32(0x1000E010)
#define D_PCR   REG32(0x1000E020)
#define D_SQWC  REG32(0x1000E030)
#define D_ENABLEW REG32(0x1000F590)

#define CHCR_DIR_FROM_MEM 0x001
#define CHCR_MOD_NORMAL   0x000
#define CHCR_MOD_CHAIN    0x004
#define CHCR_TTE          0x040
#define CHCR_STR          0x100

/* Source-chain DMAtag (ps2tek "DMAC Chain Mode"): low 64 bits; the high 64 bits are sent with TTE. */
#define DMATAG_REFE 0u
#define DMATAG_CNT  1u
#define DMATAG_NEXT 2u
#define DMATAG_REF  3u
#define DMATAG_END  7u
#define DMATAG_LO(qwc, id) (((u32)(qwc) & 0xFFFFu) | ((u32)(id) << 28))

/* ---- GIF (ps2tek "GIF I/O") ---- */
#define GIF_CTRL REG32(0x10003000)
#define GIF_MODE REG32(0x10003010)
#define GIF_STAT REG32(0x10003020)
#define GIF_FIFO_PHYS 0x10006000u

/* ---- VIF1 (ps2tek "VIF I/O Registers") ---- */
#define VIF1_STAT  REG32(0x10003C00)
#define VIF1_FBRST REG32(0x10003C10)
#define VIF1_ERR   REG32(0x10003C20)

/* ---- Privileged GS registers (64-bit, written with SD by gs_write64) ---- */
#define GS_PMODE    0x12000000u
#define GS_SMODE1   0x12000010u
#define GS_SMODE2   0x12000020u
#define GS_SRFSH    0x12000030u
#define GS_SYNCH1   0x12000040u
#define GS_SYNCH2   0x12000050u
#define GS_SYNCV    0x12000060u
#define GS_DISPFB1  0x12000070u
#define GS_DISPLAY1 0x12000080u
#define GS_DISPFB2  0x12000090u
#define GS_DISPLAY2 0x120000A0u
#define GS_BGCOLOR  0x120000E0u
#define GS_CSR      0x12001000u
#define GS_IMR      0x12001010u
#define GS_BUSDIR   0x12001040u

/* ---- GS general registers (ps2tek "GS Register List") ---- */
#define GS_PRIM       0x00
#define GS_RGBAQ      0x01
#define GS_ST         0x02
#define GS_UV         0x03
#define GS_XYZF2      0x04
#define GS_XYZ2       0x05
#define GS_TEX0_1     0x06
#define GS_CLAMP_1    0x08
#define GS_FOG        0x0A
#define GS_XYZ3       0x0D
#define GS_TEX1_1     0x14
#define GS_XYOFFSET_1 0x18
#define GS_PRMODECONT 0x1A
#define GS_TEXCLUT    0x1C
#define GS_SCANMSK    0x22
#define GS_TEXA       0x3B
#define GS_FOGCOL     0x3D
#define GS_TEXFLUSH   0x3F
#define GS_SCISSOR_1  0x40
#define GS_ALPHA_1    0x42
#define GS_DIMX       0x44
#define GS_DTHE       0x45
#define GS_COLCLAMP   0x46
#define GS_TEST_1     0x47
#define GS_PABE       0x49
#define GS_FBA_1      0x4A
#define GS_FRAME_1    0x4C
#define GS_ZBUF_1     0x4E
#define GS_BITBLTBUF  0x50
#define GS_TRXPOS     0x51
#define GS_TRXREG     0x52
#define GS_TRXDIR     0x53

/* GIFtag register descriptors for PACKED/REGLIST mode. */
#define GIF_REG_PRIM  0x0
#define GIF_REG_RGBAQ 0x1
#define GIF_REG_ST    0x2
#define GIF_REG_UV    0x3
#define GIF_REG_XYZF2 0x4
#define GIF_REG_XYZ2  0x5
#define GIF_REG_AD    0xE
#define GIF_REG_NOP   0xF

#define GIF_FLG_PACKED  0
#define GIF_FLG_REGLIST 1
#define GIF_FLG_IMAGE   2

/* PRIM register (ps2tek "GS Primitives"). */
#define PRIM_POINT     0
#define PRIM_LINE      1
#define PRIM_LINESTRIP 2
#define PRIM_TRI       3
#define PRIM_TRISTRIP  4
#define PRIM_TRIFAN    5
#define PRIM_SPRITE    6
#define PRIM_IIP  (1u << 3) /* Gouraud */
#define PRIM_TME  (1u << 4) /* texture */
#define PRIM_FGE  (1u << 5)
#define PRIM_ABE  (1u << 6) /* alpha blend */
#define PRIM_AA1  (1u << 7)
#define PRIM_FST  (1u << 8) /* UV (1) / STQ (0) */

/* Pixel storage formats. */
#define PSMCT32 0x00
#define PSMCT24 0x01
#define PSMCT16 0x02
#define PSMT8   0x13
#define PSMT4   0x14
#define PSMZ32  0x30
#define PSMZ24  0x31

/* 64-bit register values (constant shifts only, so clang never calls a libgcc helper). */
#define GS_RGBAQ_V(r, g, b, a) ((u64)(((u32)(r) & 0xFF) | (((u32)(g) & 0xFF) << 8) | (((u32)(b) & 0xFF) << 16) | ((u32)(a) << 24)) | ((u64)0x3F800000u << 32))
#define GS_XYZ2_V(x16, y16, z) ((u64)(((u32)(x16) & 0xFFFF) | ((u32)(y16) << 16)) | ((u64)(u32)(z) << 32))
#define GS_UV_V(u16, v16) ((u64)(((u32)(u16) & 0x3FFF) | (((u32)(v16) & 0x3FFF) << 16)))
#define GS_FRAME_V(fbp, fbw, psm, fbmsk) ((u64)((u32)(fbp) | ((u32)(fbw) << 16) | ((u32)(psm) << 24)) | ((u64)(u32)(fbmsk) << 32))
#define GS_ZBUF_V(zbp, psm, zmsk) ((u64)((u32)(zbp) | (((u32)(psm) & 0xF) << 24)) | ((u64)(zmsk) << 32))
#define GS_XYOFFSET_V(ofx, ofy) ((u64)(u32)(ofx) | ((u64)(u32)(ofy) << 32))
#define GS_SCISSOR_V(x0, x1, y0, y1) ((u64)((u32)(x0) | ((u32)(x1) << 16)) | ((u64)((u32)(y0) | ((u32)(y1) << 16)) << 32))
#define GS_TEST_V(ate, atst, aref, afail, date, datm, zte, ztst) \
    ((u64)((u32)(ate) | ((u32)(atst) << 1) | ((u32)(aref) << 4) | ((u32)(afail) << 12) | ((u32)(date) << 14) | \
           ((u32)(datm) << 15) | ((u32)(zte) << 16) | ((u32)(ztst) << 17)))
#define ZTST_NEVER 0
#define ZTST_ALWAYS 1
#define ZTST_GEQUAL 2
#define ZTST_GREATER 3
/* ALPHA: A 0-1, B 2-3, C 4-5, D 6-7, FIX 32-39 (the GS manual and PCSX2/Play! place FIX at bit 32;
 * ps2tek's "8-15" is a typo). */
#define GS_ALPHA_V(a, b, c, d, fix) ((u64)((u32)(a) | ((u32)(b) << 2) | ((u32)(c) << 4) | ((u32)(d) << 6)) | ((u64)(u32)(fix) << 32))
/* TEX0: TBP0 0-13, TBW 14-19, PSM 20-25, TW 26-29, TH 30-33, TCC 34, TFX 35-36, CBP 37-50, CPSM 51-54, CSM 55, CSA 56-60, CLD 61-63. */
#define GS_TEX0_V(tbp, tbw, psm, tw, th, tcc, tfx, cbp, cpsm, csm, csa, cld) \
    ((u64)((u32)(tbp) | ((u32)(tbw) << 14) | ((u32)(psm) << 20) | ((u32)(tw) << 26) | (((u32)(th) & 3) << 30)) | \
     ((u64)(((u32)(th) >> 2) | ((u32)(tcc) << 2) | ((u32)(tfx) << 3) | ((u32)(cbp) << 5) | ((u32)(cpsm) << 19) | \
            ((u32)(csm) << 23) | ((u32)(csa) << 24) | ((u32)(cld) << 29)) << 32))
#define TFX_MODULATE 0
#define TFX_DECAL 1
/* TEX1: LCM 0, MXL 2-4, MMAG 5, MMIN 6-8, MTBA 9, L 19-20, K 32-43. */
#define GS_TEX1_V(mmag, mmin) ((u64)(((u32)(mmag) << 5) | ((u32)(mmin) << 6)))
#define GS_BITBLTBUF_V(sbp, sbw, spsm, dbp, dbw, dpsm) \
    ((u64)((u32)(sbp) | ((u32)(sbw) << 16) | ((u32)(spsm) << 24)) | ((u64)((u32)(dbp) | ((u32)(dbw) << 16) | ((u32)(dpsm) << 24)) << 32))
#define GS_TRXPOS_V(sx, sy, dx, dy) ((u64)((u32)(sx) | ((u32)(sy) << 16)) | ((u64)((u32)(dx) | ((u32)(dy) << 16)) << 32))
#define GS_TRXREG_V(w, h) ((u64)(u32)(w) | ((u64)(u32)(h) << 32))

/* GIFtag (ps2tek "GIFtag Format"): NLOOP 0-14, EOP 15, PRE 46, PRIM 47-57, FLG 58-59, NREG 60-63, REGS 64-127. */
static inline void gif_tag(qword_t *q, u32 nloop, u32 eop, u32 pre, u32 prim, u32 flg, u32 nreg, u32 regs_lo, u32 regs_hi)
{
    q->w[0] = (nloop & 0x7FFF) | (eop << 15);
    q->w[1] = (pre << 14) | ((prim & 0x7FF) << 15) | (flg << 26) | ((nreg & 0xF) << 28);
    q->w[2] = regs_lo;
    q->w[3] = regs_hi;
}

/* An empty GIFtag (NLOOP 0) with EOP: ends a packet so PATH3 releases the GIF. */
static inline void gif_end(qword_t *q)
{
    gif_tag(q, 0, 1, 0, 0, GIF_FLG_PACKED, 0, 0, 0);
}

/* PACKED A+D quadword: 64-bit data then the register address. */
static inline void gif_ad(qword_t *q, u32 reg, u64 v)
{
    q->w[0] = (u32)v;
    q->w[1] = (u32)(v >> 32);
    q->w[2] = reg;
    q->w[3] = 0;
}

/* PACKED RGBA quadword (Q unchanged). */
static inline void gif_rgba(qword_t *q, u32 r, u32 g, u32 b, u32 a)
{
    q->w[0] = r; q->w[1] = g; q->w[2] = b; q->w[3] = a;
}

/* PACKED XYZ2 quadword: X/Y in 12.4 fixed point (already shifted), 32-bit Z. */
static inline void gif_xyz2(qword_t *q, u32 x16, u32 y16, u32 z)
{
    q->w[0] = x16; q->w[1] = y16; q->w[2] = z; q->w[3] = 0;
}

/* PACKED UV quadword (14-bit 10.4 fixed point). */
static inline void gif_uv(qword_t *q, u32 u16, u32 v16)
{
    q->w[0] = u16; q->w[1] = v16; q->w[2] = 0; q->w[3] = 0;
}

/* PACKED STQ quadword (IEEE single bit patterns). */
static inline void gif_stq(qword_t *q, u32 s, u32 t, u32 qv)
{
    q->w[0] = s; q->w[1] = t; q->w[2] = qv; q->w[3] = 0;
}

/* Single-precision bit patterns used instead of C floats. */
#define F_0_0 0x00000000u
#define F_0_5 0x3F000000u
#define F_0_75 0x3F400000u
#define F_1_0 0x3F800000u
#define F_2_0 0x40000000u

/* Drawing coordinates: every program sets XYOFFSET_1 to (1024, 1024), so screen pixel (x, y) is GS vertex
 * (x + 1024, y + 1024), in 12.4 fixed point. */
#define SCREEN_OFS 1024
#define VX(x) ((u32)(((x) + SCREEN_OFS) << 4))
#define VY(y) ((u32)(((y) + SCREEN_OFS) << 4))

/* Frame layout in GS memory, shared by all programs: 640x448 PSMCT32 frame at page 0 (FBW 10), a PSMZ24
 * Z buffer at page 140, free memory from page 280 (texture base pointer 280*32 = 8960). */
#define FB_WIDTH 640
#define FB_HEIGHT 448
#define FB_FBP 0
#define FB_FBW 10
#define ZB_ZBP 140
#define TEX_FREE_PAGE 280

/* Status block at physical 0x001FFF00 (KSEG1 alias), written when a program reaches its idle loop. */
#define GV_STATUS ((volatile u32 *)0xA01FFF00u)
#define GV_STATUS_MAGIC 0x42485647u /* "GVHB" */

/* ---- common.c / ee.S ---- */
void gs_write64(u32 phys, u32 lo, u32 hi);
#define GS_WRITE64(phys, v) gs_write64((phys), (u32)(u64)(v), (u32)((u64)(v) >> 32))
void qcopy(void *dst, const void *src, u32 nqw);
void gif_fifo_write(const void *src, u32 nqw);
void gs_init_ntsc_640x448(void);
void dma_reset(void);
void dma_gif_send(const qword_t *pk, u32 nqw);
void dma_gif_wait(void);
qword_t *pk_buffer(void);
void gs_setup_context1(qword_t **pp, u32 zmsk);
void gv_finish(u32 program_id);
void *memset(void *d, int c, unsigned n);
void *memcpy(void *d, const void *s, unsigned n);

static inline void compiler_barrier_sync(void)
{
    __asm__ volatile("sync" ::: "memory");
}

#endif
