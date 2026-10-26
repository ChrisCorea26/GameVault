using System.Buffers.Binary;
using GameVault.Core.PlayStation2;

namespace GameVault.Core.Tests.PlayStation2.Dma;

/// <summary>A vector unit that only has memories and a running flag the test controls.</summary>
internal sealed class FakeVu(int index) : IPs2VectorUnit
{
    public int Index { get; } = index;

    public byte[] MicroMemory { get; } = new byte[index == 0 ? 4096 : 16384];

    public byte[] DataMemory { get; } = new byte[index == 0 ? 4096 : 16384];

    public bool Running { get; set; }

    public List<uint> Starts { get; } = [];

    public int Continues { get; private set; }

    public void StartMicro(uint address)
    {
        Starts.Add(address);
        Running = StayRunning;
    }

    public void ContinueMicro()
    {
        Continues++;
        Running = StayRunning;
    }

    /// <summary>Whether a started program keeps running until the test stops it.</summary>
    public bool StayRunning { get; set; }

    public long Run(long cycles) => 0;

    public uint Word(int byteAddress) => BinaryPrimitives.ReadUInt32LittleEndian(DataMemory.AsSpan(byteAddress, 4));

    public uint[] Vector(int quadword)
    {
        var v = new uint[4];
        for (var i = 0; i < 4; i++)
        {
            v[i] = Word((quadword * 16) + (i * 4));
        }

        return v;
    }
}

/// <summary>A GIF that records what each path sent and can refuse data.</summary>
internal sealed class FakeGif : IPs2Gif
{
    public List<UInt128> Path2 { get; } = [];

    public List<UInt128> Path3 { get; } = [];

    public Queue<UInt128> Download { get; } = new();

    /// <summary>How many more quadwords PATH2/PATH3 accept (int.MaxValue = all).</summary>
    public int Path2Room { get; set; } = int.MaxValue;

    public int Path3Room { get; set; } = int.MaxValue;

    public bool Path3Masked { get; set; }

    public bool Path2Active { get; set; }

    public bool Path3Active { get; set; }

    public void KickPath1(byte[] vu1DataMemory, uint address)
    {
    }

    public int WritePath2(ReadOnlySpan<UInt128> data)
    {
        var n = Math.Min(data.Length, Path2Room);
        for (var i = 0; i < n; i++)
        {
            Path2.Add(data[i]);
        }

        if (Path2Room != int.MaxValue)
        {
            Path2Room -= n;
        }

        return n;
    }

    public int WritePath3(ReadOnlySpan<UInt128> data)
    {
        if (Path3Masked)
        {
            return 0;
        }

        var n = Math.Min(data.Length, Path3Room);
        for (var i = 0; i < n; i++)
        {
            Path3.Add(data[i]);
        }

        if (Path3Room != int.MaxValue)
        {
            Path3Room -= n;
        }

        return n;
    }

    public int ReadDownload(Span<UInt128> destination)
    {
        var n = 0;
        while (n < destination.Length && Download.TryDequeue(out var q))
        {
            destination[n++] = q;
        }

        return n;
    }
}

/// <summary>An IPU whose FIFOs are lists.</summary>
internal sealed class FakeIpu : IPs2DmaIpu
{
    public List<UInt128> In { get; } = [];

    public Queue<UInt128> Out { get; } = new();

    public int WriteIn(ReadOnlySpan<UInt128> data)
    {
        foreach (var q in data)
        {
            In.Add(q);
        }

        return data.Length;
    }

    public int ReadOut(Span<UInt128> destination)
    {
        var n = 0;
        while (n < destination.Length && Out.TryDequeue(out var q))
        {
            destination[n++] = q;
        }

        return n;
    }
}

/// <summary>
/// The EE's DMA side wired together with fakes for the VUs and the GIF: RDRAM, the scratchpad, the DMAC, both VIFs, the
/// SIF and the INTC, with helpers that write registers by address as EE code does.
/// </summary>
internal sealed class Ps2DmaRig
{
    public const int RamSize = 4 * 1024 * 1024;

