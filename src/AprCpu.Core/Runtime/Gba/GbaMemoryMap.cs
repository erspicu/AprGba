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

    // IO register offsets (relative to IoBase) we explicitly handle.
    public const uint DISPSTAT_Off = 0x004;       // 0x04000004
    public const ushort STAT_VBLANK_FLG = 0x0001;
}
