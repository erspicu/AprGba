// PcLockstep — Phase 30.15b harness for finding the first architectural
// divergence between the per-instruction CPU and the block-JIT CPU when
// running real-pcxtbios + FreeDOS.
//
// Design rationale (per Gemini consult 20260517_024459.txt):
//   The block-JIT real-BIOS hang reproduces deep into the FreeDOS boot
//   (somewhere around the kernel-load CALL/JMP chain in 1FE0 segment).
//   Diff'ing screenshots can only tell you "they look different at
//   end of run"; we need an instruction-by-instruction comparator so
//   we can name the exact opcode whose block-JIT translation differs
//   from per-instr.
//
// Approach:
//   - Build TWO independent minimal-PC environments (each with its own
//     X86Memory, CPU, PortBus, FDC, DMA, PIT, PIC). Per-instr backend
//     on side A, block-JIT (forced to block size = 1) on side B.
//   - Both load the same BIOS image into their own memory and mount
//     the same floppy into their own FDC instance. Identical initial
//     state.
//   - Disable PIT-driven IRQ delivery (PcPit.IrqEnabled = false) so
//     timer interrupts don't fire at different wall-clock moments in
//     the two backends — kept architecturally deterministic.
//   - Step both via LockstepDiff.Run(). After each step it snapshots
//     X86State (AX..DI, segments, IP, FLAGS) and reports the first
//     register whose value differs.
//   - Before each Step we re-point the static "active" hooks
//     (`X86JsonCpu._activeMem` / `_activeCpu`, `X86JsonCpu.PortRead8Handler`,
//     `PcPortBus.Active`) so the JIT extern shims route into the
//     environment whose turn it is.
//
// Limitations / known caveats:
//   - The harness does NOT use the GUI / WinForms thread; runs in a
//     single thread from the CLI. PIT advances are driven manually
//     between step pairs (not wall-clock).
//   - Memory writes that bypass the X86JsonCpu extern (FDC DMA, HLE
//     bus writes from PcMemoryBus) happen in each environment's own
//     memory — the two stay in sync only as long as the CPUs execute
//     identical I/O. Once they diverge, HW state diverges too, which
//     is the expected outcome (we stop at the first divergence anyway).

using AprCpu.Core.JsonSpec;
using AprCpu.Core.Validation;
using AprPc.Cli.Hardware;
using AprPc.Cli.Memory;
using AprX86.Cli.Cpu;
using AprX86.Cli.Memory;
using AprX86.Cli.Validation;

namespace AprPc.Cli.Diagnostics;

/// <summary>
/// One self-contained PC environment (CPU + memory + bus + HW chips).
/// All hooks the JIT-emitted code needs (memory externs, port externs,
/// PortBus.Active) are switched onto this environment when Activate() is
/// called. The companion lockstep harness alternates Activate() between
/// the two envs before each Step().
/// </summary>
internal sealed class PcLockstepEnv
{
    public required string Tag { get; init; }
    public required PcMemoryBus Bus { get; init; }
    public required X86JsonCpu Cpu { get; init; }
    public required Pic8259 Pic { get; init; }
    public required PcPit Pit { get; init; }
    public Fdc8272? Fdc { get; init; }
    public Dma8237? Dma { get; init; }
    public required PcPortBus Ports { get; init; }

    /// <summary>
    /// Make this env the singleton seen by the unmanaged JIT extern shims.
    /// Called by the lockstep stepper before each Cpu.Step() so writes /
    /// reads land in the env's own memory and ports.
    /// </summary>
    public void Activate()
    {
        Cpu.SetActiveForLockstep();
        X86JsonCpu.PortRead8Handler   = Ports.Read8;
        X86JsonCpu.PortRead16Handler  = Ports.Read16;
        X86JsonCpu.PortWrite8Handler  = Ports.Write8;
        X86JsonCpu.PortWrite16Handler = Ports.Write16;
        PcPortBus.Active = Ports;
    }
}

