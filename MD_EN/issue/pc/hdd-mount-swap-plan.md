# AprPc — Virtual HDD + Host-dir mount + Floppy swap plan

> **Status**: 📋 PLANNED (2026-05-18). Three related storage features.
> Integrated with Gemini architectural review at
> `tools/knowledgebase/message/20260518_184350.txt`.
>
> **Trigger**: CheckIt 3.0 multi-disk install can't proceed (floppy is
> A:/B: only, no swap); FreeDOS install has the same constraint; once
> installed, programs run off floppy slowly with no persistent store.
>
> **Key corrections from Gemini review** (avoid re-discovering these):
> 1. **HDD INT 41h/46h FDPT is mandatory** (CheckIt, FDISK, SpinRite
>    read it directly, not via INT 13h AH=08).
> 2. **HDDs > 504 MB require LBA extensions** or DOS truncates/crashes.
> 3. **FreeDOS actively probes INT 13h AH=41h LBA**; if not supported,
>    must set CF=1 + AH=01h or boot will hang.
> 4. **Host-dir mount cannot use INT 21h trap** (real DOS has SDA/SFT
>    internal state that desyncs). Use vvfat or Guest TSR + INT 2Fh
>    Redirector.
> 5. **Floppy swap MUST assert DSKCHG** (port 0x3F7 bit 7), otherwise
>    DOS writes disk 1's cached FAT to disk 2 — **permanent corruption**.

## Implementation order

| Phase | Feature | Effort | Unblocks |
|---|---|---|---|
| **32.1** | Floppy swap hotkey + DSKCHG | ~1 day | CheckIt 2-disk, FreeDOS install 4-disk |
| **32.2** | Virtual HDD (HLE INT 13h, FDPT, LBA stub) | ~2-3 day | CheckIt persistent install, FreeDOS C: |
| **32.2g** | Real-BIOS INT 13h hijack for HDD (chain floppy to pcxtbios; HDD to HLE) | ~0.5-1 day | **FreeDOS / CheckIt / FDISK can see HDD** |
| **32.3** | Host-dir mount (vvfat V1 read-only, TSR V2 read-write) | ~2+ week | dev-loop QoL, host/guest file transfer |

(Full design + sprint breakdown in 中文 [`MD/issue/pc/hdd-mount-swap-plan.md`](../../../MD/issue/pc/hdd-mount-swap-plan.md).)

## Cross-references

- Gemini consult: `tools/knowledgebase/message/20260518_184350.txt`
- Existing disk infra:
  - `src/AprPc.Cli/Hardware/DiskImage.cs`
  - `src/AprPc.Cli/Hardware/Fdc8272.cs`
  - `src/AprPc.Cli/Bios/HleBios.cs` (INT 13h HLE)
  - `src/AprPc.Cli/PcSystemRunner.cs` (MountDisk wiring)
- Existing deferred list: [`MD/issue/pc/deferred-26-30x-summary.md`](deferred-26-30x-summary.md)
