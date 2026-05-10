; ----------------------------------------------------------------------
; Phase 27b — NULL-selector → SS fault demo
;
; Loading a NULL selector (index = 0) into SS in protected mode raises
; #GP(0) immediately per Intel 80286 PRM §10.3.4 (Sprint 27.11d IR).
; Loading NULL into DS / ES is allowed (deferred fault on access),
; so this demo specifically targets SS.
;
; Expected outcome on i80286 backend:
;   EXC pending=1 vector=0x0D error=0x0000
; ----------------------------------------------------------------------
        bits    16
        org     0x100

%include "desc.inc"

start:
        mov     ax, 0xFFF1
        lgdt    [0x140]
        lmsw    ax
        mov     ax, 0x0000               ; NULL selector
        mov     ss, ax                   ; #GP(0) — NULL → SS in PE=1
        hlt

times 0x40-($-$$) db 0x90
        GDTR_IMAGE 0x0010, 0x00000150
times 0x50-($-$$) db 0x00

; GDT[1] is unused in this demo (the load faults before the descriptor
; is fetched), but we keep the same shape for layout consistency.
gdt:    dq      0
        DESC    0xFFFF, 0x0100, 0x92
