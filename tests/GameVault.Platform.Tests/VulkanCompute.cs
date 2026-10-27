using System.Runtime.InteropServices;
using GameVault.Platform.Video;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace GameVault.Platform.Tests;

/// <summary>
/// <see cref="IGpuCompute"/> on Vulkan, for running the GPU renderers' HLSL kernels on Linux: the HLSL is compiled to
/// SPIR-V by glslang (through shaderc) and run on a software Vulkan device, SwiftShader (as Chromium ships it) or
/// Mesa's lavapipe. Buffers are host-visible storage buffers, bound at bindings 0, 1, … as the kernels' u registers.
/// </summary>
internal sealed unsafe class VulkanCompute : IGpuCompute
{
    private readonly Vk _vk;
    private readonly Instance _instance;
    private readonly PhysicalDevice _physical;
    private readonly Device _device;
    private readonly Queue _queue;
    private readonly CommandPool _pool;
    private readonly uint _memoryType;

    private VulkanCompute(Vk vk, Instance instance, PhysicalDevice physical)
    {
        _vk = vk;
        _instance = instance;
        _physical = physical;
        uint count = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, &count, p);
        }

        var family = (uint)Array.FindIndex(families, f => (f.QueueFlags & QueueFlags.ComputeBit) != 0);
        var priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = family, QueueCount = 1, PQueuePriorities = &priority };
        var deviceInfo = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo };
        Check(vk.CreateDevice(physical, &deviceInfo, null, out _device));
        vk.GetDeviceQueue(_device, family, 0, out _queue);
        var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = family, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
        Check(vk.CreateCommandPool(_device, &poolInfo, null, out _pool));
        vk.GetPhysicalDeviceMemoryProperties(physical, out var properties);
        const MemoryPropertyFlags hostVisible = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        for (uint t = 0; t < properties.MemoryTypeCount; t++)
        {
            if ((properties.MemoryTypes[(int)t].PropertyFlags & hostVisible) == hostVisible)
            {
                _memoryType = t;
                break;
            }
        }
    }

    /// <summary>A software Vulkan device, or null (with why) when none is installed.</summary>
    public static VulkanCompute? TryCreate(out string why)
    {
        why = "";
        if (OperatingSystem.IsWindows())
        {
            why = "Windows tests use Direct3D 11";
            return null;
        }

        var icd = Environment.GetEnvironmentVariable("VK_ICD_FILENAMES") ?? FindSoftwareIcd();
        if (icd is null)
        {
            why = "no software Vulkan device (SwiftShader or lavapipe) is installed";
            return null;
        }

        // The loader reads the variable from the C environment, which Environment.SetEnvironmentVariable doesn't change.
        _ = SetEnv("VK_ICD_FILENAMES", icd, 1);
        try
        {
            var vk = Vk.GetApi();
            var name = (byte*)SilkMarshal.StringToPtr("GameVault tests");
            var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, PApplicationName = name, ApiVersion = Vk.Version11 };
            var info = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app };
            if (vk.CreateInstance(&info, null, out var instance) != Result.Success)
            {
                why = "the Vulkan instance couldn't be created";
                return null;
            }

            uint count = 0;
            vk.EnumeratePhysicalDevices(instance, &count, null);
            if (count == 0)
            {
                why = "no Vulkan device";
                return null;
            }

            var devices = new PhysicalDevice[count];
            fixed (PhysicalDevice* p = devices)
            {
                vk.EnumeratePhysicalDevices(instance, &count, p);
            }

            return new VulkanCompute(vk, instance, devices[0]);
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or InvalidOperationException)
        {
            why = ex.Message;
            return null;
        }
    }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnv(string name, string value, int overwrite);

    private static string? FindSoftwareIcd()
    {
        foreach (var root in new[] { "/opt/pw-browsers" })
        {
            if (Directory.Exists(root))
            {
                var found = Directory.EnumerateFiles(root, "vk_swiftshader_icd.json", SearchOption.AllDirectories).FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
        }

        foreach (var dir in new[] { "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d" })
        {
            if (Directory.Exists(dir))
            {
                var found = Directory.EnumerateFiles(dir, "lvp_icd*.json").FirstOrDefault();
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    public IGpuBuffer CreateBuffer(int words)
    {
        words = Math.Max(words, 4);
        var size = (ulong)words * 4;
        var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = BufferUsageFlags.StorageBufferBit, SharingMode = SharingMode.Exclusive };
        Check(_vk.CreateBuffer(_device, &info, null, out var buffer));
        _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
        var allocate = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size, MemoryTypeIndex = _memoryType };
        Check(_vk.AllocateMemory(_device, &allocate, null, out var memory));
        Check(_vk.BindBufferMemory(_device, buffer, memory, 0));
        void* map;
        Check(_vk.MapMemory(_device, memory, 0, size, 0, &map));
        new Span<uint>(map, words).Clear();
        return new Storage(this, buffer, memory, (uint*)map, words);
    }

    /// <summary>Each kernel is built once (SwiftShader takes a while) and kept until the device goes.</summary>
    public IGpuKernel CreateKernel(string source, string entryPoint)
    {
        if (!_kernels.TryGetValue((source, entryPoint), out var kernel))
        {
            _kernels[(source, entryPoint)] = kernel = Compile(source, entryPoint);
        }

        return kernel;
    }

    private readonly Dictionary<(string, string), Kernel> _kernels = [];

    private Kernel Compile(string source, string entryPoint)
    {
        var shaderc = new Shaderc(Shaderc.CreateDefaultContext([ShadercLibrary()]));
        var compiler = shaderc.CompilerInitialize();
        var options = shaderc.CompileOptionsInitialize();
        shaderc.CompileOptionsSetSourceLanguage(options, SourceLanguage.Hlsl);
        shaderc.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan11);
        shaderc.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
        var result = shaderc.CompileIntoSpv(compiler, source, (nuint)System.Text.Encoding.UTF8.GetByteCount(source), ShaderKind.ComputeShader,
            "shader.hlsl", entryPoint, options);
        try
        {
            if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
            {
                throw new InvalidOperationException(shaderc.ResultGetErrorMessageS(result));
            }

            var length = (int)shaderc.ResultGetLength(result);
            var spirv = new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), length).ToArray();
            return CreatePipeline(spirv, entryPoint);
        }
        finally
        {
            shaderc.ResultRelease(result);
            shaderc.CompileOptionsRelease(options);
            shaderc.CompilerRelease(compiler);
        }
    }

    private static string ShadercLibrary()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libshaderc_shared.so");
        return File.Exists(path) ? path : "libshaderc_shared.so";
    }

    private Kernel CreatePipeline(byte[] spirv, string entryPoint)
    {
        const int bindings = 8;
        var layoutBindings = new DescriptorSetLayoutBinding[bindings];
        for (var i = 0; i < bindings; i++)
        {
            layoutBindings[i] = new DescriptorSetLayoutBinding { Binding = (uint)i, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
        }

        DescriptorSetLayout setLayout;
        fixed (DescriptorSetLayoutBinding* b = layoutBindings)
        {
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = bindings, PBindings = b };
            Check(_vk.CreateDescriptorSetLayout(_device, &info, null, out setLayout));
        }

        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        Check(_vk.CreatePipelineLayout(_device, &layoutInfo, null, out var pipelineLayout));
        ShaderModule module;
        fixed (byte* code = spirv)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)code };
            Check(_vk.CreateShaderModule(_device, &info, null, out module));
        }

        var name = (byte*)SilkMarshal.StringToPtr(entryPoint);
        var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.ComputeBit, Module = module, PName = name };
        var pipelineInfo = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = pipelineLayout };
        Check(_vk.CreateComputePipelines(_device, default, 1, &pipelineInfo, null, out var pipeline));
        SilkMarshal.Free((nint)name);
        _vk.DestroyShaderModule(_device, module, null);
        return new Kernel(this, setLayout, pipelineLayout, pipeline);
    }

    public void Write(IGpuBuffer buffer, ReadOnlySpan<uint> data, int offsetWords = 0)
    {
        var storage = (Storage)buffer;
        data.CopyTo(new Span<uint>(storage.Map + offsetWords, storage.Words - offsetWords));
    }

    public void Read(IGpuBuffer buffer, Span<uint> data, int offsetWords = 0)
    {
        var storage = (Storage)buffer;
        new ReadOnlySpan<uint>(storage.Map + offsetWords, data.Length).CopyTo(data);
    }

    // Dispatches wait for the queue, and the buffers are host-visible, so a copy is a copy of the mapped words.
    public void Copy(IGpuBuffer source, IGpuBuffer destination, int words) =>
        new ReadOnlySpan<uint>(((Storage)source).Map, words).CopyTo(new Span<uint>(((Storage)destination).Map, words));

    public void Dispatch(IGpuKernel kernel, ReadOnlySpan<IGpuBuffer> buffers, int groupsX, int groupsY)
    {
        var k = (Kernel)kernel;
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 8 };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &poolSize };
        Check(_vk.CreateDescriptorPool(_device, &poolInfo, null, out var descriptorPool));
        var layout = k.SetLayout;
        var allocate = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = descriptorPool, DescriptorSetCount = 1, PSetLayouts = &layout };
        Check(_vk.AllocateDescriptorSets(_device, &allocate, out var set));
        var infos = stackalloc DescriptorBufferInfo[8];
        var writes = stackalloc WriteDescriptorSet[8];
        for (var i = 0; i < 8; i++)
        {
            // Unused bindings get the first buffer, so every binding the layout declares is valid.
            var storage = (Storage)buffers[i < buffers.Length ? i : 0];
            infos[i] = new DescriptorBufferInfo { Buffer = storage.Buffer, Offset = 0, Range = Vk.WholeSize };
            writes[i] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = infos + i };
        }

        _vk.UpdateDescriptorSets(_device, 8, writes, 0, null);
        var commandInfo = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = _pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Check(_vk.AllocateCommandBuffers(_device, &commandInfo, out var command));
        var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        _vk.BeginCommandBuffer(command, &begin);
        _vk.CmdBindPipeline(command, PipelineBindPoint.Compute, k.Pipeline);
        _vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, k.PipelineLayout, 0, 1, &set, 0, null);
        _vk.CmdDispatch(command, (uint)groupsX, (uint)groupsY, 1);
        _vk.EndCommandBuffer(command);
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &command };
        Check(_vk.QueueSubmit(_queue, 1, &submit, default));
        Check(_vk.QueueWaitIdle(_queue));
        _vk.FreeCommandBuffers(_device, _pool, 1, &command);
        _vk.DestroyDescriptorPool(_device, descriptorPool, null);
    }

    private static void Check(Result result)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"Vulkan: {result}");
        }
    }

    public void Dispose()
    {
        foreach (var kernel in _kernels.Values)
        {
            kernel.Release();
        }

        _vk.DestroyCommandPool(_device, _pool, null);
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);
    }

    private sealed class Storage(VulkanCompute owner, VkBuffer buffer, DeviceMemory memory, uint* map, int words) : IGpuBuffer
    {
        public VkBuffer Buffer { get; } = buffer;

        public uint* Map { get; } = map;

        public int Words { get; } = words;

        public void Dispose()
        {
            owner._vk.UnmapMemory(owner._device, memory);
            owner._vk.DestroyBuffer(owner._device, Buffer, null);
            owner._vk.FreeMemory(owner._device, memory, null);
        }
    }

    private sealed class Kernel(VulkanCompute owner, DescriptorSetLayout setLayout, PipelineLayout pipelineLayout, Pipeline pipeline) : IGpuKernel
    {
        public DescriptorSetLayout SetLayout { get; } = setLayout;

        public PipelineLayout PipelineLayout { get; } = pipelineLayout;

        public Pipeline Pipeline { get; } = pipeline;

        public void Dispose()
        {
        }

        public void Release()
        {
            owner._vk.DestroyPipeline(owner._device, Pipeline, null);
            owner._vk.DestroyPipelineLayout(owner._device, PipelineLayout, null);
            owner._vk.DestroyDescriptorSetLayout(owner._device, SetLayout, null);
        }
    }
}
