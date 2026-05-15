using System.Globalization;
using System.Text.Json;

namespace AprCpu.Core.JsonSpec;

/// <summary>
/// The result of loading a CPU model file: the cpu.json plus every
/// instruction-set file it references, indexed by set name.
/// </summary>
public sealed record LoadedSpec(
    string CpuFilePath,
    CpuSpec Cpu,
    IReadOnlyDictionary<string, InstructionSetSpec> InstructionSets);

/// <summary>
/// Reads spec JSON files into the typed POCO model. No semantic validation
/// beyond shape-required fields and obvious type mismatches; deeper checks
/// (mask/match consistency, micro-op vocabulary lookup) live downstream.
/// </summary>
public static class SpecLoader
{
    private static readonly JsonDocumentOptions DocOpts = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Read <paramref name="path"/>, run the <c>$include</c> resolver,
    /// then parse the assembled JSON into a <see cref="JsonDocument"/>.
    /// </summary>
    private static JsonDocument LoadAndResolveDocument(string path)
    {
        var raw = File.ReadAllText(path);
        var node = System.Text.Json.Nodes.JsonNode.Parse(raw)
            ?? throw new SpecValidationException("File parsed to null.", path);
        var resolved = IncludeResolver.Resolve(node, path);
        var assembled = resolved?.ToJsonString()
            ?? throw new SpecValidationException("Include resolution produced null.", path);
        return JsonDocument.Parse(assembled, DocOpts);
    }

