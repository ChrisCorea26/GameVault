using FluentAssertions;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>The PCRTC: picture size from DISPLAY, DISPFB's buffer, the merge circuit, BGCOLOR and field mode.</summary>
public sealed class Ps2GsOutputTests
{
    private readonly ushort[] _frame = new ushort[Ps2Gs.MaxFrameWidth * Ps2Gs.MaxFrameHeight];

    private static ulong DispFb(int fbp, int fbw, int psm, int dbx = 0, int dby = 0) =>
        (ulong)fbp | ((ulong)fbw << 9) | ((ulong)psm << 15) | ((ulong)dbx << 32) | ((ulong)dby << 43);

    private static ulong Display(int dx, int dy, int magh, int magv, int dw, int dh) =>
        (ulong)dx | ((ulong)dy << 12) | ((ulong)magh << 23) | ((ulong)magv << 27) | ((ulong)dw << 32) | ((ulong)dh << 44);

    private static ushort Bgr555(int r, int g, int b) => (ushort)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10));

    private static Ps2Gs NtscInterlaced(int psm = Ps2Gs.PsmCt32, int magh = 3)
    {
        var gs = new Ps2Gs();
        gs.WritePrivileged(Ps2Gs.PrivSmode2, 1); // interlaced, field mode
        gs.WritePrivileged(Ps2Gs.PrivPmode, 1 | (1 << 5) | (0xFF << 8)); // circuit 1, ALP 0xFF
        gs.WritePrivileged(Ps2Gs.PrivDispFb1, DispFb(0, 10, psm));
        gs.WritePrivileged(Ps2Gs.PrivDisplay1, Display(636, 50, magh, 0, 2559, 447));
        return gs;
    }

    [Fact]
    public void A_typical_ntsc_field_mode_display_is_640_by_448()
    {
        var gs = NtscInterlaced();
        gs.WritePixel(Ps2Gs.PsmCt32, 0, 10, 0, 0, 0x80_8008F8);
        gs.WritePixel(Ps2Gs.PsmCt32, 0, 10, 639, 447, 0x80_FFFFFF);
        gs.WritePixel(Ps2Gs.PsmCt32, 0, 10, 300, 201, 0x80_102030);
        gs.RenderFrame(_frame, out var width, out var height);
        width.Should().Be(640);
        height.Should().Be(448);
        _frame[0].Should().Be(Bgr555(0xF8, 0x08, 0x80), "red in the low five bits");
        _frame[(447 * 640) + 639].Should().Be(0x7FFF);
        _frame[(201 * 640) + 300].Should().Be(Bgr555(0x30, 0x20, 0x10));
    }

    [Fact]
    public void Magh_4_gives_512_wide_and_pal_gives_512_lines()
    {
        var gs = NtscInterlaced(magh: 4);
        gs.RenderFrame(_frame, out var width, out var height);
        width.Should().Be(512);
        height.Should().Be(448);

        gs.WritePrivileged(Ps2Gs.PrivDisplay1, Display(652, 72, 3, 0, 2559, 511));
        gs.RenderFrame(_frame, out width, out height);
        width.Should().Be(640);
        height.Should().Be(512);
    }

    [Fact]
    public void Dispfb_offsets_and_16_bit_buffers()
    {
        var gs = NtscInterlaced(Ps2Gs.PsmCt16S);
        gs.WritePrivileged(Ps2Gs.PrivDispFb1, DispFb(20, 10, Ps2Gs.PsmCt16S, dbx: 5, dby: 7));
        gs.WritePixel(Ps2Gs.PsmCt16S, 20 * 32, 10, 5 + 3, 7 + 2, 0x7C1F);
        gs.RenderFrame(_frame, out var width, out _);
        _frame[(2 * width) + 3].Should().Be(0x7C1F);
    }

    [Fact]
    public void Merge_uses_alp_or_the_pixel_alpha_over_circuit_2_or_bgcolor()
    {
        var gs = NtscInterlaced();
        gs.WritePrivileged(Ps2Gs.PrivDispFb2, DispFb(100, 10, Ps2Gs.PsmCt32));
        gs.WritePrivileged(Ps2Gs.PrivDisplay2, Display(636, 50, 3, 0, 2559, 447));
        gs.WritePrivileged(Ps2Gs.PrivBgColor, 0x0000F8);
        gs.WritePixel(Ps2Gs.PsmCt32, 0, 10, 0, 0, 0x40_F80000); // circuit 1: blue, alpha 0x40
        gs.WritePixel(Ps2Gs.PsmCt32, 100 * 32, 10, 0, 0, 0x00_00F800); // circuit 2: green

        gs.WritePrivileged(Ps2Gs.PrivPmode, 3 | (1 << 5) | (0x80 << 8)); // ALP 0x80
        gs.RenderFrame(_frame, out _, out _);
        _frame[0].Should().Be(Bgr555(0, (0xF8 * 0x7F) / 255, (0xF8 * 0x80) / 255));

        gs.WritePrivileged(Ps2Gs.PrivPmode, 3); // pixel alpha 0x40 → weight 0x80
        gs.RenderFrame(_frame, out _, out _);
        _frame[0].Should().Be(Bgr555(0, (0xF8 * 0x7F) / 255, (0xF8 * 0x80) / 255));

        gs.WritePrivileged(Ps2Gs.PrivPmode, 3 | (1 << 5) | (1 << 7) | (0x00 << 8)); // SLBG: over BGCOLOR, ALP 0
        gs.RenderFrame(_frame, out _, out _);
        _frame[0].Should().Be(Bgr555(0xF8, 0, 0));
    }

    [Fact]
    public void Nothing_enabled_shows_bgcolor()
    {
        var gs = NtscInterlaced();
        gs.WritePrivileged(Ps2Gs.PrivPmode, 0);
        gs.WritePrivileged(Ps2Gs.PrivBgColor, 0x102030);
        gs.RenderFrame(_frame, out var width, out var height);
        (width, height).Should().Be((640, 448));
        _frame[1234].Should().Be(Bgr555(0x30, 0x20, 0x10));
    }

    [Fact]
    public void Frame_mode_reads_each_buffer_line_for_two_picture_lines()
    {
        var gs = NtscInterlaced();
        gs.WritePrivileged(Ps2Gs.PrivSmode2, 3); // interlaced, frame mode
        gs.WritePixel(Ps2Gs.PsmCt32, 0, 10, 4, 1, 0x80_0000F8);
        gs.RenderFrame(_frame, out var width, out var height);
        height.Should().Be(448);
        _frame[(2 * width) + 4].Should().Be(Bgr555(0xF8, 0, 0));
        _frame[(3 * width) + 4].Should().Be(Bgr555(0xF8, 0, 0));
        _frame[(1 * width) + 4].Should().Be(0);
    }

    [Fact]
    public void A_drawn_frame_reaches_the_picture()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegFrame1, GsRegs.Frame(0, 10, Ps2Gs.PsmCt32));
        f.W(Ps2Gs.RegScissor1, GsRegs.Scissor(0, 639, 0, 447));
        f.Sprite(0, 0, 640, 448, 0x20, 0x40, 0x60);
        f.Sprite(100, 100, 110, 110, 0xF8, 0xF8, 0xF8);
        f.Gs.WritePrivileged(Ps2Gs.PrivSmode2, 1); // interlaced, field mode
        f.Gs.WritePrivileged(Ps2Gs.PrivPmode, 1 | (1 << 5) | (0xFF << 8));
        f.Gs.WritePrivileged(Ps2Gs.PrivDispFb1, DispFb(0, 10, Ps2Gs.PsmCt32));
        f.Gs.WritePrivileged(Ps2Gs.PrivDisplay1, Display(636, 50, 3, 0, 2559, 447));
        f.Gs.RenderFrame(_frame, out var width, out _);
        _frame[(50 * width) + 50].Should().Be(Bgr555(0x20, 0x40, 0x60));
        _frame[(105 * width) + 105].Should().Be(Bgr555(0xF8, 0xF8, 0xF8));
        _frame[(110 * width) + 110].Should().Be(Bgr555(0x20, 0x40, 0x60));
    }
}
