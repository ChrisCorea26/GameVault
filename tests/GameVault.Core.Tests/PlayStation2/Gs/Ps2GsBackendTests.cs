using FluentAssertions;
using GameVault.Core.PlayStation2;
using GameVault.Core.State;
using static GameVault.Core.Tests.PlayStation2.Gs.GsRegs;

namespace GameVault.Core.Tests.PlayStation2.Gs;

/// <summary>
/// The GS's tap for a GPU renderer (<see cref="IPs2GsBackend"/>, docs/plans/gpu-renderers.md, milestone 3): each batch
/// before the GS draws it, with the pages it draws to and reads; the pages it drew after; resets and state loads.
/// </summary>
public sealed class Ps2GsBackendTests
{
    private sealed class Recording : IPs2GsBackend
    {
        public List<(int Commands, int States, int FirstWritten, bool MemoryUntouched)> Batches { get; } = [];

        public List<int> DrawnPages { get; } = [];

        public int Resyncs { get; private set; }

        public Ps2Gs? Gs { get; set; }

        public void Draw(ReadOnlySpan<Ps2Gs.Command> commands, ReadOnlySpan<Ps2Gs.DrawState> states, ReadOnlySpan<ulong> writes, ReadOnlySpan<ulong> reads) =>
            Batches.Add((commands.Length, states.Length, FirstPage(writes), Gs!.Memory.AsSpan(7 * 8192, 8192).IndexOfAnyExcept((byte)0) < 0));

        public void Drawn(ReadOnlySpan<ulong> writes) => DrawnPages.Add(FirstPage(writes));

        public void Resync() => Resyncs++;

        private static int FirstPage(ReadOnlySpan<ulong> pages)
        {
            for (var i = 0; i < pages.Length; i++)
            {
                if (pages[i] != 0)
                {
                    return (i * 64) + System.Numerics.BitOperations.TrailingZeroCount(pages[i]);
                }
            }

            return -1;
        }
    }

    [Fact]
    public void A_backend_sees_each_batch_before_it_is_drawn_and_the_pages_after_and_state_loads()
    {
        var fixture = new GsFixture();
        var recording = new Recording { Gs = fixture.Gs };
        fixture.Gs.Backend = recording;
        fixture.W(Ps2Gs.RegFrame1, Frame(7, GsFixture.Fbw, Ps2Gs.PsmCt32));
        fixture.W(Ps2Gs.RegTest1, Test(zte: false));
        fixture.Sprite(0, 0, 16, 16, 255, 0, 0);
        fixture.Sprite(4, 4, 8, 8, 0, 255, 0);
        _ = fixture.Gs.LocalMemory; // draws the batch

        recording.Batches.Should().ContainSingle();
        recording.Batches[0].Commands.Should().Be(2);
        recording.Batches[0].FirstWritten.Should().Be(7, "the frame is at page 7");
        recording.Batches[0].MemoryUntouched.Should().BeTrue("the backend sees the batch before the GS draws it");
        fixture.Gs.Memory.AsSpan(7 * 8192, 8192).IndexOfAnyExcept((byte)0).Should().BeGreaterThanOrEqualTo(0);
        recording.DrawnPages.Should().Equal(7);

        var state = new StateWriter();
        fixture.Gs.SaveState(state);
        fixture.Gs.LoadState(new StateReader(state.ToArray()));
        fixture.Gs.Reset();
        recording.Resyncs.Should().Be(2);
    }
}
