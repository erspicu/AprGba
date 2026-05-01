namespace AprCpu.Core.JsonSpec;

/// <summary>
/// Semantic validators that run after JSON-shape parsing succeeds.
/// Catches authoring mistakes the JSON Schema can't express (overlapping
/// bit-fields, selector value width, etc.).
///
/// Hard errors throw <see cref="SpecValidationException"/>.
/// Soft warnings are returned as a list — callers may print or fail
/// as they prefer.
/// </summary>
public static class SpecValidator
{
    /// <summary>
    /// Run all checks on a single instruction-set spec.
    /// Hard violations throw immediately; soft warnings are accumulated
    /// and returned.
    /// </summary>
    public static IReadOnlyList<string> ValidateInstructionSet(InstructionSetSpec set)
    {
        var warnings = new List<string>();

        if (!set.WidthBits.Fixed.HasValue)
        {
            // Variable-width sets bypass field-bound checks for now.
            return warnings;
        }
        var width = set.WidthBits.Fixed.Value;

        foreach (var group in set.EncodingGroups)
        foreach (var format in group.Formats)
        {
            ValidateFieldBoundsAndOverlap(set.Name, format, width);
            ValidateSelectorWidths(set.Name, format);
            CollectWarnings(set.Name, format, warnings);
        }

        return warnings;
    }

    // ----- hard errors -----

    private static void ValidateFieldBoundsAndOverlap(string setName, EncodingFormat fmt, int width)
    {
        // Bounds
        foreach (var (name, range) in fmt.Fields)
        {
            if (range.Low < 0 || range.High > width - 1)
            {
                throw new SpecValidationException(
                    $"[{setName}.{fmt.Name}] field '{name}' = {range} is outside instruction width [0..{width - 1}].");
            }
        }

        // Overlap (pairwise)
        var entries = fmt.Fields.OrderBy(kv => kv.Value.Low).ToList();
        for (int i = 0; i < entries.Count; i++)
        for (int j = i + 1; j < entries.Count; j++)
        {
            var (n1, r1) = (entries[i].Key, entries[i].Value);
            var (n2, r2) = (entries[j].Key, entries[j].Value);
            if (Overlaps(r1, r2))
            {
                throw new SpecValidationException(
                    $"[{setName}.{fmt.Name}] fields '{n1}' = {r1} and '{n2}' = {r2} overlap.");
            }
        }
    }

    private static bool Overlaps(BitRange a, BitRange b)
        => !(a.High < b.Low || b.High < a.Low);

    private static void ValidateSelectorWidths(string setName, EncodingFormat fmt)
    {
        foreach (var def in fmt.Instructions)
        {
            if (def.Selector is null) continue;
            if (!fmt.Fields.TryGetValue(def.Selector.Field, out var field))
            {
                throw new SpecValidationException(
                    $"[{setName}.{fmt.Name}.{def.Mnemonic}] selector references unknown field '{def.Selector.Field}'.");
            }

            var v = def.Selector.Value;
            if (LooksLikeBinaryString(v))
            {
                if (v.Length != field.Width)
                {
                    throw new SpecValidationException(
                        $"[{setName}.{fmt.Name}.{def.Mnemonic}] selector value '{v}' has {v.Length} bits but field '{def.Selector.Field}' is {field.Width} bits.");
                }
            }
            else
            {
                // Integer / hex form: ensure numeric value fits.
                ulong numeric = def.Selector.NumericValue;
                ulong max = field.Width >= 64 ? ulong.MaxValue : (1UL << field.Width) - 1UL;
                if (numeric > max)
                {
                    throw new SpecValidationException(
                        $"[{setName}.{fmt.Name}.{def.Mnemonic}] selector value {numeric} exceeds field '{def.Selector.Field}' max {max} (width {field.Width}).");
                }
            }
        }
    }

    private static bool LooksLikeBinaryString(string s)
        => s.Length > 0 && s.All(c => c is '0' or '1');

    // ----- soft warnings -----

    private static void CollectWarnings(string setName, EncodingFormat fmt, List<string> sink)
    {
        if (fmt.Instructions.Count > 1)
        {
            int withoutSelector = fmt.Instructions.Count(d => d.Selector is null);
            if (withoutSelector > 0)
            {
                sink.Add(
                    $"[{setName}.{fmt.Name}] format has {fmt.Instructions.Count} instructions but {withoutSelector} have no selector — only the first selector-less one will ever match.");
            }
        }
    }
}
