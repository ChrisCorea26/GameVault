using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GameVault.Platform.Video;

/// <summary>
/// <see cref="IGpuCompute"/> on Direct3D 11: structured buffers of 32-bit words bound as unordered access views, and
/// compute shaders compiled with the HLSL compiler (cs_5_0). Uses the hardware device, or WARP (Direct3D's software
/// device) when there is none or <c>forceWarp</c> asks for it.
/// </summary>
/// <remarks>
/// The renderers' kernels are big, and the HLSL compiler takes minutes over them, so compiled kernels are kept in
/// <see cref="DefaultCacheDirectory"/> (or the directory given), named by a hash of the source and the compile
/// settings: only the first start after an update compiles.
/// </remarks>
public sealed class D3D11Compute : IGpuCompute
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11Buffer? _staging;
    private int _stagingWords;
    private readonly string? _cacheDirectory;

    /// <summary>Where compiled kernels are kept when no other directory is given: the user's temporary folder.</summary>
    public static string DefaultCacheDirectory => Path.Combine(Path.GetTempPath(), "GameVault", "Shaders");

    /// <param name="cacheDirectory">Where compiled kernels are kept; null for <see cref="DefaultCacheDirectory"/>.</param>
    /// <exception cref="InvalidOperationException">Direct3D 11 isn't available.</exception>
    public D3D11Compute(bool forceWarp = false, string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory;
        var result = D3D11.D3D11CreateDevice(null, forceWarp ? DriverType.Warp : DriverType.Hardware, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out _device!, out _context!);
        if (result.Failure)
        {
            result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None, [FeatureLevel.Level_11_0], out _device!, out _context!);
            if (result.Failure)
            {
                throw new InvalidOperationException($"Direct3D 11 compute is not available ({result}).");
            }
        }
    }

    public IGpuBuffer CreateBuffer(int words)
    {
        words = Math.Max(words, 1);
        var buffer = _device.CreateBuffer(new BufferDescription((uint)(words * 4), BindFlags.UnorderedAccess | BindFlags.ShaderResource,
            ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4));
        var view = _device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { FirstElement = 0, NumElements = (uint)words },
        });
        return new Buffer(buffer, view, words);
    }

    /// <summary>Each kernel is compiled once and kept until the device goes (disposing one does nothing).</summary>
    public IGpuKernel CreateKernel(string source, string entryPoint)
    {
        if (!_kernels.TryGetValue((source, entryPoint), out var kernel))
        {
            _kernels[(source, entryPoint)] = kernel = new Kernel(_device.CreateComputeShader(Bytecode(source, entryPoint)));
        }

        return kernel;
    }

    private byte[] Bytecode(string source, string entryPoint)
    {
        const string Profile = "cs_5_0";
        const ShaderFlags Flags = ShaderFlags.OptimizationLevel3;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Profile}|{Flags}|{entryPoint}|{source}")));
        var path = _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, key + ".dxbc");
        if (path is not null)
        {
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllBytes(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Compile it again.
            }
        }

        var bytecode = Compiler.Compile(source, entryPoint, "shader.hlsl", Profile, Flags).ToArray();
        if (path is not null)
        {
            try
            {
                // Written beside it and moved into place, so a second process compiling the same kernel can't half-read it.
                Directory.CreateDirectory(_cacheDirectory!);
                var temporary = path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(temporary, bytecode);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not kept; it compiles again next time.
            }
        }

        return bytecode;
    }

    private readonly Dictionary<(string, string), Kernel> _kernels = [];

    public void Write(IGpuBuffer buffer, ReadOnlySpan<uint> data, int offsetWords = 0)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var b = (Buffer)buffer;
        unsafe
        {
            fixed (uint* p = data)
            {
                var box = new Box(offsetWords * 4, 0, 0, (offsetWords + data.Length) * 4, 1, 1);
                _context.UpdateSubresource(b.Resource, 0, box, (nint)p, 0, 0);
            }
        }
    }

    public void Read(IGpuBuffer buffer, Span<uint> data, int offsetWords = 0)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (_staging is null || _stagingWords < data.Length)
        {
            _staging?.Dispose();
            _stagingWords = Math.Max(data.Length, 1 << 16);
            _staging = _device.CreateBuffer(new BufferDescription((uint)(_stagingWords * 4), BindFlags.None, ResourceUsage.Staging,
                CpuAccessFlags.Read, ResourceOptionFlags.None, 0));
        }

        var b = (Buffer)buffer;
        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, b.Resource, 0,
            new Box(offsetWords * 4, 0, 0, (offsetWords + data.Length) * 4, 1, 1));
        var mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            unsafe
            {
                new ReadOnlySpan<uint>((void*)mapped.DataPointer, data.Length).CopyTo(data);
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    public void Copy(IGpuBuffer source, IGpuBuffer destination, int words)
    {
        if (words > 0)
        {
            _context.CopySubresourceRegion(((Buffer)destination).Resource, 0, 0, 0, 0, ((Buffer)source).Resource, 0, new Box(0, 0, 0, words * 4, 1, 1));
        }
    }

    public void Dispatch(IGpuKernel kernel, ReadOnlySpan<IGpuBuffer> buffers, int groupsX, int groupsY)
    {
        var views = new ID3D11UnorderedAccessView[buffers.Length];
        for (var i = 0; i < buffers.Length; i++)
        {
            views[i] = ((Buffer)buffers[i]).View;
        }

        _context.CSSetShader(((Kernel)kernel).Shader);
        _context.CSSetUnorderedAccessViews(0, views);
        _context.Dispatch((uint)groupsX, (uint)groupsY, 1);
        _context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView[buffers.Length]);
    }

    public void Dispose()
    {
        foreach (var kernel in _kernels.Values)
        {
            kernel.Shader.Dispose();
        }

        _staging?.Dispose();
        _context.ClearState();
        _context.Dispose();
        _device.Dispose();
    }

    private sealed class Buffer(ID3D11Buffer resource, ID3D11UnorderedAccessView view, int words) : IGpuBuffer
    {
        public ID3D11Buffer Resource { get; } = resource;

        public ID3D11UnorderedAccessView View { get; } = view;

        public int Words { get; } = words;

        public void Dispose()
        {
            View.Dispose();
            Resource.Dispose();
        }
    }

    private sealed class Kernel(ID3D11ComputeShader shader) : IGpuKernel
    {
        public ID3D11ComputeShader Shader { get; } = shader;

        public void Dispose()
        {
        }
    }
}
