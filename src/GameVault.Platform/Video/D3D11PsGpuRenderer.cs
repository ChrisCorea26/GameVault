using System.Reflection;
using System.Runtime.InteropServices;
using GameVault.Core.PlayStation;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GameVault.Platform.Video;

/// <summary>
/// The PlayStation GPU on Direct3D 11 at 1×–3× internal resolution (docs/plans/gpu-renderers.md, milestone 1). It's the
/// GPU's <see cref="IPsGpuBackend"/>: each primitive is drawn again into a scaled VRAM render target, textures are
/// read from the emulated VRAM (which stays authoritative), and <see cref="ReadDisplay"/> gives the displayed area
/// as ARGB. Every call must come from one thread (the emulator's).
/// </summary>
public sealed class D3D11PsGpuRenderer : IPsGpuBackend, IDisposable
{
    public const int MaxScale = 3;

    // Draw flags, as PsGpu.hlsl reads them.
    private const int FlagTextured = 1, FlagRaw = 2, FlagSetMask = 4, FlagSemiPass = 8, FlagOpaquePass = 16, FlagSemi = 32;

    private const int VertexStride = 7 * sizeof(float);
    private const int MaxVertices = 6;

    private readonly PsGpu _gpu;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _target;
    private readonly ID3D11RenderTargetView _targetView;
    private readonly ID3D11Texture2D _copy;
    private readonly ID3D11ShaderResourceView _copyView;
    private readonly ID3D11Texture2D _native;
    private readonly ID3D11ShaderResourceView _nativeView;
    private readonly ID3D11Texture2D _staging;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _drawShader;
    private readonly ID3D11PixelShader _uploadShader;
    private readonly ID3D11PixelShader _copyShader;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _vertices;
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11RasterizerState _scissored;
    private readonly ID3D11RasterizerState _unclipped;
    private readonly ID3D11BlendState _opaque;
    private readonly ID3D11BlendState _maskChecked;
    private readonly ID3D11BlendState[] _semi = new ID3D11BlendState[4];
    private readonly float[] _vertexData = new float[MaxVertices * 7];
    private readonly bool[] _dirtyRows = new bool[PsGpu.VramHeight];
    private uint[] _display = [];

