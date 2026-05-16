# pcxtbios — Super PC/Turbo XT BIOS source

**Upstream**: https://github.com/virtualxt/pcxtbios
**Authors**: Jon Petrosky (Plasma), Ya'akov Miles
**Original base**: Taiwanese Generic Turbo XT BIOS (reverse-engineered)
**Version snapshot**: v3.1 (10-28-2017), fetched 2026-05-16.
**License**: **public domain** for `pcxtbios.asm` + assembled `pcxtbios.bin`.

(See https://github.com/virtualxt/pcxtbios for the full README + the other
public-domain ROM tools the project ships. Third-party components in the
upstream repo — Watcom, XT-IDE, Sergey's Floppy BIOS — are NOT included
here; only the BIOS source itself.)

## Why this is in the repo

- `pcxtbios.bin` is the BIOS image we load via `apr-pc --bios=...` for
  end-to-end real-BIOS testing (Phase 30).
- When emulator behaviour disagrees with reality, the source assembly is
  the **ground truth** — preferred over Gemini consultations or third-
  party PC hardware spec documents (which describe IBM 5160 BIOS not
  pcxtbios specifically).
- Having a local copy lets us grep the source directly without
  WebFetching every time, and pins the version so spec answers don't
  drift if upstream changes.

## What's compiled from this

- `BIOS/firmware/pcxtbios.bin` — the assembled ROM image we actually
  load. Built from this source by the upstream project. We don't
  reassemble — just use the binary.

## Companion docs

- [`MD/ref/pcxtbios-device-spec.md`](../../MD/ref/pcxtbios-device-spec.md) —
  device-by-device handbook digested from this source.
- [`MD/design/30-fdc-dma-plan.md`](../../MD/design/30-fdc-dma-plan.md) —
  Phase 30 implementation plan that uses this BIOS as the integration
  test bed.

## When you'd update this snapshot

If upstream pcxtbios releases a new version AND we want to test against
it: `cd ref/pcxtbios && curl -fL -o pcxtbios.asm https://raw.githubusercontent.com/virtualxt/pcxtbios/master/pcxtbios.asm`,
re-fetch the assembled `.bin`, drop into `BIOS/firmware/pcxtbios.bin`,
re-run T1 + real-BIOS smoke. Pin the version in this README's
"Version snapshot" line.
