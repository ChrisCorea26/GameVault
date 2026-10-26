using System.Reflection;
using System.Runtime.InteropServices;
using GameVault.ViewModels.Services;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GameVault.Platform.Video;

/// <summary>
/// Direct3D 11 renderer for the display presets (SPEC §5.2). Draws the 256×240 frame into a composition
/// swap chain that the app attaches to a <c>SwapChainPanel</c>. Every method must be called from one thread
/// (the app's render thread); nothing here touches XAML.
/// </summary>
public sealed class D3D11FrameRenderer : IDisposable
{
    // The frame texture's current size: 256×240 until a system with another size presents a frame.
    private int _frameWidth = DisplayLayout.FrameWidth;
    private int _frameHeight = DisplayLayout.FrameHeight;
    private double _framePixelAspect = 1.0;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11SamplerState _pointSampler;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11Buffer _constants;
    private ID3D11Texture2D _frameTexture;
    private ID3D11ShaderResourceView _frameView;
    private readonly uint[] _pixels = new uint[IVideoSink.MaxWidth * IVideoSink.MaxHeight];
    private ID3D11RenderTargetView? _target;
    private int _width;
    private int _height;

    /// <summary>Creates the device and a swap chain of the given size in physical pixels.</summary>
    /// <exception cref="InvalidOperationException">Direct3D 11 isn't available.</exception>
    public D3D11FrameRenderer(int width, int height, bool forceWarp = false)
    {
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        var result = D3D11.D3D11CreateDevice(
            null,
            forceWarp ? DriverType.Warp : DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
            out _device!,
            out _context!);
        if (result.Failure)
        {
            // WARP keeps the presets working on machines without a usable GPU driver.
            result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_10_0], out _device!, out _context!);
            if (result.Failure)
            {
                throw new InvalidOperationException($"Direct3D 11 is not available ({result}).");
            }
        }

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDevice.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, description);
        }

        var source = LoadShaderSource();
        var vs = Compiler.Compile(source, "VS", "Display.hlsl", "vs_4_0");
        var ps = Compiler.Compile(source, "PS", "Display.hlsl", "ps_4_0");
        _vertexShader = _device.CreateVertexShader(vs.Span);
        _pixelShader = _device.CreatePixelShader(ps.Span);

        _pointSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp));
        _linearSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        _constants = _device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        _frameTexture = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)_frameWidth, (uint)_frameHeight, 1, 1, BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
        _frameView = _device.CreateShaderResourceView(_frameTexture);

        CreateTarget();
    }

    /// <summary>The native IDXGISwapChain pointer, for <c>ISwapChainPanelNative::SetSwapChain</c>.</summary>
    public IntPtr SwapChainPointer => _swapChain.NativePointer;

    /// <summary>
    /// The swap chain is sized in physical pixels; this tells the panel not to scale it up again at
    /// display scaling above 100%.
    /// </summary>
    public void SetCompositionScale(float scaleX, float scaleY)
    {
        using var swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (swapChain2 is not null && scaleX > 0 && scaleY > 0)
        {
            swapChain2.MatrixTransform = new System.Numerics.Matrix3x2(1f / scaleX, 0, 0, 1f / scaleY, 0, 0);
        }
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height)
        {
            return;
        }

        _width = width;
        _height = height;
        _context.ClearState(); // releases the back buffer before resizing; Draw sets everything again
        _target?.Dispose();
        _target = null;
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
        CreateTarget();
    }

    /// <summary>Uploads a new 256×240 frame (palette indices plus emphasis) converted through <paramref name="palette"/>.</summary>
    public void UploadFrame(ReadOnlySpan<ushort> frame, ReadOnlySpan<uint> palette) =>
        UploadFrame(frame, palette, DisplayLayout.FrameWidth, DisplayLayout.FrameHeight, 1.0);

    /// <summary>Uploads a frame of any system's size; the texture follows the frame's size.</summary>
    public void UploadFrame(VideoFrame frame, ReadOnlySpan<uint> palette) =>
        UploadFrame(frame.Span, palette, frame.Width, frame.Height, frame.PixelAspect);

    private void UploadFrame(ReadOnlySpan<ushort> frame, ReadOnlySpan<uint> palette, int width, int height, double pixelAspect)
    {
        if (width != _frameWidth || height != _frameHeight)
        {
            _frameView.Dispose();
            _frameTexture.Dispose();
            _frameWidth = width;
            _frameHeight = height;
            _frameTexture = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _frameView = _device.CreateShaderResourceView(_frameTexture);
        }

        _framePixelAspect = pixelAspect;
        var count = width * height;
        for (var i = 0; i < count; i++)
        {
            _pixels[i] = palette[frame[i] & 0x7FF];
        }

        var mapped = _context.Map(_frameTexture, 0, MapMode.WriteDiscard);
        try
        {
            var source = MemoryMarshal.AsBytes(_pixels.AsSpan(0, count));
            for (var y = 0; y < height; y++)
            {
                var row = source.Slice(y * width * 4, width * 4);
                unsafe
                {
                    row.CopyTo(new Span<byte>((byte*)mapped.DataPointer + y * mapped.RowPitch, width * 4));
                }
            }
        }
        finally
        {
            _context.Unmap(_frameTexture, 0);
        }
    }

    /// <summary>Draws the last uploaded frame and presents, waiting for vertical sync.</summary>
    public void Render(DisplayPreset preset, bool cropOverscan, bool tvPixelShape)
    {
        Draw(preset, cropOverscan, tvPixelShape);
        _swapChain.Present(1, PresentFlags.None);
    }

    private void Draw(DisplayPreset preset, bool cropOverscan, bool tvPixelShape)
    {
        var rect = DisplayLayout.Compute(_width, _height, preset, cropOverscan, tvPixelShape, _frameWidth, _frameHeight, _framePixelAspect);

        var constants = new Constants
        {
            OutputWidth = rect.Width,
            OutputHeight = rect.Height,
            SourceTop = rect.SourceTop,
            SourceHeight = rect.SourceHeight,
            Mode = (int)preset,
            FrameWidth = _frameWidth,
            FrameHeight = _frameHeight,
        };
        var mapped = _context.Map(_constants, 0, MapMode.WriteDiscard);
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_constants, 0);

        _context.OMSetRenderTargets(_target!);
        _context.ClearRenderTargetView(_target!, new Color4(0f, 0f, 0f, 1f));
        _context.RSSetViewport(new Viewport(rect.X, rect.Y, rect.Width, rect.Height));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constants);
        _context.PSSetShaderResource(0, _frameView);
        _context.PSSetSampler(0, _pointSampler);
        _context.PSSetSampler(1, _linearSampler);
        _context.Draw(3, 0);
    }

    /// <summary>Reads one pixel of the back buffer as ARGB (tests only; slow).</summary>
    internal uint ReadPixel(int x, int y)
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        var description = backBuffer.Description;
        description.BindFlags = BindFlags.None;
        description.Usage = ResourceUsage.Staging;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        using var staging = _device.CreateTexture2D(description);
        _context.CopyResource(staging, backBuffer);
        var mapped = _context.Map(staging, 0, MapMode.Read);
        try
        {
            return (uint)Marshal.ReadInt32(mapped.DataPointer + y * (int)mapped.RowPitch + x * 4);
        }
        finally
        {
            _context.Unmap(staging, 0);
        }
    }

    /// <summary>Draws without presenting (tests read the back buffer before it is flipped away).</summary>
    internal void RenderWithoutPresent(DisplayPreset preset, bool cropOverscan, bool tvPixelShape) =>
        Draw(preset, cropOverscan, tvPixelShape);

    public void Dispose()
    {
        _context.ClearState();
        _target?.Dispose();
        _frameView.Dispose();
        _frameTexture.Dispose();
        _constants.Dispose();
        _linearSampler.Dispose();
        _pointSampler.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
        _swapChain.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    private void CreateTarget()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _target = _device.CreateRenderTargetView(backBuffer);
    }

    private static string LoadShaderSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameVault.Display.hlsl")
            ?? throw new InvalidOperationException("The display shader resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Constants
    {
        public float OutputWidth;
        public float OutputHeight;
        public float SourceTop;
        public float SourceHeight;
        public int Mode;
        public float FrameWidth;
        public float FrameHeight;
        public float Padding;
    }
}
