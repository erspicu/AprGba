using System.IO.Compression;

namespace AprGb.Cli.Video;

/// <summary>
/// Minimal hand-rolled PNG encoder. Pure managed, no native deps,
/// no NuGet packages — just <see cref="DeflateStream"/> for the IDAT
/// payload plus a CRC32 + Adler32 by hand. Outputs 8-bit truecolour
/// (RGB), one byte per channel, suitable for 160×144 DMG framebuffers.
///
/// We use this instead of System.Drawing.Common because (a) it avoids
/// a Windows-only dependency and (b) PNG is small enough to write
/// directly without an extra image library on the dependency surface.
/// </summary>
public static class PngWriter
{
    /// <summary>Classic green DMG palette (0=lightest, 3=darkest).</summary>
    private static readonly (byte R, byte G, byte B)[] DmgPalette =
    {
        (0xC4, 0xCF, 0xA1),
        (0x8B, 0x95, 0x6D),
        (0x4D, 0x53, 0x39),
        (0x1F, 0x1F, 0x1F),
    };

    public static void SavePng(byte[] paletteIndexed, int width, int height, string path)
    {
        if (paletteIndexed.Length != width * height)
            throw new ArgumentException("framebuffer size doesn't match width × height", nameof(paletteIndexed));

        // 1) Build raw scanlines: filter byte (0 = None) + RGB triples
        var raw = new byte[height * (1 + width * 3)];
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            raw[dst++] = 0;     // filter type: None
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = DmgPalette[paletteIndexed[y * width + x] & 0x3];
                raw[dst++] = r;
                raw[dst++] = g;
                raw[dst++] = b;
            }
        }

        // 2) zlib-wrap the raw bytes (2-byte header, deflate stream, 4-byte adler32)
        var idat = ZlibCompress(raw);

        // 3) Write PNG file
        using var fs = File.Create(path);
        // Signature
        fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR
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
        ms.WriteByte(0x78); ms.WriteByte(0x9C);              // zlib header (default compression)
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
