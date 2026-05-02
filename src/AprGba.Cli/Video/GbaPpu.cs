using AprCpu.Core.Runtime.Gba;

namespace AprGba.Cli.Video;

/// <summary>
/// Phase 5/8 stub PPU. Implements the bare minimum needed to dump
/// a screenshot — currently Mode 3 (240×160 direct RGB555 framebuffer
/// at VRAM 0x06000000) and a black "LCD off" / "unsupported mode"
/// fallback for everything else.
///
/// jsmolka GBA test ROMs render their pass/fail text via Mode 0
/// (tile-based BG) which is NOT yet supported; for those, the
/// screenshot will be a black frame and verification has to fall back
/// to register state inspection. Mode 0 lands in Phase 8 proper.
///
/// All rendering is "snapshot from current VRAM at call time" — no
/// scanline timing, no sprite priority, no blending. Sufficient for
/// homebrew that uses Mode 3 (e.g. simple Mode 3 demos that draw
/// gradient backgrounds).
/// </summary>
public sealed class GbaPpu
{
    public const int Width  = 240;
    public const int Height = 160;

    /// <summary>RGB triples, row-major.</summary>
    public byte[] Framebuffer { get; } = new byte[Width * Height * 3];

    public void RenderFrame(GbaMemoryBus bus)
    {
        var dispcnt = bus.ReadHalfword(0x04000000);
        int mode = dispcnt & 0x7;
        bool forcedBlank = (dispcnt & 0x80) != 0;

        if (forcedBlank) { FillBlack(); return; }

        switch (mode)
        {
            case 3:
                RenderMode3(bus);
                return;
            case 4:
                RenderMode4(bus);
                return;
            default:
                FillBlack();
                return;
        }
    }

    /// <summary>
    /// Mode 3: 240×160 RGB555 directly in VRAM. Two bytes per pixel:
    /// bits 0..4 R, 5..9 G, 10..14 B, bit 15 unused.
    /// </summary>
    private void RenderMode3(GbaMemoryBus bus)
    {
        int vramOff = 0;
        int dst = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                ushort px = (ushort)(bus.Vram[vramOff] | (bus.Vram[vramOff + 1] << 8));
                vramOff += 2;
                Framebuffer[dst++] = (byte)((px        & 0x1F) << 3);   // R
                Framebuffer[dst++] = (byte)(((px >> 5) & 0x1F) << 3);   // G
                Framebuffer[dst++] = (byte)(((px >> 10) & 0x1F) << 3);  // B
            }
        }
    }

    /// <summary>
    /// Mode 4: 240×160 paletted (8-bit per pixel) framebuffer. Palette
    /// at PRAM 0x05000000, 256 entries × RGB555. Page 0 (frame 0) only;
    /// DISPCNT bit 4 (page select) ignored.
    /// </summary>
    private void RenderMode4(GbaMemoryBus bus)
    {
        int vramOff = 0;
        int dst = 0;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte idx = bus.Vram[vramOff++];
                int paletteOff = idx * 2;
                ushort px = (ushort)(bus.Palette[paletteOff] | (bus.Palette[paletteOff + 1] << 8));
                Framebuffer[dst++] = (byte)((px        & 0x1F) << 3);
                Framebuffer[dst++] = (byte)(((px >> 5) & 0x1F) << 3);
                Framebuffer[dst++] = (byte)(((px >> 10) & 0x1F) << 3);
            }
        }
    }

    private void FillBlack()
    {
        Array.Clear(Framebuffer, 0, Framebuffer.Length);
    }
}
