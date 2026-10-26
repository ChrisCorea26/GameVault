/*
 * GameVault PS2 homebrew: GS/DMA set-up shared by the test programs. GameVault's own code.
 */
#include "ps2hw.h"

/* DMA packet buffer: 64 KB in .bss, written through its uncached alias. */
static qword_t pk_storage[4096];

qword_t *pk_buffer(void)
{
    return (qword_t *)UNCACHED(pk_storage);
}

void *memset(void *d, int c, unsigned n)
{
    u8 *p = (u8 *)d;
    while (n--) *p++ = (u8)c;
    return d;
}

void *memcpy(void *d, const void *s, unsigned n)
{
    u8 *p = (u8 *)d;
    const u8 *q = (const u8 *)s;
    while (n--) *p++ = *q++;
    return d;
}

static void spin(u32 n)
{
    while (n--) __asm__ volatile("nop");
}

/*
 * NTSC interlaced CRT (SMODE2 INT=1, FFMD=0: field mode, each field shows every other line of a
 * 640x448 frame), as the BIOS's SetGsCrt(1, 2, 0) programs it. The field values follow the Linux PS2
 * port's ps2fb.c (vm_to_sp_sdtv for NTSC) and gs-registers.h:
 *   SMODE1: RC=4 LC=32 T1248=1 CMOD=2(NTSC) SPML=4 CLKSEL=1 NVCK=1 SLCK2=1 VCKSEL=1, with SINT/PRST
 *           toggled as ps2fb does (0x740834504 is the BIOS's value with PRST set).
 *   SYNCH1: HFP=64 HBP=222 HSEQ=124 HSVS=1462 HS=254; SYNCH2: HF=1240 HB=1652
 *   SYNCV:  VFP=1 VFPE=6 VBP=26 VBPE=6 VDP=480 VS=6; SRFSH=8
 *   DISPLAY1: DX=641 DY=53 MAGH=3 (4 VCK per pixel) MAGV=0 DW=2559 DH=447
 *   DISPFB1: FBP=0 FBW=10 PSM=PSMCT32 DBX=DBY=0
 *   PMODE: EN1=1 CRTMD=1 MMOD=1 ALP=0xFF (circuit 1 only, opaque, independent of frame alpha)
 */
void gs_init_ntsc_640x448(void)
{
    GS_WRITE64(GS_CSR, 0x200);                 /* RESET */
    spin(1000);
    GS_WRITE64(GS_IMR, 0xFF00);                /* mask every GS interrupt */
    GS_WRITE64(GS_BUSDIR, 0);                  /* host -> local */

    GS_WRITE64(GS_SMODE1, 0x0000000740824504ull); /* SINT=1 PRST=0 */
    GS_WRITE64(GS_SMODE2, 0x1);                   /* INT=1 FFMD=0 DPMS=0 */
    GS_WRITE64(GS_SRFSH, 8);
    GS_WRITE64(GS_SYNCH1, 0x0007F5B61F06F040ull);
    GS_WRITE64(GS_SYNCH2, 0x00000000033A4D8ull);
    GS_WRITE64(GS_SYNCV, 0x00C7800601A01801ull);
    GS_WRITE64(GS_DISPLAY1, 0x001BF9FF01835281ull);
    GS_WRITE64(GS_DISPFB1, 0x0000000000001400ull);
    GS_WRITE64(GS_PMODE, 0x000000000000FF25ull);
    GS_WRITE64(GS_BGCOLOR, 0);
    GS_WRITE64(GS_SMODE1, 0x0000000740834504ull); /* PRST=1 */
    spin(10000);
    GS_WRITE64(GS_SMODE1, 0x0000000740804504ull); /* SINT=0 PRST=0 */
}

void dma_reset(void)
{
    D_CTRL = 1;            /* DMA enable */
    D_PCR = 0;
    D_SQWC = 0;
    D_STAT = 0x3FF;        /* clear channel status (mask bits untouched) */
    D_ENABLEW = 0x1201;    /* bit 16 clear: DMAC enabled (the value the BIOS leaves) */
    D2_CHCR = 0;
    D1_CHCR = 0;
    GIF_CTRL = 1;          /* reset GIF */
    GIF_MODE = 0;
}

void dma_gif_wait(void)
{
    while (D2_CHCR & CHCR_STR) {}
}

/* PATH3: normal-mode GIF DMA of nqw quadwords (pk must be a KSEG0 or KSEG1 address). */
void dma_gif_send(const qword_t *pk, u32 nqw)
{
    compiler_barrier_sync();
    dma_gif_wait();
    D2_MADR = PHYS(pk);
    D2_QWC = nqw;
    D2_CHCR = CHCR_DIR_FROM_MEM | CHCR_MOD_NORMAL | CHCR_STR;
    dma_gif_wait();
}

/*
 * Context 1 drawing environment as A+D quadwords: 640x448 PSMCT32 frame at FBP 0, PSMZ24 Z at page 140
 * (Z writes masked when zmsk), XYOFFSET (1024,1024), full-screen scissor, Z test ALWAYS, no alpha
 * blending/dither/FBA/PABE, colour clamp on, PRIM attributes from PRIM (PRMODECONT=1). 12 quadwords.
 */
void gs_setup_context1(qword_t **pp, u32 zmsk)
{
    qword_t *q = *pp;
    gif_ad(q++, GS_FRAME_1, GS_FRAME_V(FB_FBP, FB_FBW, PSMCT32, 0));
    gif_ad(q++, GS_ZBUF_1, GS_ZBUF_V(ZB_ZBP, PSMZ24, zmsk));
    gif_ad(q++, GS_XYOFFSET_1, GS_XYOFFSET_V(SCREEN_OFS << 4, SCREEN_OFS << 4));
    gif_ad(q++, GS_SCISSOR_1, GS_SCISSOR_V(0, FB_WIDTH - 1, 0, FB_HEIGHT - 1));
    gif_ad(q++, GS_PRMODECONT, 1);
    gif_ad(q++, GS_COLCLAMP, 1);
    gif_ad(q++, GS_DTHE, 0);
    gif_ad(q++, GS_TEST_1, GS_TEST_V(0, 0, 0, 0, 0, 0, 1, ZTST_ALWAYS));
    gif_ad(q++, GS_FBA_1, 0);
    gif_ad(q++, GS_PABE, 0);
    gif_ad(q++, GS_SCANMSK, 0);
    gif_ad(q++, GS_ALPHA_1, GS_ALPHA_V(0, 1, 0, 1, 0));
    *pp = q;
}

/* Mark the program finished in the status block (physical 0x001FFF00). */
void gv_finish(u32 program_id)
{
    GV_STATUS[1] = program_id;
    GV_STATUS[0] = GV_STATUS_MAGIC;
}
