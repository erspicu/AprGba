using AprCpu.Core.Runtime.Gba;

namespace AprGba.Cli.Video;

/// <summary>
/// Phase 5/8 PPU. Per-layer buffer pipeline:
///
/// <list type="number">
///   <item>Build OBJ-Window mask from mode-2 sprites.</item>
///   <item>Render each enabled BG layer (BG0..BG3) into its own
///         <see cref="ushort"/>[Width × Height] buffer. RGB555 in
///         bits 0..14, bit 15 = "valid" flag (0 = transparent).</item>
///   <item>Render OBJ sprites into the OBJ buffer + per-pixel
///         priority + per-pixel "semi-transparent" flag.</item>
///   <item>Composite per pixel: walk layers in priority order to pick
///         topmost + second-topmost opaque pixel, then apply
///         BLDCNT alpha / brighten / darken via BLDALPHA / BLDY.
///         OBJ semi-transparent (mode 1) sprites force alpha-blending
///         with the layer underneath regardless of BLDCNT.Target1.</item>
/// </list>
///
/// Only modes 2 / 3 / 4 are wired up. Modes 0 / 1 / 5 fall through to
/// a backdrop-coloured screen. Mosaic, BG-mode-0 tile-map, and Mode-1
/// hybrid are not yet implemented.
/// </summary>
public sealed class GbaPpu
{
    public const int Width  = 240;
    public const int Height = 160;

    private const ushort ValidBit = 0x8000;     // bit 15 inside layer buffer = "this pixel was rendered"

    /// <summary>RGB triples, row-major, the final composited image.</summary>
    public byte[] Framebuffer { get; } = new byte[Width * Height * 3];

    // Per-layer pixel buffers. Bit 15 = valid; bits 0..14 = RGB555.
    private readonly ushort[] _bg0Layer = new ushort[Width * Height];
    private readonly ushort[] _bg1Layer = new ushort[Width * Height];
    private readonly ushort[] _bg2Layer = new ushort[Width * Height];
    private readonly ushort[] _bg3Layer = new ushort[Width * Height];
    private readonly ushort[] _objLayer = new ushort[Width * Height];

    // OBJ aux buffers — needed because OBJ priority is per-sprite, not per-layer.
    private readonly byte[]   _objPriority   = new byte[Width * Height];   // 0..3
    private readonly byte[]   _objSemiTrans  = new byte[Width * Height];   // 1 if pixel from mode-1 sprite

    /// <summary>OBJ-Window mask built from mode-2 sprites.</summary>
    private readonly byte[] _objWindowMask = new byte[Width * Height];

    /// <summary>Diagnostic: skip OBJ rendering to inspect BG layers only.</summary>
    public bool DisableObj { get; set; }
    /// <summary>Diagnostic: skip BG rendering to inspect OBJ only.</summary>
    public bool DisableBg { get; set; }
    /// <summary>Diagnostic: render only this OBJ index (-1 = all). Lets us look at one sprite's texture.</summary>
    public int OnlyObjIndex { get; set; } = -1;

    public void RenderFrame(GbaMemoryBus bus)
    {
        var dispcnt = bus.ReadHalfword(0x04000000);
        int mode = dispcnt & 0x7;
        bool forcedBlank = (dispcnt & 0x80) != 0;

        if (forcedBlank) { FillBlack(); return; }

        bool objEnableHw = (dispcnt & (1 << 12)) != 0;
        bool objEnable   = objEnableHw && !DisableObj;
        bool obj1D       = (dispcnt & (1 << 6))  != 0;
        bool objWindowOn = (dispcnt & (1 << 15)) != 0;

        // Bitmap modes write directly to the framebuffer (no layered
        // composite — the bitmap IS the BG2 content). OBJ + blending
        // is still applied on top.
        if (mode == 3) { RenderMode3(bus); ApplyObjOnBitmap(bus, dispcnt, obj1D, objEnable); return; }
        if (mode == 4) { RenderMode4(bus); ApplyObjOnBitmap(bus, dispcnt, obj1D, objEnable); return; }

        // Tile / affine modes go through the per-layer pipeline.
        ClearLayers();

        Array.Clear(_objWindowMask, 0, _objWindowMask.Length);
        // OBJ Window mask is built from mode-2 sprites — even when OBJ
        // drawing is disabled for diagnostic purposes (DisableObj), the
        // mask is still required so BG layers gated by WINOUT are
        // correctly visible inside the masked region.
        if (objEnableHw && objWindowOn) BuildObjWindowMask(bus, obj1D);

        if (!DisableBg)
        {
            switch (mode)
            {
                case 0:
                    // Mode 0: all four BGs are text-mode (tile-based, scrollable).
                    if ((dispcnt & (1 << 8))  != 0) RenderTextBgToLayer(bus, 0, _bg0Layer);
                    if ((dispcnt & (1 << 9))  != 0) RenderTextBgToLayer(bus, 1, _bg1Layer);
                    if ((dispcnt & (1 << 10)) != 0) RenderTextBgToLayer(bus, 2, _bg2Layer);
                    if ((dispcnt & (1 << 11)) != 0) RenderTextBgToLayer(bus, 3, _bg3Layer);
                    break;
                case 1:
                    // Mode 1: BG0/BG1 text-mode, BG2 affine, BG3 unused.
                    if ((dispcnt & (1 << 8))  != 0) RenderTextBgToLayer(bus, 0, _bg0Layer);
                    if ((dispcnt & (1 << 9))  != 0) RenderTextBgToLayer(bus, 1, _bg1Layer);
                    if ((dispcnt & (1 << 10)) != 0) RenderAffineBgToLayer(bus, 2, _bg2Layer);
                    break;
                case 2:
                    if ((dispcnt & (1 << 10)) != 0) RenderAffineBgToLayer(bus, 2, _bg2Layer);
                    if ((dispcnt & (1 << 11)) != 0) RenderAffineBgToLayer(bus, 3, _bg3Layer);
                    break;
            }
        }

        if (objEnable) RenderObjectsToLayer(bus, obj1D);

        Composite(bus, dispcnt);
    }

