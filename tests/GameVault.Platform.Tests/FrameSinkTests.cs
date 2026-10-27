using GameVault.Platform.Video;

namespace GameVault.Platform.Tests;

public class FrameSinkTests
{
    [Fact]
    public void A_pal_ps2_frame_of_640_by_512_fits()
    {
        IVideoSink.MaxWidth.Should().Be(640);
        IVideoSink.MaxHeight.Should().Be(512);
        var sink = new FrameSink();
        var pixels = new ushort[640 * 512];
        pixels[^1] = 0x7FFF;
        pixels[0] = 1;

        sink.Present(pixels, [], 640, 512, pixelAspect: 1.0);

        sink.TryGetVideoFrame(out var frame).Should().BeTrue();
        frame.Width.Should().Be(640);
        frame.Height.Should().Be(512);
        frame.Span.Length.Should().Be(640 * 512);
        frame.Span[^1].Should().Be(0x7FFF);
        frame.Span[0].Should().Be(1);
    }

    [Fact]
    public void A_true_colour_frame_reaches_the_renderer_as_argb_and_the_next_indexed_one_does_not()
    {
        var sink = new FrameSink();
        sink.PresentArgb([0xFF123456, 0xFF654321], 2, 1);

        sink.TryGetVideoFrame(out var frame).Should().BeTrue();
        frame.IsArgb.Should().BeTrue();
        var argb = new uint[2];
        frame.ToArgb([], argb);
        argb.Should().Equal(0xFF123456, 0xFF654321);

        sink.Present([1, 0], [0xFF000000, 0xFFFFFFFF], 2, 1);
        sink.TryGetVideoFrame(out frame).Should().BeTrue();
        frame.IsArgb.Should().BeFalse();
        frame.ToArgb(sink.Palette, argb);
        argb.Should().Equal(0xFFFFFFFF, 0xFF000000);
    }
}
