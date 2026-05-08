// NES PPU (Picture Processing Unit) — ported from erspicu/AprNes
// commit fcbbb23 (2026-02-21).
//
// Original is a cycle-accurate per-pixel renderer interleaved with
// CPU at PPU clock granularity. We adapt it to the AprCpu framework's
// per-instruction backend semantics: PPU.Tick(cpuCycles) accumulates
// PPU dots, and RenderFrame() does an end-of-frame full-screen scan
// for screenshot capture. Sprite-zero-hit and pixel-level timing are
// preserved best-effort but not bit-accurate (acceptable for the
// validation-screenshot use case; cycle-accurate pixel-level timing
// is a separate downstream goal that may stay outside the framework
// per MD/design/16-emulator-completeness.md's PPU-stays-platform-side
// rationale).
//
// Source: NES/AprNes/NesCore/PPU.cs (the partial-class NesCore PPU
// file flattened into a standalone NesPpu for use here).

using System;
using System.Runtime.CompilerServices;
using AprNes.Cli.Memory;

namespace AprNes.Cli.Video
{
    public sealed unsafe class NesPpu
    {
        public const int Width = 256;
        public const int Height = 240;

        // ---- caller-provided state ----
        private readonly byte[] _vram;        // 16KB nametable region (matches source's ppu_ram)
        private readonly byte[] _oam;         // 256 bytes sprite RAM (matches source's spr_ram)
        private readonly byte[] _paletteRam;  // 32 bytes palette mirror; $3F00-$3F1F authoritative copy lives in _vram
        private readonly IMapper _mapper;     // for $0000-$1FFF pattern-table reads (CHR)
        private bool _verticalMirroring;

        // ---- 256x240 RGB framebuffer (R,G,B per pixel) ----
        private readonly byte[] _framebuffer = new byte[256 * 240 * 3];

        // Internal 32bpp scratch (one uint per pixel, 0xAARRGGBB) used by
        // the per-pixel renderer; converted to RGB triples in Framebuffer.
        private readonly uint[] _screenBuf1x = new uint[256 * 240];

        // BG pixel index buffer (0..3) for sprite-priority compositing.
        private readonly int[] _bufferBgArray = new int[256 * 240];

        public int frame_count = 0;
        public int ppu_cycles_x = 0, scanline = -1;

        //Palette ref http://www.thealmightyguru.com/Games/Hacking/Wiki/index.php?title=NES_Palette & http://www.dev.bowdenweb.com/nes/nes-color-palette.html
        private static readonly uint[] NesColorsData =  {
            0xFF7C7C7C,0xFF0000FC,0xFF0000BC,0xFF4428BC,0xFF940084,0xFFA80020,0xFFA81000,0xFF881400,
            0xFF503000,0xFF007800,0xFF006800,0xFF005800,0xFF004058,0xFF000000,0xFF000000,0xFF000000,
            0xFFBCBCBC,0xFF0078F8,0xFF0058F8,0xFF6844FC,0xFFD800CC,0xFFE40058,0xFFF83800,0xFFE45C10,
            0xFFAC7C00,0xFF00B800,0xFF00A800,0xFF00A844,0xFF008888,0xFF000000,0xFF000000,0xFF000000,
            0xFFF8F8F8,0xFF3CBCFC,0xFF6888FC,0xFF9878F8,0xFFF878F8,0xFFF85898,0xFFF87858,0xFFFCA044,
            0xFFF8B800,0xFFB8F818,0xFF58D854,0xFF58F898,0xFF00E8D8,0xFF787878,0xFF000000,0xFF000000,
            0xFFFCFCFC,0xFFA4E4FC,0xFFB8B8F8,0xFFD8B8F8,0xFFF8B8F8,0xFFF8A4C0,0xFFF0D0B0,0xFFFCE0A8,
            0xFFF8D878,0xFFD8F878,0xFFB8F8B8,0xFFB8F8D8,0xFF00FCFC,0xFFF8D8F8,0xFF000000,0xFF000000 };

        //table form blargg_ppu  power_up_palette.asm test rom source
        private static readonly byte[] defaultPal = { 0x09, 0x01, 0x00, 0x01, 0x00, 0x02, 0x02, 0x0D, 0x08, 0x10, 0x08, 0x24, 0x00, 0x00, 0x04, 0x2C, 0x09, 0x01, 0x34, 0x03, 0x00, 0x04, 0x00, 0x14, 0x08, 0x3A, 0x00, 0x02, 0x00, 0x20, 0x2C, 0x08 };

        //ppu ctrl 0x2000
        private int BaseNameTableAddr = 0, VramaddrIncrement = 1, SpPatternTableAddr = 0, BgPatternTableAddr = 0;
        private bool Spritesize8x16 = false, NMIable = false;

        //ppu mask 0x2001
        public bool ShowBackGround = false, ShowSprites = false;
        private bool ShowBgLeft8 = true, ShowSprLeft8 = true; // bit1/bit2: show in leftmost 8 pixels

