; ----------------------------------------------------------------------
; Phase 28.1 — BIOS Data Area readback test
;
; Confirms PcMemoryBus initializes the equipment word at 0040:0010
; (physical 0x00410) to 0x0061 (1 floppy + 80x25 color + 1 parallel),
; and the memory-size word at 0040:0013 (physical 0x00413) to 0x0280
; (640 KB).
;
; Loaded at 0000:7C00 (HeadlessRunner.LoadTestRom convention).
; After execution:
;   BX = 0x0061   (equipment word at 0x00410)
;   AX = 0x0280   (memory size at 0x00413)
;
; ----------------------------------------------------------------------
        bits    16
        org     0x7C00

start:
        xor     ax, ax              ; AX = 0
        mov     ds, ax              ; DS = 0  (linear access via 0:offset)
        mov     bx, [0x0410]        ; BX = equipment word
        mov     ax, [0x0413]        ; AX = memory-size KB
        hlt
