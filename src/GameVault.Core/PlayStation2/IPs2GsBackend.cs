namespace GameVault.Core.PlayStation2;

/// <summary>
/// Something that draws the GS's batches again, at its own resolution (docs/plans/gpu-renderers.md, milestone 3: the
/// GPU renderer in GameVault.Platform). With no backend attached nothing changes. The page sets are bits, one per 8 KB
/// page of local memory.
/// </summary>
/// <remarks>
/// <para>By default the GS stays authoritative for local memory: it draws every batch itself as well.</para>
/// <para>
/// An <see cref="Authoritative"/> backend draws in the GS's place (docs/plans/ps2-full-speed.md §3). The GS doesn't
/// draw the batches it hands over, and the pages they draw to are the backend's until the GS reads them back
/// (<see cref="ReadBack"/>), which it does before anything reads or partly writes them in local memory.
/// </para>
/// </remarks>
internal interface IPs2GsBackend
{
    /// <summary>The backend draws in the GS's place: the GS skips its own drawing and reads pages back when needed.</summary>
    bool Authoritative { get; }

    /// <summary>
    /// A batch is about to be drawn into local memory: its primitives in order, the states they were queued with, and
    /// the pages it draws to and reads textures from. Local memory still holds what was there before the batch, but for
    /// pages the backend holds (an authoritative backend's, not yet read back).
    /// </summary>
    void Draw(ReadOnlySpan<Ps2Gs.Command> commands, ReadOnlySpan<Ps2Gs.DrawState> states, ReadOnlySpan<ulong> writes, ReadOnlySpan<ulong> reads);

    /// <summary>The batch has been drawn: local memory holds the pages it drew to as the GS left them. Not called when the backend is authoritative.</summary>
    void Drawn(ReadOnlySpan<ulong> writes);

    /// <summary>
    /// Copies <paramref name="pages"/>, which the backend drew in the GS's place, into local memory
    /// (<see cref="Ps2Gs.Memory"/>; above 1× each pixel's first sample). The backend keeps its own detail for them.
    /// </summary>
    void ReadBack(ReadOnlySpan<ulong> pages);

    /// <summary>Local memory was replaced (a reset or a state load): nothing drawn before can be relied on, and the backend holds no pages.</summary>
    void Resync();
}
