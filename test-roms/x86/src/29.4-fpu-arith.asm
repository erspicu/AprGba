; 29.4-fpu-arith.asm — Phase 29.4 arithmetic family coverage.
;
; Five sub-tests covering FADD / FMUL / FSUB / FSUBR / FDIV / FDIVR.
; Each picks pre-computed inputs whose exact f32 result is a known
; constant, so apr-x86's final-state GPR dump can verify all 6 ops.
;
; Test 1 — FADD m32fp:  3.0 + 4.0 = 7.0 (f32 = 0x40E00000)
; Test 2 — FMUL m32fp:  3.0 * 4.0 = 12.0 (f32 = 0x41400000)
; Test 3 — FSUB m32fp:  10.0 - 3.0 = 7.0
; Test 4 — FSUBR m32fp: 10.0 (operand) - 3.0 (ST(0)) = 7.0 (wait, FSUBR is operand-ST(0))
;                       Actually: load 3.0 then FSUBR 10.0 → ST(0)=10.0-3.0=7.0
; Test 5 — FDIV m32fp:  12.0 / 4.0 = 3.0 (f32 = 0x40400000)
; Test 6 — FDIVR m32fp: load 4.0 then FDIVR 12.0 → ST(0)=12.0/4.0=3.0
;
; Encoding cheat-sheet for D8 /reg with mod=00 rm=110 (direct disp16):
;   D8 06 disp16 = FADD m32  (modrm = 00_000_110 = 0x06)
;   D8 0E disp16 = FMUL m32  (modrm = 00_001_110 = 0x0E)
;   D8 26 disp16 = FSUB m32  (modrm = 00_100_110 = 0x26)
;   D8 2E disp16 = FSUBR m32 (modrm = 00_101_110 = 0x2E)
;   D8 36 disp16 = FDIV m32  (modrm = 00_110_110 = 0x36)
;   D8 3E disp16 = FDIVR m32 (modrm = 00_111_110 = 0x3E)
;
; Test ROM strategy: each sub-test does FNINIT → FLD <input> → D8 /n <other>
; → FSTP DWORD [result_n]. Final loads into the 6 GPRs.
;
; Expected final state:
;   AX = WORD [result1]   = 0x0000  (low half of 0x40E00000 = 7.0f)
;   BX = WORD [result1+2] = 0x40E0
;   CX = WORD [result2+2] = 0x4140  (12.0f)
;   DX = WORD [result3+2] = 0x40E0  (7.0f)
;   SI = WORD [result4+2] = 0x40E0  (7.0f via FSUBR)
;   DI = WORD [result5+2] = 0x4040  (3.0f via FDIV)
; And BP = WORD [result6+2] = 0x4040  (3.0f via FDIVR)

bits 16
org 0x0100

start:
    ; ===== Test 1: 3.0 + 4.0 = 7.0 (FADD) =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0x06                       ; FLD DWORD [val_3]
    dw val_3
    db 0xD8, 0x06                       ; FADD DWORD [val_4]
    dw val_4
    db 0xD9, 0x1E                       ; FSTP DWORD [result1]
    dw result1

    ; ===== Test 2: 3.0 * 4.0 = 12.0 (FMUL) =====
    db 0xD9, 0x06
    dw val_3
    db 0xD8, 0x0E                       ; FMUL DWORD [val_4]
    dw val_4
    db 0xD9, 0x1E
    dw result2

    ; ===== Test 3: 10.0 - 3.0 = 7.0 (FSUB) =====
    db 0xD9, 0x06
    dw val_10
    db 0xD8, 0x26                       ; FSUB DWORD [val_3]
    dw val_3
    db 0xD9, 0x1E
    dw result3

    ; ===== Test 4: 10.0 (operand) - 3.0 (ST0) = 7.0 (FSUBR) =====
    db 0xD9, 0x06
    dw val_3
    db 0xD8, 0x2E                       ; FSUBR DWORD [val_10]
    dw val_10
    db 0xD9, 0x1E
    dw result4

    ; ===== Test 5: 12.0 / 4.0 = 3.0 (FDIV) =====
    db 0xD9, 0x06
    dw val_12
    db 0xD8, 0x36                       ; FDIV DWORD [val_4]
    dw val_4
    db 0xD9, 0x1E
    dw result5

    ; ===== Test 6: 12.0 (operand) / 4.0 (ST0) = 3.0 (FDIVR) =====
    db 0xD9, 0x06
    dw val_4
    db 0xD8, 0x3E                       ; FDIVR DWORD [val_12]
    dw val_12
    db 0xD9, 0x1E
    dw result6

    ; ===== Verification — load each result's high word into a GPR =====
    mov ax, [result1]                   ; low half of result1
    mov bx, [result1 + 2]               ; high half: expect 0x40E0
    mov cx, [result2 + 2]               ; expect 0x4140
    mov dx, [result3 + 2]               ; expect 0x40E0
    mov si, [result4 + 2]               ; expect 0x40E0
    mov di, [result5 + 2]               ; expect 0x4040
    mov bp, [result6 + 2]               ; expect 0x4040
    hlt

; ---- Data ----
val_3:   dd 0x40400000                  ; 3.0f
val_4:   dd 0x40800000                  ; 4.0f
val_10:  dd 0x41200000                  ; 10.0f
val_12:  dd 0x41400000                  ; 12.0f
result1: dd 0xDEADBEEF
result2: dd 0xDEADBEEF
result3: dd 0xDEADBEEF
result4: dd 0xDEADBEEF
result5: dd 0xDEADBEEF
result6: dd 0xDEADBEEF
