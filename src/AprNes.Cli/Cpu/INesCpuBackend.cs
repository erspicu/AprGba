// Common surface shared by the LegacyCpu-based BoundCpu (interpreter
// oracle) and the JSON-spec-driven NesJsonCpu (LLVM-JIT'd backend).
//
// Program.cs picks one or the other based on --backend=, then dispatches
// the harness loop polymorphically. Keeping both backends behind the same
// interface lets the rest of the pipeline (PPU/Mapper/screenshot/nestest
// result codes) stay byte-identical.

namespace AprNes.Cli.Cpu;

public interface INesCpuBackend
{
    // --- Lifecycle ---

    /// <summary>Cold reset — fetch reset vector ($FFFC/$FFFD) → PC, set
    /// SP=$FD, P=$24 (I=1, U=1), match the documented 6502 reset sequence.</summary>
    void Reset();

    /// <summary>nestest-style entry: PC=$C000, SP=$FD, P=$24. Skips the
    /// reset-vector fetch.</summary>
    void InitForNestest();

    /// <summary>Direct register-state init (used by --start-pc=). Flag
    /// args are individual bits 0/1; bit 5 (U) is implicitly set.</summary>
    void SetRegisters(byte a, byte x, byte y, byte sp, ushort pc,
        byte flagN, byte flagV, byte flagD, byte flagI, byte flagZ, byte flagC);

    // --- Execution ---

    /// <summary>Run one full instruction (servicing a pending PPU NMI
    /// first if one is latched). Returns the CPU cycles consumed
    /// (including any DMA stall folded in by the implementation).</summary>
    int Step();

    // --- Register accessors (read-only after construction) ---

    ushort PC { get; }
    byte   A  { get; }
    byte   X  { get; }
    byte   Y  { get; }
    byte   SP { get; }
    /// <summary>Packed P register, NV-BDIZC. Bit 5 (U) is software-only,
    /// returned as 0 here; callers needing the BRK/PHP form should OR in
    /// 0x20 themselves to match LegacyCpu's GetFlag() behaviour.</summary>
    byte   P  { get; }

    /// <summary>Backend identifier for log output.</summary>
    string BackendName { get; }
}
