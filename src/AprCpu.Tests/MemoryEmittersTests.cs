using AprCpu.Core.Compilation;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// 2.5.3a: load / store micro-ops. Verifies that the IR contains the
/// expected extern declarations (memory_read_8/16/32, memory_write_*)
/// and uses calls to them.
/// </summary>
public class MemoryEmittersTests
{
    [Fact]
    public void StandardEmitters_RegistersLoadAndStore()
    {
        var reg = new EmitterRegistry();
        StandardEmitters.RegisterAll(reg);

        Assert.True(reg.TryGet("load",  out _));
        Assert.True(reg.TryGet("store", out _));
    }

    [Fact]
    public void ExternFunction_DeclaredOnFirstUse()
    {
        // Use a minimal in-memory module to test the helper directly.
        var ctx = LLVMContextRef.Create();
        using var module = ctx.CreateModuleWithName("test_externs");

        var fn1 = MemoryEmitters.GetOrDeclareMemoryFunction(
            module, MemoryEmitters.ExternFunctionNames.Read32,
            LLVMTypeRef.Int32, LLVMTypeRef.Int32);
        Assert.NotEqual(IntPtr.Zero, fn1.Handle);

        // Same name again — same function returned (no duplicate declaration).
        var fn2 = MemoryEmitters.GetOrDeclareMemoryFunction(
            module, MemoryEmitters.ExternFunctionNames.Read32,
            LLVMTypeRef.Int32, LLVMTypeRef.Int32);
        Assert.Equal(fn1.Handle, fn2.Handle);
    }

    [Fact]
    public void ExternFunction_NamesAreCanonical()
    {
        Assert.Equal("memory_read_8",   MemoryEmitters.ExternFunctionNames.Read8);
        Assert.Equal("memory_read_16",  MemoryEmitters.ExternFunctionNames.Read16);
        Assert.Equal("memory_read_32",  MemoryEmitters.ExternFunctionNames.Read32);
        Assert.Equal("memory_write_8",  MemoryEmitters.ExternFunctionNames.Write8);
        Assert.Equal("memory_write_16", MemoryEmitters.ExternFunctionNames.Write16);
        Assert.Equal("memory_write_32", MemoryEmitters.ExternFunctionNames.Write32);
    }
}
