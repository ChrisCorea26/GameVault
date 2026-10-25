using GameVault.Core.Cartridge;
using GameVault.Core.Cartridge.Mappers;
using GameVault.Core.Ppu;
using GameVault.Core.Tests.Harness;
using GameVault.Core.Tests.State;

namespace GameVault.Core.Tests.Ppu;

/// <summary>Register-level and rendering tests for the 2C02 (NESdev Wiki: "PPU registers", "PPU scrolling").</summary>
public class PpuTests
{
    private readonly Nrom _mapper;
    private readonly Ppu2C02 _ppu;

    public PpuTests()
    {
        _mapper = new Nrom(INesRom.Parse(TestRoms.Nrom([0xEA], chrBanks: 0, flags6: 0x01))); // CHR-RAM, vertical
        _ppu = new Ppu2C02(_mapper);
        _ppu.Power();
    }

    private void Write(ushort register, byte value) => _ppu.WriteRegister(register, value);

    private byte Read(ushort register) => _ppu.ReadRegister(register);

    private void SetAddress(ushort address)
    {
        Write(0x2006, (byte)(address >> 8));
        Write(0x2006, (byte)address);
    }

    private void RunUntil(int scanline, int dot)
    {
        while (_ppu.Scanline != scanline || _ppu.Dot != dot)
        {
            _ppu.Tick();
        }
    }

    private void RunFrame()
    {
        var frame = _ppu.FrameCount;
        while (_ppu.FrameCount == frame)
        {
            _ppu.Tick();
        }
    }

    [Fact]
    public void Scroll_and_address_writes_follow_the_loopy_model()
    {
        // The worked example from NESdev Wiki "PPU scrolling".
        Write(0x2000, 0x00);
        Read(0x2002);
        Write(0x2005, 0x7D);
        _ppu.T.Should().Be(0x000F);
        _ppu.FineX.Should().Be(5);
        _ppu.WriteToggle.Should().BeTrue();

        Write(0x2005, 0x5E);
        _ppu.T.Should().Be(0x616F);
        _ppu.WriteToggle.Should().BeFalse();

        Write(0x2006, 0x3D);
        _ppu.T.Should().Be(0x3D6F);
        Write(0x2006, 0xF0);
        _ppu.T.Should().Be(0x3DF0);
        _ppu.V.Should().Be(0x3DF0);
    }

    [Fact]
    public void Ctrl_write_sets_the_nametable_bits_of_t()
    {
        Write(0x2000, 0x03);

        _ppu.T.Should().Be(0x0C00);
    }

    [Fact]
    public void Status_read_clears_the_write_toggle()
    {
        Write(0x2005, 0x10);
        Read(0x2002);

        _ppu.WriteToggle.Should().BeFalse();
    }

    [Fact]
    public void Data_reads_below_the_palette_are_buffered()
    {
        SetAddress(0x2400);
        Write(0x2007, 0x11);
        Write(0x2007, 0x22);

        SetAddress(0x2400);
        Read(0x2007); // primes the buffer
        Read(0x2007).Should().Be(0x11);
        Read(0x2007).Should().Be(0x22);
    }

    [Fact]
    public void Palette_reads_are_immediate_and_mirror_the_backdrop_entries()
    {
        SetAddress(0x3F10);
        Write(0x2007, 0x2A);

        SetAddress(0x3F00);
        (Read(0x2007) & 0x3F).Should().Be(0x2A);
        _ppu.PeekPalette(0x10).Should().Be(0x2A);
    }

    [Fact]
    public void Increment_32_mode_steps_down_a_column()
    {
        Write(0x2000, 0x04);
        SetAddress(0x2000);
        Write(0x2007, 0x01);

        _ppu.V.Should().Be(0x2020);
    }

    [Fact]
    public void Vertical_mirroring_maps_2800_onto_2000()
    {
        SetAddress(0x2005);
        Write(0x2007, 0x77);

        SetAddress(0x2805);
        Read(0x2007);
        Read(0x2007).Should().Be(0x77);
    }

    [Fact]
    public void Oam_attribute_bytes_read_back_without_bits_2_to_4()
    {
        Write(0x2003, 0x02);
        Write(0x2004, 0xFF);
        Write(0x2003, 0x02);

        Read(0x2004).Should().Be(0xE3);
    }

    [Fact]
    public void Vblank_starts_at_line_241_dot_1_and_raises_nmi_when_enabled()
    {
        Write(0x2000, 0x80);
        RunUntil(241, 1);
        _ppu.NmiOutput.Should().BeFalse();

        _ppu.Tick();
        _ppu.NmiOutput.Should().BeTrue();

        (Read(0x2002) & 0x80).Should().Be(0x80);
        _ppu.NmiOutput.Should().BeFalse("reading $2002 clears the flag");
    }

    [Fact]
    public void Reading_status_one_dot_before_vblank_suppresses_it_for_the_frame()
    {
        RunUntil(241, 1);

        (Read(0x2002) & 0x80).Should().Be(0);
        _ppu.Tick();
        _ppu.Tick();

        (Read(0x2002) & 0x80).Should().Be(0);
    }

    [Fact]
    public void Vblank_clears_on_the_pre_render_line()
    {
        RunUntil(250, 0);
        RunUntil(261, 2);

        (Read(0x2002) & 0x80).Should().Be(0);
    }

