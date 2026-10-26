/*
 * GameVault PS2 homebrew "gs-basic": CRT set-up by the privileged GS registers, then flat and
 * Gouraud primitives sent as GIF packets by PATH3 DMA (PACKED and REGLIST) and through the GIF
 * FIFO with SQ. GameVault's own code; README.md lists what the screen shows.
 */
#include "../common/ps2hw.h"

#define PROGRAM_ID 1

/* One flat-coloured vertex in PACKED mode: RGBA then XYZ2. */
static qword_t *vtx(qword_t *q, u32 r, u32 g, u32 b, int x, int y)
{
    gif_rgba(q++, r, g, b, 0x80);
    gif_xyz2(q++, VX(x), VY(y), 0);
    return q;
}

/* GIFtag for nverts PACKED (RGBA, XYZ2) vertices with PRIM in the tag. */
static qword_t *prim_tag(qword_t *q, u32 prim, u32 nverts, u32 eop)
{
    gif_tag(q++, nverts, eop, 1, prim, GIF_FLG_PACKED, 2, (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
    return q;
}

int main(void)
{
    qword_t *base, *q;

    dma_reset();
    gs_init_ntsc_640x448();

    /* ---- Packet 1 (PATH3 DMA): environment, clear, primitives ---- */
    base = q = pk_buffer();

    gif_tag(q++, 12, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    gs_setup_context1(&q, 1);

    /* Clear: full-screen sprite, colour (16, 16, 64), through A+D. */
    gif_tag(q++, 4, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    gif_ad(q++, GS_PRIM, PRIM_SPRITE);
    gif_ad(q++, GS_RGBAQ, GS_RGBAQ_V(16, 16, 64, 0x80));
    gif_ad(q++, GS_XYZ2, GS_XYZ2_V(VX(0), VY(0), 0));
    gif_ad(q++, GS_XYZ2, GS_XYZ2_V(VX(FB_WIDTH), VY(FB_HEIGHT), 0));

    /* 1. Flat red triangle. */
    q = prim_tag(q, PRIM_TRI, 3, 0);
    q = vtx(q, 255, 0, 0, 40, 40);
    q = vtx(q, 255, 0, 0, 200, 40);
    q = vtx(q, 255, 0, 0, 40, 200);

    /* 2. Gouraud triangle: red, green, blue corners. */
    q = prim_tag(q, PRIM_TRI | PRIM_IIP, 3, 0);
    q = vtx(q, 255, 0, 0, 240, 40);
    q = vtx(q, 0, 255, 0, 400, 40);
    q = vtx(q, 0, 0, 255, 240, 200);

    /* 3. Flat triangle strip (440,40)-(600,200): flat shading takes the colour of the vertex that
     * completes each triangle, so the upper-left half is yellow (v2) and the lower-right half cyan (v3);
     * the white of v0/v1 never shows. */
    q = prim_tag(q, PRIM_TRISTRIP, 4, 0);
    q = vtx(q, 255, 255, 255, 440, 40);
    q = vtx(q, 255, 255, 255, 600, 40);
    q = vtx(q, 255, 255, 0, 440, 200);
    q = vtx(q, 0, 255, 255, 600, 200);

    /* 4. Flat triangle fan: centre (120,320), square (40,240)-(200,400) split in four:
     * top magenta, right orange, bottom white, left light blue. */
    q = prim_tag(q, PRIM_TRIFAN, 6, 0);
    q = vtx(q, 0, 0, 0, 120, 320);
    q = vtx(q, 0, 0, 0, 40, 240);
    q = vtx(q, 255, 0, 255, 200, 240);
    q = vtx(q, 255, 128, 0, 200, 400);
    q = vtx(q, 255, 255, 255, 40, 400);
    q = vtx(q, 0, 128, 255, 40, 240);

    /* 5. Lines: white horizontal y=260, Gouraud red->blue y=280, green vertical x=420. */
    q = prim_tag(q, PRIM_LINE, 2, 0);
    q = vtx(q, 255, 255, 255, 240, 260);
    q = vtx(q, 255, 255, 255, 400, 260);
    q = prim_tag(q, PRIM_LINE | PRIM_IIP, 2, 0);
    q = vtx(q, 255, 0, 0, 240, 280);
    q = vtx(q, 0, 0, 255, 400, 280);
    q = prim_tag(q, PRIM_LINE, 2, 0);
    q = vtx(q, 0, 255, 0, 420, 240);
    q = vtx(q, 0, 255, 0, 420, 400);
    /* Yellow line strip zigzag. */
    q = prim_tag(q, PRIM_LINESTRIP, 5, 0);
    q = vtx(q, 255, 255, 0, 240, 300);
    q = vtx(q, 255, 255, 0, 280, 380);
    q = vtx(q, 255, 255, 0, 320, 300);
    q = vtx(q, 255, 255, 0, 360, 380);
    q = vtx(q, 255, 255, 0, 400, 300);

    /* 6. Grey sprite (460,300)-(600,400), PRIM written as a PACKED PRIM register (no PRE). */
    gif_tag(q++, 1, 0, 0, 0, GIF_FLG_PACKED, 4,
            (GIF_REG_XYZ2 << 12) | (GIF_REG_XYZ2 << 8) | (GIF_REG_RGBAQ << 4) | GIF_REG_PRIM, 0);
    q->w[0] = PRIM_SPRITE; q->w[1] = 0; q->w[2] = 0; q->w[3] = 0; q++;
    gif_rgba(q++, 128, 128, 128, 0x80);
    gif_xyz2(q++, VX(460), VY(300), 0);
    gif_xyz2(q++, VX(600), VY(400), 0);

    /* 7. Eight points in REGLIST mode (RGBAQ, XYZ2 as 64-bit pairs) at (460 + 20i, 260). The GIF honours a
     * GIFtag's PRE only in PACKED mode (as Play! and PCSX2 do), so PRIM is set by A+D first. */
    {
        static const u8 cols[8][3] = {
            {255, 0, 0}, {0, 255, 0}, {0, 0, 255}, {255, 255, 0},
            {0, 255, 255}, {255, 0, 255}, {255, 255, 255}, {255, 128, 0},
        };
        u32 i;
        gif_tag(q++, 1, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
        gif_ad(q++, GS_PRIM, PRIM_POINT);
        gif_tag(q++, 8, 1, 0, 0, GIF_FLG_REGLIST, 2, (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
        for (i = 0; i < 8; i++) {
            u64 c = GS_RGBAQ_V(cols[i][0], cols[i][1], cols[i][2], 0x80);
            u64 p = GS_XYZ2_V(VX(460 + 20 * i), VY(260), 0);
            q->w[0] = (u32)c; q->w[1] = (u32)(c >> 32);
            q->w[2] = (u32)p; q->w[3] = (u32)(p >> 32);
            q++;
        }
    }

    dma_gif_send(base, (u32)(q - base));

    /* ---- Packet 2 (GIF FIFO, SQ stores): white 8x8 corner markers ---- */
    {
        qword_t *f = q;
        gif_tag(q++, 2, 1, 1, PRIM_SPRITE, GIF_FLG_PACKED, 3,
                (GIF_REG_XYZ2 << 8) | (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
        gif_rgba(q++, 255, 255, 255, 0x80);
        gif_xyz2(q++, VX(0), VY(0), 0);
        gif_xyz2(q++, VX(8), VY(8), 0);
        gif_rgba(q++, 255, 255, 255, 0x80);
        gif_xyz2(q++, VX(FB_WIDTH - 8), VY(FB_HEIGHT - 8), 0);
        gif_xyz2(q++, VX(FB_WIDTH), VY(FB_HEIGHT), 0);
        compiler_barrier_sync();
        gif_fifo_write(f, (u32)(q - f));
    }

    gv_finish(PROGRAM_ID);
    for (;;) {}
    return 0;
}
