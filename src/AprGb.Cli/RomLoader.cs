using System.IO.Compression;

namespace AprGb.Cli;

/// <summary>
/// Loads a Game Boy ROM file from disk. Accepts a raw <c>.gb</c> or a
/// <c>.zip</c> archive containing a single <c>.gb</c> entry. For zip
/// support we use <see cref="System.IO.Compression"/> — no third-party
/// libraries.
/// </summary>
public static class RomLoader
{
    public static byte[] Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("ROM file not found", path);

        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return LoadFromZip(path);

        return File.ReadAllBytes(path);
    }

    private static byte[] LoadFromZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        // DMG-only — we don't model CGB.
        var gbEntry = archive.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".gb", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"No .gb entry found inside {path}");

        using var stream = gbEntry.Open();
        using var ms     = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Read the Game Boy cartridge header (offset 0x0100 onwards) and
    /// return human-readable summary. Used by the CLI's --info mode and
    /// also good for sanity-checking that a ROM loaded correctly.
    /// </summary>
    public static string DescribeHeader(byte[] rom)
    {
        if (rom.Length < 0x150)
            return "(ROM too short to contain a valid header)";

        var title = System.Text.Encoding.ASCII.GetString(rom, 0x134, 16).TrimEnd('\0', ' ');
        var cartType = rom[0x147];
        var romSize  = rom[0x148];
        var ramSize  = rom[0x149];
        var cgbFlag  = rom[0x143];
        return $"title='{title}' cartType=0x{cartType:X2} romSize=0x{romSize:X2} ramSize=0x{ramSize:X2} cgbFlag=0x{cgbFlag:X2}";
    }
}
