using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AprCpu.Core.IR;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Glue between the JIT'd code's <c>memory_read/write_*</c> externs and
/// a C# <see cref="IMemoryBus"/> implementation.
///
/// Trampoline functions are <c>[UnmanagedCallersOnly]</c> static methods.
/// They route to a single static <see cref="_current"/> bus — not
/// thread-safe today, intentional for Phase 3 (one CPU, one main thread).
/// Multi-CPU later will need TLS or a context-pointer argument in the IR
/// signature.
/// </summary>
public static unsafe class MemoryBusBindings
{
    private static IMemoryBus? _current;

    /// <summary>
    /// Bind the six memory-bus externs in <paramref name="rt"/>'s module
    /// to <paramref name="bus"/>. Must be called BEFORE
    /// <see cref="HostRuntime.Finalize"/> (the trampoline addresses are
    /// baked into the IR as constant initializers).
    ///
    /// The returned scope restores the prior <c>_current</c> binding when
    /// disposed — note this only changes which bus the trampolines route
    /// to; it does NOT re-bake the IR.
    /// </summary>
    public static IDisposable Install(HostRuntime rt, IMemoryBus bus)
    {
        var prior = _current;
        _current = bus;

        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read8,   (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte>)&Read8);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read16,  (IntPtr)(delegate* unmanaged[Cdecl]<uint, ushort>)&Read16);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Read32,  (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint>)&Read32);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write8,  (IntPtr)(delegate* unmanaged[Cdecl]<uint, byte, void>)&Write8);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write16, (IntPtr)(delegate* unmanaged[Cdecl]<uint, ushort, void>)&Write16);
        rt.BindExtern(MemoryEmitters.ExternFunctionNames.Write32, (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint, void>)&Write32);

        return new RestoreOnDispose(prior);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte   Read8 (uint addr)              => _current!.ReadByte(addr);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Read16(uint addr)              => _current!.ReadHalfword(addr);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint   Read32(uint addr)              => _current!.ReadWord(addr);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void   Write8 (uint addr, byte v)     => _current!.WriteByte(addr, v);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void   Write16(uint addr, ushort v)   => _current!.WriteHalfword(addr, v);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void   Write32(uint addr, uint v)     => _current!.WriteWord(addr, v);

    private sealed class RestoreOnDispose : IDisposable
    {
        private readonly IMemoryBus? _prior;
        public RestoreOnDispose(IMemoryBus? prior) => _prior = prior;
        public void Dispose() => _current = _prior;
    }
}
