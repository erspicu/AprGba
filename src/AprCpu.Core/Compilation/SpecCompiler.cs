using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.Compilation;

/// <summary>
/// Top-level orchestrator: load a spec, build the decoder, walk every
/// instruction in every encoding format and emit one LLVM function per
/// instruction into a single module.
/// </summary>
public sealed unsafe class SpecCompiler
{
    public sealed record CompileResult(
        LLVMModuleRef                            Module,
        IReadOnlyDictionary<string, DecoderTable> DecoderTables,
        IReadOnlyDictionary<string, LLVMValueRef> Functions,
        IReadOnlyList<string>                    Diagnostics);

    /// <summary>
    /// Compile a CPU spec from its <c>cpu.json</c> path. Loads referenced
    /// instruction-set files, builds decoder tables, and emits one function
    /// per (instruction-set, format, instruction) into a fresh LLVM module.
    /// </summary>
    public static CompileResult Compile(string cpuJsonPath)
    {
        var loaded = SpecLoader.LoadCpuSpec(cpuJsonPath);
        return Compile(loaded);
    }

    public static CompileResult Compile(LoadedSpec loaded)
    {
        var moduleName = $"AprCpu_{loaded.Cpu.Architecture.Id}";
        var module = LLVMModuleRef.CreateWithName(moduleName);
        var layout = new CpuStateLayout(
            module.Context,
            loaded.Cpu.RegisterFile,
            loaded.Cpu.ProcessorModes);

        var registry = new EmitterRegistry();
        StandardEmitters.RegisterAll(registry);

        var resolverRegistry = new OperandResolverRegistry();
        StandardOperandResolvers.RegisterAll(resolverRegistry);

        var decoderTables = new Dictionary<string, DecoderTable>(StringComparer.Ordinal);
        var functions = new Dictionary<string, LLVMValueRef>(StringComparer.Ordinal);
        var diagnostics = new List<string>();

        foreach (var (setName, set) in loaded.InstructionSets)
        {
            // Surface SpecValidator warnings as diagnostics (hard errors
            // already threw at load time).
            foreach (var w in SpecValidator.ValidateInstructionSet(set))
                diagnostics.Add($"[warn] {w}");

            DecoderTable table;
            try
            {
                table = new DecoderTable(set);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"[{setName}] decoder table construction failed: {ex.Message}");
                continue;
            }
            decoderTables[setName] = table;

            var fb = new InstructionFunctionBuilder(module, layout, registry, resolverRegistry);

            foreach (var format in table.Formats)
            foreach (var def in format.Instructions)
            {
                try
                {
                    var fn = fb.Build(set, format, def);
                    functions[$"{setName}.{format.Name}.{def.Mnemonic}"] = fn;
                }
                catch (Exception ex)
                {
                    diagnostics.Add(
                        $"[{setName}.{format.Name}.{def.Mnemonic}] emission failed: {ex.Message}");
                }
            }
        }

        // Module verification: gather diagnostics but don't abort.
        if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var verifyErr) &&
            !string.IsNullOrEmpty(verifyErr))
        {
            diagnostics.Add($"[verify] {verifyErr}");
        }

        return new CompileResult(module, decoderTables, functions, diagnostics);
    }

    /// <summary>Convenience: compile and write IR text to a file.</summary>
    public static CompileResult CompileToFile(string cpuJsonPath, string outputLlPath)
    {
        var result = Compile(cpuJsonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputLlPath))!);
        result.Module.PrintToFile(outputLlPath);
        return result;
    }
}
