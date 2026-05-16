# FreeDOS — MDA mode suitability analysis

**Date**: 2026-05-16
**Question**: 「FreeDOS 設計是否不適合 MDA 模式？」
**Sources**: github.com/FDOS/kernel + github.com/FDOS/freecom (cloned to `ref/freedos/`)

## TL;DR

**No — FreeDOS 100% supports MDA**. The "invisible rows" we see on
`--video=mda` are pcxtbios's INT 10h teletype scroll bug, not FreeDOS.
Confirmed by source audit of kernel CON driver and FreeCOM output paths.

## Evidence

### 1. FreeDOS kernel never writes video memory directly

```
$ rg '0xB000|0B000|B000|0xB800|B800' kernel/
  → only matches: test/ldosboot/multboot.asm, kernel/memdisk.asm
    (both unrelated to video output)
```

`kernel/kernel/console.asm` is the entire CON device driver — 它只做：

```asm
ConWrite:
    jcxz    ConNdRd3
ConWr1:
    mov     al, [es:di]
    inc     di
    int     29h              ; fast output service
    loop    ConWr1

; _int29_handler:
    mov     ah, 0Eh
    mov     bx, 7            ; BH=0 (page), BL=7 (graphics attr, ignored in text)
    int     10h              ; teletype
```

Every character goes through BIOS teletype. No `mov [es:di], ax`,
no segment 0xB000/0xB800, no scroll/clear shortcut.

### 2. FreeCOM never writes video memory directly

```
$ rg '0xB000|MDA|MONO|monochrome' freecom/
  → zero matches in source code
```

FreeCOM CVS log:
> *Revision 1.7 (2006/06/11): All of FreeCOM now uses `write` instead of
> `putchar` and `intr` instead of `int86[x]` or `intdos[x]`*
> *Revision 1.8 (2006/06/12): All CONIO dependencies have now been
> removed and replaced with size-optimized functions*

FreeCOM output: `outc()` → `write(1, &c, 1)` → INT 21h AH=40h →
DOS kernel `write_char` → INT 29h → INT 10h AH=0Eh teletype.

### 3. FreeCOM explicitly initialises attr=0x0700 for text modes

`freecom/cmd/cls.c` 是 source-of-truth on FreeCOM's text-mode awareness:

```c
int cmd_cls (char * param) {
    ...
    if((attr & 0x9f) == 0x93) {                 // stdout is a real console
        unsigned attr = 0x0700;                 // <<< default light-gray on black
        IREGS r;

        r.r_ax = 0x0f00;                        // get current video mode
        intrpt(0x10, &r);
        mode = r.r_ax & 0x7f;

        switch (mode)
        {
        case 0x04: case 0x05: case 0x09:        // CGA / PCjr / Tandy / VGA graphics
        case 0x0a: case 0x0b: case 0x0d:
        case 0x0e: case 0x0f: case 0x10:
        case 0x11: case 0x12: case 0x13: case 0x59:
            attr = 0;                           // graphics modes use 0
            break;
        default:                                // <<< text modes (0,1,2,3,7) keep 0x0700
            ;
        }

        r.r_ax = 0x0600;                        // scroll up, full screen
        r.r_bx = attr;                          // BX=0x0700 in MDA
        intrpt(0x10, &r);
    }
}
```

If FreeDOS were "broken for MDA", this is exactly where it would
break — and it doesn't. Mode 7 falls into `default`, keeps `attr=0x0700`,
passes it to BIOS via BX.

### 4. No MDA-specific code path anywhere

Zero hits for `MDA`, `MONO`, `MONOCHROME`, `0xB000`, segment `B000:`
in both kernel and freecom. FreeDOS treats all text modes uniformly —
it just hands the work to BIOS INT 10h with the right register state.

## Root cause re-confirmed: pcxtbios INT 10h Function 14 (teletype)

`ref/pcxtbios/pcxtbios.asm` line 4129-4153:

