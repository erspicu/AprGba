using AprCpu.Core.Compilation;
using AprCpu.Core.Decoder;
using AprCpu.Core.IR;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;
using AprCpu.Core.Runtime.Gba;
using Xunit;
using Xunit.Abstractions;

namespace AprCpu.Tests;

/// <summary>
/// Phase 4.2 — drive a real GBA ROM through the host runtime end-to-end.
/// The big assertion is "jsmolka's arm/arm.gba reaches its halt loop with
/// R12 == 0", which means every ARM-mode subtest passed.
///
/// Initial CPU state mirrors what the GBA BIOS leaves before jumping
/// to the cart:
///   CPSR = System mode (0x1F), no flags, IRQ/FIQ enabled
///   R13 (SP) = 0x03007F00 (top of usable IWRAM for User/System)
///   PC = 0x08000000 (ROM entry)
/// </summary>
public class GbaRomExecutionTests
{
    private readonly ITestOutputHelper _output;
    public GbaRomExecutionTests(ITestOutputHelper output) => _output = output;

    private static string CpuJson => Path.Combine(TestPaths.SpecRoot, "arm7tdmi", "cpu.json");

    private sealed class Setup : IDisposable
    {
        public CpuExecutor Exec { get; }
        public GbaMemoryBus Bus { get; }
        public HostRuntime Rt { get; }
        private readonly IDisposable _busBinding;
        private readonly IDisposable _swapBinding;
        private readonly IDisposable _userRegBinding;
        public Setup(CpuExecutor e, GbaMemoryBus b, HostRuntime r,
                     IDisposable bb, IDisposable sb, IDisposable ub)
        { Exec = e; Bus = b; Rt = r; _busBinding = bb; _swapBinding = sb; _userRegBinding = ub; }
        public void Dispose()
        {
            _busBinding.Dispose();
            _swapBinding.Dispose();
            _userRegBinding.Dispose();
            Rt.Dispose();
        }
    }

    private static Setup BootGba(GbaMemoryBus bus)
    {
        bus.InstallMinimalBiosStubs();

        var compileResult = SpecCompiler.Compile(CpuJson);
        var loaded = SpecLoader.LoadCpuSpec(CpuJson);
        var layout = new CpuStateLayout(
            compileResult.Module.Context,
            loaded.Cpu.RegisterFile,
            loaded.Cpu.ProcessorModes,
            loaded.Cpu.ExceptionVectors);

        var rt = HostRuntime.Build(compileResult.Module, layout);
        var swapHandler  = new Arm7tdmiBankSwapHandler(rt);
        var busBinding   = MemoryBusBindings.Install(rt, bus);
        var swapBinding  = BankSwapBindings.Install(rt, swapHandler);
        var userBinding  = UserModeRegBindings.Install(rt, swapHandler);
        rt.Compile();

        // Multi-set executor: read CPSR.T to dispatch ARM vs Thumb.
        var setsByName = new Dictionary<string, (InstructionSetSpec, DecoderTable)>(StringComparer.Ordinal);
        foreach (var (name, set) in loaded.InstructionSets)
            setsByName[name] = (set, new DecoderTable(set));
        var dispatch = loaded.Cpu.InstructionSetDispatch
            ?? throw new InvalidOperationException("CPU spec missing instruction_set_dispatch");
        var exec = new CpuExecutor(rt, setsByName, dispatch, bus);

        // Post-BIOS initial state.
        exec.WriteStatus("CPSR", 0x1Fu);             // System mode, no flags
        exec.WriteGpr(13, 0x03007F00u);              // User/System SP at top of IWRAM
        exec.Pc = 0x08000000u;                       // ROM entry

        return new Setup(exec, bus, rt, busBinding, swapBinding, userBinding);
    }

    [Fact]
    public void RunUntilHalt_StopsAtSelfBranch()
    {
        // Sanity-check the halt detector with a tiny in-bus program:
        //   0x00: MOV R0, #1   (0xE3A00001)
        //   0x04: B    .       (0xEAFFFFFE — branch with offset = -2 → target = PC)
        var bus = new GbaMemoryBus();
        // Put the program in EWRAM so we can run it from there
        bus.WriteWord(0x02000000, 0xE3A00001);
        bus.WriteWord(0x02000004, 0xEAFFFFFE);

        var setup = BootGba(bus);
        try
        {
            setup.Exec.Pc = 0x02000000u;
            var (executed, halted) = setup.Exec.RunUntilHalt(maxSteps: 10);
            Assert.True(halted, "Should detect B-to-self halt");
            Assert.Equal(1u, setup.Exec.ReadGpr(0));
            Assert.InRange(executed, 2, 3);  // MOV + B + (next iter detects halt)
        }
        finally { setup.Dispose(); }
    }

