namespace AprGb.Cli.Memory;

/// <summary>
/// Game Boy memory bus skeleton. Real implementation lands in the next
/// pass — this stub just owns the 64 KB address space and lets ROM
/// data be loaded into the cart region for early bring-up.
/// </summary>
public sealed class GbMemoryBus
{
    /// <summary>
    /// Full 16-bit address space (0x0000-0xFFFF). For Phase 4.5 the
    /// MBC dispatching will overlay real backing arrays; this flat
    /// buffer is the placeholder.
    /// </summary>
    public byte[] Mem { get; } = new byte[0x10000];

    /// <summary>The cartridge ROM loaded from disk (size depends on cart).</summary>
    public byte[] Rom { get; private set; } = Array.Empty<byte>();

    public void LoadRom(byte[] romBytes)
    {
        Rom = romBytes;
        // GB ROM bank 0 is fixed at 0x0000-0x3FFF. Bank N at 0x4000-0x7FFF
        // (default N=1). MBC mapping comes later.
        var len0 = Math.Min(0x4000, romBytes.Length);
        Buffer.BlockCopy(romBytes, 0, Mem, 0x0000, len0);
        if (romBytes.Length > 0x4000)
        {
            var len1 = Math.Min(0x4000, romBytes.Length - 0x4000);
            Buffer.BlockCopy(romBytes, 0x4000, Mem, 0x4000, len1);
        }
    }

    public byte ReadByte(ushort addr) => Mem[addr];

    public void WriteByte(ushort addr, byte value)
    {
        // ROM region (0x0000-0x7FFF) is read-only for now.
        if (addr < 0x8000) return;
        Mem[addr] = value;
    }
}
