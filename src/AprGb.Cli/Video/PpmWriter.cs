namespace AprGb.Cli.Video;

/// <summary>
/// Saves a Game Boy framebuffer (palette-indexed 0..3) as a PPM image.
/// PPM is plain binary RGB with a tiny ASCII header — viewable by
/// most image tools (Windows Photos, IrfanView, GIMP, VS Code preview)
/// and trivial to compare byte-for-byte. No external dependencies.
/// </summary>
public static class PpmWriter
{
    /// <summary>Classic green DMG palette (0=lightest, 3=darkest).</summary>
    private static readonly (byte R, byte G, byte B)[] DmgPalette =
    {
        (0xC4, 0xCF, 0xA1),
        (0x8B, 0x95, 0x6D),
        (0x4D, 0x53, 0x39),
        (0x1F, 0x1F, 0x1F),
    };

    public static void SavePpm(byte[] paletteIndexed, int width, int height, string path)
    {
        if (paletteIndexed.Length != width * height)
            throw new ArgumentException("framebuffer size doesn't match width × height", nameof(paletteIndexed));

        using var fs  = File.Create(path);
        using var w   = new BinaryWriter(fs);
        var header = $"P6\n{width} {height}\n255\n";
        w.Write(System.Text.Encoding.ASCII.GetBytes(header));
        foreach (var idx in paletteIndexed)
        {
            var (r, g, b) = DmgPalette[idx & 0x3];
            w.Write(r); w.Write(g); w.Write(b);
        }
    }
}
