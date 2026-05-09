// Phase 24.6.4 — X86JsonCpu (per-instr JSON-driven backend) end-to-end
// roundtrip tests. The smoke group from 24.6.2 (NOP + HLT) is the only
// coverage at this sub-step; later sub-phases (24.6.5 MOV, 24.6.6 ALU,
// 24.6.7 ctrl/shift/string/misc) bring real opcodes online and run the
// full Tom Harte SST 8088 v2 sweep against this backend.
//
// What this file proves:
//   - SpecCompiler → HostRuntime → ORC LLJIT pipeline hands back live
//     function pointers for x86-16 instructions
//   - X86JsonCpu.Step() correctly: fetches opcode at CS:IP, advances IP
//     past the opcode byte, dispatches to the JIT'd function, mirrors
//     state out via the X86State accessor
//   - x86_halt emitter works — HLT sets Halted=true after one Step()

using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;
using Xunit;

namespace AprCpu.Tests;

public class X86JsonCpuTests
{
    private static (X86JsonCpu cpu, X86Memory mem) Setup(byte[] code)
    {
        var mem = new X86Memory();
        mem.LoadBinary(code, 0, 0x100);
        var cpu = new X86JsonCpu(mem);
        cpu.Reset();
        cpu.SetEntryPoint(0, 0x100);
        return (cpu, mem);
    }

    [Fact]
    public void BackendName_IsJsonLlvm()
    {
        var (cpu, _) = Setup(new byte[] { 0xF4 });
        Assert.Equal("json-llvm", cpu.BackendName);
    }

    [Fact]
    public void Reset_RestoresArchitecturalResetState()
    {
        var (cpu, _) = Setup(new byte[] { 0x90 });
        // SetEntryPoint moved CS:IP; Reset should revert.
        cpu.Reset();
        Assert.Equal(0xFFFF, cpu.State.CS);
        Assert.Equal(0,      cpu.State.IP);
        Assert.False(cpu.Halted);
    }

    /// <summary>
    /// 0x90 NOP — empty steps body. After Step(), IP advances by 1, no
    /// other state changes, not halted.
    /// </summary>
    [Fact]
    public void Step_Nop_AdvancesIpByOneNothingElseChanges()
    {
        var (cpu, _) = Setup(new byte[] { 0x90 });

        var before = cpu.State;
        Assert.Equal(0x100, before.IP);

        cpu.Step();

        var after = cpu.State;
        Assert.Equal(0x101, after.IP);
        Assert.False(cpu.Halted);
        // GPRs untouched.
        Assert.Equal(before.A.X, after.A.X);
        Assert.Equal(before.B.X, after.B.X);
        Assert.Equal(before.C.X, after.C.X);
        Assert.Equal(before.D.X, after.D.X);
        // FLAGS untouched.
        Assert.Equal(before.GetFlags(), after.GetFlags());
    }

    /// <summary>
    /// 0xF4 HLT — single x86_halt step. After Step(), Halted=true and IP
    /// is past the HLT byte (silicon increments IP first, then halts).
    /// </summary>
    [Fact]
    public void Step_Hlt_SetsHaltedAndAdvancesIp()
    {
        var (cpu, _) = Setup(new byte[] { 0xF4 });

        Assert.False(cpu.Halted);
        cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(0x101, cpu.State.IP);
    }

    /// <summary>
    /// Several NOPs followed by HLT — exercises the function-pointer cache
    /// (NOP runs 3× from cache, HLT runs once) and the inter-step state
    /// continuity. After 4 steps Halted=true and IP=0x104.
    /// </summary>
    [Fact]
    public void Step_NopNopNopHlt_StopsAtHlt()
    {
        var (cpu, _) = Setup(new byte[] { 0x90, 0x90, 0x90, 0xF4 });

        for (int i = 0; i < 16 && !cpu.Halted; i++) cpu.Step();

        Assert.True(cpu.Halted);
        Assert.Equal(0x104, cpu.State.IP);
    }

    /// <summary>
    /// Step() returns -1 for an opcode the spec doesn't yet cover (e.g. 0x00
    /// in 24.6.4 — only NOP/HLT are wired). IP must NOT advance; the caller
    /// can fall through to legacy or extend the spec.
    /// </summary>
    [Fact]
    public void Step_UnknownOpcode_ReturnsMinusOneAndPreservesIp()
    {
        var (cpu, _) = Setup(new byte[] { 0x00, 0x00 });

        var rc = cpu.Step();
        Assert.Equal(-1, rc);
        Assert.Equal(0x100, cpu.State.IP);
        Assert.False(cpu.Halted);
    }

    /// <summary>
    /// LoadState mirrors a full architectural snapshot onto the spec
    /// buffer; State getter must round-trip the same values out (GPRs,
    /// segment regs, IP, FLAGS).
    /// </summary>
    [Fact]
    public void LoadState_ThenStateGetter_RoundTrips()
    {
        var mem = new X86Memory();
        var cpu = new X86JsonCpu(mem);

        var s = new X86State();
        s.A.X = 0x1234; s.C.X = 0x5678; s.D.X = 0x9ABC; s.B.X = 0xDEF0;
        s.SP = 0xCAFE; s.BP = 0xBABE; s.SI = 0x1111; s.DI = 0x2222;
        s.ES = 0xAA00; s.CS = 0xBB00; s.SS = 0xCC00; s.DS = 0xDD00;
        s.IP = 0x3333;
        s.FlagC = true;  s.FlagZ = true;  s.FlagS = true;
        s.FlagP = false; s.FlagA = false; s.FlagO = false;

        cpu.LoadState(s);
        var rt = cpu.State;

        Assert.Equal(0x1234, rt.A.X);
        Assert.Equal(0x5678, rt.C.X);
        Assert.Equal(0x9ABC, rt.D.X);
        Assert.Equal(0xDEF0, rt.B.X);
        Assert.Equal(0xCAFE, rt.SP);
        Assert.Equal(0xBABE, rt.BP);
        Assert.Equal(0x1111, rt.SI);
        Assert.Equal(0x2222, rt.DI);
        Assert.Equal(0xAA00, rt.ES);
        Assert.Equal(0xBB00, rt.CS);
        Assert.Equal(0xCC00, rt.SS);
        Assert.Equal(0xDD00, rt.DS);
        Assert.Equal(0x3333, rt.IP);
        Assert.True(rt.FlagC);  Assert.True(rt.FlagZ);  Assert.True(rt.FlagS);
        Assert.False(rt.FlagP); Assert.False(rt.FlagA); Assert.False(rt.FlagO);
    }
}
