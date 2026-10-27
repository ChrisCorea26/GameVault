namespace GameVault.Platform.Video;

/// <summary>
/// The little a compute-shader renderer needs from a GPU: buffers of 32-bit words, kernels compiled from HLSL, and a
/// dispatch with the buffers bound at u0, u1, … in order (docs/plans/gpu-renderers.md). Direct3D 11 implements it in
/// the app; the tests implement it on a Vulkan software device, so the same kernels run on Linux.
/// </summary>
public interface IGpuCompute : IDisposable
{
    IGpuBuffer CreateBuffer(int words);

    /// <summary>
    /// Compiles <paramref name="source"/>'s <paramref name="entryPoint"/> (a compute shader). It may hand back a kernel it
    /// built before for the same source; disposing that is harmless (the device owns it).
    /// </summary>
    IGpuKernel CreateKernel(string source, string entryPoint);

    void Write(IGpuBuffer buffer, ReadOnlySpan<uint> data, int offsetWords = 0);

    /// <summary>Reads words back; it waits for the GPU's work on the buffer to finish.</summary>
    void Read(IGpuBuffer buffer, Span<uint> data, int offsetWords = 0);

    /// <summary>Copies the first <paramref name="words"/> words of one buffer into another, on the GPU.</summary>
    void Copy(IGpuBuffer source, IGpuBuffer destination, int words);

    void Dispatch(IGpuKernel kernel, ReadOnlySpan<IGpuBuffer> buffers, int groupsX, int groupsY);
}

public interface IGpuBuffer : IDisposable
{
    int Words { get; }
}

public interface IGpuKernel : IDisposable;
