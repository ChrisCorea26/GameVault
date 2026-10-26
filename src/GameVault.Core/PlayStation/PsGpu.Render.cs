namespace GameVault.Core.PlayStation;

public sealed partial class PsGpu
{
    // The rasterizer (polygons, lines and rectangles) arrives with the GPU work in step 3 of the Phase 15 plan.
    // Until then the commands are decoded and consumed, so the command stream stays in step, but draw nothing.

    private void DrawPolygon()
    {
    }

    private void DrawLine()
    {
    }

    private void DrawLineSegment(uint from, uint fromColor, uint to, uint toColor)
    {
    }

    private void DrawRectangle()
    {
    }
}
