namespace GameVault.Core.Cpu.R5900;

/// <summary>
/// The Emotion Engine's bus, in physical addresses: the CPU translates virtual addresses itself (KSEG0/KSEG1 and the
/// TLB) and keeps the scratchpad, so the bus sees RDRAM, the registers, the VU memories, the GS, the IOP's RAM and the
/// BIOS (ps2tek: "EE Virtual/Physical Memory Map"). Wider accesses are naturally aligned; a 128-bit access ignores
/// the address's low four bits.
/// </summary>
public interface IR5900Bus
{
    /// <summary>The 32 MB of RDRAM at physical address 0, which the CPU reads and writes directly on its fast path.</summary>
    byte[] Rdram { get; }

    byte Read8(uint address);

    ushort Read16(uint address);

    uint Read32(uint address);

    ulong Read64(uint address);

    UInt128 Read128(uint address);

    void Write8(uint address, byte value);

    void Write16(uint address, ushort value);

    void Write32(uint address, uint value);

    void Write64(uint address, ulong value);

    void Write128(uint address, UInt128 value);
}

/// <summary>
/// Coprocessor 2 as the EE sees it: VU0 in macro mode (ps2tek: "EE Instruction Decoding", COP2). Implemented by VU0, so
/// the CPU doesn't depend on the vector unit's internals.
/// </summary>
public interface IR5900Cop2
{
    /// <summary>A VF register (QMFC2 and SQC2 read it; QMTC2 and LQC2 write it).</summary>
    UInt128 ReadVf(int index);

    void WriteVf(int index, UInt128 value);

    /// <summary>
    /// A control register as CFC2 reads it: 0–15 the VI registers, 16 status, 17 MAC, 18 clipping, 20 R, 21 I, 22 Q,
    /// 26 TPC, 27 CMSAR0, 28 FBRST, 29 VPU-STAT, 31 CMSAR1.
    /// </summary>
    uint ReadControl(int index);

    /// <summary>CTC2. Writing CMSAR1 starts VU1 at that address; FBRST resets or force-breaks the VUs.</summary>
    void WriteControl(int index, uint value);

    /// <summary>A COP2 instruction with the CO bit (bit 25) set: an upper or lower VU operation, or VCALLMS/VCALLMSR.</summary>
    void ExecuteMacro(uint instruction);

    /// <summary>VU0 is running a micro program (the interlocked transfers wait, and BC2F/BC2T test this).</summary>
    bool MicroRunning { get; }

    /// <summary>Runs VU0 until its micro program ends: what the interlock bit of QMFC2/QMTC2/CFC2/CTC2 waits for.</summary>
    void WaitForMicro();
}
