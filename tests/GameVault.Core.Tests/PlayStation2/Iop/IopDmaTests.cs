using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;

namespace GameVault.Core.Tests.PlayStation2.Iop;

/// <summary>An SPU2 stand-in that records what the IOP sends it.</summary>
internal sealed class FakeSpu2 : IPs2Spu2
{
    public readonly List<(uint Offset, ushort Value)> Writes = [];
    public readonly List<ushort> DmaData = [];
    public int Ticks;
    public int TakeLimit = int.MaxValue;

    public ushort Read16(uint offset) => (ushort)(0x5000 | offset);

    public void Write16(uint offset, ushort value) => Writes.Add((offset, value));

    public int DmaWrite(int core, ReadOnlySpan<ushort> data)
    {
        var count = Math.Min(TakeLimit, data.Length);
        foreach (var h in data[..count])
        {
            DmaData.Add(h);
        }

        return count;
    }

    public int DmaRead(int core, Span<ushort> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (ushort)(0xA000 + i);
        }

        return data.Length;
    }

    public void Tick() => Ticks++;

    public Action<short, short>? SampleReady { get; set; }

    public Action? Irq { get; set; }

    public void Reset()
    {
    }

    public void SaveState(StateWriter writer) => writer.WriteInt32(Ticks);

    public void LoadState(StateReader reader) => Ticks = reader.ReadInt32();
}

/// <summary>The IOP's DMA channels: the SIF FIFOs (SIF0's tag mode, SIF1's chain), the SPU2 and the interrupts.</summary>
public sealed class IopDmaTests
{
    private const uint Dicr = 0x1F8010F4;
    private const uint Dpcr2 = 0x1F801570;
    private const uint Dicr2 = 0x1F801574;

    private readonly FakeSpu2 _spu = new();
    private readonly IopHarness _h;

    public IopDmaTests()
    {
        _h = new IopHarness(_spu);
    }

    private Ps2Iop Iop => _h.Iop;

    private static uint ChannelBase(int channel) => channel < 7 ? 0x1F801080u + (uint)channel * 0x10 : 0x1F801500u + (uint)(channel - 7) * 0x10;

    private void Enable(int channel)
    {
        if (channel < 7)
        {
            Iop.Write32(0x1F8010F0, Iop.Read32(0x1F8010F0) | (8u << (channel * 4)));
        }
        else
        {
            Iop.Write32(Dpcr2, Iop.Read32(Dpcr2) | (8u << ((channel - 7) * 4)));
        }
    }

    private void Start(int channel, uint madr, uint bcr, uint chcr, uint tadr = 0)
    {
        var b = ChannelBase(channel);
        Iop.Write32(b, madr);
        Iop.Write32(b + 4, bcr);
        Iop.Write32(b + 12, tadr);
        Iop.Write32(b + 8, chcr);
    }

    private uint Chcr(int channel) => Iop.Read32(ChannelBase(channel) + 8);

    private List<uint> DrainSif0()
    {
        var words = new List<uint>();
        while (_h.Sif.Sif0.TryPop(out uint word))
        {
            words.Add(word);
        }

        return words;
    }

    [Fact]
    public void Sif0_tag_mode_sends_the_ee_tag_then_the_data_padded_to_quadwords()
    {
        // IOP tag at $10000: data at $20000, 6 words, end; the EE's tag after it; then the next tag's words.
        Iop.Write32(0x10000, 0x80020000);
        Iop.Write32(0x10004, 6);
        Iop.Write32(0x10008, 0x70000002); // the EE's destination tag (low word)
        Iop.Write32(0x1000C, 0x00123450); // (high word: its address)
        Iop.Write32(0x10010, 0xDEAD0001); // the next IOP tag, sent as padding
        Iop.Write32(0x10014, 0xDEAD0002);
        for (uint i = 0; i < 6; i++)
        {
            Iop.Write32(0x20000 + i * 4, 0x100 + i);
        }

        Iop.Write32(Dicr, 0x00800000); // master enable
        Iop.Write32(Dicr2, 1u << 18); // channel 9's mask
        Enable(9);
        Start(9, 0, 0, 0x01000701, tadr: 0x10000);
        _h.Run(100);

        DrainSif0().Should().Equal(
            0x70000002u, 0x00123450u, 0xDEAD0001u, 0xDEAD0002u,
            0x100u, 0x101u, 0x102u, 0x103u, 0x104u, 0x105u, 0u, 0u);
        (Chcr(9) & 0x01000000).Should().Be(0, "the channel finished");
        (Iop.Read32(Dicr2) & (1u << 26)).Should().NotBe(0, "channel 9's flag");
        (Iop.Read32(Dicr) & 0x80000000).Should().NotBe(0, "the master flag");
        (Iop.InterruptStatus & (1u << 3)).Should().NotBe(0, "IRQ 3");
        Iop.Read32(ChannelBase(9) + 12).Should().Be(0x10010u, "TADR moved past the tag and the EE's tag");
    }

