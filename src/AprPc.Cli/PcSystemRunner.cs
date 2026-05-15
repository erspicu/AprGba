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

    /// <summary>Begin emulation on a background thread.</summary>
    public void Start()
    {
        if (_state is not RunnerState.Idle)
            throw new InvalidOperationException($"cannot Start() from state {_state}");

        _state = RunnerState.Running;
        _resumeEvent.Set();
        _thread = new Thread(EmulatorThreadProc)
        {
            Name = "AprPc.Emulator",
            IsBackground = true,
        };
        _thread.Start();
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
        // Phase 28.0 placeholder loop: just count "instructions" so the
        // status bar has something to display, and drain input events
        // so the queue doesn't grow unbounded if the UI feeds it before
        // the real 8042 emulator exists. Sleeps 10 ms per tick to keep
        // CPU usage near zero in scaffolding mode.
        var token = _cts.Token;
        while (!token.IsCancellationRequested && _state is not RunnerState.Stopping)
        {
            _resumeEvent.Wait(token);
            if (token.IsCancellationRequested) break;

            // Drain any pending input events (discarded in 28.0).
            while (_inputQueue.TryDequeue(out _)) { }

            Interlocked.Increment(ref _instructionsExecuted);
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        Stop();
        _resumeEvent.Dispose();
        _cts.Dispose();
    }
}
