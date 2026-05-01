namespace AprCpu.Core.Runtime;

/// <summary>
/// Host-side memory bus interface. The JIT'd code calls into externs
/// (<c>memory_read_8/16/32</c>, <c>memory_write_8/16/32</c>) which are
/// trampolined to one implementation of this interface via
/// <see cref="MemoryBusBindings"/>.
///
/// All addresses are unsigned 32-bit. Implementations decide endianness
/// and alignment policy; the JIT'd CPU code does not pre-byte-swap.
/// </summary>
public interface IMemoryBus
{
    byte   ReadByte    (uint addr);
    ushort ReadHalfword(uint addr);
    uint   ReadWord    (uint addr);
    void   WriteByte    (uint addr, byte   value);
    void   WriteHalfword(uint addr, ushort value);
    void   WriteWord    (uint addr, uint   value);
}
