// PNG encoder for the NES 256×240 framebuffer. Hand-rolled, pure
// managed (DeflateStream + manual CRC32 + Adler32). Cloned from
// AprGb.Cli.Video.PngWriter; same algorithm, just consumes a packed
// RGB byte buffer directly (NesPpu writes RGB triples since the
// 64-entry NES NTSC palette is already a full-colour table, no
// palette-index indirection step needed at write time).

using System;
using System.IO;
using System.IO.Compression;

namespace AprNes.Cli.Video;

public static class PngWriter
{
    /// <summary>
    /// Save a packed RGB framebuffer (3 bytes per pixel: R, G, B) as
    /// 8-bit truecolour PNG. Suitable for the NES 256×240 framebuffer.
    /// </summary>
    public static void SavePng(byte[] framebufferRgb, int width, int height, string path)
    {
        if (framebufferRgb.Length != width * height * 3)
            throw new ArgumentException(
                $"framebuffer size mismatch: got {framebufferRgb.Length}, expected {width}×{height}×3 = {width * height * 3}",
                nameof(framebufferRgb));

        // 1) Build raw scanlines: filter byte (0 = None) + RGB triples
        var raw = new byte[height * (1 + width * 3)];
        int dst = 0;
        int srcStride = width * 3;
        for (int y = 0; y < height; y++)
        {
            raw[dst++] = 0;     // filter type: None
            Buffer.BlockCopy(framebufferRgb, y * srcStride, raw, dst, srcStride);
            dst += srcStride;
        }

        // 2) zlib-wrap the raw bytes (2-byte header, deflate stream, 4-byte adler32)
        var idat = ZlibCompress(raw);

        // 3) Write PNG file
        using var fs = File.Create(path);
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8]  = 8;     // bit depth
        ihdr[9]  = 2;     // color type: RGB
        ihdr[10] = 0;     // compression
        ihdr[11] = 0;     // filter
        ihdr[12] = 0;     // interlace
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
        buf[off]     = (byte)(v >> 24);
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