    [Fact]
    public void Odd_frames_are_one_dot_shorter_with_rendering_on()
    {
        Write(0x2001, 0x08);
        RunFrame();

        var dots = new List<int>();
        for (var i = 0; i < 2; i++)
        {
            var count = 0;
            var frame = _ppu.FrameCount;
            while (_ppu.FrameCount == frame)
            {
                _ppu.Tick();
                count++;
            }

            dots.Add(count);
        }

        dots.Should().BeEquivalentTo([89342, 89341]);
    }

    [Fact]
    public void Renders_background_tiles_with_attribute_palettes_and_fine_scroll()
    {
        // Tile 1: every pixel uses color 3. Nametable (0,0) = tile 1, palette 0 color 3 = $16.
        for (var row = 0; row < 8; row++)
        {
            _mapper.PpuWrite((ushort)(0x10 + row), 0xFF);
            _mapper.PpuWrite((ushort)(0x18 + row), 0xFF);
        }

        SetAddress(0x2000);
        Write(0x2007, 0x01);
        SetAddress(0x3F00);
        Write(0x2007, 0x0F);
        Write(0x2007, 0x01);
        Write(0x2007, 0x02);
        Write(0x2007, 0x16);

        Write(0x2000, 0x00);
        Write(0x2005, 0x00);
        Write(0x2005, 0x00);
        Write(0x2001, 0x0A); // background on, including the left 8 pixels
        RunFrame();
        RunFrame();

        var frame = _ppu.FrameBuffer;
        frame[0].Should().Be(0x16);
        frame[7].Should().Be(0x16);
        frame[8].Should().Be(0x0F);
        frame[7 * 256].Should().Be(0x16);
        frame[8 * 256].Should().Be(0x0F);

        // Fine X = 3 shifts the tile left by 3 pixels.
        Write(0x2005, 0x03);
        Write(0x2005, 0x00);
        RunFrame();
        frame = _ppu.FrameBuffer;
        frame[4].Should().Be(0x16);
        frame[5].Should().Be(0x0F);
    }

    [Fact]
    public void Left_column_mask_hides_the_first_8_background_pixels()
    {
        for (var row = 0; row < 8; row++)
        {
            _mapper.PpuWrite((ushort)(0x10 + row), 0xFF);
        }

        SetAddress(0x2000);
        Write(0x2007, 0x01);
        SetAddress(0x3F00);
        Write(0x2007, 0x0F);
        Write(0x2007, 0x30);
        Write(0x2001, 0x08); // background on, left 8 pixels hidden
        Write(0x2005, 0x00);
        Write(0x2005, 0x00);
        RunFrame();
        RunFrame();

        _ppu.FrameBuffer[0].Should().Be(0x0F);
    }

    [Fact]
    public void Grayscale_and_emphasis_are_applied_to_output()
    {
        SetAddress(0x3F00);
        Write(0x2007, 0x16);
        SetAddress(0x2000); // with rendering off, v pointing into the palette would show that entry instead
        Write(0x2001, 0x21); // grayscale + red emphasis, rendering off

        RunFrame();
        RunFrame();

        _ppu.FrameBuffer[1000].Should().Be(0x10 | (1 << 6));
    }

    [Fact]
    public void Sprite_zero_hit_and_overflow_are_set_during_rendering()
    {
        // Tile 1 is solid color 1 for both background and sprites.
        for (var row = 0; row < 8; row++)
        {
            _mapper.PpuWrite((ushort)(0x10 + row), 0xFF);
        }

        // Fill the first two rows of the nametable with tile 1.
        SetAddress(0x2000);
        for (var i = 0; i < 64; i++)
        {
            Write(0x2007, 0x01);
        }

        // Nine sprites on line 5: sprite 0 overlaps the background, and the ninth causes overflow.
        Write(0x2003, 0x00);
        for (var i = 0; i < 64; i++)
        {
            var y = i < 9 ? (byte)4 : (byte)0xF0;
            Write(0x2004, y);
            Write(0x2004, 0x01);
            Write(0x2004, 0x00);
            Write(0x2004, (byte)(16 + i * 8));
        }

        Write(0x2005, 0x00);
        Write(0x2005, 0x00);
        Write(0x2001, 0x1E);
        RunFrame();
        RunUntil(240, 0);

        var status = Read(0x2002);
        (status & 0x40).Should().Be(0x40, "sprite 0 overlaps an opaque background pixel");
        (status & 0x20).Should().Be(0x20, "nine sprites share a line");
    }

    [Fact]
    public void With_rendering_off_the_entry_v_points_at_is_shown()
    {
        SetAddress(0x3F00);
        Write(0x2007, 0x0F);
        Write(0x2007, 0x21); // v is now $3F02
        SetAddress(0x3F01);

        RunFrame();
        RunFrame();

        _ppu.FrameBuffer[1000].Should().Be(0x21);
    }

    [Fact]
    public void State_round_trips_mid_frame()
    {
        StateAssert.RoundTrips(() =>
        {
            var ppu = new Ppu2C02(new Nrom(INesRom.Parse(TestRoms.Nrom([0xEA], chrBanks: 0))));
            ppu.Power();
            return ppu;
        }, ppu =>
        {
            ppu.WriteRegister(0x2000, 0x90);
            ppu.WriteRegister(0x2001, 0x1E);
            ppu.WriteRegister(0x2006, 0x3F);
            ppu.WriteRegister(0x2006, 0x01);
            ppu.WriteRegister(0x2007, 0x21);
            ppu.WriteRegister(0x2004, 0x55);
            for (var i = 0; i < 50_000; i++)
            {
                ppu.Tick();
            }
        });
    }
}
