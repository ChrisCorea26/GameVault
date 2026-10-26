/*
 * GameVault PS2 homebrew "gs-texture": IMAGE-mode texture uploads (PSMCT32, PSMT8 + 256-colour CLUT,
 * PSMT4 + 16-colour CLUT, CSM1), textured sprites and triangles with UV and STQ, MODULATE/DECAL,
 * nearest/bilinear, alpha blending and a Z-tested pair of triangles (PSMZ24, GEQUAL).
 * GameVault's own code; README.md lists what the screen shows.
 */
#include "../common/ps2hw.h"

#define PROGRAM_ID 2

/* GS memory (texture base pointers in 64-word blocks, 32 blocks per page). */
#define TBP_CHECKER (TEX_FREE_PAGE * 32)          /* pages 280-281: 64x64 PSMCT32, TBW 1 */
#define TBP_T8      ((TEX_FREE_PAGE + 2) * 32)    /* page 282: 16x16 PSMT8, TBW 2 */
#define TBP_T4      ((TEX_FREE_PAGE + 3) * 32)    /* page 283: 16x16 PSMT4, TBW 2 */
#define CBP_256     ((TEX_FREE_PAGE + 4) * 32)    /* page 284: 256-entry PSMCT32 CLUT (16x16) */
#define CBP_16      ((TEX_FREE_PAGE + 5) * 32)    /* page 285: 16-entry PSMCT32 CLUT (8x2) */

#define CHECK_WHITE 0x80FFFFFFu
#define CHECK_BLUE  0x80FF0000u

/* 16-colour palette of the PSMT4 texture (R, G, B). */
static const u8 pal16[16][3] = {
    {0, 0, 0}, {255, 0, 0}, {0, 255, 0}, {0, 0, 255},
    {255, 255, 0}, {0, 255, 255}, {255, 0, 255}, {255, 255, 255},
    {128, 128, 128}, {128, 0, 0}, {0, 128, 0}, {0, 0, 128},
    {255, 128, 0}, {0, 128, 128}, {128, 0, 128}, {128, 128, 0},
};

static u32 rgba32(u32 r, u32 g, u32 b, u32 a)
{
    return r | (g << 8) | (b << 16) | (a << 24);
}

/* Starts a host->local transfer of a w x h rectangle at (0,0) of the buffer (dbp, dbw, psm) and returns
 * where the nqw quadwords of IMAGE data go. */
