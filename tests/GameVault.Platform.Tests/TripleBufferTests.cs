using GameVault.Platform.Video;

namespace GameVault.Platform.Tests;

public class TripleBufferTests
{
    [Fact]
    public void Reader_gets_nothing_until_a_frame_is_published()
    {
        var buffer = new TripleBuffer<int[]>(() => new int[1]);

        buffer.TryAcquire(out _).Should().BeFalse();
    }

    [Fact]
    public void Reader_gets_the_newest_frame_and_skips_older_ones()
    {
        var buffer = new TripleBuffer<int[]>(() => new int[1]);
        for (var i = 1; i <= 3; i++)
        {
            buffer.BackBuffer[0] = i;
            buffer.Publish();
        }

        buffer.TryAcquire(out var frame).Should().BeTrue();
        frame[0].Should().Be(3);
        buffer.TryAcquire(out _).Should().BeFalse("nothing new since the last acquire");
    }

    [Fact]
    public void Writer_never_writes_into_the_buffer_the_reader_holds()
    {
        var buffer = new TripleBuffer<int[]>(() => new int[1]);
        buffer.BackBuffer[0] = 1;
        buffer.Publish();
        buffer.TryAcquire(out var held);

        for (var i = 2; i < 10; i++)
        {
            buffer.BackBuffer.Should().NotBeSameAs(held);
            buffer.BackBuffer[0] = i;
            buffer.Publish();
        }

        held[0].Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_writer_and_reader_never_see_a_torn_frame()
    {
        var buffer = new TripleBuffer<int[]>(() => new int[64]);
        var done = false;
        var writer = Task.Run(() =>
        {
            for (var n = 1; n <= 200_000; n++)
            {
                Array.Fill(buffer.BackBuffer, n);
                buffer.Publish();
            }

            Volatile.Write(ref done, true);
        });

        var torn = 0;
        while (!Volatile.Read(ref done))
        {
            if (buffer.TryAcquire(out var frame) && frame.Any(v => v != frame[0]))
            {
                torn++;
            }
        }

        await writer;
        torn.Should().Be(0);
    }
}
