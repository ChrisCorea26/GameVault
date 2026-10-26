namespace GameVault.Core.PlayStation2;

/// <summary>The EE's interrupt sources: the bits of INTC_STAT and INTC_MASK (ps2tek: "EE Map", INTC).</summary>
public enum Ps2Interrupt
{
    Gs = 0,
    Sbus = 1,
    VBlankStart = 2,
    VBlankEnd = 3,
    Vif0 = 4,
    Vif1 = 5,
    Vu0 = 6,
    Vu1 = 7,
    Ipu = 8,
    Timer0 = 9,
    Timer1 = 10,
    Timer2 = 11,
    Timer3 = 12,
    Sfifo = 13,
    Vu0Watchdog = 14,
}

/// <summary>
/// A vector unit as the VIF and the console drive it (ps2tek: "VIF Commands"; the VU User's Manual). VU0 and VU1 differ
/// in memory sizes and VU1's extra instructions (the EFU and XGKICK).
/// </summary>
public interface IPs2VectorUnit
{
    /// <summary>0 or 1.</summary>
    int Index { get; }

    /// <summary>Micro memory: 4 KB on VU0, 16 KB on VU1. MPG writes here.</summary>
    byte[] MicroMemory { get; }

    /// <summary>Data memory: 4 KB on VU0, 16 KB on VU1. UNPACK, STROW-filled writes and XGKICK use it.</summary>
    byte[] DataMemory { get; }

    /// <summary>A micro program is running (VIF's MSCAL waits, FLUSHE waits for it to end).</summary>
    bool Running { get; }

    /// <summary>Starts a micro program at <paramref name="address"/>, in bytes into micro memory (MSCAL's immediate × 8).</summary>
    void StartMicro(uint address);

    /// <summary>Continues at the address where the last micro program ended (MSCNT).</summary>
    void ContinueMicro();

    /// <summary>Runs a running micro program for up to <paramref name="cycles"/> VU cycles (the EE's clock); returns the cycles used.</summary>
    long Run(long cycles);
}

/// <summary>What a vector unit needs from the rest of the machine.</summary>
public interface IPs2VuHost
{
    /// <summary>XTOP: VIF1's TOP register.</summary>
    uint ReadTop(int unit);

    /// <summary>XITOP: VIFn's ITOP register.</summary>
    uint ReadItop(int unit);

    /// <summary>
    /// XGKICK: VU1 starts a GIF packet (PATH1) at quadword <paramref name="address"/> of its data memory. The GIF reads
    /// the packet from VU1's data memory, wrapping at its end.
    /// </summary>
    void XgKick(uint address);

    /// <summary>A micro program ended with its D or T bit set (the VU0/VU1 interrupt to the INTC, when FBRST enables it).</summary>
    void RaiseInterrupt(int unit);

    /// <summary>VU0 reaching VU1's registers through its data space ($4000–$43FF): the VU1 to read or write.</summary>
    IPs2VectorUnit Vu1 { get; }
}

/// <summary>
/// The GIF's inputs (ps2tek: "GIF I/O"): PATH1 from VU1's XGKICK, PATH2 from VIF1's DIRECT/DIRECTHL, PATH3 from the
/// GIF's DMA channel. Paths interleave only at packet boundaries, so a write returns fewer quadwords than it was given
/// when another path holds the GIF; the caller retries later.
/// </summary>
public interface IPs2Gif
{
    /// <summary>PATH1: a whole GIF packet from VU1's data memory starting at quadword <paramref name="address"/>.</summary>
    void KickPath1(byte[] vu1DataMemory, uint address);

    /// <summary>PATH2 data; returns how many quadwords the GIF took.</summary>
    int WritePath2(ReadOnlySpan<UInt128> data);

    /// <summary>PATH3 data; returns how many quadwords the GIF took (none while PATH3 is masked or another path holds the GIF).</summary>
    int WritePath3(ReadOnlySpan<UInt128> data);

    /// <summary>VIF1's MSKPATH3.</summary>
    bool Path3Masked { get; set; }

    /// <summary>PATH2 is in the middle of a packet (VIF1's DIRECTHL and FLUSHA wait on the other paths).</summary>
    bool Path2Active { get; }

    /// <summary>PATH3 is in the middle of a packet.</summary>
    bool Path3Active { get; }

    /// <summary>
    /// Local-to-host transfers (BUSDIR reversed): reads quadwords the GS has ready for VIF1's DMA; returns how many.
    /// </summary>
    int ReadDownload(Span<UInt128> destination);
}
