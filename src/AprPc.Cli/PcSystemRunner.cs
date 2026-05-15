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
        var spec = MachineSpecLoader.LoadFromFile(PcMemoryBus.LocateMachineSpec());
        _bus = new PcMemoryBus(spec, biosMode: _options.BiosMode, biosImagePath: _options.BiosPath);
        _bus.Reset();
        _cpu = new X86JsonCpu(_bus.Memory,
            enableBlockJit: _options.Backend == "json-block",
            variant: _options.Cpu);
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
        _pit.Reset();

        // Phase 28.IO — port dispatch bus. Wires PIC / PIT / 8042 /
        // CMOS / speaker / NMI ports to host handlers. Hook the
        // X86JsonCpu delegate handlers (declared in AprX86.Cli, the
        // CPU project; we install them here from AprPc.Cli to keep
        // the cross-project reference one-directional).
        _ports = new PcPortBus(_pic, _pit, traceIo: _options.TraceIo);
        PcPortBus.Active = _ports;
        X86JsonCpu.PortRead8Handler   = _ports.Read8;
        X86JsonCpu.PortRead16Handler  = _ports.Read16;
        X86JsonCpu.PortWrite8Handler  = _ports.Write8;
        X86JsonCpu.PortWrite16Handler = _ports.Write16;

        // Phase 28.2 — install HLE BIOS INT handlers + IVT entries.
        _bios = new HleBios(_cpu, _bus, _kbd, _pit, traceInt: _options.TraceInt);
        _bios.Install();

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

                if (_cpu is { Halted: true })
                {
                    // Park until external Reset/Stop arrives.
                    Thread.Sleep(50);
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
                    // Phase 28.8b — optional CPU step trace.
                    if (_options.TraceCpu &&
                        (_options.TraceCpuMax is null || _instructionsExecuted < _options.TraceCpuMax.Value))
                    {
                        var stForTrace = _cpu.State;
                        Console.Error.WriteLine(
                            $"  [CPU] step#{_instructionsExecuted,6} " +
                            $"CS:IP={stForTrace.CS:X4}:{stForTrace.IP:X4} " +
                            $"AX={stForTrace.A.X:X4} BX={stForTrace.B.X:X4} " +
                            $"CX={stForTrace.C.X:X4} DX={stForTrace.D.X:X4} " +
                            $"DS={stForTrace.DS:X4} SS={stForTrace.SS:X4} SP={stForTrace.SP:X4} " +
                            $"FL={stForTrace.GetFlags():X4} " +
                            $"op={_bus!.ReadByte(((stForTrace.CS << 4) + stForTrace.IP) & 0xFFFFF):X2}");
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
    }

    public void Dispose()
    {
        Stop();
        _pit?.Dispose();
        _resumeEvent.Dispose();
        _cts.Dispose();
    }
}
