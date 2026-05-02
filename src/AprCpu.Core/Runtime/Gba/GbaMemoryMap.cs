namespace AprCpu.Core.Runtime.Gba;

/// <summary>
/// GBA address-space layout. Constants only — pulled out so the bus
/// implementation and any debugging tools share the same offsets.
/// Region sizes follow GBATEK; we don't yet model wait-state mirrors
/// (0x0A/0x0C ROM mirrors) — they alias to the same backing array.
/// </summary>
public static class GbaMemoryMap
{
    public const uint BiosBase    = 0x00000000;
    public const uint BiosSize    = 0x00004000;   // 16 KB

    public const uint EwramBase   = 0x02000000;
    public const uint EwramSize   = 0x00040000;   // 256 KB

    public const uint IwramBase   = 0x03000000;
    public const uint IwramSize   = 0x00008000;   // 32 KB

    public const uint IoBase      = 0x04000000;
    public const uint IoSize      = 0x00000400;   // 1 KB

    public const uint PaletteBase = 0x05000000;
    public const uint PaletteSize = 0x00000400;   // 1 KB

    public const uint VramBase    = 0x06000000;
    public const uint VramSize    = 0x00018000;   // 96 KB

    public const uint OamBase     = 0x07000000;
    public const uint OamSize     = 0x00000400;   // 1 KB

    public const uint RomBase     = 0x08000000;
    public const uint RomMaxSize  = 0x02000000;   // 32 MB max

    // ---- IO register offsets (relative to IoBase, GBATEK) ----

    // PPU display
    public const uint DISPCNT_Off  = 0x000;       // 0x04000000  R/W  Display Control
    public const uint DISPSTAT_Off = 0x004;       // 0x04000004  R/W  Display Status
    public const uint VCOUNT_Off   = 0x006;       // 0x04000006  R    Vertical Counter

    // BG control + scroll (BG0..BG3)
    public const uint BG0CNT_Off   = 0x008;       // 0x04000008  R/W
    public const uint BG1CNT_Off   = 0x00A;
    public const uint BG2CNT_Off   = 0x00C;
    public const uint BG3CNT_Off   = 0x00E;
    public const uint BG0HOFS_Off  = 0x010;       // 0x04000010  W    BG0 H scroll
    public const uint BG0VOFS_Off  = 0x012;       // 0x04000012  W    BG0 V scroll
    // ... BG1HOFS/VOFS through BG3HOFS/VOFS at +0x004 strides

    // DMA channels — each channel has SAD/DAD/CNT_L/CNT_H starting at
    // these bases, all 12 bytes wide:
    //   SAD     uint32  source address
    //   DAD     uint32  dest address
    //   CNT_L   uint16  word count
    //   CNT_H   uint16  control (enable=bit15)
    public const uint DMA0_Base    = 0x0B0;       // 0x040000B0
    public const uint DMA1_Base    = 0x0BC;       // 0x040000BC
    public const uint DMA2_Base    = 0x0C8;       // 0x040000C8
    public const uint DMA3_Base    = 0x0D4;       // 0x040000D4
    public const uint DMA_ChannelStride = 0x0C;   // 12 bytes per channel

    // Per-channel sub-offsets
    public const uint DMA_SAD_Off    = 0x00;
    public const uint DMA_DAD_Off    = 0x04;
    public const uint DMA_CNT_L_Off  = 0x08;
    public const uint DMA_CNT_H_Off  = 0x0A;

    // DMA CNT_H bits
    public const ushort DMA_DEST_INC      = 0x0000;   // bits 5..6
    public const ushort DMA_DEST_DEC      = 0x0020;
    public const ushort DMA_DEST_FIXED    = 0x0040;
    public const ushort DMA_DEST_RELOAD   = 0x0060;
    public const ushort DMA_DEST_MASK     = 0x0060;
    public const ushort DMA_SRC_INC       = 0x0000;   // bits 7..8
    public const ushort DMA_SRC_DEC       = 0x0080;
    public const ushort DMA_SRC_FIXED     = 0x0100;
    public const ushort DMA_SRC_MASK      = 0x0180;
    public const ushort DMA_REPEAT        = 0x0200;   // bit 9
    public const ushort DMA_WIDTH_32      = 0x0400;   // bit 10 (0=16-bit, 1=32-bit)
    public const ushort DMA_TIMING_MASK   = 0x3000;   // bits 12..13
    public const ushort DMA_TIMING_NOW    = 0x0000;   // immediate
    public const ushort DMA_TIMING_VBLANK = 0x1000;
    public const ushort DMA_TIMING_HBLANK = 0x2000;
    public const ushort DMA_TIMING_SPECIAL= 0x3000;
    public const ushort DMA_IRQ_ON_END    = 0x4000;   // bit 14
    public const ushort DMA_ENABLE        = 0x8000;   // bit 15

    // Interrupt control
    public const uint IE_Off       = 0x200;       // 0x04000200  R/W  Interrupt Enable
    public const uint IF_Off       = 0x202;       // 0x04000202  R/W  Interrupt Flag (write 1 to clear)
    public const uint WAITCNT_Off  = 0x204;       // 0x04000204  R/W  GamePak wait control (we ignore)
    public const uint IME_Off      = 0x208;       // 0x04000208  R/W  Interrupt Master Enable
    public const uint POSTFLG_Off  = 0x300;       // 0x04000300  R/W  Post boot flag
    public const uint HALTCNT_Off  = 0x301;       // 0x04000301  W    HALT/STOP

    // DISPSTAT bits
    public const ushort STAT_VBLANK_FLG = 0x0001; // bit 0 — VBlank active
    public const ushort STAT_HBLANK_FLG = 0x0002; // bit 1 — HBlank active
    public const ushort STAT_VCOUNT_FLG = 0x0004; // bit 2 — VCount match
    public const ushort STAT_VBLANK_IE  = 0x0008; // bit 3 — VBlank IRQ enable
    public const ushort STAT_HBLANK_IE  = 0x0010; // bit 4 — HBlank IRQ enable
    public const ushort STAT_VCOUNT_IE  = 0x0020; // bit 5 — VCount IRQ enable

    // Interrupt vector base (in BIOS region)
    public const uint IrqVectorBase = 0x00000018;
}

/// <summary>
/// GBA interrupt sources (bit positions in IE / IF). Names match GBATEK.
/// </summary>
public enum GbaInterrupt
{
    VBlank   = 0,
    HBlank   = 1,
    VCount   = 2,
    Timer0   = 3,
    Timer1   = 4,
    Timer2   = 5,
    Timer3   = 6,
    Serial   = 7,
    Dma0     = 8,
    Dma1     = 9,
    Dma2     = 10,
    Dma3     = 11,
    Keypad   = 12,
    GamePak  = 13,
}
