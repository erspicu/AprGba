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
            _entries.Add(new Entry(format, pattern));
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
        foreach (var entry in _entries)
        {
            if ((instruction & entry.Format.Mask) != entry.Format.Match) continue;

            // Walk instructions inside this format and find the first whose
            // selector matches. If no selector, the (sole) instruction wins.
            foreach (var insDef in entry.Format.Instructions)
            {
                if (insDef.Selector is null)
                    return new DecodedInstruction(entry.Format, insDef, entry.Pattern!);

                if (!entry.Format.Fields.TryGetValue(insDef.Selector.Field, out var selRange))
                {
                    throw new SpecValidationException(
                        $"Format '{entry.Format.Name}': selector references unknown field '{insDef.Selector.Field}'.");
                }
                var actual = BitPatternCompiler.ExtractField(instruction, selRange);
                if (actual == insDef.Selector.NumericValue)
                    return new DecodedInstruction(entry.Format, insDef, entry.Pattern!);
            }
            // Format matched mask/match but no instruction selector matched.
            // Continue searching subsequent formats (priority).
        }
        return null;
    }

    private sealed record Entry(EncodingFormat Format, CompiledPattern? Pattern);
}
