using System.Text.Json;
using System.Text.Json.Nodes;

namespace AprCpu.Core.JsonSpec;

/// <summary>
/// RFC 7386 (JSON Merge Patch) implementation, used by spec inheritance
/// (doc #23) to apply a child CPU's <c>overrides</c> dict over a parent
/// instruction's full JSON form.
///
/// <para>Semantics:</para>
/// <list type="bullet">
/// <item>If the patch is not an object, the patch itself is the result
/// (scalars + arrays replace the target).</item>
/// <item>For each key in the patch object:
///   <list type="bullet">
///   <item>If the patch value is <c>null</c>, the key is removed from the target.</item>
///   <item>If the patch value is an object, recursively merge it onto
///   the target's value at that key (creating an empty object if absent).</item>
///   <item>Otherwise (scalar / array), the patch value replaces the target's value.</item>
///   </list>
/// </item>
/// </list>
///
/// <para>Arrays are NOT merged element-by-element — they replace wholesale.
/// This matters for <c>steps</c> (the spec calls out replace semantics there)
/// and for <c>writes_memory</c> / <c>quirks</c>. If a child needs to extend
/// a parent's step list, it has to re-state the full list.</para>
///
/// <para>The implementation works on <see cref="JsonNode"/> rather than
/// <see cref="JsonElement"/> because the result needs to be mutable
/// (we're re-keying / deleting fields) before it's reparsed back into
/// the typed POCO model.</para>
/// </summary>
public static class JsonMergePatch
{
    /// <summary>
    /// Apply <paramref name="patch"/> on top of <paramref name="target"/>
    /// per RFC 7386. Both nodes are deep-cloned before merging — neither
    /// input is mutated. Returns a fresh <see cref="JsonNode"/>.
    /// </summary>
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        // Patch is not an object -> patch wholly replaces target.
        if (patch is not JsonObject patchObj)
            return patch?.DeepClone();

        // Target is not an object -> start with empty object and apply patch.
        var result = target is JsonObject targetObj
            ? (JsonObject)targetObj.DeepClone()
            : new JsonObject();

        foreach (var kv in patchObj)
        {
            var key = kv.Key;
            var pv = kv.Value;

            if (pv is null)
            {
                result.Remove(key);
                continue;
            }

            if (pv is JsonObject)
            {
                var existing = result.TryGetPropertyValue(key, out var ev) ? ev : null;
                var merged = Apply(existing, pv);
                result.Remove(key);
                if (merged is not null)
                    result.Add(key, merged);
                continue;
            }

            // Scalar or array -> replace.
            result.Remove(key);
            result.Add(key, pv.DeepClone());
        }

        return result;
    }

    /// <summary>
    /// Apply a patch represented as a <see cref="JsonElement"/> over a
    /// target <see cref="JsonElement"/>; returns the merged result as a
    /// fresh <see cref="JsonNode"/>. Convenience overload for callers
    /// reading from <see cref="JsonDocument"/>.
    /// </summary>
    public static JsonNode? Apply(JsonElement target, JsonElement patch)
        => Apply(JsonNode.Parse(target.GetRawText()), JsonNode.Parse(patch.GetRawText()));
}
