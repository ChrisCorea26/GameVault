using FluentAssertions;
using GameVault.Platform.Video;
using GameVault.ViewModels.Services;

namespace GameVault.Platform.Tests;

/// <summary>Frames of other sizes than the NES's 256×240 (Phase 12: the Apple II's 560×384).</summary>
public sealed class VariableFrameTests
{
    [Fact]
    public void The_frame_sink_keeps_each_frames_size()
    {
        var sink = new FrameSink();
        var pixels = new ushort[560 * 384];
        pixels[^1] = 7;
        sink.Present(pixels, [0xFF000000u, 0xFFFFFFFFu], 560, 384, 0.914);

        sink.TryGetVideoFrame(out var frame).Should().BeTrue();
        frame.Width.Should().Be(560);
        frame.Height.Should().Be(384);
        frame.PixelAspect.Should().Be(0.914);
        frame.IsStandard.Should().BeFalse();
        frame.Span.Length.Should().Be(560 * 384);
        frame.Span[^1].Should().Be(7);
    }

    [Fact]
    public void Other_frames_ignore_overscan_and_use_their_own_pixel_shape()
    {
        // 560×384 at 0.914 is 512×384 on screen: 4:3.
        var rect = DisplayLayout.Compute(1024, 768, DisplayPreset.Smooth, cropOverscan: true, tvPixelShape: true, 560, 384, 512.0 / 560);
        rect.SourceTop.Should().Be(0);
        rect.SourceHeight.Should().Be(384);
        rect.Width.Should().Be(1024);
        rect.Height.Should().Be(768);

        var sharp = DisplayLayout.Compute(1920, 1080, DisplayPreset.SharpPixels, cropOverscan: false, tvPixelShape: false, 560, 384, 512.0 / 560);
        sharp.Height.Should().Be(768, "whole-number vertical scale (2)");
        sharp.Width.Should().Be(1024);
    }

    [Fact]
    public void Nes_frames_are_unchanged()
    {
        DisplayLayout.Compute(1024, 960, DisplayPreset.SharpPixels, true, false)
            .Should().Be(DisplayLayout.Compute(1024, 960, DisplayPreset.SharpPixels, true, false, 256, 240, 1.0));
    }

    [Fact]
    public void Thumbnails_scale_any_frame_to_128_by_120()
    {
        var frame = new ushort[560 * 384];
        var png = PngEncoder.Thumbnail(frame, [0xFF102030u], 560, 384);
        png.AsSpan(16, 8).ToArray().Should().Equal(0, 0, 0, 128, 0, 0, 0, 120);
    }
}
