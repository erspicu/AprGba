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
        IReadOnlyList<string>                    Diagnostics,
        // Phase 7 A.2: expose registries so callers (CpuExecutor's
        // future block-JIT path, BlockFunctionBuilder tests) can
        // construct additional functions targeting the same module.
        EmitterRegistry                          EmitterRegistry,
        OperandResolverRegistry                  ResolverRegistry,
        CpuStateLayout                           Layout);

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
            loaded.Cpu.ProcessorModes,
            loaded.Cpu.ExceptionVectors);

        var registry = new EmitterRegistry();
        StandardEmitters.RegisterAll(registry);

        var resolverRegistry = new OperandResolverRegistry();
        StandardOperandResolvers.RegisterAll(resolverRegistry);

        // R5: register architecture-specific emitter / resolver bundles.
        // Generic CPU specs that don't reference ARM-specific micro-ops or
        // operand kinds will simply not exercise any of these.
        var family = loaded.Cpu.Architecture.Family;
        if (string.Equals(family, "ARM", StringComparison.OrdinalIgnoreCase))
        {
            ArmEmitters.RegisterAll(registry);
        }
        else if (string.Equals(family, "Sharp-SM83", StringComparison.OrdinalIgnoreCase))
        {
            Lr35902Emitters.RegisterAll(registry);
        }

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
            {
                // Disambiguate the function key when a single format declares
                // multiple instructions sharing a mnemonic (e.g. Thumb F2 has
                // two ADDs and two SUBs distinguished only by selector value).
                // Without this, a second registration silently overwrites the
                // first in the dictionary AND collides on the LLVM module
                // function name.
                var mnemonicCounts = format.Instructions
                    .GroupBy(d => d.Mnemonic, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

                foreach (var def in format.Instructions)
                {
                    var ambiguousMnemonic = mnemonicCounts.TryGetValue(def.Mnemonic, out var c) && c > 1;
                    var keySuffix = ambiguousMnemonic && def.Selector is not null
                        ? $".{def.Mnemonic}_{def.Selector.Value}"
                        : $".{def.Mnemonic}";
                    var key = $"{setName}.{format.Name}{keySuffix}";

                    try
                    {
                        // Pass an LLVM-safe disambiguating name through to the builder.
                        var fn = fb.Build(set, format, def,
                            ambiguousMnemonic && def.Selector is not null
                                ? $"{def.Mnemonic}_{def.Selector.Value}"
                                : def.Mnemonic);
                        functions[key] = fn;
                    }
                    catch (Exception ex)
                    {
                        diagnostics.Add($"[{key}] emission failed: {ex.Message}");
                    }
                }
            }
        }

        // Module verification: gather diagnostics but don't abort.
        if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var verifyErr) &&
            !string.IsNullOrEmpty(verifyErr))
        {
            diagnostics.Add($"[verify] {verifyErr}");
        }

        return new CompileResult(module, decoderTables, functions, diagnostics,
            registry, resolverRegistry, layout);
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
