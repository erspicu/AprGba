using AprGb.Cli.Memory;

namespace AprGb.Cli.Cpu;

/// <summary>
/// Direct port of <c>AprGBemu/Emu_GB/CPU.cs</c> — the proven big-switch
/// LR35902 implementation. The opcode dispatch comes from the original
/// AprGBemu source with bulk renames (r_A → _a, MEM_r8 → _bus.ReadByte,
/// flagX → _flagX). Bug fixes vs the original are commented inline.
///
/// Scope: DMG only (no GBC modes / banked WRAM / HDMA / CGB palettes /
/// double speed). SOUND and JOYPAD intentionally omitted. Boot ROM
/// skipped — we initialise registers to post-BIOS state and start at
/// 0x0100.
/// </summary>
public sealed partial class LegacyCpu : ICpuBackend
{
    public string Name => "legacy";

    private GbMemoryBus _bus = null!;

    // CPU registers (LR35902 = SM83 = "GB Z80")
    private byte _a, _b, _c, _d, _e, _h, _l;
    private ushort _sp, _pc;

    // Flags carried as separate ints (matching AprGBemu's source); F-byte
    // is composed on PUSH AF / POP AF.
    private int _flagZ, _flagN, _flagH, _flagC;

    // Interrupt + halt — kept under their AprGBemu names so the
    // ported Step() body compiles unchanged. Public IsHalted reflects
    // flagHalt for the CLI.
    private bool flagIME;
    private bool flagHalt;
    private int  halt_cycle;
    private bool DMA_CYCLE;

    // EI delays IME enable by one instruction (LR35902 spec). Set to 2
    // by the EI opcode; decremented at end of every instruction; when
    // it reaches 0, flagIME flips to true. RETI (0xD9) bypasses this
    // and enables IME immediately, which is also correct.
    private int _eiDelay;

    // Per-instruction cycle counter (m-cycles); flushed to total at end of Step.
    private int _cycles;
    private long _totalCycles;
    private long _totalInstructions;

    public bool IsHalted => flagHalt;
    public long InstructionsExecuted => _totalInstructions;

    private const int FlagClear = 0;
    private const int FlagSet   = 1;

    public void Reset(GbMemoryBus bus)
    {
        _bus = bus;
        // Post-BIOS DMG state per Pan Docs.
        _a = 0x01; _b = 0x00; _c = 0x13;
        _d = 0x00; _e = 0xD8;
        _h = 0x01; _l = 0x4D;
        _sp = 0xFFFE;
        _pc = 0x0100;
        // F = 0xB0 = Z=1 N=0 H=1 C=1
        _flagZ = FlagSet; _flagN = FlagClear; _flagH = FlagSet; _flagC = FlagSet;
        flagIME = false;
        flagHalt = false;
        halt_cycle = 0;
        DMA_CYCLE = false;
        _eiDelay = 0;
        _totalCycles = 0;
        _totalInstructions = 0;
    }

    public long RunCycles(long targetCycles)
    {
        long start = _totalCycles;
        while (_totalCycles - start < targetCycles)
        {
            if (flagHalt)
            {
                _bus.Tick(4);            // timers tick during HALT too
                _totalCycles += 4;
                CheckInterrupts();
                continue;
            }
            _cycles = 0;
            Step();
            _totalInstructions++;
            _bus.Tick(_cycles);          // advance hardware timers
            _totalCycles += _cycles;     // _cycles in t-cycles after `*= 4` in Step()
            TickEiDelay();
            CheckInterrupts();
        }
        return _totalCycles - start;
    }

    private void TickEiDelay()
    {
        if (_eiDelay <= 0) return;
        _eiDelay--;
        if (_eiDelay == 0) flagIME = true;
    }

    public ushort ReadReg16(GbReg16 reg) => reg switch
    {
        GbReg16.AF => (ushort)((_a << 8) | ComposeF()),
        GbReg16.BC => (ushort)((_b << 8) | _c),
        GbReg16.DE => (ushort)((_d << 8) | _e),
        GbReg16.HL => (ushort)((_h << 8) | _l),
        GbReg16.SP => _sp,
        GbReg16.PC => _pc,
        _          => 0
    };

