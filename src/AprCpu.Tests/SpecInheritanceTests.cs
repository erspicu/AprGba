using System.IO;
using AprCpu.Core.JsonSpec;
using Xunit;

namespace AprCpu.Tests;

/// <summary>
/// Sprint 25.1 — exercises SpecLoader's extends-chain resolution against
/// purpose-built tiny CPU specs in a temp directory. These tests cover
/// the merge primitives end-to-end without needing the real i80186 spec
/// (sprint 25.3) or the i8086 ID retrofit (sprint 25.2).
/// </summary>
public class SpecInheritanceTests : IDisposable
{
    private readonly string _tempDir;

    public SpecInheritanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"aprcpu_inherit_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string relPath, string content)
    {
        var path = Path.Combine(_tempDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // Minimal valid base instruction-set file with two instructions.
    private const string ParentSetJson = """
    {
      "spec_version": "1.0",
      "name": "Main",
      "width_bits": 8,
      "alignment_bytes": 1,
      "pc_offset_bytes": 0,
      "endian_within_word": "little",
      "decode_strategy": "mask_match_priority",
      "encoding_groups": [
        {
          "name": "smoke",
          "formats": [
            {
              "name": "Nop",
              "fields": {},
              "mask": "0xFF",
              "match": "0x90",
              "instructions": [
                {
                  "id": "NOP",
                  "mnemonic": "NOP",
                  "writes_pc": "never",
                  "cycles": { "form": "3" },
                  "steps": []
                }
              ]
            },
            {
              "name": "Hlt",
              "fields": {},
              "mask": "0xFF",
              "match": "0xF4",
              "instructions": [
                {
                  "id": "HLT",
                  "mnemonic": "HLT",
                  "writes_pc": "always",
                  "cycles": { "form": "2" },
                  "steps": [{ "op": "halt" }]
                }
              ]
            }
          ]
        }
      ]
    }
    """;

    private const string ParentCpuJson = """
    {
      "spec_version": "1.0",
      "architecture": {
        "id": "ParentCpu",
        "family": "test",
        "extends": null,
        "endianness": "little",
        "word_size_bits": 16
      },
      "variants": [],
      "register_file": {
        "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
        "register_pairs": [],
        "stack_pointer": "A",
        "status": []
      },
      "exception_vectors": [],
      "instruction_sets": [
        { "name": "Main", "file": "main.json" }
      ]
    }
    """;

    [Fact]
    public void Base_Spec_Loads_Without_Inheritance_Diff()
    {
        Write("parent/main.json", ParentSetJson);
        var path = Write("parent/cpu.json", ParentCpuJson);
        var loaded = SpecLoader.LoadCpuSpec(path);
        Assert.Equal("ParentCpu", loaded.Cpu.Architecture.Id);
        Assert.Single(loaded.InstructionSets);
        var main = loaded.InstructionSets["Main"];
        var allInstr = main.EncodingGroups.SelectMany(g => g.Formats).SelectMany(f => f.Instructions).ToList();
        Assert.Equal(2, allInstr.Count);
        Assert.Contains(allInstr, i => i.Id == "NOP");
        Assert.Contains(allInstr, i => i.Id == "HLT");
        // Provenance: base spec instructions tagged with OriginCpu.
        Assert.All(allInstr, i => Assert.Equal("ParentCpu", i.OriginCpu));
    }

    [Fact]
    public void Child_Without_ExtendsPath_When_Extends_Set_Throws()
    {
        Write("parent/main.json", ParentSetJson);
        Write("parent/cpu.json", ParentCpuJson);
        var childCpu = """
        {
          "spec_version": "1.0",
          "architecture": {
            "id": "ChildCpu",
            "family": "test",
            "extends": "ParentCpu",
            "endianness": "little",
            "word_size_bits": 16
          },
          "variants": [],
          "register_file": {
            "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
            "register_pairs": [], "stack_pointer": "A", "status": []
          },
          "exception_vectors": [],
          "instruction_sets": []
        }
        """;
        var path = Write("child/cpu.json", childCpu);
        var ex = Assert.Throws<SpecValidationException>(() => SpecLoader.LoadCpuSpec(path));
        Assert.Contains("extends_path is null", ex.Message);
    }

    [Fact]
    public void Child_With_Override_Replaces_Cycles_Form()
    {
        Write("parent/main.json", ParentSetJson);
        Write("parent/cpu.json", ParentCpuJson);
        var childCpu = """
        {
          "spec_version": "1.0",
          "architecture": {
            "id": "ChildCpu",
            "family": "test",
            "extends": "ParentCpu",
            "extends_path": "../parent/cpu.json",
            "endianness": "little",
            "word_size_bits": 16
          },
          "variants": [],
          "register_file": {
            "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
            "register_pairs": [], "stack_pointer": "A", "status": []
          },
          "exception_vectors": [],
          "instruction_sets": [],
          "instruction_set_diff": {
            "Main": {
              "overrides": {
                "NOP": { "cycles": { "form": "1" } }
              }
            }
          }
        }
        """;
        var path = Write("child/cpu.json", childCpu);
        var loaded = SpecLoader.LoadCpuSpec(path);
        var nop = loaded.InstructionSets["Main"]
            .EncodingGroups.SelectMany(g => g.Formats).SelectMany(f => f.Instructions)
            .First(i => i.Id == "NOP");
        Assert.Equal("1", nop.Cycles!.Form);
        Assert.Equal("ParentCpu", nop.OriginCpu);
        Assert.Equal("ChildCpu", nop.OverriddenBy);

        // HLT untouched
        var hlt = loaded.InstructionSets["Main"]
            .EncodingGroups.SelectMany(g => g.Formats).SelectMany(f => f.Instructions)
            .First(i => i.Id == "HLT");
        Assert.Equal("2", hlt.Cycles!.Form);
        Assert.Null(hlt.OverriddenBy);
    }

    [Fact]
    public void Child_With_Removal_Drops_Instruction()
    {
        Write("parent/main.json", ParentSetJson);
        Write("parent/cpu.json", ParentCpuJson);
        var childCpu = """
        {
          "spec_version": "1.0",
          "architecture": {
            "id": "ChildCpu", "family": "test",
            "extends": "ParentCpu", "extends_path": "../parent/cpu.json",
            "endianness": "little", "word_size_bits": 16
          },
          "variants": [],
          "register_file": {
            "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
            "register_pairs": [], "stack_pointer": "A", "status": []
          },
          "exception_vectors": [],
          "instruction_sets": [],
          "instruction_set_diff": {
            "Main": { "removals": ["NOP"] }
          }
        }
        """;
        var path = Write("child/cpu.json", childCpu);
        var loaded = SpecLoader.LoadCpuSpec(path);
        var ids = loaded.InstructionSets["Main"]
            .EncodingGroups.SelectMany(g => g.Formats).SelectMany(f => f.Instructions)
            .Select(i => i.Id).ToList();
        Assert.DoesNotContain("NOP", ids);
        Assert.Contains("HLT", ids);
    }

    [Fact]
    public void Override_Of_Unknown_Id_Throws()
    {
        Write("parent/main.json", ParentSetJson);
        Write("parent/cpu.json", ParentCpuJson);
        var childCpu = """
        {
          "spec_version": "1.0",
          "architecture": {
            "id": "ChildCpu", "family": "test",
            "extends": "ParentCpu", "extends_path": "../parent/cpu.json",
            "endianness": "little", "word_size_bits": 16
          },
          "variants": [],
          "register_file": {
            "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
            "register_pairs": [], "stack_pointer": "A", "status": []
          },
          "exception_vectors": [],
          "instruction_sets": [],
          "instruction_set_diff": {
            "Main": { "overrides": { "NOPE": { "cycles": { "form": "1" } } } }
          }
        }
        """;
        var path = Write("child/cpu.json", childCpu);
        var ex = Assert.Throws<SpecValidationException>(() => SpecLoader.LoadCpuSpec(path));
        Assert.Contains("'NOPE'", ex.Message);
    }

    [Fact]
    public void Removal_Of_Unknown_Id_Throws()
    {
        Write("parent/main.json", ParentSetJson);
        Write("parent/cpu.json", ParentCpuJson);
        var childCpu = """
        {
          "spec_version": "1.0",
          "architecture": {
            "id": "ChildCpu", "family": "test",
            "extends": "ParentCpu", "extends_path": "../parent/cpu.json",
            "endianness": "little", "word_size_bits": 16
          },
          "variants": [],
          "register_file": {
            "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
            "register_pairs": [], "stack_pointer": "A", "status": []
          },
          "exception_vectors": [],
          "instruction_sets": [],
          "instruction_set_diff": {
            "Main": { "removals": ["NOPE"] }
          }
        }
        """;
        var path = Write("child/cpu.json", childCpu);
        var ex = Assert.Throws<SpecValidationException>(() => SpecLoader.LoadCpuSpec(path));
        Assert.Contains("'NOPE'", ex.Message);
    }

    [Fact]
    public void Cyclic_Inheritance_Throws()
    {
        // A -> B -> A cycle (B claims to extend A, A claims to extend B).
        var aCpu = """
        {
          "spec_version": "1.0",
          "architecture": {
            "id": "A", "family": "test",
            "extends": "B", "extends_path": "../b/cpu.json",
            "endianness": "little", "word_size_bits": 16
          },
          "variants": [],
          "register_file": {
            "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
            "register_pairs": [], "stack_pointer": "A", "status": []
          },
          "exception_vectors": [],
          "instruction_sets": []
        }
        """;
        var bCpu = """
        {
          "spec_version": "1.0",
          "architecture": {
            "id": "B", "family": "test",
            "extends": "A", "extends_path": "../a/cpu.json",
            "endianness": "little", "word_size_bits": 16
          },
          "variants": [],
          "register_file": {
            "general_purpose": { "count": 1, "width_bits": 16, "names": ["A"], "aliases": {}, "pc_index": null },
            "register_pairs": [], "stack_pointer": "A", "status": []
          },
          "exception_vectors": [],
          "instruction_sets": []
        }
        """;
        var pathA = Write("a/cpu.json", aCpu);
        Write("b/cpu.json", bCpu);
        var ex = Assert.Throws<SpecValidationException>(() => SpecLoader.LoadCpuSpec(pathA));
        Assert.Contains("Cyclic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
