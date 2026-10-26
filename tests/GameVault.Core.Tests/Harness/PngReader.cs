using System.Buffers.Binary;
using System.IO.Compression;

namespace GameVault.Core.Tests.Harness;

/// <summary>
/// Decodes the 8-bit, non-interlaced PNGs that reference images use (greyscale, RGB, palette and RGBA) into rows of
/// RGB triples, for comparing emulated output with pictures from real hardware.
/// </summary>
public static class PngReader
{
    public static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
    {
        int width = 0, height = 0, colorType = 0;
        byte[]? palette = null;
        using var compressed = new MemoryStream();
        for (var pos = 8; pos + 8 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    if (data[8] != 8 || data[12] != 0)
                    {
                        throw new NotSupportedException("Only 8-bit, non-interlaced PNGs are supported.");
                    }

                    colorType = data[9];
                    break;
                case "PLTE":
                    palette = data.ToArray();
                    break;
                case "IDAT":
                    compressed.Write(data);
                    break;
            }

            pos += 12 + length;
        }

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException() };
        var stride = width * channels;
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var raw = new byte[(stride + 1) * height];
        inflater.ReadExactly(raw);

        var rgb = new byte[width * height * 3];
        var previous = new byte[stride];
        var line = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Array.Copy(raw, y * (stride + 1) + 1, line, 0, stride);
            for (var x = 0; x < stride; x++)
            {
                int a = x >= channels ? line[x - channels] : 0, b = previous[x], c = x >= channels ? previous[x - channels] : 0;
                line[x] += filter switch
                {
                    1 => (byte)a,
                    2 => (byte)b,
                    3 => (byte)((a + b) / 2),
                    4 => (byte)Paeth(a, b, c),
                    _ => 0,
                };
            }

            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 3;
                switch (colorType)
                {
                    case 0:
                    case 4:
                        rgb[o] = rgb[o + 1] = rgb[o + 2] = line[x * channels];
                        break;
                    case 3:
                        Array.Copy(palette!, line[x] * 3, rgb, o, 3);
                        break;
                    default:
                        Array.Copy(line, x * channels, rgb, o, 3);
                        break;
                }
            }

            (previous, line) = (line, previous);
        }

        return (width, height, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
