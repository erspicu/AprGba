using AprCpu.Core.Runtime.Gba;

namespace AprGba.Cli.Video;

/// <summary>
/// Phase 5/8 stub PPU. Implements the subset of GBA video modes needed
/// to dump a meaningful screenshot:
/// <list type="bullet">
///   <item>Mode 2 (affine BG2 + BG3, 8bpp, 256-colour palette) — used by
///         the BIOS startup logo intro and many homebrew demos.</item>
///   <item>Mode 3 (240×160 direct RGB555 framebuffer at VRAM 0x06000000).</item>
///   <item>Mode 4 (240×160 paletted, page 0 only).</item>
///   <item>Other modes: backdrop fill (palette[0]) so the user sees the
///         BG colour the cart programmed instead of always black.</item>
/// </list>
///
/// Sprites (OBJ) and modes 0/1/5 are not yet implemented; the BIOS
/// flying-GBA-logo and jsmolka pass/fail text are mostly OBJ + mode 0
/// respectively. Snapshot-only — no scanline timing, no priority, no
/// blending, no windows.
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
            case 2:
                RenderMode2(bus, dispcnt);
                break;
            case 3:
                RenderMode3(bus);
                break;
            case 4:
                RenderMode4(bus);
                break;
            default:
                FillBackdrop(bus);
                break;
        }

        // Composite OBJ sprites on top — needed for the BIOS logo intro
        // (the rotating Nintendo / dropping GBA-logo) as well as most
        // commercial games. Only when OBJ enable bit is set in DISPCNT.
        bool objEnable = (dispcnt & (1 << 12)) != 0;
        bool obj1D     = (dispcnt & (1 << 6))  != 0;
        if (objEnable) RenderObjects(bus, obj1D);
    }

    /// <summary>
    /// Mode 2: affine BG2 + BG3, 8bpp tile data, 256-colour palette.
    /// Per GBATEK, mode-2 BGs use:
    /// <list type="bullet">
    ///   <item>BGxCNT bits 2-3 = char base (×0x4000), bits 8-12 = screen
    ///         base (×0x800), bits 14-15 = screen size (16/32/64/128
    ///         tiles square), bit 13 = wrap-on-overflow.</item>
    ///   <item>BGxPA/PB/PC/PD: 8.8 signed fixed-point affine matrix.</item>
    ///   <item>BGxX/BGxY: 19.8 signed fixed-point reference point.</item>
    ///   <item>For each screen pixel (sx, sy): bg_x = (PA·sx + PB·sy + X) >> 8,
    ///         bg_y = (PC·sx + PD·sy + Y) >> 8. Out-of-range → transparent
    ///         (overflow=0) or wrapped (overflow=1).</item>
    /// </list>
    /// Both BGs are flattened in priority order (BG3 then BG2 on top
    /// when both have priority 0). Sprites (OBJ) are not yet drawn —
    /// for the BIOS intro that means the Nintendo logo background
    /// shows but the flying GBA-logo sprites do not.
    /// </summary>
    private void RenderMode2(GbaMemoryBus bus, ushort dispcnt)
    {
        // Start from the backdrop, then composite BGs that are enabled.
        FillBackdrop(bus);

        bool bg2On = (dispcnt & (1 << 10)) != 0;
        bool bg3On = (dispcnt & (1 << 11)) != 0;
        if (!bg2On && !bg3On) return;

        // Sort by priority — lower priority value = drawn ON TOP. Draw
        // higher priority first (= further BACK), then overlay lower.
        var bgs = new (int idx, int prio)[2];
        int n = 0;
        if (bg3On) bgs[n++] = (3, bus.ReadHalfword(0x0400000A) & 0x3);
        if (bg2On) bgs[n++] = (2, bus.ReadHalfword(0x04000008) & 0x3);
        // Stable insertion sort by priority desc (back-to-front).
        if (n == 2 && bgs[0].prio < bgs[1].prio) (bgs[0], bgs[1]) = (bgs[1], bgs[0]);

        for (int i = 0; i < n; i++) RenderAffineBg(bus, bgs[i].idx);
    }

    private void RenderAffineBg(GbaMemoryBus bus, int bgIndex)
    {
        // Register offsets (BG2 starts at 0x04000020, BG3 at 0x04000030).
        uint cntAddr = bgIndex == 2 ? 0x04000008u : 0x0400000Au;
        uint paBase  = bgIndex == 2 ? 0x04000020u : 0x04000030u;

        ushort cnt = bus.ReadHalfword(cntAddr);
        int charBase   = ((cnt >> 2) & 0x3) * 0x4000;
        int screenBase = ((cnt >> 8) & 0x1F) * 0x800;
        int sizeIdx    = (cnt >> 14) & 0x3;
        int sideTiles  = sizeIdx switch { 0 => 16, 1 => 32, 2 => 64, _ => 128 };
        int sidePixels = sideTiles * 8;
        bool wrap      = (cnt & (1 << 13)) != 0;

        // 8.8 signed PA/PB/PC/PD.
        short pa = (short)bus.ReadHalfword(paBase + 0);
        short pb = (short)bus.ReadHalfword(paBase + 2);
        short pc = (short)bus.ReadHalfword(paBase + 4);
        short pd = (short)bus.ReadHalfword(paBase + 6);
        // 28-bit signed 19.8 X / Y, sign-extended from bit 27.
        int x = SignExtend28(bus.ReadWord(paBase + 8));
        int y = SignExtend28(bus.ReadWord(paBase + 12));

        int dst = 0;
        for (int sy = 0; sy < Height; sy++)
        {
            // Origin for this scanline = (X + PB·sy, Y + PD·sy)
            // Each pixel along the scanline adds (PA, PC).
            int curX = x + pb * sy;
            int curY = y + pd * sy;
            for (int sx = 0; sx < Width; sx++, curX += pa, curY += pc, dst += 3)
            {
                int bgX = curX >> 8;
                int bgY = curY >> 8;

                if (wrap)
                {
                    bgX = ((bgX % sidePixels) + sidePixels) % sidePixels;
                    bgY = ((bgY % sidePixels) + sidePixels) % sidePixels;
                }
                else if ((uint)bgX >= sidePixels || (uint)bgY >= sidePixels)
                {
                    continue;     // transparent — leave backdrop pixel
                }

                int tileX = bgX >> 3;
                int tileY = bgY >> 3;
                int mapOff  = screenBase + tileY * sideTiles + tileX;
                if (mapOff < 0 || mapOff >= bus.Vram.Length) continue;
                byte tileId = bus.Vram[mapOff];

                int pixInTileX = bgX & 7;
                int pixInTileY = bgY & 7;
                int tileOff = charBase + tileId * 64 + pixInTileY * 8 + pixInTileX;
                if (tileOff < 0 || tileOff >= bus.Vram.Length) continue;
                byte palIdx = bus.Vram[tileOff];
                if (palIdx == 0) continue;     // colour 0 = transparent

                int palOff = palIdx * 2;
                ushort px = (ushort)(bus.Palette[palOff] | (bus.Palette[palOff + 1] << 8));
                Framebuffer[dst    ] = (byte)((px        & 0x1F) << 3);
                Framebuffer[dst + 1] = (byte)(((px >> 5) & 0x1F) << 3);
                Framebuffer[dst + 2] = (byte)(((px >> 10) & 0x1F) << 3);
            }
        }
    }

    private static int SignExtend28(uint v)
    {
        v &= 0x0FFFFFFFu;
        return (v & 0x08000000u) != 0 ? (int)(v | 0xF0000000u) : (int)v;
    }

    // ---------------- OBJ (sprite) rendering ----------------
    //
    // 128 OBJ entries, 8 bytes each, packed into OAM (0x07000000..3FF):
    //   attr0  (off 0-1): bits  0-7  Y, 8 affine, 9 double/disable, 10-11 mode,
    //                     12 mosaic, 13 256-colour, 14-15 shape
    //   attr1  (off 2-3): bits  0-8  X, 9-13 affine index | hflip+vflip,
    //                     14-15 size
    //   attr2  (off 4-5): bits  0-9  tile id, 10-11 priority,
    //                     12-15 palette# (4bpp only)
    //   filler (off 6-7): one 16-bit lane of an affine matrix
    //
    // Affine matrices: 32 of them, indexed by attr1[9..13]. Matrix N is
    // assembled from the filler slots of OBJ entries 4N..4N+3:
    //   PA = OAM[8·(4N+0) + 6], PB = ...+8 +6, PC = ...+16 +6, PD = ...+24 +6
    //
    // Tile pixel data lives in VRAM 0x06010000..0x06017FFF (sprite tile
    // region, 32 KB). 4bpp = 32 B/tile (16-colour palette# from attr2),
    // 8bpp = 64 B/tile (256-colour shared sprite palette). Sprite palette
    // base in PRAM 0x05000200.
    //
    // Tile mapping (DISPCNT bit 6):
    //   0 = 2D — VRAM is a 32-tile-wide grid of sprite tiles
    //   1 = 1D — tiles for one sprite are laid out contiguously
    //
    // Sprite dimensions per (shape, size) GBATEK table.

    private void RenderObjects(GbaMemoryBus bus, bool oneDimensional)
    {
        // Render in REVERSE priority order so lower-priority pixels go
        // down first and higher-priority sprites overwrite them. Within
        // a priority bucket, lower OBJ index draws on top.
        for (int prio = 3; prio >= 0; prio--)
        {
            for (int i = 127; i >= 0; i--)
            {
                int oamOff = i * 8;
                ushort a0 = (ushort)(bus.Oam[oamOff    ] | (bus.Oam[oamOff + 1] << 8));
                ushort a1 = (ushort)(bus.Oam[oamOff + 2] | (bus.Oam[oamOff + 3] << 8));
                ushort a2 = (ushort)(bus.Oam[oamOff + 4] | (bus.Oam[oamOff + 5] << 8));

                bool affine    = (a0 & (1 << 8))  != 0;
                bool disable   = !affine && (a0 & (1 << 9)) != 0;
                if (disable) continue;
                int spritePri = (a2 >> 10) & 0x3;
                if (spritePri != prio) continue;

                int  shape   = (a0 >> 14) & 0x3;
                int  size    = (a1 >> 14) & 0x3;
                bool color8  = (a0 & (1 << 13)) != 0;
                int  paletteN = color8 ? 0 : (a2 >> 12) & 0xF;
                int  tileBase = a2 & 0x3FF;
                int  yOrigin = a0 & 0xFF;          // 0..255
                int  xOrigin = a1 & 0x1FF;         // 0..511
                bool doubleSize = affine && (a0 & (1 << 9)) != 0;
                int  affineIdx  = affine ? (a1 >> 9) & 0x1F : 0;
                bool hflip = !affine && (a1 & (1 << 12)) != 0;
                bool vflip = !affine && (a1 & (1 << 13)) != 0;

                var (sw, sh) = SpriteDimensions(shape, size);
                int boxW = doubleSize ? sw * 2 : sw;
                int boxH = doubleSize ? sh * 2 : sh;

                // Sign-extend X (9-bit) and Y (8-bit) to allow off-screen
                // negative origins (ARM ARM-style sprite wraparound is
                // 256-pixel for Y and 512 for X — but we just clamp to
                // signed range for screen coordinates).
                int sxOrigin = xOrigin >= 256 ? xOrigin - 512 : xOrigin;
                int syOrigin = yOrigin >= 160 ? yOrigin - 256 : yOrigin;

                // Affine matrix (8.8 signed fixed-point). For non-affine
                // sprites use identity + flip flags handled inline.
                int pa = 0x100, pb = 0, pc = 0, pd = 0x100;
                if (affine)
                {
                    int matBase = affineIdx * 32;
                    pa = (short)(bus.Oam[matBase + 0x06] | (bus.Oam[matBase + 0x07] << 8));
                    pb = (short)(bus.Oam[matBase + 0x0E] | (bus.Oam[matBase + 0x0F] << 8));
                    pc = (short)(bus.Oam[matBase + 0x16] | (bus.Oam[matBase + 0x17] << 8));
                    pd = (short)(bus.Oam[matBase + 0x1E] | (bus.Oam[matBase + 0x1F] << 8));
                }

                // Sprite-internal half-extents (from box centre to edge).
                int halfW = boxW / 2;
                int halfH = boxH / 2;

                int tilesPerRow = oneDimensional ? sw / 8 : 32;
                int bytesPerTile = color8 ? 64 : 32;

                for (int dy = 0; dy < boxH; dy++)
                {
                    int sy = syOrigin + dy;
                    if ((uint)sy >= Height) continue;
                    for (int dx = 0; dx < boxW; dx++)
                    {
                        int sx = sxOrigin + dx;
                        if ((uint)sx >= Width) continue;

                        // Map screen pixel (dx, dy) within the sprite's
                        // bounding box into texture coords (tx, ty).
                        int tx, ty;
                        if (affine)
                        {
                            // Origin-relative offsets from box centre.
                            int rx = dx - halfW;
                            int ry = dy - halfH;
                            tx = (pa * rx + pb * ry) >> 8;
                            ty = (pc * rx + pd * ry) >> 8;
                            // Translate to sprite-internal coords (origin
                            // at top-left of the sprite, NOT the box).
                            tx += sw / 2;
                            ty += sh / 2;
                            if ((uint)tx >= sw || (uint)ty >= sh) continue;
                        }
                        else
                        {
                            tx = hflip ? (sw - 1 - dx) : dx;
                            ty = vflip ? (sh - 1 - dy) : dy;
                        }

                        int tileX = tx >> 3;
                        int tileY = ty >> 3;
                        int tileId = color8
                            ? (tileBase / 2) + (tileY * tilesPerRow + tileX)    // 8bpp halves the effective tile id
                            : tileBase + (tileY * tilesPerRow + tileX);
                        // GBA sprite tile area is 0x10000..0x17FFF (32 KB).
                        int vramTileOff = 0x10000 + tileId * bytesPerTile;
                        int pixInTileX = tx & 7;
                        int pixInTileY = ty & 7;

                        byte palIdx;
                        if (color8)
                        {
                            int byteOff = vramTileOff + pixInTileY * 8 + pixInTileX;
                            if ((uint)byteOff >= bus.Vram.Length) continue;
                            palIdx = bus.Vram[byteOff];
                        }
                        else
                        {
                            int byteOff = vramTileOff + pixInTileY * 4 + (pixInTileX >> 1);
                            if ((uint)byteOff >= bus.Vram.Length) continue;
                            byte twoPix = bus.Vram[byteOff];
                            int nybble  = (pixInTileX & 1) == 0 ? (twoPix & 0xF) : (twoPix >> 4);
                            palIdx = (byte)nybble;
                        }
                        if (palIdx == 0) continue;     // colour 0 = transparent

                        // Sprite palette base = 0x200 in PRAM. 4bpp adds
                        // palette# × 16; 8bpp uses raw 8-bit index.
                        int palOff = 0x200 + (color8 ? palIdx : (paletteN * 16 + palIdx)) * 2;
                        if ((uint)(palOff + 1) >= bus.Palette.Length) continue;
                        ushort px = (ushort)(bus.Palette[palOff] | (bus.Palette[palOff + 1] << 8));
                        int dst = (sy * Width + sx) * 3;
                        Framebuffer[dst    ] = (byte)((px        & 0x1F) << 3);
                        Framebuffer[dst + 1] = (byte)(((px >> 5) & 0x1F) << 3);
                        Framebuffer[dst + 2] = (byte)(((px >> 10) & 0x1F) << 3);
                    }
                }
            }
        }
    }

    private static (int width, int height) SpriteDimensions(int shape, int size)
    {
        // Per GBATEK "OBJ Attribute 0 — Shape" + "Attribute 1 — Size".
        return (shape, size) switch
        {
            (0, 0) => (8,  8),  (0, 1) => (16, 16), (0, 2) => (32, 32), (0, 3) => (64, 64),
            (1, 0) => (16, 8),  (1, 1) => (32, 8),  (1, 2) => (32, 16), (1, 3) => (64, 32),
            (2, 0) => (8, 16),  (2, 1) => (8,  32), (2, 2) => (16, 32), (2, 3) => (32, 64),
            _      => (8, 8),    // shape 3 reserved → match emulators' "act as 8×8".
        };
    }

    /// <summary>
    /// Fill the framebuffer with the backdrop colour (palette entry 0,
    /// RGB555). Used by mode 2 as the initial layer before BGs are
    /// composited, and as the fallback for unimplemented modes.
    /// </summary>
    private void FillBackdrop(GbaMemoryBus bus)
    {
        ushort px = (ushort)(bus.Palette[0] | (bus.Palette[1] << 8));
        byte r = (byte)((px        & 0x1F) << 3);
        byte g = (byte)(((px >> 5) & 0x1F) << 3);
        byte b = (byte)(((px >> 10) & 0x1F) << 3);
        for (int i = 0; i < Framebuffer.Length; i += 3)
        {
            Framebuffer[i    ] = r;
            Framebuffer[i + 1] = g;
            Framebuffer[i + 2] = b;
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
