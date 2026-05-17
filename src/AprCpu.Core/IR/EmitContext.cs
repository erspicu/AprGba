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

    /// <summary>
    /// N1.B' — abstraction for "where does a state-slot read/write
    /// resolve?". Default = <see cref="StateBufferProvider"/> (per-instr;
    /// every GEP goes straight into the state struct). Block-JIT mode
    /// swaps in an <see cref="AllocaSlotProvider"/> so emitters' loads/
    /// stores hit function-entry alloca slots that mem2reg promotes to
    /// pure SSA values. See <c>MD/design/18-block-jit-state-abstraction.md</c>.
    ///
    /// Spec emitters never construct or replace this themselves — they
    /// always go through <see cref="GepGpr"/> /
    /// <see cref="GepStatusRegister"/> / <see cref="GepBankedGpr"/>.
    /// <see cref="BlockFunctionBuilder"/> sets it for the block-emit
    /// duration; instruction-level builders leave it null (which means
    /// "use the lazy-built default StateBufferProvider").
    /// </summary>
    public IStateSlotProvider? Slots { get; set; }

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
    public void BeginInstruction(EncodingFormat format, InstructionDef def, LLVMValueRef instructionWord, uint? baseAddress = null, byte? lengthBytes = null, int currentInstructionCycleCost = 0, int currentInstructionExtraTakenCycles = 0, ulong? packedTailBytes = null)
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
        CurrentInstructionImmConsumed = 0;
        CurrentInstructionPackedTailBytes = packedTailBytes;
    }

    /// <summary>
    /// 24.6.8d — pre-fetched trailing-byte payload for the current
    /// instruction (CISC ISAs only; null in per-instr mode and for
    /// fixed-width ISAs). When set, x86 FetchImm8 / FetchImm16 emit a
    /// shift+trunc on this i64 constant instead of a memory_read_8
    /// extern. See <see cref="DecodedBlockInstruction.PackedTailBytes"/>.
    /// </summary>
    public ulong? CurrentInstructionPackedTailBytes { get; private set; }

    /// <summary>
    /// 24.6.8e — when set (block-JIT only, on instructions that
    /// BlockDetector identified as intra-block back-edges), the LLVM
    /// BasicBlock that x86 LOOP / Jcc / JMP rel emitters should branch
    /// to on TAKEN (instead of writing IP + setting PcWritten=1 + exiting
    /// the block). Pair with <see cref="BackEdgeFallthroughBB"/> for the
    /// not-taken side. Reset to null between instructions by
    /// <see cref="BlockFunctionBuilder"/> — emitters check it inside
    /// their Emit() to decide between the existing IP-write path and
    /// the new internal-CFG path.
    /// </summary>
    public LLVMBasicBlockRef? BackEdgeTargetBB { get; set; }

    /// <summary>
    /// 24.6.8e — when <see cref="BackEdgeTargetBB"/> is set, this is the
    /// BB to fall through to on the NOT-TAKEN side of a conditional back-
    /// edge branch (LOOP / Jcc / JCXZ). Typically <c>postBBs[i]</c> set by
    /// <see cref="BlockFunctionBuilder"/> so the existing budget-check +
    /// advance flow runs unmodified. Unconditional back-edges (JMP rel)
    /// ignore this field and emit a single <c>BuildBr(BackEdgeTargetBB)</c>.
    /// </summary>
    public LLVMBasicBlockRef? BackEdgeFallthroughBB { get; set; }

    /// <summary>
    /// 24.6.8d — CISC immediate-baking offset tracker. x86 emitters can
    /// invoke <see cref="X86_16Emitters.FetchImm8"/> / FetchImm16 multiple
    /// times within a single instruction body (e.g. ModR/M byte → disp8 →
    /// imm8 sequence in MOV r/m8, imm8 with [BX+disp]). When block-JIT
    /// has packed the trailing bytes into <see cref="Instruction"/>, each
    /// fetch must extract from a different bit offset; this counter
    /// tracks "how many bytes after the opcode have already been consumed
    /// by prior fetches in THIS instruction." Reset to 0 by
    /// <see cref="BeginInstruction"/>; bumped by 1 / 2 by FetchImm8 /
    /// FetchImm16 in their constant-extraction fast path. Per-instr
    /// (non-block) callers ignore this — they go through the bus path
    /// regardless.
    /// </summary>
    public int CurrentInstructionImmConsumed { get; private set; }

    /// <summary>
    /// 24.6.8d — atomically reserve <paramref name="bytes"/> trailing
    /// bytes for the current FetchImm call and return the OFFSET (in
    /// bytes after the opcode) where extraction should start. Used by
    /// FetchImm8/FetchImm16 fast path to compute shift = (1 + offset) * 8
    /// against <see cref="Instruction"/>.
    /// </summary>
    public int ReserveImmediateBytes(int bytes)
    {
        var offset = CurrentInstructionImmConsumed;
        CurrentInstructionImmConsumed += bytes;
        return offset;
    }

    /// <summary>
    /// Phase 30.15c-B — explicit setter for use by emitters that have
    /// CONDITIONAL switch arms each containing FetchImm calls. Those
    /// emitters save the baseline before each arm, do the emit, then
    /// after the switch end set the counter to the runtime-actual
    /// consumption (= baseline + bytes-the-taken-arm-will-consume).
    /// Without this, IR-emit-order bumps shared by all arms cause
    /// later FetchImm calls to read wrong offsets into packed-tail.
    /// See X86ModRmComputeEaEmitter.
    /// </summary>
    public void SetImmediateConsumed(int value)
    {
        CurrentInstructionImmConsumed = value;
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

    /// <summary>
    /// N1.B' — return the pointer for GPR <paramref name="idx"/> through
    /// the active <see cref="IStateSlotProvider"/>. Per-instr / non-block
    /// callers leave <see cref="Slots"/> null and we fall through to a
    /// direct state-buffer GEP (same IR as before this refactor).
    /// </summary>
    public LLVMValueRef GepGpr(int idx)
        => Slots is { } slots
            ? slots.GprPtr(idx)
            : Layout.GepGpr(Builder, StatePtr, idx);

    /// <summary>
    /// N1.B' — return the pointer for the named status register through
    /// the active <see cref="IStateSlotProvider"/>. Per-instr / non-block
    /// callers leave <see cref="Slots"/> null and we fall through to a
    /// direct state-buffer GEP (same IR as before this refactor).
    /// </summary>
    public LLVMValueRef GepStatusRegister(string name, string? mode = null)
        => Slots is { } slots
            ? slots.StatusRegPtr(name, mode)
            : Layout.GepStatusRegister(Builder, StatePtr, name, mode);

    /// <summary>
    /// N1.B' — return the pointer for a banked GPR through the active
    /// <see cref="IStateSlotProvider"/>. Per-instr / non-block callers
    /// leave <see cref="Slots"/> null and we fall through to a direct
    /// state-buffer GEP.
    /// </summary>
    public LLVMValueRef GepBankedGpr(string mode, int idxInGroup)
        => Slots is { } slots
            ? slots.BankedGprPtr(mode, idxInGroup)
            : Layout.GepBankedGpr(Builder, StatePtr, mode, idxInGroup);

    /// <summary>
    /// N1.B' — flush any provider-managed in-register slot values back
    /// to the live state buffer. Called at every block exit point (block
    /// epilogue, pre-exit branch-taken, budget exit, sync-emitter
    /// mid-block ret) and at any mid-block sync hook. No-op when
    /// <see cref="Slots"/> is the default <see cref="StateBufferProvider"/>
    /// (writes already hit live memory).
    /// </summary>
    public void DrainShadowsToState() => Slots?.SyncToState();
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
