using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Phase 5: GbaSystemRunner ties CpuExecutor + scheduler + IRQ delivery
/// together. These tests verify the IRQ entry sequence is correct and
/// that running with the scheduler doesn't regress any existing
/// jsmolka behaviour.
/// </summary>
public class GbaSystemRunnerTests
{
    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    private sealed class Boot : IDisposable
    {
        public CpuExecutor Cpu { get; }
        public GbaMemoryBus Bus { get; }
        public Arm7tdmiBankSwapHandler Swap { get; }
        public HostRuntime Rt { get; }
        public GbaSystemRunner Runner { get; }
        private readonly IDisposable _busBinding;
        private readonly IDisposable _swapBinding;
        private readonly IDisposable _userBinding;

        public Boot(CpuExecutor cpu, GbaMemoryBus bus, Arm7tdmiBankSwapHandler swap,
                    HostRuntime rt, IDisposable bb, IDisposable sb, IDisposable ub)
        {
            Cpu = cpu; Bus = bus; Swap = swap; Rt = rt;
            _busBinding = bb; _swapBinding = sb; _userBinding = ub;
            Runner = new GbaSystemRunner(cpu, bus, swap);
        }
        public void Dispose()
        {
            _busBinding.Dispose(); _swapBinding.Dispose(); _userBinding.Dispose();
            Rt.Dispose();
        }
    }

    private static Boot BootSystem(byte[]? bios = null)
    {
        var bus = new GbaMemoryBus();
        if (bios is not null) bus.LoadBios(bios);
        else                  bus.InstallMinimalBiosStubs();

        var compileResult = SpecCompiler.Compile(CpuJson);
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        var layout = new CpuStateLayout(
            compileResult.Module.Context,
            loaded.Cpu.RegisterFile,
            loaded.Cpu.ProcessorModes,
            loaded.Cpu.ExceptionVectors);

        var rt = HostRuntime.Build(compileResult.Module, layout);
        var swap = new Arm7tdmiBankSwapHandler(rt);
        var bb = MemoryBusBindings.Install(rt, bus);
        var sb = BankSwapBindings.Install(rt, swap);
        var ub = UserModeRegBindings.Install(rt, swap);
        rt.Compile();

        var setsByName = new Dictionary<string, (InstructionSetSpec, DecoderTable)>(StringComparer.Ordinal);
        foreach (var (name, set) in loaded.InstructionSets)
            setsByName[name] = (set, new DecoderTable(set));
        var dispatch = loaded.Cpu.InstructionSetDispatch
            ?? throw new InvalidOperationException("missing instruction_set_dispatch");
        var exec = new CpuExecutor(rt, setsByName, dispatch, bus);

        // Post-BIOS state.
        exec.WriteStatus("CPSR", 0x1Fu);             // System mode, IRQs enabled (I=0)
        exec.WriteGpr(13, 0x03007F00u);
        exec.Pc = 0x08000000u;

        return new Boot(exec, bus, swap, rt, bb, sb, ub);
    }

    [Fact]
    public void DeliverIrqIfPending_NoOpWhenNoIrqRaised()
    {
        using var b = BootSystem();
        var pcBefore = b.Cpu.Pc;
        var cpsrBefore = b.Cpu.ReadStatus("CPSR");

        b.Runner.DeliverIrqIfPending();

        Assert.Equal(pcBefore, b.Cpu.Pc);
        Assert.Equal(cpsrBefore, b.Cpu.ReadStatus("CPSR"));
        Assert.Equal(0, b.Runner.IrqsDelivered);
    }

    [Fact]
    public void DeliverIrqIfPending_SwitchesToIrqMode_SetsLrAndPc()
    {
        using var b = BootSystem();
        b.Cpu.Pc = 0x08001000;

        // Enable VBlank in IE + IME, raise the IRQ.
        b.Bus.WriteHalfword(0x04000200, 0x0001);    // IE = VBlank
        b.Bus.WriteWord    (0x04000208, 0x00000001); // IME = 1
        b.Bus.RaiseInterrupt(GbaInterrupt.VBlank);

        b.Runner.DeliverIrqIfPending();

        Assert.Equal(1, b.Runner.IrqsDelivered);
        Assert.Equal(0x18u, b.Cpu.Pc);                              // jumped to vector

        var cpsr = b.Cpu.ReadStatus("CPSR");
        Assert.Equal(0x12u, cpsr & 0x1Fu);                          // IRQ mode
        Assert.NotEqual(0u, cpsr & 0x80u);                          // I bit set

        // R14_irq = pc + 4. After bank swap, visible R14 IS R14_irq.
        Assert.Equal(0x08001004u, b.Cpu.ReadGpr(14));
    }

    [Fact]
    public void DeliverIrqIfPending_RespectsCpsrIBit()
    {
        using var b = BootSystem();
        // Mask IRQs (set CPSR.I).
        var cpsr = b.Cpu.ReadStatus("CPSR");
        b.Cpu.WriteStatus("CPSR", cpsr | 0x80u);

        // Enable + raise.
        b.Bus.WriteHalfword(0x04000200, 0x0001);
        b.Bus.WriteWord    (0x04000208, 0x00000001);
        b.Bus.RaiseInterrupt(GbaInterrupt.VBlank);

        b.Runner.DeliverIrqIfPending();
        Assert.Equal(0, b.Runner.IrqsDelivered);
    }

    [Fact]
    public void RunFrames_AdvancesSchedulerAndStillReachesArmGbaHalt()
    {
        // Verify the SystemRunner doesn't regress jsmolka arm.gba completion.
        var rom = File.ReadAllBytes(Path.Combine(TestPaths.TestRomsRoot, "gba-tests", "arm", "arm.gba"));
        using var b = BootSystem();
        b.Bus.LoadRom(rom);

        // Run a generous number of CPU steps via RunCycles. arm.gba reaches
        // halt loop in ~7400 instructions; with cyclesPerInstr=4 that's ~30K
        // cycles. Use 10x margin.
        b.Runner.RunCycles(cycleBudget: 300_000);

        // R12 = 0 means all subtests passed.
        Assert.Equal(0u, b.Cpu.ReadGpr(12));
        Assert.True(b.Runner.Scheduler.FrameCount > 0,
            "scheduler should have advanced past at least one full frame");
    }
}