/// <summary>
/// CLI entry point for `apr-pc --lockstep-bjit`. Constructs two PC envs
/// (A = per-instr, B = block-JIT with block size = 1) loaded with the
/// same BIOS + floppy, then runs LockstepDiff and prints the trail of
/// matched instructions plus the first diverging state.
/// </summary>
public static class PcLockstep
{
    public static int Run(PcOptions options)
    {
        Console.WriteLine("apr-pc lockstep diff (per-instr vs block-JIT)");
        Console.WriteLine($"  cpu       = {options.Cpu}");
        Console.WriteLine($"  bios      = {options.BiosPath ?? "(none — HLE not supported for lockstep)"}");
        Console.WriteLine($"  video-bios= {options.VideoBiosPath ?? "(none)"}");
        Console.WriteLine($"  floppy A  = {options.FloppyAPath ?? "(none)"}");
        Console.WriteLine($"  max-steps = {options.MaxCycles ?? 5_000_000}");

        // Force block-JIT side to compile one instruction at a time. With
        // multi-instr blocks the step counts in the two envs diverge — A
        // does 1 architectural instr per Step, B does N — and LockstepDiff
        // can't compare those.
        Environment.SetEnvironmentVariable("APR_X86_BLOCK_MAX", "1");

        var envA = BuildEnv("A=perinstr",  blockJit: false, options);
        var envB = BuildEnv("B=blockjit-1", blockJit: true,  options);

        if (options.FloppyAPath is { } fa)
        {
            var size = new FileInfo(fa).Length;
            if (size >= 64 * 1024)
            {
                var diskA = DiskImage.LoadFloppy(fa);
                envA.Fdc?.AttachDrive(0x00, diskA);
                var diskB = DiskImage.LoadFloppy(fa);   // separate instance, identical bytes
                envB.Fdc?.AttachDrive(0x00, diskB);
                Console.WriteLine($"  floppy mounted ({size / 1024} KB, {diskA.Cylinders}x{diskA.Heads}x{diskA.Sectors} CHS) into both envs");
            }
        }

        // Disable PIT IRQ on both so timer interrupts don't fire at
        // different wall-clock moments. The bug we're chasing is not
        // IRQ-timing related (verified earlier with APR_DISABLE_PIT_IRQ
        // diagnostic; black screen persisted), so dropping IRQs costs us
        // nothing diagnostically.
        envA.Pit.StopForLockstep();
        envB.Pit.StopForLockstep();
        Console.WriteLine("  PIT timer + IRQ:  fully stopped in both envs for deterministic lockstep");

        // Adapter wraps each env's CPU + Memory into ISteppableCpu so the
        // generic LockstepDiff harness can drive both. Step() on the
        // adapter calls Env.Activate() to swap the active singletons
        // before the actual Cpu.Step().
        var stepA = new X86LockstepStepper(envA);
        var stepB = new X86LockstepStepper(envB);

        // Phase 30.15c — manually advance the BDA tick counter every N
        // architectural instructions so BIOS POST delay loops that poll
        // the counter actually progress (without re-introducing the
        // wall-clock non-determinism of the real timer thread). The
        // period was chosen empirically — anything between 50 and 5000
        // tends to work; too small makes the BDA counter race ahead of
        // POST's expectation, too large makes POST take a long time.
        int detPit = ParseDetPitEnv(envDefault: 200);
        if (detPit > 0)
        {
            stepA.DeterministicPitPeriod = detPit;
            stepB.DeterministicPitPeriod = detPit;
            Console.WriteLine($"  PIT tick: deterministic, 1 BDA tick per {detPit} arch instructions");
        }
        else
        {
            Console.WriteLine("  PIT tick: fully disabled (APR_LOCKSTEP_PIT_PERIOD=0)");
        }

        // Phase 30.15c — APR_LOCKSTEP_IRQ=1 also asserts PIT IRQ on
        // each deterministic tick AND delivers pending vectors. Used
        // to reproduce the async-IRQ-related divergence the lockstep
        // harness misses by default.
        bool deliverIrqs = Environment.GetEnvironmentVariable("APR_LOCKSTEP_IRQ") == "1";
        if (deliverIrqs)
        {
            stepA.DeliverPendingIrqs = true;
            stepB.DeliverPendingIrqs = true;
            Console.WriteLine("  IRQ delivery: ENABLED (synthesised PUSH/JMP at every Step pre-check)");
        }

        long maxSteps = options.MaxCycles ?? 5_000_000;
        Console.WriteLine($"  running lockstep up to {maxSteps:N0} steps...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Ignore IP from the comparator? No — we WANT IP divergence to
        // surface, that's often the most useful early signal. Just compare
        // everything (PC is linear CS:IP, registers include IP separately
        // anyway). FLAGS too: but x86 emulators often diverge on
        // undefined bits (1/3/5/12-15), so we mask those out at compare.
        var ignore = new HashSet<string>(StringComparer.Ordinal);
        // (Reserved for future field exclusions; currently empty.)

        LockstepResult result = LockstepDiff.Run(
            stepA, stepB,
            maxSteps: maxSteps,
            haltConditionA: null,
            trailDepth: 16,
            ignoreFields: ignore,
            cycleSensitive: false);

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"  elapsed:  {sw.Elapsed}");
        Console.Write(result.FormatReport());

        if (result.Status == LockstepStatus.Diverged)
        {
            DumpContext(envA, envB, result);
            return 5;   // distinctive exit code so CI can flag
        }
        return result.Status == LockstepStatus.NoDiff ? 0 : 1;
    }

