namespace GameVault.Platform.Tests;

/// <summary>
/// Runs a test's setup without xUnit's synchronization context, so services capture none and raise their events
/// directly instead of posting them. xUnit's context runs only a few posts at a time, and on a busy CI runner they
/// can wait behind other tests for longer than a test's deadline.
/// </summary>
internal readonly struct NoSynchronizationContext : IDisposable
{
    private readonly SynchronizationContext? _previous;

    private NoSynchronizationContext(SynchronizationContext? previous) => _previous = previous;

    public static NoSynchronizationContext Enter()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        return new NoSynchronizationContext(previous);
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previous);
}
