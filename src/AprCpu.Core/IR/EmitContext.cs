using System.Text.Json;
using AprCpu.Core.JsonSpec;
using LLVMSharp.Interop;

namespace AprCpu.Core.IR;

/// <summary>
/// State carried through the emission of a single instruction's body.
/// Each instruction gets its own EmitContext: value cache, current basic
/// block, etc. The IR builder is shared with the parent module.
/// </summary>
public sealed unsafe class EmitContext
{
    public LLVMModuleRef    Module      { get; }
    public LLVMContextRef   Context     { get; }
    public LLVMBuilderRef   Builder     { get; }
    public LLVMValueRef     Function    { get; }
    public LLVMValueRef     StatePtr    { get; }
    public CpuStateLayout   Layout      { get; }
    public InstructionSetSpec  InstructionSet { get; }
    // Phase 7 A.2 — Format/Def/Instruction are per-instruction. In
    // single-instruction mode they're set once by the constructor; in
    // block mode BlockFunctionBuilder calls BeginInstruction() between
    // each instruction's emission to swap them out + reset Values.
    public LLVMValueRef     Instruction { get; private set; }
    public EncodingFormat   Format      { get; private set; }
    public InstructionDef   Def         { get; private set; }

    /// <summary>Named values: field extractions, operand outputs, step `out`s.</summary>
    public Dictionary<string, LLVMValueRef> Values { get; } = new(StringComparer.Ordinal);

    public EmitContext(
        LLVMModuleRef module,
        LLVMBuilderRef builder,
        LLVMValueRef function,
        LLVMValueRef statePtr,
        LLVMValueRef instructionWord,
        CpuStateLayout layout,
        InstructionSetSpec set,
        EncodingFormat format,
        InstructionDef def)
    {
        Module      = module;
        Context     = module.Context;
        Builder     = builder;
        Function    = function;
        StatePtr    = statePtr;
        Instruction = instructionWord;
        Layout      = layout;
        InstructionSet = set;
        Format      = format;
        Def         = def;
    }

    /// <summary>
    /// Resolve a name to an LLVM value:
    ///   1. If already in cache, return it.
    ///   2. If matches a format field, build the extraction (and cache it).
    ///   3. Otherwise throw — unknown name.
    /// </summary>
    public LLVMValueRef Resolve(string name)
    {
        if (Values.TryGetValue(name, out var cached)) return cached;

        if (Format.Fields.TryGetValue(name, out var range))
        {
            var v = ExtractField(range, name);
            Values[name] = v;
            return v;
        }

        throw new InvalidOperationException(
            $"EmitContext: cannot resolve name '{name}' (not in cache, not a field of format '{Format.Name}').");
    }

    /// <summary>Build IR to extract a bit-range from the instruction word.</summary>
    public LLVMValueRef ExtractField(BitRange range, string name)
    {
        var i32 = LLVMTypeRef.Int32;
        var shift = LLVMValueRef.CreateConstInt(i32, (ulong)range.Low, SignExtend: false);
        var mask  = LLVMValueRef.CreateConstInt(i32, range.LowMask, SignExtend: false);
        var shifted = Builder.BuildLShr(Instruction, shift, $"{name}_shifted");
        return Builder.BuildAnd(shifted, mask, name);
    }

