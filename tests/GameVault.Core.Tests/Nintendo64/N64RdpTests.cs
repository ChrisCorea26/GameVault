using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Nintendo64;
using GameVault.Core.State;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>
/// The RDP on hand-built command lists: fill, copy and one-cycle rectangles, triangles with their coverage rule,
/// textures through TMEM and the TLUT, the Z buffer, the blender, the DPC registers and save states. Expected pixels
/// are worked out from the command values (n64brew: "Reality Display Processor/Commands").
/// </summary>
public sealed class N64RdpTests
{
    private const uint Frame = 0x100000;
    private const uint ZBuffer = 0x200000;
    private const uint Texture = 0x300000;
    private const uint Commands = 0x010000;
    private const int Width = 64;

    // Set Other Modes bits.
    private const ulong CycleOne = 0UL << 52;
    private const ulong CycleTwo = 1UL << 52;
    private const ulong CycleCopy = 2UL << 52;
    private const ulong CycleFill = 3UL << 52;
    private const ulong NoDither = (3UL << 38) | (3UL << 36);
    private const ulong Tlut = 1UL << 47;
    private const ulong ForceBlend = 1UL << 14;
    private const ulong ImageRead = 1UL << 6;
    private const ulong ZUpdate = 1UL << 5;
    private const ulong ZCompare = 1UL << 4;
    private const ulong ZSourcePrim = 1UL << 2;
    private const ulong AlphaCompare = 1;

    private readonly Host _host = new();
    private readonly N64Rdp _rdp;
    private readonly List<ulong> _list = [];

    public N64RdpTests()
    {
        _rdp = new N64Rdp(_host);
    }

    [Fact]
    public void Fill_rectangle_includes_its_right_column_and_bottom_row_and_alternates_the_fill_halves()
    {
        Setup16();
        Add(OtherModes(CycleFill));
        Add(0x37UL << 56 | 0x12345678);
        Add(FillRect(10, 10, 19, 19));
        Run();

        Pixel16(10, 10).Should().Be(0x1234, "even pixels take the fill colour's high half");
        Pixel16(11, 10).Should().Be(0x5678);
        Pixel16(19, 19).Should().Be(0x5678, "XL and YL are included in fill mode");
        Pixel16(20, 10).Should().Be(0);
        Pixel16(10, 20).Should().Be(0);
        Pixel16(9, 10).Should().Be(0);
    }

