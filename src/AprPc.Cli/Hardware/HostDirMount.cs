// HostDirMount — Phase 32.3 host-directory mount (skeleton).
//
// Goal: expose a host filesystem path as a DOS drive letter inside
// AprPc, without requiring an .img file. DOSBox-style ergonomics.
//
// Architecture (per Gemini consult 2026-05-18, knowledgebase/
// message/20260518_184350.txt):
//   - **NOT** an INT 21h trap. We boot real FreeDOS, which maintains
//     internal SDA / SFT state that desyncs if we intercept INT 21h
//     directly. INT 21h trap is what DOSBox uses, but DOSBox is an
//     HLE-kernel emulator, not a full-system emulator.
//   - **V1 vvfat (this skeleton)**: synthesize a FAT16 boot sector +
//     FAT tables + root directory from the host directory in memory,
//     expose as a virtual HDD via HLE INT 13h. Read-only. The guest
//     sees a normal FAT16 disk. Works with any DOS without modification.
//   - **V2 Guest TSR (deferred)**: write an x86-16 .SYS / .COM that
//     hooks INT 2Fh AX=1100h Redirector, communicates with C# via a
//     custom backdoor port. Adds write-back and long filename support.
//     Same pattern VirtualBox shared folders use.
//
// This file is the V1 skeleton — CLI flag wiring + synthesis stub.
// Full FAT16 synthesizer + read-back paths are TODO; see plan doc
// MD/issue/pc/hdd-mount-swap-plan.md §32.3 for the staged sprint list.

namespace AprPc.Cli.Hardware;

/// <summary>
/// Phase 32.3 — host-directory mount as virtual FAT16 disk.
/// Skeleton stage: parses CLI flag, allocates synthetic-disk buffer,
/// stubs the boot sector + empty FAT. Real directory scanning, FAT
/// chain build, and LFN aliasing are sprint 32.3a-c work.
/// </summary>
public sealed class HostDirMount
{
    /// <summary>Drive letter (e.g. 'D') the guest sees.</summary>
    public char DriveLetter { get; }

    /// <summary>Host directory rooted at this path.</summary>
    public string HostPath { get; }

    /// <summary>Read-write enabled? Default false; toggled by ":rw" suffix on CLI flag.</summary>
    public bool ReadWrite { get; }

    /// <summary>
    /// Synthetic FAT16 disk size (default 32 MB, fits the DOS 3.3
    /// per-partition max). Configurable via --mount=DRV:path:SIZE_MB.
    /// </summary>
    public int SizeMB { get; }

    private HostDirMount(char drv, string path, bool rw, int sizeMB)
    {
        DriveLetter = drv;
        HostPath    = path;
        ReadWrite   = rw;
        SizeMB      = sizeMB;
    }

    /// <summary>
    /// Parse --mount=DRV:host\path[:ro|:rw][:SIZE_MB]. Examples:
    ///   --mount=D:C:\Users\me\dos-stuff           (D:, read-only, 32MB)
    ///   --mount=E:.\local-files:rw                (E:, read-write, 32MB)
    ///   --mount=F:./build-output:ro:128           (F:, read-only, 128MB)
    /// Returns null if the spec is malformed (caller should warn + skip).
    /// </summary>
    public static HostDirMount? TryParse(string spec)
    {
        if (string.IsNullOrEmpty(spec) || spec.Length < 3 || spec[1] != ':') return null;
        char drv = char.ToUpperInvariant(spec[0]);
        if (drv < 'C' || drv > 'Z') return null;
        // Split the rest carefully: host path may contain ':' on Windows.
        // Treat trailing ":ro" / ":rw" / ":N" tokens specially.
        string rest = spec.Substring(2);
        bool rw = false;
        int sizeMB = 32;
        while (true)
        {
            int colon = rest.LastIndexOf(':');
            if (colon <= 0) break;
            string suffix = rest.Substring(colon + 1).Trim();
            if (string.Equals(suffix, "ro", StringComparison.OrdinalIgnoreCase))
                { rest = rest.Substring(0, colon); rw = false; continue; }
            if (string.Equals(suffix, "rw", StringComparison.OrdinalIgnoreCase))
                { rest = rest.Substring(0, colon); rw = true; continue; }
            if (int.TryParse(suffix, out int n) && n > 0 && n <= 2048)
                { rest = rest.Substring(0, colon); sizeMB = n; continue; }
            break;
        }
        if (rest.Length == 0) return null;
        return new HostDirMount(drv, rest, rw, sizeMB);
    }

    /// <summary>
    /// Build the synthetic FAT16 image as a flat byte[].
    /// Sprint 32.3a stub — returns an all-zero image of the requested
    /// size. Real synthesis (boot sector + BPB + FATs + root dir +
    /// data area following host directory contents) is the next
    /// sprint per the plan doc.
    /// </summary>
    public byte[] Synthesize()
    {
        long bytes = (long)SizeMB * 1024 * 1024;
        var img = new byte[bytes];
        // TODO 32.3a: write FAT16 boot sector at offset 0x0000.
        // TODO 32.3b: scan HostPath, build dir entries + FAT chain.
        // TODO 32.3c: optional write-back if ReadWrite (vvfat dirty
        //             cluster tracking — see plan doc §32.3).
        return img;
    }
}