    /// <summary>Constant i32 helper.</summary>
    public LLVMValueRef ConstU32(uint v)
        => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, v, SignExtend: false);

    /// <summary>Constant i1 helper.</summary>
    public LLVMValueRef ConstBool(bool b)
        => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, b ? 1u : 0u, SignExtend: false);

    /// <summary>Append a fresh basic block to the current function.</summary>
    public LLVMBasicBlockRef AppendBlock(string name) => Function.AppendBasicBlock(name);

    /// <summary>
    /// Phase 7 A.2 — switch the per-instruction state for the next
    /// instruction in a block. Resets <see cref="Values"/> (per-instr
    /// local cache) but preserves <see cref="StatusShadowAllocas"/> +
    /// <see cref="EntryBlock"/> (block-wide).
    ///
    /// Phase 7 A.6.1 Strategy 2 — also captures the instruction's base
    /// address (<paramref name="baseAddress"/>). Set by
    /// <see cref="BlockFunctionBuilder"/> in block-JIT mode so PC reads
    /// can be statically rewritten to constant <c>baseAddress + pcOffset</c>
    /// (matches mGBA / Dynarmic dynarec patterns where R15 reads inside
    /// the block become compile-time constants, eliminating the need for
    /// the executor's per-step "pre-set R15" memory write). Per-instr
    /// mode leaves it null so the legacy load-from-GPR[15] path runs.
    /// </summary>
    public void BeginInstruction(EncodingFormat format, InstructionDef def, LLVMValueRef instructionWord, uint? baseAddress = null, byte? lengthBytes = null, int currentInstructionCycleCost = 0, int currentInstructionExtraTakenCycles = 0)
    {
        Format = format;
        Def = def;
        Instruction = instructionWord;
        Values.Clear();
        CurrentInstructionBaseAddress = baseAddress;
        CurrentInstructionLengthBytes = lengthBytes;
        CurrentInstructionCycleCost = currentInstructionCycleCost;
        CurrentInstructionExtraTakenCycles = currentInstructionExtraTakenCycles;
        PcWriteEmittedInCurrentInstruction = false;
    }

    /// <summary>
    /// Phase 7 GB block-JIT P0.7 — when set (block-JIT mode), the t-cycle
    /// cost of the current instruction (parsed from spec cycles.form).
    /// Used by sync-exit IR (Lr35902StoreByteEmitter etc.) to decrement
    /// cycles_left budget before returning, so the host scheduler sees
    /// the correct number of cycles consumed even when block exits
    /// early via sync. 0 means unknown / per-instr mode.
    /// </summary>
    public int CurrentInstructionCycleCost { get; private set; }

    /// <summary>
    /// Phase 7 GB block-JIT P0.7b — extra t-cycles to deduct when a
    /// conditional branch is TAKEN at runtime (parsed from spec
    /// <c>cycles.form</c> like "2m_or_3m" → not_taken=8, taken=12,
    /// extra_taken=4). The base cost <see cref="CurrentInstructionCycleCost"/>
    /// is the not-taken cost; branch_cc/call_cc/ret_cc emitters add
    /// this extra inside their taken-path IR.
    /// 0 means non-conditional or single-cycle-form spec.
    /// </summary>
    public int CurrentInstructionExtraTakenCycles { get; private set; }

    /// <summary>
    /// Phase 7 GB block-JIT P0.4 — when set (block-JIT mode for variable-width
    /// sets), this is the byte length of the current instruction. Drives
    /// <see cref="PipelinePcConstant"/> for variable-width sets where
    /// "what PC value does this instruction's read_pc see" depends on how
    /// many bytes were fetched (1/2/3 for LR35902, where per-instr-mode
    /// read_imm8 + read_imm16 bump PC during execution; block-JIT bakes
    /// those out, so read_pc must compensate by adding the full length
    /// instead of the spec's static pc_offset_bytes).
    ///
    /// Fixed-width sets (ARM/Thumb) leave this null and continue using
    /// <see cref="InstructionSetSpec.PcOffsetBytes"/> via the original
    /// pipeline-quirk path (ARM PC = pc+8 etc.).
    /// </summary>
    public byte? CurrentInstructionLengthBytes { get; private set; }

    /// <summary>
    /// Phase 7 A.6.1 — set by emitters that store into GPR[15] via
    /// <see cref="WriteReg.MarkPcWritten"/>. While true, subsequent
    /// <c>read_reg(15)</c> calls in the SAME instruction must NOT
    /// short-circuit to <see cref="PipelinePcConstant"/> — the popped /
    /// computed PC value must be re-read from memory. Reset by
    /// <see cref="BeginInstruction"/>. Without this, the spec pattern in
    /// Thumb POP {PC}: <c>block_load → if r_bit=1 [read_reg 15; and
    /// 0xFFFFFFFE; write_reg 15]</c> would have the read_reg return the
    /// pipeline constant instead of the just-popped PC, overwriting it
    /// with the static value at the next write_reg. Caused BIOS LLE
    /// divergence: POP {R4,R5,PC} stored bogus aligned-pipeline PC
    /// instead of the popped target.
    /// </summary>
    public bool PcWriteEmittedInCurrentInstruction { get; set; }

    /// <summary>
    /// Phase 7 A.6.1 — when set (block-JIT mode), this is the address of
    /// the instruction currently being emitted (NOT the pipeline-offset
    /// value — that's <see cref="PipelinePcConstant"/>). Null in per-instr
    /// mode where the executor controls PC via real GPR[15] writes.
    /// </summary>
    public uint? CurrentInstructionBaseAddress { get; private set; }

    /// <summary>
    /// Phase 7 A.6.1 — the value an emitter should see when reading the
    /// PC register inside this instruction's body (in block-JIT mode).
    /// Equivalent to what <c>GPR[15]</c> would hold after the executor's
    /// "pre-set R15" pipeline write in per-instr mode. Null in per-instr
    /// mode (callers fall back to actually loading the GPR slot).
    /// </summary>
    public uint? PipelinePcConstant
        => CurrentInstructionBaseAddress is uint pc
           ? pc + (CurrentInstructionLengthBytes is byte len
                ? (uint)len                          // variable-width: pc + actual byte length
                : (uint)InstructionSet.PcOffsetBytes) // fixed-width (ARM): spec's pipeline offset
           : null;
}

/// <summary>
/// One JSON-described condition used by control-flow ops like `if`:
///   <c>{ "field": "s_bit", "eq": 1 }</c> or
///   <c>{ "var": "result",  "eq": 0 }</c>.
/// </summary>
public readonly struct CondExpr
{
    public string Source { get; }   // "field" or "var"
    public string Name   { get; }
    public ulong  EqValue { get; }
    public string Op      { get; }  // "eq" / "ne" / etc.

    public CondExpr(string source, string name, ulong eqValue, string op)
    {
        Source  = source; Name = name; EqValue = eqValue; Op = op;
    }

    public static CondExpr Parse(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Cond must be an object, got {el.ValueKind}");

        string source, name;
        if (el.TryGetProperty("field", out var f)) { source = "field"; name = f.GetString()!; }
        else if (el.TryGetProperty("var", out var v)) { source = "var"; name = v.GetString()!; }
        else throw new InvalidOperationException("Cond requires 'field' or 'var'.");

        if (el.TryGetProperty("eq", out var eqProp))
            return new CondExpr(source, name, eqProp.GetUInt64(), "eq");
        if (el.TryGetProperty("ne", out var neProp))
            return new CondExpr(source, name, neProp.GetUInt64(), "ne");
        throw new InvalidOperationException("Cond requires an 'eq' or 'ne' value.");
    }

    /// <summary>Lower this condition expression to an i1 LLVMValueRef.</summary>
    public LLVMValueRef Lower(EmitContext ctx)
    {
        var lhs = ctx.Resolve(Name);
        var rhs = ctx.ConstU32((uint)EqValue);
        var pred = Op switch
        {
            "eq" => LLVMIntPredicate.LLVMIntEQ,
            "ne" => LLVMIntPredicate.LLVMIntNE,
            _    => throw new InvalidOperationException($"Unknown comparison '{Op}'."),
        };
        return ctx.Builder.BuildICmp(pred, lhs, rhs, $"cond_{Name}_{Op}");
    }
}
