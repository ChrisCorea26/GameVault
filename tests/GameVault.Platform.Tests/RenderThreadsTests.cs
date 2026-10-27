using FluentAssertions;
using GameVault.Platform.Emulation;

namespace GameVault.Platform.Tests;

/// <summary>The render threads: every piece runs exactly once per call, the call waits for all of them, and failures surface.</summary>
public sealed class RenderThreadsTests
{
    [Fact]
    public void Every_piece_runs_once_and_the_call_waits_for_all_of_them()
    {
        using var threads = new RenderThreads(4);
        threads.Concurrency.Should().Be(4);
        for (var round = 0; round < 2000; round++)
        {
            var count = (round % 9) + 1;
            var runs = new int[count];
            threads.For(count, i =>
            {
                if (i % 3 == 0)
                {
                    Thread.SpinWait(200);
                }

                Interlocked.Increment(ref runs[i]);
            });

            runs.Should().AllBeEquivalentTo(1, $"round {round}");
        }
    }

    [Fact]
    public void Two_callers_take_turns()
    {
        using var threads = new RenderThreads(3);
        var total = 0;
        Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < 300; i++)
            {
                threads.For(3, _ => Interlocked.Increment(ref total));
            }
        });

        total.Should().Be(4 * 300 * 3);
    }

    [Fact]
    public void A_failing_piece_fails_the_call_after_the_others_finish()
    {
        using var threads = new RenderThreads(3);
        var finished = 0;
        var call = () => threads.For(6, i =>
        {
            if (i == 2)
            {
                throw new InvalidOperationException("boom");
            }

            Interlocked.Increment(ref finished);
        });

        call.Should().Throw<AggregateException>().WithInnerException<InvalidOperationException>();
        finished.Should().Be(5);

        // And the threads still work afterwards.
        var after = 0;
        threads.For(4, _ => Interlocked.Increment(ref after));
        after.Should().Be(4);
    }

    [Fact]
    public void One_thread_runs_everything_on_the_caller()
    {
        using var threads = new RenderThreads(1);
        var caller = Environment.CurrentManagedThreadId;
        var seen = new List<int>();
        threads.For(3, i =>
        {
            Environment.CurrentManagedThreadId.Should().Be(caller);
            seen.Add(i);
        });

        seen.Should().Equal(0, 1, 2);
    }
}
