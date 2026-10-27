namespace GameVault.Core.PlayStation2;

/// <summary>
/// Something that draws the GS's batches again, at its own resolution (docs/plans/gpu-renderers.md, milestone 3: the
/// GPU renderer in GameVault.Platform). The GS stays authoritative for local memory; with no backend attached nothing
/// changes. The page sets are bits, one per 8 KB page of local memory.
/// </summary>
internal interface IPs2GsBackend
{
    /// <summary>
    /// A batch is about to be drawn into local memory: its primitives in order, the states they were queued with, and
    /// the pages it draws to and reads textures from. Local memory still holds what was there before the batch.
    /// </summary>
    void Draw(ReadOnlySpan<Ps2Gs.Command> commands, ReadOnlySpan<Ps2Gs.DrawState> states, ReadOnlySpan<ulong> writes, ReadOnlySpan<ulong> reads);

    /// <summary>The batch has been drawn: local memory holds the pages it drew to as the GS left them.</summary>
    void Drawn(ReadOnlySpan<ulong> writes);

    /// <summary>Local memory was replaced (a reset or a state load): nothing drawn before can be relied on.</summary>
    void Resync();
}
