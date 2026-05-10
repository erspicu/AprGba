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
    public void Real_I80186_Spec_Loads_And_Inherits_From_I8086()
    {
        // Sprint 25.3 — load the actual i80186 spec from the repo and
        // verify it inherits from i8086 + adds the new opcodes.
        var repoRoot = LocateRepoRoot();
        var i80186Cpu = Path.Combine(repoRoot, "spec", "x86-16", "i80186", "cpu.json");
        Assert.True(File.Exists(i80186Cpu), $"i80186 cpu.json missing at {i80186Cpu}");

        var loaded = SpecLoader.LoadCpuSpec(i80186Cpu);

        Assert.Equal("Intel80186", loaded.Cpu.Architecture.Id);
        Assert.Equal("Intel8086", loaded.Cpu.Architecture.Extends);

        // Main set must contain parent's 149 instructions PLUS i80186 additions.
        var main = loaded.InstructionSets["Main"];
        var allInstr = main.EncodingGroups
            .SelectMany(g => g.Formats)
            .SelectMany(f => f.Instructions)
            .ToList();
        Assert.True(allInstr.Count >= 149 + 14,
            $"expected at least 163 instructions (149 i8086 + 14 i80186-new), got {allInstr.Count}");

        // Sample i8086 entries inherited (with provenance).
        var addRm8R8 = allInstr.First(i => i.Id == "ADD_rm8_r8");
        Assert.Equal("Intel8086", addRm8R8.OriginCpu);
        Assert.Null(addRm8R8.OverriddenBy);

        // Sample i80186 additions present (with provenance = Intel80186).
        var pushaIds = allInstr.Where(i => i.Id == "PUSHA_noargs").ToList();
        Assert.Single(pushaIds);
        Assert.Equal("Intel80186", pushaIds[0].OriginCpu);

        var enterIds = allInstr.Where(i => i.Id == "ENTER_imm16_imm8").ToList();
        Assert.Single(enterIds);
        Assert.Equal("Intel80186", enterIds[0].OriginCpu);

        // PUSH_SP_i80186 added (parent's PUSH_reg16 still present too).
        var pushSpI80186 = allInstr.FirstOrDefault(i => i.Id == "PUSH_SP_i80186");
        Assert.NotNull(pushSpI80186);
        Assert.Equal("Intel80186", pushSpI80186!.OriginCpu);

        var pushReg16Parent = allInstr.FirstOrDefault(i => i.Id == "PUSH_reg16");
        Assert.NotNull(pushReg16Parent);
        Assert.Equal("Intel8086", pushReg16Parent!.OriginCpu);

        // i80186 shift-imm group: 7 entries × 2 sizes (8/16) = 14 total
        var shiftImmCount = allInstr.Count(i =>
            i.Id is not null && (i.Id.Contains("rm8_imm8_sel") || i.Id.Contains("rm16_imm8_sel"))
            && i.OriginCpu == "Intel80186");
        Assert.Equal(14, shiftImmCount);
    }

    private static string LocateRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, "spec")) && Directory.Exists(Path.Combine(dir, "src")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Real_I80286_Spec_Loads_Through_Depth_3_Chain()
    {
        // Sprint 26.1 — load the actual i80286 spec from the repo and
        // verify the chain depth=3 (i80286 -> i80186 -> i8086) works.
        // 26.2b — also verifies the new instruction_sets_added mechanism:
        // i80286 introduces a TwoByteEsc set (parent didn't have it) for
        // 0x0F-prefixed system instructions.
        var repoRoot = LocateRepoRoot();
        var i80286Cpu = Path.Combine(repoRoot, "spec", "x86-16", "i80286", "cpu.json");
        Assert.True(File.Exists(i80286Cpu), $"i80286 cpu.json missing at {i80286Cpu}");

        var loaded = SpecLoader.LoadCpuSpec(i80286Cpu);

        Assert.Equal("Intel80286", loaded.Cpu.Architecture.Id);
        Assert.Equal("Intel80186", loaded.Cpu.Architecture.Extends);

        // Main set inherited intact: 149 i8086 + 26 i80186 = 175 instructions.
        var main = loaded.InstructionSets["Main"];
        var mainInstr = main.EncodingGroups
            .SelectMany(g => g.Formats)
            .SelectMany(f => f.Instructions)
            .ToList();
        Assert.True(mainInstr.Count >= 175,
            $"expected >= 175 instructions in Main (149 i8086 + 26 i80186), got {mainInstr.Count}");

        // 26.2b — TwoByteEsc set is the i80286-specific addition.
        Assert.True(loaded.InstructionSets.ContainsKey("TwoByteEsc"),
            "i80286 should have a TwoByteEsc set added via instruction_sets_added");
        var twoByte = loaded.InstructionSets["TwoByteEsc"];
        var twoByteInstr = twoByte.EncodingGroups
            .SelectMany(g => g.Formats)
            .SelectMany(f => f.Instructions)
            .ToList();
        Assert.NotEmpty(twoByteInstr);
        var clts = twoByteInstr.First(i => i.Id == "CLTS_noargs");
        Assert.Equal("Intel80286", clts.OriginCpu);  // originated at i80286

        // Provenance flows through three levels in Main:
        var addRm8R8 = mainInstr.First(i => i.Id == "ADD_rm8_r8");
        Assert.Equal("Intel8086", addRm8R8.OriginCpu);  // originally from base

        var pushaI80186 = mainInstr.First(i => i.Id == "PUSHA_noargs");
        Assert.Equal("Intel80186", pushaI80186.OriginCpu);  // added at i80186
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