    [Fact]
    public void Sif0_waits_for_the_ee_to_drain_a_full_fifo_and_follows_a_chain_of_tags()
    {
        // Two tags: 200 words (not the end), then 3 words with the end bit.
        Iop.Write32(0x10000, 0x00020000);
        Iop.Write32(0x10004, 200);
        Iop.Write32(0x10010, 0x80030000);
        Iop.Write32(0x10014, 3);
        for (uint i = 0; i < 200; i++)
        {
            Iop.Write32(0x20000 + i * 4, i);
        }

        for (uint i = 0; i < 3; i++)
        {
            Iop.Write32(0x30000 + i * 4, 0x9000 + i);
        }

        Enable(9);
        Start(9, 0, 0, 0x01000701, tadr: 0x10000);
        _h.Run(100);
        _h.Sif.Sif0.Free.Should().Be(0);
        (Chcr(9) & 0x01000000).Should().NotBe(0, "still waiting for room");

        var received = new List<uint>();
        for (var round = 0; round < 10 && (Chcr(9) & 0x01000000) != 0; round++)
        {
            received.AddRange(DrainSif0());
            Iop.ServiceSif();
            _h.Run(10);
        }

        received.AddRange(DrainSif0());
        (Chcr(9) & 0x01000000).Should().Be(0);
        received.Should().HaveCount(4 + 200 + 4 + 3 + 1);
        received.Skip(4).Take(200).Should().Equal(Enumerable.Range(0, 200).Select(i => (uint)i));
        received.Skip(208).Take(3).Should().Equal(0x9000u, 0x9001u, 0x9002u);
    }

    [Fact]
    public void Sif1_reads_iop_tags_and_their_data_from_the_fifo()
    {
        Enable(10);
        Start(10, 0, 0, 0x01000201);
        _h.Run(50);
        (Chcr(10) & 0x01000000).Should().NotBe(0, "waiting for the EE");

        // A tag for 4 words at $30000 (not the end), then one for 4 at $31000 with the end bit.
        uint[] words =
        [
            0x00030000, 4, 0, 0, 0xA0, 0xA1, 0xA2, 0xA3,
            0x80031000, 4, 0, 0, 0xB0, 0xB1, 0xB2, 0xB3,
        ];
        foreach (var w in words)
        {
            _h.Sif.Sif1.TryPush(w).Should().BeTrue();
        }

        Iop.ServiceSif();
        _h.Run(50);
        (Chcr(10) & 0x01000000).Should().Be(0);
        Enumerable.Range(0, 4).Select(i => Iop.Read32(0x30000 + (uint)i * 4)).Should().Equal(0xA0u, 0xA1u, 0xA2u, 0xA3u);
        Enumerable.Range(0, 4).Select(i => Iop.Read32(0x31000 + (uint)i * 4)).Should().Equal(0xB0u, 0xB1u, 0xB2u, 0xB3u);
        (Iop.Read32(Dicr2) & (1u << 27)).Should().NotBe(0, "SIF1's flag is always raised");
    }

    [Fact]
    public void A_channel_waits_for_its_dpcr_enable_bit()
    {
        Iop.Write32(0x1F80157C, 0);
        _h.Sif.Sif1.TryPush(0x80030000);
        _h.Sif.Sif1.TryPush(0);
        _h.Sif.Sif1.TryPush(0);
        _h.Sif.Sif1.TryPush(0);
        Start(10, 0, 0, 0x01000201);
        _h.Run(50);
        (Chcr(10) & 0x01000000).Should().NotBe(0);
        _h.Sif.Sif1.Count.Should().Be(4);
        Enable(10);
        _h.Run(50);
        (Chcr(10) & 0x01000000).Should().Be(0);
    }

    [Fact]
    public void Dicr_flags_clear_on_writing_one_and_irq_3_follows_the_master_flag()
    {
        Iop.Write32(Dicr, 0x00800000 | (1u << (16 + 6)));
        Enable(6);
        Start(6, 0x1000, 4, 0x11000002);
        _h.Run(50);
        (Iop.Read32(Dicr) & (1u << 30)).Should().NotBe(0);
        (Iop.InterruptStatus & 8).Should().NotBe(0);
        Iop.Read32(0x1000).Should().Be(0xFFCu, "OTC built the table backwards");
        Iop.Read32(0x1000 - 12).Should().Be(0xFFFFFFu);

        Iop.Write32(Dicr, 0x00800000 | (1u << (16 + 6)) | (1u << 30));
        (Iop.Read32(Dicr) & 0xC0000000).Should().Be(0);
    }

    [Fact]
    public void Spu2_registers_dma_and_sample_clock_are_routed()
    {
        Iop.Write16(0x1F90019A, 0xC000);
        Iop.Write32(0x1F900760, 0x22221111);
        _spu.Writes.Should().Equal((0x19Au, (ushort)0xC000), (0x760u, (ushort)0x1111), (0x762u, (ushort)0x2222));
        Iop.Read16(0x1F900344).Should().Be(0x5344);

        _h.Run(IPs2Spu2.CyclesPerSample * 10);
        _spu.Ticks.Should().Be(10);

        // Core 1 (channel 7): 2 blocks of 4 words = 16 halfwords, taken 5 at a time as the SPU2 frees room.
        for (uint i = 0; i < 8; i++)
        {
            Iop.Write32(0x40000 + i * 4, ((i * 2 + 1) << 16) | (i * 2));
        }

        _spu.TakeLimit = 5;
        Enable(7);
        Start(7, 0x40000, 0x00020004, 0x01000201);
        _h.Run(IPs2Spu2.CyclesPerSample * 4);
        _spu.DmaData.Should().Equal(Enumerable.Range(0, 16).Select(i => (ushort)i));
        (Chcr(7) & 0x01000000).Should().Be(0);

        // Core 0 (channel 4) reads into RAM.
        Enable(4);
        Start(4, 0x50000, 0x00010002, 0x01000200);
        _h.Run(10);
        Iop.Read32(0x50000).Should().Be(0xA001A000u);

        _spu.Irq!.Invoke();
        (Iop.InterruptStatus & (1u << 9)).Should().NotBe(0);
    }
}
