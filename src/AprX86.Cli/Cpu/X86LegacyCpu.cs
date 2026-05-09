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

        // Prefix loop — segment override is the only prefix we honour
        // in phase 24.2.2 (REP / LOCK come with string ops in 24.2.3+).
        SegReg? segOverride = null;
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
                default: goto exec;
            }
        }
    exec:

        return Execute(opcode, segOverride);
    }

    /// <summary>
    /// Dispatch a single (already-fetched, prefix-stripped) opcode.
    /// Phase 24.2.2 implements: NOP / HLT / OUT 0xE9 / JMP far / full
    /// MOV set. Anything else throws with diagnostic info.
    /// </summary>
    private int Execute(byte opcode, SegReg? segOverride)
    {
        // ===== NOP / HLT / magic-port stubs / JMP far =====
        switch (opcode)
        {
            case 0x90:    // NOP
                return 3;

            case 0xF4:    // HLT
                _halted = true;
                return 2;

            case 0xE6:    // OUT imm8, AL — magic-port stub
            {
                byte port = FetchByte();
                if      (port == 0xE9) Console.Write((char)_state.A.L);
                else if (port == 0xF4) _halted = true;
                return 8;
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