        //ppu status 0x2002.
        private bool isSpriteOverflow = false, isSprite0hit = false, isVblank = false;

        private int vram_addr_internal = 0, vram_addr = 0, scrol_y = 0, FineX = 0;
        private bool vram_latch = false;
        private byte ppu_2007_buffer = 0, ppu_2007_temp = 0;
        private byte spr_ram_add = 0;
        private bool oddSwap = false;

        // ---- NMI line ----
        private bool nmi_pending = false;

        // ---- open bus ----
        private byte openbus;
        private int open_bus_decay_timer = 77777;

        // ---- VBL suppression / look-ahead ----
        private bool SuppressVbl = false;

        // ---- end-of-frame trigger consumed by Tick() ----
        private bool _frameReady = false;

        //https://wiki.nesdev.com/w/index.php/PPU_scrolling

        public NesPpu(byte[] vram, byte[] oam, byte[] paletteRam, IMapper mapper, bool verticalMirroring)
        {
            _vram = vram ?? throw new ArgumentNullException(nameof(vram));
            _oam = oam ?? throw new ArgumentNullException(nameof(oam));
            _paletteRam = paletteRam ?? throw new ArgumentNullException(nameof(paletteRam));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _verticalMirroring = verticalMirroring;
            Reset();
        }

        public void Reset()
        {
            BaseNameTableAddr = 0;
            VramaddrIncrement = 1;
            SpPatternTableAddr = 0;
            BgPatternTableAddr = 0;
            Spritesize8x16 = false;
            NMIable = false;
            ShowBackGround = false;
            ShowSprites = false;
            ShowBgLeft8 = true;
            ShowSprLeft8 = true;
            isSpriteOverflow = false;
            isSprite0hit = false;
            isVblank = false;
            vram_addr_internal = 0;
            vram_addr = 0;
            scrol_y = 0;
            FineX = 0;
            vram_latch = false;
            ppu_2007_buffer = 0;
            ppu_2007_temp = 0;
            spr_ram_add = 0;
            oddSwap = false;
            nmi_pending = false;
            openbus = 0;
            open_bus_decay_timer = 77777;
            SuppressVbl = false;
            ppu_cycles_x = 0;
            scanline = -1;
            frame_count = 0;
            _frameReady = false;

            // Power-on palette (matches blargg_ppu power_up_palette).
            for (int i = 0; i < 32; i++)
            {
                _paletteRam[i] = defaultPal[i];
                _vram[0x3F00 + i] = defaultPal[i];
            }

            Array.Clear(_screenBuf1x, 0, _screenBuf1x.Length);
            Array.Clear(_bufferBgArray, 0, _bufferBgArray.Length);
            Array.Clear(_framebuffer, 0, _framebuffer.Length);
        }

        public void SetMirroring(bool verticalMirroring) => _verticalMirroring = verticalMirroring;

        // ---- public scheduler / NMI surface ----

        public bool NmiPending => nmi_pending;

        public bool ConsumeNmi()
        {
            if (nmi_pending) { nmi_pending = false; return true; }
            return false;
        }

        /// <summary>End-of-frame RGB framebuffer (256x240, 3 bytes per pixel).</summary>
        public byte[] Framebuffer => _framebuffer;

        /// <summary>
        /// Advance the PPU by N CPU cycles (= N*3 PPU dots). Drives
        /// scanline / dot counters, sets/clears VBL flag and fires NMI.
        /// </summary>
        public void Tick(int cpuCycles)
        {
            int ppu_remaining = cpuCycles * 3;
            for (int i = 0; i < ppu_remaining; i++) ppu_step_new();
        }

        /// <summary>True when the PPU has just completed a visible frame
        /// (scanline 240, cycle 1). Auto-clears on read.</summary>
        public bool ConsumeFrameReady()
        {
            if (_frameReady) { _frameReady = false; return true; }
            return false;
        }

        // ---- public register API ----

        public byte ReadRegister(ushort regAddr)
        {
            switch (regAddr & 7)
            {
                case 0: return openbus; // $2000 write-only
                case 1: return openbus; // $2001 write-only
                case 2: return ppu_r_2002();
                case 3: return openbus; // $2003 write-only
                case 4: return ppu_r_2004();
                case 5: return openbus; // $2005 write-only
                case 6: return openbus; // $2006 write-only
                case 7: return ppu_r_2007();
            }
            return openbus;
        }

        public void WriteRegister(ushort regAddr, byte value)
        {
            switch (regAddr & 7)
            {
                case 0: ppu_w_2000(value); break;
                case 1: ppu_w_2001(value); break;
                case 2: openbus = value; break; // $2002 read-only
                case 3: ppu_w_2003(value); break;
                case 4: ppu_w_2004(value); break;
                case 5: ppu_w_2005(value); break;
                case 6: ppu_w_2006(value); break;
                case 7: ppu_w_2007(value); break;
            }
        }

