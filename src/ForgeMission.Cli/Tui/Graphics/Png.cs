using System.IO.Compression;

namespace ForgeMission.Cli.Tui.Graphics;

// Phase 56 (G1): the PNG the kitty transmit carries. Colour type 2 (RGB 8-bit), filter 0, zlib
// through System.IO.Compression; no image library.

/// <summary>An opaque 8-bit sRGB image, row-major RGB.</summary>
internal sealed record RgbImage(int Width, int Height, byte[] Pixels)
{
    public static RgbImage Blank(int width, int height) => new(width, height, new byte[width * height * 3]);

    /// <summary>Copies the rectangle (x, y, w, h) into a new image.</summary>
    public RgbImage Crop(int x, int y, int w, int h)
    {
        var crop = Blank(w, h);
        for (var row = 0; row < h; row++)
            Array.Copy(Pixels, ((y + row) * Width + x) * 3, crop.Pixels, row * w * 3, w * 3);
        return crop;
    }
}

internal static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(RgbImage image)
    {
        using var png = new MemoryStream();
        png.Write(Signature);
        WriteChunk(png, "IHDR", Header(image));
        WriteChunk(png, "IDAT", Compress(Scanlines(image)));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] Header(RgbImage image)
    {
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)image.Width);
        WriteBigEndian(header, 4, (uint)image.Height);
        header[8] = 8; // bit depth
        header[9] = 2; // truecolour RGB
        return header;
    }

    /// <summary>Each row prefixed with filter byte 0 (none).</summary>
    private static byte[] Scanlines(RgbImage image)
    {
        var stride = image.Width * 3;
        var raw = new byte[image.Height * (stride + 1)];
        for (var y = 0; y < image.Height; y++)
            Array.Copy(image.Pixels, y * stride, raw, y * (stride + 1) + 1, stride);
        return raw;
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            zlib.Write(data);
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var typeAndData = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typeAndData[i] = (byte)type[i];
        data.CopyTo(typeAndData, 4);
        var buffer = new byte[4];
        WriteBigEndian(buffer, 0, (uint)data.Length);
        stream.Write(buffer);
        stream.Write(typeAndData);
        WriteBigEndian(buffer, 0, Crc32(typeAndData));
        stream.Write(buffer);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
