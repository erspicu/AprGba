; 30.14-hello.com.asm  --  DOS .COM that prints to Port 0xE9 + DOS stdout
;
; Built as a real .COM: org 0x100, exits via INT 21h AH=4C.
; Demonstrates Phase 30.14a Port 0xE9 hook + Phase 30.14b --floppy-b mount.
;
; Build:
;   "C:/Program Files/NASM/nasm.exe" -f bin \
;     test-roms/x86/src/30.14-hello.com.asm \
;     -o test-roms/x86/fat12-b/HELLO.COM

bits 16
org  0x100

start:
        ; --- 1. blast banner to port 0xE9 (host log + stdout) ---
        mov  si, e9msg
.e9loop:
        lodsb
        or   al, al
        jz   .dos_print
        out  0xE9, al
        jmp  .e9loop

.dos_print:
        ; --- 2. also print to DOS stdout so the guest screen shows it ---
        mov  ah, 0x09
        mov  dx, dos_msg
        int  0x21

        ; --- 3. signal end-of-test on port 0xE9 with a sentinel string,
        ;        useful for AutoTester pattern-match ---
        mov  si, sentinel
.sloop:
        lodsb
        or   al, al
        jz   .exit
        out  0xE9, al
        jmp  .sloop

.exit:
        mov  ah, 0x4C       ; DOS terminate
        mov  al, 0          ; exit code 0
        int  0x21

e9msg:
        db   '[Phase 30.14] hello from B:\HELLO.COM via Port 0xE9!', 10, 0

dos_msg:
        db   'Hello from B:\HELLO.COM (DOS stdout).', 13, 10, '$'

sentinel:
        db   '[TEST_PASS]', 10, 0