        // ---- PPU bus helpers (replace direct ppu_ram[]/MapperObj.MapperR_CHR()) ----

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte PpuBusRead(int addr)
        {
            addr &= 0x3FFF;
            if (addr < 0x2000) return _mapper.PpuRead((ushort)addr);
            if (addr < 0x3F00)
            {
                int nt = MirrorNametable(addr);
                return _vram[nt];
            }
            int p = addr & 0x1F;
            if ((p & 0x03) == 0) p &= 0x0C; // $3F10/$14/$18/$1C mirror to $3F00/$04/$08/$0C
            return _paletteRam[p];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void PpuBusWrite(int addr, byte value)
        {
            addr &= 0x3FFF;
            if (addr < 0x2000) { _mapper.PpuWrite((ushort)addr, value); return; }
            if (addr < 0x3F00)
            {
                int nt = MirrorNametable(addr);
                _vram[nt] = value;
                // Keep the legacy $2000-$2FFF region in _vram coherent for the
                // ppu_ram[] reads that still index by $2xxx directly.
                return;
            }
            int p = addr & 0x1F;
            if ((p & 0x03) == 0) p &= 0x0C;
            _paletteRam[p] = value;
            _vram[0x3F00 + p] = value;
        }

        // Mirror $2000-$3EFF to a [$2000..$2FFF] index in _vram, accounting
        // for the cartridge's nametable mirroring mode.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int MirrorNametable(int addr)
        {
            int a = (addr & 0x0FFF);   // 0x000-0xFFF in nametable space
            int table = (a >> 10) & 3; // 0..3
            int offset = a & 0x03FF;
            int phys;
            if (_verticalMirroring)
            {
                // tables 0/2 -> bank 0, 1/3 -> bank 1
                phys = ((table & 1) * 0x400) + offset;
            }
            else
            {
                // horizontal: 0/1 -> bank 0, 2/3 -> bank 1
                phys = ((table >> 1) * 0x400) + offset;
            }
            return 0x2000 + phys;
        }

        // Convenience: emulate the source's `ppu_ram[idx]` direct access for
        // the rendering path. Indices used in rendering are $2000-$2FFF and
        // $3F00-$3F1F; both of these are kept coherent in _vram.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte PpuRamDirect(int idx) => _vram[idx & 0x3FFF];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint NesColor(int idx) => NesColorsData[idx & 0x3F];

        // ============================================================
        // BELOW: cycle-accurate rendering core (verbatim from source,
        // adapted to instance-method form + helper accessors).
        // ============================================================

        #region cycle-accurate PPU

        // Coarse X increment
        private void CXinc()
        {
            if ((vram_addr & 0x001F) == 31)
            {
                vram_addr &= ~0x001F;
                vram_addr ^= 0x0400;
            }
            else
                vram_addr += 1;
        }

        // Y increment
        private void Yinc()
        {
            if ((vram_addr & 0x7000) != 0x7000)
                vram_addr += 0x1000;
            else
            {
                vram_addr &= ~0x7000;
                int y = (vram_addr & 0x03E0) >> 5;
                if (y == 29)
                {
                    y = 0;
                    vram_addr ^= 0x0800;
                }
                else if (y == 31)
                    y = 0;
                else
                    y += 1;
                vram_addr = (vram_addr & ~0x03E0) | (y << 5);
            }
        }

        // hori(v) = hori(t)
        private void CopyHoriV()
        {
            vram_addr = (vram_addr & ~0x041F) | (vram_addr_internal & 0x041F);
        }

        // ---- Tile fetch state ----
        private byte NTVal = 0, ATVal = 0, lowTile = 0, highTile = 0;
        private int ioaddr = 0;

        // ---- BG shift registers (16-bit, two tiles: high=current, low=next) ----
        private ushort lowshift = 0, highshift = 0;

        // ---- Attribute 3-stage pipeline ----
        // Phase-3 shifts ATVal into p1; phase-7 render reads p3 (2 groups later).
        // This correctly delays attribute by 2 fetch groups with no index drift.
        private byte bg_attr_p1 = 0, bg_attr_p2 = 0, bg_attr_p3 = 0;

        // Render 8 BG pixels at screen positions [ppu_cycles_x-7 .. ppu_cycles_x]
        // using shift registers BEFORE reload (high byte = current tile data).
        private void RenderBGTile()
        {
            byte renderAttr = bg_attr_p3;
            byte nextAttr   = bg_attr_p2;

            int baseX = ppu_cycles_x - 7;
            int scanOff = scanline << 8;
            for (int loc = 0; loc < 8; loc++)
            {
                int screenX = baseX + loc;
                if (screenX > 255) break;

                bool inLeft8 = screenX < 8;
                int bit = 15 - loc - FineX;           // 1..15 always (FineX 0..7, loc 0..7)
                byte attrUse = (bit >= 8) ? renderAttr : nextAttr;
                int bgPixel = ((lowshift >> bit) & 1) | (((highshift >> bit) & 1) << 1);

                int slot = scanOff + screenX;
                _bufferBgArray[slot] = (!ShowBgLeft8 && inLeft8) ? 0 : bgPixel;

                if (!ShowBgLeft8 && inLeft8)
                    _screenBuf1x[slot] = NesColor(PpuRamDirect(0x3f00));
                else if (bgPixel == 0)
                    _screenBuf1x[slot] = NesColor(PpuRamDirect(0x3f00));
                else
                    _screenBuf1x[slot] = NesColor(PpuRamDirect((0x3f00 | (attrUse << 2)) + bgPixel));

                // Sprite 0 hit detection (per-pixel, cycle-accurate)
                if (sprite0_on_line && !isSprite0hit && screenX != 255)
                {
                    int sprCol = screenX - sprite0_line_x;
                    if (sprCol >= 0 && sprCol < 8 && bgPixel != 0
                        && !(!ShowBgLeft8 && inLeft8) && !(!ShowSprLeft8 && inLeft8))
                    {
                        int loc_t = sprite0_flip_x ? (7 - sprCol) : sprCol;
                        int mask = 1 << (7 - loc_t);
                        int sprPixel = (((sprite0_tile_high & mask) << 1) + (sprite0_tile_low & mask)) >> (7 - loc_t);
                        if (sprPixel != 0)
                            isSprite0hit = true;
                    }
                }
            }
        }

        // Per-8-cycle tile fetch: runs each PPU cycle on visible/pre-render scanlines when rendering enabled.
        // BG tiles fetched at cycles 0-255 (visible) and 320-335 (next-scanline prefetch).
        // A12 transitions detected at CHR address setup cycles (phase 4 and 6).
        private void ppu_rendering_tick()
        {
            if (ppu_cycles_x < 256 || (ppu_cycles_x >= 320 && ppu_cycles_x < 336))
            {
                switch (ppu_cycles_x & 7)
                {
                    case 0:
                        ioaddr = 0x2000 | (vram_addr & 0x0FFF);
                        break;
                    case 1:
                        NTVal = PpuRamDirect(ioaddr);
                        break;
                    case 2:
                        ioaddr = 0x23C0 | (vram_addr & 0x0C00) | ((vram_addr >> 4) & 0x38) | ((vram_addr >> 2) & 0x07);
                        break;
                    case 3:
                        ATVal = (byte)((PpuRamDirect(ioaddr) >> (((vram_addr >> 4) & 0x04) | (vram_addr & 0x02))) & 0x03);
                        bg_attr_p3 = bg_attr_p2; bg_attr_p2 = bg_attr_p1; bg_attr_p1 = ATVal;
                        break;
                    case 4:
                        ioaddr = BgPatternTableAddr | (NTVal << 4) | ((vram_addr >> 12) & 7);
                        break;
                    case 5:
                        lowTile = _mapper.PpuRead((ushort)(ioaddr & 0x1FFF));
                        break;
                    case 6:
                        ioaddr = BgPatternTableAddr | (NTVal << 4) | ((vram_addr >> 12) & 7) | 8;
                        break;
                    case 7:
                        highTile = _mapper.PpuRead((ushort)(ioaddr & 0x1FFF));
                        // Render 8 pixels using shift registers BEFORE reload (visible only, BG on)
                        if (scanline < 240 && ppu_cycles_x < 256 && ShowBackGround)
                            RenderBGTile();
                        // Load shift registers (high = old-low = previous tile, low = new tile)
                        lowshift  = (ushort)((lowshift  << 8) | lowTile);
                        highshift = (ushort)((highshift << 8) | highTile);
                        CXinc();
                        break;
                }
            }
            else if (ppu_cycles_x == 256)
            {
                Yinc();
            }
            else if (ppu_cycles_x == 257)
            {
                CopyHoriV();
            }

            // Pre-render scanline: continuous vert(v) = vert(t) copy at cycles 280-304
            if (scanline == 261 && ppu_cycles_x >= 280 && ppu_cycles_x <= 304)
                vram_addr = (vram_addr & ~0x7BE0) | (vram_addr_internal & 0x7BE0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ppu_step_new()
        {
            // Open bus decay
            if (--open_bus_decay_timer == 0)
            {
                open_bus_decay_timer = 77777;
                openbus = 0;
            }

            bool renderingEnabled = ShowBackGround || ShowSprites;

            if (scanline < 240 || scanline == 261)
            {
                if (renderingEnabled)
                    ppu_rendering_tick();

                if (scanline >= 0 && scanline < 240)
                {
                    // At start of each visible scanline: always zero Buffer_BG_array to prevent
                    // stale data from prior frames causing incorrect sprite priority decisions.
                    if (ppu_cycles_x == 0)
                    {
                        int scanOff = scanline << 8;
                        for (int i = 0; i < 256; i++)
                            _bufferBgArray[scanOff + i] = 0;
                        if (!ShowBackGround)
                        {
                            uint bgColor = NesColor(PpuRamDirect(0x3f00));
                            for (int i = 0; i < 256; i++)
                                _screenBuf1x[scanOff + i] = bgColor;
                        }
                        PrecomputeSprite0Line();
                    }

                    // Sprite evaluation + rendering at cycle 257 (after BG tiles complete at cycle 255)
                    if (ppu_cycles_x == 257)
                        RenderSpritesLine();

                    // MMC3 IRQ: clock on visible scanlines at cycle 260 (A12 rising edge, sprite fetch region)
                    // (mapper-specific IRQ skipped for the simplified port)
                }
            }

            // MMC3 IRQ on pre-render scanline 261 cycle 260: skipped (mapper-specific)

            // Screen output at scanline 240 cycle 1 (matches ppu_step timing)
            if (scanline == 240 && ppu_cycles_x == 1)
            {
                RenderScreen();
                frame_count++;
                _frameReady = true;
            }

            // Advance cycle counter
            ppu_cycles_x++;

            // VBlank start at scanline 241, cycle 1 (post-increment)
            if (scanline == 241 && ppu_cycles_x == 1)
            {
                if (!SuppressVbl)
                {
                    isVblank = true;
                    if (NMIable) nmi_pending = true;
                }
                SuppressVbl = false;
            }

            // Pre-render: clear PPU status flags at cycle 1 (post-increment)
            if (scanline == 261 && ppu_cycles_x == 1)
                isVblank = isSprite0hit = isSpriteOverflow = false;

            // Odd frame skip: on odd frames with rendering enabled, skip last idle cycle of pre-render
            if (scanline == 261 && ppu_cycles_x == 339)
            {
                oddSwap = !oddSwap;
                if (!oddSwap && (ShowBackGround || ShowSprites)) ppu_cycles_x++;
            }

            // Advance scanline
            if (ppu_cycles_x == 341)
            {
                if (++scanline == 262) scanline = 0;
                ppu_cycles_x = 0;
            }
        }

        #endregion

        // Pre-computed sprite 0 data for per-pixel hit detection during BG rendering
        private bool sprite0_on_line;
        private int sprite0_line_x;
        private byte sprite0_tile_low, sprite0_tile_high;
        private bool sprite0_flip_x;

        // Pre-compute sprite 0 tile data for the current scanline so hit detection
        // can happen per-pixel inside RenderBGTile() at the correct PPU cycle.
        private void PrecomputeSprite0Line()
        {
            sprite0_on_line = false;
            if (isSprite0hit) return;
            if (!ShowBackGround || !ShowSprites) return;

            int y_loc = _oam[0] + 1; // NES hardware: sprites display at OAM_Y + 1
            int height = Spritesize8x16 ? 15 : 7;
            if (scanline < y_loc || scanline - y_loc > height) return;

            sprite0_on_line = true;
            sprite0_line_x = _oam[3];

            byte sprite_attr = _oam[2];
            sprite0_flip_x = (sprite_attr & 0x40) != 0;
            int offset, tile_th_t, line, line_t;
            byte tile_th;

            if (Spritesize8x16)
            {
                byte byte0 = _oam[1];
                tile_th = (byte)(byte0 & 0xfe);
                offset = (byte0 & 1) != 0 ? 256 : 0;
            }
            else
            {
                tile_th = _oam[1];
                offset = SpPatternTableAddr >> 4;
            }

            if (scanline <= y_loc + 7)
            {
                tile_th_t = tile_th + offset;
                line = scanline - y_loc;
            }
            else
            {
                tile_th_t = tile_th + offset + 1;
                line = scanline - y_loc - 8;
            }

            if ((sprite_attr & 0x80) != 0)
            {
                line_t = 7 - line;
                if (Spritesize8x16) tile_th_t ^= 1;
            }
            else line_t = line;

            sprite0_tile_high = _mapper.PpuRead((ushort)(((tile_th_t << 4) | (line_t + 8)) & 0x1FFF));
            sprite0_tile_low  = _mapper.PpuRead((ushort)(((tile_th_t << 4) | line_t) & 0x1FFF));
        }

        private int pixel, array_loc;

        private void RenderSpritesLine()
        {
            // Pass 1: scan OAM 0→63, pick first 8 sprites visible on this scanline.
            // NES hardware only performs sprite evaluation when rendering is enabled.
            int* sel = stackalloc int[8];
            int selCount = 0, spriteCount = 0;
            int height = Spritesize8x16 ? 15 : 7;
            bool renderingEnabled = ShowBackGround || ShowSprites;

            if (renderingEnabled)
            {
                for (int oam_th = 0; oam_th < 64; oam_th++)
                {
                    int oam_y = _oam[oam_th << 2];
                    // Overflow evaluation uses raw OAM Y (hardware evaluates before 1-scanline pipeline delay)
                    if (scanline >= oam_y && scanline - oam_y <= height)
                    {
                        if (++spriteCount == 9) isSpriteOverflow = true;
                    }
                    // Selection for rendering uses Y+1 (sprites display one scanline later)
                    int render_y = oam_y + 1;
                    if (scanline < render_y || scanline - render_y > height) continue;
                    if (selCount < 8) sel[selCount++] = oam_th;
                }
            }

            if (!ShowSprites) return;

            // Per-pixel sprite winner buffers.
            // NES hardware picks ONE winning sprite per pixel (lowest OAM index with opaque pixel).
            // That winner's priority bit then decides the BG/sprite composite for ALL sprites at that pixel.
            // This implements the "sprite priority quirk": a behind-BG mask sprite (low OAM index,
            // priority=1) can suppress a front sprite (high OAM index, priority=0) at the same pixel.
            uint* sprColor    = stackalloc uint[256];
            byte* sprPriority = stackalloc byte[256]; // 1 = behind BG, 0 = in front
            byte* sprSet      = stackalloc byte[256]; // 1 = a winning sprite pixel exists here
            for (int i = 0; i < 256; i++) sprSet[i] = 0;

            // Pass 2: evaluate sprites in reverse OAM order so lower-index sprites overwrite higher,
            // making the lowest-index sprite the final winner at each pixel.
            for (int si = selCount - 1; si >= 0; si--)
            {
                int oam_th = sel[si];
                int oam_addr = oam_th << 2;
                int y_loc = _oam[oam_addr] + 1; // NES hardware: sprites display at OAM_Y + 1

                int offset, tile_th_t, line, line_t;
                byte tile_th;

                if (Spritesize8x16)
                {
                    byte byte0 = _oam[oam_addr | 1];
                    tile_th = (byte)(byte0 & 0xfe);
                    offset = (byte0 & 1) != 0 ? 256 : 0;
                }
                else
                {
                    tile_th = _oam[oam_addr | 1];
                    offset = SpPatternTableAddr >> 4;
                }

                byte sprite_attr = _oam[oam_addr | 2];
                byte x_loc = _oam[oam_addr | 3];
                bool priority = (sprite_attr & 0x20) != 0;

                if (scanline <= y_loc + 7)
                {
                    tile_th_t = tile_th + offset;
                    line = scanline - y_loc;
                }
                else
                {
                    tile_th_t = tile_th + offset + 1;
                    line = scanline - y_loc - 8;
                }

                if ((sprite_attr & 0x80) != 0)
                {
                    line_t = 7 - line;
                    if (Spritesize8x16) tile_th_t ^= 1;
                }
                else line_t = line;

                byte tile_hbyte = _mapper.PpuRead((ushort)(((tile_th_t << 4) | (line_t + 8)) & 0x1FFF));
                byte tile_lbyte = _mapper.PpuRead((ushort)(((tile_th_t << 4) | line_t) & 0x1FFF));
                bool flip_x = (sprite_attr & 0x40) != 0;

                for (int loc = 0; loc < 8; loc++)
                {
                    int screenX = x_loc + loc;
                    if (screenX > 255) continue;
                    if (!ShowSprLeft8 && screenX < 8) continue;
                    int loc_t = flip_x ? (7 - loc) : loc;
                    int mask = 1 << (7 - loc_t);
                    pixel = (((tile_hbyte & mask) << 1) + (tile_lbyte & mask)) >> (7 - loc_t);
                    if (pixel == 0) continue;

                    array_loc = (scanline << 8) + screenX;

                    // Record as winner at this column (lower OAM index will overwrite later)
                    sprSet[screenX]      = 1;
                    sprPriority[screenX] = (byte)(priority ? 1 : 0);
                    sprColor[screenX]    = NesColor(PpuRamDirect(0x3f10 + ((sprite_attr & 3) << 2) | pixel));
                }
            }

            // Pass 3: composite — draw winning sprite pixel only if:
            //   BG is disabled, OR BG pixel is transparent, OR winning sprite is front-priority.
            // A behind-BG winner (priority=1) with opaque BG blocks ALL sprites at that pixel,
            // correctly implementing the mask-sprite trick used by SMB3.
            int scanOff = scanline << 8;
            for (int screenX = 0; screenX < 256; screenX++)
            {
                if (sprSet[screenX] == 0) continue;
                array_loc = scanOff + screenX;
                if (!ShowBackGround || _bufferBgArray[array_loc] == 0 || sprPriority[screenX] == 0)
                    _screenBuf1x[array_loc] = sprColor[screenX];
            }
        }

        private void RenderScreen()
        {
            // Convert the 32bpp ARGB scratch into the public RGB byte triple
            // framebuffer. The cycle-accurate per-pixel renderer has already
            // populated _screenBuf1x by this point.
            int n = 256 * 240;
            int o = 0;
            for (int i = 0; i < n; i++)
            {
                uint c = _screenBuf1x[i];
                _framebuffer[o++] = (byte)((c >> 16) & 0xFF); // R
                _framebuffer[o++] = (byte)((c >> 8)  & 0xFF); // G
                _framebuffer[o++] = (byte)( c        & 0xFF); // B
            }
        }

        /// <summary>End-of-frame full-screen scan for screenshot capture.
        /// Walks the visible nametable + sprites and produces the public
        /// RGB framebuffer. Independent of the per-pixel cycle renderer
        /// driven by Tick(); use whichever surface fits the caller. </summary>
        public void RenderFrame()
        {
            // If the cycle-accurate renderer has already produced a frame
            // this NES second, _screenBuf1x is current — just convert.
            if (_frameReady)
            {
                RenderScreen();
                return;
            }

            // Otherwise, do a simplified end-of-frame full-screen scan: walk
            // the four nametables (using the current scroll origin in
            // vram_addr_internal) and render 30 rows of 32 tiles, then
            // overlay sprites in OAM-priority order.

            // Background pass.
            int bgPattern = BgPatternTableAddr;
            uint backdrop = NesColor(PpuRamDirect(0x3f00));
            for (int i = 0; i < 256 * 240; i++) _screenBuf1x[i] = backdrop;
            Array.Clear(_bufferBgArray, 0, _bufferBgArray.Length);

            if (ShowBackGround)
            {
                // Use the base name table from PPUCTRL ($2000 | (ctrl & 3) << 10).
                int ntBase = BaseNameTableAddr;
                for (int row = 0; row < 30; row++)
                {
                    for (int col = 0; col < 32; col++)
                    {
                        int ntIdx = ntBase + (row * 32) + col;
                        byte tileIdx = PpuRamDirect(ntIdx);

                        int atIdx = (ntBase & 0x2C00) | 0x03C0 | ((row >> 2) << 3) | (col >> 2);
                        byte atByte = PpuRamDirect(atIdx);
                        int shift = ((row & 2) << 1) | (col & 2);
                        int attr = (atByte >> shift) & 3;

                        for (int yi = 0; yi < 8; yi++)
                        {
                            int chrAddr = bgPattern | (tileIdx << 4) | yi;
                            byte lo = _mapper.PpuRead((ushort)(chrAddr & 0x1FFF));
                            byte hi = _mapper.PpuRead((ushort)((chrAddr | 8) & 0x1FFF));
                            for (int xi = 0; xi < 8; xi++)
                            {
                                int bit = 7 - xi;
                                int p = ((lo >> bit) & 1) | (((hi >> bit) & 1) << 1);
                                int sx = col * 8 + xi;
                                int sy = row * 8 + yi;
                                int slot = sy * 256 + sx;
                                _bufferBgArray[slot] = p;
                                if (p != 0)
                                    _screenBuf1x[slot] = NesColor(PpuRamDirect(0x3F00 | (attr << 2) | p));
                            }
                        }
                    }
                }
            }

            if (ShowSprites)
            {
                // Reverse OAM order so sprite 0 wins at any shared pixel.
                int spPattern = SpPatternTableAddr;
                int hRange = Spritesize8x16 ? 16 : 8;
                for (int s = 63; s >= 0; s--)
                {
                    int oa = s << 2;
                    int sy = _oam[oa] + 1;
                    byte tileIdx = _oam[oa | 1];
                    byte attr = _oam[oa | 2];
                    int sx = _oam[oa | 3];
                    bool flipX = (attr & 0x40) != 0;
                    bool flipY = (attr & 0x80) != 0;
                    bool behindBg = (attr & 0x20) != 0;
                    int pal = attr & 3;

                    int patternBase;
                    int tileNum;
                    if (Spritesize8x16)
                    {
                        patternBase = (tileIdx & 1) != 0 ? 0x1000 : 0x0000;
                        tileNum = tileIdx & 0xFE;
                    }
                    else
                    {
                        patternBase = spPattern;
                        tileNum = tileIdx;
                    }

                    for (int yi = 0; yi < hRange; yi++)
                    {
                        int rowY = sy + yi;
                        if (rowY >= 240) break;
                        int srcY = flipY ? (hRange - 1 - yi) : yi;
                        int tileFinal = tileNum;
                        int subY = srcY;
                        if (Spritesize8x16 && srcY >= 8)
                        {
                            tileFinal = tileNum + 1;
                            subY = srcY - 8;
                        }
                        int chrAddr = patternBase | (tileFinal << 4) | subY;
                        byte lo = _mapper.PpuRead((ushort)(chrAddr & 0x1FFF));
                        byte hi = _mapper.PpuRead((ushort)((chrAddr | 8) & 0x1FFF));
                        for (int xi = 0; xi < 8; xi++)
                        {
                            int colX = sx + xi;
                            if (colX > 255) break;
                            int bit = flipX ? xi : (7 - xi);
                            int p = ((lo >> bit) & 1) | (((hi >> bit) & 1) << 1);
                            if (p == 0) continue;
                            int slot = rowY * 256 + colX;
                            if (behindBg && _bufferBgArray[slot] != 0) continue;
                            _screenBuf1x[slot] = NesColor(PpuRamDirect(0x3F10 | (pal << 2) | p));
                        }
                    }
                }
            }

            RenderScreen();
        }

        // ============================================================
        // Register handlers (verbatim from source; debug prints / CPU
        // look-ahead removed since we don't have CPU cycle context here).
        // ============================================================

        //ref http://wiki.nesdev.com/w/index.php/PPU_scrolling
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ppu_r_2002() //ok
        {
            openbus = (byte)(((isVblank) ? 0x80 : 0) | ((isSprite0hit) ? 0x40 : 0) | ((isSpriteOverflow) ? 0x20 : 0) | (openbus & 0x1f));

            if (ppu_cycles_x == 1 && scanline == 241)
            {
                SuppressVbl = true;
            }
            else
            {
                SuppressVbl = false;
                isVblank = false;
            }

            vram_latch = false;
            return openbus;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ppu_r_2007()
        {
            int va = vram_addr & 0x3FFF;
            byte ret;
            if (va < 0x3F00)
            {
                // Buffered read for $0000-$3EFF.
                ret = ppu_2007_buffer;
                if (va < 0x2000)
                    ppu_2007_buffer = _mapper.PpuRead((ushort)va);
                else
                    ppu_2007_buffer = _vram[MirrorNametable(va)];
            }
            else
            {
                // Palette: returned immediately, but underlying VRAM ($2F00-$2FFF
                // mirror) is loaded into the buffer at the same time.
                int p = va & 0x1F;
                if ((p & 0x03) == 0) p &= 0x0C;
                ret = (byte)((openbus & 0xC0) | (_paletteRam[p] & 0x3F));
                ppu_2007_buffer = _vram[MirrorNametable(va & 0x2FFF)];
            }
            vram_addr = (vram_addr + VramaddrIncrement) & 0x7FFF;
            openbus = ret;
            open_bus_decay_timer = 77777;
            return ret;
        }

        private void ppu_w_2000(byte value) //ok
        {
            openbus = value;

            // t: ...BA.. ........ = d: ......BA
            vram_addr_internal = (ushort)((vram_addr_internal & 0x73ff) | ((value & 3) << 10)); // 0xx73ff
            BaseNameTableAddr = 0x2000 | ((value & 3) << 10);
            VramaddrIncrement = ((value & 4) > 0) ? 32 : 1;
            SpPatternTableAddr = ((value & 8) > 0) ? 0x1000 : 0;
            BgPatternTableAddr = ((value & 0x10) > 0) ? 0x1000 : 0;
            Spritesize8x16 = ((value & 0x20) > 0) ? true : false;
            bool wasNMIable = NMIable;
            NMIable = ((value & 0x80) > 0) ? true : false;
            // Rising edge: enabling NMI while VBL flag is already set fires NMI immediately
            if (!wasNMIable && NMIable && isVblank) nmi_pending = true;
        }

        private void ppu_w_2001(byte value) //ok
        {
            openbus = value;

            ShowBgLeft8  = (value & 0x02) != 0; // bit1: show BG in leftmost 8 pixels
            ShowSprLeft8 = (value & 0x04) != 0; // bit2: show sprites in leftmost 8 pixels
            ShowBackGround = (value & 0x08) != 0;
            ShowSprites    = (value & 0x10) != 0;
        }

        private void ppu_w_2003(byte value) //ok
        {
            openbus = value;
            spr_ram_add = value;
        }

        private void ppu_w_2004(byte value) //ok
        {
            openbus = value;
            _oam[spr_ram_add++] = value;
        }

        private byte ppu_r_2004()
        {
            byte val = _oam[spr_ram_add];
            if ((spr_ram_add & 3) == 2) val &= 0xE3; // mask unimplemented bits of attribute byte only
            open_bus_decay_timer = 77777;
            return openbus = val;
        }

        private void ppu_w_2005(byte value) //ok
        {
            openbus = value;
            if (vram_latch)
            {
                scrol_y = value & 7;
                vram_addr_internal = (vram_addr_internal & 0x0C1F) | ((value & 0x7) << 12) | ((value & 0xF8) << 2);
            }
            else
            {//first
                vram_addr_internal = (vram_addr_internal & 0x7fe0) | ((value & 0xf8) >> 3);
                FineX = value & 0x07;
            }
            vram_latch = !vram_latch;
        }

        private void ppu_w_2006(byte value)//ok
        {
            openbus = value;
            if (!vram_latch) //first
                vram_addr_internal = (vram_addr_internal & 0x00FF) | ((value & 0x3F) << 8);
            else
            {
                vram_addr_internal = (vram_addr_internal & 0x7F00) | value;
                vram_addr = vram_addr_internal;
            }
            vram_latch = !vram_latch;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ppu_w_2007(byte value)
        {
            open_bus_decay_timer = 77777;
            openbus = value;
            PpuBusWrite(vram_addr, value);
            vram_addr = (vram_addr + VramaddrIncrement) & 0x7FFF;
        }
    }
}
