using System.IO.Compression;

namespace AprGba.Cli.Video;

/// <summary>
/// Minimal hand-rolled PNG encoder for the GBA CLI. Pure managed,
/// no native deps. Sister of AprGb.Cli's PngWriter — same algorithm,
/// but takes raw RGB byte triples directly (no palette indirection,
/// since GBA Mode 3 framebuffers are already in 15-bit RGB which we
/// expand to 24-bit). 240×160 is the GBA screen size.
/// </summary>
public static class PngWriter
{
    /// <summary>
    /// Save a width×height image where <paramref name="rgb"/> holds
    /// (R, G, B) byte triples row-major. Length must be width*height*3.
    /// </summary>
    public static void SaveRgbPng(byte[] rgb, int width, int height, string path)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException(
                $"rgb buffer size {rgb.Length} doesn't match width*height*3 = {width * height * 3}",
                nameof(rgb));

        // Build raw scanlines (filter byte + RGB triples).
        var raw = new byte[height * (1 + width * 3)];
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            raw[dst++] = 0;     // filter: None
            Array.Copy(rgb, y * width * 3, raw, dst, width * 3);
            dst += width * 3;
        }

        var idat = ZlibCompress(raw);

        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8]  = 8;     // bit depth
        ihdr[9]  = 2;     // color type: RGB
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(fs, "IHDR", ihdr);
        WriteChunk(fs, "IDAT", idat);
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0x9C);
        using (var ds = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            ds.Write(data, 0, data.Length);
        var adler = Adler32(data);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var lenBuf = new byte[4];
        WriteBE(lenBuf, 0, (uint)data.Length);
        s.Write(lenBuf);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var crcBuf = new byte[4];
        WriteBE(crcBuf, 0, crc);
        s.Write(crcBuf);
    }

    private static void WriteBE(byte[] buf, int off, uint v)
    {
        buf[off    ] = (byte)(v >> 24);
        buf[off + 1] = (byte)(v >> 16);
        buf[off + 2] = (byte)(v >> 8);
        buf[off + 3] = (byte)v;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        const uint MOD = 65521;
        foreach (var x in data)
        {
            a = (a + x) % MOD;
            b = (b + a) % MOD;
        }
        return (b << 16) | a;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
