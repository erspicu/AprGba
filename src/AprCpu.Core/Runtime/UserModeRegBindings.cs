using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AprCpu.Core.Runtime;

/// <summary>
/// Host externs that read/write registers through the User-mode view.
/// Used by ARM block transfers with the S-bit (LDM/STM with the
/// <c>^</c> suffix), which always access User-mode register storage
/// regardless of the current processor mode.
///
/// Routes calls to <see cref="Arm7tdmiBankSwapHandler"/>
/// (or any future handler implementing the same access pattern).
/// </summary>
public static unsafe class UserModeRegBindings
{
    public const string ReadExternName  = "host_user_reg_read";
    public const string WriteExternName = "host_user_reg_write";

    private static Arm7tdmiBankSwapHandler? _handler;

    public static IDisposable Install(HostRuntime rt, Arm7tdmiBankSwapHandler handler)
    {
        var prior = _handler;
        _handler = handler;
        rt.BindExtern(ReadExternName,  (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, uint>)&ReadTrampoline);
        rt.BindExtern(WriteExternName, (IntPtr)(delegate* unmanaged[Cdecl]<byte*, uint, uint, void>)&WriteTrampoline);
        return new RestoreOnDispose(prior);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadTrampoline(byte* state, uint regIndex)
        => _handler!.ReadUserModeReg(state, (int)regIndex);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTrampoline(byte* state, uint regIndex, uint value)
        => _handler!.WriteUserModeReg(state, (int)regIndex, value);

    private sealed class RestoreOnDispose : IDisposable
    {
        private readonly Arm7tdmiBankSwapHandler? _prior;
        public RestoreOnDispose(Arm7tdmiBankSwapHandler? prior) => _prior = prior;
        public void Dispose() => _handler = _prior;
    }
}