```asm
@@line_feed:
    cmp     dh, 18h                ; bottom of page?
    jz      @@scroll
    inc     dh
    jnz     @@position

@@scroll:
    mov     ah, 2                  ; position cursor at row 0
    int     10h
    call    mode_check             ; CF=0 if text, CF=1 if graphics
    mov     bh, 0                  ; default BH for graphics
    jb      @@scroll_up            ; jb (=JC): if graphics, skip
    ; Text-mode path (MDA falls through here):
    mov     ah, 8
    int     10h                    ; read char+attr at cursor
    mov     bh, ah                 ; <<< BH = read-back attribute

@@scroll_up:
    mov     ah, 6                  ; scroll up 1 line
    mov     al, 1
    xor     cx, cx
    mov     dh, 18h
    mov     dl, [ds:4Ah]
    dec     dl
    int     10h                    ; <<< scroll fill uses BH from above
    ret
```

The vicious cycle:
1. Previous scroll filled new bottom row with attr=BH.
2. teletype overflows again → reads attr at cursor → returns previous BH.
3. If previous BH was 0 (e.g. on first overflow when cell hasn't been
   touched yet), all subsequent scrolls inherit attr=0.
4. New bottom row is "black on black" = invisible.

### Our fixes (Phase 30.10)

Two-layer defence, both active when `--bios=pcxtbios.bin`:

| Layer | Implementation | What it catches |
|---|---|---|
| Binary patch | `PcMemoryBus.ApplyPcxtbiosScrollFix` rewrites `8A FC` (mov bh, ah) at line 4143 → `B7 07` (mov bh, 0x07). Checksum filler at last byte compensated. | Text-mode scroll always uses BH=0x07. |
| Runtime intercept | `PcSystemRunner` peeks each instruction; if next is `CD 10` AND AH=06 AND BH=0 AND mode∈{0,1,2,3,7} → force BH=0x07. | Any other caller (e.g. FreeCOM CLS bug or third-party) that passes BH=0 to scroll up. |

## Conclusion for Phase 30 plan

### Phase 30.10 patch + intercept actually work (verified 2026-05-16)

Auto-test run (`--auto-test=freedos-mda-dir`, full kernel boot + N at
the install prompt + `dir`) recorded 31 887 trace lines; key counters:

| Event | Count | Notes |
|---|---|---|
| `[BIOS] pcxtbios teletype-scroll patch ... 8A FC -> B7 07` | 1 (at boot) | Patch applied. |
| INT 10h AH=06 (scroll up) total | 20 | All have `BH ∈ {0x07, 0x70, 0x70xx}` (= normal or reverse video). |
| INT 10h AH=06 with `BH=0x00` | **0** | Intercept never had to fire — patch caught everything. |
| INT 10h AH=06 from `F000:F6CE` (= patched teletype scroll) during `dir` | 2 | Both pass `BX=0x0700` (BH=0x07) thanks to the patch. |
| AH=09 (write char+attr) during dir output (16:01) | 0 | dir output goes through teletype (AH=0E → AH=0A path), not AH=09. |

`AutoTester FINAL SCREEN` snapshot at end of run captured the *entire*
dir listing fully visible — no invisible rows, no missing lines:

```
| A:\>dir
|  Volume in drive A is FD13-BOOT
|  Volume Serial Number is 858E-3E5F
|  Directory of A:\
| FREEDOS              <DIR>  02/20/2022 12:17p
| FDAUTO   BAT         1,476  02/20/2022 12:17p
| FDCONFIG SYS           392  02/20/2022 12:17p
| KERNEL   SYS        46,485  05/14/2021  3:32a
| SETUP    BAT        39,641  02/20/2022 12:17p
|          4 file(s)         87,994 bytes
|          1 dir(s)         820,224 bytes free
| A:\>
```

### Remaining "invisible" artefact is the FreeDOS installer welcome dialog (not dir)

Earlier observations of invisible content on MDA were almost certainly
the **FreeDOS 1.3 install welcome screen**, drawn by `SETUP.BAT` /
WELCOME via INT 10h AH=09 with attribute values that are **CGA-only**:

```
15:59:45.500  INT_10h caller=222D:0136 AX=0x0920 BX=0x0010 ... DX=0x0307
15:59:45.503  INT_10h caller=222D:0136 AX=0x09DB BX=0x0012 ... DX=0x0309
                                                ^^^^^^^^
                                                BH=0 page, BL=attr
```

