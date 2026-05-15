; 29.7-fpu-transcendental.asm — Phase 29.7 transcendental coverage.
;
; Verifies F2XM1 / FYL2X / FPTAN / FPATAN by computing values whose
; exact f32 representation we can verify via final GPR state.
;
; Test 1 — F2XM1: 2^0.5 - 1 = √2 - 1 ≈ 0.41421356...f32
;   FNINIT, FLD [val_0p5], F2XM1, FSTP DWORD [result1]
;   √2 - 1 as f32 = 0x3ED413CD (per Python: struct.pack('<f', math.sqrt(2)-1))
;
; Test 2 — FYL2X: y=2.0, x=8.0 → y*log2(x) = 2*3 = 6.0
;   FNINIT, FLD [val_2], FLD [val_8], FYL2X, FSTP DWORD [result2]
;   FYL2X consumes ST(0)=8.0 and ST(1)=2.0, writes result into ST(1) then
;   pops. After: TOP back to 7, ST(0)=6.0. FSTP pops it to memory.
;   6.0 as f32 = 0x40C00000.
;
; Test 3 — FPTAN: tan(0) = 0; pushes 1.0 after.
;   FNINIT, FLD [val_0], FPTAN, FSTP DWORD [result3] (pops the 1.0),
;   FSTP DWORD [result3b] (pops the 0.0)
;   result3 (pushed 1.0)  = 0x3F800000
;   result3b (tan(0)=0)   = 0x00000000
;
; Test 4 — FPATAN: atan2(1.0, 1.0) = π/4 ≈ 0.7853982
;   FNINIT, FLD [val_1] (ST0=1, future ST1 after next push), FLD [val_1_b] (now
;   ST0=1 ST1=1), FPATAN computes atan2(ST1=1, ST0=1) = π/4 and pops.
;   π/4 as f32 = 0x3F490FDB.
;
; Final verification (high WORDs into GPRs):
;   AX = WORD [result1+2]  ≈ 0x3ED4   (F2XM1 √2-1)
;   BX = WORD [result2+2]  = 0x40C0   (FYL2X 6.0)
;   CX = WORD [result3+2]  = 0x3F80   (FPTAN push 1.0)
;   DX = WORD [result3b+2] = 0x0000   (FPTAN tan(0))
;   SI = WORD [result4+2]  ≈ 0x3F49   (FPATAN π/4)

bits 16
org 0x0100

start:
    ; ===== Test 1: F2XM1(0.5) =====
    db 0xDB, 0xE3                       ; FNINIT
    db 0xD9, 0x06                       ; FLD DWORD [val_0p5]
    dw val_0p5
    db 0xD9, 0xF0                       ; F2XM1
    db 0xD9, 0x1E                       ; FSTP DWORD [result1]
    dw result1

    ; ===== Test 2: FYL2X(2.0, 8.0) → 2*log2(8) = 6 =====
    db 0xDB, 0xE3
    db 0xD9, 0x06
    dw val_2
    db 0xD9, 0x06
    dw val_8
    db 0xD9, 0xF1                       ; FYL2X
    db 0xD9, 0x1E
    dw result2

    ; ===== Test 3: FPTAN(0) = 0, push 1.0 =====
    db 0xDB, 0xE3
    db 0xD9, 0x06
    dw val_0
    db 0xD9, 0xF2                       ; FPTAN
    db 0xD9, 0x1E                       ; FSTP (pops the pushed 1.0)
    dw result3
    db 0xD9, 0x1E                       ; FSTP (pops the tan(0)=0)
    dw result3b

    ; ===== Test 4: FPATAN(1, 1) → atan2(1, 1) = π/4 =====
    db 0xDB, 0xE3
    db 0xD9, 0x06
    dw val_1                            ; push 1.0 to ST(0)
    db 0xD9, 0x06
    dw val_1_b                          ; push 1.0; now ST(0)=1, ST(1)=1
    db 0xD9, 0xF3                       ; FPATAN: atan2(ST(1), ST(0)) → ST(1), pop
    db 0xD9, 0x1E                       ; FSTP result4
    dw result4

    ; ===== Verification =====
    mov ax, [result1 + 2]
    mov bx, [result2 + 2]
    mov cx, [result3 + 2]
    mov dx, [result3b + 2]
    mov si, [result4 + 2]
    hlt

; ---- Data ----
val_0p5:    dd 0x3F000000              ; 0.5f
val_2:      dd 0x40000000              ; 2.0f
val_8:      dd 0x41000000              ; 8.0f
val_0:      dd 0x00000000              ; 0.0f
val_1:      dd 0x3F800000              ; 1.0f
val_1_b:    dd 0x3F800000              ; 1.0f (second copy for Test 4)
result1:    dd 0xDEADBEEF
result2:    dd 0xDEADBEEF
result3:    dd 0xDEADBEEF
result3b:   dd 0xDEADBEEF
result4:    dd 0xDEADBEEF
