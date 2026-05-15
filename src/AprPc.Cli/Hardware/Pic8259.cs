// Pic8259 — Programmable Interrupt Controller (Intel 8259A) HLE.
//
// Real chip has ports 0x20 / 0x21 with ICW1/ICW2/ICW3/ICW4 init word
// sequences and OCW1/OCW2/OCW3 runtime control. Phase 28.7 ships a
// minimum-viable model that's sufficient for the FreeDOS boot path:
//
//   - Single PIC at vector base 0x08 (master). IRQ 0..7 → INT 8..F.
//   - IMR (interrupt mask register) starts with all 8 IRQs enabled.
//   - Edge-triggered semantics: AssertIrq(n) sets a pending bit if not
//     masked. DequeueNextVector returns the lowest unmasked pending
//     IRQ (or null). The emulator thread calls DequeueNextVector
//     between CPU steps; when delivery succeeds it pops the pending
//     bit (so a fresh AssertIrq is required for the next interrupt).
//   - **No real port emulation yet** — host code calls AssertIrq /
//     SetMask directly. Programs that OUT to port 0x20 / 0x21
//     (typical EOI) hit our IN/OUT stub which is a no-op (per the
//     i8086 spec's "IN/OUT no-op stubs" decision in Phase 24.6.7d).
//     Future port-IO wiring (Phase 28.9+ if FreeDOS needs it) can
//     route those writes here through a small hook.
//
// Thread safety: PcPit timer thread + emulator thread + UI input
// thread all call AssertIrq from different threads; a single object
// lock keeps state consistent.

namespace AprPc.Cli.Hardware;

public sealed class Pic8259
{
    /// <summary>Master PIC vector base: IRQ n → INT (VectorBase + n).</summary>
    public const byte VectorBase = 0x08;

    private readonly object _lock = new();
    private byte _imr     = 0x00;       // 0 = enabled, 1 = masked
    private byte _pending = 0x00;       // pending IRR-equivalent

    /// <summary>True iff IRQ <paramref name="irq"/> is masked (won't deliver).</summary>
    public bool IsMasked(byte irq)
    {
        lock (_lock) return (_imr & (1 << (irq & 7))) != 0;
    }

    /// <summary>Mask or unmask a single IRQ line. (Other lines preserved.)</summary>
    public void SetMask(byte irq, bool masked)
    {
        lock (_lock)
        {
            int bit = 1 << (irq & 7);
            if (masked) _imr |= (byte)bit;
            else        _imr &= (byte)~bit;
        }
    }

    /// <summary>Read the current 8-bit IMR (debug / port emulation later).</summary>
    public byte GetImr() { lock (_lock) return _imr; }

    /// <summary>
    /// Edge-triggered "this IRQ line just rose". If not masked, marks
    /// pending; later picked up by DequeueNextVector.
    /// </summary>
    public void AssertIrq(byte irq)
    {
        lock (_lock)
        {
            int bit = 1 << (irq & 7);
            if ((_imr & bit) != 0) return;   // masked → ignored
            _pending |= (byte)bit;
        }
    }

    /// <summary>
    /// If any unmasked IRQ is pending, return its vector
    /// (<c>VectorBase + irq</c>) and clear the pending bit.
    /// Otherwise return null. Lowest IRQ number wins (priority 0..7).
    /// </summary>
    public byte? DequeueNextVector()
    {
        lock (_lock)
        {
            if (_pending == 0) return null;
            for (int i = 0; i < 8; i++)
            {
                int bit = 1 << i;
                if ((_pending & bit) == 0) continue;
                if ((_imr & bit) != 0)   continue;
                _pending &= (byte)~bit;
                return (byte)(VectorBase + i);
            }
            return null;
        }
    }

    /// <summary>Reset to power-on state: nothing masked, nothing pending.</summary>
    public void Reset()
    {
        lock (_lock) { _imr = 0; _pending = 0; }
    }
}
