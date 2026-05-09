// Loader for the 256-glyph 8x14 CGA-style font borrowed from Apr86's
// ASII_FONT/<HH>.png set. Loads once on first use into a packed 1bpp
// bitmap (256 chars × 14 rows × 1 byte/row = 3584 bytes), then the
// renderer reads bits out without re-touching disk.
//
// The PNG decoder we already have is for output only; for input we
// need the System.Drawing.Bitmap path or a hand-rolled PNG reader.
// Since this font load happens once at process start and runs on
// Windows (CLAUDE.md target), System.Drawing.Common is fine here.

using System.Drawing;

namespace AprX86.Cli.Video;

public static class X86CgaFont
{
    public const int FontW = 8;
    public const int FontH = 14;
    public const int Glyphs = 256;

    /// <summary>1-bit-per-pixel packed bitmap. Index = char*FontH + row;
    /// each byte's bit 7 is leftmost pixel.</summary>
    private static byte[]? _bitmap;

    public static byte[] Bitmap => _bitmap ?? throw new InvalidOperationException("font not loaded");

    public static void EnsureLoaded(string fontDir)
    {
        if (_bitmap != null) return;
        var bm = new byte[Glyphs * FontH];
        for (int c = 0; c < Glyphs; c++)
        {
            var path = Path.Combine(fontDir, $"{c:x2}.png");
            using var img = new Bitmap(path);
            for (int y = 0; y < FontH; y++)
            {
                byte row = 0;
                for (int x = 0; x < FontW; x++)
                {
                    var px = img.GetPixel(x, y);
                    // Apr86's PNGs use white-on-black: any non-black = "set".
                    bool on = px.R > 0x40 || px.G > 0x40 || px.B > 0x40;
                    if (on) row |= (byte)(1 << (7 - x));
                }
                bm[c * FontH + y] = row;
            }
        }
        _bitmap = bm;
    }

    /// <summary>Locate the Apr86 ASII_FONT/ directory by walking up
    /// from the running binary. Returns the absolute path on success
    /// or null if not found (caller should embed a fallback font or
    /// fail with a clear message).</summary>
    public static string? LocateAprFontDir()
    {
        // Apr86 ships its 256 PNG glyphs under bin/Debug/ASII_FONT/ in
        // the Visual Studio output dir. We probe that path first, then
        // fall back to a few alternate layouts in case the user moved them.
        string[] candidates =
        {
            Path.Combine("OldProject", "Apr86", "Apr8086", "bin", "Debug", "ASII_FONT"),
            Path.Combine("OldProject", "Apr86", "Apr8086", "ASII_FONT"),
            Path.Combine("test-roms", "x86", "ASII_FONT"),
        };

        for (var d = new DirectoryInfo(AppContext.BaseDirectory);
             d != null;
             d = d.Parent)
        {
            foreach (var rel in candidates)
            {
                var probe = Path.Combine(d.FullName, rel);
                if (Directory.Exists(probe)) return probe;
            }
        }
        return null;
    }
}
