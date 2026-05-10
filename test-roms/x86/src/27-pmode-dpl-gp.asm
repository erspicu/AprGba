; ----------------------------------------------------------------------
; Phase 27b — DPL/RPL/CPL privilege fault demo
;
; Loads DS with selector 0x000B (idx=1, RPL=3) while CPL=0 and
; GDT[1].DPL=0. Per Intel 80286 PRM §6.2.1.1 the rule for data segments
; is max(CPL, RPL) <= DPL — here max(0,3) = 3 > 0, so #GP fires
; (Sprint 27.11e IR). Error code = sel & 0xFFFC = 0x0008.
;
; Expected outcome on i80286 backend:
;   EXC pending=1 vector=0x0D error=0x0008
; ----------------------------------------------------------------------
        bits    16
        org     0x100

%include "desc.inc"

start:
        mov     ax, 0xFFF1
        lgdt    [0x140]
        lmsw    ax
        mov     ax, 0x000B               ; idx=1, RPL=3 (low 2 bits)
        mov     ds, ax                   ; #GP — RPL=3 > DPL=0
        hlt

times 0x40-($-$$) db 0x90
        GDTR_IMAGE 0x0010, 0x00000150
times 0x50-($-$$) db 0x00

gdt:    dq      0
        DESC    0xFFFF, 0x0100, 0x92    ; access=0x92 P=1, DPL=0, writable data
