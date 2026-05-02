namespace AprCpu.Core.Runtime.Gba;

/// <summary>
/// Phase 5: ties CpuExecutor + GbaMemoryBus + GbaScheduler together.
/// One Step does: cpu.Step → scheduler.Tick → deliver IRQ if pending.
/// One RunFrames advances by N full GBA frames (228 scanlines × 1232
/// cycles each) which is enough granularity for headless test-ROM
/// screenshots.
///
/// Cycle accounting is approximate: every instruction is charged a
/// constant <c>cyclesPerInstr</c> (default 4 = one ARM S-cycle). That's
/// way off for real games but fine for our purpose — VBlank still fires
/// once per frame, DMA still completes, and jsmolka tests still terminate.
/// Phase 7 / future cycle-accurate work would replace this with per-
/// instruction cycle tracking from the spec's <c>cycles.form</c>.
///
/// IRQ delivery (DeliverIrqIfPending) follows the ARM IRQ entry
/// sequence: save SPSR_irq, switch CPSR to IRQ mode + disable I bit,
/// call BankSwap so visible R13/R14 point at IRQ slots, set R14_irq =
/// next-PC + 4 (standard SUBS PC, LR, #4 return convention), branch to
/// 0x00000018. The IRQ handler in BIOS (or HLE shim) takes it from there.
/// </summary>
public sealed unsafe class GbaSystemRunner
{
    private const uint CpsrModeMask = 0x1Fu;
    private const uint CpsrIBit     = 0x80u;
    private const uint IrqModeEnc   = 0x12u;
    private const uint IrqVector    = 0x00000018u;

    public CpuExecutor              Cpu       { get; }
    public GbaMemoryBus             Bus       { get; }
    public GbaScheduler             Scheduler { get; }
    public Arm7tdmiBankSwapHandler  Swap      { get; }

    public long IrqsDelivered { get; private set; }

    public GbaSystemRunner(CpuExecutor cpu, GbaMemoryBus bus, Arm7tdmiBankSwapHandler swap)
    {
        Cpu  = cpu;
        Bus  = bus;
        Swap = swap;
        Scheduler = new GbaScheduler(bus);
    }

    /// <summary>Run for at least <paramref name="cycleBudget"/> CPU cycles.</summary>
    public long RunCycles(long cycleBudget, int cyclesPerInstr = 4)
    {
        long consumed = 0;
        while (consumed < cycleBudget)
        {
            if (Bus.CpuHalted)
            {
                // CPU paused on HALTCNT write — only tick scheduler so VBlank/
                // HBlank/Timer can fire and raise an IRQ that wakes us.
                // Wake on ANY IE & IF transition (regardless of IME, per GBATEK).
                Scheduler.Tick(cyclesPerInstr);
                consumed += cyclesPerInstr;
                if (BusHasAnyPendingIrq()) Bus.CpuHalted = false;
                DeliverIrqIfPending();
                continue;
            }

            Cpu.Step();
            Scheduler.Tick(cyclesPerInstr);
            consumed += cyclesPerInstr;
            DeliverIrqIfPending();
        }
        return consumed;
    }

    /// <summary>
    /// HALT wake-up condition is IE &amp; IF != 0, regardless of IME (the
    /// CPU resumes; whether the IRQ then dispatches depends on IME).
    /// </summary>
    private bool BusHasAnyPendingIrq()
    {
        var ie = Bus.Io[(int)GbaMemoryMap.IE_Off]
               | (Bus.Io[(int)GbaMemoryMap.IE_Off + 1] << 8);
        var iff = Bus.Io[(int)GbaMemoryMap.IF_Off]
                | (Bus.Io[(int)GbaMemoryMap.IF_Off + 1] << 8);
        return (ie & iff & 0x3FFF) != 0;
    }

    /// <summary>Run exactly <paramref name="frames"/> full GBA frames.</summary>
    public long RunFrames(int frames, int cyclesPerInstr = 4)
        => RunCycles((long)frames * GbaScheduler.CyclesPerFrame, cyclesPerInstr);

    /// <summary>
    /// Standard ARM IRQ entry. Called after each Step when an enabled
    /// IRQ is pending. No-op if CPSR.I disables IRQs.
    /// </summary>
    public void DeliverIrqIfPending()
    {
        if (!Bus.HasPendingInterrupt()) return;

        var cpsr = Cpu.ReadStatus("CPSR");
        if ((cpsr & CpsrIBit) != 0) return;       // IRQs masked

        var oldMode = cpsr & CpsrModeMask;
        var pc      = Cpu.Pc;

        // 1. Save SPSR_irq = old CPSR (write to banked SPSR slot directly).
        Cpu.WriteStatus("SPSR", cpsr, "IRQ");

        // 2. Switch to IRQ mode + set I bit. SwapBank uses the OLD mode
        //    field on entry, so we change CPSR first then call swap.
        var newCpsr = (cpsr & ~CpsrModeMask) | IrqModeEnc | CpsrIBit;
        Cpu.WriteStatus("CPSR", newCpsr);

        // 3. Move banked R13/R14 — visible R13/R14 now point at IRQ slots.
        fixed (byte* state = Cpu.State)
            Swap.SwapBank(state, oldMode, IrqModeEnc);

        // 4. R14_irq = next-PC + 4. IRQ handler returns via SUBS PC, LR, #4
        //    which lands back at "next-PC" (the instruction the CPU was
        //    about to execute).
        Cpu.WriteGpr(14, pc + 4);

        // 5. Branch to vector.
        Cpu.Pc = IrqVector;

        IrqsDelivered++;
    }
}
