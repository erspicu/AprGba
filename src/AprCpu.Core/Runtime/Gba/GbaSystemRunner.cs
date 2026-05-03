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
    private const uint CpsrTBit     = 0x20u;       // Thumb-state bit (bit 5)
    private const uint CpsrIBit     = 0x80u;
    private const uint IrqModeEnc   = 0x12u;
    private const uint IrqVector    = 0x00000018u;

    public CpuExecutor              Cpu       { get; }
    public GbaMemoryBus             Bus       { get; }
    public GbaScheduler             Scheduler { get; }
    public Arm7tdmiBankSwapHandler  Swap      { get; }

    public long IrqsDelivered { get; private set; }

    /// <summary>Phase 7 A.6.1 phase1b — original budget assigned to Cpu.CyclesLeft
    /// at the start of the current Step(). Used by MMIO catch-up to figure out
    /// "how many cycles consumed since Step entry".</summary>
    private int _budgetAtStep;

    /// <summary>Phase 7 A.6.1 phase1b — re-entry guard for SyncSchedulerForMmio.
    /// Scheduler.Tick can cross VBlank/HBlank, calling Dma.TriggerOnVBlank,
    /// which may itself read MMIO via the bus — re-firing OnMmioRead.
    /// Without this guard the recursion crashes testhost via stack overflow.</summary>
    private bool _inMmioSync;

    public GbaSystemRunner(CpuExecutor cpu, GbaMemoryBus bus, Arm7tdmiBankSwapHandler swap)
    {
        Cpu  = cpu;
        Bus  = bus;
        Swap = swap;
        Scheduler = new GbaScheduler(bus);
        // Phase 7 A.6.1 phase1b — bus calls this before any IO read so the
        // scheduler advances to "now" for accurate VCOUNT/DISPSTAT/IF reads
        // by polling code inside a block-JIT block.
        Bus.OnMmioRead = SyncSchedulerForMmio;
    }

    /// <summary>
    /// Called by the bus before MMIO reads. Computes cycles consumed since
    /// the current Step started, ticks the scheduler, delivers IRQs, and
    /// rebases the budget so the rest of the block doesn't double-count.
    /// </summary>
    private void SyncSchedulerForMmio()
    {
        if (_inMmioSync) return;          // re-entry guard (DMA inside Scheduler.Tick → bus read → here)
        var consumed = _budgetAtStep - Cpu.CyclesLeft;
        if (consumed <= 0) return;
        _inMmioSync = true;
        try
        {
            Scheduler.Tick(consumed);
            // Note: don't deliver IRQ here — block IR is in the middle of an
            // instruction that just called us. Delivering an IRQ would corrupt
            // PC/CPSR mid-instruction. IRQ delivery happens in the outer loop
            // after Cpu.Step() returns. We just need MMIO reads to see updated
            // hardware state (VCOUNT, DISPSTAT, IF flags) — which they will,
            // because the scheduler tick above already updated those bytes.
            _budgetAtStep = Cpu.CyclesLeft;   // rebase so we don't re-tick.
        }
        finally { _inMmioSync = false; }
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

            // Phase 7 A.6.1 — predictive downcounting. Set the CPU's cycle
            // budget to the smaller of (remaining frame budget, distance
            // to next scheduler event). Block-JIT IR will decrement the
            // budget per instruction and exit early if exhausted, giving
            // sub-block IRQ delivery granularity. Per-instr mode ignores
            // this (always charges cyclesPerInstr) so its loop iteration
            // is unchanged.
            int budget = (int)Math.Min(cycleBudget - consumed, Scheduler.CyclesUntilNextEvent());
            if (budget < cyclesPerInstr) budget = cyclesPerInstr;   // never set zero/negative
            Cpu.CyclesLeft = budget;
            _budgetAtStep  = budget;   // phase1b: MMIO catch-up reference point

            Cpu.Step();
            // Phase 7 A.6.1 phase1b — Cpu.LastStepCycles is the authoritative
            // count: per-instr always sets 4; block-JIT sets it from the
            // (budget - CyclesLeft) delta in StepBlock. MMIO catch-up may
            // have already ticked SOME of those cycles into the scheduler,
            // tracked via _budgetAtStep being rebased toward CyclesLeft.
            // Tick only the un-synced remainder.
            var totalConsumed = Cpu.LastStepCycles;
            var alreadyTicked = budget - _budgetAtStep;        // sum of MMIO catch-up ticks
            var remainingTicks = totalConsumed - alreadyTicked;
            if (remainingTicks > 0) Scheduler.Tick(remainingTicks);
            consumed += totalConsumed;
            DeliverIrqIfPending();
        }
        return consumed;
    }

    /// <summary>
    /// Public wrapper around <see cref="BusHasAnyPendingIrq"/> for callers
    /// outside this class that need the same wake-up condition (e.g. an
    /// external trace loop that mimics RunCycles).
    /// </summary>
    public bool HasAnyPendingIrqPublic() => BusHasAnyPendingIrq();

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
    /// Phase 7 B.h: AggressiveInlining hint — DeliverIrqIfPending is
    /// called every instruction in RunCycles' hot loop; the fast path
    /// (no IRQ pending) is just one method call + branch. Inlining lets
    /// JIT fold the call into the loop.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void DeliverIrqIfPending()
    {
        if (!Bus.HasPendingInterrupt()) return;

        var cpsr = Cpu.ReadStatus("CPSR");
        if ((cpsr & CpsrIBit) != 0) return;       // IRQs masked

        var oldMode = cpsr & CpsrModeMask;
        var pc      = Cpu.Pc;

        // 1. Save SPSR_irq = old CPSR (write to banked SPSR slot directly).
        Cpu.WriteStatus("SPSR", cpsr, "IRQ");

        // 2. Switch to IRQ mode + set I bit + force ARM state (T=0).
        //    Per ARM ARM B1.8.5: all exception entries on ARMv4T clear
        //    CPSR.T because the vector table at 0x00..0x1C is ARM code.
        //    SwapBank uses the OLD mode field on entry, so we change
        //    CPSR first then call swap.
        var newCpsr = (cpsr & ~(CpsrModeMask | CpsrTBit)) | IrqModeEnc | CpsrIBit;
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
