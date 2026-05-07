// .nes (iNES) ROM file loader.
//
// Header format (16 bytes):
//   0-3  : magic "NES\x1A"
//   4    : PRG ROM size / 16KB
//   5    : CHR ROM size / 8KB (0 = CHR RAM)
//   6    : flags6 — bit 0 mirroring (0 horiz, 1 vert), bit 1 battery, bit 2 trainer, bit 3 four-screen, bits 4-7 mapper low nybble
//   7    : flags7 — bits 0-1 system, bits 2-3 NES2.0 marker, bits 4-7 mapper high nybble
//   8-15 : (NES 1.0) padding zeros (NES 2.0 has more fields)
//
// PRG ROM follows header; CHR ROM follows PRG ROM.

using System;
using System.IO;

namespace AprNes.Cli;

public sealed class NesRom
{
    public byte[] PrgRom { get; init; } = Array.Empty<byte>();
    public byte[] ChrRom { get; init; } = Array.Empty<byte>();
    public int    MapperId { get; init; }
    public bool   Vertical { get; init; }      // true = vertical mirroring, false = horizontal
    public bool   FourScreen { get; init; }
    public bool   HasBattery { get; init; }
    public bool   HasTrainer { get; init; }
}

public static class NesRomLoader
{
    public static NesRom Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 16)
            throw new InvalidDataException($"ROM too small ({bytes.Length} bytes, need at least 16-byte iNES header).");
        if (bytes[0] != 'N' || bytes[1] != 'E' || bytes[2] != 'S' || bytes[3] != 0x1A)
            throw new InvalidDataException("Not an iNES file (missing 'NES\\x1A' magic).");

        int prgBanks  = bytes[4];
        int chrBanks  = bytes[5];
        byte flags6   = bytes[6];
        byte flags7   = bytes[7];

        bool vertical   = (flags6 & 0x01) != 0;
        bool battery    = (flags6 & 0x02) != 0;
        bool hasTrainer = (flags6 & 0x04) != 0;
        bool fourScreen = (flags6 & 0x08) != 0;
        int mapperId    = ((flags6 >> 4) & 0x0F) | (flags7 & 0xF0);

        int prgSize = prgBanks * 16 * 1024;
        int chrSize = chrBanks * 8 * 1024;
        int trainer = hasTrainer ? 512 : 0;
        int offset  = 16 + trainer;

        if (bytes.Length < offset + prgSize + chrSize)
            throw new InvalidDataException(
                $"ROM body too short: header claims {prgSize}+{chrSize} bytes after {offset}-byte header, file is {bytes.Length} bytes total.");

        var prg = new byte[prgSize];
        Buffer.BlockCopy(bytes, offset, prg, 0, prgSize);

        var chr = new byte[chrSize];
        if (chrSize > 0) Buffer.BlockCopy(bytes, offset + prgSize, chr, 0, chrSize);

        return new NesRom
        {
            PrgRom     = prg,
            ChrRom     = chr,
            MapperId   = mapperId,
            Vertical   = vertical,
            FourScreen = fourScreen,
            HasBattery = battery,
            HasTrainer = hasTrainer
        };
    }
}
