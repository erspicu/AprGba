using AprCpu.Core.JsonSpec;

namespace AprCpu.Core.IR;

/// <summary>
/// One emitter is responsible for translating exactly one
/// micro-op name (e.g. "add", "write_reg", "if") into LLVM IR.
/// </summary>
public interface IMicroOpEmitter
{
    string OpName { get; }
    void Emit(EmitContext ctx, MicroOpStep step);
}

/// <summary>Lookup table of emitters by op name.</summary>
public sealed class EmitterRegistry
{
    private readonly Dictionary<string, IMicroOpEmitter> _emitters
        = new(StringComparer.Ordinal);

    public void Register(IMicroOpEmitter emitter)
    {
        if (_emitters.ContainsKey(emitter.OpName))
            throw new InvalidOperationException($"Emitter for op '{emitter.OpName}' already registered.");
        _emitters[emitter.OpName] = emitter;
    }

    public bool TryGet(string opName, out IMicroOpEmitter emitter)
        => _emitters.TryGetValue(opName, out emitter!);

    public void EmitStep(EmitContext ctx, MicroOpStep step)
    {
        if (!_emitters.TryGetValue(step.Op, out var emitter))
        {
            throw new InvalidOperationException(
                $"No emitter registered for micro-op '{step.Op}' (instruction '{ctx.Def.Mnemonic}' in format '{ctx.Format.Name}').");
        }
        emitter.Emit(ctx, step);
    }

    public IReadOnlyCollection<string> RegisteredOpNames => _emitters.Keys;
}
