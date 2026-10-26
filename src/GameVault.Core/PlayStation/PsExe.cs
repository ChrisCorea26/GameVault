using System.Buffers.Binary;

namespace GameVault.Core.PlayStation;

/// <summary>
/// A PS-EXE program (homebrew and the boot executables on discs): a 2 KB header with the entry point, the global
/// pointer, the load address and the stack, then the code (psx-spx: "CDROM File Formats", "PS-X EXE").
/// </summary>
public sealed class PsExe
{
    public const int HeaderSize = 0x800;

    /// <summary>The stack used when the header leaves it at zero (the BIOS's default).</summary>
    public const uint DefaultStack = 0x801FFFF0;

    private PsExe(uint pc, uint gp, uint loadAddress, uint stack, byte[] code)
    {
        Pc = pc;
        Gp = gp;
        LoadAddress = loadAddress;
        Stack = stack;
        Code = code;
    }

    public uint Pc { get; }

    public uint Gp { get; }

    public uint LoadAddress { get; }

    /// <summary>The initial stack pointer (SP and FP).</summary>
    public uint Stack { get; }

    public byte[] Code { get; }

    public static bool IsPsExe(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[..8].SequenceEqual("PS-X EXE"u8);

    /// <exception cref="InvalidDataException">The data isn't a PS-EXE.</exception>
    public static PsExe Parse(ReadOnlySpan<byte> data)
    {
        if (!IsPsExe(data))
        {
            throw new InvalidDataException("Not a PlayStation executable (no \"PS-X EXE\" header).");
        }

        var pc = BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]);
        var gp = BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]);
        var loadAddress = BinaryPrimitives.ReadUInt32LittleEndian(data[0x18..]);
        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[0x1C..]), (uint)(data.Length - HeaderSize));
        var stackBase = BinaryPrimitives.ReadUInt32LittleEndian(data[0x30..]);
        var stackOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[0x34..]);
        var stack = stackBase == 0 ? DefaultStack : stackBase + stackOffset;
        return new PsExe(pc, gp, loadAddress, stack, data.Slice(HeaderSize, size).ToArray());
    }
}
