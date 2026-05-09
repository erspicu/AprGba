// X86LegacyCpu — phase 24.1 STUB.
//
// Just enough to: fetch one byte at CS:IP, recognise NOP (0x90) and HLT
// (0xF4), and detect "OUT 0xE9" (E6 E9) for the magic-port stdout
// convention. Everything else throws NotImplementedException so the
// harness can immediately tell what real opcode is hit by test ROMs.
//
// Phase 24.2 will replace this with the full 256-opcode handler — at
// which point this file may stay as the "Apr86-port" reference oracle
// for lockstep diffing, with bug fixes for AAM/AAD/IMUL etc. flagged
// in MD/design/24-8086-port-plan.md §1.

using AprX86.Cli.Memory;

namespace AprX86.Cli.Cpu;

public sealed class X86LegacyCpu : IX86CpuBackend
{
    private readonly X86Memory _mem;
    private readonly X86State  _state = new();
    private bool _halted;

    public string BackendName => "legacy";
    public X86State State => _state;
    public X86Memory Memory => _mem;
    public bool Halted => _halted;

    public X86LegacyCpu(X86Memory memory) { _mem = memory; }

    public void Reset()
    {
        _state.Reset();
        _halted = false;
    }

    public void SetEntryPoint(ushort segment, ushort offset)
    {
        _state.CS = segment;
        _state.IP = offset;
        _halted = false;
    }

    /// <summary>
    /// Phase 24.1 stub — executes the tiny set of opcodes needed to verify
    /// the harness loop + memory + screenshot path can run end-to-end.
    /// Real instruction set lands in phase 24.2.
    /// </summary>
    public int Step()
    {
        if (_halted) return 0;

        int linearIp = X86Memory.LinearAddr(_state.CS, _state.IP);
        byte opcode = _mem.ReadByte(linearIp);

        switch (opcode)
        {
            case 0x90:    // NOP
                _state.IP++;
                return 3;

            case 0xF4:    // HLT
                _halted = true;
                return 2;

            case 0xE6:    // OUT imm8, AL — phase 24.1 magic-port stub
            {
                byte port = _mem.ReadByte(X86Memory.LinearAddr(_state.CS, (ushort)(_state.IP + 1)));
                _state.IP += 2;
                if (port == 0xE9)
                {
                    // Bochs/qemu convention: OUT 0xE9 emits AL as a debug char.
                    Console.Write((char)_state.A.L);
                }
                else if (port == 0xF4)
                {
                    // qemu isa-debug-exit: AL=0 → success exit; AL≠0 → fail.
                    _halted = true;
                }
                return 8;
            }

            case 0xEA:    // JMP far ptr16:16 — useful for entry-jump prologues
            {
                int b = X86Memory.LinearAddr(_state.CS, (ushort)(_state.IP + 1));
                ushort newIp = (ushort)(_mem.ReadByte(b) | (_mem.ReadByte(b + 1) << 8));
                ushort newCs = (ushort)(_mem.ReadByte(b + 2) | (_mem.ReadByte(b + 3) << 8));
                _state.IP = newIp;
                _state.CS = newCs;
                return 15;
            }

            default:
                throw new NotImplementedException(
                    $"X86LegacyCpu phase 24.1 stub: opcode 0x{opcode:X2} at CS:IP={_state.CS:X4}:{_state.IP:X4} not implemented yet (full ISA in phase 24.2).");
        }
    }
}
