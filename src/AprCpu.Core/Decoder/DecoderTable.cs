using AprCpu.Core.JsonSpec;

namespace AprCpu.Core.Decoder;

/// <summary>One match against a fully-decoded instruction word.</summary>
public sealed record DecodedInstruction(
    EncodingFormat Format,
    InstructionDef Instruction,
    CompiledPattern Pattern);

/// <summary>
/// Priority-ordered dispatch table for an instruction set.
/// Constructor pre-validates all patterns against declared mask/match/fields.
/// <see cref="Decode"/> performs the lookup.
/// </summary>
public sealed class DecoderTable
{
    private readonly List<Entry> _entries;
    public string Name { get; }
    public InstructionWidth Width { get; }

    public DecoderTable(InstructionSetSpec spec)
    {
        if (!spec.WidthBits.Fixed.HasValue)
        {
            throw new NotSupportedException(
                $"Variable-width instruction sets ('{spec.Name}') not yet supported by DecoderTable.");
        }
        var width = spec.WidthBits.Fixed.Value;
        Name = spec.Name;
        Width = spec.WidthBits;
        _entries = new List<Entry>();

        foreach (var group in spec.EncodingGroups)
        foreach (var format in group.Formats)
        {
            CompiledPattern? pattern = null;
            if (!string.IsNullOrEmpty(format.Pattern))
            {
                pattern = BitPatternCompiler.Compile(format.Pattern, width);
                BitPatternCompiler.Validate(pattern, format.Fields, format.Mask, format.Match, format.Name);
            }
            // Phase 7 F.y: pre-build a DecodedInstruction for each
            // InstructionDef in the format. Decode now returns the cached
            // instance instead of allocating one per call.
            var decodedByDef = new DecodedInstruction[format.Instructions.Count];
            for (int i = 0; i < format.Instructions.Count; i++)
                decodedByDef[i] = new DecodedInstruction(format, format.Instructions[i], pattern!);
            _entries.Add(new Entry(format, pattern, decodedByDef));
        }
    }

    public IReadOnlyList<EncodingFormat> Formats =>
        _entries.Select(e => e.Format).ToList();

    /// <summary>Return the compiled pattern for a format (null if no pattern declared).</summary>
    public CompiledPattern? GetCompiledPattern(string formatName)
        => _entries.FirstOrDefault(e => e.Format.Name == formatName)?.Pattern;

    /// <summary>
    /// Decode one instruction word. Returns the first matching
    /// (format, instruction) pair using priority order, or null if no
    /// format claims the bits.
    /// </summary>
    public DecodedInstruction? Decode(uint instruction)
    {
        // Phase 7 F.y: returns cached DecodedInstruction (one per
        // (format, insdef)) instead of allocating per call.
        // Loop intentionally uses indexed access on List<Entry> so the
        // tier-1 JIT can avoid allocating the foreach IEnumerator.
        for (int e = 0; e < _entries.Count; e++)
        {
            var entry = _entries[e];
            if ((instruction & entry.Format.Mask) != entry.Format.Match) continue;

            var insDefs = entry.Format.Instructions;
            for (int i = 0; i < insDefs.Count; i++)
            {
                var insDef = insDefs[i];
                if (insDef.Selector is null)
                    return entry.DecodedByDef[i];

                if (!entry.Format.Fields.TryGetValue(insDef.Selector.Field, out var selRange))
                {
                    throw new SpecValidationException(
                        $"Format '{entry.Format.Name}': selector references unknown field '{insDef.Selector.Field}'.");
                }
                var actual = BitPatternCompiler.ExtractField(instruction, selRange);
                if (actual == insDef.Selector.NumericValue)
                    return entry.DecodedByDef[i];
            }
            // Format matched mask/match but no instruction selector matched.
            // Continue searching subsequent formats (priority).
        }
        return null;
    }

    private sealed record Entry(
        EncodingFormat Format,
        CompiledPattern? Pattern,
        DecodedInstruction[] DecodedByDef);
}
