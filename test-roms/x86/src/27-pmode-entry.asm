; ----------------------------------------------------------------------
; Phase 27b — protected-mode entry demo (the "happy path")
;
; Enters protected mode, loads DS with a valid writable-data descriptor,
; reads through DS:0 to verify the descriptor's BASE field is honored.
;
; Expected outcome on i80286 backend:
;   BX = 0xF1B8         (first 2 bytes of code at physical 0x100)
;   EXC_PENDING = 0     (no fault)
; ----------------------------------------------------------------------
        bits    16
        org     0x100

%include "desc.inc"

start:
        mov     ax, 0xFFF1              ; PE bit + reserved-set bits
        lgdt    [0x140]                  ; load GDTR from image below
        lmsw    ax                       ; PE goes live here
        mov     ax, 0x0008               ; selector idx=1, RPL=0, GDT
        mov     ds, ax                   ; descriptor fetch + cache populate
        mov     bx, [0x0000]             ; reads from DS_BASE + 0 = 0x100
        hlt

times 0x40-($-$$) db 0x90               ; pad to file offset 0x40

; GDTR image at file offset 0x40 (= seg-relative 0x140).
        GDTR_IMAGE 0x0010, 0x00000150

times 0x50-($-$$) db 0x00               ; pad to file offset 0x50

; GDT at file offset 0x50 (= linear 0x150 in real-mode).
gdt:    dq      0                        ; GDT[0] NULL descriptor
        DESC    0xFFFF, 0x0100, 0x92    ; GDT[1] writable data, P=1, DPL=0