    /// <summary>Load a cpu.json plus all its referenced instruction-set files.
    ///
    /// <para>25.1 — when <c>architecture.extends</c> is non-null, the parent
    /// spec is resolved via <c>architecture.extends_path</c> (relative to
    /// the current cpu.json) and recursively loaded. The child's
    /// <c>instruction_set_diff</c> is then applied: additions add new
    /// instructions, overrides patch existing instructions (RFC 7386
    /// JSON Merge Patch by ID), removals delete instructions by ID.
    /// Cycles in the chain throw; chain depth &gt; 4 throws.</para>
    /// </summary>
    public static LoadedSpec LoadCpuSpec(string cpuJsonPath)
        => LoadCpuSpecInternal(cpuJsonPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), depth: 0);

    /// <summary>
    /// Maximum inheritance chain depth. Per Gemini's 2026-05-09 review:
    /// deep trees become anti-patterns; capped at 4 (e.g.
    /// i80286 → i80186 → i8086 → _base_808x is 4 hops).
    /// </summary>
    public const int MaxInheritanceDepth = 4;

    /// <summary>
    /// N29.1 — load a CPU spec PLUS one or more coprocessor / ISA-extension
    /// spec files, merging the extensions' instruction groups (and, in
    /// Phase 29.2+, register additions) into the base CPU's instruction
    /// sets. Returns a single unified <see cref="LoadedSpec"/> that the
    /// SpecCompiler can feed into a single LLVM module — matching the
    /// QEMU TCG / Bochs / 86Box approach (separate JSON files, unified
    /// runtime data model). See <c>MD/design/29-x87-fpu-plan.md</c>.
    ///
    /// Extension file shape (minimal Phase 29.1 schema):
    /// <code>
    /// {
    ///   "name": "i8087",
    ///   "extends_cpu": "Intel8086",
    ///   "instruction_set_additions": {
    ///     "Main": { "encoding_groups": [ { "$include": "groups/fpu-esc.json" }, ... ] }
    ///   }
    /// }
    /// </code>
    /// Each extension's encoding groups are PREPENDED to the matching
    /// base instruction set's <c>EncodingGroups[]</c> so DecoderTable's
    /// mask-match priority sees them BEFORE base patterns (same convention
    /// as 25.1 inheritance additions).
    ///
    /// Empty / null <paramref name="extensionPaths"/> behaves identically
    /// to <see cref="LoadCpuSpec(string)"/>.
    /// </summary>
    public static LoadedSpec LoadCpuSpecWithExtensions(string cpuJsonPath, IReadOnlyList<string>? extensionPaths)
    {
        var loaded = LoadCpuSpec(cpuJsonPath);
        if (extensionPaths is null || extensionPaths.Count == 0)
            return loaded;

        var mergedSets = new Dictionary<string, InstructionSetSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, s) in loaded.InstructionSets) mergedSets[n] = s;

        // N29.2 — accumulate register additions across all extensions, then
        // splice into the base RegisterFile.Status[] at the end. Doing this
        // in one pass keeps the merged CpuSpec immutable-rebuild simple
        // (rather than mutating across each loop iteration).
        var statusAdditions = new List<StatusRegister>();
        var seenStatusNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in loaded.Cpu.RegisterFile.Status) seenStatusNames.Add(s.Name);

        foreach (var extPath in extensionPaths)
        {
            var fullExtPath = Path.GetFullPath(extPath);
            if (!File.Exists(fullExtPath))
                throw new SpecValidationException(
                    $"Extension spec file not found: {fullExtPath}",
                    fullExtPath);

            using var extDoc = LoadAndResolveDocument(fullExtPath);
            var extRoot = extDoc.RootElement;

            // Sanity check: extension's extends_cpu (if declared) must
            // match the loaded CPU's architecture id. Catches misconfigs
            // like attaching an x87 extension to an ARM7TDMI.
            if (extRoot.TryGetProperty("extends_cpu", out var ecEl)
                && ecEl.ValueKind == JsonValueKind.String)
            {
                var ec = ecEl.GetString();
                if (!string.IsNullOrEmpty(ec)
                    && !string.Equals(ec, loaded.Cpu.Architecture.Id, StringComparison.Ordinal))
                {
                    throw new SpecValidationException(
                        $"Extension '{fullExtPath}' declares extends_cpu='{ec}' but " +
                        $"target CPU is '{loaded.Cpu.Architecture.Id}'. " +
                        $"Extensions can only attach to their declared base CPU.",
                        fullExtPath, "$.extends_cpu");
                }
            }

            var extName = extRoot.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? "<unnamed>" : "<unnamed>";

            // N29.2 — register_file_additions.status: extension contributes
            // its own status registers (e.g. x87 ST0-ST7, FPU_CW, FPU_SW,
            // FPU_TOP, FPU_TAGS). These appear in the merged CpuStateLayout
            // after the base CPU's own status regs, in extension-declaration
            // order. Phase 29.2 only handles .status (no banked_per_mode);
            // pure x87 doesn't need per-mode banking. Phase 29.3+ FPU
            // emitters access these slots via the same StatusOffset path
            // as integer status regs, bitcasting i64 ↔ f64 for arithmetic.
            if (extRoot.TryGetProperty("register_file_additions", out var rfAddEl)
                && rfAddEl.ValueKind == JsonValueKind.Object
                && rfAddEl.TryGetProperty("status", out var rfStatusEl)
                && rfStatusEl.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var s in rfStatusEl.EnumerateArray())
                {
                    var added = ParseStatusRegister(s, fullExtPath,
                        $"$.register_file_additions.status[{idx}]");
                    if (!seenStatusNames.Add(added.Name))
                        throw new SpecValidationException(
                            $"Extension '{extName}' adds status register '{added.Name}' " +
                            $"which already exists in base CPU '{loaded.Cpu.Architecture.Id}' " +
                            $"or in a previously-merged extension.",
                            fullExtPath, $"$.register_file_additions.status[{idx}].name");
                    statusAdditions.Add(added);
                    idx++;
                }
            }

            if (!extRoot.TryGetProperty("instruction_set_additions", out var addEl))
                continue;  // Pure register-only extensions are fine — just no opcode groups to merge.
            if (addEl.ValueKind != JsonValueKind.Object)
                throw new SpecValidationException(
                    "'instruction_set_additions' must be an object keyed by instruction-set name.",
                    fullExtPath, "$.instruction_set_additions");

            foreach (var setEntry in addEl.EnumerateObject())
            {
                var setName = setEntry.Name;
                if (!mergedSets.TryGetValue(setName, out var baseSet))
                    throw new SpecValidationException(
                        $"Extension '{extName}' targets instruction set '{setName}' which is not present in base CPU '{loaded.Cpu.Architecture.Id}'.",
                        fullExtPath, $"$.instruction_set_additions.{setName}");

                if (!setEntry.Value.TryGetProperty("encoding_groups", out var groupsEl)
                    || groupsEl.ValueKind != JsonValueKind.Array)
                    throw new SpecValidationException(
                        $"instruction_set_additions['{setName}'] must contain an 'encoding_groups' array.",
                        fullExtPath, $"$.instruction_set_additions.{setName}.encoding_groups");

                var addedGroups = new List<EncodingGroup>();
                int groupIdx = 0;
                foreach (var gEl in groupsEl.EnumerateArray())
                {
                    var parsed = ParseEncodingGroup(gEl, baseSet.WidthBits, fullExtPath,
                        $"$.instruction_set_additions.{setName}.encoding_groups[{groupIdx}]");
                    // Tag each instruction with OriginCpu = extension name so
                    // diagnostics / dumps can trace provenance.
                    var taggedFormats = parsed.Formats.Select(f =>
                        f with { Instructions = f.Instructions.Select(i =>
                            i with { OriginCpu = i.OriginCpu ?? extName }).ToList() }).ToList();
                    addedGroups.Add(parsed with { Formats = taggedFormats });
                    groupIdx++;
                }

                // Prepend additions so DecoderTable sees more-specific
                // extension masks BEFORE base patterns (matches 25.1
                // inheritance addition ordering).
                var newGroups = new List<EncodingGroup>(addedGroups.Count + baseSet.EncodingGroups.Count);
                newGroups.AddRange(addedGroups);
                newGroups.AddRange(baseSet.EncodingGroups);
                mergedSets[setName] = baseSet with { EncodingGroups = newGroups };
            }
        }

        // N29.2 — splice extension status registers onto the base CPU's
        // RegisterFile.Status[]. Appended (not prepended) so existing base
        // status-register slot offsets remain stable — every consumer of
        // status[i] indexing (X86JsonCpu's pre-cached FLAGS/IP/CS/etc.
        // offsets) keeps working without rebuild.
        var mergedCpu = loaded.Cpu;
        if (statusAdditions.Count > 0)
        {
            var newStatus = new List<StatusRegister>(
                loaded.Cpu.RegisterFile.Status.Count + statusAdditions.Count);
            newStatus.AddRange(loaded.Cpu.RegisterFile.Status);
            newStatus.AddRange(statusAdditions);
            mergedCpu = loaded.Cpu with
            {
                RegisterFile = loaded.Cpu.RegisterFile with { Status = newStatus }
            };
        }

        return loaded with { Cpu = mergedCpu, InstructionSets = mergedSets };
    }

    private static LoadedSpec LoadCpuSpecInternal(string cpuJsonPath, HashSet<string> inProgress, int depth)
    {
        var fullPath = Path.GetFullPath(cpuJsonPath);
        if (!File.Exists(fullPath))
            throw new SpecValidationException("File not found.", fullPath);

        if (depth > MaxInheritanceDepth)
            throw new SpecValidationException(
                $"Inheritance chain exceeds maximum depth of {MaxInheritanceDepth}.",
                fullPath);

        if (!inProgress.Add(fullPath))
            throw new SpecValidationException(
                $"Cyclic inheritance detected — '{fullPath}' is already being resolved.",
                fullPath);

        try
        {
            using var doc = LoadAndResolveDocument(fullPath);
            var childCpu = ParseCpuSpec(doc.RootElement, fullPath);

            var dir = Path.GetDirectoryName(fullPath)!;

            // Base case: no parent. Load instruction sets from disk and return.
            if (childCpu.Architecture.Extends is null)
            {
                if (childCpu.Architecture.ExtendsPath is not null)
                    throw new SpecValidationException(
                        "architecture.extends_path is set but architecture.extends is null — both must be set together.",
                        fullPath, "$.architecture.extends");
                if (childCpu.InstructionSetDiff is not null)
                    throw new SpecValidationException(
                        "instruction_set_diff is set but architecture.extends is null — diff only applies to child specs.",
                        fullPath, "$.instruction_set_diff");

                return LoadBaseCpuSpec(fullPath, childCpu, dir);
            }

            // Inheriting case: resolve parent + apply diff.
            if (childCpu.Architecture.ExtendsPath is null)
                throw new SpecValidationException(
                    $"architecture.extends is '{childCpu.Architecture.Extends}' but extends_path is null — explicit relative path is required.",
                    fullPath, "$.architecture.extends_path");

            var parentPath = Path.GetFullPath(Path.Combine(dir, childCpu.Architecture.ExtendsPath));
            var parent = LoadCpuSpecInternal(parentPath, inProgress, depth + 1);

            // Sanity check: parent's id must match child's declared `extends`.
            if (!string.Equals(parent.Cpu.Architecture.Id, childCpu.Architecture.Extends, StringComparison.Ordinal))
                throw new SpecValidationException(
                    $"architecture.extends declares parent id '{childCpu.Architecture.Extends}' but parent at '{childCpu.Architecture.ExtendsPath}' has id '{parent.Cpu.Architecture.Id}'.",
                    fullPath, "$.architecture.extends");

            var mergedSets = ApplyInstructionSetDiff(parent, childCpu, fullPath);

            // 25.1 — inherit register_file / exception_vectors from parent
            // when child uses the placeholder (empty placeholder = "I want
            // to inherit"). Child's own values take precedence when set.
            var mergedCpu = childCpu;
            if (mergedCpu.RegisterFile.GeneralPurpose.Count == 0
                && mergedCpu.RegisterFile.Status.Count == 0)
            {
                mergedCpu = mergedCpu with { RegisterFile = parent.Cpu.RegisterFile };
            }
            if (mergedCpu.ExceptionVectors.Count == 0)
            {
                mergedCpu = mergedCpu with { ExceptionVectors = parent.Cpu.ExceptionVectors };
            }
            // Inherit instruction_sets ref list too (so callers see the
            // same set names parent declared, even if child didn't list them).
            if (mergedCpu.InstructionSets.Count == 0)
            {
                mergedCpu = mergedCpu with { InstructionSets = parent.Cpu.InstructionSets };
            }
            return new LoadedSpec(fullPath, mergedCpu, mergedSets);
        }
        finally
        {
            inProgress.Remove(fullPath);
        }
    }

    private static LoadedSpec LoadBaseCpuSpec(string fullPath, CpuSpec cpu, string dir)
    {
        var sets = new Dictionary<string, InstructionSetSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var setRef in cpu.InstructionSets)
        {
            var setPath = Path.GetFullPath(Path.Combine(dir, setRef.File));
            if (!File.Exists(setPath))
            {
                throw new SpecValidationException(
                    $"Referenced instruction-set file '{setRef.File}' not found.",
                    fullPath, $"$.instruction_sets[?(@.name=='{setRef.Name}')].file");
            }
            using var setDoc = LoadAndResolveDocument(setPath);
            var set = ParseInstructionSetSpec(setDoc.RootElement, setPath);
            SpecValidator.ValidateInstructionSet(set);
            // 25.1.4 — tag every instruction in a base spec with OriginCpu so
            // a future override can reference where it came from.
            set = TagInstructionsWithOrigin(set, cpu.Architecture.Id);
            sets[setRef.Name] = set;
        }
        return new LoadedSpec(fullPath, cpu, sets);
    }

    /// <summary>
    /// 25.1 — apply child's <c>instruction_set_diff</c> over parent's loaded
    /// instruction sets. Returns a fresh dictionary of merged instruction
    /// sets. Parent's sets that aren't mentioned in the diff pass through
    /// unchanged (still tagged with parent's OriginCpu).
    /// </summary>
    private static IReadOnlyDictionary<string, InstructionSetSpec> ApplyInstructionSetDiff(
        LoadedSpec parent, CpuSpec child, string childFilePath)
    {
        var result = new Dictionary<string, InstructionSetSpec>(StringComparer.OrdinalIgnoreCase);
        // Start with parent's sets (already tagged with origin).
        foreach (var (name, parentSet) in parent.InstructionSets)
            result[name] = parentSet;

        if (child.InstructionSetDiff is null) return result;

        foreach (var (setName, diff) in child.InstructionSetDiff.PerSet)
        {
            if (!result.TryGetValue(setName, out var parentSet))
                throw new SpecValidationException(
                    $"instruction_set_diff references set '{setName}' which is not present in parent spec.",
                    childFilePath, $"$.instruction_set_diff.{setName}");

            var merged = ApplyDiffToSet(parentSet, diff, child.Architecture.Id, childFilePath, setName);
            result[setName] = merged;
        }

        // 26.2b — load + add entirely new instruction sets the child
        // introduces (parent didn't have them). Each one's file is
        // resolved relative to the child cpu.json's directory.
        var childDir = Path.GetDirectoryName(childFilePath)!;
        foreach (var addedRef in child.InstructionSetDiff.InstructionSetsAdded)
        {
            if (result.ContainsKey(addedRef.Name))
                throw new SpecValidationException(
                    $"instruction_sets_added['{addedRef.Name}'] collides with an existing set inherited from parent.",
                    childFilePath, $"$.instruction_set_diff.instruction_sets_added");

            var setPath = Path.GetFullPath(Path.Combine(childDir, addedRef.File));
            if (!File.Exists(setPath))
                throw new SpecValidationException(
                    $"instruction_sets_added references file '{addedRef.File}' which doesn't exist (resolved to {setPath}).",
                    childFilePath, $"$.instruction_set_diff.instruction_sets_added");

            using var setDoc = LoadAndResolveDocument(setPath);
            var newSet = ParseInstructionSetSpec(setDoc.RootElement, setPath);
            SpecValidator.ValidateInstructionSet(newSet);
            // Tag added-set instructions with child's OriginCpu for provenance.
            newSet = TagInstructionsWithOrigin(newSet, child.Architecture.Id);
            result[addedRef.Name] = newSet;
        }

        return result;
    }

    private static InstructionSetSpec ApplyDiffToSet(
        InstructionSetSpec parentSet,
        PerSetDiff diff,
        string childCpuId,
        string childFilePath,
        string setName)
    {
        // Flatten parent instructions to a working list keyed by ID for easy
        // lookup. Instructions without IDs (pre-25.2 retrofit) can't be
        // referenced by overrides/removals — only by additions.
        // Each entry preserves its (groupIndex, formatIndex, instrIndex)
        // for re-emit at the end.
        var workingGroups = parentSet.EncodingGroups.Select(g =>
            new EncodingGroup(g.Name, g.AppliesWhen,
                g.Formats.Select(f => new EncodingFormat(
                    f.Name, f.Comment, f.Pattern, f.Fields, f.Mask, f.Match, f.Operands,
                    f.Instructions.ToList())).ToList())).ToList();

        // ===== removals =====
        foreach (var rmId in diff.Removals)
        {
            bool removed = false;
            foreach (var g in workingGroups)
            {
                foreach (var f in g.Formats)
                {
                    var list = (List<InstructionDef>)f.Instructions;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].Id == rmId) { list.RemoveAt(i); removed = true; break; }
                    }
                    if (removed) break;
                }
                if (removed) break;
            }
            if (!removed)
                throw new SpecValidationException(
                    $"instruction_set_diff['{setName}'].removals references id '{rmId}' which is not present in parent.",
                    childFilePath, $"$.instruction_set_diff.{setName}.removals");
        }

        // ===== overrides =====
        foreach (var (id, partial) in diff.Overrides)
        {
            bool patched = false;
            foreach (var g in workingGroups)
            {
                foreach (var f in g.Formats)
                {
                    var list = (List<InstructionDef>)f.Instructions;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i].Id != id) continue;
                        var parentInstr = list[i];
                        var parentRaw = SerializeInstructionDef(parentInstr);
                        var mergedNode = JsonMergePatch.Apply(parentRaw, System.Text.Json.Nodes.JsonNode.Parse(partial.GetRawText()));
                        if (mergedNode is null)
                            throw new SpecValidationException(
                                $"override of '{id}' produced null result.",
                                childFilePath, $"$.instruction_set_diff.{setName}.overrides.{id}");
                        using var mergedDoc = JsonDocument.Parse(mergedNode.ToJsonString());
                        var mergedInstr = ParseInstructionDef(mergedDoc.RootElement, childFilePath,
                            $"$.instruction_set_diff.{setName}.overrides.{id}");
                        // Preserve OriginCpu (where instruction was first
                        // defined) and tag OverriddenBy = current child.
                        mergedInstr = mergedInstr with
                        {
                            OriginCpu = parentInstr.OriginCpu ?? parentInstr.Mnemonic, // fallback if origin lost
                            OverriddenBy = childCpuId
                        };
                        list[i] = mergedInstr;
                        patched = true;
                        break;
                    }
                    if (patched) break;
                }
                if (patched) break;
            }
            if (!patched)
                throw new SpecValidationException(
                    $"instruction_set_diff['{setName}'].overrides references id '{id}' which is not present in parent.",
                    childFilePath, $"$.instruction_set_diff.{setName}.overrides.{id}");
        }

        // ===== additions =====
        // Additions go into the FIRST format whose mask matches the new
        // instruction's encoding... but we don't have a generic way to
        // assign a new instruction to a format without explicit declaration.
        // For simplicity we put all additions into a synthetic group named
        // "_additions_<childCpuId>" with one format per addition. This
        // preserves the additions' own format declarations from the JSON.
        // Convention: each addition is a full encoding-format-like object
        // with embedded `instructions: [...]`. If the addition is a flat
        // instruction (no format wrapper) we synthesize a format around it
        // using its own `mask` / `match` fields.
        if (diff.AdditionsRaw.Count > 0)
        {
            var addFormats = new List<EncodingFormat>();
            foreach (var raw in diff.AdditionsRaw)
            {
                // The addition is expected to be an EncodingFormat-shaped
                // object (with `instructions`) — i.e. same structure as
                // parent's encoding_groups[*].formats[*].
                var fmt = ParseEncodingFormat(raw, parentSet.WidthBits, childFilePath,
                    $"$.instruction_set_diff.{setName}.additions");
                // Tag every instruction in this addition with OriginCpu = childCpuId.
                var taggedInstrs = fmt.Instructions.Select(i =>
                    i with { OriginCpu = childCpuId }).ToList();
                addFormats.Add(fmt with { Instructions = taggedInstrs });
            }

            // Validate ID uniqueness across parent + additions.
            var parentIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in workingGroups)
                foreach (var f in g.Formats)
                    foreach (var i in f.Instructions)
                        if (i.Id is not null) parentIds.Add(i.Id);

            foreach (var f in addFormats)
                foreach (var i in f.Instructions)
                {
                    if (i.Id is not null && parentIds.Contains(i.Id))
                        throw new SpecValidationException(
                            $"addition '{i.Id}' collides with an existing instruction in parent — use overrides instead.",
                            childFilePath, $"$.instruction_set_diff.{setName}.additions");
                }

            // Insert additions at the FRONT of the encoding_groups list so
            // that DecoderTable evaluates them BEFORE parent formats. This
            // matters when an addition uses a more-specific mask (e.g. the
            // i80186 PUSH_SP override = mask 0xFF / match 0x54) that needs
            // to win over a parent's broader pattern (e.g. i8086
            // PUSH_reg16 = mask 0xF8 / match 0x50). Without prepend the
            // parent's broader pattern would shadow the addition.
            workingGroups.Insert(0, new EncodingGroup(
                Name: $"_additions_{childCpuId}",
                AppliesWhen: null,
                Formats: addFormats));
        }

        // Re-build set with merged groups.
        return parentSet with { EncodingGroups = workingGroups };
    }

    /// <summary>
    /// 25.1.4 — tag every instruction in a freshly-loaded base spec with
    /// <c>OriginCpu = cpuId</c> so future overrides can identify provenance.
    /// </summary>
    private static InstructionSetSpec TagInstructionsWithOrigin(InstructionSetSpec set, string cpuId)
    {
        var groups = set.EncodingGroups.Select(g =>
            g with { Formats = g.Formats.Select(f =>
                f with { Instructions = f.Instructions.Select(i =>
                    i with { OriginCpu = i.OriginCpu ?? cpuId }).ToList() }).ToList() }).ToList();
        return set with { EncodingGroups = groups };
    }

    /// <summary>
    /// 25.1 — re-serialize an InstructionDef back to its JSON shape so
    /// JsonMergePatch can operate on it. Only the fields that ParseInstructionDef
    /// can re-read are written; provenance / Id are preserved.
    /// </summary>
    private static System.Text.Json.Nodes.JsonNode SerializeInstructionDef(InstructionDef instr)
    {
        var obj = new System.Text.Json.Nodes.JsonObject();
        if (instr.Id is not null) obj["id"] = instr.Id;
        obj["mnemonic"] = instr.Mnemonic;
        if (instr.Since is not null) obj["since"] = instr.Since;
        if (instr.Until is not null) obj["until"] = instr.Until;
        if (instr.RequiresFeature is not null) obj["requires_feature"] = instr.RequiresFeature;
        if (instr.Unconditional) obj["unconditional"] = true;
        if (instr.WritesPc is not null) obj["writes_pc"] = instr.WritesPc;
        if (instr.WritesMemory.Count > 0)
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var w in instr.WritesMemory) arr.Add(w);
            obj["writes_memory"] = arr;
        }
        if (instr.ChangesMode) obj["changes_mode"] = true;
        if (instr.SwitchesInstructionSet) obj["switches_instruction_set"] = true;
        if (instr.RequiresIoBarrier) obj["requires_io_barrier"] = true;
        if (instr.Quirks.Count > 0)
        {
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var q in instr.Quirks) arr.Add(q);
            obj["quirks"] = arr;
        }
        if (instr.ManualRef is not null) obj["manual_ref"] = instr.ManualRef;
        if (instr.Cycles is not null)
        {
            var c = new System.Text.Json.Nodes.JsonObject();
            if (instr.Cycles.Form is not null) c["form"] = instr.Cycles.Form;
            obj["cycles"] = c;
        }
        // Steps — serialize each step's Raw element.
        var stepsArr = new System.Text.Json.Nodes.JsonArray();
        foreach (var step in instr.Steps)
            stepsArr.Add(System.Text.Json.Nodes.JsonNode.Parse(step.Raw.GetRawText()));
        obj["steps"] = stepsArr;
        if (instr.Selector is not null)
        {
            var sel = new System.Text.Json.Nodes.JsonObject();
            sel["field"] = instr.Selector.Field;
            sel["value"] = instr.Selector.Value;
            obj["selector"] = sel;
        }
        return obj;
    }

    /// <summary>Load a single instruction-set file directly (testing helper).</summary>
    public static InstructionSetSpec LoadInstructionSet(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new SpecValidationException("File not found.", fullPath);
        using var doc = LoadAndResolveDocument(fullPath);
        var set = ParseInstructionSetSpec(doc.RootElement, fullPath);

        // Run semantic validators (hard errors throw).
        SpecValidator.ValidateInstructionSet(set);

        return set;
    }

    // ---------------- CPU spec ----------------

    private static CpuSpec ParseCpuSpec(JsonElement root, string filePath)
    {
        EnsureObject(root, filePath, "$");

        var specVer  = ReqString(root, "spec_version", filePath, "$.spec_version");
        var arch     = ParseArchitecture(ReqObject(root, "architecture", filePath, "$.architecture"), filePath);
        var variants = ParseList(root, "variants", ParseVariant, filePath, "$.variants");
        // 25.1 — register_file / exception_vectors become optional for child
        // specs (architecture.extends != null). Inheritance resolution will
        // copy them from parent. Base specs still require these fields.
        bool isChild = arch.Extends is not null;
        RegisterFile regFile;
        if (TryGetObject(root, "register_file", out var rfEl))
        {
            regFile = ParseRegisterFile(rfEl, filePath);
        }
        else if (isChild)
        {
            // Empty placeholder — gets replaced by parent's register_file
            // during inheritance resolution.
            regFile = new RegisterFile(
                new GeneralPurposeRegisters(0, 0, Array.Empty<string>(),
                    new Dictionary<string, string>(StringComparer.Ordinal), null),
                Array.Empty<StatusRegister>(),
                Array.Empty<RegisterPair>(),
                null);
        }
        else
        {
            throw new SpecValidationException(
                "register_file is required (or set architecture.extends to inherit from a parent).",
                filePath, "$.register_file");
        }

        var modes    = TryGetObject(root, "processor_modes", out var pm) ? ParseProcessorModes(pm, filePath) : null;
        // 25.1 — exception_vectors also optional for child specs.
        IReadOnlyList<ExceptionVector> vectors;
        if (root.TryGetProperty("exception_vectors", out var evEl) && evEl.ValueKind == JsonValueKind.Array)
        {
            vectors = ParseList(root, "exception_vectors", ParseExceptionVector, filePath, "$.exception_vectors");
        }
        else if (isChild)
        {
            vectors = Array.Empty<ExceptionVector>();
        }
        else
        {
            throw new SpecValidationException(
                "exception_vectors is required (or set architecture.extends to inherit from a parent).",
                filePath, "$.exception_vectors");
        }

        var sets     = ParseList(root, "instruction_sets", ParseInstructionSetRef, filePath, "$.instruction_sets");
        // 25.1 — base specs must declare at least one instruction set; child
        // specs (architecture.extends != null) inherit sets from parent
        // and may legitimately leave instruction_sets empty.
        bool hasExtends = false;
        if (TryGetObject(root, "architecture", out var archEl)
            && archEl.TryGetProperty("extends", out var extEl)
            && extEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(extEl.GetString()))
        {
            hasExtends = true;
        }
        if (sets.Count == 0 && !hasExtends)
            throw new SpecValidationException(
                "instruction_sets must contain at least one entry (or set architecture.extends to inherit from a parent).",
                filePath, "$.instruction_sets");
        var dispatch = TryGetObject(root, "instruction_set_dispatch", out var d) ? ParseDispatch(d, filePath) : null;
        var memory   = TryGetObject(root, "memory_model", out var mm) ? ParseMemoryModel(mm, filePath) : null;
        var custom   = ParseList(root, "custom_micro_ops", ParseCustomMicroOp, filePath, "$.custom_micro_ops");
        var isaMeta  = TryGetObject(root, "isa_metadata", out var im) ? ParseIsaMetadata(im, filePath) : null;

        // 25.1 — optional instruction_set_diff for child specs that
        // extend a parent. Only sane when arch.Extends is non-null;
        // SpecLoader.LoadCpuSpec validates this combination.
        InstructionSetDiff? diff = null;
        if (TryGetObject(root, "instruction_set_diff", out var diffEl))
            diff = ParseInstructionSetDiff(diffEl, filePath);

        return new CpuSpec(specVer, arch, variants, regFile, modes, vectors, sets, dispatch, memory, custom, isaMeta, diff);
    }

    /// <summary>
    /// 25.1 — parse <c>instruction_set_diff</c>. Each top-level key is an
    /// instruction-set name (e.g. "Main"); its value carries optional
    /// <c>additions</c> / <c>overrides</c> / <c>removals</c>.
    ///
    /// 26.2b — also accepts a special <c>instruction_sets_added</c> key
    /// at the top level whose value is an array of new InstructionSetRef
    /// objects (entirely new sets the child introduces, parent doesn't
    /// have them). Used by i80286 to add a TwoByteEsc set for 0x0F-
    /// prefixed instructions.
    /// </summary>
    private static InstructionSetDiff ParseInstructionSetDiff(JsonElement el, string filePath)
    {
        var perSet = new Dictionary<string, PerSetDiff>(StringComparer.Ordinal);
        var setsAdded = new List<InstructionSetRef>();
        foreach (var prop in el.EnumerateObject())
        {
            // Special handling for instruction_sets_added top-level key.
            if (prop.Name == "instruction_sets_added")
            {
                if (prop.Value.ValueKind != JsonValueKind.Array)
                    throw new SpecValidationException(
                        "instruction_set_diff.instruction_sets_added must be an array.",
                        filePath, "$.instruction_set_diff.instruction_sets_added");
                int idx = 0;
                foreach (var refEl in prop.Value.EnumerateArray())
                {
                    setsAdded.Add(ParseInstructionSetRef(refEl, filePath,
                        $"$.instruction_set_diff.instruction_sets_added[{idx}]"));
                    idx++;
                }
                continue;
            }
            var setName = prop.Name;
            var setEl = prop.Value;
            if (setEl.ValueKind != JsonValueKind.Object)
                throw new SpecValidationException(
                    $"instruction_set_diff['{setName}'] must be an object.",
                    filePath, $"$.instruction_set_diff.{setName}");

            // additions: array of raw instruction objects (parsed at merge time
            // through the same ParseInstructionDef path).
            var additions = new List<JsonElement>();
            if (setEl.TryGetProperty("additions", out var addEl)
                && addEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var instr in addEl.EnumerateArray())
                    additions.Add(instr.Clone());
            }

            // overrides: object mapping ID → partial-instruction patch.
            var overrides = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (setEl.TryGetProperty("overrides", out var ovEl)
                && ovEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var ov in ovEl.EnumerateObject())
                    overrides[ov.Name] = ov.Value.Clone();
            }

            // removals: array of ID strings.
            var removals = new List<string>();
            if (setEl.TryGetProperty("removals", out var rmEl)
                && rmEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in rmEl.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.String)
                        throw new SpecValidationException(
                            $"instruction_set_diff['{setName}'].removals must be an array of strings.",
                            filePath, $"$.instruction_set_diff.{setName}.removals");
                    removals.Add(id.GetString()!);
                }
            }

            perSet[setName] = new PerSetDiff(additions, overrides, removals);
        }
        return new InstructionSetDiff(perSet, setsAdded);
    }

    /// <summary>
    /// N2.5 — parse optional <c>isa_metadata</c> section into typed
    /// IsaMetadata. Defaults: cycles_per_spec_unit=4 (GB/ARM m-cycle×4
    /// convention; NES sets to 1 explicitly), pc_update_policy=lazy,
    /// interrupt_check_policy=end_of_block.
    /// </summary>
    private static IsaMetadata ParseIsaMetadata(JsonElement el, string filePath)
    {
        EnsureObject(el, filePath, "$.isa_metadata");
        string? endianness = null;
        if (el.TryGetProperty("endianness", out var en) && en.ValueKind == JsonValueKind.String)
            endianness = en.GetString();
        int cyclesPerUnit = 4;
        if (el.TryGetProperty("cycles_per_spec_unit", out var cpu)
            && cpu.ValueKind == JsonValueKind.Number)
        {
            cyclesPerUnit = cpu.GetInt32();
        }
        string pcPolicy = "lazy";
        if (el.TryGetProperty("pc_update_policy", out var pcp) && pcp.ValueKind == JsonValueKind.String)
            pcPolicy = pcp.GetString() ?? "lazy";
        string irqPolicy = "end_of_block";
        if (el.TryGetProperty("interrupt_check_policy", out var ip) && ip.ValueKind == JsonValueKind.String)
            irqPolicy = ip.GetString() ?? "end_of_block";
        return new IsaMetadata(endianness, cyclesPerUnit, pcPolicy, irqPolicy);
    }

    private static Architecture ParseArchitecture(JsonElement el, string filePath)
    {
        return new Architecture(
            Id:           ReqString(el, "id",     filePath, "$.architecture.id"),
            Family:       ReqString(el, "family", filePath, "$.architecture.family"),
            Extends:      OptString(el, "extends"),
            Endianness:   OptString(el, "endianness") ?? "little",
            WordSizeBits: OptInt(el, "word_size_bits") ?? 32,
            ExtendsPath:  OptString(el, "extends_path"));
    }

    private static CpuVariant ParseVariant(JsonElement el, string filePath, string jsonPath)
    {
        return new CpuVariant(
            Id:       ReqString(el, "id", filePath, jsonPath + ".id"),
            Core:     OptString(el, "core"),
            Features: ParseStringList(el, "features"),
            Notes:    OptString(el, "notes"));
    }

    private static RegisterFile ParseRegisterFile(JsonElement el, string filePath)
    {
        var gp = ReqObject(el, "general_purpose", filePath, "$.register_file.general_purpose");
        var status = ParseList(el, "status", ParseStatusRegister, filePath, "$.register_file.status");

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        if (gp.TryGetProperty("aliases", out var al))
        {
            foreach (var prop in al.EnumerateObject())
                aliases[prop.Name] = prop.Value.GetString() ?? "";
        }

        var generalPurpose = new GeneralPurposeRegisters(
            Count:      ReqInt(gp, "count",      filePath, "$.register_file.general_purpose.count"),
            WidthBits:  ReqInt(gp, "width_bits", filePath, "$.register_file.general_purpose.width_bits"),
            Names:      ParseStringList(gp, "names"),
            Aliases:    aliases,
            PcIndex:    OptInt(gp, "pc_index"));

        var pairs = new List<RegisterPair>();
        if (el.TryGetProperty("register_pairs", out var rp) && rp.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (var item in rp.EnumerateArray())
            {
                var jp = $"$.register_file.register_pairs[{idx}]";
                pairs.Add(new RegisterPair(
                    Name: ReqString(item, "name", filePath, $"{jp}.name"),
                    High: ReqString(item, "high", filePath, $"{jp}.high"),
                    Low:  ReqString(item, "low",  filePath, $"{jp}.low")));
                idx++;
            }
        }

        // Optional stack pointer reference. Two forms:
        //   "stack_pointer": "SP"                            (status reg by name)
        //   "stack_pointer": { "gpr_index": 13 }              (GPR by index)
        // Used by generic push/pop/call/ret emitters.
        StackPointerRef? sp = null;
        if (el.TryGetProperty("stack_pointer", out var spEl))
        {
            if (spEl.ValueKind == JsonValueKind.String)
                sp = new StackPointerRef(GprIndex: null, StatusName: spEl.GetString());
            else if (spEl.ValueKind == JsonValueKind.Object && spEl.TryGetProperty("gpr_index", out var gi))
                sp = new StackPointerRef(GprIndex: gi.GetInt32(), StatusName: null);
            else if (spEl.ValueKind == JsonValueKind.Object && spEl.TryGetProperty("status_name", out var sn))
                sp = new StackPointerRef(GprIndex: null, StatusName: sn.GetString());
        }

        return new RegisterFile(generalPurpose, status, pairs, sp);
    }

    private static StatusRegister ParseStatusRegister(JsonElement el, string filePath, string jsonPath)
    {
        var fields = new Dictionary<string, BitRange>(StringComparer.Ordinal);
        if (el.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in f.EnumerateObject())
                fields[prop.Name] = ParseBitRange(prop.Value, filePath, $"{jsonPath}.fields.{prop.Name}");
        }
        var banked = ParseStringList(el, "banked_per_mode");
        return new StatusRegister(
            Name:           ReqString(el, "name",       filePath, $"{jsonPath}.name"),
            WidthBits:      ReqInt   (el, "width_bits", filePath, $"{jsonPath}.width_bits"),
            Fields:         fields,
            BankedPerMode:  banked);
    }

    private static ProcessorModes ParseProcessorModes(JsonElement el, string filePath)
    {
        var modes = ParseList(el, "modes", (m, fp, jp) => new ProcessorMode(
            Id:         ReqString(m, "id", fp, $"{jp}.id"),
            Encoding:   OptString(m, "encoding"),
            Privileged: OptBool(m, "privileged") ?? false), filePath, "$.processor_modes.modes");

        var banked = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (el.TryGetProperty("banked_registers", out var br) && br.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in br.EnumerateObject())
            {
                var list = new List<string>();
                foreach (var item in prop.Value.EnumerateArray())
                    list.Add(item.GetString() ?? "");
                banked[prop.Name] = list;
            }
        }
        return new ProcessorModes(modes, banked);
    }

    private static ExceptionVector ParseExceptionVector(JsonElement el, string filePath, string jsonPath)
    {
        var addrStr = ReqString(el, "address", filePath, $"{jsonPath}.address");
        var addr = ParseHexU32(addrStr, filePath, $"{jsonPath}.address");
        var disable = ParseStringList(el, "disable");
        return new ExceptionVector(
            Name:         ReqString(el, "name", filePath, $"{jsonPath}.name"),
            Address:      addr,
            EnterMode:    OptString(el, "enter_mode"),
            DisableFlags: disable);
    }

    private static InstructionSetRef ParseInstructionSetRef(JsonElement el, string filePath, string jsonPath)
    {
        InstructionSetExtends? ext = null;
        if (el.TryGetProperty("extends", out var x) && x.ValueKind == JsonValueKind.Object)
        {
            ext = new InstructionSetExtends(
                Spec: ReqString(x, "spec", filePath, $"{jsonPath}.extends.spec"),
                Set:  ReqString(x, "set",  filePath, $"{jsonPath}.extends.set"));
        }
        return new InstructionSetRef(
            Name:    ReqString(el, "name", filePath, $"{jsonPath}.name"),
            File:    ReqString(el, "file", filePath, $"{jsonPath}.file"),
            Extends: ext);
    }

    private static InstructionSetDispatch ParseDispatch(JsonElement el, string filePath)
    {
        var sv = new Dictionary<string, string>(StringComparer.Ordinal);
        if (el.TryGetProperty("selector_values", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in v.EnumerateObject())
                sv[prop.Name] = prop.Value.GetString() ?? "";
        }
        return new InstructionSetDispatch(
            Selector:        ReqString(el, "selector", filePath, "$.instruction_set_dispatch.selector"),
            SelectorValues:  sv,
            SwitchVia:       ParseStringList(el, "switch_via"),
            TransitionRule:  OptString(el, "transition_rule"));
    }

    private static MemoryModel ParseMemoryModel(JsonElement el, string filePath)
    {
        AlignmentPolicy? policy = null;
        if (el.TryGetProperty("alignment_policy", out var ap) && ap.ValueKind == JsonValueKind.Object)
        {
            policy = new AlignmentPolicy(
                LoadUnaligned:  OptString(ap, "load_unaligned")  ?? "permit",
                StoreUnaligned: OptString(ap, "store_unaligned") ?? "permit");
        }
        return new MemoryModel(
            DefaultEndianness: OptString(el, "default_endianness") ?? "little",
            AlignmentPolicy:   policy);
    }

    // ---------------- Instruction-set spec ----------------

    private static InstructionSetSpec ParseInstructionSetSpec(JsonElement root, string filePath)
    {
        EnsureObject(root, filePath, "$");

        var specVer = ReqString(root, "spec_version", filePath, "$.spec_version");
        var name    = ReqString(root, "name",         filePath, "$.name");

        var widthBits = ParseInstructionWidth(root, filePath);

        var alignBytes = OptInt(root, "alignment_bytes") ?? (widthBits.Fixed.HasValue ? widthBits.Fixed.Value / 8 : 1);
        var pcOffset   = OptInt(root, "pc_offset_bytes") ?? 0;
        var endian     = OptString(root, "endian_within_word") ?? "little";

        GlobalCondition? gc = null;
        if (root.TryGetProperty("global_condition", out var gcEl) && gcEl.ValueKind == JsonValueKind.Object)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in ReqObject(gcEl, "table", filePath, "$.global_condition.table").EnumerateObject())
                table[prop.Name] = prop.Value.GetString() ?? "";
            gc = new GlobalCondition(
                Field:      ParseBitRange(ReqProperty(gcEl, "field", filePath, "$.global_condition.field"),
                                          filePath, "$.global_condition.field"),
                Table:      table,
                AppliesTo:  OptString(gcEl, "applies_to") ?? "all_unless_marked_unconditional");
        }

        var decodeStrategy = OptString(root, "decode_strategy") ?? "mask_match_priority";

        WidthDecision? widthDecision = null;
        if (root.TryGetProperty("width_decision", out var wd) && wd.ValueKind == JsonValueKind.Object)
        {
            var ruleEl = ReqObject(wd, "rule", filePath, "$.width_decision.rule");
            widthDecision = new WidthDecision(
                FirstUnitBits: ReqInt(wd, "first_unit_bits", filePath, "$.width_decision.first_unit_bits"),
                Rule: new WidthDecisionRule(
                    Field:          ReqString(ruleEl, "field",            filePath, "$.width_decision.rule.field"),
                    LongWhenIn:     ParseStringList(ruleEl, "long_when_in"),
                    LongTotalBits:  ReqInt(ruleEl, "long_total_bits",     filePath, "$.width_decision.rule.long_total_bits")));
        }

        var groups = ParseList(root, "encoding_groups",
            (e, fp, jp) => ParseEncodingGroup(e, widthBits, fp, jp),
            filePath, "$.encoding_groups");

        InstructionSetExtends? extends = null;
        if (root.TryGetProperty("extends", out var ext) && ext.ValueKind == JsonValueKind.Object)
        {
            extends = new InstructionSetExtends(
                Spec: ReqString(ext, "spec", filePath, "$.extends.spec"),
                Set:  ReqString(ext, "set",  filePath, "$.extends.set"));
        }

        var custom = ParseList(root, "custom_micro_ops", ParseCustomMicroOp, filePath, "$.custom_micro_ops");

        return new InstructionSetSpec(
            specVer, name, widthBits, alignBytes, pcOffset, endian,
            gc, decodeStrategy, widthDecision, groups, extends, custom);
    }

    private static InstructionWidth ParseInstructionWidth(JsonElement root, string filePath)
    {
        if (!root.TryGetProperty("width_bits", out var wb))
            throw new SpecValidationException("Missing 'width_bits'.", filePath, "$.width_bits");

        return wb.ValueKind switch
        {
            JsonValueKind.Number => InstructionWidth.OfFixed(wb.GetInt32()),
            JsonValueKind.String when wb.GetString() == "variable" => InstructionWidth.Variable(),
            _ => throw new SpecValidationException(
                $"width_bits must be an integer (8/16/32/64) or the string 'variable'; got {wb.ValueKind}.",
                filePath, "$.width_bits")
        };
    }

    private static EncodingGroup ParseEncodingGroup(JsonElement el, InstructionWidth widthBits, string filePath, string jsonPath)
    {
        var formats = ParseList(el, "formats",
            (f, fp, jp) => ParseEncodingFormat(f, widthBits, fp, jp),
            filePath, $"{jsonPath}.formats");
        return new EncodingGroup(
            Name:        ReqString(el, "name", filePath, $"{jsonPath}.name"),
            AppliesWhen: OptString(el, "applies_when"),
            Formats:     formats);
    }

    private static EncodingFormat ParseEncodingFormat(JsonElement el, InstructionWidth widthBits, string filePath, string jsonPath)
    {
        var name    = ReqString(el, "name", filePath, $"{jsonPath}.name");
        var pattern = OptString(el, "pattern");
        var mask    = ParseHexU32(ReqString(el, "mask",  filePath, $"{jsonPath}.mask"),  filePath, $"{jsonPath}.mask");
        var match   = ParseHexU32(ReqString(el, "match", filePath, $"{jsonPath}.match"), filePath, $"{jsonPath}.match");

        var fields = new Dictionary<string, BitRange>(StringComparer.Ordinal);
        if (el.TryGetProperty("fields", out var fEl) && fEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in fEl.EnumerateObject())
                fields[prop.Name] = ParseBitRange(prop.Value, filePath, $"{jsonPath}.fields.{prop.Name}");
        }

        var operands = new Dictionary<string, OperandResolver>(StringComparer.Ordinal);
        if (el.TryGetProperty("operands", out var opEl) && opEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in opEl.EnumerateObject())
            {
                var kind    = ReqString(prop.Value, "kind", filePath, $"{jsonPath}.operands.{prop.Name}.kind");
                var outputs = ParseStringList(prop.Value, "outputs");
                operands[prop.Name] = new OperandResolver(kind, outputs, prop.Value.Clone());
            }
        }

        var instructions = ParseList(el, "instructions", ParseInstructionDef, filePath, $"{jsonPath}.instructions");

        return new EncodingFormat(
            Name:         name,
            Comment:      OptString(el, "comment"),
            Pattern:      pattern,
            Fields:       fields,
            Mask:         mask,
            Match:        match,
            Operands:     operands,
            Instructions: instructions);
    }

    private static InstructionDef ParseInstructionDef(JsonElement el, string filePath, string jsonPath)
    {
        InstructionSelector? selector = null;
        if (el.TryGetProperty("selector", out var sEl) && sEl.ValueKind == JsonValueKind.Object)
        {
            var fld = ReqString(sEl, "field", filePath, $"{jsonPath}.selector.field");
            string val = sEl.GetProperty("value").ValueKind switch
            {
                JsonValueKind.String => sEl.GetProperty("value").GetString()!,
                JsonValueKind.Number => sEl.GetProperty("value").GetInt64().ToString(CultureInfo.InvariantCulture),
                _ => throw new SpecValidationException("selector.value must be string or number.",
                                                       filePath, $"{jsonPath}.selector.value")
            };
            selector = new InstructionSelector(fld, val);
        }

        Cycles? cycles = null;
        if (el.TryGetProperty("cycles", out var cEl) && cEl.ValueKind == JsonValueKind.Object)
        {
            // N3.3 — optional `table` for per-(mnemonic, addressing-mode)
            // cycle granularity.
            CycleTable? cycleTable = null;
            if (cEl.TryGetProperty("table", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
            {
                var fieldName = ReqString(tEl, "field", filePath, $"{jsonPath}.cycles.table.field");
                if (!tEl.TryGetProperty("values", out var vEl) || vEl.ValueKind != JsonValueKind.Object)
                    throw new SpecValidationException(
                        "cycles.table.values must be an object mapping bit-pattern strings to integer cycle counts.",
                        filePath, $"{jsonPath}.cycles.table.values");

                var values = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var kv in vEl.EnumerateObject())
                {
                    if (kv.Value.ValueKind != JsonValueKind.Number)
                        throw new SpecValidationException(
                            $"cycles.table.values['{kv.Name}'] must be an integer cycle count.",
                            filePath, $"{jsonPath}.cycles.table.values.{kv.Name}");
                    values[kv.Name] = kv.Value.GetInt32();
                }
                cycleTable = new CycleTable(fieldName, values);
            }

            // N9 — dynamic cycle penalties (optional ints).
            int? extraWhenTaken = null;
            if (cEl.TryGetProperty("extra_when_taken", out var etEl) &&
                etEl.ValueKind == JsonValueKind.Number)
            {
                extraWhenTaken = etEl.GetInt32();
            }

            int? extraWhenPageCross = null;
            if (cEl.TryGetProperty("extra_when_page_cross", out var epcEl) &&
                epcEl.ValueKind == JsonValueKind.Number)
            {
                extraWhenPageCross = epcEl.GetInt32();
            }

            cycles = new Cycles(
                Form:                OptStringFlexible(cEl, "form"),
                FormAlt:             ParseStringList(cEl, "form_alt"),
                ExtraWhenDestPc:     OptString(cEl, "extra_when_dest_pc"),
                ExtraWhenLoadPc:     OptString(cEl, "extra_when_load_pc"),
                ComputedAt:          OptString(cEl, "computed_at"),
                Table:               cycleTable,
                ExtraWhenTaken:      extraWhenTaken,
                ExtraWhenPageCross:  extraWhenPageCross);
        }

        var steps = ParseList(el, "steps", ParseMicroOpStep, filePath, $"{jsonPath}.steps");

        return new InstructionDef(
            Selector:                  selector,
            Mnemonic:                  ReqString(el, "mnemonic", filePath, $"{jsonPath}.mnemonic"),
            Since:                     OptString(el, "since"),
            Until:                     OptString(el, "until"),
            RequiresFeature:           OptString(el, "requires_feature"),
            Unconditional:             OptBool  (el, "unconditional") ?? false,
            WritesPc:                  OptString(el, "writes_pc"),
            WritesMemory:              ParseStringList(el, "writes_memory"),
            ChangesMode:               OptBool  (el, "changes_mode") ?? false,
            SwitchesInstructionSet:    OptBool  (el, "switches_instruction_set") ?? false,
            RequiresIoBarrier:         OptBool  (el, "requires_io_barrier") ?? false,
            Quirks:                    ParseStringList(el, "quirks"),
            ManualRef:                 OptString(el, "manual_ref"),
            Cycles:                    cycles,
            Steps:                     steps,
            // 25.1 — parse optional `id`. Required for instructions that
            // a child spec wants to override / remove (sprint 25.2 retrofits
            // existing 8086 spec with IDs). Until then the field stays null
            // and inheritance can only do additions.
            Id:                        OptString(el, "id"));
    }

    private static MicroOpStep ParseMicroOpStep(JsonElement el, string filePath, string jsonPath)
    {
        var op = ReqString(el, "op", filePath, $"{jsonPath}.op");
        return new MicroOpStep(op, el.Clone());
    }

    private static CustomMicroOp ParseCustomMicroOp(JsonElement el, string filePath, string jsonPath)
    {
        return new CustomMicroOp(
            Name:               ReqString(el, "name", filePath, $"{jsonPath}.name"),
            Inputs:             ParsePortList(el, "inputs"),
            Outputs:            ParsePortList(el, "outputs"),
            Summary:            OptString(el, "summary"),
            ImplementationHint: OptString(el, "implementation_hint"));
    }

    private static IReadOnlyList<CustomMicroOpPort> ParsePortList(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<CustomMicroOpPort>();
        var list = new List<CustomMicroOpPort>();
        foreach (var p in arr.EnumerateArray())
        {
            list.Add(new CustomMicroOpPort(
                Name:  p.GetProperty("name").GetString() ?? "",
                Width: p.TryGetProperty("width", out var w) ? w.GetInt32() : null));
        }
        return list;
    }

    // ---------------- Helpers ----------------

    private static void EnsureObject(JsonElement el, string filePath, string jsonPath)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new SpecValidationException($"Expected object, got {el.ValueKind}.", filePath, jsonPath);
    }

    private static JsonElement ReqProperty(JsonElement el, string name, string filePath, string jsonPath)
    {
        if (!el.TryGetProperty(name, out var p))
            throw new SpecValidationException($"Missing property '{name}'.", filePath, jsonPath);
        return p;
    }

    private static JsonElement ReqObject(JsonElement el, string name, string filePath, string jsonPath)
    {
        var p = ReqProperty(el, name, filePath, jsonPath);
        if (p.ValueKind != JsonValueKind.Object)
            throw new SpecValidationException($"'{name}' must be an object.", filePath, jsonPath);
        return p;
    }

    private static bool TryGetObject(JsonElement el, string name, out JsonElement obj)
    {
        if (el.TryGetProperty(name, out obj) && obj.ValueKind == JsonValueKind.Object)
            return true;
        obj = default;
        return false;
    }

    private static string ReqString(JsonElement el, string name, string filePath, string jsonPath)
    {
        var p = ReqProperty(el, name, filePath, jsonPath);
        if (p.ValueKind != JsonValueKind.String)
            throw new SpecValidationException($"'{name}' must be a string.", filePath, jsonPath);
        return p.GetString()!;
    }

    private static int ReqInt(JsonElement el, string name, string filePath, string jsonPath)
    {
        var p = ReqProperty(el, name, filePath, jsonPath);
        if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var i))
            throw new SpecValidationException($"'{name}' must be a 32-bit integer.", filePath, jsonPath);
        return i;
    }

    private static string? OptString(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? OptStringFlexible(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Number => p.GetInt64().ToString(CultureInfo.InvariantCulture),
            _ => null
        };
    }

    private static int? OptInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;

    private static bool? OptBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False)
            ? p.GetBoolean() : null;

    private static IReadOnlyList<string> ParseStringList(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in p.EnumerateArray())
            list.Add(item.GetString() ?? "");
        return list;
    }

    private static IReadOnlyList<T> ParseList<T>(
        JsonElement parent, string name,
        Func<JsonElement, string, string, T> elementParser,
        string filePath, string jsonPath)
    {
        if (!parent.TryGetProperty(name, out var arr)) return Array.Empty<T>();
        if (arr.ValueKind != JsonValueKind.Array)
            throw new SpecValidationException($"'{name}' must be an array.", filePath, jsonPath);
        var list = new List<T>();
        var i = 0;
        foreach (var el in arr.EnumerateArray())
        {
            list.Add(elementParser(el, filePath, $"{jsonPath}[{i}]"));
            i++;
        }
        return list;
    }

    private static BitRange ParseBitRange(JsonElement el, string filePath, string jsonPath)
    {
        if (el.ValueKind != JsonValueKind.String)
            throw new SpecValidationException("Bit range must be a string.", filePath, jsonPath);
        try
        {
            return BitRange.Parse(el.GetString()!);
        }
        catch (Exception ex)
        {
            throw new SpecValidationException($"Invalid bit range: {ex.Message}", ex, filePath, jsonPath);
        }
    }

    private static uint ParseHexU32(string s, string filePath, string jsonPath)
    {
        if (!s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new SpecValidationException($"Hex value must start with '0x'; got '{s}'.", filePath, jsonPath);
        if (!uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            throw new SpecValidationException($"Invalid hex value '{s}'.", filePath, jsonPath);
        return v;
    }
}