    /// <summary>
    /// Bitmap-mode helper: OBJ sprites + simple mode-1 alpha blend on
    /// top of an already-rendered Mode 3/4 framebuffer. Skips the full
    /// per-layer composite (since a bitmap mode has no layered BGs).
    /// </summary>
    private void ApplyObjOnBitmap(GbaMemoryBus bus, ushort dispcnt, bool obj1D, bool objEnable)
    {
        if (!objEnable) return;
        bool objWindowOn = (dispcnt & (1 << 15)) != 0;
        Array.Clear(_objWindowMask, 0, _objWindowMask.Length);
        if (objWindowOn) BuildObjWindowMask(bus, obj1D);

        ushort bldAlpha = bus.ReadHalfword(0x04000052);
        int eva = System.Math.Min(16,  bldAlpha       & 0x1F);
        int evb = System.Math.Min(16, (bldAlpha >> 8) & 0x1F);

        // Rasterise OBJ entries directly onto the framebuffer in priority order.
        for (int prio = 3; prio >= 0; prio--)
        {
            for (int i = 127; i >= 0; i--)
            {
                int oamOff = i * 8;
                ushort a0 = (ushort)(bus.Oam[oamOff    ] | (bus.Oam[oamOff + 1] << 8));
                ushort a1 = (ushort)(bus.Oam[oamOff + 2] | (bus.Oam[oamOff + 3] << 8));
                ushort a2 = (ushort)(bus.Oam[oamOff + 4] | (bus.Oam[oamOff + 5] << 8));
                bool affine = (a0 & (1 << 8)) != 0;
                if (!affine && (a0 & (1 << 9)) != 0) continue;
                int objMode = (a0 >> 10) & 0x3;
                if (objMode == 2) continue;
                if (((a2 >> 10) & 0x3) != prio) continue;

                bool semi = objMode == 1;
                RasterizeObjPixelsPaletted(bus, a0, a1, a2, obj1D, (sx, sy, color) =>
                {
                    if (!LayerVisibleAt(bus, /*OBJ*/4, sx, sy, dispcnt)) return;
                    int dst = (sy * Width + sx) * 3;
                    if (semi)
                    {
                        BlendInto(dst, color, eva, evb);
                    }
                    else
                    {
                        Framebuffer[dst    ] = (byte)((color        & 0x1F) << 3);
                        Framebuffer[dst + 1] = (byte)(((color >> 5) & 0x1F) << 3);
                        Framebuffer[dst + 2] = (byte)(((color >> 10) & 0x1F) << 3);
                    }
                });
            }
        }
    }

    private void BlendInto(int dst, ushort srcColor555, int eva, int evb)
    {
        int sr = (srcColor555        & 0x1F);
        int sg = ((srcColor555 >> 5) & 0x1F);
        int sb = ((srcColor555 >> 10) & 0x1F);
        int dr = Framebuffer[dst    ] >> 3;
        int dg = Framebuffer[dst + 1] >> 3;
        int db = Framebuffer[dst + 2] >> 3;
        int r = System.Math.Min(31, (sr * eva + dr * evb) >> 4);
        int g = System.Math.Min(31, (sg * eva + dg * evb) >> 4);
        int b = System.Math.Min(31, (sb * eva + db * evb) >> 4);
        Framebuffer[dst    ] = (byte)(r << 3);
        Framebuffer[dst + 1] = (byte)(g << 3);
        Framebuffer[dst + 2] = (byte)(b << 3);
    }

