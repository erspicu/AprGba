using System.Text.Json;
using System.Text.Json.Nodes;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// RFC 7386 conformance + spec-inheritance specific cases.
/// </summary>
public class JsonMergePatchTests
{
    private static JsonNode? Parse(string s) => JsonNode.Parse(s);
    private static string Stringify(JsonNode? n) => n?.ToJsonString() ?? "null";

    [Fact]
    public void Scalar_Replaces_Target()
    {
        var t = Parse(""" {"a": 1} """);
        var p = Parse("42");
        Assert.Equal("42", Stringify(JsonMergePatch.Apply(t, p)));
    }

    [Fact]
    public void Object_Adds_New_Key()
    {
        var t = Parse(""" {"a": 1} """);
        var p = Parse(""" {"b": 2} """);
        var r = JsonMergePatch.Apply(t, p);
        Assert.Equal(1, r!["a"]!.GetValue<int>());
        Assert.Equal(2, r["b"]!.GetValue<int>());
    }

    [Fact]
    public void Object_Replaces_Existing_Scalar()
    {
        var t = Parse(""" {"a": 1, "b": 2} """);
        var p = Parse(""" {"a": 99} """);
        var r = JsonMergePatch.Apply(t, p);
        Assert.Equal(99, r!["a"]!.GetValue<int>());
        Assert.Equal(2,  r["b"]!.GetValue<int>());
    }

    [Fact]
    public void Null_Removes_Key()
    {
        var t = Parse(""" {"a": 1, "b": 2} """);
        var p = Parse(""" {"a": null} """);
        var r = JsonMergePatch.Apply(t, p) as JsonObject;
        Assert.NotNull(r);
        Assert.False(r!.ContainsKey("a"));
        Assert.Equal(2, r["b"]!.GetValue<int>());
    }

    [Fact]
    public void Object_Recursively_Merges_Subobjects()
    {
        var t = Parse(""" {"cycles": {"form": "21m", "extra": 4}} """);
        var p = Parse(""" {"cycles": {"form": "13m"}} """);
        var r = JsonMergePatch.Apply(t, p);
        // form overridden, extra preserved
        Assert.Equal("13m", r!["cycles"]!["form"]!.GetValue<string>());
        Assert.Equal(4,     r["cycles"]!["extra"]!.GetValue<int>());
    }

    [Fact]
    public void Array_Replaces_Wholesale_Not_ElementWise()
    {
        // Spec inheritance contract: `steps` is replaced, not merged.
        var t = Parse(""" {"steps": [{"op": "x86_a"}, {"op": "x86_b"}]} """);
        var p = Parse(""" {"steps": [{"op": "x86_c"}]} """);
        var r = JsonMergePatch.Apply(t, p);
        var arr = r!["steps"]!.AsArray();
        Assert.Single(arr);
        Assert.Equal("x86_c", arr[0]!["op"]!.GetValue<string>());
    }

    [Fact]
    public void Patch_Of_Null_Returns_Null()
    {
        var t = Parse(""" {"a": 1} """);
        Assert.Null(JsonMergePatch.Apply(t, null));
    }

    [Fact]
    public void Target_Null_With_Object_Patch_Yields_Patch()
    {
        var p = Parse(""" {"a": 1} """);
        var r = JsonMergePatch.Apply(null, p);
        Assert.Equal(1, r!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Target_Scalar_With_Object_Patch_Yields_Object()
    {
        // Per RFC 7386: if target is not an object, replace with empty object
        // then apply patch. The end result is just the patch's keys.
        var t = Parse("42");
        var p = Parse(""" {"a": 1} """);
        var r = JsonMergePatch.Apply(t, p);
        Assert.Equal(1, r!["a"]!.GetValue<int>());
    }

    [Fact]
    public void Inputs_Not_Mutated()
    {
        var t = Parse(""" {"a": {"b": 1}} """);
        var p = Parse(""" {"a": {"b": 99}} """);
        var tBefore = Stringify(t);
        var pBefore = Stringify(p);
        _ = JsonMergePatch.Apply(t, p);
        Assert.Equal(tBefore, Stringify(t));
        Assert.Equal(pBefore, Stringify(p));
    }

    [Fact]
    public void Spec_Inheritance_Cycles_Override_Example()
    {
        // Realistic case: i80186 overrides i8086's MUL_rm16 cycle count.
        // Parent instruction:
        var parent = Parse("""
        {
          "id": "MUL_rm16",
          "encoding": { "primary_opcode": "0xF7", "modrm_reg": "100" },
          "mnemonic": "MUL",
          "writes_pc": "never",
          "cycles": { "form": "118m" },
          "steps": [{"op": "x86_mul_rm16"}]
        }
        """);
        // Child override (partial):
        var override_ = Parse("""
        { "cycles": { "form": "21m" } }
        """);
        var merged = JsonMergePatch.Apply(parent, override_);
        Assert.Equal("MUL_rm16", merged!["id"]!.GetValue<string>());
        Assert.Equal("21m",      merged["cycles"]!["form"]!.GetValue<string>());
        // steps preserved (not in override)
        Assert.Single(merged["steps"]!.AsArray());
    }

    [Fact]
    public void Spec_Inheritance_Steps_Replace_Example()
    {
        // i80186 overrides i8086's PUSH_SP behavior — entire steps array
        // gets replaced with the new pre-decrement variant.
        var parent = Parse("""
        {
          "id": "PUSH_SP",
          "mnemonic": "PUSH",
          "cycles": {"form": "11m"},
          "steps": [{"op": "x86_push_sp_8086_quirk"}]
        }
        """);
        var override_ = Parse("""
        { "steps": [{"op": "x86_push_sp_pre_decrement"}] }
        """);
        var merged = JsonMergePatch.Apply(parent, override_);
        var steps = merged!["steps"]!.AsArray();
        Assert.Single(steps);
        Assert.Equal("x86_push_sp_pre_decrement", steps[0]!["op"]!.GetValue<string>());
        // mnemonic, cycles preserved
        Assert.Equal("PUSH", merged["mnemonic"]!.GetValue<string>());
        Assert.Equal("11m",  merged["cycles"]!["form"]!.GetValue<string>());
    }

    [Fact]
    public void Element_Overload_Works()
    {
        using var tDoc = JsonDocument.Parse(""" {"a": 1, "b": 2} """);
        using var pDoc = JsonDocument.Parse(""" {"a": 99} """);
        var r = JsonMergePatch.Apply(tDoc.RootElement, pDoc.RootElement);
        Assert.Equal(99, r!["a"]!.GetValue<int>());
        Assert.Equal(2,  r["b"]!.GetValue<int>());
    }
}