    private uint _nextAlloc = 0x0010_0000;

    public Ps2DmaRig()
    {
        Intc = new Ps2Intc();
        Gif = new FakeGif();
        Vu0 = new FakeVu(0);
        Vu1 = new FakeVu(1);
        Vif0 = new Ps2Vif(0, Vu0, Intc);
        Vif1 = new Ps2Vif(1, Vu1, Intc, Gif);
        Sif = new Ps2Sif();
        Dmac = new Ps2Dmac(Ram, Scratchpad, Vif0, Vif1, Gif, Sif, Intc);

        // The kernel enables the DMAC at boot (D_CTRL.DMAE), before any test program runs.
        Dmac.WriteRegister(0x1000E000, 1);
    }

    public byte[] Ram { get; } = new byte[RamSize];

    public byte[] Scratchpad { get; } = new byte[Ps2Dmac.ScratchpadSize];

    public Ps2Intc Intc { get; }

    public FakeGif Gif { get; }

    public FakeVu Vu0 { get; }

    public FakeVu Vu1 { get; }

    public Ps2Vif Vif0 { get; }

    public Ps2Vif Vif1 { get; }

    public Ps2Sif Sif { get; }

    public Ps2Dmac Dmac { get; }

    public static uint ChannelBase(int ch) => 0x10000000 + ch switch
    {
        0 => 0x8000u,
        1 => 0x9000u,
        2 => 0xA000u,
        3 => 0xB000u,
        4 => 0xB400u,
        5 => 0xC000u,
        6 => 0xC400u,
        7 => 0xC800u,
        8 => 0xD000u,
        _ => 0xD400u,
    };

    /// <summary>16-byte aligned RAM, like memalign(16, size).</summary>
    public uint Alloc(int size)
    {
        var address = _nextAlloc;
        _nextAlloc += (uint)((size + 15) & ~15);
        return address;
    }

    public uint Read32(uint address) => BinaryPrimitives.ReadUInt32LittleEndian(Ram.AsSpan((int)address, 4));

    public void Write32(uint address, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Ram.AsSpan((int)address, 4), value);

    public void Fill(uint address, int size, byte value) => Ram.AsSpan((int)address, size).Fill(value);

    public uint Reg(uint address) => Dmac.ReadRegister(address);

    public void SetReg(uint address, uint value) => Dmac.WriteRegister(address, value);

    public uint Chcr(int ch) => Reg(ChannelBase(ch));

    public void SetChcr(int ch, uint value) => SetReg(ChannelBase(ch), value);

    public uint Madr(int ch) => Reg(ChannelBase(ch) + 0x10);

    public uint Tadr(int ch) => Reg(ChannelBase(ch) + 0x30);

    public uint Sadr(int ch) => Reg(ChannelBase(ch) + 0x80);

    public void SetSadr(int ch, uint value) => SetReg(ChannelBase(ch) + 0x80, value);

    /// <summary>Lets the DMAC run until the channel's STR clears, as the test programs' polling loops do.</summary>
    public bool RunUntilDone(int ch)
    {
        for (var i = 0; i < 100000 && (Chcr(ch) & 0x100) != 0; i++)
        {
            Dmac.Advance(64);
        }

        return (Chcr(ch) & 0x100) == 0;
    }

    /// <summary>ps2autotests' DMA::SendSimple (tests/dma/dmasend.cpp), with its printed warnings.</summary>
    public void SendSimple(int ch, uint data, int size, Action<string>? print = null)
    {
        var b = ChannelBase(ch);
        SetReg(0x1000E000, 1);
        SetReg(b + 0x10, data);
        SetReg(b + 0x20, (uint)(size / 16));
        SetReg(b + 0x30, 0);
        if (Reg(b + 0x10) != data)
        {
            print?.Invoke($"WARNING: DMA transfer did not accept MADR: {Reg(b + 0x10) ^ data:x8}\n");
        }

        var madrStart = Reg(b + 0x10);
        SetChcr(ch, Chcr(ch) | 0x100 | 0x1);
        var done = RunUntilDone(ch);
        if (Reg(b + 0x10) != madrStart + (uint)size)
        {
            print?.Invoke($"WARNING: DMA transfer MADR did not increase as expected: {Reg(b + 0x10) - madrStart:x8}\n");
        }

        if (!done)
        {
            print?.Invoke("ERROR: DMA transfer timed out.\n");
        }
    }