    private void ClearLayers()
    {
        Array.Clear(_bg0Layer, 0, _bg0Layer.Length);
        Array.Clear(_bg1Layer, 0, _bg1Layer.Length);
        Array.Clear(_bg2Layer, 0, _bg2Layer.Length);
        Array.Clear(_bg3Layer, 0, _bg3Layer.Length);
        Array.Clear(_objLayer, 0, _objLayer.Length);
        Array.Clear(_objPriority, 0, _objPriority.Length);
        Array.Clear(_objSemiTrans, 0, _objSemiTrans.Length);
    }

    // ---------------- Mode 3/4 (bitmap) ----------------

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
                Framebuffer[dst++] = (byte)((px        & 0x1F) << 3);
                Framebuffer[dst++] = (byte)(((px >> 5) & 0x1F) << 3);
                Framebuffer[dst++] = (byte)(((px >> 10) & 0x1F) << 3);
            }
        }
    }

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

    // ---------------- Mode 2 affine BG (per-layer buffer) ----------------

    private void RenderAffineBgToLayer(GbaMemoryBus bus, int bgIndex, ushort[] layer)
    {
        // BG control register MMIO addresses:
        //   BG0CNT 0x04000008, BG1CNT 0x0400000A, BG2CNT 0x0400000C, BG3CNT 0x0400000E
        uint cntAddr = bgIndex == 2 ? 0x0400000Cu : 0x0400000Eu;
        uint paBase  = bgIndex == 2 ? 0x04000020u : 0x04000030u;

        ushort cnt = bus.ReadHalfword(cntAddr);
        int charBase   = ((cnt >> 2) & 0x3) * 0x4000;
        int screenBase = ((cnt >> 8) & 0x1F) * 0x800;
        int sizeIdx    = (cnt >> 14) & 0x3;
        int sideTiles  = sizeIdx switch { 0 => 16, 1 => 32, 2 => 64, _ => 128 };
        int sidePixels = sideTiles * 8;
        bool wrap      = (cnt & (1 << 13)) != 0;

        short pa = (short)bus.ReadHalfword(paBase + 0);
        short pb = (short)bus.ReadHalfword(paBase + 2);
        short pc = (short)bus.ReadHalfword(paBase + 4);
        short pd = (short)bus.ReadHalfword(paBase + 6);
        int x = SignExtend28(bus.ReadWord(paBase + 8));
        int y = SignExtend28(bus.ReadWord(paBase + 12));

        for (int sy = 0; sy < Height; sy++)
        {
            int curX = x + pb * sy;
            int curY = y + pd * sy;
            int rowBase = sy * Width;
            for (int sx = 0; sx < Width; sx++, curX += pa, curY += pc)
            {
                int bgX = curX >> 8;
                int bgY = curY >> 8;
                if (wrap)
                {
                    bgX = ((bgX % sidePixels) + sidePixels) % sidePixels;
                    bgY = ((bgY % sidePixels) + sidePixels) % sidePixels;
                }
                else if ((uint)bgX >= sidePixels || (uint)bgY >= sidePixels) continue;

                int tileX = bgX >> 3;
                int tileY = bgY >> 3;
                int mapOff = screenBase + tileY * sideTiles + tileX;
                if ((uint)mapOff >= bus.Vram.Length) continue;
                byte tileId = bus.Vram[mapOff];
                int pixInTileX = bgX & 7;
                int pixInTileY = bgY & 7;
                int tileOff = charBase + tileId * 64 + pixInTileY * 8 + pixInTileX;
                if ((uint)tileOff >= bus.Vram.Length) continue;
                byte palIdx = bus.Vram[tileOff];
                if (palIdx == 0) continue;

                int palOff = palIdx * 2;
                ushort px = (ushort)(bus.Palette[palOff] | (bus.Palette[palOff + 1] << 8));
                layer[rowBase + sx] = (ushort)(px | ValidBit);
            }
        }
    }

    // ---------------- Text-mode BG (mode 0/1) ----------------

    /// <summary>
    /// Tile-based scrollable BG used by mode 0 (all four BGs) and
    /// mode 1 (BG0/BG1). Per GBATEK:
    /// <list type="bullet">
    ///   <item>BGxCNT: bit 7 = 8bpp/4bpp, bits 14-15 = size
    ///         (0=256×256, 1=512×256, 2=256×512, 3=512×512).</item>
    ///   <item>BGxHOFS / BGxVOFS: 9-bit scroll offsets.</item>
    ///   <item>Map data: 16-bit entries, packed as
    ///         <c>tile_id (10) | hflip (1) | vflip (1) | palette# (4)</c>.
    ///         For sizes &gt; 256 the BG is split into 1-4 screen blocks
    ///         (SCs) of 32×32 tiles each, laid out as
    ///         <code>[SC0] [SC1]
    ///         [SC2] [SC3]</code> in linear screenBase memory.</item>
    ///   <item>Tile pixel data: 4bpp = 32 B/tile, 8bpp = 64 B/tile,
    ///         in row-major order. 4bpp uses palette# from the map
    ///         entry (16 colours per palette × 16 palettes).</item>
    /// </list>
    /// jsmolka's "All tests passed" text and most commercial GBA games
    /// use mode 0 BG layers for their UI / level rendering.
    /// </summary>
    private void RenderTextBgToLayer(GbaMemoryBus bus, int bgIndex, ushort[] layer)
    {
        // BGxCNT @ 0x04000008 + bgIndex × 2;
        // BGxHOFS @ 0x04000010 + bgIndex × 4;  BGxVOFS @ 0x04000012 + bgIndex × 4.
        uint cntAddr  = 0x04000008u + (uint)bgIndex * 2;
        uint hofsAddr = 0x04000010u + (uint)bgIndex * 4;
        uint vofsAddr = 0x04000012u + (uint)bgIndex * 4;

        ushort cnt = bus.ReadHalfword(cntAddr);
        int hofs   = bus.ReadHalfword(hofsAddr) & 0x1FF;
        int vofs   = bus.ReadHalfword(vofsAddr) & 0x1FF;

        int charBase   = ((cnt >> 2) & 0x3) * 0x4000;
        int screenBase = ((cnt >> 8) & 0x1F) * 0x800;
        bool color8    = (cnt & (1 << 7)) != 0;
        int sizeIdx    = (cnt >> 14) & 0x3;
        int bgWidth    = (sizeIdx & 1) != 0 ? 512 : 256;
        int bgHeight   = (sizeIdx & 2) != 0 ? 512 : 256;
        int layerBit   = bgIndex;        // for window gating (handled in Composite)

        for (int sy = 0; sy < Height; sy++)
        {
            int bgY = (sy + vofs) & (bgHeight - 1);
            int rowBase = sy * Width;
            int mapTileY = (bgY & 0xFF) >> 3;
            int pixInTileY = bgY & 7;

            for (int sx = 0; sx < Width; sx++)
            {
                int bgX = (sx + hofs) & (bgWidth - 1);

                // Pick screen block (SC) for sizes > 256:
                //   size 1 (512×256): bgX≥256 → SC1
                //   size 2 (256×512): bgY≥256 → SC1
                //   size 3 (512×512): both bits → SC0/1/2/3
                int sc = 0;
                if ((sizeIdx & 1) != 0 && bgX >= 256) sc += 1;
                if ((sizeIdx & 2) != 0 && bgY >= 256) sc += (sizeIdx == 3) ? 2 : 1;

                int mapTileX = (bgX & 0xFF) >> 3;
                int mapEntryOff = screenBase + sc * 0x800 + (mapTileY * 32 + mapTileX) * 2;
                if ((uint)(mapEntryOff + 1) >= bus.Vram.Length) continue;
                ushort mapEntry = (ushort)(bus.Vram[mapEntryOff] | (bus.Vram[mapEntryOff + 1] << 8));

                int tileId    = mapEntry & 0x3FF;
                bool hflip    = (mapEntry & (1 << 10)) != 0;
                bool vflip    = (mapEntry & (1 << 11)) != 0;
                int paletteN  = (mapEntry >> 12) & 0xF;

                int pixInTileX = bgX & 7;
                int tx = hflip ? 7 - pixInTileX : pixInTileX;
                int ty = vflip ? 7 - pixInTileY : pixInTileY;

                byte palIdx;
                if (color8)
                {
                    int tileOff = charBase + tileId * 64 + ty * 8 + tx;
                    if ((uint)tileOff >= bus.Vram.Length) continue;
                    palIdx = bus.Vram[tileOff];
                }
                else
                {
                    int tileOff = charBase + tileId * 32 + ty * 4 + (tx >> 1);
                    if ((uint)tileOff >= bus.Vram.Length) continue;
                    byte twoPix = bus.Vram[tileOff];
                    palIdx = (byte)((tx & 1) == 0 ? (twoPix & 0xF) : (twoPix >> 4));
                }
                if (palIdx == 0) continue;     // 4bpp palIdx 0 OR 8bpp idx 0 = transparent

                int palOff = (color8 ? palIdx : (paletteN * 16 + palIdx)) * 2;
                if ((uint)(palOff + 1) >= bus.Palette.Length) continue;
                ushort px = (ushort)(bus.Palette[palOff] | (bus.Palette[palOff + 1] << 8));
                layer[rowBase + sx] = (ushort)(px | ValidBit);
            }
        }
    }

    // ---------------- OBJ sprites (per-layer buffer) ----------------

    private void RenderObjectsToLayer(GbaMemoryBus bus, bool oneDimensional)
    {
        // Render in REVERSE priority order so lower-priority pixels go
        // down first and higher-priority sprites overwrite. Within a
        // priority bucket, lower OBJ index draws on top.
        for (int prio = 3; prio >= 0; prio--)
        {
            for (int i = 127; i >= 0; i--)
            {
                if (OnlyObjIndex >= 0 && i != OnlyObjIndex) continue;
                int oamOff = i * 8;
                ushort a0 = (ushort)(bus.Oam[oamOff    ] | (bus.Oam[oamOff + 1] << 8));
                ushort a1 = (ushort)(bus.Oam[oamOff + 2] | (bus.Oam[oamOff + 3] << 8));
                ushort a2 = (ushort)(bus.Oam[oamOff + 4] | (bus.Oam[oamOff + 5] << 8));
                bool affine = (a0 & (1 << 8)) != 0;
                if (!affine && (a0 & (1 << 9)) != 0) continue;
                int objMode = (a0 >> 10) & 0x3;
                if (objMode == 2) continue;        // OBJ-Window: handled in BuildObjWindowMask
                int spritePri = (a2 >> 10) & 0x3;
                if (spritePri != prio) continue;

                bool semi = objMode == 1;
                byte priByte = (byte)spritePri;
                RasterizeObjPixelsPaletted(bus, a0, a1, a2, oneDimensional, (sx, sy, color) =>
                {
                    int idx = sy * Width + sx;
                    _objLayer[idx]    = (ushort)(color | ValidBit);
                    _objPriority[idx] = priByte;
                    _objSemiTrans[idx] = semi ? (byte)1 : (byte)0;
                });
            }
        }
    }

    // ---------------- OBJ-Window mask ----------------

    private void BuildObjWindowMask(GbaMemoryBus bus, bool oneDimensional)
    {
        for (int i = 0; i < 128; i++)
        {
            int oamOff = i * 8;
            ushort a0 = (ushort)(bus.Oam[oamOff    ] | (bus.Oam[oamOff + 1] << 8));
            ushort a1 = (ushort)(bus.Oam[oamOff + 2] | (bus.Oam[oamOff + 3] << 8));
            ushort a2 = (ushort)(bus.Oam[oamOff + 4] | (bus.Oam[oamOff + 5] << 8));
            int objMode = (a0 >> 10) & 0x3;
            if (objMode != 2) continue;
            bool affine = (a0 & (1 << 8)) != 0;
            if (!affine && (a0 & (1 << 9)) != 0) continue;
            RasterizeObjPixelsPaletted(bus, a0, a1, a2, oneDimensional, (sx, sy, _) =>
            {
                _objWindowMask[sy * Width + sx] = 1;
            });
        }
    }

    // ---------------- OBJ rasteriser core ----------------

    /// <summary>
    /// Walk an OBJ entry's bounding box, decoding affine / flip / 4bpp /
    /// 8bpp / 1D vs 2D mapping, and call <paramref name="emit"/> with
    /// (sx, sy, RGB555 colour) for each non-transparent pixel.
    /// </summary>
    private static void RasterizeObjPixelsPaletted(
        GbaMemoryBus bus, ushort a0, ushort a1, ushort a2,
        bool oneDimensional, System.Action<int, int, ushort> emit)
    {
        bool affine    = (a0 & (1 << 8))  != 0;
        bool color8    = (a0 & (1 << 13)) != 0;
        int  paletteN  = color8 ? 0 : (a2 >> 12) & 0xF;
        int  tileBase  = a2 & 0x3FF;
        int  yOrigin   = a0 & 0xFF;
        int  xOrigin   = a1 & 0x1FF;
        bool doubleSize = affine && (a0 & (1 << 9)) != 0;
        int  affineIdx = affine ? (a1 >> 9) & 0x1F : 0;
        bool hflip = !affine && (a1 & (1 << 12)) != 0;
        bool vflip = !affine && (a1 & (1 << 13)) != 0;
        int shape = (a0 >> 14) & 0x3, size = (a1 >> 14) & 0x3;
        var (sw, sh) = SpriteDimensions(shape, size);
        int boxW = doubleSize ? sw * 2 : sw;
        int boxH = doubleSize ? sh * 2 : sh;
        int sxOrigin = xOrigin >= 256 ? xOrigin - 512 : xOrigin;
        int syOrigin = yOrigin >= 160 ? yOrigin - 256 : yOrigin;

        int pa = 0x100, pb = 0, pc = 0, pd = 0x100;
        if (affine)
        {
            int matBase = affineIdx * 32;
            pa = (short)(bus.Oam[matBase + 0x06] | (bus.Oam[matBase + 0x07] << 8));
            pb = (short)(bus.Oam[matBase + 0x0E] | (bus.Oam[matBase + 0x0F] << 8));
            pc = (short)(bus.Oam[matBase + 0x16] | (bus.Oam[matBase + 0x17] << 8));
            pd = (short)(bus.Oam[matBase + 0x1E] | (bus.Oam[matBase + 0x1F] << 8));
        }
        int halfW = boxW / 2, halfH = boxH / 2;
        // Per GBATEK "OBJ Memory Map":
        //   1D mapping: tiles for one sprite are contiguous → stride = sw/8
        //   2D 4bpp:    32 tiles per row in the 32×32 OBJ tile grid
        //   2D 8bpp:    16 tiles per row in the 32×16 grid (8bpp tiles take
        //               two 4bpp slots each, so the effective horizontal
        //               stride is halved). mGBA's software-obj.c uses
        //               stride = 0x80 bytes for this case = 16 tiles ×
        //               8 bytes per scanline.
        int tilesPerRow  = oneDimensional ? sw / 8 : (color8 ? 16 : 32);
        int bytesPerTile = color8 ? 64 : 32;

        for (int dy = 0; dy < boxH; dy++)
        {
            int sy = syOrigin + dy;
            if ((uint)sy >= Height) continue;
            for (int dx = 0; dx < boxW; dx++)
            {
                int sx = sxOrigin + dx;
                if ((uint)sx >= Width) continue;
                int tx, ty;
                if (affine)
                {
                    int rx = dx - halfW, ry = dy - halfH;
                    tx = (pa * rx + pb * ry) >> 8;
                    ty = (pc * rx + pd * ry) >> 8;
                    tx += sw / 2; ty += sh / 2;
                    if ((uint)tx >= sw || (uint)ty >= sh) continue;
                }
                else
                {
                    tx = hflip ? (sw - 1 - dx) : dx;
                    ty = vflip ? (sh - 1 - dy) : dy;
                }
                int tileX = tx >> 3, tileY = ty >> 3;
                int tileId = color8
                    ? (tileBase / 2) + (tileY * tilesPerRow + tileX)
                    : tileBase + (tileY * tilesPerRow + tileX);
                int vramTileOff = 0x10000 + tileId * bytesPerTile;
                int pixInTileX = tx & 7, pixInTileY = ty & 7;
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
                    palIdx = (byte)((pixInTileX & 1) == 0 ? (twoPix & 0xF) : (twoPix >> 4));
                }
                if (palIdx == 0) continue;

                int palOff = 0x200 + (color8 ? palIdx : (paletteN * 16 + palIdx)) * 2;
                if ((uint)(palOff + 1) >= bus.Palette.Length) continue;
                ushort px = (ushort)(bus.Palette[palOff] | (bus.Palette[palOff + 1] << 8));
                emit(sx, sy, px);
            }
        }
    }

    // ---------------- Composite (BLDCNT / BLDALPHA / BLDY) ----------------

    /// <summary>
    /// Walk every screen pixel; pick topmost + second-topmost opaque
    /// layer per BG / OBJ priority interleaving rules; apply BLDCNT
    /// blending mode (alpha / brighten / darken) plus per-pixel OBJ
    /// semi-transparent override; write final RGB to Framebuffer.
    /// </summary>
    private void Composite(GbaMemoryBus bus, ushort dispcnt)
    {
        ushort bldcnt   = bus.ReadHalfword(0x04000050);
        ushort bldAlpha = bus.ReadHalfword(0x04000052);
        ushort bldy     = bus.ReadHalfword(0x04000054);
        int blendMode = (bldcnt >> 6) & 0x3;
        int t1Bits = bldcnt & 0x3F;
        int t2Bits = (bldcnt >> 8) & 0x3F;
        int eva = System.Math.Min(16,  bldAlpha       & 0x1F);
        int evb = System.Math.Min(16, (bldAlpha >> 8) & 0x1F);
        int evy = System.Math.Min(16,  bldy           & 0x1F);

        // BG priorities (lower priority value = drawn ON TOP) per BGxCNT bits 0-1.
        // BG0CNT 0x04000008, BG1CNT 0x0400000A, BG2CNT 0x0400000C, BG3CNT 0x0400000E.
        int p0 = bus.ReadHalfword(0x04000008) & 0x3;
        int p1 = bus.ReadHalfword(0x0400000A) & 0x3;
        int p2 = bus.ReadHalfword(0x0400000C) & 0x3;
        int p3 = bus.ReadHalfword(0x0400000E) & 0x3;
        bool bg0On = (dispcnt & (1 << 8))  != 0;
        bool bg1On = (dispcnt & (1 << 9))  != 0;
        bool bg2On = (dispcnt & (1 << 10)) != 0;
        bool bg3On = (dispcnt & (1 << 11)) != 0;

        ushort backdrop = (ushort)(bus.Palette[0] | (bus.Palette[1] << 8));

        const int LBG0 = 0, LBG1 = 1, LBG2 = 2, LBG3 = 3, LOBJ = 4, LBD = 5;

        for (int y = 0; y < Height; y++)
        {
            int rowBase = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int idx = rowBase + x;

                // Find topmost + second-topmost visible opaque layer.
                int topLayer = -1, secondLayer = -1;
                ushort topColor = 0, secondColor = 0;
                bool topSemi = false;

                for (int prio = 0; prio < 4 && (topLayer == -1 || secondLayer == -1); prio++)
                {
                    // OBJ at this priority (highest precedence within priority).
                    if ((_objLayer[idx] & ValidBit) != 0 && _objPriority[idx] == prio
                        && LayerVisibleAt(bus, LOBJ, x, y, dispcnt))
                    {
                        ushort c = (ushort)(_objLayer[idx] & 0x7FFF);
                        bool semi = _objSemiTrans[idx] != 0;
                        if (topLayer == -1)
                        {
                            topLayer = LOBJ; topColor = c; topSemi = semi;
                        }
                        else if (secondLayer == -1)
                        {
                            secondLayer = LOBJ; secondColor = c;
                        }
                    }
                    // BGs at this priority, in numeric order (BG0 highest).
                    if (bg0On && p0 == prio) ConsiderBg(_bg0Layer, idx, LBG0, x, y, dispcnt, bus, ref topLayer, ref topColor, ref topSemi, ref secondLayer, ref secondColor);
                    if (bg1On && p1 == prio) ConsiderBg(_bg1Layer, idx, LBG1, x, y, dispcnt, bus, ref topLayer, ref topColor, ref topSemi, ref secondLayer, ref secondColor);
                    if (bg2On && p2 == prio) ConsiderBg(_bg2Layer, idx, LBG2, x, y, dispcnt, bus, ref topLayer, ref topColor, ref topSemi, ref secondLayer, ref secondColor);
                    if (bg3On && p3 == prio) ConsiderBg(_bg3Layer, idx, LBG3, x, y, dispcnt, bus, ref topLayer, ref topColor, ref topSemi, ref secondLayer, ref secondColor);
                }

                // Backdrop fills any still-empty slot.
                if (topLayer == -1)    { topLayer    = LBD; topColor    = backdrop; }
                if (secondLayer == -1) { secondLayer = LBD; secondColor = backdrop; }

                bool topIsT1    = (t1Bits & (1 << topLayer))    != 0;
                bool secondIsT2 = (t2Bits & (1 << secondLayer)) != 0;

                ushort finalColor;
                if (topSemi && secondIsT2)
                {
                    // OBJ semi-transparent always alpha-blends regardless of BLDCNT.mode.
                    finalColor = AlphaBlend(topColor, secondColor, eva, evb);
                }
                else if (blendMode == 1 && topIsT1 && secondIsT2)
                {
                    finalColor = AlphaBlend(topColor, secondColor, eva, evb);
                }
                else if (blendMode == 2 && topIsT1)
                {
                    finalColor = Brighten(topColor, evy);
                }
                else if (blendMode == 3 && topIsT1)
                {
                    finalColor = Darken(topColor, evy);
                }
                else
                {
                    finalColor = topColor;
                }

                int dst = idx * 3;
                Framebuffer[dst    ] = (byte)((finalColor        & 0x1F) << 3);
                Framebuffer[dst + 1] = (byte)(((finalColor >> 5) & 0x1F) << 3);
                Framebuffer[dst + 2] = (byte)(((finalColor >> 10) & 0x1F) << 3);
            }
        }
    }

    private void ConsiderBg(ushort[] layer, int idx, int layerId, int x, int y, ushort dispcnt,
        GbaMemoryBus bus, ref int topLayer, ref ushort topColor, ref bool topSemi,
        ref int secondLayer, ref ushort secondColor)
    {
        if ((layer[idx] & ValidBit) == 0) return;
        if (!LayerVisibleAt(bus, layerId, x, y, dispcnt)) return;
        ushort c = (ushort)(layer[idx] & 0x7FFF);
        if (topLayer == -1)
        {
            topLayer = layerId; topColor = c; topSemi = false;
        }
        else if (secondLayer == -1)
        {
            secondLayer = layerId; secondColor = c;
        }
    }

    // ---------------- Blend math (RGB555 in, RGB555 out) ----------------

    private static ushort AlphaBlend(ushort a, ushort b, int eva, int evb)
    {
        int ar = (a        & 0x1F), ag = ((a >> 5) & 0x1F), ab = ((a >> 10) & 0x1F);
        int br = (b        & 0x1F), bg = ((b >> 5) & 0x1F), bb = ((b >> 10) & 0x1F);
        int r = System.Math.Min(31, (ar * eva + br * evb) >> 4);
        int g = System.Math.Min(31, (ag * eva + bg * evb) >> 4);
        int bch = System.Math.Min(31, (ab * eva + bb * evb) >> 4);
        return (ushort)(r | (g << 5) | (bch << 10));
    }

    private static ushort Brighten(ushort c, int evy)
    {
        // result = top + ((31 - top) * EVY) >> 4   per channel
        int r = (c        & 0x1F), g = ((c >> 5) & 0x1F), b = ((c >> 10) & 0x1F);
        r = r + (((31 - r) * evy) >> 4);
        g = g + (((31 - g) * evy) >> 4);
        b = b + (((31 - b) * evy) >> 4);
        return (ushort)(r | (g << 5) | (b << 10));
    }

    private static ushort Darken(ushort c, int evy)
    {
        // result = top - (top * EVY) >> 4   per channel
        int r = (c        & 0x1F), g = ((c >> 5) & 0x1F), b = ((c >> 10) & 0x1F);
        r = r - ((r * evy) >> 4);
        g = g - ((g * evy) >> 4);
        b = b - ((b * evy) >> 4);
        return (ushort)(r | (g << 5) | (b << 10));
    }

    // ---------------- Window visibility ----------------

    private bool LayerVisibleAt(GbaMemoryBus bus, int layerBit, int sx, int sy, ushort dispcnt)
    {
        bool win0On = (dispcnt & (1 << 13)) != 0;
        bool win1On = (dispcnt & (1 << 14)) != 0;
        bool objWindowOn = (dispcnt & (1 << 15)) != 0;
        if (!win0On && !win1On && !objWindowOn) return true;

        ushort win0H = bus.ReadHalfword(0x04000040);
        ushort win1H = bus.ReadHalfword(0x04000042);
        ushort win0V = bus.ReadHalfword(0x04000044);
        ushort win1V = bus.ReadHalfword(0x04000046);
        ushort winin  = bus.ReadHalfword(0x04000048);
        ushort winout = bus.ReadHalfword(0x0400004A);

        if (win0On && InsideRect(sx, sy, win0H, win0V))
            return (winin & (1 << layerBit)) != 0;
        if (win1On && InsideRect(sx, sy, win1H, win1V))
            return (winin & (1 << (layerBit + 8))) != 0;
        if (objWindowOn && _objWindowMask[sy * Width + sx] != 0)
            return (winout & (1 << (layerBit + 8))) != 0;
        return (winout & (1 << layerBit)) != 0;
    }

    private static bool InsideRect(int sx, int sy, ushort h, ushort v)
    {
        int x1 = (h >> 8) & 0xFF, x2 = h & 0xFF;
        int y1 = (v >> 8) & 0xFF, y2 = v & 0xFF;
        if (x2 == 0 || x2 > Width)  x2 = Width;
        if (y2 == 0 || y2 > Height) y2 = Height;
        bool xIn = x1 <= x2 ? (sx >= x1 && sx < x2) : (sx >= x1 || sx < x2);
        bool yIn = y1 <= y2 ? (sy >= y1 && sy < y2) : (sy >= y1 || sy < y2);
        return xIn && yIn;
    }

    // ---------------- Misc helpers ----------------

    private static (int width, int height) SpriteDimensions(int shape, int size)
    {
        return (shape, size) switch
        {
            (0, 0) => (8,  8),  (0, 1) => (16, 16), (0, 2) => (32, 32), (0, 3) => (64, 64),
            (1, 0) => (16, 8),  (1, 1) => (32, 8),  (1, 2) => (32, 16), (1, 3) => (64, 32),
            (2, 0) => (8, 16),  (2, 1) => (8,  32), (2, 2) => (16, 32), (2, 3) => (32, 64),
            _      => (8, 8),
        };
    }

    private static int SignExtend28(uint v)
    {
        v &= 0x0FFFFFFFu;
        return (v & 0x08000000u) != 0 ? (int)(v | 0xF0000000u) : (int)v;
    }

    private void FillBlack() => Array.Clear(Framebuffer, 0, Framebuffer.Length);
}