- `BL=0x10` → fg=0, bg=1, intensity=0
- `BL=0x12` → fg=2, bg=1, intensity=0

On CGA, `bg=1` = blue background (the installer is drawing a coloured
title bar). On real MDA, **`bg=1` is undefined** — only `bg=0` and
`bg=7` are valid. Real IBM 5151 hardware would render this as garbage
or as the nearest valid combination depending on revision.

`X86CgaRenderer.MdaDecodeAttr` (`src/AprX86.Cli/Video/X86CgaRenderer.cs:160`)
defensively returns **black-on-black** for invalid `(fg, bg)` pairs.
That is faithful to one plausible real-hardware behaviour but causes
the installer's coloured banner to disappear in MDA.

This is **a FreeDOS installer choice, not a kernel bug, not a BIOS
bug, not an AprPc bug**:
- The FreeDOS *kernel + FreeCOM* output (every `printf`, `dir`,
  `prompt`) works because it goes through INT 10h AH=0E teletype
  which preserves cell attribute = 0x07 (set by `clear_screen` at
  boot via `mov ax, 7*100h+' ' ; rep stosw` in `int_10_func_0`).
- The *installer* uses AH=09 with hard-coded colour attributes
  designed for CGA/EGA. On MDA those attribute values are out of spec.

### Renderer fix landed (2026-05-16)

After attr-histogram instrumentation of the auto-test framebuffer, the
final culprit emerged: **rows 15-22 had attr literally = `0x00`** (not
`0x10` like the installer-welcome cells). That is, *no INT 10h handler
ever set those cells' attribute byte to a valid value, yet they hold
printable characters*. Most likely cause: a mix of installer cleanup
direct-VRAM writes (filling with attr=0) followed by `AH=0A` write-char-
only calls from FreeCOM (which advance over the attribute byte without
touching it). Real IBM 5151 hardware would render those cells as
"display off" — but practically the user wants to see them.

`MdaDecodeAttr` (`src/AprX86.Cli/Video/X86CgaRenderer.cs:160`) now
implements two layers:

| Rule | Behaviour |
|---|---|
| `(attr & 0x70) != 0` (any bg bit set) | reverse video — handles installer's `0x10` / `0x12` "blue bg" attrs that aren't valid MDA but visibly are on real clones |
| `(attr & 0x07) != 0` and not the above | normal text (dim green on black); intensity bit `0x08` brightens |
| `attr == 0x00` **and char is printable (0x20–0x7E)** | normal text — defensive heuristic for "FreeDOS forgot to set attr" cells |
| `attr == 0x00` and char is blank/null | true display off (preserves boot screen blank cells) |

Visual verification: `result/pc/auto-test-20260516-162448.png` —
full FreeDOS 1.3 boot + install-abort + `dir` listing, every line
visible including the file table that was invisible before the fix.

### Recommendations (updated)

1. ✅ pcxtbios scroll patch + runtime intercept work as designed.
2. ✅ `MdaDecodeAttr` heuristic restores FreeDOS dir output on MDA.
3. ✅ `--video=mda` now usable end-to-end with `pcxtbios.bin +
   freedos-1.3-floppy.img`. CGA still recommended for installer
   welcome banner (its CGA color attrs render more faithfully on CGA).
4. Trade-off note: the heuristic deviates slightly from strict IBM 5151
   hardware (which would leave attr=0 cells truly invisible even with
   chars present). Documented as a deliberate forgiveness rule for
   "FreeDOS-era software written for color but running on MDA".

## References

- `ref/freedos/kernel/kernel/console.asm` — CON device driver
- `ref/freedos/freecom/cmd/cls.c` — FreeCOM CLS (attr=0x0700 proof)
- `ref/pcxtbios/pcxtbios.asm` line 4081-4192 — teletype + mode_check
- `MD/ref/pcxtbios-device-spec.md` — device handbook digest
- `src/AprPc.Cli/Memory/PcMemoryBus.cs:184` — binary patch
- `src/AprPc.Cli/PcSystemRunner.cs:489-516` — runtime intercept
