/*
 * GameVault PS2 homebrew "mmi-fpu-check": runs 16 R5900-specific instructions (MMI, pipeline-1
 * multiply, MADD, FPU accumulator ops, FPU saturation), compares them with the expected values and
 * draws a green (pass) or red (fail) rectangle per check, plus a summary bar. Results also go to the
 * status block. GameVault's own code; README.md lists what the screen shows.
 */
#include "../common/ps2hw.h"

#define PROGRAM_ID 4
#define NCHECKS 16

void run_checks(u32 out[NCHECKS][4]);

static u32 results[NCHECKS][4] __attribute__((aligned(16)));

/* Expected results and the words that are compared (bit k = word k). */
static const struct { u32 w[4]; u32 mask; } expected[NCHECKS] = {
    {{0x00000003, 0x80000000, 0x7FFFFFFF, 0x00000000}, 0xF}, /*  0 PADDW A, B */
    {{0xFFFFFFFF, 0x7FFFFFFE, 0x80000001, 0xFFFFFFFE}, 0xF}, /*  1 PSUBW A, B */
    {{0x02FE807F, 0x02FE807F, 0x02FE807F, 0x02FE807F}, 0xF}, /*  2 PADDSB C, C */
    {{0x02FFFFFE, 0x02FFFFFE, 0x02FFFFFE, 0x02FFFFFE}, 0xF}, /*  3 PADDUB C, C */
    {{0x00000002, 0x00000001, 0x00000001, 0x7FFFFFFF}, 0xF}, /*  4 PEXTLW A, B */
    {{0x00000001, 0x7FFFFFFF, 0x00000002, 0x00000001}, 0xF}, /*  5 PCPYLD B, A */
    {{0x80000000, 0xFFFFFFFF, 0xFFFFFFFF, 0x00000001}, 0xF}, /*  6 PCPYUD A, B */
    {{0x11111111, 0x11111111, 0x33333333, 0x33333333}, 0xF}, /*  7 PCPYH D */
    {{0x7FFFFFFF, 0x00000001, 0x00000005, 0x00000005}, 0xF}, /*  8 PABSW E */
    {{0x0000001E, 0x00000000, 0, 0}, 0x3},                   /*  9 PLZCW A (low 64 bits) */
    {{0xFF951644, 0, 0, 0}, 0x1},                            /* 10 MULT1/MFLO1 1234 * -5678 */
    {{0xA817C800, 0x00000004, 0, 0}, 0x3},                   /* 11 MADD x2: LO, HI of 2e10 */
    {{0x41180000, 0, 0, 0}, 0x1},                            /* 12 ADDA.S + MADD.S = 9.5 */
    {{0x41000000, 0, 0, 0}, 0x1},                            /* 13 MULA.S + MADDA.S = 8.0 */
    {{0x40000000, 0xBF800000, 0, 0}, 0x3},                   /* 14 MAX.S, MIN.S (-1, 2) */
    {{0x7FFFFFFF, 0, 0, 0}, 0x1},                            /* 15 ADD.S overflow saturates */
};

static qword_t *sprite(qword_t *q, u32 r, u32 g, u32 b, int x0, int y0, int x1, int y1)
{
    gif_tag(q++, 1, 0, 1, PRIM_SPRITE, GIF_FLG_PACKED, 3, (GIF_REG_XYZ2 << 8) | (GIF_REG_XYZ2 << 4) | GIF_REG_RGBAQ, 0);
    gif_rgba(q++, r, g, b, 0x80);
    gif_xyz2(q++, VX(x0), VY(y0), 0);
    gif_xyz2(q++, VX(x1), VY(y1), 0);
    return q;
}

int main(void)
{
    qword_t *base, *q;
    u32 i, k, fails = 0, npass = 0;

    for (i = 0; i < NCHECKS; i++)
        for (k = 0; k < 4; k++) results[i][k] = 0;
    run_checks(results);
    for (i = 0; i < NCHECKS; i++) {
        u32 ok = 1;
        for (k = 0; k < 4; k++)
            if ((expected[i].mask >> k) & 1)
                ok &= results[i][k] == expected[i].w[k];
        if (ok) npass++;
        else fails |= 1u << i;
    }
    GV_STATUS[2] = npass;
    GV_STATUS[3] = fails;

    dma_reset();
    gs_init_ntsc_640x448();

    base = q = pk_buffer();
    gif_tag(q++, 12, 0, 0, 0, GIF_FLG_PACKED, 1, GIF_REG_AD, 0);
    gs_setup_context1(&q, 1);
    q = sprite(q, 32, 32, 32, 0, 0, FB_WIDTH, FB_HEIGHT);
    /* Check i: 56x80 rectangle at (40 + 72*(i%8), 120 + 104*(i/8)). */
    for (i = 0; i < NCHECKS; i++) {
        int x = 40 + 72 * (int)(i & 7), y = 120 + 104 * (int)(i >> 3);
        if (fails & (1u << i)) q = sprite(q, 255, 0, 0, x, y, x + 56, y + 80);
        else q = sprite(q, 0, 255, 0, x, y, x + 56, y + 80);
    }
    /* Summary bar (40,360)-(600,400): green when every check passed. */
    if (fails) q = sprite(q, 255, 0, 0, 40, 360, 600, 400);
    else q = sprite(q, 0, 255, 0, 40, 360, 600, 400);
    gif_end(q++);
    dma_gif_send(base, (u32)(q - base));

    gv_finish(PROGRAM_ID);
    for (;;) {}
    return 0;
}