    [Fact]
    public void JsmolkaArmGba_RunsToHaltLoop()
    {
        // Phase 4.2 infrastructure check: the ROM loads, executes, and
        // reaches its `idle: b idle` halt within a generous step budget.
        // Whether all subtests pass is a separate question — the
        // SkippedAllTestsPass test below tracks that.
        var romPath = Path.Combine(TestPaths.TestRomsRoot, "gba-tests", "arm", "arm.gba");
        if (!File.Exists(romPath))
            throw new Xunit.Sdk.XunitException($"ROM missing: {romPath}");

        var bus = new GbaMemoryBus();
        bus.LoadRom(File.ReadAllBytes(romPath));
        using var setup = BootGba(bus);

        var (executed, halted) = setup.Exec.RunUntilHalt(maxSteps: 200_000);

        var r12 = setup.Exec.ReadGpr(12);
        _output.WriteLine($"executed={executed} halted={halted} r12=0x{r12:X8} pc=0x{setup.Exec.Pc:X8}");
        Assert.True(halted, $"ROM did not reach halt loop in {executed} instructions (PC=0x{setup.Exec.Pc:X8}, R12=0x{r12:X8})");
    }

    [Fact]
    public unsafe void StmfdUserMode_AccessesUserRegistersFromFiq()
    {
        // Trace test 511 in isolation:
        //   1. start in System mode, set R8 = 32 (User R8)
        //   2. switch to FIQ via direct CPSR write + bank swap
        //   3. set R8 = 64 (FIQ R8) directly via state
        //   4. encode + run STMFD r0, {r8, r9}^ (S-bit set)
        //   5. expected stored value at [r0-8] = 32 (user R8), not 64
        var bus = new GbaMemoryBus();
        using var setup = BootGba(bus);
        var exec = setup.Exec;

        // Setup: R0 = 0x03001000 (IWRAM), R8_user = 32
        exec.WriteGpr(0, 0x03001000u);
        exec.WriteGpr(8, 32u);
        exec.WriteStatus("CPSR", 0x1Fu);     // System mode

        // Manually invoke the SwapBank path: System(0x1F) → FIQ(0x11)
        var handler = new Arm7tdmiBankSwapHandler(setup.Rt);
        // We can't reuse handler instance from the BootGba — but the bindings
        // already wired one. Instead, execute an MSR via the IR.
        // Use a minimal in-EWRAM program: msr cpsr, #0x11; stmfd r0, {r8, r9}^
        // Encoding for `msr cpsr_fc, #0x11`:
        //   cccc 00110 P 10 ffff 1111 rrrr iiiiiiii  with f=1100 (control+flags)... actually
        //   simplest: set_user_mode bits via field_mask = 0001 (control only).
        //   Encoding: 1110 0011 0010 0001 1111 0000 0001 0001 = 0xE321F011
        bus.WriteWord(0x02000000, 0xE321F011);   // msr cpsr_c, #0x11 (FIQ)
        bus.WriteWord(0x02000004, 0xE3A0_8040);  // mov r8, #64 (sets FIQ R8 = 64)
        // STMFD r0, {r8, r9}^  — STM with P=1 U=0 S=1 W=0 L=0
        //   cccc 100 P U S W L nnnn 0000_0011_0000_0000  (regs r8, r9)
        //   1110 100 1 0 1 0 0 0000 0000 0011 0000 0000 = 0xE9400300
        bus.WriteWord(0x02000008, 0xE9400300);
        // halt
        bus.WriteWord(0x0200000C, 0xEAFFFFFE);   // b .

        exec.Pc = 0x02000000u;
        exec.RunUntilHalt(maxSteps: 20);

        var storedR8 = bus.ReadWord(0x03001000u - 8);  // [r0 - 8] = stored r8
        var storedR9 = bus.ReadWord(0x03001000u - 4);  // [r0 - 4] = stored r9
        _output.WriteLine($"stored R8={storedR8} (expect 32, user value)  R9={storedR9} (expect 0)");
        _output.WriteLine($"final visible R8 = {exec.ReadGpr(8)} (expect 64, FIQ value)");

        Assert.Equal(32u, storedR8);   // User R8, not FIQ R8 (=64)
    }

    [Fact]
    public void JsmolkaArmGba_AllTestsPass_R12IsZero()
    {
        var romPath = Path.Combine(TestPaths.TestRomsRoot, "gba-tests", "arm", "arm.gba");
        if (!File.Exists(romPath))
            throw new Xunit.Sdk.XunitException($"ROM missing: {romPath}");

        var bus = new GbaMemoryBus();
        bus.LoadRom(File.ReadAllBytes(romPath));
        using var setup = BootGba(bus);

        var (executed, halted) = setup.Exec.RunUntilHalt(maxSteps: 200_000);

        Assert.True(halted);
        Assert.Equal(0u, setup.Exec.ReadGpr(12));
    }

