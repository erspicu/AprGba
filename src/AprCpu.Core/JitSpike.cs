using LLVMSharp.Interop;

namespace AprCpu.Core;

/// <summary>
/// Phase 0 spike: build a trivial <c>int add(int, int)</c> via LLVMSharp,
/// emit it to LLVM IR, JIT-compile it, and execute it.
/// Verifies the LLVMSharp.Interop + libLLVM toolchain is wired up correctly.
/// </summary>
public static unsafe class JitSpike
{
    private static bool _jitInitialized;
    private static readonly object _initLock = new();

    private static void EnsureJitInitialized()
    {
        if (_jitInitialized) return;
        lock (_initLock)
        {
            if (_jitInitialized) return;
            LLVM.InitializeAllTargetInfos();
            LLVM.InitializeAllTargets();
            LLVM.InitializeAllTargetMCs();
            LLVM.InitializeAllAsmPrinters();
            LLVM.InitializeAllAsmParsers();
            LLVM.LinkInMCJIT();
            _jitInitialized = true;
        }
    }

    /// <summary>
    /// Build an LLVM module containing a single <c>int add(int, int)</c> function.
    /// </summary>
    public static LLVMModuleRef BuildAddModule()
    {
        var module = LLVMModuleRef.CreateWithName("AprCpu_Spike");
        var builder = module.Context.CreateBuilder();

        var paramTypes = new[] { LLVMTypeRef.Int32, LLVMTypeRef.Int32 };
        var funcType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, paramTypes);
        var addFunc = module.AddFunction("add", funcType);

        var entry = addFunc.AppendBasicBlock("entry");
        builder.PositionAtEnd(entry);

        var sum = builder.BuildAdd(addFunc.GetParam(0), addFunc.GetParam(1), "sum");
        builder.BuildRet(sum);

        if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var error))
        {
            throw new InvalidOperationException($"LLVM module verification failed: {error}");
        }

        return module;
    }

    /// <summary>Emit the spike module as a textual <c>.ll</c> string.</summary>
    public static string EmitAddIR()
    {
        var module = BuildAddModule();
        return module.PrintToString();
    }

    /// <summary>Write the spike module as a textual <c>.ll</c> file.</summary>
    public static void DumpAddIRToFile(string path)
    {
        var module = BuildAddModule();
        module.PrintToFile(path);
    }

    /// <summary>JIT-compile the spike module and invoke <c>add(a, b)</c>.</summary>
    public static int JitAndRunAdd(int a, int b)
    {
        EnsureJitInitialized();

        var module = BuildAddModule();
        var engine = module.CreateMCJITCompiler();
        var addr = engine.GetFunctionAddress("add");
        if (addr == 0)
        {
            throw new InvalidOperationException("MCJIT failed to resolve 'add' function address.");
        }

        var fn = (delegate* unmanaged[Cdecl]<int, int, int>)addr;
        return fn(a, b);
    }
}
