using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Glue between the JIT'd code's <c>host_swap_register_bank</c> extern
/// and a C# <see cref="IBankSwapHandler"/>.
///
/// Trampoline is <c>[UnmanagedCallersOnly]</c> static so we can hand its
/// address to MCJIT. Routes to <see cref="_current"/>; same threading
/// caveat as <see cref="MemoryBusBindings"/> — single CPU per process
/// for now.
///
/// The handler does the architecture-specific R8-R14 swap (ARM banks
/// R13/R14 for IRQ/Supervisor/Abort/Undefined plus R8-R14 for FIQ).
/// Pure-spec architectures (e.g. GB LR35902, no banking) can install a
/// no-op handler.
/// </summary>
public static unsafe class BankSwapBindings
{
    public const string ExternName = "host_swap_register_bank";

    private static IBankSwapHandler? _current;

    public static IDisposable Install(HostRuntime rt, IBankSwapHandler handler)
    {
        var prior = _current;
        _current = handler;
        rt.BindExtern(ExternName, (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, uint, void>)&Trampoline);
        return new RestoreOnDispose(prior);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Trampoline(byte* state, uint oldMode, uint newMode)
        => _current!.SwapBank(state, oldMode, newMode);

    private sealed class RestoreOnDispose : IDisposable
    {
        private readonly IBankSwapHandler? _prior;
        public RestoreOnDispose(IBankSwapHandler? prior) => _prior = prior;
        public void Dispose() => _current = _prior;
    }
}

/// <summary>
/// Architecture-specific banked register swap. Called by the JIT'd code
/// at every mode transition (raise_exception entry, restore_cpsr_from_spsr
/// exit). Implementations move R8-R14 between the visible struct slots
/// and the per-mode banked storage according to the architecture's
/// banking scheme.
/// </summary>
public unsafe interface IBankSwapHandler
{
    /// <summary>
    /// <paramref name="state"/> is the raw CPU-state buffer that the IR
    /// is operating on. <paramref name="oldMode"/>/<paramref name="newMode"/>
    /// are the 5-bit CPSR.M values BEFORE/AFTER the transition.
    /// </summary>
    void SwapBank(byte* state, uint oldMode, uint newMode);
}
