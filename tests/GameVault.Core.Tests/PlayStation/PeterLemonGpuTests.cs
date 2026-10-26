using GameVault.Core.PlayStation;
using GameVault.Core.Snes;
using GameVault.Core.Tests.Harness;

namespace GameVault.Core.Tests.PlayStation;

/// <summary>
/// PeterLemon's bare-metal PSX GPU programs (downloaded at test time, see tests/TestRoms/SOURCES.md): polygons,
/// textures at each depth, rectangles, lines, clipping, dithering and transfers, checked against golden hashes of the
/// 320×240 display. Each hash was recorded after comparing the picture with the reference PNG in PeterLemon's
/// repository (docs/plans/phase-15.md lists the comparison).
/// </summary>
public sealed class PeterLemonGpuTests
{
    [TestRomTheory("psx-peterlemon/GPU/16BPP/RenderPolygon/RenderPolygon16BPP.exe")]
    [InlineData("16BPP/MemoryTransfer/MemoryTransfer16BPP.exe")]
    [InlineData("16BPP/RenderLine/RenderLine16BPP.exe")]
    [InlineData("16BPP/RenderPolygon/RenderPolygon16BPP.exe")]
    [InlineData("16BPP/RenderPolygonClip/RenderPolygonClip16BPP.exe")]
    [InlineData("16BPP/RenderPolygonDither/RenderPolygonDither16BPP.exe")]
    [InlineData("16BPP/RenderRectangle/RenderRectangle16BPP.exe")]
    [InlineData("16BPP/RenderRectangleClip/RenderRectangleClip16BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygon/15BPP/RenderTexturePolygon15BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygon/CLUT4BPP/RenderTexturePolygonCLUT4BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygon/CLUT8BPP/RenderTexturePolygonCLUT8BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygon/MASK15BPP/RenderTexturePolygonMASK15BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygonClip/15BPP/RenderTexturePolygonClip15BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygonClip/CLUT4BPP/RenderTexturePolygonClipCLUT4BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygonClip/CLUT8BPP/RenderTexturePolygonClipCLUT8BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygonClip/MASK15BPP/RenderTexturePolygonClipMASK15BPP.exe")]
    [InlineData("16BPP/RenderTexturePolygonDither/RenderTexturePolygon15BPPDither.exe")]
    [InlineData("16BPP/RenderTextureRectangle/15BPP/RenderTextureRectangle15BPP.exe")]
    [InlineData("16BPP/RenderTextureRectangle/CLUT4BPP/RenderTextureRectangleCLUT4BPP.exe")]
    [InlineData("16BPP/RenderTextureRectangle/CLUT8BPP/RenderTextureRectangleCLUT8BPP.exe")]
    [InlineData("16BPP/RenderTextureRectangle/MASK15BPP/RenderTextureRectangleMASK15BPP.exe")]
    [InlineData("16BPP/RenderTextureWindowRectangle/15BPP/RenderTextureWindowRectangle15BPP.exe")]
    [InlineData("16BPP/RenderTextureWindowRectangle/CLUT4BPP/RenderTextureWindowRectangleCLUT4BPP.exe")]
    [InlineData("16BPP/RenderTextureWindowRectangle/CLUT8BPP/RenderTextureWindowRectangleCLUT8BPP.exe")]
    [InlineData("16BPP/RenderTextureWindowRectangle/MASK15BPP/RenderTextureWindowRectangleMASK15BPP.exe")]
    [InlineData("24BPP/MemoryTransfer/MemoryTransfer24BPP.exe")]
    public void The_picture_matches_its_golden_hash(string program)
    {
        var console = PeterLemonCpuTests.RunToFinalLoop("psx-peterlemon/GPU/" + program);
        var gpu = console.Gpu;
        var frame = new ushort[gpu.DisplayWidth * gpu.DisplayHeight];
        gpu.CopyDisplay(frame);

        GoldenImages.AssertMatches("psx/" + Path.GetFileNameWithoutExtension(program), frame, gpu.DisplayWidth, gpu.DisplayHeight, SnesPpu.Palette);
    }
}
