using AprCpu.Core.JsonSpec;

namespace AprCpu.Core.IR;

/// <summary>
/// One operand resolver translates a single JSON-declared
/// <see cref="OperandResolver"/> (e.g. <c>{"kind":"immediate_rotated", ...}</c>)
/// into LLVM IR that pre-computes named values usable by subsequent
/// micro-op steps.
///
/// Resolvers populate <see cref="EmitContext.Values"/> with the names listed
/// in the format's <c>operands.&lt;name&gt;.outputs</c>. The same registry
/// pattern as <see cref="IMicroOpEmitter"/> lets new architectures plug in
/// their own operand kinds without touching framework code.
/// </summary>
public interface IOperandResolver
{
    string Kind { get; }
    void Resolve(EmitContext ctx, string operandName, OperandResolver resolver);
}

/// <summary>
/// Lookup table of operand resolvers by `kind` name. Mirrors
/// <see cref="EmitterRegistry"/>.
/// </summary>
public sealed class OperandResolverRegistry
{
    private readonly Dictionary<string, IOperandResolver> _resolvers
        = new(StringComparer.Ordinal);

    public void Register(IOperandResolver resolver)
    {
        if (_resolvers.ContainsKey(resolver.Kind))
            throw new InvalidOperationException(
                $"Operand resolver for kind '{resolver.Kind}' already registered.");
        _resolvers[resolver.Kind] = resolver;
    }

    public bool TryGet(string kind, out IOperandResolver resolver)
        => _resolvers.TryGetValue(kind, out resolver!);

    public IReadOnlyCollection<string> RegisteredKinds => _resolvers.Keys;

    /// <summary>
    /// Apply every operand resolver declared in the current format,
    /// populating <see cref="EmitContext.Values"/> with their outputs.
    /// </summary>
    public void Apply(EmitContext ctx)
    {
        foreach (var (name, resolver) in ctx.Format.Operands)
        {
            if (!_resolvers.TryGetValue(resolver.Kind, out var impl))
            {
                throw new NotSupportedException(
                    $"Operand resolver kind '{resolver.Kind}' is not registered (operand '{name}' in format '{ctx.Format.Name}'). " +
                    $"Available kinds: {string.Join(", ", _resolvers.Keys)}.");
            }
            impl.Resolve(ctx, name, resolver);
        }
    }
}
