using FluentAssertions;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>
/// Pixel probes of the rasterizer and pixel pipeline. Expected values are worked out from the register values and
/// the formulas ps2tek gives ("GS Textures", "GS Alpha Blending", "GS Tests and Pixel Control").
/// </summary>
public sealed class Ps2GsRasterTests
{
    private static uint Rgba(int r, int g, int b, int a = 0x80) => (uint)(r | (g << 8) | (b << 16) | (a << 24));

    private static int Red(uint c) => (int)(c & 0xFF);

    // ---- Primitives and coverage ------------------------------------------------------------------------------

    [Fact]
    public void Sprite_covers_its_top_left_corner_but_not_its_bottom_right()
    {
        var f = new GsFixture();
        f.Sprite(10, 20, 14, 23, 0xFF, 0, 0);
        f.Pixel(10, 20).Should().Be(Rgba(0xFF, 0, 0));
        f.Pixel(13, 22).Should().Be(Rgba(0xFF, 0, 0));
        f.Pixel(14, 22).Should().Be(0);
        f.Pixel(13, 23).Should().Be(0);
        f.Pixel(9, 20).Should().Be(0);
    }

    [Fact]
    public void Sprite_edges_at_subpixel_positions_cover_the_sample_points_inside()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(1, 2, 3));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz((10 * 16) + 1, (5 * 16) + 8));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz((12 * 16) + 1, (7 * 16) + 8));
        f.Pixel(10, 6).Should().Be(0, "x = 10 is left of 10 + 1/16");
        f.Pixel(11, 6).Should().NotBe(0);
        f.Pixel(12, 6).Should().NotBe(0, "x = 12 is left of 12 + 1/16");
        f.Pixel(12, 7).Should().NotBe(0);
        f.Pixel(12, 8).Should().Be(0);
        f.Pixel(11, 5).Should().Be(0);
    }

    [Fact]
    public void Triangles_sharing_an_edge_draw_each_pixel_exactly_once()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegAlpha1, GsRegs.Alpha(0, 2, 2, 1, fix: 0x80)); // Cs + Cd
        f.W(Ps2Gs.RegColClamp, 1);
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangleStrip, abe: true));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(10, 0, 0));
        // A quad (3.5, 2.25)–(40.75, 30.5) as a strip of two triangles split on a diagonal, plus a third triangle
        // sharing the far edge.
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(56, 36));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(652, 36));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(56, 488));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(652, 488));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(900, 800));

        var drawn = 0;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var r = Red(f.Pixel(x, y));
                r.Should().BeOneOf([0, 10], $"pixel ({x}, {y}) was drawn more than once");
                drawn += r == 10 ? 1 : 0;
            }
        }

        // Columns 4..40 and rows 3..30 inside the quad.
        var quad = 0;
        for (var y = 3; y <= 30; y++)
        {
            for (var x = 4; x <= 40; x++)
            {
                Red(f.Pixel(x, y)).Should().Be(10, $"({x}, {y}) is inside the quad");
                quad++;
            }
        }

        drawn.Should().BeGreaterThan(quad, "the third triangle adds pixels beyond the quad");
    }

    [Fact]
    public void Triangle_fill_follows_the_top_left_rule()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangle));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0xFF, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(16 * 16, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 16 * 16));
        f.Pixel(0, 0).Should().NotBe(0, "the top edge and the left edge are included");
        f.Pixel(15, 0).Should().NotBe(0);
        f.Pixel(16, 0).Should().Be(0, "the right vertex is on the excluded diagonal");
        f.Pixel(0, 15).Should().NotBe(0);
        f.Pixel(0, 16).Should().Be(0);
        f.Pixel(8, 8).Should().Be(0, "on the diagonal, a right-hand edge");
        f.Pixel(7, 8).Should().NotBe(0);
    }

    [Fact]
    public void Triangle_fan_and_vertex_queue()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangleFan));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0xFF, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(50 * 16, 50 * 16)); // centre
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(60 * 16, 50 * 16));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(50 * 16, 60 * 16));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(40 * 16, 50 * 16));
        f.Pixel(52, 52).Should().NotBe(0, "first fan triangle");
        f.Pixel(47, 52).Should().NotBe(0, "second fan triangle shares the centre");
        f.Pixel(50, 45).Should().Be(0);
    }

    [Fact]
    public void Xyz3_advances_the_queue_without_drawing()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangleStrip));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0xFF, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(32 * 16, 0));
        f.W(Ps2Gs.RegXyz3, GsRegs.Xyz(0, 32 * 16));
        f.Pixel(4, 4).Should().Be(0, "XYZ3 completes the triangle without a drawing kick");
        f.W(Ps2Gs.RegXyzf2, GsRegs.Xyzf(32 * 16, 32 * 16, 0, 0));
        f.Pixel(28, 28).Should().NotBe(0, "the strip's next triangle uses the vertex XYZ3 queued");
        f.Pixel(4, 4).Should().Be(0);
    }

    [Fact]
    public void Prim_write_resets_the_vertex_queue()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0xFF, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(8 * 16, 8 * 16));
        f.Pixel(2, 2).Should().Be(0);
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(12 * 16, 12 * 16));
        f.Pixel(10, 10).Should().NotBe(0);
    }

    [Fact]
    public void Lines_and_strips_draw_their_shared_pixels_once()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegAlpha1, GsRegs.Alpha(0, 2, 2, 1, fix: 0x80));
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimLineStrip, abe: true));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(10, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(10 * 16, 10 * 16));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(20 * 16, 10 * 16));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(20 * 16, 20 * 16));
        for (var x = 10; x < 20; x++)
        {
            Red(f.Pixel(x, 10)).Should().Be(10, $"({x}, 10) on the first segment");
        }

        Red(f.Pixel(20, 10)).Should().Be(10, "the joint belongs to the second segment only");
        Red(f.Pixel(20, 19)).Should().Be(10);
        Red(f.Pixel(20, 20)).Should().Be(0, "the strip's last vertex is not drawn");
    }

    [Fact]
    public void Points_draw_the_nearest_pixel()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimPoint));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0x40, 0x50, 0x60));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz((5 * 16) + 4, (7 * 16) + 12));
        f.Pixel(5, 8).Should().Be(Rgba(0x40, 0x50, 0x60));
    }

    // ---- Shading ----------------------------------------------------------------------------------------------

    [Fact]
    public void Gouraud_interpolates_colours_and_flat_takes_the_last_vertex()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangle, iip: true));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0, 0, 200));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(255, 0, 200));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(255 * 16, 0));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0, 255, 200));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 255 * 16));
        var c = f.Pixel(100, 30);
        Red(c).Should().Be(100);
        ((int)((c >> 8) & 0xFF)).Should().Be(30);
        ((int)((c >> 16) & 0xFF)).Should().Be(200);

        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangle));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(1, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(2, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(255 * 16, 0));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(3, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 255 * 16));
        Red(f.Pixel(100, 30)).Should().Be(3);
    }

    [Fact]
    public void Xyoffset_moves_the_window_origin()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegXyOffset1, (ulong)(2048 * 16) | ((ulong)(2048 * 16) << 32));
        f.Sprite(2048 + 3, 2048 + 4, 2048 + 5, 2048 + 6, 0xFF, 0, 0);
        f.Pixel(3, 4).Should().NotBe(0);
        f.Pixel(4, 5).Should().NotBe(0);
        f.Pixel(5, 5).Should().Be(0);
    }

    [Fact]
    public void Scissor_clips_inclusively()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegScissor1, GsRegs.Scissor(5, 9, 6, 8));
        f.Sprite(0, 0, 32, 32, 0xFF, 0, 0);
        f.Pixel(5, 6).Should().NotBe(0);
        f.Pixel(9, 8).Should().NotBe(0);
        f.Pixel(4, 6).Should().Be(0);
        f.Pixel(10, 8).Should().Be(0);
        f.Pixel(9, 9).Should().Be(0);
        f.Pixel(9, 5).Should().Be(0);
    }

    [Fact]
    public void Context_2_uses_its_own_frame_and_scissor()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegFrame2, GsRegs.Frame(50, 1, Ps2Gs.PsmCt32));
        f.W(Ps2Gs.RegScissor2, GsRegs.Scissor(0, 63, 0, 63));
        f.W(Ps2Gs.RegTest2, GsRegs.Test(zte: false));
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite, ctxt: 1));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0x11, 0x22, 0x33));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(4 * 16, 4 * 16));
        f.Pixel(1, 1).Should().Be(0);
        f.Gs.ReadPixel(Ps2Gs.PsmCt32, 50 * 32, 1, 1, 1).Should().Be(Rgba(0x11, 0x22, 0x33));
    }

    [Fact]
    public void Prmodecont_zero_takes_the_attributes_from_prmode()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegPrModeCont, 0);
        f.W(Ps2Gs.RegPrMode, GsRegs.Prim(0, ctxt: 1));
        f.W(Ps2Gs.RegFrame2, GsRegs.Frame(50, 1, Ps2Gs.PsmCt32));
        f.W(Ps2Gs.RegScissor2, GsRegs.Scissor(0, 63, 0, 63));
        f.Sprite(0, 0, 2, 2, 0x44, 0, 0);
        f.Pixel(0, 0).Should().Be(0);
        f.Gs.ReadPixel(Ps2Gs.PsmCt32, 50 * 32, 1, 0, 0).Should().Be(Rgba(0x44, 0, 0));
    }

    // ---- Depth ------------------------------------------------------------------------------------------------

    [Fact]
    public void Depth_test_greater_and_gequal_and_zmsk()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegTest1, GsRegs.Test(ztst: 1));
        f.Sprite(0, 0, 4, 4, 1, 0, 0, z: 100);
        f.Depth(1, 1).Should().Be(100);

        f.W(Ps2Gs.RegTest1, GsRegs.Test(ztst: 2)); // GEQUAL
        f.Sprite(0, 0, 4, 4, 2, 0, 0, z: 50);
        Red(f.Pixel(1, 1)).Should().Be(1, "50 < 100 fails");
        f.Sprite(0, 0, 4, 4, 3, 0, 0, z: 100);
        Red(f.Pixel(1, 1)).Should().Be(3, "100 >= 100 passes");

        f.W(Ps2Gs.RegTest1, GsRegs.Test(ztst: 3)); // GREATER
        f.Sprite(0, 0, 4, 4, 4, 0, 0, z: 100);
        Red(f.Pixel(1, 1)).Should().Be(3);
        f.W(Ps2Gs.RegZbuf1, GsRegs.Zbuf(GsFixture.ZPage, Ps2Gs.PsmZ32, zmsk: true));
        f.Sprite(0, 0, 4, 4, 5, 0, 0, z: 300);
        Red(f.Pixel(1, 1)).Should().Be(5);
        f.Depth(1, 1).Should().Be(100, "ZMSK keeps the Z buffer");

        f.W(Ps2Gs.RegTest1, GsRegs.Test(ztst: 0));
        f.Sprite(0, 0, 4, 4, 6, 0, 0, z: 1000);
        Red(f.Pixel(1, 1)).Should().Be(5, "NEVER");
    }

    [Fact]
    public void Depth_is_interpolated_across_triangles_and_clamped_to_the_format()
    {
        var f = new GsFixture(zPsm: Ps2Gs.PsmZ16);
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimTriangle));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(1, 1, 1));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(256 * 16, 0, 2560));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 256 * 16, 0));
        f.Depth(100, 10, Ps2Gs.PsmZ16).Should().Be(1000);

        f.Sprite(0, 0, 2, 2, 1, 1, 1, z: 0x123456);
        f.Depth(0, 0, Ps2Gs.PsmZ16).Should().Be(0xFFFF);
    }

    // ---- Alpha ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 1, 0, 1, 0, 0x40, 0x80)] // (Cs − Cd) × As >> 7 + Cd
    [InlineData(0, 2, 2, 1, 0x10, 0x80, 0x58)] // Cs × FIX >> 7 + Cd
    [InlineData(1, 2, 2, 0, 0x40, 0x80, 0xE0)] // Cd × FIX >> 7 + Cs
    [InlineData(0, 1, 1, 1, 0, 0x00, 0xC0)] // C = Ad: a frame alpha of 0x80 is 1.0, giving Cs
    public void Blend_equations(int a, int b, int c, int d, int fix, int sourceAlpha, int expected)
    {
        var f = new GsFixture();
        f.Sprite(0, 0, 2, 2, 0x40, 0, 0, a: 0x80); // destination red 0x40, alpha 0x80
        f.W(Ps2Gs.RegAlpha1, GsRegs.Alpha(a, b, c, d, fix));
        f.Sprite(0, 0, 2, 2, 0xC0, 0, 0, a: sourceAlpha, abe: true);
        Red(f.Pixel(0, 0)).Should().Be(expected);
    }

    [Fact]
    public void Blend_halfway_and_alpha_written_is_the_source_alpha()
    {
        var f = new GsFixture();
        f.Sprite(0, 0, 2, 2, 0x20, 0, 0, a: 0x11);
        f.W(Ps2Gs.RegAlpha1, GsRegs.Alpha(0, 1, 0, 1));
        f.Sprite(0, 0, 2, 2, 0xA0, 0, 0, a: 0x40, abe: true);
        f.Pixel(0, 0).Should().Be(Rgba(0x20 + (((0xA0 - 0x20) * 0x40) >> 7), 0, 0, 0x40));
    }

    [Fact]
    public void Colclamp_clamps_or_wraps_the_blend()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegAlpha1, GsRegs.Alpha(0, 2, 2, 1, fix: 0x80));
        f.Sprite(0, 0, 2, 2, 0xC0, 0, 0);
        f.W(Ps2Gs.RegColClamp, 1);
        f.Sprite(0, 0, 2, 2, 0x80, 0, 0, abe: true);
        Red(f.Pixel(0, 0)).Should().Be(0xFF);
        f.Sprite(2, 0, 4, 2, 0xC0, 0, 0);
        f.W(Ps2Gs.RegColClamp, 0);
        f.Sprite(2, 0, 4, 2, 0x80, 0, 0, abe: true);
        Red(f.Pixel(2, 0)).Should().Be(0x40, "0x140 & 0xFF");
    }

    [Fact]
    public void Pabe_skips_blending_when_the_source_alpha_msb_is_clear()
    {
        var f = new GsFixture();
        f.Sprite(0, 0, 4, 2, 0x40, 0, 0);
        f.W(Ps2Gs.RegAlpha1, GsRegs.Alpha(0, 2, 2, 1, fix: 0x80));
        f.W(Ps2Gs.RegPabe, 1);
        f.Sprite(0, 0, 2, 2, 0x10, 0, 0, a: 0x7F, abe: true);
        f.Sprite(2, 0, 4, 2, 0x10, 0, 0, a: 0x80, abe: true);
        Red(f.Pixel(0, 0)).Should().Be(0x10);
        Red(f.Pixel(2, 0)).Should().Be(0x50);
    }

    [Theory]
    [InlineData(0, 0x30, false)] // NEVER
    [InlineData(1, 0x30, true)] // ALWAYS
    [InlineData(2, 0x31, true)] // LESS
    [InlineData(2, 0x30, false)]
    [InlineData(3, 0x30, true)] // LEQUAL
    [InlineData(3, 0x2F, false)]
    [InlineData(4, 0x30, true)] // EQUAL
    [InlineData(4, 0x31, false)]
    [InlineData(5, 0x30, true)] // GEQUAL
    [InlineData(5, 0x31, false)]
    [InlineData(6, 0x2F, true)] // GREATER
    [InlineData(6, 0x30, false)]
    [InlineData(7, 0x31, true)] // NOTEQUAL
    [InlineData(7, 0x30, false)]
    public void Alpha_test_methods_on_alpha_0x30(int atst, int aref, bool drawn)
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegTest1, GsRegs.Test(ate: true, atst: atst, aref: aref));
        f.Sprite(0, 0, 2, 2, 0xFF, 0, 0, a: 0x30);
        (f.Pixel(0, 0) != 0).Should().Be(drawn);
    }

    [Theory]
    [InlineData(0, false, false)] // KEEP
    [InlineData(1, true, false)] // FB_ONLY
    [InlineData(2, false, true)] // ZB_ONLY
    public void Alpha_test_failure_processing(int afail, bool frameWritten, bool zWritten)
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegTest1, GsRegs.Test(ate: true, atst: 0, afail: afail, ztst: 1));
        f.Sprite(0, 0, 2, 2, 0xFF, 0, 0, z: 77);
        (f.Pixel(0, 0) != 0).Should().Be(frameWritten);
        (f.Depth(0, 0) == 77).Should().Be(zWritten);
    }

    [Fact]
    public void Alpha_test_rgb_only_keeps_the_frame_alpha()
    {
        var f = new GsFixture();
        f.Sprite(0, 0, 2, 2, 0, 0, 0, a: 0x12);
        f.W(Ps2Gs.RegTest1, GsRegs.Test(ate: true, atst: 0, afail: 3));
        f.Sprite(0, 0, 2, 2, 0x33, 0x44, 0x55, a: 0x66);
        f.Pixel(0, 0).Should().Be(Rgba(0x33, 0x44, 0x55, 0x12));
    }

    [Fact]
    public void Destination_alpha_test_reads_the_frame_alpha_msb()
    {
        var f = new GsFixture();
        f.Sprite(0, 0, 2, 2, 1, 0, 0, a: 0x80);
        f.Sprite(2, 0, 4, 2, 1, 0, 0, a: 0x7F);
        f.W(Ps2Gs.RegTest1, GsRegs.Test(date: true, datm: 1));
        f.Sprite(0, 0, 4, 2, 9, 0, 0);
        Red(f.Pixel(0, 0)).Should().Be(9);
        Red(f.Pixel(2, 0)).Should().Be(1);
    }

    [Fact]
    public void Frame_mask_and_fba()
    {
        var f = new GsFixture();
        f.Sprite(0, 0, 2, 2, 0x12, 0x34, 0x56, a: 0x00);
        f.W(Ps2Gs.RegFrame1, GsRegs.Frame(0, GsFixture.Fbw, Ps2Gs.PsmCt32, fbmsk: 0x0000_FF00));
        f.W(Ps2Gs.RegFba1, 1);
        f.Sprite(0, 0, 2, 2, 0xAA, 0xBB, 0xCC, a: 0x01);
        f.Pixel(0, 0).Should().Be(Rgba(0xAA, 0x34, 0xCC, 0x81));
    }

    [Fact]
    public void Psmct16_frame_converts_masks_and_dithers()
    {
        var f = new GsFixture(Ps2Gs.PsmCt16);
        f.Sprite(0, 0, 4, 4, 0xFF, 0x80, 0x08, a: 0x80);
        f.Pixel(0, 0).Should().Be(0x1F | (0x10 << 5) | (0x01 << 10) | 0x8000);

        // DIMX entry (0, 1) is +3 and (0, 0) is -4; DTHE only applies to 16-bit frames.
        f.W(Ps2Gs.RegDimx, 0x4 | (0x3 << 4));
        f.W(Ps2Gs.RegDthe, 1);
        f.Sprite(0, 0, 4, 4, 0x20, 0x20, 0x20, a: 0);
        f.Pixel(0, 0).Should().Be(0x03 | (0x03 << 5) | (0x03 << 10), "0x20 − 4 = 0x1C → 3");
        f.Pixel(1, 0).Should().Be(0x04 | (0x04 << 5) | (0x04 << 10), "0x20 + 3 = 0x23 → 4");
    }

    [Fact]
    public void Psmct24_frame_leaves_the_upper_byte()
    {
        var f = new GsFixture(Ps2Gs.PsmCt24);
        f.Gs.WritePixel(Ps2Gs.PsmCt32, 0, GsFixture.Fbw, 0, 0, 0x5A00_0000);
        f.Sprite(0, 0, 2, 2, 1, 2, 3, a: 0x80);
        f.Gs.ReadPixel(Ps2Gs.PsmCt32, 0, GsFixture.Fbw, 0, 0).Should().Be(0x5A03_0201);
    }

    [Fact]
    public void Fog_mixes_towards_fogcol()
    {
        var f = new GsFixture();
        f.W(Ps2Gs.RegFogCol, 0x00_00FF);
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite, fge: true));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0, 200, 0));
        f.W(Ps2Gs.RegXyzf2, GsRegs.Xyzf(0, 0, 0, 0x40));
        f.W(Ps2Gs.RegXyzf2, GsRegs.Xyzf(32, 32, 0, 0x40));
        var c = f.Pixel(0, 0);
        Red(c).Should().Be(((0x40 * 0) + ((255 - 0x40) * 255)) >> 8);
        ((int)((c >> 8) & 0xFF)).Should().Be((0x40 * 200) >> 8);
    }

    // ---- Textures ---------------------------------------------------------------------------------------------

    private static GsFixture WithTexture32(int size = 16)
    {
        var f = new GsFixture();
        var texels = new List<uint>();
        for (var v = 0; v < size; v++)
        {
            for (var u = 0; u < size; u++)
            {
                texels.Add(Rgba(u, v, 0x77, 0x80));
            }
        }

        f.Upload(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, 0, 0, size, size, texels);
        var log = (int)Math.Log2(size);
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, log, log, tfx: 1));
        return f;
    }

    private static void TexturedSprite(GsFixture f, int x0, int y0, int x1, int y1, int u0, int v0, int u1, int v1, int r = 0x80, int g = 0x80, int b = 0x80, int a = 0x80)
    {
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite, tme: true, fst: true));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(r, g, b, a));
        f.W(Ps2Gs.RegUv, GsRegs.Uv(u0, v0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(x0 * 16, y0 * 16));
        f.W(Ps2Gs.RegUv, GsRegs.Uv(u1, v1));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(x1 * 16, y1 * 16));
    }

    [Fact]
    public void Sprite_uv_maps_texels_one_to_one()
    {
        var f = WithTexture32();
        TexturedSprite(f, 10, 20, 26, 36, 0, 0, 16 * 16, 16 * 16);
        for (var j = 0; j < 16; j++)
        {
            for (var i = 0; i < 16; i++)
            {
                f.Pixel(10 + i, 20 + j).Should().Be(Rgba(i, j, 0x77), $"texel ({i}, {j})");
            }
        }
    }

    [Fact]
    public void Sprite_uv_scales_by_two()
    {
        var f = WithTexture32();
        TexturedSprite(f, 0, 0, 32, 32, 0, 0, 16 * 16, 16 * 16);
        f.Pixel(5, 9).Should().Be(Rgba(2, 4, 0x77));
        f.Pixel(31, 31).Should().Be(Rgba(15, 15, 0x77));
    }

    [Fact]
    public void Stq_triangles_divide_by_q()
    {
        var f = WithTexture32();
        f.W(Ps2Gs.RegPrim, GsRegs.Prim(Ps2Gs.PrimSprite, tme: true));
        // S, T premultiplied by Q = 2: (0.25, 0.5) → texel (4, 8).
        f.W(Ps2Gs.RegSt, GsRegs.St(0.5f, 1.0f));
        f.W(Ps2Gs.RegRgbaq, GsRegs.Rgbaq(0x80, 0x80, 0x80, 0x80, q: 2));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(0, 0));
        f.W(Ps2Gs.RegXyz2, GsRegs.Xyz(16, 16));
        f.Pixel(0, 0).Should().Be(Rgba(4, 8, 0x77));
    }

    [Fact]
    public void Modulate_scales_by_the_vertex_colour_and_tcc_controls_alpha()
    {
        var f = WithTexture32();
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, 4, 4, tcc: false, tfx: 0));
        TexturedSprite(f, 0, 0, 16, 16, 0, 0, 256, 256, r: 0x40, g: 0x80, b: 0xFF, a: 0x33);
        f.Pixel(10, 6).Should().Be(Rgba((10 * 0x40) >> 7, 6, Math.Min((0x77 * 0xFF) >> 7, 255), 0x33));
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, 4, 4, tcc: true, tfx: 0));
        TexturedSprite(f, 0, 0, 16, 16, 0, 0, 256, 256, r: 0x80, g: 0x80, b: 0x80, a: 0x40);
        ((int)(f.Pixel(10, 6) >> 24)).Should().Be((0x80 * 0x40) >> 7);
    }

    [Fact]
    public void Highlight_adds_the_vertex_alpha()
    {
        var f = WithTexture32();
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, 4, 4, tcc: true, tfx: 2));
        TexturedSprite(f, 0, 0, 16, 16, 0, 0, 256, 256, r: 0x80, g: 0x80, b: 0x80, a: 0x10);
        f.Pixel(3, 4).Should().Be(Rgba(3 + 0x10, 4 + 0x10, 0x77 + 0x10, 0x90));
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, 4, 4, tcc: true, tfx: 3));
        TexturedSprite(f, 0, 0, 16, 16, 0, 0, 256, 256, r: 0x80, g: 0x80, b: 0x80, a: 0x10);
        f.Pixel(3, 4).Should().Be(Rgba(3 + 0x10, 4 + 0x10, 0x77 + 0x10, 0x80));
    }

    [Theory]
    [InlineData(0, 20, 4)] // REPEAT: 20 & 15
    [InlineData(1, 20, 15)] // CLAMP
    [InlineData(2, 20, 9)] // REGION_CLAMP to [3, 9]
    [InlineData(3, 21, 5)] // REGION_REPEAT: (21 & MINU 3) | MAXU 4
    public void Wrap_modes(int wms, int u, int expectedU)
    {
        var f = WithTexture32();
        f.W(Ps2Gs.RegClamp1, wms == 3 ? GsRegs.Clamp(wms, 0, 3, 4) : GsRegs.Clamp(wms, 0, 3, 9));
        TexturedSprite(f, 0, 0, 1, 1, u * 16, 0, (u + 1) * 16, 16);
        Red(f.Pixel(0, 0)).Should().Be(expectedU);
    }

    [Fact]
    public void Bilinear_blends_neighbouring_texels()
    {
        var f = new GsFixture();
        f.Upload(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, 0, 0, 2, 1, [Rgba(0, 0, 0), Rgba(0xF0, 0, 0)]);
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt32, 1, 0, tfx: 1));
        f.W(Ps2Gs.RegClamp1, GsRegs.Clamp(1, 1));
        f.W(Ps2Gs.RegTex1_1, GsRegs.Tex1(true, true));
        // Sample at u = 1.0: halfway between texel centres 0.5 and 1.5.
        TexturedSprite(f, 0, 0, 1, 1, 16, 8, 32, 24);
        Red(f.Pixel(0, 0)).Should().Be(0x78);
        f.W(Ps2Gs.RegTex1_1, GsRegs.Tex1(false, false));
        TexturedSprite(f, 0, 0, 1, 1, 16, 8, 32, 24);
        Red(f.Pixel(0, 0)).Should().Be(0xF0, "nearest takes texel 1");
    }

    [Fact]
    public void Texa_expands_16_and_24_bit_textures()
    {
        var f = new GsFixture();
        f.Upload(GsFixture.TexBlock, 1, Ps2Gs.PsmCt16, 0, 0, 4, 1, [0x801F, 0x001F, 0x0000, 0x8000]);
        f.W(Ps2Gs.RegTexA, 0x11UL | (1UL << 15) | (0x99UL << 32));
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt16, 2, 0, tfx: 1));
        TexturedSprite(f, 0, 0, 4, 1, 0, 0, 64, 16);
        f.Pixel(0, 0).Should().Be(Rgba(0xF8, 0, 0, 0x99));
        f.Pixel(1, 0).Should().Be(Rgba(0xF8, 0, 0, 0x11));
        f.Pixel(2, 0).Should().Be(Rgba(0, 0, 0, 0), "AEM: black with the bit clear is transparent");
        f.Pixel(3, 0).Should().Be(Rgba(0, 0, 0, 0x99));

        f.Upload(GsFixture.TexBlock, 1, Ps2Gs.PsmCt24, 0, 0, 2, 1, [0x123456, 0]);
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmCt24, 1, 0, tfx: 1));
        TexturedSprite(f, 0, 0, 2, 1, 0, 0, 32, 16);
        f.Pixel(0, 0).Should().Be(0x1112_3456);
        f.Pixel(1, 0).Should().Be(0);
    }

    // ---- CLUTs ------------------------------------------------------------------------------------------------

    /// <summary>The position of 8-bit CLUT entry <paramref name="index"/> in a CSM1 16×16 block (bits 3 and 4 swapped).</summary>
    private static (int X, int Y) Csm1Position(int index)
    {
        var p = (index & ~0x18) | ((index & 0x08) << 1) | ((index & 0x10) >> 1);
        return (p & 15, p >> 4);
    }

    [Fact]
    public void Psmt8_with_a_csm1_psmct32_clut()
    {
        var f = new GsFixture();
        var clut = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            var (x, y) = Csm1Position(i);
            clut[(y * 16) + x] = Rgba(i, 255 - i, 0x10, 0x80);
        }

        f.Upload(GsFixture.ClutBlock, 1, Ps2Gs.PsmCt32, 0, 0, 16, 16, clut);
        f.Upload(GsFixture.TexBlock, 2, Ps2Gs.PsmT8, 0, 0, 16, 2, Enumerable.Range(0, 32).Select(i => (uint)(i * 7)).ToList());
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 2, Ps2Gs.PsmT8, 4, 1, tfx: 1, cbp: GsFixture.ClutBlock, cpsm: Ps2Gs.PsmCt32, cld: 1));
        TexturedSprite(f, 0, 0, 16, 2, 0, 0, 256, 32);
        for (var i = 0; i < 32; i++)
        {
            var index = i * 7;
            f.Pixel(i % 16, i / 16).Should().Be(Rgba(index, 255 - index, 0x10), $"index {index}");
        }
    }

    [Fact]
    public void Psmt4_with_psmct32_and_psmct16_cluts_and_csa()
    {
        var f = new GsFixture();
        // Two 16-colour CSM1 PSMCT32 palettes: 8×2 each, the second at CSA 1 (the next 8×2 in the block's layout).
        var pal = new List<uint>();
        for (var i = 0; i < 16; i++)
        {
            pal.Add(Rgba(i * 16, 1, 2));
        }

        f.Upload(GsFixture.ClutBlock, 1, Ps2Gs.PsmCt32, 0, 0, 8, 2, pal);
        f.Upload(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 0, 0, 16, 1, Enumerable.Range(0, 16).Select(i => (uint)(15 - i)).ToList());
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 4, 0, tfx: 1, cbp: GsFixture.ClutBlock, cpsm: Ps2Gs.PsmCt32, csa: 1, cld: 1));
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 4, 0, tfx: 1, cbp: GsFixture.ClutBlock, cpsm: Ps2Gs.PsmCt32, csa: 1, cld: 0));
        TexturedSprite(f, 0, 0, 16, 1, 0, 0, 256, 16);
        f.Pixel(0, 0).Should().Be(Rgba(15 * 16, 1, 2), "CSA 1 holds the palette loaded with CSA 1");
        f.Pixel(15, 0).Should().Be(Rgba(0, 1, 2));
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 4, 0, tfx: 1, cbp: GsFixture.ClutBlock, cpsm: Ps2Gs.PsmCt32, csa: 0, cld: 0));
        TexturedSprite(f, 0, 0, 16, 1, 0, 0, 256, 16);
        f.Pixel(0, 0).Should().Be(0, "CSA 0 was never loaded");

        // A PSMCT16 palette with TEXA's alpha.
        f.W(Ps2Gs.RegTexA, 0x22UL | (0x44UL << 32));
        f.Upload(GsFixture.ClutBlock + 4, 1, Ps2Gs.PsmCt16, 0, 0, 8, 2, Enumerable.Range(0, 16).Select(i => (uint)(i | (i == 15 ? 0x8000 : 0))).ToList());
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 4, 0, tfx: 1, cbp: GsFixture.ClutBlock + 4, cpsm: Ps2Gs.PsmCt16, csa: 3, cld: 1));
        TexturedSprite(f, 0, 0, 16, 1, 0, 0, 256, 16);
        f.Pixel(0, 0).Should().Be(Rgba(15 << 3, 0, 0, 0x44));
        f.Pixel(5, 0).Should().Be(Rgba(10 << 3, 0, 0, 0x22));
    }

    [Fact]
    public void Clut_load_conditions_follow_cld_and_cbp0()
    {
        var f = new GsFixture();
        f.Upload(GsFixture.ClutBlock, 1, Ps2Gs.PsmCt32, 0, 0, 8, 2, Enumerable.Repeat(Rgba(1, 0, 0), 16).ToList());
        f.Upload(GsFixture.ClutBlock + 1, 1, Ps2Gs.PsmCt32, 0, 0, 8, 2, Enumerable.Repeat(Rgba(2, 0, 0), 16).ToList());
        f.Upload(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 0, 0, 1, 1, [0]);

        void Tex(int cbp, int cld) =>
            f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 0, 0, tfx: 1, cbp: cbp, cpsm: Ps2Gs.PsmCt32, cld: cld));

        int Sample()
        {
            TexturedSprite(f, 0, 0, 1, 1, 0, 0, 16, 16);
            return Red(f.Pixel(0, 0));
        }

        Tex(GsFixture.ClutBlock, 2); // load, CBP0 = ClutBlock
        Sample().Should().Be(1);
        f.Upload(GsFixture.ClutBlock, 1, Ps2Gs.PsmCt32, 0, 0, 8, 2, Enumerable.Repeat(Rgba(3, 0, 0), 16).ToList());
        Tex(GsFixture.ClutBlock, 4); // CBP0 matches: no load
        Sample().Should().Be(1);
        Tex(GsFixture.ClutBlock + 1, 4); // differs: load
        Sample().Should().Be(2);
        Tex(GsFixture.ClutBlock, 0);
        Sample().Should().Be(2, "CLD 0 keeps the buffer");
        Tex(GsFixture.ClutBlock, 1);
        Sample().Should().Be(3);
    }

    [Fact]
    public void Csm2_reads_a_row_at_texclut_offset()
    {
        var f = new GsFixture();
        f.Upload(GsFixture.ClutBlock, 2, Ps2Gs.PsmCt16, 32, 5, 16, 1, Enumerable.Range(0, 16).Select(i => (uint)(i | 0x8000)).ToList());
        f.W(Ps2Gs.RegTexClut, 2 | (2 << 6) | (5 << 12)); // CBW 2, COU 2 (×16 = 32), COV 5
        f.W(Ps2Gs.RegTexA, 0x80UL << 32);
        f.Upload(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 0, 0, 2, 1, [7, 12]);
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 2, Ps2Gs.PsmT4, 1, 0, tfx: 1, cbp: GsFixture.ClutBlock, cpsm: Ps2Gs.PsmCt16, csm2: true, cld: 1));
        TexturedSprite(f, 0, 0, 2, 1, 0, 0, 32, 16);
        f.Pixel(0, 0).Should().Be(Rgba(7 << 3, 0, 0, 0x80));
        f.Pixel(1, 0).Should().Be(Rgba(12 << 3, 0, 0, 0x80));
    }

    [Fact]
    public void Psmt8h_texture_reads_the_upper_byte_through_a_clut()
    {
        var f = new GsFixture();
        var clut = new uint[256];
        var (cx, cy) = Csm1Position(0x5A);
        clut[(cy * 16) + cx] = Rgba(9, 8, 7);
        f.Upload(GsFixture.ClutBlock, 1, Ps2Gs.PsmCt32, 0, 0, 16, 16, clut);
        f.Gs.WritePixel(Ps2Gs.PsmCt32, GsFixture.TexBlock, 1, 0, 0, 0x5A00_1234);
        f.W(Ps2Gs.RegTex0_1, GsRegs.Tex0(GsFixture.TexBlock, 1, Ps2Gs.PsmT8H, 0, 0, tfx: 1, cbp: GsFixture.ClutBlock, cpsm: Ps2Gs.PsmCt32, cld: 1));
        TexturedSprite(f, 0, 0, 1, 1, 0, 0, 16, 16);
        f.Pixel(0, 0).Should().Be(Rgba(9, 8, 7));
    }
}
