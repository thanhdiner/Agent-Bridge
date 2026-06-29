using System.Buffers.Binary;
using System.IO.Compression;

namespace LocalMcp.Agent.Windows.UiAutomation;

internal static class BgraPngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(width));

        var expectedLength = checked(width * height * 4);
        if (bgra.Length != expectedLength)
            throw new ArgumentException("BGRA buffer length does not match its dimensions.", nameof(bgra));

        using var scanlines = new MemoryStream(checked((width * 4 + 1) * height));
        for (var y = 0; y < height; y++)
        {
            scanlines.WriteByte(0);
            var rowOffset = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var offset = rowOffset + x * 4;
                scanlines.WriteByte(bgra[offset + 2]);
                scanlines.WriteByte(bgra[offset + 1]);
                scanlines.WriteByte(bgra[offset]);
                scanlines.WriteByte(255);
            }
        }

        using var compressed = new MemoryStream();
        scanlines.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            scanlines.CopyTo(zlib);

        using var png = new MemoryStream(Signature.Length + compressed.Capacity + 128);
        png.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)width));
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], checked((uint)height));
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WriteChunk(Stream destination, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(integer, checked((uint)data.Length));
        destination.Write(integer);
        destination.Write(type);
        destination.Write(data);

        var crc = UpdateCrc(uint.MaxValue, type);
        crc = UpdateCrc(crc, data) ^ uint.MaxValue;
        BinaryPrimitives.WriteUInt32BigEndian(integer, crc);
        destination.Write(integer);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
            crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xedb88320U ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
}
