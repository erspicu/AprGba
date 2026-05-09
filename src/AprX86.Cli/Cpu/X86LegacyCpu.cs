// X86LegacyCpu — 8086 hand-coded interpreter (incremental).
//
// Phase 24.2.2 deliverable: full MOV instruction set + segment override
// prefixes + operand fetch/store helpers built on the ModR/M decoder
// from 24.2.1. Subsequent phases (24.2.3 ALU, 24.2.4 Tom Harte) layer
// on the same helper foundation.
//
// Style:
//   - Fetch advances IP via FetchByte/FetchWord (modular within the
//     16-bit IP counter; CS held constant per fetch).
//   - Memory access respects an optional segment override prefix
//     (0x26/0x2E/0x36/0x3E for ES/CS/SS/DS); falls back to the
//     ModR/M default segment from ComputeEffectiveAddress.
//   - Anything not yet implemented throws NotImplementedException
//     with the offending opcode + CS:IP for fast triage.

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

    // ---------------- Instruction fetch ----------------

    private byte FetchByte()
    {
        byte b = _mem.ReadByte(X86Memory.LinearAddr(_state.CS, _state.IP));
        _state.IP++;
        return b;
    }

    private ushort FetchWord()
    {
        // 8086 fetches little-endian; two byte fetches. FetchByte
        // increments IP within ushort, so 0xFFFF → 0x0000 wrap within
        // the code segment is automatic.
        byte lo = FetchByte();
        byte hi = FetchByte();
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>Fetch the displacement (0/1/2 bytes per ModR/M).</summary>
    private ushort FetchDisplacement(int dispBytes)
    {
        return dispBytes switch
        {
            0 => 0,
            1 => (ushort)(sbyte)FetchByte(),     // sign-extend disp8 to 16-bit
            2 => FetchWord(),
            _ => throw new InvalidOperationException($"unexpected displacement byte count {dispBytes}"),
        };
    }

    // ---------------- Stack helpers (PUSH/POP/CALL/RET) ----------------
    //
    // 8086 stack: SS:SP, grows DOWNWARD. PUSH word: SP -= 2 first, then
    // store to SS:SP (low byte at SS:SP, high byte at SS:SP+1, with the
    // 16-bit offset wrap honoured per the same rule as ReadMem16/WriteMem16).
    // POP word: read from SS:SP, then SP += 2.
    //
    // 8086 quirk: PUSH SP pushes the NEW (decremented) value of SP, not
    // the original. 80286+ flipped this to push the ORIGINAL. We're 8086.

    private void PushWord(ushort value)
    {
        _state.SP = (ushort)(_state.SP - 2);
        WriteMem16(SegReg.SS, _state.SP, value);
    }

    private ushort PopWord()
    {
        ushort v = ReadMem16(SegReg.SS, _state.SP);
        _state.SP = (ushort)(_state.SP + 2);
        return v;
    }

    // ---------------- Memory access with segment override ----------------

    private byte ReadMem8(SegReg seg, ushort offset)
        => _mem.ReadByte(X86Memory.LinearAddr(_state.GetSeg(seg), offset));

    private void WriteMem8(SegReg seg, ushort offset, byte value)
        => _mem.WriteByte(X86Memory.LinearAddr(_state.GetSeg(seg), offset), value);

    /// <summary>
    /// Read a 16-bit word from <c>seg:offset</c>. The 16-bit OFFSET wraps
    /// from 0xFFFF → 0x0000 within the same segment — the segment base is
    /// recomputed for each byte separately so we DON'T accidentally cross
    /// segments via 20-bit linear arithmetic.
    /// </summary>
    private ushort ReadMem16(SegReg seg, ushort offset)
    {
        ushort segBase = _state.GetSeg(seg);
        byte lo = _mem.ReadByte(X86Memory.LinearAddr(segBase, offset));
        byte hi = _mem.ReadByte(X86Memory.LinearAddr(segBase, (ushort)(offset + 1)));
        return (ushort)(lo | (hi << 8));
    }

    private void WriteMem16(SegReg seg, ushort offset, ushort value)
    {
        ushort segBase = _state.GetSeg(seg);
        _mem.WriteByte(X86Memory.LinearAddr(segBase, offset),                 (byte)(value & 0xFF));
        _mem.WriteByte(X86Memory.LinearAddr(segBase, (ushort)(offset + 1)),   (byte)((value >> 8) & 0xFF));
    }

    /// <summary>
    /// Read an 8-bit operand specified by ModR/M (register or memory).
    /// Caller has already fetched displacement bytes.
    /// </summary>
    private byte ReadRm8(ModRmFields f, ushort disp, SegReg? segOverride)
    {
        if (f.IsRegister) return _state.GetReg8(f.RM);
        var ea = ModRm.ComputeEffectiveAddress(f, disp, _state);
        return ReadMem8(segOverride ?? ea.DefaultSegment, ea.Offset);
    }

    private void WriteRm8(ModRmFields f, ushort disp, SegReg? segOverride, byte value)
    {
        if (f.IsRegister) { _state.SetReg8(f.RM, value); return; }
        var ea = ModRm.ComputeEffectiveAddress(f, disp, _state);
        WriteMem8(segOverride ?? ea.DefaultSegment, ea.Offset, value);
    }

    private ushort ReadRm16(ModRmFields f, ushort disp, SegReg? segOverride)
    {
        if (f.IsRegister) return _state.GetReg16(f.RM);
        var ea = ModRm.ComputeEffectiveAddress(f, disp, _state);
        return ReadMem16(segOverride ?? ea.DefaultSegment, ea.Offset);
    }

    private void WriteRm16(ModRmFields f, ushort disp, SegReg? segOverride, ushort value)
    {
        if (f.IsRegister) { _state.SetReg16(f.RM, value); return; }
        var ea = ModRm.ComputeEffectiveAddress(f, disp, _state);
        WriteMem16(segOverride ?? ea.DefaultSegment, ea.Offset, value);
    }

    // ---------------- Step() — single-instruction execution ----------------

    public int Step()
    {
        if (_halted) return 0;

        // Prefix loop — segment override + REP/REPNE for string ops.
        SegReg? segOverride = null;
        byte? repPrefix = null;       // 0xF2 (REPNE) or 0xF3 (REP/REPE)
        byte opcode;
        while (true)
        {
            opcode = FetchByte();
            switch (opcode)
            {
                case 0x26: segOverride = SegReg.ES; continue;
                case 0x2E: segOverride = SegReg.CS; continue;
                case 0x36: segOverride = SegReg.SS; continue;
                case 0x3E: segOverride = SegReg.DS; continue;
                case 0xF2: repPrefix = 0xF2; continue;
                case 0xF3: repPrefix = 0xF3; continue;
                default: goto exec;
            }
        }
    exec:

        return Execute(opcode, segOverride, repPrefix);
    }

    /// <summary>
    /// Dispatch a single (already-fetched, prefix-stripped) opcode.
    /// Phase 24.2.2 implements: NOP / HLT / OUT 0xE9 / JMP far / full
    /// MOV set. Anything else throws with diagnostic info.
    /// </summary>
    private int Execute(byte opcode, SegReg? segOverride, byte? repPrefix = null)
    {
        // ===== String ops (0xA4-0xA7, 0xAA-0xAF) — handle REP prefix =====
        if (opcode is 0xA4 or 0xA5 or 0xA6 or 0xA7
                  or 0xAA or 0xAB or 0xAC or 0xAD or 0xAE or 0xAF)
        {
            return ExecuteStringOp(opcode, segOverride, repPrefix);
        }
        // For non-string opcodes, REP prefix is reserved/ignored on 8086.
        // (Tom Harte SST sometimes captures interesting behaviour for
        // misused REP, but real software doesn't emit it; we ignore.)


        // ===== NOP / HLT / magic-port stubs / JMP far =====
        switch (opcode)
        {
            case 0x90:    // NOP
                return 3;

            case 0xF4:    // HLT
                _halted = true;
                return 2;

            case 0xE6:    // OUT imm8, AL — uses port-IO hook
            {
                byte port = FetchByte();
                WriteIoPort8(port, _state.A.L);
                return 10;
            }

            case 0xEA:    // JMP far ptr16:16
            {
                ushort newIp = FetchWord();
                ushort newCs = FetchWord();
                _state.IP = newIp;
                _state.CS = newCs;
                return 15;
            }
        }

        // ===== MOV r/m8, r8 (0x88)  /  MOV r/m16, r16 (0x89)
        //       MOV r8, r/m8 (0x8A)  /  MOV r16, r/m16 (0x8B) =====
        if (opcode is 0x88 or 0x89 or 0x8A or 0x8B)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);

            bool isWord    = (opcode & 0x01) != 0;     // bit 0 of opcode = w
            bool toReg     = (opcode & 0x02) != 0;     // bit 1 of opcode = d
            // toReg=false: r/m ← reg ;   toReg=true: reg ← r/m

            if (isWord)
            {
                if (toReg)
                {
                    ushort src = ReadRm16(f, disp, segOverride);
                    _state.SetReg16(f.Reg, src);
                }
                else
                {
                    ushort src = _state.GetReg16(f.Reg);
                    WriteRm16(f, disp, segOverride, src);
                }
            }
            else
            {
                if (toReg)
                {
                    byte src = ReadRm8(f, disp, segOverride);
                    _state.SetReg8(f.Reg, src);
                }
                else
                {
                    byte src = _state.GetReg8(f.Reg);
                    WriteRm8(f, disp, segOverride, src);
                }
            }
            return f.IsRegister ? 2 : 9;       // approx 8086 cycles
        }

        // ===== MOV r/m16, sreg (0x8C)  /  MOV sreg, r/m16 (0x8E) =====
        if (opcode is 0x8C or 0x8E)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            // reg field encodes sreg (only low 2 bits used; high bit reserved
            // on 8086 — accept all but only low 2 bits route to GetSeg).
            var seg = (SegReg)(f.Reg & 0b11);

            if (opcode == 0x8C)        // r/m16 ← sreg
            {
                ushort src = _state.GetSeg(seg);
                WriteRm16(f, disp, segOverride, src);
            }
            else                        // sreg ← r/m16
            {
                ushort src = ReadRm16(f, disp, segOverride);
                _state.SetSeg(seg, src);
            }
            return f.IsRegister ? 2 : 12;
        }

        // ===== MOV r8, imm8  (0xB0-0xB7) =====
        if (opcode >= 0xB0 && opcode <= 0xB7)
        {
            byte imm = FetchByte();
            _state.SetReg8(opcode - 0xB0, imm);
            return 4;
        }

        // ===== MOV r16, imm16 (0xB8-0xBF) =====
        if (opcode >= 0xB8 && opcode <= 0xBF)
        {
            ushort imm = FetchWord();
            _state.SetReg16(opcode - 0xB8, imm);
            return 4;
        }

        // ===== MOV AL, [disp16]    (0xA0)
        //       MOV AX, [disp16]    (0xA1)
        //       MOV [disp16], AL    (0xA2)
        //       MOV [disp16], AX    (0xA3) =====
        if (opcode is 0xA0 or 0xA1 or 0xA2 or 0xA3)
        {
            ushort addr = FetchWord();
            SegReg seg = segOverride ?? SegReg.DS;     // direct addressing defaults DS
            switch (opcode)
            {
                case 0xA0: _state.A.L = ReadMem8(seg, addr); break;
                case 0xA1: _state.A.X = ReadMem16(seg, addr); break;
                case 0xA2: WriteMem8(seg, addr, _state.A.L); break;
                case 0xA3: WriteMem16(seg, addr, _state.A.X); break;
            }
            return 10;
        }

        // ===== MOV r/m8, imm8  (0xC6)
        //       MOV r/m16, imm16 (0xC7) =====
        //
        // Officially these are /0 group opcodes (ModR/M reg=000), but
        // on real 8086 silicon the reg field is NOT decoded — any reg
        // value executes as MOV r/m, imm. Tom Harte SST confirms this.
        if (opcode is 0xC6 or 0xC7)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);

            if (opcode == 0xC6)
            {
                byte imm = FetchByte();
                WriteRm8(f, disp, segOverride, imm);
            }
            else
            {
                ushort imm = FetchWord();
                WriteRm16(f, disp, segOverride, imm);
            }
            return f.IsRegister ? 4 : 10;
        }

        // ===== ALU groups (0x00-0x3D, 8 ops × 6 forms) =====
        //
        // Each ALU group occupies 8 consecutive opcodes (low 3 bits of
        // (op >> 3) selects one of {ADD, OR, ADC, SBB, AND, SUB, XOR, CMP}).
        // Within each group the low 3 bits of the opcode select the form:
        //
        //   +0  r/m8, r8     (0x00, 0x08, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38)
        //   +1  r/m16, r16
        //   +2  r8, r/m8
        //   +3  r16, r/m16
        //   +4  AL, imm8     (0x04, 0x0C, 0x14, 0x1C, 0x24, 0x2C, 0x34, 0x3C)
        //   +5  AX, imm16
        //   +6 / +7 reserved (some are SREG push/pop in opcode space; not
        //                    part of the ALU pattern)
        //
        // We detect ALU-pattern opcodes by `op & 0xC6 == 0` (i.e. low 6
        // bits are 0xx0xx, where the high two bits select op class — but
        // this gets fiddly; explicit range check is clearer and faster).
        if ((opcode & 0xC0) == 0x00 && (opcode & 0x06) != 0x06)
        {
            AluOp aluOp = (AluOp)((opcode >> 3) & 0x07);
            int form = opcode & 0x07;
            return ExecuteAluGroup(aluOp, form, segOverride);
        }

        // ===== Group opcodes 0x80/0x81/0x82/0x83 — r/m, imm =====
        // ModR/M reg field selects the AluOp (0..7).
        //   0x80: r/m8, imm8
        //   0x81: r/m16, imm16
        //   0x82: r/m8, imm8 (sign-extended; identical to 0x80 on 8086)
        //   0x83: r/m16, imm8 sign-extended to 16-bit
        if (opcode is 0x80 or 0x81 or 0x82 or 0x83)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            AluOp aluOp = (AluOp)f.Reg;

            bool isWord = (opcode == 0x81 || opcode == 0x83);
            if (isWord)
            {
                ushort imm;
                if (opcode == 0x83)
                    imm = (ushort)(sbyte)FetchByte();   // sign-extended imm8
                else
                    imm = FetchWord();
                ushort dst = ReadRm16(f, disp, segOverride);
                ushort res = X86Alu.Execute16(aluOp, dst, imm, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    WriteRm16(f, disp, segOverride, res);
            }
            else
            {
                byte imm = FetchByte();
                byte dst = ReadRm8(f, disp, segOverride);
                byte res = X86Alu.Execute8(aluOp, dst, imm, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    WriteRm8(f, disp, segOverride, res);
            }
            return f.IsRegister ? 4 : 17;
        }

        // ===== INC/DEC short forms (0x40-0x4F) =====
        // 0x40-0x47 INC r16 (low 3 bits = reg index)
        // 0x48-0x4F DEC r16
        // Note: INC/DEC do NOT affect CF (only OF/SF/ZF/AF/PF).
        if (opcode >= 0x40 && opcode <= 0x4F)
        {
            int reg = opcode & 0x07;
            ushort cur = _state.GetReg16(reg);
            ushort res = (opcode < 0x48) ? X86Alu.Inc16(cur, _state) : X86Alu.Dec16(cur, _state);
            _state.SetReg16(reg, res);
            return 2;
        }

        // ===== PUSH r16 (0x50-0x57) / POP r16 (0x58-0x5F) =====
        if (opcode >= 0x50 && opcode <= 0x57)
        {
            int reg = opcode & 0x07;
            if (reg == 4)
            {
                // 8086 PUSH SP quirk: push the DECREMENTED value of SP,
                // not the original. (80286+ flipped this; we're 8086.)
                _state.SP = (ushort)(_state.SP - 2);
                WriteMem16(SegReg.SS, _state.SP, _state.SP);
            }
            else
            {
                PushWord(_state.GetReg16(reg));
            }
            return 11;
        }
        if (opcode >= 0x58 && opcode <= 0x5F)
        {
            int reg = opcode & 0x07;
            _state.SetReg16(reg, PopWord());
            return 8;
        }

        // ===== PUSH/POP segment registers =====
        // 0x06 PUSH ES, 0x07 POP ES, 0x0E PUSH CS, 0x16 PUSH SS, 0x17 POP SS,
        // 0x1E PUSH DS, 0x1F POP DS. (No POP CS on 8086.)
        switch (opcode)
        {
            case 0x06: PushWord(_state.ES); return 10;
            case 0x07: _state.ES = PopWord(); return 8;
            case 0x0E: PushWord(_state.CS); return 10;
            case 0x16: PushWord(_state.SS); return 10;
            case 0x17: _state.SS = PopWord(); return 8;
            case 0x1E: PushWord(_state.DS); return 10;
            case 0x1F: _state.DS = PopWord(); return 8;
        }

        // ===== PUSHF / POPF =====
        if (opcode == 0x9C) { PushWord(_state.GetFlags()); return 10; }
        if (opcode == 0x9D) { _state.SetFlags(PopWord()); return 8; }

        // ===== XCHG AX, r16 (0x91-0x97) — 0x90 is NOP (XCHG AX, AX) =====
        if (opcode >= 0x91 && opcode <= 0x97)
        {
            int reg = opcode & 0x07;
            ushort tmp = _state.A.X;
            _state.A.X = _state.GetReg16(reg);
            _state.SetReg16(reg, tmp);
            return 3;
        }

        // ===== XCHG r/m8, r8 (0x86)  /  XCHG r/m16, r16 (0x87) =====
        if (opcode is 0x86 or 0x87)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            if (opcode == 0x86)
            {
                byte a = ReadRm8(f, disp, segOverride);
                byte b = _state.GetReg8(f.Reg);
                WriteRm8(f, disp, segOverride, b);
                _state.SetReg8(f.Reg, a);
            }
            else
            {
                ushort a = ReadRm16(f, disp, segOverride);
                ushort b = _state.GetReg16(f.Reg);
                WriteRm16(f, disp, segOverride, b);
                _state.SetReg16(f.Reg, a);
            }
            return f.IsRegister ? 4 : 17;
        }

        // ===== Group 0xFE: INC/DEC r/m8 (reg field = 0/1) =====
        if (opcode == 0xFE)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            byte v = ReadRm8(f, disp, segOverride);
            byte r = f.Reg switch
            {
                0 => X86Alu.Inc8(v, _state),
                1 => X86Alu.Dec8(v, _state),
                _ => throw new NotImplementedException($"0xFE /{f.Reg} undefined on 8086"),
            };
            WriteRm8(f, disp, segOverride, r);
            return f.IsRegister ? 3 : 15;
        }

        // ===== Group 0xFF: INC/DEC/CALL/JMP/PUSH r/m16 =====
        // reg field selects: 0=INC, 1=DEC, 2=CALL near, 3=CALL far,
        //                    4=JMP near, 5=JMP far, 6=PUSH, 7=undefined
        // For 24.4.1, only INC/DEC/PUSH; CALL/JMP indirect lands in 24.4.2.
        if (opcode == 0xFF)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            switch (f.Reg)
            {
                case 0:    // INC r/m16
                {
                    ushort v = ReadRm16(f, disp, segOverride);
                    WriteRm16(f, disp, segOverride, X86Alu.Inc16(v, _state));
                    return f.IsRegister ? 3 : 15;
                }
                case 1:    // DEC r/m16
                {
                    ushort v = ReadRm16(f, disp, segOverride);
                    WriteRm16(f, disp, segOverride, X86Alu.Dec16(v, _state));
                    return f.IsRegister ? 3 : 15;
                }
                case 2:    // CALL near (indirect): IP ← r/m16, push old IP
                {
                    ushort target = ReadRm16(f, disp, segOverride);
                    PushWord(_state.IP);
                    _state.IP = target;
                    return f.IsRegister ? 16 : 21;
                }
                case 3:    // CALL far (indirect): m16:16 — IP/CS ← memory
                {
                    if (f.IsRegister)
                        throw new InvalidOperationException("0xFF /3 with mod=11 is undefined (CALL far needs memory)");
                    var ea = ModRm.ComputeEffectiveAddress(f, disp, _state);
                    SegReg seg = segOverride ?? ea.DefaultSegment;
                    ushort newIp = ReadMem16(seg, ea.Offset);
                    ushort newCs = ReadMem16(seg, (ushort)(ea.Offset + 2));
                    PushWord(_state.CS);
                    PushWord(_state.IP);
                    _state.IP = newIp;
                    _state.CS = newCs;
                    return 37;
                }
                case 4:    // JMP near (indirect): IP ← r/m16
                {
                    _state.IP = ReadRm16(f, disp, segOverride);
                    return f.IsRegister ? 11 : 18;
                }
                case 5:    // JMP far (indirect): m16:16
                {
                    if (f.IsRegister)
                        throw new InvalidOperationException("0xFF /5 with mod=11 is undefined (JMP far needs memory)");
                    var ea = ModRm.ComputeEffectiveAddress(f, disp, _state);
                    SegReg seg = segOverride ?? ea.DefaultSegment;
                    _state.IP = ReadMem16(seg, ea.Offset);
                    _state.CS = ReadMem16(seg, (ushort)(ea.Offset + 2));
                    return 24;
                }
                case 6:    // PUSH r/m16
                {
                    // 8086 quirk also applies via FF /6 when r/m = SP:
                    // push the DECREMENTED SP, not the original.
                    if (f.IsRegister && f.RM == 4)
                    {
                        _state.SP = (ushort)(_state.SP - 2);
                        WriteMem16(SegReg.SS, _state.SP, _state.SP);
                    }
                    else
                    {
                        ushort v = ReadRm16(f, disp, segOverride);
                        PushWord(v);
                    }
                    return f.IsRegister ? 11 : 16;
                }
                default:
                    throw new NotImplementedException($"0xFF /{f.Reg} undefined on 8086");
            }
        }

        // ===== INT/IRET/INTO =====
        if (opcode == 0xCD)        // INT imm8
        {
            byte vec = FetchByte();
            Interrupt(vec);
            return 51;
        }
        if (opcode == 0xCC)        // INT 3
        {
            Interrupt(3);
            return 52;
        }
        if (opcode == 0xCE)        // INTO — INT 4 if OF=1
        {
            if (_state.FlagO) { Interrupt(4); return 53; }
            return 4;
        }
        if (opcode == 0xCF)        // IRET
        {
            _state.IP = PopWord();
            _state.CS = PopWord();
            _state.SetFlags(PopWord());
            return 24;
        }

        // ===== BCD adjustment opcodes =====
        if (opcode is 0x27 or 0x2F or 0x37 or 0x3F or 0xD4 or 0xD5)
        {
            return ExecuteBcdOp(opcode);
        }

        // ===== CBW / CWD =====
        if (opcode == 0x98)        // CBW — sign-extend AL → AX
        {
            _state.A.X = (ushort)(short)(sbyte)_state.A.L;
            return 2;
        }
        if (opcode == 0x99)        // CWD — sign-extend AX → DX:AX
        {
            _state.D.X = (_state.A.X & 0x8000) != 0 ? (ushort)0xFFFF : (ushort)0;
            return 5;
        }

        // ===== IN / OUT (port I/O) =====
        // Real 8086 PIC/PIT/etc not modelled — use OUT 0xE9 / 0xF4 magic
        // ports as before; other ports return open-bus 0xFF/0xFFFF on read,
        // silently drop on write.
        if (opcode == 0xE4)        // IN AL, imm8
        {
            byte port = FetchByte();
            _state.A.L = ReadIoPort8(port);
            return 10;
        }
        if (opcode == 0xE5)        // IN AX, imm8
        {
            byte port = FetchByte();
            _state.A.X = ReadIoPort16(port);
            return 10;
        }
        if (opcode == 0xE7)        // OUT imm8, AX
        {
            byte port = FetchByte();
            WriteIoPort16(port, _state.A.X);
            return 10;
        }
        if (opcode == 0xEC)        // IN AL, DX
        {
            _state.A.L = ReadIoPort8(_state.D.X);
            return 8;
        }
        if (opcode == 0xED)        // IN AX, DX
        {
            _state.A.X = ReadIoPort16(_state.D.X);
            return 8;
        }
        if (opcode == 0xEE)        // OUT DX, AL
        {
            WriteIoPort8(_state.D.X, _state.A.L);
            return 8;
        }
        if (opcode == 0xEF)        // OUT DX, AX
        {
            WriteIoPort16(_state.D.X, _state.A.X);
            return 8;
        }

        // ===== TEST AL, imm8 / TEST AX, imm16 / TEST r/m, r =====
        // TEST is AND that doesn't store the result (only sets flags).
        if (opcode == 0xA8)
        {
            byte imm = FetchByte();
            X86Alu.Execute8(AluOp.And, _state.A.L, imm, _state);
            return 4;
        }
        if (opcode == 0xA9)
        {
            ushort imm = FetchWord();
            X86Alu.Execute16(AluOp.And, _state.A.X, imm, _state);
            return 4;
        }
        if (opcode is 0x84 or 0x85)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            if (opcode == 0x84)
            {
                byte a = ReadRm8(f, disp, segOverride);
                byte b = _state.GetReg8(f.Reg);
                X86Alu.Execute8(AluOp.And, a, b, _state);
            }
            else
            {
                ushort a = ReadRm16(f, disp, segOverride);
                ushort b = _state.GetReg16(f.Reg);
                X86Alu.Execute16(AluOp.And, a, b, _state);
            }
            return f.IsRegister ? 3 : 9;
        }

        // ===== Group 0xF6 (r/m8) / 0xF7 (r/m16) — TEST/NOT/NEG/MUL/IMUL/DIV/IDIV =====
        // ModR/M reg field selects:
        //   0 / 1 (alias) : TEST r/m, imm    (imm8 for F6, imm16 for F7)
        //   2             : NOT r/m
        //   3             : NEG r/m
        //   4             : MUL  AX/DX:AX, r/m
        //   5             : IMUL AX/DX:AX, r/m  (signed)
        //   6             : DIV  AX/DX:AX / r/m → AL,AH or AX,DX
        //   7             : IDIV (signed)
        if (opcode is 0xF6 or 0xF7)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            bool isWord = (opcode == 0xF7);
            return ExecuteGroupF6F7(f, disp, segOverride, isWord);
        }

        // ===== Shift / rotate groups — 0xD0/0xD1/0xD2/0xD3 =====
        //   0xD0 r/m8, 1            (count = 1)
        //   0xD1 r/m16, 1
        //   0xD2 r/m8, CL
        //   0xD3 r/m16, CL
        // ModR/M reg field selects ShiftOp (0..7).
        if (opcode is 0xD0 or 0xD1 or 0xD2 or 0xD3)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            var op = (X86Alu.ShiftOp)f.Reg;
            byte count = (opcode is 0xD2 or 0xD3) ? _state.C.L : (byte)1;
            bool isWord = (opcode == 0xD1 || opcode == 0xD3);

            if (isWord)
            {
                ushort v = ReadRm16(f, disp, segOverride);
                ushort r = X86Alu.Shift16(op, v, count, _state);
                WriteRm16(f, disp, segOverride, r);
            }
            else
            {
                byte v = ReadRm8(f, disp, segOverride);
                byte r = X86Alu.Shift8(op, v, count, _state);
                WriteRm8(f, disp, segOverride, r);
            }
            return f.IsRegister ? 8 : 20;
        }

        // ===== Flag manipulation single-byte opcodes =====
        switch (opcode)
        {
            case 0xF5: _state.FlagC = !_state.FlagC; return 2;     // CMC
            case 0xF8: _state.FlagC = false;         return 2;     // CLC
            case 0xF9: _state.FlagC = true;          return 2;     // STC
            case 0xFA: _state.FlagI = false;         return 2;     // CLI
            case 0xFB: _state.FlagI = true;          return 2;     // STI
            case 0xFC: _state.FlagD = false;         return 2;     // CLD
            case 0xFD: _state.FlagD = true;          return 2;     // STD
            case 0x9E:                                              // SAHF
            {
                // AH bits 0/2/4/6/7 → CF/PF/AF/ZF/SF
                byte ah = _state.A.H;
                _state.FlagC = (ah & 0x01) != 0;
                _state.FlagP = (ah & 0x04) != 0;
                _state.FlagA = (ah & 0x10) != 0;
                _state.FlagZ = (ah & 0x40) != 0;
                _state.FlagS = (ah & 0x80) != 0;
                return 4;
            }
            case 0x9F:                                              // LAHF
            {
                byte ah = 0x02;     // bit 1 always set in 8086 AH-flag image
                if (_state.FlagC) ah |= 0x01;
                if (_state.FlagP) ah |= 0x04;
                if (_state.FlagA) ah |= 0x10;
                if (_state.FlagZ) ah |= 0x40;
                if (_state.FlagS) ah |= 0x80;
                _state.A.H = ah;
                return 4;
            }
        }

        // ===== Conditional jumps (Jcc rel8) — 0x70-0x7F =====
        //
        // Each opcode tests a flag predicate; if true, IP += sign-extended
        // disp8 (added to the post-fetched IP, i.e. relative to the
        // instruction following Jcc).
        if (opcode >= 0x70 && opcode <= 0x7F)
        {
            sbyte disp = (sbyte)FetchByte();
            bool taken = opcode switch
            {
                0x70 => _state.FlagO,                                            // JO
                0x71 => !_state.FlagO,                                           // JNO
                0x72 => _state.FlagC,                                            // JB / JC / JNAE
                0x73 => !_state.FlagC,                                           // JNB / JAE / JNC
                0x74 => _state.FlagZ,                                            // JE / JZ
                0x75 => !_state.FlagZ,                                           // JNE / JNZ
                0x76 => _state.FlagC || _state.FlagZ,                            // JBE / JNA
                0x77 => !_state.FlagC && !_state.FlagZ,                          // JA / JNBE
                0x78 => _state.FlagS,                                            // JS
                0x79 => !_state.FlagS,                                           // JNS
                0x7A => _state.FlagP,                                            // JP / JPE
                0x7B => !_state.FlagP,                                           // JNP / JPO
                0x7C => _state.FlagS != _state.FlagO,                            // JL / JNGE
                0x7D => _state.FlagS == _state.FlagO,                            // JGE / JNL
                0x7E => _state.FlagZ || (_state.FlagS != _state.FlagO),          // JLE / JNG
                0x7F => !_state.FlagZ && (_state.FlagS == _state.FlagO),         // JG / JNLE
                _ => false,
            };
            if (taken) _state.IP = (ushort)(_state.IP + disp);
            return taken ? 16 : 4;
        }

        // ===== Unconditional near/short jumps =====
        if (opcode == 0xEB)        // JMP rel8
        {
            sbyte disp = (sbyte)FetchByte();
            _state.IP = (ushort)(_state.IP + disp);
            return 15;
        }
        if (opcode == 0xE9)        // JMP rel16
        {
            short disp = (short)FetchWord();
            _state.IP = (ushort)(_state.IP + disp);
            return 15;
        }

        // ===== JCXZ rel8 — branch if CX == 0 =====
        if (opcode == 0xE3)
        {
            sbyte disp = (sbyte)FetchByte();
            if (_state.C.X == 0) _state.IP = (ushort)(_state.IP + disp);
            return _state.C.X == 0 ? 18 : 6;
        }

        // ===== LOOP / LOOPE/LOOPZ / LOOPNE/LOOPNZ — 0xE0/E1/E2 =====
        // CX -= 1 ; branch if CX != 0 (LOOP), or CX != 0 && ZF==1 (LOOPE),
        // or CX != 0 && ZF==0 (LOOPNE).
        if (opcode is 0xE0 or 0xE1 or 0xE2)
        {
            sbyte disp = (sbyte)FetchByte();
            _state.C.X = (ushort)(_state.C.X - 1);
            bool taken = _state.C.X != 0 && opcode switch
            {
                0xE0 => !_state.FlagZ,    // LOOPNE / LOOPNZ
                0xE1 => _state.FlagZ,     // LOOPE  / LOOPZ
                0xE2 => true,             // LOOP
                _ => false,
            };
            if (taken) _state.IP = (ushort)(_state.IP + disp);
            return taken ? 17 : 5;
        }

        // ===== CALL near rel16 — 0xE8 =====
        if (opcode == 0xE8)
        {
            short disp = (short)FetchWord();
            PushWord(_state.IP);
            _state.IP = (ushort)(_state.IP + disp);
            return 19;
        }

        // ===== CALL far ptr16:16 — 0x9A =====
        if (opcode == 0x9A)
        {
            ushort newIp = FetchWord();
            ushort newCs = FetchWord();
            PushWord(_state.CS);
            PushWord(_state.IP);
            _state.IP = newIp;
            _state.CS = newCs;
            return 28;
        }

        // ===== RET / RETF =====
        if (opcode == 0xC3) { _state.IP = PopWord(); return 16; }   // RET near
        if (opcode == 0xC2)                                          // RET imm16 (near, with stack adjust)
        {
            ushort imm = FetchWord();
            _state.IP = PopWord();
            _state.SP = (ushort)(_state.SP + imm);
            return 20;
        }
        if (opcode == 0xCB)                                          // RETF
        {
            _state.IP = PopWord();
            _state.CS = PopWord();
            return 26;
        }
        if (opcode == 0xCA)                                          // RETF imm16
        {
            ushort imm = FetchWord();
            _state.IP = PopWord();
            _state.CS = PopWord();
            _state.SP = (ushort)(_state.SP + imm);
            return 25;
        }

        // ===== 0x8F /0 — POP r/m16 =====
        if (opcode == 0x8F)
        {
            byte modrm = FetchByte();
            var f = ModRmFields.Decode(modrm);
            ushort disp = FetchDisplacement(f.DisplacementBytes);
            if (f.Reg != 0)
                throw new NotImplementedException($"0x8F /{f.Reg} undefined on 8086");
            ushort v = PopWord();
            WriteRm16(f, disp, segOverride, v);
            return f.IsRegister ? 8 : 17;
        }

        // ===== Anything else = not implemented yet =====
        throw new NotImplementedException(
            $"X86LegacyCpu: opcode 0x{opcode:X2} at CS:IP={_state.CS:X4}:{(ushort)(_state.IP - 1):X4} not implemented yet.");
    }

    /// <summary>
    /// Group 0xF6 / 0xF7 dispatch — 8086's "unary ALU" group covering
    /// TEST/NOT/NEG/MUL/IMUL/DIV/IDIV. Apr86's CPU.cs had bugs in
    /// MUL/IMUL CF/OF logic + IMUL byte sign-extension (per doc #24
    /// §2.1); we implement straight from the 8086 datasheet + validate
    /// against Tom Harte SST.
    /// </summary>
    private int ExecuteGroupF6F7(ModRmFields f, ushort disp, SegReg? segOverride, bool isWord)
    {
        switch (f.Reg)
        {
            case 0:    // TEST r/m, imm
            case 1:    // alias on 8086 (some sources document; identical behaviour)
            {
                if (isWord)
                {
                    ushort dst = ReadRm16(f, disp, segOverride);
                    ushort imm = FetchWord();
                    X86Alu.Execute16(AluOp.And, dst, imm, _state);
                }
                else
                {
                    byte dst = ReadRm8(f, disp, segOverride);
                    byte imm = FetchByte();
                    X86Alu.Execute8(AluOp.And, dst, imm, _state);
                }
                return f.IsRegister ? 5 : 11;
            }

            case 2:    // NOT r/m — bitwise complement, no flag changes
            {
                if (isWord)
                {
                    ushort v = ReadRm16(f, disp, segOverride);
                    WriteRm16(f, disp, segOverride, (ushort)~v);
                }
                else
                {
                    byte v = ReadRm8(f, disp, segOverride);
                    WriteRm8(f, disp, segOverride, (byte)~v);
                }
                return f.IsRegister ? 3 : 16;
            }

            case 3:    // NEG r/m — two's complement; flags as 0 - r/m
            {
                if (isWord)
                {
                    ushort v = ReadRm16(f, disp, segOverride);
                    ushort r = X86Alu.Execute16(AluOp.Sub, 0, v, _state);
                    WriteRm16(f, disp, segOverride, r);
                }
                else
                {
                    byte v = ReadRm8(f, disp, segOverride);
                    byte r = X86Alu.Execute8(AluOp.Sub, 0, v, _state);
                    WriteRm8(f, disp, segOverride, r);
                }
                return f.IsRegister ? 3 : 16;
            }

            case 4:    // MUL — unsigned: AX = AL * r/m8 ; DX:AX = AX * r/m16
            {
                // 8088 quirk per Tom Harte SST v1: SF/ZF/PF all reflect the
                // HIGH BYTE only (AH for byte MUL; DL for word MUL — the
                // microcode happens to leave the parity-line latched on
                // the high byte).
                //   CF/OF: 1 iff high half non-zero
                //   SF:    bit 7 of high byte (= bit 15 of full result)
                //   ZF:    high byte == 0  (effectively !CF)
                //   PF:    parity of high byte
                //   AF:    preserved
                // 8088 silicon (per Tom Harte SST v1) — different rule per width:
                //   Byte MUL: SF/ZF/PF all from AH (high byte of result)
                //   Word MUL: SF from DH (bit 15 of DX), PF from DL (parity
                //             of low byte of DX), ZF from DX==0 (full high half)
                if (isWord)
                {
                    ushort src = ReadRm16(f, disp, segOverride);
                    uint result = (uint)_state.A.X * src;
                    _state.A.X = (ushort)result;
                    _state.D.X = (ushort)(result >> 16);
                    bool overflow = _state.D.X != 0;
                    _state.FlagC = _state.FlagO = overflow;
                    _state.FlagS = (_state.D.X & 0x8000) != 0;
                    _state.FlagZ = _state.D.X == 0;
                    _state.FlagP = X86Alu.ParityEven((byte)_state.D.X);
                }
                else
                {
                    byte src = ReadRm8(f, disp, segOverride);
                    ushort result = (ushort)(_state.A.L * src);
                    _state.A.X = result;
                    bool overflow = _state.A.H != 0;
                    _state.FlagC = _state.FlagO = overflow;
                    _state.FlagS = (_state.A.H & 0x80) != 0;
                    _state.FlagZ = _state.A.H == 0;
                    _state.FlagP = X86Alu.ParityEven(_state.A.H);
                }
                return f.IsRegister ? 70 : 76;
            }

            case 5:    // IMUL — signed
            {
                if (isWord)
                {
                    short a = (short)_state.A.X;
                    short b = (short)ReadRm16(f, disp, segOverride);
                    int result = a * b;
                    _state.A.X = (ushort)result;
                    _state.D.X = (ushort)(result >> 16);
                    bool nontrivial = (short)_state.A.X < 0
                        ? _state.D.X != 0xFFFF
                        : _state.D.X != 0x0000;
                    _state.FlagC = _state.FlagO = nontrivial;
                    _state.FlagS = (_state.D.X & 0x8000) != 0;
                    _state.FlagZ = _state.D.X == 0;
                    _state.FlagP = X86Alu.ParityEven((byte)_state.D.X);
                }
                else
                {
                    sbyte a = (sbyte)_state.A.L;
                    sbyte b = (sbyte)ReadRm8(f, disp, segOverride);
                    short result = (short)(a * b);
                    _state.A.X = (ushort)result;
                    bool nontrivial = (sbyte)_state.A.L < 0
                        ? _state.A.H != 0xFF
                        : _state.A.H != 0x00;
                    _state.FlagC = _state.FlagO = nontrivial;
                    _state.FlagS = (_state.A.H & 0x80) != 0;
                    _state.FlagZ = _state.A.H == 0;
                    _state.FlagP = X86Alu.ParityEven(_state.A.H);
                }
                return f.IsRegister ? 80 : 86;
            }

            case 6:    // DIV — unsigned: divide DX:AX (or AX) by r/m
            {
                if (isWord)
                {
                    ushort src = ReadRm16(f, disp, segOverride);
                    if (src == 0) { Interrupt(0); return 50; }
                    uint dividend = ((uint)_state.D.X << 16) | _state.A.X;
                    uint quotient = dividend / src;
                    uint remainder = dividend % src;
                    if (quotient > 0xFFFF) { Interrupt(0); return 50; }
                    _state.A.X = (ushort)quotient;
                    _state.D.X = (ushort)remainder;
                }
                else
                {
                    byte src = ReadRm8(f, disp, segOverride);
                    if (src == 0) { Interrupt(0); return 50; }
                    ushort dividend = _state.A.X;
                    int quotient = dividend / src;
                    int remainder = dividend % src;
                    if (quotient > 0xFF) { Interrupt(0); return 50; }
                    _state.A.L = (byte)quotient;
                    _state.A.H = (byte)remainder;
                }
                return f.IsRegister ? 80 : 86;
            }

            case 7:    // IDIV — signed
            {
                if (isWord)
                {
                    short src = (short)ReadRm16(f, disp, segOverride);
                    if (src == 0) { Interrupt(0); return 50; }
                    int dividend = ((int)(short)_state.D.X << 16) | _state.A.X;
                    // For unbiased rounding we need C# integer division semantics
                    // (truncate toward zero) — that matches 8086 IDIV.
                    int quotient = dividend / src;
                    int remainder = dividend % src;
                    if (quotient > 0x7FFF || quotient < -0x8000) { Interrupt(0); return 50; }
                    _state.A.X = (ushort)quotient;
                    _state.D.X = (ushort)remainder;
                }
                else
                {
                    sbyte src = (sbyte)ReadRm8(f, disp, segOverride);
                    if (src == 0) { Interrupt(0); return 50; }
                    short dividend = (short)_state.A.X;
                    int quotient = dividend / src;
                    int remainder = dividend % src;
                    if (quotient > 0x7F || quotient < -0x80) { Interrupt(0); return 50; }
                    _state.A.L = (byte)quotient;
                    _state.A.H = (byte)remainder;
                }
                return f.IsRegister ? 100 : 106;
            }

            default:
                throw new NotImplementedException($"F6/F7 /{f.Reg} undefined on 8086");
        }
    }

    /// <summary>
    /// Execute one of the string opcodes 0xA4-0xA7, 0xAA-0xAF, optionally
    /// wrapped in a REP / REPNE prefix loop. The string ops use:
    ///   SI (with DS source segment, segment-overrideable) — for source
    ///   DI (with ES destination segment, NOT overrideable on 8086)
    ///   DF (direction flag) — selects increment or decrement of SI/DI
    ///
    /// REP semantics:
    ///   No prefix:        single iteration, set flags as appropriate
    ///   0xF3 (REP/REPE):  loop while CX != 0; for CMPS/SCAS also requires ZF=1
    ///   0xF2 (REPNE):     loop while CX != 0; for CMPS/SCAS also requires ZF=0
    /// </summary>
    private int ExecuteStringOp(byte opcode, SegReg? segOverride, byte? rep)
    {
        bool isWord = (opcode & 0x01) != 0;          // bit 0 differentiates byte/word forms
        int delta = _state.FlagD
            ? (isWord ? -2 : -1)
            : (isWord ?  2 :  1);
        SegReg srcSeg = segOverride ?? SegReg.DS;    // DS for source, override allowed
        bool isConditional = opcode is 0xA6 or 0xA7 or 0xAE or 0xAF;
        int cycles = 0;

        do
        {
            // REP loop entry: stop when CX == 0
            if (rep.HasValue)
            {
                if (_state.C.X == 0) break;
                _state.C.X = (ushort)(_state.C.X - 1);
            }

            switch (opcode)
            {
                case 0xA4:    // MOVSB — [ES:DI] ← [DS:SI]
                    WriteMem8(SegReg.ES, _state.DI, ReadMem8(srcSeg, _state.SI));
                    break;
                case 0xA5:    // MOVSW
                    WriteMem16(SegReg.ES, _state.DI, ReadMem16(srcSeg, _state.SI));
                    break;
                case 0xA6:    // CMPSB — flags from [DS:SI] - [ES:DI]
                    X86Alu.Execute8(AluOp.Cmp,
                        ReadMem8(srcSeg, _state.SI),
                        ReadMem8(SegReg.ES, _state.DI),
                        _state);
                    break;
                case 0xA7:    // CMPSW
                    X86Alu.Execute16(AluOp.Cmp,
                        ReadMem16(srcSeg, _state.SI),
                        ReadMem16(SegReg.ES, _state.DI),
                        _state);
                    break;
                case 0xAA:    // STOSB — [ES:DI] ← AL
                    WriteMem8(SegReg.ES, _state.DI, _state.A.L);
                    break;
                case 0xAB:    // STOSW
                    WriteMem16(SegReg.ES, _state.DI, _state.A.X);
                    break;
                case 0xAC:    // LODSB — AL ← [DS:SI]
                    _state.A.L = ReadMem8(srcSeg, _state.SI);
                    break;
                case 0xAD:    // LODSW
                    _state.A.X = ReadMem16(srcSeg, _state.SI);
                    break;
                case 0xAE:    // SCASB — flags from AL - [ES:DI]
                    X86Alu.Execute8(AluOp.Cmp, _state.A.L, ReadMem8(SegReg.ES, _state.DI), _state);
                    break;
                case 0xAF:    // SCASW — flags from AX - [ES:DI]
                    X86Alu.Execute16(AluOp.Cmp, _state.A.X, ReadMem16(SegReg.ES, _state.DI), _state);
                    break;
            }

            // SI/DI update — depends on which registers the op touches.
            switch (opcode)
            {
                case 0xAA: case 0xAB: case 0xAE: case 0xAF:    // STOS / SCAS — DI only
                    _state.DI = (ushort)(_state.DI + delta);
                    break;
                case 0xAC: case 0xAD:                          // LODS — SI only
                    _state.SI = (ushort)(_state.SI + delta);
                    break;
                default:                                        // MOVS / CMPS — both
                    _state.SI = (ushort)(_state.SI + delta);
                    _state.DI = (ushort)(_state.DI + delta);
                    break;
            }
            cycles += 4;

            // Conditional REP early-exit (for CMPS/SCAS):
            //   0xF3 (REP/REPE): continue while ZF=1; exit when ZF=0
            //   0xF2 (REPNE):    continue while ZF=0; exit when ZF=1
            if (rep.HasValue && isConditional)
            {
                if (rep.Value == 0xF3 && !_state.FlagZ) break;
                if (rep.Value == 0xF2 &&  _state.FlagZ) break;
            }
        }
        while (rep.HasValue);

        return cycles;
    }

    // ---------------- BCD adjustments ----------------
    //
    // 8086's BCD-adjust ops fix up AL after binary arithmetic so AL
    // contains a valid packed/unpacked BCD digit pair. Apr86's CPU.cs
    // had the AAM/AAD imm8 ignored bug (per doc #24 §2.1) — these are
    // the correct implementations.

    private int ExecuteBcdOp(byte opcode)
    {
        switch (opcode)
        {
            case 0x37:    // AAA
            {
                bool adjust = (_state.A.L & 0x0F) > 9 || _state.FlagA;
                if (adjust)
                {
                    _state.A.L = (byte)((_state.A.L + 6) & 0x0F);
                    _state.A.H = (byte)(_state.A.H + 1);
                    _state.FlagA = true;
                    _state.FlagC = true;
                }
                else
                {
                    _state.A.L = (byte)(_state.A.L & 0x0F);
                    _state.FlagA = false;
                    _state.FlagC = false;
                }
                _state.FlagS = (_state.A.L & 0x80) != 0;
                _state.FlagZ = _state.A.L == 0;
                _state.FlagP = X86Alu.ParityEven(_state.A.L);
                return 4;
            }
            case 0x3F:    // AAS
            {
                bool adjust = (_state.A.L & 0x0F) > 9 || _state.FlagA;
                if (adjust)
                {
                    _state.A.L = (byte)((_state.A.L - 6) & 0x0F);
                    _state.A.H = (byte)(_state.A.H - 1);
                    _state.FlagA = true;
                    _state.FlagC = true;
                }
                else
                {
                    _state.A.L = (byte)(_state.A.L & 0x0F);
                    _state.FlagA = false;
                    _state.FlagC = false;
                }
                _state.FlagS = (_state.A.L & 0x80) != 0;
                _state.FlagZ = _state.A.L == 0;
                _state.FlagP = X86Alu.ParityEven(_state.A.L);
                return 4;
            }
            case 0x27:    // DAA
            {
                byte oldAl = _state.A.L;
                bool oldCf = _state.FlagC;
                _state.FlagC = false;
                if ((oldAl & 0x0F) > 9 || _state.FlagA)
                {
                    int t = oldAl + 6;
                    _state.A.L = (byte)t;
                    _state.FlagC = oldCf || (t > 0xFF);
                    _state.FlagA = true;
                }
                else
                {
                    _state.FlagA = false;
                }
                if (oldAl > 0x99 || oldCf)
                {
                    _state.A.L = (byte)(_state.A.L + 0x60);
                    _state.FlagC = true;
                }
                _state.FlagS = (_state.A.L & 0x80) != 0;
                _state.FlagZ = _state.A.L == 0;
                _state.FlagP = X86Alu.ParityEven(_state.A.L);
                return 4;
            }
            case 0x2F:    // DAS
            {
                byte oldAl = _state.A.L;
                bool oldCf = _state.FlagC;
                _state.FlagC = false;
                if ((oldAl & 0x0F) > 9 || _state.FlagA)
                {
                    int t = oldAl - 6;
                    _state.A.L = (byte)t;
                    _state.FlagC = oldCf || (t < 0);
                    _state.FlagA = true;
                }
                else
                {
                    _state.FlagA = false;
                }
                if (oldAl > 0x99 || oldCf)
                {
                    _state.A.L = (byte)(_state.A.L - 0x60);
                    _state.FlagC = true;
                }
                _state.FlagS = (_state.A.L & 0x80) != 0;
                _state.FlagZ = _state.A.L == 0;
                _state.FlagP = X86Alu.ParityEven(_state.A.L);
                return 4;
            }
            case 0xD4:    // AAM imm8 — ah = al / imm; al = al % imm
            {
                byte imm = FetchByte();    // Apr86 ignored this — doc #24 §2.1 bug #1
                if (imm == 0) { Interrupt(0); return 50; }
                _state.A.H = (byte)(_state.A.L / imm);
                _state.A.L = (byte)(_state.A.L % imm);
                _state.FlagS = (_state.A.L & 0x80) != 0;
                _state.FlagZ = _state.A.L == 0;
                _state.FlagP = X86Alu.ParityEven(_state.A.L);
                return 83;
            }
            case 0xD5:    // AAD imm8 — al = (ah*imm + al) & 0xFF; ah = 0
            {
                byte imm = FetchByte();    // Apr86 ignored this — bug #2
                int t = _state.A.H * imm + _state.A.L;
                _state.A.L = (byte)t;
                _state.A.H = 0;
                _state.FlagS = (_state.A.L & 0x80) != 0;     // Apr86 used 0x8000 — bug #3
                _state.FlagZ = _state.A.L == 0;
                _state.FlagP = X86Alu.ParityEven(_state.A.L);
                return 60;
            }
            default:
                throw new NotImplementedException($"BCD opcode 0x{opcode:X2} not handled");
        }
    }

    // ---------------- I/O port hooks ----------------
    //
    // No real PIC/PIT/8237 modelling. Magic ports retained for
    // host-debug bridging (Bochs 0xE9 + qemu isa-debug-exit 0xF4); all
    // other ports return open-bus and silently drop writes.

    private byte ReadIoPort8(ushort port) => 0xFF;
    private ushort ReadIoPort16(ushort port) => 0xFFFF;

    private void WriteIoPort8(ushort port, byte value)
    {
        if (port == 0xE9) Console.Write((char)value);
        else if (port == 0xF4) _halted = true;
        // else: dropped
    }

    private void WriteIoPort16(ushort port, ushort value)
    {
        WriteIoPort8(port, (byte)value);
    }

    /// <summary>
    /// Software interrupt entry. Pushes flags + CS + IP, fetches new
    /// CS:IP from the IVT at 0:type*4, clears IF/TF. Used by INT/INTO/
    /// DIV-by-zero etc. Full INT opcode dispatch comes in 24.4.6.
    /// </summary>
    private void Interrupt(byte type)
    {
        PushWord(_state.GetFlags());
        PushWord(_state.CS);
        PushWord(_state.IP);
        _state.FlagI = false;
        _state.FlagT = false;
        int vec = type * 4;
        _state.IP = (ushort)(_mem.ReadByte(vec) | (_mem.ReadByte(vec + 1) << 8));
        _state.CS = (ushort)(_mem.ReadByte(vec + 2) | (_mem.ReadByte(vec + 3) << 8));
    }

    /// <summary>
    /// Dispatch the 6 form variants of an ALU group (forms +0..+5).
    /// Forms +6/+7 inside the same byte range are NOT ALU — caller
    /// must filter them out before calling this.
    /// </summary>
    private int ExecuteAluGroup(AluOp aluOp, int form, SegReg? segOverride)
    {
        switch (form)
        {
            case 0:    // r/m8, r8
            {
                byte modrm = FetchByte();
                var f = ModRmFields.Decode(modrm);
                ushort disp = FetchDisplacement(f.DisplacementBytes);
                byte dst = ReadRm8(f, disp, segOverride);
                byte src = _state.GetReg8(f.Reg);
                byte res = X86Alu.Execute8(aluOp, dst, src, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    WriteRm8(f, disp, segOverride, res);
                return f.IsRegister ? 3 : 16;
            }
            case 1:    // r/m16, r16
            {
                byte modrm = FetchByte();
                var f = ModRmFields.Decode(modrm);
                ushort disp = FetchDisplacement(f.DisplacementBytes);
                ushort dst = ReadRm16(f, disp, segOverride);
                ushort src = _state.GetReg16(f.Reg);
                ushort res = X86Alu.Execute16(aluOp, dst, src, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    WriteRm16(f, disp, segOverride, res);
                return f.IsRegister ? 3 : 16;
            }
            case 2:    // r8, r/m8
            {
                byte modrm = FetchByte();
                var f = ModRmFields.Decode(modrm);
                ushort disp = FetchDisplacement(f.DisplacementBytes);
                byte dst = _state.GetReg8(f.Reg);
                byte src = ReadRm8(f, disp, segOverride);
                byte res = X86Alu.Execute8(aluOp, dst, src, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    _state.SetReg8(f.Reg, res);
                return f.IsRegister ? 3 : 9;
            }
            case 3:    // r16, r/m16
            {
                byte modrm = FetchByte();
                var f = ModRmFields.Decode(modrm);
                ushort disp = FetchDisplacement(f.DisplacementBytes);
                ushort dst = _state.GetReg16(f.Reg);
                ushort src = ReadRm16(f, disp, segOverride);
                ushort res = X86Alu.Execute16(aluOp, dst, src, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    _state.SetReg16(f.Reg, res);
                return f.IsRegister ? 3 : 9;
            }
            case 4:    // AL, imm8
            {
                byte imm = FetchByte();
                byte res = X86Alu.Execute8(aluOp, _state.A.L, imm, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    _state.A.L = res;
                return 4;
            }
            case 5:    // AX, imm16
            {
                ushort imm = FetchWord();
                ushort res = X86Alu.Execute16(aluOp, _state.A.X, imm, _state);
                if (!X86Alu.IsCompareOnly(aluOp))
                    _state.A.X = res;
                return 4;
            }
            default:
                throw new InvalidOperationException($"ExecuteAluGroup: form {form} not in ALU range");
        }
    }
}
