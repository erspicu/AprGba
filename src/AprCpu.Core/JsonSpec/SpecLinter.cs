using System.Text.Json;

namespace AprCpu.Core.JsonSpec;

/// <summary>
/// Phase 30.18n — proactive spec linter that catches common JSON-spec
/// authoring mistakes BEFORE they surface as fuzzer-found block-JIT bugs.
///
/// Background: this session's fuzzer found that GB LDI (0x22 LD (HL+),A)
/// had its step order backwards: store_byte BEFORE the HL increment.
/// When the store targeted an IRQ-relevant IO address, block-JIT's
/// sync-exit fired inside store_byte and the HL update was lost,
/// causing per-instr vs block-JIT divergence (Phase 30.18k).
///
/// The linter walks each loaded InstructionDef and reports patterns
/// that are known to be wrong. Linter warnings are LOGGED (not thrown)
/// so legacy specs that haven't been audited still load — fix them
/// incrementally as the warnings surface.
///
/// Rule families:
///   - PostStoreRegisterUpdate: store_byte/word followed by
///     write_reg_pair_named for the same pair that supplied the
///     store address. (LDI/LDD bug pattern.)
///
/// Future rules to add as they catch more bugs:
///   - Length-table vs spec-step consistency (would have caught
///     GB 0x08 = LD (a16),SP wrongly tabled as 1 byte)
///   - HALT/STOP step semantics matching writes_pc / changes_mode
///   - Selector coverage (warn if not all selector values have entry)
/// </summary>
public static class SpecLinter
{
    public sealed record Warning(string Rule, string Where, string Message);

    /// <summary>
    /// Lint a loaded CpuSpec. Returns the list of warnings; caller
    /// chooses what to do with them (log, throw, ignore).
    /// </summary>
    public static List<Warning> Lint(LoadedSpec spec)
    {
        var warnings = new List<Warning>();
        foreach (var (setName, set) in spec.InstructionSets)
        {
            foreach (var group in set.EncodingGroups)
            foreach (var format in group.Formats)
            foreach (var instr in format.Instructions)
            {
                var where = $"{setName}/{group.Name}/{format.Name}/{instr.Mnemonic}";
                CheckPostStoreRegisterUpdate(instr, where, warnings);
                CheckHaltStopSemantics(instr, where, warnings);
                CheckEmptyStepsNonNop(instr, where, warnings);
            }
        }
        return warnings;
    }

    /// <summary>
    /// Detects the LDI/LDD bug pattern:
    ///   step[i]    = read_reg_pair_named → "addr" from pair P
    ///   step[i+1]  = store_byte/store_word using "addr"
    ///   step[j>i+1] = write_reg_pair_named to the SAME pair P
    ///
    /// If sync-exit fires inside the store, the post-store write to P
    /// is lost — block-JIT and per-instr diverge.
    /// Fix: reorder so the write_reg_pair_named happens BEFORE the
    /// store_byte, using a snapshot of the original pair value as the
    /// store address.
    /// </summary>
    /// <summary>
    /// HALT and STOP instructions stop CPU execution until an IRQ. They
    /// MUST end the block (writes_pc:"always" or changes_mode:true) so
    /// block-JIT exits to the host scheduler. The block-detector's
    /// HasHaltOrStopStep check (BlockDetector.cs ~line 587) covers the
    /// step-op side, but spec authors sometimes forget to set the
    /// metadata fields too — the metadata is what the JIT IR uses
    /// to emit the right code path.
    /// </summary>
    private static void CheckHaltStopSemantics(
        InstructionDef instr, string where, List<Warning> warnings)
    {
        if (instr.Steps is null) return;
        bool hasHaltOrStop = false;
        foreach (var step in instr.Steps)
            if (step.Op == "halt" || step.Op == "stop") { hasHaltOrStop = true; break; }
        if (!hasHaltOrStop) return;

        bool isMode = instr.ChangesMode;
        bool writesPc = instr.WritesPc == "always";
        if (!isMode && !writesPc)
        {
            warnings.Add(new Warning(
                Rule: "HaltStopMetadata",
                Where: where,
                Message: $"instruction has op=\"halt\"/\"stop\" step but neither " +
                         $"changes_mode:true nor writes_pc:\"always\" is set. Without " +
                         $"these the block-detector may not end the block at this " +
                         $"instruction; block-JIT then runs PAST the halt, silently " +
                         $"corrupting state. Set changes_mode:true."));
        }
    }

