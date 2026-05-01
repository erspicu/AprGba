namespace AprGb.Cli.Video;

/// <summary>
/// Minimal Game Boy PPU. Phase 4.5 first cut: produces a 160×144
/// framebuffer of palette indices (0..3) the CLI can save as PPM/PNG.
/// Real tile/sprite rendering lands when the LegacyCpu port runs and
/// the test ROM populates VRAM.
/// </summary>
public sealed class GbPpu
{
    public const int Width  = 160;
    public const int Height = 144;

    /// <summary>
    /// Pixel buffer, row-major, one byte per pixel containing the
    /// 2-bit DMG palette index (0=white, 3=black).
    /// </summary>
    public byte[] Framebuffer { get; } = new byte[Width * Height];

    /// <summary>
    /// Render N scanlines worth of pixels by reading VRAM/IO from the
    /// memory bus. Stub for now — fills with a recognisable pattern so
    /// the CLI's screenshot path can be validated end-to-end before the
    /// real PPU lands.
    /// </summary>
    public void RenderFrameStub()
    {
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Framebuffer[y * Width + x] = (byte)((x ^ y) & 0x3);
    }
}
