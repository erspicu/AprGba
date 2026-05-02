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

    /// <summary>
    /// Called by <see cref="CpuExecutor"/> immediately after a successful
    /// instruction fetch. Buses that model fetch-side state — GBA BIOS
    /// open-bus protection, ARM prefetch buffer, etc. — implement this
    /// to track (pc, last fetched word, instruction size). The size
    /// lets the bus reproduce the 3-stage pipeline's prefetch offset
    /// (PC+2×instrSize) where it matters. Default is a no-op so buses
    /// without such behaviour need not opt in.
    /// </summary>
    void NotifyInstructionFetch(uint pc, uint instructionWord, uint instrSizeBytes) { }
}
