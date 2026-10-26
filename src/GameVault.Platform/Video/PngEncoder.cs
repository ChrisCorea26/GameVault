using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace GameVault.Platform.Video;

/// <summary>Minimal PNG encoder (RGB, 8-bit) for save-state thumbnails and screenshots.</summary>
public static class PngEncoder
{
    public static byte[] Encode(ReadOnlySpan<uint> argb, int width, int height)
    {
        var raw = new byte[height * (1 + width * 3)];
        var o = 0;
        for (var y = 0; y < height; y++)
        {
            raw[o++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var c = argb[y * width + x];
                raw[o++] = (byte)(c >> 16);
                raw[o++] = (byte)(c >> 8);
                raw[o++] = (byte)c;
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // color type: RGB
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>Converts a PPU framebuffer (palette index | emphasis) to ARGB with the given palette.</summary>
    public static uint[] ToArgb(ReadOnlySpan<ushort> frame, ReadOnlySpan<uint> palette)
    {
        var argb = new uint[frame.Length];
        for (var i = 0; i < frame.Length; i++)
        {
            argb[i] = palette[frame[i] & 0x7FF];
        }

        return argb;
    }

    /// <summary>A small picture of a frame (nearest pixel), as ARGB pixels: the rooms of automatic maps.</summary>
    public static uint[] Downscale(
        ReadOnlySpan<ushort> frame, ReadOnlySpan<uint> palette, int width, int height, int sourceWidth = 256, int sourceHeight = 240)
    {
        var argb = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * sourceHeight / height * sourceWidth;
            for (var x = 0; x < width; x++)
            {
                argb[y * width + x] = palette[frame[row + x * sourceWidth / width] & 0x7FF];
            }
        }

        return argb;
    }

    /// <summary>A 128×120 thumbnail of a frame (half size for the NES's 256×240), for save-state slots.</summary>
    public static byte[] Thumbnail(ReadOnlySpan<ushort> frame, ReadOnlySpan<uint> palette, int sourceWidth = 256, int sourceHeight = 240) =>
        Encode(Downscale(frame, palette, 128, 120, sourceWidth, sourceHeight), 128, 120);

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);

        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        png.Write(crcBytes);
    }
}
