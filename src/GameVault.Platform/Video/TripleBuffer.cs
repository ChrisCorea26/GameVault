namespace GameVault.Platform.Video;

/// <summary>
/// Lock-free triple buffer (SPEC §5.1). The writer fills <see cref="BackBuffer"/> and calls
/// <see cref="Publish"/>. The reader calls <see cref="TryAcquire"/> to get the newest published buffer.
/// Neither side ever blocks or sees a half-written buffer; frames the reader skips are simply dropped.
/// </summary>
public sealed class TripleBuffer<T>
{
    private const int FreshBit = 4;
    private const int IndexMask = 3;

    private readonly T[] _buffers;
    private int _back;
    private int _shared = 1;
    private int _front = 2;

    public TripleBuffer(Func<T> create)
    {
        _buffers = [create(), create(), create()];
    }

    /// <summary>The buffer the writer may fill. Owned by the writer until <see cref="Publish"/>.</summary>
    public T BackBuffer => _buffers[_back];

    /// <summary>The buffer the reader last acquired. Owned by the reader until its next successful acquire.</summary>
    public T FrontBuffer => _buffers[_front];

    /// <summary>Hands the back buffer to the reader and takes the old shared one as the new back buffer.</summary>
    public void Publish() => _back = Interlocked.Exchange(ref _shared, _back | FreshBit) & IndexMask;

    /// <summary>Takes the newest published buffer if there is one the reader hasn't seen.</summary>
    public bool TryAcquire(out T buffer)
    {
        if ((Volatile.Read(ref _shared) & FreshBit) == 0)
        {
            buffer = _buffers[_front];
            return false;
        }

        _front = Interlocked.Exchange(ref _shared, _front) & IndexMask;
        buffer = _buffers[_front];
        return true;
    }
}
