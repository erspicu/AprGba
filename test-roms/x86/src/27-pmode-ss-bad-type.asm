; ----------------------------------------------------------------------
; Phase 27b — segment-type fault demo (code descriptor → SS)
;
; GDT[1] is built as a CODE segment (access=0x9A: P=1, S=1, executable=1,
; readable=1, DPL=0). Loading this into SS at CPL=0/RPL=0/DPL=0 would
; otherwise pass the privilege check, but the segment-type check
; (Sprint 27.11f IR) blocks first: SS demands writable DATA. #GP.
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
        mov     ax, 0x0008
        mov     ss, ax                   ; #GP — descriptor is code, not writable data
        hlt

times 0x40-($-$$) db 0x90
        GDTR_IMAGE 0x0010, 0x00000150
times 0x50-($-$$) db 0x00

gdt:    dq      0
        DESC    0xFFFF, 0x0100, 0x9A    ; access=0x9A — P=1, S=1, exec=1, readable=1 (code)