    /// <summary>
    /// Warn on instructions whose mnemonic is non-trivial but steps is
    /// empty — typically a typo or in-progress entry. NOP / STOP / HALT
    /// are legitimately empty (NOP does nothing; HALT/STOP have steps
    /// via the op field). Anything else with empty steps should declare
    /// at least one step OR be marked as `unconditional:true` no-op.
    /// </summary>
    private static void CheckEmptyStepsNonNop(
        InstructionDef instr, string where, List<Warning> warnings)
    {
        if (instr.Steps is null || instr.Steps.Count > 0) return;
        if (instr.Mnemonic == "NOP" || instr.Mnemonic == "STOP" || instr.Mnemonic == "HALT")
            return;
        // FPU no-op stubs are intentionally empty; recognise common ones.
        if (instr.Mnemonic == "FPU_NOOP" || instr.Mnemonic == "ESCAPE")
            return;
        // Instructions that switch instruction set (GB CB-prefix, ARM BX
        // when changing T-bit) are legitimately empty — the decoder /
        // host dispatch handles the actual work.
        if (instr.SwitchesInstructionSet) return;
        warnings.Add(new Warning(
            Rule: "EmptyStepsNonNop",
            Where: where,
            Message: $"instruction has zero steps but mnemonic '{instr.Mnemonic}' is not " +
                     $"a known no-op (NOP/STOP/HALT). Likely an in-progress spec entry."));
    }

    private static void CheckPostStoreRegisterUpdate(
        InstructionDef instr, string where, List<Warning> warnings)
    {
        var steps = instr.Steps;
        if (steps is null || steps.Count < 3) return;

        // Build map: var name → pair name it was read from.
        var addrVarToPair = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Op == "read_reg_pair_named")
            {
                var raw = steps[i].Raw;
                if (raw.TryGetProperty("name", out var nameEl) &&
                    raw.TryGetProperty("out", out var outEl) &&
                    nameEl.ValueKind == JsonValueKind.String &&
                    outEl.ValueKind == JsonValueKind.String)
                {
                    addrVarToPair[outEl.GetString()!] = nameEl.GetString()!;
                }
            }
        }

        // Scan for store followed by write to the source pair.
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].Op != "store_byte" && steps[i].Op != "store_word") continue;
            var raw = steps[i].Raw;
            if (!raw.TryGetProperty("address", out var addrEl) ||
                addrEl.ValueKind != JsonValueKind.String) continue;
            var addrVar = addrEl.GetString()!;
            if (!addrVarToPair.TryGetValue(addrVar, out var pair)) continue;

            // Look for a later write_reg_pair_named to the same pair.
            for (int j = i + 1; j < steps.Count; j++)
            {
                if (steps[j].Op != "write_reg_pair_named") continue;
                var jraw = steps[j].Raw;
                if (!jraw.TryGetProperty("name", out var jname) ||
                    jname.ValueKind != JsonValueKind.String) continue;
                if (jname.GetString() != pair) continue;

                warnings.Add(new Warning(
                    Rule: "PostStoreRegisterUpdate",
                    Where: where,
                    Message: $"step[{i}] {steps[i].Op} with address from pair '{pair}', " +
                             $"then step[{j}] write_reg_pair_named to same pair '{pair}'. " +
                             $"Block-JIT sync-exit inside the store will lose this update. " +
                             $"Reorder so the pair update happens BEFORE the store (snapshot " +
                             $"the original value for the store address)."));
                break;
            }
        }
    }
}