    [Fact]
    public void Fill_rectangle_writes_32_bit_pixels()
    {
        Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size32, Width, Frame));
        Add(Scissor(0, 0, Width, 64));
        Add(OtherModes(CycleFill));
        Add(0x37UL << 56 | 0xAABBCCDD);
        Add(FillRect(2, 3, 4, 5));
        Run();

        Pixel32(2, 3).Should().Be(0xAABBCCDD);
        Pixel32(4, 5).Should().Be(0xAABBCCDD);
        Pixel32(5, 5).Should().Be(0);
    }

    [Fact]
    public void One_cycle_rectangle_excludes_its_right_column_and_bottom_row()
    {
        Setup16();
        Add(OtherModes(CycleOne | NoDither));
        Add(CombinePrim());
        Add(0x3AUL << 56 | 0xF8000000); // primitive colour: red
        Add(FillRect(10, 10, 20, 20));
        Run();

        Color16(10, 10).Should().Be(0xF800);
        Color16(19, 19).Should().Be(0xF800);
        Color16(20, 19).Should().Be(0, "the right edge is exclusive outside fill and copy modes");
        Color16(19, 20).Should().Be(0);
    }

    [Fact]
    public void Scissor_clips_rectangles()
    {
        Setup16();
        Add(Scissor(12, 12, 15, 15));
        Add(OtherModes(CycleFill));
        Add(0x37UL << 56 | 0xFFFFFFFF);
        Add(FillRect(10, 10, 19, 19));
        Run();

        Pixel16(12, 12).Should().Be(0xFFFF);
        Pixel16(14, 14).Should().Be(0xFFFF);
        Pixel16(11, 12).Should().Be(0);
        Pixel16(15, 14).Should().Be(0xFFFF, "in fill mode the span runs up to and including the clipped edge");
        Pixel16(16, 14).Should().Be(0);
        Pixel16(14, 15).Should().Be(0, "YL is still exclusive");
    }

    [Fact]
    public void Copy_mode_texture_rectangle_copies_rgba16_texels_unchanged()
    {
        Setup16();
        var texels = new ushort[16];
        for (var i = 0; i < 16; i++)
        {
            texels[i] = (ushort)(0x1000 * (i + 1) | 1);
            WriteTexture16(i, texels[i]);
        }

        Add(SetTextureImage(N64Rdp.FormatRgba, N64Rdp.Size16, 4, Texture));
        Add(SetTile(N64Rdp.FormatRgba, N64Rdp.Size16, line: 1, tmem: 0, tile: 0));
        Add(LoadTile(0, 0, 0, 3, 3));
        Add(OtherModes(CycleCopy));
        AddTextureRectangle(0, 20, 20, 23, 23, s: 0, t: 0, dsdx: 4 << 10, dtdy: 1 << 10);
        Run();

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                Pixel16(20 + x, 20 + y).Should().Be(texels[y * 4 + x], $"texel ({x}, {y})");
            }
        }

        Pixel16(24, 20).Should().Be(0);
    }

    [Fact]
    public void Copy_mode_alpha_compare_skips_texels_without_their_alpha_bit()
    {
        Setup16();
        WriteTexture16(0, 0xF801);
        WriteTexture16(1, 0x07C0); // alpha bit clear
        Add(SetTextureImage(N64Rdp.FormatRgba, N64Rdp.Size16, 4, Texture));
        Add(SetTile(N64Rdp.FormatRgba, N64Rdp.Size16, line: 1, tmem: 0, tile: 0));
        Add(LoadTile(0, 0, 0, 3, 0));
        Add(OtherModes(CycleCopy | AlphaCompare));
        AddTextureRectangle(0, 0, 0, 1, 0, s: 0, t: 0, dsdx: 4 << 10, dtdy: 1 << 10);
        Run();

        Pixel16(0, 0).Should().Be(0xF801);
        Pixel16(1, 0).Should().Be(0);
    }

    [Fact]
    public void One_cycle_textured_rectangle_reads_i8_texels_point_sampled()
    {
        Setup16();
        for (var i = 0; i < 8; i++)
        {
            _host.Rdram[Texture + i] = (byte)(i * 32);
        }

        Add(SetTextureImage(N64Rdp.FormatI, N64Rdp.Size8, 8, Texture));
        Add(SetTile(N64Rdp.FormatI, N64Rdp.Size8, line: 1, tmem: 0, tile: 0));
        Add(LoadTile(0, 0, 0, 7, 0));
        Add(OtherModes(CycleOne | NoDither | (1UL << 43))); // point sampling, BI_LERP_0
        Add(CombineTexel0());
        AddTextureRectangle(0, 0, 0, 8, 1, s: 0, t: 0, dsdx: 1 << 10, dtdy: 1 << 10);
        Run();

        for (var x = 0; x < 8; x++)
        {
            var i = x * 32 >> 3;
            Color16(x, 0).Should().Be((ushort)(i << 11 | i << 6 | i << 1), $"texel {x}");
        }
    }

    [Fact]
    public void Ia8_texels_give_intensity_and_alpha()
    {
        Setup16();
        _host.Rdram[Texture] = 0xF8; // intensity 15, alpha 8

        Add(SetTextureImage(N64Rdp.FormatIa, N64Rdp.Size8, 8, Texture));
        Add(SetTile(N64Rdp.FormatIa, N64Rdp.Size8, line: 1, tmem: 0, tile: 0));
        Add(LoadTile(0, 0, 0, 7, 0));
        Add(OtherModes(CycleOne | NoDither | (1UL << 43) | AlphaCompare));
        Add(0x39UL << 56 | 0x00000089); // blend colour alpha 0x89: alpha compare drops the texel (alpha 0x88 is below)
        Add(CombineTexel0());
        AddTextureRectangle(0, 0, 0, 1, 1, s: 0, t: 0, dsdx: 1 << 10, dtdy: 1 << 10);
        Add(0x39UL << 56 | 0x00000080); // alpha 0x88 >= 0x80: drawn
        AddTextureRectangle(0, 0, 1, 1, 2, s: 0, t: 0, dsdx: 1 << 10, dtdy: 1 << 10);
        Run();

        Color16(0, 0).Should().Be(0, "the texel's alpha (0x88) is below the threshold");
        Color16(0, 1).Should().Be(0xFFFE, "intensity 0xFF on all channels; alpha doesn't reach a 16-bit pixel's colour");
    }

    [Fact]
    public void Ci4_texels_go_through_the_tlut_with_the_tile_palette()
    {
        Setup16();
        _host.Rdram[Texture] = 0x12; // texels 1 and 2
        BinaryPrimitives.WriteUInt16BigEndian(_host.Rdram.AsSpan((int)Texture + 0x100 + 0x11 * 2), 0xF801); // palette 1, entry 1
        BinaryPrimitives.WriteUInt16BigEndian(_host.Rdram.AsSpan((int)Texture + 0x100 + 0x12 * 2), 0x07C1); // palette 1, entry 2

        Add(SetTextureImage(N64Rdp.FormatRgba, N64Rdp.Size16, 1, Texture + 0x100));
        Add(SetTile(0, N64Rdp.Size4, line: 0, tmem: 0x100, tile: 7));
        Add(LoadTlut(7, 0, 0, 255));
        Add(SetTextureImage(N64Rdp.FormatCi, N64Rdp.Size8, 8, Texture));
        Add(SetTile(N64Rdp.FormatCi, N64Rdp.Size8, line: 1, tmem: 0, tile: 0));
        Add(LoadTile(0, 0, 0, 7, 0));
        Add(SetTile(N64Rdp.FormatCi, N64Rdp.Size4, line: 1, tmem: 0, tile: 0, palette: 1));
        Add(OtherModes(CycleOne | NoDither | (1UL << 43) | Tlut));
        Add(CombineTexel0());
        AddTextureRectangle(0, 0, 0, 2, 1, s: 0, t: 0, dsdx: 1 << 10, dtdy: 1 << 10);
        Run();

        Color16(0, 0).Should().Be(0xF800, "texel 1 of palette 1 is red");
        Color16(1, 0).Should().Be(0x07C0, "texel 2 of palette 1 is green");
    }

    [Fact]
    public void Triangle_draws_pixels_whose_top_left_sample_is_inside()
    {
        // A right triangle: the left edge at X = 10 (major, on the left) from Y = 10 to 30, a flat top from X = 10 to 30,
        // and the hypotenuse from (30, 10) down to (10, 30).
        Setup16();
        Add(OtherModes(CycleOne | NoDither));
        Add(CombinePrim());
        Add(0x3AUL << 56 | 0xFFFFFFFF);
        AddTriangle(0x08, leftMajor: true, yl: 30, ym: 10, yh: 10, xl: 30, dxldy: -1, xh: 10, dxhdy: 0, xm: 30, dxmdy: 0);
        Run();

        Color16(10, 10).Should().Be(0xFFFE);
        Color16(29, 10).Should().Be(0xFFFE);
        Color16(30, 10).Should().Be(0, "the right edge is exclusive");
        Color16(20, 19).Should().Be(0xFFFE, "on row 19 the hypotenuse is at X = 21");
        Color16(21, 19).Should().Be(0);
        Color16(10, 29).Should().Be(0xFFFE);
        Color16(11, 29).Should().Be(0);
        Color16(10, 30).Should().Be(0, "YL is exclusive");
        Color16(9, 15).Should().Be(0);
    }

    [Fact]
    public void Shaded_triangle_interpolates_from_the_major_edge()
    {
        Setup16();
        Add(OtherModes(CycleOne | NoDither));
        Add(CombineShade());
        var shade = new ulong[8];
        shade[1] = 8UL << 48; // DrDx = 8.0: the per-pixel integer word holds R, G, B and A
        AddTriangle(0x0C, leftMajor: true, yl: 30, ym: 10, yh: 10, xl: 30, dxldy: -1, xh: 10, dxhdy: 0, xm: 30, dxmdy: 0, shade);
        Run();

        Color16(10, 12).Should().Be(0, "red is 0 on the major edge");
        Color16(15, 12).Should().Be((ushort)((40 >> 3) << 11), "five pixels in, red is 40");
        Color16(20, 15).Should().Be((ushort)((80 >> 3) << 11));
    }

    [Fact]
    public void Z_buffer_keeps_the_nearer_rectangle_whatever_the_order()
    {
        Setup16();
        Add(0x3EUL << 56 | ZBuffer);
        Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size16, Width, ZBuffer));
        Add(OtherModes(CycleFill));
        Add(0x37UL << 56 | 0xFFFCFFFC); // clear the Z buffer to the farthest depth
        Add(FillRect(0, 0, Width - 1, 31));
        Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size16, Width, Frame));
        Add(OtherModes(CycleOne | NoDither | ZCompare | ZUpdate | ZSourcePrim));
        Add(CombinePrim());

        // Near (Z 0x1000) red first, then far (Z 0x6000) green over it: green only where red isn't.
        Add(0x2EUL << 56 | 0x1000UL << 16);
        Add(0x3AUL << 56 | 0xF8000000);
        Add(FillRect(0, 0, 10, 10));
        Add(0x2EUL << 56 | 0x6000UL << 16);
        Add(0x3AUL << 56 | 0x00F800FF);
        Add(FillRect(5, 0, 20, 10));
        Run();

        Color16(2, 2).Should().Be(0xF800);
        Color16(7, 2).Should().Be(0xF800, "the far green rectangle is hidden behind the red one");
        Color16(15, 2).Should().Be(0x07C0);
        var stored = BinaryPrimitives.ReadUInt16BigEndian(_host.Rdram.AsSpan((int)ZBuffer + (2 * Width + 7) * 2));
        N64Rdp.DecompressZ(stored >> 2).Should().BeCloseTo(0x1000 << 3, 64, "the red rectangle's depth was stored");
    }

    [Fact]
    public void Z_compression_round_trips_within_its_precision()
    {
        foreach (var z in new[] { 0, 0x100, 0x1FFFF, 0x20000, 0x30000, 0x3C000, 0x3F800, 0x3FFFF })
        {
            var back = N64Rdp.DecompressZ(N64Rdp.CompressZ(z));
            back.Should().BeLessThanOrEqualTo(z);
            (z - back).Should().BeLessThan(64);
        }

        N64Rdp.CompressZ(0x3FFFF).Should().Be(0x3FFF);
    }

    [Fact]
    public void Force_blend_mixes_the_pixel_with_memory_by_its_alpha()
    {
        Setup16();
        Add(OtherModes(CycleFill));
        Add(0x37UL << 56 | 0x003E003E); // blue background
        Add(FillRect(0, 0, 7, 7));

        // P = pixel, A = pixel alpha, M = memory, B = 1 − A: half red over blue.
        Add(OtherModes(CycleOne | NoDither | ForceBlend | ImageRead | (1UL << 22)));
        Add(CombinePrim());
        Add(0x3AUL << 56 | 0xFF000080);
        Add(FillRect(0, 0, 4, 4));
        Run();

        var pixel = Pixel16(1, 1);
        var red = (pixel >> 11) & 31;
        var blue = (pixel >> 1) & 31;
        red.Should().BeInRange(15, 16);
        blue.Should().BeInRange(15, 16);
    }

    [Fact]
    public void Two_cycle_mode_feeds_the_first_cycle_into_the_second()
    {
        Setup16();
        Add(OtherModes(CycleTwo | NoDither | (3UL << 26) | (2UL << 18))); // first blender cycle passes the pixel through

        // Cycle 0: primitive colour. Cycle 1: COMBINED × ENVIRONMENT.
        Add(Combine(subA0: 15, mul0: 31, subAa0: 7, mula0: 7, subA1: 0, mul1: 5, subB0: 15, subB1: 15, subAa1: 7, mula1: 7,
            add0: 3, subBa0: 7, adda0: 7, add1: 7, subBa1: 7, adda1: 6));
        Add(0x3AUL << 56 | 0xF0F0F0FF);
        Add(0x3BUL << 56 | 0x80808080);
        Add(FillRect(0, 0, 4, 4));
        Run();

        var expected = (0xF0 * 0x80 + 0x80) >> 8; // 120
        Color16(1, 1).Should().Be((ushort)((expected >> 3) << 11 | (expected >> 3) << 6 | (expected >> 3) << 1));
    }

    [Fact]
    public void Sync_full_raises_the_dp_interrupt()
    {
        Setup16();
        Add(0x29UL << 56);
        Run();
        _host.Raised.Should().Contain(N64Interrupt.Dp);
    }

    [Fact]
    public void Commands_can_come_from_dmem_over_the_xbus()
    {
        Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size16, Width, Frame));
        Add(Scissor(0, 0, Width, 64));
        Add(OtherModes(CycleFill));
        Add(0x37UL << 56 | 0x55555555);
        Add(FillRect(1, 1, 1, 1));
        for (var i = 0; i < _list.Count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(_host.Dmem[(i * 8)..], _list[i]);
        }

        _rdp.WriteRegister(N64Rdp.RegisterBase + 0xC, 2); // set XBUS
        _rdp.WriteRegister(N64Rdp.RegisterBase, 0);
        _rdp.WriteRegister(N64Rdp.RegisterBase + 4, (uint)(_list.Count * 8));

        (_rdp.ReadRegister(N64Rdp.RegisterBase + 0xC) & N64Rdp.StatusXbus).Should().NotBe(0);
        Pixel16(1, 1).Should().Be(0x5555);
    }

    [Fact]
    public void A_command_split_across_two_submissions_runs_once_complete()
    {
        Setup16();
        Add(OtherModes(CycleOne | NoDither));
        Add(CombinePrim());
        Add(0x3AUL << 56 | 0xFFFFFFFF);
        AddTriangle(0x08, leftMajor: true, yl: 30, ym: 10, yh: 10, xl: 30, dxldy: -1, xh: 10, dxhdy: 0, xm: 30, dxmdy: 0);
        WriteList();
        var end = Commands + (uint)(_list.Count * 8);

        _rdp.WriteRegister(N64Rdp.RegisterBase, Commands);
        _rdp.WriteRegister(N64Rdp.RegisterBase + 4, end - 16); // the triangle's last two words aren't there yet
        Color16(15, 15).Should().Be(0);
        _rdp.ReadRegister(N64Rdp.RegisterBase + 8).Should().Be(end - 16);

        _rdp.WriteRegister(N64Rdp.RegisterBase + 4, end);
        Color16(15, 15).Should().Be(0xFFFE);
    }

    [Fact]
    public void Save_state_round_trip_continues_identically()
    {
        Setup16();
        _host.Rdram[Texture] = 0x80;
        Add(SetTextureImage(N64Rdp.FormatI, N64Rdp.Size8, 8, Texture));
        Add(SetTile(N64Rdp.FormatI, N64Rdp.Size8, line: 1, tmem: 0, tile: 0));
        Add(LoadTile(0, 0, 0, 7, 0));
        Add(OtherModes(CycleOne | (1UL << 43)));
        Add(CombineTexel0());
        Run();

        var writer = new StateWriter();
        _rdp.SaveState(writer);
        var copyHost = new Host();
        _host.Rdram.CopyTo(copyHost.Rdram, 0);
        var copy = new N64Rdp(copyHost);
        copy.LoadState(new StateReader(writer.ToArray()));

        // The same textured rectangle on both, using the TMEM and modes carried by the state.
        _list.Clear();
        AddTextureRectangle(0, 0, 0, 16, 16, s: 0, t: 0, dsdx: 1 << 10, dtdy: 1 << 10);
        WriteList(Commands + 0x1000);
        WriteList(Commands + 0x1000, copyHost);
        foreach (var rdp in new[] { _rdp, copy })
        {
            rdp.WriteRegister(N64Rdp.RegisterBase, Commands + 0x1000);
            rdp.WriteRegister(N64Rdp.RegisterBase + 4, Commands + 0x1000 + (uint)(_list.Count * 8));
        }

        copyHost.Rdram.AsSpan((int)Frame, Width * 32 * 2).SequenceEqual(_host.Rdram.AsSpan((int)Frame, Width * 32 * 2)).Should().BeTrue();
        Pixel16(3, 3).Should().NotBe(0);

        var again = new StateWriter();
        copy.SaveState(again);
        var mine = new StateWriter();
        _rdp.SaveState(mine);
        again.ToArray().Should().Equal(mine.ToArray());
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private void Setup16()
    {
        Add(SetColorImage(N64Rdp.FormatRgba, N64Rdp.Size16, Width, Frame));
        Add(Scissor(0, 0, Width, 64));
    }

    private void Add(ulong word) => _list.Add(word);

    private void Run()
    {
        WriteList();
        _rdp.WriteRegister(N64Rdp.RegisterBase, Commands);
        _rdp.WriteRegister(N64Rdp.RegisterBase + 4, Commands + (uint)(_list.Count * 8));
    }

    private void WriteList(uint at = Commands, Host? host = null)
    {
        for (var i = 0; i < _list.Count; i++)
        {
            BinaryPrimitives.WriteUInt64BigEndian((host ?? _host).Rdram.AsSpan((int)at + i * 8), _list[i]);
        }
    }

    private ushort Pixel16(int x, int y) => BinaryPrimitives.ReadUInt16BigEndian(_host.Rdram.AsSpan((int)Frame + (y * Width + x) * 2));

    /// <summary>A 16-bit pixel's colour, without its coverage bit.</summary>
    private ushort Color16(int x, int y) => (ushort)(Pixel16(x, y) & 0xFFFE);

    private uint Pixel32(int x, int y) => BinaryPrimitives.ReadUInt32BigEndian(_host.Rdram.AsSpan((int)Frame + (y * Width + x) * 4));

    private void WriteTexture16(int index, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(_host.Rdram.AsSpan((int)Texture + index * 2), value);

    private static ulong SetColorImage(int format, int size, int width, uint address) =>
        0x3FUL << 56 | (ulong)(uint)format << 53 | (ulong)(uint)size << 51 | (ulong)(uint)(width - 1) << 32 | address;

    private static ulong SetTextureImage(int format, int size, int width, uint address) =>
        0x3DUL << 56 | (ulong)(uint)format << 53 | (ulong)(uint)size << 51 | (ulong)(uint)(width - 1) << 32 | address;

    private static ulong Scissor(int xh, int yh, int xl, int yl) =>
        0x2DUL << 56 | (ulong)(uint)(xh * 4) << 44 | (ulong)(uint)(yh * 4) << 32 | (ulong)(uint)(xl * 4) << 12 | (ulong)(uint)(yl * 4);

    private static ulong OtherModes(ulong bits) => 0x2FUL << 56 | bits;

    private static ulong FillRect(int xh, int yh, int xl, int yl) =>
        0x36UL << 56 | (ulong)(uint)(xl * 4) << 44 | (ulong)(uint)(yl * 4) << 32 | (ulong)(uint)(xh * 4) << 12 | (ulong)(uint)(yh * 4);

    private static ulong SetTile(int format, int size, int line, int tmem, int tile, int palette = 0) =>
        0x35UL << 56 | (ulong)(uint)format << 53 | (ulong)(uint)size << 51 | (ulong)(uint)line << 41 | (ulong)(uint)tmem << 32 | (ulong)(uint)tile << 24 |
        (ulong)(uint)palette << 20;

    private static ulong LoadTile(int tile, int sl, int tl, int sh, int th) =>
        0x34UL << 56 | (ulong)(uint)(sl * 4) << 44 | (ulong)(uint)(tl * 4) << 32 | (ulong)(uint)tile << 24 | (ulong)(uint)(sh * 4) << 12 | (ulong)(uint)(th * 4);

    private static ulong LoadTlut(int tile, int sl, int tl, int sh) =>
        0x30UL << 56 | (ulong)(uint)(sl * 4) << 44 | (ulong)(uint)(tl * 4) << 32 | (ulong)(uint)tile << 24 | (ulong)(uint)(sh * 4) << 12 | (ulong)(uint)(tl * 4);

    private void AddTextureRectangle(int tile, int xh, int yh, int xl, int yl, int s, int t, int dsdx, int dtdy)
    {
        Add(0x24UL << 56 | (ulong)(uint)(xl * 4) << 44 | (ulong)(uint)(yl * 4) << 32 | (ulong)(uint)tile << 24 | (ulong)(uint)(xh * 4) << 12 | (ulong)(uint)(yh * 4));
        Add((ulong)(ushort)s << 48 | (ulong)(ushort)t << 32 | (ulong)(ushort)dsdx << 16 | (ushort)dtdy);
    }

    /// <summary>A triangle with integer coordinates and slopes (in pixels per scanline); coefficient words follow.</summary>
    private void AddTriangle(int id, bool leftMajor, int yl, int ym, int yh, int xl, int dxldy, int xh, int dxhdy, int xm, int dxmdy,
        ulong[]? shade = null)
    {
        Add((ulong)(uint)id << 56 | (leftMajor ? 1UL << 55 : 0) | (ulong)(uint)(yl * 4 & 0x3FFF) << 32 | (ulong)(uint)(ym * 4 & 0x3FFF) << 16 |
            (ulong)(uint)(yh * 4 & 0x3FFF));
        Add((ulong)(uint)(xl << 16) << 32 | (uint)(dxldy << 16));
        Add((ulong)(uint)(xh << 16) << 32 | (uint)(dxhdy << 16));
        Add((ulong)(uint)(xm << 16) << 32 | (uint)(dxmdy << 16));
        if (shade is not null)
        {
            foreach (var word in shade)
            {
                Add(word);
            }
        }
    }

    private static ulong Combine(int subA0, int mul0, int subAa0, int mula0, int subA1, int mul1, int subB0, int subB1, int subAa1, int mula1,
        int add0, int subBa0, int adda0, int add1, int subBa1, int adda1) =>
        0x3CUL << 56 | (ulong)(uint)subA0 << 52 | (ulong)(uint)mul0 << 47 | (ulong)(uint)subAa0 << 44 | (ulong)(uint)mula0 << 41 | (ulong)(uint)subA1 << 37 |
        (ulong)(uint)mul1 << 32 | (ulong)(uint)subB0 << 28 | (ulong)(uint)subB1 << 24 | (ulong)(uint)subAa1 << 21 | (ulong)(uint)mula1 << 18 | (ulong)(uint)add0 << 15 |
        (ulong)(uint)subBa0 << 12 | (ulong)(uint)adda0 << 9 | (ulong)(uint)add1 << 6 | (ulong)(uint)subBa1 << 3 | (ulong)(uint)adda1;

    /// <summary>Both cycles: colour and alpha = primitive.</summary>
    private static ulong CombinePrim() => Combine(15, 31, 7, 7, 15, 31, 15, 15, 7, 7, 3, 7, 3, 3, 7, 3);

    /// <summary>Both cycles: colour and alpha = shade.</summary>
    private static ulong CombineShade() => Combine(15, 31, 7, 7, 15, 31, 15, 15, 7, 7, 4, 7, 4, 4, 7, 4);

    /// <summary>Both cycles: colour and alpha = texel 0.</summary>
    private static ulong CombineTexel0() => Combine(15, 31, 7, 7, 15, 31, 15, 15, 7, 7, 1, 7, 1, 1, 7, 1);

    private sealed class Host : IN64RcpHost
    {
        private readonly byte[] _dmem = new byte[4096];

        public byte[] Rdram { get; } = new byte[8 * 1024 * 1024];

        public List<N64Interrupt> Raised { get; } = [];

        Span<byte> IN64RcpHost.Rdram => Rdram;

        public Span<byte> Dmem => _dmem;

        public void RaiseInterrupt(N64Interrupt source) => Raised.Add(source);
    }
}
