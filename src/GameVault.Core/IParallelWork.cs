namespace GameVault.Core;

/// <summary>
/// Runs pieces of work that don't depend on each other, perhaps at once. The core has no threads of its own (it must
/// stay deterministic); the platform layer may hand a component one of these so its rasterizer can draw on several
/// cores. Whatever the order or overlap the pieces run in, the results are the same: the component only splits work
/// that touches separate memory.
/// </summary>
public interface IParallelWork
{
    /// <summary>How many pieces are worth splitting work into (the cores available), at least 1.</summary>
    int Concurrency { get; }

    /// <summary>Runs <paramref name="work"/> for 0 … <paramref name="count"/> − 1 and returns when every call has.</summary>
    void For(int count, Action<int> work);
}
