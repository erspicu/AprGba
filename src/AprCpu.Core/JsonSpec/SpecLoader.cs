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

    /// <summary>Load a cpu.json plus all its referenced instruction-set files.</summary>
    public static LoadedSpec LoadCpuSpec(string cpuJsonPath)
    {
        var fullPath = Path.GetFullPath(cpuJsonPath);
        if (!File.Exists(fullPath))
            throw new SpecValidationException("File not found.", fullPath);

        using var doc = LoadAndResolveDocument(fullPath);
        var cpu = ParseCpuSpec(doc.RootElement, fullPath);

        var dir = Path.GetDirectoryName(fullPath)!;
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
            sets[setRef.Name] = set;
        }

        return new LoadedSpec(fullPath, cpu, sets);
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
        var regFile  = ParseRegisterFile(ReqObject(root, "register_file", filePath, "$.register_file"), filePath);
        var modes    = TryGetObject(root, "processor_modes", out var pm) ? ParseProcessorModes(pm, filePath) : null;
        var vectors  = ParseList(root, "exception_vectors", ParseExceptionVector, filePath, "$.exception_vectors");
        var sets     = ParseList(root, "instruction_sets", ParseInstructionSetRef, filePath, "$.instruction_sets");
        if (sets.Count == 0)
            throw new SpecValidationException("instruction_sets must contain at least one entry.", filePath, "$.instruction_sets");
        var dispatch = TryGetObject(root, "instruction_set_dispatch", out var d) ? ParseDispatch(d, filePath) : null;
        var memory   = TryGetObject(root, "memory_model", out var mm) ? ParseMemoryModel(mm, filePath) : null;
        var custom   = ParseList(root, "custom_micro_ops", ParseCustomMicroOp, filePath, "$.custom_micro_ops");

        return new CpuSpec(specVer, arch, variants, regFile, modes, vectors, sets, dispatch, memory, custom);
    }

    private static Architecture ParseArchitecture(JsonElement el, string filePath)
    {
        return new Architecture(
            Id:           ReqString(el, "id",     filePath, "$.architecture.id"),
            Family:       ReqString(el, "family", filePath, "$.architecture.family"),
            Extends:      OptString(el, "extends"),
            Endianness:   OptString(el, "endianness") ?? "little",
            WordSizeBits: OptInt(el, "word_size_bits") ?? 32);
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

        return new RegisterFile(generalPurpose, status, pairs);
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
            cycles = new Cycles(
                Form:              OptStringFlexible(cEl, "form"),
                FormAlt:           ParseStringList(cEl, "form_alt"),
                ExtraWhenDestPc:   OptString(cEl, "extra_when_dest_pc"),
                ExtraWhenLoadPc:   OptString(cEl, "extra_when_load_pc"),
                ComputedAt:        OptString(cEl, "computed_at"));
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
            Steps:                     steps);
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
