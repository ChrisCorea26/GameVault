using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.Snes;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.Nintendo64;

/// <summary>
/// PeterLemon's RDP programs (public domain; downloaded at test time, see tests/TestRoms/SOURCES.md), run through the
/// whole console: the CPU and RSP send the commands, the RDP draws, and the VI shows the framebuffer. Each program's
/// framebuffer is compared with the reference picture in the repository (an emulator's capture; a few are captured a
/// row or two off, so the bar is the share of pixels that match exactly), then pinned as a golden image. Programs
/// whose references can't be matched (animated, or drawn with older data than the ROM holds) are only pinned.
/// </summary>
public sealed class PeterLemonN64RdpTests
{
    [N64TestRomTheory]
    [InlineData("RDPTest/CPU/RDPTestCPU.N64", 4)]
    [InlineData("RDPTest/RSP/RDPTestRSP.N64", 4)]
    public void Rdp_register_programs_pass(string program, int checks)
    {
        var console = PeterLemonN64Tests.Boot(program);
        for (var frame = 0; frame < 60; frame++)
        {
            console.RunFrame();
        }

        var (passes, failures) = PeterLemonN64Tests.CountResults(console.Rdram, 320);
        failures.Should().Be(0);
        passes.Should().Be(checks);
    }

    [N64TestRomTheory]
    [InlineData("RDP/16BPP/Line/FillLine/Cycle1FillLine320x240/Cycle1FillLine16BPP320X240.N64", "RDP/16BPP/Line/FillLine/Cycle1FillLine320x240/Cycle1FillLine16BPP320X240.png", 98)]
    [InlineData("RDP/16BPP/Line/FillLine/FillLine320x240/FillLine16BPP320X240.N64", "RDP/16BPP/Line/FillLine/FillLine320x240/FillLine16BPP320X240.png", 98)]
    [InlineData("RDP/16BPP/Line/Plot/FillLine/PlotFillLine16BPP320X240.N64", "RDP/16BPP/Line/Plot/FillLine/PlotFillLine16BPP320X240.png", 98)]
    [InlineData("RDP/16BPP/Line/Rotate/FillLine/RotateFillLine16BPP320X240.N64", null, 0)]
    [InlineData("RDP/16BPP/Rectangle/FillRectangle/Cycle1FillRectangle320x240/Cycle1FillRectangle16BPP320X240.N64", "RDP/16BPP/Rectangle/FillRectangle/Cycle1FillRectangle320x240/Cycle1FillRectangle16BPP320X240.png", 97)]
    [InlineData("RDP/16BPP/Rectangle/FillRectangle/FillRectangle320x240/FillRectangle16BPP320X240.N64", "RDP/16BPP/Rectangle/FillRectangle/FillRectangle320x240/FillRectangle16BPP320X240.png", 97)]
    [InlineData("RDP/16BPP/Rectangle/FillZBufferRectangle320x240/Cycle1FillZBufferRectangle16BPP320X240.N64", "RDP/16BPP/Rectangle/FillZBufferRectangle320x240/Cycle1FillZBufferRectangle16BPP320X240.png", 97)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/CopyTextureRectangleRGBA16B320x240/CopyTextureRectangle16BPPRGBA16B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/CopyTextureRectangleRGBA16B320x240/CopyTextureRectangle16BPPRGBA16B320X240.png", 98)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI4B320x240/Cycle1TextureRectangle16BPPI4B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI4B320x240/Cycle1TextureRectangle16BPPI4B320X240.png", 92)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI8B320x240/Cycle1TextureRectangle16BPPI8B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI8B320x240/Cycle1TextureRectangle16BPPI8B320X240.png", 94)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA16B320x240/Cycle1TextureRectangle16BPPIA16B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA16B320x240/Cycle1TextureRectangle16BPPIA16B320X240.png", 94)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA4B320x240/Cycle1TextureRectangle16BPPIA4B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA4B320x240/Cycle1TextureRectangle16BPPIA4B320X240.png", 93)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA8B320x240/Cycle1TextureRectangle16BPPIA8B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA8B320x240/Cycle1TextureRectangle16BPPIA8B320X240.png", 94)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA16B320x240/Cycle1TextureRectangle16BPPRGBA16B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA16B320x240/Cycle1TextureRectangle16BPPRGBA16B320X240.png", 94)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA32B320x240/Cycle1TextureRectangle16BPPRGBA32B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA32B320x240/Cycle1TextureRectangle16BPPRGBA32B320X240.png", 95)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleYUV16B320x240/Cycle1TextureRectangle16BPPYUV16B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleYUV16B320x240/Cycle1TextureRectangle16BPPYUV16B320X240.png", 98)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/TLUT/CopyTextureRectangleTLUTRGBA4B320x240/CopyTextureRectangle16BPPTLUTRGBA4B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/TLUT/CopyTextureRectangleTLUTRGBA4B320x240/CopyTextureRectangle16BPPTLUTRGBA4B320X240.png", 98)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/TLUT/CopyTextureRectangleTLUTRGBA8B320x240/CopyTextureRectangle16BPPTLUTRGBA8B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/TLUT/CopyTextureRectangleTLUTRGBA8B320x240/CopyTextureRectangle16BPPTLUTRGBA8B320X240.png", 98)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA4B320x240/Cycle1TextureRectangle16BPPTLUTIA4B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA4B320x240/Cycle1TextureRectangle16BPPTLUTIA4B320X240.png", 88)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA8B320x240/Cycle1TextureRectangle16BPPTLUTIA8B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA8B320x240/Cycle1TextureRectangle16BPPTLUTIA8B320X240.png", 94)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA4B320x240/Cycle1TextureRectangle16BPPTLUTRGBA4B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA4B320x240/Cycle1TextureRectangle16BPPTLUTRGBA4B320X240.png", 88)]
    [InlineData("RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA8B320x240/Cycle1TextureRectangle16BPPTLUTRGBA8B320X240.N64", "RDP/16BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA8B320x240/Cycle1TextureRectangle16BPPTLUTRGBA8B320X240.png", 94)]
    [InlineData("RDP/16BPP/SetPrimColor/SetPrimColor16BPP.N64", "RDP/16BPP/SetPrimColor/SetPrimColor16BPP.png", 99)]
    [InlineData("RDP/16BPP/Triangle/Cube/FillTriangle320x240/CubeFillTriangle16BPP320X240.N64", null, 0)]
    [InlineData("RDP/16BPP/Triangle/FillTriangle/Cycle1FillTriangle320x240/Cycle1FillTriangle16BPP320X240.N64", "RDP/16BPP/Triangle/FillTriangle/Cycle1FillTriangle320x240/Cycle1FillTriangle16BPP320X240.png", 97)]
    [InlineData("RDP/16BPP/Triangle/FillTriangle/FillTriangle320x240/FillTriangle16BPP320X240.N64", "RDP/16BPP/Triangle/FillTriangle/FillTriangle320x240/FillTriangle16BPP320X240.png", 97)]
    [InlineData("RDP/16BPP/Triangle/FillZBufferTriangle320x240/Cycle1FillZBufferTriangle16BPP320X240.N64", "RDP/16BPP/Triangle/FillZBufferTriangle320x240/Cycle1FillZBufferTriangle16BPP320X240.png", 97)]
    [InlineData("RDP/16BPP/Triangle/Plot/FillTriangle/LeftMajorTriangle320x240/LeftMajorTriangle16BPP320X240.N64", "RDP/16BPP/Triangle/Plot/FillTriangle/LeftMajorTriangle320x240/LeftMajorTriangle16BPP320X240.png", 98)]
    [InlineData("RDP/16BPP/Triangle/Plot/FillTriangle/RightMajorTriangle320x240/RightMajorTriangle16BPP320X240.N64", "RDP/16BPP/Triangle/Plot/FillTriangle/RightMajorTriangle320x240/RightMajorTriangle16BPP320X240.png", 98)]
    [InlineData("RDP/16BPP/Triangle/Rotate/FillTriangle/LeftMajorTriangle320x240/RotateLeftMajorTriangle16BPP320X240.N64", null, 0)]
    [InlineData("RDP/16BPP/Triangle/ShadeTriangle320x240/Cycle1ShadeTriangle16BPP320X240.N64", "RDP/16BPP/Triangle/ShadeTriangle320x240/Cycle1ShadeTriangle16BPP320X240.png", 92)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/CopyTextureTriangleRGBA16B320x240/CopyTextureTriangle16BPPRGBA16B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/CopyTextureTriangleRGBA16B320x240/CopyTextureTriangle16BPPRGBA16B320X240.png", 98)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI4B320x240/Cycle1TextureTriangle16BPPI4B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI4B320x240/Cycle1TextureTriangle16BPPI4B320X240.png", 92)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI8B320x240/Cycle1TextureTriangle16BPPI8B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI8B320x240/Cycle1TextureTriangle16BPPI8B320X240.png", 94)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA16B320x240/Cycle1TextureTriangle16BPPIA16B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA16B320x240/Cycle1TextureTriangle16BPPIA16B320X240.png", 94)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA4B320x240/Cycle1TextureTriangle16BPPIA4B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA4B320x240/Cycle1TextureTriangle16BPPIA4B320X240.png", 93)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA8B320x240/Cycle1TextureTriangle16BPPIA8B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA8B320x240/Cycle1TextureTriangle16BPPIA8B320X240.png", 94)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA16B320x240/Cycle1TextureTriangle16BPPRGBA16B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA16B320x240/Cycle1TextureTriangle16BPPRGBA16B320X240.png", 94)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA32B320x240/Cycle1TextureTriangle16BPPRGBA32B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA32B320x240/Cycle1TextureTriangle16BPPRGBA32B320X240.png", 95)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleYUV16B320x240/Cycle1TextureTriangle16BPPYUV16B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/Cycle1TextureTriangleYUV16B320x240/Cycle1TextureTriangle16BPPYUV16B320X240.png", 98)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/TLUT/CopyTextureTriangleTLUTRGBA4B320x240/CopyTextureTriangle16BPPTLUTRGBA4B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/TLUT/CopyTextureTriangleTLUTRGBA4B320x240/CopyTextureTriangle16BPPTLUTRGBA4B320X240.png", 98)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/TLUT/CopyTextureTriangleTLUTRGBA8B320x240/CopyTextureTriangle16BPPTLUTRGBA8B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/TLUT/CopyTextureTriangleTLUTRGBA8B320x240/CopyTextureTriangle16BPPTLUTRGBA8B320X240.png", 98)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA4B320x240/Cycle1TextureTriangle16BPPTLUTIA4B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA4B320x240/Cycle1TextureTriangle16BPPTLUTIA4B320X240.png", 88)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA8B320x240/Cycle1TextureTriangle16BPPTLUTIA8B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA8B320x240/Cycle1TextureTriangle16BPPTLUTIA8B320X240.png", 94)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA4B320x240/Cycle1TextureTriangle16BPPTLUTRGBA4B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA4B320x240/Cycle1TextureTriangle16BPPTLUTRGBA4B320X240.png", 88)]
    [InlineData("RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA8B320x240/Cycle1TextureTriangle16BPPTLUTRGBA8B320X240.N64", "RDP/16BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA8B320x240/Cycle1TextureTriangle16BPPTLUTRGBA8B320X240.png", 94)]
    [InlineData("RDP/32BPP/Line/FillLine/Cycle1FillLine320x240/Cycle1FillLine32BPP320X240.N64", "RDP/32BPP/Line/FillLine/Cycle1FillLine320x240/Cycle1FillLine32BPP320X240.png", 98)]
    [InlineData("RDP/32BPP/Line/FillLine/FillLine320x240/FillLine32BPP320X240.N64", "RDP/32BPP/Line/FillLine/FillLine320x240/FillLine32BPP320X240.png", 98)]
    [InlineData("RDP/32BPP/Line/Plot/FillLine/PlotFillLine32BPP320X240.N64", "RDP/32BPP/Line/Plot/FillLine/PlotFillLine32BPP320X240.png", 98)]
    [InlineData("RDP/32BPP/Line/Rotate/FillLine/RotateFillLine32BPP320X240.N64", "RDP/32BPP/Line/Rotate/FillLine/RotateFillLine32BPP320X240.png", 98)]
    [InlineData("RDP/32BPP/Rectangle/FillRectangle/Cycle1FillRectangle320x240/Cycle1FillRectangle32BPP320X240.N64", "RDP/32BPP/Rectangle/FillRectangle/Cycle1FillRectangle320x240/Cycle1FillRectangle32BPP320X240.png", 97)]
    [InlineData("RDP/32BPP/Rectangle/FillRectangle/FillRectangle320x240/FillRectangle32BPP320X240.N64", "RDP/32BPP/Rectangle/FillRectangle/FillRectangle320x240/FillRectangle32BPP320X240.png", 97)]
    [InlineData("RDP/32BPP/Rectangle/FillZBufferRectangle320x240/Cycle1FillZBufferRectangle32BPP320X240.N64", "RDP/32BPP/Rectangle/FillZBufferRectangle320x240/Cycle1FillZBufferRectangle32BPP320X240.png", 97)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI4B320x240/Cycle1TextureRectangle32BPPI4B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI4B320x240/Cycle1TextureRectangle32BPPI4B320X240.png", 92)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI8B320x240/Cycle1TextureRectangle32BPPI8B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleI8B320x240/Cycle1TextureRectangle32BPPI8B320X240.png", 94)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA16B320x240/Cycle1TextureRectangle32BPPIA16B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA16B320x240/Cycle1TextureRectangle32BPPIA16B320X240.png", 95)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA4B320x240/Cycle1TextureRectangle32BPPIA4B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA4B320x240/Cycle1TextureRectangle32BPPIA4B320X240.png", 93)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA8B320x240/Cycle1TextureRectangle32BPPIA8B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleIA8B320x240/Cycle1TextureRectangle32BPPIA8B320X240.png", 95)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA16B320x240/Cycle1TextureRectangle32BPPRGBA16B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA16B320x240/Cycle1TextureRectangle32BPPRGBA16B320X240.png", 95)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA32B320x240/Cycle1TextureRectangle32BPPRGBA32B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleRGBA32B320x240/Cycle1TextureRectangle32BPPRGBA32B320X240.png", 95)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleYUV16B320x240/Cycle1TextureRectangle32BPPYUV16B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/Cycle1TextureRectangleYUV16B320x240/Cycle1TextureRectangle32BPPYUV16B320X240.png", 98)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA4B320x240/Cycle1TextureRectangle32BPPTLUTIA4B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA4B320x240/Cycle1TextureRectangle32BPPTLUTIA4B320X240.png", 88)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA8B320x240/Cycle1TextureRectangle32BPPTLUTIA8B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTIA8B320x240/Cycle1TextureRectangle32BPPTLUTIA8B320X240.png", 95)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA4B320x240/Cycle1TextureRectangle32BPPTLUTRGBA4B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA4B320x240/Cycle1TextureRectangle32BPPTLUTRGBA4B320X240.png", 88)]
    [InlineData("RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA8B320x240/Cycle1TextureRectangle32BPPTLUTRGBA8B320X240.N64", "RDP/32BPP/Rectangle/TextureRectangle/TLUT/Cycle1TextureRectangleTLUTRGBA8B320x240/Cycle1TextureRectangle32BPPTLUTRGBA8B320X240.png", 95)]
    [InlineData("RDP/32BPP/SetPrimColor/SetPrimColor32BPP.N64", null, 0)]
    [InlineData("RDP/32BPP/Triangle/Cube/FillTriangle320x240/CubeFillTriangle32BPP320X240.N64", null, 0)]
    [InlineData("RDP/32BPP/Triangle/FillTriangle/Cycle1FillTriangle320x240/Cycle1FillTriangle32BPP320X240.N64", "RDP/32BPP/Triangle/FillTriangle/Cycle1FillTriangle320x240/Cycle1FillTriangle32BPP320X240.png", 97)]
    [InlineData("RDP/32BPP/Triangle/FillTriangle/FillTriangle320x240/FillTriangle32BPP320X240.N64", "RDP/32BPP/Triangle/FillTriangle/FillTriangle320x240/FillTriangle32BPP320X240.png", 97)]
    [InlineData("RDP/32BPP/Triangle/FillZBufferTriangle320x240/Cycle1FillZBufferTriangle32BPP320X240.N64", "RDP/32BPP/Triangle/FillZBufferTriangle320x240/Cycle1FillZBufferTriangle32BPP320X240.png", 97)]
    [InlineData("RDP/32BPP/Triangle/Plot/FillTriangle/LeftMajorTriangle320x240/LeftMajorTriangle32BPP320X240.N64", "RDP/32BPP/Triangle/Plot/FillTriangle/LeftMajorTriangle320x240/LeftMajorTriangle32BPP320X240.png", 98)]
    [InlineData("RDP/32BPP/Triangle/Plot/FillTriangle/RightMajorTriangle320x240/RightMajorTriangle32BPP320X240.N64", "RDP/32BPP/Triangle/Plot/FillTriangle/RightMajorTriangle320x240/RightMajorTriangle32BPP320X240.png", 98)]
    [InlineData("RDP/32BPP/Triangle/ShadeTriangle320x240/Cycle1ShadeTriangle32BPP320X240.N64", null, 0)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI4B320x240/Cycle1TextureTriangle32BPPI4B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI4B320x240/Cycle1TextureTriangle32BPPI4B320X240.png", 92)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI8B320x240/Cycle1TextureTriangle32BPPI8B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleI8B320x240/Cycle1TextureTriangle32BPPI8B320X240.png", 94)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA16B320x240/Cycle1TextureTriangle32BPPIA16B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA16B320x240/Cycle1TextureTriangle32BPPIA16B320X240.png", 95)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA4B320x240/Cycle1TextureTriangle32BPPIA4B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA4B320x240/Cycle1TextureTriangle32BPPIA4B320X240.png", 93)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA8B320x240/Cycle1TextureTriangle32BPPIA8B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleIA8B320x240/Cycle1TextureTriangle32BPPIA8B320X240.png", 95)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA16B320x240/Cycle1TextureTriangle32BPPRGBA16B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA16B320x240/Cycle1TextureTriangle32BPPRGBA16B320X240.png", 95)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA32B320x240/Cycle1TextureTriangle32BPPRGBA32B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleRGBA32B320x240/Cycle1TextureTriangle32BPPRGBA32B320X240.png", 95)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleYUV16B320x240/Cycle1TextureTriangle32BPPYUV16B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/Cycle1TextureTriangleYUV16B320x240/Cycle1TextureTriangle32BPPYUV16B320X240.png", 98)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA4B320x240/Cycle1TextureTriangle32BPPTLUTIA4B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA4B320x240/Cycle1TextureTriangle32BPPTLUTIA4B320X240.png", 88)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA8B320x240/Cycle1TextureTriangle32BPPTLUTIA8B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTIA8B320x240/Cycle1TextureTriangle32BPPTLUTIA8B320X240.png", 95)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA4B320x240/Cycle1TextureTriangle32BPPTLUTRGBA4B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA4B320x240/Cycle1TextureTriangle32BPPTLUTRGBA4B320X240.png", 88)]
    [InlineData("RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA8B320x240/Cycle1TextureTriangle32BPPTLUTRGBA8B320X240.N64", "RDP/32BPP/Triangle/TextureTriangle/TLUT/Cycle1TextureTriangleTLUTRGBA8B320x240/Cycle1TextureTriangle32BPPTLUTRGBA8B320X240.png", 95)]
    [InlineData("RDP/8BPP/Rectangle/TextureRectangle/TLUT/InternalPalette/InternalPalette.N64", null, 0)]
    [InlineData("RDP/AlphaCompare/AlphaCompare.N64", "RDP/AlphaCompare/AlphaCompare.png", 98)]
    [InlineData("RDP/AlphaCoverage/AlphaCoverage.N64", null, 0)]
    [InlineData("RDP/CombinerLongTailConstants/CombinerLongTailConstants.N64", "RDP/CombinerLongTailConstants/CombinerLongTailConstants.png", 99)]
    [InlineData("RDP/CombinerOverflow/CombinerOverflow.N64", "RDP/CombinerOverflow/CombinerOverflow.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b16_b16.N64", "RDP/LoadTLUT/LoadTLUT_b16_b16.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b16_b32.N64", "RDP/LoadTLUT/LoadTLUT_b16_b32.png", 94)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b16_b4.N64", "RDP/LoadTLUT/LoadTLUT_b16_b4.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b16_b8.N64", "RDP/LoadTLUT/LoadTLUT_b16_b8.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b16_b8_uls16.N64", "RDP/LoadTLUT/LoadTLUT_b16_b8_uls16.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b16_overflow.N64", "RDP/LoadTLUT/LoadTLUT_b16_overflow.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b32_b16.N64", "RDP/LoadTLUT/LoadTLUT_b32_b16.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b32_b32.N64", "RDP/LoadTLUT/LoadTLUT_b32_b32.png", 96)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b32_b4.N64", "RDP/LoadTLUT/LoadTLUT_b32_b4.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b32_b8.N64", "RDP/LoadTLUT/LoadTLUT_b32_b8.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b8_b16.N64", "RDP/LoadTLUT/LoadTLUT_b8_b16.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b8_b32.N64", "RDP/LoadTLUT/LoadTLUT_b8_b32.png", 97)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b8_b4.N64", "RDP/LoadTLUT/LoadTLUT_b8_b4.png", 99)]
    [InlineData("RDP/LoadTLUT/LoadTLUT_b8_b8.N64", "RDP/LoadTLUT/LoadTLUT_b8_b8.png", 99)]
    [InlineData("RDP/RDPModeInput/RDPModeInput.N64", null, 0)]
    [InlineData("RDP/RDPTex0And1/RDPTex0And1.N64", "RDP/RDPTex0And1/RDPTex0And1.png", 99)]
    [InlineData("RDP/TEXRECT_Palette/TEXRECT_Palette.N64", "RDP/TEXRECT_Palette/TEXRECT_Palette.png", 99)]
    [InlineData("RDP/TextureCoordinates/TextureCoordinates.N64", "RDP/TextureCoordinates/TextureCoordinates.png", 99)]
    [InlineData("RDP/TextureCoordinates1/TextureCoordinates1.N64", "RDP/TextureCoordinates1/TextureCoordinates1.png", 99)]
    [InlineData("RDP/TextureCoordinates1b/TextureCoordinates1b.N64", "RDP/TextureCoordinates1b/TextureCoordinates1b.png", 99)]
    [InlineData("RDP/TextureCoordinates2/TextureCoordinates2.N64", "RDP/TextureCoordinates2/TextureCoordinates2.png", 99)]
    [InlineData("RDP/TextureCoordinates2b/TextureCoordinates2b.N64", "RDP/TextureCoordinates2b/TextureCoordinates2b.png", 99)]
    [InlineData("RDP/TexturesMaskShiftMirror/TexturesMaskCMShiftT0_1.N64", "RDP/TexturesMaskShiftMirror/TexturesMaskCMShiftT0_1.png", 99)]
    [InlineData("RDP/TexturesMaskShiftMirror/TexturesMaskCMShiftT0_2.N64", null, 0)]
    [InlineData("RDP/TexturesMaskShiftMirror/TexturesMaskCMShiftT15_1.N64", "RDP/TexturesMaskShiftMirror/TexturesMaskCMShiftT15_1.png", 99)]
    [InlineData("RSPTest/XBUS/RSPXBUSRDP.N64", null, 0)]
    public void Rdp_programs_draw_their_pictures(string program, string? reference, int minimumPercent)
    {
        var console = PeterLemonN64Tests.Boot(program);
        for (var frame = 0; frame < 30; frame++)
        {
            console.RunFrame();
        }

        if (reference is not null)
        {
            MatchingPercent(console, reference).Should().BeGreaterThanOrEqualTo(minimumPercent, $"{program} should match {reference}");
        }

        GoldenImages.AssertMatches(
            "n64/" + Path.GetFileNameWithoutExtension(program), console.FrameBuffer, console.ScreenWidth, console.ScreenHeight, SnesPpu.Palette);
    }

    /// <summary>
    /// PeterLemon's demos that use the whole machine: RSP programs that compute pictures (a gradient, DCT decoding,
    /// strided DMA) or send the RDP its commands over the XBUS, held to their reference pictures; and FPU fractals drawn
    /// by the CPU, only pinned (the Julia set animates, and the Mandelbrot reference differs along the set's edge, where
    /// single-precision rounding decides).
    /// </summary>
    [N64TestRomTheory]
    [InlineData("RSP/Gradient/RSPGradient.N64", 30, 99)]
    [InlineData("RSP/DCT/FastDCTBlockDecode/RSPFastDCTBlockDecode.N64", 30, 99)]
    [InlineData("RSP/DMAStride/RSPDMAStride.N64", 30, 99)]
    [InlineData("RSP/XBUS/RSPPlotTriangle/RSPPlotTriangle.N64", 30, 99)]
    [InlineData("RSP/XBUS/RSPTrans3DRectangle/RSPTrans3DRectangle.N64", 30, 98)]
    [InlineData("CP1/Fractal/32BPP/320X240/Mandelbrot/Single/Mandelbrot32BPP320X240.N64", 240, 0)]
    [InlineData("CP1/Fractal/32BPP/320X240/Julia/Double/Julia32BPP320X240.N64", 240, 0)]
    public void Whole_machine_demos_draw_their_pictures(string program, int frames, int minimumPercent)
    {
        var console = PeterLemonN64Tests.Boot(program);
        for (var frame = 0; frame < frames; frame++)
        {
            console.RunFrame();
        }

        if (minimumPercent > 0)
        {
            MatchingPercent(console, Path.ChangeExtension(program, ".png")).Should().BeGreaterThanOrEqualTo(minimumPercent);
        }

        GoldenImages.AssertMatches(
            "n64/" + Path.GetFileNameWithoutExtension(program), console.FrameBuffer, console.ScreenWidth, console.ScreenHeight, SnesPpu.Palette);
    }

    /// <summary>The share of the reference's pixels the framebuffer at VI_ORIGIN matches exactly (5-bit colour widened by repeating its top bits).</summary>
    internal static double MatchingPercent(GameVault.Core.Nintendo64.N64Console console, string reference)
    {
        var (width, height, rgb) = PngReader.Decode(TestRomLocator.ReadCached(PeterLemonN64Tests.CacheFolder + reference));
        var origin = (int)(console.Read32(0x0440_0004) & 0xFF_FFFF);
        var stride = (int)(console.Read32(0x0440_0008) & 0xFFF);
        var wide = (console.Read32(0x0440_0000) & 3) == 3;
        var matching = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                int r, g, b;
                if (wide)
                {
                    var pixel = BinaryPrimitives.ReadUInt32BigEndian(console.Rdram.AsSpan(origin + (y * stride + x) * 4));
                    (r, g, b) = ((int)(pixel >> 24), (int)(pixel >> 16) & 0xFF, (int)(pixel >> 8) & 0xFF);
                }
                else
                {
                    var pixel = BinaryPrimitives.ReadUInt16BigEndian(console.Rdram.AsSpan(origin + (y * stride + x) * 2));
                    (r, g, b) = (Widen(pixel >> 11), Widen(pixel >> 6), Widen(pixel >> 1));
                }

                var at = (y * width + x) * 3;
                matching += rgb[at] == r && rgb[at + 1] == g && rgb[at + 2] == b ? 1 : 0;
            }
        }

        return 100.0 * matching / (width * height);
    }

    private static int Widen(int fiveBits) => ((fiveBits & 31) << 3) | ((fiveBits & 31) >> 2);
}
