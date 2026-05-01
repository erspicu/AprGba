using System.Text;
using AprCpu.Core.JsonSpec;

namespace AprCpu.Core.Decoder;

/// <summary>
/// The result of compiling a bit-pattern string into machine-friendly form.
/// <see cref="LetterFields"/> lists each letter-run found in the pattern,
/// keyed by the single letter character. Multiple letter runs of different
/// letters can appear; the same letter must be contiguous (we reject
/// re-use). For matching against JSON-declared fields, callers should
/// compare by <see cref="BitRange"/> rather than by name.
/// </summary>
public sealed record CompiledPattern(
    string                              Original,
    int                                 WidthBits,
    uint                                Mask,
    uint                                Match,
    IReadOnlyList<(char Letter, BitRange Range)> LetterFields);

/// <summary>
/// Parses encoding patterns of the form <c>"cccc_001_oooo_s_nnnn_dddd_..."</c>:
/// <list type="bullet">
///   <item>'0' / '1' contribute to the fixed bits (mask = 1, match = bit value)</item>
///   <item>any letter [a-z] is a field placeholder; consecutive identical
///         letters group into one field name</item>
///   <item>'x' is a don't-care bit (no mask, no field)</item>
///   <item>'_' is a visual separator and is stripped</item>
/// </list>
///
/// Fields are extracted by scanning left-to-right where index 0 represents
/// the most-significant bit (bit <c>width-1</c>). A field name may appear in
/// multiple disjoint groups in the same pattern, which is rejected (each name
/// must occupy one contiguous range).
/// </summary>
public static class BitPatternCompiler
{
    public static CompiledPattern Compile(string pattern, int widthBits)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException("Pattern must not be empty.", nameof(pattern));
        if (widthBits is not (8 or 16 or 32 or 64))
            throw new ArgumentException($"Unsupported widthBits {widthBits}.", nameof(widthBits));

        var stripped = Strip(pattern);
        if (stripped.Length != widthBits)
        {
            throw new SpecValidationException(
                $"Pattern '{pattern}' has {stripped.Length} bits after stripping separators; expected {widthBits}.");
        }

        uint mask = 0, match = 0;
        var letterFields = new List<(char Letter, BitRange Range)>();
        var seenLetters = new HashSet<char>();

        // Walk left→right. Index 0 is the MSB (bit width-1).
        int i = 0;
        while (i < stripped.Length)
        {
            char c = stripped[i];
            int bitIndex = widthBits - 1 - i;

            switch (c)
            {
                case '0':
                    mask |= 1u << bitIndex;
                    i++;
                    continue;
                case '1':
                    mask  |= 1u << bitIndex;
                    match |= 1u << bitIndex;
                    i++;
                    continue;
                case 'x':
                    i++;
                    continue;
            }

            if (!IsLetter(c))
            {
                throw new SpecValidationException(
                    $"Invalid pattern char '{c}' at position {i} of '{pattern}'.");
            }

            // Field placeholder: consume the run.
            int j = i;
            while (j < stripped.Length && stripped[j] == c) j++;
            int high = widthBits - 1 - i;
            int low  = widthBits - 1 - (j - 1);

            if (!seenLetters.Add(c))
            {
                throw new SpecValidationException(
                    $"Pattern '{pattern}': field letter '{c}' appears in non-contiguous groups.");
            }
            letterFields.Add((c, new BitRange(high, low)));
            i = j;
        }

        // Order by descending position for human readability.
        letterFields.Sort((a, b) => b.Range.High.CompareTo(a.Range.High));

        return new CompiledPattern(pattern, widthBits, mask, match, letterFields);
    }

    /// <summary>
    /// Cross-validate a CompiledPattern against an encoding format's
    /// declared fields/mask/match. Throws <see cref="SpecValidationException"/>
    /// on any inconsistency.
    ///
    /// The match strategy is by <see cref="BitRange"/>: every declared
    /// field's range must appear as one of the pattern's letter-runs.
    /// Field name ↔ pattern letter correspondence is intentionally not
    /// enforced (e.g. JSON name "rd" with pattern letter 'd' is fine).
    /// </summary>
    public static void Validate(
        CompiledPattern compiled,
        IReadOnlyDictionary<string, BitRange> declaredFields,
        uint declaredMask,
        uint declaredMatch,
        string formatName)
    {
        if (compiled.Mask != declaredMask)
        {
            throw new SpecValidationException(
                $"Format '{formatName}': pattern-derived mask 0x{compiled.Mask:X8} ≠ declared mask 0x{declaredMask:X8}.");
        }
        if (compiled.Match != declaredMatch)
        {
            throw new SpecValidationException(
                $"Format '{formatName}': pattern-derived match 0x{compiled.Match:X8} ≠ declared match 0x{declaredMatch:X8}.");
        }

        var patternRanges = new HashSet<BitRange>(compiled.LetterFields.Select(p => p.Range));

        foreach (var (name, range) in declaredFields)
        {
            if (!patternRanges.Contains(range))
            {
                throw new SpecValidationException(
                    $"Format '{formatName}': declared field '{name}' = {range} has no matching letter run in pattern '{compiled.Original}'.");
            }
        }

        // Coverage: declared fields' ranges should jointly cover all
        // pattern letter runs (warn if pattern has unnamed letter groups).
        var declaredRanges = new HashSet<BitRange>(declaredFields.Values);
        foreach (var (letter, range) in compiled.LetterFields)
        {
            if (!declaredRanges.Contains(range))
            {
                throw new SpecValidationException(
                    $"Format '{formatName}': pattern letter run '{letter}' = {range} has no declared field name.");
            }
        }
    }

    /// <summary>
    /// Extract a value out of an instruction word using a bit range
    /// (no LLVM IR involved; for tests / decoder logic).
    /// </summary>
    public static uint ExtractField(uint instruction, BitRange range)
        => (instruction >> range.Low) & range.LowMask;

    // ---------------- internals ----------------

    private static bool IsLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    private static string Strip(string pattern)
    {
        var sb = new StringBuilder(pattern.Length);
        foreach (var c in pattern)
        {
            if (c == '_' || char.IsWhiteSpace(c)) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

}