    public byte ReadReg8(GbReg8 reg) => reg switch
    {
        GbReg8.A => _a, GbReg8.F => ComposeF(),
        GbReg8.B => _b, GbReg8.C => _c,
        GbReg8.D => _d, GbReg8.E => _e,
        GbReg8.H => _h, GbReg8.L => _l,
        _        => 0
    };

    private byte ComposeF()
        => (byte)((_flagZ << 7) | (_flagN << 6) | (_flagH << 5) | (_flagC << 4));

    private void DecomposeF(byte f)
    {
        _flagZ = (f >> 7) & 1;
        _flagN = (f >> 6) & 1;
        _flagH = (f >> 5) & 1;
        _flagC = (f >> 4) & 1;
    }

    /// <summary>Service pending interrupts. Called between instructions.</summary>
    private void CheckInterrupts()
    {
        if (!flagIME && !flagHalt) return;     // halted CPU still wakes on enabled IRQ

        var pending = (byte)(_bus.InterruptEnable & _bus.InterruptFlag & 0x1F);
        if (pending == 0) return;

        // Wake from HALT regardless of IME (HALT exits when IE & IF != 0).
        flagHalt = false;
        if (!flagIME) return;

        // Service the highest-priority pending IRQ. Bits: VBLANK=1, LCDC=2, TIMER=4, SERIAL=8, JOYPAD=16.
        ushort vector;
        byte mask;
        if      ((pending & 0x01) != 0) { vector = 0x40; mask = 0x01; }
        else if ((pending & 0x02) != 0) { vector = 0x48; mask = 0x02; }
        else if ((pending & 0x04) != 0) { vector = 0x50; mask = 0x04; }
        else if ((pending & 0x08) != 0) { vector = 0x58; mask = 0x08; }
        else                            { vector = 0x60; mask = 0x10; }

        flagIME = false;
        _bus.InterruptFlag &= (byte)~mask;
        // Push PC and jump to vector — 5 m-cycles per Pan Docs.
        _sp -= 2;
        _bus.WriteByte(_sp,                (byte)(_pc & 0xFF));
        _bus.WriteByte((ushort)(_sp + 1),  (byte)(_pc >> 8));
        _pc = vector;
        _cycles += 5 * 4;
    }

    private void LcdSync()           { /* stub — Phase 4.5 PPU work */ }
    private void DebugTrace(byte op) { /* stub */ }

    // Cycle tables from AprGBemu/Emu_GB/Define.cs (DMG m-cycles).
    private static readonly byte[] mCycleTable =
    {
        1, 3, 2, 2, 1, 1, 2, 1, 5, 2, 2, 2, 1, 1, 2, 1,
        1, 3, 2, 2, 1, 1, 2, 1, 3, 2, 2, 2, 1, 1, 2, 1,
        3, 3, 2, 2, 1, 1, 2, 1, 3, 2, 2, 2, 1, 1, 2, 1,
        3, 3, 2, 2, 1, 3, 3, 3, 3, 2, 2, 2, 1, 1, 2, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 1,
        2, 2, 2, 2, 2, 2, 1, 2, 1, 1, 1, 1, 1, 1, 2, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 1,
        5, 3, 4, 4, 6, 4, 2, 4, 5, 4, 4, 1, 6, 6, 2, 4,
        5, 3, 4, 0, 6, 4, 2, 4, 5, 4, 4, 0, 6, 0, 2, 4,
        3, 3, 2, 0, 0, 4, 2, 4, 4, 1, 4, 0, 0, 0, 2, 4,
        3, 3, 2, 1, 0, 4, 2, 4, 3, 2, 4, 1, 0, 0, 2, 4,
    };

    private static readonly byte[] cbMCycleTable =
    {
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 3, 2, 2, 2, 2, 2, 2, 2, 3, 2,
        2, 2, 2, 2, 2, 2, 3, 2, 2, 2, 2, 2, 2, 2, 3, 2,
        2, 2, 2, 2, 2, 2, 3, 2, 2, 2, 2, 2, 2, 2, 3, 2,
        2, 2, 2, 2, 2, 2, 3, 2, 2, 2, 2, 2, 2, 2, 3, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
        2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 2, 2, 2, 2, 4, 2,
    };

    // Step() body lives in LegacyCpu.Step.cs (auto-generated from
    // AprGBemu/Emu_GB/CPU.cs via bulk rename — see scripts/port-legacy-cpu.sh).
}
