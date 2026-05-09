// Common surface shared by X86 backends — mirrors the INesCpuBackend
// pattern from AprNes.Cli/Cpu/INesCpuBackend.cs.
//
// Phase 24.1: only legacy backend (X86LegacyCpu) implements this.
// Phase 24.4 will add X86JsonCpu (per-instr) and Phase later add
// X86JsonCpu (block-JIT) following the same shape as Nes.

using AprX86.Cli.Memory;

namespace AprX86.Cli.Cpu;

public interface IX86CpuBackend
{
    /// <summary>Architectural reset — CS=0xFFFF, IP=0, flags clear.</summary>
    void Reset();

    /// <summary>
    /// Set CS:IP to a specific (segment, offset) pair. Used by .com test
    /// ROM loaders to start at CS=0:IP=0x100 rather than the BIOS reset
    /// vector at FFFF:0.
    /// </summary>
    void SetEntryPoint(ushort segment, ushort offset);

    /// <summary>Run one architectural instruction. Returns cycles consumed.</summary>
    int Step();

    // --- Architectural state accessors (read-only after init) ---
    X86State State { get; }
    X86Memory Memory { get; }

    /// <summary>Backend identifier for log output (e.g. "legacy", "json", "json-block").</summary>
    string BackendName { get; }

    /// <summary>
    /// True when the CPU has executed HLT and is waiting indefinitely.
    /// Test harness uses this as a clean stop condition for `.com` programs.
    /// </summary>
    bool Halted { get; }
}
