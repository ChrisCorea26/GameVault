using System.Buffers.Binary;
using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;
using GameVault.Core.Tests.PlayStation;

namespace GameVault.Core.Tests.PlayStation2.Iop;

/// <summary>The IOP's save state: a run continued from a loaded state matches the run that never stopped.</summary>
public sealed class IopStateTests
{
    /// <summary>
    /// An IOP mid-way through everything: a counting loop with timer interrupts, a DVD read in flight, a pad in
    /// DualShock 2 mode and a memory card.
    /// </summary>
    private static IopHarness Busy()
    {
        var h = new IopHarness(new FakeSpu2());
        h.Iop.Write32(0x80, 0x08000020);
        h.Iop.Write32(0x84, Mips.Nop);
        h.Load(
        [
            .. IopHarness.Li(8, 0x1F801100),
            Mips.Ori(9, 0, 500), Mips.Sw(9, 8, 8),
            Mips.Ori(9, 0, 0x58), Mips.Sw(9, 8, 4),
            Mips.Addiu(10, 10, 1), // loop: count in $10 and in RAM
            Mips.Sw(10, 0, 0x200),
            Mips.Beq(0, 0, -3),
            Mips.Nop,
        ]);
        h.Iop.Disc = new MemoryPs2Disc(TestIso.Build());
        h.Iop.MemoryCard = Ps2MemoryCard.CreateFormatted();
        h.Iop.SetControllerState(0, GameVault.Core.Input.ControllerState.A);

        h.Iop.Write32(0x1F8010F0, 0x8000);
        h.Iop.Write32(0x1F8010B0, 0x40000);
        h.Iop.Write32(0x1F8010B4, (8u << 16) | 516);
        h.Iop.Write32(0x1F8010B8, 0x41000200);
        var p = new byte[11];
        BinaryPrimitives.WriteUInt32LittleEndian(p, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 8);
        foreach (var b in p)
        {
            h.Write8(Ps2Cdvd.Base + 5, b);
        }

        h.Write8(Ps2Cdvd.Base + 4, 0x08);
        h.Run(12_330_000); // mid-read: the seek is done, the sectors are arriving
        return h;
    }

    private static byte[] Save(Ps2Iop iop)
    {
        var writer = new StateWriter();
        iop.SaveState(writer);
        return writer.ToArray();
    }

    [Fact]
    public void A_loaded_state_continues_exactly()
    {
        var h = Busy();
        var state = Save(h.Iop);

        h.Run(500_000);
        var expected = Save(h.Iop);
        h.Iop.Read32(0x200).Should().BeGreaterThan(0u);

        var other = new IopHarness(new FakeSpu2());
        other.Iop.Disc = new MemoryPs2Disc(TestIso.Build());
        other.Iop.LoadState(new StateReader(state));
        other.Run(500_000);
        Save(other.Iop).Should().Equal(expected);
        other.Iop.Ram.AsSpan(0x40000, 2064 * 8).ToArray().Should().Equal(h.Iop.Ram.AsSpan(0x40000, 2064 * 8).ToArray());
        other.Iop.MemoryCard!.Data.Should().Equal(h.Iop.MemoryCard!.Data);
    }

    [Fact]
    public void Runs_are_deterministic()
    {
        var a = Busy();
        var b = Busy();
        a.Run(1_000_000);
        b.Run(1_000_000);
        Save(a.Iop).Should().Equal(Save(b.Iop));
    }

    [Fact]
    public void Each_component_round_trips_on_its_own()
    {
        RoundTrip(new Ps2IopTimers());
        RoundTrip(new Ps2Cdvd());
        RoundTrip(new Ps2Sio2());
        RoundTrip(new Ps2Pad());
        RoundTrip(Ps2MemoryCard.CreateFormatted());
    }

    private static void RoundTrip(IStateful component)
    {
        var writer = new StateWriter();
        component.SaveState(writer);
        var first = writer.ToArray();
        component.LoadState(new StateReader(first));
        var again = new StateWriter();
        component.SaveState(again);
        again.ToArray().Should().Equal(first);
    }
}
