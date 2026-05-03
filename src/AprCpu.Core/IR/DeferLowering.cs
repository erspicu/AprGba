using System.Text.Json;
using AprCpu.Core.JsonSpec;
using AprCpu.Core.Runtime;

namespace AprCpu.Core.IR;

/// <summary>
/// Phase 7 GB block-JIT P0.6 — generic delayed-effect mechanism.
///
/// JSON spec syntax:
/// <code>
/// {
///   "op": "defer",
///   "delay_type": "instruction_count",
///   "delay_value": 1,
///   "body": [ ...nested steps... ]
/// }
/// </code>
///
/// Block-JIT lowering pattern: phantom-instruction-injection. Before
/// emitting LLVM IR for a block, walk the instruction list, track
/// pending deferred bodies (compile-time list of (remaining_delay,
/// body_steps)), and inject expired bodies into the target instruction's
/// step list at the front. Strip defer wrappers from the final emit.
/// Result: zero runtime cost — the deferred body becomes plain inline
/// IR within the target instruction. (Per Gemini consultation
/// 2026-05-03 / message 20260503_220938; same pattern as MIPS branch
/// delay slot reordering in dynarecs like Ares.)
///
/// Edge case — defer extends past block end: V1 emits a "serialize"
/// step that writes to <see cref="CpuStateLayout.PendingDeferredFlagsFieldIndex"/>
/// (Phase 7 GB block-JIT P0.6 step 3). Block preamble checks the slot
/// + fast-path-fires expired actions. V1 limited to single deferred
/// action ID per CPU; V2 expands.
///
/// See <c>MD/design/13-defer-microop.md</c> for full design.
/// </summary>
internal static class DeferLowering
{
    /// <summary>
    /// Process a block's instruction list and produce the mutated list
    /// where defer wrappers have been resolved into phantom-injected
    /// steps. Returns the list ready for plain LLVM emit.
    ///
    /// <para>If <paramref name="pendingActionsAtBlockExit"/> is non-empty
    /// after processing, the caller (BlockFunctionBuilder) must emit
    /// IR that serializes those actions into the
    /// <c>pending_deferred_flags</c> state slot at block exit.</para>
    /// </summary>
    public static IReadOnlyList<DecodedBlockInstructionWithSteps> Lower(
        IReadOnlyList<DecodedBlockInstruction> input,
        out List<PendingDeferredAction> pendingActionsAtBlockExit)
    {
        var output = new List<DecodedBlockInstructionWithSteps>(input.Count);
        var pending = new List<PendingDeferredAction>();

        foreach (var instr in input)
        {
            // Decrement all pending delays. Anything that hits 0 (or
            // less, defensively) gets queued for APPEND after this
            // instruction's own steps complete. (Append, not prepend:
            // EI's "IME=1 after the next instruction" means the body
            // runs at the END of the next instruction, so subsequent
            // logic sees IME=1 but THIS instruction's body executes
            // with the pre-defer state. Same for any other 1-instr-delay
            // semantic.)
            var appendBodies = new List<MicroOpStep>();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                pending[i] = pending[i] with { RemainingDelay = pending[i].RemainingDelay - 1 };
                if (pending[i].RemainingDelay <= 0)
                {
                    appendBodies.AddRange(pending[i].Body);
                    pending.RemoveAt(i);
                }
            }

            // Strip any defer wrappers from this instruction's own steps,
            // pushing their bodies onto pending. The defer wrapper itself
            // does NOT produce IR.
            var emittedSteps = new List<MicroOpStep>(instr.Decoded.Instruction.Steps.Count);
            foreach (var step in instr.Decoded.Instruction.Steps)
            {
                if (step.Op == "defer")
                {
                    var parsed = DeferStep.Parse(step);
                    pending.Add(new PendingDeferredAction(
                        RemainingDelay: parsed.DelayValue,
                        Body: parsed.Body,
                        ActionId: parsed.ActionId));
                }
                else
                {
                    emittedSteps.Add(step);
                }
            }
            // Append any expired bodies AFTER this instruction's own
            // steps — preserving the LR35902 EI semantic that IME=1 takes
            // effect AFTER the delay'd instruction has completed.
            emittedSteps.AddRange(appendBodies);

            output.Add(new DecodedBlockInstructionWithSteps(instr, emittedSteps));
        }

        pendingActionsAtBlockExit = pending;
        return output;
    }
}

/// <summary>
/// One in-flight deferred action tracked by the AST pre-pass.
/// Compile-time only — never appears in runtime CPU state.
/// </summary>
internal sealed record PendingDeferredAction(
    int RemainingDelay,
    IReadOnlyList<MicroOpStep> Body,
    int ActionId);

/// <summary>
/// A decoded block instruction paired with its post-defer-lowering step
/// list. The original <see cref="DecodedBlockInstruction.Decoded"/> is
/// preserved for metadata access (Format, Cycles, etc.) but the steps
/// to actually emit come from <see cref="EmittedSteps"/>.
/// </summary>
internal sealed record DecodedBlockInstructionWithSteps(
    DecodedBlockInstruction Original,
    IReadOnlyList<MicroOpStep> EmittedSteps);

/// <summary>
/// Parsed view of a single <c>{"op":"defer", ...}</c> step.
///
/// <para><b>delay_type</b> (V1): only <c>"instruction_count"</c> is
/// supported. V2 may add <c>"branch_taken"</c>,
/// <c>"cycle_count"</c>, <c>"until_condition"</c>.</para>
///
/// <para><b>action_id</b> (optional, V1 default 0): ID for the cross-
/// block fallback. The pending_deferred_flags state slot is a bitmap
/// indexed by action_id. V1 supports 0..31 (i32 slot). LR35902 EI's
/// IME-pending uses action_id 0 by convention.</para>
/// </summary>
internal sealed record DeferStep(
    string DelayType,
    int DelayValue,
    int ActionId,
    IReadOnlyList<MicroOpStep> Body)
{
    public static DeferStep Parse(MicroOpStep step)
    {
        if (step.Op != "defer")
            throw new InvalidOperationException(
                $"DeferStep.Parse expected op='defer', got '{step.Op}'.");

        var delayType  = step.Raw.GetProperty("delay_type").GetString()
            ?? throw new InvalidOperationException("defer.delay_type missing");
        if (delayType != "instruction_count")
            throw new NotSupportedException(
                $"defer.delay_type='{delayType}' not supported in V1 — only 'instruction_count'.");

        var delayValue = step.Raw.GetProperty("delay_value").GetInt32();
        if (delayValue < 1)
            throw new InvalidOperationException(
                $"defer.delay_value must be >= 1, got {delayValue}.");

        var actionId = step.Raw.TryGetProperty("action_id", out var aIdEl)
            ? aIdEl.GetInt32()
            : 0;
        if (actionId is < 0 or > 31)
            throw new InvalidOperationException(
                $"defer.action_id must be in 0..31, got {actionId}.");

        var bodyEl = step.Raw.GetProperty("body");
        if (bodyEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("defer.body must be a JSON array of step objects.");

        var body = new List<MicroOpStep>(bodyEl.GetArrayLength());
        foreach (var subEl in bodyEl.EnumerateArray())
        {
            var subOp = subEl.GetProperty("op").GetString()
                ?? throw new InvalidOperationException("defer.body[].op missing");
            body.Add(new MicroOpStep(subOp, subEl.Clone()));
        }

        return new DeferStep(delayType, delayValue, actionId, body);
    }
}
