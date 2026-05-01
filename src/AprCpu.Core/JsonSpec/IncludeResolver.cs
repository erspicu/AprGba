using System.Text.Json.Nodes;

namespace AprCpu.Core.JsonSpec;

/// <summary>
/// Implements the spec-file <c>$include</c> mechanism. Walks a parsed
/// JSON tree and replaces every <c>{ "$include": "&lt;relative-path&gt;" }</c>
/// directive with the parsed content of the referenced file.
///
/// Resolution rules:
/// <list type="bullet">
///   <item>Path is relative to the file containing the directive.</item>
///   <item>If the included file's root is a JSON array, that array is
///         spliced into the parent array (replacing the directive
///         element, not nested as one element). This lets one include
///         file contribute multiple sibling entries.</item>
///   <item>Otherwise the included root replaces the directive object
///         in-place (object-for-object, value-for-value).</item>
///   <item>Includes recurse: an included file may itself contain more
///         <c>$include</c> directives.</item>
///   <item>Cycle detection is per-chain: A→B→A is rejected, but the
///         same file may be included from multiple places along
///         different chains (e.g. a shared sub-fragment).</item>
/// </list>
///
/// The resolver does NOT validate the resulting tree against the
/// schema — that is the loader's responsibility, after assembly. So
/// included fragments must produce shapes that are valid in their
/// host context.
/// </summary>
public static class IncludeResolver
{
    /// <summary>
    /// Resolve all <c>$include</c> directives in <paramref name="root"/>.
    /// Returns a new <see cref="JsonNode"/> tree; the input is not
    /// modified.
    /// </summary>
    public static JsonNode? Resolve(JsonNode? root, string sourceFilePath)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFull = Path.GetFullPath(sourceFilePath);
        return ResolveRecursive(root, sourceFull, visited);
    }

    private static JsonNode? ResolveRecursive(JsonNode? node, string sourceFile, HashSet<string> visited)
    {
        if (node is null) return null;

        if (node is JsonObject obj)
        {
            if (IsIncludeDirective(obj, out var includePath))
            {
                return LoadAndResolve(includePath, sourceFile, visited);
            }

            var newObj = new JsonObject();
            foreach (var prop in obj)
            {
                var resolved = ResolveRecursive(prop.Value?.DeepClone(), sourceFile, visited);
                newObj[prop.Key] = resolved;
            }
            return newObj;
        }

        if (node is JsonArray arr)
        {
            var newArr = new JsonArray();
            foreach (var item in arr)
            {
                var resolved = ResolveRecursive(item?.DeepClone(), sourceFile, visited);

                // If the resolved value is itself an array, splice its
                // elements into the parent array. This lets an included
                // file contribute multiple sibling entries via a single
                // directive.
                if (resolved is JsonArray spliceArr)
                {
                    foreach (var elem in spliceArr)
                        newArr.Add(elem?.DeepClone());
                }
                else
                {
                    newArr.Add(resolved);
                }
            }
            return newArr;
        }

        // Leaf (string / number / bool / null): pass through.
        return node.DeepClone();
    }

    private static bool IsIncludeDirective(JsonObject obj, out string includePath)
    {
        includePath = "";
        if (obj.Count != 1) return false;
        if (!obj.TryGetPropertyValue("$include", out var pathNode)) return false;
        if (pathNode is not JsonValue v) return false;
        if (!v.TryGetValue<string>(out var s) || string.IsNullOrEmpty(s)) return false;
        includePath = s;
        return true;
    }

    private static JsonNode? LoadAndResolve(string includeRelPath, string sourceFile, HashSet<string> visited)
    {
        var sourceDir = Path.GetDirectoryName(sourceFile)
            ?? throw new SpecValidationException($"Cannot derive directory from source file '{sourceFile}'.");
        var fullPath = Path.GetFullPath(Path.Combine(sourceDir, includeRelPath));

        if (visited.Contains(fullPath))
        {
            throw new SpecValidationException(
                $"Circular $include detected at '{fullPath}'. Include chain so far: " +
                string.Join(" -> ", visited));
        }
        if (!File.Exists(fullPath))
        {
            throw new SpecValidationException(
                $"$include path not found: '{includeRelPath}' (resolved to '{fullPath}').",
                sourceFile);
        }

        visited.Add(fullPath);
        try
        {
            var json = File.ReadAllText(fullPath);
            JsonNode? included;
            try
            {
                included = JsonNode.Parse(json);
            }
            catch (Exception ex)
            {
                throw new SpecValidationException(
                    $"Failed to parse included file: {ex.Message}", ex, fullPath);
            }
            return ResolveRecursive(included, fullPath, visited);
        }
        finally
        {
            visited.Remove(fullPath);
        }
    }
}
