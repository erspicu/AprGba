; 29.8-fpu-misc.asm — Phase 29.8 misc unary FPU coverage.
;
; Five sub-tests for FCHS / FABS / FSQRT / FRNDINT / FTST.
;
; Test 1 — FCHS:   load -7.0, FCHS → ST(0) = 7.0       → AX/BX = 0x40E00000
; Test 2 — FABS:   load -3.5, FABS → ST(0) = 3.5       → CX = 0x40600000 high
; Test 3 — FSQRT:  load 16.0, FSQRT → ST(0) = 4.0      → DX = 0x40800000 high
; Test 4 — FRNDINT: load 3.7, FRNDINT → ST(0) = 4.0    → SI = 0x40800000 high
; Test 5 — FTST:   load -1.0, FTST → SW C0=1 C3=0      → DI = 0x0100 (C0 only)

bits 16
org 0x0100

start:
    ; ===== Test 1: FCHS — negate −7.0 → +7.0 =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0x06                       ; FLD DWORD [val_neg7]
    dw val_neg7
    db 0xD9, 0xE0                       ; FCHS
    db 0xD9, 0x1E                       ; FSTP DWORD [result1]
    dw result1

    ; ===== Test 2: FABS — absolute value −3.5 → 3.5 =====
    db 0xD9, 0x06
    dw val_neg3p5
    db 0xD9, 0xE1                       ; FABS
    db 0xD9, 0x1E
    dw result2

    ; ===== Test 3: FSQRT — sqrt(16.0) = 4.0 =====
    db 0xD9, 0x06
    dw val_16
    db 0xD9, 0xFA                       ; FSQRT
    db 0xD9, 0x1E
    dw result3

    ; ===== Test 4: FRNDINT — round 3.7 → 4 (round-to-nearest-even) =====
    db 0xD9, 0x06
    dw val_3p7
    db 0xD9, 0xFC                       ; FRNDINT
    db 0xD9, 0x1E
    dw result4

    ; ===== Test 5: FTST — −1.0 vs 0 → C0 set =====
    db 0xDB, 0xE3                       ; FNINIT (clear SW from prior compares)
    db 0xD9, 0x06
    dw val_neg1
    db 0xD9, 0xE4                       ; FTST
    db 0xDF, 0xE0                       ; FNSTSW AX
    mov [result5], ax

    ; ===== Verification — load 5 results into GPRs =====
    mov ax, [result1 + 2]               ; expect 0x40E0 (7.0f high)
    mov bx, [result2 + 2]               ; expect 0x4060 (3.5f high)
    mov cx, [result3 + 2]               ; expect 0x4080 (4.0f high)
    mov dx, [result4 + 2]               ; expect 0x4080 (4.0f high, FRNDINT)
    mov si, [result5]                   ; expect 0x0100 (C0 set, ST(0) < 0)
    hlt

; ---- Data ----
val_neg7:   dd 0xC0E00000              ; -7.0f
val_neg3p5: dd 0xC0600000              ; -3.5f
val_16:     dd 0x41800000              ; 16.0f
val_3p7:    dd 0x4066_6666             ; 3.7f (rounds to 4 nearest-even)
val_neg1:   dd 0xBF800000              ; -1.0f
result1:    dd 0xDEADBEEF
result2:    dd 0xDEADBEEF
result3:    dd 0xDEADBEEF
result4:    dd 0xDEADBEEF
result5:    dw 0xCAFE
