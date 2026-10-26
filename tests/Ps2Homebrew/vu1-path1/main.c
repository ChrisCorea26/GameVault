/*
 * GameVault PS2 homebrew "vu1-path1": a VIF1 DMA chain uploads a VU1 microprogram (MPG), a matrix and
 * vertices (UNPACK V4-32, and V4-16 with STCYCL skipping, STMASK/STROW filling w, BASE/OFFSET double
 * buffering read back with XTOP) and starts it (MSCAL); the microprogram transforms the vertices and
 * XGKICKs a Gouraud quad (PATH1). After FLUSH, a VIF1 DIRECT sends a yellow sprite (PATH2). PATH3 draws
 * the background first and a marker last. GameVault's own code; README.md lists what the screen shows.
 */
#include "../common/ps2hw.h"

#define PROGRAM_ID 3

extern const u32 vu1_code[];
extern const u32 vu1_code_end[];

/* VIF codes (ps2tek "VIF Commands"). */
#define VIF_NOP 0x00000000u
#define VIF_STCYCL(cl, wl) (0x01000000u | ((u32)(wl) << 8) | (u32)(cl))
#define VIF_OFFSET(o) (0x02000000u | (u32)(o))
#define VIF_BASE(b) (0x03000000u | (u32)(b))
#define VIF_STMOD(m) (0x05000000u | (u32)(m))
#define VIF_MSCAL(a) (0x14000000u | (u32)(a))
#define VIF_FLUSH 0x11000000u
#define VIF_STMASK 0x20000000u
#define VIF_STROW 0x30000000u
#define VIF_MPG(num, addr) (0x4A000000u | ((u32)(num) << 16) | (u32)(addr))
#define VIF_DIRECT(qwc) (0x50000000u | (u32)(qwc))
#define VIF_UNPACK(fmt, num, addr) (((0x60u | (u32)(fmt)) << 24) | ((u32)(num) << 16) | (u32)(addr))
#define UNPACK_V4_32 0x0C
#define UNPACK_V4_16 0x0D
#define UNPACK_M 0x10          /* write mask (MASK register) */
#define UNPACK_FLG 0x8000      /* address += TOPS */

#define VU_BASE 0x100
#define VU_OFST 0x080

static void dma_tag(qword_t *q, u32 qwc, u32 id, u32 addr, u32 vif0, u32 vif1)
{
    q->w[0] = DMATAG_LO(qwc, id);
    q->w[1] = addr;
    q->w[2] = vif0;
    q->w[3] = vif1;
}

static s32 to_s16pair(s32 lo, s32 hi)
{
    return (s32)(((u32)lo & 0xFFFF) | ((u32)hi << 16));
}