    /// <exception cref="InvalidOperationException">Direct3D 11 isn't available.</exception>
    public D3D11PsGpuRenderer(PsGpu gpu, int scale, bool forceWarp = false)
    {
        _gpu = gpu;
        Scale = Math.Clamp(scale, 1, MaxScale);
        var result = D3D11.D3D11CreateDevice(null, forceWarp ? DriverType.Warp : DriverType.Hardware, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out _device!, out _context!);
        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None, [FeatureLevel.Level_10_0], out _device!, out _context!);
            if (result.Failure)
            {
                throw new InvalidOperationException($"Direct3D 11 is not available ({result}).");
            }
        }

        var width = (uint)(PsGpu.VramWidth * Scale);
        var height = (uint)(PsGpu.VramHeight * Scale);
        _target = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, width, height, 1, 1,
            BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        _targetView = _device.CreateRenderTargetView(_target);
        _copy = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, width, height, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        _copyView = _device.CreateShaderResourceView(_copy);
        _native = _device.CreateTexture2D(new Texture2DDescription(Format.R16_UInt, PsGpu.VramWidth, PsGpu.VramHeight, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None));
        _nativeView = _device.CreateShaderResourceView(_native);
        _staging = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, width, height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));

        var source = LoadShaderSource();
        var vs = Compiler.Compile(source, "VS", "PsGpu.hlsl", "vs_4_0");
        _vertexShader = _device.CreateVertexShader(vs.Span);
        _drawShader = _device.CreatePixelShader(Compiler.Compile(source, "PS", "PsGpu.hlsl", "ps_4_0").Span);
        _uploadShader = _device.CreatePixelShader(Compiler.Compile(source, "PS_Upload", "PsGpu.hlsl", "ps_4_0").Span);
        _copyShader = _device.CreatePixelShader(Compiler.Compile(source, "PS_Copy", "PsGpu.hlsl", "ps_4_0").Span);
        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32_Float, 8, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 20, 0),
        ], vs.Span);
        _vertices = _device.CreateBuffer(new BufferDescription(MaxVertices * VertexStride, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _constants = _device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<DrawConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _scissored = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { ScissorEnable = true });
        _unclipped = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _opaque = _device.CreateBlendState(BlendDescription.Opaque);

        // "Check mask": out = src × (1 − dst.a) + dst × dst.a, so pixels whose mask bit is set keep what they had.
        _maskChecked = Blend(Vortice.Direct3D11.Blend.InverseDestinationAlpha, Vortice.Direct3D11.Blend.DestinationAlpha, BlendOperation.Add);

        // The semi-transparency modes: B/2 + F/2, B + F, B − F, B + F/4 (the blend factor carries the halves and quarter).
        _semi[0] = Blend(Vortice.Direct3D11.Blend.BlendFactor, Vortice.Direct3D11.Blend.BlendFactor, BlendOperation.Add);
        _semi[1] = Blend(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.One, BlendOperation.Add);
        _semi[2] = Blend(Vortice.Direct3D11.Blend.One, Vortice.Direct3D11.Blend.One, BlendOperation.ReverseSubtract);
        _semi[3] = Blend(Vortice.Direct3D11.Blend.BlendFactor, Vortice.Direct3D11.Blend.One, BlendOperation.Add);

        Resync();
    }

    public int Scale { get; }

    private ID3D11BlendState Blend(Vortice.Direct3D11.Blend source, Vortice.Direct3D11.Blend destination, BlendOperation operation)
    {
        var description = new BlendDescription();
        description.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = source,
            DestinationBlend = destination,
            BlendOperation = operation,
            SourceBlendAlpha = Vortice.Direct3D11.Blend.One,
            DestinationBlendAlpha = Vortice.Direct3D11.Blend.Zero,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        return _device.CreateBlendState(description);
    }

    private readonly record struct Scissor(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawConstants
    {
        public int PageX, PageY, ClutX, ClutY, Depth, Flags;
        public uint TextureWindow;
        public int Scale, CopyOffsetX, CopyOffsetY, PaddingX, PaddingY;
    }

    // ---- IPsGpuBackend ----

    public void Triangle(in PsGpuVertex a, in PsGpuVertex b, in PsGpuVertex c, in PsGpuDraw draw)
    {
        int minX = Math.Min(a.X, Math.Min(b.X, c.X)), maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        int minY = Math.Min(a.Y, Math.Min(b.Y, c.Y)), maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        if (maxX - minX >= 1024 || maxY - minY >= 512)
        {
            return; // the GPU skips these (psx-spx)
        }

        var flat = !draw.Gouraud;
        Put(0, a.X, a.Y, a, a.U, a.V, a, flat);
        Put(1, b.X, b.Y, b, b.U, b.V, a, flat);
        Put(2, c.X, c.Y, c, c.U, c.V, a, flat);
        Submit(3, draw, minY, maxY);
    }

    public void Rectangle(in PsGpuVertex corner, int width, int height, bool flipX, bool flipY, in PsGpuDraw draw)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Pixel centres sample u0 ± dx: a flipped edge starts a texel on (gpu-renderers.md).
        float x0 = corner.X, y0 = corner.Y, x1 = corner.X + width, y1 = corner.Y + height;
        float u0 = flipX ? corner.U + 1 : corner.U, v0 = flipY ? corner.V + 1 : corner.V;
        var u1 = flipX ? u0 - width : u0 + width;
        var v1 = flipY ? v0 - height : v0 + height;
        Put(0, x0, y0, corner, u0, v0, corner, true);
        Put(1, x1, y0, corner, u1, v0, corner, true);
        Put(2, x0, y1, corner, u0, v1, corner, true);
        Put(3, x1, y0, corner, u1, v0, corner, true);
        Put(4, x1, y1, corner, u1, v1, corner, true);
        Put(5, x0, y1, corner, u0, v1, corner, true);
        Submit(6, draw, corner.Y, corner.Y + height - 1);
    }

    public void Line(in PsGpuVertex from, in PsGpuVertex to, in PsGpuDraw draw)
    {
        // A line one VRAM pixel thick, as a quad along its major axis covering both end pixels.
        float x0 = from.X + 0.5f, y0 = from.Y + 0.5f, x1 = to.X + 0.5f, y1 = to.Y + 0.5f;
        var horizontal = Math.Abs(to.X - from.X) >= Math.Abs(to.Y - from.Y);
        var length = MathF.Max(1, MathF.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
        float ex = (x1 - x0) / length * 0.5f, ey = (y1 - y0) / length * 0.5f;
        x0 -= ex;
        y0 -= ey;
        x1 += ex;
        y1 += ey;
        float px = horizontal ? 0 : 0.5f, py = horizontal ? 0.5f : 0;
        var flat = !draw.Gouraud;
        Put(0, x0 - px, y0 - py, from, 0, 0, from, flat);
        Put(1, x1 - px, y1 - py, to, 0, 0, from, flat);
        Put(2, x0 + px, y0 + py, from, 0, 0, from, flat);
        Put(3, x1 - px, y1 - py, to, 0, 0, from, flat);
        Put(4, x1 + px, y1 + py, to, 0, 0, from, flat);
        Put(5, x0 + px, y0 + py, from, 0, 0, from, flat);
        var untextured = draw;
        untextured.Textured = false;
        Submit(6, untextured, Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y));
    }

    public void Fill(int x, int y, int width, int height) => DrawFromNative(x, y, width, height);

    public void Upload(int x, int y, int width, int height) => DrawFromNative(x, y, width, height);

    public void Copy(int sourceX, int sourceY, int destinationX, int destinationY, int width, int height, bool setMask, bool checkMask)
    {
        _context.CopyResource(_copy, _target);
        var constants = new DrawConstants
        {
            Flags = setMask ? FlagSetMask : 0,
            Scale = Scale,
            CopyOffsetX = (destinationX - sourceX) * Scale,
            CopyOffsetY = (destinationY - sourceY) * Scale,
        };
        Quad(destinationX, destinationY, width, height);
        DrawWith(_copyShader, constants, 6, _unclipped, checkMask ? _maskChecked : _opaque, default, null);
        MarkDirty(destinationY, destinationY + height - 1);
    }

    /// <summary>Uploads the whole emulated VRAM and draws it into the scaled one (on attach and after a state load).</summary>
    public void Resync()
    {
        UploadRows(0, PsGpu.VramHeight - 1);
        Array.Clear(_dirtyRows);
        Quad(0, 0, PsGpu.VramWidth, PsGpu.VramHeight);
        DrawWith(_uploadShader, new DrawConstants { Scale = Scale }, 6, _unclipped, _opaque, default, null);
    }

    // ---- Drawing ----

    private void Put(int index, float x, float y, in PsGpuVertex colour, float u, float v, in PsGpuVertex flatColour, bool flat)
    {
        var at = index * 7;
        var c = flat ? flatColour : colour;
        _vertexData[at] = x;
        _vertexData[at + 1] = y;
        _vertexData[at + 2] = c.R;
        _vertexData[at + 3] = c.G;
        _vertexData[at + 4] = c.B;
        _vertexData[at + 5] = u;
        _vertexData[at + 6] = v;
    }

    private void Quad(int x, int y, int width, int height)
    {
        var none = default(PsGpuVertex);
        float x0 = x, y0 = y, x1 = x + width, y1 = y + height;
        Put(0, x0, y0, none, 0, 0, none, true);
        Put(1, x1, y0, none, 0, 0, none, true);
        Put(2, x0, y1, none, 0, 0, none, true);
        Put(3, x1, y0, none, 0, 0, none, true);
        Put(4, x1, y1, none, 0, 0, none, true);
        Put(5, x0, y1, none, 0, 0, none, true);
    }

    /// <summary>A region of the native VRAM, now changed by the CPU or a fill, drawn into the scaled VRAM.</summary>
    private void DrawFromNative(int x, int y, int width, int height)
    {
        UploadRows(y, y + height - 1);
        Quad(x, y, Math.Min(width, PsGpu.VramWidth - x), Math.Min(height, PsGpu.VramHeight - y));
        DrawWith(_uploadShader, new DrawConstants { Scale = Scale }, 6, _unclipped, _opaque, default, null);
    }

    private void Submit(int count, in PsGpuDraw draw, int top, int bottom)
    {
        if (draw.Textured)
        {
            // The texture's page and CLUT come from the emulated VRAM: send the rows that changed since.
            if (AnyDirty(draw.PageY, draw.PageY + 255) || _dirtyRows[draw.ClutY & (PsGpu.VramHeight - 1)])
            {
                FlushDirtyRows();
            }
        }

        var flags = (draw.Textured ? FlagTextured : 0) | (draw.Raw ? FlagRaw : 0) | (draw.SetMask ? FlagSetMask : 0);
        var constants = new DrawConstants
        {
            PageX = draw.PageX,
            PageY = draw.PageY,
            ClutX = draw.ClutX,
            ClutY = draw.ClutY,
            Depth = draw.Depth,
            TextureWindow = draw.TextureWindow,
            Scale = Scale,
        };
        var scissor = new Scissor(draw.DrawLeft * Scale, draw.DrawTop * Scale, (draw.DrawRight + 1) * Scale, (draw.DrawBottom + 1) * Scale);
        if (scissor.Right <= scissor.Left || scissor.Bottom <= scissor.Top)
        {
            return;
        }

        var opaque = draw.CheckMask ? _maskChecked : _opaque;
        if (!draw.SemiTransparent)
        {
            constants.Flags = flags;
            DrawWith(_drawShader, constants, count, _scissored, opaque, scissor, null);
        }
        else
        {
            var factor = draw.SemiMode switch { 0 => 0.5f, 3 => 0.25f, _ => 1f };
            if (draw.Textured)
            {
                // Texels without bit 15 are opaque; only those with it blend.
                constants.Flags = flags | FlagSemi | FlagOpaquePass;
                DrawWith(_drawShader, constants, count, _scissored, opaque, scissor, null);
            }

            constants.Flags = flags | FlagSemi | (draw.Textured ? FlagSemiPass : 0);
            DrawWith(_drawShader, constants, count, _scissored, _semi[draw.SemiMode & 3], scissor, factor);
        }

        MarkDirty(Math.Max(top, draw.DrawTop), Math.Min(bottom, draw.DrawBottom));
    }

    private void DrawWith(ID3D11PixelShader shader, DrawConstants constants, int vertexCount, ID3D11RasterizerState rasterizer,
        ID3D11BlendState blend, Scissor scissor, float? blendFactor)
    {
        var mapped = _context.Map(_vertices, 0, MapMode.WriteDiscard);
        Marshal.Copy(_vertexData, 0, mapped.DataPointer, vertexCount * 7);
        _context.Unmap(_vertices, 0);
        mapped = _context.Map(_constants, 0, MapMode.WriteDiscard);
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_constants, 0);

        _context.OMSetRenderTargets(_targetView);
        _context.RSSetViewport(new Viewport(0, 0, PsGpu.VramWidth * Scale, PsGpu.VramHeight * Scale));
        _context.RSSetState(rasterizer);
        if (rasterizer == _scissored)
        {
            _context.RSSetScissorRect(scissor.Left, scissor.Top, scissor.Right - scissor.Left, scissor.Bottom - scissor.Top);
        }

        var factor = blendFactor ?? 1f;
        _context.OMSetBlendState(blend, new Color4(factor, factor, factor, 1f), uint.MaxValue);
        _context.IASetInputLayout(_layout);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.IASetVertexBuffer(0, _vertices, VertexStride, 0);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(shader);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetShaderResource(0, _nativeView);
        _context.PSSetShaderResource(1, shader == _copyShader ? _copyView : null!);
        _context.Draw((uint)vertexCount, 0);
        _context.PSSetShaderResource(1, null!);
    }

    // ---- The native VRAM's rows ----

    private void MarkDirty(int top, int bottom)
    {
        for (var y = Math.Max(0, top); y <= Math.Min(PsGpu.VramHeight - 1, bottom); y++)
        {
            _dirtyRows[y] = true;
        }
    }

    private bool AnyDirty(int top, int bottom)
    {
        for (var y = top; y <= bottom; y++)
        {
            if (_dirtyRows[y & (PsGpu.VramHeight - 1)])
            {
                return true;
            }
        }

        return false;
    }

    private void FlushDirtyRows()
    {
        var y = 0;
        while (y < PsGpu.VramHeight)
        {
            if (!_dirtyRows[y])
            {
                y++;
                continue;
            }

            var start = y;
            while (y < PsGpu.VramHeight && _dirtyRows[y])
            {
                _dirtyRows[y++] = false;
            }

            UploadRows(start, y - 1);
        }
    }

    private void UploadRows(int top, int bottom)
    {
        top = Math.Clamp(top, 0, PsGpu.VramHeight - 1);
        bottom = Math.Clamp(bottom, top, PsGpu.VramHeight - 1);
        var rows = _gpu.Vram.Slice(top * PsGpu.VramWidth, (bottom - top + 1) * PsGpu.VramWidth);
        var box = new Box(0, top, 0, PsGpu.VramWidth, bottom + 1, 1);
        unsafe
        {
            fixed (ushort* data = rows)
            {
                _context.UpdateSubresource(_native, 0, box, (nint)data, PsGpu.VramWidth * sizeof(ushort), 0);
            }
        }

        for (var y = top; y <= bottom; y++)
        {
            _dirtyRows[y] = false;
        }
    }

    // ---- The picture ----

    /// <summary>
    /// The displayed area at the internal resolution as ARGB (its size is the display's times <see cref="Scale"/>), or
    /// empty when the GPU renderer can't show it (24-bit colour, or the display off).
    /// </summary>
    public ReadOnlySpan<uint> ReadDisplay(out int width, out int height)
    {
        width = _gpu.DisplayWidth * Scale;
        height = _gpu.DisplayHeight * Scale;
        if (_gpu.Display24Bit || !_gpu.DisplayEnabled)
        {
            width = height = 0;
            return default;
        }

        var x = Math.Min(_gpu.DisplayX * Scale, (PsGpu.VramWidth * Scale) - width);
        var y = Math.Min(_gpu.DisplayY * Scale, (PsGpu.VramHeight * Scale) - height);
        x = Math.Max(0, x);
        y = Math.Max(0, y);
        width = Math.Min(width, PsGpu.VramWidth * Scale);
        height = Math.Min(height, PsGpu.VramHeight * Scale);
        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, _target, 0, new Box(x, y, 0, x + width, y + height, 1));
        if (_display.Length < width * height)
        {
            _display = new uint[width * height];
        }

        var mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            for (var row = 0; row < height; row++)
            {
                var line = mapped.DataPointer + row * (nint)mapped.RowPitch;
                for (var column = 0; column < width; column++)
                {
                    var rgba = (uint)Marshal.ReadInt32(line + column * 4); // R, G, B, A in memory order
                    _display[row * width + column] = 0xFF000000 | ((rgba & 0xFF) << 16) | (rgba & 0xFF00) | ((rgba >> 16) & 0xFF);
                }
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        return _display.AsSpan(0, width * height);
    }

    public void Dispose()
    {
        _context.ClearState();
        foreach (var state in _semi)
        {
            state.Dispose();
        }

        _maskChecked.Dispose();
        _opaque.Dispose();
        _unclipped.Dispose();
        _scissored.Dispose();
        _constants.Dispose();
        _vertices.Dispose();
        _layout.Dispose();
        _copyShader.Dispose();
        _uploadShader.Dispose();
        _drawShader.Dispose();
        _vertexShader.Dispose();
        _staging.Dispose();
        _nativeView.Dispose();
        _native.Dispose();
        _copyView.Dispose();
        _copy.Dispose();
        _targetView.Dispose();
        _target.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private static string LoadShaderSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameVault.PsGpu.hlsl")
            ?? throw new InvalidOperationException("The PlayStation GPU shader resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
