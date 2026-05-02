using AprGb.Cli.Memory;
using AprGb.Cli.Video;

namespace AprGb.Cli.Cpu;

/// <summary>
/// Common interface for the two GB CPU backends:
/// <list type="bullet">
/// <item><b>Legacy</b> — direct port of <c>AprGBemu/Emu_GB/CPU.cs</c>,
/// the proven big-switch implementation. Used as the reference baseline.</item>
/// <item><b>JsonLlvm</b> — driven by <c>spec/lr35902/*.json</c> through
/// <see cref="AprCpu.Core"/>'s LLVM JIT pipeline. The thing we actually
/// want to validate.</item>
/// </list>
/// Both backends share <see cref="GbMemoryBus"/> for memory and
/// <see cref="GbPpu"/> for video, so a screenshot taken from either
/// path is directly comparable.
/// </summary>
public interface ICpuBackend
{
    /// <summary>Backend identifier — used by the CLI to label outputs.</summary>
    string Name { get; }

    /// <summary>
    /// Initialise CPU registers to the post-BIOS state and bind to the
    /// host memory bus. Idempotent.
    /// </summary>
    void Reset(GbMemoryBus bus);

    /// <summary>
    /// Run the CPU for at least <paramref name="targetCycles"/> machine
    /// cycles. Implementations may overshoot by one instruction (running
    /// to completion of the current instruction). Returns the actual
    /// number of cycles consumed.
    /// </summary>
    long RunCycles(long targetCycles);

    /// <summary>Read a register for diagnostic / comparison output.</summary>
    ushort ReadReg16(GbReg16 reg);
    byte   ReadReg8 (GbReg8  reg);

    /// <summary>True iff the program is in HALT/STOP (no further progress without IRQ).</summary>
    bool IsHalted { get; }

    /// <summary>
    /// Total number of CPU instructions executed since Reset. Used by the
    /// CLI's --bench mode to compute MIPS (instructions / wall-clock seconds).
    /// HALT no-op spins do NOT count as instructions; only real opcode
    /// dispatches do.
    /// </summary>
    long InstructionsExecuted { get; }
}

public enum GbReg8  { A, F, B, C, D, E, H, L }
public enum GbReg16 { AF, BC, DE, HL, SP, PC }