    /// <summary>ps2autotests' DMA::SendChain.</summary>
    public void SendChain(int ch, uint tag, Action<string>? print = null)
    {
        var b = ChannelBase(ch);
        SetReg(b + 0x10, 0);
        SetReg(b + 0x20, 0);
        SetReg(b + 0x30, tag);
        SetChcr(ch, (Chcr(ch) & ~0xCu) | 0x4 | 0x100);
        var done = RunUntilDone(ch);
        SetChcr(ch, Chcr(ch) & ~0xCu);
        if (!done)
        {
            print?.Invoke("ERROR: DMA transfer timed out.\n");
        }
    }
}

/// <summary>ps2autotests' DMA::SrcChainPacket / DstChainPacket (tests/dma/dmatags.cpp), emitting into the rig's RAM.</summary>
internal sealed class ChainPacket(Ps2DmaRig rig, int size)
{
    public uint Start { get; } = rig.Alloc(size);

    private uint _at;

    public void Reset()
    {
        rig.Fill(Start, (int)_at, 0);
        _at = 0;
    }

    public void Tag(uint qwc, uint id, uint address, bool irq = false)
    {
        rig.Write32(Start + _at, qwc | (id << 28) | (irq ? 0x8000_0000u : 0));
        rig.Write32(Start + _at + 4, address);
        rig.Write32(Start + _at + 8, 0);
        rig.Write32(Start + _at + 12, 0);
        _at += 16;
    }

    public void Data(uint w0, uint w1 = 0, uint w2 = 0, uint w3 = 0)
    {
        rig.Write32(Start + _at, w0);
        rig.Write32(Start + _at + 4, w1);
        rig.Write32(Start + _at + 8, w2);
        rig.Write32(Start + _at + 12, w3);
        _at += 16;
    }
}

/// <summary>ps2autotests' VIF::Packet (tests/dma/vif/emit_vifcode.cpp): VIFcodes as words in the rig's RAM.</summary>
internal sealed class VifPacket(Ps2DmaRig rig, int size)
{
    public uint Start { get; } = rig.Alloc(size);

    private uint _at;

    public void Emit(uint word)
    {
        rig.Write32(Start + _at, word);
        _at += 4;
    }

    public void Nop(uint flags = 0) => Emit(flags);

    public void Stcycl(uint cl, uint wl, uint flags = 0) => Emit((0x01u << 24) | flags | (wl << 8) | cl);

    public void Stmod(uint mode) => Emit((0x05u << 24) | mode);

    public void Mskpath3(bool masked) => Emit((0x06u << 24) | (masked ? 0x8000u : 0));

    public void Mark(uint mark, uint flags = 0) => Emit((0x07u << 24) | flags | mark);

    public void Stmask(uint mask, uint flags = 0)
    {
        Emit((0x20u << 24) | flags);
        Emit(mask);
    }

    public void Strow(uint r0, uint r1, uint r2, uint r3)
    {
        Emit(0x30u << 24);
        Emit(r0);
        Emit(r1);
        Emit(r2);
        Emit(r3);
    }

    public void Stcol(uint c0, uint c1, uint c2, uint c3)
    {
        Emit(0x31u << 24);
        Emit(c0);
        Emit(c1);
        Emit(c2);
        Emit(c3);
    }

    /// <summary>UNPACK; <paramref name="type"/> is the command byte (0x60–0x6F), <paramref name="flags"/> the raw bits.</summary>
    public void Unpack(uint type, uint num, uint address, uint flags = 0) =>
        Emit((type << 24) | flags | ((num & 0xFF) << 16) | address);
}
