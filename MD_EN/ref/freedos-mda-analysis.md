# FreeDOS — MDA mode suitability analysis (EN mirror)

> Brief mirror of `MD/ref/freedos-mda-analysis.md` per CLAUDE.md
> bilingual-docs rule. See the zh-TW version for full evidence quotes.

**Question**: is FreeDOS unsuited to MDA by design?
**Answer**: No. FreeDOS kernel + FreeCOM make zero direct VRAM writes
and treat all text modes the same — `cls.c` even explicitly sets
`attr = 0x0700` for text modes (including mode 7 = MDA). Source audit
of `ref/freedos/kernel/kernel/console.asm` (CON device driver →
`int 29h` → `int 10h ah=0Eh` teletype path) and `ref/freedos/freecom/`
confirms.

**Root cause of the MDA invisibility we saw**: a stack of three issues
peeling off in layers:

1. `pcxtbios.asm` teletype scroll bug (line 4143 `mov bh, ah` after
   `AH=08` re-read). Patched at BIOS-load time by
   `PcMemoryBus.ApplyPcxtbiosScrollFix` + runtime intercept of any
   `INT 10h AH=06 BH=0` (Phase 30.10).
2. FreeDOS 1.3 installer welcome banner draws with CGA-coloured attrs
   (`0x10`, `0x12`) that are out-of-spec on real MDA. Real IBM 5151
   hardware shows them as reverse video; we now match that.
3. After the installer aborts, rows 15–22 hold dir-output chars but
   with `attr = 0x00` (some combination of installer cleanup + FreeCOM
   `AH=0A` write-char-only paths that never reset attr). Real MDA
   would render these "display off" — but the user expects to see
   them.

`X86CgaRenderer.MdaDecodeAttr` now applies:

| `(attr & 0x70) != 0` | reverse video (handles `0x10` / `0x12` etc.) |
| `(attr & 0x07) != 0` | normal text (intense if bit 3 set)           |
| `attr == 0x00` + printable char | normal text (FreeDOS forgiveness) |
| `attr == 0x00` + blank char | true display off                         |

Verified end-to-end with `--auto-test=freedos-mda-dir` —
`result/pc/auto-test-20260516-162448.png` shows full boot + install
abort + `dir` listing fully visible.
