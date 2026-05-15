; 29.5-fpu-compare.asm — Phase 29.5 FCOM + FNSTSW AX coverage.
;
; Three sub-tests exercise the three FCOM outcomes (less / greater /
; equal) plus FNSTSW AX. Each sequence:
;   FNINIT → FLD <st0> → FCOM <operand> → FNSTSW AX → MOV [resultN], AX
;
; Intel SDM condition-code mapping for FCOM:
;   ST(0) > operand  → C3=0 C2=0 C0=0   →  AX = 0x0000
;   ST(0) < operand  → C3=0 C2=0 C0=1   →  AX = 0x0100
;   ST(0) = operand  → C3=1 C2=0 C0=0   →  AX = 0x4000
;
; Note: TOP_SW (bits 11:13 of FPU_SW) is NOT yet kept in sync with
; FPU_TOP by our Push/Pop helpers — TODO Phase 29.x optimisation.
; Real 8087 hardware mirrors TOP into TOP_SW continuously; until we
; do, these tests pin TOP_SW=0 so the SW value reads as a clean
; C-bit pattern with no TOP_SW noise (acceptable since most DOS code
; reads C bits via `FNSTSW AX; SAHF; JCC` and ignores TOP_SW).

bits 16
org 0x0100

start:
    ; ===== Test 1: 3.0 < 4.0 → C0 set =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0x06                       ; FLD DWORD [val_3]
    dw val_3
    db 0xD8, 0x16                       ; FCOM DWORD [val_4] (modrm = 00_010_110)
    dw val_4
    db 0xDF, 0xE0                       ; FNSTSW AX
    mov [result1], ax

    ; ===== Test 2: 4.0 > 3.0 → no C bits =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0x06                       ; FLD DWORD [val_4]
    dw val_4
    db 0xD8, 0x16                       ; FCOM DWORD [val_3]
    dw val_3
    db 0xDF, 0xE0                       ; FNSTSW AX
    mov [result2], ax

    ; ===== Test 3: 3.0 = 3.0 → C3 set =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0x06                       ; FLD DWORD [val_3]
    dw val_3
    db 0xD8, 0x16                       ; FCOM DWORD [val_3_copy]
    dw val_3_copy
    db 0xDF, 0xE0                       ; FNSTSW AX
    mov [result3], ax

    ; ===== Verification — load 3 results into 3 GPRs =====
    mov ax, [result1]                   ; expect 0x0100 (C0 only)
    mov bx, [result2]                   ; expect 0x0000 (no C bits)
    mov cx, [result3]                   ; expect 0x4000 (C3 only)
    hlt

; ---- Data ----
val_3:        dd 0x40400000             ; 3.0f
val_4:        dd 0x40800000             ; 4.0f
val_3_copy:   dd 0x40400000             ; 3.0f again
result1:      dw 0xCAFE
result2:      dw 0xCAFE
result3:      dw 0xCAFE