    [Fact]
    public void JsmolkaThumbGba_TraceUntilPcLeavesRomCode()
    {
        var romPath = Path.Combine(TestPaths.TestRomsRoot, "gba-tests", "thumb", "thumb.gba");
        var romBytes = File.ReadAllBytes(romPath);
        var bus = new GbaMemoryBus();
        bus.LoadRom(romBytes);
        using var setup = BootGba(bus);

        var ring = new (uint pc, uint instr, uint t, uint r7)[20];
        int idx = 0, n = 0;
        while (n < 200_000)
        {
            var pc = setup.Exec.Pc;
            var cpsr = setup.Exec.ReadStatus("CPSR");
            var t = (cpsr >> 5) & 1;
            uint instr = t == 1 ? bus.ReadHalfword(pc) : bus.ReadWord(pc);
            var r7 = setup.Exec.ReadGpr(7);
            ring[idx] = (pc, instr, t, r7);
            idx = (idx + 1) % ring.Length;

            // Anomaly: PC misaligned (Thumb requires bit 0 = 0).
            if (t == 1 && (pc & 1) == 1)
            {
                _output.WriteLine($"PC went odd at step {n}: pc=0x{pc:X8}");
                break;
            }
            // Anomaly: PC outside actual ROM bytes.
            uint pcOff = pc - 0x08000000u;
            if (pcOff >= (uint)romBytes.Length)
            {
                _output.WriteLine($"PC left ROM bytes at step {n}: pc=0x{pc:X8} (rom size 0x{romBytes.Length:X})");
                break;
            }
            try { setup.Exec.Step(); }
            catch (Exception ex) { _output.WriteLine($"step {n}: THROW {ex.Message}"); break; }
            n++;
        }
        _output.WriteLine($"--- last 20 steps before anomaly (n={n}) ---");
        for (int i = 0; i < ring.Length; i++)
        {
            var k = (idx + i) % ring.Length;
            var (pc, instr, t, r7) = ring[k];
            _output.WriteLine($"  pc=0x{pc:X8} {(t == 1 ? "Thumb" : "ARM  ")} instr=0x{instr:X8} R7=0x{r7:X8}");
        }
    }

    [Fact]
    public void JsmolkaThumbGba_TraceFirstSteps()
    {
        var romPath = Path.Combine(TestPaths.TestRomsRoot, "gba-tests", "thumb", "thumb.gba");
        var bus = new GbaMemoryBus();
        bus.LoadRom(File.ReadAllBytes(romPath));
        using var setup = BootGba(bus);

        for (int i = 0; i < 50; i++)
        {
            var pc = setup.Exec.Pc;
            var cpsr = setup.Exec.ReadStatus("CPSR");
            var t = (cpsr >> 5) & 1;
            uint instr = t == 1 ? bus.ReadHalfword(pc) : bus.ReadWord(pc);
            _output.WriteLine($"step {i,3} pc=0x{pc:X8} {(t == 1 ? "Thumb" : "ARM")} instr=0x{instr:X8} R7=0x{setup.Exec.ReadGpr(7):X8}");
            try { setup.Exec.Step(); }
            catch (Exception ex) { _output.WriteLine($"  THROW: {ex.Message}"); break; }
        }
    }

    [Fact]
    public void JsmolkaThumbGba_RunsToHaltLoop()
    {
        // Phase 4.4 infrastructure check: thumb.gba loads, executes
        // (with ARM↔Thumb mode switching via BX), and reaches halt.
        // R7 (not R12!) holds the failed-test signal in thumb.gba.
        var romPath = Path.Combine(TestPaths.TestRomsRoot, "gba-tests", "thumb", "thumb.gba");
        if (!File.Exists(romPath))
            throw new Xunit.Sdk.XunitException($"ROM missing: {romPath}");

        var bus = new GbaMemoryBus();
        bus.LoadRom(File.ReadAllBytes(romPath));
        using var setup = BootGba(bus);

        var (executed, halted) = setup.Exec.RunUntilHalt(maxSteps: 200_000);

        var r7 = setup.Exec.ReadGpr(7);
        var cpsr = setup.Exec.ReadStatus("CPSR");
        _output.WriteLine($"executed={executed} halted={halted} r7=0x{r7:X8} pc=0x{setup.Exec.Pc:X8} cpsr=0x{cpsr:X8}");
        Assert.True(halted, $"thumb.gba did not reach halt loop in {executed} steps (PC=0x{setup.Exec.Pc:X8} R7=0x{r7:X8})");
    }

}
