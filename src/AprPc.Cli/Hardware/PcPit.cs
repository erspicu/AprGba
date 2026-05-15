// PcPit — Programmable Interval Timer (Intel 8253) HLE.
//
// Real chip has 3 channels at ports 0x40-0x42 plus a control word at
// 0x43. Channel 0 drives IRQ 0 (BIOS keeps it at the default reload of
// 65536, giving ~18.2 Hz tick); channel 1 is DRAM refresh on real PCs
// (we ignore); channel 2 gates the PC speaker via port 0x61 bit 1.
//
// Phase 28.4 scope:
//   - Channel 0 ticks at a real wall-clock 55 ms cadence via
//     System.Threading.Timer; BDA tick DWORD @ 0x046C increments
//     each tick. INT 1Ah AH=00 reads this DWORD.
//   - IRQ 0 delivery NOT wired (deferred to 28.7 PIC); programs that
//     need timer interrupts won't see them yet — but INT 1Ah HLE
//     gives wall-clock ticks for "what time is it" questions.
//   - Port 0x43 / 0x40-0x42 emulation NOT included (no program in our
//     boot path reprograms the PIT; FreeDOS keeps BIOS defaults).
//   - Channel 2 + port 0x61: speaker gate state is tracked but not
//     turned into audio. Phase 28.11 (optional) wires WAV output.

using AprPc.Cli.Memory;

namespace AprPc.Cli.Hardware;

public sealed class PcPit : IDisposable
{
    /// <summary>
    /// Wall-clock interval per tick. Real PC: 1.193182 MHz / 65536 ≈ 18.2065 Hz
    /// = 54.9254 ms per tick. We use the IBM BIOS-documented rounded
    /// 55 ms (matches what most DOS programs assume).
    /// </summary>
    public const int TickIntervalMs = 55;

    public const int BdaTickLow         = 0x0046C;
    public const int BdaTickHigh        = 0x0046E;
    public const int BdaMidnightRolled  = 0x00470;

    private readonly PcMemoryBus _bus;
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private uint _ticks;
    private byte _midnightRolled;
    private bool _speakerGate;

    public PcPit(PcMemoryBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    /// <summary>Snapshot of the current 32-bit tick count.</summary>
    public uint Ticks { get { lock (_lock) return _ticks; } }

    /// <summary>Midnight-rollover flag (cleared after INT 1Ah AH=00 reads it).</summary>
    public byte MidnightRolled
    {
        get { lock (_lock) return _midnightRolled; }
        set { lock (_lock) { _midnightRolled = value; _bus.WriteByte(BdaMidnightRolled, value); } }
    }

    /// <summary>Port 0x61 bit 1 mirror — true iff speaker gate is on.</summary>
    public bool SpeakerGate
    {
        get { lock (_lock) return _speakerGate; }
        set { lock (_lock) _speakerGate = value; }
    }

    /// <summary>
    /// Initialise BDA tick fields to zero and start the wall-clock timer.
    /// Call after PcMemoryBus.Reset() and before runner.Resume().
    /// </summary>
    public void Reset()
    {
        Stop();
        lock (_lock)
        {
            _ticks = 0;
            _midnightRolled = 0;
            _bus.WriteWord16(BdaTickLow,        0);
            _bus.WriteWord16(BdaTickHigh,       0);
            _bus.WriteByte (BdaMidnightRolled,  0);
        }
        // Start the wall-clock timer; first tick after one interval.
        _timer = new System.Threading.Timer(_ => OnTick(), null,
            dueTime: TickIntervalMs,
            period:  TickIntervalMs);
    }

    /// <summary>
    /// Manually advance the tick count. Used by tests / fixtures that
    /// want a deterministic value without waiting for wall clock.
    /// </summary>
    public void AdvanceTicks(uint delta)
    {
        lock (_lock)
        {
            _ticks += delta;
            WriteBdaTickLocked();
        }
    }

    /// <summary>Force the tick counter to a specific value (test helper).</summary>
    public void SetTicks(uint ticks)
    {
        lock (_lock)
        {
            _ticks = ticks;
            WriteBdaTickLocked();
        }
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => Stop();

    private void OnTick()
    {
        lock (_lock)
        {
            _ticks++;
            // Wrap at 24h = 1,573,040 ticks (= 18.2065 Hz × 86400 s)
            // — real BIOS rolls over and sets the rolled flag.
            const uint TicksPerDay = 1_573_040;
            if (_ticks >= TicksPerDay)
            {
                _ticks -= TicksPerDay;
                _midnightRolled = 1;
                _bus.WriteByte(BdaMidnightRolled, 1);
            }
            WriteBdaTickLocked();
        }
    }

    private void WriteBdaTickLocked()
    {
        // BDA tick layout: low word at 0x046C, high word at 0x046E.
        // Real BIOS treats the pair as one little-endian DWORD.
        _bus.WriteWord16(BdaTickLow,  (ushort)(_ticks & 0xFFFF));
        _bus.WriteWord16(BdaTickHigh, (ushort)((_ticks >> 16) & 0xFFFF));
    }
}
