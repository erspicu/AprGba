using AprCpu.Core.IR;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// R3: OperandResolverRegistry parallels EmitterRegistry. Verifies built-in
/// resolvers are registered and that custom kinds plug in cleanly without
/// touching framework code.
/// </summary>
public class OperandResolverRegistryTests
{
    [Fact]
    public void StandardOperandResolvers_RegistersAllBuiltInKinds()
    {
        var reg = new OperandResolverRegistry();
        StandardOperandResolvers.RegisterAll(reg);

        var kinds = reg.RegisteredKinds.ToHashSet();
        Assert.Contains("immediate_rotated",              kinds);
        Assert.Contains("pc_relative_offset",             kinds);
        Assert.Contains("register_direct",                kinds);
        Assert.Contains("shifted_register_by_immediate",  kinds);
        Assert.Contains("shifted_register_by_register",   kinds);
    }

    [Fact]
    public void Register_RejectsDuplicateKind()
    {
        var reg = new OperandResolverRegistry();
        StandardOperandResolvers.RegisterAll(reg);

        // Re-registering the same kind (here via a stub with kind="immediate_rotated")
        // must throw — duplicate registrations would silently shadow built-ins.
        Assert.Throws<InvalidOperationException>(
            () => reg.Register(new StubResolver("immediate_rotated")));
    }

    private sealed class StubResolver : IOperandResolver
    {
        public StubResolver(string kind) { Kind = kind; }
        public string Kind { get; }
        public void Resolve(EmitContext ctx, string operandName, AprCpu.Core.JsonSpec.OperandResolver resolver) { }
    }

    [Fact]
    public void TryGet_UnknownKindReturnsFalse()
    {
        var reg = new OperandResolverRegistry();
        StandardOperandResolvers.RegisterAll(reg);

        Assert.False(reg.TryGet("nonexistent_kind", out _));
    }

    [Fact]
    public void CustomResolver_PluginIsCallable()
    {
        var reg = new OperandResolverRegistry();
        reg.Register(new FakeCustomResolver());

        Assert.True(reg.TryGet("test_custom_kind", out var r));
        Assert.Equal("test_custom_kind", r.Kind);
    }

    private sealed class FakeCustomResolver : IOperandResolver
    {
        public string Kind => "test_custom_kind";
        public void Resolve(EmitContext ctx, string operandName, AprCpu.Core.JsonSpec.OperandResolver resolver)
        {
            // No-op: this test only exercises registration / lookup, not IR emit.
        }
    }
}
