using System.Buffers.Binary;
using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 3.2 — fetch-decode-execute loop tests. Lay a tiny ARM program
/// in a flat memory bus and let CpuExecutor.Step() / Run() walk through
/// it, asserting register state matches what the ARM ARM specifies.
/// </summary>
public class CpuExecutorTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    private sealed class Setup : IDisposable
    {
        public CpuExecutor Exec { get; }
        public FlatMemoryBus Bus { get; }
        public HostRuntime Rt { get; }
        private readonly IDisposable _busBinding;
        private readonly IDisposable _swapBinding;
        private readonly IDisposable _userRegBinding;

        public Setup(CpuExecutor exec, FlatMemoryBus bus, HostRuntime rt,
                     IDisposable busBinding, IDisposable swapBinding, IDisposable userRegBinding)
        {
            Exec = exec; Bus = bus; Rt = rt;
            _busBinding = busBinding; _swapBinding = swapBinding; _userRegBinding = userRegBinding;
        }

        public void Dispose()
        {
            _busBinding.Dispose();
            _swapBinding.Dispose();
            _userRegBinding.Dispose();
            Rt.Dispose();
        }
    }

    private static Setup BuildArm(uint[] program, uint loadAddr = 0)
    {
        var compileResult = SpecCompiler.Compile(CpuJson);
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        var armSet = loaded.InstructionSets["ARM"];
        var layout = new CpuStateLayout(
            compileResult.Module.Context,
            loaded.Cpu.RegisterFile,
            loaded.Cpu.ProcessorModes,
            loaded.Cpu.ExceptionVectors);

        var rt = HostRuntime.Build(compileResult.Module, layout);
        var bus = new FlatMemoryBus(0x1000);

        for (int i = 0; i < program.Length; i++)
            bus.WriteWord(loadAddr + (uint)(i * 4), program[i]);

        var swapHandler    = new Arm7tdmiBankSwapHandler(rt);
        var busBinding     = MemoryBusBindings.Install(rt, bus);
        var swapBinding    = BankSwapBindings.Install(rt, swapHandler);
        var userRegBinding = UserModeRegBindings.Install(rt, swapHandler);
        rt.Compile();

        var decoder = new DecoderTable(armSet);
        var exec = new CpuExecutor(rt, armSet, decoder, bus);
        exec.Pc = loadAddr;
        return new Setup(exec, bus, rt, busBinding, swapBinding, userRegBinding);
    }

    [Fact]
    public unsafe void Run_ThreeAdds_AccumulatesIntoR0()
    {
        // ADD R0, R0, #1   x3
        //   1110 001 0100 0 0000 0000 0000 00000001  = 0xE2800001
        var program = new uint[] { 0xE2800001, 0xE2800001, 0xE2800001 };

        using var setup = BuildArm(program);
        var exec = setup.Exec;

        exec.Run(maxSteps: 3);

        Assert.Equal(3u, exec.ReadGpr(0));
        Assert.Equal(12u, exec.Pc);  // started at 0, 3 × 4 bytes
    }

    [Fact]
    public unsafe void Run_BranchForward_JumpsAndContinues()
    {
        // Layout:
        //   0x00:  MOV R0, #1            ; set R0=1
        //   0x04:  B    +8 (to 0x10)     ; skip the next two
        //   0x08:  MOV R0, #99           ; should NOT execute
        //   0x0C:  MOV R0, #99           ; should NOT execute
        //   0x10:  MOV R0, #2            ; should run after branch
        //
        // ARM B encoding:
        //   cccc 101 L iiiiiiiiiiiiiiiiiiiiiiii   (offset = signed 24, shifted left 2, +8 for pipeline)
        //   We want target = 0x10, current PC+8 = 0x04+8 = 0x0C. So offset_bytes = 0x10 - 0x0C = 4.
        //   imm24 = 4 / 4 = 1. So: 1110 1010 000000000000000000000001 = 0xEA000001
        var program = new uint[]
        {
            0xE3A00001,  // MOV R0, #1
            0xEA000001,  // B +8
            0xE3A00063,  // MOV R0, #99 (skipped)
            0xE3A00063,  // MOV R0, #99 (skipped)
            0xE3A00002,  // MOV R0, #2
        };

        using var setup = BuildArm(program);
        var exec = setup.Exec;

        exec.Step();                   // MOV R0, #1
        Assert.Equal(1u, exec.ReadGpr(0));
        Assert.Equal(0x4u, exec.Pc);

        exec.Step();                   // B +8 → PC=0x10
        Assert.Equal(0x10u, exec.Pc);

        exec.Step();                   // MOV R0, #2
        Assert.Equal(2u, exec.ReadGpr(0));
        Assert.Equal(0x14u, exec.Pc);
    }

    [Fact]
    public unsafe void Run_LdrThenAdd_ReadsFromMemoryAndComputes()
    {
        // 0x00:  LDR R1, [R2, #0]    ; load word at *R2 into R1
        // 0x04:  ADD R0, R1, #5       ; R0 = R1 + 5
        // Pre-set: R2 = 0x100, memory[0x100] = 0x37
        // Expect: after 2 steps, R1=0x37, R0=0x3C
        //
        // LDR encoding: cccc 010 P U 0 W 1 nnnn dddd iiiiiiiiiiii
        //   1110 010 1 1 0 0 1 0010 0001 000000000000  = 0xE5921000
        // ADD imm: 1110 001 0100 0 0001 0000 0000 00000101 = 0xE2810005
        var program = new uint[]
        {
            0xE5921000,  // LDR R1, [R2, #0]
            0xE2810005,  // ADD R0, R1, #5
        };

        using var setup = BuildArm(program);
        var exec = setup.Exec;
        var bus  = setup.Bus;
        bus.WriteWord(0x100, 0x37u);
        exec.WriteGpr(2, 0x100u);

        exec.Run(maxSteps: 2);

        Assert.Equal(0x37u, exec.ReadGpr(1));
        Assert.Equal(0x3Cu, exec.ReadGpr(0));
        Assert.Equal(0x8u, exec.Pc);
    }
}
