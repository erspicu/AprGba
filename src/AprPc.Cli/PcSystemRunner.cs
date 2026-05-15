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
    public PcMemoryBus? Bus => _bus;
    public X86JsonCpu?  Cpu => _cpu;
    public HleBios?     Bios => _bios;

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
        var spec = MachineSpecLoader.LoadFromFile(PcMemoryBus.LocateMachineSpec());
        _bus = new PcMemoryBus(spec);
        _bus.Reset();
        _cpu = new X86JsonCpu(_bus.Memory,
            enableBlockJit: _options.Backend == "json-block",
            variant: _options.Cpu);
        _cpu.Reset();
        _cpu.SetEntryPoint(0xFFFF, 0x0000);   // 8086 reset vector

        // Phase 28.2 — install HLE BIOS INT handlers + IVT entries.
        _bios = new HleBios(_cpu, _bus, traceInt: _options.TraceInt);
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
                    // Phase 28.2 — HLE trap check. If CS:IP landed at
                    // an installed BIOS vector (F000:00xx), dispatch
                    // the C# handler + simulate IRET instead of
                    // executing the F000:00xx body (there isn't one).
                    var st = _cpu.State;
                    if (_bios.IsTrapped(st.CS, st.IP))
                    {
                        _bios.Dispatch((byte)st.IP);
                        Interlocked.Increment(ref _instructionsExecuted);
                        continue;
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

    public void Dispose()
    {
        Stop();
        _resumeEvent.Dispose();
        _cts.Dispose();
    }
}