    /// <summary>
    /// Construct one PC environment: bus + CPU + HW chips. Mirrors the
    /// equivalent setup in <see cref="PcSystemRunner.Start"/> but trimmed
    /// to what the headless lockstep needs (no GUI, no emulator thread,
    /// no HLE BIOS install when BiosPath is set).
    /// </summary>
    private static PcLockstepEnv BuildEnv(string tag, bool blockJit, PcOptions options)
    {
        var machineSpecPath = PcMemoryBus.LocateMachineSpec();
        var spec = MachineSpecLoader.LoadFromFile(machineSpecPath);
        var bus = new PcMemoryBus(
            spec,
            biosMode: options.BiosMode,
            biosImagePath: options.BiosPath,
            videoBiosPath: options.VideoBiosPath);
        bus.Reset();

        List<string>? resolvedExtensions = null;
        if (spec.Extensions is { Count: > 0 } exts)
        {
            var machineDir = Path.GetDirectoryName(Path.GetFullPath(machineSpecPath))!;
            resolvedExtensions = new List<string>(exts.Count);
            foreach (var rel in exts)
                resolvedExtensions.Add(Path.GetFullPath(Path.Combine(machineDir, rel)));
        }

        // Build the CPU with the per-env backend choice. Both A and B
        // are X86JsonCpu — only the JIT-enable flag differs.
        var cpu = new X86JsonCpu(bus.Memory,
            enableBlockJit: blockJit,
            variant: options.Cpu,
            extensionPaths: resolvedExtensions);
        cpu.Reset();
        cpu.SetEntryPoint(0xFFFF, 0x0000);

        var pic = new Pic8259();
        pic.Reset();

        var pit = new PcPit(bus, pic);
        pit.Reset();
        // Phase 30.15c — Reset spins up a System.Threading.Timer that
        // would fire BDA tick increments on a thread-pool thread,
        // racing the lockstep stepper. Stop it immediately, even before
        // the harness's post-build StopForLockstep (which only fires
        // after both envs build, leaving a ~ms window for spurious ticks).
        pit.StopForLockstep();

        Fdc8272? fdc = null;
        Dma8237? dma = null;
        if (options.BiosPath is not null)
        {
            dma = new Dma8237();
            dma.Reset();
            fdc = new Fdc8272(dma, pic, bus.Memory, trace: false);
            fdc.Reset();
        }

        int floppyCount = (options.FloppyAPath is not null ? 1 : 0)
                        + (options.FloppyBPath is not null ? 1 : 0);
        if (floppyCount == 0) floppyCount = 1;
        var ports = new PcPortBus(pic, pit, traceIo: false,
            fdc: fdc, dma: dma, video: options.Video, floppyCount: floppyCount);

        var env = new PcLockstepEnv
        {
            Tag = tag,
            Bus = bus,
            Cpu = cpu,
            Pic = pic,
            Pit = pit,
            Fdc = fdc,
            Dma = dma,
            Ports = ports,
        };

        // Activate once at end so cpu.Reset already saw a coherent
        // _activeMem (it sets that itself, but extra defensive).
        env.Activate();
        return env;
    }

    /// <summary>
    /// On divergence, print 32 bytes around each side's PC so the trail
    /// reader can see what instruction caused it.
    /// </summary>
    /// <summary>
    /// Read APR_LOCKSTEP_PIT_PERIOD env var. Lets the user tune
    /// determinism vs speed without rebuild. 0 = fully disabled,
    /// missing = default.
    /// </summary>
    private static int ParseDetPitEnv(int envDefault)
    {
        var s = Environment.GetEnvironmentVariable("APR_LOCKSTEP_PIT_PERIOD");
        if (s is null) return envDefault;
        return int.TryParse(s, out var n) ? n : envDefault;
    }

