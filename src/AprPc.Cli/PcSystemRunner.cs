// PcSystemRunner — owns the CPU + memory + (later) IO controllers,
// and runs them on a dedicated emulator thread so the UI thread stays
// responsive.
//
// Phase 28.0: skeleton. The actual CPU dispatch loop + IO ticking
// arrives in 28.1+. This file pins down the threading + lifecycle API:
//
//     var runner = new PcSystemRunner(options);
//     runner.Start();      // spins up emulator thread
//     runner.Pause();      // request pause at next safe boundary
//     runner.Resume();     // resume
//     runner.Stop();       // request termination + join
//
// Plus framebuffer access for the UI thread:
//
//     lock (runner.FramebufferLock) {
//         Buffer.BlockCopy(runner.Framebuffer, 0, dst, 0, dst.Length);
//     }
//
// And input event injection from the UI thread:
//
//     runner.PostKeyEvent(scancode, KeyEventKind.Down);
//
// Thread safety: framebuffer is byte[] guarded by FramebufferLock; the
// input event queue is ConcurrentQueue. Pause/Resume/Stop use a single
// volatile state field + ManualResetEventSlim.

using System.Collections.Concurrent;
using AprCpu.Core.JsonSpec;
using AprPc.Cli.Bios;
using AprPc.Cli.Hardware;
using AprPc.Cli.Memory;
using AprX86.Cli.Cpu;

namespace AprPc.Cli;

public enum RunnerState
{
    Idle,
    Running,
    Paused,
    Stopping,
    Stopped,
}

public enum KeyEventKind
{
    Down,
    Up,
}

public readonly record struct KeyEvent(byte Scancode, KeyEventKind Kind);

public sealed class PcSystemRunner : IDisposable
{
    private readonly PcOptions _options;
    private Thread? _thread;
    private volatile RunnerState _state = RunnerState.Idle;
    private readonly ManualResetEventSlim _resumeEvent = new(initialState: false);
    private readonly CancellationTokenSource _cts = new();

    // Phase 28.1 — real PC bus + CPU. Built lazily on Start() so the UI
    // can construct PcSystemRunner before a disk image is available.
    private PcMemoryBus? _bus;
    private X86JsonCpu? _cpu;
    private HleBios? _bios;
    private PcKeyboard? _kbd;
    private PcPit? _pit;
    private Pic8259? _pic;
    private PcPortBus? _ports;
    private Fdc8272? _fdc;       // Phase 30 — only constructed in real-BIOS mode
    private Dma8237? _dma;       // Phase 30 — paired with _fdc

    // Per-vector IRQ delivery counter. Snapshotted + reset to KbdTrace
    // every 1s by the emulator thread so we can see "X dispatched 10000
    // times in the last second" pathological patterns.
    private readonly long[] _irqCounts = new long[256];
    private DateTime _lastIrqSnapshotTime = DateTime.UtcNow;
    public PcMemoryBus? Bus      => _bus;
    public X86JsonCpu?  Cpu      => _cpu;
    public HleBios?     Bios     => _bios;
    public PcKeyboard?  Keyboard => _kbd;
    public PcPit?       Pit      => _pit;
    public Pic8259?     Pic      => _pic;
    public PcPortBus?   Ports    => _ports;

    // Phase 28.2 will fill this in from the CGA framebuffer slice
    // (4 KB at 0xB8000-0xB8FFF). For 28.0 the runner just zero-fills it
    // periodically so the UI has something deterministic to draw.
    public readonly object FramebufferLock = new();
    public byte[] Framebuffer { get; } = new byte[80 * 25 * 2]; // 4000 bytes char/attr pairs

    // Input event queue — UI thread enqueues, emulator thread dequeues.
    private readonly ConcurrentQueue<KeyEvent> _inputQueue = new();

    // Status snapshot — UI thread reads these; emulator thread writes them.
    // long can't be `volatile` in C#; use Interlocked for atomicity.
    private long _instructionsExecuted;
    public long InstructionsExecuted => Interlocked.Read(ref _instructionsExecuted);
    public RunnerState State => _state;

