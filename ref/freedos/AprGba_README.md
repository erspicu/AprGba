# FreeDOS source — local reference snapshot

Cloned 2026-05-16 for offline analysis of the MDA-mode "invisible row"
behaviour observed in AprPc Phase 30.10 / 30.11.

## Provenance

| Component | Upstream | License |
|---|---|---|
| `kernel/`  | https://github.com/FDOS/kernel  | GPL v2 |
| `freecom/` | https://github.com/FDOS/freecom | GPL v2 |

Both cloned with `--depth 1` then `.git/` stripped — these are
read-only references, not vendored dependencies. Do not modify in-place.

## Why this is here

Phase 30.10 (HLE intercept INT 10h scroll bug) closed pcxtbios's own
teletype scroll bug, but `dir` output on MDA still has invisible rows.
Trace shows char/attr writes hitting `0xB000:xxxx` with `attr=0x00`,
which does not come from `pcxtbios.asm` (its scroll fill uses
`BL=attribute` from caller). The writer must be FreeDOS itself —
either kernel CON driver or FreeCOM's output path.

This snapshot is for grepping that code path. See
`MD/ref/freedos-mda-analysis.md` for the conclusion.

## What we will NOT do

- Vendor or build from this tree. We continue to use the released
  `freedos-1.3-floppy.img` shipped under `BIOS/`.
- Patch this tree. If FreeDOS needs a fix, it goes upstream, not here.
- Add to the runtime data flow — `ref/` is for human/AI reading only.
