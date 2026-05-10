; ----------------------------------------------------------------------
; 80286 segment descriptor + GDTR helpers (Phase 27b protected-mode demos)
; ----------------------------------------------------------------------

; DESC limit, base, access — emit one 8-byte 80286 segment descriptor.
;   bytes 0..1  limit (16 bits)
;   bytes 2..4  base low 24 bits
;   byte  5     access rights
;   bytes 6..7  reserved (must be 0 on 80286)
;
; Common access values:
;   0x92 — P=1, S=1, DPL=0, writable data, not accessed
;   0x12 — P=0, S=1, DPL=0, writable data (used to trigger #NP)
;   0x9A — P=1, S=1, DPL=0, readable code (used to trigger SS bad-type)
;   0x98 — P=1, S=1, DPL=0, execute-only code
%macro DESC 3
        dw      %1                              ; limit (low 16)
        dw      (%2) & 0xFFFF                   ; base bits 0..15
        db      ((%2) >> 16) & 0xFF             ; base bits 16..23
        db      %3                              ; access
        dw      0                               ; 80286 reserved
%endmacro

; GDTR_IMAGE limit, base — emit the 6-byte LIDT/LGDT memory operand.
%macro GDTR_IMAGE 2
        dw      %1
        dd      %2
%endmacro
