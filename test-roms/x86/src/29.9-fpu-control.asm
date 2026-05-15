; 29.9-fpu-control.asm — Phase 29.9 control op coverage.
;
; Test 1 — FSTCW after FNINIT: stored value should be 0x037F.
;   FNINIT → FSTCW [cw_save] → check [cw_save] == 0x037F.
;
; Test 2 — FLDCW round-trip: write a custom CW pattern, FLDCW it,
;   FSTCW it back, verify byte-identical.
;   FLDCW [cw_custom] (= 0x0E72) → FSTCW [cw_readback].
;
; Test 3 — FNCLEX: set up FPU_SW with a synthetic exception flag
;   (via a synthetic divide-by-zero), then FNCLEX, verify flags
;   cleared.
;   Actually generating an FPU exception with masked exceptions is
;   tricky without #MF delivery. Skip Test 3 for now — the FNCLEX
;   implementation is straightforward IR (RMW mask) and will be
;   verified once 29.7 transcendentals or 29.10 m80fp arrive with
;   real exception generation paths.

bits 16
org 0x0100

start:
    ; ===== Test 1: FNINIT then FSTCW =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0x3E                       ; FSTCW [cw_save] (modrm = 00_111_110, /7)
    dw cw_save

    ; ===== Test 2: FLDCW custom + FSTCW round-trip =====
    db 0xD9, 0x2E                       ; FLDCW [cw_custom] (modrm = 00_101_110, /5)
    dw cw_custom
    db 0xD9, 0x3E                       ; FSTCW [cw_readback]
    dw cw_readback

    ; ===== Verification — load both saved CWs into GPRs =====
    mov ax, [cw_save]                   ; expect 0x037F (FNINIT default)
    mov bx, [cw_custom]                 ; expect 0x0E72 (original custom value)
    mov cx, [cw_readback]               ; expect 0x0E72 (FSTCW after FLDCW round-trip)
    hlt

; ---- Data ----
cw_custom:   dw 0x0E72                  ; Custom CW: PC=10 (53-bit), RC=11 (truncate),
                                        ;            IM=0 (Invalid unmasked), DM/ZM/OM/UM/PM masked
cw_save:     dw 0xCAFE                  ; will be overwritten by Test 1 FSTCW
cw_readback: dw 0xCAFE                  ; will be overwritten by Test 2 FSTCW
