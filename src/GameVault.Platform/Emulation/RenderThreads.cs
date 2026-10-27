using GameVault.Core;

namespace GameVault.Platform.Emulation;

/// <summary>
/// The cores a console's rasterizer may draw on (the PS2's GS): a few background threads that join the emulation
/// thread for each batch, and wait (briefly spinning, then sleeping) in between. The emulation thread does its share
/// and returns once every piece is done, so the core sees an ordinary call. The pictures don't depend on the threads
/// (see <see cref="IParallelWork"/>).
/// </summary>
public sealed class RenderThreads : IParallelWork, IDisposable
{
    private static readonly Lazy<RenderThreads> s_shared = new(() => new RenderThreads(DefaultConcurrency()));

    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();
    private Job? _job;
    private volatile bool _stopping;

    public RenderThreads(int concurrency)
    {
        Concurrency = Math.Max(1, concurrency);
        _threads = new Thread[Concurrency - 1];
        for (var i = 0; i < _threads.Length; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Name = $"GameVault render {i + 1}" };
            _threads[i].Start();
        }
    }

    /// <summary>One set for the whole app (one game runs at a time; a second caller waits its turn).</summary>
    public static RenderThreads Shared => s_shared.Value;

    public int Concurrency { get; }

    /// <summary>The processors less one (the UI and audio keep one), at most 8.</summary>
    public static int DefaultConcurrency() => Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    public void For(int count, Action<int> work)
    {
        if (count <= 0)
        {
            return;
        }

        if (count == 1 || _threads.Length == 0)
        {
            for (var i = 0; i < count; i++)
            {
                work(i);
            }

            return;
        }

        lock (_gate)
        {
            var job = new Job(work, count);
            Volatile.Write(ref _job, job);
            _wake.Release(Math.Min(count - 1, _threads.Length));
            job.Run();
            var spin = default(SpinWait);
            while (Volatile.Read(ref job.Done) < count)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }

            if (job.Error is { } error)
            {
                throw new AggregateException("A render thread failed.", error);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (_threads.Length > 0)
        {
            _wake.Release(_threads.Length);
        }

        foreach (var thread in _threads)
        {
            thread.Join();
        }

        _wake.Dispose();
    }

    private void Work()
    {
        while (true)
        {
            _wake.Wait();
            if (_stopping)
            {
                return;
            }

            // A late wake-up may find the next job, or a finished one: either way it only takes pieces still left.
            Volatile.Read(ref _job)?.Run();
        }
    }

    /// <summary>One call's pieces, handed out one at a time.</summary>
    private sealed class Job(Action<int> work, int count)
    {
        public int Next = -1;
        public int Done;
        public Exception? Error;

        public void Run()
        {
            int index;
            while ((index = Interlocked.Increment(ref Next)) < count)
            {
                try
                {
                    work(index);
                }
                catch (Exception ex)
                {
                    Error ??= ex;
                }
                finally
                {
                    Interlocked.Increment(ref Done);
                }
            }
        }
    }
}
