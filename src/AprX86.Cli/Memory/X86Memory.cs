// X86Memory — 1 MB linear address space for 8086 real mode.
//
// 8086 has a 20-bit physical address bus, so the full address space is
// exactly 0x100000 bytes. Segmented addresses (`(seg << 4) + off`) all
// resolve to the same flat byte[] here.
//
// Phase 24.1: minimum viable shape — direct byte[] read/write. The
// CGA text framebuffer at 0xB8000-0xBFFFF lives inside this same
// array; the renderer (X86CgaRenderer, phase 24.3) walks it on demand.

namespace AprX86.Cli.Memory;

public sealed class X86Memory
{
    /// <summary>Full 1 MB physical RAM.</summary>
    public const int Size = 0x100000;

    private readonly byte[] _ram = new byte[Size];

    /// <summary>Direct access for the CGA framebuffer renderer + load helpers.</summary>
    public byte[] Ram => _ram;

    /// <summary>
    /// Compute the 20-bit physical address from a real-mode (segment, offset)
    /// pair: <c>(segment &lt;&lt; 4) + offset</c>, masked to 20 bits.
    /// 8086 does not wrap to 0 above 0x10FFEF (the so-called "A20 line"
    /// behaviour was added on 80286+); for the AprX86 base we mirror the
    /// 8086 behaviour and mask to 20 bits.
    /// </summary>
    public static int LinearAddr(ushort segment, ushort offset)
        => ((segment << 4) + offset) & 0xFFFFF;

    public byte ReadByte(int addr)
        => _ram[addr & 0xFFFFF];

    public ushort ReadWord(int addr)
    {
        int a = addr & 0xFFFFF;
        // 8086 unaligned reads are allowed; just two byte fetches.
        // Wraparound at 0xFFFFF only matters if 8086 spec requires it —
        // for now mask each byte independently.
        return (ushort)(_ram[a] | (_ram[(a + 1) & 0xFFFFF] << 8));
    }

    public void WriteByte(int addr, byte value)
        => _ram[addr & 0xFFFFF] = value;

    public void WriteWord(int addr, ushort value)
    {
        int a = addr & 0xFFFFF;
        _ram[a]                    = (byte)(value & 0xFF);
        _ram[(a + 1) & 0xFFFFF]    = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// Load a flat binary blob at a given (segment, offset) pair. Used
    /// for hand-crafted .com test ROMs (CP/M convention: load at 0:0x100).
    /// </summary>
    public void LoadBinary(byte[] bytes, ushort segment, ushort offset)
    {
        int linear = LinearAddr(segment, offset);
        if (linear + bytes.Length > Size)
            throw new ArgumentException(
                $"binary of {bytes.Length} bytes at {segment:X4}:{offset:X4} (linear {linear:X5}) overflows 1 MB");
        Array.Copy(bytes, 0, _ram, linear, bytes.Length);
    }

    public void Clear()
        => Array.Clear(_ram, 0, _ram.Length);
}
