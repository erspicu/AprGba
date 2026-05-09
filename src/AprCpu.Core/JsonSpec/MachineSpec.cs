using System.Globalization;
using System.Text.Json;

namespace AprCpu.Core.JsonSpec;

/// <summary>
/// Board-/system-level spec describing the memory map and per-region
/// optimization policy for one machine. Pairs with a <see cref="CpuSpec"/>
/// (referenced by name in <see cref="CpuRef"/>) to fully describe a target
/// (e.g. NES NTSC = 2A03 + nes_ntsc.json memory map).
///
/// Loaded from <c>spec/machines/&lt;name&gt;.json</c>. See
/// <c>MD/design/19-declarative-jit-policy.md</c> for the rationale of
/// keeping this separate from <see cref="CpuSpec"/> — the CPU doesn't know
/// or care what board it's on, so memory-map and mapper concerns belong
/// here, not in <see cref="CpuSpec"/>.
/// </summary>
public sealed record MachineSpec(
    string Name,
    string CpuRef,
    IReadOnlyList<MemoryRegion> MemoryRegions,
    IReadOnlyDictionary<string, uint> InterruptVectors);

/// <summary>
/// One contiguous CPU-bus address range with a uniform handling policy.
/// Regions are open intervals — <c>[addr_start, addr_end_exclusive)</c>.
/// Multiple non-overlapping regions tile the addressable space; addresses
/// outside all regions are open-bus / unmapped.
/// </summary>
public sealed record MemoryRegion(
    string Name,
    uint AddrStart,
    uint AddrEndExclusive,
    MemoryRegionKind Kind,
    uint? MirrorMask,
    bool FastmemEligible,
    bool ForcesEndOfBlock,
    bool SmcNotify,
    bool Writable,
    IReadOnlyList<string> SideEffects);

public enum MemoryRegionKind
{
    Ram,
    Rom,
    Io
}

/// <summary>
/// Load and parse a machine spec JSON file. Mirrors <see cref="SpecLoader"/>
/// pattern: shape-required fields are mandatory, optional fields fall back
/// to sensible defaults, deeper validation lives downstream (or in tests).
/// </summary>
public static class MachineSpecLoader
{
    public static MachineSpec LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"machine spec not found: {path}", path);

        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return Parse(doc.RootElement);
    }

    public static MachineSpec Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("machine spec root must be a JSON object");

        var name = root.GetProperty("name").GetString()
            ?? throw new InvalidDataException("machine spec must have 'name' string");
        var cpuRef = root.GetProperty("cpu").GetString()
            ?? throw new InvalidDataException("machine spec must have 'cpu' string (CPU id)");

        var regions = new List<MemoryRegion>();
        if (root.TryGetProperty("memory_regions", out var regionsEl))
        {
            if (regionsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("'memory_regions' must be an array");
            foreach (var r in regionsEl.EnumerateArray())
                regions.Add(ParseMemoryRegion(r));
        }

        var vectors = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (root.TryGetProperty("interrupt_vectors", out var vecEl))
        {
            if (vecEl.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("'interrupt_vectors' must be an object");
            foreach (var v in vecEl.EnumerateObject())
                vectors[v.Name] = ParseUint(v.Value, $"interrupt_vectors.{v.Name}");
        }

        return new MachineSpec(name, cpuRef, regions, vectors);
    }

    private static MemoryRegion ParseMemoryRegion(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("memory_regions[] entries must be objects");

        var name = el.GetProperty("name").GetString()
            ?? throw new InvalidDataException("memory region must have 'name' string");
        var addrStart = ParseUint(el.GetProperty("addr_start"),
            $"memory_regions[{name}].addr_start");
        var addrEnd = ParseUint(el.GetProperty("addr_end_exclusive"),
            $"memory_regions[{name}].addr_end_exclusive");

        if (addrEnd <= addrStart)
            throw new InvalidDataException(
                $"memory_regions[{name}]: addr_end_exclusive (0x{addrEnd:X}) must be > addr_start (0x{addrStart:X})");

        var kindStr = el.GetProperty("type").GetString()
            ?? throw new InvalidDataException(
                $"memory_regions[{name}] must have 'type' string");
        var kind = kindStr.ToLowerInvariant() switch
        {
            "ram" => MemoryRegionKind.Ram,
            "rom" => MemoryRegionKind.Rom,
            "io"  => MemoryRegionKind.Io,
            _ => throw new InvalidDataException(
                $"memory_regions[{name}].type must be one of ram/rom/io; got '{kindStr}'")
        };

        uint? mirrorMask = null;
        if (el.TryGetProperty("mirror_mask", out var mmEl))
            mirrorMask = ParseUint(mmEl, $"memory_regions[{name}].mirror_mask");

        var fastmem = el.TryGetProperty("fastmem_eligible", out var fmEl)
            ? fmEl.GetBoolean() : false;
        var forcesEnd = el.TryGetProperty("forces_end_of_block", out var feEl)
            ? feEl.GetBoolean() : kind == MemoryRegionKind.Io;
        var smcNotify = el.TryGetProperty("smc_notify", out var smcEl)
            ? smcEl.GetBoolean() : kind == MemoryRegionKind.Ram;
        var writable = el.TryGetProperty("writable", out var wEl)
            ? wEl.GetBoolean() : kind != MemoryRegionKind.Rom;

        var sideEffects = new List<string>();
        if (el.TryGetProperty("side_effects", out var seEl))
        {
            if (seEl.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(
                    $"memory_regions[{name}].side_effects must be a string array");
            foreach (var s in seEl.EnumerateArray())
            {
                var str = s.GetString();
                if (str != null) sideEffects.Add(str);
            }
        }

        return new MemoryRegion(
            name, addrStart, addrEnd, kind, mirrorMask,
            fastmem, forcesEnd, smcNotify, writable, sideEffects);
    }

    /// <summary>Parse a JSON value as uint — accepts integer, decimal string, or "0x..." hex string.</summary>
    private static uint ParseUint(JsonElement el, string path)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return el.GetUInt32();
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString()!;
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt32(s.Substring(2), 16);
            return uint.Parse(s, CultureInfo.InvariantCulture);
        }
        throw new InvalidDataException(
            $"{path}: expected uint (number or hex string), got {el.ValueKind}");
    }
}