    private static void DumpContext(PcLockstepEnv envA, PcLockstepEnv envB, LockstepResult result)
    {
        Console.WriteLine();
        Console.WriteLine($"  --- divergence context ---");
        DumpEnv("A (per-instr)", envA, (int)result.SnapshotA.Pc);
        DumpEnv("B (block-JIT)", envB, (int)result.SnapshotB.Pc);
    }

    private static void DumpEnv(string label, PcLockstepEnv env, int pc)
    {
        int from = Math.Max(0, pc - 16);
        Console.Write($"  {label}  bytes @ 0x{from:X5}: ");
        for (int i = from; i < from + 32 && i < 0x100000; i++)
            Console.Write($"{env.Bus.Memory.ReadByte(i):X2}{(i == pc ? "*" : " ")}");
        Console.WriteLine();
    }
}

/// <summary>
/// Thin wrapper around <see cref="X86SteppableCpu"/> that also activates
/// the surrounding <see cref="PcLockstepEnv"/> (swaps port/memory hooks)
/// before each step.
/// </summary>
internal sealed class X86LockstepStepper : ISteppableCpu
{
    private readonly PcLockstepEnv _env;
    private long _steps;

    public X86LockstepStepper(PcLockstepEnv env) { _env = env; }

    /// <summary>
    /// When &gt; 0, manually advance the BDA tick counter by 1 every N
    /// architectural-instruction steps. Both A and B steppers should use
    /// the same value so the two PITs stay in sync without depending on
    /// the wall-clock timer (which would fire on a thread-pool thread
    /// at different moments in A vs B and surface false divergences).
    /// </summary>
    public int DeterministicPitPeriod { get; set; } = 0;

    public X86JsonCpu Cpu => _env.Cpu;
    public PcLockstepEnv Env => _env;

    public string Name => _env.Tag;

    public ICpuStateSnapshot Snapshot()
    {
        _env.Activate();
        return new X86CpuStateSnapshot(_env.Cpu.State, _steps);
    }

    public void Step()
    {
        _env.Activate();
        // Phase 30.15c — mirror PcSystemRunner's pre-step IRQ delivery
        // so the lockstep harness can reproduce the async-IRQ path that
        // the normal emulator uses. When DeterministicPitPeriod > 0 the
        // step counter periodically asserts IRQ 0 (PIT timer); if IF=1
        // at that moment, the pending vector is delivered (synthesised
        // PUSH FLAGS/CS/IP + JMP IVT[n]) BEFORE the next Cpu.Step().
        if (DeliverPendingIrqs)
        {
            var st = _env.Cpu.State;
            if (st.FlagI && _env.Pic.DequeueNextVector() is byte vec)
            {
                DeliverInterruptToEnv(_env, vec, st);
                _env.Cpu.LoadState(st);
            }
        }
        _env.Cpu.Step();
        _steps++;
        if (DeterministicPitPeriod > 0 && (_steps % DeterministicPitPeriod) == 0)
        {
            _env.Pit.AdvanceTicks(1);
            if (DeliverPendingIrqs)
                _env.Pic.AssertIrq(0);
        }
    }

    /// <summary>
    /// When true, also mirror PcSystemRunner's pre-step IRQ delivery
    /// (asserting PIT IRQ on the deterministic tick + delivering the
    /// next pending vector before each Step). Off by default — used
    /// only when hunting Phase 30.15c async-IRQ bugs.
    /// </summary>
    public bool DeliverPendingIrqs { get; set; } = false;

    private static void DeliverInterruptToEnv(PcLockstepEnv env, byte vec, AprX86.Cli.Cpu.X86State st)
    {
        // Identical to PcSystemRunner.DeliverInterrupt minus the trace
        // log (which references private _irqCounts there). Pushes
        // FLAGS, CS, IP onto the SS stack, clears IF/TF, jumps to
        // IVT[vec].
        var bus = env.Bus;
        ushort flagsVal = st.GetFlags();
        st.SP = (ushort)(st.SP - 2);
        bus.WriteWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, st.SP), flagsVal);
        st.SP = (ushort)(st.SP - 2);
        bus.WriteWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, st.SP), st.CS);
        st.SP = (ushort)(st.SP - 2);
        bus.WriteWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, st.SP), st.IP);
        st.FlagI = false;
        st.FlagT = false;
        int slot = vec * 4;
        st.IP = bus.ReadWord16(slot);
        st.CS = bus.ReadWord16(slot + 2);
    }

    public byte ReadByteFromBus(ulong addr)
        => _env.Bus.Memory.ReadByte((int)(addr & 0xFFFFF));
}
