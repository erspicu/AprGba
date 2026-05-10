; ----------------------------------------------------------------------
; Phase 27b — segment-not-present fault demo
;
; Same prologue + same selector as 27-pmode-entry, but GDT[1] has the
; P (present) bit cleared. Sprint 27.11c IR raises #NP(sel & 0xFFFC).
;
; Expected outcome on i80286 backend:
;   EXC pending=1 vector=0x0B error=0x0008
;   BX = 0x0000   (DS cache stays at last-good base; mov bx,[0] reads 0)
; ----------------------------------------------------------------------
        bits    16
        org     0x100

%include "desc.inc"

start:
        mov     ax, 0xFFF1
        lgdt    [0x140]
        lmsw    ax
        mov     ax, 0x0008
        mov     ds, ax                   ; #NP — descriptor.P = 0
        mov     bx, [0x0000]             ; cache untouched, read returns 0
        hlt

times 0x40-($-$$) db 0x90
        GDTR_IMAGE 0x0010, 0x00000150
times 0x50-($-$$) db 0x00

gdt:    dq      0
        DESC    0xFFFF, 0x0100, 0x12    ; access=0x12 — P=0 not present
