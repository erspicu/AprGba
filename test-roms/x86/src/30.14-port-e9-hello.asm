; 30.14-port-e9-hello.asm
;
; Smoke test for the Phase 30.14a Port 0xE9 debug-out hook.
; Loaded via --test-rom=test-roms/x86/30.14-port-e9-hello.bin which puts
; the bytes at 0000:7C00 and jumps there. Runs in real mode immediately
; (no BIOS, no DOS). Prints a banner to port 0xE9 then halts the CPU.
;
; Build:
;   "C:/Program Files/NASM/nasm.exe" -f bin \
;      test-roms/x86/src/30.14-port-e9-hello.asm \
;      -o test-roms/x86/30.14-port-e9-hello.bin
;
; Run:
;   dotnet src/AprPc.Cli/bin/Debug/net10.0-windows/apr-pc.dll \
;     --test-rom=test-roms/x86/30.14-port-e9-hello.bin --headless \
;     --max-cycles=10000
;
; Expected on stdout (and in temp/port-e9.log):
;   [E9] HELLO FROM PORT 0xE9!
;   [E9] If you can read this, the Bochs hack works.

bits 16
org  0x7C00

start:
        cli                             ; disable IRQs (no need for them)
        mov  si, msg
.loop:
        lodsb                           ; AL = [SI++]
        or   al, al
        jz   .done
        out  0xE9, al                   ; <-- the hook fires here
        jmp  .loop
.done:
        hlt                             ; park forever (host harness sees HLT)
        jmp  .done

msg:
        db   'HELLO FROM PORT 0xE9!', 10
        db   'If you can read this, the Bochs hack works.', 10
        db   0
