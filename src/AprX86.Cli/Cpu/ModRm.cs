// 8086 ModR/M decoder — pure functions, no CPU coupling.
//
// The ModR/M byte is the single biggest source of complexity in 8086
// instruction encoding. It packs three fields:
//
//   bits 7:6  mod  — addressing mode selector
//   bits 5:3  reg  — register operand OR opcode extension
//   bits 2:0  r/m  — register-direct OR memory-addressing form
//
// Memory r/m forms (mod ≠ 11) on 8086:
//   r/m | base+index               | default segment
//   ----+--------------------------+----------------
//   000 | [BX + SI]                | DS
//   001 | [BX + DI]                | DS
//   010 | [BP + SI]                | SS  ← BP defaults to SS
//   011 | [BP + DI]                | SS  ← BP defaults to SS
//   100 | [SI]                     | DS
//   101 | [DI]                     | DS
//   110 | [BP]   except mod=00     | SS  ← BP defaults to SS
//       | disp16 (direct addr)     | DS  (mod=00, r/m=110 only)
//   111 | [BX]                     | DS
//
// Mod values:
//   00 — no displacement (except mod=00, r/m=110 = disp16 direct)
//   01 — disp8 (sign-extended to 16-bit)
//   10 — disp16
//   11 — register direct (no memory access; r/m is a register encoding)
//
// 80386+ extends this with a SIB byte after ModR/M when r/m=100 in
// 32-bit modes; we do NOT model SIB (this is the x86-16 chain only).

namespace AprX86.Cli.Cpu;

/// <summary>
/// Result of decoding the ModR/M byte (without yet fetching displacement
/// bytes). The displacement count tells the caller how many extra bytes
/// to fetch from the instruction stream.
/// </summary>
public readonly struct ModRmFields
{
    /// <summary>bits 7:6 of the ModR/M byte.</summary>
    public byte Mod { get; init; }
    /// <summary>bits 5:3 of the ModR/M byte. Either a register index
    /// or an opcode extension (group-opcode patterns).</summary>
    public byte Reg { get; init; }
    /// <summary>bits 2:0 of the ModR/M byte.</summary>
    public byte RM  { get; init; }

    /// <summary>True if mod == 11 (register-direct form, no memory access).</summary>
    public bool IsRegister => Mod == 0b11;

    /// <summary>
    /// How many additional bytes follow the ModR/M byte for the
    /// displacement field. 0, 1 (disp8), or 2 (disp16). Mod=00 with
    /// r/m=110 is the special "disp16 direct address" case (2 bytes
    /// displacement despite mod=00).
    /// </summary>
    public int DisplacementBytes
    {
        get
        {
            if (Mod == 0b00) return RM == 0b110 ? 2 : 0;
            if (Mod == 0b01) return 1;
            if (Mod == 0b10) return 2;
            return 0;       // mod=11 register-direct
        }
    }

    public static ModRmFields Decode(byte modrm) => new()
    {
        Mod = (byte)((modrm >> 6) & 0b11),
        Reg = (byte)((modrm >> 3) & 0b111),
        RM  = (byte)(modrm & 0b111),
    };
}

/// <summary>
/// Computed effective address for a memory operand (mod ≠ 11).
/// Both the offset (within the segment) and the default segment
/// register are produced; the caller may override the segment via
/// the segment-override prefix.
/// </summary>
public readonly struct EffectiveAddress
{
    public ushort Offset { get; init; }
    public SegReg DefaultSegment { get; init; }
}

public static class ModRm
{
    /// <summary>
    /// Compute the effective address for a memory operand given the
    /// decoded ModR/M fields, the (already-fetched) displacement, and
    /// current register state. Caller is responsible for: (a) ensuring
    /// <c>fields.IsRegister == false</c> before calling, (b) fetching
    /// the right number of displacement bytes per
    /// <see cref="ModRmFields.DisplacementBytes"/>.
    /// </summary>
    public static EffectiveAddress ComputeEffectiveAddress(
        ModRmFields fields,
        ushort disp,
        in X86State state)
    {
        if (fields.IsRegister)
            throw new InvalidOperationException(
                "ComputeEffectiveAddress called with mod==11 (register-direct). Caller bug.");

        // Special case: mod=00, r/m=110 — disp16 direct address (no
        // base/index registers; segment defaults to DS).
        if (fields.Mod == 0b00 && fields.RM == 0b110)
            return new EffectiveAddress { Offset = disp, DefaultSegment = SegReg.DS };

        // Normal base+index pattern. r/m selects the addressing-mode template.
        ushort baseIndex = fields.RM switch
        {
            0b000 => (ushort)(state.B.X + state.SI),     // [BX + SI]
            0b001 => (ushort)(state.B.X + state.DI),     // [BX + DI]
            0b010 => (ushort)(state.BP + state.SI),      // [BP + SI]
            0b011 => (ushort)(state.BP + state.DI),      // [BP + DI]
            0b100 => state.SI,                            // [SI]
            0b101 => state.DI,                            // [DI]
            0b110 => state.BP,                            // [BP]    (mod=01 / mod=10 only — direct case handled above)
            0b111 => state.B.X,                           // [BX]
            _     => 0,
        };

        // Default segment is SS if BP is part of the addressing mode,
        // else DS. r/m values 010, 011, 110 are the BP-based forms.
        SegReg defaultSeg = fields.RM switch
        {
            0b010 or 0b011 or 0b110 => SegReg.SS,
            _ => SegReg.DS,
        };

        ushort offset = (ushort)(baseIndex + disp);
        return new EffectiveAddress { Offset = offset, DefaultSegment = defaultSeg };
    }
}