static qword_t *upload_begin(qword_t **pp, u32 dbp, u32 dbw, u32 psm, u32 w, u32 h, u32 nqw)
{
    qword_t *q = *pp;
    gif_tag(q++, 4, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    gif_ad(q++, GS_BITBLTBUF, GS_BITBLTBUF_V(0, 0, 0, dbp, dbw, psm));
    gif_ad(q++, GS_TRXPOS, GS_TRXPOS_V(0, 0, 0, 0));
    gif_ad(q++, GS_TRXREG, GS_TRXREG_V(w, h));
    gif_ad(q++, GS_TRXDIR, 0);
    gif_tag(q++, nqw, 1, 0, 0, GIF_FLG_IMAGE, 0, 0, 0);
    *pp = q + nqw;
    return q;
}

/* A sprite with UV coordinates (texels, 10.4 fixed point) in PACKED mode. */
static qword_t *uv_sprite(qword_t *q, u32 prim, int x0, int y0, int x1, int y1, u32 u0, u32 v0, u32 u1, u32 v1)
{
    gif_tag(q++, 2, 0, 1, prim, GIF_FLG_PACKED, 2, (GIF_REG_XYZ2 << 4) | GIF_REG_UV, 0);
    gif_uv(q++, u0 << 4, v0 << 4);
    gif_xyz2(q++, VX(x0), VY(y0), 0);
    gif_uv(q++, u1 << 4, v1 << 4);
    gif_xyz2(q++, VX(x1), VY(y1), 0);
    return q;
}

static qword_t *ad_tag(qword_t *q, u32 n)
{
    gif_tag(q++, n, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    return q;
}

int main(void)
{
    qword_t *base, *q;
    u32 x, y, i;

    dma_reset();
    gs_init_ntsc_640x448();

    /* ---- 1. Environment and clear (colour (48,48,48), Z = 0 written) ---- */
    base = q = pk_buffer();
    gif_tag(q++, 12, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    gs_setup_context1(&q, 0);
    q = ad_tag(q, 6);
    gif_ad(q++, GS_PRIM, PRIM_SPRITE);
    gif_ad(q++, GS_RGBAQ, GS_RGBAQ_V(48, 48, 48, 0x80));
    gif_ad(q++, GS_XYZ2, GS_XYZ2_V(VX(0), VY(0), 0));
    gif_ad(q++, GS_XYZ2, GS_XYZ2_V(VX(FB_WIDTH), VY(FB_HEIGHT), 0));
    gif_ad(q++, GS_ZBUF_1, GS_ZBUF_V(ZB_ZBP, PSMZ24, 1)); /* Z writes off until the Z test */
    gif_ad(q++, GS_CLAMP_1, 0);                             /* REPEAT */
    gif_end(q++);
    dma_gif_send(base, (u32)(q - base));

    /* ---- 2. Texture and CLUT uploads (IMAGE mode) ---- */
    base = q = pk_buffer();
    {
        /* 64x64 PSMCT32 checkerboard of 8x8 squares: white where (x/8 + y/8) is even, else blue. */
        u32 *d = (u32 *)upload_begin(&q, TBP_CHECKER, 1, PSMCT32, 64, 64, 64 * 64 * 4 / 16);
        for (y = 0; y < 64; y++)
            for (x = 0; x < 64; x++)
                d[y * 64 + x] = (((x >> 3) + (y >> 3)) & 1) ? CHECK_BLUE : CHECK_WHITE;
    }
    {
        /* 16x16 PSMT8: texel (x, y) holds index y*16 + x. */
        u8 *d = (u8 *)upload_begin(&q, TBP_T8, 2, PSMT8, 16, 16, 16 * 16 / 16);
        for (i = 0; i < 256; i++) d[i] = (u8)i;
    }
    {
        /* 16x16 PSMT4: 4x4 cells of 4x4 texels, cell (cx, cy) holds index cx + 4*cy; even x in the low nibble. */
        u8 *d = (u8 *)upload_begin(&q, TBP_T4, 2, PSMT4, 16, 16, 16 * 16 / 2 / 16);
        for (y = 0; y < 16; y++)
            for (x = 0; x < 16; x += 2) {
                u32 lo = (x >> 2) + ((y >> 2) << 2);
                u32 hi = ((x + 1) >> 2) + ((y >> 2) << 2);
                d[(y * 16 + x) / 2] = (u8)(lo | (hi << 4));
            }
    }
    {
        /* 256-entry CSM1 PSMCT32 CLUT as a 16x16 image: entry i = (R (i&15)*17, G (i>>4)*17, B 64, A 128),
         * stored at position i with bits 3 and 4 swapped (CSM1 layout, as Play!'s GSHandler.cpp reads it). */
        u32 *d = (u32 *)upload_begin(&q, CBP_256, 1, PSMCT32, 16, 16, 16 * 16 * 4 / 16);
        for (i = 0; i < 256; i++) {
            u32 p = (i & ~0x18u) | ((i & 0x08u) << 1) | ((i & 0x10u) >> 1);
            d[p] = rgba32((i & 15) * 17, (i >> 4) * 17, 64, 0x80);
        }
    }
    {
        /* 16-entry CSM1 PSMCT32 CLUT as an 8x2 image (entries 0-7, then 8-15). */
        u32 *d = (u32 *)upload_begin(&q, CBP_16, 1, PSMCT32, 8, 2, 8 * 2 * 4 / 16);
        for (i = 0; i < 16; i++) d[i] = rgba32(pal16[i][0], pal16[i][1], pal16[i][2], 0x80);
    }
    q = ad_tag(q, 1);
    gif_ad(q++, GS_TEXFLUSH, 0);
    gif_end(q++);
    dma_gif_send(base, (u32)(q - base));

    /* ---- 3. Drawing ---- */
    base = q = pk_buffer();

    /* D1: checkerboard, DECAL, nearest, sprite (32,32)-(160,160), 2x. */
    q = ad_tag(q, 3);
    gif_ad(q++, GS_TEX0_1, GS_TEX0_V(TBP_CHECKER, 1, PSMCT32, 6, 6, 1, TFX_DECAL, 0, 0, 0, 0, 0));
    gif_ad(q++, GS_TEX1_1, GS_TEX1_V(0, 0));
    gif_ad(q++, GS_RGBAQ, GS_RGBAQ_V(128, 128, 128, 128));
    q = uv_sprite(q, PRIM_SPRITE | PRIM_TME | PRIM_FST, 32, 32, 160, 160, 0, 0, 64, 64);

    /* D2: checkerboard, MODULATE by (64,128,128,128), sprite (192,32)-(320,160). */
    q = ad_tag(q, 2);
    gif_ad(q++, GS_TEX0_1, GS_TEX0_V(TBP_CHECKER, 1, PSMCT32, 6, 6, 1, TFX_MODULATE, 0, 0, 0, 0, 0));
    gif_ad(q++, GS_RGBAQ, GS_RGBAQ_V(64, 128, 128, 128));
    q = uv_sprite(q, PRIM_SPRITE | PRIM_TME | PRIM_FST, 192, 32, 320, 160, 0, 0, 64, 64);

    /* D3: texels (0,0)-(32,16) of the checkerboard, DECAL, bilinear, sprite (352,32)-(608,160), 8x. */
    q = ad_tag(q, 2);
    gif_ad(q++, GS_TEX0_1, GS_TEX0_V(TBP_CHECKER, 1, PSMCT32, 6, 6, 1, TFX_DECAL, 0, 0, 0, 0, 0));
    gif_ad(q++, GS_TEX1_1, GS_TEX1_V(1, 1));
    q = uv_sprite(q, PRIM_SPRITE | PRIM_TME | PRIM_FST, 352, 32, 608, 160, 0, 0, 32, 16);

    /* D4: PSMT8 + 256-colour CLUT, DECAL, nearest, sprite (32,192)-(160,320), 8x. */
    q = ad_tag(q, 2);
    gif_ad(q++, GS_TEX0_1, GS_TEX0_V(TBP_T8, 2, PSMT8, 4, 4, 1, TFX_DECAL, CBP_256, PSMCT32, 0, 0, 1));
    gif_ad(q++, GS_TEX1_1, GS_TEX1_V(0, 0));
    q = uv_sprite(q, PRIM_SPRITE | PRIM_TME | PRIM_FST, 32, 192, 160, 320, 0, 0, 16, 16);

    /* D5: PSMT4 + 16-colour CLUT, DECAL, nearest, sprite (192,192)-(320,320), 8x. */
    q = ad_tag(q, 1);
    gif_ad(q++, GS_TEX0_1, GS_TEX0_V(TBP_T4, 2, PSMT4, 4, 4, 1, TFX_DECAL, CBP_16, PSMCT32, 0, 0, 1));
    q = uv_sprite(q, PRIM_SPRITE | PRIM_TME | PRIM_FST, 192, 192, 320, 320, 0, 0, 16, 16);

    /* D6: checkerboard on a triangle-strip quad (352,192)-(608,320) with STQ: Q = 1 on the left edge,
     * 0.5 on the right (S, T pre-multiplied by Q), DECAL, nearest. */
    q = ad_tag(q, 1);
    gif_ad(q++, GS_TEX0_1, GS_TEX0_V(TBP_CHECKER, 1, PSMCT32, 6, 6, 1, TFX_DECAL, 0, 0, 0, 0, 0));
    /* In PACKED mode the STQ descriptor's Q only reaches the GS with the next RGBAQ descriptor (ps2tek: "GIF Data
     * Formats"), so each vertex sends STQ, RGBAQ, XYZ2. */
    gif_tag(q++, 4, 0, 1, PRIM_TRISTRIP | PRIM_TME, GIF_FLG_PACKED, 3,
            (GIF_REG_XYZ2 << 8) | (GIF_REG_RGBAQ << 4) | GIF_REG_ST, 0);
    gif_stq(q++, F_0_0, F_0_0, F_1_0);
    gif_rgba(q++, 128, 128, 128, 0x80);
    gif_xyz2(q++, VX(352), VY(192), 0);
    gif_stq(q++, F_0_5, F_0_0, F_0_5);
    gif_rgba(q++, 128, 128, 128, 0x80);
    gif_xyz2(q++, VX(608), VY(192), 0);
    gif_stq(q++, F_0_0, F_1_0, F_1_0);
    gif_rgba(q++, 128, 128, 128, 0x80);
    gif_xyz2(q++, VX(352), VY(320), 0);
    gif_stq(q++, F_0_5, F_0_5, F_0_5);
    gif_rgba(q++, 128, 128, 128, 0x80);
    gif_xyz2(q++, VX(608), VY(320), 0);

    /* D7: translucent red sprite (255,0,0, A=0x40) over D1, the background and D2: (128,96)-(256,176),
     * ALPHA_1 = (Cs - Cd) * As >> 7 + Cd. */
    q = ad_tag(q, 3);
    gif_ad(q++, GS_ALPHA_1, GS_ALPHA_V(0, 1, 0, 1, 0));
    gif_ad(q++, GS_PRIM, PRIM_SPRITE | PRIM_ABE);
    gif_ad(q++, GS_RGBAQ, GS_RGBAQ_V(255, 0, 0, 0x40));
    gif_tag(q++, 2, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_XYZ2, 0);
    gif_xyz2(q++, VX(128), VY(96), 0);
    gif_xyz2(q++, VX(256), VY(176), 0);

    /* D8: Z test (PSMZ24, ZTST GEQUAL, Z writes on). Triangle A (green, Z 0x8000) first, then B (magenta,
     * Z 0xC000 at x=360 falling to 0x4000 at x=600), which is hidden where it lies behind A. */
    q = ad_tag(q, 2);
    gif_ad(q++, GS_ZBUF_1, GS_ZBUF_V(ZB_ZBP, PSMZ24, 0));
    gif_ad(q++, GS_TEST_1, GS_TEST_V(0, 0, 0, 0, 0, 0, 1, ZTST_GEQUAL));
    gif_tag(q++, 3, 0, 1, PRIM_TRI, GIF_FLG_PACKED, 2, (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
    gif_rgba(q++, 0, 255, 0, 0x80);
    gif_xyz2(q++, VX(360), VY(336), 0x8000);
    gif_rgba(q++, 0, 255, 0, 0x80);
    gif_xyz2(q++, VX(600), VY(336), 0x8000);
    gif_rgba(q++, 0, 255, 0, 0x80);
    gif_xyz2(q++, VX(360), VY(440), 0x8000);
    gif_tag(q++, 3, 1, 1, PRIM_TRI, GIF_FLG_PACKED, 2, (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
    gif_rgba(q++, 255, 0, 255, 0x80);
    gif_xyz2(q++, VX(360), VY(350), 0xC000);
    gif_rgba(q++, 255, 0, 255, 0x80);
    gif_xyz2(q++, VX(600), VY(350), 0x4000);
    gif_rgba(q++, 255, 0, 255, 0x80);
    gif_xyz2(q++, VX(600), VY(440), 0x4000);

    dma_gif_send(base, (u32)(q - base));

    gv_finish(PROGRAM_ID);
    for (;;) {}
    return 0;
}
