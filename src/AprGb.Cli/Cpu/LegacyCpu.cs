using AprGb.Cli.Memory;

namespace AprGb.Cli.Cpu;

/// <summary>
/// Direct port of <c>AprGBemu/Emu_GB/CPU.cs</c> — the proven big-switch
/// LR35902 implementation. **Skeleton only** in this iteration; the
/// full ~1900-line opcode dispatch comes in the next pass once the
/// surrounding harness (memory bus, PPU, screenshot) is shaken out.
///
/// Scope: DMG only. No GBC modes, no double speed, no CGB palettes,
/// no HDMA. SOUND and JOYPAD intentionally omitted (per user spec).
/// </summary>
public sealed class LegacyCpu : ICpuBackend
{
    public string Name => "legacy";

    private GbMemoryBus _bus = null!;

    // CPU registers (DMG = Sharp LR35902)
    private byte _a, _f, _b, _c, _d, _e, _h, _l;
    private ushort _sp, _pc;
    private bool _ime;       // interrupt master enable
    private bool _halted;

    public bool IsHalted => _halted;

    public void Reset(GbMemoryBus bus)
    {
        _bus = bus;
        // Post-BIOS DMG state per Pan Docs.
        _a = 0x01; _f = 0xB0;
        _b = 0x00; _c = 0x13;
        _d = 0x00; _e = 0xD8;
        _h = 0x01; _l = 0x4D;
        _sp = 0xFFFE;
        _pc = 0x0100;
        _ime = false;
        _halted = false;
    }

    public long RunCycles(long targetCycles)
    {
        // STUB: pretend we executed targetCycles worth of NOPs while
        // the full opcode dispatch is being ported. Lets the harness +
        // PPU + screenshot pipeline be developed in parallel.
        if (_halted) return 0;
        _pc = (ushort)(_pc + 1);
        return targetCycles;
    }

    public ushort ReadReg16(GbReg16 reg) => reg switch
    {
        GbReg16.AF => (ushort)((_a << 8) | _f),
        GbReg16.BC => (ushort)((_b << 8) | _c),
        GbReg16.DE => (ushort)((_d << 8) | _e),
        GbReg16.HL => (ushort)((_h << 8) | _l),
        GbReg16.SP => _sp,
        GbReg16.PC => _pc,
        _          => 0
    };

    public byte ReadReg8(GbReg8 reg) => reg switch
    {
        GbReg8.A => _a, GbReg8.F => _f,
        GbReg8.B => _b, GbReg8.C => _c,
        GbReg8.D => _d, GbReg8.E => _e,
        GbReg8.H => _h, GbReg8.L => _l,
        _        => 0
    };
}
