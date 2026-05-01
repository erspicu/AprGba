using System.Text.Json.Nodes;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Tests for the spec $include resolver. Each test creates a temp
/// directory with a small set of files, exercises the resolver, then
/// cleans up.
/// </summary>
public class IncludeResolverTests : IDisposable
{
    private readonly string _tempDir;

    public IncludeResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            $"aprcpu_include_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relPath, string contents)
    {
        var full = Path.Combine(_tempDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    private static JsonNode? Resolve(string contents, string sourcePath)
    {
        var node = JsonNode.Parse(contents);
        return IncludeResolver.Resolve(node, sourcePath);
    }

    [Fact]
    public void Inline_ContentPassThrough()
    {
        var src = Write("inline.json", """{ "a": 1, "b": [10, 20] }""");
        var resolved = Resolve(File.ReadAllText(src), src);

        var json = resolved!.ToJsonString();
        Assert.Contains("\"a\":1", json);
        Assert.Contains("\"b\":[10,20]", json);
    }

    [Fact]
    public void IncludeReplacesArrayElement_WithObjectRoot()
    {
        Write("frag.json", """{ "name": "fragment", "value": 42 }""");
        var src = Write("main.json", """
            { "items": [
                { "name": "before" },
                { "$include": "frag.json" },
                { "name": "after" }
              ] }
            """);

        var resolved = Resolve(File.ReadAllText(src), src);
        var items = resolved!["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        Assert.Equal("before",   (string?)items[0]!["name"]);
        Assert.Equal("fragment", (string?)items[1]!["name"]);
        Assert.Equal(42,         (int?)items[1]!["value"]);
        Assert.Equal("after",    (string?)items[2]!["name"]);
    }

    [Fact]
    public void IncludeWithArrayRoot_SplicesIntoParentArray()
    {
        Write("multi.json", """[ { "n": 1 }, { "n": 2 }, { "n": 3 } ]""");
        var src = Write("main.json", """
            { "items": [
                { "$include": "multi.json" },
                { "n": 4 }
              ] }
            """);

        var resolved = Resolve(File.ReadAllText(src), src);
        var items = resolved!["items"]!.AsArray();
        Assert.Equal(4, items.Count);
        Assert.Equal(1, (int?)items[0]!["n"]);
        Assert.Equal(2, (int?)items[1]!["n"]);
        Assert.Equal(3, (int?)items[2]!["n"]);
        Assert.Equal(4, (int?)items[3]!["n"]);
    }

    [Fact]
    public void NestedIncludes_AreFollowed()
    {
        Write("inner.json", """{ "leaf": "deep" }""");
        Write("middle.json", """{ "wrap": { "$include": "inner.json" } }""");
        var src = Write("outer.json", """{ "outer": { "$include": "middle.json" } }""");

        var resolved = Resolve(File.ReadAllText(src), src);
        var leaf = (string?)resolved!["outer"]!["wrap"]!["leaf"];
        Assert.Equal("deep", leaf);
    }

    [Fact]
    public void RelativePath_ResolvedFromContainingFile()
    {
        Write("sub/frag.json", """{ "where": "subdir" }""");
        var src = Write("main.json", """{ "x": { "$include": "sub/frag.json" } }""");

        var resolved = Resolve(File.ReadAllText(src), src);
        Assert.Equal("subdir", (string?)resolved!["x"]!["where"]);
    }

    [Fact]
    public void CyclicInclude_IsRejected()
    {
        Write("a.json", """{ "$include": "b.json" }""");
        Write("b.json", """{ "$include": "a.json" }""");
        var srcPath = Path.Combine(_tempDir, "a.json");

        var ex = Assert.Throws<SpecValidationException>(
            () => Resolve(File.ReadAllText(srcPath), srcPath));
        Assert.Contains("Circular", ex.Message);
    }

    [Fact]
    public void MissingIncludeFile_ThrowsWithPath()
    {
        var src = Write("main.json", """{ "x": { "$include": "no_such.json" } }""");
        var ex = Assert.Throws<SpecValidationException>(
            () => Resolve(File.ReadAllText(src), src));
        Assert.Contains("not found", ex.Message);
        Assert.Contains("no_such.json", ex.Message);
    }

    [Fact]
    public void IncludeDirectiveWithExtraKeys_TreatedAsRegularObject()
    {
        // {"$include": "x", "extra": 1} is NOT a single-key directive,
        // so the $include is preserved as data (not resolved).
        Write("frag.json", """{ "y": "should not appear" }""");
        var src = Write("main.json", """
            { "obj": { "$include": "frag.json", "extra": 1 } }
            """);

        var resolved = Resolve(File.ReadAllText(src), src);
        // The include did NOT happen — $include kept as a literal property.
        Assert.Equal("frag.json", (string?)resolved!["obj"]!["$include"]);
        Assert.Equal(1,            (int?)resolved!["obj"]!["extra"]);
    }

    [Fact]
    public void SameFileIncludedFromMultipleSiblings_IsAllowed()
    {
        // Not a cycle — just convenience reuse.
        Write("frag.json", """{ "shared": true }""");
        var src = Write("main.json", """
            { "items": [
                { "$include": "frag.json" },
                { "$include": "frag.json" }
              ] }
            """);

        var resolved = Resolve(File.ReadAllText(src), src);
        var items = resolved!["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.True((bool)items[0]!["shared"]!);
        Assert.True((bool)items[1]!["shared"]!);
    }
}
