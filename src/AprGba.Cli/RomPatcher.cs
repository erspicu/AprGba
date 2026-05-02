namespace AprGba.Cli;

/// <summary>
/// Bypass for the GBA BIOS's Nintendo-logo anti-piracy check. The real
/// BIOS reads the 156 bytes at cart ROM offset 0x004..0x09F and compares
/// against a canonical Nintendo logo sequence baked into the BIOS itself.
/// If they don't match, BIOS halts forever instead of jumping to ROM @
/// 0x08000000. jsmolka / homebrew test ROMs typically have arbitrary
/// bytes in this region.
///
/// We extract the canonical 156 bytes from the loaded BIOS image (the
/// BIOS literally contains its own copy of the logo for comparison
/// purposes — locating it via the 6-byte signature 24 FF AE 51 69 9A,
/// the well-known prefix of the Nintendo logo bitmap).
///
/// We also fix the cart header checksum at offset 0x0BD per GBATEK:
///   chk = -(sum(cart[0xA0..0xBC])) - 0x19  (low byte)
/// Some BIOS versions verify this; can't hurt to be correct.
/// </summary>
public static class RomPatcher
{
    private const int LogoOffset = 0x04;
    private const int LogoLength = 0x9C;     // 156 bytes
    private const int HeaderChecksumOffset = 0xBD;

    /// <summary>
    /// Finds the canonical Nintendo logo inside a BIOS image and patches
    /// the cart's logo bytes + header checksum to match. Returns true if
    /// any bytes were changed.
    /// </summary>
    public static bool EnsureValidLogoAndChecksum(byte[] cartRom, byte[] bios)
    {
        if (cartRom.Length < 0xC0)
            return false;     // ROM too small to even contain the header

        var canonical = ExtractLogoFromBios(bios);
        if (canonical is null) return false;     // BIOS without recognisable logo

        bool changed = false;

        // Patch logo bytes if they don't already match.
        for (int i = 0; i < LogoLength; i++)
        {
            if (cartRom[LogoOffset + i] != canonical[i])
            {
                cartRom[LogoOffset + i] = canonical[i];
                changed = true;
            }
        }

        // Re-compute header checksum at 0xBD over bytes [0xA0..0xBC].
        int sum = 0;
        for (int i = 0xA0; i <= 0xBC; i++) sum += cartRom[i];
        byte chk = (byte)(-(sum + 0x19) & 0xFF);
        if (cartRom[HeaderChecksumOffset] != chk)
        {
            cartRom[HeaderChecksumOffset] = chk;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Search the BIOS for the 6-byte Nintendo-logo prefix
    /// (24 FF AE 51 69 9A) — distinctive enough that any false-positive
    /// match within a 16KB BIOS image is extremely unlikely. Return the
    /// 156-byte slice starting there.
    /// </summary>
    private static byte[]? ExtractLogoFromBios(byte[] bios)
    {
        // GBATEK-documented canonical prefix.
        ReadOnlySpan<byte> prefix = stackalloc byte[]
            { 0x24, 0xFF, 0xAE, 0x51, 0x69, 0x9A };

        for (int i = 0; i + LogoLength <= bios.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < prefix.Length; j++)
            {
                if (bios[i + j] != prefix[j]) { match = false; break; }
            }
            if (match)
            {
                var slice = new byte[LogoLength];
                Array.Copy(bios, i, slice, 0, LogoLength);
                return slice;
            }
        }
        return null;
    }
}