int main(void)
{
    qword_t *base, *q, *chain, *direct, *t2;
    u32 *w, *w0, ncode, i;

    dma_reset();
    VIF1_FBRST = 1;   /* reset VIF1 */
    VIF1_ERR = 2;     /* ME0: ignore DMAtag mismatch */
    gs_init_ntsc_640x448();

    /* ---- PATH3: environment and clear to (24,24,24) ---- */
    base = q = pk_buffer();
    gif_tag(q++, 12, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    gs_setup_context1(&q, 1);
    gif_tag(q++, 4, 1, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    gif_ad(q++, GS_PRIM, PRIM_SPRITE);
    gif_ad(q++, GS_RGBAQ, GS_RGBAQ_V(24, 24, 24, 0x80));
    gif_ad(q++, GS_XYZ2, GS_XYZ2_V(VX(0), VY(0), 0));
    gif_ad(q++, GS_XYZ2, GS_XYZ2_V(VX(FB_WIDTH), VY(FB_HEIGHT), 0));
    dma_gif_send(base, (u32)(q - base));

    /* ---- PATH2 packet: yellow sprite (360,280)-(520,400), 4 quadwords ---- */
    direct = q;
    gif_tag(q++, 1, 1, 1, PRIM_SPRITE, GIF_FLG_PACKED, 3, (GIF_REG_XYZ2 << 8) | (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
    gif_rgba(q++, 255, 255, 0, 0x80);
    gif_xyz2(q++, VX(360), VY(280), 0);
    gif_xyz2(q++, VX(520), VY(400), 0);

    /* ---- VIF1 source chain ---- */
    chain = q;
    ncode = (u32)(vu1_code_end - vu1_code) / 2;            /* 64-bit instructions (28) */
    /* T1 ref: the microprogram, straight from .rodata; MPG travels in the tag's upper word (TTE). */
    dma_tag(q++, ncode / 2, DMATAG_REF, PHYS(vu1_code), VIF_NOP, VIF_MPG(ncode, 0));
    /* T2 cnt: the data that follows it. */
    t2 = q++;
    w0 = w = (u32 *)q;
    *w++ = VIF_STCYCL(1, 1);
    *w++ = VIF_STMOD(0);
    /* Matrix columns and the GIFtag at VU1 addresses 0-4 (V4-32, raw 32-bit words). */
    *w++ = VIF_UNPACK(UNPACK_V4_32, 5, 0);
    *w++ = 0x3F400000; *w++ = 0x3F400000; *w++ = 0; *w++ = 0;            /* c0 = ( 0.75, 0.75, 0, 0) */
    *w++ = 0xBF400000; *w++ = 0x3F400000; *w++ = 0; *w++ = 0;            /* c1 = (-0.75, 0.75, 0, 0) */
    *w++ = 0; *w++ = 0; *w++ = 0; *w++ = 0;                              /* c2 = 0 */
    *w++ = 0x44A80000; *w++ = 0x449C0000; *w++ = 0; *w++ = 0x3F800000;   /* c3 = (1344, 1248, 0, 1) */
    *w++ = 0x8004;                                                       /* NLOOP 4, EOP */
    *w++ = (1u << 14) | ((PRIM_TRISTRIP | PRIM_IIP) << 15) | (GIF_FLG_PACKED << 26) | (2u << 28);
    *w++ = (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ;
    *w++ = 0;
    /* Double buffer: BASE 100h, OFFSET 80h -> TOPS = BASE (DBF 0); MSCAL makes TOP = 100h. */
    *w++ = VIF_BASE(VU_BASE);
    *w++ = VIF_OFFSET(VU_OFST);
    /* Positions into TOPS+0, +2, +4, +6 (CL 2, WL 1: write one, skip one), V4-16 sign-extended, with
     * w taken from row register R3 = 1 (MASK: w field of every cycle = 01b) instead of the 7777h sent. */
    *w++ = VIF_STCYCL(2, 1);
    *w++ = VIF_STMASK;
    *w++ = 0x40404040;
    *w++ = VIF_STROW;
    *w++ = 0; *w++ = 0; *w++ = 0; *w++ = 1;
    *w++ = VIF_UNPACK(UNPACK_V4_16 | UNPACK_M, 4, UNPACK_FLG | 0);
    {
        static const s16 pos[4][2] = { {-80, -80}, {80, -80}, {-80, 80}, {80, 80} };
        for (i = 0; i < 4; i++) {
            *w++ = (u32)to_s16pair(pos[i][0], pos[i][1]);
            *w++ = (u32)to_s16pair(0, 0x7777);
        }
    }
    /* Colours into TOPS+1, +3, +5, +7: red, green, blue, white (A 128). */
    *w++ = VIF_UNPACK(UNPACK_V4_32, 4, UNPACK_FLG | 1);
    {
        static const u8 col[4][3] = { {255, 0, 0}, {0, 255, 0}, {0, 0, 255}, {255, 255, 255} };
        for (i = 0; i < 4; i++) {
            *w++ = col[i][0]; *w++ = col[i][1]; *w++ = col[i][2]; *w++ = 0x80;
        }
    }
    *w++ = VIF_MSCAL(0);
    *w++ = VIF_FLUSH;          /* wait for the microprogram and its PATH1 transfer */
    while ((u32)(w - w0) & 3) *w++ = VIF_NOP;
    q = (qword_t *)w;
    dma_tag(t2, (u32)(q - (t2 + 1)), DMATAG_CNT, 0, VIF_NOP, VIF_NOP);
    /* T3 ref: the PATH2 packet; DIRECT travels in the tag's upper word. */
    dma_tag(q++, 4, DMATAG_REF, PHYS(direct), VIF_NOP, VIF_DIRECT(4));
    /* T4 end. */
    dma_tag(q++, 0, DMATAG_END, 0, VIF_NOP, VIF_NOP);

    compiler_barrier_sync();
    D1_QWC = 0;
    D1_MADR = 0;
    D1_TADR = PHYS(chain);
    D1_CHCR = CHCR_DIR_FROM_MEM | CHCR_MOD_CHAIN | CHCR_TTE | CHCR_STR;
    while (D1_CHCR & CHCR_STR) {}
    while (VIF1_STAT & 0x4) {}     /* VEW: VU1 still running */

    /* ---- PATH3 last: green marker (8,8)-(24,24) ---- */
    base = q;
    gif_tag(q++, 1, 1, 1, PRIM_SPRITE, GIF_FLG_PACKED, 3, (GIF_REG_XYZ2 << 8) | (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
    gif_rgba(q++, 0, 255, 0, 0x80);
    gif_xyz2(q++, VX(8), VY(8), 0);
    gif_xyz2(q++, VX(24), VY(24), 0);
    dma_gif_send(base, (u32)(q - base));

    gv_finish(PROGRAM_ID);
    for (;;) {}
    return 0;
}
