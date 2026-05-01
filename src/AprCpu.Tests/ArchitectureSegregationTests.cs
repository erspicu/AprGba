using AprCpu.Core.IR;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// R5: ARM-specific emitters live in <see cref="ArmEmitters"/>, generic
/// ones in <see cref="StandardEmitters"/>. SpecCompiler picks based on
/// the spec's architecture.family, so a non-ARM spec won't have access
/// to ARM micro-ops.
/// </summary>
public class ArchitectureSegregationTests
{
    [Fact]
    public void StandardEmitters_DoesNotRegisterArmSpecificOps()
    {
        var reg = new EmitterRegistry();
        StandardEmitters.RegisterAll(reg);

        var armOnlyOps = new[]
        {
            "adc", "sbc", "rsc",
            "update_nz",
            "update_c_add", "update_c_sub",
            "update_v_add", "update_v_sub",
            "update_c_shifter", "update_c_add_carry", "update_c_sub_carry",
            "read_psr", "write_psr",
            "branch_indirect",
            "restore_cpsr_from_spsr",
        };

        foreach (var opName in armOnlyOps)
        {
            Assert.False(reg.TryGet(opName, out _),
                $"StandardEmitters should NOT register ARM-specific op '{opName}' " +
                $"(it belongs to ArmEmitters).");
        }
    }

    [Fact]
    public void StandardEmitters_RegistersGenericOps()
    {
        var reg = new EmitterRegistry();
        StandardEmitters.RegisterAll(reg);

        var genericOps = new[]
        {
            "read_reg", "write_reg",
            "add", "sub", "and", "or", "xor",
            "shl", "lsr", "asr", "rsb",
            "mvn", "bic",
            "if",
            "branch", "branch_link",
        };

        foreach (var opName in genericOps)
        {
            Assert.True(reg.TryGet(opName, out _),
                $"StandardEmitters should register generic op '{opName}'.");
        }
    }

    [Fact]
    public void ArmEmitters_RegistersExpectedOps()
    {
        var reg = new EmitterRegistry();
        ArmEmitters.RegisterAll(reg);

        var expected = new[]
        {
            "adc", "sbc", "rsc",
            "update_nz", "update_c_add", "update_c_sub",
            "update_v_add", "update_v_sub", "update_c_shifter",
            "update_c_add_carry", "update_c_sub_carry",
            "read_psr", "write_psr",
            "branch_indirect", "restore_cpsr_from_spsr",
        };

        foreach (var opName in expected)
        {
            Assert.True(reg.TryGet(opName, out _),
                $"ArmEmitters should register '{opName}'.");
        }
    }

    [Fact]
    public void ArmEmitters_DoesNotRegisterGenericOps()
    {
        // Sanity: ArmEmitters does not double-register ops that
        // StandardEmitters owns (would throw on combined registration).
        var reg = new EmitterRegistry();
        StandardEmitters.RegisterAll(reg);
        // Combined registration must succeed.
        ArmEmitters.RegisterAll(reg);

        // Spot-check that a couple of generic ops still come from Standard.
        Assert.True(reg.TryGet("add",        out _));
        Assert.True(reg.TryGet("write_reg",  out _));
        // And ARM-only ops are now also available.
        Assert.True(reg.TryGet("update_nz",  out _));
        Assert.True(reg.TryGet("read_psr",   out _));
    }
}