    public PcSystemRunner(PcOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Build the PC bus + CPU and spin up the emulator thread.
    /// The thread starts in <see cref="RunnerState.Paused"/> so callers
    /// can wire up test ROMs / disk images before stepping begins;
    /// call <see cref="Resume"/> to actually start executing.
    /// </summary>
    public void Start()
    {
        if (_state is not RunnerState.Idle)
            throw new InvalidOperationException($"cannot Start() from state {_state}");

        // Phase 28.1 — construct the real PC bus + CPU.
        // BiosMode / BiosPath plumb through from PcOptions (--bios-mode,
        // --bios) so the BIOS reset-vector stub is configurable.
        var machineSpecPath = PcMemoryBus.LocateMachineSpec();
        var spec = MachineSpecLoader.LoadFromFile(machineSpecPath);
        _bus = new PcMemoryBus(
            spec,
            biosMode: _options.BiosMode,
            biosImagePath: _options.BiosPath,
            videoBiosPath: _options.VideoBiosPath);
        _bus.Reset();

        // Phase 29.1 — resolve machine-declared coprocessor / ISA extension
        // paths (e.g. spec/coprocessors/x87/i8087/cpu.json) against the
        // machine spec file's directory. Empty list / null means "no
        // extensions" — same path as pre-29.1.
        List<string>? resolvedExtensions = null;
        if (spec.Extensions is { Count: > 0 } exts)
        {
            var machineDir = Path.GetDirectoryName(Path.GetFullPath(machineSpecPath))!;
            resolvedExtensions = new List<string>(exts.Count);
            foreach (var rel in exts)
                resolvedExtensions.Add(Path.GetFullPath(Path.Combine(machineDir, rel)));
        }

        _cpu = new X86JsonCpu(_bus.Memory,
            enableBlockJit: _options.Backend == "json-block",
            variant: _options.Cpu,
            extensionPaths: resolvedExtensions);
        _cpu.Reset();
        _cpu.SetEntryPoint(0xFFFF, 0x0000);   // 8086 reset vector

        // Phase 28.7 — PIC 8259A (must come before PIT + keyboard so
        // they can assert IRQ 0 / 1 from their respective threads).
        _pic = new Pic8259();
        _pic.Reset();

        // Phase 28.3 — keyboard state (used by HLE INT 16h).
        _kbd = new PcKeyboard(_bus, _pic);
        _kbd.Reset();

        // Phase 28.4 — PIT 8253 (used by HLE INT 1Ah).
        _pit = new PcPit(_bus, _pic);
        // Phase 30.x — let CLI override the tick rate. Default 18Hz
        // (= 55ms per tick, IBM PC stock). For interactive real-BIOS
        // demos a higher rate (100-200Hz) unblocks BIOS HLT loops
        // much faster -- BDA time-of-day drifts in exchange but
        // commands like dir/ver respond in human-scale time instead
        // of multiple minutes. See PcPit.TickIntervalMsOverride.
        if (_options.PitRateHz > 0 && _options.PitRateHz != 18)
        {
            _pit.TickIntervalMsOverride = Math.Max(1, 1000 / _options.PitRateHz);
        }
        _pit.Reset();

        // Phase 30 — when running with a real BIOS image, construct FDC
        // + DMA controllers so the BIOS POST's INT 19h can actually do
        // disk I/O. HLE mode skips these (HLE INT 13h talks to
        // DiskImage directly without the FDC/DMA layer).
        if (_options.BiosPath is not null)
        {
            _dma = new Dma8237();
            _dma.Reset();
            _fdc = new Fdc8272(_dma, _pic, _bus.Memory, trace: _options.TraceIo);
            _fdc.Reset();
        }

        // Phase 30 debug — wire up the memory-write watch range.
        if (_options.WatchMemHi > _options.WatchMemLo)
        {
            X86JsonCpu.WriteWatchLo = _options.WatchMemLo;
            X86JsonCpu.WriteWatchHi = _options.WatchMemHi;
        }
        else if (_options.BiosPath is not null)
        {
            // Real-BIOS keyboard debug: auto-watch BDA keyboard region
            // (0x0041A head/tail + 0x0041E-0x0043D ring buffer) so we
            // can see whether BIOS INT 9 ISR is writing scancodes.
            // MDA framebuffer watch (0xB0000-0xB0F9F) was useful for
            // Phase 30.10 debug but extremely noisy in normal runs --
            // re-enable via --watch-mem=B0000:B1000 only when needed.
            X86JsonCpu.WriteWatchLo = 0x00410;      // catch BDA[0x10-0x11] equipment word + keyboard
            X86JsonCpu.WriteWatchHi = 0x00440;
            X86JsonCpu.OnWriteWatch = (a, v) =>
                AprPc.Cli.Diagnostics.KbdTrace.Log(
                    $"BDA_WRITE phys=0x{a:X5} <- 0x{v:X2}");
        }

        // Pipe unknown-opcode warnings into kbd-trace too so all the
        // diagnostic noise from one session lands in one place.
        X86JsonCpu.OnUnknownOpcode = (cs, ip, op) =>
            AprPc.Cli.Diagnostics.KbdTrace.Log(
                $"UNKNOWN_OPCODE 0x{op:X2} at {cs:X4}:{ip:X4} (CPU will infinite-loop until implemented)");
        if (_options.ReadWatchHi > _options.ReadWatchLo)
        {
            X86JsonCpu.ReadWatchLo = _options.ReadWatchLo;
            X86JsonCpu.ReadWatchHi = _options.ReadWatchHi;
        }

        // Phase 28.IO — port dispatch bus. Wires PIC / PIT / 8042 /
        // CMOS / speaker / NMI ports to host handlers. Hook the
        // X86JsonCpu delegate handlers (declared in AprX86.Cli, the
        // CPU project; we install them here from AprPc.Cli to keep
        // the cross-project reference one-directional).
        int floppyCount = (_options.FloppyAPath is not null ? 1 : 0) +
                          (_options.FloppyBPath is not null ? 1 : 0);
        if (floppyCount == 0) floppyCount = 1;   // BIOS expects at least 1
        _ports = new PcPortBus(_pic, _pit, traceIo: _options.TraceIo,
            fdc: _fdc, dma: _dma, video: _options.Video, floppyCount: floppyCount);
        PcPortBus.Active = _ports;
        X86JsonCpu.PortRead8Handler   = _ports.Read8;
        X86JsonCpu.PortRead16Handler  = _ports.Read16;
        X86JsonCpu.PortWrite8Handler  = _ports.Write8;
        X86JsonCpu.PortWrite16Handler = _ports.Write16;

        // Phase 28.2 — install HLE BIOS INT handlers + IVT entries.
        //
        // Phase 30.7 — only do this when running in pure HLE mode (no
        // real BIOS image loaded). When --bios=PATH is in play, real
        // BIOS POST owns the IVT — it installs its own INT 9 keyboard
        // ISR, INT 10h video, INT 13h disk handlers, etc. If we pre-
        // populate IVT with HLE trap pointers (F000:00xx), real BIOS
        // POST only overrides vectors it explicitly cares about during
        // POST. INT 9 specifically: real BIOS expects IVT[9] to be zero
        // at boot, sees our non-zero pointer, may skip its own install,
        // and then IRQ 1 lands in HleBios.Dispatch(9) which is a
        // no-op IRET — the scancode is read off port 0x60 but never
        // makes it into the BDA keyboard buffer, so INT 16h reads
        // forever-empty.
        _bios = new HleBios(_cpu, _bus, _kbd, _pit, traceInt: _options.TraceInt);
        if (_options.BiosPath is null)
        {
            _bios.Install();
        }
        else if (_options.TraceInt)
        {
            Console.Error.WriteLine("  [HLE] real BIOS image loaded — skipping HLE IVT install");
        }

        // Start in Paused so LoadTestRom() / Open Floppy can land
        // before the CPU starts stepping. Avoids a race where the
        // emulator thread executes the BIOS-ROM HLT at FFFF:0000
        // before the host had a chance to redirect entry point.
        _state = RunnerState.Paused;
        _resumeEvent.Reset();
        _thread = new Thread(EmulatorThreadProc)
        {
            Name = "AprPc.Emulator",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>
    /// Phase 28.1 — preload a test ROM at a given (segment, offset)
    /// and set the CPU entry point there, bypassing the BIOS reset
    /// vector. Used for unit-test-style fixtures before Phase 28.6
    /// adds the real INT 19h bootstrap.
    /// </summary>
    public void LoadTestRom(byte[] bytes, ushort segment, ushort offset)
    {
        if (_bus is null || _cpu is null)
            throw new InvalidOperationException("LoadTestRom must be called after Start()");
        _bus.LoadBinary(bytes, segment, offset);
        _cpu.SetEntryPoint(segment, offset);
    }

    /// <summary>
    /// Phase 28.5 — mount a disk image at a BIOS drive number
    /// (0x00 = A, 0x01 = B, 0x80 = C). The image becomes readable
    /// via INT 13h.
    /// </summary>
    public void MountDisk(byte drive, DiskImage img)
    {
        if (_bios is null)
            throw new InvalidOperationException("MountDisk must be called after Start()");
        _bios.AttachDisk(drive, img);
        // Phase 30 — also wire to the FDC if running in real-BIOS mode.
        // Real BIOS POST reads disk via FDC ports, not HLE INT 13h.
        _fdc?.AttachDrive(drive, img);
    }

    /// <summary>Request pause; emulator thread parks at next safe boundary.</summary>
    public void Pause()
    {
        if (_state is RunnerState.Running)
        {
            _state = RunnerState.Paused;
            _resumeEvent.Reset();
        }
    }

    /// <summary>Resume after Pause(). No-op if not currently paused.</summary>
    public void Resume()
    {
        if (_state is RunnerState.Paused)
        {
            _state = RunnerState.Running;
            _resumeEvent.Set();
        }
    }

    /// <summary>Request termination; join emulator thread.</summary>
    public void Stop()
    {
        if (_state is RunnerState.Stopped or RunnerState.Idle) return;
        _state = RunnerState.Stopping;
        _resumeEvent.Set();           // unstick any pause-wait
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _state = RunnerState.Stopped;
    }

    /// <summary>Inject a keystroke from the UI thread.</summary>
    public void PostKeyEvent(byte scancode, KeyEventKind kind)
        => _inputQueue.Enqueue(new KeyEvent(scancode, kind));

    private void EmulatorThreadProc()
    {
        // Phase 28.1 — real CPU dispatch. Step the 8086 at the configured
        // backend speed, drain input events (still ignored — Phase 28.3
        // adds the 8042 buffer), update the instruction counter for the
        // status bar. Halts when CPU hits HLT (Phase 28.7+ will instead
        // hook in IRQ wake-up via the PIC).
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested && _state is not RunnerState.Stopping)
            {
                _resumeEvent.Wait(token);
                if (token.IsCancellationRequested) break;

                while (_inputQueue.TryDequeue(out _)) { /* TODO 28.3 */ }

                // Phase 30.7a debug — snapshot per-vector IRQ delivery
                // counts to KbdTrace once per second. A pathological
                // pattern (e.g. IRQ 6 firing 10000/sec) makes the cause
                // of "CPU alive but no progress" debuggable post-hoc.
                var nowIrq = DateTime.UtcNow;
                if ((nowIrq - _lastIrqSnapshotTime).TotalSeconds >= 1.0)
                {
                    var nonZero = new System.Text.StringBuilder();
                    for (int v = 0; v < 256; v++)
                    {
                        if (_irqCounts[v] == 0) continue;
                        if (nonZero.Length > 0) nonZero.Append(' ');
                        nonZero.Append($"INT{v:X2}={_irqCounts[v]}");
                        _irqCounts[v] = 0;
                    }
                    if (nonZero.Length > 0)
                        AprPc.Cli.Diagnostics.KbdTrace.Log($"IRQ_RATE_1s {nonZero}");
                    _lastIrqSnapshotTime = nowIrq;

                    // Same cadence -- per-port read counts. Only log ports
                    // hit > 50 times per second (filters out PIT BDA poll,
                    // PIC IMR check, etc.). A port read 5000+ times = a
                    // tight polling loop on that port.
                    if (_ports is { } ports)
                    {
                        var hotPorts = new System.Text.StringBuilder();
                        for (int p = 0; p < ports.PortReadCounts.Length; p++)
                        {
                            long c = ports.PortReadCounts[p];
                            if (c >= 50)
                            {
                                if (hotPorts.Length > 0) hotPorts.Append(' ');
                                hotPorts.Append($"0x{p:X3}={c}");
                            }
                            ports.PortReadCounts[p] = 0;
                        }
                        if (hotPorts.Length > 0)
                            AprPc.Cli.Diagnostics.KbdTrace.Log($"PORT_READ_RATE_1s {hotPorts}");
                    }
                }

                if (_cpu is { Halted: true })
                {
                    // Phase 28.7c — HLT wake-on-IRQ. Real silicon resumes
                    // the CPU from HLT when an unmasked IRQ arrives;
                    // without this check our dispatcher just sleeps
                    // forever and IRQ 1 (keyboard) stays pending while the
                    // FreeDOS installer's `STI; HLT` INT 16h wait loop
                    // hangs. If IF=1 + IRQ pending, clear HALTED, deliver
                    // the vector, fall through to the regular loop.
                    var stH = _cpu.State;
                    if (_pic is not null && stH.FlagI
                        && _pic.DequeueNextVector() is byte vecH)
                    {
                        _cpu.ClearHalted();
                        DeliverInterrupt(vecH, stH);
                        _cpu.LoadState(stH);
                        Interlocked.Increment(ref _instructionsExecuted);
                        continue;
                    }
                    // Shorter parking time so a fresh IRQ wakes us within
                    // a couple ms instead of up to 50 ms.
                    Thread.Sleep(2);
                    continue;
                }

                if (_cpu is not null && _bios is not null)
                {
                    var st = _cpu.State;

                    // Phase 28.7 — hardware interrupt delivery.
                    // If IF=1 and PIC has an unmasked pending IRQ,
                    // deliver the corresponding INT n vector before
                    // the next instruction. This is the same machinery
                    // the CPU's INT opcode does — but triggered by host
                    // hardware (PIT / keyboard / etc.) instead of
                    // software.
                    if (_pic is not null && st.FlagI && _pic.DequeueNextVector() is byte vec)
                    {
                        DeliverInterrupt(vec, st);
                        _cpu.LoadState(st);
                        Interlocked.Increment(ref _instructionsExecuted);
                        continue;
                    }

                    if (_bios.IsTrapped(st.CS, st.IP))
                    {
                        // Phase 28.3 — INT 16h AH=00 blocks on an empty
                        // keyboard buffer. Park the CPU instead of
                        // returning AL=0, mirroring real-mode INT 16h
                        // behavior.
                        if (_bios.IsBlockedOnKeyboard(st.CS, st.IP))
                        {
                            Thread.Sleep(5);
                            continue;
                        }
                        _bios.Dispatch((byte)st.IP);
                        Interlocked.Increment(ref _instructionsExecuted);
                        continue;
                    }
                    // Phase 28.8b / 30 debug — optional CPU step trace.
                    if (_options.TraceCpu &&
                        (_options.TraceCpuMax is null || _instructionsExecuted < _options.TraceCpuMax.Value))
                    {
                        var stForTrace = _cpu.State;
                        bool csOk = _options.TraceCpuCs is null
                            || _options.TraceCpuCs.Length == 0
                            || Array.IndexOf(_options.TraceCpuCs, stForTrace.CS) >= 0;
                        if (csOk)
                        {
                            // Print 4 bytes at IP for visual disasm hint.
                            int lin = ((stForTrace.CS << 4) + stForTrace.IP) & 0xFFFFF;
                            byte b0 = _bus!.ReadByte(lin);
                            byte b1 = _bus.ReadByte((lin + 1) & 0xFFFFF);
                            byte b2 = _bus.ReadByte((lin + 2) & 0xFFFFF);
                            byte b3 = _bus.ReadByte((lin + 3) & 0xFFFFF);
                            Console.Error.WriteLine(
                                $"  [CPU] {_instructionsExecuted,7} " +
                                $"{stForTrace.CS:X4}:{stForTrace.IP:X4} " +
                                $"{b0:X2} {b1:X2} {b2:X2} {b3:X2}  " +
                                $"AX={stForTrace.A.X:X4} BX={stForTrace.B.X:X4} " +
                                $"CX={stForTrace.C.X:X4} DX={stForTrace.D.X:X4} " +
                                $"SI={stForTrace.SI:X4} DI={stForTrace.DI:X4} " +
                                $"DS={stForTrace.DS:X4} ES={stForTrace.ES:X4} SS={stForTrace.SS:X4} SP={stForTrace.SP:X4} " +
                                $"FL={stForTrace.GetFlags():X4}");
                        }
                    }

                    // Phase 30.10 — runtime HLE intercept of INT 10h
                    // AH=06 (scroll up) with BH=0 in text mode.
                    // pcxtbios scroll handler is correct (uses caller's
                    // BH), but FreeDOS COMMAND.COM passes BH=0 when it
                    // CLS-scrolls the lower screen region during dir
                    // output. Result: scrolled-in rows get attr=0
                    // (black on black). Fix: peek next opcode -- if
                    // CD 10 (INT 10h) is next AND AH=06 AND BH=0 AND
                    // text mode (BDA[0x49] != 4-6), force BH=0x07
                    // (normal mono / light gray on black) so the new
                    // line is visible. Real hardware would render
                    // invisible -- this is an intentional accuracy
                    // departure for FreeDOS interactive usability.
                    // The BIOS-internal scroll (int_10_func_14) was
                    // already fixed via the binary patch in PcMemoryBus.
                    if (_options.BiosPath is not null)
                    {
                        var stI = _cpu.State;
                        int linI = ((stI.CS << 4) + stI.IP) & 0xFFFFF;
                        if (_bus!.Memory.Ram[linI] == 0xCD &&
                            _bus.Memory.Ram[(linI + 1) & 0xFFFFF] == 0x10)
                        {
                            // Phase 30.10 debug — trace every INT 10h
                            // call so we can see what AH/AL/BX caller
                            // is passing. KEY: are we picking up
                            // AH=06 BH=0 from DOS / COMMAND.COM, or
                            // some other AH leading to attr=0?
                            byte mode = _bus.Memory.Ram[0x0449];
                            AprPc.Cli.Diagnostics.KbdTrace.Log(
                                $"INT_10h caller={stI.CS:X4}:{stI.IP:X4} " +
                                $"AX=0x{stI.A.X:X4} BX=0x{stI.B.X:X4} " +
                                $"CX=0x{stI.C.X:X4} DX=0x{stI.D.X:X4} " +
                                $"BDA[0x49]={mode}");
                            // Force BH=0x07 for AH=06 scroll with BH=0
                            // in text mode (workaround per 30.10).
                            if (stI.A.H == 0x06 && stI.B.H == 0x00 &&
                                mode is 0 or 1 or 2 or 3 or 7)
                            {
                                stI.B.H = 0x07;
                                _cpu.LoadState(stI);
                                AprPc.Cli.Diagnostics.KbdTrace.Log(
                                    "INT_10h FORCED BH=0x00 -> 0x07");
                            }
                        }
                    }
                    _cpu.Step();
                    Interlocked.Increment(ref _instructionsExecuted);
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected — Stop() cancels the token to unblock Wait()
        }
    }

    /// <summary>
    /// Phase 28.7 — push FLAGS / CS / IP onto the stack and jump to
    /// IVT[vec], mirroring real-mode INT instruction semantics.
    /// Caller must <see cref="X86JsonCpu.LoadState"/> the state back.
    /// </summary>
    private void DeliverInterrupt(byte vec, AprX86.Cli.Cpu.X86State st)
    {
        if (_bus is null) return;

        // Push FLAGS, CS, IP (in that order; SP -= 2 each).
        ushort flagsVal = st.GetFlags();
        st.SP = (ushort)(st.SP - 2);
        _bus.WriteWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, st.SP), flagsVal);
        st.SP = (ushort)(st.SP - 2);
        _bus.WriteWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, st.SP), st.CS);
        st.SP = (ushort)(st.SP - 2);
        _bus.WriteWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, st.SP), st.IP);

        // Clear IF and TF for the duration of the handler.
        st.FlagI = false;
        st.FlagT = false;

        // Read IVT[vec] (4 bytes at phys vec*4): IP_low IP_high CS_low CS_high.
        int slot = vec * 4;
        ushort newIp = _bus.ReadWord16(slot);
        ushort newCs = _bus.ReadWord16(slot + 2);
        st.CS = newCs;
        st.IP = newIp;

        // Trace for IRQ 1 / IRQ 0 only (keep volume sane). Records:
        //   - where CPU was when IRQ hit (caller CS:IP saved on stack)
        //   - target IVT entry (where real BIOS ISR lives, e.g. F000:E987)
        //   - BDA keyboard tail BEFORE the ISR runs (so a subsequent
        //     log line after IRET shows whether ISR wrote anything).
        if (vec == 0x09)
        {
            ushort tailBefore = _bus.ReadWord16(0x0041C);
            ushort headBefore = _bus.ReadWord16(0x0041A);
            AprPc.Cli.Diagnostics.KbdTrace.Log(
                $"DeliverInterrupt vec=0x09 IVT[9]={newCs:X4}:{newIp:X4} " +
                $"(caller at {_bus.ReadWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, (ushort)(st.SP + 2))):X4}:" +
                $"{_bus.ReadWord16(AprX86.Cli.Memory.X86Memory.LinearAddr(st.SS, st.SP)):X4}) " +
                $"BDA head=0x{headBefore:X4} tail=0x{tailBefore:X4}");
        }
        // Count every IRQ vector dispatched -- exposes any "this IRQ
        // fires thousands of times per second" pathological pattern.
        // Snapshot dumped to KbdTrace every 1s by the IRQ rate watcher.
        _irqCounts[vec & 0xFF]++;
    }

    public void Dispose()
    {
        Stop();
        _pit?.Dispose();
        _resumeEvent.Dispose();
        _cts.Dispose();
    }
}
