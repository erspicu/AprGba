using AprGb.Cli.Memory;

namespace AprGb.Cli.Video;

/// <summary>
/// Minimal Game Boy DMG PPU. Phase 4.5 first cut: renders only the
/// BG layer (no window, no sprites). Sufficient for Blargg test
/// ROMs which print results as BG text. No timing emulation — we
/// just composite the current VRAM whenever <see cref="RenderFrame"/>
/// is called (typically once at the end of a test run for screenshot).
/// </summary>
public sealed class GbPpu
{
    public const int Width  = 160;
    public const int Height = 144;

    /// <summary>Row-major; one byte per pixel = 2-bit DMG colour index.</summary>
    public byte[] Framebuffer { get; } = new byte[Width * Height];

    /// <summary>
    /// Snapshot the BG layer from current VRAM into the framebuffer.
    /// </summary>
    public void RenderFrame(GbMemoryBus bus)
    {
        byte lcdc = bus.Io[0x40];
        byte bgp  = bus.Io[0x47];
        byte scy  = bus.Io[0x42];
        byte scx  = bus.Io[0x43];

        // LCD off → blank white frame.
        if ((lcdc & 0x80) == 0) { Array.Fill(Framebuffer, (byte)0); return; }

        bool tileMapHi    = (lcdc & 0x08) != 0;     // 0=0x9800, 1=0x9C00
        bool tileDataLo   = (lcdc & 0x10) != 0;     // 0=0x8800 signed, 1=0x8000 unsigned
        bool bgEnable     = (lcdc & 0x01) != 0;

        if (!bgEnable) { Array.Fill(Framebuffer, (byte)0); return; }

        int tileMapBase  = tileMapHi  ? 0x1C00 : 0x1800;        // VRAM-relative
        int tileDataBase = tileDataLo ? 0x0000 : 0x0800;

        for (int py = 0; py < Height; py++)
        {
            int bgY    = (py + scy) & 0xFF;
            int tileY  = bgY >> 3;
            int rowIn  = bgY & 7;

            for (int px = 0; px < Width; px++)
            {
                int bgX   = (px + scx) & 0xFF;
                int tileX = bgX >> 3;
                int colIn = bgX & 7;

                int mapIdx  = tileY * 32 + tileX;
                int tileNum = bus.Vram[tileMapBase + mapIdx];

                int tileAddr;
                if (tileDataLo)
                {
                    tileAddr = tileDataBase + tileNum * 16;
                }
                else
                {
                    // signed mode: tile numbers 0-127 map to 0x9000-0x97FF,
                    // 128-255 map to 0x8800-0x8FFF.
                    sbyte signed = (sbyte)tileNum;
                    tileAddr = 0x1000 + signed * 16;
                }

                byte lo = bus.Vram[tileAddr + rowIn * 2];
                byte hi = bus.Vram[tileAddr + rowIn * 2 + 1];
                int shift = 7 - colIn;
                int colorIdx = ((lo >> shift) & 1) | (((hi >> shift) & 1) << 1);
                byte palettedColor = (byte)((bgp >> (colorIdx * 2)) & 0x3);

                Framebuffer[py * Width + px] = palettedColor;
            }
        }
    }
}
